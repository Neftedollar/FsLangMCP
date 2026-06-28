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

        let versionResponse: Task<JsonNode> =
            task {
                return
                    jobj
                        [ "status", jstr "ok"
                          "fslangmcpVersion", jstr FsLangMcp.Version.current
                          "productName", jstr FsLangMcp.Version.productName ]
                    :> JsonNode
            }

        let server =
            mcpServer {
                name "fsharp-fsautocomplete"
                version FsLangMcp.Version.current

                tool (
                    TypedTool.define<CompletionArgs>
                        "textDocument_completion"
                        "Raw LSP proxy to fsautocomplete textDocument/completion. Exact-position IDE primitive; requires set_project first. line/character are 0-based. Pass 'text' for unsaved content. Avoid for free-form agent flows — completion is exact-position editor IO; for symbol semantics use fcs_symbol_at_word or fcs_type_at_position instead."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.Completion args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<CheckArgs>
                        "check"
                        "One trustworthy verdict for the active F# context. Bare check() suffices: returns `verdict` (clean|errors|unknown) after a FRESH in-process type-check, so it never reports a stale-`{}` false-clean and you don't fall back to dotnet build. Optional: scope (auto|file|project|workspace|snippet), path, snippet (inline source), speed (trusted default | fast = cached FSAC snapshot), severity. Prefer over workspace_diagnostics / fcs_check_file when you just need a yes/no answer."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (
                                runLimited fcsGate (fun () ->
                                    Dispatcher.CheckDispatch.run fcsBridge bridge (Dispatcher.Check args))))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<SetProjectArgs>
                        "set_project"
                        "Initialize or switch the FSAC/LSP project context. Must be called before textDocument_* and workspace_* tools. Accepts .fsproj, .sln, .slnx, or directory. Waits up to 30s for workspace load and clears FCS caches. Response includes loadedProjects (.fsproj paths discovered) and readiness (lsp / projectOptions / symbolIndex flags)."
                        (fun args ->
                            toolResult (
                                runLimited lspGate (fun () ->
                                    task {
                                        let! result = bridge.SetProject args
                                        fcsBridge.ClearCaches()

                                        // Fire-and-forget pre-warm (issue #128): kick a background
                                        // ParseAndCheckProject over every member project so the FIRST
                                        // `find` sweep hits FCS's (solution-scale) project cache warm
                                        // instead of paying cold type-check cost. Acquire the fcsGate
                                        // per project so concurrent real tool calls still interleave.
                                        match bridge.CurrentProjectPath with
                                        | Some activeProject ->
                                            let warmTargets =
                                                FsLangMcp.ProjectFiles.SolutionParsing.listProjects activeProject

                                            if warmTargets.Length > 0 then
                                                (task {
                                                    for fsproj in warmTargets do
                                                        do! fcsGate.WaitAsync()

                                                        try
                                                            do! fcsBridge.PrewarmProject fsproj
                                                        finally
                                                            fcsGate.Release() |> ignore
                                                 }
                                                 :> Task)
                                                |> ignore
                                        | None -> ()

                                        // Enrich readiness.projectOptions by probing the first loaded .fsproj.
                                        // Bridge cannot do this itself (no FCS handle); we own that wiring here.
                                        match result["result"] with
                                        | :? JsonObject as resultObj ->
                                            let probeTarget =
                                                match resultObj["loadedProjects"] with
                                                | :? JsonArray as arr when arr.Count > 0 ->
                                                    match arr[0] with
                                                    | null -> None
                                                    | node ->
                                                        let v = node.GetValue<string>()
                                                        if System.String.IsNullOrWhiteSpace v then None else Some v
                                                | _ -> None

                                            match probeTarget with
                                            | Some path ->
                                                let! probe = fcsBridge.ProbeProjectOptions path

                                                let ok =
                                                    match probe with
                                                    | Ok _ -> true
                                                    | Error _ -> false

                                                match resultObj["readiness"] with
                                                | :? JsonObject as readinessObj ->
                                                    readinessObj["projectOptions"] <- jbool ok
                                                | _ -> ()
                                            | None -> ()
                                        | _ -> ()

                                        return result
                                    })
                            ))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<ProjectHealthArgs>
                        "project_health"
                        "Fast read-only preflight for one F# project. Reports whether FsLangMCP can trust semantic tooling, project options availability, source file readability, analyzer setup, test project discovery, and current LSP readiness. projectPath is optional after set_project (falls back to the active project); pass it explicitly to inspect a different .fsproj/.sln/.slnx. Does not start/switch FSAC, run compile, or run tests."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

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
                        "Read-only .fsproj inspection for agents. Prefer over textual reads of `.fsproj` — handles MSBuild evaluation correctly. Returns project identity, compile order, package/project references, signature/implementation pairing, and shared scan filtering summary. `projectPath` is optional after `set_project`. Does not build, restore, test, or edit files."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (Task.FromResult(inspectProject args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsReferencedSymbolsArgs>
                        "fcs_referenced_symbols"
                        "Substring search across the project's referenced assemblies (NuGet + framework) by DisplayName or FullName (case-insensitive). Prefer `fcs_nuget_types` when you already know the exact assembly name. Complements `workspace_symbol` (project-local). Reports assembly, kind, accessibility, isObsolete. `includeNonPublic=true` for internals. Paginated; default 200, max 1000. First call triggers ParseAndCheckProject. Details: docs/tools-detailed.md#fcs_referenced_symbols."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (runLimited fcsGate (fun () -> fcsBridge.ReferencedSymbols args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSuggestOpenArgs>
                        "fcs_suggest_open"
                        "Given an unresolved symbol name (FS0039), returns ranked `open` directive candidates — project-local first, then referenced assemblies. Use when an agent sees 'X is not defined' to get the right namespace instantly. Set includeReferences=false for project-only. Caveat: openPath is empty for global-namespace symbols; doesn't deduplicate the same name across multiple assemblies."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (runLimited fcsGate (fun () -> fcsBridge.SuggestOpen args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsNugetTypesArgs>
                        "fcs_nuget_types"
                        "Enumerate all types in one referenced assembly matched by EXACT SimpleName (case-insensitive). Prefer `fcs_referenced_symbols` when you need substring search across assemblies. `Spectre.Console` resolves only to that assembly — not `Spectre.Console.Cli`; call once per assembly. Each entry: displayName, fullName, kind, accessibility, isObsolete. Paginated; default 500, max 2000. Returns `matchedAssemblies=[]` on no match. Mechanics: docs/tools-detailed.md#fcs_nuget_types."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (runLimited fcsGate (fun () -> fcsBridge.NugetTypes args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsNugetMembersArgs>
                        "fcs_nuget_members"
                        "Enumerate members of one type from a referenced assembly (matched by packageId + typeName). Prefer `fcs_nuget_types` to discover available type names first. Each entry: name, kind, signature, accessibility, isObsolete, xmlDocSummary. Paginated; default 500, max 2000. Returns `matchedTypes=[]` on no type match. Mechanics: docs/tools-detailed.md#fcs_nuget_members."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (runLimited fcsGate (fun () -> fcsBridge.NugetMembers args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsFileOutlineArgs>
                        "fcs_file_outline"
                        "Agent-friendly compact F# outline for one file. Filters local/noisy symbols by default and returns name, kind, range, signature/type, accessibility, and declaration range. Prefer this over fcs_file_symbols for navigation."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.FileOutline args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsMakeInternalVisibleArgs>
                        "fcs_make_internal_visible"
                        "Drop the `private` keyword from a declaration at `(line, character)`. Returns a non-destructive workspace edit `{ status, edits, appliedPreview, originalLineText }` — does NOT write the file. Use before tests need to call internals. Returns `{ status: 'no_action', reason }` on no symbol or no recognized modifier. Supported forms and Variant B status: docs/tools-detailed.md#fcs_make_internal_visible."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (runLimited fcsGate (fun () -> fcsBridge.MakeInternalVisible args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FindArgs>
                        "find"
        "Multi-project F# semantic search: definitions, references, record-field set sites, and member call-sites on a type (`x.Foo`), unioned across every solution .fsproj. FCS-resolved — beats a textual rg that over-matches `.Member()` on unrelated types or can't cross projects. Bare find(query) gives a compact one-line-per-site list (file/line/kind/lineText); contextLines adds code. Narrow with kind (symbol|members|field|definition|position)+scope; member call sites = kind=members + member=Name."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (
                                runLimited fcsGate (fun () ->
                                    Dispatcher.FindDispatch.run fcsBridge bridge (Dispatcher.Find args))))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSymbolAtWordArgs>
                        "fcs_symbol_at_word"
                        "Tolerant FCS symbol lookup for agent workflows. Accepts a line plus word/occurrence, finds the candidate span, and returns symbol identity, kind, type string, definition range, and optional documentation. Prefer over exact-position hover/type queries."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.SymbolAtWord args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsProjectOutlineArgs>
                        "fcs_project_outline"
                        "Agent-friendly project outline over filtered compile files. Prefer over `workspace_symbol` for whole-project structural overview — skips generated/build artifacts and returns compact per-file outlines. `projectPath` is optional after `set_project` (falls back to the active project); pass it explicitly for a different .fsproj. Use `maxFiles`/`maxResultsPerFile` on large projects."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (runLimited fcsGate (fun () -> fcsBridge.ProjectOutline args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSignatureHelpArgs>
                        "fcs_signature_help"
                        "Low-level exact-position FCS signature help. Returns overloads/parameters around a call site. line/character are 0-based. Pass projectPath/projectOptions and 'text' when available."
                        (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.SignatureHelp args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<PositionArgs>
                        "fsharp_signature_data"
                        "Structured FSAC signature help via fsharp/signatureData. Requires set_project and an exact call-site position. Use this when FCS fallback is insufficient or when validating FSAC's current workspace view."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.SignatureData args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FormattingArgs>
                        "textDocument_formatting"
                        "Raw LSP formatting proxy via fsautocomplete/Fantomas. Requires set_project first. Returns formatted text and edits; it does not write to disk. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.Formatting args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<CodeActionArgs>
                        "textDocument_codeAction"
                        "Raw LSP codeAction proxy at an exact position with empty diagnostic context. Requires set_project first. Useful for debugging FSAC; prefer future diagnostics-to-fix workflows for agent repairs. Pass 'text' for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.CodeAction args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RenameArgs>
                        "textDocument_rename"
                        "Raw LSP semantic rename at an exact position. Requires `set_project` first. Prefer over textual rename — handles shadowing and aliased opens safely. Returns raw WorkspaceEdit; needs a precise target. Pass `text` for unsaved content."
                        (fun args -> toolResult (runLimited lspGate (fun () -> bridge.Rename args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsGetProjectOptionsArgs>
                        "fcs_get_project_options"
                        "Diagnostic helper: get FSharp compiler OtherOptions for a .fsproj via proj-info. projectPath is optional after set_project (falls back to the active project); pass it explicitly to inspect a different one."
                        (fun args ->
                            let resolved =
                                args.projectPath
                                |> Option.orElse bridge.CurrentProjectPath
                                |> Option.filter (System.String.IsNullOrWhiteSpace >> not)

                            match resolved with
                            | None ->
                                toolResult (
                                    Task.FromException<JsonNode>(
                                        ArgumentException
                                            "projectPath is required. Either pass it explicitly or call set_project first to establish a default."
                                    )
                                )
                            | Some path -> toolResult (runLimited fcsGate (fun () -> runProjInfoAsync path)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsCheckCompileOrderArgs>
                        "fcs_check_compile_order"
                        "Detect F#'s file-ordering gotcha: a symbol used before the file that DEFINES it in <Compile> order reads as FS0039 'not defined' though it exists. Returns { symbol, definedIn, usedIn{file,compileIndex,range,lineText}, fix } so an agent reorders the .fsproj. Use when `check` reports FS0039 'X is not defined' to tell a compile-ORDER problem from a missing `open` (fcs_suggest_open handles that). projectPath optional after set_project; `symbol` narrows to one name."
                        (fun args ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (runLimited fcsGate (fun () -> fcsBridge.CheckCompileOrder args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FslangmcpVersionArgs>
                        "fslangmcp_version"
                        "Returns the installed FsLangMCP product version and name. Zero-arg (pass {}). Same value is also surfaced in the set_project response (fslangmcpVersion field) and the fsharp_runtime_status response. Use this tool when filing UX feedback so reports can be matched to a specific release of the MCP server. Pure: no project context required, no side effects, no caches read."
                        (fun _ -> toolResult versionResponse)
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RuntimeStatusArgs>
                        "fsharp_runtime_status"
                        "Read-only observational snapshot of the FsLangMCP process runtime state: managed-heap sizes by generation/LOH/POH, GC collection counts, isServerGC flag, assembly load count, FCS checker configuration flags and project-results cache size, and the FSAC child-process working set. Returns numbers only — no interpretation. Never triggers a GC collection, never walks the heap, never attaches diagnostic listeners."
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
