module FsLangMcp.Tests.NugetClassFieldTests

// ─── Codex P2 #4: fcs_nuget_members must enumerate reference-type CLASS fields ───
//
// Public `val` fields on a reference-type class live ONLY in FSharpFields — they are
// not in MembersFunctionsAndValues — so the old guard `IsFSharpRecord || IsValueType`
// silently dropped them. The fix enumerates classes too, while filtering
// compiler-generated backing fields so an auto-property (`member val`) is not duplicated
// by its hidden `Name@` field.
//
// Empirically (verified while building this fix): FCS surfaces F# `val` class fields via
// FSharpFields for an imported F# assembly, but returns an EMPTY FSharpFields for an
// imported C#/IL class — so imported C# instance fields remain unrecoverable through the
// symbol surface (an FCS limitation, documented in tools-detailed.md caveat #4). This
// test therefore exercises the recoverable F# case.
//
// Fixture: an F# library exposing a class with a public `val` field, an auto-property
// (`member val` → a real `Name@` backing field), consumed by a trivial F# project so
// Ionide.ProjInfo resolves it and FCS imports Widget from metadata.

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── Fixture sources ────────────────────────────────────────────────────────────

let private widgetFs =
    String.concat
        "\n"
        [ "namespace FieldProbe"
          "type Widget(initial: int) ="
          "    [<DefaultValue>] val mutable PublicCounter : int"
          "    member val Name : string = \"\" with get, set"
          "    member _.Compute () = initial + 1"
          "" ]

let private fieldProbeFsproj =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          "  <ItemGroup><Compile Include=\"Widget.fs\" /></ItemGroup>"
          "</Project>" ]

let private consumerFsproj =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          "  <ItemGroup><Compile Include=\"Consumer.fs\" /></ItemGroup>"
          "  <ItemGroup><ProjectReference Include=\"../FieldProbe/FieldProbe.fsproj\" /></ItemGroup>"
          "</Project>" ]

let private consumerFs =
    String.concat "\n" [ "module Consumer.Main"; ""; "let private _widget = FieldProbe.Widget(0)"; "" ]

// ── Class fixture: written + built ONCE ──────────────────────────────────────────

type FieldProbeFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_fieldprobe_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    do write "FieldProbe/FieldProbe.fsproj" fieldProbeFsproj |> ignore
    do write "FieldProbe/Widget.fs" widgetFs |> ignore
    let consumer = write "Consumer/Consumer.fsproj" consumerFsproj
    do write "Consumer/Consumer.fs" consumerFs |> ignore

    // Build the F# consumer (pulls FieldProbe via P2P). Isolation/retry flags mirror the
    // Find/Check fixtures to survive parallel-collection MSBuild contention.
    let buildOnce () =
        let psi =
            ProcessStartInfo(
                "dotnet",
                $"build \"{consumer}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
            )

        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] <- "1"
        use p = Process.Start(psi)
        let stdout = p.StandardOutput.ReadToEnd()
        let stderr = p.StandardError.ReadToEnd()
        p.WaitForExit()
        p.ExitCode, stdout + stderr

    let rec buildWithRetry attempt =
        let code, log = buildOnce ()

        if code = 0 || attempt >= 3 then
            code, log
        else
            System.Threading.Thread.Sleep(1500)
            buildWithRetry (attempt + 1)

    let buildExit, buildLog = buildWithRetry 1

    member _.Root = root
    member _.ConsumerFsproj = consumer
    member _.BuildExitCode = buildExit
    member _.BuildLog = buildLog

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ── Helpers ──────────────────────────────────────────────────────────────────────

let private entries (result: JsonNode) =
    match result["results"] with
    | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toList
    | _ -> []

let private names (result: JsonNode) =
    entries result |> List.map (fun e -> e["name"].GetValue<string>())

let private kindOf (result: JsonNode) (name: string) =
    entries result
    |> List.tryPick (fun e ->
        if e["name"].GetValue<string>() = name then
            Some(e["kind"].GetValue<string>())
        else
            None)

let private isBackingFieldName (n: string) = n.Contains "k__BackingField" || n.Contains "@"

let private nugetArgs (projectPath: string) (includeNonPublic: bool) : FcsNugetMembersArgs =
    { packageId = "FieldProbe"
      typeName = "Widget"
      projectPath = Some projectPath
      includeNonPublic = Some includeNonPublic
      maxResults = Some 500
      cursor = None }

// ─────────────────────────────────────────────────────────────────────────────────

type NugetClassFieldTests(fx: FieldProbeFixture) =
    interface IClassFixture<FieldProbeFixture>

    [<Fact>]
    member _.``NugetMembers lists a public class val field and excludes compiler-generated backing fields``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // ── Default (public-only) ────────────────────────────────────────────
            let! result = bridge.NugetMembers(nugetArgs fx.ConsumerFsproj false)

            Assert.Equal("ok", result["status"].GetValue<string>())
            Assert.True((result["matchedTypes"] :?> JsonArray).Count > 0, "Widget must match")

            let publicNames = names result

            // INCLUSION: the public class `val` field is now enumerated, as a field — the
            // old IsFSharpRecord||IsValueType guard dropped it.
            Assert.Contains("PublicCounter", publicNames)
            Assert.Equal(Some "field", kindOf result "PublicCounter")

            // The auto-property is still surfaced as a property…
            Assert.Equal(Some "property", kindOf result "Name")

            // …and is NOT duplicated by its compiler-generated `Name@` backing field.
            Assert.DoesNotContain(publicNames, isBackingFieldName)

            // PublicCounter is the only field-kind row (no backing-field rows leak in).
            let fieldRows =
                entries result
                |> List.filter (fun e -> e["kind"].GetValue<string>() = "field")
                |> List.map (fun e -> e["name"].GetValue<string>())

            Assert.Equal<string list>([ "PublicCounter" ], fieldRows)

            // ── includeNonPublic=true: backing fields STILL filtered ─────────────
            // FSharpFields holds the private, compiler-generated backing fields (Name@,
            // ctor captures). includeNonPublic bypasses the accessibility filter, so only
            // the explicit compiler-generated filter keeps them out — assert it does.
            let! all = bridge.NugetMembers(nugetArgs fx.ConsumerFsproj true)
            let allNames = names all

            Assert.Contains("PublicCounter", allNames)
            Assert.DoesNotContain(allNames, isBackingFieldName)
        }
