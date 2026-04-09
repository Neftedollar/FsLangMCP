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
open System.Text.Json
open System.Text.Json.Nodes
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

    let jstrOrNull (value: string) : JsonNode =
        if String.IsNullOrWhiteSpace(value) then null else JsonValue.Create(value)

    let rangeToJson (r: range) : JsonNode =
        jobj
            [ "file", jstr (normalizePath r.FileName)
              "startLine", jint r.StartLine
              "startColumn", jint r.StartColumn
              "endLine", jint r.EndLine
              "endColumn", jint r.EndColumn ]
        :> JsonNode

    let positionToJson (p: Position) : JsonNode =
        jobj [ "line", jint p.Line; "column", jint p.Column ] :> JsonNode

    let diagnosticToJson (d: FSharpDiagnostic) : JsonNode =
        jobj
            [ "file", jstr (normalizePath d.FileName)
              "message", jstr d.Message
              "severity", jstr (d.Severity.ToString())
              "errorNumber", jint d.ErrorNumber
              "errorNumberText", jstr d.ErrorNumberText
              "subcategory", jstr d.Subcategory
              "range", rangeToJson d.Range
              "start", positionToJson d.Start
              "end", positionToJson d.End ]
        :> JsonNode

    let symbolToJson (symbol: FSharpSymbol) : JsonNode =
        let declarationLocation =
            match symbol.DeclarationLocation with
            | Some r -> rangeToJson r
            | None -> null

        jobj
            [ "displayName", jstr symbol.DisplayName
              "fullName", jstrOrNull symbol.FullName
              "assembly", jstr symbol.Assembly.SimpleName
              "declarationLocation", declarationLocation
              "isExplicitlySuppressed", jbool symbol.IsExplicitlySuppressed ]
        :> JsonNode

    let symbolUseToJson (symbolUse: FSharpSymbolUse) : JsonNode =
        jobj
            [ "file", jstr (normalizePath symbolUse.FileName)
              "range", rangeToJson symbolUse.Range
              "isFromDefinition", jbool symbolUse.IsFromDefinition
              "isFromUse", jbool symbolUse.IsFromUse
              "isFromPattern", jbool symbolUse.IsFromPattern
              "isFromAttribute", jbool symbolUse.IsFromAttribute
              "symbol", symbolToJson symbolUse.Symbol ]
        :> JsonNode

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

    member this.ParseAndCheckFile(args: FcsParseAndCheckArgs) : Task<JsonNode> =
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
                jobj
                    [ "status", jstr status
                      "file", jstr path
                      "optionsSource", jstr optionsSource
                      "projectFileName", jstr projectOptions.ProjectFileName
                      "projectSourceFiles",
                      JsonArray(projectOptions.SourceFiles |> Array.map jstr) :> JsonNode
                      "parseHadErrors", jbool parseResults.ParseHadErrors
                      "hasFullTypeCheckInfo", jbool hasTypeCheckInfo
                      "parseDiagnostics", JsonArray(parseDiagnostics) :> JsonNode
                      "checkDiagnostics", JsonArray(checkDiagnostics) :> JsonNode ]
                :> JsonNode
        }

    member this.FileSymbols(args: FcsFileSymbolsArgs) : Task<JsonNode> =
        task {
            let! path, _, optionsSource, _, parseResults, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None ->
                return
                    jobj
                        [ "status", jstr "aborted"
                          "file", jstr path
                          "optionsSource", jstr optionsSource
                          "parseHadErrors", jbool parseResults.ParseHadErrors
                          "message", jstr "Type checking was aborted. Symbols are unavailable."
                          "parseDiagnostics",
                          JsonArray(parseResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode ]
                    :> JsonNode
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
                    jobj
                        [ "status", jstr "succeeded"
                          "file", jstr path
                          "optionsSource", jstr optionsSource
                          "includeAllUses", jbool includeAllUses
                          "count", jint symbols.Length
                          "symbols", JsonArray(symbols) :> JsonNode
                          "parseDiagnostics",
                          JsonArray(parseResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode
                          "checkDiagnostics",
                          JsonArray(checkResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode ]
                    :> JsonNode
        }

    member this.ProjectSymbolUses(args: FcsProjectSymbolUsesArgs) : Task<JsonNode> =
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
                jobj
                    [ "status", jstr "succeeded"
                      "optionsSource", jstr optionsSource
                      "projectFileName", jstr projectOptions.ProjectFileName
                      "query", jstr query
                      "exact", jbool exact
                      "cached", jbool cached
                      "totalProjectSymbolUses", jint allUses.Length
                      "matchedCount", jint matchedUses.Length
                      "uses", JsonArray(matchedUses) :> JsonNode
                      "projectDiagnostics",
                      JsonArray(projectResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode ]
                :> JsonNode
        }

    member this.TypeAtPosition(args: FcsTypeAtPositionArgs) : Task<JsonNode> =
        task {
            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None ->
                return
                    jobj [ "status", jstr "aborted"; "message", jstr "Type checking was aborted." ]
                    :> JsonNode
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
                        jobj
                            [ "status", jstr "no_symbol"
                              "message", jstr "No symbol at this position"
                              "file", jstr path
                              "line", jint args.line
                              "character", jint args.character
                              "typeString", jstr typeString ]
                        :> JsonNode
                | Some su ->
                    return
                        jobj
                            [ "status", jstr "ok"
                              "file", jstr path
                              "line", jint args.line
                              "character", jint args.character
                              "optionsSource", jstr optionsSource
                              "symbolName", jstr su.Symbol.DisplayName
                              "fullName", jstrOrNull su.Symbol.FullName
                              "typeString", jstr typeString
                              "xmlDoc", jstr xmlDoc ]
                        :> JsonNode
        }

    member _.ClearCaches() =
        optionsCache.Clear()
        projectResultsCache.Clear()

    member private _.BuildSignatureHelpResult
        (path: string, source: string, optionsSource: string, args: FcsSignatureHelpArgs, checkedResults: FSharpCheckFileResults option)
        : JsonNode =
        match checkedResults with
        | None ->
            jobj [ "status", jstr "aborted"; "message", jstr "Type checking was aborted." ]
            :> JsonNode
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
                                    jobj
                                        [ "name", jstr (p.Name |> Option.defaultValue "")
                                          "type", jstr p.Type.BasicQualifiedName ]
                                    :> JsonNode)
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
                                        let pName = p["name"].GetValue<string>()
                                        let pType = p["type"].GetValue<string>()
                                        $"{pName}: {pType}")
                                    |> String.concat ", "
                                $"{m.DisplayName}({paramStr}) -> {returnType}"

                            Some (jobj
                                [ "signature", jstr signature
                                  "parameters", JsonArray(parameters) :> JsonNode
                                  "returnType", jstr returnType ]
                            :> JsonNode)
                        | _ -> None)
                    |> List.toArray

            if overloads.Length = 0 then
                jobj
                    [ "status", jstr "no_overloads"
                      "file", jstr path
                      "line", jint args.line
                      "character", jint args.character ]
                :> JsonNode
            else
                jobj
                    [ "status", jstr "ok"
                      "file", jstr path
                      "line", jint args.line
                      "character", jint args.character
                      "optionsSource", jstr optionsSource
                      "overloads", JsonArray(overloads) :> JsonNode ]
                :> JsonNode

    member this.SignatureHelp(args: FcsSignatureHelpArgs) : Task<JsonNode> =
        task {
            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)
            return this.BuildSignatureHelpResult(path, source, optionsSource, args, checkedResults)
        }
