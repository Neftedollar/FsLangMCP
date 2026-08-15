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
        let fileName, arguments, script =
            if OperatingSystem.IsWindows() then
                let script =
                    tempScript
                        $"System.IO.File.WriteAllText(@\"%s{pidPath}\", System.Environment.ProcessId.ToString())\nSystem.Threading.Thread.Sleep(30000)\n"

                "dotnet", [ "fsi"; "--exec"; script ], Some script
            else
                // Use a tiny native shell tree so suite-wide CPU pressure tests
                // termination rather than racing the .NET SDK/F# Interactive startup.
                // Record both the direct shell and its background descendant.
                "/bin/sh",
                [ "-c"
                  "sleep 30 & child=$!; printf '%s\n%s\n' \"$$\" \"$child\" > \"$1\"; wait \"$child\""
                  "fslangmcp-runner"
                  pidPath ],
                None

        try
            let operation =
                runAsync fileName arguments (TimeSpan.FromSeconds(3.0)) CancellationToken.None

            let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation :> Task)
            Assert.Contains("timed out", error.Message)
            Assert.True(File.Exists(pidPath), "The child did not start before the timeout.")

            let pids =
                File.ReadAllLines(pidPath)
                |> Array.choose (fun value ->
                    match Int32.TryParse value with
                    | true, pid -> Some pid
                    | _ -> None)

            Assert.NotEmpty pids

            for pid in pids do
                let stillRunning = isProcessRunning pid
                let state = tryLinuxProcessState pid |> Option.map string |> Option.defaultValue "unavailable"

                Assert.False(stillRunning, $"Timed-out process %d{pid} is still running (state={state}).")
        finally
            if File.Exists(pidPath) then
                File.Delete(pidPath)

            script |> Option.iter File.Delete
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
