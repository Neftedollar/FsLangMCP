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

// ─── FieldFormClassifier ────────────────────────────────────────────────────────
// Parse-tree-based classification of record field use sites as either a record
// literal (`{ Field = expr }`) or a record-update expression (`{ x with Field = expr }`).
// Replaces the old textual lookback heuristic that mis-classified fields more than
// 2 lines below the `with` keyword. See issue #122.
//
// v2 (issue #124): replaced the hand-rolled walkExpr/walkDecl with ParsedInput.fold,
// a full-tree fold provided by FCS 43.12+ that visits every expression-containing
// AST node without a position filter. This covers all previously missing arms:
// SynModuleDecl.Types, SynExpr.ForEach/For/While, SynExpr.LetOrUseBang,
// SynExpr.MatchLambda, SynExpr.ObjExpr, SynExpr.Lazy, and any future FCS additions.
// fallbackHeuristic call-sites in formOf are retained as a defensive safety net.

open FSharp.Compiler.Syntax

/// A (startLine, startColumn) pair used as a dictionary key for field-name ranges.
/// FCS range has [<NoComparison>], so it cannot be used directly as a map key.
type private FieldFormKey = int * int

/// Module that walks a FCS ParsedInput and tags every record-field expression
/// site as `true` (with-update form) or `false` (literal form).
module private FieldFormClassifier =

    let private tagFields
        (isUpdate: bool)
        (fields: SynExprRecordField list)
        (d: System.Collections.Generic.Dictionary<FieldFormKey, bool>)
        =
        for field in fields do
            match field with
            | SynExprRecordField((lid, _), _, _, _, _) ->
                let r = lid.Range
                d[(r.StartLine, r.StartColumn)] <- isUpdate

    /// Walk the full parse tree using ParsedInput.fold (FCS 43.12+).
    /// ParsedInput.fold is a position-independent full-tree accumulator that visits
    /// every SyntaxNode in the tree, including those inside type member bodies,
    /// for-loops, CE binds, object expressions, and all other expression-containing arms.
    let classify (input: ParsedInput) : System.Collections.Generic.Dictionary<FieldFormKey, bool> =
        // SigFile (.fsi): signature files declare types but contain no expression
        // use-sites — there are no record literals/updates here to classify. Empty
        // dictionary is the correct return; do not "complete" this arm.
        match input with
        | ParsedInput.SigFile _ -> System.Collections.Generic.Dictionary<FieldFormKey, bool>()
        | ParsedInput.ImplFile _ ->
            let d = System.Collections.Generic.Dictionary<FieldFormKey, bool>()

            (d, input)
            ||> ParsedInput.fold (fun acc _path node ->
                match node with
                | SyntaxNode.SynExpr(SynExpr.Record(_, copyInfo, fields, _)) ->
                    tagFields copyInfo.IsSome fields acc
                    acc
                | _ -> acc)


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

    // ─── Helpers for member enumeration (fcs_nuget_members, #125) ───────────────

    let memberKindString (m: FSharpMemberOrFunctionOrValue) : string =
        try
            if m.IsConstructor then "constructor"
            elif m.IsEvent then "event"
            elif m.IsProperty then "property"
            elif m.IsMember then "method"
            else "function"
        with _ ->
            "member"

    let memberAccessibilityString (m: FSharpMemberOrFunctionOrValue) : string =
        try
            let acc = m.Accessibility

            if acc.IsPrivate then "private"
            elif acc.IsInternal then "internal"
            elif acc.IsPublic then "public"
            else "unknown"
        with _ ->
            "unknown"

    let isObsoleteMember (m: FSharpMemberOrFunctionOrValue) : bool =
        try
            m.Attributes
            |> Seq.exists (fun a ->
                try
                    let tn = a.AttributeType.FullName

                    not (isNull tn)
                    && (tn = "System.ObsoleteAttribute"
                        || tn.EndsWith(".ObsoleteAttribute", StringComparison.Ordinal))
                with _ ->
                    false)
        with _ ->
            false

    let memberSignature (m: FSharpMemberOrFunctionOrValue) : string =
        try
            let paramGroups = m.CurriedParameterGroups

            let paramStr =
                paramGroups
                |> Seq.collect id
                |> Seq.map (fun p ->
                    let pName = p.Name |> Option.defaultValue "_"
                    let pType = try typeName p.Type with _ -> "?"
                    $"{pName}: {pType}")
                |> String.concat ", "

            let returnType =
                try
                    typeName m.ReturnParameter.Type
                with _ ->
                    "?"

            $"{m.DisplayName}({paramStr}) -> {returnType}"
        with _ ->
            try
                m.DisplayName
            with _ ->
                "<unknown>"

    let tryExtractXmlSummary (xmlDoc: FSharpXmlDoc) : JsonNode =
        try
            match xmlDoc with
            | FSharpXmlDoc.FromXmlText xmlText ->
                let text = xmlText.GetXmlText()

                if String.IsNullOrWhiteSpace text then
                    null
                else
                    let startTag = "<summary>"
                    let endTag = "</summary>"
                    let si = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase)

                    if si < 0 then
                        null
                    else
                        let contentStart = si + startTag.Length
                        let ei = text.IndexOf(endTag, contentStart, StringComparison.OrdinalIgnoreCase)

                        if ei < 0 then
                            null
                        else
                            let summary = text.Substring(contentStart, ei - contentStart).Trim()

                            if String.IsNullOrWhiteSpace summary then null
                            else jstr summary
            | _ -> null
        with _ ->
            null

    let referencedMemberToJson (m: FSharpMemberOrFunctionOrValue) : JsonNode =
        let xmlDocNode : JsonNode =
            try tryExtractXmlSummary m.XmlDoc with _ -> null

        jobj
            [ "name", jstr (try m.DisplayName with _ -> "<unknown>")
              "kind", jstr (memberKindString m)
              "signature", jstr (memberSignature m)
              "accessibility", jstr (memberAccessibilityString m)
              "isObsolete", jbool (isObsoleteMember m)
              "xmlDocSummary", xmlDocNode ]
        :> JsonNode

    let fieldAccessibilityString (f: FSharpField) : string =
        try
            let acc = f.Accessibility

            if acc.IsPrivate then "private"
            elif acc.IsInternal then "internal"
            elif acc.IsPublic then "public"
            else "unknown"
        with _ ->
            "unknown"

    let referencedFieldToJson (f: FSharpField) : JsonNode =
        let signature =
            try
                $"{f.Name}: {typeName f.FieldType}"
            with _ ->
                try f.Name with _ -> "<unknown>"

        let isObsolete =
            try
                f.Attributes
                |> Seq.exists (fun a ->
                    try
                        let tn = a.AttributeType.FullName

                        not (isNull tn)
                        && (tn = "System.ObsoleteAttribute"
                            || tn.EndsWith(".ObsoleteAttribute", StringComparison.Ordinal))
                    with _ ->
                        false)
            with _ ->
                false

        jobj
            [ "name", jstr (try f.Name with _ -> "<unknown>")
              "kind", jstr "field"
              "signature", jstr signature
              "accessibility", jstr (fieldAccessibilityString f)
              "isObsolete", jbool isObsolete
              "xmlDocSummary", null ]
        :> JsonNode

    let referencedUnionCaseToJson (uc: FSharpUnionCase) : JsonNode =
        let signature =
            try
                if uc.Fields.Count = 0 then
                    uc.Name
                else
                    let fieldTypes =
                        uc.Fields
                        |> Seq.map (fun f -> try typeName f.FieldType with _ -> "?")
                        |> String.concat " * "

                    $"{uc.Name} of {fieldTypes}"
            with _ ->
                try uc.Name with _ -> "<unknown>"

        let accessibility =
            try
                let acc = uc.Accessibility

                if acc.IsPrivate then "private"
                elif acc.IsInternal then "internal"
                elif acc.IsPublic then "public"
                else "unknown"
            with _ ->
                "unknown"

        let isObsolete =
            try
                uc.Attributes
                |> Seq.exists (fun a ->
                    try
                        let tn = a.AttributeType.FullName

                        not (isNull tn)
                        && (tn = "System.ObsoleteAttribute"
                            || tn.EndsWith(".ObsoleteAttribute", StringComparison.Ordinal))
                    with _ ->
                        false)
            with _ ->
                false

        jobj
            [ "name", jstr (try uc.Name with _ -> "<unknown>")
              "kind", jstr "union-case"
              "signature", jstr signature
              "accessibility", jstr accessibility
              "isObsolete", jbool isObsolete
              "xmlDocSummary", null ]
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
            match ArgsValidation.requireNonBlank "symbolQuery" args.symbolQuery with
            | Error envelope -> return envelope
            | Ok query ->

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
            match ArgsValidation.requireNonBlank "typeName" args.typeName with
            | Error envelope -> return envelope
            | Ok typeName ->
            match ArgsValidation.requireNonBlank "memberName" args.memberName with
            | Error envelope -> return envelope
            | Ok memberName ->

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

    /// Finds every record-construction site for `typeName.fieldName`, covering
    /// both `{ Field = expr; ... }` literal form AND `{ x with Field = expr }`
    /// update form. Solves the gap where `fcs_find_symbol`/`textDocument_references`
    /// look up the *type name* and miss field-set uses — confirmed on
    /// LlmTrader's `TraderRole.Propose` (28 caller sites) and `RiskDebatorRole`.
    /// See #114.
    member this.RecordFieldAudit(args: FcsRecordFieldAuditArgs) : Task<JsonNode> =
        task {
            match ArgsValidation.requireNonBlank "typeName" args.typeName with
            | Error envelope -> return envelope
            | Ok typeName ->
            match ArgsValidation.requireNonBlank "fieldName" args.fieldName with
            | Error envelope -> return envelope
            | Ok fieldName ->

            // Resolve project options either via path or projectPath, matching
            // FindMemberUsages's resolution path so callers see the same fallback
            // semantics across both record/member audit tools.
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

            // typeName matching for the declaring entity: same exact-DisplayName-OR-
            // segment-boundary-FullName logic as FindMemberUsages, so users get the
            // same semantics across both tools.
            let fullNameEndsAtBoundary (fullName: string) =
                String.Equals(fullName, typeName, StringComparison.Ordinal)
                || (fullName.EndsWith(typeName, StringComparison.Ordinal)
                    && fullName.Length > typeName.Length
                    && fullName[fullName.Length - typeName.Length - 1] = '.')

            let matchesDeclaringEntity (entity: FSharpEntity) =
                let displayOk =
                    String.Equals(entity.DisplayName, typeName, StringComparison.Ordinal)

                let fullName = entity.FullName

                let fullOk =
                    if isNull fullName then
                        false
                    else
                        fullNameEndsAtBoundary fullName

                displayOk || fullOk

            // A record-field SymbolUse is a write site if the symbol is an
            // FSharpField whose declaring entity matches typeName and whose Name
            // matches fieldName. FCS reports both reads and writes through the
            // same use; we exclude IsFromDefinition (the field's declaration in
            // the record type itself).
            let fieldFilter (symbolUse: FSharpSymbolUse) : bool =
                if symbolUse.IsFromDefinition then
                    false
                else
                    match symbolUse.Symbol with
                    | :? FSharpField as field ->
                        try
                            let nameOk =
                                String.Equals(field.Name, fieldName, StringComparison.Ordinal)

                            let typeOk =
                                let declaring = field.DeclaringEntity

                                match declaring with
                                | Some e -> matchesDeclaringEntity e
                                | None -> false

                            nameOk && typeOk
                        with _ ->
                            // FCS occasionally throws on synthetic / anonymous-record fields;
                            // treat as non-match rather than letting the whole audit fail.
                            false
                    | _ -> false

            let allUses = projectResults.GetAllUsesOfAllSymbols()

            let sortedMatches =
                allUses
                |> Array.filter fieldFilter
                |> Array.sortBy (fun symbolUse ->
                    let r = symbolUse.Range
                    symbolUse.FileName, r.StartLine, r.StartColumn)

            let totalMatched = sortedMatches.Length

            let pageUses =
                sortedMatches
                |> Array.skip (min pageOffset totalMatched)
                |> Array.truncate pageSize

            // When the caller provides unsaved text for a specific path, use that
            // content for parsing that file; all other files use their on-disk content.
            let unsavedText =
                match args.path, args.text with
                | Some p, Some t when not (String.IsNullOrWhiteSpace t) ->
                    Some(normalizePath p, t)
                | _ -> None

            // Derive parsing options from the already-resolved project options.
            let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(projectOptions)

            // Build a per-file parse-tree classifier cache.
            // Each file is parsed at most once; results are stored here and reused
            // for every symbolUse in that file.
            let fieldFormCache =
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<FieldFormKey, bool> option>()

            // PERF: this parses each file serially via Async.RunSynchronously. Warm FCS
            // cache makes this ~microseconds per file, but a cold-cache audit over many
            // distinct files is O(file count × parse-time). If this surfaces as a
            // bottleneck, parallelise via Async.Parallel or batch through the project's
            // checker. See #124 for the parse-tree walker follow-up where this could be
            // rolled in.
            // Parse one file and return its field-form dictionary, or None on failure.
            let parseFileForForms (filePath: string) =
                match fieldFormCache.TryGetValue(filePath) with
                | true, cached -> cached
                | _ ->
                    let result =
                        try
                            let source =
                                match unsavedText with
                                | Some(textPath, text) when
                                    String.Equals(filePath, textPath, StringComparison.Ordinal)
                                    ->
                                    text
                                | _ ->
                                    if File.Exists filePath then
                                        File.ReadAllText filePath
                                    else
                                        ""

                            if String.IsNullOrEmpty source then
                                None
                            else
                                let sourceText = SourceText.ofString source
                                // checker.ParseFile is cached by FCS internally on warm cache;
                                // the Async is started synchronously here since we are already
                                // inside a task{} and this is a fast in-process parse.
                                let parseResults =
                                    checker.ParseFile(filePath, sourceText, parsingOptions)
                                    |> Async.RunSynchronously

                                Some(FieldFormClassifier.classify parseResults.ParseTree)
                        with _ ->
                            // Fall back gracefully: classification will return "unknown"
                            // for all uses in this file. Likely causes: file deleted
                            // between project check and audit, or parse exception.
                            None

                    fieldFormCache[filePath] <- result
                    result

            // Classify a single symbolUse's form using the parse-tree classifier.
            // Falls back to the old textual heuristic if the parse tree is unavailable
            // OR if the walker did not visit the site, so callers never regress to worse
            // output than the previous version.

            // Named local so it can be called from BOTH miss paths (parse failure and
            // parse-tree walker miss — e.g. record inside a type member body or for-loop).
            let fallbackHeuristic (symbolUse: FSharpSymbolUse) : string =
                let filePath = normalizePath symbolUse.FileName
                let r = symbolUse.Range

                try
                    let lines =
                        match unsavedText with
                        | Some(textPath, text) when
                            String.Equals(filePath, textPath, StringComparison.Ordinal)
                            ->
                            Some(text.Split('\n'))
                        | _ ->
                            if File.Exists symbolUse.FileName then
                                Some(File.ReadAllLines(symbolUse.FileName))
                            else
                                None

                    match lines with
                    | None -> "unknown"
                    | Some lines ->
                        let startIdx = max 0 (r.StartLine - 3)
                        let endIdx = min (lines.Length - 1) (r.StartLine - 1)
                        let mutable foundWith = false

                        for i in startIdx..endIdx do
                            if not foundWith && lines[i].Contains(" with ") then
                                foundWith <- true

                        if foundWith then "with-update" else "literal"
                with _ ->
                    "unknown"

            let formOf (symbolUse: FSharpSymbolUse) =
                let filePath = normalizePath symbolUse.FileName
                let r = symbolUse.Range

                match parseFileForForms filePath with
                | Some d ->
                    let key = (r.StartLine, r.StartColumn)

                    match d.TryGetValue(key) with
                    | true, isUpdate -> if isUpdate then "with-update" else "literal"
                    | false, _ ->
                        // Parse tree was available but the walker did not visit this site
                        // (e.g., inside a type member body, for-loop, or computation
                        // expression bind). Fall back to the textual heuristic so we never
                        // regress below v0.8.1 behaviour for these site classes.
                        fallbackHeuristic symbolUse
                | None ->
                    // Parse failed — fall back to textual heuristic.
                    // This preserves behaviour for callers that rely on formOf during
                    // parse failures (e.g. files in error, or temp-file projects).
                    fallbackHeuristic symbolUse

            let siteToJson (symbolUse: FSharpSymbolUse) =
                let r = symbolUse.Range
                let context = lineContextToJson 2 symbolUse.FileName r.StartLine
                let form = formOf symbolUse

                jobj
                    [ "file", jstr (normalizePath symbolUse.FileName)
                      "range", rangeToJson r
                      "form", jstr form
                      "context", context ]
                :> JsonNode

            let pageNodes = pageUses |> Array.map siteToJson

            let paginationFields =
                Cursor.paginationFields "sites" totalMatched pageOffset pageSize pageUses.Length

            let baseFields =
                [ "status", jstr "succeeded"
                  "optionsSource", jstr optionsSource
                  "projectFileName", jstr projectOptions.ProjectFileName
                  "typeName", jstr typeName
                  "fieldName", jstr fieldName
                  "cached", jbool cached
                  "totalProjectSymbolUses", jint allUses.Length
                  "matchedCount", jint totalMatched
                  "sites", JsonArray(pageNodes) :> JsonNode ]

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    member this.FindSymbol(args: FcsFindSymbolArgs) : Task<JsonNode> =
        task {
            match ArgsValidation.requireNonBlank "symbolQuery" args.symbolQuery with
            | Error envelope -> return envelope
            | Ok query ->

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

            // ── projectDiagnostics scoping (#116) ─────────────────────────────
            // Default: only return diagnostics for files that actually contain a
            // match for the queried symbol, AND drop Info/Hint severity. The
            // reporter on #100 got unrelated FS3520 XML-comment chatter in the
            // response which drowned out signal. `includeInfo=true` restores
            // Info/Hint when callers want the full payload.
            let includeInfo = defaultArg args.includeInfo false

            let matchedFileSet =
                matchedUses
                |> Array.map (fun u -> normalizePath u.FileName)
                |> Set.ofArray

            // When no matches were found, fall back to Error-severity diagnostics only
            // so callers can still detect broken projects. The scope field distinguishes
            // the two regimes: "matched-files" (normal) vs "errors-only-no-matches"
            // (zero hits — full project errors surfaced so callers don't lose signal).
            let scopedDiagnostics =
                projectResults.Diagnostics
                |> Array.filter (fun d ->
                    let fileOk =
                        if matchedFileSet.IsEmpty then
                            // No matches → surface errors only, regardless of file.
                            d.Severity = FSharpDiagnosticSeverity.Error
                        else
                            matchedFileSet.Contains(normalizePath d.FileName)

                    let severityOk =
                        matchedFileSet.IsEmpty
                        || includeInfo
                        || d.Severity = FSharpDiagnosticSeverity.Error
                        || d.Severity = FSharpDiagnosticSeverity.Warning

                    fileOk && severityOk)

            let diagnosticsScope =
                if matchedFileSet.IsEmpty then "errors-only-no-matches" else "matched-files"

            let baseFields =
                [ "status", jstr "succeeded"
                  "optionsSource", jstr optionsSource
                  "projectFileName", jstr projectOptions.ProjectFileName
                  "query", jstr query
                  "exact", jbool exact
                  "cached", jbool cached
                  "matchedUseCount", jint matchedUses.Length
                  "matchedFileCount", jint matchedFileSet.Count
                  "symbolCount", jint pageGroups.Length
                  "includeInfo", jbool includeInfo
                  "symbols", JsonArray(groupNodes) :> JsonNode
                  "projectDiagnosticsScope", jstr diagnosticsScope
                  "projectDiagnostics",
                  JsonArray(scopedDiagnostics |> Array.map diagnosticToJson) :> JsonNode ]

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

    // Returns true if the character at `pos` in `line` is inside a string literal
    // (regular, verbatim @"...", or triple-quoted """...""") OR past a real //
    // line comment. Conservative: if the line is too tricky to parse, returns
    // true so we refuse to edit rather than risk corruption.
    // Handles:
    //   Regular  "..."  — \" escapes a quote, \\ escapes a backslash.
    //   Verbatim @"..." — \ is literal; "" is the escaped-quote sequence.
    //   Triple   """...""" — no escaping; only """ ends the literal.
    member private _.PositionIsUnsafe(line: string, pos: int) : bool =
        let mutable i = 0
        let mutable state = 0 // 0=code, 1=regular string, 2=verbatim, 3=triple
        let mutable past = false // set when a real // comment is seen before pos

        while i < pos && not past do
            match state with
            | 0 ->
                if i + 2 < line.Length && line[i] = '"' && line[i + 1] = '"' && line[i + 2] = '"' then
                    state <- 3
                    i <- i + 3
                elif i + 1 < line.Length && line[i] = '@' && line[i + 1] = '"' then
                    state <- 2
                    i <- i + 2
                elif line[i] = '"' then
                    state <- 1
                    i <- i + 1
                elif i + 1 < line.Length && line[i] = '(' && line[i + 1] = '*' then
                    // F# block comment — advance through nested (* *) pairs.
                    // We record the start so we can tell if pos fell inside the comment.
                    let commentStart = i
                    let mutable depth = 1
                    i <- i + 2

                    while depth > 0 && i + 1 < line.Length do
                        if line[i] = '(' && line[i + 1] = '*' then
                            depth <- depth + 1
                            i <- i + 2
                        elif line[i] = '*' && line[i + 1] = ')' then
                            depth <- depth - 1
                            i <- i + 2
                        else
                            i <- i + 1

                    if depth > 0 then
                        // Unclosed block comment on this line — too complex to parse safely.
                        // If pos was anywhere at or after the opener, refuse.
                        past <- true
                        i <- line.Length
                    elif pos < i then
                        // pos is INSIDE the closed comment span [commentStart, i) — refuse.
                        // (pos > commentStart is already implied by entering this branch,
                        // so the discriminator is pos < i, i.e. the comment end.)
                        past <- true
                        i <- line.Length
                    // else: comment fully closed at i ≤ pos, so pos is in real code
                    // after the closed comment. Fall through: state is back to 0 and
                    // the outer while loop continues scanning from i.
                elif i + 1 < line.Length && line[i] = '/' && line[i + 1] = '/' then
                    // Line comment starts here; pos is inside it if pos > i.
                    past <- true
                    i <- line.Length
                else
                    i <- i + 1
            | 1 -> // regular string — \" escapes a quote, \\ escapes a backslash
                if i + 1 < line.Length && line[i] = '\\' then
                    i <- i + 2
                elif line[i] = '"' then
                    state <- 0
                    i <- i + 1
                else
                    i <- i + 1
            | 2 -> // verbatim — "" is the escape sequence, \ is literal
                if i + 1 < line.Length && line[i] = '"' && line[i + 1] = '"' then
                    i <- i + 2
                elif line[i] = '"' then
                    state <- 0
                    i <- i + 1
                else
                    i <- i + 1
            | 3 -> // triple-quoted — only """ ends the literal
                if i + 2 < line.Length && line[i] = '"' && line[i + 1] = '"' && line[i + 2] = '"' then
                    state <- 0
                    i <- i + 3
                else
                    i <- i + 1
            | _ -> i <- i + 1

        // Unsafe if still inside a string state OR a // comment started before pos.
        state <> 0 || past

    // ── Locate the " private" keyword span on a line, only when it follows a
    // recognized declaration keyword (let / let rec / and / module / module rec /
    // type / member / val / new / static / abstract / override) AND is NOT
    // inside a line comment or a string literal (regular, verbatim, or triple).
    // Returns the (startColumn, endColumn) range to delete. The leading space
    // before "private" is included in the range; the trailing space before the
    // identifier is preserved. Returns None when no matching " private" token
    // is found on the line.
    member private this.FindPrivateSpan(rawLine: string) : (int * int) option =
        let privateKw = " private"

        let mutable idx = 0
        let mutable found = None

        while idx <= rawLine.Length - privateKw.Length && found.IsNone do
            let pos = rawLine.IndexOf(privateKw, idx, StringComparison.Ordinal)

            if pos < 0 then
                idx <- rawLine.Length
            else
                let afterPos = pos + privateKw.Length

                // Token boundary: " private" must be followed by whitespace or EOL,
                // not by another letter (would be "privateer" / "privately" etc.).
                let isWordBoundary =
                    afterPos >= rawLine.Length
                    || rawLine[afterPos] = ' '
                    || rawLine[afterPos] = '\t'

                if isWordBoundary && not (this.PositionIsUnsafe(rawLine, pos)) then
                    // Strip leading whitespace, then peel off any number of
                    // `[<...>]` attribute blocks and `(* ... *)` block comments
                    // (each optionally followed by whitespace).
                    // `[<Fact>] let private foo = 1` must match `let` after the attribute.
                    // `(* doc *) let private foo = 1` must match `let` after the comment.
                    let mutable before = rawLine.Substring(0, pos).TrimStart()

                    let mutable keepStripping = true

                    while keepStripping do
                        if before.StartsWith("[<", StringComparison.Ordinal) then
                            match before.IndexOf(">]", StringComparison.Ordinal) with
                            | -1 -> keepStripping <- false
                            | endIdx -> before <- before.Substring(endIdx + 2).TrimStart()
                        elif before.StartsWith("(*", StringComparison.Ordinal) then
                            // Skip the closed block comment. Use a simple depth-tracking
                            // scan to handle nested (* *) pairs.
                            let mutable j = 2
                            let mutable depth = 1
                            while depth > 0 && j + 1 < before.Length do
                                if before[j] = '(' && before[j + 1] = '*' then
                                    depth <- depth + 1
                                    j <- j + 2
                                elif before[j] = '*' && before[j + 1] = ')' then
                                    depth <- depth - 1
                                    j <- j + 2
                                else
                                    j <- j + 1
                            if depth = 0 then
                                // Comment was fully closed; strip it and continue.
                                before <- before.Substring(j).TrimStart()
                            else
                                // Unclosed comment — give up; `recognised` will be false.
                                keepStripping <- false
                        else
                            keepStripping <- false

                    let recognised =
                        [ "let rec"; "let"; "and"; "module rec"; "module"; "type"; "member"; "val"; "new"
                          "static member"; "static"; "abstract member"; "abstract"; "override" ]
                        |> List.exists (fun kw ->
                            before = kw
                            || before.StartsWith(kw + " ", StringComparison.Ordinal)
                            || before.StartsWith(kw + "\t", StringComparison.Ordinal))

                    if recognised then
                        // Drop [pos, afterPos) — the leading space + "private".
                        // The space after "private" (at afterPos) is preserved so the
                        // remaining text reads `let foo = …` not `letfoo = …`.
                        found <- Some(pos, afterPos)

                idx <- pos + 1

        found

    // Synchronous core extracted from the task{} body to keep the F# state
    // machine compilable (avoids FS3511 on long branches in task computation
    // expressions).
    member private this.BuildMakeInternalVisibleResult
        (path: string, args: FcsMakeInternalVisibleArgs, source: string, checkResults: FSharpCheckFileResults)
        : JsonNode =

        let noAction reason =
            jobj
                [ "status", jstr "no_action"
                  "file", jstr path
                  "reason", jstr reason ]
            :> JsonNode

        let fcsLine = args.line + 1
        let lines = source.Split('\n')

        let lineText =
            if fcsLine >= 1 && fcsLine <= lines.Length then
                lines[fcsLine - 1].TrimEnd('\r')
            else
                ""

        // We do NOT use FCS GetSymbolUseAtLocation for the "is there a symbol
        // here" guard — it proved unreliable for module-level let bindings (FCS
        // returns None at the identifier column in test fixtures). FindPrivateSpan
        // is authoritative: it requires (a) a recognized declaration keyword
        // before " private", and (b) the " private" must not be inside a line
        // comment. That covers the failure modes we care about (comments, free
        // text) and avoids the FCS predicate brittleness.
        let _ = checkResults // retained for future extensions; not consulted here

        if String.IsNullOrWhiteSpace lineText then
            noAction
                $"Line %d{args.line} is empty. Move the cursor to the declaration's line."
        else
            match this.FindPrivateSpan(lineText) with
            | None ->
                noAction
                    $"No `private` modifier found on line %d{args.line}. Either the declaration is on a different line, or it is already not private."
            | Some(startCol, endCol) ->
                let appliedPreview =
                    lineText.Substring(0, startCol) + lineText.Substring(endCol)

                let editRange =
                    jobj
                        [ "startLine", jint args.line
                          "startColumn", jint startCol
                          "endLine", jint args.line
                          "endColumn", jint endCol ]
                    :> JsonNode

                let edit = jobj [ "range", editRange; "newText", jstr "" ] :> JsonNode

                jobj
                    [ "status", jstr "ok"
                      "file", jstr path
                      "edits", JsonArray([| edit |]) :> JsonNode
                      "appliedPreview", jstr appliedPreview
                      "originalLineText", jstr lineText ]
                :> JsonNode

    /// Returns a workspace edit that drops the `private` keyword from a
    /// declaration at the given position. Uses FCS only to confirm there IS a
    /// symbol at the position (so we don't strip text inside a comment),
    /// then scans the line for ` private` following a recognized declaration
    /// keyword and returns the edit range. Variant A of #118.
    member this.MakeInternalVisible(args: FcsMakeInternalVisibleArgs) : Task<JsonNode> =
        task {
            match ArgsValidation.requireNonBlank "path" args.path with
            | Error envelope -> return envelope
            | Ok _ ->

            match validateSourcePath "fcs_make_internal_visible" args.text args.path with
            | Some err -> return err
            | None ->

            let! path, source, _, _, _, checkedResults =
                this.PrepareCheckContext(args.path, args.text, args.projectPath, None)

            return
                match checkedResults with
                | None ->
                    jobj
                        [ "status", jstr "aborted"
                          "message", jstr "Type checking was aborted." ]
                    :> JsonNode
                | Some checkResults ->
                    this.BuildMakeInternalVisibleResult(path, args, source, checkResults)
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
            match ArgsValidation.requireNonBlank "query" args.query with
            | Error envelope -> return envelope
            | Ok query ->

            if args.projectPath.IsNone then
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
            match ArgsValidation.requireNonBlank "packageId" args.packageId with
            | Error envelope -> return envelope
            | Ok packageId ->

            if args.projectPath.IsNone then
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

    /// Enumerate members of one specific type from a referenced assembly.
    /// Companion to NugetTypes: resolves the assembly by packageId (exact SimpleName),
    /// finds the entity by typeName (case-insensitive DisplayName or FullName match),
    /// then lists MembersFunctionsAndValues, FSharpFields, and UnionCases.
    member this.NugetMembers(args: FcsNugetMembersArgs) : Task<JsonNode> =
        task {
            match ArgsValidation.requireNonBlank "packageId" args.packageId with
            | Error envelope -> return envelope
            | Ok packageId ->

            match ArgsValidation.requireNonBlank "typeName" args.typeName with
            | Error envelope -> return envelope
            | Ok typeName ->

            if args.projectPath.IsNone then
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

            // FCS exposes generic types with a CLR arity suffix (e.g. `FSharpOption`1`).
            // Strip a trailing `N so callers can pass the bare compiled name (`FSharpOption`).
            let stripArity (s: string) =
                let idx = s.LastIndexOf('`')

                if idx > 0
                   && idx < s.Length - 1
                   && s.Substring(idx + 1) |> Seq.forall Char.IsDigit then
                    s.Substring(0, idx)
                else
                    s

            let typeNameLower = (stripArity typeName).ToLowerInvariant()

            // Case-insensitive match on DisplayName (exact) or FullName (exact or segment-boundary suffix),
            // with the generic-arity suffix stripped from both sides.
            let matchesTypeName (entity: FSharpEntity) =
                try
                    let displayName =
                        try entity.DisplayName |> Option.ofObj |> Option.defaultValue "" with _ -> ""

                    let fullName =
                        try entity.FullName |> Option.ofObj |> Option.defaultValue "" with _ -> ""

                    let displayLower = (stripArity displayName).ToLowerInvariant()
                    let fullLower = (stripArity fullName).ToLowerInvariant()

                    displayLower = typeNameLower
                    || fullLower = typeNameLower
                    || (not (String.IsNullOrEmpty fullLower)
                        && fullLower.EndsWith($".{typeNameLower}"))
                with _ ->
                    false

            let matchedEntities =
                seq {
                    for asm in matchingAssemblies do
                        for entity in allEntitiesFromAssembly asm do
                            if matchesTypeName entity then
                                yield entity
                }
                |> Seq.toList

            let passesMemberAccessibility (m: FSharpMemberOrFunctionOrValue) =
                if includeNonPublic then
                    true
                else
                    let acc = memberAccessibilityString m
                    acc = "public" || acc = "unknown"

            let passesFieldAccessibility (f: FSharpField) =
                if includeNonPublic then
                    true
                else
                    let acc = fieldAccessibilityString f
                    acc = "public" || acc = "unknown"

            let passesUnionCaseAccessibility (uc: FSharpUnionCase) =
                if includeNonPublic then
                    true
                else
                    try
                        let acc = uc.Accessibility
                        acc.IsPublic || (not acc.IsPrivate && not acc.IsInternal)
                    with _ ->
                        true

            let allMembers =
                seq {
                    for entity in matchedEntities do
                        // Methods, properties, constructors, events
                        let mfvs : seq<FSharpMemberOrFunctionOrValue> =
                            try
                                entity.MembersFunctionsAndValues :> seq<_>
                            with _ ->
                                Seq.empty

                        for m in mfvs do
                            // Skip compiler-generated property accessors — the property itself is
                            // already listed, and the get_/set_ method duplicates it.
                            let isAccessor =
                                try
                                    m.IsPropertyGetterMethod || m.IsPropertySetterMethod
                                with _ ->
                                    false

                            if passesMemberAccessibility m && not isAccessor then
                                yield referencedMemberToJson m

                        // Record fields / struct fields
                        try
                            if entity.IsFSharpRecord || entity.IsValueType then
                                for f in entity.FSharpFields do
                                    if passesFieldAccessibility f then
                                        yield referencedFieldToJson f
                        with _ ->
                            ()

                        // F# union cases
                        try
                            if entity.IsFSharpUnion then
                                for uc in entity.UnionCases do
                                    if passesUnionCaseAccessibility uc then
                                        yield referencedUnionCaseToJson uc
                        with _ ->
                            ()
                }
                |> Seq.toArray

            // FCS can surface the same logical member twice (e.g. a property and its
            // compiler-generated accessor) that render to an identical row — collapse those.
            let dedupedMembers =
                allMembers
                |> Array.distinctBy (fun (node: JsonNode) ->
                    let field (name: string) =
                        try
                            match node[name] with
                            | null -> ""
                            | v -> v.GetValue<string>()
                        with _ ->
                            ""

                    field "name", field "kind", field "signature")

            let pageEntries =
                dedupedMembers
                |> Array.skip (min pageOffset dedupedMembers.Length)
                |> Array.truncate pageSize

            let matchedTypeFullNames =
                matchedEntities
                |> List.map (fun e ->
                    try
                        e.FullName |> Option.ofObj |> Option.defaultValue e.DisplayName
                    with _ ->
                        "")
                |> List.filter (fun s -> s <> "")

            let baseFields =
                [ "status", jstr "ok"
                  "packageId", jstr packageId
                  "typeName", jstr typeName
                  "matchedTypes", JsonArray(matchedTypeFullNames |> List.map jstr |> List.toArray) :> JsonNode
                  "projectFileName", jstr options.ProjectFileName
                  "optionsSource", jstr optionsSource
                  "includeNonPublic", jbool includeNonPublic
                  "results", JsonArray(pageEntries) :> JsonNode ]

            let paginationFields =
                Cursor.paginationFields "members" dedupedMembers.Length pageOffset pageSize pageEntries.Length

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

    // ─── fcs_suggest_open (#67) ──────────────────────────────────────────────────

    /// Given an unresolved symbol name (e.g. from FS0039), returns ranked `open`
    /// directive candidates: project-local symbols first, referenced assemblies second.
    member this.SuggestOpen(args: FcsSuggestOpenArgs) : Task<JsonNode> =
        task {
            match ArgsValidation.requireNonBlank "symbolName" args.symbolName with
            | Error envelope -> return envelope
            | Ok symbolName ->

            if args.projectPath.IsNone then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "projectPath is required (or call set_project first to set the active project)" ]
                    :> JsonNode
            else

            let includeReferences = defaultArg args.includeReferences true
            let requested         = defaultArg args.maxResults 20
            let pageSize          = min (max 1 requested) 100

            let! projectResults, options, _optionsSource = this.EnsureProjectResults args.projectPath

            // ── A. Project-local candidates ─────────────────────────────────────
            // Walk every symbol use recorded during ParseAndCheckProject.
            // Filter for FSharpEntity whose DisplayName exactly equals symbolName.
            let projectCandidates =
                try
                    projectResults.GetAllUsesOfAllSymbols()
                    |> Array.choose (fun symbolUse ->
                        match symbolUse.Symbol with
                        | :? FSharpEntity as entity when entity.DisplayName = symbolName ->
                            let accessPath =
                                try entity.AccessPath |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                            let fullName =
                                try entity.FullName   |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                            let asmName =
                                try entity.Assembly.SimpleName |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                            Some (accessPath, fullName, asmName)
                        | _ -> None)
                    |> Array.distinctBy (fun (ap, fn, _) -> (ap, fn))
                with _ ->
                    [||]

            // ── B. Referenced-assembly candidates ────────────────────────────────
            let referenceCandidates =
                if not includeReferences then
                    [||]
                else
                    try
                        let assemblies = projectResults.ProjectContext.GetReferencedAssemblies()
                        seq {
                            for asm in assemblies do
                                let asmName =
                                    try asm.SimpleName |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                                for entity in allEntitiesFromAssembly asm do
                                    let dn = try entity.DisplayName with _ -> ""
                                    if dn = symbolName then
                                        let accessPath =
                                            try entity.AccessPath |> Option.ofObj |> Option.defaultValue ""
                                            with _ -> ""
                                        let fullName =
                                            try entity.FullName |> Option.ofObj |> Option.defaultValue ""
                                            with _ -> ""
                                        yield (accessPath, fullName, asmName)
                        }
                        |> Seq.distinctBy (fun (ap, fn, _) -> (ap, fn))
                        |> Seq.toArray
                    with _ ->
                        [||]

            // ── C. Build deduplicated ranked list ────────────────────────────────
            // Project hits first (more relevant — same codebase), then references.
            // Within each tier deduplicate by (openPath, entityFullName).

            let toCandidate source (accessPath: string, fullName: string, asmName: string) =
                // Determine kind + accessibility from the entity (best effort via FullName lookup).
                // We re-use data already in the tuple; expensive entity re-lookup is avoided.
                jobj
                    [ "openPath",       jstr accessPath
                      "entityFullName", jstr fullName
                      "source",         jstr source
                      "assembly",       jstr asmName
                      "kind",           jstr "unknown"      // enriched below when entity is available
                      "accessibility",  jstr "unknown" ]
                :> JsonNode

            // Produce candidate JsonNodes from both tiers.
            // We need kind + accessibility, so we re-walk — but only for project and only
            // for the entries that survive deduplication.
            let projectSet  = projectCandidates  |> Set.ofArray
            let referenceSet = referenceCandidates |> Set.ofArray

            // Remove reference entries that are already covered by a project entry
            // (same openPath + fullName).
            let referenceUnique =
                referenceCandidates
                |> Array.filter (fun (ap, fn, _) -> not (projectSet |> Set.exists (fun (ap2, fn2, _) -> ap = ap2 && fn = fn2)))

            let allCandidates =
                [| yield! projectCandidates  |> Array.truncate pageSize |> Array.map (toCandidate "project")
                   yield! referenceUnique |> Array.truncate pageSize |> Array.map (toCandidate "reference") |]

            // ── D. Enrich kind + accessibility for project-tier via symbol walk ──
            // Build a lookup: (accessPath, fullName) → (kind, accessibility)
            // This re-uses the existing symbolKind helper on FSharpEntity.
            let projectEnrichment =
                try
                    projectResults.GetAllUsesOfAllSymbols()
                    |> Array.choose (fun symbolUse ->
                        match symbolUse.Symbol with
                        | :? FSharpEntity as entity when entity.DisplayName = symbolName ->
                            let accessPath =
                                try entity.AccessPath |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                            let fullName =
                                try entity.FullName   |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                            let kind = entityKindString entity
                            let acc  = entityAccessibilityString entity
                            Some ((accessPath, fullName), (kind, acc))
                        | _ -> None)
                    |> Array.distinctBy fst
                    |> Map.ofArray
                with _ ->
                    Map.empty

            // Enrich reference-tier via walking assemblies again for the surviving entries.
            let referenceEnrichment =
                if not includeReferences then
                    Map.empty
                else
                    try
                        let assemblies = projectResults.ProjectContext.GetReferencedAssemblies()
                        seq {
                            for asm in assemblies do
                                for entity in allEntitiesFromAssembly asm do
                                    let dn = try entity.DisplayName with _ -> ""
                                    if dn = symbolName then
                                        let accessPath =
                                            try entity.AccessPath |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                                        let fullName =
                                            try entity.FullName |> Option.ofObj |> Option.defaultValue "" with _ -> ""
                                        let key = (accessPath, fullName)
                                        let kind = entityKindString entity
                                        let acc  = entityAccessibilityString entity
                                        yield key, (kind, acc)
                        }
                        |> Seq.distinctBy fst
                        |> Map.ofSeq
                    with _ ->
                        Map.empty

            // Patch kind + accessibility in each candidate node.
            let enriched =
                allCandidates
                |> Array.map (fun node ->
                    let ap  = node["openPath"].GetValue<string>()
                    let fn  = node["entityFullName"].GetValue<string>()
                    let src = node["source"].GetValue<string>()
                    let lookup = if src = "project" then projectEnrichment else referenceEnrichment
                    match Map.tryFind (ap, fn) lookup with
                    | Some (kind, acc) ->
                        let obj = node :?> JsonObject
                        obj["kind"]          <- jstr kind
                        obj["accessibility"] <- jstr acc
                        node
                    | None -> node)

            return
                jobj
                    [ "status",           jstr "ok"
                      "symbolName",       jstr symbolName
                      "projectFileName",  jstr options.ProjectFileName
                      "candidateCount",   jint enriched.Length
                      "candidates",       JsonArray(enriched) :> JsonNode ]
                :> JsonNode
        }
