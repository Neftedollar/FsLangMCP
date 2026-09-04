module FsLangMcp.McpHost

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open Microsoft.Extensions.AI
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server

/// FsMcp.Server 1.2.2 builds a validated ServerConfig but its stdio transport
/// does not forward Config.Name/Version to McpServerOptions.ServerInfo. Keep the
/// FsMcp authoring surface and bridge its current tool-only configuration to the
/// official SDK until the dependency exposes a corrected transport hook.

let private toSdkContentBlock (content: Content) : ContentBlock =
    match content with
    | Text text -> TextContentBlock(Text = text) :> ContentBlock
    | Image(data, mimeType) ->
        ImageContentBlock(MimeType = MimeType.value mimeType, Data = ReadOnlyMemory(data))
        :> ContentBlock
    | EmbeddedResource resource ->
        let sdkResource =
            match resource with
            | TextResource(uri, mimeType, text) ->
                TextResourceContents(
                    Uri = ResourceUri.value uri,
                    MimeType = MimeType.value mimeType,
                    Text = text
                )
                :> ResourceContents
            | BlobResource(uri, mimeType, data) ->
                BlobResourceContents(
                    Uri = ResourceUri.value uri,
                    MimeType = MimeType.value mimeType,
                    Blob = ReadOnlyMemory(data)
                )
                :> ResourceContents

        EmbeddedResourceBlock(Resource = sdkResource) :> ContentBlock

let internal mcpErrorText (error: McpError) =
    match error with
    | McpError.TransportError payload -> payload
    | other -> $"%A{other}"

type private ToolAIFunction(definition: ToolDefinition) =
    inherit AIFunction()

    let schema =
        match definition.InputSchema with
        | Some value -> value
        | None ->
            use document = JsonDocument.Parse("""{"type":"object","properties":{}}""")
            document.RootElement.Clone()

    override _.Name = ToolName.value definition.Name
    override _.Description = definition.Description
    override _.JsonSchema = schema

    override _.InvokeCoreAsync(arguments, cancellationToken) =
        ValueTask<obj>(task {
            let values =
                if isNull arguments then
                    Map.empty
                else
                    arguments
                    |> Seq.choose (fun pair ->
                        match pair.Value with
                        | :? JsonElement as value -> Some(pair.Key, value.Clone())
                        | value when not (isNull value) ->
                            Some(pair.Key, JsonSerializer.SerializeToElement(value))
                        | _ -> None)
                    |> Map.ofSeq

            try
                // FsMcp 2.0 handlers are cancellation-aware; forward the SDK's token
                // so client-side request cancellation reaches the tool body.
                let! result = definition.Handler values cancellationToken

                match result with
                | Ok contents ->
                    return
                        CallToolResult(Content = (contents |> List.map toSdkContentBlock |> List.toArray))
                        :> obj
                | Error error ->
                    return
                        CallToolResult(
                            Content = [| TextContentBlock(Text = mcpErrorText error) |],
                            IsError = true
                        )
                        :> obj
            with ex ->
                return
                    CallToolResult(
                        Content = [| TextContentBlock(Text = ex.Message) |],
                        IsError = true
                    )
                    :> obj
        })

let private createSdkTool (definition: ToolDefinition) =
    let functionDefinition = ToolAIFunction(definition)

    McpServerTool.Create(
        functionDefinition,
        McpServerToolCreateOptions(
            Name = ToolName.value definition.Name,
            Description = definition.Description
        )
    )

let runToolOnly (serverInfoVersion: string) (config: ServerConfig) : Task<unit> =
    task {
        if String.IsNullOrWhiteSpace(serverInfoVersion) then
            invalidArg (nameof serverInfoVersion) "MCP serverInfo.version must be the exact product version."

        if not (List.isEmpty config.Resources) || not (List.isEmpty config.Prompts) then
            invalidOp "The FsLangMCP stdio adapter currently supports tool-only ServerConfig values."

        // ServerConfig.Middleware is obsolete in FsMcp 2.0 (declarations were never
        // executed; FsMcp itself now fails closed). This adapter bypasses FsMcp's
        // registration, so keep our own fail-closed guard rather than silently
        // ignoring a middleware declaration that still compiles via the CE op.
#nowarn "44"
        if not (List.isEmpty config.Middleware) then
            invalidOp "The FsLangMCP stdio adapter does not silently ignore FsMcp middleware."
#warnon "44"

        let hostBuilder = Host.CreateApplicationBuilder()
        // Stdout is the protocol channel. Disable inherited console providers;
        // FsLangMCP and FSAC already write bounded operational messages to stderr.
        hostBuilder.Logging.ClearProviders() |> ignore

        let configureOptions =
            Action<McpServerOptions>(fun options ->
                options.ServerInfo <-
                    Implementation(
                        Name = ServerName.value config.Name,
                        // FsMcp.Server normalizes its ServerVersion wrapper through
                        // System.Version (for example 0.16.0 -> 0.16.0.0). The wire
                        // contract and package verifier need the exact SemVer product
                        // identity, so receive the raw value explicitly.
                        Version = serverInfoVersion
                    ))

        let mcpBuilder = hostBuilder.Services.AddMcpServer(configureOptions)
        mcpBuilder.WithStdioServerTransport() |> ignore
        mcpBuilder.WithTools(config.Tools |> List.map createSdkTool :> IEnumerable<McpServerTool>)
        |> ignore

        use host = hostBuilder.Build()
        do! host.RunAsync()
    }
