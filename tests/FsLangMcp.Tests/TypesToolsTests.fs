module FsLangMcp.Tests.TypesToolsTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsMcp.Core
open FsLangMcp.Types
open FsLangMcp.Tools

// ─── Group A: Types.fs helper functions ───────────────────────────────────────

[<Fact>]
let ``jstr creates a JsonNode that serializes to a JSON string value`` () =
    let node = jstr "hello"
    let json = JsonSerializer.Serialize(node)
    Assert.Equal("\"hello\"", json)

[<Fact>]
let ``jstr empty string serializes correctly`` () =
    let node = jstr ""
    let json = JsonSerializer.Serialize(node)
    Assert.Equal("\"\"", json)

[<Fact>]
let ``jint creates a JsonNode that serializes to a JSON number`` () =
    let node = jint 42
    let json = JsonSerializer.Serialize(node)
    Assert.Equal("42", json)

[<Fact>]
let ``jint zero serializes correctly`` () =
    let node = jint 0
    let json = JsonSerializer.Serialize(node)
    Assert.Equal("0", json)

[<Fact>]
let ``jbool true creates a JsonNode that serializes to JSON true`` () =
    let node = jbool true
    let json = JsonSerializer.Serialize(node)
    Assert.Equal("true", json)

[<Fact>]
let ``jbool false creates a JsonNode that serializes to JSON false`` () =
    let node = jbool false
    let json = JsonSerializer.Serialize(node)
    Assert.Equal("false", json)

[<Fact>]
let ``jobj creates a JsonObject with correct key-value pairs`` () =
    let node = jobj [ "name", jstr "Alice"; "age", jint 30 ]
    let json = JsonSerializer.Serialize(node)
    Assert.Contains("\"name\":\"Alice\"", json)
    Assert.Contains("\"age\":30", json)

[<Fact>]
let ``jobj with null value produces null in JSON`` () =
    let node = jobj [ "key", null ]
    let json = JsonSerializer.Serialize(node)
    Assert.Contains("\"key\":null", json)

[<Fact>]
let ``normalizePath resolves relative path to absolute`` () =
    let relative = "somefile.fs"
    let result = normalizePath relative
    Assert.True(Path.IsPathRooted(result), "normalizePath should return an absolute path")
    Assert.EndsWith("somefile.fs", result)

[<Fact>]
let ``normalizePath resolves dotdot segments`` () =
    let tempDir = Path.GetTempPath()
    let withDotDot = Path.Combine(tempDir, "subdir", "..", "file.fs")
    let result = normalizePath withDotDot
    Assert.False(result.Contains(".."), "normalizePath should eliminate .. segments")
    Assert.True(Path.IsPathRooted(result))

[<Fact>]
let ``toFileUri returns a URI starting with file scheme and containing filename`` () =
    let tempFile = Path.Combine(Path.GetTempPath(), "testfile.fs")
    let uri = toFileUri tempFile
    Assert.StartsWith("file://", uri)
    Assert.Contains("testfile.fs", uri)

// ─── Group B: Tools.fs toolResult exception mapping ───────────────────────────

[<Fact>]
let ``toolResult success case returns Ok with Content text`` () : Task =
    task {
        let node : JsonNode = JsonValue.Create(42)
        let! result = toolResult (Task.FromResult(node))
        match result with
        | Ok contents ->
            Assert.NotEmpty(contents)
        | Error e ->
            Assert.Fail($"Expected Ok but got Error: {e}")
    }

[<Fact>]
let ``toolResult OperationCanceledException returns Error with FcsAborted errorKind`` () : Task =
    task {
        let work =
            task {
                raise (OperationCanceledException("cancelled by user"))
                return (null : JsonNode)
            }
        let! result = toolResult work
        match result with
        | Error (McpError.TransportError msg) ->
            Assert.Contains("\"errorKind\":\"FcsAborted\"", msg)
            Assert.Contains("cancelled by user", msg)
        | Ok _ ->
            Assert.Fail("Expected Error but got Ok")
        | Error e ->
            Assert.Fail($"Expected TransportError but got: {e}")
    }

[<Fact>]
let ``toolResult ArgumentException returns Error with InvalidArgs errorKind`` () : Task =
    task {
        let work =
            task {
                raise (ArgumentException("bad param"))
                return (null : JsonNode)
            }
        let! result = toolResult work
        match result with
        | Error (McpError.TransportError msg) ->
            Assert.Contains("\"errorKind\":\"InvalidArgs\"", msg)
            Assert.Contains("bad param", msg)
        | Ok _ ->
            Assert.Fail("Expected Error but got Ok")
        | Error e ->
            Assert.Fail($"Expected TransportError but got: {e}")
    }

[<Fact>]
let ``toolResult FileNotFoundException returns Error with FileNotFound errorKind`` () : Task =
    task {
        let work =
            task {
                raise (System.IO.FileNotFoundException("file is missing"))
                return (null : JsonNode)
            }
        let! result = toolResult work
        match result with
        | Error (McpError.TransportError msg) ->
            Assert.Contains("\"errorKind\":\"FileNotFound\"", msg)
            Assert.Contains("file is missing", msg)
        | Ok _ ->
            Assert.Fail("Expected Error but got Ok")
        | Error e ->
            Assert.Fail($"Expected TransportError but got: {e}")
    }

[<Fact>]
let ``toolResult generic exception returns Error with InfraFailure errorKind`` () : Task =
    task {
        let work =
            task {
                raise (Exception("something went wrong"))
                return (null : JsonNode)
            }
        let! result = toolResult work
        match result with
        | Error (McpError.TransportError msg) ->
            Assert.Contains("\"errorKind\":\"InfraFailure\"", msg)
            Assert.Contains("something went wrong", msg)
        | Ok _ ->
            Assert.Fail("Expected Error but got Ok")
        | Error e ->
            Assert.Fail($"Expected TransportError but got: {e}")
    }

[<Fact>]
let ``toolResult exception with not ready message returns Error with NotReady errorKind`` () : Task =
    task {
        let work =
            task {
                raise (Exception("service not ready yet"))
                return (null : JsonNode)
            }
        let! result = toolResult work
        match result with
        | Error (McpError.TransportError msg) ->
            Assert.Contains("\"errorKind\":\"NotReady\"", msg)
            Assert.Contains("not ready", msg)
        | Ok _ ->
            Assert.Fail("Expected Error but got Ok")
        | Error e ->
            Assert.Fail($"Expected TransportError but got: {e}")
    }
