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
open FsLangMcp.ProjectFiles
open FsLangMcp.Cursor
open Ionide.ProjInfo
open Ionide.ProjInfo.Types

// ─── Helper: find nearest .fsproj ──────────────────────────────────────────────

let private findNearestFsproj (filePath: string) : string option =
    let rec walk (dir: string) =
        if isNull dir then
            None
        else
            let fsprojs = Directory.GetFiles(dir, "*.fsproj")

            if fsprojs.Length > 0 then
                Some fsprojs[0]
            else
                walk (Path.GetDirectoryName(dir))

    walk (Path.GetDirectoryName(Path.GetFullPath(filePath)))

let private explicitFsproj (projectPath: string option) : string option =
    projectPath
    |> Option.map normalizePath
    |> Option.filter (fun path -> String.Equals(Path.GetExtension(path), ".fsproj", StringComparison.OrdinalIgnoreCase))

// ─── FcsBridge ─────────────────────────────────────────────────────────────────

type internal FcsBridge() =
    let checker =
        FSharpChecker.Create(
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = true,
            keepAllBackgroundSymbolUses = true
        )

    // Bounded caches keyed by a string combining projectPath + projectOptions hash.
    // Keep the source label with the options so cache hits report the same
    // resolution path as the original miss.
    let optionsCache = BoundedCache<string, FSharpProjectOptions * string>(10)
    let projectResultsCache = BoundedCache<string, FSharpCheckProjectResults>(3)

    let asTask (workflow: Async<'T>) : Task<'T> =
        Async.StartAsTask(workflow, cancellationToken = CancellationToken.None)

    let jstrOrNull (value: string) : JsonNode =
        if String.IsNullOrWhiteSpace(value) then
            null
        else
            JsonValue.Create(value)

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

    let typeName (typ: FSharpType) =
        typ.BasicQualifiedName
        |> Option.defaultWith (fun () -> typ.Format(FSharpDisplayContext.Empty))

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

    let tryDeclarationRange (symbol: FSharpSymbol) =
        symbol.DeclarationLocation |> Option.map rangeToJson |> Option.defaultValue null

    let tryReflectionStringProperty propertyName (value: obj) =
        try
            let property = value.GetType().GetProperty(propertyName)

            if isNull property then
                None
            else
                match property.GetValue(value) with
                | null -> None
                | propertyValue -> Some(propertyValue.ToString())
        with _ ->
            None

    let symbolKind (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpEntity as entity when entity.IsNamespace -> "namespace"
        | :? FSharpEntity as entity when entity.IsFSharpModule -> "module"
        | :? FSharpEntity as entity when entity.IsInterface -> "interface"
        | :? FSharpEntity as entity when entity.IsFSharpRecord -> "record"
        | :? FSharpEntity as entity when entity.IsFSharpUnion -> "union"
        | :? FSharpEntity as entity when entity.IsEnum -> "enum"
        | :? FSharpEntity as entity when entity.IsDelegate -> "delegate"
        | :? FSharpEntity as entity when entity.IsClass -> "class"
        | :? FSharpMemberOrFunctionOrValue as memberOrValue when memberOrValue.IsConstructor -> "constructor"
        | :? FSharpMemberOrFunctionOrValue as memberOrValue when memberOrValue.IsProperty -> "property"
        | :? FSharpMemberOrFunctionOrValue as memberOrValue when memberOrValue.IsMember -> "member"
        | :? FSharpMemberOrFunctionOrValue as memberOrValue when memberOrValue.IsModuleValueOrMember -> "function_or_value"
        | :? FSharpField -> "field"
        | _ -> symbol.GetType().Name

    let symbolTypeString (symbol: FSharpSymbol) =
        try
            match symbol with
            | :? FSharpMemberOrFunctionOrValue as memberOrValue -> typeName memberOrValue.FullType
            | :? FSharpField as field -> typeName field.FieldType
            | :? FSharpEntity as entity -> entity.DisplayName
            | _ -> ""
        with _ ->
            ""

    let symbolAccessibility (symbol: FSharpSymbol) =
        symbol :> obj |> tryReflectionStringProperty "Accessibility" |> Option.map jstr |> Option.defaultValue null

    let compactSymbolToJson (symbol: FSharpSymbol) =
        jobj
            [ "name", jstr symbol.DisplayName
              "fullName", jstrOrNull symbol.FullName
              "kind", jstr (symbolKind symbol)
              "typeString", jstr (symbolTypeString symbol)
              "accessibility", symbolAccessibility symbol
              "declarationRange", tryDeclarationRange symbol ]
        :> JsonNode

    let isNoisyLocalSymbol (symbolUse: FSharpSymbolUse) =
        let name = symbolUse.Symbol.DisplayName
        let kind = symbolKind symbolUse.Symbol

        String.IsNullOrWhiteSpace(name)
        || name = "_"
        || name = "this"
        || String.IsNullOrWhiteSpace(symbolUse.Symbol.FullName)
        || String.Equals(kind, "FSharpMemberOrFunctionOrValue", StringComparison.Ordinal)
        || String.Equals(kind, "field", StringComparison.Ordinal)

    let sourceLines (source: string) =
        source.Split('\n') |> Array.map (fun line -> line.TrimEnd('\r'))

    let lineContextToJson contextLines filePath startLine =
        let contextLines = max 0 contextLines

        if not (File.Exists filePath) then
            jobj [ "lineText", jstr ""; "before", JsonArray() :> JsonNode; "after", JsonArray() :> JsonNode ]
        else
            let lines = File.ReadAllLines(filePath)
            let lineIndex = max 0 (startLine - 1)

            let lineText =
                if lineIndex < lines.Length then
                    lines[lineIndex]
                else
                    ""

            let beforeStart = max 0 (lineIndex - contextLines)
            let beforeEnd = lineIndex - 1
            let afterStart = lineIndex + 1
            let afterEnd = min (lines.Length - 1) (lineIndex + contextLines)

            let indexedLine number text =
                jobj [ "line", jint number; "text", jstr text ] :> JsonNode

            let before =
                if beforeEnd < beforeStart then
                    [||]
                else
                    [| beforeStart..beforeEnd |] |> Array.map (fun idx -> indexedLine (idx + 1) lines[idx])

            let after =
                if afterEnd < afterStart then
                    [||]
                else
                    [| afterStart..afterEnd |] |> Array.map (fun idx -> indexedLine (idx + 1) lines[idx])

            jobj
                [ "lineText", jstr lineText
                  "before", JsonArray(before) :> JsonNode
                  "after", JsonArray(after) :> JsonNode ]

    let symbolMatches query exact (symbol: FSharpSymbol) =
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

    let isIdentifierChar (ch: char) =
        Char.IsLetterOrDigit(ch) || ch = '_' || ch = '\'' || ch = '`'

    let identifierSpans (lineText: string) =
        let spans = ResizeArray<int * int * string>()
        let mutable index = 0

        while index < lineText.Length do
            if isIdentifierChar lineText[index] then
                let start = index

                while index < lineText.Length && isIdentifierChar lineText[index] do
                    index <- index + 1

                let text = lineText.Substring(start, index - start)
                spans.Add(start, index, text)
            else
                index <- index + 1

        spans |> Seq.toArray

    let wordSpans word (lineText: string) =
        match word with
        | Some query when not (String.IsNullOrWhiteSpace query) ->
            let spans = ResizeArray<int * int * string>()
            let mutable searchFrom = 0
            let mutable keepSearching = true

            while keepSearching && searchFrom <= lineText.Length do
                let index = lineText.IndexOf(query, searchFrom, StringComparison.Ordinal)

                if index < 0 then
                    keepSearching <- false
                else
                    spans.Add(index, index + query.Length, query)
                    searchFrom <- index + query.Length

            spans |> Seq.toArray
        | _ -> identifierSpans lineText

    let candidateToJson occurrence line startColumn endColumn text =
        jobj
            [ "occurrence", jint occurrence
              "line", jint line
              "startColumn", jint startColumn
              "endColumn", jint endColumn
              "text", jstr text ]
        :> JsonNode

    // Build a stable cache key from projectPath and projectOptions list
    let makeCacheKey (projectPath: string option) (projectOptions: string list option) =
        let pp = projectPath |> Option.defaultValue ""

        let po =
            projectOptions
            |> Option.map (fun opts -> String.concat "|" opts)
            |> Option.defaultValue ""

        $"%s{pp}::%s{po}"

    let makeResolvedProjectCacheKey (projectOptions: FSharpProjectOptions) =
        let optionsHash = String.concat "|" projectOptions.OtherOptions
        $"%s{projectOptions.ProjectFileName}::%s{optionsHash}"

    let countDiagnosticsBySeverity (diagnostics: FSharpDiagnostic array) =
        let errors =
            diagnostics
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

        let warnings =
            diagnostics
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Warning)

        errors.Length, warnings.Length

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
                Console.Error.WriteLine($"[proj-info] Failed to load %s{fsprojPath}: %s{ex.Message}")
                None)

    member private this.ResolveFsprojOptions(fsprojPath: string) : Task<FSharpProjectOptions * string> =
        task {
            let fullPath = normalizePath fsprojPath
            let fsprojKey = $"fsproj::%s{fullPath}"

            match optionsCache.TryGet(fsprojKey) with
            | Some(cached, source) -> return cached, source
            | None ->
                let! projInfoResult = this.LoadProjectOptionsFromFsproj(fullPath)

                match projInfoResult with
                | Some projOpts ->
                    optionsCache.Set(fsprojKey, (projOpts, "ionide-proj-info"))
                    return projOpts, "ionide-proj-info"
                | None ->
                    return
                        raise (
                            InvalidOperationException(
                                $"Unable to load F# project options from explicit projectPath: %s{fullPath}"
                            )
                        )
        }

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
                | Some(cached, source) -> return cached, source
                | None ->
                    let resolvedOptions =
                        checker.GetProjectOptionsFromCommandLineArgs(projectFileName, options |> List.toArray)

                    optionsCache.Set(cacheKey, (resolvedOptions, "commandLineArgs"))
                    return resolvedOptions, "commandLineArgs"
            | _ ->
                let requestedFsproj = explicitFsproj projectPath

                let resolvedFsproj =
                    requestedFsproj |> Option.orElseWith (fun () -> findNearestFsproj fullPath)

                match resolvedFsproj with
                | Some fsprojPath ->
                    try
                        return! this.ResolveFsprojOptions(fsprojPath)
                    with ex ->
                        match requestedFsproj with
                        | Some _ -> return raise ex
                        | None ->
                            // Fall back to script inference with honest labelling.
                            let sourceText = SourceText.ofString text

                            let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask

                            let discovered =
                                { scriptOptions with
                                    ProjectFileName = fsprojPath }

                            let fsprojKey = $"fsproj::%s{fsprojPath}"
                            optionsCache.Set(fsprojKey, (discovered, "auto-discovered-script-fallback"))
                            return discovered, "auto-discovered-script-fallback"
                | None ->
                    let scriptKey = $"script::%s{fullPath}"

                    match optionsCache.TryGet(scriptKey) with
                    | Some(cached, source) -> return cached, source
                    | None ->
                        let sourceText = SourceText.ofString text
                        let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask
                        optionsCache.Set(scriptKey, (scriptOptions, "scriptInference"))
                        return scriptOptions, "scriptInference"
        }

    member private this.PrepareCheckContext
        (path: string, text: string option, projectPath: string option, projectOptions: string list option)
        : Task<string * string * string * FSharpProjectOptions * FSharpParseFileResults * FSharpCheckFileResults option> =
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

            let hasTypeCheckInfo =
                checkedResults |> Option.map _.HasFullTypeCheckInfo |> Option.defaultValue false

            let status = if checkedResults.IsSome then "succeeded" else "aborted"

            return
                jobj
                    [ "status", jstr status
                      "file", jstr path
                      "optionsSource", jstr optionsSource
                      "projectFileName", jstr projectOptions.ProjectFileName
                      "projectSourceFiles", JsonArray(projectOptions.SourceFiles |> Array.map jstr) :> JsonNode
                      "parseHadErrors", jbool parseResults.ParseHadErrors
                      "hasFullTypeCheckInfo", jbool hasTypeCheckInfo
                      "parseDiagnostics", JsonArray(parseDiagnostics) :> JsonNode
                      "checkDiagnostics", JsonArray(checkDiagnostics) :> JsonNode ]
                :> JsonNode
        }

    member this.CompileProject(args: FSharpCompileArgs) : Task<JsonNode> =
        task {
            let projectPath = normalizePath args.projectPath

            if not (String.Equals(Path.GetExtension(projectPath), ".fsproj", StringComparison.OrdinalIgnoreCase)) then
                invalidArg (nameof args.projectPath) $"projectPath must point to an .fsproj file: %s{projectPath}"

            if not (File.Exists projectPath) then
                invalidArg (nameof args.projectPath) $"Project file does not exist: %s{projectPath}"

            let timeoutMs = args.timeoutMs |> Option.defaultValue 60000
            let! projectOptions, optionsSource = this.ResolveFsprojOptions(projectPath)
            let cacheKey = makeResolvedProjectCacheKey projectOptions

            use timeoutCts = new CancellationTokenSource(timeoutMs)

            let parseAndCheckProject () =
                Async.StartAsTask(checker.ParseAndCheckProject(projectOptions), cancellationToken = timeoutCts.Token)

            try
                let! projectResults, cached =
                    task {
                        match projectResultsCache.TryGet(cacheKey) with
                        | Some existing -> return existing, true
                        | None ->
                            let! results = parseAndCheckProject ()
                            projectResultsCache.Set(cacheKey, results)
                            return results, false
                    }

                let diagnostics = projectResults.Diagnostics
                let errorCount, warningCount = countDiagnosticsBySeverity diagnostics
                let status = if errorCount = 0 then "succeeded" else "failed"

                return
                    jobj
                        [ "status", jstr status
                          "backend", jstr "fcs-parse-and-check-project"
                          "projectPath", jstr projectPath
                          "projectFileName", jstr projectOptions.ProjectFileName
                          "optionsSource", jstr optionsSource
                          "cached", jbool cached
                          "exitCode", null
                          "diagnosticsCount", jint diagnostics.Length
                          "errorCount", jint errorCount
                          "warningCount", jint warningCount
                          "sourceFileCount", jint projectOptions.SourceFiles.Length
                          "diagnostics", JsonArray(diagnostics |> Array.map diagnosticToJson) :> JsonNode
                          "notes",
                          JsonArray(
                              [| jstr
                                     "This is an FCS project parse+typecheck, not a dotnet build/MSBuild emit/test run." |]
                          )
                          :> JsonNode ]
                    :> JsonNode
            with
            | :? OperationCanceledException
            | :? TaskCanceledException ->
                return
                    jobj
                        [ "status", jstr "timeout"
                          "backend", jstr "fcs-parse-and-check-project"
                          "projectPath", jstr projectPath
                          "projectFileName", jstr projectOptions.ProjectFileName
                          "optionsSource", jstr optionsSource
                          "timeoutMs", jint timeoutMs
                          "message", jstr $"FCS ParseAndCheckProject timed out after %d{timeoutMs}ms." ]
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

    member this.FileOutline(args: FcsFileOutlineArgs) : Task<JsonNode> =
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
                          "message", jstr "Type checking was aborted. Outline is unavailable."
                          "parseDiagnostics",
                          JsonArray(parseResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode ]
                    :> JsonNode
            | Some checkResults ->
                let includeLocal = args.includeLocal |> Option.defaultValue false
                let includePrivate = args.includePrivate |> Option.defaultValue true
                let maxResults = args.maxResults |> Option.defaultValue 200

                let entries =
                    checkResults.GetAllUsesOfAllSymbolsInFile()
                    |> Seq.filter _.IsFromDefinition
                    |> Seq.filter (fun symbolUse -> includeLocal || not (isNoisyLocalSymbol symbolUse))
                    |> Seq.distinctBy (fun symbolUse ->
                        symbolUse.Symbol.FullName,
                        symbolUse.Range.StartLine,
                        symbolUse.Range.StartColumn,
                        symbolUse.Range.EndLine,
                        symbolUse.Range.EndColumn)
                    |> Seq.sortBy (fun symbolUse -> symbolUse.Range.StartLine, symbolUse.Range.StartColumn)
                    |> Seq.truncate maxResults
                    |> Seq.map (fun symbolUse ->
                        jobj
                            [ "name", jstr symbolUse.Symbol.DisplayName
                              "fullName", jstrOrNull symbolUse.Symbol.FullName
                              "kind", jstr (symbolKind symbolUse.Symbol)
                              "accessibility", symbolAccessibility symbolUse.Symbol
                              "range", rangeToJson symbolUse.Range
                              "signature", jstr (symbolTypeString symbolUse.Symbol)
                              "declarationRange", tryDeclarationRange symbolUse.Symbol ]
                        :> JsonNode)
                    |> Seq.toArray

                return
                    jobj
                        [ "status", jstr "succeeded"
                          "file", jstr path
                          "optionsSource", jstr optionsSource
                          "includePrivate", jbool includePrivate
                          "includeLocal", jbool includeLocal
                          "count", jint entries.Length
                          "entries", JsonArray(entries) :> JsonNode
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

            // Use the resolved project as the cache key. When projectPath is omitted,
            // different files may auto-discover different projects.
            let cacheKey = makeResolvedProjectCacheKey projectOptions

            let! projectResults, cached =
                task {
                    match projectResultsCache.TryGet(cacheKey) with
                    | Some existing -> return existing, true
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
                |> Seq.sortBy (fun symbolUse ->
                    symbolUse.FileName, symbolUse.Range.StartLine, symbolUse.Range.StartColumn)
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

    member this.FindSymbol(args: FcsFindSymbolArgs) : Task<JsonNode> =
        task {
            let query = args.symbolQuery.Trim()

            if String.IsNullOrWhiteSpace(query) then
                invalidArg (nameof args.symbolQuery) "symbolQuery must be non-empty."

            let! _, _, optionsSource, projectOptions, _, _ =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            let cacheKey = makeResolvedProjectCacheKey projectOptions

            let! projectResults, cached =
                task {
                    match projectResultsCache.TryGet(cacheKey) with
                    | Some existing -> return existing, true
                    | None ->
                        let! results = checker.ParseAndCheckProject(projectOptions) |> asTask
                        projectResultsCache.Set(cacheKey, results)
                        return results, false
                }

            let exact = args.exact |> Option.defaultValue false
            let maxResults = args.maxResults |> Option.defaultValue 500
            let contextLines = args.contextLines |> Option.defaultValue 1
            let includeDeclaration = args.includeDeclaration |> Option.defaultValue true

            let matchedUses =
                projectResults.GetAllUsesOfAllSymbols()
                |> Seq.filter (fun symbolUse -> symbolMatches query exact symbolUse.Symbol)
                |> Seq.filter (fun symbolUse -> includeDeclaration || not symbolUse.IsFromDefinition)
                |> Seq.sortBy (fun symbolUse ->
                    symbolUse.Symbol.FullName, symbolUse.FileName, symbolUse.Range.StartLine, symbolUse.Range.StartColumn)
                |> Seq.truncate maxResults
                |> Seq.toArray

            let useToJson (symbolUse: FSharpSymbolUse) =
                let context = lineContextToJson contextLines symbolUse.FileName symbolUse.Range.StartLine

                jobj
                    [ "file", jstr (normalizePath symbolUse.FileName)
                      "range", rangeToJson symbolUse.Range
                      "isDefinition", jbool symbolUse.IsFromDefinition
                      "isReference", jbool symbolUse.IsFromUse
                      "lineText", context["lineText"].DeepClone()
                      "before", context["before"].DeepClone()
                      "after", context["after"].DeepClone() ]
                :> JsonNode

            let groups =
                matchedUses
                |> Array.groupBy (fun symbolUse ->
                    let symbol = symbolUse.Symbol
                    let declaration =
                        symbol.DeclarationLocation
                        |> Option.map (fun range -> $"{normalizePath range.FileName}:{range.StartLine}:{range.StartColumn}")
                        |> Option.defaultValue ""

                    symbol.FullName, symbol.DisplayName, declaration)
                |> Array.map (fun ((_, _, _), uses) ->
                    let symbol = uses[0].Symbol

                    let definitions =
                        uses
                        |> Array.filter _.IsFromDefinition
                        |> Array.map useToJson

                    let references =
                        uses
                        |> Array.filter (fun symbolUse -> not symbolUse.IsFromDefinition)
                        |> Array.map useToJson

                    jobj
                        [ "symbol", compactSymbolToJson symbol
                          "definitionCount", jint definitions.Length
                          "referenceCount", jint references.Length
                          "definitions", JsonArray(definitions) :> JsonNode
                          "references", JsonArray(references) :> JsonNode ]
                    :> JsonNode)

            return
                jobj
                    [ "status", jstr "succeeded"
                      "optionsSource", jstr optionsSource
                      "projectFileName", jstr projectOptions.ProjectFileName
                      "query", jstr query
                      "exact", jbool exact
                      "cached", jbool cached
                      "matchedUseCount", jint matchedUses.Length
                      "symbolCount", jint groups.Length
                      "symbols", JsonArray(groups) :> JsonNode
                      "projectDiagnostics",
                      JsonArray(projectResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode ]
                :> JsonNode
        }

    member this.TypeAtPosition(args: FcsTypeAtPositionArgs) : Task<JsonNode> =
        task {
            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None -> return jobj [ "status", jstr "aborted"; "message", jstr "Type checking was aborted." ] :> JsonNode
            | Some checkResults ->
                // FCS uses 1-based lines; assume input is 0-based (LSP convention)
                let fcsLine = args.line + 1
                let fcsCol = args.character

                // Get the line text for FCS APIs
                let lines = source.Split('\n')

                let lineText =
                    if fcsLine - 1 < lines.Length then
                        lines[fcsLine - 1].TrimEnd('\r')
                    else
                        ""

                let symbolUse = checkResults.GetSymbolUseAtLocation(fcsLine, fcsCol, lineText, [])

                let toolTip =
                    checkResults.GetToolTip(
                        fcsLine,
                        fcsCol,
                        lineText,
                        [],
                        FSharp.Compiler.Tokenization.FSharpTokenTag.IDENT
                    )

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
                                    | FSharpXmlDoc.FromXmlText xmlText -> Some(xmlText.GetXmlText())
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

    member this.SymbolAtWord(args: FcsSymbolAtWordArgs) : Task<JsonNode> =
        task {
            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None -> return jobj [ "status", jstr "aborted"; "message", jstr "Type checking was aborted." ] :> JsonNode
            | Some checkResults ->
                let lines = sourceLines source

                if args.line < 0 || args.line >= lines.Length then
                    return
                        jobj
                            [ "status", jstr "invalid_line"
                              "file", jstr path
                              "line", jint args.line
                              "lineCount", jint lines.Length ]
                        :> JsonNode
                else
                    let lineText = lines[args.line]
                    let candidates = wordSpans args.word lineText

                    if candidates.Length = 0 then
                        return
                            jobj
                                [ "status", jstr "no_candidate"
                                  "file", jstr path
                                  "line", jint args.line
                                  "word", args.word |> Option.map jstr |> Option.defaultValue null
                                  "lineText", jstr lineText ]
                            :> JsonNode
                    else
                        let occurrence = args.occurrence |> Option.defaultValue -1

                        if occurrence < 0 && candidates.Length > 1 then
                            let candidateJson =
                                candidates
                                |> Array.mapi (fun index (startColumn, endColumn, text) ->
                                    candidateToJson index args.line startColumn endColumn text)

                            return
                                jobj
                                    [ "status", jstr "ambiguous_word"
                                      "file", jstr path
                                      "line", jint args.line
                                      "word", args.word |> Option.map jstr |> Option.defaultValue null
                                      "lineText", jstr lineText
                                      "candidates", JsonArray(candidateJson) :> JsonNode ]
                                :> JsonNode
                        elif occurrence >= candidates.Length then
                            return
                                jobj
                                    [ "status", jstr "invalid_occurrence"
                                      "file", jstr path
                                      "line", jint args.line
                                      "occurrence", jint occurrence
                                      "candidateCount", jint candidates.Length ]
                                :> JsonNode
                        else
                            let candidateIndex = if occurrence < 0 then 0 else occurrence
                            let startColumn, endColumn, text = candidates[candidateIndex]
                            let fcsLine = args.line + 1
                            let columnsToTry = [| endColumn; startColumn + 1; startColumn |] |> Array.distinct

                            let symbolUse =
                                columnsToTry
                                |> Array.tryPick (fun column ->
                                    checkResults.GetSymbolUseAtLocation(fcsLine, column, lineText, [ text ]))

                            let toolTip =
                                checkResults.GetToolTip(
                                    fcsLine,
                                    endColumn,
                                    lineText,
                                    [ text ],
                                    FSharp.Compiler.Tokenization.FSharpTokenTag.IDENT
                                )

                            let typeString =
                                match toolTip with
                                | ToolTipText elements ->
                                    elements
                                    |> List.choose (fun element ->
                                        match element with
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

                            let documentation =
                                if args.includeDocumentation |> Option.defaultValue false then
                                    match toolTip with
                                    | ToolTipText elements ->
                                        elements
                                        |> List.choose (fun element ->
                                            match element with
                                            | ToolTipElement.Group items ->
                                                items
                                                |> List.choose (fun item ->
                                                    match item.XmlDoc with
                                                    | FSharpXmlDoc.FromXmlText xmlText -> Some(xmlText.GetXmlText())
                                                    | _ -> None)
                                                |> Some
                                            | _ -> None)
                                        |> List.collect id
                                        |> String.concat "\n"
                                        |> jstr
                                else
                                    null

                            match symbolUse with
                            | None ->
                                return
                                    jobj
                                        [ "status", jstr "no_symbol"
                                          "file", jstr path
                                          "line", jint args.line
                                          "lineText", jstr lineText
                                          "candidate", candidateToJson candidateIndex args.line startColumn endColumn text
                                          "typeString", jstr typeString ]
                                    :> JsonNode
                            | Some symbolUse ->
                                return
                                    jobj
                                        [ "status", jstr "ok"
                                          "file", jstr path
                                          "line", jint args.line
                                          "lineText", jstr lineText
                                          "optionsSource", jstr optionsSource
                                          "candidate", candidateToJson candidateIndex args.line startColumn endColumn text
                                          "symbolName", jstr symbolUse.Symbol.DisplayName
                                          "fullName", jstrOrNull symbolUse.Symbol.FullName
                                          "kind", jstr (symbolKind symbolUse.Symbol)
                                          "typeString", jstr (if String.IsNullOrWhiteSpace(typeString) then symbolTypeString symbolUse.Symbol else typeString)
                                          "definitionRange", tryDeclarationRange symbolUse.Symbol
                                          "documentation", documentation ]
                                    :> JsonNode
        }

    member this.ProjectOutline(args: FcsProjectOutlineArgs) : Task<JsonNode> =
        task {
            let projectPath = normalizePath args.projectPath

            if not (File.Exists projectPath) then
                invalidArg (nameof args.projectPath) $"Project file does not exist: %s{projectPath}"

            // ── Decode cursor (fail fast on malformed input) ────────────────────
            let pageOffset =
                match args.cursor with
                | None -> 0
                | Some cursorStr ->
                    match Cursor.tryDecode cursorStr with
                    | Ok payload -> payload.offset
                    | Error reason ->
                        invalidArg (nameof args.cursor) $"Invalid cursor: %s{reason}"

            let workspaceRoot =
                args.workspacePath
                |> Option.map Path.GetFullPath
                |> Option.defaultValue (Path.GetDirectoryName(projectPath))

            let doc =
                match tryReadProject projectPath with
                | Ok doc -> doc
                | Error reason -> raise (InvalidOperationException($"Project file cannot be read: %s{reason}"))

            // ── Conservative defaults (issue #78) ───────────────────────────────
            // maxFiles=50 and maxResultsPerFile=30 keep responses within typical
            // 25k–50k token context windows on projects up to ~50 files / 10k LOC.
            // Callers that previously relied on the effectively-unlimited behaviour
            // must now opt in via explicit larger values or cursor pagination.
            let pageSize = args.maxFiles |> Option.defaultValue 50
            let maxResultsPerFile = args.maxResultsPerFile |> Option.defaultValue 30
            let summaryOnly = args.summaryOnly |> Option.defaultValue true

            // ── Build regex / substring matchers ───────────────────────────────
            let filterRegex =
                match args.filter with
                | None -> None
                | Some pattern ->
                    try
                        Some(System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    with ex ->
                        invalidArg (nameof args.filter) $"Invalid filter regex: %s{ex.Message}"

            let nameContains =
                args.nameContains
                |> Option.filter (fun lst -> not lst.IsEmpty)

            // Returns true when the entry name / signature passes the filter.
            let entryMatchesFilter (name: string) (signature: string) =
                let matchesRegex =
                    match filterRegex with
                    | None -> true
                    | Some rx -> rx.IsMatch(name) || rx.IsMatch(signature)

                let matchesNameContains =
                    match nameContains with
                    | None -> true
                    | Some fragments ->
                        fragments
                        |> List.exists (fun fragment ->
                            name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                            || signature.Contains(fragment, StringComparison.OrdinalIgnoreCase))

                matchesRegex && matchesNameContains

            // ── Enumerate all project files (no MaxFiles cap here — we page manually) ─
            let filterOptions =
                { defaultFilterOptions Outline with
                    IncludeGenerated = args.includeGeneratedFiles |> Option.defaultValue false
                    IncludeTests = args.includeTests |> Option.defaultValue false
                    MaxFiles = None }

            let files = compileFiles projectPath doc

            // Sort deterministically by path so cursor offsets are stable.
            let allFiles =
                filterProjectFiles workspaceRoot filterOptions files
                |> fun result ->
                    { result with
                        Included = result.Included |> List.sortBy (fun f -> f.Path) }

            let totalFileCount = allFiles.Included.Length

            // ── Apply cursor offset then take pageSize ──────────────────────────
            let pageFiles =
                allFiles.Included
                |> List.skip (min pageOffset totalFileCount)
                |> List.truncate pageSize

            let fileEntries = ResizeArray<JsonNode>()

            for file in pageFiles do
                let! outline =
                    this.FileOutline(
                        { path = file.Path
                          text = None
                          projectPath = Some projectPath
                          projectOptions = None
                          includePrivate = args.includePrivate
                          includeLocal = Some false
                          maxResults = Some maxResultsPerFile }
                    )

                // Pull raw entries array (may be null if outline aborted).
                let rawEntries: JsonNode array =
                    match outline["entries"] with
                    | null -> [||]
                    | entries ->
                        match entries with
                        | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toArray
                        | _ -> [||]

                // Apply filter BEFORE truncation, then apply per-file cap.
                let filteredEntries =
                    if filterRegex.IsNone && nameContains.IsNone then
                        rawEntries |> Array.truncate maxResultsPerFile
                    else
                        rawEntries
                        |> Array.filter (fun entry ->
                            let name =
                                match entry["name"] with
                                | null -> ""
                                | n -> n.GetValue<string>()

                            let signature =
                                match entry["signature"] with
                                | null -> ""
                                | s -> s.GetValue<string>()

                            entryMatchesFilter name signature)
                        |> Array.truncate maxResultsPerFile

                // summaryOnly: strip per-member signature detail, keep headers & counts.
                let outlineEntries: JsonNode =
                    if summaryOnly then
                        // Group by "kind" (module/record/union/class/interface) for a
                        // compact header + member-count summary.
                        let containerKinds =
                            [| "module"; "record"; "union"; "class"; "interface"; "enum"; "delegate"; "namespace" |]

                        let topLevel =
                            filteredEntries
                            |> Array.filter (fun entry ->
                                match entry["kind"] with
                                | null -> false
                                | k -> containerKinds |> Array.contains (k.GetValue<string>()))

                        let memberCount = filteredEntries.Length - topLevel.Length

                        let summaryNodes =
                            topLevel
                            |> Array.map (fun entry ->
                                jobj
                                    [ "name", entry["name"].DeepClone()
                                      "kind", entry["kind"].DeepClone()
                                      "fullName",
                                      (match entry["fullName"] with
                                       | null -> null
                                       | fn -> fn.DeepClone())
                                      "range",
                                      (match entry["range"] with
                                       | null -> null
                                       | r -> r.DeepClone()) ]
                                :> JsonNode)

                        // Append a synthetic _members_count sentinel for LLM context.
                        let withCount =
                            Array.append
                                summaryNodes
                                [| jobj [ "kind", jstr "_summary"; "memberCount", jint memberCount ] :> JsonNode |]

                        JsonArray(withCount) :> JsonNode
                    else
                        JsonArray(filteredEntries) :> JsonNode

                fileEntries.Add(
                    jobj
                        [ "file", jstr file.Path
                          "kind", jstr (if file.IsSignature then "signature" else "implementation")
                          "outlineStatus", outline["status"].DeepClone()
                          "entries", outlineEntries
                          "count",
                          (match outline["count"] with
                           | null -> jint 0
                           | count -> count.DeepClone()) ]
                    :> JsonNode
                )

            // ── Pagination envelope ─────────────────────────────────────────────
            let paginationFields =
                Cursor.paginationFields totalFileCount pageOffset pageSize pageFiles.Length

            let baseFields =
                [ "status", jstr "ok"
                  "projectPath", jstr projectPath
                  "workspaceRoot", jstr workspaceRoot
                  "summaryOnly", jbool summaryOnly
                  "filterSummary", filterSummaryToJson allFiles :> JsonNode
                  "files", JsonArray(fileEntries.ToArray()) :> JsonNode ]

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    member _.ClearCaches() =
        optionsCache.Clear()
        projectResultsCache.Clear()

    member this.ProbeProjectOptions(fsprojPath: string) : Task<Result<string, string>> =
        task {
            try
                let! result = this.LoadProjectOptionsFromFsproj(fsprojPath)

                return
                    match result with
                    | Some _ -> Ok "ionide-proj-info"
                    | None -> Error "Ionide.ProjInfo could not load project options."
            with ex ->
                return Error ex.Message
        }

    member private _.BuildSignatureHelpResult
        (
            path: string,
            source: string,
            optionsSource: string,
            args: FcsSignatureHelpArgs,
            checkedResults: FSharpCheckFileResults option
        ) : JsonNode =
        match checkedResults with
        | None -> jobj [ "status", jstr "aborted"; "message", jstr "Type checking was aborted." ] :> JsonNode
        | Some checkResults ->
            // FCS uses 1-based lines; assume input is 0-based (LSP convention)
            let fcsLine = args.line + 1
            let fcsCol = args.character

            let lines = source.Split('\n')

            let lineText =
                if fcsLine - 1 < lines.Length then
                    lines[fcsLine - 1].TrimEnd('\r')
                else
                    ""

            let methodsOpt = checkResults.GetMethodsAsSymbols(fcsLine, fcsCol, lineText, [])

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
                                          "type", jstr (typeName p.Type) ]
                                    :> JsonNode)
                                |> Seq.toArray

                            let returnType =
                                try
                                    typeName m.ReturnParameter.Type
                                with ex ->
                                    Console.Error.WriteLine(
                                        $"[fcs_signature_help] ReturnParameter error: %s{ex.Message}"
                                    )

                                    ""

                            let signature =
                                let paramStr =
                                    parameters
                                    |> Array.map (fun p ->
                                        let pName = p["name"].GetValue<string>()
                                        let pType = p["type"].GetValue<string>()
                                        $"%s{pName}: %s{pType}")
                                    |> String.concat ", "

                                $"%s{m.DisplayName}(%s{paramStr}) -> %s{returnType}"

                            Some(
                                jobj
                                    [ "signature", jstr signature
                                      "parameters", JsonArray(parameters) :> JsonNode
                                      "returnType", jstr returnType ]
                                :> JsonNode
                            )
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
