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
    { projectPath = projectPath
      workspacePath = workspacePath
      scope = None
      compileCheck = None }

let private readySnapshot projectPath root =
    { ProjectPath = Some projectPath
      WorkspaceRoot = Some root
      WorkspaceReady = true
      DiagnosticsFileCount = 0 }

let private report args snapshot =
    let probe _ = async { return Ok "test-probe" }
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
                { projectPath = projectPath
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
