module FsLangMcp.Dispatcher

open System.Text.Json.Nodes
open System.Threading.Tasks
open FsLangMcp.Types
open FsLangMcp.LspBridge
open FsLangMcp.FcsBridge

// ─── find/check dispatcher seam (issue #128, #136) ──────────────────────────────
//
// The consolidated `find` / `check` tools in Program.fs route their backend calls
// through this module instead of touching FcsBridge/LspBridge directly. Each
// request DU case names the consolidated entry point; `run` maps it to the exact
// backend call. The legacy single-tool "cluster" alias cases were removed in #136
// once `find` / `check` superseded them; only `Find` / `Check` remain.
//
// Wrapping concerns (concurrency gating via runLimited, projectPath fall-back to
// the active set_project) stay in the Program.fs arg-adapters — they are not part
// of member→backend routing and must remain exactly where they were to keep
// externally-observable output unchanged.

/// Internal request representation for the "find" tool cluster.
/// find-cluster spans both FCS (fcsBridge) and FSAC (lspBridge); the dispatcher
/// hides which backend each member resolves to.
type FindRequest =
    /// Consolidated multi-project symbol search (issue #128, Stage 1). Routes to the
    /// FCS union sweep; after an empty FCS result the project-bound FSAC symbol index
    /// can still prove presence. It is injected here so the substrate stays LSP-agnostic.
    | Find of FindArgs

/// Internal request representation for the "check" tool cluster.
/// check-cluster spans both FCS (fcsBridge) and FSAC (lspBridge); the dispatcher
/// hides which backend each member resolves to.
type CheckRequest =
    /// Consolidated one-verdict check (issue #128, Stage 1). Routes to the fresh FCS
    /// re-check substrate; the FSAC cached snapshot is projected in here as the
    /// speed="fast"-only signal, keeping the FCS substrate LSP-agnostic.
    | Check of CheckArgs

[<RequireQualifiedAccess>]
module FindDispatch =

    let private normalizeContextPath (path: string) =
        let full = System.IO.Path.GetFullPath(path)
        let root = System.IO.Path.GetPathRoot(full)

        if System.String.Equals(full, root, System.StringComparison.Ordinal) then
            full
        else
            full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)

    let private contextPathsEqual (left: string) (right: string) =
        let comparison =
            if System.OperatingSystem.IsWindows() then
                System.StringComparison.OrdinalIgnoreCase
            else
                System.StringComparison.Ordinal

        try
            System.String.Equals(normalizeContextPath left, normalizeContextPath right, comparison)
        with _ ->
            false

    /// `workspace/symbol` is workspace-wide, so it may only prove a positive result
    /// when that workspace is exactly the scope requested by `find`. In particular,
    /// an active solution must never satisfy scope=file or a member-project request.
    let internal fsacWorkspaceProbeEligibility
        (args: FindArgs)
        (activeProjectPath: string option)
        : Result<unit, string> =
        let scope =
            args.scope
            |> Option.defaultValue "auto"
            |> fun value -> value.Trim().ToLowerInvariant()

        if scope = "file" then
            Error "FSAC workspace symbols are broader than find scope=file."
        else
            match args.projectPath |> Option.filter (System.String.IsNullOrWhiteSpace >> not), activeProjectPath with
            | None, _ -> Error "The requested find context was not explicit."
            | _, None -> Error "FSAC has no active project context."
            | Some requested, Some active when not (contextPathsEqual requested active) ->
                Error "The active FSAC workspace is broader than or different from the requested find context."
            | Some requested, Some _
                when scope = "project"
                     && not (requested.EndsWith(".fsproj", System.StringComparison.OrdinalIgnoreCase)) ->
                Error "FSAC workspace symbols cannot prove a project-scoped result inside an active solution."
            | Some _, Some _ -> Ok()

    let private tryString (node: JsonNode) (key: string) =
        match node with
        | :? JsonObject as obj ->
            match obj[key] with
            | :? JsonValue as value ->
                try
                    Some(value.GetValue<string>())
                with _ ->
                    None
            | _ -> None
        | _ -> None

    let private tryBool (node: JsonNode) (key: string) =
        match node with
        | :? JsonObject as obj ->
            match obj[key] with
            | :? JsonValue as value ->
                let mutable result = false
                if value.TryGetValue(&result) then Some result else None
            | _ -> None
        | _ -> None

    /// Convert the project-bound LSP response into a typed probe result. Only an
    /// explicit status=ok response for the requested context may be Available;
    /// every other state remains distinguishable from a genuine zero-hit index.
    let internal classifyFsacProbeResponse (response: JsonNode) : FindFsacProbeResult =
        let message fallback = tryString response "message" |> Option.defaultValue fallback

        match tryString response "status" with
        | Some "ok" ->
            match tryBool response "contextMatched" with
            | None ->
                FindFsacProbeResult.Failed
                    "FSAC workspace-symbol response omitted contextMatched."
            | Some false ->
                FindFsacProbeResult.ContextMismatch(message "FSAC response belongs to another project context.")
            | Some true ->
                match tryBool response "symbolIndexReady" with
                | Some false -> FindFsacProbeResult.NotReady(message "FSAC symbol index is still warming.")
                | _ ->
                    match response["result"] with
                    | :? JsonArray as arr -> FindFsacProbeResult.Available arr.Count
                    | _ ->
                        FindFsacProbeResult.Failed
                            "FSAC workspace-symbol response did not contain an array result."
        | Some "context_mismatch" ->
            FindFsacProbeResult.ContextMismatch(message "FSAC active project does not match the requested project.")
        | Some "not_ready" -> FindFsacProbeResult.NotReady(message "FSAC is not ready.")
        | Some "infrastructure_error" -> FindFsacProbeResult.Failed(message "FSAC infrastructure failure.")
        | Some status -> FindFsacProbeResult.Failed(message $"Unexpected FSAC status '{status}'.")
        | None -> FindFsacProbeResult.Failed "FSAC workspace-symbol response omitted status."

    /// Route a find-cluster request to the same backend call its handler made
    /// before the dispatcher seam was introduced.
    let internal run (fcsBridge: FcsBridge) (lspBridge: FsAutoCompleteBridge) (request: FindRequest) : Task<JsonNode> =
        match request with
        | Find args ->
            // FSAC symbol-index probe: consulted only when the FCS sweep has no
            // hits. Keep every non-success state typed; failure or project mismatch
            // is not equivalent to a valid zero-hit result.
            let fsacProbe (q: string) : Task<FindFsacProbeResult> =
                task {
                    match fsacWorkspaceProbeEligibility args lspBridge.CurrentProjectPath with
                    | Error reason -> return FindFsacProbeResult.Unavailable reason
                    | Ok() ->
                        try
                            let! response =
                                lspBridge.WorkspaceSymbolForContext(args.projectPath, { query = q })

                            return classifyFsacProbeResponse response
                        with ex ->
                            return FindFsacProbeResult.Failed ex.Message
                }

            fcsBridge.Find(args, fsacProbe = fsacProbe)

[<RequireQualifiedAccess>]
module CheckDispatch =

    /// Route a check-cluster request to the same backend call its handler made
    /// before the dispatcher seam was introduced.
    let internal run (fcsBridge: FcsBridge) (lspBridge: FsAutoCompleteBridge) (request: CheckRequest) : Task<JsonNode> =
        match request with
        | Check args ->
            // speed="fast" reads only the context-bound FSAC publications for the
            // evaluated FCS SourceFiles derived by Check. Missing/stale/mismatched
            // coverage stays typed and can never become a clean verdict.
            let fsacSnapshot (expectation: CheckFsacExpectation) : Task<CheckFsacSnapshot> =
                task {
                    try
                        let! response =
                            lspBridge.DiagnosticsForContext(
                                expectation.RequestedProjectPath,
                                expectation.ExpectedFiles,
                                expectation.FileGlob,
                                expectation.ContextFingerprint
                            )

                        return CheckFsacSnapshot.ofDiagnosticsResponse response
                    with
                    | SdkPreflight.SdkPinUnsatisfiable failure ->
                        return
                            CheckFsacSnapshot.unavailableWithBlockingReason
                                expectation
                                failure.Message
                                (SdkPreflight.toBlockingReason failure.Pin failure.InstalledSdks)
                    | :? System.TimeoutException as timedOut ->
                        return
                            CheckFsacSnapshot.unavailableWithTypedFailure
                                expectation
                                timedOut.Message
                                "timeout"
                                true
                    | :? System.OperationCanceledException as cancelled ->
                        return
                            CheckFsacSnapshot.unavailableWithTypedFailure
                                expectation
                                cancelled.Message
                                "cancelled"
                                true
                    | ex ->
                        return
                            CheckFsacSnapshot.unavailableWithTypedFailure
                                expectation
                                ex.Message
                                "fsac_unavailable"
                                true
                }

            fcsBridge.Check(args, fsacSnapshot = fsacSnapshot)
