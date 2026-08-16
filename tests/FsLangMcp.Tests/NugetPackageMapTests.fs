module FsLangMcp.Tests.NugetPackageMapTests

// ─── Issue #191: packageId → assembly mapping ───────────────────────────────────
//
// `fcs_nuget_types` / `fcs_nuget_members` used to match a packageId against the assembly
// SimpleName ONLY. `Microsoft.Orleans.Core.Abstractions` ships `Orleans.Core.Abstractions.dll`,
// so the field report in #100 got `matchedTypes: []` for a type that exists — the tool said
// "ok" and returned nothing.
//
// These tests cover the replacement: a packageId → assembly-name map derived from the
// project's own restore output (project.assets.json, or the `-r:` reference paths as a
// fallback), plus the miss payload that turns a zero-match into a one-turn self-correction.
//
// The parser is exercised BOTH against synthetic assets (fast, no fixtures) and against this
// repo's own real `obj/project.assets.json`, so a future change to the NuGet assets format
// cannot pass on a hand-written sketch alone.

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.FcsBridge

// ── Synthetic assets ────────────────────────────────────────────────────────────

/// Replicates the Orleans layout that broke in the field, plus the shapes a real assets file
/// mixes in: a build-only package with no assemblies, `_._` placeholders, a package whose
/// `compile` and `runtime` name different assemblies, and a second RID-qualified target.
let private orleansAssets =
    """
{
  "version": 3,
  "targets": {
    "net10.0": {
      "Microsoft.Orleans.Core.Abstractions/9.0.0": {
        "type": "package",
        "dependencies": { "Microsoft.Extensions.Logging.Abstractions": "9.0.0" },
        "compile": { "lib/net8.0/Orleans.Core.Abstractions.dll": {} },
        "runtime": { "lib/net8.0/Orleans.Core.Abstractions.dll": {} }
      },
      "Newtonsoft.Json/13.0.3": {
        "type": "package",
        "compile": { "lib/net6.0/Newtonsoft.Json.dll": {} },
        "runtime": { "lib/net6.0/Newtonsoft.Json.dll": {} }
      },
      "Microsoft.Orleans.Analyzers/9.0.0": {
        "type": "package",
        "build": { "build/Microsoft.Orleans.Analyzers.props": {} }
      },
      "SomeFramework.Facade/1.2.3": {
        "type": "package",
        "compile": { "ref/net8.0/_._": {} },
        "runtime": { "lib/net8.0/_._": {} },
        "resource": { "lib/net8.0/fr/SomeFramework.Facade.resources.dll": {} }
      },
      "Split.Refs/2.0.0": {
        "type": "package",
        "compile": { "ref/net8.0/Split.Refs.Contract.dll": {} },
        "runtime": { "lib/net8.0/Split.Refs.Impl.dll": {} }
      }
    },
    "net10.0/osx-arm64": {
      "Microsoft.Orleans.Core.Abstractions/9.0.0": {
        "type": "package",
        "compile": { "lib/net8.0/Orleans.Core.Abstractions.dll": {} },
        "runtime": { "lib/net8.0/Orleans.Core.Abstractions.dll": {} }
      }
    }
  },
  "libraries": {},
  "projectFileDependencyGroups": { "net10.0": [] }
}
"""

let private assemblies (map: NugetPackageMap.PackageAssemblies) (packageId: string) =
    map |> Map.tryFind packageId |> Option.defaultValue Set.empty

// ── The pure parser ─────────────────────────────────────────────────────────────

[<Fact>]
let ``packageAssembliesFromAssets maps the Orleans id to the assembly it actually ships`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    Assert.Equal<Set<string>>(
        Set.ofList [ "Orleans.Core.Abstractions" ],
        assemblies map "microsoft.orleans.core.abstractions"
    )

[<Fact>]
let ``packageAssembliesFromAssets keys are lower-cased and assembly names keep their shipped casing`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    // The id half of "<Id>/<Version>" is lower-cased so lookup is case-insensitive…
    Assert.True(map.ContainsKey "newtonsoft.json")
    Assert.False(map.ContainsKey "Newtonsoft.Json")
    // …while the assembly name is preserved as shipped, because the miss payload shows it.
    Assert.Equal<Set<string>>(Set.ofList [ "Newtonsoft.Json" ], assemblies map "newtonsoft.json")

[<Fact>]
let ``packageAssembliesFromAssets unions compile and runtime and folds every target`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    // ref/ vs lib/ can name different assemblies; FCS may reference either.
    Assert.Equal<Set<string>>(Set.ofList [ "Split.Refs.Contract"; "Split.Refs.Impl" ], assemblies map "split.refs")

    // The RID-qualified target repeats Orleans — folding both must not duplicate or drop it.
    Assert.Equal<Set<string>>(
        Set.ofList [ "Orleans.Core.Abstractions" ],
        assemblies map "microsoft.orleans.core.abstractions"
    )

[<Fact>]
let ``packageAssembliesFromAssets records assembly-less packages with an EMPTY set, not as absent`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    // An analyzer/build-only package ships nothing referenceable — but it IS restored, and the
    // miss payload has to be able to say so instead of "nothing similar is in this project's
    // restore graph" (#191 review I2). Present key, empty set.
    Assert.True(map.ContainsKey "microsoft.orleans.analyzers")
    Assert.Equal<Set<string>>(Set.empty, assemblies map "microsoft.orleans.analyzers")

    // Same for a package whose only payloads are the `_._` "contributes nothing for this TFM"
    // marker and a satellite `*.resources.dll` under `resource` — neither is a compile reference.
    Assert.True(map.ContainsKey "someframework.facade")
    Assert.Equal<Set<string>>(Set.empty, assemblies map "someframework.facade")

[<Fact>]
let ``an empty assembly set never matches an assembly`` () =
    // The empty set must stay inert on the MATCH path — recording the id may not turn into
    // claiming it ships something.
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    Assert.False(NugetPackageMap.matches map "Microsoft.Orleans.Analyzers" "Microsoft.Orleans.Analyzers.dll")
    Assert.False(NugetPackageMap.matches map "Microsoft.Orleans.Analyzers" "Orleans.Core.Abstractions")
    // …while the SimpleName arm still works for it, exactly as before.
    Assert.True(NugetPackageMap.matches map "Microsoft.Orleans.Analyzers" "Microsoft.Orleans.Analyzers")

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("not json at all")>]
[<InlineData("{")>]
[<InlineData("null")>]
[<InlineData("[1,2,3]")>]
[<InlineData("{\"targets\": \"not-an-object\"}")>]
[<InlineData("{\"targets\": {\"net10.0\": {\"Broken/1.0.0\": 42}}}")>]
[<InlineData("{\"no\":\"targets\"}")>]
let ``packageAssembliesFromAssets returns an empty map instead of throwing on malformed input`` (payload: string) =
    Assert.Equal<Set<string>>(Set.empty, NugetPackageMap.packageAssembliesFromAssets payload |> Map.keys |> Set.ofSeq)

// ── The `-r:` fallback ──────────────────────────────────────────────────────────

[<Fact>]
let ``packageAssembliesFromReferencePaths reads the id from the global-packages layout`` () =
    let dll =
        "/home/agent/.nuget/packages/microsoft.orleans.core.abstractions/9.0.0/lib/net8.0/Orleans.Core.Abstractions.dll"

    let map =
        NugetPackageMap.packageAssembliesFromReferencePaths [ "--simpleresolution"; $"-r:%s{dll}" ]

    Assert.Equal<Set<string>>(
        Set.ofList [ "Orleans.Core.Abstractions" ],
        assemblies map "microsoft.orleans.core.abstractions"
    )

// NUGET_PACKAGES is process-wide state, so the one test that writes it runs alone — same
// device the repo already uses for LSP process isolation (LspBridgeTests.fs). Today nothing
// else reads the variable concurrently, but a future test that exercises the fallback against
// the real environment would race silently (#191 review M3).
[<CollectionDefinition("FsLangMcp environment variable isolation", DisableParallelization = true)>]
type EnvironmentVariableIsolationCollection() = class end

[<Collection("FsLangMcp environment variable isolation")>]
type NugetPackagesEnvironmentTests() =

    [<Fact>]
    member _.``packageAssembliesFromReferencePaths honours NUGET_PACKAGES and Windows separators``() =
        let previous = Environment.GetEnvironmentVariable "NUGET_PACKAGES"

        try
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", @"D:\ci\pkgs")

            let map =
                NugetPackageMap.packageAssembliesFromReferencePaths
                    [ @"-r:D:\ci\pkgs\microsoft.orleans.core.abstractions\9.0.0\lib\net8.0\Orleans.Core.Abstractions.dll" ]

            Assert.Equal<Set<string>>(
                Set.ofList [ "Orleans.Core.Abstractions" ],
                assemblies map "microsoft.orleans.core.abstractions"
            )
        finally
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous)

[<Fact>]
let ``packageAssembliesFromReferencePaths ignores paths that are not package-cache shaped`` () =
    let map =
        NugetPackageMap.packageAssembliesFromReferencePaths
            [ "-r:/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Runtime.dll"
              "-r:/work/repo/src/Lib/bin/Debug/net10.0/Lib.dll"
              // "packages" segment present, but the next segment is not a version directory —
              // taking "src" as a package id here would be an invented mapping.
              "-r:/work/packages/src/lib/Thing.dll"
              "not-a-reference-option" ]

    Assert.Empty(map)

// ── Matching ────────────────────────────────────────────────────────────────────

[<Fact>]
let ``matches resolves a package id through the map when the assembly is named differently`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    Assert.True(NugetPackageMap.matches map "Microsoft.Orleans.Core.Abstractions" "Orleans.Core.Abstractions")
    // Case-insensitive on both halves.
    Assert.True(NugetPackageMap.matches map "microsoft.ORLEANS.core.abstractions" "orleans.core.ABSTRACTIONS")

    // Guard against a vacuous pass: without the map this is exactly the pre-#191 behaviour,
    // and it must still be false — the map, not looser string matching, is what fixes it.
    Assert.False(NugetPackageMap.matches Map.empty "Microsoft.Orleans.Core.Abstractions" "Orleans.Core.Abstractions")

[<Fact>]
let ``matches keeps the plain SimpleName arm working with no map at all`` () =
    // The happy path (package id = assembly name) must not depend on a readable restore graph.
    Assert.True(NugetPackageMap.matches Map.empty "Newtonsoft.Json" "Newtonsoft.Json")
    Assert.True(NugetPackageMap.matches Map.empty "newtonsoft.json" "Newtonsoft.Json")

[<Fact>]
let ``matches still rejects prefix matching in both directions`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    // "System" must not match every System.* assembly…
    Assert.False(NugetPackageMap.matches map "System" "System.Text.Json")
    // …and a more specific id must not fall back to a shorter assembly.
    Assert.False(NugetPackageMap.matches map "Newtonsoft.Json.Schema" "Newtonsoft.Json")
    // A package's OTHER assembly is not a licence to match an unrelated one.
    Assert.False(NugetPackageMap.matches map "Microsoft.Orleans.Core.Abstractions" "Orleans.Core")

[<Fact>]
let ``matches is false for blank inputs`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets
    Assert.False(NugetPackageMap.matches map "Microsoft.Orleans.Core.Abstractions" "")
    Assert.False(NugetPackageMap.matches map "" "Orleans.Core.Abstractions")

// ── Miss payload ────────────────────────────────────────────────────────────────

let private hintOf (fields: (string * JsonNode) list) =
    fields
    |> List.tryPick (fun (name, value) ->
        if name = "hint" then
            Some(value.GetValue<string>())
        else
            None)
    |> Option.defaultValue ""

/// Reads BOTH keys of every candidate row. Reading only `packageId` would let a dropped or
/// misspelled `assemblies` key pass the whole suite, and the spec requires each candidate to
/// carry its assembly simple names (#191 review M2).
let private candidateRowsOf (fields: (string * JsonNode) list) =
    fields
    |> List.tryPick (fun (name, value) -> if name = "candidatePackages" then Some value else None)
    |> Option.map (fun node ->
        (node :?> JsonArray)
        |> Seq.map (fun entry ->
            let assemblies =
                (entry["assemblies"] :?> JsonArray)
                |> Seq.map (fun name -> name.GetValue<string>())
                |> Seq.toList

            entry["packageId"].GetValue<string>(), assemblies)
        |> Seq.toList)
    |> Option.defaultValue []

let private candidatePackagesOf (fields: (string * JsonNode) list) = candidateRowsOf fields |> List.map fst

[<Fact>]
let ``candidates suggests the package whose ASSEMBLY name the caller passed`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    // The exact field failure inverted: the agent knows `Orleans.Core.Abstractions` and needs
    // to be told the package id is `Microsoft.Orleans.Core.Abstractions`.
    let found = NugetPackageMap.candidates map "Orleans.Core.Abstractions" 5

    Assert.Equal<string list>([ "microsoft.orleans.core.abstractions" ], found |> List.map fst)

[<Fact>]
let ``candidates matches containment in both directions and respects the cap`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    // Caller passed a prefix of the id.
    Assert.Contains("microsoft.orleans.core.abstractions", NugetPackageMap.candidates map "Orleans" 5 |> List.map fst)
    // Caller passed something longer that CONTAINS a known id.
    Assert.Contains("newtonsoft.json", NugetPackageMap.candidates map "Newtonsoft.Json.Schema" 5 |> List.map fst)
    // Cap is honoured.
    Assert.True((NugetPackageMap.candidates map "o" 2).Length <= 2)

[<Fact>]
let ``candidates returns nothing for an unrelated query`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets
    Assert.Empty(NugetPackageMap.candidates map "Zzz.Completely.Unrelated" 5)
    Assert.Empty(NugetPackageMap.candidates map "" 5)

[<Fact>]
let ``missFields names the id-versus-assembly distinction and lists candidates`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets
    let fields = NugetPackageMap.missFields map "Orleans.Core.Abstractions.Typo"

    let hint = hintOf fields
    Assert.Contains("package id", hint)
    Assert.Contains("SimpleName", hint)
    Assert.Contains("candidatePackages", hint)

    // Both keys of the row, not just the id: the assembly names are what let an agent see that
    // the id it guessed and the assembly it knows are two different strings.
    Assert.Equal<(string * string list) list>(
        [ "microsoft.orleans.core.abstractions", [ "Orleans.Core.Abstractions" ] ],
        candidateRowsOf fields
    )

[<Fact>]
let ``missFields on a total miss still explains the id-versus-assembly distinction`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets
    let fields = NugetPackageMap.missFields map "Zzz.Completely.Unrelated"

    let hint = hintOf fields
    Assert.Contains("package id", hint)
    Assert.Contains("SimpleName", hint)
    Assert.Contains("Orleans.Core.Abstractions.dll", hint)
    Assert.Empty(candidatePackagesOf fields)

[<Fact>]
let ``missFields distinguishes restored-but-not-on-the-compile-line from unknown`` () =
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets

    // The package IS in the restore graph — the caller's id is right, the assembly just is not
    // referenced (analyzer / build-only / runtime-only). Saying "no such package" would be wrong.
    let hint = hintOf (NugetPackageMap.missFields map "Split.Refs")
    Assert.Contains("restore graph", hint)
    Assert.Contains("compile line", hint)

[<Fact>]
let ``missFields tells an analyzer-package caller the package IS restored`` () =
    // Regression for #191 review I2. Microsoft.Orleans.Analyzers ships no compile/runtime
    // assembly at all, so it used to be absent from the map, and asking for it produced the
    // flatly false "nothing similar is in this project's restore graph".
    let map = NugetPackageMap.packageAssembliesFromAssets orleansAssets
    let fields = NugetPackageMap.missFields map "Microsoft.Orleans.Analyzers"

    let hint = hintOf fields
    Assert.Contains("restore graph", hint)
    Assert.Contains("compile line", hint)
    Assert.DoesNotContain("nothing similar", hint)

    // …and it is reported as shipping nothing, rather than omitted from the candidate list.
    Assert.Contains(("microsoft.orleans.analyzers", []), candidateRowsOf fields)

[<Fact>]
let ``missFields always emits both keys so the response shape is stable`` () =
    let names = NugetPackageMap.missFields Map.empty "Anything" |> List.map fst

    Assert.Equal<string list>([ "hint"; "candidatePackages" ], names)

// ── The real thing ──────────────────────────────────────────────────────────────

let private repoRoot () =
    let rec loop (dir: DirectoryInfo) =
        if isNull dir then
            failwith "Could not locate the FsLangMcp repo root from AppContext.BaseDirectory."
        elif File.Exists(Path.Combine(dir.FullName, "FsLangMcp.fsproj")) then
            dir.FullName
        else
            loop dir.Parent

    loop (DirectoryInfo AppContext.BaseDirectory)

/// This repo's own restore output. Present whenever the test project built at all, so this is
/// a real-format check with no fixture cost — the synthetic assets above are a sketch, and a
/// sketch cannot prove the parser's assumptions about the real NuGet schema.
let private realAssetsMap () =
    let path = Path.Combine(repoRoot (), "obj", "project.assets.json")
    Assert.True(File.Exists path, $"Expected a restored assets file at {path}.")
    NugetPackageMap.packageAssembliesFromAssets (File.ReadAllText path)

[<Fact>]
let ``the parser handles this repo's REAL project assets file`` () =
    let map = realAssetsMap ()

    // A sanity floor: this project references dozens of packages, so a parser that silently
    // produced nothing (wrong section names, wrong nesting) would fail here.
    Assert.True(map.Count > 20, $"Expected the real assets graph to yield >20 packages, got {map.Count}.")
    Assert.Equal<Set<string>>(Set.ofList [ "FSharp.Core" ], assemblies map "fsharp.core")

    // Multi-assembly package: one id, two shipped assemblies.
    Assert.Contains("FSharp.DependencyManager.Nuget", assemblies map "fsharp.compiler.service")

    // Real analyzer packages this project references with IncludeAssets=analyzers: restored,
    // present in the map, shipping nothing referenceable (#191 review I2). Asserted on real
    // data because the synthetic fixture cannot prove NuGet actually emits them this way.
    for analyzerPackage in [ "fsharp.analyzers.build"; "g-research.fsharp.analyzers"; "ionide.analyzers" ] do
        Assert.True(map.ContainsKey analyzerPackage, $"{analyzerPackage} must be recorded as restored.")
        Assert.Equal<Set<string>>(Set.empty, assemblies map analyzerPackage)

    let hint = hintOf (NugetPackageMap.missFields map "Ionide.Analyzers")

    Assert.Contains("restore graph", hint)
    Assert.DoesNotContain("nothing similar", hint)

[<Fact>]
let ``this repo's own restore contains a package whose assembly name differs from its id`` () =
    let map = realAssetsMap ()

    // The invariant, re-derived from the real graph rather than asserted as a fact: the bug
    // class #191 fixes is present in this very project, so the fixture below is not synthetic
    // wishful thinking. If this ever fails, the packages changed — pick a new example.
    let mismatched =
        map
        |> Map.toList
        |> List.filter (fun (packageId, names) ->
            // `Set.forall` is vacuously true on the empty set, and since #191 review I2 the map
            // deliberately carries assembly-less packages — exclude them or every analyzer
            // package would masquerade as a name mismatch and hollow out this invariant.
            not (Set.isEmpty names)
            && names
               |> Set.forall (fun name -> not (String.Equals(name, packageId, StringComparison.OrdinalIgnoreCase))))
        |> List.map fst

    Assert.NotEmpty(mismatched)

    // The concrete one, pinned in FsLangMcp.fsproj (see the issue #121 comment there):
    // package `Microsoft.VisualStudio.Threading.Only` ships `Microsoft.VisualStudio.Threading.dll`.
    // Before #191 this package was invisible to fcs_nuget_types / fcs_nuget_members.
    Assert.Equal<Set<string>>(
        Set.ofList [ "Microsoft.VisualStudio.Threading" ],
        assemblies map "microsoft.visualstudio.threading.only"
    )

    Assert.True(
        NugetPackageMap.matches map "Microsoft.VisualStudio.Threading.Only" "Microsoft.VisualStudio.Threading",
        "The real map must resolve the package id to the assembly it ships."
    )

[<Fact>]
let ``the assets parser and the -r: fallback agree on this repo's real reference set`` () : Task =
    task {
        // Two independent derivations of the same ground truth: one reads the restore graph,
        // the other reads the compile line MSBuild produced from it. Asserting they agree
        // catches drift in either.
        //
        // It is also the only place the fallback meets a REAL package-cache root. ci.yml and
        // publish.yml run the suite on ubuntu only, so `live-fsac.yml` runs this family — by
        // name — on its Windows leg specifically to exercise
        // `%USERPROFILE%\.nuget\packages` and backslash-separated `-r:` options for real. If
        // that step is ever removed, Windows path handling falls back to being pinned only by
        // the synthetic string test above (#191 review I1).
        let root = repoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()
        let! snapshot = bridge.GetEvaluatedProjectSnapshot projectPath

        let otherOptions =
            match snapshot with
            | Ok evaluated -> evaluated.OtherOptions
            | Error reason -> failwith $"Could not evaluate {projectPath}: {reason}"

        let fromReferencePaths =
            NugetPackageMap.packageAssembliesFromReferencePaths otherOptions

        let fromAssets = realAssetsMap ()

        Assert.True(
            fromReferencePaths.Count > 20,
            $"The -r: derivation found only {fromReferencePaths.Count} packages in {otherOptions.Length} compiler options; it is not reading the global-packages layout."
        )

        // Direction checked: everything the compile line yields must be in the restore graph and
        // agree with it. The reverse does NOT hold by construction — the assets map is a superset
        // by the packages that ship no referenceable assembly at all (#191 review I2), which on
        // this repo is exactly the three analyzer packages (55 from -r:, 58 from assets).
        let disagreements =
            fromReferencePaths
            |> Map.toList
            |> List.choose (fun (packageId, names) ->
                match Map.tryFind packageId fromAssets with
                | Some assetNames when Set.isSubset names assetNames -> None
                | Some assetNames ->
                    Some $"{packageId}: -r: says {Set.toList names}, assets says {Set.toList assetNames}"
                | None -> Some $"{packageId}: derived from -r: but absent from the assets graph")

        Assert.True(
            List.isEmpty disagreements,
            $"{List.length disagreements} of {fromReferencePaths.Count} -r:-derived packages disagree with the {fromAssets.Count}-package assets graph: {disagreements}"
        )

        // The fallback must fix #191 on its own, not only alongside the assets file.
        Assert.True(
            NugetPackageMap.matches
                fromReferencePaths
                "Microsoft.VisualStudio.Threading.Only"
                "Microsoft.VisualStudio.Threading"
        )
    }

[<Fact>]
let ``forProject reads the assets file next to a real fsproj and falls back when it is missing`` () =
    let root = repoRoot ()

    let fromAssets =
        NugetPackageMap.forProject (Path.Combine(root, "FsLangMcp.fsproj")) []

    Assert.True(
        NugetPackageMap.matches fromAssets "Microsoft.VisualStudio.Threading.Only" "Microsoft.VisualStudio.Threading"
    )

    // No assets file at that location → the `-r:` derivation carries the map instead.
    let missing =
        Path.Combine(Path.GetTempPath(), $"fslangmcp_no_assets_{Guid.NewGuid():N}", "Ghost.fsproj")

    let fallback =
        NugetPackageMap.forProject
            missing
            [ "-r:/home/agent/.nuget/packages/microsoft.orleans.core.abstractions/9.0.0/lib/net8.0/Orleans.Core.Abstractions.dll" ]

    Assert.True(NugetPackageMap.matches fallback "Microsoft.Orleans.Core.Abstractions" "Orleans.Core.Abstractions")
