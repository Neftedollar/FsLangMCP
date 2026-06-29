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
      includePerProject = None
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

    // ── #131: the find sweep memoizes GetAllUsesOfAllSymbols per project ──────────
    [<Fact>]
    member _.``find caches the per-project sweep: a second find on unchanged projects reuses it (#131)``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // COLD: first sweep pays GetAllUsesOfAllSymbols and populates the use-cache —
            // one entry per swept member project.
            let! cold = bridge.Find(findArgs fx.Slnx "TraderRole")
            Assert.Equal("succeeded", gs cold "status")
            let coldSites = gi cold "totalSites"
            let coldSweepMs = gi cold "sweepElapsedMs"
            let coldCacheCount = bridge.ProjectUsesCacheCount
            Assert.Equal(gi cold "projectsSwept", coldCacheCount)

            // WARM: same projects, no edits → every project is a cache HIT → no new
            // entries, identical site total.
            let! warm = bridge.Find(findArgs fx.Slnx "TraderRole")
            let warmSites = gi warm "totalSites"
            let warmSweepMs = gi warm "sweepElapsedMs"
            Assert.Equal(coldSites, warmSites)
            Assert.Equal(coldCacheCount, bridge.ProjectUsesCacheCount)

            // A DIFFERENT query on the same unchanged projects also reuses the cache —
            // the memo holds the raw all-uses enumeration, independent of the query.
            let! warm2 = bridge.Find(findArgs fx.Slnx "Propose")
            Assert.Equal("succeeded", gs warm2 "status")
            Assert.Equal(coldCacheCount, bridge.ProjectUsesCacheCount)

            output.WriteLine($"#131 cache: coldSweepMs={coldSweepMs}, warmSweepMs={warmSweepMs}, cacheEntries={coldCacheCount}")
        }

    [<Fact>]
    member _.``find use-cache invalidates when a swept file is edited on disk — next find reflects the edit (#131)``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            try
                // Warm the cache against the baseline Domain.fs.
                let! before = bridge.Find(findArgs fx.Slnx "TraderRole")
                let beforeSites = gi before "totalSites"
                let beforeCacheCount = bridge.ProjectUsesCacheCount

                // Edit Domain.fs on disk: add a module that references TraderRole, so the
                // "TraderRole" query gains at least one new site. Push the mtime forward so
                // the source-stamp provably changes (and FCS re-checks the project).
                let edited =
                    domainFs
                    + String.concat "\n" [ "module Usage ="; "    let again (r: TraderRole) : int = r.Propose 1 2"; "" ]

                File.WriteAllText(fx.DomainFs, edited)
                File.SetLastWriteTimeUtc(fx.DomainFs, DateTime.UtcNow.AddSeconds 2.0)

                let! after = bridge.Find(findArgs fx.Slnx "TraderRole")
                let afterSites = gi after "totalSites"

                // CORRECTNESS: the edit is reflected — the cached sweep is NOT served stale.
                Assert.True(
                    afterSites > beforeSites,
                    $"edit must be reflected: before={beforeSites}, after={afterSites}"
                )

                // The edited project's stamp changed → a fresh cache entry was added
                // alongside the (now-stale) baseline one, so the count grew.
                Assert.True(
                    bridge.ProjectUsesCacheCount > beforeCacheCount,
                    $"an edit must create a new cache entry: before={beforeCacheCount}, after={bridge.ProjectUsesCacheCount}"
                )
            finally
                // Restore the shared fixture so method ordering cannot leak this edit.
                File.WriteAllText(fx.DomainFs, domainFs)
        }

    // ── 0.10.1 Codex P1: cross-project invalidation via referenced-assembly mtime ──
    //
    // The #131 invalidation test above only edits a file in the SAME project, so it never
    // exercised the path the original key got wrong: a CONSUMER's key was keyed on its own
    // source-stamp ALONE — blind to a REBUILD of a referenced project. Here we warm the
    // cache, then move the mtime of Domain's referenced output (the obj/.../ref/Domain.dll
    // assembly that Stubs + App resolve via -r:) FORWARD without touching any consumer's
    // SOURCE. Under the OLD key the consumer entries would be served stale (cache count
    // unchanged); under the fixed key the bumped reference mtime moves each consumer's key
    // → cache MISS → fresh entries, so the count grows. Domain itself references no fixture
    // project, so it stays a HIT — any growth necessarily comes from the consumers, which
    // is exactly the cross-project staleness the P1 fix closes.
    [<Fact>]
    member _.``find use-cache invalidates a CONSUMER when a referenced assembly is rebuilt — no stale cross-project results (0.10.1 Codex P1)``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // Domain output assemblies the consumer projects reference via -r:.
            // ProduceReferenceAssembly (SDK default) means the resolved -r: target is
            // obj/.../ref/Domain.dll; bump EVERY Domain.dll under the fixture so whichever
            // path the consumers resolve is moved. Bumping copies that are NOT in any
            // OtherOptions (e.g. consumer bin/ copies) is a harmless no-op for the key.
            let domainDlls = Directory.GetFiles(fx.Root, "Domain.dll", SearchOption.AllDirectories)

            Assert.True(
                (domainDlls.Length > 0),
                $"fixture build must have produced a Domain.dll to reference (root: {fx.Root})"
            )

            // Capture originals so the shared fixture is left pristine for sibling tests.
            let originalMtimes = domainDlls |> Array.map (fun p -> p, File.GetLastWriteTimeUtc p)

            try
                // WARM: one cache entry per swept member project (Domain, Stubs, App).
                let! before = bridge.Find(findArgs fx.Slnx "TraderRole")
                Assert.Equal("succeeded", gs before "status")
                let beforeSites = gi before "totalSites"
                let beforeCacheCount = bridge.ProjectUsesCacheCount
                Assert.Equal(gi before "projectsSwept", beforeCacheCount)

                // Rebuild signal: move the referenced Domain assembly mtime FORWARD. No
                // consumer SOURCE is touched — only the dependency's output mtime moves.
                let bump = DateTime.UtcNow.AddSeconds 5.0

                for dll in domainDlls do
                    File.SetLastWriteTimeUtc(dll, bump)

                let! after = bridge.Find(findArgs fx.Slnx "TraderRole")
                Assert.Equal("succeeded", gs after "status")

                // CORRECTNESS (cache key): the consumer projects (Stubs, App) re-keyed on
                // the moved reference mtime → cache MISS → fresh entries added alongside the
                // now-stale baseline ones → the count GREW. On the OLD source-only key the
                // consumer keys were unchanged → no new entries → this assertion FAILS.
                Assert.True(
                    bridge.ProjectUsesCacheCount > beforeCacheCount,
                    $"a referenced-assembly rebuild must invalidate the consumer cache entries: before={beforeCacheCount}, after={bridge.ProjectUsesCacheCount}"
                )

                // The Domain assembly CONTENT is unchanged (only its mtime moved), so the
                // fresh sweep returns the SAME sites — invalidation must not corrupt results.
                Assert.Equal(beforeSites, gi after "totalSites")

                output.WriteLine(
                    $"0.10.1 P1 cross-project: beforeCache={beforeCacheCount}, afterCache={bridge.ProjectUsesCacheCount}, sites={beforeSites} (touched {domainDlls.Length} Domain.dll)"
                )
            finally
                // Restore reference mtimes so test ordering cannot leak the bump.
                for (p, t) in originalMtimes do
                    try
                        File.SetLastWriteTimeUtc(p, t)
                    with _ ->
                        ()
        }

    // ── 0.10.1 Codex P2: cross-project consumer re-keys when DEPENDENCY SOURCE changes ─
    //
    // P1 stamped the -r: DLL mtimes, catching dependency REBUILDS. P2 (this test) covers
    // the complementary scenario: a dependency's SOURCE is edited on disk WITHOUT a rebuild
    // — the DLL mtime therefore stays the same. FCS's ParseAndCheckProject for a consumer
    // reads P2P referenced project sources via ReferencedProjects[FSharpReference].SourceFiles
    // directly, so a fresh consumer check WOULD see the edit. Without the P2 stamp, the
    // consumer key is unchanged → stale cached result → stale find. With the fix
    // (referencedProjectSourcesStamp), the dependency source mtime is folded into the
    // consumer key → consumer MISS → fresh sweep.
    //
    // Setup: warm the cache (3 entries), then touch ONLY Domain.fs mtime forward (simulating
    // a developer edit) WITHOUT touching any consumer source AND WITHOUT rebuilding any DLL.
    // - OLD key (P2 bug present): only Domain's own sourceFilesStamp changes → Domain re-keys
    //   → count goes from 3 to 4 (one new entry); consumer entries stay — assertion FAILS.
    // - New key (P2 fix): Domain re-keys (own source) AND Stubs/App re-key (their
    //   referencedProjectSourcesStamp includes Domain.fs) → count goes from 3 to 6 (3 new
    //   entries); assertion PASSES. A count > beforeCacheCount + 1 proves consumers were
    //   re-keyed, not just Domain's own entry.
    [<Fact>]
    member _.``find use-cache invalidates a CONSUMER when a referenced project SOURCE is edited on disk — no stale cross-project results (0.10.1 Codex P2)``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let originalMtime = File.GetLastWriteTimeUtc fx.DomainFs

            try
                // WARM: one cache entry per swept member project (Domain, Stubs, App).
                let! before = bridge.Find(findArgs fx.Slnx "TraderRole")
                Assert.Equal("succeeded", gs before "status")
                let beforeSites = gi before "totalSites"
                let beforeCacheCount = bridge.ProjectUsesCacheCount
                Assert.Equal(gi before "projectsSwept", beforeCacheCount)

                // Simulate a developer edit to Domain.fs: bump its mtime forward WITHOUT
                // touching any consumer source and WITHOUT rebuilding any DLL. The -r: target
                // paths (obj/.../ref/Domain.dll) are NOT modified — only the .fs mtime moves.
                File.SetLastWriteTimeUtc(fx.DomainFs, DateTime.UtcNow.AddSeconds 5.0)

                let! after = bridge.Find(findArgs fx.Slnx "TraderRole")
                Assert.Equal("succeeded", gs after "status")

                // CORRECTNESS (P2 key): the consumer projects (Stubs, App) each hold Domain.fs
                // in their referencedProjectSourcesStamp → both re-key on the mtime bump.
                // Domain itself re-keys via its own sourceFilesStamp. Together that is 3 new
                // entries (1 Domain + 2 consumers). On the OLD key (no referencedProjectSourcesStamp)
                // only Domain re-keyed → count = before+1; this assertion would FAIL.
                Assert.True(
                    bridge.ProjectUsesCacheCount > beforeCacheCount + 1,
                    $"a dependency source edit must re-key CONSUMER entries (not just the edited project): before={beforeCacheCount}, after={bridge.ProjectUsesCacheCount} (expected > {beforeCacheCount + 1})"
                )

                // The Domain source was ONLY mtime-bumped (content unchanged), so results match.
                Assert.Equal(beforeSites, gi after "totalSites")

                output.WriteLine(
                    $"0.10.1 P2 dep-source: beforeCache={beforeCacheCount}, afterCache={bridge.ProjectUsesCacheCount}, sites={beforeSites}"
                )
            finally
                // Restore Domain.fs mtime so the edit doesn't bleed into sibling tests.
                try
                    File.SetLastWriteTimeUtc(fx.DomainFs, originalMtime)
                with _ ->
                    ()
        }

    // ── F1 (#100): module-qualified (dotted) query resolves via dotted-suffix match ──
    [<Fact>]
    member _.``F1 (#100): dotted-suffix query matches; bare unchanged; genuine miss carries a hint``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // App.Roles.appRole — a dotted SUFFIX query ("Roles.appRole") used to silently
            // miss (equality-only matching), now matches on the '.' boundary.
            let! dotted = bridge.Find(findArgs fx.Slnx "Roles.appRole")
            Assert.Equal("succeeded", gs dotted "status")
            Assert.True(gb dotted["resolution"] "matched", "dotted suffix query must match App.Roles.appRole")
            Assert.True((gi dotted "totalSites" > 0), "dotted query must return sites")

            // Bare identifier still matches (behaviour unchanged for non-dotted queries).
            let! bare = bridge.Find(findArgs fx.Slnx "appRole")
            Assert.True(gb bare["resolution"] "matched", "bare query must still match")

            // A genuine miss on a dotted query carries a hint pointing at the bare identifier.
            let! miss = bridge.Find(findArgs fx.Slnx "Nowhere.noSuchSymbol_4827")
            Assert.False(gb miss["resolution"] "matched", "bogus dotted query must not match")
            Assert.True(miss.AsObject().ContainsKey("hint"), "a dotted miss must carry a hint")
            Assert.Contains("noSuchSymbol_4827", gs miss "hint")
        }

    // ── F5 (#100): perProject drops zero-match noise; includePerProject=false omits it ──
    [<Fact>]
    member _.``F5 (#100): perProject trims zero-match projects and is omitted when includePerProject=false``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // appRole lives only in App; Domain + Stubs match nothing and must be trimmed,
            // while projectsSwept still reports the full sweep breadth.
            let! find = bridge.Find(findArgs fx.Slnx "appRole")
            Assert.Equal(3, gi find "projectsSwept")
            let perProject = find["perProject"].AsArray()
            Assert.True((perProject.Count >= 1), "the matching project must remain in perProject")
            Assert.True((perProject.Count < 3), "zero-match projects must be trimmed from perProject")

            // includePerProject=false omits the array entirely.
            let! lean = bridge.Find({ findArgs fx.Slnx "appRole" with includePerProject = Some false })
            Assert.False(lean.AsObject().ContainsKey("perProject"), "includePerProject=false must omit perProject")
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
