module FsLangMcp.Tests.FindTests

// ─── #128 Stage 1: the consolidated `find` tool — multi-project union sweep ──────
//
// Measures the DELTA between:
//   (baseline) single-project FcsBridge.FindSymbol / RecordFieldAudit, which run
//              ParseAndCheckProject on ONE resolved project, and
//   (new)      FcsBridge.Find (kind=auto, scope=auto), which sweeps every member
//              .fsproj of the solution, unions GetAllUsesOfAllSymbols() de-duped by
//              source range, and auto-unions record-field construction/update sites.
//
// Fixture (the documented hexagonal port-widening failure shape): a record-of-
// functions `TraderRole` is DEFINED in Domain and CONSTRUCTED/USED in Stubs + App.
// Single-project FindSymbol on Domain cannot see the cross-project construction
// sites; the sweep recovers all of them. Built once per test class (IClassFixture).

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── Fixture sources ────────────────────────────────────────────────────────────

let private domainFs =
    String.concat
        "\n"
        [ "namespace Domain"
          ""
          "/// Hexagonal port modelled as a record-of-functions."
          "type TraderRole ="
          "    { Propose: int -> int -> int }"
          "" ]

let private stubsFs =
    String.concat
        "\n"
        [ "module Stubs.Roles"
          ""
          "open Domain"
          ""
          "// S1: record-literal construction site"
          "let stub: TraderRole = { Propose = fun a b -> a + b }"
          ""
          "// S2: record-update construction site"
          "let stub2: TraderRole = { stub with Propose = fun a b -> a - b }"
          "" ]

let private appFs =
    String.concat
        "\n"
        [ "module App.Roles"
          ""
          "open Domain"
          ""
          "// A1: record-literal construction site"
          "let appRole: TraderRole = { Propose = fun a b -> a * b }"
          ""
          "// A2: field-read site"
          "let computed = appRole.Propose 6 7"
          ""
          "// A3: cross-project type-annotation + field-read site"
          "let useRole (role: TraderRole) = role.Propose 1 2"
          "" ]

let private leafProject (sourceFile: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"{sourceFile}\" /></ItemGroup>"
          "</Project>" ]

let private refProject (sourceFile: string) (refRelative: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"{sourceFile}\" /></ItemGroup>"
          $"  <ItemGroup><ProjectReference Include=\"{refRelative}\" /></ItemGroup>"
          "</Project>" ]

let private slnx =
    String.concat
        "\n"
        [ "<Solution>"
          "  <Project Path=\"Domain/Domain.fsproj\" />"
          "  <Project Path=\"Stubs/Stubs.fsproj\" />"
          "  <Project Path=\"App/App.fsproj\" />"
          "</Solution>" ]

// ── Class fixture: written + built ONCE, shared by every test in the class ───────

type FindFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    let domainFsproj = write "Domain/Domain.fsproj" (leafProject "Domain.fs")
    let domainSource = write "Domain/Domain.fs" domainFs
    do write "Stubs/Stubs.fsproj" (refProject "Stubs.fs" "../Domain/Domain.fsproj") |> ignore
    do write "Stubs/Stubs.fs" stubsFs |> ignore
    do write "App/App.fsproj" (refProject "App.fs" "../Domain/Domain.fsproj") |> ignore
    do write "App/App.fs" appFs |> ignore
    let slnxPath = write "FindSolution.slnx" slnx

    // dotnet build is ground truth and also produces Domain.dll so per-project FCS
    // sweeps resolve the cross-project TraderRole reference. -m:1 serializes MSBuild
    // (Stubs + App both P2P-reference Domain → parallel restore races on
    // Domain.fsproj.nuget.g.props). The whole suite runs xUnit collections in
    // parallel, so this external build can collide with other tests' in-process
    // Ionide.ProjInfo MSBuild evaluation — isolate it from the build servers and
    // retry to keep the gate deterministic.
    let buildOnce () =
        let psi =
            ProcessStartInfo(
                "dotnet",
                $"build \"{slnxPath}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
            )

        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] <- "1"
        use p = Process.Start(psi)
        let stdout = p.StandardOutput.ReadToEnd()
        let stderr = p.StandardError.ReadToEnd()
        p.WaitForExit()
        p.ExitCode, stdout + stderr

    let rec buildWithRetry attempt =
        let code, log = buildOnce ()

        if code = 0 || attempt >= 3 then
            code, log
        else
            System.Threading.Thread.Sleep(1500)
            buildWithRetry (attempt + 1)

    let buildSw = Stopwatch.StartNew()
    let buildExit, buildLog = buildWithRetry 1
    do buildSw.Stop()

    member _.Root = root
    member _.Slnx = slnxPath
    member _.DomainFsproj = domainFsproj
    member _.DomainFs = domainSource
    member _.BuildExitCode = buildExit
    member _.BuildLog = buildLog
    member _.BuildMs = int buildSw.ElapsedMilliseconds

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ── JSON helpers ─────────────────────────────────────────────────────────────────

let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private gb (node: JsonNode) (key: string) = node[key].GetValue<bool>()
let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()

let private sumReferenceCounts (result: JsonNode) =
    match result["symbols"] with
    | :? JsonArray as arr -> arr |> Seq.sumBy (fun s -> s["referenceCount"].GetValue<int>())
    | _ -> 0

// ── Arg builders ────────────────────────────────────────────────────────────────

let private findArgs (projectPath: string) (query: string) : FindArgs =
    { query = query
      kind = None
      scope = None
      exact = None
      ``member`` = None
      field = None
      path = None
      line = None
      word = None
      occurrence = None
      character = None
      contextLines = Some 0
      includeDeclaration = None
      includeInfo = None
      projectPath = Some projectPath
      maxResults = Some 500
      cursor = None }

let private findSymbolArgs (domainFs: string) (domainFsproj: string) (query: string) (exact: bool) : FcsFindSymbolArgs =
    { path = domainFs
      text = None
      projectPath = Some domainFsproj
      projectOptions = None
      symbolQuery = query
      exact = Some exact
      maxResults = Some 200
      contextLines = Some 0
      includeDeclaration = Some true
      includeInfo = None
      cursor = None }

// ─────────────────────────────────────────────────────────────────────────────────

type FindTests(fx: FindFixture, output: ITestOutputHelper) =
    interface IClassFixture<FindFixture>

    [<Fact>]
    member _.``DELTA: bare find sweeps all member projects and recovers cross-project field-set sites the single-project tools miss``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")

            let bridge = FcsBridge()

            // ── BASELINE 1: single-project FindSymbol on Domain (exact) ──────────
            let! baseExact = bridge.FindSymbol(findSymbolArgs fx.DomainFs fx.DomainFsproj "TraderRole" true)
            let baseExactUses = gi baseExact "matchedUseCount"
            let baseExactRefs = sumReferenceCounts baseExact

            // ── BASELINE 2: single-project RecordFieldAudit on Domain ────────────
            let! baseAudit =
                bridge.RecordFieldAudit(
                    { typeName = "TraderRole"
                      fieldName = "Propose"
                      path = None
                      text = None
                      projectPath = Some fx.DomainFsproj
                      projectOptions = None
                      maxResults = Some 200
                      cursor = None }
                )

            let baseAuditMatched = gi baseAudit "matchedCount"

            // ── NEW: bare find (kind=auto, scope=auto, exact=default true) ───────
            let! find = bridge.Find(findArgs fx.Slnx "TraderRole")

            Assert.Equal("succeeded", gs find "status")
            let resolution = find["resolution"]
            let breakdown = find["breakdown"]
            let totalSites = gi find "totalSites"
            let projectsSwept = gi find "projectsSwept"

            let fLit = gi breakdown "fieldSetLiteral"
            let fUpd = gi breakdown "fieldSetUpdate"
            let fRead = gi breakdown "fieldRead"
            let fieldSitesTotal = fLit + fUpd + fRead

            // ── Render the delta table to test output ────────────────────────────
            // Precompute every value: F# forbids "double-quoted" lookups inside an
            // interpolation hole of a $"..." string.
            let bDefs = gi breakdown "definitions"
            let bRefs = gi breakdown "references"
            let bMem = gi breakdown "memberUsages"
            let rMatched = gb resolution "matched"
            let rVia = gs resolution "via"
            let rSwept = gi resolution "projectsSwept"
            let rFcs = gi resolution "fcsSiteCount"
            let sweepMs = gi find "sweepElapsedMs"

            let line (s: string) = output.WriteLine(s)
            line "# FsLangMCP #128 — `find` measured delta (synthetic 3-project fixture)"
            line ""
            line "Fixture: TraderRole defined in Domain; constructed/used in Stubs + App."
            line "| Path                              | Query      | Sites | Cross-project field sites |"
            line "|-----------------------------------|------------|-------|---------------------------|"
            line $"| OLD FindSymbol (Domain only)      | exact=true | uses={baseExactUses}, refs={baseExactRefs} | MISSED (0) |"
            line $"| OLD RecordFieldAudit (Domain)     | Propose    | matched={baseAuditMatched} | MISSED (0) |"
            line $"| NEW find (Domain+Stubs+App)       | auto       | totalSites={totalSites} | field sites={fieldSitesTotal} |"
            line ""
            line $"find breakdown: definitions={bDefs}, references={bRefs}, fieldSetLiteral={fLit}, fieldSetUpdate={fUpd}, fieldRead={fRead}, memberUsages={bMem}"
            line $"resolution: matched={rMatched}, via={rVia}, projectsSwept={rSwept}, fcsSiteCount={rFcs}"
            line $"sweepElapsedMs={sweepMs} (fixture build {fx.BuildMs}ms)"

            // ── Assertions: the improvement must be unambiguous ──────────────────
            // 1. Baselines see ZERO cross-project field construction sites.
            Assert.Equal(0, baseAuditMatched)
            Assert.Equal(0, baseExactRefs)

            // 2. The sweep covers all three member projects.
            Assert.Equal(3, projectsSwept)

            // 3. matched=true, resolved via the FCS multi-project sweep.
            Assert.True(gb resolution "matched", "find must report matched=true for a present symbol")
            Assert.Equal("fcs-multiproject-sweep", gs resolution "via")

            // 4. The sweep recovers all 5 record-field sites (2 literal + 1 update + 2 read).
            Assert.Equal(2, fLit)
            Assert.Equal(1, fUpd)
            Assert.Equal(2, fRead)

            // 5. The same 5 cross-project field sites are recoverable from the flat
            //    `sites` list filtered by `kind` (the grouped `fieldSites` bucket was
            //    removed; `sites` + `kind` is now the single representation, and its
            //    field-kind count must agree with the breakdown total above).
            let fieldKinds = Set.ofList [ "field-set-literal"; "field-set-update"; "field-read" ]

            let fieldSitesInList =
                (find["sites"] :?> JsonArray)
                |> Seq.filter (fun s -> fieldKinds.Contains(gs s "kind"))
                |> Seq.length

            Assert.Equal(5, fieldSitesInList)
            Assert.Equal(fieldSitesTotal, fieldSitesInList)

            // 6. Headline: find finds strictly more than the single-project baseline.
            Assert.True(
                totalSites > baseExactUses + baseExactRefs,
                $"Expected find ({totalSites}) > single-project baseline ({baseExactUses + baseExactRefs})"
            )
        }

    [<Fact>]
    member _.``find with kind=field unions only the cross-project record-field sites``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! find = bridge.Find({ findArgs fx.Slnx "TraderRole" with kind = Some "field" })

            Assert.Equal("succeeded", gs find "status")
            let breakdown = find["breakdown"]
            // field-only: no definitions / references, all 5 field sites present.
            Assert.Equal(0, gi breakdown "definitions")
            Assert.Equal(0, gi breakdown "references")
            Assert.Equal(5, gi breakdown "fieldSetLiteral" + gi breakdown "fieldSetUpdate" + gi breakdown "fieldRead")
            Assert.Equal("field", gs find "kindResolved")
        }

    [<Fact>]
    member _.``find kind=field honors exact=false on the declaring type — query 'role' reaches TraderRole.Propose``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // The declaring-type predicate for field sites must honor exact=false the same
            // way the symbol branch does: substring 'role' (lowercase) must reach the
            // TraderRole.Propose field sites. field='Propose' restricts to that field.
            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "role" with
                        kind = Some "field"
                        field = Some "Propose"
                        exact = Some false }
                )

            Assert.Equal("succeeded", gs find "status")
            let breakdown = find["breakdown"]

            let fieldTotal =
                gi breakdown "fieldSetLiteral" + gi breakdown "fieldSetUpdate" + gi breakdown "fieldRead"

            // All 5 Propose field sites (2 literal + 1 update + 2 read) recovered via the substring match.
            Assert.Equal(5, fieldTotal)

            // CONTROL: the same substring query with exact=true matches nothing — no type is
            // literally named 'role' — proving exact=false is what unlocks the sites.
            let! exactFind =
                bridge.Find(
                    { findArgs fx.Slnx "role" with
                        kind = Some "field"
                        field = Some "Propose"
                        exact = Some true }
                )

            let exactBreakdown = exactFind["breakdown"]

            let exactFieldTotal =
                gi exactBreakdown "fieldSetLiteral"
                + gi exactBreakdown "fieldSetUpdate"
                + gi exactBreakdown "fieldRead"

            Assert.Equal(0, exactFieldTotal)
        }

    [<Fact>]
    member _.``find with empty query returns invalid_args naming query``() : Task =
        task {
            let bridge = FcsBridge()
            let! result = bridge.Find(findArgs fx.Slnx "   ")

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("query", gs result "message")
        }

    [<Fact>]
    member _.``find reports matched=false ONLY when the symbol is truly absent everywhere``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // A name that exists in NO member project. No fsacProbe is injected here,
            // so this exercises the FCS-empty branch directly: matched must be false.
            let! find = bridge.Find(findArgs fx.Slnx "ZzzNoSuchSymbol_4827")

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal(0, gi find "totalSites")
            let resolution = find["resolution"]
            Assert.False(gb resolution "matched", "absent symbol must report matched=false")
            Assert.Equal("none", gs resolution "via")
            Assert.Equal(3, gi resolution "projectsSwept")
        }

    [<Fact>]
    member _.``find defaults to compact one-line-per-site output; contextLines>0 restores before/after``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // DEFAULT shape: contextLines unset → compact. Every site keeps lineText but
            // emits NO before/after arrays (the overflow fix). breakdown + resolution stay.
            let! compact = bridge.Find({ findArgs fx.Slnx "TraderRole" with contextLines = None })

            Assert.Equal("succeeded", gs compact "status")
            Assert.NotNull(compact["breakdown"])
            Assert.NotNull(compact["resolution"])

            let compactSites = compact["sites"] :?> JsonArray
            Assert.True((compactSites.Count > 0), "fixture must produce sites")

            for site in compactSites do
                Assert.NotNull(site["lineText"]) // the single matched line is preserved
                Assert.Null(site["before"]) // no surrounding-context arrays
                Assert.Null(site["after"])

            // OPT-IN context: contextLines=2 restores before/after on every site, with at
            // least one non-empty (TraderRole is used mid-file across the fixture).
            let! withCtx = bridge.Find({ findArgs fx.Slnx "TraderRole" with contextLines = Some 2 })
            let ctxSites = withCtx["sites"] :?> JsonArray
            Assert.True((ctxSites.Count > 0), "context run must produce sites")

            for site in ctxSites do
                Assert.NotNull(site["before"])
                Assert.NotNull(site["after"])

            let anyContextEmitted =
                ctxSites
                |> Seq.exists (fun s -> (s["before"] :?> JsonArray).Count > 0 || (s["after"] :?> JsonArray).Count > 0)

            Assert.True(anyContextEmitted, "contextLines=2 must emit surrounding lines on at least one site")
        }

// ── kind=position FullName disambiguation (Codex P2 #1) ──────────────────────────
//
// Two types both named `Config` in DIFFERENT namespaces. kind=position resolves THE
// specific symbol under the cursor; the subsequent sweep must key on its FullName, not
// its DisplayName — otherwise it sweeps BOTH Config types. One project is enough: the
// bug is about symbol identity, not cross-project recovery.

let private configProbeFs =
    String.concat
        "\n"
        [ "namespace ConfigProbe.Alpha"
          ""
          "type Config = { Value: int }"
          ""
          "module Use ="
          "    let mk () : Config = { Value = 1 }"
          "    let other () : Config = { Value = 2 }"
          ""
          "namespace ConfigProbe.Beta"
          ""
          "type Config = { Flag: bool }"
          ""
          "module Use ="
          "    let mk () : Config = { Flag = true }"
          "" ]

type ConfigFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_findpos_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    let fsproj = write "ConfigProbe/ConfigProbe.fsproj" (leafProject "ConfigProbe.fs")
    let source = write "ConfigProbe/ConfigProbe.fs" configProbeFs

    // Build once so Ionide.ProjInfo resolves options. Isolation/retry flags mirror FindFixture.
    let buildOnce () =
        let psi =
            ProcessStartInfo(
                "dotnet",
                $"build \"{fsproj}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
            )

        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] <- "1"
        use p = Process.Start(psi)
        let stdout = p.StandardOutput.ReadToEnd()
        let stderr = p.StandardError.ReadToEnd()
        p.WaitForExit()
        p.ExitCode, stdout + stderr

    let rec buildWithRetry attempt =
        let code, log = buildOnce ()

        if code = 0 || attempt >= 3 then
            code, log
        else
            System.Threading.Thread.Sleep(1500)
            buildWithRetry (attempt + 1)

    let buildExit, buildLog = buildWithRetry 1

    member _.Root = root
    member _.Fsproj = fsproj
    member _.Source = source
    member _.SourceText = configProbeFs
    member _.BuildExitCode = buildExit
    member _.BuildLog = buildLog

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

let private symbolFullNames (result: JsonNode) =
    match result["sites"] with
    | :? JsonArray as arr ->
        arr
        |> Seq.choose (fun s ->
            match s["symbolFullName"] with
            | :? JsonValue as v -> Some(v.GetValue<string>())
            | _ -> None)
        |> Seq.toList
    | _ -> []

type FindPositionTests(fx: ConfigFixture) =
    interface IClassFixture<ConfigFixture>

    [<Fact>]
    member _.``find kind=position keys the sweep on FullName, not DisplayName — disambiguates same-named types``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // Cursor lands on `Config` in Alpha.Use.mk (the line carrying `Value = 1`) →
            // resolves ConfigProbe.Alpha.Config (FullName), NOT the Beta namesake.
            let lines = fx.SourceText.Split('\n')
            let posLine = lines |> Array.findIndex (fun l -> l.Contains "Value = 1")

            let! find =
                bridge.Find(
                    { findArgs fx.Fsproj "Config" with
                        kind = Some "position"
                        path = Some fx.Source
                        line = Some posLine
                        word = Some "Config" }
                )

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal("symbol", gs find "kindResolved")

            let fullNames = symbolFullNames find
            Assert.True((fullNames.Length > 1), $"expected multiple Alpha.Config sites, got {fullNames.Length}")

            // EVERY site belongs to the Alpha namesake; the Beta.Config sites must NOT leak in.
            let joined = String.concat ", " fullNames

            Assert.True(
                fullNames |> List.forall (fun fn -> fn = "ConfigProbe.Alpha.Config"),
                $"every position site must be ConfigProbe.Alpha.Config; got: {joined}"
            )

            Assert.DoesNotContain("ConfigProbe.Beta.Config", fullNames)

            // CONTROL: a plain exact query 'Config' keys on DisplayName and therefore sweeps
            // BOTH namesakes — the ambiguity that position resolution must avoid.
            let! plain = bridge.Find({ findArgs fx.Fsproj "Config" with exact = Some true })
            let plainNames = symbolFullNames plain
            Assert.Contains("ConfigProbe.Alpha.Config", plainNames)
            Assert.Contains("ConfigProbe.Beta.Config", plainNames)
        }
