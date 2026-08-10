module FsLangMcp.Tests.StartupTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit

[<Fact>]
let ``stdio server answers MCP initialize without host file watching`` () =
    task {
        let executable =
            Path.Combine(
                AppContext.BaseDirectory,
                if OperatingSystem.IsWindows() then "FsLangMcp.exe" else "FsLangMcp"
            )

        let startInfo = ProcessStartInfo(executable)
        startInfo.WorkingDirectory <- Path.GetTempPath()
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
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

            let serverName =
                root.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()

            Assert.False(String.IsNullOrWhiteSpace(serverName), "Initialize response omitted serverInfo.name.")

            server.StandardInput.Close()
            Assert.True(server.WaitForExit(5000), "Server did not stop after stdin closed.")
        finally
            if not server.HasExited then
                server.Kill(true)
                server.WaitForExit()
    }
