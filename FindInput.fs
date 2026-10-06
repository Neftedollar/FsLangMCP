module FsLangMcp.FindInput

open System
open System.Text.Json.Nodes
open FsLangMcp.Types

type Request =
    | Sites of FindArgs
    | Inventory of FindArgs * string array

let private invalid message =
    Error(
        jobj
            [ "status", jstr "invalid_args"
              "errorKind", jstr "invalid_find_inventory_args"
              "message", jstr message ]
        :> JsonNode
    )

let private siteArgs (args: FindToolArgs) query : FindArgs =
    { query = query
      kind = args.kind
      scope = args.scope
      exact = args.exact
      ``member`` = args.``member``
      field = args.field
      path = args.path
      line = args.line
      word = args.word
      occurrence = args.occurrence
      character = args.character
      contextLines = args.contextLines
      includeDeclaration = args.includeDeclaration
      includeInfo = args.includeInfo
      includePerProject = args.includePerProject
      includeSiteTypes = args.includeSiteTypes
      projectPath = args.projectPath
      maxResults = args.maxResults
      timeoutMs = args.timeoutMs
      cursor = args.cursor }

/// Validate the alternative input before gate admission. Backend validation still
/// owns paths/scope/deadlines, exactly as for the legacy single-query entry point.
let prepare (args: FindToolArgs) : Result<Request, JsonNode> =
    let inventory queries =
        let kind = args.kind |> Option.defaultValue "symbol" |> fun value -> value.Trim().ToLowerInvariant()

        if queries |> Array.exists String.IsNullOrWhiteSpace then
            invalid "Inventory queries must all be non-blank strings."
        elif queries.Length < 1 || queries.Length > 50 then
            invalid "Inventory requires 1..50 queries. Split larger inventories into batches."
        elif not (Set.ofList [ "auto"; "symbol"; "definition" ] |> Set.contains kind) then
            invalid "Inventory supports kind='symbol' or kind='definition'; omitted/auto means symbol."
        elif args.``member``.IsSome || args.field.IsSome then
            invalid "Inventory does not support member or field selectors. Use single-query site search."
        elif args.line.IsSome || args.word.IsSome || args.occurrence.IsSome || args.character.IsSome then
            invalid "Inventory does not support position selectors. Use single-query kind='position'."
        elif args.contextLines |> Option.exists ((<>) 0) then
            invalid "Inventory does not construct snippets; contextLines must be 0 or omitted."
        elif args.includeSiteTypes = Some true then
            invalid "Inventory does not resolve field site types; omit includeSiteTypes."
        elif args.cursor.IsSome then
            invalid "Inventory has no continuation cursor. Split queries or narrow projectPath/scope instead."
        else
            let normalized = queries |> Array.map _.Trim()

            if (normalized |> Array.distinct).Length <> normalized.Length then
                invalid "Inventory queries must be distinct after trimming."
            else
                let seed = siteArgs args normalized[0]
                let seed = { seed with kind = Some(if kind = "auto" then "symbol" else kind) }
                Ok(Inventory(seed, normalized))

    match args.query, args.queries with
    | Some _, Some _ -> invalid "Supply either query or queries, not both."
    | None, None -> invalid "Supply query for site search or queries for count-only inventory."
    | _, Some _ when args.countsOnly = Some false ->
        invalid "queries requires countsOnly=true (the default). Multi-query snippets are not supported."
    | None, Some queries -> inventory (List.toArray queries)
    | Some query, None when args.countsOnly = Some true -> inventory [| query |]
    | Some query, None -> Ok(Sites(siteArgs args query))
