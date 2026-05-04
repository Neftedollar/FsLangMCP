module FsLangMcp.Tests.ProjectHealthTests

open System.IO
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.ProjectHealth
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
        Assert.Equal("ready", (result["toolingReadiness"]["status"]).GetValue<string>())
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
    Assert.Equal("blocked", (result["toolingReadiness"]["status"]).GetValue<string>())

[<Fact>]
let ``project_health blocks directory with multiple fsproj files`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_multi_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let result = report (healthArgs root (Some root)) (readySnapshot root root)

        Assert.Equal("blocked", (result["toolingReadiness"]["status"]).GetValue<string>())
        Assert.Contains("Multiple .fsproj", ((result["toolingReadiness"]["blockers"])[0]).GetValue<string>())
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

        Assert.Equal("blocked", (result["toolingReadiness"]["status"]).GetValue<string>())
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

        Assert.Equal("ready", (result["toolingReadiness"]["status"]).GetValue<string>())
        Assert.Equal("no_analyzers_configured", (result["analyzers"]["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health degrades when lsp workspace is not ready`` () =
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

        Assert.Equal("degraded", (result["toolingReadiness"]["status"]).GetValue<string>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)
