module FsLangMcp.Tests.CheckPresentationTests

open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Tests.CheckTests

let private args: CheckArgs =
    { scope = None; path = None; snippet = None; snippetPosition = None
      fileGlob = None; mode = None; speed = None; severity = None
      compact = None; maxDiagnostics = None; projectPath = None; timeoutMs = None }

let private integer (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private text (node: JsonNode) (key: string) = node[key].GetValue<string>()

[<Fact>]
let ``grouping preserves severity resolved type and meaningful message whitespace`` () =
    let make severity message resolvedType line =
        jobj [ "severity", jstr severity; "message", jstr message; "errorNumberText", jstr "FS0001"
               "resolvedType", jstr resolvedType; "file", jstr "File.fs"
               "range", jobj ["startLine", jint line] :> JsonNode ] :> JsonNode
    let nodes =
        [| make "Error" "same\r\nmessage" "A.T" 1
           make "Error" "same\nmessage" "A.T" 2
           make "Warning" "same\nmessage" "A.T" 3
           make "Error" "same\nmessage" "B.T" 4
           make "Error" "same  message" "A.T" 5
           make "Error" "same\nmessage  " "A.T" 6 |]
    let groups = CheckDiagnostics.group nodes
    Assert.Equal(5, groups.Length)
    Assert.Equal(2, integer groups[0] "count")
    let locations = groups[0]["locations"] :?> JsonArray
    Assert.Equal("File.fs:2", locations[0].GetValue<string>())
    Assert.Null(nodes[0]["count"])

[<Fact>]
let ``LSP compact locations convert zero based lines`` () =
    let make line =
        jobj [ "severity", jint 1; "code", jstr "FS0001"; "message", jstr "same"
               "file", jstr "File.fs"
               "range", jobj [ "start", jobj ["line", jint line] :> JsonNode ] :> JsonNode ] :> JsonNode
    let groups = CheckDiagnostics.group [| make 0; make 5 |]
    let locations = groups[0]["locations"] :?> JsonArray
    Assert.Equal("File.fs:6", locations[0].GetValue<string>())

[<Theory>]
[<InlineData(0)>]
[<InlineData(1001)>]
let ``invalid diagnostic caps fail before project discovery`` maxDiagnostics : Task =
    task {
        let! result = FcsBridge().Check({args with maxDiagnostics = Some maxDiagnostics})
        Assert.Equal("invalid_args", text result "status")
    }

type CheckPresentationTests(fx: CheckFixture) =
    interface IClassFixture<CheckFixture>

    [<Fact>]
    member _.``compact check groups before the row cap and full mode recovers every location`` () : Task =
        task {
            Assert.True(fx.BuildExitCode = 0, fx.BuildLog)
            fx.ResetClean()
            File.WriteAllText(fx.MainFs, "module Probe.Main\n" + String.concat "\n" [for i in 1..60 -> $"let value{i}: int = true"])
            let bridge = FcsBridge()
            let request = {args with projectPath = Some fx.ProbeFsproj}
            let! baseline = bridge.Check(request)
            let! compact = bridge.Check({request with compact = Some true})
            let! full = bridge.Check({request with maxDiagnostics = Some 1000})
            for result in [baseline; compact; full] do
                Assert.Equal("errors", text result "verdict")
                Assert.Equal(60, integer result "errorCount")
                Assert.Equal(60, integer result "totalDiagnostics")
                Assert.Equal(0, integer result "belowSeverityFloorCount")
            Assert.Equal(50, (baseline["diagnostics"] :?> JsonArray).Count)
            Assert.True(baseline["diagnosticsTruncated"].GetValue<bool>())
            let groups = compact["diagnostics"] :?> JsonArray
            Assert.Single(groups) |> ignore
            Assert.Equal(60, integer groups[0] "count")
            Assert.Equal(20, (groups[0]["locations"] :?> JsonArray).Count)
            Assert.Equal(39, integer groups[0] "omittedLocationCount")
            Assert.True((groups[0]["locationsTruncated"]).GetValue<bool>())
            Assert.Equal(1, integer compact "diagnosticGroupCount")
            Assert.Equal(60, (full["diagnostics"] :?> JsonArray).Count)
            Assert.False(full["diagnosticsTruncated"].GetValue<bool>())
        }

    [<Fact>]
    member _.``real FS3520 is counted below the explicit floor and severity all surfaces it`` () : Task =
        task {
            Assert.True(fx.BuildExitCode = 0, fx.BuildLog)
            fx.ResetClean()
            File.WriteAllText(fx.MainFs, "module Probe.Main\nlet f x =\n    /// misplaced\n    x + 1\n")
            let bridge = FcsBridge()
            let request = {args with projectPath = Some fx.ProbeFsproj}
            let! filtered = bridge.Check(request)
            Assert.Equal("clean", text filtered "verdict")
            Assert.Equal(1, integer filtered "infoCount")
            Assert.Equal(1, integer filtered "belowSeverityFloorCount")
            Assert.Contains("severity=\"all\"", text filtered "diagnosticsNote")
            Assert.Empty(filtered["diagnostics"] :?> JsonArray)
            let! all = bridge.Check({request with severity = Some "all"; compact = Some true})
            Assert.Equal("clean", text all "verdict")
            Assert.Equal(1, integer all "infoCount")
            Assert.Equal(0, integer all "belowSeverityFloorCount")
            Assert.Null(all["diagnosticsNote"])
            let diagnostics = all["diagnostics"] :?> JsonArray
            Assert.Single(diagnostics) |> ignore
            Assert.Equal("FS3520", text diagnostics[0] "errorNumberText")
        }

let private responseForBudget (diagnostics: JsonNode array) extra : JsonNode =
    jobj ([ "status", jstr "succeeded"; "verdict", jstr "errors"; "analyzed", jbool true
            "groundTruth", jbool true; "errorCount", jint 1100; "warningCount", jint 0
            "infoCount", jint 0; "totalDiagnostics", jint 1100
            "surfacedDiagnosticCount", jint 1100; "diagnosticGroupCount", jint 50
            "diagnostics", JsonArray(diagnostics) :> JsonNode; "diagnosticsTruncated", jbool false ] @ extra) :> JsonNode

[<Fact>]
let ``production JSON budget reduces repeated long locations without corrupting group counts`` () =
    let path = "/" + String.replicate 290 "p" + "/Source.fs"
    let nodes =
        [| for group in 1 .. 50 do
               for occurrence in 1 .. 22 do
                   yield jobj ["severity", jstr "Error"; "errorNumberText", jstr "FS0001"
                               "message", jstr $"Distinct group {group}: \"quoted\" and\nnewlines"
                               "file", jstr path; "range", jobj ["startLine", jint occurrence] :> JsonNode] :> JsonNode |]
    let response = responseForBudget (CheckDiagnostics.group nodes) []
    Assert.True(Tools.renderToken(response).Length > CheckDiagnostics.ResponseBudgetChars)
    let bounded = CheckDiagnostics.boundResponse response
    Assert.True(Tools.renderToken(bounded).Length <= CheckDiagnostics.ResponseBudgetChars)
    Assert.Equal("errors", text bounded "verdict")
    Assert.Equal(1100, integer bounded "totalDiagnostics")
    Assert.Equal(50, integer bounded "diagnosticGroupCount")
    Assert.Equal(1100, integer bounded "surfacedDiagnosticCount")
    let groups = bounded["diagnostics"].AsArray()
    Assert.Equal(50, groups.Count)
    Assert.True(bounded["responseTruncatedByBudget"].GetValue<bool>())
    Assert.False(bounded["diagnosticsTruncated"].GetValue<bool>())
    for group in groups do
        Assert.Equal(22, integer group "count")
        Assert.Equal(21, integer group "omittedLocationCount")
        Assert.Empty(group["locations"].AsArray())
        Assert.True(group["locationsTruncated"].GetValue<bool>())

[<Fact>]
let ``production JSON budget caps full rows and preserves a maximal fitting prefix`` () =
    let nodes =
        [| for i in 1 .. 1000 ->
               jobj ["severity", jstr "Error"; "message", jstr (String.replicate 120 "\"\n\\")
                     "ordinal", jint i] :> JsonNode |]
    let response = CheckDiagnostics.boundResponse (responseForBudget nodes [])
    Assert.True(Tools.renderToken(response).Length <= CheckDiagnostics.ResponseBudgetChars)
    let shown = response["diagnostics"].AsArray()
    Assert.InRange(shown.Count, 1, 999)
    Assert.True(response["diagnosticsTruncatedByBudget"].GetValue<bool>())
    Assert.True(response["diagnosticsTruncated"].GetValue<bool>())
    Assert.Equal(1100, integer response "totalDiagnostics")
    for i in 0 .. shown.Count - 1 do
        Assert.Equal(i + 1, integer shown[i] "ordinal")
    shown.Add(nodes[shown.Count])
    Assert.True(Tools.renderToken(response).Length > CheckDiagnostics.ResponseBudgetChars)

[<Fact>]
let ``oversized coverage metadata returns a bounded unknown instead of partial clean evidence`` () =
    let response = responseForBudget [||] ["coverage", jstr (String.replicate 70000 "x")]
    let bounded = CheckDiagnostics.boundResponse response
    Assert.True(Tools.renderToken(bounded).Length <= CheckDiagnostics.ResponseBudgetChars)
    Assert.Equal("unknown", text bounded "verdict")
    Assert.Equal("check_response_metadata_exceeds_budget", text bounded "errorKind")
    Assert.False(bounded["groundTruth"].GetValue<bool>())
    Assert.Equal(1100, integer bounded "totalDiagnostics")
