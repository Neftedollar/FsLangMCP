module FsLangMcp.Tests.ConstructorDiagnosticsTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open Xunit
open FsLangMcp
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Tests.CheckTests

let private checker = FSharpChecker.Create(keepAssemblyContents = true)

// A genuine FCS name collision with System.Collections.Generic.HashSet, without
// adding a third-party package to the test restore graph. The later open selects
// this persistent-style seq constructor, reproducing #322's Adaptive ambiguity.
let private definitions =
    """module Persistent =
    type HashSet<'T>(items: seq<'T>) =
        member _.Items = items
open System.Collections.Generic
open Persistent
"""

let private collision =
    definitions + "let value = HashSet<int>(EqualityComparer<int>.Default)\n"

let private analyze source =
    task {
        let file = Path.Combine(Path.GetTempPath(), "ConstructorProbe.fsx")
        let text = SourceText.ofString source

        let! options, _ =
            checker.GetProjectOptionsFromScript(file, text, useSdkRefs = true, assumeDotNetFramework = false)
            |> Async.StartAsTask

        checker.InvalidateConfiguration options
        let! parseResults, answer = checker.ParseAndCheckFileInProject(file, 0, text, options) |> Async.StartAsTask

        match answer with
        | FSharpCheckFileAnswer.Succeeded results ->
            let errors =
                results.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

            let enrichment = ConstructorDiagnostics.fromFileResults parseResults results
            return errors, enrichment
        | _ -> return failwith "Expected a completed FCS file check"
    }

let private diagnosticNodes (result: JsonNode) =
    result["diagnostics"].AsArray() |> Seq.toArray

let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()

let private checkArgs projectPath : CheckArgs =
    { scope = None
      path = None
      snippet = None
      snippetPosition = None
      fileGlob = None
      mode = None
      speed = None
      severity = None
      compact = None
      maxDiagnostics = None
      projectPath = Some projectPath
      timeoutMs = None }

[<Fact>]
let ``real name collision resolves the constructor rather than its comparer argument`` () : Task =
    task {
        let! errors, enrichment = analyze collision
        Assert.NotEmpty errors
        Assert.All(errors, fun error -> Assert.Equal(193, error.ErrorNumber))
        Assert.Equal(errors.Length, enrichment.Count)

        for error in errors do
            let resolution = enrichment[error]
            Assert.Equal("ConstructorProbe.Persistent.HashSet`1", resolution.ResolvedType)
            let identifierRange = resolution.IdentifierRange
            Assert.Equal(6, identifierRange.StartLine)
            Assert.Equal(12, identifierRange.StartColumn)
            Assert.Equal(19, identifierRange.EndColumn)
            Assert.DoesNotContain(resolution.ResolvedType, [ "System.Collections.Generic.EqualityComparer`1" ])
    }

[<Theory>]
[<InlineData("let value = new HashSet<int>(EqualityComparer<int>.Default)")>]
[<InlineData("let value = Persistent.HashSet<int>(EqualityComparer<int>.Default)")>]
[<InlineData("let value = HashSet<int>(\n    EqualityComparer<int>.Default\n)")>]
[<InlineData("type Alias<'T> = Persistent.HashSet<'T>\nlet value = Alias<int>(EqualityComparer<int>.Default)")>]
let ``constructor resolution follows syntax and aliases`` expression : Task =
    task {
        let! errors, enrichment = analyze (definitions + expression + "\n")
        Assert.NotEmpty errors
        Assert.NotEmpty enrichment

        Assert.All(
            enrichment.Values,
            fun resolution -> Assert.Equal("ConstructorProbe.Persistent.HashSet`1", resolution.ResolvedType)
        )
    }

[<Theory>]
[<InlineData("let good = HashSet<int>([1])\nlet unrelated: string = 42")>]
[<InlineData("let value = (HashSet<int>([1]), (42: string))")>]
[<InlineData("let takesString (value: string) = 1\nlet value = HashSet<int>([takesString 42])")>]
[<InlineData("let HashSet<'T> (value: int) = value\nlet value = HashSet<int>(\"bad\")")>]
[<InlineData("let value = UnknownSet<int>(EqualityComparer<int>.Default)")>]
[<InlineData("let value = HashSet<int>([\"bad\"])")>]
let ``unrelated and nested expression errors do not acquire a nearby constructor`` expression : Task =
    task {
        let! errors, enrichment = analyze (definitions + expression + "\n")
        Assert.NotEmpty errors
        Assert.Empty enrichment
    }

[<Fact>]
let ``competing nested constructor targets are deliberately not attributed`` () : Task =
    task {
        let source =
            """type Inner(value: int) = class end
type Outer(value: string) = class end
let value = Outer(Inner(42))
"""

        let! errors, enrichment = analyze source
        Assert.NotEmpty errors
        Assert.Empty enrichment
    }

[<Fact>]
let ``fully qualified intended constructor is clean and needs no enrichment`` () : Task =
    task {
        let! errors, enrichment =
            analyze (
                definitions
                + "let value = System.Collections.Generic.HashSet<int>(EqualityComparer<int>.Default)\n"
            )

        Assert.Empty errors
        Assert.Empty enrichment
    }

[<Fact>]
let ``enrichment preserves compiler text and cannot migrate to a different diagnostic instance`` () : Task =
    task {
        let! errors, enrichment = analyze collision
        let original = errors[0]

        let unchanged =
            jobj [ "message", jstr original.Message; "errorNumber", jint original.ErrorNumber ] :> JsonNode

        let enriched = ConstructorDiagnostics.enrich enrichment original unchanged
        Assert.Same(unchanged, enriched)
        Assert.Equal(original.Message, gs enriched "message")
        Assert.Equal("ConstructorProbe.Persistent.HashSet`1", gs enriched "resolvedType")

        // Exactly the same path/range/message in a separate check is not evidence
        // for this instance. Linked workspace sources can resolve differently.
        let! otherErrors, otherEnrichment = analyze (collision.Replace("Persistent", "OtherTypes"))
        Assert.Equal(original.Message, otherErrors[0].Message)
        Assert.Equal(original.StartColumn, otherErrors[0].StartColumn)
        Assert.Equal("ConstructorProbe.OtherTypes.HashSet`1", otherEnrichment[otherErrors[0]].ResolvedType)
        let other = jobj [ "message", jstr otherErrors[0].Message ] :> JsonNode
        ConstructorDiagnostics.enrich enrichment otherErrors[0] other |> ignore
        Assert.Null(other["resolvedType"])
        let combined = ConstructorDiagnostics.combine [ enrichment; otherEnrichment ]
        Assert.Equal(enrichment.Count + otherEnrichment.Count, combined.Count)
        Assert.Equal("ConstructorProbe.Persistent.HashSet`1", combined[original].ResolvedType)
        Assert.Equal("ConstructorProbe.OtherTypes.HashSet`1", combined[otherErrors[0]].ResolvedType)
    }

type ConstructorDiagnosticCheckTests(fx: CheckFixture) =
    interface IClassFixture<CheckFixture>

    [<Theory>]
    [<InlineData("file")>]
    [<InlineData("project")>]
    [<InlineData("workspace")>]
    [<InlineData("snippet")>]
    member _.``trusted check exposes resolved constructor for every existing scope`` scope : Task =
        task {
            Assert.True(fx.BuildExitCode = 0, fx.BuildLog)
            fx.ResetClean()
            let source = "module Probe.Main\n" + collision
            let bridge = FcsBridge()

            let args =
                match scope with
                | "snippet" ->
                    { checkArgs fx.ProbeFsproj with
                        scope = Some scope
                        snippet = Some source }
                | "workspace" ->
                    File.WriteAllText(fx.MainFs, source)

                    { checkArgs fx.ProbeSlnx with
                        scope = Some scope }
                | _ ->
                    File.WriteAllText(fx.MainFs, source)

                    { checkArgs fx.ProbeFsproj with
                        scope = Some scope
                        path = if scope = "file" then Some fx.MainFs else None }

            let! result = bridge.Check args
            Assert.Equal("errors", gs result "verdict")
            let diagnostics = diagnosticNodes result
            Assert.NotEmpty diagnostics

            Assert.All(
                diagnostics,
                fun diagnostic ->
                    Assert.Equal("FS0193", gs diagnostic "errorNumberText")
                    Assert.NotNull(diagnostic["resolvedType"])
                    Assert.Equal("Probe.Main.Persistent.HashSet`1", gs diagnostic "resolvedType")
                    Assert.Equal(7, (diagnostic["resolvedConstructorRange"]["startLine"]).GetValue<int>())
                    Assert.Contains("EqualityComparer<int>", gs diagnostic "message")
                    Assert.Contains("int seq", gs diagnostic "message")
            )
        }

    [<Fact>]
    member _.``trusted file check does not add resolution to unrelated errors``() : Task =
        task {
            Assert.True(fx.BuildExitCode = 0, fx.BuildLog)
            fx.ResetClean()

            File.WriteAllText(
                fx.MainFs,
                "module Probe.Main\n"
                + definitions
                + "let good = HashSet<int>([1])\nlet bad: string = 42\n"
            )

            let bridge = FcsBridge()

            let! result =
                bridge.Check
                    { checkArgs fx.ProbeFsproj with
                        scope = Some "file"
                        path = Some fx.MainFs }

            Assert.Equal("errors", gs result "verdict")
            Assert.All(diagnosticNodes result, fun diagnostic -> Assert.Null(diagnostic["resolvedType"]))
        }
