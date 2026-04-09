open System
open System.IO
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharp.Compiler.EditorServices
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open StreamJsonRpc

// ─── Shared arg types ──────────────────────────────────────────────────────────

type CompletionArgs =
    { path: string
      line: int
      character: int
      text: string option
      triggerCharacter: string option }

type PositionArgs =
    { path: string
      line: int
      character: int
      text: string option }

type ReferencesArgs =
    { path: string
      line: int
      character: int
      includeDeclaration: bool option
      text: string option }

type WorkspaceSymbolArgs = { query: string }
type DiagnosticsArgs = { path: string option }

type SetProjectArgs =
    { projectPath: string
      workspacePath: string option
      restartLsp: bool option }

type FcsParseAndCheckArgs =
    { path: string
      text: string option
      projectPath: string option
      projectOptions: string list option }

type FcsFileSymbolsArgs =
    { path: string
      text: string option
      projectPath: string option
      projectOptions: string list option
      includeAllUses: bool option
      maxResults: int option }

type FcsProjectSymbolUsesArgs =
    { path: string
      text: string option
      projectPath: string option
      projectOptions: string list option
      symbolQuery: string
      exact: bool option
      maxResults: int option }

// ─── New arg types ─────────────────────────────────────────────────────────────

type FcsTypeAtPositionArgs =
    { path: string
      line: int
      character: int
      text: string option
      projectPath: string option
      projectOptions: string list option }

type FcsSignatureHelpArgs =
    { path: string
      line: int
      character: int
      text: string option
      projectPath: string option
      projectOptions: string list option }

type FormattingArgs =
    { path: string
      text: string option }

type CodeActionArgs =
    { path: string
      line: int
      character: int
      text: string option }

type RenameArgs =
    { path: string
      line: int
      character: int
      newName: string
      text: string option }

// ─── Helper: find nearest .fsproj ──────────────────────────────────────────────

let private findNearestFsproj (filePath: string) : string option =
    let rec walk (dir: string) =
        if isNull dir then None
        else
            let fsprojs = Directory.GetFiles(dir, "*.fsproj")
            if fsprojs.Length > 0 then Some fsprojs[0]
            else walk (Path.GetDirectoryName(dir))
    walk (Path.GetDirectoryName(Path.GetFullPath(filePath)))

// ─── LSP types ─────────────────────────────────────────────────────────────────

type private LspDocumentState =
    { mutable Version: int
      mutable Text: string }

type private DiagnosticsTarget(store: ConcurrentDictionary<string, JToken>) =
    [<JsonRpcMethod("textDocument/publishDiagnostics")>]
    member _.PublishDiagnostics(payload: JObject) =
        let uriToken = payload["uri"]

        if not (isNull uriToken) then
            let uri = uriToken.Value<string>()
            let diagnostics = payload["diagnostics"]
            store[uri] <- if isNull diagnostics then JArray() :> JToken else diagnostics.DeepClone()

// ─── WorkspaceLoadTarget: tracks fsharp/workspaceLoad notification ─────────────

type private WorkspaceLoadTarget(setReady: unit -> unit) =
    [<JsonRpcMethod("fsharp/workspaceLoad")>]
    member _.WorkspaceLoad(payload: JObject) =
        let statusToken = payload["status"]
        if not (isNull statusToken) then
            let status = statusToken.Value<string>()
            if String.Equals(status, "finished", StringComparison.OrdinalIgnoreCase) then
                setReady ()

// ─── FsAutoCompleteBridge ──────────────────────────────────────────────────────

type private FsAutoCompleteBridge() =
    let gate = new SemaphoreSlim(1, 1)
    let documents = ConcurrentDictionary<string, LspDocumentState>()
    let diagnostics = ConcurrentDictionary<string, JToken>()
    [<VolatileField>]
    let mutable rpc: JsonRpc option = None
    let mutable lspProcess: Process option = None
    let mutable runtimeProjectPath: string option = None
    let mutable runtimeWorkspaceRoot: string option = None
    let workspaceReadyEvent = new ManualResetEventSlim(false)
    [<VolatileField>]
    let mutable workspaceReady = false

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
            if String.IsNullOrWhiteSpace(parent) then Directory.GetCurrentDirectory() else parent

    let getWorkspaceRoot () =
        let fromRuntimeWorkspace = runtimeWorkspaceRoot |> Option.map Path.GetFullPath

        let fromWorkspaceRootEnv =
            Environment.GetEnvironmentVariable("FSA_WORKSPACE_ROOT")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map Path.GetFullPath

        let fromRuntimeProject = runtimeProjectPath |> Option.map resolveWorkspaceFromProjectPath

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
        Environment.GetEnvironmentVariable("FSAC_ARGS")
        |> Option.ofObj
        |> parseArgs

    let toFileUri (path: string) =
        let fullPath = Path.GetFullPath(path)
        Uri(fullPath).AbsoluteUri

    let jobj (props: (string * JToken) list) =
        let result = JObject()

        for (key, value) in props do
            result[key] <- value

        result

    let jstr (value: string) = JValue(value) :> JToken
    let jint (value: int) = JValue(value) :> JToken
    let jbool (value: bool) = JValue(value) :> JToken

    member private _.StopLspUnsafe() =
        match rpc with
        | Some instance ->
            instance.Dispose()
            rpc <- None
        | None -> ()

        match lspProcess with
        | Some fsacProc when not fsacProc.HasExited ->
            fsacProc.Kill(true)
            fsacProc.Dispose()
            lspProcess <- None
        | Some fsacProc ->
            fsacProc.Dispose()
            lspProcess <- None
        | None -> ()

        documents.Clear()
        diagnostics.Clear()
        workspaceReady <- false
        workspaceReadyEvent.Reset()

    member _.DiagnosticsStore = diagnostics

    // Wait until workspaceReady is true or timeout elapses
    member _.WaitForReady(timeout: TimeSpan) : Task<bool> =
        task {
            return workspaceReadyEvent.Wait(timeout)
        }

    // Return not-ready JSON if workspace not loaded yet
    member _.NotReadyResponse() : JToken =
        JObject(
            JProperty("status", "not_ready"),
            JProperty("message", "fsautocomplete is still loading the project. Try again in a moment.")
        )
        :> JToken

    member this.SetProject(args: SetProjectArgs) : Task<JToken> =
        task {
            let projectPath = Path.GetFullPath(args.projectPath)

            if not (File.Exists(projectPath) || Directory.Exists(projectPath)) then
                invalidArg (nameof args.projectPath) $"Path does not exist: {projectPath}"

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

                    Environment.SetEnvironmentVariable(
                        "FSA_PROJECT_PATH",
                        runtimeProjectPath |> Option.defaultValue "")
                    Environment.SetEnvironmentVariable("FSA_WORKSPACE_ROOT", resolvedWorkspace)

                    if restartLsp then
                        this.StopLspUnsafe()

                    runtimeProjectPath, resolvedWorkspace
                finally
                    gate.Release() |> ignore

            // After releasing gate, wait for workspace to be ready if we restarted
            if restartLsp then
                // Trigger EnsureStarted so we begin LSP and start listening for workspaceLoad
                let! _ = this.EnsureStarted()
                let! ready = this.WaitForReady(TimeSpan.FromSeconds(30.0))
                let loadStatus = if ready then "ready" else "timeout"

                return
                    JObject(
                        JProperty("status", "ok"),
                        JProperty("projectPath",
                            capturedProjectPath |> Option.map JValue |> Option.defaultValue (JValue.CreateNull()) :> JToken),
                        JProperty("workspaceRoot", capturedWorkspaceRoot),
                        JProperty("lspRestarted", restartLsp),
                        JProperty("solutionMode", isSolution),
                        JProperty("workspaceLoadStatus", loadStatus)
                    )
                    :> JToken
            else
                return
                    JObject(
                        JProperty("status", "ok"),
                        JProperty("projectPath",
                            capturedProjectPath |> Option.map JValue |> Option.defaultValue (JValue.CreateNull()) :> JToken),
                        JProperty("workspaceRoot", capturedWorkspaceRoot),
                        JProperty("lspRestarted", restartLsp),
                        JProperty("solutionMode", isSolution),
                        JProperty("workspaceLoadStatus", "not_started")
                    )
                    :> JToken
        }

    member private this.EnsureStarted() : Task<JsonRpc> =
        task {
            match rpc with
            | Some existing -> return existing
            | None ->
                do! gate.WaitAsync()

                try
                    match rpc with
                    | Some existing -> return existing
                    | None ->
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
                            invalidOp $"Unable to start {command}"

                        let pumpStderr =
                            task {
                                let mutable keepReading = true

                                while keepReading do
                                    let! line = fsacProc.StandardError.ReadLineAsync()

                                    if isNull line then
                                        keepReading <- false
                                    elif not (String.IsNullOrWhiteSpace line) then
                                        Console.Error.WriteLine($"[fsautocomplete] {line}")
                            }

                        pumpStderr |> ignore

                        let formatter = new JsonMessageFormatter()
                        formatter.JsonSerializer.NullValueHandling <- NullValueHandling.Ignore

                        let handler =
                            new HeaderDelimitedMessageHandler(
                                fsacProc.StandardInput.BaseStream,
                                fsacProc.StandardOutput.BaseStream,
                                formatter
                            )

                        let jsonRpc = new JsonRpc(handler)
                        jsonRpc.AddLocalRpcTarget(new DiagnosticsTarget(diagnostics)) |> ignore
                        jsonRpc.AddLocalRpcTarget(new WorkspaceLoadTarget(fun () ->
                            workspaceReady <- true
                            workspaceReadyEvent.Set())) |> ignore
                        jsonRpc.StartListening()

                        let rootUri = Uri(workspaceRoot).AbsoluteUri
                        let workspaceName = Path.GetFileName(workspaceRoot)

                        let initializeParams =
                            jobj
                                [ "processId", jint Environment.ProcessId
                                  "rootUri", jstr rootUri
                                  "trace", jstr "off"
                                  "clientInfo", jobj [ "name", jstr "fsmcp-fsharp"; "version", jstr "0.1.0" ]
                                  "workspaceFolders",
                                  JArray(jobj [ "uri", jstr rootUri; "name", jstr workspaceName ]) :> JToken
                                  "capabilities",
                                  jobj
                                      [ "workspace", jobj [ "workspaceFolders", jbool true ]
                                        "textDocument",
                                        jobj
                                            [ "completion",
                                              jobj [ "completionItem", jobj [ "snippetSupport", jbool true ] ] ] ] ]

                        let! _ = jsonRpc.InvokeWithParameterObjectAsync<JToken>("initialize", initializeParams)
                        do! jsonRpc.NotifyWithParameterObjectAsync("initialized", JObject())

                        rpc <- Some jsonRpc
                        lspProcess <- Some fsacProc
                        return jsonRpc
                finally
                    gate.Release() |> ignore
        }

    member private this.SyncDocument
        (jsonRpc: JsonRpc, path: string, providedText: string option)
        : Task<string> =
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
                          "contentChanges", JArray(jobj [ "text", jstr text ]) :> JToken ]

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
        (path: string, providedText: string option, methodName: string, mkParams: string -> JObject)
        : Task<JToken> =
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
            let! response = jsonRpc.InvokeWithParameterObjectAsync<JToken>(methodName, parameters)
            return response
        }

    member private this.PositionParams(uri: string, line: int, character: int) =
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
                    parameters["context"] <- jobj [ "triggerKind", jint 2; "triggerCharacter", jstr ch ]
                    parameters
                | None -> parameters
        )

    member this.Hover(args: PositionArgs) =
        this.WithDocument(args.path, args.text, "textDocument/hover", fun uri ->
            this.PositionParams(uri, args.line, args.character))

    member this.Definition(args: PositionArgs) =
        this.WithDocument(args.path, args.text, "textDocument/definition", fun uri ->
            this.PositionParams(uri, args.line, args.character))

    member this.References(args: ReferencesArgs) =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/references",
            fun uri ->
                let baseParams = this.PositionParams(uri, args.line, args.character)

                baseParams["context"] <-
                    jobj
                        [ "includeDeclaration",
                          jbool (args.includeDeclaration |> Option.defaultValue false) ]

                baseParams
        )

    member this.WorkspaceSymbol(args: WorkspaceSymbolArgs) : Task<JToken> =
        task {
            let! jsonRpc = this.EnsureStarted()

            if not workspaceReady then
                return this.NotReadyResponse()
            else

            // No document sync needed — just invoke directly, no gate
            let parameters = jobj [ "query", jstr args.query ]
            let! response = jsonRpc.InvokeWithParameterObjectAsync<JToken>("workspace/symbol", parameters)
            return response
        }

    member _.Diagnostics(args: DiagnosticsArgs) : Task<JToken> =
        task {
            let resolveByUri (uri: string) =
                match diagnostics.TryGetValue(uri) with
                | true, payload -> payload.DeepClone()
                | false, _ -> JArray() :> JToken

            match args.path with
            | Some path ->
                let uri = toFileUri path
                return resolveByUri uri
            | None ->
                let root = JObject()

                for KeyValue(uri, payload) in diagnostics do
                    root[uri] <- payload.DeepClone()

                return root :> JToken
        }

    member this.Formatting(args: FormattingArgs) : Task<JToken> =
        task {
            let! jsonRpc = this.EnsureStarted()

            if not workspaceReady then
                return this.NotReadyResponse()
            else

            let fullPath = Path.GetFullPath(args.path)
            let originalText = args.text |> Option.defaultWith (fun () -> File.ReadAllText(fullPath))

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
                JObject(
                    JProperty("textDocument", JObject(JProperty("uri", uri))),
                    JProperty("options", JObject(
                        JProperty("tabSize", 4),
                        JProperty("insertSpaces", true)))
                )

            let! editsToken = jsonRpc.InvokeWithParameterObjectAsync<JToken>("textDocument/formatting", formatParams)

            // Apply text edits to produce formatted result
            let applyEdits (text: string) (edits: JArray) =
                // Sort edits in reverse order (bottom to top) to preserve positions
                let getRangeStart (e: JToken) =
                    let r = e["range"]
                    let s = r["start"]
                    s["line"].Value<int>(), s["character"].Value<int>()

                let sortedEdits =
                    edits
                    |> Seq.cast<JToken>
                    |> Seq.sortByDescending (fun e ->
                        let sl, sc = getRangeStart e
                        sl, sc)
                    |> Seq.toArray

                let lines = text.Split('\n') |> Array.map (fun l -> l.TrimEnd('\r'))

                let applyEdit (linesArr: string array) (edit: JToken) =
                    let range = edit["range"]
                    let startPos = range["start"]
                    let endPos = range["end"]
                    let startLine = startPos["line"].Value<int>()
                    let startChar = startPos["character"].Value<int>()
                    let endLine = endPos["line"].Value<int>()
                    let endChar = endPos["character"].Value<int>()
                    let newText = edit["newText"].Value<string>()

                    let before =
                        if startLine < linesArr.Length then
                            linesArr[startLine][..startChar - 1]
                        else ""
                    let after =
                        if endLine < linesArr.Length then
                            let endLineText = linesArr[endLine]
                            if endChar < endLineText.Length then endLineText[endChar..] else ""
                        else ""

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
                | :? JArray as edits when edits.Count > 0 ->
                    applyEdits originalText edits
                | _ -> originalText

            return
                JObject(
                    JProperty("status", "ok"),
                    JProperty("formatted", formatted),
                    JProperty("edits", if editsToken :? JArray then editsToken else JArray() :> JToken)
                )
                :> JToken
        }

    member this.CodeAction(args: CodeActionArgs) : Task<JToken> =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/codeAction",
            fun uri ->
                let pos = JObject(JProperty("line", args.line), JProperty("character", args.character))
                JObject(
                    JProperty("textDocument", JObject(JProperty("uri", uri))),
                    JProperty("range", JObject(JProperty("start", pos), JProperty("end", pos))),
                    JProperty("context", JObject(JProperty("diagnostics", JArray())))
                )
        )

    member this.Rename(args: RenameArgs) : Task<JToken> =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/rename",
            fun uri ->
                JObject(
                    JProperty("textDocument", JObject(JProperty("uri", uri))),
                    JProperty("position", JObject(JProperty("line", args.line), JProperty("character", args.character))),
                    JProperty("newName", args.newName)
                )
        )

    interface IDisposable with
        member this.Dispose() =
            this.StopLspUnsafe()
            gate.Dispose()

// ─── FcsBridge ─────────────────────────────────────────────────────────────────

type private FcsBridge() =
    let checker =
        FSharpChecker.Create(
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = true,
            keepAllBackgroundSymbolUses = true
        )

    // Caches keyed by a string combining projectPath + projectOptions hash
    let optionsCache = ConcurrentDictionary<string, FSharpProjectOptions>()
    let projectResultsCache = ConcurrentDictionary<string, FSharpCheckProjectResults>()

    let asTask (workflow: Async<'T>) : Task<'T> =
        Async.StartAsTask(workflow, cancellationToken = CancellationToken.None)

    let normalizePath (path: string) = Path.GetFullPath(path)

    let jstrOrNull (value: string) =
        if String.IsNullOrWhiteSpace(value) then JValue.CreateNull() :> JToken else JValue(value) :> JToken

    let rangeToJson (r: range) =
        JObject(
            JProperty("file", normalizePath r.FileName),
            JProperty("startLine", r.StartLine),
            JProperty("startColumn", r.StartColumn),
            JProperty("endLine", r.EndLine),
            JProperty("endColumn", r.EndColumn)
        )
        :> JToken

    let positionToJson (p: Position) =
        JObject(JProperty("line", p.Line), JProperty("column", p.Column)) :> JToken

    let diagnosticToJson (d: FSharpDiagnostic) =
        JObject(
            JProperty("file", normalizePath d.FileName),
            JProperty("message", d.Message),
            JProperty("severity", d.Severity.ToString()),
            JProperty("errorNumber", d.ErrorNumber),
            JProperty("errorNumberText", d.ErrorNumberText),
            JProperty("subcategory", d.Subcategory),
            JProperty("range", rangeToJson d.Range),
            JProperty("start", positionToJson d.Start),
            JProperty("end", positionToJson d.End)
        )
        :> JToken

    let symbolToJson (symbol: FSharpSymbol) =
        let declarationLocation =
            match symbol.DeclarationLocation with
            | Some r -> rangeToJson r
            | None -> JValue.CreateNull() :> JToken

        JObject(
            JProperty("displayName", symbol.DisplayName),
            JProperty("fullName", jstrOrNull symbol.FullName),
            JProperty("assembly", symbol.Assembly.SimpleName),
            JProperty("declarationLocation", declarationLocation),
            JProperty("isExplicitlySuppressed", symbol.IsExplicitlySuppressed)
        )
        :> JToken

    let symbolUseToJson (symbolUse: FSharpSymbolUse) =
        JObject(
            JProperty("file", normalizePath symbolUse.FileName),
            JProperty("range", rangeToJson symbolUse.Range),
            JProperty("isFromDefinition", symbolUse.IsFromDefinition),
            JProperty("isFromUse", symbolUse.IsFromUse),
            JProperty("isFromPattern", symbolUse.IsFromPattern),
            JProperty("isFromAttribute", symbolUse.IsFromAttribute),
            JProperty("symbol", symbolToJson symbolUse.Symbol)
        )
        :> JToken

    // Build a stable cache key from projectPath and projectOptions list
    let makeCacheKey (projectPath: string option) (projectOptions: string list option) =
        let pp = projectPath |> Option.defaultValue ""
        let po =
            projectOptions
            |> Option.map (fun opts -> String.concat "|" opts)
            |> Option.defaultValue ""
        $"{pp}::{po}"

    member private _.ResolveProjectOptions
        (path: string, text: string, projectPath: string option, projectOptions: string list option)
        : Task<FSharpProjectOptions * string> =
        task {
            let fullPath = normalizePath path
            let cacheKey = makeCacheKey projectPath projectOptions

            match projectOptions with
            | Some options when not options.IsEmpty ->
                let projectFileName =
                    projectPath
                    |> Option.defaultValue (Path.ChangeExtension(fullPath, ".fsproj"))
                    |> normalizePath

                match optionsCache.TryGetValue(cacheKey) with
                | true, cached ->
                    return cached, "commandLineArgs"
                | false, _ ->
                    let resolvedOptions =
                        checker.GetProjectOptionsFromCommandLineArgs(projectFileName, options |> List.toArray)
                    optionsCache[cacheKey] <- resolvedOptions
                    return resolvedOptions, "commandLineArgs"
            | _ ->
                // Try auto-discover .fsproj
                let discoveredFsproj = findNearestFsproj fullPath
                match discoveredFsproj with
                | Some fsprojPath ->
                    let fsprojKey = $"fsproj::{fsprojPath}"
                    match optionsCache.TryGetValue(fsprojKey) with
                    | true, cached ->
                        return cached, "auto-discovered"
                    | false, _ ->
                        // Use GetProjectOptionsFromScript as fallback since we can't easily
                        // call Ionide's MSBuild evaluation without a proper toolsPath setup
                        let sourceText = SourceText.ofString text
                        let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask
                        // Override the project file name to the discovered .fsproj
                        let discovered =
                            { scriptOptions with
                                ProjectFileName = fsprojPath }
                        optionsCache[fsprojKey] <- discovered
                        return discovered, "auto-discovered-script-fallback"
                | None ->
                    let scriptKey = $"script::{fullPath}"
                    match optionsCache.TryGetValue(scriptKey) with
                    | true, cached ->
                        return cached, "scriptInference"
                    | false, _ ->
                        let sourceText = SourceText.ofString text
                        let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask
                        optionsCache[scriptKey] <- scriptOptions
                        return scriptOptions, "scriptInference"
        }

    member private this.PrepareCheckContext
        (path: string, text: string option, projectPath: string option, projectOptions: string list option)
        : Task<
            string
            * string
            * string
            * FSharpProjectOptions
            * FSharpParseFileResults
            * FSharpCheckFileResults option
        > =
        task {
            let fullPath = normalizePath path
            let source = text |> Option.defaultWith (fun () -> File.ReadAllText(fullPath))
            let sourceText = SourceText.ofString source
            let! options, optionsSource = this.ResolveProjectOptions(fullPath, source, projectPath, projectOptions)
            let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)
            let! parseResults = checker.ParseFile(fullPath, sourceText, parsingOptions) |> asTask
            let! _, checkAnswer = checker.ParseAndCheckFileInProject(fullPath, 0, sourceText, options) |> asTask

            let checkedResults =
                match checkAnswer with
                | FSharpCheckFileAnswer.Succeeded results -> Some results
                | FSharpCheckFileAnswer.Aborted -> None

            return fullPath, source, optionsSource, options, parseResults, checkedResults
        }

    member this.ParseAndCheckFile(args: FcsParseAndCheckArgs) : Task<JToken> =
        task {
            let! path, _, optionsSource, projectOptions, parseResults, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            let parseDiagnostics = parseResults.Diagnostics |> Array.map diagnosticToJson
            let checkDiagnostics =
                checkedResults
                |> Option.map (fun r -> r.Diagnostics |> Array.map diagnosticToJson)
                |> Option.defaultValue [||]

            let hasTypeCheckInfo = checkedResults |> Option.map _.HasFullTypeCheckInfo |> Option.defaultValue false
            let status = if checkedResults.IsSome then "succeeded" else "aborted"

            return
                JObject(
                    JProperty("status", status),
                    JProperty("file", path),
                    JProperty("optionsSource", optionsSource),
                    JProperty("projectFileName", projectOptions.ProjectFileName),
                    JProperty("projectSourceFiles", JArray(projectOptions.SourceFiles)),
                    JProperty("parseHadErrors", parseResults.ParseHadErrors),
                    JProperty("hasFullTypeCheckInfo", hasTypeCheckInfo),
                    JProperty("parseDiagnostics", JArray(parseDiagnostics)),
                    JProperty("checkDiagnostics", JArray(checkDiagnostics))
                )
                :> JToken
        }

    member this.FileSymbols(args: FcsFileSymbolsArgs) : Task<JToken> =
        task {
            let! path, _, optionsSource, _, parseResults, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None ->
                return
                    JObject(
                        JProperty("status", "aborted"),
                        JProperty("file", path),
                        JProperty("optionsSource", optionsSource),
                        JProperty("parseHadErrors", parseResults.ParseHadErrors),
                        JProperty("message", "Type checking was aborted. Symbols are unavailable."),
                        JProperty("parseDiagnostics", JArray(parseResults.Diagnostics |> Array.map diagnosticToJson))
                    )
                    :> JToken
            | Some checkResults ->
                let includeAllUses = args.includeAllUses |> Option.defaultValue false
                let maxResults = args.maxResults |> Option.defaultValue 200

                let symbols =
                    checkResults.GetAllUsesOfAllSymbolsInFile()
                    |> Seq.filter (fun symbolUse -> includeAllUses || symbolUse.IsFromDefinition)
                    |> Seq.distinctBy (fun symbolUse ->
                        symbolUse.Symbol.FullName,
                        symbolUse.Range.StartLine,
                        symbolUse.Range.StartColumn,
                        symbolUse.Range.EndLine,
                        symbolUse.Range.EndColumn)
                    |> Seq.truncate maxResults
                    |> Seq.map symbolUseToJson
                    |> Seq.toArray

                return
                    JObject(
                        JProperty("status", "succeeded"),
                        JProperty("file", path),
                        JProperty("optionsSource", optionsSource),
                        JProperty("includeAllUses", includeAllUses),
                        JProperty("count", symbols.Length),
                        JProperty("symbols", JArray(symbols)),
                        JProperty("parseDiagnostics", JArray(parseResults.Diagnostics |> Array.map diagnosticToJson)),
                        JProperty("checkDiagnostics", JArray(checkResults.Diagnostics |> Array.map diagnosticToJson))
                    )
                    :> JToken
        }

    member this.ProjectSymbolUses(args: FcsProjectSymbolUsesArgs) : Task<JToken> =
        task {
            let query = args.symbolQuery.Trim()

            if String.IsNullOrWhiteSpace(query) then
                invalidArg (nameof args.symbolQuery) "symbolQuery must be non-empty."

            let! _, _, optionsSource, projectOptions, _, _ =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            // Use cached project results if available
            let cacheKey = makeCacheKey args.projectPath args.projectOptions
            let! projectResults, cached =
                task {
                    match projectResultsCache.TryGetValue(cacheKey) with
                    | true, existing ->
                        return existing, true
                    | false, _ ->
                        let! results = checker.ParseAndCheckProject(projectOptions) |> asTask
                        projectResultsCache[cacheKey] <- results
                        return results, false
                }

            let exact = args.exact |> Option.defaultValue false
            let maxResults = args.maxResults |> Option.defaultValue 500

            let symbolMatches (symbol: FSharpSymbol) =
                let displayName = symbol.DisplayName
                let fullName = symbol.FullName

                if exact then
                    String.Equals(displayName, query, StringComparison.Ordinal)
                    || String.Equals(fullName, query, StringComparison.Ordinal)
                else
                    displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (if isNull fullName then
                            false
                        else
                            fullName.Contains(query, StringComparison.OrdinalIgnoreCase))

            let allUses = projectResults.GetAllUsesOfAllSymbols()

            let matchedUses =
                allUses
                |> Seq.filter (fun symbolUse -> symbolMatches symbolUse.Symbol)
                |> Seq.sortBy (fun symbolUse -> symbolUse.FileName, symbolUse.Range.StartLine, symbolUse.Range.StartColumn)
                |> Seq.truncate maxResults
                |> Seq.map symbolUseToJson
                |> Seq.toArray

            return
                JObject(
                    JProperty("status", "succeeded"),
                    JProperty("optionsSource", optionsSource),
                    JProperty("projectFileName", projectOptions.ProjectFileName),
                    JProperty("query", query),
                    JProperty("exact", exact),
                    JProperty("cached", cached),
                    JProperty("totalProjectSymbolUses", allUses.Length),
                    JProperty("matchedCount", matchedUses.Length),
                    JProperty("uses", JArray(matchedUses)),
                    JProperty("projectDiagnostics", JArray(projectResults.Diagnostics |> Array.map diagnosticToJson))
                )
                :> JToken
        }

    member this.TypeAtPosition(args: FcsTypeAtPositionArgs) : Task<JToken> =
        task {
            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None ->
                return
                    JObject(
                        JProperty("status", "aborted"),
                        JProperty("message", "Type checking was aborted.")
                    )
                    :> JToken
            | Some checkResults ->
                // FCS uses 1-based lines; assume input is 0-based (LSP convention)
                let fcsLine = args.line + 1
                let fcsCol = args.character

                // Get the line text for FCS APIs
                let lines = source.Split('\n')
                let lineText =
                    if fcsLine - 1 < lines.Length then lines[fcsLine - 1].TrimEnd('\r')
                    else ""

                let symbolUse =
                    checkResults.GetSymbolUseAtLocation(fcsLine, fcsCol, lineText, [])

                let toolTip =
                    checkResults.GetToolTip(fcsLine, fcsCol, lineText, [], FSharp.Compiler.Tokenization.FSharpTokenTag.IDENT)

                let typeString =
                    match toolTip with
                    | ToolTipText elements ->
                        elements
                        |> List.choose (fun el ->
                            match el with
                            | ToolTipElement.Group items ->
                                items
                                |> List.map (fun item ->
                                    item.MainDescription
                                    |> Array.map (fun tagged -> tagged.Text)
                                    |> String.concat "")
                                |> Some
                            | _ -> None)
                        |> List.collect id
                        |> String.concat "\n"

                let xmlDoc =
                    match toolTip with
                    | ToolTipText elements ->
                        elements
                        |> List.choose (fun el ->
                            match el with
                            | ToolTipElement.Group items ->
                                items
                                |> List.choose (fun item ->
                                    match item.XmlDoc with
                                    | FSharpXmlDoc.FromXmlText xmlText ->
                                        Some (xmlText.GetXmlText())
                                    | _ -> None)
                                |> Some
                            | _ -> None)
                        |> List.collect id
                        |> String.concat "\n"

                match symbolUse with
                | None ->
                    return
                        JObject(
                            JProperty("status", "no_symbol"),
                            JProperty("message", "No symbol at this position"),
                            JProperty("file", path),
                            JProperty("line", args.line),
                            JProperty("character", args.character),
                            JProperty("typeString", typeString)
                        )
                        :> JToken
                | Some su ->
                    return
                        JObject(
                            JProperty("status", "ok"),
                            JProperty("file", path),
                            JProperty("line", args.line),
                            JProperty("character", args.character),
                            JProperty("optionsSource", optionsSource),
                            JProperty("symbolName", su.Symbol.DisplayName),
                            JProperty("fullName", jstrOrNull su.Symbol.FullName),
                            JProperty("typeString", typeString),
                            JProperty("xmlDoc", xmlDoc)
                        )
                        :> JToken
        }

    member _.ClearCaches() =
        optionsCache.Clear()
        projectResultsCache.Clear()

    member this.SignatureHelp(args: FcsSignatureHelpArgs) : Task<JToken> =
        task {
            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None ->
                return
                    JObject(
                        JProperty("status", "aborted"),
                        JProperty("message", "Type checking was aborted.")
                    )
                    :> JToken
            | Some checkResults ->
                // FCS uses 1-based lines; assume input is 0-based (LSP convention)
                let fcsLine = args.line + 1
                let fcsCol = args.character

                let lines = source.Split('\n')
                let lineText =
                    if fcsLine - 1 < lines.Length then lines[fcsLine - 1].TrimEnd('\r')
                    else ""

                let methodsOpt =
                    checkResults.GetMethodsAsSymbols(fcsLine, fcsCol, lineText, [])

                let overloads =
                    match methodsOpt with
                    | None -> [||]
                    | Some symbolUses ->
                        symbolUses
                        |> List.choose (fun su ->
                            match su.Symbol with
                            | :? FSharpMemberOrFunctionOrValue as m ->
                                let paramGroups = m.CurriedParameterGroups
                                let parameters =
                                    paramGroups
                                    |> Seq.collect id
                                    |> Seq.map (fun p ->
                                        JObject(
                                            JProperty("name", p.Name |> Option.defaultValue ""),
                                            JProperty("type", p.Type.BasicQualifiedName)
                                        )
                                        :> JToken)
                                    |> Seq.toArray

                                let returnType =
                                    try m.ReturnParameter.Type.BasicQualifiedName
                                    with _ -> ""

                                let signature =
                                    let paramStr =
                                        parameters
                                        |> Array.map (fun p ->
                                            let pName = p["name"].Value<string>()
                                            let pType = p["type"].Value<string>()
                                            $"{pName}: {pType}")
                                        |> String.concat ", "
                                    $"{m.DisplayName}({paramStr}) -> {returnType}"

                                Some (JObject(
                                    JProperty("signature", signature),
                                    JProperty("parameters", JArray(parameters)),
                                    JProperty("returnType", returnType)
                                )
                                :> JToken)
                            | _ -> None)
                        |> List.toArray

                if overloads.Length = 0 then
                    return
                        JObject(
                            JProperty("status", "no_overloads"),
                            JProperty("file", path),
                            JProperty("line", args.line),
                            JProperty("character", args.character)
                        )
                        :> JToken
                else
                    return
                        JObject(
                            JProperty("status", "ok"),
                            JProperty("file", path),
                            JProperty("line", args.line),
                            JProperty("character", args.character),
                            JProperty("optionsSource", optionsSource),
                            JProperty("overloads", JArray(overloads))
                        )
                        :> JToken
        }

// ─── MCP helpers ───────────────────────────────────────────────────────────────

let private renderToken (token: JToken) =
    token.ToString(Newtonsoft.Json.Formatting.Indented)

let private toolResult (work: Task<JToken>) : Task<Result<Content list, McpError>> =
    task {
        try
            let! payload = work
            return Ok [ Content.text (renderToken payload) ]
        with ex ->
            return Error(McpError.TransportError ex.Message)
    }

// ─── CLI helpers ───────────────────────────────────────────────────────────────

type private CliParseResult =
    | Start
    | BootstrapTools
    | ShowHelp of string
    | Fail of string

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
                version "0.1.0"

                tool (
                    TypedTool.define<CompletionArgs>
                        "textDocument_completion"
                        "Proxy to fsautocomplete textDocument/completion. line/character are 0-based."
                        (fun args -> toolResult (bridge.Completion args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<PositionArgs>
                        "textDocument_hover"
                        "Proxy to fsautocomplete textDocument/hover. line/character are 0-based."
                        (fun args -> toolResult (bridge.Hover args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<PositionArgs>
                        "textDocument_definition"
                        "Proxy to fsautocomplete textDocument/definition. line/character are 0-based."
                        (fun args -> toolResult (bridge.Definition args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<ReferencesArgs>
                        "textDocument_references"
                        "Proxy to fsautocomplete textDocument/references. line/character are 0-based."
                        (fun args -> toolResult (bridge.References args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<WorkspaceSymbolArgs>
                        "workspace_symbol"
                        "Proxy to fsautocomplete workspace/symbol."
                        (fun args -> toolResult (bridge.WorkspaceSymbol args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<DiagnosticsArgs>
                        "workspace_diagnostics"
                        "Returns latest cached publishDiagnostics payload (per file or full map)."
                        (fun args -> toolResult (bridge.Diagnostics args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<SetProjectArgs>
                        "set_project"
                        "Sets active project/workspace for fsautocomplete. Accepts .fsproj, .sln, .slnx, or directory. Waits up to 30s for workspace load."
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
                        "FCS parse+typecheck for a file. Use projectOptions for accurate fsproj/sln context."
                        (fun args -> toolResult (fcsBridge.ParseAndCheckFile args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsFileSymbolsArgs>
                        "fcs_file_symbols"
                        "FCS symbol extraction from a file (definitions by default, or all uses)."
                        (fun args -> toolResult (fcsBridge.FileSymbols args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsProjectSymbolUsesArgs>
                        "fcs_project_symbol_uses"
                        "FCS project-wide symbol use search by symbol name/fullname. Results are cached per project."
                        (fun args -> toolResult (fcsBridge.ProjectSymbolUses args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsTypeAtPositionArgs>
                        "fcs_type_at_position"
                        "Returns the F# type and symbol info at a cursor position. line/character are 0-based (LSP convention)."
                        (fun args -> toolResult (fcsBridge.TypeAtPosition args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSignatureHelpArgs>
                        "fcs_signature_help"
                        "Returns method signature and overloads at a cursor position. line/character are 0-based (LSP convention)."
                        (fun args -> toolResult (fcsBridge.SignatureHelp args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FormattingArgs>
                        "textDocument_formatting"
                        "Formats F# code via fsautocomplete/Fantomas. Returns formatted text and applied edits."
                        (fun args -> toolResult (bridge.Formatting args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<CodeActionArgs>
                        "textDocument_codeAction"
                        "Returns available code actions at a position via fsautocomplete. line/character are 0-based."
                        (fun args -> toolResult (bridge.CodeAction args))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RenameArgs>
                        "textDocument_rename"
                        "Renames a symbol at a position via fsautocomplete. line/character are 0-based."
                        (fun args -> toolResult (bridge.Rename args))
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
