module FsLangMcp.Tools

open System
open System.Threading.Tasks
open FsLangMcp.Types
open FsMcp.Core
open FsMcp.Server
open Newtonsoft.Json
open Newtonsoft.Json.Linq

// ─── MCP helpers ───────────────────────────────────────────────────────────────

let toolErrorToJson (err: ToolError) : string =
    match err with
    | InvalidArgs msg  -> sprintf """{"errorKind":"InvalidArgs","message":%s}"""   (JsonConvert.SerializeObject msg)
    | NotReady msg     -> sprintf """{"errorKind":"NotReady","message":%s}"""      (JsonConvert.SerializeObject msg)
    | InfraFailure ex  -> sprintf """{"errorKind":"InfraFailure","message":%s}"""  (JsonConvert.SerializeObject ex.Message)
    | FcsAborted msg   -> sprintf """{"errorKind":"FcsAborted","message":%s}"""    (JsonConvert.SerializeObject msg)
    | FileNotFound msg -> sprintf """{"errorKind":"FileNotFound","message":%s}"""  (JsonConvert.SerializeObject msg)

let renderToken (token: JToken) =
    token.ToString(Formatting.Indented)

let toolResult (work: Task<JToken>) : Task<Result<Content list, McpError>> =
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
