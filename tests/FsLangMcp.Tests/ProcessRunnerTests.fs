module FsLangMcp.Tests.ProcessRunnerTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open FsLangMcp.ProcessRunner
open Xunit

let private tempScript (body: string) =
    let id = Guid.NewGuid().ToString("N")
    let path = Path.Combine(Path.GetTempPath(), $"fslangmcp_process_%s{id}.fsx")
    File.WriteAllText(path, body)
    path

let private tryLinuxProcessState pid =
    if OperatingSystem.IsLinux() then
        try
            let stat = File.ReadAllText($"/proc/{pid}/stat")
            let commandEnd = stat.LastIndexOf(')')

            if commandEnd >= 0 && commandEnd + 2 < stat.Length then
                Some stat[commandEnd + 2]
            else
                None
        with _ ->
            None
    else
        None

let private isProcessRunning pid =
    match tryLinuxProcessState pid with
    | Some 'Z'
    | Some 'X' -> false
    | _ ->
        try
            use child = Process.GetProcessById(pid)
            not child.HasExited
        with :? ArgumentException ->
            false

let private waitForProcessExit pid =
    task {
        let deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5.0)
        let mutable running = isProcessRunning pid

        while running && DateTime.UtcNow < deadline do
            do! Task.Delay(50)
            running <- isProcessRunning pid

        return not running
    }

let private waitForFile path =
    task {
        let deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10.0)

        while not (File.Exists path) && DateTime.UtcNow < deadline do
            do! Task.Delay(25)

        return File.Exists path
    }

let private readRecordedPids path =
    File.ReadAllLines(path)
    |> Array.choose (fun value ->
        match Int32.TryParse value with
        | true, pid -> Some pid
        | _ -> None)

let private stopRecordedProcesses pids =
    for pid in pids do
        if isProcessRunning pid then
            try
                use child = Process.GetProcessById(pid)
                child.Kill(true)
            with _ ->
                ()

let private escapeVerbatimString (value: string) = value.Replace("\"", "\"\"")

let private processTreeScripts pidPath exitCode keepParentAlive =
    let childScript = tempScript "System.Threading.Thread.Sleep(30000)\n"
    let escapedPidPath = escapeVerbatimString pidPath
    let escapedChildScript = childScript |> Path.GetFullPath |> escapeVerbatimString

    let parentScript =
        tempScript
            $"""
open System
open System.Diagnostics
open System.IO

let startInfo = ProcessStartInfo()
startInfo.FileName <- Environment.ProcessPath
startInfo.UseShellExecute <- false
startInfo.RedirectStandardOutput <- true
startInfo.RedirectStandardError <- true
startInfo.CreateNoWindow <- true
startInfo.ArgumentList.Add("fsi")
startInfo.ArgumentList.Add("--exec")
startInfo.ArgumentList.Add(@"%s{escapedChildScript}")

let child = new Process(StartInfo = startInfo)

if not (child.Start()) then
    failwith "Unable to start descendant process."

let pidPath = @"%s{escapedPidPath}"
let pendingPidPath = pidPath + ".pending"
File.WriteAllLines(pendingPidPath, [| Environment.ProcessId.ToString(); child.Id.ToString() |])
File.Move(pendingPidPath, pidPath)

if %b{keepParentAlive} then
    System.Threading.Thread.Sleep(30000)

Environment.Exit(%d{exitCode})
"""

    parentScript, childScript

[<Fact>]
let ``Runner rejects caller cancellation before entering the process launch path`` () : Task =
    task {
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        // A nonexistent executable is a deterministic launch sentinel on every OS:
        // reaching Process.Start would surface a start failure instead of cancellation.
        let id = Guid.NewGuid().ToString("N")
        let executable = $"fslangmcp-must-not-launch-%s{id}"

        let operation =
            runAsync executable Seq.empty (TimeSpan.FromSeconds(30.0)) cancellation.Token

        let! error = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> operation :> Task)
        Assert.Equal(cancellation.Token, error.CancellationToken)
    }

[<Fact>]
let ``Runner drains stdout and stderr concurrently`` () : Task =
    task {
        let script =
            tempScript "System.Console.Error.Write(System.String('e', 200000))\nSystem.Console.Out.Write(\"ok\")\n"

        try
            let! result =
                runAsync "dotnet" [ "fsi"; "--exec"; script ] (TimeSpan.FromSeconds(30.0)) CancellationToken.None

            Assert.Equal(0, result.ExitCode)
            Assert.Contains("ok", result.StandardOutput)
            Assert.True(result.StandardError.Length >= 200_000)
        finally
            File.Delete(script)
    }

[<Fact>]
let ``Runner kills a process tree when the timeout expires`` () : Task =
    task {
        let id = Guid.NewGuid().ToString("N")
        let pidPath = Path.Combine(Path.GetTempPath(), $"fslangmcp_process_%s{id}.pid")
        let fileName, arguments, scripts =
            if OperatingSystem.IsWindows() then
                let parentScript, childScript = processTreeScripts pidPath 0 true

                "dotnet", [ "fsi"; "--exec"; parentScript ], [ parentScript; childScript ]
            else
                // Use a tiny native shell tree so suite-wide CPU pressure tests
                // termination rather than racing the .NET SDK/F# Interactive startup.
                // Record both the direct shell and its background descendant.
                "/bin/sh",
                [ "-c"
                  "sleep 30 & child=$!; printf '%s\n%s\n' \"$$\" \"$child\" > \"$1\"; wait \"$child\""
                  "fslangmcp-runner"
                  pidPath ],
                []

        try
            let operation =
                runAsync fileName arguments (TimeSpan.FromSeconds(3.0)) CancellationToken.None

            let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation :> Task)
            Assert.Contains("timed out", error.Message)
            Assert.True(File.Exists(pidPath), "The child did not start before the timeout.")

            let pids = readRecordedPids pidPath

            Assert.NotEmpty pids

            for pid in pids do
                let stillRunning = isProcessRunning pid
                let state = tryLinuxProcessState pid |> Option.map string |> Option.defaultValue "unavailable"

                Assert.False(stillRunning, $"Timed-out process %d{pid} is still running (state={state}).")
        finally
            if File.Exists(pidPath) then
                File.Delete(pidPath)

            scripts |> List.iter File.Delete
    }

[<Theory>]
[<InlineData(0)>]
[<InlineData(23)>]
let ``Runner returns from ordinary exits only after detached-output descendants are drained`` exitCode : Task =
    task {
        let id = Guid.NewGuid().ToString("N")
        let pidPath = Path.Combine(Path.GetTempPath(), $"fslangmcp_completion_%s{id}.pid")
        let parentScript, childScript = processTreeScripts pidPath exitCode false
        let mutable pids = Array.empty

        try
            let! result =
                runAsync
                    "dotnet"
                    [ "fsi"; "--exec"; parentScript ]
                    (TimeSpan.FromSeconds(30.0))
                    CancellationToken.None

            Assert.Equal(exitCode, result.ExitCode)
            Assert.True(File.Exists(pidPath), "The process tree did not start.")
            pids <- readRecordedPids pidPath
            Assert.Equal(2, pids.Length)

            for pid in pids do
                Assert.False(isProcessRunning pid, $"Process %d{pid} was still active after runner completion.")
        finally
            stopRecordedProcesses pids

            if File.Exists(pidPath) then
                File.Delete(pidPath)

            File.Delete(parentScript)
            File.Delete(childScript)
    }

[<Fact>]
let ``Runner cancellation completes only after the contained process tree is drained`` () : Task =
    task {
        let id = Guid.NewGuid().ToString("N")
        let pidPath = Path.Combine(Path.GetTempPath(), $"fslangmcp_cancellation_%s{id}.pid")
        let parentScript, childScript = processTreeScripts pidPath 0 true
        let mutable pids = Array.empty
        use cancellation = new CancellationTokenSource()

        try
            let operation =
                runAsync
                    "dotnet"
                    [ "fsi"; "--exec"; parentScript ]
                    (TimeSpan.FromSeconds(30.0))
                    cancellation.Token

            let! started = waitForFile pidPath

            if not started then
                cancellation.Cancel()

                try
                    let! _ = operation
                    ()
                with _ ->
                    ()

            Assert.True(started, "The process tree did not start before cancellation.")
            pids <- readRecordedPids pidPath
            Assert.Equal(2, pids.Length)

            cancellation.Cancel()
            let! error = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> operation :> Task)
            Assert.Equal(cancellation.Token, error.CancellationToken)

            for pid in pids do
                Assert.False(isProcessRunning pid, $"Process %d{pid} was still active after cancellation completed.")
        finally
            stopRecordedProcesses pids

            if File.Exists(pidPath) then
                File.Delete(pidPath)

            File.Delete(parentScript)
            File.Delete(childScript)
    }

[<Fact>]
let ``Containment wait reports an undrained process instead of silently succeeding`` () : Task =
    task {
        let startInfo = ProcessStartInfo()
        let mutable script = None

        if OperatingSystem.IsWindows() then
            let path = tempScript "System.Threading.Thread.Sleep(30000)\n"
            script <- Some path
            startInfo.FileName <- "dotnet"
            startInfo.ArgumentList.Add("fsi")
            startInfo.ArgumentList.Add("--exec")
            startInfo.ArgumentList.Add(path)
        else
            startInfo.FileName <- "/bin/sh"
            startInfo.ArgumentList.Add("-c")
            startInfo.ArgumentList.Add("sleep 30")

        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        use containment = startContainedProcess startInfo

        let! waitError =
            task {
                try
                    do! containment.WaitForTerminationAsync(TimeSpan.Zero)
                    return None
                with error ->
                    return Some error
            }

        terminateContainedProcess containment
        do! containment.WaitForTerminationAsync(TimeSpan.FromSeconds(5.0))
        script |> Option.iter File.Delete

        match waitError with
        | Some error ->
            let timeoutError = Assert.IsType<TimeoutException>(error)
            Assert.Contains("did not drain", timeoutError.Message)
        | None -> Assert.Fail("An active contained process was reported as drained.")
    }

[<Fact>]
let ``Runner timeout also bounds pipe drain after the direct child exits`` () : Task =
    task {
        if not (OperatingSystem.IsWindows()) then
            // The shell exits immediately, while the background child keeps the
            // inherited stdout/stderr pipe handles open. Waiting only for the shell
            // process therefore succeeds but ReadToEndAsync has no EOF (#164).
            let stopwatch = Stopwatch.StartNew()

            let operation =
                runAsync
                    "/bin/sh"
                    [ "-c"; "sleep 30 &" ]
                    (TimeSpan.FromMilliseconds(500.0))
                    CancellationToken.None

            let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation :> Task)
            stopwatch.Stop()

            Assert.Contains("timed out", error.Message)
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5.0),
                $"Pipe-drain timeout took {stopwatch.Elapsed}; the inherited handle was not bounded."
            )
    }

[<Fact>]
let ``Runner bounds captured stdout and stderr while continuing to drain pipes`` () : Task =
    task {
        let script =
            tempScript
                "System.Console.Out.Write(System.String('o', 200000))\nSystem.Console.Error.Write(System.String('e', 200000))\n"

        try
            let! result =
                runAsyncWithOutputLimit
                    "dotnet"
                    [ "fsi"; "--exec"; script ]
                    (TimeSpan.FromSeconds(30.0))
                    CancellationToken.None
                    1024

            Assert.Equal(0, result.ExitCode)
            Assert.True(result.StandardOutputTruncated)
            Assert.True(result.StandardErrorTruncated)
            Assert.Contains("output truncated", result.StandardOutput)
            Assert.Contains("output truncated", result.StandardError)
            Assert.True(result.StandardOutput.Length < 1200)
            Assert.True(result.StandardError.Length < 1200)
        finally
            File.Delete(script)
    }

[<Fact>]
let ``Managed Unix session wrapper preserves arguments output and exit code`` () : Task =
    task {
        if not (OperatingSystem.IsWindows()) then
            let! result =
                runAsyncWithManagedUnixSessionWrapper
                    "/bin/sh"
                    [ "-c"
                      "printf '%s' \"$1\"; printf '%s' \"$2\" >&2; exit 23"
                      "fslangmcp-wrapper"
                      "argument with spaces"
                      "stderr value" ]
                    (TimeSpan.FromSeconds(30.0))
                    CancellationToken.None

            Assert.Equal(23, result.ExitCode)
            Assert.Equal("argument with spaces", result.StandardOutput)
            Assert.Equal("stderr value", result.StandardError)
    }

[<Fact>]
let ``Managed Unix session wrapper kills descendants on timeout`` () : Task =
    task {
        if not (OperatingSystem.IsWindows()) then
            let id = Guid.NewGuid().ToString("N")
            let pidPath = Path.Combine(Path.GetTempPath(), $"fslangmcp_descendant_%s{id}.pid")
            let mutable descendantPid = None

            try
                let operation =
                    runAsyncWithManagedUnixSessionWrapper
                        "/bin/sh"
                        [ "-c"; "sleep 30 & echo $! > \"$1\"; exit 0"; "fslangmcp-wrapper"; pidPath ]
                        (TimeSpan.FromSeconds(2.0))
                        CancellationToken.None

                let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation :> Task)
                Assert.Contains("timed out", error.Message)
                Assert.True(File.Exists(pidPath), "The descendant did not start before the timeout.")

                let pid = File.ReadAllText(pidPath) |> Int32.Parse
                descendantPid <- Some pid
                let! stopped = waitForProcessExit pid
                Assert.True(stopped, $"Timed-out descendant process %d{pid} is still running.")
            finally
                match descendantPid with
                | Some pid when isProcessRunning pid ->
                    try
                        use child = Process.GetProcessById(pid)
                        child.Kill(true)
                    with _ ->
                        ()
                | _ -> ()

                if File.Exists(pidPath) then
                    File.Delete(pidPath)
    }
