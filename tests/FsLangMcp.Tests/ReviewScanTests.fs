module FsLangMcp.Tests.ReviewScanTests

// ─── #65: fcs_review_scan — read-only, AST-based review-candidate inventory ────────
//
// The tool PARSES F# source (parse-only, no type-check, no restore) and tags
// structurally interesting sites — review CANDIDATES, never asserted bugs. These tests
// drive a hand-written fixture whose every category sits on a known line, so a found
// candidate's `range` can be checked against the exact source line it points at.

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── The 5-category fixture required by #65 ─────────────────────────────────────────
// One occurrence each of: a wildcard match, a try/with, a failwith, a mutable binding,
// and a `.Result` blocking call. Built line-by-line so line numbers are deterministic.

let private fixtureSource =
    String.concat
        "\n"
        [ "module ReviewFixture" //                                            1
          "" //                                                                2
          "let classify (x: int) =" //                                         3
          "    match x with" //                                                4
          "    | 0 -> \"zero\"" //                                             5
          "    | _ -> \"other\"" //                          match_wildcard    6
          "" //                                                                7
          "let risky () =" //                                                  8
          "    try" //                                       try_with          9
          "        failwith \"boom\"" //                     raise_or_failwith 10
          "    with ex ->" //                                                  11
          "        ()" //                                                      12
          "" //                                                                13
          "let counter () =" //                                                14
          "    let mutable total = 0" //                     mutable_binding   15
          "    total <- total + 1" //                                          16
          "    total" //                                                       17
          "" //                                                                18
          "let blocking (t: System.Threading.Tasks.Task<int>) =" //            19
          "    t.Result" ] //                                blocking_call     20

let private fixtureLines = fixtureSource.Split('\n')

/// 1-based source line carrying the given marker substring.
let private lineOf (marker: string) =
    (fixtureLines |> Array.findIndex (fun l -> l.Contains marker)) + 1

let private writeSource (fileName: string) (content: string) =
    let dir = Path.Combine(Path.GetTempPath(), $"fslangmcp_review_{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    let file = Path.Combine(dir, fileName)
    File.WriteAllText(file, content)
    file

let private writeFixture () = writeSource "ReviewFixture.fs" fixtureSource

let private fileArgs (path: string) (categories: string list option) (maxResults: int option) : FcsReviewScanArgs =
    { path = Some path
      projectPath = None
      categories = categories
      maxResults = maxResults }

// ── JSON helpers ───────────────────────────────────────────────────────────────────

let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()
let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private gb (node: JsonNode) (key: string) = node[key].GetValue<bool>()

let private arr (node: JsonNode) (key: string) =
    match node[key] with
    | :? JsonArray as a -> a |> Seq.toList
    | _ -> []

let private candidates (result: JsonNode) = arr result "candidates"
let private categoryOf (c: JsonNode) = gs c "category"

let private startLineOf (c: JsonNode) =
    let range = c["range"]
    gi range "startLine"

let private lineTextOf (c: JsonNode) = gs c "lineText"

let private candidatesOf (result: JsonNode) (category: string) =
    candidates result |> List.filter (fun c -> categoryOf c = category)

/// The single candidate of a category; fails the test if there isn't exactly one.
let private only (result: JsonNode) (category: string) =
    match candidatesOf result category with
    | [ c ] -> c
    | other -> failwith $"expected exactly one {category} candidate, got {other.Length}"

let private byCategoryCount (result: JsonNode) (category: string) =
    let counts = result["counts"]
    let byCat = counts["byCategory"]

    match byCat[category] with
    | null -> 0
    | n -> n.GetValue<int>()

let private total (result: JsonNode) =
    let counts = result["counts"]
    gi counts "total"

let private returned (result: JsonNode) =
    let counts = result["counts"]
    gi counts "returned"

// ─── Each required category is found with a correct range ──────────────────────────

[<Fact>]
let ``every required category is surfaced exactly once`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) None None)

        Assert.Equal("succeeded", gs result "status")
        Assert.Equal("file", gs result "mode")

        for category in [ "match_wildcard"; "try_with"; "raise_or_failwith"; "mutable_binding"; "blocking_call" ] do
            Assert.Equal(1, byCategoryCount result category)
    }

[<Fact>]
let ``the wildcard match candidate points at the wildcard clause line`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) None None)

        let c = only result "match_wildcard"
        Assert.Equal(lineOf "| _ ->", startLineOf c)
        Assert.Contains("_", lineTextOf c)
    }

[<Fact>]
let ``the try-with candidate points at the try line`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) None None)

        let c = only result "try_with"
        Assert.Equal(lineOf "    try", startLineOf c)
        Assert.Contains("try", lineTextOf c)
    }

[<Fact>]
let ``the failwith candidate points at the failwith line`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) None None)

        let c = only result "raise_or_failwith"
        Assert.Equal(lineOf "failwith", startLineOf c)
        Assert.Contains("failwith", lineTextOf c)
    }

[<Fact>]
let ``the mutable binding candidate points at the mutable line`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) None None)

        let c = only result "mutable_binding"
        Assert.Equal(lineOf "mutable total", startLineOf c)
        Assert.Contains("mutable", lineTextOf c)
    }

[<Fact>]
let ``the blocking-call candidate points at the dot-Result line`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) None None)

        let c = only result "blocking_call"
        Assert.Equal(lineOf "t.Result", startLineOf c)
        Assert.Contains("Result", lineTextOf c)
    }

[<Fact>]
let ``notes are neutral and never call a candidate a bug`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) None None)

        for c in candidates result do
            let note = (gs c "note").ToLowerInvariant()
            Assert.DoesNotContain("bug", note)
    }

// ─── The categories filter narrows the result set ──────────────────────────────────

[<Fact>]
let ``filtering to try_with returns only try_with candidates`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) (Some [ "try_with" ]) None)

        Assert.Equal("succeeded", gs result "status")

        let cats = candidates result |> List.map categoryOf |> List.distinct
        Assert.Equal<string list>([ "try_with" ], cats)

        // counts.total reflects only the filtered category; the others are absent.
        Assert.Equal(1, total result)
        Assert.Equal(0, byCategoryCount result "match_wildcard")
        Assert.Equal(0, byCategoryCount result "blocking_call")
    }

[<Fact>]
let ``an unknown category is rejected with invalid_args`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) (Some [ "not_a_category" ]) None)

        Assert.Equal("invalid_args", gs result "status")
        Assert.Contains("not_a_category", gs result "message")
    }

// ─── maxResults caps the candidate list but keeps honest totals ────────────────────

[<Fact>]
let ``maxResults caps candidates while counts.total stays the full count`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.ReviewScan(fileArgs (writeFixture ()) None (Some 2))

        Assert.Equal(2, (candidates result).Length)
        Assert.Equal(5, total result)
        Assert.Equal(2, returned result)
        Assert.True(gb result "truncated", "truncated must be set when total exceeds the page size")
    }

// ─── The Ident / DotGet / Downcast detection arms ──────────────────────────────────

let private castSource =
    String.concat
        "\n"
        [ "module Casts"
          ""
          "let f (o: obj) ="
          "    let s = o :?> string"
          "    let n = box 5"
          "    let t = typeof<int>"
          "    let ty = o.GetType()"
          "    let bad () = invalidOp \"no\""
          "    (s, n, t, ty, bad)" ]

[<Fact>]
let ``downcast, box, typeof, GetType and invalidOp are each tagged`` () : Task =
    task {
        let bridge = FcsBridge()
        let file = writeSource "Casts.fs" castSource
        let! result = bridge.ReviewScan(fileArgs file None None)

        Assert.Equal("succeeded", gs result "status")
        // o :?> string  +  box 5  →  two cast_or_box.
        Assert.Equal(2, byCategoryCount result "cast_or_box")
        // typeof<int>  +  o.GetType()  →  two reflection.
        Assert.Equal(2, byCategoryCount result "reflection")
        // invalidOp "no"  →  one raise_or_failwith.
        Assert.Equal(1, byCategoryCount result "raise_or_failwith")
        // None of these appear in this fixture.
        Assert.Equal(0, byCategoryCount result "try_with")
        Assert.Equal(0, byCategoryCount result "blocking_call")
    }

// ─── large_function fires on an over-long binding ──────────────────────────────────

[<Fact>]
let ``a binding whose body spans more than the threshold is flagged`` () : Task =
    task {
        // big's RHS spans ~67 lines (> the 60-line threshold); each inner let is one
        // line and stays well under it, so exactly one large_function is expected.
        let body = [ for i in 1..65 -> $"    let v{i} = {i}" ]
        let source = String.concat "\n" ([ "module Big"; ""; "let big () =" ] @ body @ [ "    v1" ])

        let bridge = FcsBridge()
        let file = writeSource "Big.fs" source
        let! result = bridge.ReviewScan(fileArgs file (Some [ "large_function" ]) None)

        Assert.Equal("succeeded", gs result "status")
        Assert.Equal(1, byCategoryCount result "large_function")
        Assert.Equal(3, startLineOf (only result "large_function")) // `let big () =` on line 3
    }

// ─── Target resolution: project mode and the no-target error ───────────────────────

[<Fact>]
let ``project mode scans the project's compiled files without a build`` () : Task =
    task {
        // Parse-only project mode reads <Compile> entries straight from the .fsproj XML —
        // no restore or build is needed.
        let dir = Path.Combine(Path.GetTempPath(), $"fslangmcp_review_{Guid.NewGuid():N}")
        Directory.CreateDirectory dir |> ignore
        let fixturePath = Path.Combine(dir, "ReviewFixture.fs")
        File.WriteAllText(fixturePath, fixtureSource)

        let fsprojPath = Path.Combine(dir, "Fixture.fsproj")

        File.WriteAllText(
            fsprojPath,
            String.concat
                "\n"
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                  "  <ItemGroup><Compile Include=\"ReviewFixture.fs\" /></ItemGroup>"
                  "</Project>" ]
        )

        let bridge = FcsBridge()

        let! result =
            bridge.ReviewScan
                { path = None
                  projectPath = Some fsprojPath
                  categories = None
                  maxResults = None }

        Assert.Equal("succeeded", gs result "status")
        Assert.Equal("project", gs result "mode")

        let scanned = arr result "scanned" |> List.map (fun n -> n.GetValue<string>())
        Assert.Contains(scanned, fun p -> p.EndsWith("ReviewFixture.fs"))
        Assert.Equal(5, total result)
    }

[<Fact>]
let ``no path and no projectPath yields invalid_args`` () : Task =
    task {
        let bridge = FcsBridge()

        let! result =
            bridge.ReviewScan
                { path = None
                  projectPath = None
                  categories = None
                  maxResults = None }

        Assert.Equal("invalid_args", gs result "status")
        Assert.Contains("target", gs result "message")
    }
