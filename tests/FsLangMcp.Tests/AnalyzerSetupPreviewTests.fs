module FsLangMcp.Tests.AnalyzerSetupPreviewTests

// ─── #75: fcs_analyzer_setup_preview — "what do I add to turn on analyzers?" ───────
//
// PREVIEW ONLY: FcsBridge.AnalyzerSetupPreview reads the target .fsproj + the nearest
// Directory.Build.props/.targets + dotnet-tools.json (textual XML/JSON — no FCS, no
// restore), diffs the present analyzer wiring against the required set, and emits the
// exact snippet to add for each gap. It applies NOTHING.
//
// Because the tool only reads project files, the fixtures here are plain temp .fsproj
// files written to a throwaway directory — no `dotnet restore`, no build, no FCS.

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── temp-project helpers ───────────────────────────────────────────────────────────

/// Write the given (relativePath, content) files under a fresh temp root and return
/// (rootDir, firstFsprojPath). The caller is responsible for cleanup.
let private writeTempProject (files: (string * string) list) : string * string =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_analyzersetup_{runId}")
    Directory.CreateDirectory root |> ignore
    let mutable fsproj = ""

    for (rel, content) in files do
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)

        if fsproj = "" && rel.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
            fsproj <- full

    root, fsproj

let private cleanup (root: string) =
    if Directory.Exists root then
        try
            Directory.Delete(root, true)
        with _ ->
            ()

// ── fixture .fsproj content ──────────────────────────────────────────────────────────

/// A bare F# project with NO analyzer wiring of any kind.
let private bareFsproj =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup>"
          "    <TargetFramework>net10.0</TargetFramework>"
          "  </PropertyGroup>"
          "  <ItemGroup>"
          "    <Compile Include=\"Library.fs\" />"
          "  </ItemGroup>"
          "</Project>" ]

/// A project that is already FULLY wired for the two default analyzer packages: package
/// refs with GeneratePathProperty, FSharp.Analyzers.Build, and the FSharpAnalyzersOtherFlags
/// property referencing both Pkg path properties. The ONLY missing piece is the local
/// fsharp-analyzers tool manifest (no dotnet-tools.json in the temp tree).
let private wiredFsproj =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup>"
          "    <TargetFramework>net10.0</TargetFramework>"
          "    <FSharpAnalyzersOtherFlags>--analyzers-path \"$(PkgG-Research_FSharp_Analyzers)/analyzers/dotnet/fs\" --analyzers-path \"$(PkgIonide_Analyzers)/analyzers/dotnet/fs\"</FSharpAnalyzersOtherFlags>"
          "  </PropertyGroup>"
          "  <ItemGroup>"
          "    <Compile Include=\"Library.fs\" />"
          "  </ItemGroup>"
          "  <ItemGroup>"
          "    <PackageReference Include=\"G-Research.FSharp.Analyzers\" Version=\"0.22.*\" GeneratePathProperty=\"true\">"
          "      <IncludeAssets>analyzers</IncludeAssets>"
          "      <PrivateAssets>all</PrivateAssets>"
          "    </PackageReference>"
          "    <PackageReference Include=\"Ionide.Analyzers\" Version=\"0.15.*\" GeneratePathProperty=\"true\">"
          "      <IncludeAssets>analyzers</IncludeAssets>"
          "      <PrivateAssets>all</PrivateAssets>"
          "    </PackageReference>"
          "    <PackageReference Include=\"FSharp.Analyzers.Build\" Version=\"0.5.*\">"
          "      <IncludeAssets>build</IncludeAssets>"
          "      <PrivateAssets>all</PrivateAssets>"
          "    </PackageReference>"
          "  </ItemGroup>"
          "</Project>" ]

// ── JSON readers ─────────────────────────────────────────────────────────────────────

let private gs (n: JsonNode) (k: string) = n[k].GetValue<string>()
let private gb (n: JsonNode) (k: string) = n[k].GetValue<bool>()
let private plannedChanges (result: JsonNode) : JsonArray = result["plannedChanges"] :?> JsonArray

let private changeKinds (result: JsonNode) : string list =
    [ for c in plannedChanges result -> c["kind"].GetValue<string>() ]

let private anyPreviewContains (result: JsonNode) (needle: string) : bool =
    plannedChanges result
    |> Seq.exists (fun c -> (c["preview"].GetValue<string>()).Contains(needle, StringComparison.Ordinal))

let private args (projectPath: string option) (packages: string list option) : FcsAnalyzerSetupPreviewArgs =
    { projectPath = projectPath
      analyzerPackages = packages }

// ─────────────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``without analyzers — plan covers package refs, build glue, flags, and manifest`` () : Task =
    task {
        let root, fsproj = writeTempProject [ "Library.fs", "module Library"; "App.fsproj", bareFsproj ]

        try
            let bridge = FcsBridge()
            let! result = bridge.AnalyzerSetupPreview(args (Some fsproj) None)

            Assert.Equal("succeeded", gs result "status")
            Assert.False(gb result "alreadyConfigured", "a bare project has no analyzers")

            // Every required piece is present as a planned change, each with a preview snippet.
            Assert.True(anyPreviewContains result "G-Research.FSharp.Analyzers", "default G-Research package ref")
            Assert.True(anyPreviewContains result "Ionide.Analyzers", "default Ionide package ref")
            Assert.True(anyPreviewContains result "FSharp.Analyzers.Build", "the build-glue package ref")
            Assert.True(anyPreviewContains result "FSharpAnalyzersOtherFlags", "the analyzer-flags property")
            Assert.True(anyPreviewContains result "fsharp-analyzers", "the local tool manifest entry")

            // The flags snippet uses the correct GeneratePathProperty mapping ('.' → '_').
            Assert.True(anyPreviewContains result "PkgG-Research_FSharp_Analyzers", "Pkg path-property name for G-Research")
            Assert.True(anyPreviewContains result "PkgIonide_Analyzers", "Pkg path-property name for Ionide")

            let kinds = changeKinds result
            Assert.Contains("add_package_ref", kinds)
            Assert.Contains("add_property", kinds)
            Assert.Contains("add_tool_manifest", kinds)

            // No planned change may carry an empty preview or reason.
            for c in plannedChanges result do
                Assert.False(String.IsNullOrWhiteSpace(c["preview"].GetValue<string>()), "preview must be non-empty")
                Assert.False(String.IsNullOrWhiteSpace(c["reason"].GetValue<string>()), "reason must be non-empty")
                Assert.False(String.IsNullOrWhiteSpace(c["file"].GetValue<string>()), "file must be non-empty")
        finally
            cleanup root
    }

[<Fact>]
let ``with analyzers — alreadyConfigured true and only the manifest gap remains`` () : Task =
    task {
        let root, fsproj = writeTempProject [ "Library.fs", "module Library"; "App.fsproj", wiredFsproj ]

        try
            let bridge = FcsBridge()
            let! result = bridge.AnalyzerSetupPreview(args (Some fsproj) None)

            Assert.Equal("succeeded", gs result "status")
            Assert.True(gb result "alreadyConfigured", "a fully-wired project reports alreadyConfigured")

            let kinds = changeKinds result

            // The package refs, GeneratePathProperty and flags are all present, so NONE of
            // those gap kinds may appear — the project is already configured for them.
            Assert.DoesNotContain("add_package_ref", kinds)
            Assert.DoesNotContain("enable_generate_path_property", kinds)
            Assert.DoesNotContain("add_property", kinds)
            Assert.DoesNotContain("update_property", kinds)

            // The only remaining gap is the local fsharp-analyzers tool manifest.
            Assert.Contains("add_tool_manifest", kinds)
            Assert.True(anyPreviewContains result "fsharp-analyzers", "the manifest gap previews the tool entry")
        finally
            cleanup root
    }

[<Fact>]
let ``custom analyzerPackages drives both the package ref and the flags mapping`` () : Task =
    task {
        let root, fsproj = writeTempProject [ "Library.fs", "module Library"; "App.fsproj", bareFsproj ]

        try
            let bridge = FcsBridge()
            let! result = bridge.AnalyzerSetupPreview(args (Some fsproj) (Some [ "My.Custom.Analyzer" ]))

            Assert.Equal("succeeded", gs result "status")
            // The custom package is planned as an analyzer ref...
            Assert.True(anyPreviewContains result "My.Custom.Analyzer", "custom package becomes the analyzer ref")
            // ...and its Pkg path-property name follows the '.' → '_' mapping in the flags.
            Assert.True(anyPreviewContains result "PkgMy_Custom_Analyzer", "custom Pkg path-property name")
            // The default packages are NOT planned when a custom list is supplied.
            Assert.False(anyPreviewContains result "Ionide.Analyzers", "defaults are not used when a custom list is given")
        finally
            cleanup root
    }

[<Fact>]
let ``no project context returns invalid_args`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.AnalyzerSetupPreview(args None None)

        Assert.Equal("invalid_args", gs result "status")
    }
