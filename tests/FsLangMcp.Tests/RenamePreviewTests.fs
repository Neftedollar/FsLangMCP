module FsLangMcp.Tests.RenamePreviewTests

open System.IO
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.LspBridge

// These tests exercise the pure RenamePreviewShape.build transform — the new logic
// that turns a raw FSAC WorkspaceEdit into the agent-friendly grouped preview. The
// FSAC round-trip itself is deliberately not started here (mirrors the rest of the
// LSP-bridge test suite, which only tests pure shapes + validation), so the tests
// stay fast, deterministic, and independent of a fsautocomplete install.

let private parse (json: string) : JsonNode = JsonNode.Parse(json)

/// Builds a Context whose readers are backed by an in-memory uri->text map and an
/// injected project resolver, so build can be tested without touching disk.
let private contextFor
    (newName: string)
    (originatingUri: string)
    (line: int)
    (character: int)
    (texts: Map<string, string>)
    (resolveProject: string -> string option)
    : RenamePreviewShape.Context =
    { NewName = newName
      OriginatingUri = originatingUri
      Line = line
      Character = character
      LookupLines = (fun uri -> texts |> Map.tryFind uri |> Option.map RenamePreviewShape.splitLines)
      ResolveProject = resolveProject
      UriToDisplay = id }

let private fileUri = "file:///proj/A.fs"

let private singleFileSource = "let foo = 1\nlet bar = foo + foo"

// documentChanges shape (what FSAC emits): three edits, all renaming `foo`.
let private singleFileEdit =
    $$"""
    {
      "documentChanges": [
        {
          "textDocument": { "uri": "{{fileUri}}", "version": 1 },
          "edits": [
            { "range": { "start": {"line":0,"character":4}, "end": {"line":0,"character":7} }, "newText": "renamed" },
            { "range": { "start": {"line":1,"character":10}, "end": {"line":1,"character":13} }, "newText": "renamed" },
            { "range": { "start": {"line":1,"character":16}, "end": {"line":1,"character":19} }, "newText": "renamed" }
          ]
        }
      ]
    }
    """

[<Fact>]
let ``build groups edits by file with counts and synthesized preview lines`` () =
    let ctx =
        contextFor "renamed" fileUri 0 5 (Map.ofList [ fileUri, singleFileSource ]) (fun _ -> Some "/proj/A.fsproj")

    let result = RenamePreviewShape.build ctx (parse singleFileEdit)

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal(3, result["totalEdits"].GetValue<int>())
    Assert.Equal(1, result["fileCount"].GetValue<int>())
    Assert.False(result["crossProject"].GetValue<bool>())

    let files = result["files"].AsArray()
    Assert.Equal(1, files.Count)
    let file0 = files[0]
    Assert.Equal(3, file0["editCount"].GetValue<int>())

    let edits = file0["edits"].AsArray()
    Assert.Equal(3, edits.Count)

[<Fact>]
let ``build synthesizes previewLineText alongside the untouched original line`` () =
    let ctx =
        contextFor "renamed" fileUri 0 5 (Map.ofList [ fileUri, singleFileSource ]) (fun _ -> None)

    let result = RenamePreviewShape.build ctx (parse singleFileEdit)
    let firstEdit = result["files"].AsArray().[0].["edits"].AsArray().[0]

    Assert.Equal("let foo = 1", firstEdit["originalLineText"].GetValue<string>())
    Assert.Equal("let renamed = 1", firstEdit["previewLineText"].GetValue<string>())

[<Fact>]
let ``build reports the original symbol read from the originating position`` () =
    let ctx =
        contextFor "renamed" fileUri 0 5 (Map.ofList [ fileUri, singleFileSource ]) (fun _ -> None)

    let result = RenamePreviewShape.build ctx (parse singleFileEdit)

    Assert.Equal("foo", result["symbol"].GetValue<string>())

[<Fact>]
let ``build returns no_symbol when the workspace edit has no changes`` () =
    // FSAC returns an empty/absent edit when the position has no renamable symbol.
    let ctx =
        contextFor "renamed" fileUri 9 99 (Map.ofList [ fileUri, singleFileSource ]) (fun _ -> None)

    let result = RenamePreviewShape.build ctx (parse """{ "documentChanges": [] }""")

    Assert.Equal("no_symbol", result["status"].GetValue<string>())
    Assert.Contains("9:99", result["reason"].GetValue<string>())

[<Fact>]
let ``build returns no_symbol when the workspace edit is null`` () =
    // textDocument/rename returns a JSON null result for unrenamable positions.
    let ctx =
        contextFor "renamed" fileUri 0 0 (Map.ofList [ fileUri, singleFileSource ]) (fun _ -> None)

    let result = RenamePreviewShape.build ctx (parse "null")

    Assert.Equal("no_symbol", result["status"].GetValue<string>())

[<Fact>]
let ``build flags crossProject when edits span two distinct projects`` () =
    let uriA = "file:///solution/projA/A.fs"
    let uriB = "file:///solution/projB/B.fs"

    let edit =
        $$"""
        {
          "documentChanges": [
            { "textDocument": { "uri": "{{uriA}}" },
              "edits": [ { "range": { "start": {"line":0,"character":4}, "end": {"line":0,"character":7} }, "newText": "renamed" } ] },
            { "textDocument": { "uri": "{{uriB}}" },
              "edits": [ { "range": { "start": {"line":0,"character":0}, "end": {"line":0,"character":3} }, "newText": "renamed" } ] }
          ]
        }
        """

    let texts = Map.ofList [ uriA, "let foo = 1"; uriB, "foo" ]

    let resolve uri =
        if uri = uriA then Some "/solution/projA/A.fsproj"
        elif uri = uriB then Some "/solution/projB/B.fsproj"
        else None

    let ctx = contextFor "renamed" uriA 0 5 texts resolve
    let result = RenamePreviewShape.build ctx (parse edit)

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal(2, result["fileCount"].GetValue<int>())
    Assert.Equal(2, result["totalEdits"].GetValue<int>())
    Assert.True(result["crossProject"].GetValue<bool>())

[<Fact>]
let ``build does not flag crossProject when every file shares one project`` () =
    let uriA = "file:///proj/A.fs"
    let uriB = "file:///proj/B.fs"

    let edit =
        $$"""
        {
          "documentChanges": [
            { "textDocument": { "uri": "{{uriA}}" },
              "edits": [ { "range": { "start": {"line":0,"character":4}, "end": {"line":0,"character":7} }, "newText": "renamed" } ] },
            { "textDocument": { "uri": "{{uriB}}" },
              "edits": [ { "range": { "start": {"line":0,"character":0}, "end": {"line":0,"character":3} }, "newText": "renamed" } ] }
          ]
        }
        """

    let texts = Map.ofList [ uriA, "let foo = 1"; uriB, "foo" ]
    let ctx = contextFor "renamed" uriA 0 5 texts (fun _ -> Some "/proj/Proj.fsproj")
    let result = RenamePreviewShape.build ctx (parse edit)

    Assert.Equal(2, result["fileCount"].GetValue<int>())
    Assert.False(result["crossProject"].GetValue<bool>())

[<Fact>]
let ``build understands the legacy changes-map workspace edit shape`` () =
    let edit =
        $$"""
        {
          "changes": {
            "{{fileUri}}": [
              { "range": { "start": {"line":0,"character":4}, "end": {"line":0,"character":7} }, "newText": "renamed" }
            ]
          }
        }
        """

    let ctx =
        contextFor "renamed" fileUri 0 5 (Map.ofList [ fileUri, singleFileSource ]) (fun _ -> None)

    let result = RenamePreviewShape.build ctx (parse edit)

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal(1, result["totalEdits"].GetValue<int>())
    Assert.Equal("let renamed = 1", result["files"].AsArray().[0].["edits"].AsArray().[0].["previewLineText"].GetValue<string>())

[<Fact>]
let ``build reads file content from disk but writes nothing back`` () =
    // Prove non-destructiveness: the transform reads the real file through its
    // lookup, yet the on-disk bytes are byte-identical afterwards.
    let dir = Path.Combine(Path.GetTempPath(), "fslangmcp-rename-preview-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    try
        let filePath = Path.Combine(dir, "Sample.fs")
        File.WriteAllText(filePath, singleFileSource)
        let uri = System.Uri(filePath).AbsoluteUri
        let before = File.ReadAllBytes(filePath)

        let edit =
            $$"""
            {
              "documentChanges": [
                { "textDocument": { "uri": "{{uri}}" },
                  "edits": [ { "range": { "start": {"line":0,"character":4}, "end": {"line":0,"character":7} }, "newText": "renamed" } ] }
              ]
            }
            """

        // LookupLines reads the actual file from disk (no in-memory map).
        let ctx: RenamePreviewShape.Context =
            { NewName = "renamed"
              OriginatingUri = uri
              Line = 0
              Character = 5
              LookupLines =
                (fun u ->
                    let p = System.Uri(u).LocalPath

                    if File.Exists p then
                        Some(RenamePreviewShape.splitLines (File.ReadAllText p))
                    else
                        None)
              ResolveProject = (fun _ -> None)
              UriToDisplay = (fun u -> System.Uri(u).LocalPath) }

        let result = RenamePreviewShape.build ctx (parse edit)

        Assert.Equal("ok", result["status"].GetValue<string>())

        Assert.Equal(
            "let renamed = 1",
            result["files"].AsArray().[0].["edits"].AsArray().[0].["previewLineText"].GetValue<string>()
        )

        let after = File.ReadAllBytes(filePath)
        Assert.Equal<byte[]>(before, after)
    finally
        Directory.Delete(dir, true)
