module FsLangMcp.Tests.ToolDescriptionSchemaTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit

// Mirrors scripts/audit-tool-descriptions.py so the same rules ship in the
// `dotnet test` gate. If you tweak one, tweak both. Reference:
// docs/tool-description-schema.md.

[<Literal>]
let OverBudgetCeiling = 500

[<Literal>]
let UnderBudgetFloor = 150

let private recognizedTags = [ "[FSAC]"; "[FCS in-process]"; "[meta]" ]

// Tools known to overlap with each other. At least one side of every pair
// must contain an explicit "prefer X" / "avoid for" / "instead of" / etc.
// callout so an agent reading either description can disambiguate.
let private overlapPairs : (string * string) list =
    [ "workspace_symbol", "fcs_find_symbol"
      "fcs_referenced_symbols", "fcs_nuget_types"
      "fcs_nuget_types", "fcs_nuget_members"
      "fcs_validate_snippet", "fcs_parse_and_check_file"
      "fcs_signature_help", "fsharp_signature_data"
      "fcs_symbol_at_word", "fcs_type_at_position"
      "fcs_file_outline", "fcs_file_symbols"
      "workspace_diagnostics", "fcs_check_file"
      "textDocument_definition", "fcs_find_symbol" ]

let private preferRegex =
    Regex(
        @"\b(prefer\b|avoid\b|better than\b|use this when\b|use when\b|instead of\b|fall back\b|fallback to\b|use\s+`?[a-z_]+`?\s+(when|for|to)\b)",
        RegexOptions.IgnoreCase
    )

let private findRepoRoot () =
    let rec loop (dir: DirectoryInfo) =
        if isNull dir then
            failwith "Could not locate FsLangMcp repo root from AppContext.BaseDirectory."
        elif File.Exists(Path.Combine(dir.FullName, "FsLangMcp.fsproj")) then
            dir.FullName
        else
            loop dir.Parent

    loop (DirectoryInfo(AppContext.BaseDirectory))

let private programFsPath () =
    Path.Combine(findRepoRoot (), "Program.fs")

let private toolPattern =
    Regex(
        "TypedTool\\.define<[^>]+>\\s*\\n\\s*\"([^\"]+)\"\\s*\\n\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Singleline
    )

let private decodeFSharpStringLiteral (raw: string) =
    raw.Replace("\\\"", "\"").Replace("\\\\", "\\")

let private parseDescriptions () : (string * string) list =
    let source = File.ReadAllText(programFsPath ())

    [ for m in toolPattern.Matches(source) -> m.Groups.[1].Value, decodeFSharpStringLiteral m.Groups.[2].Value ]

let private hasTag (desc: string) =
    recognizedTags |> List.exists desc.StartsWith

let private hasPreferCallout (desc: string) = preferRegex.IsMatch(desc)

[<Fact>]
let ``Program.fs contains at least one parseable tool description`` () =
    let descs = parseDescriptions ()
    Assert.NotEmpty(descs)

[<Fact>]
let ``every tool description starts with a recognized tag prefix`` () =
    let violations =
        parseDescriptions ()
        |> List.filter (fun (_, d) -> not (hasTag d))
        |> List.map (fun (name, d) ->
            let head = if d.Length > 40 then d.Substring(0, 40) + "..." else d
            sprintf "%s: starts with %A (expected one of %A)" name head recognizedTags)

    Assert.Empty(violations)

[<Fact>]
let ``every tool description fits within the 500-char routing ceiling`` () =
    let violations =
        parseDescriptions ()
        |> List.filter (fun (_, d) -> d.Length > OverBudgetCeiling)
        |> List.map (fun (name, d) ->
            sprintf
                "%s: length %d exceeds %d-char ceiling — split into routing-only description + section in docs/tools-detailed.md"
                name
                d.Length
                OverBudgetCeiling)

    Assert.Empty(violations)

[<Fact>]
let ``every overlap pair has at least one prefer-or-avoid callout`` () =
    let descMap = parseDescriptions () |> Map.ofList

    let violations =
        overlapPairs
        |> List.choose (fun (a, b) ->
            match Map.tryFind a descMap, Map.tryFind b descMap with
            | Some da, Some db ->
                if hasPreferCallout da || hasPreferCallout db then
                    None
                else
                    Some (
                        sprintf
                            "overlap pair (%s, %s): neither description contains a prefer/avoid/instead-of callout"
                            a
                            b
                    )
            | _ -> None)

    Assert.Empty(violations)
