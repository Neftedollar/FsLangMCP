module FsLangMcp.LspBridge

open System
open System.IO
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FsLangMcp.Types
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open StreamJsonRpc

// ─── LSP types ─────────────────────────────────────────────────────────────────

type private LspDocumentState =
    { mutable Version: int
      mutable Text: string }

type private DiagnosticsTarget(store: ConcurrentDictionary<string, JsonNode>) =
    [<JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)>]
    member _.PublishDiagnostics(payload: JsonObject) =
        let uriToken = payload["uri"]

        if not (isNull uriToken) then
            let uri = uriToken.GetValue<string>()
            let diagnostics = payload["diagnostics"]

            store[uri] <-
                if isNull diagnostics then
                    JsonArray() :> JsonNode
                else
                    diagnostics.DeepClone()

// ─── Workspace load notification parsing ──────────────────────────────────────

module internal WorkspaceNotification =
    let private tryStringProperty (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as obj ->
            match obj[name] with
            | null -> None
            | value ->
                try
                    Some(value.GetValue<string>())
                with _ ->
                    None
        | _ -> None

    let private tryObjectProperty (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as obj ->
            match obj[name] with
            | :? JsonObject as value -> Some(value :> JsonNode)
            | _ -> None
        | _ -> None

    let private jsonElementToNode (payload: JsonElement) =
        try
            match payload.ValueKind with
            | JsonValueKind.String ->
                let content = payload.GetString()

                if String.IsNullOrWhiteSpace content then
                    JsonValue.Create("") :> JsonNode
                else
                    JsonNode.Parse(content)
                    |> Option.ofObj
                    |> Option.defaultValue (JsonValue.Create(content) :> JsonNode)
            | _ ->
                JsonNode.Parse(payload.GetRawText())
                |> Option.ofObj
                |> Option.defaultValue (JsonObject() :> JsonNode)
        with _ ->
            JsonObject() :> JsonNode

    let private tryParseContentPayload (payload: JsonNode) =
        match tryStringProperty "content" payload with
        | Some content when not (String.IsNullOrWhiteSpace content) ->
            try
                JsonNode.Parse(content) |> Option.ofObj |> Option.defaultValue payload
            with _ ->
                payload
        | _ -> payload

    let private isFinished (value: string option) =
        value
        |> Option.exists (fun status -> String.Equals(status, "finished", StringComparison.OrdinalIgnoreCase))

    let isWorkspaceLoadFinished (payload: JsonElement) =
        let node = payload |> jsonElementToNode |> tryParseContentPayload

        let kind =
            tryStringProperty "Kind" node
            |> Option.orElseWith (fun () -> tryStringProperty "kind" node)

        let data =
            tryObjectProperty "Data" node
            |> Option.orElseWith (fun () -> tryObjectProperty "data" node)

        let topLevelFinished =
            tryStringProperty "status" node
            |> Option.orElseWith (fun () -> tryStringProperty "Status" node)
            |> isFinished

        let workspaceLoadFinished =
            kind
            |> Option.exists (fun value -> String.Equals(value, "workspaceLoad", StringComparison.OrdinalIgnoreCase))
            && (data
                |> Option.bind (fun value ->
                    tryStringProperty "Status" value
                    |> Option.orElseWith (fun () -> tryStringProperty "status" value))
                |> isFinished)

        topLevelFinished || workspaceLoadFinished

module internal WorkspaceSelection =
    [<Struct>]
    type CandidateKind =
        | Solution
        | Project

    type Candidate =
        { Kind: CandidateKind
          Path: string }

    type Selection =
        | Selected of projectOrWorkspacePath: string * candidates: Candidate list
        | Ambiguous of candidates: Candidate list

    let private solutionFiles directory =
        [| yield! Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
           yield! Directory.GetFiles(directory, "*.slnx", SearchOption.TopDirectoryOnly) |]
        |> Array.map Path.GetFullPath
        |> Array.sort

    let private projectFiles directory =
        Directory.GetFiles(directory, "*.fsproj", SearchOption.TopDirectoryOnly)
        |> Array.map Path.GetFullPath
        |> Array.sort

    let select (path: string) =
        let fullPath = Path.GetFullPath(path)

        if not (Directory.Exists fullPath) then
            Selected(fullPath, [])
        else
            let solutions = solutionFiles fullPath

            if solutions.Length = 1 then
                Selected(solutions[0], [ { Kind = Solution; Path = solutions[0] } ])
            elif solutions.Length > 1 then
                Ambiguous(solutions |> Array.map (fun path -> { Kind = Solution; Path = path }) |> Array.toList)
            else
                let projects = projectFiles fullPath

                if projects.Length > 1 then
                    Ambiguous(projects |> Array.map (fun path -> { Kind = Project; Path = path }) |> Array.toList)
                elif projects.Length = 1 then
                    Selected(projects[0], [ { Kind = Project; Path = projects[0] } ])
                else
                    Selected(fullPath, [])

    let candidateKindToString kind =
        match kind with
        | Solution -> "solution"
        | Project -> "project"

    let candidateToJson candidate =
        jobj [ "kind", jstr (candidateKindToString candidate.Kind); "path", jstr candidate.Path ] :> JsonNode

// ─── WorkspaceLoadTarget: tracks FsAutoComplete workspace load notifications ───

type private WorkspaceLoadTarget(setReady: unit -> unit) =
    let notifyIfReady (payload: JsonElement) =
        if WorkspaceNotification.isWorkspaceLoadFinished payload then
            setReady ()

    [<JsonRpcMethod("fsharp/notifyWorkspace", UseSingleObjectParameterDeserialization = true)>]
    member _.NotifyWorkspace(payload: JsonElement) = notifyIfReady payload

    [<JsonRpcMethod("fsharp/workspaceLoad", UseSingleObjectParameterDeserialization = true)>]
    member _.WorkspaceLoad(payload: JsonElement) = notifyIfReady payload

// ─── LspResponseShape (pure response builders, testable) ──────────────────────

module internal LspResponseShape =
    /// Maps the workspace-ready flag to the public lspState string.
    let lspStateString (workspaceReady: bool) : string =
        if workspaceReady then "ready" else "warming"

    /// Decides whether the symbol index should be considered ready.
    /// Empty results within `warmupWindow` after workspaceReady=true are treated
    /// as "still indexing" so callers can distinguish them from "no matches".
    let assessSymbolIndex
        (response: JsonNode)
        (workspaceReadyAt: DateTimeOffset voption)
        (now: DateTimeOffset)
        (warmupWindow: TimeSpan)
        : bool =
        match response with
        | :? JsonArray as arr when arr.Count = 0 ->
            match workspaceReadyAt with
            | ValueSome readyAt -> now - readyAt > warmupWindow
            | ValueNone -> false
        | _ -> true

    /// Builds the workspace_diagnostics response payload for a single file.
    let diagnosticsResponseForFile (workspaceReady: bool) (diagnosticsCount: int) (filePayload: JsonNode) : JsonNode =
        jobj
            [ "status", jstr "ok"
              "lspState", jstr (lspStateString workspaceReady)
              "diagnosticsFileCount", jint diagnosticsCount
              "result", filePayload ]
        :> JsonNode

    /// Builds the workspace_diagnostics response payload for the whole workspace.
    let diagnosticsResponseForWorkspace
        (workspaceReady: bool)
        (diagnosticsCount: int)
        (allPayloads: JsonObject)
        : JsonNode =
        jobj
            [ "status", jstr "ok"
              "lspState", jstr (lspStateString workspaceReady)
              "diagnosticsFileCount", jint diagnosticsCount
              "result", allPayloads :> JsonNode ]
        :> JsonNode

    /// Builds the workspace_symbol response payload, deciding symbolIndexReady
    /// from response shape + warmup timing.
    let workspaceSymbolResponse
        (response: JsonNode)
        (workspaceReadyAt: DateTimeOffset voption)
        (now: DateTimeOffset)
        (warmupWindow: TimeSpan)
        : JsonNode =
        let symbolIndexReady = assessSymbolIndex response workspaceReadyAt now warmupWindow

        jobj
            [ "status", jstr "ok"
              "lspState", jstr "ready"
              "symbolIndexReady", jbool symbolIndexReady
              "result", response ]
        :> JsonNode

// ─── FsAutoCompleteBridge ──────────────────────────────────────────────────────

type internal FsAutoCompleteBridge() =
    let gate = new SemaphoreSlim(1, 1)
    let documents = ConcurrentDictionary<string, LspDocumentState>()
    let diagnostics = ConcurrentDictionary<string, JsonNode>()

    [<VolatileField>]
    let mutable rpc: JsonRpc option = None

    [<VolatileField>]
    let mutable lspProcess: Process option = None

    [<VolatileField>]
    let mutable runtimeProjectPath: string option = None

    [<VolatileField>]
    let mutable runtimeWorkspaceRoot: string option = None

    let workspaceReadyEvent = new ManualResetEventSlim(false)

    [<VolatileField>]
    let mutable workspaceReady = false

    [<VolatileField>]
    let mutable workspaceReadyAt: DateTimeOffset voption = ValueNone

    [<VolatileField>]
    let mutable symbolIndexEverWarmed = false

    [<VolatileField>]
    let mutable stderrPump: Task option = None

    let parseArgs (raw: string option) =
        raw
        |> Option.defaultValue ""
        |> fun value -> value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    let resolveWorkspaceFromProjectPath (projectOrDirPath: string) =
        let full = Path.GetFullPath(projectOrDirPath)

        if Directory.Exists(full) then
            full
        else
            let parent = Path.GetDirectoryName(full)

            if String.IsNullOrWhiteSpace(parent) then
                Directory.GetCurrentDirectory()
            else
                parent

    let getWorkspaceRoot () =
        let fromRuntimeWorkspace = runtimeWorkspaceRoot |> Option.map Path.GetFullPath

        let fromWorkspaceRootEnv =
            Environment.GetEnvironmentVariable("FSA_WORKSPACE_ROOT")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map Path.GetFullPath

        let fromRuntimeProject =
            runtimeProjectPath |> Option.map resolveWorkspaceFromProjectPath

        let fromProjectPathEnv =
            Environment.GetEnvironmentVariable("FSA_PROJECT_PATH")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map resolveWorkspaceFromProjectPath

        fromRuntimeWorkspace
        |> Option.orElse fromWorkspaceRootEnv
        |> Option.orElse fromRuntimeProject
        |> Option.orElse fromProjectPathEnv
        |> Option.defaultValue (Directory.GetCurrentDirectory())
        |> Path.GetFullPath

    let fsacCommand () =
        Environment.GetEnvironmentVariable("FSAC_COMMAND")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue "fsautocomplete"

    let fsacArgs () =
        Environment.GetEnvironmentVariable("FSAC_ARGS") |> Option.ofObj |> parseArgs

    let explicitWorkspacePath () =
        runtimeProjectPath
        |> Option.filter (fun path ->
            let ext = Path.GetExtension(path)

            String.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase)
            || String.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase)
            || String.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))

    let useAutomaticWorkspaceInit () = explicitWorkspacePath().IsNone

    let markWorkspaceReady () =
        workspaceReady <- true
        workspaceReadyAt <- ValueSome DateTimeOffset.UtcNow
        workspaceReadyEvent.Set()

    member private _.StopLspUnsafe() =
        match rpc with
        | Some instance ->
            instance.Dispose()
            rpc <- None
        | None -> ()

        match lspProcess with
        | Some fsacProc when not fsacProc.HasExited ->
            fsacProc.Kill(true)
            // Give pump 500ms to notice process died
            match stderrPump with
            | Some pump ->
                try
                    pump.Wait(500) |> ignore
                with _ ->
                    ()
            | None -> ()

            fsacProc.Dispose()
            lspProcess <- None
        | Some fsacProc ->
            fsacProc.Dispose()
            lspProcess <- None
        | None -> ()

        stderrPump <- None
        documents.Clear()
        diagnostics.Clear()
        workspaceReady <- false
        workspaceReadyAt <- ValueNone
        symbolIndexEverWarmed <- false
        workspaceReadyEvent.Reset()

    member _.DiagnosticsStore = diagnostics

    member _.CurrentProjectPath = runtimeProjectPath

    member _.CurrentWorkspaceRoot = runtimeWorkspaceRoot

    member _.IsWorkspaceReady = workspaceReady

    member _.IsSymbolIndexReady = symbolIndexEverWarmed

    member _.DiagnosticsFileCount = diagnostics.Count

    /// Returns the live FSAC child process handle, or None if FSAC is not running.
    member _.FsacProcess: Process option = lspProcess

    // Wait until workspaceReady is true or timeout elapses
    member _.WaitForReady(timeout: TimeSpan) : Task<bool> =
        task { return workspaceReadyEvent.Wait(timeout) }

    // Return not-ready JSON if workspace not loaded yet
    member _.NotReadyResponse() : JsonNode =
        jobj
            [ "status", jstr "not_ready"
              "message", jstr "fsautocomplete is still loading the project. Try again in a moment." ]
        :> JsonNode

    member this.SetProject(args: SetProjectArgs) : Task<JsonNode> =
        task {
            let inputPath = Path.GetFullPath(args.projectPath)

            if not (File.Exists(inputPath) || Directory.Exists(inputPath)) then
                invalidArg (nameof args.projectPath) $"Path does not exist: %s{inputPath}"

            match WorkspaceSelection.select inputPath with
            | WorkspaceSelection.Ambiguous candidates ->
                return
                    jobj
                        [ "status", jstr "ambiguous_workspace"
                          "message", jstr "Multiple workspace candidates found. Pass an explicit .sln/.slnx/.fsproj path."
                          "candidates",
                          JsonArray(candidates |> List.map WorkspaceSelection.candidateToJson |> List.toArray) :> JsonNode ]
                    :> JsonNode
            | WorkspaceSelection.Selected(projectPath, selectionCandidates) ->
                let isSolution =
                    projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || projectPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)

                let resolvedWorkspace =
                    if isSolution then
                        Path.GetDirectoryName(projectPath)
                    else
                        args.workspacePath
                        |> Option.map Path.GetFullPath
                        |> Option.defaultWith (fun () -> resolveWorkspaceFromProjectPath projectPath)

                let restartLsp = args.restartLsp |> Option.defaultValue true
                do! gate.WaitAsync()

                let capturedProjectPath, capturedWorkspaceRoot =
                    try
                        runtimeProjectPath <- Some projectPath
                        runtimeWorkspaceRoot <- Some resolvedWorkspace

                        Environment.SetEnvironmentVariable("FSA_PROJECT_PATH", runtimeProjectPath |> Option.defaultValue "")
                        Environment.SetEnvironmentVariable("FSA_WORKSPACE_ROOT", resolvedWorkspace)

                        runtimeProjectPath, resolvedWorkspace
                    with ex ->
                        gate.Release() |> ignore
                        reraise ()

                if restartLsp then
                    try
                        this.StopLspUnsafe()
                        let! _ = this.StartLspUnsafe()
                        ()
                    finally
                        gate.Release() |> ignore
                else
                    gate.Release() |> ignore

                let loadedProjects =
                    FsLangMcp.ProjectFiles.SolutionParsing.listProjects projectPath

                let loadedProjectsNode =
                    JsonArray(loadedProjects |> Array.map jstr) :> JsonNode

                if restartLsp then
                    let! ready = this.WaitForReady(TimeSpan.FromSeconds(30.0))
                    let loadStatus = if ready then "ready" else "timeout"

                    let readinessNode =
                        jobj
                            [ "lsp", jbool ready
                              // projectOptions is enriched by the caller in Program.fs (Bridge has no FCS handle).
                              "projectOptions", jbool false
                              "symbolIndex", jbool symbolIndexEverWarmed ]
                        :> JsonNode

                    return
                        jobj
                            [ "status", jstr "ok"
                              "result",
                              jobj
                                  [ "projectPath", capturedProjectPath |> Option.map jstr |> Option.defaultValue null
                                    "requestedPath", jstr inputPath
                                    "workspaceRoot", jstr capturedWorkspaceRoot
                                    "lspRestarted", jbool restartLsp
                                    "solutionMode", jbool isSolution
                                    "workspaceLoadStatus", jstr loadStatus
                                    "loadedProjects", loadedProjectsNode
                                    "readiness", readinessNode
                                    "workspaceCandidates",
                                    JsonArray(selectionCandidates |> List.map WorkspaceSelection.candidateToJson |> List.toArray) :> JsonNode ]
                              :> JsonNode ]
                        :> JsonNode
                else
                    let readinessNode =
                        jobj
                            [ "lsp", jbool workspaceReady
                              "projectOptions", jbool false
                              "symbolIndex", jbool symbolIndexEverWarmed ]
                        :> JsonNode

                    return
                        jobj
                            [ "status", jstr "ok"
                              "result",
                              jobj
                                  [ "projectPath", capturedProjectPath |> Option.map jstr |> Option.defaultValue null
                                    "requestedPath", jstr inputPath
                                    "workspaceRoot", jstr capturedWorkspaceRoot
                                    "lspRestarted", jbool restartLsp
                                    "solutionMode", jbool isSolution
                                    "workspaceLoadStatus", jstr "not_started"
                                    "loadedProjects", loadedProjectsNode
                                    "readiness", readinessNode
                                    "workspaceCandidates",
                                    JsonArray(selectionCandidates |> List.map WorkspaceSelection.candidateToJson |> List.toArray) :> JsonNode ]
                              :> JsonNode ]
                        :> JsonNode
        }

    // StartLspUnsafe: assumes gate is already held by caller
    member private this.StartLspUnsafe() : Task<JsonRpc> =
        task {
            let command = fsacCommand ()
            let args = fsacArgs ()
            let workspaceRoot = getWorkspaceRoot ()

            let psi = ProcessStartInfo()
            psi.FileName <- command
            psi.UseShellExecute <- false
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.CreateNoWindow <- true

            if Directory.Exists(workspaceRoot) then
                psi.WorkingDirectory <- workspaceRoot

            for arg in args do
                psi.ArgumentList.Add(arg)

            let fsacProc = new Process(StartInfo = psi)

            if not (fsacProc.Start()) then
                invalidOp $"Unable to start %s{command}"

            let pumpStderr: Task =
                task {
                    let mutable keepReading = true

                    while keepReading do
                        let! line = fsacProc.StandardError.ReadLineAsync()

                        if isNull line then
                            keepReading <- false
                        elif not (String.IsNullOrWhiteSpace line) then
                            Console.Error.WriteLine($"[fsautocomplete] {line}")
                }

            pumpStderr.ContinueWith(
                (fun (t: Task) ->
                    if t.IsFaulted then
                        let ex = t.Exception.GetBaseException()

                        if not (ex :? ObjectDisposedException) then
                            Console.Error.WriteLine($"[stderr pump] %s{ex.Message}")),
                TaskContinuationOptions.OnlyOnFaulted
            )
            |> ignore

            stderrPump <- Some pumpStderr

            let formatter = new SystemTextJsonFormatter()
            let formatterOpts = JsonSerializerOptions()
            formatterOpts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
            formatter.JsonSerializerOptions <- formatterOpts

            let handler =
                new HeaderDelimitedMessageHandler(
                    fsacProc.StandardInput.BaseStream,
                    fsacProc.StandardOutput.BaseStream,
                    formatter
                )

            let jsonRpc = new JsonRpc(handler)
            let targetOptions = JsonRpcTargetOptions(AllowNonPublicInvocation = true)

            jsonRpc.AddLocalRpcTarget(new DiagnosticsTarget(diagnostics), targetOptions)
            |> ignore

            jsonRpc.AddLocalRpcTarget(new WorkspaceLoadTarget(markWorkspaceReady), targetOptions)
            |> ignore

            jsonRpc.StartListening()

            let rootUri = Uri(workspaceRoot).AbsoluteUri
            let workspaceName = Path.GetFileName(workspaceRoot)

            let initializeParams =
                jobj
                    [ "processId", jint Environment.ProcessId
                      "rootUri", jstr rootUri
                      "trace", jstr "off"
                      "clientInfo", jobj [ "name", jstr "fsmcp-fsharp"; "version", jstr "0.1.0" ]
                      "initializationOptions", jobj [ "AutomaticWorkspaceInit", jbool (useAutomaticWorkspaceInit ()) ]
                      "workspaceFolders",
                      JsonArray(jobj [ "uri", jstr rootUri; "name", jstr workspaceName ] :> JsonNode) :> JsonNode
                      "capabilities",
                      jobj
                          [ "workspace", jobj [ "workspaceFolders", jbool true ]
                            "textDocument",
                            jobj [ "completion", jobj [ "completionItem", jobj [ "snippetSupport", jbool true ] ] ] ] ]

            let! _ = jsonRpc.InvokeWithParameterObjectAsync<JsonNode>("initialize", initializeParams)
            do! jsonRpc.NotifyWithParameterObjectAsync("initialized", JsonObject())

            match explicitWorkspacePath () with
            | Some workspacePath ->
                let document = jobj [ "uri", jstr (toFileUri workspacePath) ] :> JsonNode
                let documents = JsonArray(document)

                let workspaceLoadParams =
                    jobj
                        [ "TextDocuments", documents.DeepClone()
                          "textDocuments", documents.DeepClone() ]

                let! _ = jsonRpc.InvokeWithParameterObjectAsync<JsonNode>("fsharp/workspaceLoad", workspaceLoadParams)
                markWorkspaceReady ()
            | None -> ()

            rpc <- Some jsonRpc
            lspProcess <- Some fsacProc
            return jsonRpc
        }

    member private this.EnsureStarted() : Task<JsonRpc> =
        task {
            do! gate.WaitAsync()

            try
                match rpc with
                | Some existing -> return existing
                | None ->
                    let! result = this.StartLspUnsafe()
                    return result
            finally
                gate.Release() |> ignore
        }

    member private this.SyncDocument(jsonRpc: JsonRpc, path: string, providedText: string option) : Task<string> =
        task {
            let fullPath = Path.GetFullPath(path)
            let uri = toFileUri fullPath
            let text = providedText |> Option.defaultWith (fun () -> File.ReadAllText(fullPath))

            match documents.TryGetValue(uri) with
            | true, state ->
                state.Version <- state.Version + 1
                state.Text <- text

                let didChangeParams =
                    jobj
                        [ "textDocument", jobj [ "uri", jstr uri; "version", jint state.Version ]
                          "contentChanges", JsonArray(jobj [ "text", jstr text ] :> JsonNode) :> JsonNode ]

                do! jsonRpc.NotifyWithParameterObjectAsync("textDocument/didChange", didChangeParams)
                return uri
            | false, _ ->
                documents[uri] <- { Version = 1; Text = text }

                let didOpenParams =
                    jobj
                        [ "textDocument",
                          jobj
                              [ "uri", jstr uri
                                "languageId", jstr "fsharp"
                                "version", jint 1
                                "text", jstr text ] ]

                do! jsonRpc.NotifyWithParameterObjectAsync("textDocument/didOpen", didOpenParams)
                return uri
        }

    // WithDocument: gate wraps only SyncDocument; released before LSP invoke
    member private this.WithDocument
        (path: string, providedText: string option, methodName: string, mkParams: string -> JsonObject)
        : Task<JsonNode> =
        task {
            let! jsonRpc = this.EnsureStarted()

            if not workspaceReady then
                return this.NotReadyResponse()
            else

                // Lock only for document sync
                let! uri =
                    task {
                        do! gate.WaitAsync()

                        try
                            return! this.SyncDocument(jsonRpc, path, providedText)
                        finally
                            gate.Release() |> ignore
                    }

                // Gate released — StreamJsonRpc handles concurrent requests natively
                let parameters = mkParams uri
                let! response = jsonRpc.InvokeWithParameterObjectAsync<JsonNode>(methodName, parameters)
                return jobj [ "status", jstr "ok"; "result", response ] :> JsonNode
        }

    member private this.PositionParams(uri: string, line: int, character: int) : JsonObject =
        jobj
            [ "textDocument", jobj [ "uri", jstr uri ]
              "position", jobj [ "line", jint line; "character", jint character ] ]

    member this.Completion(args: CompletionArgs) =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/completion",
            fun uri ->
                let parameters = this.PositionParams(uri, args.line, args.character)

                match args.triggerCharacter with
                | Some ch ->
                    parameters["context"] <- jobj [ "triggerKind", jint 2; "triggerCharacter", jstr ch ] :> JsonNode
                    parameters
                | None -> parameters
        )

    member this.Hover(args: PositionArgs) =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/hover",
            fun uri -> this.PositionParams(uri, args.line, args.character)
        )

    member this.Definition(args: PositionArgs) =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/definition",
            fun uri -> this.PositionParams(uri, args.line, args.character)
        )

    member this.SignatureData(args: PositionArgs) =
        this.WithDocument(
            args.path,
            args.text,
            "fsharp/signatureData",
            fun uri -> this.PositionParams(uri, args.line, args.character)
        )

    member this.References(args: ReferencesArgs) =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/references",
            fun uri ->
                let baseParams = this.PositionParams(uri, args.line, args.character)

                baseParams["context"] <-
                    jobj [ "includeDeclaration", jbool (args.includeDeclaration |> Option.defaultValue false) ]
                    :> JsonNode

                baseParams
        )

    member this.WorkspaceSymbol(args: WorkspaceSymbolArgs) : Task<JsonNode> =
        task {
            let! jsonRpc = this.EnsureStarted()

            if not workspaceReady then
                return this.NotReadyResponse()
            else

                // No document sync needed — just invoke directly, no gate
                let parameters = jobj [ "query", jstr args.query ]
                let! response = jsonRpc.InvokeWithParameterObjectAsync<JsonNode>("workspace/symbol", parameters)

                // First non-empty response is the signal that the symbol index has warmed.
                match response with
                | :? JsonArray as arr when arr.Count > 0 -> symbolIndexEverWarmed <- true
                | _ -> ()

                return
                    LspResponseShape.workspaceSymbolResponse
                        response
                        workspaceReadyAt
                        DateTimeOffset.UtcNow
                        (TimeSpan.FromSeconds 3.0)
        }

    member _.Diagnostics(args: DiagnosticsArgs) : Task<JsonNode> =
        task {
            let resolveByUri (uri: string) =
                match diagnostics.TryGetValue(uri) with
                | true, payload -> payload.DeepClone()
                | false, _ -> JsonArray() :> JsonNode

            match args.path with
            | Some path ->
                let uri = toFileUri path

                return
                    LspResponseShape.diagnosticsResponseForFile workspaceReady diagnostics.Count (resolveByUri uri)
            | None ->
                let root = JsonObject()

                for KeyValue(uri, payload) in diagnostics do
                    root[uri] <- payload.DeepClone()

                return LspResponseShape.diagnosticsResponseForWorkspace workspaceReady diagnostics.Count root
        }

    member this.Formatting(args: FormattingArgs) : Task<JsonNode> =
        task {
            let! jsonRpc = this.EnsureStarted()

            if not workspaceReady then
                return this.NotReadyResponse()
            else

                let fullPath = Path.GetFullPath(args.path)

                let originalText =
                    args.text |> Option.defaultWith (fun () -> File.ReadAllText(fullPath))

                // Lock only for document sync
                let! uri =
                    task {
                        do! gate.WaitAsync()

                        try
                            return! this.SyncDocument(jsonRpc, args.path, args.text)
                        finally
                            gate.Release() |> ignore
                    }

                let formatParams =
                    jobj
                        [ "textDocument", jobj [ "uri", jstr uri ]
                          "options", jobj [ "tabSize", jint 4; "insertSpaces", jbool true ] ]

                let! editsToken =
                    jsonRpc.InvokeWithParameterObjectAsync<JsonNode>("textDocument/formatting", formatParams)

                // Apply text edits to produce formatted result
                let applyEdits (text: string) (edits: JsonArray) =
                    // Sort edits in reverse order (bottom to top) to preserve positions
                    let getRangeStart (e: JsonNode) =
                        let r = e["range"]
                        let s = r["start"]
                        s["line"].GetValue<int>(), s["character"].GetValue<int>()

                    let sortedEdits =
                        edits
                        |> Seq.cast<JsonNode>
                        |> Seq.sortByDescending (fun e ->
                            let sl, sc = getRangeStart e
                            sl, sc)
                        |> Seq.toArray

                    let lines = text.Split('\n') |> Array.map (fun l -> l.TrimEnd('\r'))

                    let applyEdit (linesArr: string array) (edit: JsonNode) =
                        let range = edit["range"]
                        let startPos = range["start"]
                        let endPos = range["end"]
                        let startLine = startPos["line"].GetValue<int>()
                        let startChar = startPos["character"].GetValue<int>()
                        let endLine = endPos["line"].GetValue<int>()
                        let endChar = endPos["character"].GetValue<int>()
                        let newText = edit["newText"].GetValue<string>()

                        let before =
                            if startLine < linesArr.Length && startChar > 0 then
                                linesArr[startLine][.. startChar - 1]
                            else
                                ""

                        let after =
                            if endLine < linesArr.Length then
                                let endLineText = linesArr[endLine]

                                if endChar < endLineText.Length then
                                    endLineText[endChar..]
                                else
                                    ""
                            else
                                ""

                        let replacement = before + newText + after
                        let replacementLines = replacement.Split('\n')

                        let result = System.Collections.Generic.List<string>()

                        for i in 0 .. startLine - 1 do
                            result.Add(linesArr[i])

                        for l in replacementLines do
                            result.Add(l)

                        for i in endLine + 1 .. linesArr.Length - 1 do
                            result.Add(linesArr[i])

                        result.ToArray()

                    let mutable currentLines = lines

                    for edit in sortedEdits do
                        currentLines <- applyEdit currentLines edit

                    String.concat "\n" currentLines

                let formatted =
                    match editsToken with
                    | :? JsonArray as edits when edits.Count > 0 -> applyEdits originalText edits
                    | _ -> originalText

                return
                    jobj
                        [ "status", jstr "ok"
                          "result",
                          jobj
                              [ "formatted", jstr formatted
                                "edits",
                                (match editsToken with
                                 | :? JsonArray -> editsToken
                                 | _ -> JsonArray() :> JsonNode) ]
                          :> JsonNode ]
                    :> JsonNode
        }

    member this.CodeAction(args: CodeActionArgs) : Task<JsonNode> =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/codeAction",
            fun uri ->
                // Each JsonNode has a single Parent reference; assigning the same instance
                // as both `start` and `end` raises InvalidOperationException at the second
                // attach. Build two distinct position nodes.
                let posNode () =
                    jobj [ "line", jint args.line; "character", jint args.character ] :> JsonNode

                jobj
                    [ "textDocument", jobj [ "uri", jstr uri ]
                      "range", jobj [ "start", posNode (); "end", posNode () ]
                      "context", jobj [ "diagnostics", JsonArray() :> JsonNode ] ]
        )

    member this.Rename(args: RenameArgs) : Task<JsonNode> =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/rename",
            fun uri ->
                jobj
                    [ "textDocument", jobj [ "uri", jstr uri ]
                      "position", jobj [ "line", jint args.line; "character", jint args.character ]
                      "newName", jstr args.newName ]
        )

    interface IDisposable with
        member this.Dispose() =
            this.StopLspUnsafe()
            gate.Dispose()
