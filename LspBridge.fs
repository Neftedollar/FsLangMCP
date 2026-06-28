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

type private DiagnosticsTarget
    (
        store: ConcurrentDictionary<string, JsonNode>,
        analyzedAt: ConcurrentDictionary<string, DateTimeOffset>
    ) =
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

            // Record when FSAC last reported diagnostics for this URI. Surfaced in
            // workspace_diagnostics responses so callers can detect stale "all clean"
            // results after an edit (#115).
            analyzedAt[uri] <- DateTimeOffset.UtcNow

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
    /// Maps a severity name (case-insensitive) to the LSP numeric code:
    /// 1=error, 2=warning, 3=information, 4=hint. Returns None for unrecognised names.
    let severityCodeOf (raw: string) : int option =
        if isNull raw then
            None
        else
            match raw.Trim().ToLowerInvariant() with
            | "error"
            | "errors" -> Some 1
            | "warning"
            | "warnings" -> Some 2
            | "information"
            | "info" -> Some 3
            | "hint"
            | "hints" -> Some 4
            | _ -> None

    /// Translates a glob pattern to a regex body (no anchors). POSIX/gitignore
    /// segment semantics — see fileMatchesGlob for the user-facing spec.
    let internal globToRegex (pattern: string) : string =
        let sb = System.Text.StringBuilder()
        let mutable i = 0
        let len = pattern.Length

        while i < len do
            let c = pattern[i]

            if c = '*' then
                if i + 1 < len && pattern[i + 1] = '*' then
                    // `**` — cross-segment.
                    let hasLeftSlash = i > 0 && pattern[i - 1] = '/'
                    let hasRightSlash = i + 2 < len && pattern[i + 2] = '/'

                    if hasLeftSlash && hasRightSlash then
                        // `/**/`: drop preceding `/`, emit `(?:/|/.*/)`,
                        // skip `**/`. Allows zero or more directories.
                        sb.Length <- sb.Length - 1
                        sb.Append("(?:/|/.*/)") |> ignore
                        i <- i + 3
                    elif hasRightSlash then
                        // Leading `**/`: emit `(?:.*/)?`, skip `**/`.
                        sb.Append("(?:.*/)?") |> ignore
                        i <- i + 3
                    else
                        // Trailing `**` or bare `**` — match anything.
                        sb.Append(".*") |> ignore
                        i <- i + 2
                else
                    sb.Append("[^/]*") |> ignore
                    i <- i + 1
            elif c = '?' then
                sb.Append("[^/]") |> ignore
                i <- i + 1
            else
                // Escape regex metachars individually; everything else is literal.
                if "\\.+()|^$[]{}".IndexOf(c) >= 0 then
                    sb.Append('\\') |> ignore

                sb.Append(c) |> ignore
                i <- i + 1

        sb.ToString()

    /// Glob match against a string. POSIX-style segment semantics:
    /// - `*`  matches any chars EXCEPT `/` (single segment, like gitignore / VS Code)
    /// - `**` matches any chars INCLUDING `/` (cross-segment, like gitignore);
    ///        `/**/` between segments matches zero or more directories
    /// - `?`  matches a single non-`/` char
    /// Case-insensitive — file URIs are normalised platform-dependent.
    let fileMatchesGlob (pattern: string) (candidate: string) : bool =
        if isNull pattern || isNull candidate then
            false
        else
            let rx =
                System.Text.RegularExpressions.Regex(
                    "^" + globToRegex pattern + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                )

            rx.IsMatch(candidate)

    /// Filters a JSON diagnostics array (LSP shape) by severity code.
    /// Returns a fresh array containing only diagnostics with matching severity.
    /// Diagnostics missing a severity field are dropped when a filter is active.
    /// (LSP 3.17 says missing-severity is "server-determined"; FSAC always emits
    /// severity, so dropping is safe in this project — re-evaluate if another
    /// LSP backend is wired in.)
    let filterDiagnosticsBySeverity (severityCode: int) (diagnostics: JsonNode) : JsonNode =
        match diagnostics with
        | :? JsonArray as arr ->
            let kept = JsonArray()

            for node in arr do
                match node with
                | :? JsonObject as obj ->
                    match obj["severity"] with
                    | :? JsonValue as sev ->
                        let mutable code = 0

                        if sev.TryGetValue(&code) && code = severityCode then
                            kept.Add(node.DeepClone())
                    | _ -> ()
                | _ -> ()

            kept :> JsonNode
        | other -> other.DeepClone()

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

    /// Formats a UTC DateTimeOffset as an ISO-8601 "Z"-suffixed string for JSON
    /// surfacing. Returns null when the value is absent so JSON encodes `null`.
    let timestampJson (timestamp: DateTimeOffset option) : JsonNode =
        match timestamp with
        | Some t -> jstr (t.ToUniversalTime().ToString("O"))
        | None -> null

    /// Builds the workspace_diagnostics response payload for a single file.
    /// `analyzedAt` is when FSAC last pushed diagnostics for this URI (or None
    /// when FSAC has not yet reported on it).
    let diagnosticsResponseForFile
        (workspaceReady: bool)
        (diagnosticsCount: int)
        (filePayload: JsonNode)
        (analyzedAt: DateTimeOffset option)
        : JsonNode =
        jobj
            [ "status", jstr "ok"
              "lspState", jstr (lspStateString workspaceReady)
              "diagnosticsFileCount", jint diagnosticsCount
              "analyzedAt", timestampJson analyzedAt
              "result", filePayload ]
        :> JsonNode

    /// Builds the workspace_diagnostics response payload for the whole workspace.
    /// `mostRecentAnalyzedAt` is the max analyzedAt across all stored files —
    /// useful as a coarse "freshness floor". `analyzedAtByUri` carries per-URI
    /// timestamps so callers can spot individual stale files.
    let diagnosticsResponseForWorkspace
        (workspaceReady: bool)
        (diagnosticsCount: int)
        (allPayloads: JsonObject)
        (mostRecentAnalyzedAt: DateTimeOffset option)
        (analyzedAtByUri: JsonObject)
        : JsonNode =
        jobj
            [ "status", jstr "ok"
              "lspState", jstr (lspStateString workspaceReady)
              "diagnosticsFileCount", jint diagnosticsCount
              "mostRecentAnalyzedAt", timestampJson mostRecentAnalyzedAt
              "analyzedAtByUri", analyzedAtByUri :> JsonNode
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

    // ─── diagnostic-fixes shaping (fcs_diagnostic_fixes, #53) ──────────────────
    // Pure builders for the agent-friendly "diagnostics → grouped fixes" payload.
    // Tested in isolation (the live grouping path needs an FSAC process); the bridge
    // member feeds these the raw FSAC diagnostics + per-diagnostic codeAction arrays.

    /// Reads an int child from a JSON object by key. None when the node is not an
    /// object, the key is absent, or the value is not an integer.
    let private childInt (node: JsonNode) (key: string) : int option =
        match node with
        | :? JsonObject as obj ->
            match obj[key] with
            | :? JsonValue as v ->
                let mutable n = 0
                if v.TryGetValue(&n) then Some n else None
            | _ -> None
        | _ -> None

    /// Reads a string child from a JSON object by key. None when absent/not a string.
    let private childString (node: JsonNode) (key: string) : string option =
        match node with
        | :? JsonObject as obj ->
            match obj[key] with
            | :? JsonValue as v ->
                let mutable s = ""
                if v.TryGetValue(&s) then Some s else None
            | _ -> None
        | _ -> None

    /// True when (line, character) falls within the LSP range [start, end] inclusive.
    /// A missing start/end character is treated as unbounded on that edge.
    let positionWithinRange (line: int) (character: int) (range: JsonNode) : bool =
        match range with
        | :? JsonObject as obj ->
            match childInt obj["start"] "line", childInt obj["end"] "line" with
            | Some sl, Some el ->
                let afterStart =
                    line > sl
                    || (line = sl
                        && (match childInt obj["start"] "character" with
                            | Some sc -> character >= sc
                            | None -> true))

                let beforeEnd =
                    line < el
                    || (line = el
                        && (match childInt obj["end"] "character" with
                            | Some ec -> character <= ec
                            | None -> true))

                afterStart && beforeEnd
            | _ -> false
        | _ -> false

    /// True when `line` is between the range's start and end line (inclusive).
    let lineWithinRange (line: int) (range: JsonNode) : bool =
        match range with
        | :? JsonObject as obj ->
            match childInt obj["start"] "line", childInt obj["end"] "line" with
            | Some sl, Some el -> line >= sl && line <= el
            | _ -> false
        | _ -> false

    /// Position filter for a diagnostic's range:
    /// - both line+character → point-in-range
    /// - line only           → diagnostics intersecting that line
    /// - neither             → keep everything (whole-file mode)
    let diagnosticCoversPosition (line: int option) (character: int option) (range: JsonNode) : bool =
        match line, character with
        | Some l, Some c -> positionWithinRange l c range
        | Some l, None -> lineWithinRange l range
        | None, _ -> true

    /// Collects the individual TextEdits inside an LSP CodeAction's WorkspaceEdit,
    /// handling both the `documentChanges` and the `changes` representations.
    let private codeActionEdits (codeAction: JsonNode) : JsonNode list =
        match codeAction with
        | :? JsonObject as ca ->
            match ca["edit"] with
            | :? JsonObject as edit ->
                match edit["documentChanges"] with
                | :? JsonArray as docChanges ->
                    [ for dc in docChanges do
                          match dc with
                          | :? JsonObject as dco ->
                              match dco["edits"] with
                              | :? JsonArray as edits -> yield! Seq.cast<JsonNode> edits
                              | _ -> ()
                          | _ -> () ]
                | _ ->
                    match edit["changes"] with
                    | :? JsonObject as changes ->
                        [ for kv in changes do
                              match kv.Value with
                              | :? JsonArray as edits -> yield! Seq.cast<JsonNode> edits
                              | _ -> () ]
                    | _ -> []
            | _ -> []
        | _ -> []

    /// One-line human summary of what a CodeAction's edit does: edit count plus a
    /// whitespace-collapsed, truncated preview of the first inserted text. Lets an
    /// agent triage fixes without parsing the raw WorkspaceEdit.
    let editSummaryOf (codeAction: JsonNode) : string =
        let edits = codeActionEdits codeAction

        let preview (text: string) =
            let collapsed =
                System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim()

            if collapsed.Length > 60 then
                collapsed.Substring(0, 57) + "..."
            else
                collapsed

        match edits with
        | [] ->
            match codeAction with
            | :? JsonObject as ca when not (isNull ca["command"]) -> "command (no text edit)"
            | _ -> "no edit"
        | _ ->
            let firstNewText =
                edits
                |> List.tryPick (fun e -> childString e "newText")
                |> Option.defaultValue ""

            let label =
                if List.length edits = 1 then
                    "1 edit"
                else
                    $"{List.length edits} edits"

            if firstNewText = "" then
                $"{label} (deletion)"
            else
                $"{label}: {preview firstNewText}"

    /// Projects a raw LSP CodeAction to the compact agent shape { title, kind, editSummary }.
    let summarizeCodeAction (codeAction: JsonNode) : JsonNode =
        jobj
            [ "title", (childString codeAction "title" |> Option.map jstr |> Option.defaultValue null)
              "kind", (childString codeAction "kind" |> Option.map jstr |> Option.defaultValue null)
              "editSummary", jstr (editSummaryOf codeAction) ]
        :> JsonNode

    /// Deep-clones a field off a diagnostic node, or null when absent. Cloning keeps
    /// the source node (owned by the FSAC store) attached to its parent.
    let private cloneField (diag: JsonNode) (key: string) : JsonNode =
        match diag with
        | :? JsonObject as obj ->
            match obj[key] with
            | null -> null
            | node -> node.DeepClone()
        | _ -> null

    /// One grouped entry: the diagnostic's range/severity/code/message plus its fixes.
    let diagnosticFixEntry (diag: JsonNode) (fixes: JsonNode list) : JsonNode =
        let fixNodes = fixes |> List.map summarizeCodeAction |> List.toArray

        jobj
            [ "range", cloneField diag "range"
              "severity", cloneField diag "severity"
              "code", cloneField diag "code"
              "message", cloneField diag "message"
              "fixes", JsonArray(fixNodes) :> JsonNode ]
        :> JsonNode

    /// Builds the full fcs_diagnostic_fixes payload from (diagnostic, fixes) pairs.
    let buildDiagnosticFixesResponse (file: string) (entries: (JsonNode * JsonNode list) list) : JsonNode =
        let diagNodes =
            entries |> List.map (fun (d, fixes) -> diagnosticFixEntry d fixes) |> List.toArray

        let fixCount = entries |> List.sumBy (fun (_, fixes) -> List.length fixes)

        jobj
            [ "status", jstr "ok"
              "file", jstr file
              "diagnostics", JsonArray(diagNodes) :> JsonNode
              "diagnosticCount", jint (List.length entries)
              "fixCount", jint fixCount ]
        :> JsonNode

// ─── FsAutoCompleteBridge ──────────────────────────────────────────────────────

type internal FsAutoCompleteBridge() =
    let gate = new SemaphoreSlim(1, 1)
    let documents = ConcurrentDictionary<string, LspDocumentState>()
    let diagnostics = ConcurrentDictionary<string, JsonNode>()
    // Tracks when FSAC last pushed diagnostics for each URI. Surfaced via
    // `analyzedAt` / `analyzedAtByUri` / `mostRecentAnalyzedAt` in workspace_diagnostics
    // responses (#115) so callers can self-check freshness after an edit.
    let diagnosticsAnalyzedAt = ConcurrentDictionary<string, DateTimeOffset>()

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
            // Guard before Path.GetFullPath: a wrong/missing key deserializes projectPath to
            // null, and Path.GetFullPath(null) throws ArgumentNullException naming the internal
            // 'path' param — misleading callers who passed the wrong key. Surface 'projectPath'.
            match ArgsValidation.requireNonBlank "projectPath" args.projectPath with
            | Error envelope -> return envelope
            | Ok projectPathArg ->

            let inputPath = Path.GetFullPath(projectPathArg)

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
                                  [ "fslangmcpVersion", jstr FsLangMcp.Version.current
                                    "projectPath", capturedProjectPath |> Option.map jstr |> Option.defaultValue null
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
                                  [ "fslangmcpVersion", jstr FsLangMcp.Version.current
                                    "projectPath", capturedProjectPath |> Option.map jstr |> Option.defaultValue null
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

            jsonRpc.AddLocalRpcTarget(new DiagnosticsTarget(diagnostics, diagnosticsAnalyzedAt), targetOptions)
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
                            jobj
                                [ "completion", jobj [ "completionItem", jobj [ "snippetSupport", jbool true ] ]
                                  // FSAC gates codeAction *kinds* (its quick-fixes) on the client
                                  // advertising codeActionLiteralSupport; without it codeAction returns
                                  // null/Commands only. Required for fcs_diagnostic_fixes (#53).
                                  "codeAction",
                                  jobj
                                      [ "codeActionLiteralSupport",
                                        jobj
                                            [ "codeActionKind",
                                              jobj
                                                  [ "valueSet",
                                                    JsonArray(
                                                        [| "quickfix"
                                                           "refactor"
                                                           "refactor.extract"
                                                           "refactor.inline"
                                                           "refactor.rewrite"
                                                           "source"
                                                           "source.organizeImports" |]
                                                        |> Array.map jstr
                                                    )
                                                    :> JsonNode ] ]
                                        "isPreferredSupport", jbool true
                                        "dataSupport", jbool true
                                        "resolveSupport", jobj [ "properties", JsonArray(jstr "edit") :> JsonNode ] ]
                                  // Advertise push-diagnostics + save sync so FSAC publishes
                                  // diagnostics for opened/changed documents.
                                  "publishDiagnostics", jobj [ "relatedInformation", jbool true ]
                                  "synchronization", jobj [ "didSave", jbool true ] ] ] ]

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
            let severityFilter = args.severity |> Option.bind LspResponseShape.severityCodeOf

            let applySeverity (payload: JsonNode) =
                match severityFilter with
                | Some code -> LspResponseShape.filterDiagnosticsBySeverity code payload
                | None -> payload

            let resolveByUri (uri: string) =
                match diagnostics.TryGetValue(uri) with
                | true, payload -> payload.DeepClone()
                | false, _ -> JsonArray() :> JsonNode

            let analyzedAtForUri (uri: string) =
                match diagnosticsAnalyzedAt.TryGetValue(uri) with
                | true, ts -> Some ts
                | false, _ -> None

            match args.path with
            | Some path ->
                let uri = toFileUri path
                let payload = resolveByUri uri |> applySeverity
                let analyzedAt = analyzedAtForUri uri

                return
                    LspResponseShape.diagnosticsResponseForFile
                        workspaceReady
                        diagnostics.Count
                        payload
                        analyzedAt
            | None ->
                let root = JsonObject()
                let analyzedAtByUri = JsonObject()

                let globMatches (uri: string) =
                    match args.fileGlob with
                    | Some pattern -> LspResponseShape.fileMatchesGlob pattern uri
                    | None -> true

                for KeyValue(uri, payload) in diagnostics do
                    if globMatches uri then
                        let filtered = payload.DeepClone() |> applySeverity

                        // Skip empty-after-filter entries — keeps the response tight when
                        // the user asked for errors-only and a file only has warnings.
                        match severityFilter, filtered with
                        | Some _, (:? JsonArray as arr) when arr.Count = 0 -> ()
                        | _ ->
                            root[uri] <- filtered

                            match analyzedAtForUri uri with
                            | Some ts -> analyzedAtByUri[uri] <- jstr (ts.ToUniversalTime().ToString("O"))
                            | None -> analyzedAtByUri[uri] <- null

                let mostRecent =
                    let filtered =
                        diagnosticsAnalyzedAt
                        |> Seq.filter (fun kv -> globMatches kv.Key)
                        |> Seq.map (fun kv -> kv.Value)
                        |> Seq.toArray

                    if filtered.Length = 0 then None
                    else filtered |> Array.max |> Some

                return
                    LspResponseShape.diagnosticsResponseForWorkspace
                        workspaceReady
                        diagnostics.Count
                        root
                        mostRecent
                        analyzedAtByUri
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

    /// Agent-friendly wrapper over the raw codeAction proxy: fetches the file's
    /// diagnostics (FSAC publishDiagnostics), then for EACH diagnostic re-requests
    /// codeActions with that diagnostic populated in context.diagnostics — the
    /// context the raw `CodeAction` proxy leaves empty — and groups the fixes per
    /// diagnostic. line/character narrow to one position; omit for the whole file.
    member this.DiagnosticFixes(args: DiagnosticFixesArgs) : Task<JsonNode> =
        task {
            let! jsonRpc = this.EnsureStarted()

            if not workspaceReady then
                return this.NotReadyResponse()
            else
                let fullPath = Path.GetFullPath(args.path)
                let uri = toFileUri fullPath

                // Snapshot the prior publish time so we can detect a FRESH republish
                // (publishDiagnostics arrives asynchronously after didOpen/didChange).
                let priorAnalyzedAt =
                    match diagnosticsAnalyzedAt.TryGetValue(uri) with
                    | true, ts -> ValueSome ts
                    | false, _ -> ValueNone

                // Sync the document under the gate so FSAC re-analyzes and republishes.
                let! _ =
                    task {
                        do! gate.WaitAsync()

                        try
                            return! this.SyncDocument(jsonRpc, args.path, args.text)
                        finally
                            gate.Release() |> ignore
                    }

                // Bounded wait for FSAC to push a fresh diagnostics set for this URI.
                // On timeout we proceed with whatever the store holds (possibly empty)
                // rather than blocking the single LSP slot indefinitely.
                let deadline = DateTimeOffset.UtcNow.AddSeconds(5.0)

                let isFresh () =
                    match diagnosticsAnalyzedAt.TryGetValue(uri) with
                    | true, ts ->
                        match priorAnalyzedAt with
                        | ValueSome prev -> ts > prev
                        | ValueNone -> true
                    | false, _ -> false

                while not (isFresh ()) && DateTimeOffset.UtcNow < deadline do
                    do! Task.Delay(50)

                let fileDiagnostics =
                    match diagnostics.TryGetValue(uri) with
                    | true, (:? JsonArray as arr) -> arr |> Seq.cast<JsonNode> |> Seq.toList
                    | _ -> []

                let targeted =
                    fileDiagnostics
                    |> List.filter (fun d ->
                        match d with
                        | :? JsonObject as obj ->
                            LspResponseShape.diagnosticCoversPosition args.line args.character obj["range"]
                        | _ -> false)

                let entries = ResizeArray<JsonNode * JsonNode list>()

                for diag in targeted do
                    // codeAction over the diagnostic's own range, with the diagnostic
                    // populated in context — this is what unlocks the diagnostic-keyed
                    // fixes that the empty-context raw proxy never sees.
                    let range =
                        match diag with
                        | :? JsonObject as obj when not (isNull obj["range"]) -> obj["range"].DeepClone()
                        | _ ->
                            jobj
                                [ "start", jobj [ "line", jint 0; "character", jint 0 ]
                                  "end", jobj [ "line", jint 0; "character", jint 0 ] ]
                            :> JsonNode

                    let parameters =
                        jobj
                            [ "textDocument", jobj [ "uri", jstr uri ]
                              "range", range
                              "context", jobj [ "diagnostics", JsonArray(diag.DeepClone()) :> JsonNode ] ]

                    let! response =
                        jsonRpc.InvokeWithParameterObjectAsync<JsonNode>("textDocument/codeAction", parameters)

                    let fixes =
                        match response with
                        | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toList
                        | _ -> []

                    entries.Add(diag, fixes)

                return LspResponseShape.buildDiagnosticFixesResponse fullPath (List.ofSeq entries)
        }

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
