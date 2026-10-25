module FsLangMcp.Tests.ProjectFilesTests

open System.IO
open System.Xml.Linq
open Xunit
open FsLangMcp.ProjectFiles

let private workspaceRoot =
    Path.Combine(Path.GetTempPath(), "fslangmcp_unit_workspace")

let private underRoot relativePath =
    Path.Combine(workspaceRoot, relativePath)

let private projectFile relativePath =
    { Path = underRoot relativePath
      IncludePath = relativePath
      Link = None
      IsSignature = Path.GetExtension(relativePath).Equals(".fsi", System.StringComparison.OrdinalIgnoreCase)
      PairedImplementationPath = None
      PairedSignaturePath = None }

let private excludedReasons filtered =
    filtered.Excluded |> List.map snd

[<Fact>]
let ``filterProjectFiles excludes generated build linked and test artifacts by default`` () =
    let files =
        [ projectFile "Library.fs"
          projectFile "obj/Debug/net10.0/App.AssemblyInfo.fs"
          projectFile "obj/Debug/net10.0/Plain.fs"
          projectFile "bin/Debug/net10.0/Plain.fs"
          projectFile "Generated.g.fs"
          projectFile "View.Designer.fs"
          projectFile "tests/LibraryTests.fs"
          { projectFile "Linked.fs" with
              Link = Some "../Shared/Linked.fs" } ]

    let filtered = filterProjectFiles workspaceRoot (defaultFilterOptions Outline) files

    Assert.Equal<string>([ "Library.fs" ], filtered.Included |> List.map _.IncludePath)
    Assert.Contains(ObjOrBinDirectory, excludedReasons filtered)
    Assert.Contains(AssemblyInfoFile, excludedReasons filtered)
    Assert.Contains(GeneratedFile, excludedReasons filtered)
    Assert.Contains(DesignerFile, excludedReasons filtered)
    Assert.Contains(TestSource, excludedReasons filtered)
    Assert.Contains(ExternalLinkedFile, excludedReasons filtered)

[<Fact>]
let ``IncludeTests distinguishes evaluated test sources from test result artifacts`` () =
    let files =
        [ "src/Library.fs"
          "src/Contest.fs"
          "src/Latest.fs"
          "src/Protest.fs"
          "tests/App/Program.fs"
          "Tests.fs"
          "Generated.g.fs"
          "obj/Debug/net10.0/Plain.fs"
          "bin/Debug/net10.0/Plain.fs"
          "TestResults/run/Instrumented.fs"
          "test-results/run/Instrumented.fs"
          "coverage/run/Instrumented.fs" ]
        |> List.map (fun includePath -> underRoot includePath, includePath, None)
        |> projectFilesFromEvaluatedItems

    let defaults = filterProjectFiles workspaceRoot (defaultFilterOptions Outline) files

    Assert.Equal<string>(
        [ "src/Library.fs"; "src/Contest.fs"; "src/Latest.fs"; "src/Protest.fs" ],
        defaults.Included |> List.map _.IncludePath
    )

    Assert.Equal<ProjectFile * ExclusionReason>(
        [ projectFile "tests/App/Program.fs", TestSource
          projectFile "Tests.fs", TestSource
          projectFile "Generated.g.fs", GeneratedFile
          projectFile "obj/Debug/net10.0/Plain.fs", ObjOrBinDirectory
          projectFile "bin/Debug/net10.0/Plain.fs", ObjOrBinDirectory
          projectFile "TestResults/run/Instrumented.fs", TestResultArtifact
          projectFile "test-results/run/Instrumented.fs", TestResultArtifact
          projectFile "coverage/run/Instrumented.fs", TestResultArtifact ],
        defaults.Excluded
    )

    let summary = filterSummaryToJson defaults
    Assert.Equal(2, (summary["exclusionsByReason"]["test_source"]).GetValue<int>())
    Assert.Equal(3, (summary["exclusionsByReason"]["test_result_artifact"]).GetValue<int>())

    let withTests =
        filterProjectFiles
            workspaceRoot
            { defaultFilterOptions Outline with
                IncludeTests = true }
            files

    Assert.Equal<string>(
        [ "src/Library.fs"
          "src/Contest.fs"
          "src/Latest.fs"
          "src/Protest.fs"
          "tests/App/Program.fs"
          "Tests.fs" ],
        withTests.Included |> List.map _.IncludePath
    )

    Assert.Equal<ProjectFile * ExclusionReason>(
        [ projectFile "Generated.g.fs", GeneratedFile
          projectFile "obj/Debug/net10.0/Plain.fs", ObjOrBinDirectory
          projectFile "bin/Debug/net10.0/Plain.fs", ObjOrBinDirectory
          projectFile "TestResults/run/Instrumented.fs", TestResultArtifact
          projectFile "test-results/run/Instrumented.fs", TestResultArtifact
          projectFile "coverage/run/Instrumented.fs", TestResultArtifact ],
        withTests.Excluded
    )

[<Fact>]
let ``workspace containment uses path boundaries instead of string prefixes (#241)`` () =
    let siblingRoot = workspaceRoot + "Sibling"

    let siblingFile =
        { projectFile "Sibling.fs" with
            Path = Path.Combine(siblingRoot, "Sibling.fs") }

    let filtered =
        filterProjectFiles
            workspaceRoot
            (defaultFilterOptions ProjectInspection)
            [ projectFile "Inside.fs"; siblingFile ]

    Assert.Equal<string>([ "Inside.fs" ], filtered.Included |> List.map _.IncludePath)
    Assert.Equal<ProjectFile * ExclusionReason>([ siblingFile, OutsideWorkspace ], filtered.Excluded)

[<Fact>]
let ``workspace containment accepts the filesystem root (#241)`` () =
    let volumeRoot = Path.GetPathRoot(Path.GetFullPath(workspaceRoot))

    let rootFile =
        { projectFile "RootProbe.fs" with
            Path = Path.Combine(volumeRoot, "RootProbe.fs") }

    let filtered =
        filterProjectFiles volumeRoot (defaultFilterOptions ProjectInspection) [ rootFile ]

    Assert.Equal<ProjectFile>([ rootFile ], filtered.Included)
    Assert.Empty(filtered.Excluded)

[<Fact>]
let ``external linked files require both project-model policy and Link metadata (#256)`` () =
    let externalRoot = Path.Combine(Path.GetTempPath(), "fslangmcp_unit_external")

    let externalFile includePath link =
        { projectFile includePath with
            Path = Path.Combine(externalRoot, Path.GetFileName includePath)
            Link = link }

    let inside = projectFile "Program.fs"
    let linkedCompile = externalFile "../Shared/Domain.fs" (Some "Domain.fs")
    let unrelatedExternal = externalFile "../Shared/Unrelated.fs" None
    let files = [ inside; linkedCompile; unrelatedExternal ]

    let inspection =
        filterProjectFiles
            workspaceRoot
            { defaultFilterOptions ProjectInspection with
                IncludeExternalLinkedFiles = true }
            files

    Assert.Equal<ProjectFile>([ inside; linkedCompile ], inspection.Included)
    Assert.Equal<ProjectFile * ExclusionReason>([ unrelatedExternal, OutsideWorkspace ], inspection.Excluded)

    let review = filterProjectFiles workspaceRoot (defaultFilterOptions Review) files

    Assert.Equal<ProjectFile>([ inside ], review.Included)

    Assert.Equal<ProjectFile * ExclusionReason>(
        [ linkedCompile, ExternalLinkedFile; unrelatedExternal, OutsideWorkspace ],
        review.Excluded
    )

[<Fact>]
let ``IncludeGenerated surfaces generated files even when they live under obj (#186)`` () =
    // Real generated sources live in obj/ (SDK writes App.AssemblyInfo.fs and
    // .NETCoreApp,…AssemblyAttributes.fs there), so the obj/bin gate must not
    // shadow the IncludeGenerated gate.
    let files =
        [ projectFile "Library.fs"
          projectFile "obj/Debug/net10.0/App.AssemblyInfo.fs"
          projectFile "obj/Debug/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.fs" ]

    let flagged =
        filterProjectFiles
            workspaceRoot
            { defaultFilterOptions ProjectInspection with
                IncludeGenerated = true }
            files

    Assert.Equal(3, flagged.Included.Length)

    // Off by default — and the exclusion reason names WHAT the file is, not
    // merely where it lives, so a caller can see the flag is what gates it.
    let defaults = filterProjectFiles workspaceRoot (defaultFilterOptions ProjectInspection) files
    Assert.Equal<string>([ "Library.fs" ], defaults.Included |> List.map _.IncludePath)
    Assert.Equal<ExclusionReason>([ AssemblyInfoFile; AssemblyInfoFile ], excludedReasons defaults)

[<Fact>]
let ``filterProjectFiles honors include flags and max files`` () =
    let files =
        [ projectFile "A.fs"
          projectFile "Generated.g.fs"
          projectFile "obj/Debug/net10.0/B.fs"
          { projectFile "Linked.fs" with
              Link = Some "../Shared/Linked.fs" } ]

    let options =
        { defaultFilterOptions Outline with
            IncludeGenerated = true
            IncludeExternalLinkedFiles = true
            IncludeObjBin = true
            MaxFiles = Some 2 }

    let filtered = filterProjectFiles workspaceRoot options files

    Assert.Equal(2, filtered.Included.Length)
    Assert.True(filtered.Truncated)
    Assert.Equal<ExclusionReason>([ OverMaxFilesLimit; OverMaxFilesLimit ], excludedReasons filtered)

[<Fact>]
let ``filterSummaryToJson reports reason counts`` () =
    let filtered =
        filterProjectFiles
            workspaceRoot
            (defaultFilterOptions Outline)
            [ projectFile "A.fs"
              projectFile "Generated.g.fs"
              projectFile "View.Designer.fs" ]

    let summary = filterSummaryToJson filtered

    Assert.Equal(1, summary["includedFiles"].GetValue<int>())
    Assert.Equal(2, summary["excludedFiles"].GetValue<int>())
    Assert.Equal(1, (summary["exclusionsByReason"]["generated_file"]).GetValue<int>())
    Assert.Equal(1, (summary["exclusionsByReason"]["designer_file"]).GetValue<int>())
    Assert.False(summary["truncated"].GetValue<bool>())

[<Fact>]
let ``compileFiles pairs fsi and fs files from in memory project xml`` () =
    let projectPath = underRoot "App.fsproj"

    let doc =
        XDocument.Parse(
            """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Library.fsi" />
    <Compile Include="Library.fs" />
    <Compile Include="Generated.cs" />
  </ItemGroup>
</Project>
"""
        )

    let files = compileFiles projectPath doc

    Assert.Equal(2, files.Length)

    let signature = files |> List.find _.IsSignature
    let implementation = files |> List.find (fun file -> not file.IsSignature)

    Assert.Equal(Some implementation.Path, signature.PairedImplementationPath)
    Assert.Equal(Some signature.Path, implementation.PairedSignaturePath)

[<Fact>]
let ``compileFiles normalizes MSBuild backslash separators to the host OS`` () =
    // MSBuild treats `\` as a separator on every OS; on Unix it is a literal
    // filename character, so the raw include must be normalized before use (#160).
    let projectPath = underRoot "App.fsproj"

    let doc =
        XDocument.Parse(
            """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Domain\Money.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>
"""
        )

    let paths = compileFiles projectPath doc |> List.map _.Path

    Assert.Contains(underRoot (Path.Combine("Domain", "Money.fs")), paths)
    Assert.Contains(underRoot "Program.fs", paths)

let private solutionContents (extension: string) (members: string list) =
    if extension = ".slnx" then
        [ "<Solution>"
          yield! members |> List.map (fun path -> $"  <Project Path=\"{path}\" />")
          "</Solution>" ]
        |> String.concat "\n"
    else
        [ "Microsoft Visual Studio Solution File, Format Version 12.00"
          yield!
              members
              |> List.mapi (fun index path ->
                  let windowsPath = path.Replace('/', '\\')
                  $"Project(\"{{F2A71F9B-5D33-465A-A702-920D77279786}}\") = \"Project{index}\", \"{windowsPath}\", \"{{00000000-0000-0000-0000-{index:D12}}}\"\nEndProject")
          "Global"
          "EndGlobal" ]
        |> String.concat "\n"

[<Theory>]
[<InlineData(".sln")>]
[<InlineData(".slnx")>]
let ``solution discovery preserves typed declared membership while loadable view filters missing projects``
    (extension: string)
    =
    let runId = System.Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_solution_discovery_%s{runId}")

    let write (relativePath: string) (content: string) =
        let path = Path.Combine(root, relativePath)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)
        path

    try
        let existingA = write "A/A.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        let existingB = write "B/B.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />"

        let cases =
            [ "all-present", [ "A/A.fsproj"; "B/B.fsproj" ], [| true; true |]
              "one-missing", [ "A/A.fsproj"; "Missing/Missing.fsproj" ], [| true; false |]
              "all-missing", [ "MissingOne/MissingOne.fsproj"; "MissingTwo/MissingTwo.fsproj" ], [| false; false |] ]

        for name, members, expectedExists in cases do
            let solutionPath =
                write $"{name}{extension}" (solutionContents extension members)

            let discovered = SolutionParsing.discoverProjects solutionPath
            let actualExists = discovered |> Array.map SolutionParsing.isLoadable
            let declaredPaths =
                if extension = ".sln" then
                    SolutionParsing.fsprojsFromSln solutionPath
                else
                    SolutionParsing.fsprojsFromSlnx solutionPath

            Assert.Equal<bool array>(expectedExists, actualExists)
            Assert.Equal(members.Length, discovered.Length)
            Assert.Equal<string array>(discovered |> Array.map SolutionParsing.projectPath, declaredPaths)

            let loadable = SolutionParsing.listProjects solutionPath

            Assert.Equal(expectedExists |> Array.filter id |> Array.length, loadable.Length)

            for project in discovered |> Array.filter SolutionParsing.isMissing do
                Assert.False(File.Exists(SolutionParsing.projectPath project))

        Assert.Contains(existingA, SolutionParsing.listProjects(Path.Combine(root, $"all-present{extension}")))
        Assert.Contains(existingB, SolutionParsing.listProjects(Path.Combine(root, $"all-present{extension}")))
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)
