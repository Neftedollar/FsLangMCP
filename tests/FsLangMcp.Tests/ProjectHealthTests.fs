module FsLangMcp.Tests.ProjectHealthTests

open System.IO
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.ProjectHealth
open FsLangMcp.ProjectInspection
open FsLangMcp.Types

let private writeProject root =
    Directory.CreateDirectory(root) |> ignore

    let sourcePath = Path.Combine(root, "Library.fs")
    File.WriteAllText(sourcePath, "module Library\n\nlet value = 1\n")

    let projectPath = Path.Combine(root, "Library.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            "\n"
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup>"
              "    <TargetFramework>net10.0</TargetFramework>"
              "  </PropertyGroup>"
              "  <ItemGroup>"
              "    <Compile Include=\"Library.fs\" />"
              "  </ItemGroup>"
              "  <ItemGroup>"
              "    <PackageReference Include=\"Ionide.Analyzers\" Version=\"0.15.0\">"
              "      <IncludeAssets>analyzers</IncludeAssets>"
              "      <PrivateAssets>all</PrivateAssets>"
              "    </PackageReference>"
              "  </ItemGroup>"
              "</Project>" ]
    )

    projectPath

let private healthArgs projectPath workspacePath =
    { projectPath = Some projectPath
      workspacePath = workspacePath
      scope = None
      compileCheck = None }

let private readySnapshot projectPath root =
    { ProjectPath = Some projectPath
      WorkspaceRoot = Some root
      WorkspaceReady = true
      DiagnosticsFileCount = 0 }

let private report args snapshot =
    let probe _ =
        async { return Ok { Source = "test-probe"; ReferencesExisting = 0; ReferencesTotal = 0 } }

    createReport args snapshot probe |> Async.RunSynchronously

/// Like `report` but with an injectable probe so tests can simulate an unrestored
/// project (references declared but absent on disk).
let private reportWithProbe (probe: ProjectOptionsProbe) args snapshot =
    createReport args snapshot probe |> Async.RunSynchronously

[<Fact>]
let ``project_health reports source files and analyzer setup`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_%s{runId}")

    try
        let projectPath = writeProject root

        let result =
            report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal("ok", (result["status"]).GetValue<string>())
        Assert.Equal("ready", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Equal(1, (result["files"]["sourceFileCount"]).GetValue<int>())
        Assert.Equal("analyzers_configured", (result["analyzers"]["status"]).GetValue<string>())
        Assert.Equal("available", (result["projectOptions"]["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health blocks missing explicit fsproj`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let missingProject = Path.Combine(Path.GetTempPath(), $"missing_%s{runId}.fsproj")

    let args = healthArgs missingProject None
    let snapshot = readySnapshot missingProject (Path.GetTempPath())
    let result = report args snapshot

    Assert.Equal("ok", (result["status"]).GetValue<string>())
    Assert.Equal("blocked", ((result["toolingReadiness"])["overall"]).GetValue<string>())
    Assert.Equal("blocked", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
    Assert.Equal("blocked", (((result["toolingReadiness"])["lsp"])["status"]).GetValue<string>())

[<Fact>]
let ``project_health blocks directory with multiple fsproj files`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_multi_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let result = report (healthArgs root (Some root)) (readySnapshot root root)

        Assert.Equal("blocked", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal("blocked", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Contains("Multiple .fsproj", ((((result["toolingReadiness"])["fcs"])["reason"]).GetValue<string>()))
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health blocks missing compile file`` () =
    let runId = System.Guid.NewGuid().ToString("N")

    let root =
        Path.Combine(Path.GetTempPath(), $"fslangmcp_health_missing_file_%s{runId}")

    try
        let projectPath = writeProject root
        File.Delete(Path.Combine(root, "Library.fs"))

        let result =
            report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal("blocked", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Equal(1, (result["files"]["missingFiles"]).AsArray().Count)
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health reports no analyzers as capability fact`` () =
    let runId = System.Guid.NewGuid().ToString("N")

    let root =
        Path.Combine(Path.GetTempPath(), $"fslangmcp_health_no_analyzers_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "Library.fs"), "module Library\n")

        let projectPath = Path.Combine(root, "Library.fsproj")

        File.WriteAllText(
            projectPath,
            String.concat
                "\n"
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                  "  <ItemGroup><Compile Include=\"Library.fs\" /></ItemGroup>"
                  "</Project>" ]
        )

        let result =
            report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal("ready", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Equal("no_analyzers_configured", (result["analyzers"]["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health reports fcs_only overall when lsp workspace is not ready but fcs is fine`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_lsp_%s{runId}")

    try
        let projectPath = writeProject root

        let snapshot =
            { ProjectPath = Some projectPath
              WorkspaceRoot = Some root
              WorkspaceReady = false
              DiagnosticsFileCount = 0 }

        let result = report (healthArgs projectPath (Some root)) snapshot

        // FCS axis is healthy; LSP not ready — overall should be fcs_only (not degraded)
        Assert.Equal("fcs_only", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Equal("not_ready", (((result["toolingReadiness"])["lsp"])["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health reports ready overall when both fcs and lsp are ready`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_both_ready_%s{runId}")

    try
        let projectPath = writeProject root

        let result =
            report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal("ready", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["lsp"])["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``fsharp_project_inspect reports compile order and package references`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_inspect_%s{runId}")

    try
        let projectPath = writeProject root

        let result =
            inspectProject
                { projectPath = Some projectPath
                  workspacePath = Some root
                  scope = None
                  includeGeneratedFiles = None
                  includePackageDetails = Some true
                  includeResolvedOptions = Some false }

        Assert.Equal("ok", result["status"].GetValue<string>())
        Assert.Equal(1, (result["compileOrder"] :?> JsonArray).Count)
        let firstCompileFile = (result["compileOrder"] :?> JsonArray)[0]
        Assert.Equal("implementation", firstCompileFile["kind"].GetValue<string>())
        Assert.Equal(1, (result["filterSummary"]["includedFiles"]).GetValue<int>())
        Assert.True((result["references"]["packageReferences"] :?> JsonArray).Count >= 1)
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

// ─── .sln / .slnx as projectPath ─────────────────────────────────────────────

[<Fact>]
let ``project_health accepts .slnx path and resolves to the single fsproj inside`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_slnx_%s{runId}")

    try
        let projectPath = writeProject root
        let slnxPath = Path.Combine(root, "Solution.slnx")

        File.WriteAllText(
            slnxPath,
            String.concat
                "\n"
                [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  "<Solution>"
                  "  <Project Path=\"Library.fsproj\" />"
                  "</Solution>" ])

        let result = report (healthArgs slnxPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal("ok", (result["status"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health accepts .sln path and resolves to the single fsproj inside`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_sln_%s{runId}")

    try
        let projectPath = writeProject root
        let slnPath = Path.Combine(root, "Solution.sln")

        File.WriteAllText(
            slnPath,
            String.concat
                "\n"
                [ "Microsoft Visual Studio Solution File, Format Version 12.00"
                  "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"Library\", \"Library.fsproj\", \"{00000000-0000-0000-0000-000000000001}\""
                  "EndProject" ])

        let result = report (healthArgs slnPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal("ok", (result["status"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health blocks .slnx with multiple fsproj files`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_slnx_multi_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let slnxPath = Path.Combine(root, "Solution.slnx")

        File.WriteAllText(
            slnxPath,
            String.concat
                "\n"
                [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  "<Solution>"
                  "  <Project Path=\"A.fsproj\" />"
                  "  <Project Path=\"B.fsproj\" />"
                  "</Solution>" ])

        let snapshot =
            { ProjectPath = None; WorkspaceRoot = Some root; WorkspaceReady = true; DiagnosticsFileCount = 0 }

        let result = report (healthArgs slnxPath None) snapshot

        Assert.Equal("blocked", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Contains("Multiple .fsproj", (((result["toolingReadiness"])["fcs"])["reason"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health blocks .sln with multiple fsproj files`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_sln_multi_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let slnPath = Path.Combine(root, "Solution.sln")

        File.WriteAllText(
            slnPath,
            String.concat
                "\n"
                [ "Microsoft Visual Studio Solution File, Format Version 12.00"
                  "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"A\", \"A.fsproj\", \"{00000000-0000-0000-0000-000000000001}\""
                  "EndProject"
                  "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"B\", \"B.fsproj\", \"{00000000-0000-0000-0000-000000000002}\""
                  "EndProject" ])

        let snapshot =
            { ProjectPath = None; WorkspaceRoot = Some root; WorkspaceReady = true; DiagnosticsFileCount = 0 }

        let result = report (healthArgs slnPath None) snapshot

        Assert.Equal("blocked", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Contains("Multiple .fsproj", (((result["toolingReadiness"])["fcs"])["reason"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``workspacePath pointing to a slnx file is normalized to its parent directory`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_slnx_ws_%s{runId}")

    try
        let projectPath = writeProject root
        let slnxPath = Path.Combine(root, "Solution.slnx")

        File.WriteAllText(
            slnxPath,
            String.concat
                "\n"
                [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  "<Solution>"
                  "  <Project Path=\"Library.fsproj\" />"
                  "</Solution>" ])

        let result =
            report (healthArgs projectPath (Some slnxPath)) (readySnapshot projectPath root)

        let workspaceRoot = (result["workspace"]["workspaceRoot"]).GetValue<string>()
        Assert.Equal(root, workspaceRoot)
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

// ─── Regression tests for VERIFY findings on #77 (closes #81) ────────────────

[<Fact>]
let ``project_health emits nested fcs/lsp/overall shape when project path does not exist`` () =
    // Finding 2: early-exit paths must emit the same nested toolingReadiness shape as the
    // success path, not the old flat {status, blockers, recovery} shape.
    let runId = System.Guid.NewGuid().ToString("N")
    let missingProject = Path.Combine(Path.GetTempPath(), $"missing_%s{runId}.fsproj")

    let args = healthArgs missingProject None
    let snapshot = readySnapshot missingProject (Path.GetTempPath())
    let result = report args snapshot

    Assert.Equal("ok", (result["status"]).GetValue<string>())
    Assert.Equal("blocked", ((result["toolingReadiness"])["overall"]).GetValue<string>())
    Assert.Equal("blocked", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
    Assert.Equal("blocked", (((result["toolingReadiness"])["lsp"])["status"]).GetValue<string>())
    // The flat shape keys must NOT be present at the toolingReadiness root
    Assert.Null(result["toolingReadiness"]["blockers"])
    Assert.Null(result["toolingReadiness"]["status"])

[<Fact>]
let ``overallStatus is blocked when fcs is blocked and lsp is ready`` () =
    // Finding 3: "blocked" FCS axis must propagate to "blocked" overall, not "degraded".
    let runId = System.Guid.NewGuid().ToString("N")

    let root =
        Path.Combine(Path.GetTempPath(), $"fslangmcp_health_fcs_blocked_%s{runId}")

    try
        // Create a project with a missing compile file so fcs status = "blocked".
        let projectPath = writeProject root
        File.Delete(Path.Combine(root, "Library.fs"))

        let result =
            report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal("blocked", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Equal("ready",   (((result["toolingReadiness"])["lsp"])["status"]).GetValue<string>())
        Assert.Equal("blocked", ((result["toolingReadiness"])["overall"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``overallStatus is fcs_only when fcs is ready and lsp is not_ready`` () =
    // Finding 3: fcs=ready + lsp=not_ready must yield "fcs_only" (not "degraded" or "ready").
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_fcs_only_%s{runId}")

    try
        let projectPath = writeProject root

        let snapshot =
            { ProjectPath = Some projectPath
              WorkspaceRoot = Some root
              WorkspaceReady = false
              DiagnosticsFileCount = 0 }

        let result = report (healthArgs projectPath (Some root)) snapshot

        Assert.Equal("ready",     (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Equal("not_ready", (((result["toolingReadiness"])["lsp"])["status"]).GetValue<string>())
        Assert.Equal("fcs_only",  ((result["toolingReadiness"])["overall"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``overallStatus is ready when both fcs and lsp are ready`` () =
    // Finding 3: "ready" + "ready" must yield "ready".
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_both_%s{runId}")

    try
        let projectPath = writeProject root
        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["lsp"])["status"]).GetValue<string>())
        Assert.Equal("ready", ((result["toolingReadiness"])["overall"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

// ─── projectPath optional after set_project (#105) ───────────────────────────

[<Fact>]
let ``project_health blocks with clear message when projectPath is None and no fallback`` () =
    let args: ProjectHealthArgs =
        { projectPath = None
          workspacePath = None
          scope = None
          compileCheck = None }

    // Bridge fallback is applied in Program.fs; createReport sees whatever the
    // handler passes through. None here simulates "no set_project, no explicit
    // projectPath" — the failure mode that #105 is designed to produce a clear
    // error for.
    let snapshot =
        { ProjectPath = None
          WorkspaceRoot = None
          WorkspaceReady = false
          DiagnosticsFileCount = 0 }

    let result = report args snapshot

    Assert.Equal("blocked", ((result["toolingReadiness"])["overall"]).GetValue<string>())
    let reason = (((result["toolingReadiness"])["fcs"])["reason"]).GetValue<string>()
    Assert.Contains("projectPath is required", reason)
    Assert.Contains("set_project", reason)

[<Fact>]
let ``project_health succeeds when projectPath is Some valid fsproj`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_opt_%s{runId}")

    try
        let projectPath = writeProject root
        // Caller in Program.fs would have applied bridge fallback; here we just
        // verify that the explicit form still works.
        let args =
            { projectPath = Some projectPath
              workspacePath = Some root
              scope = None
              compileCheck = None }

        let result = report args (readySnapshot projectPath root)

        Assert.Equal("ok", (result["status"]).GetValue<string>())
        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

// ─── Test-discovery + build metadata fields (#117) ───────────────────────────

/// Write a non-test project (no test-framework PackageReference) to a temp dir.
let private writeNonTestProject (root: string) =
    Directory.CreateDirectory(root) |> ignore
    File.WriteAllText(Path.Combine(root, "Library.fs"), "module Library\n\nlet value = 42\n")
    let projectPath = Path.Combine(root, "Library.fsproj")
    File.WriteAllText(
        projectPath,
        String.concat "\n"
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
              "  <ItemGroup><Compile Include=\"Library.fs\" /></ItemGroup>"
              "</Project>" ])
    projectPath

/// Write an xUnit test project with one [<Fact>] and one [<Theory>] test to a temp dir.
let private writeXunitTestProject (root: string) (sourceContent: string) =
    Directory.CreateDirectory(root) |> ignore
    File.WriteAllText(Path.Combine(root, "Tests.fs"), sourceContent)
    let projectPath = Path.Combine(root, "Tests.fsproj")
    File.WriteAllText(
        projectPath,
        String.concat "\n"
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
              "  <ItemGroup><Compile Include=\"Tests.fs\" /></ItemGroup>"
              "  <ItemGroup>"
              "    <PackageReference Include=\"xunit\" Version=\"2.9.0\" />"
              "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.12.0\" />"
              "  </ItemGroup>"
              "</Project>" ])
    projectPath

[<Fact>]
let ``project_health non-test project reports isTestProject false and testCount null`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_nontestproj_%s{runId}")
    try
        let projectPath = writeNonTestProject root
        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let proj = result["project"]

        Assert.False(proj["isTestProject"].GetValue<bool>())
        // testFrameworks should be an empty array
        let frameworks = proj["testFrameworks"] :?> JsonArray
        Assert.Equal(0, frameworks.Count)
        // testCount must be null for a non-test project
        Assert.Null(proj["testCount"])
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health xunit test project with one Fact reports isTestProject true testCount 1`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_xunit1_%s{runId}")
    let source =
        String.concat "\n"
            [ "module Tests"
              "open Xunit"
              "[<Fact>]"
              "let testA () = Assert.True(true)" ]
    try
        let projectPath = writeXunitTestProject root source
        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let proj = result["project"]

        Assert.True(proj["isTestProject"].GetValue<bool>())
        Assert.Equal(1, proj["testCount"].GetValue<int>())
        let frameworks = proj["testFrameworks"] :?> JsonArray
        Assert.Equal(1, frameworks.Count)
        Assert.Equal("xunit", frameworks[0].GetValue<string>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health xunit project with Theory increments testCount`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_xunittheory_%s{runId}")
    let source =
        String.concat "\n"
            [ "module Tests"
              "open Xunit"
              "[<Fact>]"
              "let testA () = Assert.True(true)"
              "[<Theory>]"
              "[<InlineData(1)>]"
              "let testB (x: int) = Assert.True(x > 0)" ]
    try
        let projectPath = writeXunitTestProject root source
        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let proj = result["project"]

        Assert.True(proj["isTestProject"].GetValue<bool>())
        // One [<Fact>] + one [<Theory>] = 2
        Assert.Equal(2, proj["testCount"].GetValue<int>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health reports binaryOutputPath null when bin dir is empty`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_nobin_%s{runId}")
    try
        let projectPath = writeNonTestProject root
        // Ensure no bin/ directory exists under the project
        let binDir = Path.Combine(root, "bin")
        if Directory.Exists binDir then Directory.Delete(binDir, true)

        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let proj = result["project"]

        Assert.Null(proj["binaryOutputPath"])
        Assert.Null(proj["lastBuildSucceeded"])
        Assert.Null(proj["lastBuildAt"])
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health reports binaryOutputPath when matching dll exists under bin`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_withbin_%s{runId}")
    try
        let projectPath = writeNonTestProject root
        // Manually place a .dll under bin/Release/net10.0 to simulate a build artifact
        let artifactDir = Path.Combine(root, "bin", "Release", "net10.0")
        Directory.CreateDirectory(artifactDir) |> ignore
        let dllPath = Path.Combine(artifactDir, "Library.dll")
        File.WriteAllBytes(dllPath, [||])

        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let proj = result["project"]

        Assert.True(proj["lastBuildSucceeded"].GetValue<bool>())
        Assert.NotNull(proj["lastBuildAt"])
        Assert.Equal(dllPath, proj["binaryOutputPath"].GetValue<string>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health new fields present on success path for any project`` () =
    // Verifies that every successful project_health response includes the six new fields.
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_fields_present_%s{runId}")
    try
        let projectPath = writeProject root   // uses existing writeProject (has Ionide.Analyzers)
        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let proj = result["project"].AsObject()

        Assert.NotNull(proj["isTestProject"])
        Assert.NotNull(proj["testFrameworks"])
        // testCount may be null (non-test project) — check the key is present in the object
        Assert.True(proj.ContainsKey("testCount"))
        Assert.True(proj.ContainsKey("lastBuildSucceeded"))
        Assert.True(proj.ContainsKey("lastBuildAt"))
        Assert.True(proj.ContainsKey("binaryOutputPath"))
    finally
        if Directory.Exists root then Directory.Delete(root, true)

// ─── should-2: test-attribute regex word-boundary bug ────────────────────────

[<Fact>]
let ``countTestAttributesInFile does not count NUnit TestFixture as a test method`` () =
    // Regression for word-boundary bug: [<TestFixture>] used to match 'Test'
    // and the trailing 'ixture' was swallowed by [^\]]*, inflating testCount.
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_testfixture_%s{runId}")
    let source =
        String.concat "\n"
            [ "module Tests"
              "open Xunit"
              "[<TestFixture>]"   // NUnit class attribute — must NOT be counted
              "type MyTests () ="
              "    [<Fact>]"      // one real test method
              "    member _.testA () = ()" ]
    try
        let projectPath = writeXunitTestProject root source
        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let proj = result["project"]

        // Only the [<Fact>] should count; [<TestFixture>] must be ignored.
        Assert.Equal(1, proj["testCount"].GetValue<int>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)

// ─── Restore-awareness (#138) ─────────────────────────────────────────────────
// When the probe reports that the project's external references are unresolved
// (declared but absent on disk), project_health must surface "unrestored" rather
// than a bare "available", and degrade the FCS readiness axis with a clear warning
// so an agent reads "restore first" instead of trusting `ready`.

[<Fact>]
let ``project_health flags an unrestored project via restoreStatus and a degraded fcs axis`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_unrestored_%s{runId}")

    try
        let projectPath = writeProject root
        // 1 of 200 references resolved = 0.5% < 20% → unrestored.
        let unrestoredProbe _ =
            async { return Ok { Source = "ionide-proj-info"; ReferencesExisting = 1; ReferencesTotal = 200 } }

        let result =
            reportWithProbe unrestoredProbe (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        let projectOptions = result["projectOptions"]
        Assert.Equal("available", projectOptions["status"].GetValue<string>())
        Assert.Equal("unrestored", projectOptions["restoreStatus"].GetValue<string>())
        Assert.Equal(200, projectOptions["referencesTotal"].GetValue<int>())
        Assert.Equal(1, projectOptions["referencesExisting"].GetValue<int>())
        Assert.NotNull(projectOptions["warning"])

        // The FCS axis must visibly degrade with a warning — not report a bare "ready".
        let fcs = result["toolingReadiness"]["fcs"]
        Assert.Equal("degraded", fcs["status"].GetValue<string>())
        let warnings = fcs["warnings"] :?> JsonArray
        Assert.True(warnings.Count > 0, "the unrestored project must surface an FCS warning")
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health reports a restored project as restored with high referencesResolved`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_restored_%s{runId}")

    try
        let projectPath = writeProject root
        let restoredProbe _ =
            async { return Ok { Source = "ionide-proj-info"; ReferencesExisting = 200; ReferencesTotal = 200 } }

        let result =
            reportWithProbe restoredProbe (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        let projectOptions = result["projectOptions"]
        Assert.Equal("restored", projectOptions["restoreStatus"].GetValue<string>())
        Assert.Equal(1.0, projectOptions["referencesResolved"].GetValue<float>())
        // No restore warning means the FCS axis stays ready.
        Assert.Equal("ready", (((result["toolingReadiness"])["fcs"])["status"]).GetValue<string>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)
