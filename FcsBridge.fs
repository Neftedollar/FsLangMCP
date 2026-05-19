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
    // Default FCS projectCacheSize is 3. We capture it so RuntimeStatus can report it.
    let defaultProjectCacheSize = 3
    // Single source of truth for checker flags used in both Create and CheckerConfig.
    let keepAssemblyContents = true
    let keepAllBackgroundResolutions = true
    let keepAllBackgroundSymbolUses = true

    let checker =
        FSharpChecker.Create(
            projectCacheSize = defaultProjectCacheSize,
            keepAssemblyContents = keepAssemblyContents,
            keepAllBackgroundResolutions = keepAllBackgroundResolutions,
            keepAllBackgroundSymbolUses = keepAllBackgroundSymbolUses
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

    let accessibilityString (symbol: FSharpSymbol) : string =
        try
            let acc = symbol.Accessibility

            if acc.IsPrivate then "private"
            elif acc.IsInternal then "internal"
            elif acc.IsPublic then "public"
            else "unknown"
        with _ ->
            // FCS can throw on synthetic symbols; treat as unknown rather than crash.
            "unknown"

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
              "accessibility", jstr (accessibilityString symbol)
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

    // ─── Helpers for referenced-assembly traversal (F-3) ─────────────────────────

    let entityKindString (entity: FSharpEntity) : string =
        try
            if entity.IsNamespace then "namespace"
            elif entity.IsFSharpModule then "module"
            elif entity.IsInterface then "interface"
            elif entity.IsFSharpRecord then "record"
            elif entity.IsFSharpUnion then "union"
            elif entity.IsEnum then "enum"
            elif entity.IsDelegate then "delegate"
            elif entity.IsValueType then "struct"
            elif entity.IsClass then "class"
            elif entity.IsArrayType then "array"
            elif entity.IsFSharpAbbreviation then "abbreviation"
            else "type"
        with _ ->
            "type"

    let rec walkEntities (entity: FSharpEntity) : seq<FSharpEntity> =
        seq {
            yield entity

            let nested =
                try
                    Some entity.NestedEntities
                with _ ->
                    None

            match nested with
            | Some nested ->
                for child in nested do
                    yield! walkEntities child
            | None -> ()
        }

    let allEntitiesFromAssembly (asm: FSharpAssembly) : seq<FSharpEntity> =
        try
            asm.Contents.Entities |> Seq.collect walkEntities
        with _ ->
            Seq.empty

    let assemblyMatchesPackageId (asm: FSharpAssembly) (packageId: string) =
        // Exact SimpleName match only (case-insensitive). We intentionally reject prefix
        // matching in both directions: "System" must NOT match every System.* assembly,
        // and "Newtonsoft.Json.Schema" must NOT silently fall back to "Newtonsoft.Json".
        // If a NuGet package ships multiple assemblies, callers should query each
        // assembly by its actual SimpleName.
        let pkgLower = packageId.ToLowerInvariant()

        try
            let simple =
                asm.SimpleName
                |> Option.ofObj
                |> Option.map (fun s -> s.ToLowerInvariant())
                |> Option.defaultValue ""

            simple = pkgLower
        with _ ->
            false

    let isObsoleteEntity (entity: FSharpEntity) =
        try
            entity.Attributes
            |> Seq.exists (fun a ->
                try
                    let typeName = a.AttributeType.FullName

                    not (isNull typeName)
                    && (typeName = "System.ObsoleteAttribute"
                        || typeName.EndsWith(".ObsoleteAttribute", StringComparison.Ordinal))
                with _ ->
                    false)
        with _ ->
            false

    let entityAccessibilityString (entity: FSharpEntity) : string =
        try
            let acc = entity.Accessibility

            if acc.IsPrivate then "private"
            elif acc.IsInternal then "internal"
            elif acc.IsPublic then "public"
            else "unknown"
        with _ ->
            "unknown"

    let referencedEntityToJson (asmName: string) (entity: FSharpEntity) : JsonNode =
        let displayName =
            try
                entity.DisplayName
            with _ ->
                try
                    entity.LogicalName
                with _ ->
                    "<unknown>"

        let fullName =
            try
                if isNull entity.FullName then null
                else jstr entity.FullName
            with _ ->
                null

        jobj
            [ "displayName", jstr displayName
              "fullName", fullName
              "assembly", jstr asmName
              "kind", jstr (entityKindString entity)
              "accessibility", jstr (entityAccessibilityString entity)
              "isObsolete", jbool (isObsoleteEntity entity) ]
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

    // Validate that a 'path' argument points to an existing source file, not a directory.
    // When the caller supplies a non-empty 'text' buffer the file does not need to
    // exist on disk — we still reject directories (the actual papercut from #77).
    // Returns Some errorNode when validation fails; None when the path is acceptable.
    let validateSourcePath (toolName: string) (text: string option) (path: string) : JsonNode option =
        let fullPath = normalizePath path
        // The tool contract says supplying `text` carries unsaved buffer content; a new
        // empty file is a valid editor state. Treat any Some _ — including Some "" — as
        // a provided buffer; only None means "no buffer, must exist on disk".
        let hasText = text |> Option.isSome

        if Directory.Exists(fullPath) then
            Some(
                jobj
                    [ "status", jstr "error"
                      "errorKind", jstr "InvalidArgument"
                      "message",
                      jstr
                          $"%s{toolName} expects 'path' to be a source file (.fs/.fsi), not a directory. To search project-wide, pass any source file in the project as 'path' and the .fsproj as 'projectPath'." ]
                :> JsonNode
            )
        elif not hasText && not (File.Exists(fullPath)) then
            Some(
                jobj
                    [ "status", jstr "error"
                      "errorKind", jstr "InvalidArgument"
                      "message", jstr $"%s{toolName}: path does not exist or is not readable: %s{fullPath}" ]
                :> JsonNode
            )
        else
            None

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

    /// Invalidates FCS caches for this file's project and runs a fresh parse+check.
    /// Bypasses the cached projectResults that ProjectSymbolUses/FindSymbol may have
    /// populated, so changes made since the last cache write are visible.
    /// Returns a focused diagnostics view (parse + check), without the project-options
    /// metadata included in fcs_parse_and_check_file.
    member this.CheckFile(args: FcsParseAndCheckArgs) : Task<JsonNode> =
        task {
            match validateSourcePath "fcs_check_file" args.text args.path with
            | Some err -> return err
            | None ->

            // Resolve options FIRST so we can do surgical (per-project) cache
            // invalidation instead of nuking caches for every loaded project.
            let! path, _, optionsSource, projectOptions, parseResults, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            // Drop the cached project-options + project-results entries for THIS
            // project only — leaves other projects' warm caches alone.
            let fsprojKey = projectOptions.ProjectFileName
            let projectResultsKey = makeResolvedProjectCacheKey projectOptions
            optionsCache.TryRemove(fsprojKey) |> ignore
            projectResultsCache.TryRemove(projectResultsKey) |> ignore

            // Ask FCS to drop its incremental-build cache for this project so
            // transitively-checked files are re-read from disk. Per-project, not
            // global — keeps other projects' caches warm.
            checker.InvalidateConfiguration(projectOptions)

            // Re-run parse+check now that caches are invalidated. The PrepareCheckContext
            // call above gave us a baseline; if the user just invalidated mid-edit, we
            // want the post-invalidation result for diagnostics.
            let! _, _, _, _, parseResults, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            let parseDiagnostics = parseResults.Diagnostics |> Array.map diagnosticToJson

            let checkDiagnostics =
                checkedResults
                |> Option.map (fun r -> r.Diagnostics |> Array.map diagnosticToJson)
                |> Option.defaultValue [||]

            let hasTypeCheckInfo =
                checkedResults |> Option.map _.HasFullTypeCheckInfo |> Option.defaultValue false

            let status =
                if checkedResults.IsSome then "succeeded" else "aborted"

            let errorCount =
                Array.append parseDiagnostics checkDiagnostics
                |> Array.filter (fun d ->
                    match d with
                    | :? JsonObject as obj ->
                        match obj["severity"] with
                        | :? JsonValue as sev ->
                            let mutable code = 0
                            sev.TryGetValue(&code) && code = 1
                        | _ -> false
                    | _ -> false)
                |> Array.length

            return
                jobj
                    [ "status", jstr status
                      "file", jstr path
                      "optionsSource", jstr optionsSource
                      "projectFileName", jstr projectOptions.ProjectFileName
                      "parseHadErrors", jbool parseResults.ParseHadErrors
                      "hasFullTypeCheckInfo", jbool hasTypeCheckInfo
                      "errorCount", jint errorCount
                      "totalDiagnostics", jint (parseDiagnostics.Length + checkDiagnostics.Length)
                      "parseDiagnostics", JsonArray(parseDiagnostics) :> JsonNode
                      "checkDiagnostics", JsonArray(checkDiagnostics) :> JsonNode ]
                :> JsonNode
        }

    /// Compile an arbitrary F# snippet against the loaded project's references.
    /// Writes the content to a temp file, splices it into the project's SourceFiles
    /// without mutating the cached project options, and returns FCS diagnostics.
    /// Useful for "does F# 9 accept this signature?" / "does this .fsi sketch reference
    /// only existing types?" probes without spinning up dotnet build.
    member this.ValidateSnippet(args: FcsValidateSnippetArgs) : Task<JsonNode> =
        task {
            if isNull args.content then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr "content is required" ]
                    :> JsonNode
            else

            let mode =
                args.mode
                |> Option.map (fun m -> m.ToLowerInvariant())
                |> Option.defaultValue "fs"

            if mode <> "fs" && mode <> "fsi" then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr $"mode must be 'fs' or 'fsi' (got '{mode}')" ]
                    :> JsonNode
            else

            match args.projectPath with
            | None ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "projectPath is required (or call set_project first to set the active project)" ]
                    :> JsonNode
            | Some fsproj ->
                let! options, optionsSource = this.ResolveFsprojOptions(fsproj)

                let ext = if mode = "fsi" then ".fsi" else ".fs"

                let snippetFile =
                    Path.Combine(Path.GetTempPath(), $"fslangmcp_snippet_{Guid.NewGuid():N}{ext}")

                try
                    File.WriteAllText(snippetFile, args.content)

                    // Splice snippet at the end of SourceFiles WITHOUT writing back to cache.
                    // The original cached options stay unchanged for future calls.
                    let modifiedOptions =
                        { options with
                            SourceFiles = Array.append options.SourceFiles [| snippetFile |] }

                    let sourceText = SourceText.ofString args.content

                    let! parseResults, checkAnswer =
                        checker.ParseAndCheckFileInProject(snippetFile, 0, sourceText, modifiedOptions)
                        |> asTask

                    let parseDiagnostics = parseResults.Diagnostics

                    let checkDiagnostics, checkSucceeded =
                        match checkAnswer with
                        | FSharpCheckFileAnswer.Succeeded r -> r.Diagnostics, true
                        | FSharpCheckFileAnswer.Aborted -> [||], false

                    let allDiagnostics = Array.append parseDiagnostics checkDiagnostics

                    let errorCount =
                        allDiagnostics
                        |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                        |> Array.length

                    let warningCount =
                        allDiagnostics
                        |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Warning)
                        |> Array.length

                    return
                        jobj
                            [ "status", jstr (if checkSucceeded then "succeeded" else "aborted")
                              "mode", jstr mode
                              "projectFileName", jstr options.ProjectFileName
                              "optionsSource", jstr optionsSource
                              "parseHadErrors", jbool parseResults.ParseHadErrors
                              "errorCount", jint errorCount
                              "warningCount", jint warningCount
                              "totalDiagnostics", jint allDiagnostics.Length
                              "diagnostics", JsonArray(allDiagnostics |> Array.map diagnosticToJson) :> JsonNode ]
                        :> JsonNode
                finally
                    try
                        if File.Exists snippetFile then
                            File.Delete snippetFile
                    with _ ->
                        ()
        }

    member this.ParseAndCheckFile(args: FcsParseAndCheckArgs) : Task<JsonNode> =
        task {
            match validateSourcePath "fcs_parse_and_check_file" args.text args.path with
            | Some err -> return err
            | None ->

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
            let projectPath =
                match args.projectPath with
                | Some p when not (String.IsNullOrWhiteSpace p) -> normalizePath p
                | _ ->
                    invalidArg
                        (nameof args.projectPath)
                        "projectPath is required. Either pass it explicitly or call set_project first to establish a default."

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
            match validateSourcePath "fcs_file_symbols" args.text args.path with
            | Some err -> return err
            | None ->

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
                        let r = symbolUse.Range
                        symbolUse.Symbol.FullName,
                        r.StartLine,
                        r.StartColumn,
                        r.EndLine,
                        r.EndColumn)
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
            match validateSourcePath "fcs_file_outline" args.text args.path with
            | Some err -> return err
            | None ->

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
                        let r = symbolUse.Range
                        symbolUse.Symbol.FullName,
                        r.StartLine,
                        r.StartColumn,
                        r.EndLine,
                        r.EndColumn)
                    |> Seq.sortBy (fun symbolUse ->
                        let r = symbolUse.Range
                        r.StartLine, r.StartColumn)
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

            match validateSourcePath "fcs_project_symbol_uses" args.text args.path with
            | Some err -> return err
            | None ->

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
            let pageSize = args.maxResults |> Option.defaultValue 500

            // ── Decode cursor (fail fast on malformed input) ───────────────────
            let pageOffset =
                match args.cursor with
                | None -> 0
                | Some cursorStr ->
                    match Cursor.tryDecode cursorStr with
                    | Ok payload -> payload.offset
                    | Error reason ->
                        invalidArg (nameof args.cursor) $"Invalid cursor: %s{reason}"

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

            // Sort once, deterministically, so cursor offsets are stable across pages.
            let sortedMatches =
                allUses
                |> Array.filter (fun symbolUse -> symbolMatches symbolUse.Symbol)
                |> Array.sortBy (fun symbolUse ->
                    let r = symbolUse.Range
                    symbolUse.FileName, r.StartLine, r.StartColumn)

            let totalMatched = sortedMatches.Length

            let pageUses =
                sortedMatches
                |> Array.skip (min pageOffset totalMatched)
                |> Array.truncate pageSize

            let pageNodes = pageUses |> Array.map symbolUseToJson

            let paginationFields =
                Cursor.paginationFields "uses" totalMatched pageOffset pageSize pageUses.Length

            let baseFields =
                [ "status", jstr "succeeded"
                  "optionsSource", jstr optionsSource
                  "projectFileName", jstr projectOptions.ProjectFileName
                  "query", jstr query
                  "exact", jbool exact
                  "cached", jbool cached
                  "totalProjectSymbolUses", jint allUses.Length
                  "matchedCount", jint totalMatched
                  "uses", JsonArray(pageNodes) :> JsonNode
                  "projectDiagnostics",
                  JsonArray(projectResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode ]

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    member this.FindMemberUsages(args: FcsFindMemberUsagesArgs) : Task<JsonNode> =
        task {
            let typeName = args.typeName.Trim()
            let memberName = args.memberName.Trim()

            if String.IsNullOrWhiteSpace typeName then
                invalidArg (nameof args.typeName) "typeName must be non-empty."

            if String.IsNullOrWhiteSpace memberName then
                invalidArg (nameof args.memberName) "memberName must be non-empty."

            // Resolve project options either via a file context (PrepareCheckContext)
            // or directly from projectPath (.fsproj). The former gives accurate
            // single-file-aware options; the latter is enough for project-wide queries.
            let! optionsSource, projectOptions =
                task {
                    match args.path with
                    | Some path when not (String.IsNullOrWhiteSpace path) ->
                        let! _, _, src, opts, _, _ =
                            this.PrepareCheckContext(path, args.text, args.projectPath, args.projectOptions)

                        return src, opts
                    | _ ->
                        match args.projectPath with
                        | Some p when not (String.IsNullOrWhiteSpace p) ->
                            let! opts, src = this.ResolveFsprojOptions(normalizePath p)
                            return src, opts
                        | _ ->
                            return
                                invalidArg
                                    (nameof args.projectPath)
                                    "Either 'path' or 'projectPath' must be provided (or call set_project first)."
                }

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
            let pageSize = args.maxResults |> Option.defaultValue 500

            let pageOffset =
                match args.cursor with
                | None -> 0
                | Some cursorStr ->
                    match Cursor.tryDecode cursorStr with
                    | Ok payload -> payload.offset
                    | Error reason -> invalidArg (nameof args.cursor) $"Invalid cursor: %s{reason}"

            // Predicates: a symbol use qualifies if its symbol is a member of an
            // entity matching typeName, AND the symbol's own DisplayName matches
            // memberName.
            //
            // typeName matching:
            //   exact=true  → DisplayName == typeName OR FullName == typeName
            //   exact=false → DisplayName == typeName (exact, avoids `Style`
            //                 false-matching `StyleSheet`) OR FullName contains
            //                 typeName (allows namespace-qualified queries like
            //                 "MyApp.Theme.Style").
            //
            // memberName matching follows the standard exact-or-substring rule.
            let matchesText (candidate: string) (target: string) =
                if isNull candidate then
                    false
                elif exact then
                    String.Equals(candidate, target, StringComparison.Ordinal)
                else
                    candidate.Contains(target, StringComparison.OrdinalIgnoreCase)

            // FullName ends with `.typeName` at a segment boundary, or equals typeName outright.
            // Rejects `Theme.StyleSheet` for typeName="Style" while accepting `Theme.Style`.
            let fullNameEndsAtBoundary (fullName: string) =
                String.Equals(fullName, typeName, StringComparison.Ordinal)
                || (fullName.EndsWith(typeName, StringComparison.Ordinal)
                    && fullName.Length > typeName.Length
                    && fullName[fullName.Length - typeName.Length - 1] = '.')

            let matchesTypeName (entity: FSharpEntity) =
                let displayOk =
                    String.Equals(entity.DisplayName, typeName, StringComparison.Ordinal)

                let fullName = entity.FullName

                let fullOk =
                    if isNull fullName then
                        false
                    elif exact then
                        String.Equals(fullName, typeName, StringComparison.Ordinal)
                    else
                        fullNameEndsAtBoundary fullName

                displayOk || fullOk

            let memberFilter (symbolUse: FSharpSymbolUse) : bool =
                match symbolUse.Symbol with
                | :? FSharpMemberOrFunctionOrValue as m when m.IsMember ->
                    let nameOk = matchesText m.DisplayName memberName

                    let typeOk =
                        try
                            // FCS occasionally throws on synthetic / anonymous-record /
                            // closure-captured entities; treat as non-match.
                            match m.DeclaringEntity with
                            | Some e -> matchesTypeName e
                            | None -> false
                        with _ ->
                            false

                    nameOk && typeOk
                | _ -> false

            let allUses = projectResults.GetAllUsesOfAllSymbols()

            let sortedMatches =
                allUses
                |> Array.filter memberFilter
                |> Array.sortBy (fun symbolUse ->
                    let r = symbolUse.Range
                    symbolUse.FileName, r.StartLine, r.StartColumn)

            let totalMatched = sortedMatches.Length

            let pageUses =
                sortedMatches
                |> Array.skip (min pageOffset totalMatched)
                |> Array.truncate pageSize

            let pageNodes = pageUses |> Array.map symbolUseToJson

            let paginationFields =
                Cursor.paginationFields "uses" totalMatched pageOffset pageSize pageUses.Length

            let baseFields =
                [ "status", jstr "succeeded"
                  "optionsSource", jstr optionsSource
                  "projectFileName", jstr projectOptions.ProjectFileName
                  "typeName", jstr typeName
                  "memberName", jstr memberName
                  "exact", jbool exact
                  "cached", jbool cached
                  "totalProjectSymbolUses", jint allUses.Length
                  "matchedCount", jint totalMatched
                  "uses", JsonArray(pageNodes) :> JsonNode
                  "projectDiagnostics",
                  JsonArray(projectResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode ]

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    member this.FindSymbol(args: FcsFindSymbolArgs) : Task<JsonNode> =
        task {
            let query = args.symbolQuery.Trim()

            if String.IsNullOrWhiteSpace(query) then
                invalidArg (nameof args.symbolQuery) "symbolQuery must be non-empty."

            match validateSourcePath "fcs_find_symbol" args.text args.path with
            | Some err -> return err
            | None ->

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
            let pageSize = args.maxResults |> Option.defaultValue 500
            let contextLines = args.contextLines |> Option.defaultValue 1
            let includeDeclaration = args.includeDeclaration |> Option.defaultValue true

            // ── Decode cursor (fail fast on malformed input) ───────────────────
            let pageOffset =
                match args.cursor with
                | None -> 0
                | Some cursorStr ->
                    match Cursor.tryDecode cursorStr with
                    | Ok payload -> payload.offset
                    | Error reason ->
                        invalidArg (nameof args.cursor) $"Invalid cursor: %s{reason}"

            let matchedUses =
                projectResults.GetAllUsesOfAllSymbols()
                |> Seq.filter (fun symbolUse -> symbolMatches query exact symbolUse.Symbol)
                |> Seq.filter (fun symbolUse -> includeDeclaration || not symbolUse.IsFromDefinition)
                |> Seq.sortBy (fun symbolUse ->
                    let r = symbolUse.Range
                    symbolUse.Symbol.FullName, symbolUse.FileName, r.StartLine, r.StartColumn)
                |> Seq.toArray

            let useToJson (symbolUse: FSharpSymbolUse) =
                let r = symbolUse.Range
                let context = lineContextToJson contextLines symbolUse.FileName r.StartLine

                jobj
                    [ "file", jstr (normalizePath symbolUse.FileName)
                      "range", rangeToJson r
                      "isDefinition", jbool symbolUse.IsFromDefinition
                      "isReference", jbool symbolUse.IsFromUse
                      "lineText", context["lineText"].DeepClone()
                      "before", context["before"].DeepClone()
                      "after", context["after"].DeepClone() ]
                :> JsonNode

            // Group by symbol identity. Then sort the groups deterministically by
            // their key (FullName, DisplayName, declaration) so cursor offsets are
            // stable across pages.
            let allGroups =
                matchedUses
                |> Array.groupBy (fun symbolUse ->
                    let symbol = symbolUse.Symbol
                    let declaration =
                        symbol.DeclarationLocation
                        |> Option.map (fun range -> $"{normalizePath range.FileName}:{range.StartLine}:{range.StartColumn}")
                        |> Option.defaultValue ""

                    symbol.FullName, symbol.DisplayName, declaration)
                |> Array.sortBy fst

            let totalGroups = allGroups.Length

            let pageGroups =
                allGroups
                |> Array.skip (min pageOffset totalGroups)
                |> Array.truncate pageSize

            let groupNodes =
                pageGroups
                |> Array.map (fun (_, uses) ->
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

            let paginationFields =
                Cursor.paginationFields "symbols" totalGroups pageOffset pageSize pageGroups.Length

            // matchedUseCount stays project-wide (pre-pagination) so callers see the
            // total at a glance. Per-page counts are in totalEstimate / pageOffset.
            let baseFields =
                [ "status", jstr "succeeded"
                  "optionsSource", jstr optionsSource
                  "projectFileName", jstr projectOptions.ProjectFileName
                  "query", jstr query
                  "exact", jbool exact
                  "cached", jbool cached
                  "matchedUseCount", jint matchedUses.Length
                  "symbolCount", jint pageGroups.Length
                  "symbols", JsonArray(groupNodes) :> JsonNode
                  "projectDiagnostics",
                  JsonArray(projectResults.Diagnostics |> Array.map diagnosticToJson) :> JsonNode ]

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    member this.TypeAtPosition(args: FcsTypeAtPositionArgs) : Task<JsonNode> =
        task {
            match validateSourcePath "fcs_type_at_position" args.text args.path with
            | Some err -> return err
            | None ->

            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            match checkedResults with
            | None -> return jobj [ "status", jstr "aborted"; "message", jstr "Type checking was aborted." ] :> JsonNode
            | Some checkResults ->
                // FCS uses 1-based lines; tool contract is 0-based (LSP convention).
                let lines = source.Split('\n')

                let lineTextAt (fcsLine: int) =
                    if fcsLine >= 1 && fcsLine <= lines.Length then
                        lines[fcsLine - 1].TrimEnd('\r')
                    else
                        ""

                let extractTypeAndDoc (toolTip: ToolTipText) =
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

                    typeString, xmlDoc

                let tryAt (fcsLine: int) (fcsCol: int) =
                    let lt = lineTextAt fcsLine

                    if fcsLine < 1 || fcsLine > lines.Length || fcsCol < 0 || fcsCol > lt.Length then
                        None
                    else
                        match checkResults.GetSymbolUseAtLocation(fcsLine, fcsCol, lt, []) with
                        | None -> None
                        | Some su ->
                            let toolTip =
                                checkResults.GetToolTip(
                                    fcsLine,
                                    fcsCol,
                                    lt,
                                    [],
                                    FSharp.Compiler.Tokenization.FSharpTokenTag.IDENT
                                )

                            Some(su, toolTip)

                let exactFcsLine = args.line + 1
                let exactFcsCol = args.character
                let fuzzy = defaultArg args.fuzzy false

                // Generate (Δline, Δcol) offsets ordered by distance.
                // Line shifts are penalized 2× (line errors are more disruptive than column drift).
                let candidates =
                    if fuzzy then
                        seq {
                            for dl in -2 .. 2 do
                                for dc in -5 .. 5 do
                                    yield dl, dc, (abs dl) * 2 + (abs dc)
                        }
                        |> Seq.sortBy (fun (_, _, score) -> score)
                        |> Seq.toList
                    else
                        [ 0, 0, 0 ]

                let resolved =
                    candidates
                    |> List.tryPick (fun (dl, dc, _) ->
                        match tryAt (exactFcsLine + dl) (exactFcsCol + dc) with
                        | None -> None
                        | Some(su, tt) -> Some(dl, dc, su, tt))

                match resolved with
                | None ->
                    let lineText = lineTextAt exactFcsLine

                    let surrounding =
                        JsonArray(
                            [| for delta in -1 .. 1 ->
                                   let candidateLine = args.line + delta
                                   let lt = lineTextAt (candidateLine + 1)

                                   jobj [ "line", jint candidateLine; "text", jstr lt ]
                                   :> JsonNode |]
                        )

                    let hint =
                        "No symbol at this position. Compare requestedLine.text vs your expected line — "
                        + "if you used 1-based line numbers (e.g. from Read), try line - 1. "
                        + "Set fuzzy=true to snap to the nearest symbol within ±2 lines / ±5 cols."

                    return
                        jobj
                            [ "status", jstr "no_symbol"
                              "message", jstr hint
                              "file", jstr path
                              "line", jint args.line
                              "character", jint args.character
                              "lineText", jstr lineText
                              "surroundingLines", (surrounding :> JsonNode)
                              "fuzzy", jbool fuzzy ]
                        :> JsonNode
                | Some(dl, dc, su, toolTip) ->
                    let typeString, xmlDoc = extractTypeAndDoc toolTip
                    let snapped = dl <> 0 || dc <> 0

                    return
                        jobj
                            [ "status", jstr "ok"
                              "file", jstr path
                              "line", jint args.line
                              "character", jint args.character
                              "resolvedLine", jint (args.line + dl)
                              "resolvedCharacter", jint (args.character + dc)
                              "fuzzySnap", jbool snapped
                              "optionsSource", jstr optionsSource
                              "symbolName", jstr su.Symbol.DisplayName
                              "fullName", jstrOrNull su.Symbol.FullName
                              "typeString", jstr typeString
                              "xmlDoc", jstr xmlDoc ]
                        :> JsonNode
        }

    member this.SymbolAtWord(args: FcsSymbolAtWordArgs) : Task<JsonNode> =
        task {
            match validateSourcePath "fcs_symbol_at_word" args.text args.path with
            | Some err -> return err
            | None ->

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
            let projectPath =
                match args.projectPath with
                | Some p when not (String.IsNullOrWhiteSpace p) -> normalizePath p
                | _ ->
                    invalidArg
                        (nameof args.projectPath)
                        "projectPath is required. Either pass it explicitly or call set_project first to establish a default."

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

            // pageSize=0 would emit empty pages with truncated=true and a nextCursor
            // whose offset never advances — a non-terminating loop for cursor-following
            // clients. Reject up front.
            if pageSize < 1 then
                invalidArg (nameof args.maxFiles) $"maxFiles must be >= 1 (got {pageSize})"

            let maxResultsPerFile = args.maxResultsPerFile |> Option.defaultValue 30

            if maxResultsPerFile < 0 then
                invalidArg (nameof args.maxResultsPerFile) $"maxResultsPerFile must be >= 0 (got {maxResultsPerFile})"
            let summaryOnly = args.summaryOnly |> Option.defaultValue true

            // ── Build regex / substring matchers ───────────────────────────────
            let filterRegex =
                match args.filter with
                | None -> None
                | Some pattern ->
                    if pattern.Length > 1024 then
                        invalidArg (nameof args.filter) $"filter pattern must not exceed 1024 characters (got {pattern.Length})"

                    try
                        // NonBacktracking eliminates catastrophic-backtracking risk for
                        // user-supplied patterns like (a+)+$. A 250ms timeout is a belt-
                        // and-suspenders guard; NonBacktracking should never time out.
                        let opts =
                            System.Text.RegularExpressions.RegexOptions.NonBacktracking
                            ||| System.Text.RegularExpressions.RegexOptions.IgnoreCase

                        Some(
                            System.Text.RegularExpressions.Regex(
                                pattern,
                                opts,
                                TimeSpan.FromMilliseconds 250.0
                            )
                        )
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
                    | Some rx ->
                        try
                            rx.IsMatch(name) || rx.IsMatch(signature)
                        with
                        | :? System.Text.RegularExpressions.RegexMatchTimeoutException -> false

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
                // Fetch the full per-file outline (maxResults = None) so memberCounts
                // can report the true totals. Truncation is applied below to the
                // entries array only — the cap is a presentation concern, not a
                // counting concern.
                let! outline =
                    this.FileOutline(
                        { path = file.Path
                          text = None
                          projectPath = Some projectPath
                          projectOptions = None
                          includePrivate = args.includePrivate
                          includeLocal = Some false
                          maxResults = None }
                    )

                // Pull raw entries array (may be null if outline aborted).
                let rawEntries: JsonNode array =
                    match outline["entries"] with
                    | null -> [||]
                    | entries ->
                        match entries with
                        | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toArray
                        | _ -> [||]

                // Apply filter first, keep the unbounded post-filter set so memberCounts
                // can report the true totals; then truncate for the entries array.
                let postFilterEntries =
                    if filterRegex.IsNone && nameContains.IsNone then
                        rawEntries
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

                let filteredEntries = postFilterEntries |> Array.truncate maxResultsPerFile

                // summaryOnly: strip per-member signature detail, keep headers; counts
                // surface as a top-level memberCounts map per file (issue #82).
                let containerKinds =
                    [| "module"; "record"; "union"; "class"; "interface"; "enum"; "delegate"; "namespace" |]

                let outlineEntries: JsonNode =
                    if summaryOnly then
                        let topLevel =
                            filteredEntries
                            |> Array.filter (fun entry ->
                                match entry["kind"] with
                                | null -> false
                                | k -> containerKinds |> Array.contains (k.GetValue<string>()))

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

                        JsonArray(summaryNodes) :> JsonNode
                    else
                        // Deep-clone each entry to release ownership from the source
                        // JsonArray returned by FileOutline — a JsonNode may only have
                        // one parent, so re-parenting without cloning throws.
                        JsonArray(filteredEntries |> Array.map (fun e -> e.DeepClone())) :> JsonNode

                // memberCounts: kind → count over the unbounded post-filter set, so the
                // map tells the agent how many of each kind the filter actually matched
                // — independent of maxResultsPerFile, which only caps the entries array.
                let memberCounts =
                    let counts =
                        postFilterEntries
                        |> Array.choose (fun entry ->
                            match entry["kind"] with
                            | null -> None
                            | k -> Some(k.GetValue<string>()))
                        |> Array.countBy id
                        |> Array.sortBy fst
                        |> Array.map (fun (kind, n) -> kind, jint n)
                        |> Array.toList

                    jobj counts :> JsonNode

                let fileFields =
                    [ "file", jstr file.Path
                      "kind", jstr (if file.IsSignature then "signature" else "implementation")
                      "outlineStatus", outline["status"].DeepClone()
                      "entries", outlineEntries
                      "memberCounts", memberCounts
                      "count",
                      (match outline["count"] with
                       | null -> jint 0
                       | count -> count.DeepClone()) ]

                fileEntries.Add(jobj fileFields :> JsonNode)

            // ── Pagination envelope ─────────────────────────────────────────────
            let paginationFields =
                Cursor.paginationFields "files" totalFileCount pageOffset pageSize pageFiles.Length

            let baseFields =
                [ "status", jstr "ok"
                  "projectPath", jstr projectPath
                  "workspaceRoot", jstr workspaceRoot
                  "summaryOnly", jbool summaryOnly
                  "filterSummary", filterSummaryToJson allFiles :> JsonNode
                  "files", JsonArray(fileEntries.ToArray()) :> JsonNode ]

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    /// Resolve project options + ensure ParseAndCheckProject results are available.
    /// Shared by referenced-assembly tools (F-3).
    member private this.EnsureProjectResults
        (projectPath: string option)
        : Task<FSharpCheckProjectResults * FSharpProjectOptions * string> =
        task {
            match projectPath with
            | None ->
                return
                    raise (
                        InvalidOperationException(
                            "projectPath is required (or call set_project first to set the active project)"
                        )
                    )
            | Some fsproj ->
                let! options, optionsSource = this.ResolveFsprojOptions(fsproj)
                let cacheKey = makeResolvedProjectCacheKey options

                let! results =
                    task {
                        match projectResultsCache.TryGet(cacheKey) with
                        | Some existing -> return existing
                        | None ->
                            let! fresh = checker.ParseAndCheckProject(options) |> asTask
                            projectResultsCache.Set(cacheKey, fresh)
                            return fresh
                    }

                return results, options, optionsSource
        }

    /// Search across referenced-assembly types by substring on DisplayName/FullName.
    /// Powers the "find the Spectre.Console.Cell internal type" use case (F-3).
    member this.ReferencedSymbols(args: FcsReferencedSymbolsArgs) : Task<JsonNode> =
        task {
            let query = (args.query |> Option.ofObj |> Option.defaultValue "").Trim()

            if String.IsNullOrWhiteSpace(query) then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr "query must be non-empty" ]
                    :> JsonNode
            else if args.projectPath.IsNone then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "projectPath is required (or call set_project first to set the active project)" ]
                    :> JsonNode
            else

            let includeNonPublic = defaultArg args.includeNonPublic false
            let requested = defaultArg args.maxResults 200
            let pageSize = min (max 1 requested) 1000

            let pageOffsetResult =
                match args.cursor with
                | None -> Ok 0
                | Some cursorStr ->
                    match Cursor.tryDecode cursorStr with
                    | Ok payload -> Ok payload.offset
                    | Error reason -> Error reason

            match pageOffsetResult with
            | Error reason ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr $"Invalid cursor: %s{reason}" ]
                    :> JsonNode
            | Ok pageOffset ->

            let! results, options, optionsSource = this.EnsureProjectResults args.projectPath

            let assemblies =
                try
                    results.ProjectContext.GetReferencedAssemblies()
                with _ ->
                    []

            let queryLower = query.ToLowerInvariant()

            let matchesQuery (entity: FSharpEntity) =
                let displayName =
                    try entity.DisplayName |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                let fullName =
                    try entity.FullName |> Option.ofObj |> Option.defaultValue "" with _ -> ""

                displayName.ToLowerInvariant().Contains(queryLower)
                || fullName.ToLowerInvariant().Contains(queryLower)

            let passesAccessibility (entity: FSharpEntity) =
                if includeNonPublic then
                    true
                else
                    let acc = entityAccessibilityString entity
                    acc = "public" || acc = "unknown"

            let allMatches =
                seq {
                    for asm in assemblies do
                        let asmName =
                            try asm.SimpleName |> Option.ofObj |> Option.defaultValue "" with _ -> ""

                        for entity in allEntitiesFromAssembly asm do
                            if matchesQuery entity && passesAccessibility entity then
                                yield referencedEntityToJson asmName entity
                }
                |> Seq.toArray

            let pageEntries =
                allMatches
                |> Array.skip (min pageOffset allMatches.Length)
                |> Array.truncate pageSize

            let baseFields =
                [ "status", jstr "ok"
                  "query", jstr query
                  "projectFileName", jstr options.ProjectFileName
                  "optionsSource", jstr optionsSource
                  "includeNonPublic", jbool includeNonPublic
                  "assemblyCount", jint (List.length assemblies)
                  "results", JsonArray(pageEntries) :> JsonNode ]

            let paginationFields =
                Cursor.paginationFields "symbols" allMatches.Length pageOffset pageSize pageEntries.Length

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    /// Enumerate types exported by one referenced assembly (matched by package id ≈ SimpleName).
    /// Powers the "what types does this NuGet package expose" use case (F-3).
    member this.NugetTypes(args: FcsNugetTypesArgs) : Task<JsonNode> =
        task {
            let packageId = (args.packageId |> Option.ofObj |> Option.defaultValue "").Trim()

            if String.IsNullOrWhiteSpace(packageId) then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr "packageId must be non-empty" ]
                    :> JsonNode
            else if args.projectPath.IsNone then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "projectPath is required (or call set_project first to set the active project)" ]
                    :> JsonNode
            else

            let includeNonPublic = defaultArg args.includeNonPublic false
            let requested = defaultArg args.maxResults 500
            let pageSize = min (max 1 requested) 2000

            let pageOffsetResult =
                match args.cursor with
                | None -> Ok 0
                | Some cursorStr ->
                    match Cursor.tryDecode cursorStr with
                    | Ok payload -> Ok payload.offset
                    | Error reason -> Error reason

            match pageOffsetResult with
            | Error reason ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr $"Invalid cursor: %s{reason}" ]
                    :> JsonNode
            | Ok pageOffset ->

            let! results, options, optionsSource = this.EnsureProjectResults args.projectPath

            let assemblies =
                try
                    results.ProjectContext.GetReferencedAssemblies()
                with _ ->
                    []

            let matchingAssemblies =
                assemblies
                |> List.filter (fun asm -> assemblyMatchesPackageId asm packageId)

            let matchedAssemblyNames =
                matchingAssemblies
                |> List.map (fun asm ->
                    try asm.SimpleName |> Option.ofObj |> Option.defaultValue "" with _ -> "")
                |> List.filter (fun s -> s <> "")

            let passesAccessibility (entity: FSharpEntity) =
                if includeNonPublic then
                    true
                else
                    let acc = entityAccessibilityString entity
                    acc = "public" || acc = "unknown"

            let allEntities =
                seq {
                    for asm in matchingAssemblies do
                        let asmName =
                            try asm.SimpleName |> Option.ofObj |> Option.defaultValue "" with _ -> ""

                        for entity in allEntitiesFromAssembly asm do
                            if passesAccessibility entity then
                                yield referencedEntityToJson asmName entity
                }
                |> Seq.toArray

            let pageEntries =
                allEntities
                |> Array.skip (min pageOffset allEntities.Length)
                |> Array.truncate pageSize

            let baseFields =
                [ "status", jstr "ok"
                  "packageId", jstr packageId
                  "matchedAssemblies", JsonArray(matchedAssemblyNames |> List.map jstr |> List.toArray) :> JsonNode
                  "projectFileName", jstr options.ProjectFileName
                  "optionsSource", jstr optionsSource
                  "includeNonPublic", jbool includeNonPublic
                  "results", JsonArray(pageEntries) :> JsonNode ]

            let paginationFields =
                Cursor.paginationFields "types" allEntities.Length pageOffset pageSize pageEntries.Length

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    member _.ClearCaches() =
        optionsCache.Clear()
        projectResultsCache.Clear()

    /// Returns configuration flags captured at checker creation time, for use by RuntimeStatus.
    member _.CheckerConfig: FcsCheckerConfig =
        { KeepAssemblyContents = keepAssemblyContents
          KeepAllBackgroundResolutions = keepAllBackgroundResolutions
          KeepAllBackgroundSymbolUses = keepAllBackgroundSymbolUses
          ProjectCacheSize = defaultProjectCacheSize }

    /// Returns the number of entries currently held in the project-results cache.
    member _.ProjectResultsCacheCount = projectResultsCache.Count

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
            match validateSourcePath "fcs_signature_help" args.text args.path with
            | Some err -> return err
            | None ->

            let! path, source, optionsSource, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, args.projectOptions)

            return this.BuildSignatureHelpResult(path, source, optionsSource, args, checkedResults)
        }
