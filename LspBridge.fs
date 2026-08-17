module FsLangMcp.LspBridge

open System
open System.IO
open System.Collections.Concurrent
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FsLangMcp.Types
open FsLangMcp.ProcessRunner
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open StreamJsonRpc

let private timeoutFromEnv (name: string) (defaultMilliseconds: int) =
    match Environment.GetEnvironmentVariable(name) with
    | value when not (String.IsNullOrWhiteSpace(value)) ->
        match Int32.TryParse(value) with
        | true, milliseconds when milliseconds > 0 -> TimeSpan.FromMilliseconds(float milliseconds)
        | _ -> TimeSpan.FromMilliseconds(float defaultMilliseconds)
    | _ -> TimeSpan.FromMilliseconds(float defaultMilliseconds)

let private invokeWithTimeout
    (jsonRpc: JsonRpc)
    (methodName: string)
    (parameters: JsonObject)
    (timeout: TimeSpan)
    : Task<JsonNode> =
    task {
        use cts = new CancellationTokenSource()
        let invocation = jsonRpc.InvokeWithParameterObjectAsync<JsonNode>(methodName, parameters, cts.Token)

        invocation.ContinueWith(
            (fun (faulted: Task) -> faulted.Exception |> ignore),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted ||| TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

        let timeoutError () =
            TimeoutException(
                $"FSAC LSP request '%s{methodName}' timed out after %d{int64 timeout.TotalMilliseconds}ms."
            )

        try
            return! invocation.WaitAsync(timeout)
        with
        | :? TimeoutException ->
            cts.Cancel()
            return raise (timeoutError ())
        | :? OperationCanceledException when cts.IsCancellationRequested -> return raise (timeoutError ())
    }

let private notifyWithTimeout
    (jsonRpc: JsonRpc)
    (methodName: string)
    (parameters: JsonObject)
    (timeout: TimeSpan)
    : Task =
    task {
        let notification = jsonRpc.NotifyWithParameterObjectAsync(methodName, parameters)

        notification.ContinueWith(
            (fun (faulted: Task) -> faulted.Exception |> ignore),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted ||| TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

        try
            do! notification.WaitAsync(timeout)
        with :? TimeoutException ->
            return
                raise (
                    TimeoutException(
                        $"FSAC LSP notification '%s{methodName}' timed out after %d{int64 timeout.TotalMilliseconds}ms."
                    )
                )
    }

let private releaseOnDispose (semaphore: SemaphoreSlim) =
    { new IDisposable with
        member _.Dispose() = semaphore.Release() |> ignore }

let rec private classifyRenameInfrastructureError (error: exn) =
    match error with
    // #192: must precede the InvalidOperationException arm — SdkNotFoundException
    // derives from it, and an unsatisfiable SDK pin is not a protocol error.
    | SdkPreflight.SdkPinUnsatisfiable _ -> "sdk_not_found", false
    | :? TimeoutException -> "timeout", true
    | :? System.ComponentModel.Win32Exception -> "executable_missing", false
    | :? IOException
    | :? ObjectDisposedException
    | :? EndOfStreamException -> "disconnected", true
    | :? RemoteInvocationException -> "remote_error", true
    | :? JsonException
    | :? InvalidOperationException -> "protocol_error", false
    | _ when not (isNull error.InnerException) -> classifyRenameInfrastructureError error.InnerException
    | _ -> "startup_failed", true

// ─── LSP types ─────────────────────────────────────────────────────────────────

type private LspDocumentState =
    { mutable Version: int
      mutable Text: string
      mutable TextHash: string }

exception private DiagnosticsSnapshotGateBusyException

/// One publishDiagnostics notification, committed atomically. Keeping payload,
/// generation, version evidence, and receipt time together prevents readers from
/// combining fields from two concurrent FSAC notifications.
[<NoComparison; NoEquality>]
type internal DiagnosticEnvelope =
    { Generation: int64
      Payload: JsonNode
      ServerVersion: int option
      ReceivedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module internal DiagnosticIdentity =
    let pathComparer =
        if OperatingSystem.IsWindows() then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    let canonicalFileKeyFromPath (path: string) = Path.GetFullPath(path)

    let tryCanonicalFileKeyFromUri (value: string) =
        try
            let uri = Uri(value, UriKind.Absolute)

            if uri.IsFile then
                let decodedPath = Uri.UnescapeDataString(uri.AbsolutePath).Replace('\\', '/')

                let hasLeadingWindowsDrive =
                    decodedPath.Length >= 4
                    && decodedPath[0] = '/'
                    && Char.IsLetter(decodedPath[1])
                    && decodedPath[2] = ':'
                    && decodedPath[3] = '/'

                let hasWindowsDrive =
                    decodedPath.Length >= 3
                    && Char.IsLetter(decodedPath[0])
                    && decodedPath[1] = ':'
                    && decodedPath[2] = '/'

                let localPath =
                    if not (String.IsNullOrWhiteSpace uri.Host) && uri.Host <> "localhost" then
                        uri.LocalPath
                    elif OperatingSystem.IsWindows() && hasLeadingWindowsDrive then
                        decodedPath.Substring(1).Replace('/', Path.DirectorySeparatorChar)
                    elif OperatingSystem.IsWindows() then
                        uri.LocalPath
                    elif hasWindowsDrive then
                        "/" + decodedPath
                    else
                        decodedPath

                Some(canonicalFileKeyFromPath localPath)
            else
                None
        with _ ->
            None

    let textHash (text: string) =
        text
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    /// Capture a strong hash only when the file metadata is stable across the
    /// read. A racing/failed read has no baseline and therefore cannot later
    /// support an authoritative clean verdict.
    let tryStableFileTextHash (path: string) =
        try
            let fullPath = canonicalFileKeyFromPath path

            if not (File.Exists fullPath) then
                None
            else
                let before = FileInfo(fullPath)
                let beforeLength = before.Length
                let beforeWrite = before.LastWriteTimeUtc
                let text = File.ReadAllText(fullPath)
                let after = FileInfo(fullPath)

                if beforeLength = after.Length && beforeWrite = after.LastWriteTimeUtc then
                    Some(textHash text)
                else
                    None
        with _ ->
            None

type private LspLifecycleState =
    | Stopped
    | Starting of generation: int64
    | Ready of generation: int64
    | Stopping of generation: int64
    | Faulted of generation: int64 * reason: string

type private DiagnosticFreshness =
    | Current
    | DiskContentChanged
    | Stale

type internal DiagnosticsTarget
    (
        store: ConcurrentDictionary<string, DiagnosticEnvelope>,
        generation: int64,
        isCurrentGeneration: int64 -> bool,
        synchronizationRoot: obj
    ) =
    [<JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)>]
    member _.PublishDiagnostics(payload: JsonObject) =
        // A retired FSAC process can still have notifications queued while its
        // redirected streams are draining. Never let such a notification mutate
        // the diagnostic snapshot owned by the replacement generation.
        lock synchronizationRoot (fun () ->
            if isCurrentGeneration generation then
                let uriToken = payload["uri"]

                if not (isNull uriToken) then
                    let uri = uriToken.GetValue<string>()

                    match DiagnosticIdentity.tryCanonicalFileKeyFromUri uri with
                    | None -> ()
                    | Some fileKey ->
                        let diagnostics = payload["diagnostics"]

                        let serverVersion =
                            match payload["version"] with
                            | :? JsonValue as value ->
                                let mutable version = 0
                                if value.TryGetValue(&version) then Some version else None
                            | _ -> None

                        store[fileKey] <-
                            { Generation = generation
                              Payload =
                                if isNull diagnostics then
                                    JsonArray() :> JsonNode
                                else
                                    diagnostics.DeepClone()
                              ServerVersion = serverVersion
                              ReceivedAt = DateTimeOffset.UtcNow })

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
        | Invalid of reason: string

    let private recursiveFiles directory pattern =
        FsLangMcp.ProjectFiles.WorkspaceDirectoryDiscovery.filesBelow directory [| pattern |]

    let private solutionFiles directory searchOption =
        let find pattern =
            match searchOption with
            | SearchOption.TopDirectoryOnly -> Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            | _ -> recursiveFiles directory pattern

        [| yield! find "*.sln"
           yield! find "*.slnx" |]
        |> Array.map Path.GetFullPath
        |> Array.sort

    let private projectFiles directory searchOption =
        (match searchOption with
         | SearchOption.TopDirectoryOnly -> Directory.GetFiles(directory, "*.fsproj", SearchOption.TopDirectoryOnly)
         | _ -> recursiveFiles directory "*.fsproj")
        |> Array.map Path.GetFullPath
        |> Array.sort

    let private selectCandidates (kind: CandidateKind) (paths: string array) =
        if paths.Length > 1 then
            Some(Ambiguous(paths |> Array.map (fun path -> { Kind = kind; Path = path }) |> Array.toList))
        elif paths.Length = 1 then
            Some(Selected(paths[0], [ { Kind = kind; Path = paths[0] } ]))
        else
            None

    let select (path: string) =
        let fullPath = Path.GetFullPath(path)

        if not (Directory.Exists fullPath) then
            let extension = Path.GetExtension(fullPath)

            if
                String.Equals(extension, ".fsproj", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            then
                Selected(fullPath, [])
            else
                Invalid
                    $"projectPath must be a directory, .fsproj, .sln, or .slnx file; got '{fullPath}'."
        else
            // Preserve the historical top-level precedence first. If the requested
            // directory is a repository root with no top-level workspace file, recurse
            // so documented directory inputs can select `src/App/App.fsproj`. Multiple
            // candidates remain explicit instead of silently choosing by sort order.
            [ (Solution, solutionFiles fullPath SearchOption.TopDirectoryOnly)
              (Project, projectFiles fullPath SearchOption.TopDirectoryOnly)
              (Solution, solutionFiles fullPath SearchOption.AllDirectories)
              (Project, projectFiles fullPath SearchOption.AllDirectories) ]
            |> List.tryPick (fun (kind, paths) -> selectCandidates kind paths)
            |> Option.defaultValue (Selected(fullPath, []))

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

/// Minimal LSP client-side window surface. FSAC/Fantomas can ask the client to
/// display a message while servicing formatting; leaving this request unhandled
/// makes StreamJsonRpc answer "method not found" and turns valid formatting into
/// a RemoteMethodNotFoundException. A headless MCP server has no UI, so requests
/// are acknowledged with a null (dismissed) action and notifications go to stderr.
type private WindowMessageTarget() =
    let log (kind: string) (payload: JsonObject) =
        let message =
            match payload["message"] with
            | :? JsonValue as value ->
                let mutable text = ""
                if value.TryGetValue(&text) then text else payload.ToJsonString()
            | _ -> payload.ToJsonString()

        let bounded = if message.Length <= 2000 then message else message[..1999] + "…"
        Console.Error.WriteLine($"[fsautocomplete {kind}] {bounded}")

    [<JsonRpcMethod("window/showMessageRequest", UseSingleObjectParameterDeserialization = true)>]
    member _.ShowMessageRequest(payload: JsonObject) : JsonNode =
        log "message" payload
        null

    [<JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)>]
    member _.ShowMessage(payload: JsonObject) = log "message" payload

    [<JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)>]
    member _.LogMessage(payload: JsonObject) = log "log" payload

// ─── LspResponseShape (pure response builders, testable) ──────────────────────

module internal LspResponseShape =
    /// True only when a requested restart replaces an already-running LSP.
    /// A first launch is a start, not a restart.
    let lspRestartOccurred (restartRequested: bool) (lspWasRunning: bool) =
        restartRequested && lspWasRunning

    /// Warm-up grace period after workspace-ready before an empty/never-warmed
    /// symbol index is treated as stalled rather than "still indexing". Shared
    /// by assessSymbolIndex (workspace_symbol) and setProjectReadiness
    /// (set_project) — one timing source, not two (#194).
    let symbolIndexWarmupWindow = TimeSpan.FromSeconds 3.0

    /// True once `warmupWindow` has elapsed since workspace-ready; false when
    /// workspace-ready hasn't happened yet (nothing to measure the elapsed
    /// time from).
    let private warmupWindowElapsed
        (workspaceReadyAt: DateTimeOffset voption)
        (now: DateTimeOffset)
        (warmupWindow: TimeSpan)
        : bool =
        match workspaceReadyAt with
        | ValueSome readyAt -> now - readyAt > warmupWindow
        | ValueNone -> false

    /// Builds the additive readiness detail returned by set_project. Keep the
    /// original boolean fields stable while giving callers a recovery path when
    /// the symbol index is not ready yet.
    let setProjectReadiness
        (lspReady: bool)
        (symbolIndexReady: bool)
        (restartRequested: bool)
        (workspaceReadyAt: DateTimeOffset voption)
        (now: DateTimeOffset)
        (warmupWindow: TimeSpan)
        : JsonNode =
        let symbolIndexState, symbolIndexHint =
            if symbolIndexReady then
                "ready", null
            elif lspReady && warmupWindowElapsed workspaceReadyAt now warmupWindow then
                // The window has passed with no non-empty index result observed
                // (#194) — "warming" would be a lie at this point. But the FSAC
                // probe is only sent when find's own FCS sweep comes up empty
                // (Dispatcher.fs), so absence of a warm signal is not proof the
                // index failed to warm — a healthy session that never needed the
                // probe looks identical. Say what was (not) observed, not a verdict.
                "not_warmed",
                jstr
                    $"FSAC's symbol index has not been observed warm within {int warmupWindow.TotalSeconds}s of workspace load. FCS-derived results are unaffected: check, and every find site the FCS sweep produced. find is itself the consumer of the index fallback (#194 review) — on a sweep with zero hits it probes the index and reports via=\"fsac-symbol-index\" when the index answers, so while the index is cold that confirmation is missing and a zero-hit find can read as not_found. Check find's own fsacFallbackState/fsacFallbackReason before treating absence as conclusive."
            elif lspReady then
                "warming",
                jstr
                    "The FSAC symbol index is still warming, so symbol-index fallback results may be incomplete. Wait briefly, then retry the symbol-dependent request; check and find's FCS sweep remain available, but find's zero-hit index fallback can be incomplete until the index warms — read its fsacFallbackState."
            elif restartRequested then
                "blocked_on_lsp",
                jstr
                    "FSAC did not become ready, so position-based LSP tools and symbol-index fallback are unavailable — including find's zero-hit fallback, which reports fsacFallbackState instead of a hit count. find's own FCS sweep and check are unaffected. Retry set_project with restartLsp=true."
            else
                "not_started",
                jstr
                    "The LSP was not started, so position-based LSP tools and symbol-index fallback are unavailable — including find's zero-hit fallback, which reports fsacFallbackState instead of a hit count. find's own FCS sweep and check are unaffected. Call set_project with restartLsp=true."

        jobj
            [ "lsp", jbool lspReady
              // projectOptions is enriched by the caller in Program.fs (Bridge has no FCS handle).
              "projectOptions", jbool false
              "symbolIndex", jbool symbolIndexReady
              "symbolIndexState", jstr symbolIndexState
              "symbolIndexHint", symbolIndexHint ]
        :> JsonNode

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

    /// Match a user-facing workspace glob against an evaluated source path. Relative
    /// globs (for example `src/Adapters/*.fs`) are evaluated from the selected
    /// workspace root; absolute path and file-URI patterns remain supported.
    let fileMatchesWorkspaceGlob (workspaceRoot: string option) (pattern: string) (filePath: string) : bool =
        if String.IsNullOrWhiteSpace(pattern) || String.IsNullOrWhiteSpace(filePath) then
            false
        else
            try
                let slash (value: string) = value.Replace('\\', '/')
                let fullPath = Path.GetFullPath(filePath)
                let normalizedPattern = slash pattern
                let candidates = ResizeArray<string>()
                candidates.Add(slash fullPath)
                candidates.Add(Uri(fullPath).AbsoluteUri)

                match workspaceRoot with
                | Some root when not (String.IsNullOrWhiteSpace root) ->
                    let relative = Path.GetRelativePath(Path.GetFullPath(root), fullPath)

                    if relative <> ".."
                       && not (relative.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal))
                       && not (Path.IsPathRooted relative) then
                        candidates.Add(slash relative)
                | _ -> ()

                candidates
                |> Seq.distinct
                |> Seq.exists (fileMatchesGlob normalizedPattern)
            with _ ->
                false

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
        | :? JsonArray as arr when arr.Count = 0 -> warmupWindowElapsed workspaceReadyAt now warmupWindow
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

// ─── RenamePreviewShape (pure rename-preview transform, testable) ──────────────

/// Transforms a raw LSP WorkspaceEdit (as produced by FSAC's textDocument/rename)
/// into an agent-friendly grouped preview: edits bucketed by file, counted, and
/// each paired with the original line and a synthesized post-rename line. Pure —
/// it only reads file content through the injected lookups, so it can never write.
module internal RenamePreviewShape =

    /// Carries everything the transform needs that is not in the WorkspaceEdit:
    /// the new name, the originating position (to identify the renamed symbol),
    /// and three injected readers so the core stays pure and unit-testable.
    [<NoComparison; NoEquality>]
    type Context =
        { NewName: string
          /// File URI of the originating request — used to locate the renamed symbol.
          OriginatingUri: string
          /// 0-based line of the originating position.
          Line: int
          /// 0-based character of the originating position.
          Character: int
          /// uri -> the file's lines (LF-split, CR-trimmed); None when unreadable.
          LookupLines: string -> string[] option
          /// uri -> owning project identity; used to flag cross-project renames.
          ResolveProject: string -> string option
          /// uri -> human-readable file path for the response.
          UriToDisplay: string -> string }

    /// Splits text into lines on '\n', trimming a trailing '\r' so CRLF and LF
    /// inputs both yield clean line content (mirrors Formatting's line handling).
    let splitLines (text: string) : string[] =
        text.Split('\n') |> Array.map (fun l -> l.TrimEnd('\r'))

    /// Reads (startLine, startChar, endLine, endChar) from a TextEdit's `range`.
    /// Returns None when the shape is missing or malformed.
    let internal tryRange (edit: JsonNode) : (int * int * int * int) option =
        match edit with
        | :? JsonObject as o ->
            match o["range"] with
            | :? JsonObject as r ->
                match r["start"], r["end"] with
                | (:? JsonObject as s), (:? JsonObject as e) ->
                    try
                        Some(
                            s["line"].GetValue<int>(),
                            s["character"].GetValue<int>(),
                            e["line"].GetValue<int>(),
                            e["character"].GetValue<int>()
                        )
                    with _ ->
                        None
                | _ -> None
            | _ -> None
        | _ -> None

    let private editNewText (edit: JsonNode) : string =
        match edit with
        | :? JsonObject as o ->
            match o["newText"] with
            | null -> ""
            | n ->
                try
                    n.GetValue<string>()
                with _ ->
                    ""
        | _ -> ""

    let private isStringValue (node: JsonNode) =
        match node with
        | :? JsonValue as value ->
            let mutable text = ""
            value.TryGetValue(&text)
        | _ -> false

    let private validateTextEdit (edit: JsonNode) =
        match edit with
        | :? JsonObject as obj when (tryRange edit |> Option.isSome) && isStringValue obj["newText"] -> Ok()
        | _ -> Error "WorkspaceEdit contains a malformed TextEdit (range/newText)."

    let private validateTextEdits (edits: JsonArray) =
        edits
        |> Seq.cast<JsonNode>
        |> Seq.tryPick (fun edit ->
            match validateTextEdit edit with
            | Ok() -> None
            | Error reason -> Some reason)
        |> function
            | Some reason -> Error reason
            | None -> Ok()

    /// A null or genuinely empty WorkspaceEdit means "no symbol". A value that is
    /// not a valid WorkspaceEdit is a protocol failure and must never be presented
    /// as a semantic negative.
    let private validateWorkspaceEdit (workspaceEdit: JsonNode) =
        if isNull workspaceEdit then
            Ok()
        else
            match workspaceEdit with
            | :? JsonObject as edit ->
                match edit["documentChanges"], edit["changes"] with
                | (:? JsonArray as documentChanges), null ->
                    documentChanges
                    |> Seq.cast<JsonNode>
                    |> Seq.tryPick (fun change ->
                        match change with
                        | :? JsonObject as obj ->
                            match obj["textDocument"], obj["edits"], obj["kind"] with
                            | (:? JsonObject as document), (:? JsonArray as edits), _
                                when isStringValue document["uri"] ->
                                match validateTextEdits edits with
                                | Ok() -> None
                                | Error reason -> Some reason
                            | null, null, kind when isStringValue kind -> None
                            | _ -> Some "WorkspaceEdit contains a malformed documentChanges entry."
                        | _ -> Some "WorkspaceEdit documentChanges must contain objects.")
                    |> function
                        | Some reason -> Error reason
                        | None -> Ok()
                | null, (:? JsonObject as changes) ->
                    changes
                    |> Seq.tryPick (fun entry ->
                        match entry.Value with
                        | :? JsonArray as edits ->
                            match validateTextEdits edits with
                            | Ok() -> None
                            | Error reason -> Some reason
                        | _ -> Some "WorkspaceEdit changes entries must be TextEdit arrays.")
                    |> function
                        | Some reason -> Error reason
                        | None -> Ok()
                | null, null -> Ok()
                | _, _ -> Error "WorkspaceEdit must contain either documentChanges or changes in the LSP shape."
            | _ -> Error "FSAC rename result was not a WorkspaceEdit object or null."

    /// Applies a single-line TextEdit to one line, producing the preview line:
    /// keep the prefix before `startChar`, splice in `newText`, keep the suffix
    /// from `endChar`. Char offsets are clamped to the line so malformed ranges
    /// never throw.
    let applyEditToLine (line: string) (startChar: int) (endChar: int) (newText: string) : string =
        let safeStart = max 0 (min startChar line.Length)
        let safeEnd = max safeStart (min endChar line.Length)
        line[.. safeStart - 1] + newText + line[safeEnd..]

    /// Extracts (uri, edits) pairs from a raw WorkspaceEdit. Handles both the
    /// `documentChanges` array (FSAC's shape, possibly carrying resource ops that
    /// lack an `edits` array — those are skipped) and the legacy `changes` map.
    let extractFileEdits (workspaceEdit: JsonNode) : (string * JsonNode list) list =
        match workspaceEdit with
        | :? JsonObject as we ->
            match we["documentChanges"] with
            | :? JsonArray as docChanges ->
                [ for dc in docChanges do
                      match dc with
                      | :? JsonObject as o ->
                          match o["textDocument"], o["edits"] with
                          | (:? JsonObject as td), (:? JsonArray as edits) ->
                              match td["uri"] with
                              | null -> ()
                              | uriNode -> yield uriNode.GetValue<string>(), (edits |> Seq.cast<JsonNode> |> Seq.toList)
                          | _ -> ()
                      | _ -> () ]
            | _ ->
                match we["changes"] with
                | :? JsonObject as changes ->
                    [ for kv in changes do
                          match kv.Value with
                          | :? JsonArray as edits -> yield kv.Key, (edits |> Seq.cast<JsonNode> |> Seq.toList)
                          | _ -> () ]
                | _ -> []
        | _ -> []

    /// Identifies the symbol being renamed by reading the original text under the
    /// edit at the originating position. None when no covering edit is found.
    let private detectSymbol (ctx: Context) (grouped: (string * JsonNode list) list) : string option =
        grouped
        |> List.tryFind (fun (uri, _) -> String.Equals(uri, ctx.OriginatingUri, StringComparison.OrdinalIgnoreCase))
        |> Option.bind (fun (uri, edits) ->
            match ctx.LookupLines uri with
            | None -> None
            | Some lines ->
                edits
                |> List.tryPick (fun edit ->
                    match tryRange edit with
                    | Some(sl, sc, el, ec) when
                        sl = ctx.Line
                        && el = ctx.Line
                        && sc <= ctx.Character
                        && ctx.Character <= ec
                        && sl < lines.Length
                        ->
                        let line = lines[sl]
                        let safeStart = max 0 (min sc line.Length)
                        let safeEnd = max safeStart (min ec line.Length)
                        Some(line.Substring(safeStart, safeEnd - safeStart))
                    | _ -> None))

    let private rangeNodeOf (edit: JsonNode) : JsonNode =
        match edit with
        | :? JsonObject as o when not (isNull o["range"]) -> o["range"].DeepClone()
        | _ -> JsonObject() :> JsonNode

    let private editPreviewNode (linesOpt: string[] option) (edit: JsonNode) : JsonNode =
        let originalLine, previewLine =
            match tryRange edit, linesOpt with
            | Some(sl, _, el, _), Some lines when sl < lines.Length ->
                let orig = lines[sl]

                match tryRange edit with
                | Some(_, sc, _, ec) when el = sl -> orig, applyEditToLine orig sc ec (editNewText edit)
                | Some(_, sc, _, _) ->
                    // Multi-line range (rare for rename): splice newText after the
                    // start-line prefix as a best-effort single-line preview.
                    let safeStart = max 0 (min sc orig.Length)
                    orig, orig[.. safeStart - 1] + editNewText edit
                | None -> orig, orig
            | _ -> "", ""

        jobj
            [ "range", rangeNodeOf edit
              "originalLineText", jstr originalLine
              "previewLineText", jstr previewLine ]
        :> JsonNode

    /// Builds the grouped preview response from a raw WorkspaceEdit. Returns a
    /// `{ status: "no_symbol"; reason }` envelope when the edit is null/empty —
    /// i.e. FSAC could not resolve a renamable symbol at the position.
    let build (ctx: Context) (workspaceEdit: JsonNode) : JsonNode =
        match validateWorkspaceEdit workspaceEdit with
        | Error reason ->
            jobj
                [ "status", jstr "infrastructure_error"
                  "errorKind", jstr "protocol_error"
                  "message", jstr reason ]
            :> JsonNode
        | Ok() ->
            let grouped =
                extractFileEdits workspaceEdit
                |> List.groupBy fst
                |> List.map (fun (uri, items) -> uri, (items |> List.collect snd))

            let totalEdits = grouped |> List.sumBy (fun (_, edits) -> edits.Length)

            if List.isEmpty grouped || totalEdits = 0 then
                let display = ctx.UriToDisplay ctx.OriginatingUri

                jobj
                    [ "status", jstr "no_symbol"
                      "reason",
                      jstr
                          $"No renamable symbol at {Path.GetFileName display}:{ctx.Line}:{ctx.Character}. The position may fall on a keyword, literal, operator, whitespace, or a symbol the compiler cannot resolve." ]
                :> JsonNode
            else
                let symbol = detectSymbol ctx grouped

                let fileNodes =
                    grouped
                    |> List.map (fun (uri, edits) ->
                        let linesOpt = ctx.LookupLines uri
                        let editNodes = edits |> List.map (editPreviewNode linesOpt)

                        jobj
                            [ "file", jstr (ctx.UriToDisplay uri)
                              "editCount", jint edits.Length
                              "edits", JsonArray(editNodes |> List.toArray) :> JsonNode ]
                        :> JsonNode)

                let crossProject =
                    grouped
                    |> List.choose (fun (uri, _) -> ctx.ResolveProject uri)
                    |> List.distinct
                    |> List.length > 1

                jobj
                    [ "status", jstr "ok"
                      "symbol", (symbol |> Option.map jstr |> Option.defaultValue null)
                      "newName", jstr ctx.NewName
                      "totalEdits", jint totalEdits
                      "fileCount", jint grouped.Length
                      "files", JsonArray(fileNodes |> List.toArray) :> JsonNode
                      "crossProject", jbool crossProject ]
                :> JsonNode

// ─── CodeAction request building (#43) ─────────────────────────────────────────

/// Builds the `textDocument/codeAction` request params. Extracted to a pure,
/// testable seam so the #43 regression cannot recur silently: each JsonNode has a
/// single Parent reference, so assigning ONE position instance as both
/// `range.start` and `range.end` throws InvalidOperationException on the second
/// attach — failing every codeAction call before the RPC is even sent. `start` and
/// `end` MUST therefore be distinct node instances. `context.diagnostics` is always
/// present (empty here; the DiagnosticFixes wrapper populates it) to satisfy FSAC's
/// codeActionLiteralSupport handshake (#53).
module internal CodeActionRequest =

    let buildParams (uri: string) (line: int) (character: int) : JsonObject =
        let posNode () =
            jobj [ "line", jint line; "character", jint character ] :> JsonNode

        jobj
            [ "textDocument", jobj [ "uri", jstr uri ]
              "range", jobj [ "start", posNode (); "end", posNode () ]
              "context", jobj [ "diagnostics", JsonArray() :> JsonNode ] ]

// ─── FsAutoCompleteBridge ──────────────────────────────────────────────────────

type internal FsAutoCompleteBridge
    (
        ?startupTimeoutOverride: TimeSpan,
        ?requestTimeoutOverride: TimeSpan,
        ?cleanupTimeoutOverride: TimeSpan,
        ?cleanupDrainBarrierOverride: (unit -> Task option),
        ?fsacCommandOverride: string,
        ?fsacArgsOverride: string list,
        ?evaluatedSourceFilesProvider: (string -> Task<Result<string array, string>>)
    ) =
    let gate = new SemaphoreSlim(1, 1)
    let projectSwitchGate = new SemaphoreSlim(1, 1)
    let documents = ConcurrentDictionary<string, LspDocumentState>(DiagnosticIdentity.pathComparer)
    let diagnostics = ConcurrentDictionary<string, DiagnosticEnvelope>(DiagnosticIdentity.pathComparer)
    // A versionless publishDiagnostics notification cannot identify which
    // didOpen/didChange content it analyzed. Stamp every unproven initial open
    // and every subsequent change with its owning generation so an A -> B -> A
    // content cycle cannot make a delayed publication for B look current merely
    // because hashes equal A again. A first open proven identical to both the
    // generation baseline and stable disk content adds no new content state, so
    // it remains eligible for versionless diagnostics. An exact current server
    // version remains causal evidence in either case.
    let diagnosticContentTransitionTaints =
        ConcurrentDictionary<string, int64>(DiagnosticIdentity.pathComparer)
    // PublishDiagnostics runs on StreamJsonRpc's dispatch threads. Generation
    // transitions and the atomic envelope write share this lock so an old
    // notification cannot pass a generation check, pause, and repopulate the
    // replacement generation after Stop/Start has cleared it.
    let diagnosticsGenerationGate = obj()

    // Strong on-disk content hashes captured before each FSAC process starts.
    // A versionless FSAC publication can prove freshness only for content equal
    // to this causal generation baseline.
    let mutable diagnosticGenerationBaseline =
        System.Collections.Generic.Dictionary<string, string>(DiagnosticIdentity.pathComparer)

    let mutable runtimeDiagnosticContextFingerprint: string option = None
    let mutable diagnosticGenerationContextFingerprint: string option = None

    let mutable nextSessionGeneration = 0L

    [<VolatileField>]
    let mutable activeSessionGeneration = 0L

    let mutable lifecycleState = LspLifecycleState.Stopped

    [<VolatileField>]
    let mutable rpc: JsonRpc option = None

    [<VolatileField>]
    let mutable lspProcess: Process option = None

    [<VolatileField>]
    let mutable lspContainment: ProcessContainment option = None

    [<VolatileField>]
    let mutable runtimeProjectPath: string option = None

    [<VolatileField>]
    let mutable runtimeWorkspaceRoot: string option = None

    [<VolatileField>]
    let mutable runtimeLoadedProjects: string array = [||]

    let sourcePathComparer =
        if OperatingSystem.IsWindows() then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    // MSBuild Compile membership is authoritative for linked files that live outside
    // the selected workspace (and may sit below another .fsproj). Reads and replacement
    // happen under `gate`, together with the rest of the active LSP context.
    let mutable runtimeEvaluatedSourceFiles =
        System.Collections.Generic.HashSet<string>(sourcePathComparer)

    let startupTimeout =
        startupTimeoutOverride
        |> Option.defaultWith (fun () -> timeoutFromEnv "FSLANGMCP_LSP_STARTUP_TIMEOUT_MS" 60_000)

    let requestTimeout =
        requestTimeoutOverride
        |> Option.defaultWith (fun () -> timeoutFromEnv "FSLANGMCP_LSP_REQUEST_TIMEOUT_MS" 30_000)

    let cleanupTimeout =
        cleanupTimeoutOverride |> Option.defaultValue (TimeSpan.FromSeconds(5.0))

    [<VolatileField>]
    let mutable workspaceReady = false

    // The workspace-ready timestamp has three writers: markWorkspaceReady, called
    // (a) synchronously under `gate` from StartLspUnsafe's explicit-workspace path,
    // and (b) from the JSON-RPC dispatch thread with NO lock at all, whenever FSAC's
    // WorkspaceLoadTarget notification fires; and the reset in StopLspUnsafe, which
    // is gate-held at every call site except Dispose (Dispose calls StopLspUnsafe
    // directly, with no gate.WaitAsync, right before disposing `gate` itself). Because
    // (b) takes no gate — and Dispose's call takes none either — no amount of gate
    // discipline on the *readers*, or on this writer, closes the race by locking: a
    // `DateTimeOffset voption` is a multi-field struct with no atomic read/write
    // guarantee even marked [<VolatileField>], so a reader could observe a torn value
    // while writer (b) is mid-update (#194 review, M7). Storing UTC ticks as int64
    // instead sidesteps the problem without needing a lock anywhere, including in
    // Dispose: this field deliberately carries no [<VolatileField>] attribute and is
    // instead accessed only through Volatile.Read/Volatile.Write below — unlike
    // activeSessionGeneration above, which carries both the attribute and every access
    // going through Volatile.Read/Volatile.Write already (there the attribute is
    // redundant-but-harmless, not load-bearing — do NOT read this comment as license to
    // strip it). The attribute alone only orders access; it does not guarantee an
    // atomic read/write of a 64-bit value on a 32-bit runtime, whereas
    // Volatile.Read/Volatile.Write<Int64> are documented to be atomic even there — which
    // is why THIS field's correctness rests entirely on the explicit calls, with no
    // [<VolatileField>] to fall back on. So every writer — gated (a), ungated (b), or Dispose's
    // ungated reset — can just Volatile.Write it, and every reader gets a whole,
    // untorn value with no lock needed anywhere. 0L is the "unknown" sentinel
    // (ValueNone); UtcNow.UtcTicks is never 0 in practice. Go through
    // readWorkspaceReadyAt/writeWorkspaceReadyAt below rather than touching the
    // field directly, so both readers (setProjectReadiness's call site and
    // WorkspaceSymbolForContext) and both writer call sites share the one place
    // that knows about the ticks encoding.
    let mutable workspaceReadyAtTicks: int64 = 0L

    let readWorkspaceReadyAt () : DateTimeOffset voption =
        match Volatile.Read(&workspaceReadyAtTicks) with
        | 0L -> ValueNone
        | ticks -> ValueSome(DateTimeOffset(ticks, TimeSpan.Zero))

    let writeWorkspaceReadyAt (value: DateTimeOffset voption) : unit =
        let ticks =
            match value with
            | ValueNone -> 0L
            | ValueSome dto -> dto.UtcTicks

        Volatile.Write(&workspaceReadyAtTicks, ticks)

    [<VolatileField>]
    let mutable symbolIndexEverWarmed = false

    [<VolatileField>]
    let mutable stderrPump: Task option = None

    let mutable pendingLspCleanups = 0
    let mutable pendingLspCleanup: Task option = None

    let tryDispose (resource: unit -> IDisposable) =
        try
            resource().Dispose()
        with _ ->
            ()

    let cleanupLspResources
        (jsonRpc: JsonRpc option)
        (fsacProcess: Process option)
        (containment: ProcessContainment option)
        (pump: Task option)
        : Task =
        task {
            if jsonRpc.IsNone && fsacProcess.IsNone && containment.IsNone && pump.IsNone then
                return ()
            else
                Interlocked.Increment(&pendingLspCleanups) |> ignore

                try
                    // JsonRpc.Dispose only starts its asynchronous shutdown. Closing the
                    // redirected streams unblocks the pipe readers; Completion below proves
                    // that the old RPC generation is fully reaped before another is started.
                    match jsonRpc with
                    | Some instance ->
                        try
                            instance.Dispose()
                        with _ ->
                            ()
                    | None -> ()

                    match fsacProcess with
                    | Some fsacProc ->
                        try
                            match containment with
                            | Some owned -> terminateContainedProcess owned
                            | None when not fsacProc.HasExited -> fsacProc.Kill(true)
                            | None -> ()
                        with _ ->
                            ()

                        tryDispose (fun () -> fsacProc.StandardInput :> IDisposable)
                        tryDispose (fun () -> fsacProc.StandardOutput :> IDisposable)
                        tryDispose (fun () -> fsacProc.StandardError :> IDisposable)
                    | None -> ()

                    let completions =
                        [ match jsonRpc with
                          | Some instance -> yield instance.Completion
                          | None -> ()

                          match fsacProcess with
                          | Some fsacProc ->
                              try
                                  if not fsacProc.HasExited then
                                      yield fsacProc.WaitForExitAsync()
                              with _ ->
                                  ()
                          | _ -> ()

                          match pump with
                          | Some pumpTask -> yield pumpTask
                          | None -> ()

                          match cleanupDrainBarrierOverride |> Option.bind (fun barrier -> barrier ()) with
                          | Some barrier -> yield barrier
                          | None -> () ]

                    if not completions.IsEmpty then
                        try
                            do! Task.WhenAll(completions)
                        with _ ->
                            // Every completion has settled; cleanup faults are intentionally
                            // secondary to the request/startup error that initiated shutdown.
                            ()
                finally
                    match containment, fsacProcess with
                    | Some owned, _ ->
                        try
                            (owned :> IDisposable).Dispose()
                        with _ ->
                            ()
                    | None, Some fsacProc ->
                        try
                            fsacProc.Dispose()
                        with _ ->
                            ()
                    | None, None -> ()

                    Interlocked.Decrement(&pendingLspCleanups) |> ignore
        }

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

    let pathComparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let normalizedPath (path: string) =
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    let pathsEqual (left: string) (right: string) =
        String.Equals(normalizedPath left, normalizedPath right, pathComparison)

    let resolveEvaluatedSourceFiles (loadedProjects: string array) =
        task {
            let evaluatedFiles = System.Collections.Generic.HashSet<string>(sourcePathComparer)
            let elapsed = Stopwatch.StartNew()
            let mutable deadlineExpired = false

            match evaluatedSourceFilesProvider with
            | None -> ()
            | Some getEvaluatedFiles ->
                for projectPath in loadedProjects do
                    if not deadlineExpired then
                        let remaining = startupTimeout - elapsed.Elapsed

                        if remaining <= TimeSpan.Zero then
                            deadlineExpired <- true
                        else
                            try
                                let! result = (getEvaluatedFiles projectPath).WaitAsync(remaining)

                                match result with
                                | Ok paths ->
                                    for path in paths do
                                        if not (String.IsNullOrWhiteSpace path) then
                                            try
                                                evaluatedFiles.Add(normalizedPath path) |> ignore
                                            with _ ->
                                                ()
                                | Error _ -> ()
                            with
                            | :? TimeoutException -> deadlineExpired <- true
                            | _ ->
                                // Project selection and FSAC startup remain available
                                // when the optional evaluator cannot load one project.
                                // Directory/project heuristics below still provide a
                                // conservative fallback.
                                ()

            return evaluatedFiles
        }

    let captureDiagnosticGenerationBaseline () =
        let baseline =
            System.Collections.Generic.Dictionary<string, string>(DiagnosticIdentity.pathComparer)

        for file in runtimeEvaluatedSourceFiles do
            match DiagnosticIdentity.tryStableFileTextHash file with
            | Some hash -> baseline[DiagnosticIdentity.canonicalFileKeyFromPath file] <- hash
            | None -> ()

        baseline

    let tryBaselineHash (fileKey: string) =
        match diagnosticGenerationBaseline.TryGetValue(fileKey) with
        | true, hash -> Some hash
        | false, _ -> None

    let isDiagnosticContentTransitionTainted (fileKey: string) =
        match diagnosticContentTransitionTaints.TryGetValue(fileKey) with
        | true, generation -> generation = Volatile.Read(&activeSessionGeneration)
        | false, _ -> false

    let diagnosticFreshness (fileKey: string) (envelope: DiagnosticEnvelope) =
        if envelope.Generation <> Volatile.Read(&activeSessionGeneration) then
            Stale
        else
            let baseline = tryBaselineHash fileKey
            let disk = DiagnosticIdentity.tryStableFileTextHash fileKey

            match documents.TryGetValue(fileKey) with
            | true, document ->
                match envelope.ServerVersion with
                | Some version when version = document.Version -> Current
                | Some _ -> Stale
                | None when isDiagnosticContentTransitionTainted fileKey -> Stale
                | None ->
                    match baseline, disk with
                    | Some expected, Some actual when expected = actual && document.TextHash = expected -> Current
                    | Some expected, Some actual when expected <> actual && document.TextHash = actual ->
                        DiskContentChanged
                    | _ -> Stale
            | false, _ ->
                match baseline, disk with
                | Some expected, Some actual when expected = actual -> Current
                | Some expected, Some actual when expected <> actual -> DiskContentChanged
                | _ -> Stale

    let diagnosticBaselineNeedsRefresh (fileKey: string) =
        match tryBaselineHash fileKey, DiagnosticIdentity.tryStableFileTextHash fileKey with
        | Some expected, Some actual when expected <> actual ->
            match documents.TryGetValue(fileKey) with
            | false, _ -> true
            | true, document -> document.TextHash = expected || document.TextHash = actual
        | None, Some actual ->
            match documents.TryGetValue(fileKey) with
            | false, _ -> true
            | true, document -> document.TextHash = actual
        | _ -> false

    let diagnosticTaintNeedsRebaseline (fileKey: string) =
        if not (isDiagnosticContentTransitionTainted fileKey) then
            false
        else
            // Only an envelope explicitly bound to the current client document
            // version can be consumed without rebasing. A versionless envelope
            // never clears the generation taint, so if it later overwrites this
            // exact envelope the next context read still fails closed.
            match documents.TryGetValue(fileKey), diagnostics.TryGetValue(fileKey) with
            | (true, document), (true, envelope) when envelope.Generation = Volatile.Read(&activeSessionGeneration) ->
                match envelope.ServerVersion with
                | Some version -> version <> document.Version
                | None -> true
            | _ -> true

    let contextFingerprintNeedsRefresh (expected: string option) =
        match expected with
        | Some fingerprint ->
            diagnosticGenerationContextFingerprint
            |> Option.exists (fun actual ->
                String.Equals(actual, fingerprint, StringComparison.Ordinal))
            |> not
        | None -> false

    let isLiveSession () =
        match rpc, lspProcess with
        | Some jsonRpc, Some fsacProc ->
            try
                not fsacProc.HasExited && not jsonRpc.Completion.IsCompleted
            with _ ->
                false
        | _ -> false

    let contextMatches (requestedPath: string option) =
        match requestedPath, runtimeProjectPath with
        | None, Some _ -> true
        | None, None -> false
        | Some requested, Some selected ->
            let requestedFull = normalizedPath requested

            pathsEqual requestedFull selected
            || (String.Equals(Path.GetExtension(requestedFull), ".fsproj", StringComparison.OrdinalIgnoreCase)
                && (runtimeLoadedProjects |> Array.exists (pathsEqual requestedFull)))
        | Some _, None -> false

    let tryFindOwningProject (filePath: string) =
        let rec walk directory =
            if String.IsNullOrWhiteSpace directory then
                None
            else
                let projects =
                    try
                        Directory.GetFiles(directory, "*.fsproj", SearchOption.TopDirectoryOnly)
                    with _ ->
                        [||]

                match projects |> Array.tryFind (fun project -> runtimeLoadedProjects |> Array.exists (pathsEqual project)) with
                | Some loaded -> Some(normalizedPath loaded)
                | None when projects.Length = 1 -> Some(normalizedPath projects[0])
                | _ ->
                    let parent = Directory.GetParent(directory)
                    if isNull parent then None else walk parent.FullName

        try
            let fullPath = normalizedPath filePath
            let directory = if Directory.Exists fullPath then fullPath else Path.GetDirectoryName fullPath
            walk directory
        with _ ->
            None

    let fileContextMatches (filePath: string) =
        match runtimeProjectPath with
        | None -> false
        | Some _ ->
            let fullPath = normalizedPath filePath

            if runtimeEvaluatedSourceFiles.Contains fullPath then
                true
            else
                match tryFindOwningProject fullPath with
                | Some owner -> runtimeLoadedProjects |> Array.exists (pathsEqual owner)
                | None ->
                    // Scripts and unsaved files may not have an owning .fsproj. Keep them
                    // bound to the selected workspace root instead of silently accepting a
                    // path from another checkout.
                    match runtimeWorkspaceRoot with
                    | Some root ->
                        let relative = Path.GetRelativePath(normalizedPath root, fullPath)
                        relative <> ".."
                        && not (relative.StartsWith(".." + string Path.DirectorySeparatorChar, pathComparison))
                        && not (Path.IsPathRooted relative)
                    | None -> false

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
        fsacCommandOverride
        |> Option.orElseWith (fun () ->
            Environment.GetEnvironmentVariable("FSAC_COMMAND")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not))
        |> Option.defaultValue "fsautocomplete"

    let fsacArgs () =
        fsacArgsOverride
        |> Option.defaultWith (fun () -> Environment.GetEnvironmentVariable("FSAC_ARGS") |> Option.ofObj |> parseArgs)

    let explicitWorkspacePath () =
        runtimeProjectPath
        |> Option.filter (fun path ->
            let ext = Path.GetExtension(path)

            String.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase)
            || String.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase)
            || String.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))

    let useAutomaticWorkspaceInit () = explicitWorkspacePath().IsNone

    let markWorkspaceReady generation () =
        if Volatile.Read(&activeSessionGeneration) = generation then
            workspaceReady <- true
            writeWorkspaceReadyAt (ValueSome DateTimeOffset.UtcNow)
            lifecycleState <- LspLifecycleState.Ready generation

    let isTransportFailure (jsonRpc: JsonRpc) (ex: exn) =
        ex :? IOException
        || ex :? ObjectDisposedException
        || ex :? EndOfStreamException
        || jsonRpc.Completion.IsCompleted

    member private _.BeginLspCleanupUnsafe
        (
            stoppedRpc: JsonRpc option,
            stoppedProcess: Process option,
            stoppedContainment: ProcessContainment option,
            stoppedPump: Task option
        )
        =
        if stoppedRpc.IsSome || stoppedProcess.IsSome || stoppedContainment.IsSome || stoppedPump.IsSome then
            let cleanup = cleanupLspResources stoppedRpc stoppedProcess stoppedContainment stoppedPump

            pendingLspCleanup <-
                match pendingLspCleanup with
                | Some previous -> Some(Task.WhenAll([| previous; cleanup |]))
                | None -> Some cleanup

    member private _.AwaitPendingLspCleanupUnsafe() : Task<bool> =
        task {
            match pendingLspCleanup with
            | None -> return true
            | Some cleanup ->
                try
                    // Bound only this caller's wait. The actual drain task remains retained
                    // after a timeout, so no later generation can start over live pipe readers.
                    do! cleanup.WaitAsync(cleanupTimeout)
                    pendingLspCleanup <- None
                    return true
                with :? TimeoutException ->
                    return false
        }

    member private this.StopLspUnsafe() : Task<bool> =
        task {
            let stoppedRpc = rpc
            let stoppedProcess = lspProcess
            let stoppedContainment = lspContainment
            let stoppedPump = stderrPump
            let stoppedGeneration = Volatile.Read(&activeSessionGeneration)

            lifecycleState <- LspLifecycleState.Stopping stoppedGeneration

            rpc <- None
            lspProcess <- None
            lspContainment <- None
            stderrPump <- None
            lock diagnosticsGenerationGate (fun () ->
                Volatile.Write(&activeSessionGeneration, 0L)
                documents.Clear()
                diagnostics.Clear()
                diagnosticContentTransitionTaints.Clear()
                diagnosticGenerationBaseline <-
                    System.Collections.Generic.Dictionary<string, string>(DiagnosticIdentity.pathComparer)
                diagnosticGenerationContextFingerprint <- None)
            workspaceReady <- false
            writeWorkspaceReadyAt ValueNone
            symbolIndexEverWarmed <- false

            this.BeginLspCleanupUnsafe(stoppedRpc, stoppedProcess, stoppedContainment, stoppedPump)
            let! completed = this.AwaitPendingLspCleanupUnsafe()

            if completed then
                lifecycleState <- LspLifecycleState.Stopped
            else
                lifecycleState <-
                    LspLifecycleState.Faulted(stoppedGeneration, "Timed out while draining the retired FSAC session.")

            return completed
        }

    member private this.InvokeLiveUnsafe(jsonRpc: JsonRpc, methodName: string, parameters: JsonObject) : Task<JsonNode> =
        task {
            try
                return! invokeWithTimeout jsonRpc methodName parameters requestTimeout
            with
            | :? TimeoutException as ex ->
                match rpc with
                | Some current when Object.ReferenceEquals(current, jsonRpc) ->
                    let! _ = this.StopLspUnsafe()
                    ()
                | _ -> ()

                return raise ex
            | ex when isTransportFailure jsonRpc ex ->
                match rpc with
                | Some current when Object.ReferenceEquals(current, jsonRpc) ->
                    let! _ = this.StopLspUnsafe()
                    ()
                | _ -> ()

                return raise ex
        }

    member private this.NotifyLiveUnsafe(jsonRpc: JsonRpc, methodName: string, parameters: JsonObject) : Task =
        task {
            try
                do! notifyWithTimeout jsonRpc methodName parameters requestTimeout
            with
            | :? TimeoutException as ex ->
                match rpc with
                | Some current when Object.ReferenceEquals(current, jsonRpc) ->
                    let! _ = this.StopLspUnsafe()
                    ()
                | _ -> ()

                return raise ex
            | ex when isTransportFailure jsonRpc ex ->
                match rpc with
                | Some current when Object.ReferenceEquals(current, jsonRpc) ->
                    let! _ = this.StopLspUnsafe()
                    ()
                | _ -> ()

                return raise ex
        }

    member _.DiagnosticsStore = diagnostics

    member _.CurrentProjectPath = runtimeProjectPath

    member _.CurrentWorkspaceRoot = runtimeWorkspaceRoot

    member _.LoadedProjects = Array.copy runtimeLoadedProjects

    member _.IsSessionLive = isLiveSession ()

    member _.IsWorkspaceReady = workspaceReady

    member _.IsSymbolIndexReady = symbolIndexEverWarmed

    member _.DiagnosticsFileCount = diagnostics.Count

    /// Returns the live FSAC child process handle, or None if FSAC is not running.
    member _.FsacProcess: Process option = lspProcess

    /// Number of retired LSP generations still draining redirected IO.
    member _.PendingLspCleanupCount = Volatile.Read(&pendingLspCleanups)

    member _.SessionGeneration = Volatile.Read(&activeSessionGeneration)

    member _.LifecycleState =
        match lifecycleState with
        | LspLifecycleState.Stopped -> "stopped"
        | LspLifecycleState.Starting _ -> "starting"
        | LspLifecycleState.Ready _ -> "ready"
        | LspLifecycleState.Stopping _ -> "stopping"
        | LspLifecycleState.Faulted _ -> "faulted"

    // Wait until workspaceReady is true or timeout elapses
    member _.WaitForReady(timeout: TimeSpan) : Task<bool> =
        task {
            let sw = Stopwatch.StartNew()

            while not workspaceReady && sw.Elapsed < timeout do
                let remaining = timeout - sw.Elapsed
                let delay = min 50 (max 1 (int remaining.TotalMilliseconds))
                do! Task.Delay(delay)

            return workspaceReady
        }

    // Return not-ready JSON if workspace not loaded yet
    member _.NotReadyResponse() : JsonNode =
        jobj
            [ "status", jstr "not_ready"
              "contextMatched", jbool true
              "message", jstr "fsautocomplete is still loading the project. Try again in a moment."
              "activeProjectPath", runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
              "sessionGeneration", jint64 (Volatile.Read(&activeSessionGeneration)) ]
        :> JsonNode

    member private _.FileContextMismatchResponse(path: string) : JsonNode =
        jobj
            [ "status", jstr "context_mismatch"
              "contextMatched", jbool false
              "message",
              jstr
                  "The requested source file is not part of the active fsautocomplete project context. Call set_project with restartLsp=true for the file's project."
              "requestedFile", jstr (Path.GetFullPath path)
              "activeProjectPath", runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
              "sessionGeneration", jint64 (Volatile.Read(&activeSessionGeneration)) ]
        :> JsonNode

    member private _.WithContextMetadata(response: JsonNode) =
        match response with
        | :? JsonObject as obj ->
            obj["contextMatched"] <- jbool true
            obj["activeProjectPath"] <- runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
            obj["sessionGeneration"] <- jint64 (Volatile.Read(&activeSessionGeneration))
        | _ -> ()

        response

    member private _.CopyContextMetadata(source: JsonObject, response: JsonNode) =
        match response with
        | :? JsonObject as target ->
            for key in [ "contextMatched"; "activeProjectPath"; "sessionGeneration" ] do
                target[key] <-
                    match source[key] with
                    | null -> null
                    | value -> value.DeepClone()
        | _ -> ()

        response

    member private this.SetProjectCore(args: SetProjectArgs) : Task<JsonNode> =
        let invalidWorkspace =
            args.workspacePath
            |> Option.map Path.GetFullPath
            |> Option.filter (Directory.Exists >> not)

        match invalidWorkspace with
        | Some workspace ->
            Task.FromResult(
                jobj
                    [ "status", jstr "invalid_args"
                      "message", jstr $"workspacePath must be an existing directory: {workspace}" ]
                :> JsonNode
            )
        | None ->
          task {
            // Guard before Path.GetFullPath: a wrong/missing key deserializes projectPath to
            // null, and Path.GetFullPath(null) throws ArgumentNullException naming the internal
            // 'path' param — misleading callers who passed the wrong key. Surface 'projectPath'.
            match ArgsValidation.requireNonBlank "projectPath" args.projectPath with
            | Error envelope -> return envelope
            | Ok projectPathArg ->
                let inputPath = Path.GetFullPath(projectPathArg)

                if not (File.Exists(inputPath) || Directory.Exists(inputPath)) then
                    return
                        jobj
                            [ "status", jstr "invalid_args"
                              "message", jstr $"projectPath does not exist: {inputPath}" ]
                        :> JsonNode
                else
                    match WorkspaceSelection.select inputPath with
                    | WorkspaceSelection.Invalid reason ->
                        return jobj [ "status", jstr "invalid_args"; "message", jstr reason ] :> JsonNode
                    | WorkspaceSelection.Ambiguous candidates ->
                        return
                            jobj
                                [ "status", jstr "ambiguous_workspace"
                                  "message",
                                  jstr
                                      "Multiple workspace candidates found. Pass an explicit .sln/.slnx/.fsproj path."
                                  "candidates",
                                  JsonArray(candidates |> List.map WorkspaceSelection.candidateToJson |> List.toArray)
                                  :> JsonNode ]
                            :> JsonNode
                    | WorkspaceSelection.Selected(projectPath, selectionCandidates) ->
                        // #192: Init.init resolves the SDK from the *target's* global.json,
                        // and fsautocomplete calls it during its own startup with nothing to
                        // catch the failure — the child dies before answering `initialize`
                        // and the only symptom reaching the agent is StreamJsonRpc's
                        // "The JSON-RPC connection with the remote party was lost". Decide
                        // the provable case here, before any MSBuild evaluation or FSAC
                        // process exists, and leave the active context untouched.
                        // Off the dispatch thread: the (cached, once-per-process) SDK
                        // enumeration spawns `dotnet --list-sdks`.
                        // listProjects is pure solution-file parsing (no MSBuild), so it is
                        // safe to enumerate before the pre-flight; a nested global.json under
                        // one member project would kill FSAC just as effectively as the root.
                        let loadedProjects = FsLangMcp.ProjectFiles.SolutionParsing.listProjects projectPath

                        // FSAC calls Init.init with its *process working directory*, which
                        // StartLspUnsafe sets to the resolved workspace root — so the
                        // workspace candidates matter as much as the project's own directory.
                        let preflightDirectories =
                            [ resolveWorkspaceFromProjectPath projectPath
                              if Directory.Exists inputPath then
                                  inputPath
                              yield! (args.workspacePath |> Option.map Path.GetFullPath |> Option.toList)
                              for memberProject in loadedProjects do
                                  resolveWorkspaceFromProjectPath memberProject ]

                        // #192 review: the SDK list is cached for the life of the host, and the
                        // cache is one-sided — it can pass a project but never reject one. That
                        // makes "SDK installed mid-session" self-healing and "SDK REMOVED
                        // mid-session" invisible: the gate keeps passing from a stale list and
                        // FSAC dies with the opaque connection-loss error again. set_project is
                        // the one boundary where a re-probe is free next to the FSAC restart and
                        // workspace load it precedes, so drop the cache here (lazy per-project
                        // starts keep using it).
                        let! sdkVerdict =
                            Task.Run(fun () ->
                                SdkPreflight.invalidateCache ()
                                SdkPreflight.check preflightDirectories)

                        match sdkVerdict with
                        | SdkPreflight.SdkNotFound(pin, installedSdks) ->
                            return SdkPreflight.toEnvelope pin installedSdks
                        | SdkPreflight.Proceed ->

                        let isSolution =
                            projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                            || projectPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)

                        let requestedWorkspace =
                            args.workspacePath
                            |> Option.map Path.GetFullPath
                            |> Option.orElseWith (fun () ->
                                if Directory.Exists inputPath then Some inputPath else None)

                        let resolvedWorkspace =
                            requestedWorkspace
                            |> Option.defaultWith (fun () ->
                                if isSolution then
                                    Path.GetDirectoryName(projectPath)
                                else
                                    resolveWorkspaceFromProjectPath projectPath)

                        let! evaluatedSourceFiles = resolveEvaluatedSourceFiles loadedProjects
                        let restartLsp = args.restartLsp |> Option.defaultValue true
                        let mutable lspWasRunning = false
                        let mutable restartRequired = false

                        do! gate.WaitAsync()

                        try
                            lspWasRunning <- isLiveSession ()

                            let sameLiveContext =
                                runtimeProjectPath |> Option.exists (pathsEqual projectPath)
                                && runtimeWorkspaceRoot |> Option.exists (pathsEqual resolvedWorkspace)

                            if not restartLsp && lspWasRunning && not sameLiveContext then
                                // Do not create split-brain state: the advertised target and
                                // live FSAC must always identify the same workspace.
                                restartRequired <- true
                            else
                                // A completed RPC/process pair is not reusable. Reap it even
                                // when the caller only asked to select an FCS context.
                                if not lspWasRunning && (rpc.IsSome || lspProcess.IsSome) then
                                    let! cleanupCompleted = this.StopLspUnsafe()

                                    if not cleanupCompleted then
                                        invalidOp "Timed out while draining the previous FSAC LSP generation."

                                if restartLsp then
                                    let! cleanupCompleted = this.StopLspUnsafe()

                                    if not cleanupCompleted then
                                        invalidOp "Timed out while draining the previous FSAC LSP generation."

                                runtimeProjectPath <- Some projectPath
                                runtimeWorkspaceRoot <- Some resolvedWorkspace
                                runtimeLoadedProjects <- loadedProjects
                                runtimeEvaluatedSourceFiles <- evaluatedSourceFiles
                                runtimeDiagnosticContextFingerprint <- None

                                Environment.SetEnvironmentVariable("FSA_PROJECT_PATH", projectPath)
                                Environment.SetEnvironmentVariable("FSA_WORKSPACE_ROOT", resolvedWorkspace)

                                if restartLsp then
                                    let! _ = this.StartLspUnsafe()
                                    ()
                        finally
                            gate.Release() |> ignore

                        if restartRequired then
                            return
                                jobj
                                    [ "status", jstr "restart_required"
                                      "message",
                                      jstr
                                          "A live fsautocomplete session is bound to another project context. Retry with restartLsp=true; the active context was not changed."
                                      "requestedProjectPath", jstr projectPath
                                      "activeProjectPath",
                                      runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                                      "activeWorkspaceRoot",
                                      runtimeWorkspaceRoot |> Option.map jstr |> Option.defaultValue null ]
                                :> JsonNode
                        else
                            let loadedProjectsNode = JsonArray(loadedProjects |> Array.map jstr) :> JsonNode

                            let lspReplacedExistingProcess =
                                LspResponseShape.lspRestartOccurred restartLsp lspWasRunning

                            let! readyObserved =
                                if restartLsp then
                                    this.WaitForReady(TimeSpan.FromSeconds(30.0))
                                else
                                    Task.FromResult(workspaceReady && isLiveSession ())

                            let live = isLiveSession ()
                            // A ready notification is historical once the child/RPC has
                            // completed. Never advertise a dead generation as usable.
                            let ready = readyObserved && live

                            let loadStatus =
                                if restartLsp then
                                    if ready then
                                        "ready"
                                    elif not live then
                                        "faulted"
                                    else
                                        "timeout"
                                elif not live then
                                    "not_started"
                                elif ready then
                                    "ready"
                                else
                                    "warming"

                            // readWorkspaceReadyAt() is a plain Volatile.Read — no gate needed
                            // (or wanted: a second gate.WaitAsync() here would count contended
                            // wait time into "elapsed since ready", per #194 review N1, and could
                            // block this call on an unrelated in-flight request for up to
                            // requestTimeout/startupTimeout for no reason, per N2). `now` is read
                            // in the same expression so nothing sits between the two.
                            let readinessNode =
                                LspResponseShape.setProjectReadiness
                                    ready
                                    symbolIndexEverWarmed
                                    (restartLsp || live)
                                    (readWorkspaceReadyAt ())
                                    DateTimeOffset.UtcNow
                                    LspResponseShape.symbolIndexWarmupWindow

                            return
                                jobj
                                    [ "status", jstr "ok"
                                      "result",
                                      jobj
                                          [ "fslangmcpVersion", jstr FsLangMcp.Version.current
                                            "projectPath", jstr projectPath
                                            "requestedPath", jstr inputPath
                                            "workspaceRoot", jstr resolvedWorkspace
                                            "lspRestartRequested", jbool restartLsp
                                            "lspRestarted", jbool restartLsp
                                            "lspReplacedExistingProcess", jbool lspReplacedExistingProcess
                                            "solutionMode", jbool isSolution
                                            "workspaceLoadStatus", jstr loadStatus
                                            "lspLifecycleState", jstr this.LifecycleState
                                            "loadedProjects", loadedProjectsNode
                                            "readiness", readinessNode
                                            "sessionGeneration", jint64 (Volatile.Read(&activeSessionGeneration))
                                            "workspaceCandidates",
                                            JsonArray(
                                                selectionCandidates
                                                |> List.map WorkspaceSelection.candidateToJson
                                                |> List.toArray
                                            )
                                            :> JsonNode ]
                                      :> JsonNode ]
                                :> JsonNode
        }

    member this.SetProject(args: SetProjectArgs) : Task<JsonNode> =
        task {
            do! projectSwitchGate.WaitAsync()

            try
                return! this.SetProjectCore(args)
            finally
                projectSwitchGate.Release() |> ignore
        }

    // StartLspUnsafe: assumes gate is already held by caller
    member private this.StartLspUnsafe() : Task<JsonRpc> =
        task {
            let! cleanupCompleted = this.AwaitPendingLspCleanupUnsafe()

            if not cleanupCompleted then
                invalidOp "Timed out while draining the previous FSAC LSP generation."

            let command = fsacCommand ()
            let args = fsacArgs ()
            let workspaceRoot = getWorkspaceRoot ()

            // #192: set_project screens the pin up front, but a pin can be poisoned
            // AFTER a successful set_project — a branch switch that adds a global.json,
            // an SDK removed from the machine — and every later lazy start
            // (EnsureStartedUnsafe) or diagnostic-input restart lands here. Without this,
            // those paths reproduce the original opaque "connection lost": fsautocomplete
            // calls Init.init with exactly this working directory and dies before it
            // answers `initialize`. Raised, not returned: this method owes its caller a
            // JsonRpc, and every caller funnels the exception into the shared typed
            // envelope. Nothing has been mutated yet, so the failure is state-neutral.
            //
            // The member projects belong in the candidate list too: `fsharp/workspaceLoad`
            // hands FSAC the solution, so it loads every member, and a nested global.json
            // under one of them kills the handshake exactly like a root one. Screening
            // only workspaceRoot would leave that case reporting `disconnected` again.
            do!
                Task.Run(fun () ->
                    SdkPreflight.ensure
                        [ workspaceRoot
                          yield! (runtimeProjectPath |> Option.map resolveWorkspaceFromProjectPath |> Option.toList)
                          yield! (runtimeLoadedProjects |> Seq.map resolveWorkspaceFromProjectPath) ])

            let generation = Interlocked.Increment(&nextSessionGeneration)
            // Capture before the FSAC process exists: a versionless publication from
            // this generation cannot legitimately describe content older than this
            // baseline. Later disk changes therefore fail closed.
            let generationBaseline = captureDiagnosticGenerationBaseline ()

            lock diagnosticsGenerationGate (fun () ->
                // Start can follow a handshake failure for which no committed `rpc`
                // existed, so establish every generation from an empty snapshot.
                documents.Clear()
                diagnostics.Clear()
                diagnosticContentTransitionTaints.Clear()
                diagnosticGenerationBaseline <- generationBaseline
                diagnosticGenerationContextFingerprint <- runtimeDiagnosticContextFingerprint
                Volatile.Write(&activeSessionGeneration, generation))
            lifecycleState <- LspLifecycleState.Starting generation

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

            let containment =
                try
                    startContainedProcess psi
                with ex ->
                    lock diagnosticsGenerationGate (fun () ->
                        Volatile.Write(&activeSessionGeneration, 0L)
                        documents.Clear()
                        diagnostics.Clear()
                        diagnosticContentTransitionTaints.Clear()
                        diagnosticGenerationBaseline <-
                            System.Collections.Generic.Dictionary<string, string>(
                                DiagnosticIdentity.pathComparer
                            )
                        diagnosticGenerationContextFingerprint <- None)
                    lifecycleState <- LspLifecycleState.Faulted(generation, ex.Message)
                    raise ex

            let fsacProc = containment.Process

            // The handshake below (RPC construction, `initialize`, `fsharp/workspaceLoad`)
            // can throw. Until `rpc`/`lspProcess` are assigned at the very end, this method
            // is the only owner of `fsacProc` (and `jsonRpc` once constructed) — if we let
            // the exception propagate without cleaning them up first, the already-started
            // fsautocomplete process leaks (nothing else can ever reap it).
            let mutable startedJsonRpc: JsonRpc option = None
            let mutable startedStderrPump: Task option = None

            try
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
                startedStderrPump <- Some pumpStderr

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
                startedJsonRpc <- Some jsonRpc
                let targetOptions = JsonRpcTargetOptions(AllowNonPublicInvocation = true)

                jsonRpc.AddLocalRpcTarget(
                    new DiagnosticsTarget(
                        diagnostics,
                        generation,
                        (fun candidate -> Volatile.Read(&activeSessionGeneration) = candidate),
                        diagnosticsGenerationGate
                    ),
                    targetOptions
                )
                |> ignore

                jsonRpc.AddLocalRpcTarget(new WorkspaceLoadTarget(markWorkspaceReady generation), targetOptions)
                |> ignore

                jsonRpc.AddLocalRpcTarget(new WindowMessageTarget(), targetOptions) |> ignore

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
                                      "publishDiagnostics",
                                      jobj [ "relatedInformation", jbool true; "versionSupport", jbool true ]
                                      "synchronization", jobj [ "didSave", jbool true ] ] ] ]

                let! _ = invokeWithTimeout jsonRpc "initialize" initializeParams startupTimeout
                do! notifyWithTimeout jsonRpc "initialized" (JsonObject()) startupTimeout

                match explicitWorkspacePath () with
                | Some workspacePath ->
                    let document = jobj [ "uri", jstr (toFileUri workspacePath) ] :> JsonNode
                    let documents = JsonArray(document)

                    let workspaceLoadParams =
                        jobj
                            [ "TextDocuments", documents.DeepClone()
                              "textDocuments", documents.DeepClone() ]

                    let! _ = invokeWithTimeout jsonRpc "fsharp/workspaceLoad" workspaceLoadParams startupTimeout

                    markWorkspaceReady generation ()
                | None -> ()

                rpc <- Some jsonRpc
                lspProcess <- Some fsacProc
                lspContainment <- Some containment

                jsonRpc.Completion.ContinueWith(
                    (fun (completed: Task) ->
                        if Volatile.Read(&activeSessionGeneration) = generation then
                            workspaceReady <- false

                            let reason =
                                if completed.IsFaulted then
                                    completed.Exception.GetBaseException().Message
                                elif completed.IsCanceled then
                                    "FSAC RPC session was cancelled."
                                else
                                    "FSAC RPC session completed."

                            lifecycleState <- LspLifecycleState.Faulted(generation, reason)),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
                |> ignore

                return jsonRpc
            with ex ->
                // Handshake failed before the fields above were committed: this method is
                // still the sole owner of fsacProc/jsonRpc, so reap them here or they leak.
                stderrPump <- None
                lock diagnosticsGenerationGate (fun () ->
                    Volatile.Write(&activeSessionGeneration, 0L)
                    documents.Clear()
                    diagnostics.Clear()
                    diagnosticContentTransitionTaints.Clear()
                    diagnosticGenerationBaseline <-
                        System.Collections.Generic.Dictionary<string, string>(DiagnosticIdentity.pathComparer)
                    diagnosticGenerationContextFingerprint <- None)
                lifecycleState <- LspLifecycleState.Faulted(generation, ex.Message)
                this.BeginLspCleanupUnsafe(startedJsonRpc, Some fsacProc, Some containment, startedStderrPump)
                let! _ = this.AwaitPendingLspCleanupUnsafe()
                return raise ex
        }

    // Caller holds gate for the whole operation, including the eventual RPC invoke.
    member private this.EnsureStartedUnsafe() : Task<JsonRpc> =
        task {
            match rpc with
            | Some existing when isLiveSession () -> return existing
            | Some _ ->
                let! cleanupCompleted = this.StopLspUnsafe()

                if not cleanupCompleted then
                    invalidOp "Timed out while draining the failed FSAC LSP generation."

                return! this.StartLspUnsafe()
            | None -> return! this.StartLspUnsafe()
        }

    member private this.SyncDocument(jsonRpc: JsonRpc, path: string, providedText: string option) : Task<string> =
        task {
            let fullPath = Path.GetFullPath(path)
            let uri = toFileUri fullPath
            let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath fullPath
            let text = providedText |> Option.defaultWith (fun () -> File.ReadAllText(fullPath))
            let textHash = DiagnosticIdentity.textHash text
            let mutable version = 1
            let mutable opened = false
            let mutable requiresDiagnosticTaint = true

            lock diagnosticsGenerationGate (fun () ->
                // Once a document changes, no previously cached publication may be
                // correlated with the new client version. A first didOpen is the one
                // exception when strong baseline, stable disk, and opened text all
                // prove the same content; no causal content transition occurred.
                diagnostics.TryRemove(fileKey) |> ignore

                match documents.TryGetValue(fileKey) with
                | true, state ->
                    state.Version <- state.Version + 1
                    state.Text <- text
                    state.TextHash <- textHash
                    version <- state.Version
                | false, _ ->
                    opened <- true

                    requiresDiagnosticTaint <-
                        match tryBaselineHash fileKey, DiagnosticIdentity.tryStableFileTextHash fileKey with
                        | Some baselineHash, Some diskHash ->
                            textHash <> baselineHash || diskHash <> baselineHash
                        | _ -> true

                    documents[fileKey] <-
                        { Version = 1
                          Text = text
                          TextHash = textHash }

                let generation = Volatile.Read(&activeSessionGeneration)

                // Never remove a taint here: once this generation has observed a
                // causal content transition, a later A -> B -> A cycle cannot restore
                // versionless causality merely by returning to the baseline hash.
                if generation > 0L && requiresDiagnosticTaint then
                    diagnosticContentTransitionTaints[fileKey] <- generation)

            if not opened then
                let didChangeParams =
                    jobj
                        [ "textDocument", jobj [ "uri", jstr uri; "version", jint version ]
                          "contentChanges", JsonArray(jobj [ "text", jstr text ] :> JsonNode) :> JsonNode ]

                do! this.NotifyLiveUnsafe(jsonRpc, "textDocument/didChange", didChangeParams)
                return uri
            else
                let didOpenParams =
                    jobj
                        [ "textDocument",
                          jobj
                              [ "uri", jstr uri
                                "languageId", jstr "fsharp"
                                "version", jint 1
                                "text", jstr text ] ]

                do! this.NotifyLiveUnsafe(jsonRpc, "textDocument/didOpen", didOpenParams)
                return uri
        }

    // Keep sync + invoke in one critical section so set_project cannot dispose the
    // captured JsonRpc while a request is in flight.
    member private this.WithDocument
        (path: string, providedText: string option, methodName: string, mkParams: string -> JsonObject)
        : Task<JsonNode> =
        task {
            do! gate.WaitAsync()

            try
                if not (fileContextMatches path) then
                    return this.FileContextMismatchResponse(path)
                else
                    let! jsonRpc = this.EnsureStartedUnsafe()

                    if not workspaceReady then
                        return this.NotReadyResponse()
                    else
                        let! uri = this.SyncDocument(jsonRpc, path, providedText)
                        let parameters = mkParams uri
                        let! response = this.InvokeLiveUnsafe(jsonRpc, methodName, parameters)

                        return
                            this.WithContextMetadata(jobj [ "status", jstr "ok"; "result", response ] :> JsonNode)
            finally
                gate.Release() |> ignore
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

    member this.WorkspaceSymbolForContext
        (requestedProjectPath: string option, args: WorkspaceSymbolArgs)
        : Task<JsonNode> =
        task {
            do! gate.WaitAsync()

            try
                let requestedFull = requestedProjectPath |> Option.map Path.GetFullPath

                if not (contextMatches requestedFull) then
                    return
                        jobj
                            [ "status", jstr "context_mismatch"
                              "contextMatched", jbool false
                              "message",
                              jstr
                                  "The active fsautocomplete workspace does not contain the requested project. Call set_project with restartLsp=true before using this result as evidence."
                              "requestedProjectPath", requestedFull |> Option.map jstr |> Option.defaultValue null
                              "activeProjectPath", runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                              "sessionGeneration", jint64 (Volatile.Read(&activeSessionGeneration)) ]
                        :> JsonNode
                else
                    let! jsonRpc = this.EnsureStartedUnsafe()

                    if not workspaceReady then
                        return
                            jobj
                                [ "status", jstr "not_ready"
                                  "contextMatched", jbool true
                                  "message", jstr "fsautocomplete is still loading the requested project."
                                  "activeProjectPath",
                                  runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                                  "sessionGeneration", jint64 (Volatile.Read(&activeSessionGeneration)) ]
                            :> JsonNode
                    else
                        let parameters = jobj [ "query", jstr args.query ]
                        let! response = this.InvokeLiveUnsafe(jsonRpc, "workspace/symbol", parameters)

                        // First non-empty response is the signal that the symbol index has warmed.
                        match response with
                        | :? JsonArray as arr when arr.Count > 0 -> symbolIndexEverWarmed <- true
                        | _ -> ()

                        let shaped =
                            LspResponseShape.workspaceSymbolResponse
                                response
                                (readWorkspaceReadyAt ())
                                DateTimeOffset.UtcNow
                                LspResponseShape.symbolIndexWarmupWindow

                        match shaped with
                        | :? JsonObject as obj ->
                            obj["contextMatched"] <- jbool true
                            obj["activeProjectPath"] <-
                                runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                            obj["sessionGeneration"] <- jint64 (Volatile.Read(&activeSessionGeneration))
                        | _ -> ()

                        return shaped
            finally
                gate.Release() |> ignore
        }

    member this.WorkspaceSymbol(args: WorkspaceSymbolArgs) : Task<JsonNode> =
        this.WorkspaceSymbolForContext(runtimeProjectPath, args)

    /// Snapshot publishDiagnostics for an explicitly requested, FCS-evaluated file
    /// set. Admission to the lifecycle gate is deliberately fail-fast: a caller whose
    /// own timeout expires must not leave a snapshot continuation queued to mutate or
    /// restart the session later. Once admitted, the gate binds context, generation,
    /// and dictionary reads to one session. Empty diagnostic arrays count as received;
    /// absent publications and stale open-document versions remain explicit coverage
    /// holes.
    member this.DiagnosticsForContext
        (
            requestedProjectPath: string option,
            expectedFiles: string array,
            fileGlob: string option,
            contextFingerprint: string option
        )
        : Task<JsonNode> =
        task {
            let pathComparer =
                if OperatingSystem.IsWindows() then
                    StringComparer.OrdinalIgnoreCase
                else
                    StringComparer.Ordinal

            let filesNode (files: string array) =
                JsonArray(files |> Array.map jstr) :> JsonNode

            let distinctExpectedFiles () =
                let seen = System.Collections.Generic.HashSet<string>(pathComparer)

                expectedFiles
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.map normalizedPath
                |> Array.filter (fun file ->
                    match fileGlob with
                    | Some pattern -> LspResponseShape.fileMatchesWorkspaceGlob runtimeWorkspaceRoot pattern file
                    | None -> true)
                |> Array.filter seen.Add
                |> Array.sortWith (fun left right -> pathComparer.Compare(left, right))

            let gateBusyResponse () =
                let expected = distinctExpectedFiles ()
                let reason = "lsp_lifecycle_gate_busy"
                let message =
                    "The diagnostics snapshot was not admitted because another LSP operation is in progress. Retry the check; no snapshot work was queued."

                jobj
                    [ "status", jstr "not_ready"
                      "ready", jbool false
                      "reason", jstr reason
                      "lspState", jstr "warming"
                      "contextMatched", jbool false
                      "complete", jbool false
                      "message", jstr message
                      "requestedProjectPath",
                      requestedProjectPath |> Option.map normalizedPath |> Option.map jstr |> Option.defaultValue null
                      "activeProjectPath", runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                      "sessionGeneration", jint64 (Volatile.Read(&activeSessionGeneration))
                      "diagnosticsFileCount", jint 0
                      "expectedFileCount", jint expected.Length
                      "receivedFileCount", jint 0
                      "missingFileCount", jint expected.Length
                      "staleFileCount", jint 0
                      "expectedFiles", filesNode expected
                      "receivedFiles", filesNode [||]
                      "missingFiles", filesNode expected
                      "staleFiles", filesNode [||]
                      "result", JsonObject() :> JsonNode ]
                :> JsonNode

            let gateAcquired = gate.Wait(0)
            let mutable contextWasMatched = false

            try
                if not gateAcquired then
                    raise DiagnosticsSnapshotGateBusyException

                use _gateLease = releaseOnDispose gate
                let expected = distinctExpectedFiles ()
                let globMatchedNoFiles =
                    fileGlob.IsSome
                    && expected.Length = 0
                    && (expectedFiles |> Array.exists (String.IsNullOrWhiteSpace >> not))

                let requestedFull = requestedProjectPath |> Option.map normalizedPath
                let currentGeneration () = Volatile.Read(&activeSessionGeneration)

                if not (contextMatches requestedFull) then
                    return
                        jobj
                            [ "status", jstr "context_mismatch"
                              "lspState", jstr (LspResponseShape.lspStateString workspaceReady)
                              "contextMatched", jbool false
                              "complete", jbool false
                              "message",
                              jstr
                                  "The active fsautocomplete workspace does not contain the requested diagnostics context. Call set_project with restartLsp=true before using cached diagnostics."
                              "requestedProjectPath", requestedFull |> Option.map jstr |> Option.defaultValue null
                              "activeProjectPath", runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                              "sessionGeneration", jint64 (currentGeneration ())
                              "diagnosticsFileCount", jint 0
                              "expectedFileCount", jint expected.Length
                              "receivedFileCount", jint 0
                              "missingFileCount", jint expected.Length
                              "staleFileCount", jint 0
                              "expectedFiles", filesNode expected
                              "receivedFiles", filesNode [||]
                              "missingFiles", filesNode expected
                              "staleFiles", filesNode [||]
                              "result", JsonObject() :> JsonNode ]
                        :> JsonNode
                else
                    contextWasMatched <- true
                    // The context gate above must run first: a mismatched fast check must
                    // never start (or restart) FSAC in the wrong workspace.
                    let! _ = this.EnsureStartedUnsafe()
                    let generation = currentGeneration ()
                    let baselineCandidates =
                        System.Collections.Generic.HashSet<string>(DiagnosticIdentity.pathComparer)

                    for file in runtimeEvaluatedSourceFiles do
                        baselineCandidates.Add(DiagnosticIdentity.canonicalFileKeyFromPath file)
                        |> ignore

                    for file in expected do
                        baselineCandidates.Add(DiagnosticIdentity.canonicalFileKeyFromPath file)
                        |> ignore

                    let changedSinceGeneration =
                        baselineCandidates
                        |> Seq.filter diagnosticBaselineNeedsRefresh
                        |> Seq.toArray

                    let taintedSinceGeneration =
                        expected
                        |> Array.map DiagnosticIdentity.canonicalFileKeyFromPath
                        |> Array.filter diagnosticTaintNeedsRebaseline

                    let inputsNeedingRebaseline =
                        let seen =
                            System.Collections.Generic.HashSet<string>(DiagnosticIdentity.pathComparer)

                        Array.append changedSinceGeneration taintedSinceGeneration
                        |> Array.filter seen.Add
                        |> Array.sortWith (fun left right -> pathComparer.Compare(left, right))

                    let projectContextChanged = contextFingerprintNeedsRefresh contextFingerprint

                    if projectContextChanged || inputsNeedingRebaseline.Length > 0 then
                        // The live process predates this disk content, so no
                        // versionless publication from it can prove freshness. Rebase
                        // exactly once onto the current disk state and make this call
                        // fail closed; a retry can consume the new generation.
                        for file in expected do
                            runtimeEvaluatedSourceFiles.Add(
                                DiagnosticIdentity.canonicalFileKeyFromPath file
                            )
                            |> ignore

                        match contextFingerprint with
                        | Some fingerprint -> runtimeDiagnosticContextFingerprint <- Some fingerprint
                        | None -> ()

                        let! stopped = this.StopLspUnsafe()

                        if not stopped then
                            invalidOp "Timed out while restarting FSAC for changed diagnostic inputs."

                        let! _ = this.StartLspUnsafe()

                        return
                            jobj
                                [ "status", jstr "not_ready"
                                  "lspState", jstr "warming"
                                  "contextMatched", jbool true
                                  "complete", jbool false
                                  "message",
                                  jstr
                                      "The evaluated project/options/source/reference context or live document content changed after this FSAC generation started. The diagnostic session was restarted; retry after workspace diagnostics are published."
                                  "activeProjectPath",
                                  runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                                  "sessionGeneration", jint64 (currentGeneration ())
                                  "diagnosticsFileCount", jint 0
                                  "expectedFileCount", jint expected.Length
                                  "receivedFileCount", jint 0
                                  "missingFileCount", jint expected.Length
                                  "staleFileCount", jint inputsNeedingRebaseline.Length
                                  "expectedFiles", filesNode expected
                                  "receivedFiles", filesNode [||]
                                  "missingFiles", filesNode expected
                                  "staleFiles", filesNode inputsNeedingRebaseline
                                  "result", JsonObject() :> JsonNode ]
                            :> JsonNode
                    elif not workspaceReady then
                        return
                            jobj
                                [ "status", jstr "not_ready"
                                  "lspState", jstr "warming"
                                  "contextMatched", jbool true
                                  "complete", jbool false
                                  "message", jstr "fsautocomplete is still loading the requested diagnostics context."
                                  "activeProjectPath",
                                  runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                                  "sessionGeneration", jint64 generation
                                  "diagnosticsFileCount", jint 0
                                  "expectedFileCount", jint expected.Length
                                  "receivedFileCount", jint 0
                                  "missingFileCount", jint expected.Length
                                  "staleFileCount", jint 0
                                  "expectedFiles", filesNode expected
                                  "receivedFiles", filesNode [||]
                                  "missingFiles", filesNode expected
                                  "staleFiles", filesNode [||]
                                  "result", JsonObject() :> JsonNode ]
                            :> JsonNode
                    else
                        let result = JsonObject()
                        let analyzedAtByUri = JsonObject()
                        let received = ResizeArray<string>()
                        let missing = ResizeArray<string>()
                        let stale = ResizeArray<string>()
                        let currentAnalyzedAt = ResizeArray<DateTimeOffset>()

                        for file in expected do
                            let uri = toFileUri file
                            let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath file

                            match diagnostics.TryGetValue(fileKey) with
                            | false, _ -> missing.Add(file)
                            | true, envelope ->
                                received.Add(file)

                                match diagnosticFreshness fileKey envelope with
                                | Current ->
                                    result[uri] <- envelope.Payload.DeepClone()

                                    analyzedAtByUri[uri] <-
                                        jstr (envelope.ReceivedAt.ToUniversalTime().ToString("O"))

                                    currentAnalyzedAt.Add(envelope.ReceivedAt)
                                | DiskContentChanged
                                | Stale -> stale.Add(file)

                        let receivedFiles = received.ToArray()
                        let missingFiles = missing.ToArray()
                        let staleFiles = stale.ToArray()
                        let complete =
                            not globMatchedNoFiles && missingFiles.Length = 0 && staleFiles.Length = 0

                        let mostRecent =
                            if currentAnalyzedAt.Count = 0 then
                                None
                            else
                                currentAnalyzedAt |> Seq.max |> Some

                        return
                            jobj
                                [ "status", jstr "ok"
                                  "lspState", jstr "ready"
                                  "contextMatched", jbool true
                                  "complete", jbool complete
                                  "message",
                                  (if globMatchedNoFiles then
                                       jstr "fileGlob matched no evaluated source files in the requested workspace."
                                   else
                                       null)
                                  "activeProjectPath",
                                  runtimeProjectPath |> Option.map jstr |> Option.defaultValue null
                                  "sessionGeneration", jint64 generation
                                  "diagnosticsFileCount", jint receivedFiles.Length
                                  "expectedFileCount", jint expected.Length
                                  "receivedFileCount", jint receivedFiles.Length
                                  "missingFileCount", jint missingFiles.Length
                                  "staleFileCount", jint staleFiles.Length
                                  "expectedFiles", filesNode expected
                                  "receivedFiles", filesNode receivedFiles
                                  "missingFiles", filesNode missingFiles
                                  "staleFiles", filesNode staleFiles
                                  "mostRecentAnalyzedAt", LspResponseShape.timestampJson mostRecent
                                  "analyzedAtByUri", analyzedAtByUri :> JsonNode
                                  "result", result :> JsonNode ]
                            :> JsonNode
            with
            | DiagnosticsSnapshotGateBusyException -> return gateBusyResponse ()
            | ex ->
                let expected =
                    try
                        distinctExpectedFiles ()
                    with _ ->
                        [||]

                return
                    jobj
                        [ "status", jstr "infrastructure_error"
                          "lspState", jstr (LspResponseShape.lspStateString workspaceReady)
                          "contextMatched", jbool contextWasMatched
                          "complete", jbool false
                          "message", jstr ex.Message
                          "sessionGeneration", jint64 (Volatile.Read(&activeSessionGeneration))
                          "diagnosticsFileCount", jint 0
                          "expectedFileCount", jint expected.Length
                          "receivedFileCount", jint 0
                          "missingFileCount", jint expected.Length
                          "staleFileCount", jint 0
                          "expectedFiles", filesNode expected
                          "receivedFiles", filesNode [||]
                          "missingFiles", filesNode expected
                          "staleFiles", filesNode [||]
                          "result", JsonObject() :> JsonNode ]
                    :> JsonNode
        }

    member _.Diagnostics(args: DiagnosticsArgs) : Task<JsonNode> =
        task {
            let severityFilter = args.severity |> Option.bind LspResponseShape.severityCodeOf

            let applySeverity (payload: JsonNode) =
                match severityFilter with
                | Some code -> LspResponseShape.filterDiagnosticsBySeverity code payload
                | None -> payload

            let resolveByFileKey (fileKey: string) =
                match diagnostics.TryGetValue(fileKey) with
                | true, envelope -> envelope.Payload.DeepClone()
                | false, _ -> JsonArray() :> JsonNode

            let analyzedAtForFileKey (fileKey: string) =
                match diagnostics.TryGetValue(fileKey) with
                | true, envelope -> Some envelope.ReceivedAt
                | false, _ -> None

            match args.path with
            | Some path ->
                let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath path
                let payload = resolveByFileKey fileKey |> applySeverity
                let analyzedAt = analyzedAtForFileKey fileKey

                return
                    LspResponseShape.diagnosticsResponseForFile
                        workspaceReady
                        diagnostics.Count
                        payload
                        analyzedAt
            | None ->
                let root = JsonObject()
                let analyzedAtByUri = JsonObject()

                let globMatches (fileKey: string) =
                    match args.fileGlob with
                    | Some pattern ->
                        LspResponseShape.fileMatchesWorkspaceGlob runtimeWorkspaceRoot pattern fileKey
                    | None -> true

                for KeyValue(fileKey, envelope) in diagnostics do
                    if globMatches fileKey then
                        let uri = toFileUri fileKey
                        let filtered = envelope.Payload.DeepClone() |> applySeverity

                        // Skip empty-after-filter entries — keeps the response tight when
                        // the user asked for errors-only and a file only has warnings.
                        match severityFilter, filtered with
                        | Some _, (:? JsonArray as arr) when arr.Count = 0 -> ()
                        | _ ->
                            root[uri] <- filtered
                            analyzedAtByUri[uri] <-
                                jstr (envelope.ReceivedAt.ToUniversalTime().ToString("O"))

                let mostRecent =
                    let filtered =
                        diagnostics
                        |> Seq.filter (fun kv -> globMatches kv.Key)
                        |> Seq.map (fun kv -> kv.Value.ReceivedAt)
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

    member private this.WithFileContextGate(path: string, operation: unit -> Task<JsonNode>) : Task<JsonNode> =
        task {
            do! gate.WaitAsync()
            use _gateLease = releaseOnDispose gate

            if not (fileContextMatches path) then
                return this.FileContextMismatchResponse(path)
            else
                return! operation ()
        }

    member this.Formatting(args: FormattingArgs) : Task<JsonNode> =
        this.WithFileContextGate(args.path, fun () ->
          task {
            let! jsonRpc = this.EnsureStartedUnsafe()

            if not workspaceReady then
                return this.NotReadyResponse()
            else

                let fullPath = Path.GetFullPath(args.path)

                let originalText =
                    args.text |> Option.defaultWith (fun () -> File.ReadAllText(fullPath))

                let! uri = this.SyncDocument(jsonRpc, args.path, args.text)

                let formatParams =
                    jobj
                        [ "textDocument", jobj [ "uri", jstr uri ]
                          "options", jobj [ "tabSize", jint 4; "insertSpaces", jbool true ] ]

                let! editsToken =
                    this.InvokeLiveUnsafe(jsonRpc, "textDocument/formatting", formatParams)

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
                    this.WithContextMetadata(
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
                    )
          })

    member this.CodeAction(args: CodeActionArgs) : Task<JsonNode> =
        this.WithDocument(
            args.path,
            args.text,
            "textDocument/codeAction",
            fun uri -> CodeActionRequest.buildParams uri args.line args.character
        )

    /// Agent-friendly wrapper over the raw codeAction proxy: fetches the file's
    /// diagnostics (FSAC publishDiagnostics), then for EACH diagnostic re-requests
    /// codeActions with that diagnostic populated in context.diagnostics — the
    /// context the raw `CodeAction` proxy leaves empty — and groups the fixes per
    /// diagnostic. line/character narrow to one position; omit for the whole file.
    member this.DiagnosticFixes(args: DiagnosticFixesArgs) : Task<JsonNode> =
        this.WithFileContextGate(args.path, fun () ->
          task {
            let! initialRpc = this.EnsureStartedUnsafe()
            let fullPath = Path.GetFullPath(args.path)
            let uri = toFileUri fullPath
            let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath fullPath

            let! jsonRpc =
                if args.text.IsNone && diagnosticBaselineNeedsRefresh fileKey then
                    task {
                        runtimeEvaluatedSourceFiles.Add(fileKey) |> ignore
                        runtimeDiagnosticContextFingerprint <- None
                        let! stopped = this.StopLspUnsafe()

                        if not stopped then
                            invalidOp "Timed out while restarting FSAC for changed diagnostic inputs."

                        return! this.StartLspUnsafe()
                    }
                else
                    Task.FromResult(initialRpc)

            if not workspaceReady then
                return this.NotReadyResponse()
            else
                // Sync while the lifecycle gate is held so FSAC cannot restart mid-request.
                let! _ = this.SyncDocument(jsonRpc, args.path, args.text)

                let expectedVersion =
                    match documents.TryGetValue(fileKey) with
                    | true, state -> state.Version
                    | false, _ -> 0

                // Bounded wait for diagnostics proven current either by an exact
                // server document version or by equality with the generation-start
                // disk baseline. Receipt time alone is never freshness evidence.
                let deadline = DateTimeOffset.UtcNow.AddSeconds(5.0)

                let isFresh () =
                    match diagnostics.TryGetValue(fileKey) with
                    | true, envelope -> diagnosticFreshness fileKey envelope = Current
                    | false, _ -> false

                while not (isFresh ()) && DateTimeOffset.UtcNow < deadline do
                    do! Task.Delay(50)

                let staleResponse =
                    if isFresh () then
                        None
                    else
                        Some(
                            jobj
                                [ "status", jstr "unknown"
                                  "errorKind", jstr "diagnostics_stale"
                                  "message",
                                  jstr
                                      $"FSAC did not publish diagnostics for document version {expectedVersion} within the freshness deadline. Stale diagnostics were not used."
                                  "file", jstr fullPath
                                  "expectedDocumentVersion", jint expectedVersion
                                  "publishedDocumentVersion",
                                  (match diagnostics.TryGetValue(fileKey) with
                                   | true, envelope ->
                                       envelope.ServerVersion |> Option.map jint |> Option.defaultValue null
                                   | false, _ -> null) ]
                            :> JsonNode
                        )

                let fileDiagnostics =
                    match staleResponse with
                    | Some _ -> []
                    | None ->
                        match diagnostics.TryGetValue(fileKey) with
                        | true, envelope ->
                            match envelope.Payload with
                            | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toList
                            | _ -> []
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
                        this.InvokeLiveUnsafe(jsonRpc, "textDocument/codeAction", parameters)

                    let fixes =
                        match response with
                        | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toList
                        | _ -> []

                    entries.Add(diag, fixes)

                match staleResponse with
                | Some response -> return this.WithContextMetadata(response)
                | None ->
                    return
                        this.WithContextMetadata(
                            LspResponseShape.buildDiagnosticFixesResponse fullPath (List.ofSeq entries)
                        )
          })

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

    /// Non-destructive preview of a rename. Runs the same FSAC rename machinery as
    /// `Rename` (which only computes the WorkspaceEdit — it never writes), then
    /// transforms the raw WorkspaceEdit into a grouped, line-annotated preview.
    member this.RenamePreview(args: RenamePreviewArgs) : Task<JsonNode> =
        task {
            let originatingUri = toFileUri args.path

            let uriToPath (uri: string) =
                try
                    Uri(uri).LocalPath
                with _ ->
                    uri

            let tryReadLines (uri: string) =
                try
                    let path = uriToPath uri

                    if File.Exists path then
                        Some(RenamePreviewShape.splitLines (File.ReadAllText path))
                    else
                        None
                with _ ->
                    None

            // Originating file: honour the unsaved buffer when provided so the
            // preview reflects what FSAC actually saw. Other files: read from disk.
            let lookupLines (uri: string) =
                if String.Equals(uri, originatingUri, StringComparison.OrdinalIgnoreCase) then
                    match args.text with
                    | Some t -> Some(RenamePreviewShape.splitLines t)
                    | None -> tryReadLines uri
                else
                    tryReadLines uri

            // Nearest ancestor directory containing a .fsproj owns the file.
            let owningProjectFile (filePath: string) : string option =
                let rec walk (dir: string) =
                    if String.IsNullOrEmpty dir then
                        None
                    else
                        let fsprojs =
                            try
                                Directory.GetFiles(dir, "*.fsproj", SearchOption.TopDirectoryOnly)
                            with _ ->
                                [||]

                        if fsprojs.Length > 0 then
                            Some(Array.min fsprojs)
                        else
                            let parent = Path.GetDirectoryName dir

                            if String.IsNullOrEmpty parent || parent = dir then
                                None
                            else
                                walk parent

                try
                    walk (Path.GetDirectoryName(Path.GetFullPath filePath))
                with _ ->
                    None

            let context: RenamePreviewShape.Context =
                { NewName = args.newName
                  OriginatingUri = originatingUri
                  Line = args.line
                  Character = args.character
                  LookupLines = lookupLines
                  ResolveProject = uriToPath >> owningProjectFile
                  UriToDisplay = uriToPath }

            try
                let! raw =
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

                // WithDocument wraps the WorkspaceEdit as { status: "ok"; result }, or
                // returns a not-ready envelope. Only transform the success case; pass
                // everything else (e.g. not_ready) straight through.
                match raw with
                | :? JsonObject as obj when
                    (match obj["status"] with
                     | null -> false
                     | s ->
                         try
                             s.GetValue<string>() = "ok"
                         with _ ->
                             false)
                    ->
                    return
                        this.CopyContextMetadata(
                            obj,
                            RenamePreviewShape.build context obj["result"]
                        )
                | _ -> return raw
            with ex ->
                let errorKind, retryable = classifyRenameInfrastructureError ex

                // Only a valid null/empty WorkspaceEdit means no_symbol. Transport,
                // startup, remote, and protocol failures remain explicit.
                return
                    this.WithContextMetadata(
                        jobj
                            [ "status", jstr "infrastructure_error"
                              "errorKind", jstr errorKind
                              "retryable", jbool retryable
                              "message", jstr ex.Message ]
                        :> JsonNode
                    )
        }

    interface IDisposable with
        member this.Dispose() =
            this.StopLspUnsafe().GetAwaiter().GetResult() |> ignore
            projectSwitchGate.Dispose()
            gate.Dispose()
