module internal FsLangMcp.CheckDiagnostics

open System
open System.Text.Json.Nodes
open FsLangMcp.Types

// Both FCS strings and LSP numeric severities are accepted. Keep the wire's full
// diagnostic untouched; these helpers only control presentation.
let severityRank (node: JsonNode) =
    match node["severity"] with
    | :? JsonValue as value ->
        let mutable number = 0
        let mutable name = ""

        if value.TryGetValue<int>(&number) then
            number
        elif value.TryGetValue<string>(&name) then
            match name.ToLowerInvariant() with
            | "error" -> 1
            | "warning" -> 2
            | "info"
            | "information" -> 3
            | "hidden"
            | "hint" -> 4
            | _ -> 0
        else
            0
    | _ -> 0

let private text (key: string) (node: JsonNode) =
    match node[key] with
    | null -> ""
    | value -> value.ToString()

let private groupKey (node: JsonNode) =
    let code =
        match text "errorNumberText" node with
        | "" -> text "code" node
        | code -> code

    // Normalize platform newlines, not whitespace inside quoted identifiers or
    // type strings: distinct compiler explanations must never become one group.
    let message = (text "message" node).Replace("\r\n", "\n")
    code, severityRank node, message, text "resolvedType" node

let private location (node: JsonNode) =
    let file = text "file" node
    let range = node["range"]

    let line =
        if isNull range then
            "?"
        elif not (isNull range["startLine"]) then
            range["startLine"].ToString()
        elif not (isNull range["start"]) && not (isNull (range["start"]).["line"]) then
            // LSP locations are zero-based; the compact file:line format is one-based.
            let start = range["start"]
            string (start["line"].GetValue<int>() + 1)
        else
            "?"

    $"{file}:{line}"

let group (nodes: JsonNode array) =
    nodes
    |> Array.groupBy groupKey
    |> Array.map (fun (_, entries) ->
        let representative = entries[0].DeepClone()
        let otherLocations = entries |> Array.skip 1 |> Array.map location
        let shownLocations = otherLocations |> Array.truncate 20
        representative["count"] <- jint entries.Length
        representative["locations"] <- JsonArray(shownLocations |> Array.map jstr)
        representative["locationsTruncated"] <- jbool (otherLocations.Length > shownLocations.Length)
        representative["omittedLocationCount"] <- jint (otherLocations.Length - shownLocations.Length)
        representative)

[<Literal>]
let ResponseBudgetChars = 60_000

/// Bound the JSON that actually ships, not merely its row count. Prefer retaining
/// complete representatives over repeated location strings. Counts remain full-set
/// evidence, and budget cuts are distinct from the severity floor and row limit.
let boundResponse (response: JsonNode) : JsonNode =
    response["responseBudgetChars"] <- jint ResponseBudgetChars
    response["responseTruncatedByBudget"] <- jbool false
    response["diagnosticsTruncatedByBudget"] <- jbool false
    response["responseSizeHint"] <- null

    let overBudget () = renderedLength response > ResponseBudgetChars

    if overBudget () then
        response["responseTruncatedByBudget"] <- jbool true
        response["responseSizeHint"] <-
            jstr "Response details were reduced to fit the JSON budget. Narrow path/fileGlob/project scope for individual diagnostics; inspect compiler output if one diagnostic alone is too large."
        let diagnostics = response["diagnostics"].AsArray()

        for diagnostic in diagnostics do
            match diagnostic["locations"] with
            | :? JsonArray as locations when locations.Count > 0 ->
                let omitted = diagnostic["omittedLocationCount"].GetValue<int>() + locations.Count
                locations.Clear()
                diagnostic["locationsTruncated"] <- jbool true
                diagnostic["omittedLocationCount"] <- jint omitted
            | _ -> ()

        if overBudget () then
            response["diagnosticsTruncated"] <- jbool true
            response["diagnosticsTruncatedByBudget"] <- jbool true
            let rows = diagnostics |> Seq.toArray
            let setPrefix count =
                diagnostics.Clear()
                for i in 0 .. count - 1 do
                    diagnostics.Add(rows[i])

            // Find a maximal fitting prefix in O(log rows) serializations rather
            // than repeatedly serializing an almost-complete oversized response.
            let mutable low = 0
            let mutable high = rows.Length
            while low < high do
                let middle = (low + high + 1) / 2
                setPrefix middle
                if overBudget () then high <- middle - 1 else low <- middle
            setPrefix low

    if overBudget () then
        // Coverage/source metadata alone can exceed the ceiling. Do not silently
        // truncate coverage lists and let a partial envelope look complete.
        jobj [ "status", jstr "aborted"
               "verdict", jstr "unknown"
               "analyzed", jbool false
               "groundTruth", jbool false
               "errorCount", response["errorCount"].DeepClone()
               "warningCount", response["warningCount"].DeepClone()
               "infoCount", response["infoCount"].DeepClone()
               "totalDiagnostics", response["totalDiagnostics"].DeepClone()
               "errorKind", jstr "check_response_metadata_exceeds_budget"
               "reason", jstr "Check coverage metadata exceeds the response budget even without diagnostics. Retry with a narrower path, fileGlob, or project scope."
               "diagnostics", JsonArray() :> JsonNode
               "diagnosticsTruncated", jbool true
               "diagnosticsTruncatedByBudget", jbool true
               "responseTruncatedByBudget", jbool true
               "responseBudgetChars", jint ResponseBudgetChars ] :> JsonNode
    else
        response
