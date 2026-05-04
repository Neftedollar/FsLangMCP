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

module internal CompileResult =
    let private tryIntProperty (name: string) (response: JsonNode) =
        match response with
        | :? JsonObject as obj ->
            match obj[name] with
            | null -> None
            | value ->
                try
                    Some(value.GetValue<int>())
                with _ ->
                    None
        | _ -> None

    let private tryArrayCount (name: string) (response: JsonNode) =
        match response with
        | :? JsonObject as obj ->
            match obj[name] with
            | :? JsonArray as arr -> Some arr.Count
            | _ -> None
        | _ -> None

    let classify (response: JsonNode) =
        let exitCode =
            tryIntProperty "ExitCode" response
            |> Option.orElseWith (fun () -> tryIntProperty "exitCode" response)
            |> Option.orElseWith (fun () -> tryIntProperty "StatusCode" response)
            |> Option.orElseWith (fun () -> tryIntProperty "statusCode" response)

        let diagnosticsCount =
            tryArrayCount "Errors" response
            |> Option.orElseWith (fun () -> tryArrayCount "errors" response)
            |> Option.orElseWith (fun () -> tryArrayCount "Diagnostics" response)
            |> Option.orElseWith (fun () -> tryArrayCount "diagnostics" response)

        let status =
            match exitCode, diagnosticsCount with
            | Some 0, _ -> "succeeded"
            | Some _, _ -> "failed"
            | None, Some 0 -> "succeeded"
            | None, Some _ -> "failed"
            | None, None -> "unknown"

        status, exitCode, diagnosticsCount

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

// ─── WorkspaceLoadTarget: tracks FsAutoComplete workspace load notifications ───

type private WorkspaceLoadTarget(setReady: unit -> unit) =
    let notifyIfReady (payload: JsonElement) =
        if WorkspaceNotification.isWorkspaceLoadFinished payload then
            setReady ()

    [<JsonRpcMethod("fsharp/notifyWorkspace", UseSingleObjectParameterDeserialization = true)>]
    member _.NotifyWorkspace(payload: JsonElement) = notifyIfReady payload

    [<JsonRpcMethod("fsharp/workspaceLoad", UseSingleObjectParameterDeserialization = true)>]
    member _.WorkspaceLoad(payload: JsonElement) = notifyIfReady payload

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
        workspaceReadyEvent.Reset()

    member _.DiagnosticsStore = diagnostics

    member _.CurrentProjectPath = runtimeProjectPath

    member _.CurrentWorkspaceRoot = runtimeWorkspaceRoot

    member _.IsWorkspaceReady = workspaceReady

    member _.DiagnosticsFileCount = diagnostics.Count

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
            let projectPath = Path.GetFullPath(args.projectPath)

            if not (File.Exists(projectPath) || Directory.Exists(projectPath)) then
                invalidArg (nameof args.projectPath) $"Path does not exist: %s{projectPath}"

            // Detect solution files
            let isSolution =
                projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || projectPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)

            let resolvedWorkspace =
                if isSolution then
                    // For solution files use the solution's directory
                    Path.GetDirectoryName(projectPath)
                else
                    args.workspacePath
                    |> Option.map Path.GetFullPath
                    |> Option.defaultWith (fun () -> resolveWorkspaceFromProjectPath projectPath)

            let restartLsp = args.restartLsp |> Option.defaultValue true
            do! gate.WaitAsync()

            let capturedProjectPath, capturedWorkspaceRoot =
                try
                    if isSolution then
                        runtimeProjectPath <- None
                        runtimeWorkspaceRoot <- Some resolvedWorkspace
                    else
                        runtimeProjectPath <- Some projectPath
                        runtimeWorkspaceRoot <- Some resolvedWorkspace

                    Environment.SetEnvironmentVariable("FSA_PROJECT_PATH", runtimeProjectPath |> Option.defaultValue "")
                    Environment.SetEnvironmentVariable("FSA_WORKSPACE_ROOT", resolvedWorkspace)

                    runtimeProjectPath, resolvedWorkspace
                with ex ->
                    gate.Release() |> ignore
                    reraise ()

            // Call StartLspUnsafe while still holding the gate — atomic stop+start
            if restartLsp then
                try
                    this.StopLspUnsafe()
                    let! _ = this.StartLspUnsafe()
                    ()
                finally
                    gate.Release() |> ignore
            else
                gate.Release() |> ignore

            // Wait for workspace to be ready outside the gate
            if restartLsp then
                let! ready = this.WaitForReady(TimeSpan.FromSeconds(30.0))
                let loadStatus = if ready then "ready" else "timeout"

                return
                    jobj
                        [ "status", jstr "ok"
                          "result",
                          jobj
                              [ "projectPath", capturedProjectPath |> Option.map jstr |> Option.defaultValue null
                                "workspaceRoot", jstr capturedWorkspaceRoot
                                "lspRestarted", jbool restartLsp
                                "solutionMode", jbool isSolution
                                "workspaceLoadStatus", jstr loadStatus ]
                          :> JsonNode ]
                    :> JsonNode
            else
                return
                    jobj
                        [ "status", jstr "ok"
                          "result",
                          jobj
                              [ "projectPath", capturedProjectPath |> Option.map jstr |> Option.defaultValue null
                                "workspaceRoot", jstr capturedWorkspaceRoot
                                "lspRestarted", jbool restartLsp
                                "solutionMode", jbool isSolution
                                "workspaceLoadStatus", jstr "not_started" ]
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

            jsonRpc.AddLocalRpcTarget(
                new WorkspaceLoadTarget(fun () ->
                    workspaceReady <- true
                    workspaceReadyEvent.Set()),
                targetOptions
            )
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
                      "initializationOptions", jobj [ "AutomaticWorkspaceInit", jbool true ]
                      "workspaceFolders",
                      JsonArray(jobj [ "uri", jstr rootUri; "name", jstr workspaceName ] :> JsonNode) :> JsonNode
                      "capabilities",
                      jobj
                          [ "workspace", jobj [ "workspaceFolders", jbool true ]
                            "textDocument",
                            jobj [ "completion", jobj [ "completionItem", jobj [ "snippetSupport", jbool true ] ] ] ] ]

            let! _ = jsonRpc.InvokeWithParameterObjectAsync<JsonNode>("initialize", initializeParams)
            do! jsonRpc.NotifyWithParameterObjectAsync("initialized", JsonObject())

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
                return jobj [ "status", jstr "ok"; "result", response ] :> JsonNode
        }

    member this.Compile(args: FSharpCompileArgs) : Task<JsonNode> =
        task {
            let projectPath = Path.GetFullPath(args.projectPath)

            if not (File.Exists projectPath) then
                invalidArg (nameof args.projectPath) $"Project file does not exist: %s{projectPath}"

            let! jsonRpc = this.EnsureStarted()

            if not workspaceReady then
                return this.NotReadyResponse()
            else
                let timeoutMs = args.timeoutMs |> Option.defaultValue 60000

                let payloads =
                    [| jobj [ "Project", jstr projectPath ]
                       jobj [ "Project", jstr (toFileUri projectPath) ]
                       jobj [ "project", jstr projectPath ] |]

                let invokeWithTimeout (payload: JsonObject) =
                    task {
                        let request =
                            jsonRpc.InvokeWithParameterObjectAsync<JsonNode>("fsharp/compile", payload)

                        let! completed = Task.WhenAny(request, Task.Delay(timeoutMs))

                        if Object.ReferenceEquals(completed, request) then
                            let! result = request
                            return Ok result
                        else
                            return Error $"fsharp/compile timed out after %d{timeoutMs}ms"
                    }

                let mutable compileResult: Result<JsonNode, string> =
                    Error "No compile payload was attempted."

                let mutable payloadIndex = 0

                while payloadIndex < payloads.Length && Result.isError compileResult do
                    try
                        let! attempt = invokeWithTimeout payloads[payloadIndex]
                        compileResult <- attempt
                    with ex ->
                        compileResult <- Error ex.Message

                    payloadIndex <- payloadIndex + 1

                match compileResult with
                | Error error ->
                    return
                        jobj
                            [ "status", jstr "failed"
                              "backend", jstr "fsautocomplete"
                              "projectPath", jstr projectPath
                              "error", jstr error ]
                        :> JsonNode
                | Ok response ->
                    let compileStatus, exitCode, diagnosticsCount = CompileResult.classify response

                    return
                        jobj
                            [ "status", jstr compileStatus
                              "backend", jstr "fsautocomplete"
                              "projectPath", jstr projectPath
                              "exitCode", exitCode |> Option.map jint |> Option.defaultValue null
                              "diagnosticsCount", diagnosticsCount |> Option.map jint |> Option.defaultValue null
                              "result", response.DeepClone() ]
                        :> JsonNode
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
                return jobj [ "status", jstr "ok"; "result", resolveByUri uri ] :> JsonNode
            | None ->
                let root = JsonObject()

                for KeyValue(uri, payload) in diagnostics do
                    root[uri] <- payload.DeepClone()

                return jobj [ "status", jstr "ok"; "result", root :> JsonNode ] :> JsonNode
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
                let pos = jobj [ "line", jint args.line; "character", jint args.character ]

                jobj
                    [ "textDocument", jobj [ "uri", jstr uri ]
                      "range", jobj [ "start", pos :> JsonNode; "end", pos :> JsonNode ]
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
