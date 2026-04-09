module FsLangMcp.Program

open System
open System.IO
open System.Diagnostics
open System.Threading.Tasks
open FsLangMcp.Types
open FsLangMcp.LspBridge
open FsLangMcp.FcsBridge
open FsLangMcp.Tools
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open Newtonsoft.Json.Linq

// ─── CLI helpers ───────────────────────────────────────────────────────────────

let private runProcess (fileName: string) (args: string list) =
    let psi = ProcessStartInfo()
    psi.FileName <- fileName
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.CreateNoWindow <- true

    for arg in args do
        psi.ArgumentList.Add(arg)

    use proc = new Process(StartInfo = psi)

    if not (proc.Start()) then
        invalidOp $"Unable to start process: {fileName}"

    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, stdout, stderr

let private parseProjInfoOutput (path: string) (exitCode: int) (stdout: string) (stderr: string) : JToken =
    if exitCode <> 0 then
        JObject(
            JProperty("error", $"proj-info failed (exit {exitCode})"),
            JProperty("stderr", stderr)
        ) :> JToken
    elif String.IsNullOrWhiteSpace(stdout) then
        JObject(JProperty("error", "proj-info produced no output")) :> JToken
    else
        let token = JToken.Parse(stdout)
        let json =
            match token with
            | :? JArray as arr when arr.Count > 0 ->
                match arr.[0] with
                | :? JObject as obj -> obj
                | other -> JObject(JProperty("warning", sprintf "unexpected element type: %s" (other.Type.ToString())))
            | :? JObject as obj -> obj
            | _ -> JObject()
        let otherOptions =
            json["OtherOptions"]
            |> Option.ofObj
            |> Option.map (fun t -> t.ToObject<string[]>())
            |> Option.defaultValue [||]
        JObject(
            JProperty("projectPath", path),
            JProperty("otherOptions", JArray(otherOptions)),
            JProperty("optionsCount", otherOptions.Length)
        ) :> JToken

let private runProjInfoAsync (path: string) : Task<JToken> = task {
    let psi = ProcessStartInfo()
    psi.FileName <- "proj-info"
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.CreateNoWindow <- true
    psi.ArgumentList.Add("--project")
    psi.ArgumentList.Add(path)
    psi.ArgumentList.Add("--fcs")
    psi.ArgumentList.Add("--serialize")

    use proc = new Process(StartInfo = psi)
    let started =
        try
            proc.Start()
        with
        | :? System.ComponentModel.Win32Exception
        | :? System.IO.IOException ->
            raise (FileNotFoundException "proj-info not found on PATH. Install with: dotnet tool install -g ionide.projinfo.tool")
    if not started then
        raise (InvalidOperationException "Unable to start proj-info process")

    let stdoutTask = proc.StandardOutput.ReadToEndAsync()
    let stderrTask  = proc.StandardError.ReadToEndAsync()
    let! stdout = stdoutTask
    let! stderr = stderrTask
    do! proc.WaitForExitAsync()
    return parseProjInfoOutput path proc.ExitCode stdout stderr
}

let private ensureDotnetGlobalTool (toolId: string) =
    let updateCode, _, updateErr = runProcess "dotnet" [ "tool"; "update"; "-g"; toolId ]

    if updateCode = 0 then
        Console.Error.WriteLine($"[bootstrap] updated {toolId}")
        true
    else
        let installCode, _, installErr = runProcess "dotnet" [ "tool"; "install"; "-g"; toolId ]

        if installCode = 0 then
            Console.Error.WriteLine($"[bootstrap] installed {toolId}")
            true
        else
            Console.Error.WriteLine($"[bootstrap] failed for {toolId}")
            if not (String.IsNullOrWhiteSpace(updateErr)) then Console.Error.WriteLine(updateErr)
            if not (String.IsNullOrWhiteSpace(installErr)) then Console.Error.WriteLine(installErr)
            false

let private bootstrapTools () =
    [ "fsautocomplete"; "ionide.projinfo.tool" ]
    |> List.map ensureDotnetGlobalTool
    |> List.forall id

let private applyCliOverrides (argv: string array) =
    let rec loop index =
        if index >= argv.Length then
            Start
        else
            match argv[index] with
            | "--project"
            | "-p" ->
                if index + 1 >= argv.Length then
                    Fail "--project requires a value."
                else
                    Environment.SetEnvironmentVariable("FSA_PROJECT_PATH", argv[index + 1])
                    loop (index + 2)
            | "--fsac-command" ->
                if index + 1 >= argv.Length then
                    Fail "--fsac-command requires a value."
                else
                    Environment.SetEnvironmentVariable("FSAC_COMMAND", argv[index + 1])
                    loop (index + 2)
            | "--fsac-args" ->
                if index + 1 >= argv.Length then
                    Fail "--fsac-args requires a value."
                else
                    Environment.SetEnvironmentVariable("FSAC_ARGS", argv[index + 1])
                    loop (index + 2)
            | "--help"
            | "-h" ->
                ShowHelp
                    "Usage: fslangmcp [--project <path-to-fsproj>] [--fsac-command <cmd>] [--fsac-args \"...\"] [--bootstrap-tools]"
            | "--bootstrap-tools" -> BootstrapTools
            | unknown -> Fail $"Unknown argument: {unknown}"

    loop 0

// ─── Entry point ───────────────────────────────────────────────────────────────

[<EntryPoint>]
let main argv =
    match applyCliOverrides argv with
    | BootstrapTools ->
        if bootstrapTools () then 0 else 1
    | ShowHelp message ->
        Console.WriteLine(message)
        0
    | Fail message ->
        Console.Error.WriteLine(message)
        1
    | Start ->
        use bridge = new FsAutoCompleteBridge()
        let fcsBridge = new FcsBridge()

        let server =
            mcpServer {
                name "fsharp-fsautocomplete"
                version "0.2.0"

                tool (
                    TypedTool.define<CompletionArgs>
                        "textDocument_completion"
                        "Proxy to fsautocomplete textDocument/completion. line/character are 0-based. Requires set_project to be called first (or --project at startup). Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (bridge.Completion args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<PositionArgs>
                        "textDocument_hover"
                        "Get hover info (type, docs) for the F# symbol at the cursor via fsautocomplete. Richer output than fcs_type_at_position but requires a loaded LSP workspace. Requires set_project first. Pass 'text' for unsaved content."
                        (fun args -> toolResult (bridge.Hover args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<PositionArgs>
                        "textDocument_definition"
                        "Proxy to fsautocomplete textDocument/definition. line/character are 0-based. Requires set_project to be called first (or --project at startup). Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (bridge.Definition args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<ReferencesArgs>
                        "textDocument_references"
                        "Proxy to fsautocomplete textDocument/references. line/character are 0-based. Requires set_project to be called first (or --project at startup). Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (bridge.References args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<WorkspaceSymbolArgs>
                        "workspace_symbol"
                        "Proxy to fsautocomplete workspace/symbol. Requires set_project to be called first (or --project at startup)."
                        (fun args -> toolResult (bridge.WorkspaceSymbol args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<DiagnosticsArgs>
                        "workspace_diagnostics"
                        "Returns latest cached publishDiagnostics payload (per file or full map). Requires set_project to be called first (or --project at startup)."
                        (fun args -> toolResult (bridge.Diagnostics args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<SetProjectArgs>
                        "set_project"
                        "Initialize or switch the F# project context. Must be called once before any textDocument_* or workspace_* tool will work. Accepts .fsproj, .sln, .slnx, or directory. Waits up to 30s for workspace load. Also clears the FCS symbol cache."
                        (fun args ->
                            toolResult (task {
                                let! result = bridge.SetProject args
                                fcsBridge.ClearCaches()
                                return result
                            }))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsParseAndCheckArgs>
                        "fcs_parse_and_check_file"
                        "FCS parse+typecheck for a file. Use projectOptions for accurate fsproj/sln context. Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (fcsBridge.ParseAndCheckFile args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsFileSymbolsArgs>
                        "fcs_file_symbols"
                        "FCS symbol extraction from a file (definitions by default, or all uses). Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (fcsBridge.FileSymbols args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsProjectSymbolUsesArgs>
                        "fcs_project_symbol_uses"
                        "FCS project-wide symbol use search by symbol name/fullname. Results are cached per project. Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (fcsBridge.ProjectSymbolUses args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsTypeAtPositionArgs>
                        "fcs_type_at_position"
                        "Get the inferred F# type and symbol info at a cursor position using the compiler directly. Works without a loaded LSP workspace (no set_project needed). For better accuracy provide projectOptions (run: proj-info --project App.fsproj --fcs --serialize, use OtherOptions). Pass 'text' for unsaved content."
                        (fun args -> toolResult (fcsBridge.TypeAtPosition args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSignatureHelpArgs>
                        "fcs_signature_help"
                        "Returns method signature and overloads at a cursor position. line/character are 0-based (LSP convention). Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (fcsBridge.SignatureHelp args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FormattingArgs>
                        "textDocument_formatting"
                        "Formats F# code via fsautocomplete/Fantomas. Returns formatted text and applied edits. Requires set_project to be called first (or --project at startup). Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (bridge.Formatting args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<CodeActionArgs>
                        "textDocument_codeAction"
                        "Returns available code actions at a position via fsautocomplete. line/character are 0-based. Requires set_project to be called first (or --project at startup). Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (bridge.CodeAction args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RenameArgs>
                        "textDocument_rename"
                        "Renames a symbol at a position via fsautocomplete. line/character are 0-based. Requires set_project to be called first (or --project at startup). Pass 'text' to analyze unsaved source content without writing to disk."
                        (fun args -> toolResult (bridge.Rename args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsGetProjectOptionsArgs>
                        "fcs_get_project_options"
                        "Get FSharp compiler project options (OtherOptions) for a .fsproj file using proj-info. Returns the options list to use as `projectOptions` in FCS tools."
                        (fun args ->
                            let path = args.projectPath
                            if String.IsNullOrWhiteSpace(path) then
                                toolResult (Task.FromException<JToken>(ArgumentException "projectPath is required"))
                            else
                                toolResult (runProjInfoAsync path))
                    |> unwrapResult
                )

                useStdio
            }

        try
            Server.run server |> fun t -> t.GetAwaiter().GetResult()
            0
        with ex ->
            Console.Error.WriteLine($"Fatal error: {ex.Message}")
            1
