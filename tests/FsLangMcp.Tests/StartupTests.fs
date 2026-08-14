module FsLangMcp.Tests.StartupTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FsLangMcp.Program

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
