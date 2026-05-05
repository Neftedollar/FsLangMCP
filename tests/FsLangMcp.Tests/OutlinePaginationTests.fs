module FsLangMcp.Tests.OutlinePaginationTests

/// Unit tests for the cursor pagination + filter features added in issue #78.
///
/// Coverage:
///   A. Cursor encode / decode round-trip
///   B. Cursor rejects malformed input (structured error, not exception)
///   C. paginationFields produces correct truncated / nextCursor / totalEstimate
///   D. Filter regex — entry matching
///   E. Default args shape (summaryOnly=true, maxFiles=50, maxResultsPerFile=30)

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.Cursor
open FsLangMcp.Types

// ─── A. Cursor round-trip ──────────────────────────────────────────────────────

[<Fact>]
let ``encode then tryDecode round-trips offset zero`` () =
    let cursor = encode 0
    match tryDecode cursor with
    | Ok payload -> Assert.Equal(0, payload.offset)
    | Error msg -> Assert.Fail($"Expected Ok but got Error: {msg}")

[<Fact>]
let ``encode then tryDecode round-trips arbitrary positive offset`` () =
    let cursor = encode 87
    match tryDecode cursor with
    | Ok payload -> Assert.Equal(87, payload.offset)
    | Error msg -> Assert.Fail($"Expected Ok but got Error: {msg}")

[<Fact>]
let ``encode then tryDecode round-trips large offset`` () =
    let cursor = encode 10_000
    match tryDecode cursor with
    | Ok payload -> Assert.Equal(10_000, payload.offset)
    | Error msg -> Assert.Fail($"Expected Ok but got Error: {msg}")

[<Fact>]
let ``encode produces a non-empty Base64 string`` () =
    let cursor = encode 5
    Assert.NotEmpty(cursor)
    // Base64 alphabet — no spaces
    Assert.DoesNotContain(" ", cursor)

[<Fact>]
let ``encoded cursor is not human-readable JSON (opaque)`` () =
    let cursor = encode 3
    // The raw cursor string must not look like a plain JSON object.
    Assert.False(cursor.StartsWith("{"), "cursor should be Base64, not raw JSON")

// ─── B. Cursor rejects malformed input ────────────────────────────────────────

[<Fact>]
let ``tryDecode returns Error for empty string`` () =
    match tryDecode "" with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Expected Error for empty cursor")

[<Fact>]
let ``tryDecode returns Error for whitespace-only string`` () =
    match tryDecode "   " with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Expected Error for whitespace cursor")

[<Fact>]
let ``tryDecode returns Error for non-Base64 garbage`` () =
    match tryDecode "not-valid-base64!!!" with
    | Error msg ->
        Assert.Contains("Base64", msg, StringComparison.OrdinalIgnoreCase)
    | Ok _ -> Assert.Fail("Expected Error for garbage cursor")

[<Fact>]
let ``tryDecode returns Error for valid Base64 but not JSON`` () =
    // Base64 of "hello world"
    let cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello world"))
    match tryDecode cursor with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Expected Error for non-JSON payload")

[<Fact>]
let ``tryDecode returns Error when offset field is missing`` () =
    let cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"page":1}"""))
    match tryDecode cursor with
    | Error msg -> Assert.Contains("offset", msg)
    | Ok _ -> Assert.Fail("Expected Error when offset field is missing")

[<Fact>]
let ``tryDecode returns Error when offset is negative`` () =
    let cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"offset":-1}"""))
    match tryDecode cursor with
    | Error msg -> Assert.Contains("non-negative", msg)
    | Ok _ -> Assert.Fail("Expected Error for negative offset")

[<Fact>]
let ``tryDecode returns Error when offset is a string`` () =
    let cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"offset":"five"}"""))
    match tryDecode cursor with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Expected Error when offset is a string")

// ─── C. paginationFields ──────────────────────────────────────────────────────

[<Fact>]
let ``paginationFields truncated=false when all items fit on one page`` () =
    let fields = paginationFields 10 0 50 10
    let dict = fields |> Map.ofList

    let truncated = dict["truncated"].GetValue<bool>()
    Assert.False(truncated)

[<Fact>]
let ``paginationFields truncated=true when more items remain`` () =
    let fields = paginationFields 100 0 50 50
    let dict = fields |> Map.ofList

    let truncated = dict["truncated"].GetValue<bool>()
    Assert.True(truncated)

[<Fact>]
let ``paginationFields nextCursor is null when not truncated`` () =
    let fields = paginationFields 10 0 50 10
    let dict = fields |> Map.ofList

    Assert.Null(dict["nextCursor"])

[<Fact>]
let ``paginationFields nextCursor is a non-null string when truncated`` () =
    let fields = paginationFields 100 0 50 50
    let dict = fields |> Map.ofList

    let cursor = dict["nextCursor"]
    Assert.NotNull(cursor)
    let cursorStr = cursor.GetValue<string>()
    Assert.NotEmpty(cursorStr)

[<Fact>]
let ``paginationFields nextCursor offset equals pageOffset plus pageCount`` () =
    // First page: offset 0, size 50, got 50 items
    let fields = paginationFields 120 0 50 50
    let dict = fields |> Map.ofList
    let cursorStr = dict["nextCursor"].GetValue<string>()

    match tryDecode cursorStr with
    | Ok payload -> Assert.Equal(50, payload.offset)
    | Error msg -> Assert.Fail($"nextCursor did not decode: {msg}")

[<Fact>]
let ``paginationFields second page cursor offset advances correctly`` () =
    // Second page: offset 50, pageSize 50, got 50 → expect cursor at 100
    let fields = paginationFields 150 50 50 50
    let dict = fields |> Map.ofList
    let cursorStr = dict["nextCursor"].GetValue<string>()

    match tryDecode cursorStr with
    | Ok payload -> Assert.Equal(100, payload.offset)
    | Error msg -> Assert.Fail($"nextCursor did not decode: {msg}")

[<Fact>]
let ``paginationFields totalEstimate files equals totalCount`` () =
    let fields = paginationFields 87 0 50 50
    let dict = fields |> Map.ofList
    let totalEstimate = dict["totalEstimate"]
    let filesCount = totalEstimate["files"].GetValue<int>()
    Assert.Equal(87, filesCount)

// ─── D. Filter regex matching (isolated helper simulation) ────────────────────

// The real matching logic lives inside FcsBridge.ProjectOutline, which requires a
// live FCS project. We test the conceptual behaviour here by replicating the
// lightweight predicate for isolation — confirming the regex contract independently
// of the full outline pipeline.

let private matchesRegexFilter (pattern: string) (name: string) (signature: string) =
    let rx = System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
    rx.IsMatch(name) || rx.IsMatch(signature)

[<Fact>]
let ``filter regex matches on member name`` () =
    Assert.True(matchesRegexFilter "Timer" "MyTimer" "unit -> unit")

[<Fact>]
let ``filter regex matches on signature when name does not match`` () =
    Assert.True(matchesRegexFilter "MailboxProcessor" "runAgent" "MailboxProcessor<int> -> unit")

[<Fact>]
let ``filter regex is case-insensitive`` () =
    Assert.True(matchesRegexFilter "mailboxprocessor" "MyMailboxProcessor" "")

[<Fact>]
let ``filter regex returns false when neither name nor signature matches`` () =
    Assert.False(matchesRegexFilter "Channel" "openFile" "string -> Result<unit, exn>")

[<Fact>]
let ``filter alternation regex matches any of the patterns`` () =
    // Simulates: Event|Timer|MailboxProcessor|Channel|BackgroundService
    let pattern = "Event|Timer|MailboxProcessor|Channel|BackgroundService"
    Assert.True(matchesRegexFilter pattern "myTimer" "unit -> unit")
    Assert.True(matchesRegexFilter pattern "EventLoop" "unit -> unit")
    Assert.True(matchesRegexFilter pattern "processMsg" "MailboxProcessor<int> -> unit")
    Assert.False(matchesRegexFilter pattern "doWork" "string -> bool")

// ─── E. Default shape — FcsProjectOutlineArgs ─────────────────────────────────

[<Fact>]
let ``FcsProjectOutlineArgs all optional fields default to None`` () =
    // This test confirms the record compiles with None for all new optional fields.
    // It acts as a compile-time guard: if a required field were accidentally added
    // without a default, this would fail to compile.
    let args: FcsProjectOutlineArgs =
        { projectPath = "/some/project.fsproj"
          workspacePath = None
          includePrivate = None
          includeTests = None
          includeGeneratedFiles = None
          maxFiles = None           // default 50 at runtime
          maxResultsPerFile = None  // default 30 at runtime
          summaryOnly = None        // default true at runtime
          cursor = None
          filter = None
          nameContains = None }

    Assert.Equal("/some/project.fsproj", args.projectPath)
    Assert.True(args.summaryOnly.IsNone, "summaryOnly should be None (uses runtime default)")
    Assert.True(args.maxFiles.IsNone, "maxFiles should be None (uses runtime default of 50)")
    Assert.True(args.maxResultsPerFile.IsNone, "maxResultsPerFile should be None (runtime default 30)")
    Assert.True(args.cursor.IsNone)
    Assert.True(args.filter.IsNone)
    Assert.True(args.nameContains.IsNone)

[<Fact>]
let ``FcsProjectOutlineArgs accepts explicit values for all new fields`` () =
    let args: FcsProjectOutlineArgs =
        { projectPath = "/some/project.fsproj"
          workspacePath = None
          includePrivate = Some true
          includeTests = Some false
          includeGeneratedFiles = Some false
          maxFiles = Some 100
          maxResultsPerFile = Some 60
          summaryOnly = Some false
          cursor = Some (encode 50)
          filter = Some "Timer|Channel"
          nameContains = Some [ "Event"; "Timer" ] }

    Assert.Equal(Some 100, args.maxFiles)
    Assert.Equal(Some 60, args.maxResultsPerFile)
    Assert.Equal(Some false, args.summaryOnly)
    Assert.True(args.cursor.IsSome)
    Assert.Equal(Some "Timer|Channel", args.filter)
    Assert.Equal(Some [ "Event"; "Timer" ], args.nameContains)

[<Fact>]
let ``cursor stored in FcsProjectOutlineArgs round-trips correctly`` () =
    let cursorStr = encode 42
    let args: FcsProjectOutlineArgs =
        { projectPath = "/p.fsproj"
          workspacePath = None
          includePrivate = None
          includeTests = None
          includeGeneratedFiles = None
          maxFiles = None
          maxResultsPerFile = None
          summaryOnly = None
          cursor = Some cursorStr
          filter = None
          nameContains = None }

    match args.cursor with
    | None -> Assert.Fail("cursor was None")
    | Some c ->
        match tryDecode c with
        | Ok payload -> Assert.Equal(42, payload.offset)
        | Error msg -> Assert.Fail($"cursor did not decode: {msg}")
