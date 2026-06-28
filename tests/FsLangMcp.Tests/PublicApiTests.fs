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
