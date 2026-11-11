module FsLangMcp.Tests.ScriptCheckTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge

let private args: CheckArgs =
    { scope = Some "script"; path = None; snippet = None; snippetPosition = None
      fileGlob = None; mode = None; speed = None; severity = Some "all"
      compact = None; maxDiagnostics = None; projectPath = None; timeoutMs = Some 30000 }

let private text (node: JsonNode) (key: string) = node[key].GetValue<string>()
let private integer (node: JsonNode) (key: string) = node[key].GetValue<int>()

let private withScript (run: string -> Task) : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_script_{Guid.NewGuid():N}")
        Directory.CreateDirectory(Path.Combine(root, "scripts")) |> ignore
        try
            do! run root
        finally
            Directory.Delete(root, true)
    }

[<Fact>]
let ``standalone script uses SDK references and relative load without executing top level code`` () : Task =
    withScript (fun root -> task {
        let loaded = Path.Combine(root, "Lib.fs")
        File.WriteAllText(loaded, "module Lib\nlet one = 1\n")
        let path = Path.Combine(root, "scripts", "script.fsx")
        let marker = Path.Combine(root, "executed")
        File.WriteAllText(path, $"#load \"../Lib.fs\"\nlet n: int = Lib.one\nlet arguments: string array = fsi.CommandLineArgs\nfsi.ShowDeclarationValues <- false\nSystem.IO.File.WriteAllText(@\"{marker}\", \"ran\")\n")
        let bridge = FcsBridge()
        for scope in [Some "script"; Some "file"; None] do
            let! result = bridge.Check({args with scope = scope; path = Some path; projectPath = Some "/absent/unrelated.fsproj"})
            Assert.Equal("clean", text result "verdict")
            Assert.Equal("script", text result "scope")
            Assert.Equal("fsx", text result "mode")
            Assert.False(result["evaluated"].GetValue<bool>())
            Assert.False(File.Exists(marker), "A check must never run script top-level effects.")
            let sources = result["sourceFiles"] :?> JsonArray |> Seq.map (fun n -> n.GetValue<string>())
            Assert.Contains(loaded, sources)
        // Same bridge sees edits in a loaded file, rather than reusing a false-clean cache.
        File.WriteAllText(loaded, "module Lib\nlet one = \"no\"\n")
        let! changed = bridge.Check({args with path = Some path})
        Assert.Equal("errors", text changed "verdict")
        Assert.True(integer changed "errorCount" > 0)
    })

[<Fact>]
let ``missing load and syntax failures are compiler errors not clean`` () : Task =
    withScript (fun root -> task {
        let path = Path.Combine(root, "scripts", "broken.fsx")
        let bridge = FcsBridge()
        for source in ["#load \"missing.fs\"\nlet value = 1\n"; "let = \n"] do
            File.WriteAllText(path, source)
            let! result = bridge.Check({args with path = Some path})
            Assert.Equal("errors", text result "verdict")
            Assert.True(integer result "errorCount" > 0)
    })

[<Fact>]
let ``script argument validation and zero deadline never claim clean`` () : Task =
    withScript (fun root -> task {
        let path = Path.Combine(root, "scripts", "valid.fsx")
        File.WriteAllText(path, "let value = 42\n")
        let bridge = FcsBridge()
        for request in [args; {args with path = Some "source.fs"}; {args with path = Some path; speed = Some "fast"}] do
            let! result = bridge.Check(request)
            Assert.Equal("invalid_args", text result "status")
        let! timedOut = bridge.Check({args with path = Some path; timeoutMs = Some 0})
        Assert.Equal("unknown", text timedOut "verdict")
        Assert.Equal("timeout", text timedOut["blockingReason"] "errorKind")
        let! retry = bridge.Check({args with path = Some path})
        Assert.Equal("clean", text retry "verdict")
    })

[<Fact>]
let ``unavailable exact script SDK pin is unknown and recovers after the pin is removed`` () : Task =
    withScript (fun root -> task {
        let pin = Path.Combine(root, "global.json")
        File.WriteAllText(pin, "{\"sdk\":{\"version\":\"99.0.100\",\"rollForward\":\"disable\"}}")
        let path = Path.Combine(root, "scripts", "sdk.fsx")
        File.WriteAllText(path, "let value = 42\n")
        let bridge = FcsBridge()
        let! blocked = bridge.Check({args with path = Some path})
        Assert.Equal("unknown", text blocked "verdict")
        Assert.False(blocked["analyzed"].GetValue<bool>())
        Assert.False(blocked["groundTruth"].GetValue<bool>())
        let reason = blocked["blockingReason"]
        Assert.Equal("sdk_not_found", text reason "errorKind")
        Assert.Equal("99.0.100", text reason "requestedSdkVersion")
        Assert.Equal(pin, text reason "globalJsonPath")
        Assert.NotEmpty(reason["installedSdks"].AsArray())
        File.Delete(pin)
        let! recovered = bridge.Check({args with path = Some path})
        Assert.Equal("clean", text recovered "verdict")
        Assert.True(recovered["groundTruth"].GetValue<bool>())
    })

[<Fact>]
let ``unresolved script SDK with default roll forward is not certified against fallback references`` () : Task =
    withScript (fun root -> task {
        let pin = Path.Combine(root, "global.json")
        File.WriteAllText(pin, "{\"sdk\":{\"version\":\"99.0.100\"}}")
        let path = Path.Combine(root, "scripts", "sdk.fsx")
        File.WriteAllText(path, "let value = 42\n")
        let bridge = FcsBridge()
        let! result = bridge.Check({args with path = Some path})
        Assert.Equal("unknown", text result "verdict")
        Assert.False(result["groundTruth"].GetValue<bool>())
        let reason = result["blockingReason"]
        Assert.Equal("script_sdk_unavailable", text reason "errorKind")
        Assert.Contains("99.0.100", text reason "message")
        File.Delete(pin)
        let! recovered = bridge.Check({args with path = Some path})
        Assert.Equal("clean", text recovered "verdict")
        Assert.True(recovered["groundTruth"].GetValue<bool>())
    })

[<Fact>]
let ``script constructor mismatch identifies the selected type and preserves grouping`` () : Task =
    withScript (fun root -> task {
        let path = Path.Combine(root, "collision.fsx")
        File.WriteAllText(path, "module Persistent =\n    type HashSet<'T>(items: seq<'T>) =\n        member _.Items = items\nopen System.Collections.Generic\nopen Persistent\nlet a = HashSet<int>(EqualityComparer<int>.Default)\nlet b = HashSet<int>(EqualityComparer<int>.Default)\n")
        let! result = FcsBridge().Check({args with path = Some path; compact = Some true})
        Assert.Equal("errors", text result "verdict")
        let errors = result["diagnostics"].AsArray() |> Seq.filter (fun d -> text d "severity" = "Error") |> Seq.toArray
        // FCS reports two distinct constraint explanations per call. Keep those
        // separate while grouping each explanation's repeated occurrences.
        Assert.Equal(2, errors.Length)
        Assert.All(errors, fun diagnostic ->
            Assert.Equal("Collision.Persistent.HashSet`1", text diagnostic "resolvedType")
            Assert.Equal(2, integer diagnostic "count")
            Assert.NotNull(diagnostic["resolvedConstructorRange"]))
    })

[<Fact>]
let ``compact script check does not combine distinct obsolete messages with trailing whitespace`` () : Task =
    withScript (fun root -> task {
        let path = Path.Combine(root, "obsolete.fsx")
        File.WriteAllText(path, "[<System.Obsolete(\"deprecated  \")>]\nlet oldA = 1\n[<System.Obsolete(\"deprecated\")>]\nlet oldB = 2\nlet a = oldA + oldA\nlet b = oldB + oldB\n")
        let bridge = FcsBridge()
        let! full = bridge.Check({args with path = Some path})
        let! compact = bridge.Check({args with path = Some path; compact = Some true})
        let obsolete (result: JsonNode) =
            result["diagnostics"].AsArray() |> Seq.filter (fun d -> text d "errorNumberText" = "FS0044") |> Seq.toArray
        let fullWarnings = obsolete full
        let groups = obsolete compact
        Assert.Equal(4, fullWarnings.Length)
        Assert.Equal(2, groups.Length)
        Assert.All(groups, fun d -> Assert.Equal(2, integer d "count"))
        let messages nodes = nodes |> Array.map (fun d -> text d "message") |> Set.ofArray
        Assert.Equal<Set<string>>(messages fullWarnings, messages groups)
    })

[<Fact>]
let ``script resolves a NuGet reference from its local feed without executing it`` () : Task =
    withScript (fun root -> task {
        let feed = Path.Combine(root, "feed")
        Directory.CreateDirectory(feed) |> ignore
        let packageId = "FsLangMcp.ScriptCheck.Probe"
        let packagePath = Path.Combine(feed, packageId + ".1.0.0.nupkg")
        // A real, local NuGet package makes this regression independent of network
        // availability. The referenced assembly is NOT in normal script SDK refs.
        do
            use archive = System.IO.Compression.ZipFile.Open(packagePath, System.IO.Compression.ZipArchiveMode.Create)
            let manifest = archive.CreateEntry(packageId + ".nuspec")
            do
                use stream = manifest.Open()
                use writer = new StreamWriter(stream)
                writer.Write($"<?xml version=\"1.0\"?><package><metadata><id>{packageId}</id><version>1.0.0</version><authors>FsLangMcp tests</authors><description>Local script-check regression fixture</description></metadata></package>")
                writer.Flush()
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, typeof<Assert>.Assembly.Location, "lib/net10.0/xunit.assert.dll") |> ignore
        File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"<configuration><packageSources><clear/><add key=\"fixture\" value=\"{feed}\"/></packageSources><config><add key=\"globalPackagesFolder\" value=\"{root}/packages\"/></config></configuration>")
        let path = Path.Combine(root, "script.fsx")
        File.WriteAllText(path, $"#r \"nuget: {packageId}, 1.0.0\"\nXunit.Assert.True(false)\n")
        let! result = FcsBridge().Check({args with path = Some path; timeoutMs = Some 60000})
        Assert.Equal("clean", text result "verdict")
        Assert.Equal(0, integer result "errorCount")
        Assert.False(result["evaluated"].GetValue<bool>())
    })


[<Fact>]
let ``timed out script worker retains admission until actual completion`` () : Task =
    task {
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let beforeCheck () : Task =
            entered.TrySetResult(()) |> ignore
            release.Task :> Task
        let runner = FsLangMcp.ScriptChecking.Runner(FSharp.Compiler.CodeAnalysis.FSharpChecker.Create(), beforeCheckOverride = beforeCheck)
        try
            let running = runner.Check("does-not-exist.fsx", TimeSpan.FromSeconds(1.0))
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
            let! timedOut = running.WaitAsync(TimeSpan.FromSeconds(10.0))
            match timedOut with
            | Error FsLangMcp.ScriptChecking.ScriptFailure.Timeout -> ()
            | other -> failwithf "Expected timeout, got %A" other
            Assert.True(runner.IsBusy)
            let! blocked = runner.Check("another.fsx", TimeSpan.FromSeconds(1.0))
            match blocked with
            | Error FsLangMcp.ScriptChecking.ScriptFailure.Busy -> ()
            | other -> failwithf "Expected retained admission, got %A" other
        finally
            release.TrySetResult(()) |> ignore
        let guard = System.Diagnostics.Stopwatch.StartNew()
        while runner.IsBusy && guard.Elapsed < TimeSpan.FromSeconds(10.0) do
            do! Task.Delay(10)
        Assert.False(runner.IsBusy, "The completed timed-out worker must release admission.")
    }
