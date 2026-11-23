module internal FsLangMcp.TypedToolDiagnostics

open System
open System.Text.Json
open FsMcp.Core
open FsMcp.Server
open ModelContextProtocol

let private property name (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Object then None
    else
        match element.TryGetProperty(name: string) with
        | true, value -> Some value
        | _ -> None

let private strings (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Array then Seq.empty
    else
        element.EnumerateArray()
        |> Seq.choose (fun item ->
            if item.ValueKind = JsonValueKind.String then Some(item.GetString()) else None)

let private safeName (name: string) =
    // Show a short identifier, never a control character, path, or a long user key.
    name.Length > 0 && name.Length <= 32
    && (Char.IsAsciiLetter(name[0]) || name[0] = '_')
    && (name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit(c) || c = '_'))

let private fieldList label count (names: seq<string>) =
    let candidates = names |> Seq.truncate (count + 1) |> Seq.toArray
    let visible = candidates |> Array.truncate count
    if visible.Length = 0 then None
    else
        let suffix = if candidates.Length > count then ", …" else ""
        let joined = String.concat ", " visible
        Some $"{label}: {joined}{suffix}."

let private compatibleJsonType schema actual =
    match schema with
    | "string" -> actual = JsonValueKind.String
    | "integer"
    | "number" -> actual = JsonValueKind.Number
    | "boolean" -> actual = JsonValueKind.True || actual = JsonValueKind.False
    | "object" -> actual = JsonValueKind.Object
    | "array" -> actual = JsonValueKind.Array
    | "null" -> actual = JsonValueKind.Null
    | _ -> true

let private incompatibleType (schema: JsonElement) (value: JsonElement) =
    match property "type" schema with
    | Some definition when definition.ValueKind = JsonValueKind.String ->
        not (compatibleJsonType (definition.GetString()) value.ValueKind)
    | Some definition when definition.ValueKind = JsonValueKind.Array ->
        let accepted = strings definition |> Seq.toArray
        accepted.Length > 0 && not (accepted |> Array.exists (fun kind -> compatibleJsonType kind value.ValueKind))
    | _ -> false

let private decorate (definition: ToolDefinition) =
    match definition.InputSchema with
    | None -> definition
    | Some schema ->
        let properties =
            match property "properties" schema with
            | Some fields when fields.ValueKind = JsonValueKind.Object ->
                fields.EnumerateObject() |> Seq.map (fun item -> item.Name, item.Value) |> Seq.toArray
            | _ -> [||]
        let required = property "required" schema |> Option.map (strings >> Seq.toArray) |> Option.defaultValue [||]
        let allowed = properties |> Array.map fst |> Array.filter safeName |> Array.sort
        let missingRequired arguments name =
            match Map.tryFind name arguments with
            | Some (value: JsonElement) ->
                value.ValueKind = JsonValueKind.Null || value.ValueKind = JsonValueKind.Undefined
            | None -> true

        { definition with
            Handler = fun arguments cancellationToken ->
                task {
                    try
                        return! definition.Handler arguments cancellationToken
                    with :? McpProtocolException as error when
                        error.ErrorCode = McpErrorCode.InvalidParams
                        && error.Message = "Invalid tool arguments." ->
                        let missing = required |> Array.filter (fun name -> safeName name && missingRequired arguments name)
                        let unknown = ResizeArray<string>()
                        let incompatible = ResizeArray<string>()
                        // FsMcp's required-name precheck is exact; its deserializer recognizes
                        // accepted aliases case-insensitively. Report unknown names accordingly.
                        for KeyValue(name, value) in arguments do
                            if safeName name then
                                match properties |> Array.tryFind (fun (known, _) ->
                                    String.Equals(known, name, StringComparison.OrdinalIgnoreCase)) with
                                | None when unknown.Count < 5 -> unknown.Add(name)
                                | Some (canonical, field) when
                                    missing.Length = 0 && incompatible.Count < 5 && incompatibleType field value ->
                                    incompatible.Add(canonical)
                                | _ -> ()
                        let details =
                            [ fieldList "Missing required fields" 4 missing
                              fieldList "Unknown fields" 4 unknown
                              fieldList "Fields with incompatible JSON types" 4 (incompatible |> Seq.distinct)
                              fieldList "Allowed fields" 6 allowed ]
                            |> List.choose id
                            |> String.concat " "
                        let message =
                            if String.IsNullOrEmpty(details) then error.Message
                            else $"{error.Message} {details} See tools/list for the complete input schema."
                        return raise (McpProtocolException(message, error.ErrorCode))
                } }

/// Preserve FsMcp's binding semantics and only decorate its pre-handler InvalidParams.
let define<'TArgs> name description handler =
    TypedTool.define<'TArgs> name description handler |> Result.map decorate
