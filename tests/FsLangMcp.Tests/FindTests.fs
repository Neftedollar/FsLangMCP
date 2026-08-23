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
open FsLangMcp.Dispatcher

// ── Fixture sources ────────────────────────────────────────────────────────────

let private domainFs =
    String.concat
        "\n"
        [ "namespace Domain"
          ""
          "/// Hexagonal port modelled as a record-of-functions."
          "type TraderRole ="
          "    { Propose: int -> int -> int }"
          ""
          "/// Regression fixture: internal members are still valid FCS call-site targets."
          "type GrainContract<'T>() ="
          "    member internal _.Resolve(value: 'T) = value"
          ""
          "module InternalMemberCalls ="
          "    let private contract = GrainContract<int>()"
          "    let first = contract.Resolve 1"
          "    let second = contract.Resolve 2"
          "    let third = contract.Resolve 3"
          "    let fourth = contract.Resolve 4"
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

// ── #207 field-impact sources ──────────────────────────────────────────────────
// A SECOND record, defined in Domain and consumed only from App, carrying every
// field-site shape a field-type change has to edit differently: construction literal,
// copy-and-update, mutation of a `mutable` field, pattern destructuring, and plain
// expression reads. Deliberately disjoint from TraderRole/Propose (no "role" substring
// anywhere) so the existing exact/non-exact TraderRole assertions keep their counts.

let private shippingFs =
    String.concat
        "\n"
        [ "namespace Domain.Shipping"
          ""
          "/// Two fields of DIFFERENT types, so one query's site rows must carry"
          "/// different siteType values. `Attempts` is mutable → mutation sites exist."
          "type Shipment ="
          "    { Code: string"
          "      mutable Attempts: int }"
          ""
          "/// Generic record: pins the documented limit that siteType reports the type"
          "/// PARAMETER, not the instantiation at the site (FCS gives no per-use"
          "/// generic arguments for record fields)."
          "type Box<'T> = { Value: 'T }"
          "" ]

let private impactFs =
    String.concat
        "\n"
        [ "module App.Impact"
          ""
          "open Domain.Shipping"
          ""
          "// I1: record-literal construction — two field-set-literal sites"
          "let create (code: string) : Shipment = { Code = code; Attempts = 0 }"
          ""
          "// I2: copy-and-update — one field-set-update site"
          "let retitle (s: Shipment) (code: string) = { s with Code = code }"
          ""
          "// I3: mutation on the LHS + expression read on the RHS, same line"
          "let bump (s: Shipment) = s.Attempts <- s.Attempts + 1"
          ""
          "// I4: expression read"
          "let codeLength (s: Shipment) = s.Code.Length"
          ""
          "// I5: pattern destructuring"
          "let codeOf (s: Shipment) ="
          "    match s with"
          "    | { Code = c } -> c"
          ""
          "// I6: SynExpr.DotSet mutation. FCS reports this symbol use over the WHOLE"
          "// target expression (`parcels[0].Attempts`), not just the field ident, so the"
          "// parse-tree lookup only lines up on the END position — the case the"
          "// FieldSiteForms.MutationEnds fallback exists for."
          "let parcels = [| create \"a\" |]"
          "let bumpFirst () = parcels[0].Attempts <- 5"
          ""
          "// I7: generic-record literal + read sites"
          "let intBox: Box<int> = { Value = 1 }"
          "let textBox: Box<string> = { Value = \"x\" }"
          "let readInt = intBox.Value"
          "let readText = textBox.Value"
          "" ]

let private leafProject (sourceFiles: string list) =
    let compiles =
        sourceFiles |> List.map (fun f -> $"<Compile Include=\"{f}\" />") |> String.concat ""

    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup>{compiles}</ItemGroup>"
          "</Project>" ]

let private refProject (sourceFiles: string list) (refRelative: string) =
    let compiles =
        sourceFiles |> List.map (fun f -> $"<Compile Include=\"{f}\" />") |> String.concat ""

    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup>{compiles}</ItemGroup>"
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

let private partialSlnx =
    String.concat
        "\n"
        [ "<Solution>"
          "  <Project Path=\"Domain/Domain.fsproj\" />"
          "  <Project Path=\"Broken/Broken.fsproj\" />"
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

    let domainFsproj = write "Domain/Domain.fsproj" (leafProject [ "Domain.fs"; "Shipping.fs" ])
    let domainSource = write "Domain/Domain.fs" domainFs
    do write "Domain/Shipping.fs" shippingFs |> ignore
    do write "Stubs/Stubs.fsproj" (refProject [ "Stubs.fs" ] "../Domain/Domain.fsproj") |> ignore
    do write "Stubs/Stubs.fs" stubsFs |> ignore

    do
        write "App/App.fsproj" (refProject [ "App.fs"; "Impact.fs" ] "../Domain/Domain.fsproj")
        |> ignore

    do write "App/App.fs" appFs |> ignore
    let impactSource = write "App/Impact.fs" impactFs
    // Deliberately malformed project used to prove that a failed member cannot
    // be interpreted as a zero-match project.
    do write "Broken/Broken.fsproj" "<Project><ItemGroup>" |> ignore
    let slnxPath = write "FindSolution.slnx" slnx
    let partialSlnxPath = write "PartialFindSolution.slnx" partialSlnx

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
    member _.PartialSlnx = partialSlnxPath
    member _.DomainFsproj = domainFsproj
    member _.DomainFs = domainSource
    member _.ImpactFs = impactSource
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
      includeSiteTypes = None
      projectPath = Some projectPath
      maxResults = Some 500
      timeoutMs = None
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
    member _.``find kind=members accepts the member name as query and finds internal call sites``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! byMemberName =
                bridge.Find(
                    { findArgs fx.Slnx "Resolve" with
                        kind = Some "members"
                        ``member`` = Some "Resolve" }
                )

            Assert.Equal("succeeded", gs byMemberName "status")
            Assert.Equal(4, gi byMemberName["breakdown"] "memberUsages")

            let memberSites: JsonNode array =
                FindTests.SitesOfKinds byMemberName (Set.singleton "member-usage")

            Assert.Equal(4, memberSites.Length)

            // Preserve the older type-qualified form while fixing the documented
            // query=member form used by the tool description and getting-started guide.
            let! byDeclaringType =
                bridge.Find(
                    { findArgs fx.Slnx "GrainContract" with
                        kind = Some "members"
                        ``member`` = Some "Resolve" }
                )

            Assert.Equal("succeeded", gs byDeclaringType "status")
            Assert.Equal(4, gi byDeclaringType["breakdown"] "memberUsages")
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

    // ── #207: field-impact mode — per-site type context on record-field sites ───────
    //
    // The field scenario from #100 batch 5: a record's shape changes and every consumer,
    // including ones in sibling projects, has to be edited. `find` already LOCATED them;
    // the agent still had to open each file to decide the edit. These tests pin the two
    // halves that close that loop from the tool output alone — the type the site consumes
    // TODAY, and a site classification that distinguishes the four different edits.

    /// Sites of `kind` from a find response, in document order.
    static member private SitesOfKinds (find: JsonNode) (kinds: string Set) =
        (find["sites"] :?> JsonArray)
        |> Seq.filter (fun s -> kinds.Contains(gs s "kind"))
        |> Seq.toArray

    [<Fact>]
    member this.``#207: kind=field + includeSiteTypes types every field site and splits literal / copy-update / mutation / pattern``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "Shipment" with
                        kind = Some "field"
                        includeSiteTypes = Some true }
                )

            Assert.Equal("succeeded", gs find "status")
            let breakdown = find["breakdown"]

            // Domain/Shipping.fs is consumed ONLY from App/Impact.fs — a cross-project
            // sweep is the only thing that sees these sites at all.
            // I1 `{ Code = code; Attempts = 0 }`          → 2 literal
            // I2 `{ s with Code = code }`                  → 1 copy-update
            // I3 `s.Attempts <- s.Attempts + 1`            → 1 mutation + 1 read
            // I4 `s.Code.Length`                           → 1 read
            // I5 `| { Code = c } ->`                       → 1 pattern
            // I6 `parcels[0].Attempts <- 5`                → 1 mutation (DotSet shape)
            Assert.Equal(2, gi breakdown "fieldSetLiteral")
            Assert.Equal(1, gi breakdown "fieldSetUpdate")
            Assert.Equal(2, gi breakdown "fieldSetMutation")
            Assert.Equal(1, gi breakdown "fieldPattern")
            Assert.Equal(2, gi breakdown "fieldRead")

            let fieldKinds =
                Set.ofList
                    [ "field-set-literal"
                      "field-set-update"
                      "field-set-mutation"
                      "field-pattern"
                      "field-read" ]

            let sites = FindTests.SitesOfKinds find fieldKinds
            Assert.Equal(8, sites.Length)

            // Both mutation SHAPES are reachable: `s.Attempts <- ...` (SynExpr.LongIdentSet,
            // start positions align) and `parcels[0].Attempts <- ...` (SynExpr.DotSet, only
            // the end position aligns). A regression in either falls back to "field-read",
            // i.e. silently reports a WRITE as a read.
            let mutationLines =
                sites
                |> Array.filter (fun s -> gs s "kind" = "field-set-mutation")
                |> Array.map (fun s -> (gs s "lineText").Trim())
                |> Array.sort

            Assert.Equal<string array>(
                [| "let bump (s: Shipment) = s.Attempts <- s.Attempts + 1"
                   "let bumpFirst () = parcels[0].Attempts <- 5" |],
                mutationLines
            )

            // EVERY field row carries the key — never a missing key an agent has to guess at.
            for site in sites do
                let where = gs site "file"
                let startLine = gi site["range"] "startLine"

                Assert.True(
                    site.AsObject().ContainsKey("siteType"),
                    $"every field site must carry siteType; missing on {where}:{startLine}"
                )

            // The payoff: rows of ONE query carry DIFFERENT types, so the agent reads the
            // "old type" per site instead of cross-referencing the record definition.
            let typeOfField (fieldName: string) =
                sites
                |> Array.filter (fun s -> (gs s "symbolFullName").EndsWith("." + fieldName, StringComparison.Ordinal))
                |> Array.map (fun s -> gs s "siteType")
                |> Array.distinct

            Assert.Equal<string array>([| "string" |], typeOfField "Code")
            Assert.Equal<string array>([| "int" |], typeOfField "Attempts")

            // The ledger must account for every field site.
            let siteTypes = find["siteTypes"]
            Assert.True(gb siteTypes "requested")
            Assert.Equal(8, gi siteTypes "fieldSites")
            Assert.Equal(8, gi siteTypes "typed")
            Assert.Equal(0, gi siteTypes "degraded")
            Assert.Equal(gi siteTypes "fieldSites", gi siteTypes "typed" + gi siteTypes "degraded")

            // The documented boundary: this tool never speculates about the NEW shape.
            let note = gs find "siteTypesNote"
            Assert.Contains("check(scope='project')", note)
            Assert.Contains("does NOT typecheck", note)

            let sitesJson = find["sites"].ToJsonString()
            output.WriteLine($"#207 typed sites: {sitesJson}")
        }

    [<Fact>]
    member _.``#207: without includeSiteTypes the site row and the response envelope are unchanged``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! find = bridge.Find({ findArgs fx.Slnx "Shipment" with kind = Some "field" })

            Assert.Equal("succeeded", gs find "status")
            Assert.False(find.AsObject().ContainsKey("siteTypes"), "siteTypes must be opt-in")
            Assert.False(find.AsObject().ContainsKey("siteTypesNote"), "siteTypesNote must be opt-in")

            for site in (find["sites"] :?> JsonArray) do
                Assert.False(site.AsObject().ContainsKey("siteType"), "default rows must not carry siteType")
        }

    [<Fact>]
    member this.``#207: under kind=auto only field sites are typed — definitions and references stay lean``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "Shipment" with
                        kind = Some "auto"
                        includeSiteTypes = Some true }
                )

            Assert.Equal("succeeded", gs find "status")

            let nonFieldSites =
                FindTests.SitesOfKinds find (Set.ofList [ "definition"; "reference"; "member-usage" ])

            Assert.True((nonFieldSites.Length > 0), "kind=auto must still produce name sites for Shipment")

            for site in nonFieldSites do
                let siteKind = gs site "kind"

                Assert.False(
                    site.AsObject().ContainsKey("siteType"),
                    $"non-field site {siteKind} must not carry siteType"
                )

            // The ledger counts field sites only, not every site in the sweep.
            let siteTypes = find["siteTypes"]
            Assert.Equal(8, gi siteTypes "fieldSites")
            Assert.True((gi find "totalSites" > gi siteTypes "fieldSites"))
        }

    [<Fact>]
    member _.``#207: includeSiteTypes on a kind that produces no field sites explains the no-op instead of going silent``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "TraderRole" with
                        kind = Some "definition"
                        includeSiteTypes = Some true }
                )

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal(0, gi find["siteTypes"] "fieldSites")
            Assert.Equal(0, gi find["siteTypes"] "degraded")

            let note = gs find "siteTypesNote"
            Assert.Contains("kind='field'", note)
            Assert.Contains("produced none", note)
        }

    [<Fact>]
    member this.``#207: past the per-site deadline every field row degrades to JSON null and the ledger says so``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")

            // The ONE degraded arm that is reachable in production: a solution large enough
            // that find's wall-clock budget expires between a project's sweep returning and
            // its site loop finishing. It cannot be reached with timeoutMs alone — timeoutMs=0
            // is already exhausted BEFORE the sweep, so the project fails and yields no sites
            // at all (that path is covered separately). The seam supplies the clock verdict so
            // the branch runs deterministically, with everything else the real code.
            let bridge = FcsBridge(findSiteTypeDeadlineExpiredOverride = (fun () -> true))

            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "Shipment" with
                        kind = Some "field"
                        includeSiteTypes = Some true }
                )

            // 1. A per-site miss NEVER fails the call, and never drops a site.
            Assert.Equal("succeeded", gs find "status")

            let fieldKinds =
                Set.ofList
                    [ "field-set-literal"
                      "field-set-update"
                      "field-set-mutation"
                      "field-pattern"
                      "field-read" ]

            let sites = FindTests.SitesOfKinds find fieldKinds
            Assert.Equal(8, sites.Length)

            // 2. The ledger with degraded > 0 — the state no other test reaches, so the
            //    identity is only actually exercised here.
            let siteTypes = find["siteTypes"]
            Assert.Equal(8, gi siteTypes "fieldSites")
            Assert.Equal(0, gi siteTypes "typed")
            Assert.Equal(8, gi siteTypes "degraded")
            Assert.Equal(8, gi siteTypes "degradedTimedOut")
            Assert.Equal(0, gi siteTypes "degradedUnresolved")
            Assert.Equal(gi siteTypes "fieldSites", gi siteTypes "typed" + gi siteTypes "degraded")

            Assert.Equal(
                gi siteTypes "degraded",
                gi siteTypes "degradedUnresolved" + gi siteTypes "degradedTimedOut"
            )

            // 3. Every row KEEPS the key and carries JSON null — not a dropped key, and not
            //    the string "null", which a naive presence check would accept.
            for site in sites do
                Assert.True(site.AsObject().ContainsKey("siteType"), "a degraded row must keep the key")
                Assert.True(isNull site["siteType"], "a degraded row's siteType must be JSON null")

            let sitesJson = find["sites"].ToJsonString()
            Assert.Contains("\"siteType\":null", sitesJson)
            Assert.DoesNotContain("\"siteType\":\"null\"", sitesJson)

            // 4. The note names the degradation and its cause instead of claiming success.
            let note = gs find "siteTypesNote"
            Assert.Contains("8 of 8 field sites could not be typed", note)
            Assert.Contains("timeoutMs", note)

            // 5. Site kinds are parse-tree-derived and must survive an exhausted type budget
            //    untouched — losing the classification too would double the damage.
            Assert.Equal(2, gi find["breakdown"] "fieldSetLiteral")
            Assert.Equal(2, gi find["breakdown"] "fieldSetMutation")
            Assert.Equal(1, gi find["breakdown"] "fieldPattern")
        }

    [<Fact>]
    member _.``#207: an empty typed result on an incomplete sweep blames coverage, not the caller's kind``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // timeoutMs=0 is an already-exhausted budget, so no project is analyzed and no
            // field site exists. The note must NOT tell the caller to "use kind='field'" —
            // they already did; the sweep simply never ran. (Same determinism trick as the
            // P1-01 exhausted-budget test: no background FCS work is started.)
            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "Shipment" with
                        kind = Some "field"
                        includeSiteTypes = Some true
                        timeoutMs = Some 0 }
                )

            Assert.Equal("unknown", gs find "status")
            Assert.Equal(0, gi find["siteTypes"] "fieldSites")

            let note = gs find "siteTypesNote"
            Assert.Contains("sweep is incomplete", note)
            Assert.Contains("timeoutMs", note)
            Assert.DoesNotContain("Use kind='field'", note)
        }

    [<Fact>]
    member this.``#207: on a generic record siteType reports the type PARAMETER, the documented limit``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "Box" with
                        kind = Some "field"
                        field = Some "Value"
                        includeSiteTypes = Some true }
                )

            Assert.Equal("succeeded", gs find "status")

            let sites =
                FindTests.SitesOfKinds find (Set.ofList [ "field-set-literal"; "field-read" ])

            // `{ Value = 1 }`, `{ Value = "x" }`, `intBox.Value`, `textBox.Value`.
            Assert.Equal(4, sites.Length)

            // FCS reports no per-use generic arguments for record fields, so every site of
            // Box.Value renders as 'T rather than int/string. This test PINS that limit: if
            // a future FCS starts instantiating, it fails and we upgrade the docs with it.
            let distinct = sites |> Array.map (fun s -> gs s "siteType") |> Array.distinct
            Assert.Equal<string array>([| "'T" |], distinct)
            Assert.Contains("type PARAMETER", gs find "siteTypesNote")
        }

    [<Fact>]
    member this.``#207: the type column stays inside find's documented page budget``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let baseArgs = { findArgs fx.Slnx "Shipment" with kind = Some "field" }
            let! lean = bridge.Find(baseArgs)
            let! fat = bridge.Find({ baseArgs with includeSiteTypes = Some true })

            let leanChars = lean["sites"].ToJsonString().Length
            let fatChars = fat["sites"].ToJsonString().Length
            let siteCount = (fat["sites"] :?> JsonArray).Count
            Assert.True((siteCount > 0), "budget check needs sites to measure")

            let perSiteGrowth = float (fatChars - leanChars) / float siteCount

            // Hard per-site cap: siteTypeMaxChars (200) + the "..." truncation marker.
            let siteTypeCapChars = 203

            for site in (fat["sites"] :?> JsonArray) do
                match site["siteType"] with
                | null -> ()
                | v ->
                    let text = v.GetValue<string>()

                    Assert.True(
                        (text.Length <= siteTypeCapChars),
                        $"siteType must be capped at {siteTypeCapChars} chars; got {text.Length}"
                    )

            // The envelope FcsBridge.Find documents for the compact row: ~527 chars per
            // site, default page 80, MCP ceiling ~72k chars (~25k tokens). Worst case with
            // the column is a capped type on every row plus its JSON key. Asserted as
            // arithmetic so raising the cap without re-doing the page math fails HERE.
            let compactSiteChars = 527
            let defaultPageSize = 80
            let mcpCeilingChars = 72_000
            let jsonKeyOverhead = 15
            let worstCasePage = defaultPageSize * (compactSiteChars + siteTypeCapChars + jsonKeyOverhead)

            Assert.True(
                (worstCasePage < mcpCeilingChars),
                $"a full default page with the type column must stay under the MCP ceiling; worst case {worstCasePage} >= {mcpCeilingChars}"
            )

            // ...and the real-world growth is far below the worst case, so the default page
            // size does not need lowering when the flag is on.
            Assert.True(
                (perSiteGrowth <= float (siteTypeCapChars + jsonKeyOverhead)),
                $"measured per-site growth {perSiteGrowth} exceeded the capped worst case"
            )

            output.WriteLine(
                $"#207 budget: {siteCount} sites, lean={leanChars} fat={fatChars} chars, +{perSiteGrowth:F1}/site; worst-case 80-site page={worstCasePage}"
            )
        }

    [<Fact>]
    member _.``#207: a swept file with a parse error degrades per site, never the whole call``() : Task =
        task {
            // The degraded path the spec asks for. NOTE the finding this test records:
            // FCS resolves field TYPES through a parse error just fine (verified against
            // 43.12.400), so what a broken file actually costs is the parse-tree form
            // classification, not siteType. The contract asserted here is the one that
            // matters either way — the ledger balances and the call still succeeds.
            let runId = Guid.NewGuid().ToString("N")
            let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_207_{runId}")
            let dir = Path.Combine(root, "Broken207")
            Directory.CreateDirectory dir |> ignore

            try
                let source =
                    String.concat
                        "\n"
                        [ "module Broken207.Library"
                          ""
                          "type Parcel = { Code: string; Attempts: int }"
                          ""
                          "let ok = { Code = \"a\"; Attempts = 1 }"
                          "let broken (p: Parcel) = p.Code +"
                          "let after (p: Parcel) = p.Attempts"
                          "" ]

                File.WriteAllText(Path.Combine(dir, "Library.fs"), source)

                let projectPath = Path.Combine(dir, "Broken207.fsproj")
                File.WriteAllText(projectPath, leafProject [ "Library.fs" ])

                let bridge = FcsBridge()

                let! find =
                    bridge.Find(
                        { findArgs projectPath "Parcel" with
                            kind = Some "field"
                            includeSiteTypes = Some true }
                    )

                // A per-site miss must never become a whole-call failure.
                Assert.Equal("succeeded", gs find "status")

                let siteTypes = find["siteTypes"]
                let fieldSites = gi siteTypes "fieldSites"
                Assert.True((fieldSites > 0), "the broken file must still yield field sites")

                Assert.Equal(fieldSites, gi siteTypes "typed" + gi siteTypes "degraded")

                Assert.Equal(
                    gi siteTypes "degraded",
                    gi siteTypes "degradedUnresolved" + gi siteTypes "degradedTimedOut"
                )

                // Whatever the per-site outcome, the key is present on every field row so a
                // consumer reads `null` rather than an absent key.
                for site in (find["sites"] :?> JsonArray) do
                    Assert.True(site.AsObject().ContainsKey("siteType"))

                let typedCount = gi siteTypes "typed"
                let degradedCount = gi siteTypes "degraded"
                let breakdownJson = find["breakdown"].ToJsonString()

                output.WriteLine(
                    $"#207 parse-error fixture: fieldSites={fieldSites}, typed={typedCount}, degraded={degradedCount}, breakdown={breakdownJson}"
                )
            finally
                if Directory.Exists root then
                    try
                        Directory.Delete(root, true)
                    with _ ->
                        ()
        }

    [<Fact>]
    member _.``find with empty query returns invalid_args naming query``() : Task =
        task {
            let bridge = FcsBridge()
            let! result = bridge.Find(findArgs fx.Slnx "   ")

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("Expected parameters", gs result "message")
            Assert.Contains("query", gs result "message")
            Assert.Contains("kind", gs result "message")
            Assert.Contains("scope", gs result "message")
            Assert.Contains("projectPath", gs result "message")
        }

    [<Fact>]
    member _.``find rejects unknown enums unsafe ranges missing scoped context and malformed cursors``() : Task =
        task {
            let bridge = FcsBridge()
            let baseline = findArgs fx.Slnx "TraderRole"

            let cases =
                [ "kind", { baseline with kind = Some "typo" }, "kind must be one of"
                  "scope", { baseline with scope = Some "solution" }, "scope must be one of"
                  "maxResults-low", { baseline with maxResults = Some 0 }, "maxResults"
                  "maxResults-high", { baseline with maxResults = Some 1001 }, "maxResults"
                  "contextLines", { baseline with contextLines = Some -1 }, "contextLines"
                  "timeoutMs", { baseline with timeoutMs = Some -1 }, "timeoutMs"
                  "line", { baseline with line = Some -1 }, "line"
                  "character", { baseline with character = Some -1 }, "character"
                  "occurrence", { baseline with occurrence = Some -2 }, "occurrence"
                  "file-path", { baseline with scope = Some "file" }, "requires a non-empty path"
                  "project-context", { baseline with scope = Some "project" }, "whole solution"
                  "cursor", { baseline with cursor = Some "not-base64" }, "Invalid cursor" ]

            for (label, invalidArgs, expectedMessage) in cases do
                let! result = bridge.Find(invalidArgs)

                Assert.True(
                    String.Equals("invalid_args", gs result "status", StringComparison.Ordinal),
                    $"%s{label} should return invalid_args: %s{result.ToJsonString()}"
                )

                Assert.Contains(expectedMessage, gs result "message")
        }

    [<Fact>]
    member _.``find project scope cannot escape the requested solution membership``() : Task =
        task {
            let outsideRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_outside_{Guid.NewGuid():N}")
            let outsideProject = Path.Combine(outsideRoot, "Outside.fsproj")
            let outsideSource = Path.Combine(outsideRoot, "Outside.fs")

            try
                Directory.CreateDirectory(outsideRoot) |> ignore
                File.WriteAllText(outsideProject, leafProject [ "Outside.fs" ])
                File.WriteAllText(outsideSource, "module Outside")
                let bridge = FcsBridge()

                let! result =
                    bridge.Find(
                        { findArgs fx.Slnx "TraderRole" with
                            scope = Some "project"
                            path = Some outsideSource }
                    )

                Assert.Equal("invalid_args", gs result "status")
                Assert.Contains("not a member", gs result "message")
                Assert.Contains(Path.GetFullPath(outsideProject), gs result "message")
            finally
                if Directory.Exists outsideRoot then
                    Directory.Delete(outsideRoot, true)
        }

    [<Fact>]
    member _.``find file scope keeps an explicit fsproj authoritative for an external path``() : Task =
        task {
            let outsideRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_linked_{Guid.NewGuid():N}")
            let outsideProject = Path.Combine(outsideRoot, "Outside.fsproj")
            let outsideSource = Path.Combine(outsideRoot, "Linked.fs")

            try
                Directory.CreateDirectory(outsideRoot) |> ignore
                File.WriteAllText(outsideProject, leafProject [ "Linked.fs" ])
                File.WriteAllText(outsideSource, "module Linked")
                let bridge = FcsBridge()

                // A zero budget avoids loading FCS; the per-project timeout envelope
                // still reveals which project the scope resolver selected.
                let! result =
                    bridge.Find(
                        { findArgs fx.DomainFsproj "TraderRole" with
                            scope = Some "file"
                            path = Some outsideSource
                            timeoutMs = Some 0 }
                    )

                Assert.Equal("unknown", gs result "status")
                Assert.Equal(1, gi result "projectsRequested")
                let perProject = result["perProject"].AsArray()
                Assert.Single(perProject) |> ignore
                Assert.Equal(Path.GetFullPath(fx.DomainFsproj), gs perProject[0] "fsproj")
                Assert.False(
                    String.Equals(
                        Path.GetFullPath(outsideProject),
                        gs perProject[0] "fsproj",
                        StringComparison.Ordinal
                    )
                )

                // #193 round 2: this is scope='file' AND incomplete coverage at once — the
                // note must combine both honestly: it cannot claim the project was "swept"
                // (it timed out), and it must still name the file-level filter, not just the
                // sibling-project blind spot.
                Assert.Equal(
                    $"find could not fully analyze this project (0 failed, 1 timed out) — this response is incomplete; see coverage/message before trusting an absence of matches. Sites, where present, are also filtered to '{Path.GetFullPath(outsideSource)}'; other files in this project, and all sibling projects, are not visible. To sweep the whole solution, pass its .sln/.slnx as projectPath (or set_project it) with scope='workspace'.",
                    gs result "scopeNote"
                )
            finally
                if Directory.Exists outsideRoot then
                    Directory.Delete(outsideRoot, true)
        }

    // ── #193 (post-review design revision): NO narrowing mechanism — sweep breadth
    // is unchanged everywhere, exactly as at BASE. Review proved SolutionParsing.
    // listProjects(<.fsproj>) = [itself] already at BASE and at v0.13.1, so an
    // "auto narrows on an explicit .fsproj" mechanism can never change what gets
    // swept — it is unreachable dead logic, and worse, a scopeNote telling an agent
    // to retry with scope='workspace' + the SAME .fsproj projectPath is an inert
    // escape hatch (it re-sweeps the identical one project). Instead, `find` now
    // always emits a top-level scopeNote naming the ACTUAL outcome — one project
    // swept vs. N member projects of a solution — independent of which `scope`
    // argument produced it, with a widening/narrowing recipe that names the
    // argument that actually works (projectPath, not scope alone).

    [<Fact>]
    member _.``#193 a single-project sweep (explicit fsproj, scope=auto) emits the single-project scopeNote``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! find = bridge.Find(findArgs fx.DomainFsproj "TraderRole")

            Assert.Equal("succeeded", gs find "status")
            // scope is a pure echo of the request — never resolved/rewritten.
            Assert.Equal("auto", gs find "scope")
            Assert.Equal(1, gi find "projectsSwept")

            let perProject = find["perProject"].AsArray()
            Assert.Single(perProject) |> ignore
            Assert.Equal(Path.GetFullPath(fx.DomainFsproj), gs perProject[0] "fsproj")

            Assert.True(find.AsObject().ContainsKey("scopeNote"), "every response carries a top-level scopeNote")

            Assert.Equal(
                "find swept only this one project — cross-project usages in sibling projects are not visible. To sweep the whole solution, pass its .sln/.slnx as projectPath (or set_project it) with scope='workspace'.",
                gs find "scopeNote"
            )
        }

    [<Fact>]
    member _.``#193 a multi-project sweep (explicit solution path, scope=auto) emits the multi-project scopeNote with the swept count``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! find = bridge.Find(findArgs fx.Slnx "TraderRole")

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal("auto", gs find "scope")
            // Unchanged from BASE: an explicit .slnx still sweeps every member project.
            Assert.Equal(3, gi find "projectsSwept")

            Assert.Equal(
                $"find swept 3 member projects of '{Path.GetFullPath(fx.Slnx)}'. To narrow to just one project (faster, but misses cross-project usages), pass its .fsproj as projectPath.",
                gs find "scopeNote"
            )
        }

    [<Fact>]
    member _.``#193 a single-project sweep reached via path-only fallback (no projectPath) gets the identical note — 'however it arrived'``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // No projectPath at all — sweepTarget is resolved purely from `path` via
            // findNearestFsproj (Domain.fsproj). The note is driven by the OUTCOME
            // (one project swept), not by how the sweep target arrived, so it must
            // read identically to the explicit-projectPath case above.
            let! find =
                bridge.Find(
                    { findArgs fx.DomainFsproj "TraderRole" with
                        projectPath = None
                        path = Some fx.DomainFs }
                )

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal("auto", gs find "scope")
            Assert.Equal(1, gi find "projectsSwept")

            Assert.Equal(
                "find swept only this one project — cross-project usages in sibling projects are not visible. To sweep the whole solution, pass its .sln/.slnx as projectPath (or set_project it) with scope='workspace'.",
                gs find "scopeNote"
            )
        }

    [<Fact>]
    member _.``#193 explicit scope=workspace with only a bare fsproj as projectPath still sweeps just that one project``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // `scope='workspace'` alone cannot widen a sweep target that is already a
            // single .fsproj — SolutionParsing.listProjects(<.fsproj>) = [itself]
            // regardless of the requested scope, so this call is NOT the escape hatch
            // it might look like. This is the assertion the review's Important 2
            // finding named explicitly: projectsSwept must be pinned to 1, not assumed.
            let! find =
                bridge.Find(
                    { findArgs fx.DomainFsproj "TraderRole" with
                        scope = Some "workspace" }
                )

            Assert.Equal("succeeded", gs find "status")
            // scope is a pure echo — "workspace" is reported even though only one
            // project was actually swept; scopeResolved / projectsSwept carry the truth.
            Assert.Equal("workspace", gs find "scope")
            let resolution = find["resolution"]
            Assert.Equal("project", gs resolution "scopeResolved")
            Assert.Equal(1, gi find "projectsSwept")

            Assert.Equal(
                "find swept only this one project — cross-project usages in sibling projects are not visible. To sweep the whole solution, pass its .sln/.slnx as projectPath (or set_project it) with scope='workspace'.",
                gs find "scopeNote"
            )
        }

    [<Fact>]
    member _.``#193 the corrected escape hatch works — scope=workspace with the SOLUTION path as projectPath sweeps every member project``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // The scopeNote's widening recipe names projectPath (the solution path),
            // not scope alone — this proves that recipe genuinely widens the sweep,
            // unlike the previous (reverted) design's inert "pass scope='workspace'"
            // remedy against the SAME .fsproj projectPath.
            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "TraderRole" with
                        scope = Some "workspace" }
                )

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal("workspace", gs find "scope")
            Assert.Equal(3, gi find "projectsSwept")

            Assert.Equal(
                $"find swept 3 member projects of '{Path.GetFullPath(fx.Slnx)}'. To narrow to just one project (faster, but misses cross-project usages), pass its .fsproj as projectPath.",
                gs find "scopeNote"
            )
        }

    [<Fact>]
    member _.``#193 round 2: scope=file names the file-level filter too, not just the sibling-project blind spot``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // scope=file sweeps the one owning project (like scope=project), then
            // additionally post-filters sites down to args.path (FcsBridge.fs's `allSites`
            // filter). The single-project note's generic "cross-project usages... not
            // visible" text is true but understates it — files OTHER than fx.DomainFs in
            // this SAME project are also invisible here, which the note must say.
            let! find =
                bridge.Find(
                    { findArgs fx.DomainFsproj "TraderRole" with
                        scope = Some "file"
                        path = Some fx.DomainFs }
                )

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal("file", gs find "scope")
            Assert.Equal(1, gi find "projectsSwept")

            Assert.Equal(
                $"find swept only this one project, and kept only sites in '{Path.GetFullPath(fx.DomainFs)}' — other files in this project, and all sibling projects, are not visible. To sweep the whole solution, pass its .sln/.slnx as projectPath (or set_project it) with scope='workspace'.",
                gs find "scopeNote"
            )
        }

    [<Fact>]
    member _.``#193 round 2: a single-project sweep with incomplete coverage and no file filter combines both honestly``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // The fourth (coverageComplete, filterPath) combination — round 2's commit
            // (f68fc51) claimed all four were tested, but this one (single project,
            // non-file scope, incomplete coverage) was not. Reached via an explicit
            // .fsproj projectPath with timeoutMs=0: the project times out before
            // analysis runs, so the note must say "could not fully analyze" rather
            // than "swept", and — since scope != 'file' — there is no file filter to
            // name, only the sibling-project blind spot.
            let! find =
                bridge.Find(
                    { findArgs fx.DomainFsproj "TraderRole" with
                        timeoutMs = Some 0 }
                )

            Assert.Equal("unknown", gs find "status")
            Assert.Equal(1, gi find "projectsRequested")
            Assert.Equal(0, gi find "projectsAnalyzed")
            Assert.Equal(0, gi find "projectsFailed")
            Assert.Equal(1, gi find "projectsTimedOut")

            Assert.Equal(
                "find could not fully analyze this project (0 failed, 1 timed out) — this response is incomplete; see coverage/message before trusting an absence of matches. Cross-project usages in sibling projects are also not visible. To sweep the whole solution, pass its .sln/.slnx as projectPath (or set_project it) with scope='workspace'.",
                gs find "scopeNote"
            )
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
            Assert.Equal("not_found", gs find "outcome")
            Assert.True(gb find["coverage"] "complete")
            Assert.Equal(3, gi find "projectsRequested")
            Assert.Equal(3, gi find "projectsAnalyzed")
            Assert.Equal(0, gi find "projectsFailed")
            Assert.Equal(0, gi find "projectsTimedOut")
            Assert.Equal("none", gs resolution "via")
            Assert.Equal(3, gi resolution "projectsSwept")
        }

    [<Fact>]
    member _.``P1-01 incomplete project sweep returns partial or unknown and never authoritative false``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // Domain is valid and contains TraderRole; Broken.fsproj is malformed.
            // A positive result is useful, but the full result set is incomplete.
            let! partial = bridge.Find(findArgs fx.PartialSlnx "TraderRole")
            let partialCoverage = partial["coverage"]
            let partialResolution = partial["resolution"]

            Assert.Equal("partial", gs partial "status")
            Assert.Equal("matched", gs partial "outcome")
            Assert.True(gb partialResolution "matched")
            Assert.False(gb partialCoverage "complete")
            Assert.Equal(2, gi partialCoverage "projectsRequested")
            Assert.Equal(1, gi partialCoverage "projectsAnalyzed")
            Assert.Equal(1, gi partialCoverage "projectsFailed")
            Assert.Equal(0, gi partialCoverage "projectsTimedOut")

            // A genuine zero from the FSAC index cannot fill a hole in the FCS
            // project sweep. Absence is still indeterminate, so matched is JSON null.
            let! indexedMiss =
                bridge.Find(
                    findArgs fx.PartialSlnx "ZzzNoSuchSymbol_168",
                    fsacProbe = (fun _ -> Task.FromResult(FindFsacProbeResult.Available 0))
                )

            let indexedMissResolution = indexedMiss["resolution"]
            Assert.Equal("unknown", gs indexedMiss "status")
            Assert.Equal("indeterminate", gs indexedMiss "outcome")
            Assert.Null(indexedMissResolution["matched"])
            Assert.Equal("available", gs indexedMissResolution "fsacFallbackState")
            Assert.Equal("incomplete-fcs-sweep", gs indexedMissResolution "via")

            // A context-mismatched FSAC response must also remain typed rather than
            // being folded into an authoritative zero-hit result.
            let! contextUnknown =
                bridge.Find(
                    findArgs fx.PartialSlnx "ZzzNoSuchSymbol_168",
                    fsacProbe = (fun _ ->
                        Task.FromResult(
                            FindFsacProbeResult.ContextMismatch
                                "active FSAC workspace differs from the requested project"
                        ))
                )

            let contextUnknownResolution = contextUnknown["resolution"]
            Assert.Equal("unknown", gs contextUnknown "status")
            Assert.Equal("indeterminate", gs contextUnknown "outcome")
            Assert.Null(contextUnknownResolution["matched"])
            Assert.Equal("context_mismatch", gs contextUnknownResolution "fsacFallbackState")
            Assert.Equal("incomplete-fcs-sweep", gs contextUnknownResolution "via")

            // A valid positive FSAC result proves presence, but cannot make the
            // incomplete FCS site set look complete.
            let! fsacPositive =
                bridge.Find(
                    findArgs fx.PartialSlnx "ZzzFsacOnly_168",
                    fsacProbe = (fun _ -> Task.FromResult(FindFsacProbeResult.Available 1))
                )

            Assert.Equal("partial", gs fsacPositive "status")
            Assert.Equal("matched", gs fsacPositive "outcome")
            Assert.True(gb fsacPositive["resolution"] "matched")
            Assert.Equal("fsac-symbol-index", gs fsacPositive["resolution"] "via")
        }

    [<Fact>]
    member _.``P1-01 exhausted find budget classifies every skipped project as timed out``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // Zero is already an exhausted wall-clock budget. This makes the
            // classification deterministic without starting background FCS work.
            let! result =
                bridge.Find(
                    { findArgs fx.Slnx "ZzzTimeout_168" with
                        timeoutMs = Some 0 }
                )

            Assert.Equal("unknown", gs result "status")
            Assert.Equal("indeterminate", gs result "outcome")
            Assert.Null(result["resolution"]["matched"])
            Assert.Equal(3, gi result "projectsRequested")
            Assert.Equal(0, gi result "projectsAnalyzed")
            Assert.Equal(0, gi result "projectsFailed")
            Assert.Equal(3, gi result "projectsTimedOut")

            let perProject = result["perProject"].AsArray()
            Assert.Equal(3, perProject.Count)

            for project in perProject do
                Assert.Equal("timed_out", gs project "status")
                Assert.Equal("timeout", gs project "errorKind")

            // #193 round 2: this response's coverage is INCOMPLETE (0 of 3 analyzed) —
            // scopeNote must say so honestly ("analyzed 0 of 3") rather than claiming
            // "find swept 3 member projects", which would overstate work that never ran.
            Assert.Equal(
                $"find analyzed 0 of 3 member projects of '{Path.GetFullPath(fx.Slnx)}' (0 failed, 3 timed out) — this response is incomplete; see coverage/message before trusting an absence of matches. To narrow to just one project (faster, but misses cross-project usages), pass its .fsproj as projectPath.",
                gs result "scopeNote"
            )
        }

    [<Fact>]
    member _.``P1-01 dispatcher preserves context mismatch and infrastructure failure as typed probe states``() =
        let mismatch =
            JsonNode.Parse("""{"status":"context_mismatch","message":"wrong project"}""")
            |> FindDispatch.classifyFsacProbeResponse

        let failure =
            JsonNode.Parse("""{"status":"infrastructure_error","message":"rpc disconnected"}""")
            |> FindDispatch.classifyFsacProbeResponse

        let available =
            JsonNode.Parse("""{"status":"ok","contextMatched":true,"sessionGeneration":7,"result":[{},{}]}""")
            |> FindDispatch.classifyFsacProbeResponse

        let unbound =
            JsonNode.Parse("""{"status":"ok","result":[]}""")
            |> FindDispatch.classifyFsacProbeResponse

        match mismatch with
        | FindFsacProbeResult.ContextMismatch reason -> Assert.Contains("wrong project", reason)
        | other -> Assert.Fail($"expected ContextMismatch, got {other}")

        match failure with
        | FindFsacProbeResult.Failed reason -> Assert.Contains("disconnected", reason)
        | other -> Assert.Fail($"expected Failed, got {other}")

        match available with
        | FindFsacProbeResult.Available hits -> Assert.Equal(2, hits)
        | other -> Assert.Fail($"expected Available, got {other}")

        match unbound with
        | FindFsacProbeResult.Failed reason -> Assert.Contains("contextMatched", reason)
        | other -> Assert.Fail($"expected Failed, got {other}")

    [<Fact>]
    member _.``P1-02 FSAC workspace fallback cannot escape the requested find scope``() =
        let projectA = Path.Combine(fx.Root, "Domain", "Domain.fsproj")
        let solution = fx.Slnx

        let eligible scope requested active =
            FindDispatch.fsacWorkspaceProbeEligibility
                { findArgs requested "OnlyInAnotherScope" with scope = Some scope }
                (Some active)

        match eligible "file" projectA projectA with
        | Error reason -> Assert.Contains("scope=file", reason)
        | Ok() -> Assert.Fail("workspace/symbol must never prove a file-scoped match")

        match eligible "project" solution solution with
        | Error reason -> Assert.Contains("project-scoped", reason)
        | Ok() -> Assert.Fail("an active solution is broader than scope=project")

        match eligible "project" projectA solution with
        | Error reason -> Assert.Contains("broader", reason)
        | Ok() -> Assert.Fail("a solution workspace must not satisfy a member-project request")

        match eligible "project" projectA projectA with
        | Ok() -> ()
        | Error reason -> Assert.Fail($"the exact active fsproj is a safe project scope: {reason}")

        match eligible "workspace" solution solution with
        | Ok() -> ()
        | Error reason -> Assert.Fail($"the exact active solution is a safe workspace scope: {reason}")

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

    [<Fact>]
    member _.``find use-cache invalidates referenced projects after a same-mtime content edit (P1-07)``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()
            let originalMtime = File.GetLastWriteTimeUtc fx.DomainFs

            try
                let! before = bridge.Find(findArgs fx.Slnx "TraderRole")
                Assert.Equal("succeeded", gs before "status")
                let beforeSites = gi before "totalSites"
                let beforeCacheCount = bridge.ProjectUsesCacheCount
                Assert.Equal(gi before "projectsSwept", beforeCacheCount)

                // Replace the dependency's declaration without changing its byte count
                // or timestamp. The former mtime/length stamps re-keyed neither Domain
                // nor its consumers and therefore returned their cached symbol sweeps.
                let edited =
                    domainFs.Replace("TraderRole", "DealerRole", StringComparison.Ordinal)

                Assert.Equal(domainFs.Length, edited.Length)
                File.WriteAllText(fx.DomainFs, edited)
                File.SetLastWriteTimeUtc(fx.DomainFs, originalMtime)

                Assert.Equal(originalMtime, File.GetLastWriteTimeUtc fx.DomainFs)
                Assert.Equal(int64 domainFs.Length, FileInfo(fx.DomainFs).Length)

                let! after = bridge.Find(findArgs fx.Slnx "TraderRole")
                Assert.Equal("succeeded", gs after "status")
                let afterSites = gi after "totalSites"

                // One new entry is the edited Domain project. Growth by more than one
                // proves that at least one consumer key also includes the referenced
                // project's source bytes, even though all metadata is unchanged.
                Assert.True(
                    bridge.ProjectUsesCacheCount > beforeCacheCount + 1,
                    $"a same-mtime dependency edit must re-key consumer entries: before={beforeCacheCount}, after={bridge.ProjectUsesCacheCount} (expected > {beforeCacheCount + 1})"
                )

                output.WriteLine(
                    $"P1-07 same-mtime dependency edit: beforeCache={beforeCacheCount}, afterCache={bridge.ProjectUsesCacheCount}, beforeSites={beforeSites}, afterSites={afterSites}"
                )
            finally
                // Restore both bytes and metadata for sibling tests sharing this fixture.
                try
                    File.WriteAllText(fx.DomainFs, domainFs)
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

    let fsproj = write "ConfigProbe/ConfigProbe.fsproj" (leafProject [ "ConfigProbe.fs" ])
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

// ── #207 review: one linked file, two projects, two different types ─────────────
//
// find de-duplicates sites by physical source location, so a `.fs` linked into two
// `.fsproj` files is swept once per project — and `#if` (or any other per-project type
// context) can make the SAME range resolve to different field types. Before the fix the
// last project swept overwrote the row outright: the payload reported ProjB's `string`,
// counted the site as `typed`, and said nothing about ProjA's `int`.
//
// The fixture needs no `dotnet build`: neither project references anything, so ProjInfo
// resolves options on its own and the whole sweep is a couple of seconds.

let private linkedSharedFs =
    String.concat
        "\n"
        [ "module Shared"
          ""
          "type Parcel ="
          "    { Weight:"
          "#if ALT"
          "        string"
          "#else"
          "        int"
          "#endif"
          "    }"
          ""
          "// ONE physical site, compiled by BOTH projects — `int` in ProjA, `string` in ProjB."
          "let create w = { Weight = w }"
          "" ]

let private linkedProject (defineConstants: string option) =
    let defines =
        match defineConstants with
        | Some value -> $"<DefineConstants>{value}</DefineConstants>"
        | None -> ""

    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          $"  <PropertyGroup><TargetFramework>net10.0</TargetFramework>{defines}</PropertyGroup>"
          "  <ItemGroup><Compile Include=\"../Shared/Shared.fs\" Link=\"Shared.fs\" /></ItemGroup>"
          "</Project>" ]

type LinkedSourceFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_findlinked_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    let sharedSource = write "Shared/Shared.fs" linkedSharedFs
    do write "ProjA/ProjA.fsproj" (linkedProject None) |> ignore
    do write "ProjB/ProjB.fsproj" (linkedProject (Some "ALT")) |> ignore

    let slnxPath =
        write
            "Linked.slnx"
            (String.concat
                "\n"
                [ "<Solution>"
                  "  <Project Path=\"ProjA/ProjA.fsproj\" />"
                  "  <Project Path=\"ProjB/ProjB.fsproj\" />"
                  "</Solution>" ])

    member _.Slnx = slnxPath
    member _.SharedSource = sharedSource

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

type FindLinkedSourceTests(fx: LinkedSourceFixture) =
    interface IClassFixture<LinkedSourceFixture>

    [<Fact>]
    member _.``#207 review: a site two projects type differently keeps the first type and lists the other``
        ()
        : Task =
        task {
            let bridge = FcsBridge()

            let! find =
                bridge.Find(
                    { findArgs fx.Slnx "Parcel" with
                        kind = Some "field"
                        includeSiteTypes = Some true }
                )

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal(2, gi find "projectsAnalyzed")

            // De-duplication by physical location is intact: ONE row, not one per project.
            let sites = find["sites"] :?> JsonArray
            Assert.Equal(1, sites.Count)
            let site = sites[0]

            Assert.Equal("field-set-literal", gs site "kind")
            Assert.Equal(fx.SharedSource, gs site "file")

            // The FIRST project to resolve the site owns both the type and the label; the
            // other project's answer is preserved instead of overwriting it.
            Assert.Equal("ProjA", gs site "project")
            Assert.Equal("int", gs site "siteType")

            // #207 review 2: each alternative names the project that resolved it, so
            // "plan the site per project" is something the caller can actually do.
            let alternatives = site["siteTypeAlternatives"] :?> JsonArray
            let alternative = Assert.Single(alternatives)
            Assert.Equal("string", gs alternative "siteType")

            let alternativeProjects =
                (alternative["projects"] :?> JsonArray)
                |> Seq.map (fun value -> value.GetValue<string>())
                |> Seq.toList

            Assert.Equal<string list>([ "ProjB" ], alternativeProjects)
            Assert.False(site.AsObject().ContainsKey("siteTypeAlternativesOmitted"))

            // Ledger: the row is still `typed` — the identity typed + degraded = fieldSites
            // must not move — and the disagreement gets its own subset counter plus a note.
            let ledger = find["siteTypes"]
            Assert.Equal(1, gi ledger "fieldSites")
            Assert.Equal(1, gi ledger "typed")
            Assert.Equal(0, gi ledger "degraded")
            Assert.Equal(1, gi ledger "typedDifferentlyByAnotherProject")
            Assert.Contains("resolved to DIFFERENT types", gs find "siteTypesNote")
        }

    [<Fact>]
    member _.``#207 review: a single-project sweep carries no alternatives and no disagreement count``() : Task =
        task {
            // The control: the same query narrowed to ONE project must produce the row shape
            // that shipped before the fix — no siteTypeAlternatives key at all.
            let bridge = FcsBridge()
            let projA = Path.Combine(Path.GetDirectoryName fx.Slnx, "ProjA", "ProjA.fsproj")

            let! find =
                bridge.Find(
                    { findArgs projA "Parcel" with
                        kind = Some "field"
                        includeSiteTypes = Some true }
                )

            Assert.Equal("succeeded", gs find "status")
            Assert.Equal(1, gi find "projectsAnalyzed")

            let site = (find["sites"] :?> JsonArray)[0]
            Assert.Equal("int", gs site "siteType")
            Assert.False(site.AsObject().ContainsKey("siteTypeAlternatives"))
            Assert.Equal(0, gi find["siteTypes"] "typedDifferentlyByAnotherProject")
        }
