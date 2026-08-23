module FsLangMcp.Tests.InstallationHealthTests

open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open FsMcp.Core
open FsLangMcp.InstallationHealth
open FsLangMcp.Tools
open Xunit

[<Fact>]
let ``assessAt stays healthy when every required runtime file exists`` () =
    let required = [| "FsLangMcp.dll"; "Ionide.ProjInfo.dll" |]
    let present = required |> Set.ofArray
    let exists (path: string) = present.Contains(Path.GetFileName path)

    Assert.True(assessAt "/tool/0.16.0" required exists |> Option.isNone)

[<Fact>]
let ``assessAt reports the exact missing lazy dependencies`` () =
    let required =
        [| "FsLangMcp.dll"
           "Ionide.ProjInfo.dll"
           "Microsoft.VisualStudio.Threading.dll" |]

    let exists (path: string) = Path.GetFileName(path) = "FsLangMcp.dll"

    let failure = assessAt "/tool/0.15.0" required exists |> Option.get

    Assert.Equal("/tool/0.15.0", failure.InstallationDirectory)

    Assert.Equal<string array>(
        [| "Ionide.ProjInfo.dll"; "Microsoft.VisualStudio.Threading.dll" |],
        failure.MissingRuntimeFiles
    )

[<Fact>]
let ``toolResult renders a stale process as an actionable typed envelope`` () : Task =
    task {
        let failure =
            { RunningVersion = "0.15.0"
              InstallationDirectory = "/tool/.store/fslangmcp/0.15.0"
              MissingRuntimeFiles = [| "Microsoft.VisualStudio.Threading.dll" |] }

        let mutable handlerStarted = false

        let work () =
            handlerStarted <- true
            Task.FromResult<JsonNode>(JsonValue.Create("must not run"))

        let! result =
            toolResultWithHealthCheck
                (fun () -> raise (StaleToolProcessException failure))
                work

        Assert.False(handlerStarted, "The handler must remain cold until the installation guard passes.")

        match result with
        | Error error -> Assert.Fail($"Expected a typed payload, got transport error: {error}")
        | Ok [ Content.Text text ] ->
            let payload = JsonNode.Parse(text)
            Assert.Equal("infrastructure_error", payload["status"].GetValue<string>())
            Assert.Equal("stale_tool_process", payload["errorKind"].GetValue<string>())
            Assert.True(payload["restartRequired"].GetValue<bool>())
            Assert.Contains("MCP server/client connection", payload["message"].GetValue<string>())
            Assert.Contains("only restarts fsautocomplete", payload["message"].GetValue<string>())
        | Ok other -> Assert.Fail($"Expected one text content item, got {other.Length}.")
    }
