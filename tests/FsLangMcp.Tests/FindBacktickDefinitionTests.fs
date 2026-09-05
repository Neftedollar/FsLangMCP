module FsLangMcp.Tests.FindBacktickDefinitionTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open FsLangMcp.FcsBridge
open FsLangMcp.Types
open Xunit

let private writeLookupProject () =
    let root =
        Path.Combine(Path.GetTempPath(), $"fslangmcp_find_backtick_%O{Guid.NewGuid()}")

    Directory.CreateDirectory(root) |> ignore

    let sourcePath = Path.Combine(root, "Library.fs")

    File.WriteAllText(
        sourcePath,
        """module BacktickLookupFixture

let ordinaryName = 1
let ``value with spaces + punctuation!`` = ordinaryName + 1

module First =
    let ``ambiguous value!`` = 1

module Second =
    let ``ambiguous value!`` = 2
"""
    )

    let projectPath = Path.Combine(root, "BacktickLookupFixture.fsproj")

    File.WriteAllText(
        projectPath,
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Library.fs" />
  </ItemGroup>
</Project>
"""
    )

    root, sourcePath, projectPath

let private findDefinitionWithExact (bridge: FcsBridge) projectPath exact query =
    bridge.Find(
        { query = query
          kind = Some "definition"
          scope = Some "project"
          exact = Some exact
          ``member`` = None
          field = None
          path = None
          line = None
          word = None
          occurrence = None
          character = None
          contextLines = Some 0
          includeDeclaration = None
          includeInfo = Some false
          includePerProject = Some false
          includeSiteTypes = None
          projectPath = Some projectPath
          maxResults = Some 20
          timeoutMs = Some 60_000
          cursor = None }
    )

let private findDefinition bridge projectPath query =
    findDefinitionWithExact bridge projectPath true query

let private definitionSites (result: JsonNode) =
    result["sites"] :?> JsonArray

let private assertDefinitionCount expected (result: JsonNode) =
    Assert.Equal("succeeded", result["status"].GetValue<string>())
    Assert.Equal(expected, (result["breakdown"]["definitions"]).GetValue<int>())
    Assert.Equal(expected, definitionSites result |> Seq.length)

[<Fact>]
let ``find definition resolves quoted and unquoted FSharp source names exactly`` () : Task =
    task {
        let root, sourcePath, projectPath = writeLookupProject ()

        try
            let bridge = FcsBridge()

            let! unquotedBacktick =
                findDefinition bridge projectPath "value with spaces + punctuation!"

            let! quotedBacktick =
                findDefinition bridge projectPath "``value with spaces + punctuation!``"

            let! ordinary = findDefinition bridge projectPath "ordinaryName"
            let! quotedOrdinary = findDefinition bridge projectPath "``ordinaryName``"
            let! ambiguous = findDefinition bridge projectPath "ambiguous value!"

            let! qualifiedUnquoted =
                findDefinition bridge projectPath "First.ambiguous value!"

            let! qualifiedQuoted =
                findDefinition bridge projectPath "First.``ambiguous value!``"

            let! caseInsensitiveUnquoted =
                findDefinitionWithExact bridge projectPath false "VALUE WITH SPACES + PUNCTUATION!"

            let! genuineMiss =
                findDefinition bridge projectPath "value with spaces + punctuation?"

            assertDefinitionCount 1 unquotedBacktick
            assertDefinitionCount 1 quotedBacktick
            assertDefinitionCount 1 ordinary
            assertDefinitionCount 1 quotedOrdinary
            assertDefinitionCount 2 ambiguous
            assertDefinitionCount 1 qualifiedUnquoted
            assertDefinitionCount 1 qualifiedQuoted
            assertDefinitionCount 1 caseInsensitiveUnquoted
            assertDefinitionCount 0 genuineMiss
            Assert.Equal("not_found", genuineMiss["outcome"].GetValue<string>())

            for result in
                [ unquotedBacktick
                  quotedBacktick
                  ordinary
                  quotedOrdinary
                  ambiguous
                  qualifiedUnquoted
                  qualifiedQuoted
                  caseInsensitiveUnquoted ] do
                for site in definitionSites result do
                    Assert.Equal(Path.GetFullPath(sourcePath), site["file"].GetValue<string>())
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }
