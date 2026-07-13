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

        let script =
            tempScript
                $"System.IO.File.WriteAllText(@\"%s{pidPath}\", System.Environment.ProcessId.ToString())\nSystem.Threading.Thread.Sleep(30000)\n"

        try
            let operation =
                runAsync "dotnet" [ "fsi"; "--exec"; script ] (TimeSpan.FromSeconds(3.0)) CancellationToken.None

            let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation :> Task)
            Assert.Contains("timed out", error.Message)
            Assert.True(File.Exists(pidPath), "The child did not start before the timeout.")

            let pid = File.ReadAllText(pidPath) |> Int32.Parse

            let stillRunning =
                try
                    use child = Process.GetProcessById(pid)
                    not child.HasExited
                with :? ArgumentException ->
                    false

            Assert.False(stillRunning, $"Timed-out child process %d{pid} is still running.")
        finally
            if File.Exists(pidPath) then
                File.Delete(pidPath)

            File.Delete(script)
    }
