module FsLangMcp.Dispatcher

open System.Text.Json.Nodes
open System.Threading.Tasks
open FsLangMcp.Types
open FsLangMcp.LspBridge
open FsLangMcp.FcsBridge

// ─── find/check dispatcher seam (issue #128, Stage 0) ───────────────────────────
//
// The 12 "cluster" tool handlers in Program.fs no longer call FcsBridge/LspBridge
// directly — they route through this module. Each request DU case names one
// cluster member; `run` maps it to the exact backend call the per-tool handler
// made before. This is the internal seam Stage 1 mounts the consolidated
// `find` / `check` tools on. Stage 0 is byte-identical: it only adds indirection.
//
// Wrapping concerns (concurrency gating via runLimited, projectPath fall-back to
// the active set_project) stay in the Program.fs arg-adapters — they are not part
// of member→backend routing and must remain exactly where they were to keep
// externally-observable output unchanged.

/// Internal request representation for the "find" tool cluster.
/// find-cluster spans both FCS (fcsBridge) and FSAC (lspBridge); the dispatcher
/// hides which backend each member resolves to.
type FindRequest =
    | FindSymbol of FcsFindSymbolArgs
    | RecordFieldAudit of FcsRecordFieldAuditArgs
    | FindMemberUsages of FcsFindMemberUsagesArgs
    | WorkspaceSymbol of WorkspaceSymbolArgs
    | ProjectSymbolUses of FcsProjectSymbolUsesArgs
    | References of ReferencesArgs
    | Definition of PositionArgs

/// Internal request representation for the "check" tool cluster.
/// check-cluster spans both FCS (fcsBridge) and FSAC (lspBridge); the dispatcher
/// hides which backend each member resolves to.
type CheckRequest =
    | Diagnostics of DiagnosticsArgs
    | Compile of FSharpCompileArgs
    | CheckFile of FcsParseAndCheckArgs
    | ParseAndCheckFile of FcsParseAndCheckArgs
    | ValidateSnippet of FcsValidateSnippetArgs

[<RequireQualifiedAccess>]
module FindDispatch =

    /// Route a find-cluster request to the same backend call its handler made
    /// before the dispatcher seam was introduced.
    let internal run (fcsBridge: FcsBridge) (lspBridge: FsAutoCompleteBridge) (request: FindRequest) : Task<JsonNode> =
        match request with
        | FindSymbol args -> fcsBridge.FindSymbol args
        | RecordFieldAudit args -> fcsBridge.RecordFieldAudit args
        | FindMemberUsages args -> fcsBridge.FindMemberUsages args
        | WorkspaceSymbol args -> lspBridge.WorkspaceSymbol args
        | ProjectSymbolUses args -> fcsBridge.ProjectSymbolUses args
        | References args -> lspBridge.References args
        | Definition args -> lspBridge.Definition args

[<RequireQualifiedAccess>]
module CheckDispatch =

    /// Route a check-cluster request to the same backend call its handler made
    /// before the dispatcher seam was introduced.
    let internal run (fcsBridge: FcsBridge) (lspBridge: FsAutoCompleteBridge) (request: CheckRequest) : Task<JsonNode> =
        match request with
        | Diagnostics args -> lspBridge.Diagnostics args
        | Compile args -> fcsBridge.CompileProject args
        | CheckFile args -> fcsBridge.CheckFile args
        | ParseAndCheckFile args -> fcsBridge.ParseAndCheckFile args
        | ValidateSnippet args -> fcsBridge.ValidateSnippet args
