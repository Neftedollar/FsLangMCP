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

            // 5. Field sites are surfaced in the grouped `fieldSites` bucket too.
            let fieldBucket = find["fieldSites"] :?> JsonArray
            Assert.Equal(5, fieldBucket.Count)

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
