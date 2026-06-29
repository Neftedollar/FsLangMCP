module FsLangMcp.Tests.SignatureStatusTests

/// End-to-end tests for FcsBridge.SignatureStatus (fcs_signature_status, issue #74).
///
/// These call the production SignatureStatus member against a real temp fsproj whose
/// source files include paired .fs/.fsi modules, so the impl-only type-check, the .fsi
/// signature parse, and the public-surface diff all run through the same path the MCP
/// tool uses.
///
/// Coverage:
///   * drift  — a public impl member absent from the .fsi shows up in missingFromSig
///   * no .fsi — hasSignatureFile=false and the would-be signature is listed
///   * clean  — a matching .fsi yields no missing / no stale entries
///   * stale  — a .fsi entry with no impl member shows up in staleInSig

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ─── Fixture: one temp project with paired .fs/.fsi modules, loaded once ──────────

let private driftFs =
    String.concat
        "\n"
        [ "module Probe.Drift"
          ""
          "let exported (x: int) = x + 1"
          ""
          "let alsoExported (x: int) = x - 1" ]

// Drift.fsi omits `alsoExported` — it is public in the impl but hidden from the surface.
let private driftFsi =
    String.concat
        "\n"
        [ "module Probe.Drift"
          ""
          "val exported: int -> int" ]

let private cleanFs =
    String.concat
        "\n"
        [ "module Probe.Clean"
          ""
          "let one (x: int) = x"
          ""
          "let two (x: int) = x" ]

// Clean.fsi lists exactly the public surface — no drift in either direction.
let private cleanFsi =
    String.concat
        "\n"
        [ "module Probe.Clean"
          ""
          "val one: int -> int"
          "val two: int -> int" ]

let private bareFs =
    String.concat
        "\n"
        [ "module Probe.Bare"
          ""
          "let bareFn (x: int) = x * 2" ]

let private staleFs =
    String.concat
        "\n"
        [ "module Probe.Stale"
          ""
          "let present (x: int) = x" ]

// Stale.fsi declares `ghost`, which has no matching impl member.
let private staleFsi =
    String.concat
        "\n"
        [ "module Probe.Stale"
          ""
          "val present: int -> int"
          "val ghost: int -> int" ]

let private fixtureFsproj =
    String.concat
        Environment.NewLine
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup>"
          "    <TargetFramework>net10.0</TargetFramework>"
          "  </PropertyGroup>"
          "  <ItemGroup>"
          "    <Compile Include=\"Drift.fsi\" />"
          "    <Compile Include=\"Drift.fs\" />"
          "    <Compile Include=\"Clean.fsi\" />"
          "    <Compile Include=\"Clean.fs\" />"
          "    <Compile Include=\"Bare.fs\" />"
          "    <Compile Include=\"Stale.fsi\" />"
          "    <Compile Include=\"Stale.fs\" />"
          "  </ItemGroup>"
          "</Project>" ]

type SignatureFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_sigstatus_{runId}")

    do Directory.CreateDirectory(root) |> ignore
    do File.WriteAllText(Path.Combine(root, "Drift.fs"), driftFs)
    do File.WriteAllText(Path.Combine(root, "Drift.fsi"), driftFsi)
    do File.WriteAllText(Path.Combine(root, "Clean.fs"), cleanFs)
    do File.WriteAllText(Path.Combine(root, "Clean.fsi"), cleanFsi)
    do File.WriteAllText(Path.Combine(root, "Bare.fs"), bareFs)
    do File.WriteAllText(Path.Combine(root, "Stale.fs"), staleFs)
    do File.WriteAllText(Path.Combine(root, "Stale.fsi"), staleFsi)
    let project = Path.Combine(root, "Surface.fsproj")
    do File.WriteAllText(project, fixtureFsproj)

    let bridge = FcsBridge()

    member _.Root = root
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

let private argsFor (project: string) (implFile: string) : FcsSignatureStatusArgs =
    { path = implFile
      projectPath = Some project
      text = None }

let private names (result: JsonNode) (key: string) : string list =
    match result[key] with
    | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.map (fun n -> n["name"].GetValue<string>()) |> Seq.toList
    | _ -> []

// ─────────────────────────────────────────────────────────────────────────────────

type SignatureStatusTests(fx: SignatureFixture) =
    interface IClassFixture<SignatureFixture>

    [<Fact>]
    member _.``drift surfaces an impl member missing from the .fsi`` () : Task =
        task {
            let implFile = Path.Combine(fx.Root, "Drift.fs")
            let! result = fx.Bridge.SignatureStatus(argsFor fx.Project implFile)

            Assert.Equal("drift", result["status"].GetValue<string>())
            Assert.True(result["hasSignatureFile"].GetValue<bool>())

            // The impl exposes both functions…
            let publicNames = names result "publicMembers"
            Assert.Contains("exported", publicNames)
            Assert.Contains("alsoExported", publicNames)

            // …but only `alsoExported` is missing from the signature.
            let missing = names result "missingFromSig"
            Assert.Contains("alsoExported", missing)
            Assert.DoesNotContain("exported", missing)

            // Nothing in the .fsi is stale for the drift fixture.
            Assert.Empty(names result "staleInSig")
        }

    [<Fact>]
    member _.``missingFromSig carries a val signature preview for the hidden member`` () : Task =
        task {
            let implFile = Path.Combine(fx.Root, "Drift.fs")
            let! result = fx.Bridge.SignatureStatus(argsFor fx.Project implFile)

            let preview =
                match result["missingFromSig"] with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.cast<JsonNode>
                    |> Seq.tryPick (fun n ->
                        if n["name"].GetValue<string>() = "alsoExported" then
                            Some(n["signaturePreview"].GetValue<string>())
                        else
                            None)
                | _ -> None

            Assert.True(preview.IsSome, "alsoExported must carry a signaturePreview")
            // The preview is a ready-to-paste `val` line.
            Assert.StartsWith("val alsoExported:", preview.Value)
        }

    [<Fact>]
    member _.``no signature file lists the would-be signature`` () : Task =
        task {
            let implFile = Path.Combine(fx.Root, "Bare.fs")
            let! result = fx.Bridge.SignatureStatus(argsFor fx.Project implFile)

            Assert.Equal("no_signature_file", result["status"].GetValue<string>())
            Assert.False(result["hasSignatureFile"].GetValue<bool>())
            Assert.True((result["sigPath"] = null), "sigPath must be null when no .fsi exists")

            // The would-be signature is the impl's whole public surface.
            let publicNames = names result "publicMembers"
            Assert.Contains("bareFn", publicNames)

            // With no .fsi, every public member is "missing" (i.e. the would-be signature).
            let missing = names result "missingFromSig"
            Assert.Contains("bareFn", missing)
            Assert.Empty(names result "staleInSig")
        }

    [<Fact>]
    member _.``a matching .fsi reports clean with no drift`` () : Task =
        task {
            let implFile = Path.Combine(fx.Root, "Clean.fs")
            let! result = fx.Bridge.SignatureStatus(argsFor fx.Project implFile)

            Assert.Equal("clean", result["status"].GetValue<string>())
            Assert.True(result["hasSignatureFile"].GetValue<bool>())
            Assert.Empty(names result "missingFromSig")
            Assert.Empty(names result "staleInSig")

            // Both members are part of the reported public surface.
            let publicNames = names result "publicMembers"
            Assert.Contains("one", publicNames)
            Assert.Contains("two", publicNames)
        }

    [<Fact>]
    member _.``a .fsi entry with no impl member surfaces in staleInSig`` () : Task =
        task {
            let implFile = Path.Combine(fx.Root, "Stale.fs")
            let! result = fx.Bridge.SignatureStatus(argsFor fx.Project implFile)

            Assert.Equal("drift", result["status"].GetValue<string>())

            let stale = names result "staleInSig"
            Assert.Contains("ghost", stale)

            // `present` exists in both impl and signature, so it is neither stale nor missing.
            Assert.DoesNotContain("present", stale)
            Assert.DoesNotContain("present", names result "missingFromSig")
        }

    [<Fact>]
    member _.``a .fsi input path is rejected as invalid`` () : Task =
        task {
            let sigFile = Path.Combine(fx.Root, "Drift.fsi")
            let! result = fx.Bridge.SignatureStatus(argsFor fx.Project sigFile)

            Assert.Equal("invalid_args", result["status"].GetValue<string>())
        }
