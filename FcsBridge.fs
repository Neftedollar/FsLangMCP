module FsLangMcp.FcsBridge

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FsLangMcp.Types
open FsLangMcp.BoundedCache
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharp.Compiler.EditorServices
open Newtonsoft.Json.Linq
open Ionide.ProjInfo
open Ionide.ProjInfo.Types

// ─── Helper: find nearest .fsproj ──────────────────────────────────────────────

let private findNearestFsproj (filePath: string) : string option =
    let rec walk (dir: string) =
        if isNull dir then None
        else
            let fsprojs = Directory.GetFiles(dir, "*.fsproj")
            if fsprojs.Length > 0 then Some fsprojs[0]
            else walk (Path.GetDirectoryName(dir))
    walk (Path.GetDirectoryName(Path.GetFullPath(filePath)))

// ─── FcsBridge ─────────────────────────────────────────────────────────────────

type internal FcsBridge() =
    let checker =
        FSharpChecker.Create(
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = true,
            keepAllBackgroundSymbolUses = true
        )

    // Bounded caches keyed by a string combining projectPath + projectOptions hash
    let optionsCache = BoundedCache<string, FSharpProjectOptions>(10)
    let projectResultsCache = BoundedCache<string, FSharpCheckProjectResults>(3)

    let asTask (workflow: Async<'T>) : Task<'T> =
        Async.StartAsTask(workflow, cancellationToken = CancellationToken.None)

    let jstrOrNull (value: string) =
        if String.IsNullOrWhiteSpace(value) then JValue.CreateNull() :> JToken else JValue(value) :> JToken

    let rangeToJson (r: range) =
        JObject(
            JProperty("file", normalizePath r.FileName),
            JProperty("startLine", r.StartLine),
            JProperty("startColumn", r.StartColumn),
            JProperty("endLine", r.EndLine),
            JProperty("endColumn", r.EndColumn)
        )
        :> JToken

    let positionToJson (p: Position) =
        JObject(JProperty("line", p.Line), JProperty("column", p.Column)) :> JToken

    let diagnosticToJson (d: FSharpDiagnostic) =
        JObject(
            JProperty("file", normalizePath d.FileName),
            JProperty("message", d.Message),
            JProperty("severity", d.Severity.ToString()),
            JProperty("errorNumber", d.ErrorNumber),
            JProperty("errorNumberText", d.ErrorNumberText),
            JProperty("subcategory", d.Subcategory),
            JProperty("range", rangeToJson d.Range),
            JProperty("start", positionToJson d.Start),
            JProperty("end", positionToJson d.End)
        )
        :> JToken

    let symbolToJson (symbol: FSharpSymbol) =
        let declarationLocation =
            match symbol.DeclarationLocation with
            | Some r -> rangeToJson r
            | None -> JValue.CreateNull() :> JToken

        JObject(
            JProperty("displayName", symbol.DisplayName),
            JProperty("fullName", jstrOrNull symbol.FullName),
            JProperty("assembly", symbol.Assembly.SimpleName),
            JProperty("declarationLocation", declarationLocation),
            JProperty("isExplicitlySuppressed", symbol.IsExplicitlySuppressed)
        )
        :> JToken

    let symbolUseToJson (symbolUse: FSharpSymbolUse) =
        JObject(
            JProperty("file", normalizePath symbolUse.FileName),
            JProperty("range", rangeToJson symbolUse.Range),
            JProperty("isFromDefinition", symbolUse.IsFromDefinition),
            JProperty("isFromUse", symbolUse.IsFromUse),
            JProperty("isFromPattern", symbolUse.IsFromPattern),
            JProperty("isFromAttribute", symbolUse.IsFromAttribute),
            JProperty("symbol", symbolToJson symbolUse.Symbol)
        )
        :> JToken

    // Build a stable cache key from projectPath and projectOptions list
    let makeCacheKey (projectPath: string option) (projectOptions: string list option) =
        let pp = projectPath |> Option.defaultValue ""
        let po =
            projectOptions
            |> Option.map (fun opts -> String.concat "|" opts)
            |> Option.defaultValue ""
        $"{pp}::{po}"

    member private _.LoadProjectOptionsFromFsproj(fsprojPath: string) : Task<FSharpProjectOptions option> =
        // Offload to thread pool — MSBuild/SDK probing is CPU+IO bound.
        // Assumption: Init.init and WorkspaceLoader do not rely on thread-local or
        // SynchronizationContext state (safe for file-system-based SDK resolution).
        Task.Run(fun () ->
            try
                let projectDir = Path.GetDirectoryName(fsprojPath)
                let toolsPath = Init.init (DirectoryInfo(projectDir)) None
                let loader = WorkspaceLoader.Create(toolsPath, [])
                let projects = loader.LoadProjects([ fsprojPath ]) |> Seq.toList
                match projects with
                | proj :: _ ->
                    let fcsOpts = FCS.mapToFSharpProjectOptions proj (projects |> Seq.map id)
                    Some fcsOpts
                | [] -> None
            with ex ->
                Console.Error.WriteLine($"[proj-info] Failed to load {fsprojPath}: {ex.Message}")
                None
        )

    member private this.ResolveProjectOptions
        (path: string, text: string, projectPath: string option, projectOptions: string list option)
        : Task<FSharpProjectOptions * string> =
        task {
            let fullPath = normalizePath path
            let cacheKey = makeCacheKey projectPath projectOptions

            match projectOptions with
            | Some options when not options.IsEmpty ->
                let projectFileName =
                    projectPath
                    |> Option.defaultValue (Path.ChangeExtension(fullPath, ".fsproj"))
                    |> normalizePath

                match optionsCache.TryGet(cacheKey) with
                | Some cached ->
                    return cached, "commandLineArgs"
                | None ->
                    let resolvedOptions =
                        checker.GetProjectOptionsFromCommandLineArgs(projectFileName, options |> List.toArray)
                    optionsCache.Set(cacheKey, resolvedOptions)
                    return resolvedOptions, "commandLineArgs"
            | _ ->
                // Try auto-discover .fsproj
                let discoveredFsproj = findNearestFsproj fullPath
                match discoveredFsproj with
                | Some fsprojPath ->
                    let fsprojKey = $"fsproj::{fsprojPath}"
                    match optionsCache.TryGet(fsprojKey) with
                    | Some cached ->
                        return cached, "auto-discovered"
                    | None ->
                        // Try real MSBuild loading via Ionide.ProjInfo.FCS
                        let! projInfoResult = this.LoadProjectOptionsFromFsproj(fsprojPath)
                        match projInfoResult with
                        | Some projOpts ->
                            optionsCache.Set(fsprojKey, projOpts)
                            return projOpts, "ionide-proj-info"
                        | None ->
                            // Fall back to script inference with honest labelling
                            let sourceText = SourceText.ofString text
                            let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask
                            let discovered = { scriptOptions with ProjectFileName = fsprojPath }
                            optionsCache.Set(fsprojKey, discovered)
                            return discovered, "auto-discovered-script-fallback"
                | None ->
                    let scriptKey = $"script::{fullPath}"
                    match optionsCache.TryGet(scriptKey) with
                    | Some cached ->
                        return cached, "scriptInference"
                    | None ->
                        let sourceText = SourceText.ofString text
                        let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask
                        optionsCache.Set(scriptKey, scriptOptions)
                        return scriptOptions, "scriptInference"
        }

    member private this.PrepareCheckContext
        (path: string, text: string option, projectPath: string option, projectOptions: string list option)
        : Task<
            string
            * string
            * string
            * FSharpProjectOptions
            * FSharpParseFileResults
            * FSharpCheckFileResults option
        > =
        task {
            let fullPath = normalizePath path
            let source = text |> Option.defaultWith (fun () -> File.ReadAllText(fullPath))
            let sourceText = SourceText.ofString source
            let! options, optionsSource = this.ResolveProjectOptions(fullPath, source, projectPath, projectOptions)
            let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)
            let! parseResults = checker.ParseFile(fullPath, sourceText, parsingOptions) |> asTask
            let! _, checkAnswer = checker.ParseAndCheckFileInProject(fullPath, 0, sourceText, options) |> asTask

            let checkedResults =
                match checkAnswer with
                | FSharpCheckFileAnswer.Succeeded results -> Some results
                | FSharpCheckFileAnswer.Aborted -> None

            return fullPath, source, optionsSource, options, parseResults, checkedResults
        }

    member this.ParseAndCheckFile(args: FcsParseAndCheckArgs) : Task<JToken> =
        task {
            let! path, _, optionsSource, projectOptions, parseResults, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            let parseDiagnostics = parseResults.Diagnostics |> Array.map diagnosticToJson
            let checkDiagnostics =
                checkedResults
                |> Option.map (fun r -> r.Diagnostics |> Array.map diagnosticToJson)
                |> Option.defaultValue [||]

            let hasTypeCheckInfo = checkedResults |> Option.map _.HasFullTypeCheckInfo |> Option.defaultValue false
            let status = if checkedResults.IsSome then "succeeded" else "aborted"

            return
                JObject(
                    JProperty("status", status),
                    JProperty("file", path),
                    JProperty("optionsSource", optionsSource),
                    JProperty("projectFileName", projectOptions.ProjectFileName),
                    JProperty("projectSourceFiles", JArray(projectOptions.SourceFiles)),
                    JProperty("parseHadErrors", parseResults.ParseHadErrors),
                    JProperty("hasFullTypeCheckInfo", hasTypeCheckInfo),
                    JProperty("parseDiagnostics", JArray(parseDiagnostics)),
                    JProperty("checkDiagnostics", JArray(checkDiagnostics))
                )
                :> JToken
        }

    member this.FileSymbols(args: FcsFileSymbolsArgs) : Task<JToken> =
        task {
            let! path, _, optionsSource, _, parseResults, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None ->
                return
                    JObject(
                        JProperty("status", "aborted"),
                        JProperty("file", path),
                        JProperty("optionsSource", optionsSource),
                        JProperty("parseHadErrors", parseResults.ParseHadErrors),
                        JProperty("message", "Type checking was aborted. Symbols are unavailable."),
                        JProperty("parseDiagnostics", JArray(parseResults.Diagnostics |> Array.map diagnosticToJson))
                    )
                    :> JToken
            | Some checkResults ->
                let includeAllUses = args.includeAllUses |> Option.defaultValue false
                let maxResults = args.maxResults |> Option.defaultValue 200

                let symbols =
                    checkResults.GetAllUsesOfAllSymbolsInFile()
                    |> Seq.filter (fun symbolUse -> includeAllUses || symbolUse.IsFromDefinition)
                    |> Seq.distinctBy (fun symbolUse ->
                        symbolUse.Symbol.FullName,
                        symbolUse.Range.StartLine,
                        symbolUse.Range.StartColumn,
                        symbolUse.Range.EndLine,
                        symbolUse.Range.EndColumn)
                    |> Seq.truncate maxResults
                    |> Seq.map symbolUseToJson
                    |> Seq.toArray

                return
                    JObject(
                        JProperty("status", "succeeded"),
                        JProperty("file", path),
                        JProperty("optionsSource", optionsSource),
                        JProperty("includeAllUses", includeAllUses),
                        JProperty("count", symbols.Length),
                        JProperty("symbols", JArray(symbols)),
                        JProperty("parseDiagnostics", JArray(parseResults.Diagnostics |> Array.map diagnosticToJson)),
                        JProperty("checkDiagnostics", JArray(checkResults.Diagnostics |> Array.map diagnosticToJson))
                    )
                    :> JToken
        }

    member this.ProjectSymbolUses(args: FcsProjectSymbolUsesArgs) : Task<JToken> =
        task {
            let query = args.symbolQuery.Trim()

            if String.IsNullOrWhiteSpace(query) then
                invalidArg (nameof args.symbolQuery) "symbolQuery must be non-empty."

            let! _, _, optionsSource, projectOptions, _, _ =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            // Use cached project results if available
            let cacheKey = makeCacheKey args.projectPath args.projectOptions
            let! projectResults, cached =
                task {
                    match projectResultsCache.TryGet(cacheKey) with
                    | Some existing ->
                        return existing, true
                    | None ->
                        let! results = checker.ParseAndCheckProject(projectOptions) |> asTask
                        projectResultsCache.Set(cacheKey, results)
                        return results, false
                }

            let exact = args.exact |> Option.defaultValue false
            let maxResults = args.maxResults |> Option.defaultValue 500

            let symbolMatches (symbol: FSharpSymbol) =
                let displayName = symbol.DisplayName
                let fullName = symbol.FullName

                if exact then
                    String.Equals(displayName, query, StringComparison.Ordinal)
                    || String.Equals(fullName, query, StringComparison.Ordinal)
                else
                    displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (if isNull fullName then
                            false
                        else
                            fullName.Contains(query, StringComparison.OrdinalIgnoreCase))

            let allUses = projectResults.GetAllUsesOfAllSymbols()

            let matchedUses =
                allUses
                |> Seq.filter (fun symbolUse -> symbolMatches symbolUse.Symbol)
                |> Seq.sortBy (fun symbolUse -> symbolUse.FileName, symbolUse.Range.StartLine, symbolUse.Range.StartColumn)
                |> Seq.truncate maxResults
                |> Seq.map symbolUseToJson
                |> Seq.toArray

            return
                JObject(
                    JProperty("status", "succeeded"),
                    JProperty("optionsSource", optionsSource),
                    JProperty("projectFileName", projectOptions.ProjectFileName),
                    JProperty("query", query),
                    JProperty("exact", exact),
                    JProperty("cached", cached),
                    JProperty("totalProjectSymbolUses", allUses.Length),
                    JProperty("matchedCount", matchedUses.Length),
                    JProperty("uses", JArray(matchedUses)),
                    JProperty("projectDiagnostics", JArray(projectResults.Diagnostics |> Array.map diagnosticToJson))
                )
                :> JToken
        }

    member this.TypeAtPosition(args: FcsTypeAtPositionArgs) : Task<JToken> =
        task {
            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None ->
                return
                    JObject(
                        JProperty("status", "aborted"),
                        JProperty("message", "Type checking was aborted.")
                    )
                    :> JToken
            | Some checkResults ->
                // FCS uses 1-based lines; assume input is 0-based (LSP convention)
                let fcsLine = args.line + 1
                let fcsCol = args.character

                // Get the line text for FCS APIs
                let lines = source.Split('\n')
                let lineText =
                    if fcsLine - 1 < lines.Length then lines[fcsLine - 1].TrimEnd('\r')
                    else ""

                let symbolUse =
                    checkResults.GetSymbolUseAtLocation(fcsLine, fcsCol, lineText, [])

                let toolTip =
                    checkResults.GetToolTip(fcsLine, fcsCol, lineText, [], FSharp.Compiler.Tokenization.FSharpTokenTag.IDENT)

                let typeString =
                    match toolTip with
                    | ToolTipText elements ->
                        elements
                        |> List.choose (fun el ->
                            match el with
                            | ToolTipElement.Group items ->
                                items
                                |> List.map (fun item ->
                                    item.MainDescription
                                    |> Array.map (fun tagged -> tagged.Text)
                                    |> String.concat "")
                                |> Some
                            | _ -> None)
                        |> List.collect id
                        |> String.concat "\n"

                let xmlDoc =
                    match toolTip with
                    | ToolTipText elements ->
                        elements
                        |> List.choose (fun el ->
                            match el with
                            | ToolTipElement.Group items ->
                                items
                                |> List.choose (fun item ->
                                    match item.XmlDoc with
                                    | FSharpXmlDoc.FromXmlText xmlText ->
                                        Some (xmlText.GetXmlText())
                                    | _ -> None)
                                |> Some
                            | _ -> None)
                        |> List.collect id
                        |> String.concat "\n"

                match symbolUse with
                | None ->
                    return
                        JObject(
                            JProperty("status", "no_symbol"),
                            JProperty("message", "No symbol at this position"),
                            JProperty("file", path),
                            JProperty("line", args.line),
                            JProperty("character", args.character),
                            JProperty("typeString", typeString)
                        )
                        :> JToken
                | Some su ->
                    return
                        JObject(
                            JProperty("status", "ok"),
                            JProperty("file", path),
                            JProperty("line", args.line),
                            JProperty("character", args.character),
                            JProperty("optionsSource", optionsSource),
                            JProperty("symbolName", su.Symbol.DisplayName),
                            JProperty("fullName", jstrOrNull su.Symbol.FullName),
                            JProperty("typeString", typeString),
                            JProperty("xmlDoc", xmlDoc)
                        )
                        :> JToken
        }

    member _.ClearCaches() =
        optionsCache.Clear()
        projectResultsCache.Clear()

    member private _.BuildSignatureHelpResult
        (path: string, source: string, optionsSource: string, args: FcsSignatureHelpArgs, checkedResults: FSharpCheckFileResults option)
        : JToken =
        match checkedResults with
        | None ->
            JObject(
                JProperty("status", "aborted"),
                JProperty("message", "Type checking was aborted.")
            )
            :> JToken
        | Some checkResults ->
            // FCS uses 1-based lines; assume input is 0-based (LSP convention)
            let fcsLine = args.line + 1
            let fcsCol = args.character

            let lines = source.Split('\n')
            let lineText =
                if fcsLine - 1 < lines.Length then lines[fcsLine - 1].TrimEnd('\r')
                else ""

            let methodsOpt =
                checkResults.GetMethodsAsSymbols(fcsLine, fcsCol, lineText, [])

            let overloads =
                match methodsOpt with
                | None -> [||]
                | Some symbolUses ->
                    symbolUses
                    |> List.choose (fun su ->
                        match su.Symbol with
                        | :? FSharpMemberOrFunctionOrValue as m ->
                            let paramGroups = m.CurriedParameterGroups
                            let parameters =
                                paramGroups
                                |> Seq.collect id
                                |> Seq.map (fun p ->
                                    JObject(
                                        JProperty("name", p.Name |> Option.defaultValue ""),
                                        JProperty("type", p.Type.BasicQualifiedName)
                                    )
                                    :> JToken)
                                |> Seq.toArray

                            let returnType =
                                try m.ReturnParameter.Type.BasicQualifiedName
                                with ex ->
                                    Console.Error.WriteLine($"[fcs_signature_help] ReturnParameter error: {ex.Message}")
                                    ""

                            let signature =
                                let paramStr =
                                    parameters
                                    |> Array.map (fun p ->
                                        let pName = p["name"].Value<string>()
                                        let pType = p["type"].Value<string>()
                                        $"{pName}: {pType}")
                                    |> String.concat ", "
                                $"{m.DisplayName}({paramStr}) -> {returnType}"

                            Some (JObject(
                                JProperty("signature", signature),
                                JProperty("parameters", JArray(parameters)),
                                JProperty("returnType", returnType)
                            )
                            :> JToken)
                        | _ -> None)
                    |> List.toArray

            if overloads.Length = 0 then
                JObject(
                    JProperty("status", "no_overloads"),
                    JProperty("file", path),
                    JProperty("line", args.line),
                    JProperty("character", args.character)
                )
                :> JToken
            else
                JObject(
                    JProperty("status", "ok"),
                    JProperty("file", path),
                    JProperty("line", args.line),
                    JProperty("character", args.character),
                    JProperty("optionsSource", optionsSource),
                    JProperty("overloads", JArray(overloads))
                )
                :> JToken

    member this.SignatureHelp(args: FcsSignatureHelpArgs) : Task<JToken> =
        task {
            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)
            return this.BuildSignatureHelpResult(path, source, optionsSource, args, checkedResults)
        }
