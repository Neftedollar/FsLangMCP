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
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open StreamJsonRpc

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

type private FsAutoCompleteBridge() =
    let gate = new SemaphoreSlim(1, 1)
    let documents = ConcurrentDictionary<string, LspDocumentState>()
    let diagnostics = ConcurrentDictionary<string, JToken>()
    let mutable rpc: JsonRpc option = None
    let mutable lspProcess: Process option = None
    let mutable runtimeProjectPath: string option = None
    let mutable runtimeWorkspaceRoot: string option = None

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

    member _.DiagnosticsStore = diagnostics

    member this.SetProject(args: SetProjectArgs) : Task<JToken> =
        task {
            let projectPath = Path.GetFullPath(args.projectPath)

            if not (File.Exists(projectPath) || Directory.Exists(projectPath)) then
                invalidArg (nameof args.projectPath) $"Path does not exist: {projectPath}"

            let resolvedWorkspace =
                args.workspacePath
                |> Option.map Path.GetFullPath
                |> Option.defaultWith (fun () -> resolveWorkspaceFromProjectPath projectPath)

            let restartLsp = args.restartLsp |> Option.defaultValue true
            do! gate.WaitAsync()

            try
                runtimeProjectPath <- Some projectPath
                runtimeWorkspaceRoot <- Some resolvedWorkspace
                Environment.SetEnvironmentVariable("FSA_PROJECT_PATH", projectPath)
                Environment.SetEnvironmentVariable("FSA_WORKSPACE_ROOT", resolvedWorkspace)

                if restartLsp then
                    this.StopLspUnsafe()

                return
                    JObject(
                        JProperty("status", "ok"),
                        JProperty("projectPath", projectPath),
                        JProperty("workspaceRoot", resolvedWorkspace),
                        JProperty("lspRestarted", restartLsp)
                    )
                    :> JToken
            finally
                gate.Release() |> ignore
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

    member private this.WithDocument
        (path: string, providedText: string option, methodName: string, mkParams: string -> JObject)
        : Task<JToken> =
        task {
            let! jsonRpc = this.EnsureStarted()
            do! gate.WaitAsync()

            try
                let! uri = this.SyncDocument(jsonRpc, path, providedText)
                let parameters = mkParams uri
                let! response = jsonRpc.InvokeWithParameterObjectAsync<JToken>(methodName, parameters)
                return response
            finally
                gate.Release() |> ignore
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
            do! gate.WaitAsync()

            try
                let parameters = jobj [ "query", jstr args.query ]
                let! response = jsonRpc.InvokeWithParameterObjectAsync<JToken>("workspace/symbol", parameters)
                return response
            finally
                gate.Release() |> ignore
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

    interface IDisposable with
        member this.Dispose() =
            this.StopLspUnsafe()
            gate.Dispose()

type private FcsBridge() =
    let checker =
        FSharpChecker.Create(
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = true,
            keepAllBackgroundSymbolUses = true
        )

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

    member private _.ResolveProjectOptions
        (path: string, text: string, projectPath: string option, projectOptions: string list option)
        : Task<FSharpProjectOptions * string> =
        task {
            let fullPath = normalizePath path
            let sourceText = SourceText.ofString text

            match projectOptions with
            | Some options when not options.IsEmpty ->
                let projectFileName =
                    projectPath
                    |> Option.defaultValue (Path.ChangeExtension(fullPath, ".fsproj"))
                    |> normalizePath

                let resolvedOptions =
                    checker.GetProjectOptionsFromCommandLineArgs(projectFileName, options |> List.toArray)

                return resolvedOptions, "commandLineArgs"
            | _ ->
                let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask
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

            let! projectResults = checker.ParseAndCheckProject(projectOptions) |> asTask

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
                    JProperty("totalProjectSymbolUses", allUses.Length),
                    JProperty("matchedCount", matchedUses.Length),
                    JProperty("uses", JArray(matchedUses)),
                    JProperty("projectDiagnostics", JArray(projectResults.Diagnostics |> Array.map diagnosticToJson))
                )
                :> JToken
        }

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
                        "Sets active project/workspace for fsautocomplete and optionally restarts LSP."
                        (fun args -> toolResult (bridge.SetProject args))
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
                        "FCS project-wide symbol use search by symbol name/fullname."
                        (fun args -> toolResult (fcsBridge.ProjectSymbolUses args))
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
