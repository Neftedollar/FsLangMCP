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

let private renderOpts =
    JsonSerializerOptions(
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true)

let toolErrorToJson (err: ToolError) : string =
    match err with
    | InvalidArgs msg  -> sprintf """{"errorKind":"InvalidArgs","message":%s}"""   (JsonSerializer.Serialize(msg, serializeOpts))
    | NotReady msg     -> sprintf """{"errorKind":"NotReady","message":%s}"""      (JsonSerializer.Serialize(msg, serializeOpts))
    | InfraFailure ex  -> sprintf """{"errorKind":"InfraFailure","message":%s}"""  (JsonSerializer.Serialize(ex.Message, serializeOpts))
    | FcsAborted msg   -> sprintf """{"errorKind":"FcsAborted","message":%s}"""    (JsonSerializer.Serialize(msg, serializeOpts))
    | FileNotFound msg -> sprintf """{"errorKind":"FileNotFound","message":%s}"""  (JsonSerializer.Serialize(msg, serializeOpts))

let renderToken (token: JsonNode) =
    JsonSerializer.Serialize(token, renderOpts)

let toolResult (work: Task<JsonNode>) : Task<Result<Content list, McpError>> =
    task {
        try
            let! payload = work
            return Ok [ Content.text (renderToken payload) ]
        with
        | :? OperationCanceledException as ex ->
            // TaskCanceledException is a subclass of OperationCanceledException — both caught here
            let err = FcsAborted ex.Message
            return Error(McpError.TransportError (toolErrorToJson err))
        | :? ArgumentException as ex ->
            let err = InvalidArgs ex.Message
            return Error(McpError.TransportError (toolErrorToJson err))
        // Guard for external process exceptions that surface "not ready" text.
        // Note: current not-ready paths return via NotReadyResponse() (no exception),
        // so this arm is defensive — it would fire if a future external dep raises.
        | ex when ex.Message.IndexOf("not ready", StringComparison.OrdinalIgnoreCase) >= 0
               || ex.Message.IndexOf("NotReady", StringComparison.Ordinal) >= 0 ->
            let err = NotReady ex.Message
            return Error(McpError.TransportError (toolErrorToJson err))
        | :? System.IO.FileNotFoundException as ex ->
            let err = FileNotFound ex.Message
            return Error(McpError.TransportError (toolErrorToJson err))
        | ex ->
            let err = InfraFailure ex
            return Error(McpError.TransportError (toolErrorToJson err))
    }
