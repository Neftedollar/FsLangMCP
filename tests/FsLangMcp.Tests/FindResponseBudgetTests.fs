module FsLangMcp.Tests.FindResponseBudgetTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Cursor
open FsLangMcp.FcsBridge
open FsLangMcp.Tools
open FsLangMcp.Types

let private dotnetHost =
    Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.defaultValue "/Users/roman/.dotnet/dotnet"

let private jsonArrayPrefix (nodes: JsonNode array) count =
    nodes
    |> Array.take count
    |> Array.map (fun node -> node.DeepClone())
    |> JsonArray
    :> JsonNode

let private repeatedNodes key count chars =
    Array.init count (fun index ->
        jobj
            [ "index", jint index
              key, jstr (String.replicate chars "界") ]
        :> JsonNode)

type FindBudgetFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_budget_{runId}")
    let sourcePath = Path.Combine(root, "Budget.fs")
    let projectPath = Path.Combine(root, "Budget.fsproj")
    let solutionPath = Path.Combine(root, "Budget.slnx")

    let longComment = String.replicate 1_800 "🙂界"

    let source =
        [ "module FindBudget.Fixture"
          ""
          "let target = 1" ]
        @ [ for index in 0..31 -> $"let value{index:D2} = target + {index} // {longComment}" ]
        |> String.concat "\n"

    do
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(sourcePath, source)

        File.WriteAllText(
            projectPath,
            String.concat
                Environment.NewLine
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                  "  <ItemGroup><Compile Include=\"Budget.fs\" /></ItemGroup>"
                  "</Project>" ]
        )

        File.WriteAllText(
            solutionPath,
            String.concat
                Environment.NewLine
                [ "<Solution>"
                  "  <Project Path=\"Budget.fsproj\" />"
                  "  <Project Path=\"Missing.fsproj\" />"
                  "</Solution>" ]
        )

    let buildInfo =
        let startInfo =
            ProcessStartInfo(
                dotnetHost,
                $"build \"{projectPath}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
            )

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

    member _.SourcePath = sourcePath
    member _.ProjectPath = projectPath
    member _.SolutionPath = solutionPath
    member _.BuildExitCode = fst buildInfo
    member _.BuildLog = snd buildInfo

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                Directory.Delete(root, true)

let private findArgs (fixture: FindBudgetFixture) cursor =
    { query = "target"
      kind = Some "symbol"
      scope = Some "project"
      exact = Some true
      ``member`` = None
      field = None
      path = Some fixture.SourcePath
      line = None
      word = None
      occurrence = None
      character = None
      contextLines = Some Int32.MaxValue
      includeDeclaration = Some true
      includeInfo = Some true
      includePerProject = Some true
      includeSiteTypes = Some false
      projectPath = Some fixture.ProjectPath
      maxResults = Some 1000
      timeoutMs = None
      cursor = cursor }

[<Fact>]
let ``bounded source slices stay valid Unicode and preserve UTF-16 source offsets`` () =
    let source = String.replicate 300 "🙂" + "target" + String.replicate 400 "界"
    let targetStart = source.IndexOf("target", StringComparison.Ordinal)
    let snippet = FindResponseBudget.boundedSnippet targetStart (targetStart + "target".Length) source

    Assert.True(snippet.Truncated)
    Assert.True(snippet.Text.Length <= FindResponseBudget.MaxSnippetChars)
    Assert.Contains("target", snippet.Text)

    Assert.Equal(
        source.Substring(snippet.SourceStartColumn, snippet.SourceEndColumn - snippet.SourceStartColumn),
        snippet.Text
    )

    Assert.Equal(source.Length, snippet.SourceLength)
    Assert.False(snippet.Text.Length > 0 && Char.IsLowSurrogate(snippet.Text[0]))
    Assert.False(snippet.Text.Length > 0 && Char.IsHighSurrogate(snippet.Text[snippet.Text.Length - 1]))

[<Fact>]
let ``planner measures sites diagnostics and per-project metadata with the production serializer`` () =
    let sites = repeatedNodes "site" 2 1_000
    let diagnostics = repeatedNodes "diagnostic" 2 32_000
    let projects = repeatedNodes "project" 2 32_000

    let build siteCount diagnosticCount projectCount =
        jobj
            [ "status", jstr "succeeded"
              "sites", jsonArrayPrefix sites siteCount
              "projectDiagnostics", jsonArrayPrefix diagnostics diagnosticCount
              "perProject", jsonArrayPrefix projects projectCount ]
        :> JsonNode

    match
        FindResponseBudget.planResponse
            FindResponseBudget.MaxSerializedChars
            sites.Length
            diagnostics.Length
            projects.Length
            build
    with
    | FindResponseBudget.FitPlan.Fits(siteCount, diagnosticCount, projectCount) ->
        Assert.Equal(sites.Length, siteCount)
        Assert.True(diagnosticCount < diagnostics.Length, "oversized diagnostics must participate in the ceiling")
        Assert.True(projectCount < projects.Length, "oversized per-project metadata must participate in the ceiling")
        let response = build siteCount diagnosticCount projectCount
        Assert.Equal((renderToken response).Length, renderedLength response)
        Assert.True(renderedLength response <= FindResponseBudget.MaxSerializedChars)
    | result -> Assert.Fail($"expected a fitting bounded plan, got {result}")

[<Fact>]
let ``planner worst-case metadata trimming probe count is bounded`` () =
    let mutable probes = 0

    let build siteCount diagnosticCount projectCount =
        probes <- probes + 1
        let metadataCount = diagnosticCount + projectCount

        jobj
            [ "sites", JsonArray(Array.init siteCount (fun _ -> jstr "site")) :> JsonNode
              "metadataCount", jint metadataCount
              "metadata",
              jstr (
                  if metadataCount = 0 then
                      ""
                  else
                      String.replicate 600 "x"
              ) ]
        :> JsonNode

    let result = FindResponseBudget.planResponse 500 1 200 1_000 build

    match result with
    | FindResponseBudget.FitPlan.Fits(1, 0, 0) -> ()
    | other -> Assert.Fail($"expected all optional metadata to be trimmed, got {other}")

    // The pre-fix loop made 2,403 full production-serialization probes here: two for every
    // removed metadata row plus a final site check. Binary prefix search has a deterministic
    // logarithmic bound for these counts (1 initial + 7 diagnostics + 10 projects + 1 site).
    Assert.True(probes <= 19, $"expected at most 19 serialized probes, got {probes}")

    probes <- 0

    match FindResponseBudget.planResponse 500 1_000 200 1_000 build with
    | FindResponseBudget.FitPlan.Fits(deliveredSites, 0, 0) ->
        Assert.InRange(deliveredSites, 1, 999)
    | other -> Assert.Fail($"expected a bounded site prefix after metadata trimming, got {other}")

    // maxResults is capped at 1,000. Adding its logarithmic site-prefix search keeps the
    // complete production-shaped worst case below 30 full serializations.
    Assert.True(probes <= 30, $"expected at most 30 serialized probes, got {probes}")

[<Fact>]
let ``logarithmic planner matches the former linear prefix policy across serialized thresholds`` () =
    let sites = repeatedNodes "site" 5 140
    let diagnostics = repeatedNodes "diagnostic" 4 170
    let projects = repeatedNodes "project" 3 190

    let build siteCount diagnosticCount projectCount =
        let truncated =
            siteCount < sites.Length
            || diagnosticCount < diagnostics.Length
            || projectCount < projects.Length

        jobj
            [ "sites", jsonArrayPrefix sites siteCount
              "projectDiagnostics", jsonArrayPrefix diagnostics diagnosticCount
              "perProject", jsonArrayPrefix projects projectCount
              "responseTruncatedByBudget", jbool truncated
              "responseSizeHint",
              (if truncated then
                   jstr (String.replicate 250 "h")
               else
                   null) ]
        :> JsonNode

    let bruteForce budget =
        let fits siteCount diagnosticCount projectCount =
            renderedLength (build siteCount diagnosticCount projectCount) <= budget

        let minimumSites = 1
        let mutable diagnosticCount = diagnostics.Length
        let mutable projectCount = projects.Length
        let mutable minimumFits = fits minimumSites diagnosticCount projectCount

        while not minimumFits && diagnosticCount > 0 do
            diagnosticCount <- diagnosticCount - 1
            minimumFits <- fits minimumSites diagnosticCount projectCount

        while not minimumFits && projectCount > 0 do
            projectCount <- projectCount - 1
            minimumFits <- fits minimumSites diagnosticCount projectCount

        if not minimumFits then
            if fits 0 diagnosticCount projectCount then
                FindResponseBudget.FitPlan.FirstSiteOverflow
            else
                FindResponseBudget.FitPlan.FixedMetadataOverflow
        else
            let mutable deliveredSites = sites.Length

            while deliveredSites > minimumSites && not (fits deliveredSites diagnosticCount projectCount) do
                deliveredSites <- deliveredSites - 1

            FindResponseBudget.FitPlan.Fits(deliveredSites, diagnosticCount, projectCount)

    for budget in 100..37..4_000 do
        let expected = bruteForce budget

        let actual =
            FindResponseBudget.planResponse budget sites.Length diagnostics.Length projects.Length build

        Assert.Equal(expected, actual)

[<Fact>]
let ``planner returns a typed first-site overflow instead of a zero-progress page`` () =
    let build siteCount _ _ =
        jobj
            [ "status", jstr "succeeded"
              "sites",
              JsonArray(
                  if siteCount = 0 then
                      [||]
                  else
                      [| jstr (String.replicate (FindResponseBudget.MaxSerializedChars + 1_000) "x") |]
              )
              :> JsonNode ]
        :> JsonNode

    let result =
        FindResponseBudget.planResponse FindResponseBudget.MaxSerializedChars 1 0 0 build

    match result with
    | FindResponseBudget.FitPlan.FirstSiteOverflow -> ()
    | other -> Assert.Fail($"expected FirstSiteOverflow, got {other}")

type FindResponseBudgetIntegrationTests(fixture: FindBudgetFixture) =
    interface IClassFixture<FindBudgetFixture>

    [<Fact>]
    member _.``long Unicode context with huge limits stays bounded and cursor traversal has no skips`` () : Task =
        task {
            Assert.True(
                fixture.BuildExitCode = 0,
                $"Budget fixture build failed ({fixture.BuildExitCode}):\n{fixture.BuildLog}"
            )

            let bridge = FcsBridge()
            let sourceLines = File.ReadAllLines(fixture.SourcePath)
            let mutable cursor = None
            let mutable pageCount = 0
            let mutable totalSites = -1
            let mutable deliveredCount = 0
            let mutable identities = Set.empty<string>
            let mutable sawTruncatedLine = false
            let mutable sawBudgetClose = false

            while cursor.IsSome || pageCount = 0 do
                Assert.True(pageCount < 100, "find cursor traversal must terminate")
                let! page = bridge.Find(findArgs fixture cursor)
                pageCount <- pageCount + 1

                Assert.Equal("succeeded", page["status"].GetValue<string>())
                Assert.Equal(FindResponseBudget.MaxSerializedChars, page["responseBudgetChars"].GetValue<int>())
                Assert.Equal(FindResponseBudget.SizeUnit, page["responseSizeUnit"].GetValue<string>())
                Assert.Equal(deliveredCount, page["pageOffset"].GetValue<int>())
                Assert.Equal(1000, page["pageSize"].GetValue<int>())
                Assert.True(renderToken(page).Length <= FindResponseBudget.MaxSerializedChars)

                if totalSites < 0 then
                    totalSites <- page["totalSites"].GetValue<int>()
                    Assert.True(totalSites > 20, $"fixture should expose many sites, got {totalSites}")

                let sites = page["sites"] :?> JsonArray
                Assert.NotEmpty(sites)
                Assert.Equal(sites.Count, page["returnedSiteCount"].GetValue<int>())
                Assert.Equal(sites.Count, page["cursorAdvancedBy"].GetValue<int>())
                sawBudgetClose <- sawBudgetClose || page["sitesTruncatedByBudget"].GetValue<bool>()

                for site in sites do
                    let range = site["range"]
                    let startLine = range["startLine"].GetValue<int>()
                    let startColumn = range["startColumn"].GetValue<int>()
                    let endLine = range["endLine"].GetValue<int>()
                    let endColumn = range["endColumn"].GetValue<int>()
                    Assert.Equal(startLine, endLine)
                    let sourceLine = sourceLines[startLine - 1]
                    Assert.Equal("target", sourceLine.Substring(startColumn, endColumn - startColumn))

                    let snippetStart = site["lineTextSourceStartColumn"].GetValue<int>()
                    let snippetEnd = site["lineTextSourceEndColumn"].GetValue<int>()
                    let snippet = site["lineText"].GetValue<string>()
                    Assert.Equal(sourceLine.Length, site["lineTextSourceLength"].GetValue<int>())
                    Assert.Equal(sourceLine.Substring(snippetStart, snippetEnd - snippetStart), snippet)
                    Assert.True(snippet.Length <= FindResponseBudget.MaxSnippetChars)
                    sawTruncatedLine <- sawTruncatedLine || site["lineTextTruncated"].GetValue<bool>()

                    Assert.Equal(Int32.MaxValue, site["contextLinesRequested"].GetValue<int>())
                    Assert.Equal(FindResponseBudget.MaxContextLines, site["contextLinesApplied"].GetValue<int>())
                    Assert.True(site["contextLinesTruncated"].GetValue<bool>())

                    let before = site["before"] :?> JsonArray
                    let after = site["after"] :?> JsonArray
                    Assert.True(before.Count <= FindResponseBudget.MaxContextLines)
                    Assert.True(after.Count <= FindResponseBudget.MaxContextLines)

                    for context in Seq.append before after do
                        let contextLine = context["line"].GetValue<int>()
                        let contextSource = sourceLines[contextLine - 1]
                        let contextStart = context["sourceStartColumn"].GetValue<int>()
                        let contextEnd = context["sourceEndColumn"].GetValue<int>()
                        let contextText = context["text"].GetValue<string>()
                        Assert.Equal(contextSource.Length, context["sourceLength"].GetValue<int>())
                        Assert.Equal(contextSource.Substring(contextStart, contextEnd - contextStart), contextText)
                        Assert.True(contextText.Length <= FindResponseBudget.MaxSnippetChars)

                    let file = site["file"].GetValue<string>()
                    let identity = $"{file}:{startLine}:{startColumn}:{endLine}:{endColumn}"

                    Assert.DoesNotContain(identity, identities)
                    identities <- identities.Add identity

                deliveredCount <- deliveredCount + sites.Count

                cursor <-
                    match page["nextCursor"] with
                    | null -> None
                    | node ->
                        let next = node.GetValue<string>()

                        match tryDecode next with
                        | Ok payload -> Assert.Equal(deliveredCount, payload.offset)
                        | Error reason -> Assert.Fail($"find emitted an invalid cursor: {reason}")

                        Some next

            Assert.True(pageCount > 1, "the response budget should force multiple pages")
            Assert.True(sawBudgetClose, "at least one page must close on serialized size")
            Assert.True(sawTruncatedLine, "long source lines must expose truncation metadata")
            Assert.Equal(totalSites, deliveredCount)
            Assert.Equal(totalSites, identities.Count)
        }

    [<Fact>]
    member _.``first-site overflow returns bounded typed recovery with no non-advancing cursor`` () : Task =
        task {
            Assert.True(
                fixture.BuildExitCode = 0,
                $"Budget fixture build failed ({fixture.BuildExitCode}):\n{fixture.BuildLog}"
            )

            let tinyBudget = 6_000
            let bridge = FcsBridge(findResponseBudgetCharsOverride = tinyBudget)

            let! result =
                bridge.Find(
                    { findArgs fixture None with
                        maxResults = Some 1
                        scope = Some "workspace"
                        projectPath = Some fixture.SolutionPath }
                )

            Assert.Equal("aborted", result["status"].GetValue<string>())
            Assert.Equal("blocked", result["deliveryStatus"].GetValue<string>())
            Assert.Equal("find_site_exceeds_response_budget", result["errorCode"].GetValue<string>())
            Assert.Equal(0, result["returnedSiteCount"].GetValue<int>())
            Assert.Equal(0, result["cursorAdvancedBy"].GetValue<int>())
            Assert.Null(result["nextCursor"])
            Assert.True(result["retryable"].GetValue<bool>())
            Assert.NotNull(result["coverage"])
            Assert.NotNull(result["resolution"])
            let resultResolution = result["resolution"]
            Assert.Equal(1, result["projectsSwept"].GetValue<int>())
            Assert.Equal(2, result["projectsRequested"].GetValue<int>())
            Assert.Equal(1, result["projectsAnalyzed"].GetValue<int>())
            Assert.Equal(1, result["projectsMissing"].GetValue<int>())
            Assert.Equal(1, resultResolution["projectsSwept"].GetValue<int>())
            Assert.Equal(2, resultResolution["projectsRequested"].GetValue<int>())
            Assert.Equal(1, resultResolution["projectsMissing"].GetValue<int>())
            Assert.Equal("project", resultResolution["scopeResolved"].GetValue<string>())
            Assert.NotNull(result["recovery"])
            Assert.True(renderToken(result).Length <= tinyBudget)
        }
