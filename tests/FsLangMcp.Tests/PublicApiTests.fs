module FsLangMcp.Tests.PublicApiTests

/// End-to-end tests for FcsBridge.PublicApi (fcs_public_api, issue #59).
///
/// These call the production PublicApi member against a real temp fsproj so the
/// AssemblySignature walk, accessibility filtering, stable sort, and pagination
/// all run through the same path the MCP tool uses.
///
/// Coverage:
///   * public type + member present; private type/member absent (default)
///   * includeInternal=true surfaces internal type + internal members; private stays hidden
///   * entities + members are stably sorted (deterministic snapshot order)
///   * namespaceFilter narrows the surface by FullName substring
///   * cursor pagination round-trip over the entity list

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Cursor

// ─── Fixture: one temp project, loaded once ──────────────────────────────────────

let private surfaceFs =
    String.concat
        "\n"
        [ "module Probe.Surface"
          ""
          "type PublicRecord = { Name: string; Age: int }"
          ""
          "type internal InternalRecord = { Secret: string }"
          ""
          "type private PrivateRecord = { Hidden: string }"
          ""
          "type Box<'T> = { Value: 'T }"
          ""
          "type GenericRecord ="
          "    { Text: string option"
          "      Count: int option"
          "      Names: string list"
          "      Boxed: Box<string> }"
          ""
          "type GenericMethods ="
          "    static member Convert(value: Box<int>) : Box<string> = { Value = string value.Value }"
          ""
          "module Acme ="
          "    module Microsoft ="
          "        module FSharp ="
          "            module Core ="
          "                type Widget<'T> = { Value: 'T }"
          ""
          "type QualifiedRecord = { Custom: Acme.Microsoft.FSharp.Core.Widget<string> }"
          ""
          "type Widget() ="
          "    member _.Visible () = 1"
          "    member internal _.HiddenMember () = 2"
          ""
          "module Calc ="
          "    let add (a: int) (b: int) = a + b"
          "    let internal helper (x: int) = x * 2"
          "    let private secret (x: int) = x - 1"
          "" ]

let private surfaceFsproj =
    String.concat
        Environment.NewLine
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup>"
          "    <TargetFramework>net10.0</TargetFramework>"
          "  </PropertyGroup>"
          "  <ItemGroup>"
          "    <Compile Include=\"Surface.fs\" />"
          "  </ItemGroup>"
          "</Project>" ]

type SurfaceFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_publicapi_{runId}")

    do Directory.CreateDirectory(root) |> ignore
    do File.WriteAllText(Path.Combine(root, "Surface.fs"), surfaceFs)
    let project = Path.Combine(root, "Surface.fsproj")
    do File.WriteAllText(project, surfaceFsproj)

    let bridge = FcsBridge()

    member _.Project = project
    member _.Source = Path.Combine(root, "Surface.fs")
    member internal _.Bridge = bridge

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ─── Helpers ─────────────────────────────────────────────────────────────────────

let private baseArgs (project: string) : FcsPublicApiArgs =
    { projectPath = Some project
      includeInternal = None
      namespaceFilter = None
      maxResults = None
      cursor = None }

let private entities (result: JsonNode) : JsonNode list =
    match result["entities"] with
    | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toList
    | _ -> []

let private entityFullNames (result: JsonNode) : string list =
    entities result |> List.map (fun e -> e["fullName"].GetValue<string>())

let private tryEntity (result: JsonNode) (fullName: string) : JsonNode option =
    entities result
    |> List.tryFind (fun e -> e["fullName"].GetValue<string>() = fullName)

let private memberNames (entity: JsonNode) : string list =
    match entity["members"] with
    | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.map (fun m -> m["name"].GetValue<string>()) |> Seq.toList
    | _ -> []

let private memberKind (entity: JsonNode) (name: string) : string option =
    match entity["members"] with
    | :? JsonArray as arr ->
        arr
        |> Seq.cast<JsonNode>
        |> Seq.tryPick (fun m ->
            if m["name"].GetValue<string>() = name then
                Some(m["kind"].GetValue<string>())
            else
                None)
    | _ -> None

let private memberSignature (entity: JsonNode) (name: string) : string =
    (entity["members"] :?> JsonArray)
    |> Seq.cast<JsonNode>
    |> Seq.find (fun m -> m["name"].GetValue<string>() = name)
    |> fun m -> m["signature"].GetValue<string>()

// ─────────────────────────────────────────────────────────────────────────────────

type PublicApiTests(fx: SurfaceFixture) =
    interface IClassFixture<SurfaceFixture>

    [<Fact>]
    member _.``default surface includes public type+member and excludes private and internal`` () : Task =
        task {
            let! result = fx.Bridge.PublicApi(baseArgs fx.Project)

            Assert.Equal("ok", result["status"].GetValue<string>())
            Assert.False(result["includeInternal"].GetValue<bool>())

            let names = entityFullNames result

            // Public entities are present…
            Assert.Contains("Probe.Surface.PublicRecord", names)
            Assert.Contains("Probe.Surface.Widget", names)
            Assert.Contains("Probe.Surface.Calc", names)

            // …private and internal types are not.
            Assert.DoesNotContain("Probe.Surface.PrivateRecord", names)
            Assert.DoesNotContain("Probe.Surface.InternalRecord", names)

            // PublicRecord exposes its fields.
            let pubRec = tryEntity result "Probe.Surface.PublicRecord"
            Assert.True(pubRec.IsSome, "PublicRecord must be present")
            let recMembers = memberNames pubRec.Value
            Assert.Contains("Name", recMembers)
            Assert.Contains("Age", recMembers)
            Assert.Equal(Some "field", memberKind pubRec.Value "Name")

            // Widget exposes its public method but not the internal one.
            let widget = tryEntity result "Probe.Surface.Widget"
            Assert.True(widget.IsSome, "Widget must be present")
            Assert.Contains("Visible", memberNames widget.Value)
            Assert.DoesNotContain("HiddenMember", memberNames widget.Value)

            // Calc exposes the public function but not internal/private ones.
            let calc = tryEntity result "Probe.Surface.Calc"
            Assert.True(calc.IsSome, "Calc must be present")
            Assert.Contains("add", memberNames calc.Value)
            Assert.DoesNotContain("helper", memberNames calc.Value)
            Assert.DoesNotContain("secret", memberNames calc.Value)
        }

    [<Fact>]
    member _.``includeInternal surfaces internal type and members but never private`` () : Task =
        task {
            let! result = fx.Bridge.PublicApi({ baseArgs fx.Project with includeInternal = Some true })

            Assert.Equal("ok", result["status"].GetValue<string>())
            Assert.True(result["includeInternal"].GetValue<bool>())

            let names = entityFullNames result

            // Internal type now surfaces; private is still hidden.
            Assert.Contains("Probe.Surface.InternalRecord", names)
            Assert.DoesNotContain("Probe.Surface.PrivateRecord", names)

            // Internal members now surface; private members never do.
            let widget = tryEntity result "Probe.Surface.Widget"
            Assert.True(widget.IsSome)
            Assert.Contains("HiddenMember", memberNames widget.Value)

            let calc = tryEntity result "Probe.Surface.Calc"
            Assert.True(calc.IsSome)
            Assert.Contains("add", memberNames calc.Value)
            Assert.Contains("helper", memberNames calc.Value)
            Assert.DoesNotContain("secret", memberNames calc.Value)
        }

    [<Fact>]
    member _.``generic signatures preserve concrete arguments in readable FSharp form`` () : Task =
        task {
            let! result = fx.Bridge.PublicApi(baseArgs fx.Project)

            let record = tryEntity result "Probe.Surface.GenericRecord"
            Assert.True(record.IsSome)
            Assert.Equal("Text: string option", memberSignature record.Value "Text")
            Assert.Equal("Count: int option", memberSignature record.Value "Count")
            Assert.Equal("Names: string list", memberSignature record.Value "Names")
            Assert.Equal("Boxed: Probe.Surface.Box<string>", memberSignature record.Value "Boxed")

            let methods = tryEntity result "Probe.Surface.GenericMethods"
            Assert.True(methods.IsSome)
            Assert.Equal(
                "Convert(value: Probe.Surface.Box<int>) -> Probe.Surface.Box<string>",
                memberSignature methods.Value "Convert"
            )

            let qualified = tryEntity result "Probe.Surface.QualifiedRecord"
            Assert.True(qualified.IsSome)
            Assert.Equal(
                "Custom: Probe.Surface.Acme.Microsoft.FSharp.Core.Widget<string>",
                memberSignature qualified.Value "Custom"
            )

            for signature in
                [ memberSignature record.Value "Text"
                  memberSignature record.Value "Count"
                  memberSignature record.Value "Names"
                  memberSignature record.Value "Boxed"
                  memberSignature methods.Value "Convert"
                  memberSignature qualified.Value "Custom" ] do
                Assert.DoesNotContain("`1", signature)
        }

    [<Fact>]
    member _.``entities and members are stably sorted`` () : Task =
        task {
            let! result = fx.Bridge.PublicApi({ baseArgs fx.Project with includeInternal = Some true })

            Assert.Equal("ok", result["status"].GetValue<string>())

            // Entities are emitted in ascending fullName order.
            let names = entityFullNames result
            Assert.Equal<string list>(List.sort names, names)

            // Members within every entity are emitted in ascending name order.
            for entity in entities result do
                let ms = memberNames entity
                Assert.Equal<string list>(List.sort ms, ms)
        }

    [<Fact>]
    member _.``namespaceFilter narrows the surface by FullName substring`` () : Task =
        task {
            let! result =
                fx.Bridge.PublicApi({ baseArgs fx.Project with namespaceFilter = Some "Calc" })

            Assert.Equal("ok", result["status"].GetValue<string>())
            Assert.Equal("Calc", result["namespaceFilter"].GetValue<string>())

            let names = entityFullNames result
            Assert.Contains("Probe.Surface.Calc", names)
            // Entities whose FullName lacks the substring are filtered out.
            Assert.DoesNotContain("Probe.Surface.Widget", names)
            Assert.DoesNotContain("Probe.Surface.PublicRecord", names)
        }

    [<Fact>]
    member _.``entityCount and memberCount report the whole surface, not just the page`` () : Task =
        task {
            let! result = fx.Bridge.PublicApi(baseArgs fx.Project)

            let entityCount = result["entityCount"].GetValue<int>()
            let memberCount = result["memberCount"].GetValue<int>()

            // At least PublicRecord, Widget, Calc.
            Assert.True(entityCount >= 3, $"expected >= 3 entities, got {entityCount}")
            // memberCount is the sum across the whole surface and must be positive.
            Assert.True(memberCount > 0, $"expected memberCount > 0, got {memberCount}")
        }

    [<Fact>]
    member _.``public API cache invalidates when a project source file changes`` () : Task =
        task {
            let original = File.ReadAllText(fx.Source)

            try
                let! before = fx.Bridge.PublicApi(baseArgs fx.Project)
                Assert.DoesNotContain("Probe.Surface.AddedAfterEdit", entityFullNames before)

                File.WriteAllText(fx.Source, original + "\ntype AddedAfterEdit = { Value: int }\n")
                File.SetLastWriteTimeUtc(fx.Source, DateTime.UtcNow.AddSeconds(2.0))

                let! after = fx.Bridge.PublicApi(baseArgs fx.Project)
                Assert.Contains("Probe.Surface.AddedAfterEdit", entityFullNames after)
            finally
                File.WriteAllText(fx.Source, original)
        }

    [<Fact>]
    member _.``public API reloads project options when the compile graph changes`` () : Task =
        task {
            let originalProject = File.ReadAllText(fx.Project)
            let addedFile = Path.Combine(Path.GetDirectoryName(fx.Project), "Added.fs")

            try
                let! before = fx.Bridge.PublicApi(baseArgs fx.Project)
                Assert.DoesNotContain("Probe.Added.AddedByCompileGraph", entityFullNames before)

                File.WriteAllText(
                    addedFile,
                    "module Probe.Added\n\ntype AddedByCompileGraph = { Value: int }\n"
                )

                let updatedProject =
                    originalProject.Replace(
                        "    <Compile Include=\"Surface.fs\" />",
                        "    <Compile Include=\"Surface.fs\" />"
                        + Environment.NewLine
                        + "    <Compile Include=\"Added.fs\" />"
                    )

                File.WriteAllText(fx.Project, updatedProject)
                File.SetLastWriteTimeUtc(fx.Project, DateTime.UtcNow.AddSeconds(2.0))

                let! after = fx.Bridge.PublicApi(baseArgs fx.Project)
                Assert.Contains("Probe.Added.AddedByCompileGraph", entityFullNames after)
            finally
                File.WriteAllText(fx.Project, originalProject)

                if File.Exists addedFile then
                    File.Delete addedFile
        }

    [<Fact>]
    member _.``project options reload when a wildcard gains a file in a new sibling directory (#163)``
        ()
        : Task =
        task {
            let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_wildcard_%O{Guid.NewGuid()}")
            let project = Path.Combine(root, "Wildcard.fsproj")
            let existing = Path.Combine(root, "src", "Existing", "Existing.fs")
            let added = Path.Combine(root, "src", "NewSibling", "Added.fs")

            try
                Directory.CreateDirectory(Path.GetDirectoryName existing) |> ignore

                File.WriteAllText(
                    project,
                    String.concat
                        Environment.NewLine
                        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                          "  <PropertyGroup>"
                          "    <TargetFramework>net10.0</TargetFramework>"
                          "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
                          "  </PropertyGroup>"
                          "  <ItemGroup><Compile Include=\"src/**/*.fs\" /></ItemGroup>"
                          "</Project>" ]
                )

                File.WriteAllText(existing, "module Wildcard.Existing\n\ntype Existing = { Value: int }\n")

                let bridge = FcsBridge()
                let! before = bridge.PublicApi(baseArgs project)
                Assert.Contains("Wildcard.Existing.Existing", entityFullNames before)
                Assert.DoesNotContain("Wildcard.Added.Added", entityFullNames before)
                let loadsBefore = bridge.ProjectOptionsLoadCount
                let reloadsBefore = bridge.ProjectOptionsStaleReloadCount

                // Do not touch the project file. Only a brand-new sibling directory
                // and source file appear beneath the wildcard search root.
                Directory.CreateDirectory(Path.GetDirectoryName added) |> ignore
                File.WriteAllText(added, "module Wildcard.Added\n\ntype Added = { Value: string }\n")

                let! after = bridge.PublicApi(baseArgs project)
                Assert.Contains("Wildcard.Added.Added", entityFullNames after)
                Assert.Equal(loadsBefore + 1L, bridge.ProjectOptionsLoadCount)
                Assert.Equal(reloadsBefore + 1L, bridge.ProjectOptionsStaleReloadCount)
            finally
                if Directory.Exists root then
                    Directory.Delete(root, true)
        }

    [<Fact>]
    member _.``project options stay warm across analysis clears and coalesce an input refresh`` () : Task =
        task {
            let bridge = FcsBridge()
            let projectDir = Path.GetDirectoryName(fx.Project)
            let directoryBuildProps = Path.Combine(projectDir, "Directory.Build.props")

            let assertLoaded = function
                | Ok _ -> ()
                | Error message -> Assert.Fail($"project-options probe failed: {message}")

            try
                let before = bridge.ProjectOptionsLoadCount
                let reloadsBefore = bridge.ProjectOptionsStaleReloadCount
                let! first = bridge.ProbeProjectOptions(fx.Project)
                assertLoaded first

                let afterFirst = bridge.ProjectOptionsLoadCount
                Assert.Equal(before + 1L, afterFirst)
                Assert.Equal(reloadsBefore, bridge.ProjectOptionsStaleReloadCount)

                // set_project uses this lighter clear: unchanged MSBuild inputs must not
                // invoke Ionide again (each real invocation owns MSBuild node threads).
                for _ in 1..8 do
                    bridge.ClearAnalysisCaches()
                    let! warm = bridge.ProbeProjectOptions(fx.Project)
                    assertLoaded warm

                Assert.Equal(afterFirst, bridge.ProjectOptionsLoadCount)
                Assert.Equal(reloadsBefore, bridge.ProjectOptionsStaleReloadCount)

                // A previously-missing ancestor import is part of the fingerprint. All
                // concurrent stale callers must share one refresh, then remain warm.
                File.WriteAllText(
                    directoryBuildProps,
                    "<Project><PropertyGroup><FsLangMcpCacheProbe>true</FsLangMcpCacheProbe></PropertyGroup></Project>"
                )

                let probes = Array.init 12 (fun _ -> bridge.ProbeProjectOptions(fx.Project))
                let! refreshed = Task.WhenAll(probes)
                refreshed |> Array.iter assertLoaded

                let afterRefresh = bridge.ProjectOptionsLoadCount
                Assert.Equal(afterFirst + 1L, afterRefresh)
                Assert.Equal(reloadsBefore + 1L, bridge.ProjectOptionsStaleReloadCount)

                // Fresh file checks invalidate semantic results, not project options.
                let checkArgs: FcsParseAndCheckArgs =
                    { path = fx.Source
                      text = None
                      projectPath = Some fx.Project
                      projectOptions = None }

                let! firstCheck = bridge.CheckFile(checkArgs)
                let! secondCheck = bridge.CheckFile(checkArgs)
                Assert.Equal("succeeded", firstCheck["status"].GetValue<string>())
                Assert.Equal("succeeded", secondCheck["status"].GetValue<string>())
                Assert.Equal(afterRefresh, bridge.ProjectOptionsLoadCount)
                Assert.Equal(reloadsBefore + 1L, bridge.ProjectOptionsStaleReloadCount)
            finally
                if File.Exists directoryBuildProps then
                    File.Delete directoryBuildProps
        }

    [<Fact>]
    member _.``cursor pagination returns a disjoint remainder covering the whole surface`` () : Task =
        task {
            // includeInternal widens the surface so there is something to page.
            let wide = { baseArgs fx.Project with includeInternal = Some true }

            let! full = fx.Bridge.PublicApi(wide)
            let allNames = entityFullNames full |> Set.ofList
            let total = full["entityCount"].GetValue<int>()
            Assert.True(total >= 2, $"need >= 2 entities to page; got {total}")

            // Page 1: a single entity, truncated, with a nextCursor.
            let! page1 = fx.Bridge.PublicApi({ wide with maxResults = Some 1 })
            Assert.True(page1["truncated"].GetValue<bool>(), "page 1 must be truncated")
            let cursorNode = page1["nextCursor"]
            Assert.NotNull(cursorNode)
            let cursor = cursorNode.GetValue<string>()

            match tryDecode cursor with
            | Error msg -> Assert.Fail($"nextCursor did not decode: {msg}")
            | Ok payload -> Assert.Equal(1, payload.offset)

            let page1Names = entityFullNames page1 |> Set.ofList
            Assert.Equal(1, page1Names.Count)

            // Walk the remaining pages and union the names.
            let mutable acc = page1Names
            let mutable nextCursor = Some cursor

            while nextCursor.IsSome do
                let! page = fx.Bridge.PublicApi({ wide with maxResults = Some 1; cursor = nextCursor })
                let pageNames = entityFullNames page |> Set.ofList
                // No page overlaps the names already seen.
                Assert.Empty(Set.intersect acc pageNames)
                acc <- Set.union acc pageNames

                nextCursor <-
                    match page["nextCursor"] with
                    | null -> None
                    | n -> Some(n.GetValue<string>())

            // The paged union reconstructs the full surface exactly.
            Assert.Equal<Set<string>>(allNames, acc)
        }
