module FsLangMcp.Tests.FindFieldResolutionTests

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.FcsBridge
open FsLangMcp.Types

let private source = """module FieldRepro.Model

type Card =
    { Name: string
      Load: int64 -> Async<string option>
      mutable Attempts: int
      ``Part.Name``: string }
type Other = { Name: string }
type Unused = { Value: int }

let make () : Card =
    { Name = "a"; Load = (fun _ -> async { return None }); Attempts = 0; ``Part.Name`` = "p" }
let read (card: Card) = card.Name, card.Load 1L
let renamed (card: Card) = { card with Name = "b" }
let isA (card: Card) = match card with { Name = "a" } -> true | _ -> false
let bump (card: Card) = card.Attempts <- card.Attempts + 1
let part (card: Card) = card.``Part.Name``
let other () : Other = { Name = "other" }
"""

// A later project deliberately changes the interpretation of Card.Name, and an
// UNUSED whole type must also take precedence over Card.Load's actual field sites.
let private collisionSource = """module FieldRepro.Card
type Name = { Tag: int }
type Load = { Value: int }
let make () : Name = { Tag = 1 }
let read (value: Name) = value.Tag
"""

type FieldResolutionFixture() =
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_field_resolution_{Guid.NewGuid():N}")
    let aProject = Path.Combine(root, "A", "A.fsproj")
    let sourcePath = Path.Combine(root, "A", "Source.fs")
    let collisionPath = Path.Combine(root, "B", "Source.fs")
    let solution = Path.Combine(root, "Fields.slnx")
    let linkedSolution = Path.Combine(root, "Linked.slnx")
    let buildSolution = Path.Combine(root, "BuildAll.slnx")
    let missingSolution = Path.Combine(root, "Missing.slnx")

    do
        for name, contents in [ "A", source; "B", collisionSource ] do
            let directory = Path.Combine(root, name)
            Directory.CreateDirectory(directory) |> ignore
            File.WriteAllText(Path.Combine(directory, "Source.fs"), contents)
            File.WriteAllText(
                Path.Combine(directory, $"{name}.fsproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="Source.fs" /></ItemGroup></Project>""")
        File.WriteAllText(solution, """<Solution><Project Path="A/A.fsproj" /><Project Path="B/B.fsproj" /></Solution>""")
        File.WriteAllText(missingSolution, """<Solution><Project Path="A/A.fsproj" /><Project Path="Missing/Missing.fsproj" /></Solution>""")
        let linkedDirectory = Path.Combine(root, "C")
        Directory.CreateDirectory(linkedDirectory) |> ignore
        File.WriteAllText(Path.Combine(linkedDirectory, "C.fsproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="../A/Source.fs" Link="Source.fs" /></ItemGroup></Project>""")
        File.WriteAllText(linkedSolution, """<Solution><Project Path="A/A.fsproj" /><Project Path="C/C.fsproj" /></Solution>""")
        File.WriteAllText(buildSolution, """<Solution><Project Path="A/A.fsproj" /><Project Path="B/B.fsproj" /><Project Path="C/C.fsproj" /></Solution>""")

    let build =
        let dotnetHost =
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            |> Option.ofObj
            |> Option.defaultValue "dotnet"
        let startInfo = ProcessStartInfo(dotnetHost, $"build \"{buildSolution}\" -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false")
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        startInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] <- "1"
        use child = Process.Start(startInfo)
        let stdout = child.StandardOutput.ReadToEnd()
        let stderr = child.StandardError.ReadToEnd()
        child.WaitForExit()
        child.ExitCode, stdout + stderr

    // Tests in this class are sequential; the mutation test restores its source.
    // Reuse the FCS caches rather than cold-loading the same projects per case.
    let bridge = FcsBridge()

    member internal _.Bridge = bridge
    member _.Project = aProject
    member _.Source = sourcePath
    member _.Solution = solution
    member _.LinkedSolution = linkedSolution
    member _.MissingSolution = missingSolution
    member _.AssertBuilt() = Assert.True(fst build = 0, snd build)
    member _.SetCollision(enabled: bool) =
        let contents = if enabled then collisionSource else collisionSource.Replace("module FieldRepro.Card", "module FieldRepro.Unrelated")
        File.WriteAllText(collisionPath, contents)

    interface IDisposable with
        member _.Dispose() = Directory.Delete(root, true)

let private args project query : FindArgs =
    { query = query
      kind = Some "field"
      scope = None
      exact = None
      ``member`` = None
      field = None
      path = None
      line = None
      word = None
      occurrence = None
      character = None
      contextLines = None
      includeDeclaration = None
      includeInfo = None
      includePerProject = None
      includeSiteTypes = None
      projectPath = Some project
      maxResults = Some 100
      timeoutMs = Some 120_000
      cursor = None }

let private stringValue (response: JsonNode) (key: string) =
    match response[key] |> Option.ofObj with
    | Some value -> value.GetValue<string>()
    | None -> failwith $"Expected '{key}' in response: {response.ToJsonString()}"
let private siteSet (response: JsonNode) =
    response["sites"].AsArray()
    |> Seq.map _.ToJsonString()
    |> Set.ofSeq

type FindFieldResolutionTests(fixture: FieldResolutionFixture) =
    interface IClassFixture<FieldResolutionFixture>

    [<Theory>]
    [<InlineData("Name", false)>]
    [<InlineData("Attempts", false)>]
    [<InlineData("Load", false)>]
    [<InlineData("Name", true)>]
    [<InlineData("Part.Name", false)>]
    [<InlineData("``Part.Name``", false)>]
    member _.``dotted field spelling yields the explicit selector site set``(field: string, typed: bool) : Task = task {
        fixture.AssertBuilt()
        let bridge = fixture.Bridge
        let selector = field.Trim('`')
        let! expected = bridge.Find({ args fixture.Project "FieldRepro.Model.Card" with field = Some selector; includeSiteTypes = Some typed })
        let! actual = bridge.Find({ args fixture.Project $"Model.Card.{field}" with includeSiteTypes = Some typed })

        Assert.Equal("matched", stringValue actual "outcome")
        Assert.NotEmpty(siteSet expected)
        Assert.Equal<Set<string>>(siteSet expected, siteSet actual)
        Assert.Equal(expected["breakdown"].ToJsonString(), actual["breakdown"].ToJsonString())
        Assert.Equal(expected.["perProject"].[0].["fieldMatchUses"].GetValue<int>(), actual.["perProject"].[0].["fieldMatchUses"].GetValue<int>())
    }

    [<Fact>]
    member _.``field sites retain all five edit shapes without including another record``() : Task = task {
        fixture.AssertBuilt()
        let! response = fixture.Bridge.Find(args fixture.Project "Model.Card")
        let kinds = response["sites"].AsArray() |> Seq.map (fun site -> stringValue site "kind") |> Set.ofSeq
        Assert.Equal<Set<string>>(set [ "field-set-literal"; "field-set-update"; "field-set-mutation"; "field-pattern"; "field-read" ], kinds)
        Assert.All(response["sites"].AsArray(), fun site -> Assert.Contains("Model.Card.", stringValue site "symbolFullName"))
    }

    [<Theory>]
    [<InlineData("Name")>]
    [<InlineData("Load")>]
    member _.``a later whole declaring type takes precedence even when unused``(name: string) : Task = task {
        fixture.AssertBuilt()
        let bridge = fixture.Bridge
        let! expected = bridge.Find(args fixture.Solution $"FieldRepro.Card.{name}")
        let! actual = bridge.Find(args fixture.Solution $"Card.{name}")
        Assert.Equal<Set<string>>(siteSet expected, siteSet actual)
        Assert.Equal(stringValue expected "outcome", stringValue actual "outcome")
        if name = "Load" then Assert.Equal("not_found", stringValue actual "outcome")
        else Assert.NotEmpty(siteSet actual)
    }

    [<Theory>]
    [<InlineData("Name", "")>]
    [<InlineData("Name", "Name")>]
    [<InlineData("Model.Card.Missing", "")>]
    [<InlineData("Model.Card.Name", "Load")>]
    [<InlineData("Model.Card.Name", "Name")>]
    [<InlineData("Unused.Value", "")>]
    [<InlineData(".Name", "")>]
    member _.``zero field sites cannot match through FSAC and always explain the query contract``(query: string, selector: string) : Task = task {
        fixture.AssertBuilt()
        let calls = ResizeArray<string>()
        let probe name = calls.Add(name); Task.FromResult(FindFsacProbeResult.Available 7)
        let! response = fixture.Bridge.Find({ args fixture.Project query with field = if selector = "" then None else Some selector }, fsacProbe = probe)
        Assert.Equal("not_found", stringValue response "outcome")
        Assert.False(response.["resolution"].["matched"].GetValue<bool>())
        Assert.Equal(0, response["totalSites"].GetValue<int>())
        Assert.Empty(calls)
        Assert.Equal("not_needed", stringValue (response["resolution"]) "fsacFallbackState")
        let fallbackPhase =
            response.["coverage"].["phases"].AsArray()
            |> Seq.find (fun phase -> stringValue phase "phase" = "fsac_fallback")
        Assert.Equal("not_needed", stringValue fallbackPhase "status")
        Assert.Contains("declaring record type", stringValue response "hint")
        Assert.DoesNotContain("try the bare identifier", stringValue response "hint")
    }

    [<Fact>]
    member _.``incomplete coverage withholds shorthand and does not claim absence``() : Task = task {
        fixture.AssertBuilt()
        let! response = fixture.Bridge.Find(args fixture.MissingSolution "Model.Card.Name")
        Assert.Equal("indeterminate", stringValue response "outcome")
        Assert.False(response.["coverage"].["complete"].GetValue<bool>())
        Assert.Empty(siteSet response)
        Assert.Contains("incomplete", stringValue response "hint")
    }

    [<Fact>]
    member _.``an immediate field timeout does not claim a match or absence``() : Task = task {
        fixture.AssertBuilt()
        let! response = fixture.Bridge.Find({ args fixture.Project "Model.Card.Name" with timeoutMs = Some 0 })
        Assert.Equal("indeterminate", stringValue response "outcome")
        Assert.Empty(siteSet response)
    }

    [<Fact>]
    member _.``a later project timeout withholds provisional dotted matches``() : Task = task {
        fixture.AssertBuilt()
        let beforeLoad (project: string) =
            if project.EndsWith("B.fsproj", StringComparison.Ordinal) then
                Task.FromException(TimeoutException "Injected later-project timeout")
            else
                Task.CompletedTask
        let bridge = FcsBridge(projectEvaluationBeforeLoadOverride = beforeLoad)
        let! response = bridge.Find(args fixture.Solution "Model.Card.Name")
        Assert.Equal(1, response["projectsAnalyzed"].GetValue<int>())
        Assert.Equal(1, response["projectsTimedOut"].GetValue<int>())
        Assert.Equal("indeterminate", stringValue response "outcome")
        Assert.Empty(siteSet response)
        Assert.Contains("incomplete", stringValue response "hint")
    }

    [<Fact>]
    member _.``dotted continuation binds the original spelling and yields the explicit site set``() : Task = task {
        fixture.AssertBuilt()
        let bridge = fixture.Bridge
        let request = args fixture.Project "Model.Card.Name"
        let! first = bridge.Find({ request with maxResults = Some 1 })
        let cursor = stringValue first "nextCursor"
        let! next = bridge.Find({ request with cursor = Some cursor })
        let! expected = bridge.Find({ args fixture.Project "Model.Card" with field = Some "Name" })
        Assert.Equal("Model.Card.Name", stringValue first "query")
        Assert.Equal<Set<string>>(siteSet expected, Set.union (siteSet first) (siteSet next))
        let! rejected = bridge.Find({ request with query = "Model.Card"; field = Some "Name"; cursor = Some cursor })
        Assert.Equal("cursor_query_mismatch", stringValue rejected "errorKind")
    }

    [<Theory>]
    [<InlineData("auto")>]
    [<InlineData("symbol")>]
    [<InlineData("members")>]
    member _.``non-field kinds retain their legacy index fallback``(kind: string) : Task = task {
        fixture.AssertBuilt()
        let! response = fixture.Bridge.Find({ args fixture.Project "NoSuchSymbol" with kind = Some kind }, fsacProbe = fun _ -> Task.FromResult(FindFsacProbeResult.Available 1))
        Assert.Equal("matched", stringValue response "outcome")
        Assert.Equal("fsac-symbol-index", stringValue (response["resolution"]) "via")
    }

    [<Fact>]
    member _.``explicit selector and non-exact declaring-type searches retain their behavior``() : Task = task {
        fixture.AssertBuilt()
        let bridge = fixture.Bridge
        let! expected = bridge.Find({ args fixture.Project "Card" with field = Some "Name" })
        let! actual = bridge.Find({ args fixture.Project "model.car" with field = Some "Name"; exact = Some false })
        Assert.Equal<Set<string>>(siteSet expected, siteSet actual)
    }

    [<Fact>]
    member _.``linked-project dotted sites preserve explicit de-duplication and type metadata``() : Task = task {
        fixture.AssertBuilt()
        let request = { args fixture.LinkedSolution "Model.Card" with field = Some "Name"; includeSiteTypes = Some true }
        let! expected = fixture.Bridge.Find(request)
        let! actual = fixture.Bridge.Find({ request with query = "Model.Card.Name"; field = None })
        Assert.Equal("matched", stringValue actual "outcome")
        Assert.Equal<Set<string>>(siteSet expected, siteSet actual)
        Assert.Equal(expected["siteTypes"].ToJsonString(), actual["siteTypes"].ToJsonString())
        Assert.Equal(2, actual["projectsAnalyzed"].GetValue<int>())
        Assert.Equal(siteSet actual |> Set.count, actual["totalSites"].GetValue<int>())
    }

    [<Fact>]
    member _.``response expiry during dotted count selection cannot issue a complete cursor``() : Task = task {
        fixture.AssertBuilt()
        let expiry = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let beforeStep phase _ =
            if phase = "field-selection" then expiry.TrySetResult(()) |> ignore
        let bridge =
            FcsBridge(
                findResponseDeadlineSignalOverride = (fun () -> expiry.Task :> Task),
                findResponseConstructionBeforeStepOverride = beforeStep)
        let! response = bridge.Find({ args fixture.Project "Model.Card.Name" with maxResults = Some 1 })
        Assert.Equal("find_response_timeout", stringValue response "errorKind")
        Assert.False(response.["resolution"].["complete"].GetValue<bool>())
        Assert.True(response["totalSites"].GetValue<int>() > 0)
        Assert.True(response["nextCursor"] |> Option.ofObj |> Option.isNone)
    }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``adding or removing a whole-type competitor invalidates the dotted cursor``(initialCollision: bool) : Task = task {
        fixture.AssertBuilt()
        try
            fixture.SetCollision(initialCollision)
            let request = { args fixture.Solution "Card.Name" with maxResults = Some 1 }
            let! first = fixture.Bridge.Find(request)
            let cursor = stringValue first "nextCursor"
            fixture.SetCollision(not initialCollision)
            let! continuation = fixture.Bridge.Find({ request with cursor = Some cursor })
            Assert.Equal("cursor_stale", stringValue continuation "errorKind")
            Assert.Empty(siteSet continuation)
        finally
            fixture.SetCollision(true)
    }
