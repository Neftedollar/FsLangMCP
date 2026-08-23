module FsLangMcp.Tools

open System
open System.Threading.Tasks
open FsLangMcp.Types
open FsMcp.Core
open FsMcp.Server
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

// ─── MCP helpers ───────────────────────────────────────────────────────────────

let private serializeOpts =
    JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let toolErrorToJson (err: ToolError) : string =
    match err with
    | InvalidArgs msg ->
        sprintf """{"errorKind":"InvalidArgs","message":%s}""" (JsonSerializer.Serialize(msg, serializeOpts))
    | NotReady msg -> sprintf """{"errorKind":"NotReady","message":%s}""" (JsonSerializer.Serialize(msg, serializeOpts))
    | InfraFailure ex ->
        sprintf """{"errorKind":"InfraFailure","message":%s}""" (JsonSerializer.Serialize(ex.Message, serializeOpts))
    | FcsAborted msg ->
        sprintf """{"errorKind":"FcsAborted","message":%s}""" (JsonSerializer.Serialize(msg, serializeOpts))
    | FileNotFound msg ->
        sprintf """{"errorKind":"FileNotFound","message":%s}""" (JsonSerializer.Serialize(msg, serializeOpts))

// #206: renders through the shared `Types.mcpRenderOptions` — the same options
// `FcsBridge.fs`'s response-size budget checks measure against (`Types.renderedLength`
// / `Types.isOverRenderedBudget`), so a budget-checked response and the response that
// actually ships can never silently use different serializations.
let renderToken (token: JsonNode) =
    JsonSerializer.Serialize(token, mcpRenderOptions)

let internal toolResultWithHealthCheck
    (healthCheck: unit -> unit)
    (work: unit -> Task<JsonNode>)
    : Task<Result<Content list, McpError>> =
    task {
        try
            // A dotnet-tool update can remove this running process's versioned
            // installation directory without stopping the process. Detect that
            // before a lazy dependency load turns it into a misleading missing-DLL
            // transport error. The project-load path repeats the guard so work that
            // started synchronously while this wrapper was being constructed cannot
            // enter Ionide/MSBuild either.
            healthCheck ()
            let! payload = work ()
            return Ok [ Content.text (renderToken payload) ]
        with
        | InstallationHealth.StaleToolProcessException failure ->
            return Ok [ Content.text (renderToken (InstallationHealth.envelope failure)) ]
        // #192: an unsatisfiable global.json pin is machine configuration, not a tool
        // fault. Rendering it here — ahead of every other arm — is what makes every
        // consumer of EnsureProjectResults report the same typed sdk_not_found
        // envelope instead of a generic "Unable to load F# project options".
        | SdkPreflight.SdkPinUnsatisfiable failure -> return Ok [ Content.text (renderToken failure.Envelope) ]
        | :? OperationCanceledException as ex ->
            // TaskCanceledException is a subclass of OperationCanceledException — both caught here
            let err = FcsAborted ex.Message
            return Error(McpError.TransportError(toolErrorToJson err))
        | :? ArgumentException as ex ->
            let err = InvalidArgs ex.Message
            return Error(McpError.TransportError(toolErrorToJson err))
        // Guard for external process exceptions that surface "not ready" text.
        // Note: current not-ready paths return via NotReadyResponse() (no exception),
        // so this arm is defensive — it would fire if a future external dep raises.
        | ex when
            ex.Message.IndexOf("not ready", StringComparison.OrdinalIgnoreCase) >= 0
            || ex.Message.IndexOf("NotReady", StringComparison.Ordinal) >= 0
            ->
            let err = NotReady ex.Message
            return Error(McpError.TransportError(toolErrorToJson err))
        | :? System.IO.FileNotFoundException as ex ->
            let err = FileNotFound ex.Message
            return Error(McpError.TransportError(toolErrorToJson err))
        | ex ->
            let err = InfraFailure ex
            return Error(McpError.TransportError(toolErrorToJson err))
    }

/// Accept a thunk, not an already-created Task: F# task expressions are hot and
/// can execute synchronously during argument evaluation. The installation guard
/// must run before the handler starts, including before any future lazy loader a
/// new tool might introduce.
let toolResult (work: unit -> Task<JsonNode>) : Task<Result<Content list, McpError>> =
    toolResultWithHealthCheck InstallationHealth.ensureCurrent work
