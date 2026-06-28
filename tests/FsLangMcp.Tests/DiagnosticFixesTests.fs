module FsLangMcp.Tests.DiagnosticFixesTests

// Deterministic tests for the pure builders behind `fcs_diagnostic_fixes` (#53).
//
// The live grouping path (FSAC publishDiagnostics → per-diagnostic codeAction)
// needs a running fsautocomplete process, which CI does not provision — so, as with
// the rest of LspBridge, the *shaping* logic is extracted into pure functions in
// LspResponseShape and exercised here. The fixtures below use the EXACT JSON shapes
// captured from a live FSAC 0.83 session (an FS0039 "value not defined" diagnostic
// and FSAC's "Replace with 'Failure'" quickfix), so the deterministic tests mirror
// real wire data rather than an invented shape.

open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.Types
open FsLangMcp.LspBridge.LspResponseShape

// ── Real-shaped FSAC fixtures ────────────────────────────────────────────────────

/// An FS0039 diagnostic exactly as FSAC 0.83 publishes it (code is an int, not a
/// string; range is 0-based LSP).
let private fs39Diagnostic () : JsonNode =
    jobj
        [ "range",
          jobj
              [ "start", jobj [ "line", jint 2; "character", jint 14 ]
                "end", jobj [ "line", jint 2; "character", jint 18 ] ]
          "severity", jint 1
          "code", jint 39
          "source", jstr "F# Compiler"
          "message", jstr "The value, namespace, type or module 'File' is not defined." ]
    :> JsonNode

/// FSAC's quickfix CodeAction (documentChanges form of WorkspaceEdit).
let private resolveFix () : JsonNode =
    let edit =
        jobj [ "range", jobj [ "start", jobj [ "line", jint 2; "character", jint 14 ]
                               "end", jobj [ "line", jint 2; "character", jint 18 ] ]
               "newText", jstr "Failure" ]

    let documentChange =
        jobj
            [ "textDocument", jobj [ "uri", jstr "file:///Lib.fs"; "version", jint 2 ]
              "edits", JsonArray(edit :> JsonNode) :> JsonNode ]

    jobj
        [ "title", jstr "Replace with 'Failure'"
          "kind", jstr "quickfix"
          "edit", jobj [ "documentChanges", JsonArray(documentChange :> JsonNode) :> JsonNode ] ]
    :> JsonNode

// ── buildDiagnosticFixesResponse ─────────────────────────────────────────────────

[<Fact>]
let ``a clean file (no diagnostics) yields an empty, well-formed payload`` () =
    let result = buildDiagnosticFixesResponse "/tmp/Lib.fs" []

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("/tmp/Lib.fs", result["file"].GetValue<string>())
    Assert.Equal(0, result["diagnosticCount"].GetValue<int>())
    Assert.Equal(0, result["fixCount"].GetValue<int>())
    Assert.Equal(0, (result["diagnostics"] :?> JsonArray).Count)

[<Fact>]
let ``a fixable diagnostic returns at least one fix grouped under it`` () =
    let result =
        buildDiagnosticFixesResponse "/tmp/Lib.fs" [ fs39Diagnostic (), [ resolveFix () ] ]

    Assert.Equal(1, result["diagnosticCount"].GetValue<int>())
    Assert.Equal(1, result["fixCount"].GetValue<int>())

    let diag = (result["diagnostics"] :?> JsonArray)[0]
    // The diagnostic's own fields are preserved on the grouped entry.
    Assert.Equal(39, diag["code"].GetValue<int>())
    Assert.Equal(1, diag["severity"].GetValue<int>())
    Assert.Contains("not defined", diag["message"].GetValue<string>())
    let rangeStart = diag["range"]["start"]
    Assert.Equal(2, rangeStart["line"].GetValue<int>())

    // The fix is grouped UNDER that diagnostic, in the compact shape.
    let fixes = diag["fixes"] :?> JsonArray
    Assert.Equal(1, fixes.Count)
    let fix0 = fixes[0]
    Assert.Equal("Replace with 'Failure'", fix0["title"].GetValue<string>())
    Assert.Equal("quickfix", fix0["kind"].GetValue<string>())
    Assert.Equal("1 edit: Failure", fix0["editSummary"].GetValue<string>())

[<Fact>]
let ``fixCount sums fixes across every diagnostic`` () =
    let twoFixes = [ resolveFix (); resolveFix () ]
    let oneFix = [ resolveFix () ]

    let result =
        buildDiagnosticFixesResponse "/tmp/Lib.fs" [ fs39Diagnostic (), twoFixes; fs39Diagnostic (), oneFix ]

    Assert.Equal(2, result["diagnosticCount"].GetValue<int>())
    Assert.Equal(3, result["fixCount"].GetValue<int>())

[<Fact>]
let ``a diagnostic with no available fixes is still surfaced with an empty fixes array`` () =
    let result = buildDiagnosticFixesResponse "/tmp/Lib.fs" [ fs39Diagnostic (), [] ]

    Assert.Equal(1, result["diagnosticCount"].GetValue<int>())
    Assert.Equal(0, result["fixCount"].GetValue<int>())
    let diag = (result["diagnostics"] :?> JsonArray)[0]
    Assert.Equal(0, (diag["fixes"] :?> JsonArray).Count)

[<Fact>]
let ``the builder deep-clones diagnostic fields rather than reparenting the source`` () =
    // A node may have only one parent; if the builder attached the live range node
    // instead of cloning it, serializing the original diagnostic afterwards would throw.
    let diag = fs39Diagnostic ()
    let _ = buildDiagnosticFixesResponse "/tmp/Lib.fs" [ diag, [ resolveFix () ] ]

    let roundtrip = JsonSerializer.Serialize(diag)
    Assert.Contains("\"code\":39", roundtrip)

// ── summarizeCodeAction ──────────────────────────────────────────────────────────

[<Fact>]
let ``summarizeCodeAction projects title, kind and editSummary`` () =
    let summary = summarizeCodeAction (resolveFix ())

    Assert.Equal("Replace with 'Failure'", summary["title"].GetValue<string>())
    Assert.Equal("quickfix", summary["kind"].GetValue<string>())
    Assert.Equal("1 edit: Failure", summary["editSummary"].GetValue<string>())

[<Fact>]
let ``summarizeCodeAction leaves title and kind null when absent`` () =
    let bare = jobj [ "edit", jobj [ "documentChanges", JsonArray() :> JsonNode ] ] :> JsonNode
    let summary = summarizeCodeAction bare

    Assert.Null(summary["title"])
    Assert.Null(summary["kind"])

// ── editSummaryOf ────────────────────────────────────────────────────────────────

[<Fact>]
let ``editSummaryOf summarizes the changes-map form of a WorkspaceEdit`` () =
    // WorkspaceEdit.changes : { "<uri>": [ TextEdit ] } — the alternative to documentChanges.
    let action =
        jobj
            [ "title", jstr "Open System.IO"
              "kind", jstr "quickfix"
              "edit",
              jobj
                  [ "changes",
                    jobj
                        [ "file:///Lib.fs",
                          JsonArray(
                              jobj
                                  [ "range", jobj [ "start", jobj [ "line", jint 0; "character", jint 0 ]
                                                    "end", jobj [ "line", jint 0; "character", jint 0 ] ]
                                    "newText", jstr "open System.IO\n" ]
                              :> JsonNode
                          )
                          :> JsonNode ] ] ]
        :> JsonNode

    Assert.Equal("1 edit: open System.IO", editSummaryOf action)

[<Fact>]
let ``editSummaryOf reports a deletion when the only edit inserts empty text`` () =
    let action =
        jobj
            [ "title", jstr "Remove unused open"
              "edit",
              jobj
                  [ "documentChanges",
                    JsonArray(
                        jobj
                            [ "edits",
                              JsonArray(
                                  jobj
                                      [ "range", jobj [ "start", jobj [ "line", jint 2; "character", jint 0 ]
                                                        "end", jobj [ "line", jint 3; "character", jint 0 ] ]
                                        "newText", jstr "" ]
                                  :> JsonNode
                              )
                              :> JsonNode ]
                        :> JsonNode
                    )
                    :> JsonNode ] ]
        :> JsonNode

    Assert.Equal("1 edit (deletion)", editSummaryOf action)

[<Fact>]
let ``editSummaryOf reports a command-only action with no text edit`` () =
    let action =
        jobj
            [ "title", jstr "Run something"
              "command", jobj [ "command", jstr "fsharp.doThing"; "title", jstr "Run something" ] ]
        :> JsonNode

    Assert.Equal("command (no text edit)", editSummaryOf action)

[<Fact>]
let ``editSummaryOf reports no edit for an empty action`` () =
    Assert.Equal("no edit", editSummaryOf (jobj [ "title", jstr "x" ] :> JsonNode))

[<Fact>]
let ``editSummaryOf counts multiple edits and previews the first`` () =
    let mkEdit newText =
        jobj
            [ "range", jobj [ "start", jobj [ "line", jint 0; "character", jint 0 ]
                              "end", jobj [ "line", jint 0; "character", jint 0 ] ]
              "newText", jstr newText ]
        :> JsonNode

    let action =
        jobj
            [ "edit",
              jobj
                  [ "documentChanges",
                    JsonArray(
                        jobj [ "edits", JsonArray(mkEdit "first", mkEdit "second") :> JsonNode ] :> JsonNode
                    )
                    :> JsonNode ] ]
        :> JsonNode

    Assert.Equal("2 edits: first", editSummaryOf action)

[<Fact>]
let ``editSummaryOf collapses whitespace and truncates long inserts`` () =
    let long = System.String('a', 80)

    let action =
        jobj
            [ "edit",
              jobj
                  [ "documentChanges",
                    JsonArray(
                        jobj
                            [ "edits",
                              JsonArray(
                                  jobj
                                      [ "range", jobj [ "start", jobj [ "line", jint 0; "character", jint 0 ]
                                                        "end", jobj [ "line", jint 0; "character", jint 0 ] ]
                                        "newText", jstr ("let x =\n    " + long) ]
                                  :> JsonNode
                              )
                              :> JsonNode ]
                        :> JsonNode
                    )
                    :> JsonNode ] ]
        :> JsonNode

    let summary = editSummaryOf action
    Assert.StartsWith("1 edit: let x = aaa", summary)
    Assert.EndsWith("...", summary)
    // "1 edit: " (8) + 57 preview chars + "..." (3)
    Assert.Equal(8 + 57 + 3, summary.Length)

// ── diagnosticCoversPosition / positionWithinRange / lineWithinRange ──────────────

let private range sl sc el ec : JsonNode =
    jobj
        [ "start", jobj [ "line", jint sl; "character", jint sc ]
          "end", jobj [ "line", jint el; "character", jint ec ] ]
    :> JsonNode

[<Fact>]
let ``diagnosticCoversPosition keeps everything when no position is given`` () =
    Assert.True(diagnosticCoversPosition None None (range 2 14 2 18))

[<Fact>]
let ``diagnosticCoversPosition matches a point inside the range`` () =
    Assert.True(diagnosticCoversPosition (Some 2) (Some 16) (range 2 14 2 18))

[<Fact>]
let ``diagnosticCoversPosition rejects a point outside the range`` () =
    Assert.False(diagnosticCoversPosition (Some 2) (Some 30) (range 2 14 2 18))
    Assert.False(diagnosticCoversPosition (Some 5) (Some 16) (range 2 14 2 18))

[<Fact>]
let ``diagnosticCoversPosition with line only matches any column on that line`` () =
    Assert.True(diagnosticCoversPosition (Some 2) None (range 2 14 2 18))
    Assert.False(diagnosticCoversPosition (Some 3) None (range 2 14 2 18))

[<Fact>]
let ``positionWithinRange is inclusive at both boundaries`` () =
    let r = range 2 14 2 18
    Assert.True(positionWithinRange 2 14 r) // at start
    Assert.True(positionWithinRange 2 18 r) // at end
    Assert.False(positionWithinRange 2 13 r) // one before start
    Assert.False(positionWithinRange 2 19 r) // one past end

[<Fact>]
let ``positionWithinRange spans interior lines of a multi-line range`` () =
    let r = range 2 5 5 3
    Assert.True(positionWithinRange 3 0 r) // interior line, any column
    Assert.True(positionWithinRange 2 5 r) // start edge
    Assert.False(positionWithinRange 2 4 r) // before start column on start line
    Assert.False(positionWithinRange 5 4 r) // past end column on end line

[<Fact>]
let ``lineWithinRange covers the inclusive line span`` () =
    let r = range 2 0 5 0
    Assert.True(lineWithinRange 2 r)
    Assert.True(lineWithinRange 4 r)
    Assert.True(lineWithinRange 5 r)
    Assert.False(lineWithinRange 1 r)
    Assert.False(lineWithinRange 6 r)
