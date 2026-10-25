module FsLangMcp.Tests.StartupTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.FcsBridge
open FsLangMcp.Program
open FsLangMcp.McpHost
open FsMcp.Core
open FsMcp.Server

let private executablePath () =
    Path.Combine(
        AppContext.BaseDirectory,
        if OperatingSystem.IsWindows() then "FsLangMcp.exe" else "FsLangMcp"
    )

let private startCaptured (arguments: string list) =
    let startInfo = ProcessStartInfo(executablePath ())
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.Environment.Remove("FSA_PROJECT_PATH") |> ignore

    for argument in arguments do
        startInfo.ArgumentList.Add(argument)

    Process.Start(startInfo)

[<Fact>]
let ``embedded runtime manifest is the exact bootstrap source of truth`` () =
    let pins = RuntimeToolManifest.loadPinnedRuntimeTools ()
    Assert.Equal(3, pins.Length)

    let byId = pins |> Seq.map (fun pin -> pin.PackageId, pin) |> Map.ofSeq
    Assert.Equal("7.0.5", byId["fantomas"].Version)
    Assert.Equal("0.83.0", byId["fsautocomplete"].Version)
    Assert.Equal("0.74.2", byId["ionide.projinfo.tool"].Version)

    for pin in pins do
        for verb in [ "install"; "update" ] do
            let args = RuntimeToolManifest.dotnetToolArgs verb pin
            Assert.Contains("--version", args)
            Assert.Contains(pin.Version, args)
            Assert.Contains("--allow-downgrade", args)

[<Fact>]
let ``version switch reports the runtime product version`` () =
    use child = startCaptured [ "--version" ]
    let stdout = child.StandardOutput.ReadToEnd()
    let stderr = child.StandardError.ReadToEnd()
    Assert.True(child.WaitForExit(5000), "--version did not terminate.")
    Assert.Equal(0, child.ExitCode)
    Assert.Equal(FsLangMcp.Version.current, stdout.Trim())
    Assert.True(String.IsNullOrWhiteSpace stderr, stderr)

[<Fact>]
let ``project switch fails before serving requests when preload target is invalid`` () =
    let missing = Path.Combine(Path.GetTempPath(), $"missing-project-{Guid.NewGuid():N}.fsproj")
    use child = startCaptured [ "--project"; missing ]
    let stderr = child.StandardError.ReadToEnd()
    Assert.True(child.WaitForExit(5000), "Invalid --project preload did not terminate.")
    Assert.Equal(1, child.ExitCode)
    Assert.Contains("projectPath does not exist", stderr)

[<Fact>]
let ``FSA_PROJECT_PATH uses the same fail-fast preload pipeline as --project`` () =
    let missing = Path.Combine(Path.GetTempPath(), $"missing-env-project-{Guid.NewGuid():N}.fsproj")
    let startInfo = ProcessStartInfo(executablePath ())
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.Environment["FSA_PROJECT_PATH"] <- missing

    use child = Process.Start(startInfo)
    let stderr = child.StandardError.ReadToEnd()
    Assert.True(child.WaitForExit(5000), "Invalid FSA_PROJECT_PATH preload did not terminate.")
    Assert.Equal(1, child.ExitCode)
    Assert.Contains("projectPath does not exist", stderr)

[<Fact>]
let ``stdio server answers MCP initialize without host file watching`` () =
    task {
        let startInfo = ProcessStartInfo(executablePath ())
        startInfo.WorkingDirectory <- Path.GetTempPath()
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.Environment.Remove("FSA_PROJECT_PATH") |> ignore
        startInfo.Environment.Remove("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE") |> ignore
        startInfo.Environment.Remove("DOTNET_USE_POLLING_FILE_WATCHER") |> ignore

        use server = Process.Start(startInfo)

        try
            let initialize =
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"startup-regression-test","version":"1.0"}}}"""

            do! server.StandardInput.WriteLineAsync(initialize)
            do! server.StandardInput.FlushAsync()

            let! response =
                server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10.0))

            Assert.False(String.IsNullOrWhiteSpace(response), "Server returned EOF before initialize response.")

            use document = JsonDocument.Parse(response)
            let root = document.RootElement
            Assert.Equal(1, root.GetProperty("id").GetInt32())

            let serverInfo = root.GetProperty("result").GetProperty("serverInfo")
            let serverName = serverInfo.GetProperty("name").GetString()
            let serverVersion = serverInfo.GetProperty("version").GetString()

            Assert.Equal("fsharp-fsautocomplete", serverName)
            Assert.Equal(FsLangMcp.Version.current, serverVersion)

            server.StandardInput.Close()
            Assert.True(server.WaitForExit(5000), "Server did not stop after stdin closed.")
        finally
            if not server.HasExited then
                server.Kill(true)
                server.WaitForExit()
    }

[<Fact>]
let ``FCS admission bounds queued requests and never starts them after cancellation`` () =
    task {
        use gate = new SemaphoreSlim(2, 2)
        use protectedCancellation = new CancellationTokenSource()
        use queuedCancellation = new CancellationTokenSource()
        let releaseProtected = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let protectedStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable protectedStarts = 0
        let mutable lateStarts = 0

        let protectedWork () =
            runLimited gate protectedCancellation.Token (fun () ->
                task {
                    if Interlocked.Increment(&protectedStarts) = 2 then
                        protectedStarted.TrySetResult(()) |> ignore

                    do! releaseProtected.Task
                    return JsonObject() :> JsonNode
                })

        let first = protectedWork ()
        let second = protectedWork ()
        do! protectedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0))

        // Once work has started, cancellation must not release its slots early.
        protectedCancellation.Cancel()

        let startLateWork () =
            Interlocked.Increment(&lateStarts) |> ignore
            Task.FromResult(JsonObject() :> JsonNode)

        let stopwatch = Stopwatch.StartNew()

        let deadlineRequest =
            runLimitedWithTimeout gate CancellationToken.None (Some 100) (fun _ -> startLateWork ())

        let cancelledRequest = runLimited gate queuedCancellation.Token startLateWork
        queuedCancellation.CancelAfter(50)

        let! deadlineResult = deadlineRequest
        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> cancelledRequest :> Task)

        stopwatch.Stop()
        Assert.Equal("timeout", deadlineResult["status"].GetValue<string>())
        Assert.Equal("fcs_admission_timeout", deadlineResult["errorKind"].GetValue<string>())
        Assert.Equal(100, deadlineResult["timeoutMs"].GetValue<int>())
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2.0), $"Queued requests took %O{stopwatch.Elapsed}.")
        Assert.Equal(0, Volatile.Read(&lateStarts))
        Assert.Equal(0, gate.CurrentCount)

        releaseProtected.TrySetResult(()) |> ignore
        let! _ = first
        and! _ = second

        do! Task.Delay(150)
        Assert.Equal(0, Volatile.Read(&lateStarts))
        Assert.Equal(2, gate.CurrentCount)
    }

[<Fact>]
let ``FCS admission subtracts queue time from the operation timeout`` () =
    task {
        use gate = new SemaphoreSlim(1, 1)
        do! gate.WaitAsync()

        let mutable remainingTimeoutMs = None

        let request =
            runLimitedWithTimeout gate CancellationToken.None (Some 2_000) (fun remaining ->
                remainingTimeoutMs <- remaining
                Task.FromResult(JsonObject() :> JsonNode))

        do! Task.Delay(100)
        gate.Release() |> ignore
        let! _ = request

        match remainingTimeoutMs with
        | Some remaining -> Assert.InRange(remaining, 1, 1_950)
        | None -> Assert.Fail("The admitted operation did not receive its remaining timeout.")
    }

[<Fact>]
let ``FCS admission rejects negative timeouts before waiting`` () =
    task {
        use gate = new SemaphoreSlim(0, 1)
        let mutable starts = 0

        for timeoutMs in [ -1; Int32.MinValue ] do
            let stopwatch = Stopwatch.StartNew()

            let! result =
                runLimitedWithTimeout gate CancellationToken.None (Some timeoutMs) (fun _ ->
                    Interlocked.Increment(&starts) |> ignore
                    Task.FromResult(JsonObject() :> JsonNode))

            stopwatch.Stop()
            Assert.Equal("invalid_args", result["status"].GetValue<string>())
            Assert.Equal("invalid_timeout", result["errorKind"].GetValue<string>())
            Assert.Equal(timeoutMs, result["timeoutMs"].GetValue<int>())
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.0))

        Assert.Equal(0, Volatile.Read(&starts))
        Assert.Equal(0, gate.CurrentCount)
    }

[<Fact>]
let ``FCS admission rechecks cancellation after receiving a slot`` () =
    task {
        use gate = new SemaphoreSlim(1, 1)
        use cancellation = new CancellationTokenSource()
        let mutable starts = 0

        let request =
            runLimitedWithTimeoutCore
                gate
                cancellation.Token
                None
                cancellation.Cancel
                (fun _ ->
                    Interlocked.Increment(&starts) |> ignore
                    Task.FromResult(JsonObject() :> JsonNode))

        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> request :> Task)
        Assert.Equal(0, Volatile.Read(&starts))
        Assert.Equal(1, gate.CurrentCount)
    }

[<Fact>]
let ``FCS admission restores its slot for synchronous faulted and cancelled work`` () =
    task {
        use gate = new SemaphoreSlim(1, 1)

        let synchronous =
            runLimited gate CancellationToken.None (fun () ->
                raise (InvalidOperationException("synchronous")))

        let! _ = Assert.ThrowsAsync<InvalidOperationException>(fun () -> synchronous :> Task)
        Assert.Equal(1, gate.CurrentCount)

        let faulted =
            runLimited gate CancellationToken.None (fun () ->
                Task.FromException<JsonNode>(InvalidOperationException("faulted")))

        let! _ = Assert.ThrowsAsync<InvalidOperationException>(fun () -> faulted :> Task)
        Assert.Equal(1, gate.CurrentCount)

        use workCancellation = new CancellationTokenSource()
        workCancellation.Cancel()

        let cancelled =
            runLimited gate CancellationToken.None (fun () ->
                Task.FromCanceled<JsonNode>(workCancellation.Token))

        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> cancelled :> Task)
        Assert.Equal(1, gate.CurrentCount)
    }

[<Fact>]
let ``retained protected worker keeps FCS slot through response and fault then releases`` () =
    task {
        use gate = new SemaphoreSlim(1, 1)

        let retainedWorker =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable lateStarts = 0

        let! response =
            runLimitedWithTimeoutRetained gate CancellationToken.None (Some 1_000) (fun _ retainUntil ->
                retainUntil retainedWorker.Task
                Task.FromResult(JsonObject() :> JsonNode))

        Assert.NotNull(response)
        Assert.Equal(0, gate.CurrentCount)

        let! queued =
            runLimitedWithTimeoutRetained gate CancellationToken.None (Some 0) (fun _ _ ->
                Interlocked.Increment(&lateStarts) |> ignore
                Task.FromResult(JsonObject() :> JsonNode))

        Assert.Equal("fcs_admission_timeout", queued["errorKind"].GetValue<string>())
        Assert.Equal(0, Volatile.Read(&lateStarts))

        retainedWorker.TrySetException(InvalidOperationException("retained worker fault"))
        |> ignore

        do! gate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2.0))
        Assert.Equal(0, gate.CurrentCount)
        gate.Release() |> ignore
        Assert.Equal(1, gate.CurrentCount)
    }

[<Fact>]
let ``find admission observes the shared deadline without leaving a queued waiter`` () =
    task {
        use gate = new SemaphoreSlim(0, 1)
        let expiry = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let deadline = FindRequestDeadline(20_000, semanticExpirySignal = expiry.Task)
        let mutable starts = 0

        let request =
            runLimitedWithFindDeadlineRetainedCore
                gate
                CancellationToken.None
                deadline
                ignore
                (fun _ _ ->
                    Interlocked.Increment(&starts) |> ignore
                    Task.FromResult(JsonObject() :> JsonNode))

        expiry.TrySetResult(()) |> ignore
        let! result = request.WaitAsync(TimeSpan.FromSeconds(2.0))

        Assert.Equal("timeout", result["status"].GetValue<string>())
        Assert.Equal("fcs_admission_timeout", result["errorKind"].GetValue<string>())
        Assert.Equal(0, Volatile.Read(&starts))

        // The expired waiter was cancelled and drained, so a later permit remains
        // available instead of being consumed by work that already returned.
        gate.Release() |> ignore
        Assert.Equal(1, gate.CurrentCount)
    }

[<Fact>]
let ``project outline keeps a non-admission result unchanged`` () =
    let result =
        JsonObject(
            [ KeyValuePair<string, JsonNode>("status", JsonValue.Create("ok"))
              KeyValuePair<string, JsonNode>("files", JsonArray()) ]
        )
        :> JsonNode

    let normalized = normalizeProjectOutlineAdmissionResult 60_000 result

    Assert.Same(result, normalized)

[<Fact>]
let ``project outline expands an admission timeout into unknown coverage`` () =
    let result =
        JsonObject(
            [ KeyValuePair<string, JsonNode>("status", JsonValue.Create("timeout"))
              KeyValuePair<string, JsonNode>("errorKind", JsonValue.Create("fcs_admission_timeout"))
              KeyValuePair<string, JsonNode>("message", JsonValue.Create("queue expired")) ]
        )
        :> JsonNode

    let normalized = normalizeProjectOutlineAdmissionResult 321 result
    let coverage = normalized["coverage"]
    let phases = coverage["phases"].AsArray()
    let issues = coverage["issues"].AsArray()

    Assert.Equal("unknown", normalized["status"].GetValue<string>())
    Assert.Equal("fcs_admission_timeout", normalized["errorKind"].GetValue<string>())
    Assert.Equal("queue expired", normalized["message"].GetValue<string>())
    Assert.Equal(321, normalized["timeoutMs"].GetValue<int>())
    Assert.True(normalized["retryable"].GetValue<bool>())
    Assert.False(normalized["resultSetComplete"].GetValue<bool>())
    Assert.False(coverage["complete"].GetValue<bool>())
    Assert.Equal(0, coverage["filesRequested"].GetValue<int>())
    Assert.Equal(0, coverage["filesScanned"].GetValue<int>())
    Assert.Equal(0, coverage["filesTimedOut"].GetValue<int>())
    Assert.Equal(0, coverage["filesFailed"].GetValue<int>())
    Assert.Equal(0, coverage["filesNotStarted"].GetValue<int>())
    let phase = Assert.Single(phases)
    Assert.Equal("admission", phase["phase"].GetValue<string>())
    Assert.Equal("timed_out", phase["status"].GetValue<string>())
    let issue = Assert.Single(issues)
    Assert.Equal("fcs_admission_timeout", issue["errorKind"].GetValue<string>())
    Assert.Equal("queue expired", issue["message"].GetValue<string>())
    Assert.Equal(1, coverage["issuesReturned"].GetValue<int>())
    Assert.False(coverage["issuesTruncated"].GetValue<bool>())
    Assert.False(normalized["truncated"].GetValue<bool>())
    Assert.Null(normalized["nextCursor"])
    Assert.Empty(normalized["files"].AsArray())

[<Fact>]
let ``MCP adapter preserves structured transport errors without debug wrapping (#242)`` () =
    let payload = "{\"errorKind\":\"FcsAborted\",\"message\":\"cancelled\"}"
    Assert.Equal(payload, mcpErrorText (McpError.TransportError payload))
