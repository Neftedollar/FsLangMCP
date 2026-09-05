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
    let ordinaryValue = 11

module Second =
    let ``ambiguous value!`` = 2
    let ordinaryValue = 22

let ``value.with dots + punctuation!`` = 3

type LookupMethods() =
    member _.``method.with dots + punctuation!``() = 42
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
    Assert.True((result["coverage"]["complete"]).GetValue<bool>())
    Assert.True(result["resultSetComplete"].GetValue<bool>())
    Assert.True(result["projectDiagnosticsCountComplete"].GetValue<bool>())
    Assert.Equal(0, result["projectDiagnosticsTotalCount"].GetValue<int>())
    Assert.Equal(expected, (result["resolution"]["fcsSiteCount"]).GetValue<int>())
    Assert.Equal(expected, (result["breakdown"]["definitions"]).GetValue<int>())
    Assert.Equal(expected, definitionSites result |> Seq.length)

// The expected identities and coordinates below are fixed by the fixture declarations,
// independently of the compiler results and of find's name-matching implementation.
let private assertDefinitions sourcePath expected (result: JsonNode) =
    assertDefinitionCount (List.length expected) result

    let actual =
        definitionSites result
        |> Seq.map (fun site ->
            Assert.Equal(Path.GetFullPath(sourcePath), site["file"].GetValue<string>())
            Assert.Equal("definition", site["kind"].GetValue<string>())
            let range = site["range"]

            site["symbolFullName"].GetValue<string>(),
            range["startLine"].GetValue<int>(),
            range["startColumn"].GetValue<int>(),
            range["endLine"].GetValue<int>(),
            range["endColumn"].GetValue<int>())
        |> Seq.toArray

    Assert.Equal<string * int * int * int * int>(List.toArray expected, actual)

let private firstDefinition =
    "BacktickLookupFixture.First.``ambiguous value!``", 7, 8, 7, 28

let private secondDefinition =
    "BacktickLookupFixture.Second.``ambiguous value!``", 11, 8, 11, 28

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
            let! genuineMiss =
                findDefinition bridge projectPath "value with spaces + punctuation?"

            assertDefinitionCount 1 unquotedBacktick
            assertDefinitionCount 1 quotedBacktick
            assertDefinitionCount 1 ordinary
            assertDefinitionCount 1 quotedOrdinary
            assertDefinitionCount 0 genuineMiss
            Assert.Equal("not_found", genuineMiss["outcome"].GetValue<string>())

            for result in [ unquotedBacktick; quotedBacktick; ordinary; quotedOrdinary ] do
                for site in definitionSites result do
                    Assert.Equal(Path.GetFullPath(sourcePath), site["file"].GetValue<string>())

            for query, expected in
                [ "ambiguous value!", [ firstDefinition; secondDefinition ]
                  "``ambiguous value!``", [ firstDefinition; secondDefinition ]
                  "First.ambiguous value!", [ firstDefinition ]
                  "First.``ambiguous value!``", [ firstDefinition ]
                  "Second.ambiguous value!", [ secondDefinition ]
                  "Second.``ambiguous value!``", [ secondDefinition ]
                  "BacktickLookupFixture.First.ambiguous value!", [ firstDefinition ]
                  "BacktickLookupFixture.Second.``ambiguous value!``", [ secondDefinition ] ] do
                let! result = findDefinition bridge projectPath query
                assertDefinitions sourcePath expected result
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Theory>]
[<InlineData("fIrSt.AMBIGUOUS VALUE!", "BacktickLookupFixture.First.``ambiguous value!``", 7, 28)>]
[<InlineData("fIrSt.``ORDINARYVALUE``", "BacktickLookupFixture.First.ordinaryValue", 8, 21)>]
let ``find non-exact qualified source-name equivalence is case insensitive``
    (query: string, fullName: string, line: int, endColumn: int)
    : Task =
    task {
        let root, sourcePath, projectPath = writeLookupProject ()

        try
            let bridge = FcsBridge()
            // Neither mixed-case query is a substring of the FCS display/full name
            // on v0.17.1: these require the new source-name equivalence branch.
            let! result = findDefinitionWithExact bridge projectPath false query
            assertDefinitions sourcePath [ fullName, line, 8, line, endColumn ] result
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Theory>]
[<InlineData("value.with dots + punctuation!", "BacktickLookupFixture", 14, 4, 38)>]
[<InlineData("method.with dots + punctuation!", "BacktickLookupFixture.LookupMethods", 17, 13, 48)>]
let ``find definitions preserve dots and punctuation inside value and method names``
    (name: string, qualifier: string, line: int, startColumn: int, endColumn: int)
    : Task =
    task {
        let root, sourcePath, projectPath = writeLookupProject ()

        try
            let bridge = FcsBridge()
            let fullName = $"{qualifier}.``{name}``"
            let expected = [ fullName, line, startColumn, line, endColumn ]

            for query in [ name; $"``{name}``"; $"{qualifier}.{name}"; fullName ] do
                let! result = findDefinition bridge projectPath query
                assertDefinitions sourcePath expected result
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``find source-name equivalence rejects wrong and truncated qualifiers`` () : Task =
    task {
        let root, sourcePath, projectPath = writeLookupProject ()

        try
            let bridge = FcsBridge()

            for exact, query in
                [ true, "Missing.ambiguous value!"
                  true, "irst.ambiguous value!"
                  true, "NotFirst.``ambiguous value!``"
                  true, "First.ambiguous value?"
                  true, "Missing.value.with dots + punctuation!"
                  true, "with dots + punctuation!"
                  true, "Methods.method.with dots + punctuation!"
                  true, "Missing.LookupMethods.``method.with dots + punctuation!``"
                  true, "LookupMethods.method.with dots + punctuation?"
                  false, "iRsT.AMBIGUOUS VALUE!"
                  false, "MISSING.``ORDINARYVALUE``" ] do
                let! result = findDefinitionWithExact bridge projectPath exact query
                assertDefinitions sourcePath [] result
                Assert.Equal("not_found", result["outcome"].GetValue<string>())
                Assert.False((result["resolution"]["matched"]).GetValue<bool>())
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }
