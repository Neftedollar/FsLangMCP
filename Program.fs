module FsLangMcp.Program

open System
open System.IO
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FsLangMcp.Types
open FsLangMcp.LspBridge
open FsLangMcp.FcsBridge
open FsLangMcp.ProjectHealth
open FsLangMcp.ProjectInspection
open FsLangMcp.Tools
open FsLangMcp.RuntimeStatus
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open System.Text.Json
open System.Text.Json.Nodes

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
        invalidOp $"Unable to start process: %s{fileName}"

    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, stdout, stderr

let private parseProjInfoOutput (path: string) (exitCode: int) (stdout: string) (stderr: string) : JsonNode =
    if exitCode <> 0 then
        jobj [ "error", jstr $"proj-info failed (exit %d{exitCode})"; "stderr", jstr stderr ] :> JsonNode
    elif String.IsNullOrWhiteSpace(stdout) then
        jobj [ "error", jstr "proj-info produced no output" ] :> JsonNode
    else
        let token = JsonNode.Parse(stdout)

        let json =
            match token with
            | :? JsonArray as arr when arr.Count > 0 ->
                match arr.[0] with
                | :? JsonObject as obj -> obj
                | other ->
                    let kind = other.GetValueKind()
                    jobj [ "warning", jstr (sprintf "unexpected element type: %s" (kind.ToString())) ]
            | :? JsonObject as obj -> obj
            | _ -> JsonObject()

        let otherOptions =
            json["OtherOptions"]
            |> Option.ofObj
            |> Option.map (fun t -> t.Deserialize<string array>() |> Option.ofObj |> Option.defaultValue [||])
            |> Option.defaultValue [||]

        jobj
            [ "projectPath", jstr path
              "otherOptions", JsonArray(otherOptions |> Array.map jstr) :> JsonNode
              "optionsCount", jint otherOptions.Length ]
        :> JsonNode

let private runProjInfoAsync (path: string) : Task<JsonNode> =
    task {
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
                raise (
                    FileNotFoundException
                        "proj-info not found on PATH. Install with: dotnet tool install -g ionide.projinfo.tool"
                )

        if not started then
            raise (InvalidOperationException "Unable to start proj-info process")

        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        let! stdout = stdoutTask
        let! stderr = stderrTask
        do! proc.WaitForExitAsync()
        return parseProjInfoOutput path proc.ExitCode stdout stderr
    }

let private readPositiveIntEnv (name: string) (defaultValue: int) =
    match Environment.GetEnvironmentVariable(name) with
    | null -> defaultValue
    | value ->
        match Int32.TryParse(value) with
        | true, parsed when parsed > 0 -> parsed
        | _ -> defaultValue

let private runLimited (gate: SemaphoreSlim) (work: unit -> Task<JsonNode>) : Task<JsonNode> =
    task {
        do! gate.WaitAsync()

        try
            return! work ()
        finally
            gate.Release() |> ignore
    }

let private ensureDotnetGlobalTool (toolId: string) =
    let updateCode, _, updateErr =
        runProcess "dotnet" [ "tool"; "update"; "-g"; toolId ]

    if updateCode = 0 then
        Console.Error.WriteLine($"[bootstrap] updated %s{toolId}")
        true
    else
        let installCode, _, installErr =
            runProcess "dotnet" [ "tool"; "install"; "-g"; toolId ]

        if installCode = 0 then
            Console.Error.WriteLine($"[bootstrap] installed %s{toolId}")
            true
        else
            Console.Error.WriteLine($"[bootstrap] failed for %s{toolId}")

            if not (String.IsNullOrWhiteSpace(updateErr)) then
                Console.Error.WriteLine(updateErr)

            if not (String.IsNullOrWhiteSpace(installErr)) then
                Console.Error.WriteLine(installErr)

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
            | unknown -> Fail $"Unknown argument: %s{unknown}"

    loop 0

// ─── Entry point ───────────────────────────────────────────────────────────────

[<EntryPoint>]
let main argv =
    match applyCliOverrides argv with
    | BootstrapTools -> if bootstrapTools () then 0 else 1
    | ShowHelp message ->
        Console.WriteLine(message)
        0
    | Fail message ->
        Console.Error.WriteLine(message)
        1
    | Start ->
        use bridge = new FsAutoCompleteBridge()
        let fcsBridge = new FcsBridge()
        use fcsGate = new SemaphoreSlim(readPositiveIntEnv "FSLANGMCP_MAX_CONCURRENT_FCS" 2)
        use lspGate = new SemaphoreSlim(readPositiveIntEnv "FSLANGMCP_MAX_CONCURRENT_LSP" 1)

        let server =
            mcpServer {
                name "fsharp-fsautocomplete"
                version "0.4.0"

                tool (
                    TypedTool.define<CompletionArgs>
                        "textDocument_completion"
                        "[FSAC] Raw LSP proxy to fsautocomplete textDocument/completion. Exact-position IDE primitive; requires set_project first. Prefer agent-friendly FCS/navigation tools for codebase understanding. line/character are 0-based. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.Completion args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<PositionArgs>
                        "textDocument_definition"
                        "[FSAC] Raw LSP proxy to fsautocomplete textDocument/definition. Exact-position IDE primitive; requires set_project first. line/character are 0-based. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.Definition args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<ReferencesArgs>
                        "textDocument_references"
                        "[FSAC] Raw LSP proxy to fsautocomplete textDocument/references. Exact-position IDE primitive; requires set_project first. For query-based agent workflows prefer project symbol-use tools. line/character are 0-based. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.References args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<WorkspaceSymbolArgs>
                        "workspace_symbol"
                        "[FSAC] Raw LSP proxy to fsautocomplete workspace/symbol. Useful for quick lookup after set_project, but returns IDE-shaped results without source context."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.WorkspaceSymbol args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<DiagnosticsArgs>
                        "workspace_diagnostics"
                        "[FSAC] Raw cached LSP publishDiagnostics payload, either for one file or all files. Requires set_project first. Use for current FSAC/compiler/analyzer diagnostics; it does not run build/tests."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.Diagnostics args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FSharpCompileArgs>
                        "fsharp_compile"
                        "[FCS in-process] Agent-friendly FCS project validation. Requires an explicit .fsproj path, loads project options through Ionide.ProjInfo, then runs FSharpChecker.ParseAndCheckProject. Does not require set_project, does not run dotnet build/test, and does not emit assemblies."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.CompileProject args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<SetProjectArgs>
                        "set_project"
                        "[FSAC] Initialize or switch the FSAC/LSP project context. Must be called before textDocument_* and workspace_* tools. Accepts .fsproj, .sln, .slnx, or directory. Waits up to 30s for workspace load and clears FCS caches."
                        (fun args ->
                            toolResult (
                                runLimited lspGate (fun () ->
                                    task {
                                        let! result = bridge.SetProject args
                                        fcsBridge.ClearCaches()
                                        return result
                                    })
                            ))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<ProjectHealthArgs>
                        "project_health"
                        "[FCS in-process] Fast read-only preflight for one F# project. Reports whether FsLangMCP can trust semantic tooling, project options availability, source file readability, analyzer setup, test project discovery, and current LSP readiness. Does not start/switch FSAC, run compile, or run tests."
                        (fun args ->
                            let snapshot =
                                { ProjectPath = bridge.CurrentProjectPath
                                  WorkspaceRoot = bridge.CurrentWorkspaceRoot
                                  WorkspaceReady = bridge.IsWorkspaceReady
                                  DiagnosticsFileCount = bridge.DiagnosticsFileCount }

                            let probe path =
                                fcsBridge.ProbeProjectOptions(path) |> Async.AwaitTask

                            toolResult (
                                runLimited fcsGate (fun () -> createReport args snapshot probe |> Async.StartAsTask)
                            ))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FSharpProjectInspectArgs>
                        "fsharp_project_inspect"
                        "[FCS in-process] Read-only .fsproj inspection for agents. Returns project identity, compile order, package/project references, signature/implementation pairing, and shared scan filtering summary. Does not build, restore, test, edit files, or require set_project."
                        (fun args -> toolResult (Task.FromResult(inspectProject args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsParseAndCheckArgs>
                        "fcs_parse_and_check_file"
                        "[FCS in-process] Agent-friendly FCS parse+typecheck for one file. Prefer passing projectPath (.fsproj) for accurate project context; projectOptions can override. Falls back to script inference only when no project can be resolved. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.ParseAndCheckFile args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsFileSymbolsArgs>
                        "fcs_file_symbols"
                        "[FCS in-process] Raw FCS symbol extraction for one file. Can be noisy on large files; default returns definitions, includeAllUses returns locals/parameters/usages too. Prefer fcs_file_outline for normal agent navigation. Pass projectPath when possible and 'text' for unsaved content."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.FileSymbols args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsFileOutlineArgs>
                        "fcs_file_outline"
                        "[FCS in-process] Agent-friendly compact F# outline for one file. Filters local/noisy symbols by default and returns name, kind, range, signature/type, accessibility, and declaration range. Prefer this over fcs_file_symbols for navigation."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.FileOutline args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsProjectSymbolUsesArgs>
                        "fcs_project_symbol_uses"
                        "[FCS in-process] Agent-friendly FCS project-wide symbol-use search by symbol name/full name. Results are cached by resolved project options. Prefer exact=true for narrow queries. Pass projectPath when possible and 'text' for unsaved content."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.ProjectSymbolUses args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsFindSymbolArgs>
                        "fcs_find_symbol"
                        "[FCS in-process] Agent-friendly project-wide symbol search with grouped definitions/references and source line context. Better than chaining workspace_symbol, fcs_project_symbol_uses, and shell line reads. Pass projectPath when possible."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.FindSymbol args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSymbolAtWordArgs>
                        "fcs_symbol_at_word"
                        "[FCS in-process] Tolerant FCS symbol lookup for agent workflows. Accepts a line plus word/occurrence, finds the candidate span, and returns symbol identity, kind, type string, definition range, and optional documentation. Prefer over exact-position hover/type queries."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.SymbolAtWord args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsProjectOutlineArgs>
                        "fcs_project_outline"
                        "[FCS in-process] Agent-friendly project outline over filtered compile files. Uses shared filtering to skip generated/build artifacts and returns compact per-file outlines. Use maxFiles/maxResultsPerFile on large projects."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.ProjectOutline args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsTypeAtPositionArgs>
                        "fcs_type_at_position"
                        "[FCS in-process] Low-level exact-position FCS type/symbol query. Works without LSP set_project, but requires accurate line/character and project context for good results. Prefer fcs_symbol_at_word for normal agent workflows. Pass projectPath/projectOptions and 'text' when available."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.TypeAtPosition args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSignatureHelpArgs>
                        "fcs_signature_help"
                        "[FCS in-process] Low-level exact-position FCS signature help. Returns overloads/parameters around a call site. line/character are 0-based. Pass projectPath/projectOptions and 'text' when available."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.SignatureHelp args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<PositionArgs>
                        "fsharp_signature_data"
                        "[FSAC] Structured FSAC signature help via fsharp/signatureData. Requires set_project and an exact call-site position. Use this when FCS fallback is insufficient or when validating FSAC's current workspace view."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.SignatureData args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FormattingArgs>
                        "textDocument_formatting"
                        "[FSAC] Raw LSP formatting proxy via fsautocomplete/Fantomas. Requires set_project first. Returns formatted text and edits; it does not write to disk. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.Formatting args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<CodeActionArgs>
                        "textDocument_codeAction"
                        "[FSAC] Raw LSP codeAction proxy at an exact position with empty diagnostic context. Requires set_project first. Useful for debugging FSAC; prefer future diagnostics-to-fix workflows for agent repairs. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.CodeAction args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RenameArgs>
                        "textDocument_rename"
                        "[FSAC] Raw LSP semantic rename at an exact position. Requires set_project first. Safer than text search, but returns raw WorkspaceEdit and needs a precise target. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.Rename args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsGetProjectOptionsArgs>
                        "fcs_get_project_options"
                        "[FCS in-process] Diagnostic helper: get FSharp compiler OtherOptions for a .fsproj via proj-info. Useful when automatic project resolution fails or when passing explicit projectOptions to FCS tools."
                        (fun args ->
                            let path = args.projectPath

                            if String.IsNullOrWhiteSpace(path) then
                                toolResult (Task.FromException<JsonNode>(ArgumentException "projectPath is required"))
                            else
                                toolResult (runLimited fcsGate (fun () -> runProjInfoAsync path)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RuntimeStatusArgs>
                        "fsharp_runtime_status"
                        "[FCS in-process] Read-only observational snapshot of the FsLangMCP process runtime state: managed-heap sizes by generation/LOH/POH, GC collection counts, isServerGC flag, assembly load count, FCS checker configuration flags and project-results cache size, and the FSAC child-process working set. Returns numbers only — no interpretation. Never triggers a GC collection, never walks the heap, never attaches diagnostic listeners."
                        (fun args ->
                            toolResult (
                                Task.FromResult(
                                    buildSnapshot
                                        args
                                        fcsBridge.CheckerConfig
                                        bridge.FsacProcess
                                )
                            ))
                    |> unwrapResult
                )

                useStdio
            }

        try
            Server.run server |> fun t -> t.GetAwaiter().GetResult()
            0
        with ex ->
            Console.Error.WriteLine($"Fatal error: %s{ex.Message}")
            1
