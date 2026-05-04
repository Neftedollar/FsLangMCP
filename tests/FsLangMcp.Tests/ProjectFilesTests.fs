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
          projectFile "Generated.g.fs"
          projectFile "View.Designer.fs"
          projectFile "tests/LibraryTests.fs"
          { projectFile "Linked.fs" with
              Link = Some "../Shared/Linked.fs" } ]

    let filtered = filterProjectFiles workspaceRoot (defaultFilterOptions Outline) files

    Assert.Equal<string>([ "Library.fs" ], filtered.Included |> List.map _.IncludePath)
    Assert.Contains(ObjOrBinDirectory, excludedReasons filtered)
    Assert.Contains(GeneratedFile, excludedReasons filtered)
    Assert.Contains(DesignerFile, excludedReasons filtered)
    Assert.Contains(TestResultArtifact, excludedReasons filtered)
    Assert.Contains(ExternalLinkedFile, excludedReasons filtered)

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
