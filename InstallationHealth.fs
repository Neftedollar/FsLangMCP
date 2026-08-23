/// Detects a long-lived MCP process whose versioned dotnet-tool installation was
/// replaced on disk while the process stayed alive. Already-loaded assemblies keep
/// running, but a later lazy load (notably Ionide.ProjInfo/MSBuild) then fails with a
/// misleading FileNotFoundException even though the newly installed package is intact.
module FsLangMcp.InstallationHealth

open System
open System.IO
open System.Text.Json.Nodes
open FsLangMcp.Types

[<NoComparison>]
type StaleToolProcessFailure =
    { RunningVersion: string
      InstallationDirectory: string
      MissingRuntimeFiles: string array }

exception StaleToolProcessException of StaleToolProcessFailure

let internal requiredRuntimeFileNames =
    [| "FsLangMcp.dll"
       "Ionide.ProjInfo.dll"
       "Ionide.ProjInfo.FCS.dll"
       "Microsoft.VisualStudio.Threading.dll"
       "Microsoft.NET.StringTools.dll" |]

let internal assessAt
    (installationDirectory: string)
    (requiredFiles: string array)
    (fileExists: string -> bool)
    : StaleToolProcessFailure option =

    if String.IsNullOrWhiteSpace installationDirectory then
        None
    else
        let missing =
            requiredFiles
            |> Array.distinct
            |> Array.choose (fun fileName ->
                let fullPath = Path.Combine(installationDirectory, fileName)
                if fileExists fullPath then None else Some fileName)

        if missing.Length = 0 then
            None
        else
            Some
                { RunningVersion = FsLangMcp.Version.current
                  InstallationDirectory = installationDirectory
                  MissingRuntimeFiles = missing }

let private startupInstallationDirectory =
    let assemblyLocation = typeof<ToolError>.Assembly.Location

    if String.IsNullOrWhiteSpace assemblyLocation then
        // Assembly.Location is empty only for deployment forms this dotnet tool does
        // not currently ship (for example single-file). Do not invent a false stale
        // diagnosis there; the ordinary loader error remains available.
        ""
    else
        Path.GetDirectoryName assemblyLocation

let currentFailure () =
    assessAt startupInstallationDirectory requiredRuntimeFileNames File.Exists

let ensureCurrent () =
    match currentFailure () with
    | None -> ()
    | Some failure -> raise (StaleToolProcessException failure)

let envelope (failure: StaleToolProcessFailure) : JsonNode =
    let missing =
        failure.MissingRuntimeFiles
        |> Array.map jstr
        |> JsonArray
        :> JsonNode

    jobj
        [ "status", jstr "infrastructure_error"
          "errorKind", jstr "stale_tool_process"
          "message",
          jstr
              $"FsLangMCP %s{failure.RunningVersion} is still running from an installation directory that was removed or became incomplete. This usually means `dotnet tool update` replaced the on-disk tool while the MCP process stayed alive. Restart the MCP server/client connection; `set_project(restartLsp=true)` only restarts fsautocomplete and cannot repair the parent process."
          "runningVersion", jstr failure.RunningVersion
          "installationDirectory", jstr failure.InstallationDirectory
          "missingRuntimeFiles", missing
          "restartRequired", jbool true
          "recommendation", jstr "Restart the MCP connection, then call fslangmcp_version and set_project again." ]
    :> JsonNode
