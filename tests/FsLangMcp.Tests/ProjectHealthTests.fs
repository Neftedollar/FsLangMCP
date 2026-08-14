module FsLangMcp.Tests.ProjectHealthTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.ProjectHealth
open FsLangMcp.ProjectInspection
open FsLangMcp.ProjectFiles
open FsLangMcp.FcsBridge
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
      LoadedProjects = [| projectPath |]
      SessionLive = true
      WorkspaceReady = true
      DiagnosticsFileCount = 0 }

let private testItemMetadata name (element: XElement) =
    element.Attributes()
    |> Seq.tryPick (fun attribute ->
        if attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) then
            Some attribute.Value
        else
            None)
    |> Option.orElseWith (fun () ->
        element.Elements()
        |> Seq.tryPick (fun child ->
            if child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) then
                Some child.Value
            else
                None))

let private testEvaluatedSnapshot referencesExisting referencesTotal projectPath =
    match tryReadProject projectPath with
    | Error reason -> Error reason
    | Ok doc ->
        let fullPath = Path.GetFullPath projectPath
        let projectDir = Path.GetDirectoryName fullPath
        let property name = childValue name doc

        let packages =
            doc.Descendants(xname "PackageReference")
            |> Seq.choose (fun element ->
                attr "Include" element
                |> Option.map (fun packageId ->
                    { PackageId = packageId
                      Version = testItemMetadata "Version" element
                      FullPath = None
                      IncludeAssets = testItemMetadata "IncludeAssets" element
                      PrivateAssets = testItemMetadata "PrivateAssets" element }))
            |> Seq.toList

        let projectReferences =
            doc.Descendants(xname "ProjectReference")
            |> Seq.choose (fun element ->
                attr "Include" element
                |> Option.map (fun includePath ->
                    let path =
                        if Path.IsPathFullyQualified includePath then includePath
                        else Path.Combine(projectDir, includePath)

                    { IncludePath = includePath
                      ProjectPath = Path.GetFullPath path
                      TargetFramework = None }))
            |> Seq.toList

        let properties =
            doc.Descendants()
            |> Seq.filter (fun element -> element.Parent <> null && element.Parent.Name.LocalName = "PropertyGroup")
            |> Seq.map (fun element -> element.Name.LocalName, element.Value.Trim())
            |> Map.ofSeq

        let targetFrameworks =
            property "TargetFrameworks"
            |> Option.map (fun value ->
                value.Split(';', StringSplitOptions.RemoveEmptyEntries) |> Array.toList)
            |> Option.defaultValue (property "TargetFramework" |> Option.toList)

        let isTestProject =
            boolProperty "IsTestProject" doc = Some true
            || packages
               |> List.exists (fun package ->
                   package.PackageId.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                   || package.PackageId.Contains("nunit", StringComparison.OrdinalIgnoreCase)
                   || package.PackageId.Contains("expecto", StringComparison.OrdinalIgnoreCase))

        Ok
            { ProjectPath = fullPath
              ProjectDirectory = projectDir
              ProjectName = Path.GetFileNameWithoutExtension fullPath
              EvaluationSource = "test-evaluated"
              Sdk = attr "Sdk" doc.Root
              TargetFramework = property "TargetFramework"
              TargetFrameworks = targetFrameworks
              OutputType = property "OutputType"
              AssemblyName = property "AssemblyName" |> Option.defaultValue (Path.GetFileNameWithoutExtension fullPath)
              Configuration = property "Configuration"
              IsTestProject = isTestProject
              RestoreSucceeded = true
              TargetPath = None
              Properties = properties
              Files = compileFiles fullPath doc
              PackageReferences = packages
              ProjectReferences = projectReferences
              ImportedProjects = []
              OtherOptions = [||]
              ReferencesExisting = referencesExisting
              ReferencesTotal = referencesTotal }

let private testEvaluatedProvider path =
    async { return testEvaluatedSnapshot 0 0 path }

let private report args snapshot =
    createReport args snapshot testEvaluatedProvider |> Async.RunSynchronously

/// Like `report` but with an injectable probe so tests can simulate an unrestored
/// project (references declared but absent on disk).
let private reportWithProbe (probe: ProjectOptionsProbe) args snapshot =
    let provider path =
        async {
            match! probe path with
            | Error reason -> return Error reason
            | Ok info ->
                return testEvaluatedSnapshot info.ReferencesExisting info.ReferencesTotal path
        }

    createReport args snapshot provider |> Async.RunSynchronously

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
let ``project_health resolves MSBuild backslash Compile includes on the host OS`` () =
    // A Windows-authored `Domain\Money.fs` include must not be reported as a
    // missing file on macOS/Linux (#160).
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_%s{runId}")

    try
        Directory.CreateDirectory(Path.Combine(root, "Domain")) |> ignore
        File.WriteAllText(Path.Combine(root, "Domain", "Money.fs"), "module Money\n\nlet value = 1\n")

        let projectPath = Path.Combine(root, "Library.fsproj")

        File.WriteAllText(
            projectPath,
            String.concat
                "\n"
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                  "  <ItemGroup><Compile Include=\"Domain\\Money.fs\" /></ItemGroup>"
                  "</Project>" ]
        )

        let result =
            report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)

        Assert.Equal(1, (result["files"]["sourceFileCount"]).GetValue<int>())
        Assert.Equal(0, (result["files"]["missingFiles"]).AsArray().Count)
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
let ``project_health summarizes a directory with multiple fsproj files (#100)`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_multi_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let result = report (healthArgs root (Some root)) (readySnapshot root root)

        // #100: a directory with multiple projects is summarized, not blocked.
        Assert.Equal("solution", (result["reportKind"]).GetValue<string>())
        Assert.Equal("solution", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal(2, ((result["solution"])["projectCount"]).GetValue<int>())
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
              LoadedProjects = [| projectPath |]
              SessionLive = false
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
                testEvaluatedProvider
            |> Async.RunSynchronously

        Assert.Equal("ok", result["status"].GetValue<string>())
        Assert.Equal(1, (result["compileOrder"] :?> JsonArray).Count)
        let firstCompileFile = (result["compileOrder"] :?> JsonArray)[0]
        Assert.Equal("implementation", firstCompileFile["kind"].GetValue<string>())
        Assert.Equal(1, (result["filterSummary"]["includedFiles"]).GetValue<int>())
        Assert.True((result["references"]["packageReferences"] :?> JsonArray).Count >= 1)
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``inspection and health share evaluated SDK defaults conditions imports and references (P1-08)`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_evaluated_project_%s{runId}")
        let appDir = Path.Combine(root, "App")
        let dependencyDir = Path.Combine(root, "Dependency")
        let sharedDir = Path.Combine(root, "Shared")

        let write (path: string) (content: string) =
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, content)

        try
            Directory.CreateDirectory(appDir) |> ignore
            Directory.CreateDirectory(dependencyDir) |> ignore
            Directory.CreateDirectory(sharedDir) |> ignore

            let dependencyProject = Path.Combine(dependencyDir, "Dependency.fsproj")
            let appProject = Path.Combine(appDir, "App.fsproj")
            let importedProps = Path.Combine(root, "Directory.Build.props")

            write (Path.Combine(dependencyDir, "Dependency.fs")) "module Dependency\n\nlet value = 1\n"

            write
                dependencyProject
                (String.concat
                    "\n"
                    [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                      "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                      "  <ItemGroup><Compile Include=\"Dependency.fs\" /></ItemGroup>"
                      "</Project>" ])

            write (Path.Combine(appDir, "Default.fs")) "module App.Default\n\nlet value = Dependency.value\n"
            write (Path.Combine(appDir, "Conditional.fs")) "module App.Conditional\n"
            write (Path.Combine(sharedDir, "Imported.fs")) "module App.Imported\n\nlet imported = 42\n"

            write
                importedProps
                (String.concat
                    "\n"
                    [ "<Project>"
                      "  <PropertyGroup Condition=\"'$(MSBuildProjectName)' == 'App'\">"
                      "    <LangVersion>preview</LangVersion>"
                      "    <P108Stamp>before</P108Stamp>"
                      "  </PropertyGroup>"
                      "  <ItemGroup Condition=\"'$(MSBuildProjectName)' == 'App'\">"
                      "    <Compile Include=\"$(MSBuildThisFileDirectory)Shared/Imported.fs\" Link=\"Imported.fs\" />"
                      "    <PackageReference Include=\"Ionide.Analyzers\" Version=\"0.15.0\" IncludeAssets=\"analyzers\" PrivateAssets=\"all\" />"
                      "    <ProjectReference Include=\"$(MSBuildThisFileDirectory)Dependency/Dependency.fsproj\" />"
                      "  </ItemGroup>"
                      "</Project>" ])

            write
                appProject
                (String.concat
                    "\n"
                    [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                      "  <PropertyGroup>"
                      "    <TargetFramework>net10.0</TargetFramework>"
                      "    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>"
                      "    <IncludeConditional>false</IncludeConditional>"
                      "    <P108Project>alpha1</P108Project>"
                      "  </PropertyGroup>"
                      "  <ItemGroup>"
                      "    <Compile Remove=\"Conditional.fs\" Condition=\"'$(IncludeConditional)' != 'true'\" />"
                      "  </ItemGroup>"
                      "</Project>" ])

            // Keep the regression hermetic: both packages are already restored for
            // this repository, so expose their nupkgs through a tiny local feed.
            let globalPackages =
                Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.defaultWith (fun () ->
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"))

            let localFeed = Path.Combine(root, "local-feed")
            Directory.CreateDirectory(localFeed) |> ignore

            for packageId in [ "fsharp.core"; "ionide.analyzers" ] do
                let packageDirectory = Path.Combine(globalPackages, packageId)

                for packageArchive in Directory.EnumerateFiles(packageDirectory, "*.nupkg", SearchOption.AllDirectories) do
                    File.Copy(packageArchive, Path.Combine(localFeed, Path.GetFileName packageArchive), true)

            let nugetConfig = Path.Combine(root, "NuGet.Config")

            write
                nugetConfig
                (String.concat
                    "\n"
                    [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                      "<configuration>"
                      "  <packageSources>"
                      "    <clear />"
                      "    <add key=\"fixture-local\" value=\"local-feed\" />"
                      "  </packageSources>"
                      "  <auditSources><clear /></auditSources>"
                      "</configuration>" ])

            let psi =
                ProcessStartInfo(
                    "dotnet",
                    $"restore \"%s{appProject}\" --configfile \"%s{nugetConfig}\" --ignore-failed-sources --nologo --disable-build-servers -p:UseSharedCompilation=false -p:NuGetAudit=false"
                )

            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
            psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] <- "1"
            psi.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] <- "true"

            use restore = Process.Start psi
            let restoreOutput = restore.StandardOutput.ReadToEnd() + restore.StandardError.ReadToEnd()
            restore.WaitForExit()
            Assert.True(restore.ExitCode = 0, $"fixture restore failed:\n{restoreOutput}")

            let bridge = FcsBridge()

            let provider path =
                bridge.GetEvaluatedProjectSnapshot(path) |> Async.AwaitTask

            let! inspected =
                inspectProject
                    { projectPath = Some appProject
                      workspacePath = Some root
                      scope = None
                      includeGeneratedFiles = Some false
                      includePackageDetails = Some true
                      includeResolvedOptions = Some true }
                    provider
                |> Async.StartAsTask

            Assert.Equal("ok", inspected["status"].GetValue<string>())
            Assert.Equal("evaluated", (inspected["evaluation"]["status"]).GetValue<string>())
            Assert.Equal("ionide-proj-info", (inspected["evaluation"]["source"]).GetValue<string>())

            let evaluatedImports =
                (inspected["evaluation"]["imports"]).AsArray()
                |> Seq.map _.GetValue<string>()
                |> Seq.toArray

            let actualImports = String.concat "\n" evaluatedImports

            Assert.True(
                Array.contains importedProps evaluatedImports,
                $"Expected import '%s{importedProps}'. Actual imports:\n%s{actualImports}"
            )

            let compilePaths =
                inspected["compileOrder"].AsArray()
                |> Seq.map (fun item -> item["path"].GetValue<string>())
                |> Seq.toArray

            Assert.Contains(Path.Combine(appDir, "Default.fs"), compilePaths)
            Assert.Contains(Path.Combine(sharedDir, "Imported.fs"), compilePaths)
            Assert.DoesNotContain(Path.Combine(appDir, "Conditional.fs"), compilePaths)

            let packageIds =
                (inspected["references"]["packageReferences"]).AsArray()
                |> Seq.map (fun item -> item["packageId"].GetValue<string>())
                |> Seq.toArray

            Assert.Contains("Ionide.Analyzers", packageIds)

            let projectReferencePaths =
                (inspected["references"]["projectReferences"]).AsArray()
                |> Seq.map (fun item -> item["path"].GetValue<string>())
                |> Seq.toArray

            Assert.Contains(dependencyProject, projectReferencePaths)

            let langVersion =
                inspected["properties"].AsArray()
                |> Seq.find (fun property -> property["name"].GetValue<string>() = "LangVersion")

            Assert.Equal("preview", langVersion["value"].GetValue<string>())

            let loadCountAfterInspection = bridge.ProjectOptionsLoadCount

            let healthSnapshot =
                { ProjectPath = Some appProject
                  WorkspaceRoot = Some appDir
                  LoadedProjects = [| appProject |]
                  SessionLive = true
                  WorkspaceReady = true
                  DiagnosticsFileCount = 0 }

            let! health =
                createReport (healthArgs appProject (Some appDir)) healthSnapshot provider
                |> Async.StartAsTask

            Assert.Equal("ok", health["status"].GetValue<string>())
            Assert.Equal("evaluated", (health["evaluation"]["status"]).GetValue<string>())
            Assert.Equal("analyzers_configured", (health["analyzers"]["status"]).GetValue<string>())
            Assert.True((health["files"]["sourceFileCount"]).GetValue<int>() >= 2)
            Assert.Equal(loadCountAfterInspection, bridge.ProjectOptionsLoadCount)

            // A project/import cache key must not trust timestamp + length. Editors
            // and atomic replace tools can preserve both while changing evaluation.
            let originalProps = File.ReadAllText importedProps
            let originalTimestamp = File.GetLastWriteTimeUtc importedProps
            let updatedProps = originalProps.Replace(">before<", ">after!<", StringComparison.Ordinal)
            Assert.Equal(originalProps.Length, updatedProps.Length)
            File.WriteAllText(importedProps, updatedProps)
            File.SetLastWriteTimeUtc(importedProps, originalTimestamp)

            let loadCountBeforeSameStampEdit = bridge.ProjectOptionsLoadCount
            let! refreshedResult = bridge.GetEvaluatedProjectSnapshot(appProject)

            match refreshedResult with
            | Error reason -> Assert.Fail($"evaluation after same-stamp import edit failed: %s{reason}")
            | Ok refreshed ->
                Assert.Equal("after!", refreshed.Properties["P108Stamp"])
                Assert.True(bridge.ProjectOptionsLoadCount > loadCountBeforeSameStampEdit)

            let originalProject = File.ReadAllText appProject
            let originalProjectTimestamp = File.GetLastWriteTimeUtc appProject
            let updatedProject = originalProject.Replace(">alpha1<", ">beta22<", StringComparison.Ordinal)
            Assert.Equal(originalProject.Length, updatedProject.Length)
            File.WriteAllText(appProject, updatedProject)
            File.SetLastWriteTimeUtc(appProject, originalProjectTimestamp)

            let loadCountBeforeSameStampProjectEdit = bridge.ProjectOptionsLoadCount
            let! projectRefreshResult = bridge.GetEvaluatedProjectSnapshot(appProject)

            match projectRefreshResult with
            | Error reason -> Assert.Fail($"evaluation after same-stamp project edit failed: %s{reason}")
            | Ok refreshed ->
                Assert.Equal("beta22", refreshed.Properties["P108Project"])
                Assert.True(bridge.ProjectOptionsLoadCount > loadCountBeforeSameStampProjectEdit)

            // SDK default Compile globs must also notice a new source when the
            // directory timestamp is restored to its previous value.
            let originalDirectoryTimestamp = Directory.GetLastWriteTimeUtc appDir
            let addedDefaultSource = Path.Combine(appDir, "AddedDefault.fs")
            write addedDefaultSource "module App.AddedDefault\n"
            Directory.SetLastWriteTimeUtc(appDir, originalDirectoryTimestamp)

            let loadCountBeforeSourceListingEdit = bridge.ProjectOptionsLoadCount
            let! listingRefreshResult = bridge.GetEvaluatedProjectSnapshot(appProject)

            match listingRefreshResult with
            | Error reason -> Assert.Fail($"evaluation after source-list edit failed: %s{reason}")
            | Ok refreshed ->
                Assert.Contains(addedDefaultSource, refreshed.Files |> List.map _.Path)
                Assert.True(bridge.ProjectOptionsLoadCount > loadCountBeforeSourceListingEdit)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``fsharp_project_inspect resolves MSBuild backslash ProjectReference includes on the host OS`` () =
    // `..\Dep\Dep.fsproj` is how Windows-authored projects reference siblings; it
    // must resolve (exists=true) on macOS/Linux too (#160).
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_inspect_%s{runId}")

    try
        let appDir = Path.Combine(root, "App")
        let depDir = Path.Combine(root, "Dep")
        Directory.CreateDirectory appDir |> ignore
        Directory.CreateDirectory depDir |> ignore
        File.WriteAllText(Path.Combine(depDir, "Dep.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(appDir, "Library.fs"), "module Library\n\nlet value = 1\n")

        let projectPath = Path.Combine(appDir, "App.fsproj")

        File.WriteAllText(
            projectPath,
            String.concat
                "\n"
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                  "  <ItemGroup><Compile Include=\"Library.fs\" /></ItemGroup>"
                  "  <ItemGroup><ProjectReference Include=\"..\\Dep\\Dep.fsproj\" /></ItemGroup>"
                  "</Project>" ]
        )

        let result =
            inspectProject
                { projectPath = Some projectPath
                  workspacePath = Some root
                  scope = None
                  includeGeneratedFiles = None
                  includePackageDetails = None
                  includeResolvedOptions = Some false }

        let projectReferences = result["references"]["projectReferences"] :?> JsonArray
        Assert.Equal(1, projectReferences.Count)
        let firstReference = projectReferences[0]
        Assert.True(firstReference["exists"].GetValue<bool>())
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
let ``project_health summarizes a .slnx with multiple fsproj files (#100)`` () =
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
            { ProjectPath = None
              WorkspaceRoot = Some root
              LoadedProjects = [||]
              SessionLive = true
              WorkspaceReady = true
              DiagnosticsFileCount = 0 }

        let result = report (healthArgs slnxPath None) snapshot

        Assert.Equal("solution", (result["reportKind"]).GetValue<string>())
        Assert.Equal("solution", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal(2, ((result["solution"])["projectCount"]).GetValue<int>())
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``project_health summarizes a .sln with multiple fsproj files (#100)`` () =
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
            { ProjectPath = None
              WorkspaceRoot = Some root
              LoadedProjects = [||]
              SessionLive = true
              WorkspaceReady = true
              DiagnosticsFileCount = 0 }

        let result = report (healthArgs slnPath None) snapshot

        Assert.Equal("solution", (result["reportKind"]).GetValue<string>())
        Assert.Equal("solution", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal(2, ((result["solution"])["projectCount"]).GetValue<int>())
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
              LoadedProjects = [| projectPath |]
              SessionLive = false
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
          LoadedProjects = [||]
          SessionLive = false
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
        Assert.Equal("Release", proj["configuration"].GetValue<string>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health new fields present on success path for any project`` () =
    // Verifies that every successful project_health response includes the test/build fields.
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
        Assert.True(proj.ContainsKey("configuration"))
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

[<Fact>]
let ``project_health summarizes a multi-project solution instead of blocking (#100)`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_sln_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore

        let writeProj name =
            let dir = Path.Combine(root, name)
            Directory.CreateDirectory(dir) |> ignore
            File.WriteAllText(Path.Combine(dir, name + ".fs"), $"module %s{name}\n\nlet v = 1\n")
            let p = Path.Combine(dir, name + ".fsproj")

            File.WriteAllText(
                p,
                String.concat
                    "\n"
                    [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                      "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                      $"  <ItemGroup><Compile Include=\"%s{name}.fs\" /></ItemGroup>"
                      "</Project>" ]
            )

        writeProj "Alpha"
        writeProj "Beta"

        let slnx = Path.Combine(root, "Sln.slnx")

        File.WriteAllText(
            slnx,
            String.concat
                "\n"
                [ "<Solution>"
                  "  <Project Path=\"Alpha/Alpha.fsproj\" />"
                  "  <Project Path=\"Beta/Beta.fsproj\" />"
                  "</Solution>" ]
        )

        let result = report (healthArgs slnx None) (readySnapshot slnx root)

        Assert.Equal("ok", (result["status"]).GetValue<string>())
        Assert.Equal("solution", (result["reportKind"]).GetValue<string>())
        // The whole point of #100: a solution must NOT read as blocked.
        Assert.Equal("solution", ((result["toolingReadiness"])["overall"]).GetValue<string>())
        Assert.Equal(2, ((result["solution"])["projectCount"]).GetValue<int>())
        Assert.Equal(2, ((result["solution"])["projects"]).AsArray().Count)
    finally
        if Directory.Exists root then Directory.Delete(root, true)

// ─── v0.13.2 regressions from #100 field feedback ──────────────────────────────

[<Fact>]
let ``project_health uses the active solution workspace root for reverse test discovery`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_workspace_%s{runId}")

    try
        let sourceDir = Path.Combine(root, "src", "Library")
        let projectPath = writeNonTestProject sourceDir
        let testsDir = Path.Combine(root, "tests", "Library.Tests")
        Directory.CreateDirectory(testsDir) |> ignore
        File.WriteAllText(Path.Combine(testsDir, "Tests.fs"), "module Tests\n\n[<Xunit.Fact>]\nlet ok () = ()\n")

        let testProjectPath = Path.Combine(testsDir, "Library.Tests.fsproj")
        let relativeProjectReference = Path.GetRelativePath(testsDir, projectPath)

        File.WriteAllText(
            testProjectPath,
            String.concat
                "\n"
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                  "  <ItemGroup><Compile Include=\"Tests.fs\" /></ItemGroup>"
                  "  <ItemGroup>"
                  "    <PackageReference Include=\"xunit\" Version=\"2.9.0\" />"
                  $"    <ProjectReference Include=\"%s{relativeProjectReference}\" />"
                  "  </ItemGroup>"
                  "</Project>" ]
        )

        let solutionPath = Path.Combine(root, "Workspace.slnx")
        File.WriteAllText(solutionPath, "<Solution />")

        // Simulate an explicit per-project health request after set_project selected
        // the solution: no workspacePath argument, but the live LSP snapshot knows its root.
        let snapshot =
            { ProjectPath = Some solutionPath
              WorkspaceRoot = Some root
              LoadedProjects = [| projectPath; testProjectPath |]
              SessionLive = true
              WorkspaceReady = true
              DiagnosticsFileCount = 0 }

        let result = report (healthArgs projectPath None) snapshot
        let discovered = (result["tests"]["projects"]).AsArray()

        Assert.Equal(root, (result["workspace"]["workspaceRoot"]).GetValue<string>())
        Assert.Equal("test_projects_found", (result["tests"]["status"]).GetValue<string>())
        Assert.Single(discovered) |> ignore
        Assert.Equal(testProjectPath, (discovered[0]["projectPath"]).GetValue<string>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health does not reuse ready LSP state from a different active project`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_context_%s{runId}")

    try
        let activeProject = writeNonTestProject (Path.Combine(root, "Active"))
        let inspectedProject = writeNonTestProject (Path.Combine(root, "Inspected"))
        let activeSnapshot = readySnapshot activeProject root

        let result = report (healthArgs inspectedProject (Some root)) activeSnapshot
        let fcs = result["toolingReadiness"]["fcs"]
        let lsp = result["toolingReadiness"]["lsp"]
        let overall = result["toolingReadiness"]["overall"]
        let workspace = result["workspace"]

        Assert.Equal("ready", fcs["status"].GetValue<string>())
        Assert.Equal("not_ready", lsp["status"].GetValue<string>())
        Assert.False(lsp["contextMatched"].GetValue<bool>())
        Assert.Equal("fcs_only", overall.GetValue<string>())
        Assert.Equal(activeProject, workspace["lspProjectPath"].GetValue<string>())
        Assert.False(workspace["lspContextMatched"].GetValue<bool>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health serializes evaluated project loads during reverse discovery`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_serial_%s{runId}")

    try
        let currentProject = writeNonTestProject (Path.Combine(root, "Current"))
        writeNonTestProject (Path.Combine(root, "SiblingA")) |> ignore
        writeNonTestProject (Path.Combine(root, "SiblingB")) |> ignore

        let mutable activeCalls = 0
        let mutable maxConcurrentCalls = 0
        let mutable totalCalls = 0
        let counterGate = obj ()

        let provider path =
            async {
                let active = Interlocked.Increment(&activeCalls)
                Interlocked.Increment(&totalCalls) |> ignore

                lock counterGate (fun () ->
                    maxConcurrentCalls <- max maxConcurrentCalls active)

                try
                    do! Async.Sleep 25
                    return testEvaluatedSnapshot 0 0 path
                finally
                    Interlocked.Decrement(&activeCalls) |> ignore
            }

        let result =
            createReport (healthArgs currentProject (Some root)) (readySnapshot currentProject root) provider
            |> Async.RunSynchronously

        Assert.Equal("ok", result["status"].GetValue<string>())
        Assert.True(totalCalls >= 4, $"expected current evaluation plus discovery loads, got %d{totalCalls}")
        Assert.Equal(1, maxConcurrentCalls)
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``analyzer config discovery walks ancestors independently and nearest files shadow parents`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_configs_%s{runId}")

    try
        let projectDir = Path.Combine(root, "src", "Library")
        let projectPath = writeNonTestProject projectDir
        let nearerProps = Path.Combine(root, "src", "Directory.Build.props")
        let rootProps = Path.Combine(root, "Directory.Build.props")
        let rootTargets = Path.Combine(root, "Directory.Build.targets")
        let rootPackages = Path.Combine(root, "Directory.Packages.props")
        let rootEditorConfig = Path.Combine(root, ".editorconfig")

        // The parent props contains a real analyzer, but normal nearest-file MSBuild
        // lookup must stop at the closer, bare props file under src/.
        File.WriteAllText(
            rootProps,
            "<Project><ItemGroup><PackageReference Include=\"Parent.Analyzer\" Version=\"1.0\" /></ItemGroup></Project>"
        )
        File.WriteAllText(nearerProps, "<Project />")
        File.WriteAllText(rootTargets, "<Project />")
        File.WriteAllText(rootPackages, "<Project />")
        File.WriteAllText(rootEditorConfig, "root = true\n")

        let config = detectAnalyzerConfig projectPath |> Result.defaultWith failwith

        Assert.False(config.Configured)
        Assert.Empty(config.Packages)
        Assert.Equal<string list>(
            [ nearerProps; rootTargets; rootPackages; rootEditorConfig ],
            config.ConfigFiles
        )

        // A sibling project with no nearer override inherits and scans the root props.
        let siblingProjectPath = writeNonTestProject (Path.Combine(root, "samples", "Consumer"))
        let inheritedConfig = detectAnalyzerConfig siblingProjectPath |> Result.defaultWith failwith
        Assert.True(inheritedConfig.Configured)
        Assert.Contains(inheritedConfig.Packages, fun package -> package.PackageId = "Parent.Analyzer")
        Assert.Contains(rootProps, inheritedConfig.ConfigFiles)
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``analyzer PackageReference reads child metadata and attributes take precedence`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_package_metadata_%s{runId}")

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
                  "  <ItemGroup>"
                  "    <PackageReference Include=\"Contoso.Tooling\">"
                  "      <Version>1.2.3</Version>"
                  "      <IncludeAssets>analyzers;build</IncludeAssets>"
                  "      <PrivateAssets>all</PrivateAssets>"
                  "    </PackageReference>"
                  "    <PackageReference Include=\"Override.Analyzer\" Version=\"2.0.0\" IncludeAssets=\"runtime\" PrivateAssets=\"compile\">"
                  "      <Version>9.9.9</Version>"
                  "      <IncludeAssets>analyzers</IncludeAssets>"
                  "      <PrivateAssets>all</PrivateAssets>"
                  "    </PackageReference>"
                  "    <PackageReference Include=\"CaseInsensitive.Tooling\">"
                  "      <version>3.4.5</version>"
                  "      <includeassets>analyzers</includeassets>"
                  "      <privateassets>all</privateassets>"
                  "    </PackageReference>"
                  "  </ItemGroup>"
                  "</Project>" ]
        )

        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let packages = (result["analyzers"]["analyzers"]).AsArray()
        let byId = packages |> Seq.map (fun p -> (p["packageId"]).GetValue<string>(), p) |> Map.ofSeq
        let childOnly = byId["Contoso.Tooling"]
        let attributeFirst = byId["Override.Analyzer"]
        let caseInsensitive = byId["CaseInsensitive.Tooling"]

        Assert.Equal("analyzers_configured", (result["analyzers"]["status"]).GetValue<string>())
        Assert.Equal("1.2.3", childOnly["version"].GetValue<string>())
        Assert.Equal("analyzers;build", childOnly["includeAssets"].GetValue<string>())
        Assert.Equal("all", childOnly["privateAssets"].GetValue<string>())
        Assert.Equal("2.0.0", attributeFirst["version"].GetValue<string>())
        Assert.Equal("runtime", attributeFirst["includeAssets"].GetValue<string>())
        Assert.Equal("compile", attributeFirst["privateAssets"].GetValue<string>())
        Assert.Equal("3.4.5", caseInsensitive["version"].GetValue<string>())
        Assert.Equal("analyzers", caseInsensitive["includeAssets"].GetValue<string>())
        Assert.Equal("all", caseInsensitive["privateAssets"].GetValue<string>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)

[<Fact>]
let ``project_health reports the configuration of the selected build artifact`` () =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_health_build_config_%s{runId}")

    try
        let projectPath = writeNonTestProject root
        let debugDir = Path.Combine(root, "bin", "Debug", "net10.0")
        let releaseDir = Path.Combine(root, "bin", "Release", "net10.0")
        Directory.CreateDirectory(debugDir) |> ignore
        Directory.CreateDirectory(releaseDir) |> ignore
        let debugDll = Path.Combine(debugDir, "Library.dll")
        let releaseDll = Path.Combine(releaseDir, "Library.dll")
        File.WriteAllBytes(debugDll, [||])
        File.WriteAllBytes(releaseDll, [||])
        File.SetLastWriteTimeUtc(debugDll, System.DateTime.UtcNow.AddMinutes(-2.0))
        File.SetLastWriteTimeUtc(releaseDll, System.DateTime.UtcNow.AddMinutes(-1.0))

        let result = report (healthArgs projectPath (Some root)) (readySnapshot projectPath root)
        let project = result["project"]

        Assert.Equal(releaseDll, project["binaryOutputPath"].GetValue<string>())
        Assert.Equal("Release", project["configuration"].GetValue<string>())
    finally
        if Directory.Exists root then Directory.Delete(root, true)
