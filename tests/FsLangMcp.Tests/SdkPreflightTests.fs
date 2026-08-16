module FsLangMcp.Tests.SdkPreflightTests

// ─── #192: the unsatisfiable-global.json wall ─────────────────────────────────
//
// Reproduced mechanism (see the PR body): Ionide.ProjInfo's `Init.init` resolves the
// SDK by running `dotnet --version` in the TARGET project's directory, so it inherits
// that project's global.json. With `rollForward: "disable"` and the pinned version
// absent the muxer exits 155 and ProjInfo raises. fsautocomplete calls `Init.init` on
// its own startup path with nothing to catch it: the child dies before answering
// `initialize`, and all the host can report is StreamJsonRpc's "The JSON-RPC
// connection with the remote party was lost" — naming neither the SDK nor the file.
//
// The pre-flight is deliberately one-sided. Only the provable case (`disable` + an
// exact version confirmed absent) may hard-fail; everything else proceeds, because a
// false positive here would block a project that loads fine.

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsMcp.Core
open FsLangMcp
open FsLangMcp.Types
open FsLangMcp.Tools
open FsLangMcp.LspBridge
open FsLangMcp.FcsBridge

// A version no machine can have installed, so the "absent" side of every assertion
// below stays true regardless of the host's SDK set.
let private absentVersion = "999.999.999"

let private tempRoot (label: string) =
    let root =
        Path.Combine(Path.GetTempPath(), $"fslangmcp_sdk_{label}_{Guid.NewGuid():N}")

    Directory.CreateDirectory(root) |> ignore
    root

let private writeGlobalJson (directory: string) (content: string) =
    Directory.CreateDirectory(directory) |> ignore
    File.WriteAllText(Path.Combine(directory, "global.json"), content)

let private pin (version: string) (rollForward: string option) =
    match rollForward with
    | Some policy -> $$"""{ "sdk": { "version": "{{version}}", "rollForward": "{{policy}}" } }"""
    | None -> $$"""{ "sdk": { "version": "{{version}}" } }"""

let private writeMinimalProject (directory: string) =
    Directory.CreateDirectory(directory) |> ignore
    let projectPath = Path.Combine(directory, "Poison.fsproj")

    File.WriteAllText(
        projectPath,
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <Compile Include=\"Library.fs\" />\n  </ItemGroup>\n</Project>\n"
    )

    File.WriteAllText(Path.Combine(directory, "Library.fs"), "module Poison.Library\n\nlet answer = 42\n")
    projectPath

let private lists (sdks: string list) = fun () -> Some sdks
let private undeterminable: unit -> string list option = fun () -> None

// ─── Verdict: the provable failure ────────────────────────────────────────────

[<Fact>]
let ``rollForward disable pinning an absent SDK is unsatisfiable`` () =
    let root = tempRoot "absent"

    try
        writeGlobalJson root (pin absentVersion (Some "disable"))

        match SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) root with
        | SdkPreflight.SdkNotFound(found, installed) ->
            Assert.Equal(absentVersion, found.Version)
            Assert.Equal(Some "disable", found.RollForward)
            Assert.Equal(Path.Combine(root, "global.json"), found.GlobalJsonPath)
            Assert.Equal<string list>([ "10.0.400" ], installed)
        | SdkPreflight.Proceed -> Assert.Fail("Expected SdkNotFound for an absent exact pin.")
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``rollForward disable pinning an installed SDK proceeds`` () =
    let root = tempRoot "installed"

    try
        writeGlobalJson root (pin "10.0.400" (Some "disable"))
        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory (lists [ "9.0.100"; "10.0.400" ]) root)
    finally
        Directory.Delete(root, true)

// ─── Verdict: every "not provable" case must proceed ──────────────────────────

[<Theory>]
[<InlineData("latestPatch")>]
[<InlineData("latestFeature")>]
[<InlineData("latestMinor")>]
[<InlineData("latestMajor")>]
[<InlineData("feature")>]
[<InlineData("major")>]
let ``rollForward policies other than disable proceed even when the pin is absent`` (policy: string) =
    let root = tempRoot "policy"

    try
        writeGlobalJson root (pin absentVersion (Some policy))
        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) root)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``an absent rollForward proceeds even when the pin is absent`` () =
    let root = tempRoot "norollforward"

    try
        writeGlobalJson root (pin absentVersion None)
        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) root)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``no global.json anywhere above the directory proceeds`` () =
    let root = tempRoot "nofile"

    try
        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory (lists []) root)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``an sdk paths redirect defers to the real SDK resolver`` () =
    // `sdk.paths` points resolution at locations `dotnet --list-sdks` does not
    // enumerate, so absence from our list would not prove absence for the muxer.
    let root = tempRoot "sdkpaths"

    try
        writeGlobalJson
            root
            $$"""{ "sdk": { "version": "{{absentVersion}}", "rollForward": "disable", "paths": [ "$host$", ".dotnet" ] } }"""

        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) root)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a malformed global.json proceeds`` () =
    let root = tempRoot "malformed"

    try
        writeGlobalJson root "{ this is not json"
        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) root)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a global.json without an sdk version proceeds`` () =
    let root = tempRoot "nosdk"

    try
        writeGlobalJson root """{ "msbuild-sdks": { "Some.Sdk": "1.0.0" } }"""
        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) root)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``an undeterminable installed SDK set proceeds`` () =
    // Our own probe failing must never become the reason a project cannot load.
    let root = tempRoot "unknownsdks"

    try
        writeGlobalJson root (pin absentVersion (Some "disable"))
        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory undeterminable root)
    finally
        Directory.Delete(root, true)

// ─── global.json discovery ────────────────────────────────────────────────────

[<Fact>]
let ``the nearest global.json wins over an ancestor pin`` () =
    let root = tempRoot "nearest"
    let nested = Path.Combine(root, "src", "Project")

    try
        writeGlobalJson root (pin absentVersion (Some "disable"))
        writeGlobalJson nested (pin "10.0.400" (Some "disable"))

        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) nested)

        match SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) root with
        | SdkPreflight.SdkNotFound(found, _) -> Assert.Equal(absentVersion, found.Version)
        | SdkPreflight.Proceed -> Assert.Fail("The root's own pin should still be evaluated.")
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``an ancestor global.json applies to a nested project directory`` () =
    let root = tempRoot "ancestor"
    let nested = Path.Combine(root, "src", "Project")

    try
        writeGlobalJson root (pin absentVersion (Some "disable"))
        Directory.CreateDirectory(nested) |> ignore

        match SdkPreflight.verdictForDirectory (lists [ "10.0.400" ]) nested with
        | SdkPreflight.SdkNotFound(found, _) -> Assert.Equal(Path.Combine(root, "global.json"), found.GlobalJsonPath)
        | SdkPreflight.Proceed -> Assert.Fail("Expected the ancestor pin to apply.")
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``verdictFor reports the first provable failure across the candidate directories`` () =
    let clean = tempRoot "multiclean"
    let poisoned = tempRoot "multipoison"

    try
        writeGlobalJson poisoned (pin absentVersion (Some "disable"))

        match SdkPreflight.verdictFor (lists [ "10.0.400" ]) [ clean; poisoned ] with
        | SdkPreflight.SdkNotFound(found, _) -> Assert.Equal(absentVersion, found.Version)
        | SdkPreflight.Proceed -> Assert.Fail("Expected the poisoned directory to decide the verdict.")
    finally
        Directory.Delete(clean, true)
        Directory.Delete(poisoned, true)

// ─── SDK-list caching ─────────────────────────────────────────────────────────

[<Fact>]
let ``a stale cached SDK list can let a project through but never reject one`` () =
    // Someone who installs the missing SDK and retries must not have to restart the
    // MCP server before the cached enumeration notices.
    let root = tempRoot "cacherefresh"

    try
        writeGlobalJson root (pin "10.0.400" (Some "disable"))
        let mutable refreshes = 0

        let refreshed () =
            refreshes <- refreshes + 1
            Some [ "10.0.400" ]

        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.checkWith (lists [ "10.0.400" ]) refreshed [ root ])
        Assert.Equal(0, refreshes)

        // Stale cache says the pin is absent; the refreshed probe says otherwise.
        Assert.Equal(SdkPreflight.Proceed, SdkPreflight.checkWith (lists [ "9.0.100" ]) refreshed [ root ])
        Assert.Equal(1, refreshes)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a confirmed-absent SDK still rejects after the refresh`` () =
    let root = tempRoot "cachereject"

    try
        writeGlobalJson root (pin absentVersion (Some "disable"))

        match SdkPreflight.checkWith (lists [ "9.0.100" ]) (lists [ "10.0.400" ]) [ root ] with
        | SdkPreflight.SdkNotFound(_, installed) ->
            // The refreshed list is the one reported, not the stale one.
            Assert.Equal<string list>([ "10.0.400" ], installed)
        | SdkPreflight.Proceed -> Assert.Fail("Expected the refreshed probe to confirm the rejection.")
    finally
        Directory.Delete(root, true)

// ─── SDK enumeration ──────────────────────────────────────────────────────────

[<Fact>]
let ``dotnet --list-sdks output parses to bare version strings`` () =
    let stdout =
        "9.0.100 [/usr/local/share/dotnet/sdk]\r\n10.0.400 [/Users/dev/.dotnet/sdk]\n\n"

    Assert.Equal<string list>([ "9.0.100"; "10.0.400" ], SdkPreflight.parseInstalledSdks stdout)

[<Fact>]
let ``the test host's own SDK set is enumerable`` () =
    // Guards the integration tests below: they only prove anything while the real
    // `dotnet --list-sdks` probe answers.
    match SdkPreflight.enumerateInstalledSdks () with
    | Some sdks -> Assert.NotEmpty(sdks)
    | None -> Assert.Fail("Expected `dotnet --list-sdks` to answer inside `dotnet test`.")

// ─── Error envelope ───────────────────────────────────────────────────────────

[<Fact>]
let ``the sdk_not_found envelope names the version, the global.json and both remedies`` () =
    let pinned =
        { SdkPreflight.SdkPin.Version = absentVersion
          SdkPreflight.SdkPin.RollForward = Some "disable"
          SdkPreflight.SdkPin.GlobalJsonPath = "/repo/global.json" }

    let envelope = SdkPreflight.toEnvelope pinned [ "10.0.400" ]

    Assert.Equal("infrastructure_error", envelope["status"].GetValue<string>())
    Assert.Equal("sdk_not_found", envelope["errorKind"].GetValue<string>())
    Assert.Equal(absentVersion, envelope["requestedSdkVersion"].GetValue<string>())
    Assert.Equal("disable", envelope["rollForward"].GetValue<string>())
    Assert.Equal("/repo/global.json", envelope["globalJsonPath"].GetValue<string>())

    let installed =
        envelope["installedSdks"].AsArray()
        |> Seq.map (fun n -> n.GetValue<string>())
        |> List.ofSeq

    Assert.Equal<string list>([ "10.0.400" ], installed)

    let remedies =
        envelope["remedies"].AsArray()
        |> Seq.map (fun n -> n.GetValue<string>())
        |> List.ofSeq

    Assert.Equal(2, remedies.Length)
    Assert.Contains(remedies, fun remedy -> remedy.Contains("Install the .NET SDK " + absentVersion))
    Assert.Contains(remedies, fun remedy -> remedy.Contains("/repo/global.json"))

    let message = envelope["message"].GetValue<string>()
    Assert.Contains(absentVersion, message)
    Assert.Contains("/repo/global.json", message)
    Assert.Contains("10.0.400", message)

[<Fact>]
let ``toolResult renders SdkNotFoundException as the typed envelope rather than a transport error`` () : Task =
    task {
        let pinned =
            { SdkPreflight.SdkPin.Version = absentVersion
              SdkPreflight.SdkPin.RollForward = Some "disable"
              SdkPreflight.SdkPin.GlobalJsonPath = "/repo/global.json" }

        let work =
            task {
                raise (SdkPreflight.SdkNotFoundException(pinned, [ "10.0.400" ]))
                return (null: JsonNode)
            }

        let! result = toolResult work

        match result with
        | Ok [ Content.Text rendered ] ->
            Assert.Contains("\"errorKind\": \"sdk_not_found\"", rendered)
            Assert.Contains("\"status\": \"infrastructure_error\"", rendered)
            Assert.Contains(absentVersion, rendered)
        | Ok other -> Assert.Fail($"Expected a single text content, got: {other}")
        | Error e -> Assert.Fail($"Expected Ok envelope but got Error: {e}")
    }

[<Fact>]
let ``a wrapped SdkNotFoundException is still recognised and rendered`` () : Task =
    task {
        // The pre-flight raises inside the project-load task, so callers can observe
        // it nested behind an AggregateException or an InnerException.
        let pinned =
            { SdkPreflight.SdkPin.Version = absentVersion
              SdkPreflight.SdkPin.RollForward = Some "disable"
              SdkPreflight.SdkPin.GlobalJsonPath = "/repo/global.json" }

        let inner = SdkPreflight.SdkNotFoundException(pinned, [ "10.0.400" ])

        let wrapped =
            InvalidOperationException("project load failed", AggregateException(inner))

        match wrapped with
        | SdkPreflight.SdkPinUnsatisfiable failure -> Assert.Equal(absentVersion, failure.Pin.Version)
        | _ -> Assert.Fail("Expected the wrapper chain to be unwrapped.")

        let work =
            task {
                raise wrapped
                return (null: JsonNode)
            }

        let! result = toolResult work

        match result with
        | Ok [ Content.Text rendered ] -> Assert.Contains("\"errorKind\": \"sdk_not_found\"", rendered)
        | Ok other -> Assert.Fail($"Expected a single text content, got: {other}")
        | Error e -> Assert.Fail($"Expected Ok envelope but got Error: {e}")
    }

// ─── Integration: the MSBuild load path is never entered ──────────────────────

[<Fact>]
let ``a poisoned project never reaches the MSBuild load path`` () : Task =
    task {
        let root = tempRoot "fcsblocked"

        try
            writeGlobalJson root (pin absentVersion (Some "disable"))
            let projectPath = writeMinimalProject root

            let bridge = FcsBridge()
            let! probed = bridge.ProbeProjectOptions(projectPath)

            match probed with
            | Ok _ -> Assert.Fail("Expected the pre-flight to reject the poisoned project.")
            | Error message ->
                Assert.Contains(absentVersion, message)
                Assert.Contains("global.json", message)

            // The counter increments only after Init.init and WorkspaceLoader.Create
            // have both succeeded, so zero proves MSBuild was never reached.
            Assert.Equal(0L, bridge.ProjectOptionsLoadCount)
        finally
            Directory.Delete(root, true)
    }

[<Fact>]
let ``a satisfiable pin still reaches the MSBuild load path`` () : Task =
    task {
        let installedSdk =
            match SdkPreflight.enumerateInstalledSdks () with
            | Some(sdk :: _) -> sdk
            | _ -> failwith "Expected `dotnet --list-sdks` to report at least one SDK inside `dotnet test`."

        let root = tempRoot "fcsallowed"

        try
            // Same `rollForward: "disable"` shape as the poisoned case — only the
            // version differs — so this isolates the pre-flight's decision.
            writeGlobalJson root (pin installedSdk (Some "disable"))
            let projectPath = writeMinimalProject root

            let bridge = FcsBridge()
            // The load itself may still fail (nothing is restored here); what matters
            // is that Init.init/WorkspaceLoader ran at all.
            let! _ = bridge.ProbeProjectOptions(projectPath)
            Assert.Equal(1L, bridge.ProjectOptionsLoadCount)
        finally
            Directory.Delete(root, true)
    }

[<Fact>]
let ``set_project returns sdk_not_found and never starts fsautocomplete`` () : Task =
    task {
        let root = tempRoot "setproject"

        try
            writeGlobalJson root (pin absentVersion (Some "disable"))
            let projectPath = writeMinimalProject root

            // A command that cannot exist: if the pre-flight ever regresses, the
            // start attempt throws instead of quietly passing this test.
            use bridge =
                new FsAutoCompleteBridge(fsacCommandOverride = $"missing-fsac-{Guid.NewGuid():N}")

            let! result =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            Assert.Equal("infrastructure_error", result["status"].GetValue<string>())
            Assert.Equal("sdk_not_found", result["errorKind"].GetValue<string>())
            Assert.Equal(absentVersion, result["requestedSdkVersion"].GetValue<string>())
            Assert.Equal(Path.Combine(root, "global.json"), result["globalJsonPath"].GetValue<string>())

            Assert.True(bridge.FsacProcess.IsNone, "The pre-flight must reject before any FSAC process exists.")
            Assert.Equal(0L, bridge.SessionGeneration)
            Assert.True(bridge.CurrentProjectPath.IsNone, "A rejected set_project must not change the active context.")
        finally
            Directory.Delete(root, true)
    }

[<Fact>]
let ``a pin poisoned after set_project is caught on the next lazy FSAC start`` () : Task =
    task {
        // The time-shift case: set_project screens a clean workspace, then a branch
        // switch adds a global.json (or the SDK is removed) and the NEXT LSP tool call
        // lazily starts FSAC. Without a pre-flight in StartLspUnsafe that start
        // reproduces the original opaque "connection lost".
        let root = tempRoot "lazystart"
        let sourcePath = Path.Combine(root, "Library.fs")

        try
            let projectPath = writeMinimalProject root

            // A command that cannot exist: if the pre-flight regresses, the spawn
            // fails with executable_missing instead, and this test fails loudly.
            use bridge =
                new FsAutoCompleteBridge(fsacCommandOverride = $"missing-fsac-{Guid.NewGuid():N}")

            let! selected =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some false }
                )

            Assert.Equal("ok", selected["status"].GetValue<string>())

            // Poison the workspace only AFTER the context is established.
            writeGlobalJson root (pin absentVersion (Some "disable"))

            let! result =
                bridge.RenamePreview(
                    { path = sourcePath
                      line = 2
                      character = 4
                      newName = "renamed"
                      text = None }
                )

            Assert.Equal("infrastructure_error", result["status"].GetValue<string>())
            Assert.Equal("sdk_not_found", result["errorKind"].GetValue<string>())
            Assert.Contains(absentVersion, result["message"].GetValue<string>())
            Assert.True(bridge.FsacProcess.IsNone, "No FSAC process may be spawned into a poisoned workspace.")
        finally
            Directory.Delete(root, true)
    }

[<Fact>]
let ``a solution member's own poisoned global.json is caught before fsautocomplete starts`` () : Task =
    task {
        // A nested global.json under one member project kills FSAC's startup exactly
        // like a root one, so set_project screens every project the solution lists.
        let root = tempRoot "solutionmember"
        let memberDirectory = Path.Combine(root, "src", "Member")

        try
            writeMinimalProject memberDirectory |> ignore
            writeGlobalJson memberDirectory (pin absentVersion (Some "disable"))
            let solutionPath = Path.Combine(root, "Poison.slnx")

            File.WriteAllText(
                solutionPath,
                "<Solution>\n  <Project Path=\"src/Member/Poison.fsproj\" />\n</Solution>\n"
            )

            use bridge =
                new FsAutoCompleteBridge(fsacCommandOverride = $"missing-fsac-{Guid.NewGuid():N}")

            let! result =
                bridge.SetProject(
                    { projectPath = solutionPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            Assert.Equal("sdk_not_found", result["errorKind"].GetValue<string>())

            Assert.Equal(Path.Combine(memberDirectory, "global.json"), result["globalJsonPath"].GetValue<string>())

            Assert.True(bridge.FsacProcess.IsNone, "The pre-flight must reject before any FSAC process exists.")
        finally
            Directory.Delete(root, true)
    }
