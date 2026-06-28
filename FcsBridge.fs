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


// ─── check: cached FSAC diagnostics snapshot (issue #128, Stage 1) ──────────────
//
// The `check` tool's speed="fast" path reads the cheap cached FSAC publishDiagnostics
// snapshot instead of re-type-checking in FCS. The FSAC dictionary lives in
// FsAutoCompleteBridge, so — exactly like `find` injecting its fsacProbe — the
// dispatcher projects a workspace_diagnostics response into this struct and hands it
// to the FCS-only substrate, which therefore stays LSP-agnostic.
//
// Honesty contract: `MostRecentAnalyzedAt = None` (or `Ready = false`) is the stale
// `{}` ambiguity from #100 — FSAC has not pushed analysis yet, so absence of cached
// errors does NOT mean "clean". The fast path turns that into verdict="unknown"
// rather than a false-clean.
[<NoComparison; NoEquality>]
type internal CheckFsacSnapshot =
    { /// FSAC workspace reached the "ready" state (lspState = "ready").
      Ready: bool
      /// Number of files FSAC currently holds diagnostics for.
      AnalyzedFileCount: int
      /// Error-severity (LSP code 1) diagnostics across the snapshot.
      ErrorCount: int
      /// Warning-severity (LSP code 2) diagnostics across the snapshot.
      WarningCount: int
      /// Most recent analyzedAt / mostRecentAnalyzedAt timestamp, when present.
      /// None is the un-analyzed (stale-`{}`) case — the fast path cannot confirm clean.
      MostRecentAnalyzedAt: string option
      /// ALL-severity diagnostics, LSP-shaped (each carries its `severity` code),
      /// deep-cloned for surfacing. The fast path filters these by the requested
      /// severity floor so check(speed="fast", severity="warning"|"all") surfaces
      /// the warnings the caller asked for — not just errors.
      Diagnostics: JsonNode }

[<RequireQualifiedAccess>]
module internal CheckFsacSnapshot =

    let empty: CheckFsacSnapshot =
        { Ready = false
          AnalyzedFileCount = 0
          ErrorCount = 0
          WarningCount = 0
          MostRecentAnalyzedAt = None
          Diagnostics = JsonArray() }

    /// Projects a workspace_diagnostics response (file or workspace shape) into a
    /// CheckFsacSnapshot. Tolerant of missing fields — anything it cannot read
    /// degrades toward `empty`, which the fast path reads as "unknown".
    let ofDiagnosticsResponse (resp: JsonNode) : CheckFsacSnapshot =
        try
            let ready =
                match resp["lspState"] with
                | :? JsonValue as v ->
                    match v.GetValue<string>() with
                    | "ready" -> true
                    | _ -> false
                | _ -> false

            let fileCount =
                match resp["diagnosticsFileCount"] with
                | :? JsonValue as v ->
                    let mutable n = 0
                    if v.TryGetValue(&n) then n else 0
                | _ -> 0

            let analyzedAt =
                let read (key: string) =
                    match resp[key] with
                    | :? JsonValue as v ->
                        match v.GetValue<string>() with
                        | null -> None
                        | s when String.IsNullOrWhiteSpace s -> None
                        | s -> Some s
                    | _ -> None

                read "mostRecentAnalyzedAt" |> Option.orElseWith (fun () -> read "analyzedAt")

            let mutable errorCount = 0
            let mutable warningCount = 0
            // ALL severities are retained (each node keeps its LSP `severity` code) so the
            // fast path can surface by the requested floor. errorCount/warningCount stay
            // the FULL-set tallies the caller-facing counts are built from.
            let diags = JsonArray()

            let severityOf (obj: JsonObject) =
                match obj["severity"] with
                | :? JsonValue as s ->
                    let mutable code = 0
                    if s.TryGetValue(&code) then code else 0
                | _ -> 0

            let consume (file: string option) (arr: JsonArray) =
                for node in arr do
                    match node with
                    | :? JsonObject as obj ->
                        match severityOf obj with
                        | 1 -> errorCount <- errorCount + 1
                        | 2 -> warningCount <- warningCount + 1
                        | _ -> ()

                        // Retain every diagnostic (all severities), patching `file` for the
                        // workspace shape where it is the dictionary key, not a node field.
                        let clone = node.DeepClone()

                        match file, clone with
                        | Some f, (:? JsonObject as co) -> co["file"] <- jstr f
                        | _ -> ()

                        diags.Add(clone)
                    | _ -> ()

            match resp["result"] with
            | :? JsonArray as arr -> consume None arr
            | :? JsonObject as obj ->
                for kv in obj do
                    match kv.Value with
                    | :? JsonArray as arr -> consume (Some kv.Key) arr
                    | _ -> ()
            | _ -> ()

            { Ready = ready
              AnalyzedFileCount = fileCount
              ErrorCount = errorCount
              WarningCount = warningCount
              MostRecentAnalyzedAt = analyzedAt
              Diagnostics = diags }
        with _ ->
            empty

// ─── FcsBridge ─────────────────────────────────────────────────────────────────

type internal FcsBridge() =
    // FCS default projectCacheSize is 3. The `find` multi-project union sweep
    // (issue #128) re-checks EVERY member project of the active solution on each
    // call; with a cache of 3 a >3-project solution thrashes FCS's project cache
    // and re-pays the cold type-check cost every sweep. Raise it to a
    // solution-scale bound so a whole solution stays warm between sweeps. We
    // capture the value so RuntimeStatus can report it.
    let defaultProjectCacheSize = 50
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
    // Sized to keep a whole solution's resolved options warm during a `find` sweep
    // (issue #128) — a 10-entry cache would evict early projects mid-sweep on a
    // >10-project solution, forcing redundant Ionide.ProjInfo re-resolution.
    let optionsCache = BoundedCache<string, FSharpProjectOptions * string>(50)
    let projectResultsCache = BoundedCache<string, FSharpCheckProjectResults>(3)

    // find sweep (issue #131): memoize each project's whole-symbol-use enumeration +
    // diagnostics. FCS's project cache keeps ParseAndCheckProject warm, but
    // GetAllUsesOfAllSymbols() is NOT memoized — it re-walks every recorded symbol use
    // (~16-18k/project) on EVERY call, so a warm `find` re-paid ~3s/project. The cache
    // key is (resolved-options key + source-file write-time stamp + referenced-assembly
    // write-time stamp): any on-disk source edit moves a file's mtime → the stamp changes
    // → cache MISS; and any rebuild of a referenced project (P2P) or assembly moves THAT
    // file's mtime → the consumer's stamp changes → cache MISS (0.10.1 Codex P1 fix:
    // without the referenced-assembly stamp a dependency rebuild left consumers stale).
    // Either MISS runs the original ParseAndCheckProject + GetAllUsesOfAllSymbols path. A
    // cached `find` is therefore never staler than an uncached one (a miss is byte-for-byte
    // the prior behaviour) — correctness comes first, speed is the unchanged-project fast
    // path.
    // Cleared by ClearCaches() (so set_project invalidates it). Sized to the same
    // solution scale as optionsCache so a whole solution stays warm between sweeps.
    let projectUsesCache =
        BoundedCache<string, FSharpSymbolUse array * FSharpDiagnostic array>(50)

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

    // Per-file write-time vector for the find use-cache (issue #131). The stamp changes
    // when ANY source file's on-disk mtime changes (not just the newest), so an edit to
    // an older file still invalidates. One stat per source file — sub-millisecond even
    // for large projects, and the same signal FCS itself uses to decide staleness, so a
    // cache miss and an FCS re-check stay in lockstep. Missing/unreadable files map to
    // -1 (a removed file changes the vector; the .fsproj re-resolve changes the options
    // key independently).
    let sourceFilesStamp (projectOptions: FSharpProjectOptions) =
        let sb = System.Text.StringBuilder()

        for f in projectOptions.SourceFiles do
            let ticks =
                try
                    if File.Exists f then File.GetLastWriteTimeUtc(f).Ticks else -1L
                with _ ->
                    -1L

            sb.Append(ticks).Append('|') |> ignore

        sb.ToString()

    // Per-reference write-time vector for the find use-cache (issue #131; 0.10.1 Codex
    // P1). sourceFilesStamp alone is BLIND to cross-project rebuilds: when project A
    // references project B (P2P) — or any other assembly — a rebuild of B leaves A's own
    // SourceFiles untouched, so A's source-stamp (and therefore A's cache key) would NOT
    // move, and ProjectSweepUses would serve A's sweep STALE even though a fresh
    // ParseAndCheckProject would see B's new metadata. Folding the referenced-assembly
    // mtimes into the key closes that gap: B's rebuilt output (the -r: target a consumer
    // resolves to — for a P2P that is B's obj/.../ref/B.dll reference assembly) moves its
    // mtime → every consumer's key changes → cache MISS → fresh sweep.
    //
    // We stamp EVERY -r:/--reference: target, framework and NuGet refs included.
    // Correctness-first by deliberate choice: a path-based "this ref is immutable" skip
    // would risk misclassifying a mutable ref (relocated NuGet caches via NUGET_PACKAGES,
    // non-standard SDK install roots, copied-local DLLs) and reintroducing exactly this
    // staleness bug, while the saving is marginal — the stats are OS-metadata-cached and
    // sub-millisecond in aggregate even for ~150 BCL refs, so a warm find stays in the
    // cache-hit fast path. A reference *path* change (NuGet/SDK version bump) already moves
    // the options key via makeResolvedProjectCacheKey, which concatenates OtherOptions;
    // this stamp adds the orthogonal *content-at-a-fixed-path* signal. Missing/unreadable
    // files map to -1, mirroring sourceFilesStamp — never throws.
    let referencedAssembliesStamp (projectOptions: FSharpProjectOptions) =
        let sb = System.Text.StringBuilder()

        for opt in projectOptions.OtherOptions do
            let refPath =
                if opt.StartsWith("-r:", StringComparison.Ordinal) then
                    Some(opt.Substring 3)
                elif opt.StartsWith("--reference:", StringComparison.Ordinal) then
                    Some(opt.Substring 12)
                else
                    None

            match refPath with
            | Some path ->
                let ticks =
                    try
                        if File.Exists path then File.GetLastWriteTimeUtc(path).Ticks else -1L
                    with _ ->
                        -1L

                sb.Append(ticks).Append('|') |> ignore
            | None -> ()

        sb.ToString()

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

    // ── find: ParseAndCheckProject warm-up for one project (issue #128) ──────────
    // Used by the set_project fire-and-forget pre-warm so the FIRST `find` sweep
    // hits FCS's (now solution-scale) project cache instead of paying cold cost.
    // Swallows all failures — pre-warming is a latency optimization, never a gate.
    member this.PrewarmProject(fsproj: string) : Task<unit> =
        task {
            try
                let! options, _ = this.ResolveFsprojOptions(normalizePath fsproj)
                let! _ = checker.ParseAndCheckProject(options) |> asTask
                return ()
            with _ ->
                return ()
        }

    // ── find: resolve the symbol name under a cursor (kind=position) ─────────────
    // Returns Ok displayName (fed to the union sweep as an exact query) or Error
    // envelope. Mirrors fcs_symbol_at_word's tolerant resolution: line + optional
    // word/occurrence/character.
    //
    // Extracted from ResolveQueryAtPosition to keep the outer task {} state machine
    // simple enough for static compilation (FS3511 fix: same pattern as ProjectSweepUses).
    // All computation here is synchronous — no awaits — so the return type is plain Result.
    member private _.ResolvePositionInFile
        (path: string,
         line: int,
         source: string,
         checkedResults: FSharpCheckFileResults option,
         args: FindArgs)
        : Result<string, JsonNode> =
        match checkedResults with
        | None ->
            Error(
                jobj
                    [ "status", jstr "aborted"
                      "message", jstr "Type checking was aborted at the requested position." ]
                :> JsonNode
            )
        | Some checkResults ->
            let lines = sourceLines source

            if line < 0 || line >= lines.Length then
                Error(
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr $"line {line} is out of range (file has {lines.Length} lines)." ]
                    :> JsonNode
                )
            else
                let lineText = lines[line]
                let candidates = wordSpans args.word lineText

                if candidates.Length = 0 then
                    Error(
                        jobj
                            [ "status", jstr "no_candidate"
                              "message", jstr "No identifier found at the requested position." ]
                        :> JsonNode
                    )
                else
                    let occurrence = args.occurrence |> Option.defaultValue -1

                    let candidateIndex =
                        match args.character with
                        | Some ch ->
                            candidates
                            |> Array.tryFindIndex (fun (s, e, _) -> ch >= s && ch <= e)
                            |> Option.defaultValue 0
                        | None -> if occurrence < 0 then 0 else min occurrence (candidates.Length - 1)

                    let startColumn, endColumn, text = candidates[candidateIndex]
                    let fcsLine = line + 1
                    let columnsToTry = [| endColumn; startColumn + 1; startColumn |] |> Array.distinct

                    let symbolUse =
                        columnsToTry
                        |> Array.tryPick (fun column ->
                            checkResults.GetSymbolUseAtLocation(fcsLine, column, lineText, [ text ]))

                    match symbolUse with
                    | Some u ->
                        // kind=position resolved THE specific symbol under the cursor.
                        // Key the subsequent sweep on its FullName so the match stays
                        // precise: DisplayName alone would also sweep an unrelated
                        // `Config` in another namespace (or every same-named overload).
                        // Locals / synthetic symbols may carry no useful FullName, so
                        // fall back to DisplayName there.
                        let key =
                            try
                                match u.Symbol.FullName with
                                | null -> u.Symbol.DisplayName
                                | fn when String.IsNullOrWhiteSpace fn -> u.Symbol.DisplayName
                                | fn -> fn
                            with _ ->
                                u.Symbol.DisplayName

                        Ok key
                    | None ->
                        Error(
                            jobj
                                [ "status", jstr "no_symbol"
                                  "message",
                                  jstr
                                      $"Could not resolve a symbol at {Path.GetFileName path}:{line}." ]
                            :> JsonNode
                        )

    // Outer shell: validates args and awaits type-check, then delegates the purely
    // synchronous symbol resolution to ResolvePositionInFile. Keeping this task {}
    // free of nested match-over-task-result branches makes it statically compilable
    // (FS3511 fix — same technique as ProjectSweepUses).
    member private this.ResolveQueryAtPosition(args: FindArgs) : Task<Result<string, JsonNode>> =
        task {
            match args.path with
            | None ->
                return
                    Error(
                        jobj
                            [ "status", jstr "invalid_args"
                              "message", jstr "kind='position' requires 'path' (and 'line')." ]
                        :> JsonNode
                    )
            | Some path when String.IsNullOrWhiteSpace path ->
                return
                    Error(
                        jobj
                            [ "status", jstr "invalid_args"
                              "message", jstr "kind='position' requires 'path' (and 'line')." ]
                        :> JsonNode
                    )
            | Some path ->
                match args.line with
                | None ->
                    return
                        Error(
                            jobj
                                [ "status", jstr "invalid_args"
                                  "message", jstr "kind='position' requires 'line' (0-based)." ]
                            :> JsonNode
                        )
                | Some line ->
                    // FindArgs carries no unsaved-buffer field; resolve position against on-disk content.
                    match validateSourcePath "find" None path with
                    | Some err -> return Error err
                    | None ->
                        let! _, source, _, _, _, checkedResults =
                            this.PrepareCheckContext(path, None, args.projectPath, None)

                        return this.ResolvePositionInFile(path, line, source, checkedResults, args)
        }

    // ── find: multi-project union sweep (issue #128, Stage 1) ───────────────────
    // Productionized from spike/find-multiproject-sweep. Sweeps every member
    // .fsproj of the active solution, unions each project's GetAllUsesOfAllSymbols()
    // (de-duped by FULL source range), and auto-unions record-field and member
    // usage sites the single-project fcs_find_symbol / fcs_record_field_audit miss.
    //
    // HEADLINE TRUST PROPERTY: matched=false is reported ONLY when BOTH the FCS
    // multi-project sweep AND the FSAC workspace/symbol index (via the injected
    // fsacProbe) are empty — never a confident "absent" from a single project,
    // which would false-negative cross-project symbols (the RiskDebatorRole /
    // TraderRole.Propose port-widening cases).
    //
    // FCS CROSS-COMPILATION INVARIANT (do NOT "fix" with ==): FSharpSymbol instances
    // from DIFFERENT ParseAndCheckProject compilations are NOT reference-equal for
    // the same logical symbol. We match the target by stable strings (DisplayName /
    // FullName via symbolMatches; DeclaringEntity.DisplayName for fields/members)
    // and de-dup by source LOCATION, never by symbol identity. FSharpSymbolUse is a
    // struct, so we bind `let r = u.Range` before reading r.FileName (FS0052).

    // issue #131: cache-or-compute one project's whole-symbol-use enumeration +
    // diagnostics, keyed by (resolved-options + source-stamp). Kept in its own method
    // so Find's per-project loop awaits a plain Task instead of nesting a task CE
    // (which is not statically compilable under Release optimization → FS3511).
    member private _.ProjectSweepUses
        (usesKey: string, options: FSharpProjectOptions)
        : Task<FSharpSymbolUse array * FSharpDiagnostic array> =
        task {
            match projectUsesCache.TryGet(usesKey) with
            | Some cached -> return cached
            | None ->
                let! results = checker.ParseAndCheckProject(options) |> asTask
                let uses = results.GetAllUsesOfAllSymbols()
                let diags = results.Diagnostics
                projectUsesCache.Set(usesKey, (uses, diags))
                return uses, diags
        }

    member this.Find(args: FindArgs, ?fsacProbe: string -> Task<int>) : Task<JsonNode> =
        task {
            match ArgsValidation.requireNonBlank "query" args.query with
            | Error envelope -> return envelope
            | Ok query0 ->

            let kind = (args.kind |> Option.defaultValue "auto").Trim().ToLowerInvariant()
            let scope = (args.scope |> Option.defaultValue "auto").Trim().ToLowerInvariant()
            let exact = args.exact |> Option.defaultValue true
            // Compact by default: 0 context lines → one line per site (lineText only),
            // no before/after arrays. A bare find on a hot symbol must never overflow
            // the MCP token ceiling (issue: 0.10.0 dogfooding — 50 sites × ctx=1 = 73k
            // chars). Surrounding context is strictly opt-in via contextLines > 0.
            let contextLines = args.contextLines |> Option.defaultValue 0
            let includeDeclaration = args.includeDeclaration |> Option.defaultValue true
            let includeInfo = args.includeInfo |> Option.defaultValue false
            // Default page size keeps the compact payload well under the MCP token
            // ceiling on a cap-case hit. Each site is now serialized ONCE — the flat
            // `sites` list — after the grouped definitions/references/fieldSites/
            // memberSites buckets (which duplicated every site) were dropped, so the
            // per-site cost dropped from ~1030 to ~527 chars (measured on
            // find("FindArgs")). With one representation the default is raised from 40
            // to 80: a full 80-site page is ~43k chars (~14k tokens, under the
            // ~25k-token ceiling ≈ ~72k chars), and a 69-site hot symbol now fits one
            // page at ~37k chars instead of truncating at 40. breakdown + totalSites
            // still report the FULL set, and cursor/nextCursor pages the rest, so a
            // complete refactor list stays reachable past the default.
            let pageSize = args.maxResults |> Option.defaultValue 80

            let pageOffset =
                match args.cursor with
                | None -> 0
                | Some cursorStr ->
                    match Cursor.tryDecode cursorStr with
                    | Ok payload -> payload.offset
                    | Error reason -> invalidArg (nameof args.cursor) $"Invalid cursor: %s{reason}"

            // kind=position resolves the symbol under the cursor, then sweeps it as a symbol.
            let! queryResult =
                task {
                    if kind = "position" then
                        return! this.ResolveQueryAtPosition(args)
                    else
                        return Ok query0
                }

            match queryResult with
            | Error envelope -> return envelope
            | Ok query ->

            let kindResolved = if kind = "position" then "symbol" else kind

            // Resolve the sweep target: explicit projectPath (already falls back to the
            // active set_project in Program.fs), else the nearest .fsproj to args.path.
            let sweepTargetOpt =
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath
                |> Option.orElseWith (fun () -> args.path |> Option.bind findNearestFsproj |> Option.map normalizePath)

            match sweepTargetOpt with
            | None ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "find needs a project context: pass projectPath (.fsproj/.sln/.slnx) or path, or call set_project first." ]
                    :> JsonNode
            | Some sweepTarget ->

            let memberProjects = SolutionParsing.listProjects sweepTarget

            // scope=file/project narrows to the single owning project; workspace/auto
            // sweeps every member project of the solution.
            let projectsToSweep =
                match scope with
                | "file"
                | "project" ->
                    let single =
                        args.path
                        |> Option.bind findNearestFsproj
                        |> Option.orElseWith (fun () ->
                            if sweepTarget.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                                Some sweepTarget
                            else
                                None)

                    match single with
                    | Some p -> [| normalizePath p |]
                    | None -> memberProjects
                | _ -> memberProjects

            if projectsToSweep.Length = 0 then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr $"find could not resolve any .fsproj to sweep from: {sweepTarget}" ]
                    :> JsonNode
            else

            // ── Matching predicates (stable-string, cross-compilation-safe) ───────
            let fullNameBoundaryMatch (fullName: string) =
                not (isNull fullName)
                && (String.Equals(fullName, query, StringComparison.Ordinal)
                    || (fullName.EndsWith(query, StringComparison.Ordinal)
                        && fullName.Length > query.Length
                        && fullName[fullName.Length - query.Length - 1] = '.'))

            // Declaring-type predicate for field/member sites. Honors `exact` the same
            // way symbolMatches does for the name branch: exact=false must do substring
            // matching so query="role", field="Propose" still reaches TraderRole.Propose.
            let entityMatchesQuery (e: FSharpEntity) =
                try
                    if exact then
                        String.Equals(e.DisplayName, query, StringComparison.Ordinal)
                        || fullNameBoundaryMatch e.FullName
                    else
                        (not (isNull e.DisplayName)
                         && e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        || (not (isNull e.FullName)
                            && e.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
                with _ ->
                    false

            let fieldRestrict = args.field
            let memberRestrict = args.``member``

            let isQueriedField (symbolUse: FSharpSymbolUse) =
                match symbolUse.Symbol with
                | :? FSharpField as field ->
                    try
                        let nameOk =
                            match fieldRestrict with
                            | Some fn -> String.Equals(field.Name, fn, StringComparison.Ordinal)
                            | None -> true

                        nameOk
                        && (match field.DeclaringEntity with
                            | Some e -> entityMatchesQuery e
                            | None -> false)
                    with _ ->
                        false
                | _ -> false

            let isQueriedMember (symbolUse: FSharpSymbolUse) =
                match symbolUse.Symbol with
                | :? FSharpMemberOrFunctionOrValue as m when m.IsMember ->
                    try
                        let nameOk =
                            match memberRestrict with
                            | Some mn -> String.Equals(m.DisplayName, mn, StringComparison.Ordinal)
                            | None -> true

                        nameOk
                        && (match m.DeclaringEntity with
                            | Some e -> entityMatchesQuery e
                            | None -> false)
                    with _ ->
                        false
                | _ -> false

            let wantName =
                kindResolved = "auto" || kindResolved = "symbol" || kindResolved = "definition"

            let wantDefsOnly = kindResolved = "definition"
            let wantField = kindResolved = "auto" || kindResolved = "field"
            let wantMember = kindResolved = "auto" || kindResolved = "members"

            // De-dup accumulator keyed by stable source location.
            // NOTE: we store only primitive coordinates, NOT the FCS `range` struct —
            // `range` carries [<NoComparison>], and anonymous records auto-derive
            // comparison, which would fail under warnings-as-errors. The range JSON is
            // rebuilt from these fields in siteToJson.
            let siteByKey =
                System.Collections.Generic.Dictionary<
                    string,
                    {| File: string
                       StartLine: int
                       StartCol: int
                       EndLine: int
                       EndCol: int
                       Kind: string
                       Project: string
                       FullName: string |}
                 >()

            let perProject = ResizeArray<JsonNode>()
            let aggregatedDiagnostics = ResizeArray<FSharpDiagnostic>()
            let sweepSw = System.Diagnostics.Stopwatch.StartNew()

            let locationKey (r: range) =
                $"{normalizePath r.FileName}:{r.StartLine}:{r.StartColumn}:{r.EndLine}:{r.EndColumn}"

            // Per-project sweep. Sequential by design: parallel Ionide.ProjInfo option
            // resolution races on MSBuild's *.nuget.g.props for sibling projects that
            // share a P2P reference; FCS also serializes ParseAndCheckProject internally,
            // so the set_project pre-warm + solution-scale cache (not intra-call
            // parallelism) is what removes the cold cost. See the spike's -m:1 finding.
            for fsproj in projectsToSweep do
                let projSw = System.Diagnostics.Stopwatch.StartNew()
                let projDisplay = Path.GetFileNameWithoutExtension fsproj

                try
                    let! options, _ = this.ResolveFsprojOptions(fsproj)

                    // issue #131: memoize the whole-symbol-use enumeration per (project,
                    // source-stamp, referenced-assembly-stamp). A cache HIT on an unchanged
                    // project skips BOTH ParseAndCheckProject AND the ~3s
                    // GetAllUsesOfAllSymbols re-walk; a MISS (first sweep, any source edit,
                    // OR any rebuild of a referenced project/assembly — 0.10.1 Codex P1)
                    // moves the stamp and runs the identical original path, so results are
                    // never served stale. The cache-or-compute lives in its own method
                    // (ProjectSweepUses) so this outer state machine stays statically
                    // compilable under Release optimization (a nested task CE inside the
                    // loop trips FS3511).
                    let usesKey =
                        $"{makeResolvedProjectCacheKey options}|{sourceFilesStamp options}|{referencedAssembliesStamp options}"
                    let! allUses, projDiagnostics = this.ProjectSweepUses(usesKey, options)
                    aggregatedDiagnostics.AddRange(projDiagnostics)

                    // Per-file record-field form classifier (literal vs with-update).
                    let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)

                    let formCache =
                        System.Collections.Generic.Dictionary<
                            string,
                            System.Collections.Generic.Dictionary<FieldFormKey, bool> option
                         >()

                    let classifyForms (filePath: string) =
                        match formCache.TryGetValue filePath with
                        | true, cached -> cached
                        | _ ->
                            let parsed =
                                try
                                    if File.Exists filePath then
                                        let st = SourceText.ofString (File.ReadAllText filePath)
                                        let p = checker.ParseFile(filePath, st, parsingOptions) |> Async.RunSynchronously
                                        Some(FieldFormClassifier.classify p.ParseTree)
                                    else
                                        None
                                with _ ->
                                    None

                            formCache[filePath] <- parsed
                            parsed

                    let fieldKind (symbolUse: FSharpSymbolUse) =
                        let r = symbolUse.Range

                        match classifyForms (normalizePath r.FileName) with
                        | Some d ->
                            match d.TryGetValue((r.StartLine, r.StartColumn)) with
                            | true, true -> "field-set-update"
                            | true, false -> "field-set-literal"
                            | _ -> "field-read"
                        | None -> "field-read"

                    let mutable nameCount = 0
                    let mutable fieldCount = 0
                    let mutable memberCount = 0

                    let add (symbolUse: FSharpSymbolUse) (siteKind: string) (overwrite: bool) =
                        let r = symbolUse.Range
                        let key = locationKey r

                        if overwrite || not (siteByKey.ContainsKey key) then
                            siteByKey[key] <-
                                {| File = normalizePath r.FileName
                                   StartLine = r.StartLine
                                   StartCol = r.StartColumn
                                   EndLine = r.EndLine
                                   EndCol = r.EndColumn
                                   Kind = siteKind
                                   Project = projDisplay
                                   FullName =
                                    (match symbolUse.Symbol.FullName with
                                     | null -> null
                                     | s -> s) |}

                    // Field/member sites first so their richer kind wins over a generic
                    // "reference" tag when a non-exact name-match overlaps the same range.
                    if wantField then
                        for u in allUses do
                            if not u.IsFromDefinition && isQueriedField u then
                                fieldCount <- fieldCount + 1
                                add u (fieldKind u) true

                    if wantMember then
                        for u in allUses do
                            if not u.IsFromDefinition && isQueriedMember u then
                                memberCount <- memberCount + 1
                                add u "member-usage" true

                    if wantName then
                        for u in allUses do
                            if symbolMatches query exact u.Symbol then
                                let passDefsOnly = (not wantDefsOnly) || u.IsFromDefinition
                                let passDecl = wantDefsOnly || includeDeclaration || not u.IsFromDefinition

                                if passDefsOnly && passDecl then
                                    nameCount <- nameCount + 1
                                    let k = if u.IsFromDefinition then "definition" else "reference"
                                    add u k false

                    projSw.Stop()

                    perProject.Add(
                        jobj
                            [ "project", jstr projDisplay
                              "fsproj", jstr (normalizePath fsproj)
                              "totalProjectSymbolUses", jint allUses.Length
                              "nameMatchUses", jint nameCount
                              "fieldMatchUses", jint fieldCount
                              "memberMatchUses", jint memberCount
                              "elapsedMs", jint (int projSw.ElapsedMilliseconds) ]
                        :> JsonNode
                    )
                with ex ->
                    projSw.Stop()

                    perProject.Add(
                        jobj
                            [ "project", jstr projDisplay
                              "fsproj", jstr (normalizePath fsproj)
                              "error", jstr ex.Message
                              "elapsedMs", jint (int projSw.ElapsedMilliseconds) ]
                        :> JsonNode
                    )

            sweepSw.Stop()

            // scope=file post-filters to args.path's file (still type-checked in-project).
            let allSites0 = siteByKey.Values |> Seq.toArray

            let allSites =
                if scope = "file" then
                    match args.path with
                    | Some p when not (String.IsNullOrWhiteSpace p) ->
                        let pf = normalizePath p
                        allSites0 |> Array.filter (fun s -> String.Equals(s.File, pf, StringComparison.Ordinal))
                    | _ -> allSites0
                else
                    allSites0

            let sortedSites =
                allSites
                |> Array.sortBy (fun s -> s.File, s.StartLine, s.StartCol, s.EndLine, s.EndCol, s.Kind)

            let countKind (k: string) =
                sortedSites |> Array.filter (fun s -> s.Kind = k) |> Array.length

            let defCount = countKind "definition"
            let refCount = countKind "reference"
            let fLit = countKind "field-set-literal"
            let fUpd = countKind "field-set-update"
            let fRead = countKind "field-read"
            let memCount = countKind "member-usage"
            let totalSites = sortedSites.Length

            // HEADLINE: matched is true if the FCS sweep found anything; only when it
            // is empty do we consult the FSAC symbol index, and only when THAT is also
            // empty do we report matched=false.
            let fcsMatched = totalSites > 0

            let! fsacHits =
                task {
                    if fcsMatched then
                        return 0
                    else
                        match fsacProbe with
                        | Some probe ->
                            try
                                return! probe query
                            with _ ->
                                return 0
                        | None -> return 0
                }

            let matched = fcsMatched || fsacHits > 0

            let via =
                if fcsMatched then "fcs-multiproject-sweep"
                elif fsacHits > 0 then "fsac-symbol-index"
                else "none"

            let pageSites =
                sortedSites |> Array.skip (min pageOffset totalSites) |> Array.truncate pageSize

            let siteToJson (s: {| File: string
                                  StartLine: int
                                  StartCol: int
                                  EndLine: int
                                  EndCol: int
                                  Kind: string
                                  Project: string
                                  FullName: string |}) =
                let ctx = lineContextToJson contextLines s.File s.StartLine

                let rangeNode =
                    jobj
                        [ "file", jstr s.File
                          "startLine", jint s.StartLine
                          "startColumn", jint s.StartCol
                          "endLine", jint s.EndLine
                          "endColumn", jint s.EndCol ]
                    :> JsonNode

                // Compact default (contextLines = 0): emit only the matched line —
                // omit before/after entirely (not even empty arrays) so a large site
                // count stays well under the MCP token ceiling. contextLines > 0
                // restores the richer surrounding-code output.
                let contextFields =
                    if contextLines > 0 then
                        [ "before", ctx["before"].DeepClone(); "after", ctx["after"].DeepClone() ]
                    else
                        []

                jobj
                    ([ "file", jstr s.File
                       "range", rangeNode
                       "kind", jstr s.Kind
                       "project", jstr s.Project
                       "symbolFullName",
                       (match s.FullName with
                        | null -> null
                        | v -> jstr v)
                       "lineText", ctx["lineText"].DeepClone() ]
                     @ contextFields)
                :> JsonNode

            // One self-describing representation per site: each node carries
            // file / range / kind / project / symbolFullName / lineText, so an agent
            // filters the flat list by `kind` and reads `breakdown` for per-kind
            // counts. The grouped definitions/references/fieldSites/memberSites buckets
            // were dropped — they re-emitted every site a second time and doubled the
            // payload (the reason the default page cap had been forced down to 40).
            let siteNodes = pageSites |> Array.map siteToJson

            // Aggregated diagnostics across swept projects: Error always (so callers can
            // detect broken projects even on zero hits), Warning/Info gated by includeInfo.
            let diagNodes =
                aggregatedDiagnostics.ToArray()
                |> Array.filter (fun d ->
                    d.Severity = FSharpDiagnosticSeverity.Error
                    || (includeInfo
                        && (d.Severity = FSharpDiagnosticSeverity.Warning
                            || d.Severity = FSharpDiagnosticSeverity.Hidden
                            || d.Severity = FSharpDiagnosticSeverity.Info)))
                |> Array.truncate 200
                |> Array.map diagnosticToJson

            let scopeResolved =
                if projectsToSweep.Length <= 1 then
                    if scope = "file" then "file" else "project"
                else
                    "workspace"

            let resolution =
                jobj
                    [ "matched", jbool matched
                      "kindResolved", jstr kindResolved
                      "scopeResolved", jstr scopeResolved
                      "projectsSwept", jint projectsToSweep.Length
                      "via", jstr via
                      "fcsSiteCount", jint totalSites
                      "fsacFallbackHits", jint fsacHits ]
                :> JsonNode

            let breakdown =
                jobj
                    [ "definitions", jint defCount
                      "references", jint refCount
                      "fieldSetLiteral", jint fLit
                      "fieldSetUpdate", jint fUpd
                      "fieldRead", jint fRead
                      "memberUsages", jint memCount ]
                :> JsonNode

            let paginationFields =
                Cursor.paginationFields "sites" totalSites pageOffset pageSize pageSites.Length

            let baseFields =
                [ "status", jstr "succeeded"
                  "query", jstr query
                  "kind", jstr kind
                  "kindResolved", jstr kindResolved
                  "scope", jstr scope
                  "exact", jbool exact
                  "resolution", resolution
                  "projectsSwept", jint projectsToSweep.Length
                  "totalSites", jint totalSites
                  "matchedUseCount", jint totalSites
                  "breakdown", breakdown
                  "sites", JsonArray(siteNodes) :> JsonNode
                  "perProject", JsonArray(perProject.ToArray()) :> JsonNode
                  "sweepElapsedMs", jint (int sweepSw.ElapsedMilliseconds)
                  "projectDiagnostics", JsonArray(diagNodes) :> JsonNode ]

            return jobj (baseFields @ paginationFields) :> JsonNode
        }

    // ── check: one fresh project type-check (issue #128, Stage 1) ────────────────
    // Drops FCS's incremental builder + cached project results for THIS project so a
    // source edit on disk is re-read — this is what makes the `check` verdict
    // trustworthy (never a stale-`{}` false-clean). The resolved MSBuild options stay
    // cached: they only change when the .fsproj itself changes, so re-resolving them
    // every call would burn cost for no freshness gain. Ok carries the project's
    // diagnostics; Error is a load failure ("timeout" or the exception message), which
    // the caller turns into verdict="unknown" rather than a confident clean.
    member private this.FreshProjectCheck
        (fsproj: string, timeoutMs: int)
        : Task<Result<FSharpDiagnostic array * string * string, string>> =
        task {
            try
                let! options, optionsSource = this.ResolveFsprojOptions(normalizePath fsproj)
                let cacheKey = makeResolvedProjectCacheKey options
                projectResultsCache.TryRemove(cacheKey) |> ignore
                checker.InvalidateConfiguration(options)
                use cts = new CancellationTokenSource(timeoutMs)
                let! results = Async.StartAsTask(checker.ParseAndCheckProject(options), cancellationToken = cts.Token)
                return Ok(results.Diagnostics, options.ProjectFileName, optionsSource)
            with
            | :? OperationCanceledException
            | :? TaskCanceledException -> return Error "timeout"
            | ex -> return Error ex.Message
        }

    // ── check: one trustworthy verdict for the active context (issue #128) ───────
    // Consolidates the 5 check-cluster tools (workspace_diagnostics, fsharp_compile,
    // fcs_check_file, fcs_parse_and_check_file, fcs_validate_snippet) behind ONE field
    // an agent can act on: `verdict` ∈ { "clean", "errors", "unknown" }.
    //
    // HEADLINE TRUST PROPERTY: the default speed="trusted" runs a FRESH in-process FCS
    // re-check (FreshProjectCheck / cache-invalidated parse+check), so it can NEVER
    // emit the stale-`{}` false-clean that made agents fall back to `dotnet build`
    // (#100). "clean"/"errors" are terminal and ground-truth. "unknown" is returned
    // ONLY when analysis genuinely could not run (aborted / timed out / a swept project
    // failed to load) — honest, never a silently-escalated `dotnet build`.
    //
    // speed="fast" is the explicit opt-in to the cheap cached FSAC snapshot
    // (old workspace_diagnostics behaviour). Even then, a cold cache (no analysis
    // pushed yet) reports verdict="unknown" instead of a false-clean.
    //
    // RELOCATED-CHOICE GUARD: no output field tells the agent which tool to call next.
    // The workspace_diagnostics → fresh-FCS-re-check escalation is hidden behind the
    // verdict (surfaced only as the debug `escalated`/`via` fields).
    //
    // fsacSnapshot is injected (like find's fsacProbe) so this FCS substrate stays
    // LSP-agnostic; it is consulted only on the speed="fast" path.
    member this.Check(args: CheckArgs, ?fsacSnapshot: unit -> Task<CheckFsacSnapshot>) : Task<JsonNode> =
        task {
            let speed = (args.speed |> Option.defaultValue "trusted").Trim().ToLowerInvariant()
            let mode = (args.mode |> Option.defaultValue "fs").Trim().ToLowerInvariant()
            let severityFloor = (args.severity |> Option.defaultValue "error").Trim().ToLowerInvariant()
            let scopeRaw = (args.scope |> Option.defaultValue "auto").Trim().ToLowerInvariant()
            let timeoutMs = args.timeoutMs |> Option.defaultValue 60000

            let invalid msg =
                jobj [ "status", jstr "invalid_args"; "message", jstr msg ] :> JsonNode

            let hasText (o: string option) =
                o |> Option.exists (String.IsNullOrWhiteSpace >> not)

            let severityNames =
                [ "error"; "errors"; "warning"; "warnings"; "information"; "info"; "hint"; "hints"; "all" ]

            if speed <> "trusted" && speed <> "fast" then
                return invalid $"speed must be 'trusted' or 'fast' (got '{speed}')"
            elif mode <> "fs" && mode <> "fsi" then
                return invalid $"mode must be 'fs' or 'fsi' (got '{mode}')"
            elif not (List.contains severityFloor severityNames) then
                return invalid $"severity must be one of error|warning|information|hint|all (got '{severityFloor}')"
            elif not (List.contains scopeRaw [ "auto"; "file"; "project"; "workspace"; "snippet" ]) then
                return invalid $"scope must be one of auto|file|project|workspace|snippet (got '{scopeRaw}')"
            else

            // Severity floor → rank (Error = 1, highest). A diagnostic is surfaced in the
            // `diagnostics` array when its rank ≤ the floor rank; errorCount/warningCount
            // are always computed from the FULL set, independent of the floor.
            let floorRank =
                match severityFloor with
                | "error"
                | "errors" -> 1
                | "warning"
                | "warnings" -> 2
                | "information"
                | "info" -> 3
                | "hint"
                | "hints" -> 4
                | _ -> 5 // "all"

            let fcsRank (sev: FSharpDiagnosticSeverity) =
                match sev with
                | FSharpDiagnosticSeverity.Error -> 1
                | FSharpDiagnosticSeverity.Warning -> 2
                | FSharpDiagnosticSeverity.Info -> 3
                | FSharpDiagnosticSeverity.Hidden -> 4

            let passesFloor (sev: FSharpDiagnosticSeverity) = fcsRank sev <= floorRank

            // Project / solution target for project + workspace scopes. (Program.fs has
            // already defaulted projectPath to the active set_project.)
            let sweepTargetOpt =
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath
                |> Option.orElseWith (fun () -> args.path |> Option.bind findNearestFsproj |> Option.map normalizePath)

            let resolvedScope =
                match scopeRaw with
                | "file" -> "file"
                | "project" -> "project"
                | "workspace" -> "workspace"
                | "snippet" -> "snippet"
                | _ -> // auto
                    if hasText args.snippet then "snippet"
                    elif hasText args.path then "file"
                    else
                        match sweepTargetOpt with
                        | Some t -> if (SolutionParsing.listProjects t).Length > 1 then "workspace" else "project"
                        | None -> "project"

            // Single response builder — keeps the actionable header (verdict/analyzed/
            // counts) identical across every scope and speed, then folds in the legacy
            // superset + per-scope extras so migrating callers lose nothing.
            let build
                (verdict: string)
                (analyzed: bool)
                (via: string)
                (escalated: string option)
                (errorCount: int)
                (warningCount: int)
                (totalDiagnostics: int)
                (diagNodes: JsonNode array)
                (files: string array)
                (reason: string option)
                (extra: (string * JsonNode) list)
                : JsonNode =
                jobj (
                    [ "status", jstr "succeeded"
                      "verdict", jstr verdict
                      "analyzed", jbool analyzed
                      "scope", jstr resolvedScope
                      "speed", jstr speed
                      "errorCount", jint errorCount
                      "warningCount", jint warningCount
                      "diagnosticsFileCount", jint files.Length
                      "files", JsonArray(files |> Array.map jstr) :> JsonNode
                      "diagnostics", JsonArray(diagNodes) :> JsonNode
                      // debug-only
                      "escalated", (match escalated with Some e -> jstr e | None -> null)
                      "via", jstr via
                      // legacy superset
                      "fresh", jbool (speed = "trusted")
                      "groundTruth", jbool (analyzed && via = "fcs")
                      "totalDiagnostics", jint totalDiagnostics
                      "reason", (match reason with Some r -> jstr r | None -> null) ]
                    @ extra
                )
                :> JsonNode

            // FCS-diagnostics surfacing shared by every trusted (FCS) scope.
            let surfaceFcs (diags: FSharpDiagnostic array) =
                let surfaced = diags |> Array.filter (fun d -> passesFloor d.Severity)
                let nodes = surfaced |> Array.map diagnosticToJson
                let files = surfaced |> Array.map (fun d -> normalizePath d.FileName) |> Array.distinct
                nodes, files

            match resolvedScope with
            // ── snippet: always FRESH (ignores speed); old ValidateSnippet logic ──
            | "snippet" ->
                match args.snippet with
                | Some snippetText when not (String.IsNullOrWhiteSpace snippetText) ->
                    let snippetProject =
                        match args.projectPath with
                        | Some p when not (String.IsNullOrWhiteSpace p) ->
                            let np = normalizePath p

                            if np.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                                Some np
                            else
                                SolutionParsing.listProjects np |> Array.tryHead
                        | _ -> args.path |> Option.bind findNearestFsproj |> Option.map normalizePath

                    match snippetProject with
                    | None ->
                        return
                            invalid
                                "snippet check needs a project context: pass projectPath (.fsproj/.sln/.slnx) or path, or call set_project first."
                    | Some fsproj ->
                        let! options, optionsSource = this.ResolveFsprojOptions(fsproj)
                        let ext = if mode = "fsi" then ".fsi" else ".fs"

                        let snippetFile =
                            Path.Combine(Path.GetTempPath(), $"fslangmcp_check_{Guid.NewGuid():N}{ext}")

                        try
                            File.WriteAllText(snippetFile, snippetText)

                            let modifiedOptions =
                                { options with
                                    SourceFiles = Array.append options.SourceFiles [| snippetFile |] }

                            let sourceText = SourceText.ofString snippetText

                            let! parseResults, checkAnswer =
                                checker.ParseAndCheckFileInProject(snippetFile, 0, sourceText, modifiedOptions)
                                |> asTask

                            let checkDiagnostics, succeeded =
                                match checkAnswer with
                                | FSharpCheckFileAnswer.Succeeded r -> r.Diagnostics, true
                                | FSharpCheckFileAnswer.Aborted -> [||], false

                            let allDiags = Array.append parseResults.Diagnostics checkDiagnostics
                            let errorCount, warningCount = countDiagnosticsBySeverity allDiags

                            let verdict, analyzed, reason =
                                if not succeeded then
                                    "unknown", false, Some "Type checking was aborted; snippet verdict is indeterminate."
                                elif errorCount > 0 then
                                    "errors", true, None
                                else
                                    "clean", true, None

                            let nodes, _ = surfaceFcs allDiags

                            return
                                build
                                    verdict
                                    analyzed
                                    "fcs"
                                    None
                                    errorCount
                                    warningCount
                                    allDiags.Length
                                    nodes
                                    [||] // synthetic temp file — no meaningful source path to surface
                                    reason
                                    [ "mode", jstr mode
                                      "projectFileName", jstr options.ProjectFileName
                                      "optionsSource", jstr optionsSource ]
                        finally
                            try
                                if File.Exists snippetFile then
                                    File.Delete snippetFile
                            with _ ->
                                ()
                | _ -> return invalid "scope='snippet' requires non-empty 'snippet' text."

            // ── file / project / workspace ───────────────────────────────────────
            | _ when speed = "fast" ->
                // Cheap cached FSAC snapshot. A cold cache (no analysis pushed yet) is
                // the stale-`{}` ambiguity → verdict="unknown", NOT a false-clean.
                let! snap =
                    match fsacSnapshot with
                    | Some thunk -> thunk ()
                    | None -> Task.FromResult CheckFsacSnapshot.empty

                let hasAnalysis = snap.Ready && snap.MostRecentAnalyzedAt.IsSome

                let verdict, analyzed, reason =
                    if not hasAnalysis then
                        "unknown",
                        false,
                        Some
                            "FSAC has not published diagnostics yet (cold cache); the default trusted check returns a ground-truth verdict."
                    elif snap.ErrorCount > 0 then
                        "errors", true, None
                    else
                        "clean", true, None

                // Surface the snapshot's diagnostics by the requested severity floor —
                // LSP severity codes (1=Error … 4=Hint) line up with floorRank, so a node
                // is emitted when 1 ≤ code ≤ floorRank. This is what makes
                // check(speed="fast", severity="warning"|"all") actually return the
                // warnings the caller asked for. errorCount/warningCount below stay the
                // FULL-set tallies, matching the trusted path's count/floor split.
                let severityCode (obj: JsonObject) =
                    match obj["severity"] with
                    | :? JsonValue as v ->
                        let mutable c = 0
                        if v.TryGetValue(&c) then c else 0
                    | _ -> 0

                let nodes =
                    match snap.Diagnostics with
                    | :? JsonArray as arr ->
                        arr
                        |> Seq.choose (fun n ->
                            match n with
                            | :? JsonObject as obj ->
                                let code = severityCode obj
                                if code >= 1 && code <= floorRank then Some(n.DeepClone()) else None
                            | _ -> None)
                        |> Seq.toArray
                    | _ -> [||]

                let files =
                    nodes
                    |> Array.choose (fun n ->
                        match n["file"] with
                        | :? JsonValue as v -> Some(v.GetValue<string>())
                        | _ -> None)
                    |> Array.distinct

                // issue #133: totalDiagnostics must count the FULL snapshot across ALL
                // severities — the snapshot now retains severity-3/4 entries (#130), so
                // `snap.ErrorCount + snap.WarningCount` undercounts and an info-only
                // snapshot surfaced a non-empty `diagnostics` list with totalDiagnostics=0.
                // Mirrors the trusted path's allDiags.Length: with severity="all" the
                // surfaced list and this count agree exactly.
                let totalSnapshotDiagnostics =
                    match snap.Diagnostics with
                    | :? JsonArray as arr ->
                        arr
                        |> Seq.filter (fun n ->
                            match n with
                            | :? JsonObject as obj -> severityCode obj >= 1
                            | _ -> false)
                        |> Seq.length
                    | _ -> 0

                return
                    build
                        verdict
                        analyzed
                        "fsac"
                        None
                        snap.ErrorCount
                        snap.WarningCount
                        totalSnapshotDiagnostics
                        nodes
                        files
                        reason
                        [ "lspState", jstr (if snap.Ready then "ready" else "warming")
                          "mostRecentAnalyzedAt", (match snap.MostRecentAnalyzedAt with Some t -> jstr t | None -> null)
                          "analyzedFileCount", jint snap.AnalyzedFileCount ]

            | "file" ->
                match args.path with
                | Some path when not (String.IsNullOrWhiteSpace path) ->
                    match validateSourcePath "check" None path with
                    | Some err -> return err
                    | None ->
                        // Resolve options, invalidate THIS project, then re-check fresh so
                        // on-disk edits (incl. cross-file) are reflected — mirrors fcs_check_file.
                        let! _, _, _, projectOptions, _, _ =
                            this.PrepareCheckContext(path, None, args.projectPath, None)

                        let projectResultsKey = makeResolvedProjectCacheKey projectOptions
                        projectResultsCache.TryRemove(projectResultsKey) |> ignore
                        checker.InvalidateConfiguration(projectOptions)

                        let! _, _, optionsSource, _, parseResults, checkedResults =
                            this.PrepareCheckContext(path, None, args.projectPath, None)

                        let checkDiagnostics =
                            checkedResults
                            |> Option.map (fun r -> r.Diagnostics)
                            |> Option.defaultValue [||]

                        let allDiags = Array.append parseResults.Diagnostics checkDiagnostics
                        let succeeded = checkedResults.IsSome
                        let errorCount, warningCount = countDiagnosticsBySeverity allDiags

                        let verdict, analyzed, reason =
                            if not succeeded then
                                "unknown", false, Some "Type checking was aborted; file verdict is indeterminate."
                            elif errorCount > 0 then
                                "errors", true, None
                            else
                                "clean", true, None

                        let nodes, files = surfaceFcs allDiags

                        return
                            build
                                verdict
                                analyzed
                                "fcs"
                                (Some "fcs-reanalyze")
                                errorCount
                                warningCount
                                allDiags.Length
                                nodes
                                files
                                reason
                                [ "projectFileName", jstr projectOptions.ProjectFileName
                                  "optionsSource", jstr optionsSource
                                  "projectsSwept", jint 1 ]
                | _ -> return invalid "scope='file' requires 'path'."

            | "project" ->
                match sweepTargetOpt with
                | None ->
                    return
                        invalid
                            "check needs a project context: pass projectPath (.fsproj/.sln/.slnx) or path, or call set_project first."
                | Some target ->
                    let fsproj =
                        if target.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                            Some target
                        else
                            args.path
                            |> Option.bind findNearestFsproj
                            |> Option.map normalizePath
                            |> Option.orElseWith (fun () -> SolutionParsing.listProjects target |> Array.tryHead)

                    match fsproj with
                    | None -> return invalid $"check could not resolve a single .fsproj to check from: {target}"
                    | Some proj ->
                        let! result = this.FreshProjectCheck(proj, timeoutMs)

                        match result with
                        | Error "timeout" ->
                            return
                                build
                                    "unknown"
                                    false
                                    "fcs"
                                    (Some "fcs-reanalyze")
                                    0
                                    0
                                    0
                                    [||]
                                    [||]
                                    (Some $"FCS type-check timed out after {timeoutMs}ms.")
                                    [ "projectsSwept", jint 1 ]
                        | Error msg ->
                            return
                                build
                                    "unknown"
                                    false
                                    "fcs"
                                    (Some "fcs-reanalyze")
                                    0
                                    0
                                    0
                                    [||]
                                    [||]
                                    (Some $"Project could not be analyzed: {msg}")
                                    [ "projectsSwept", jint 1 ]
                        | Ok(diags, projFileName, optionsSource) ->
                            let errorCount, warningCount = countDiagnosticsBySeverity diags
                            let verdict = if errorCount > 0 then "errors" else "clean"
                            let nodes, files = surfaceFcs diags

                            return
                                build
                                    verdict
                                    true
                                    "fcs"
                                    (Some "fcs-reanalyze")
                                    errorCount
                                    warningCount
                                    diags.Length
                                    nodes
                                    files
                                    None
                                    [ "projectFileName", jstr projFileName
                                      "optionsSource", jstr optionsSource
                                      "projectsSwept", jint 1 ]

            | "workspace" ->
                match sweepTargetOpt with
                | None ->
                    return
                        invalid
                            "check needs a project context: pass projectPath (.fsproj/.sln/.slnx) or path, or call set_project first."
                | Some target ->
                    let projects0 = SolutionParsing.listProjects target

                    let projects =
                        if projects0.Length = 0 && target.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                            [| target |]
                        else
                            projects0

                    if projects.Length = 0 then
                        return invalid $"check could not resolve any .fsproj to check from: {target}"
                    else
                        let allDiags = ResizeArray<FSharpDiagnostic>()
                        let perProject = ResizeArray<JsonNode>()
                        let mutable failCount = 0

                        for proj in projects do
                            let! result = this.FreshProjectCheck(proj, timeoutMs)

                            match result with
                            | Ok(diags, _, _) ->
                                allDiags.AddRange diags
                                let e, w = countDiagnosticsBySeverity diags

                                perProject.Add(
                                    jobj
                                        [ "project", jstr (Path.GetFileNameWithoutExtension proj)
                                          "fsproj", jstr (normalizePath proj)
                                          "errorCount", jint e
                                          "warningCount", jint w
                                          "analyzed", jbool true ]
                                    :> JsonNode
                                )
                            | Error msg ->
                                failCount <- failCount + 1

                                perProject.Add(
                                    jobj
                                        [ "project", jstr (Path.GetFileNameWithoutExtension proj)
                                          "fsproj", jstr (normalizePath proj)
                                          "error", jstr msg
                                          "analyzed", jbool false ]
                                    :> JsonNode
                                )

                        let diags = allDiags.ToArray()
                        let errorCount, warningCount = countDiagnosticsBySeverity diags

                        let verdict, analyzed, reason =
                            if errorCount > 0 then
                                "errors", true, None
                            elif failCount > 0 then
                                "unknown",
                                false,
                                Some $"{failCount} of {projects.Length} project(s) failed to analyze; cannot confirm clean."
                            else
                                "clean", true, None

                        let nodes, files = surfaceFcs diags

                        return
                            build
                                verdict
                                analyzed
                                "fcs"
                                (Some "fcs-reanalyze")
                                errorCount
                                warningCount
                                diags.Length
                                nodes
                                files
                                reason
                                [ "projectsSwept", jint projects.Length
                                  "perProject", JsonArray(perProject.ToArray()) :> JsonNode ]

            | _ -> return invalid $"unsupported scope '{resolvedScope}'"
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

                        // Record / struct / class fields. Public fields and consts on a
                        // reference-type CLASS (C# class fields, F# `val`) are exposed ONLY
                        // via FSharpFields — they are not in MembersFunctionsAndValues — so
                        // enumerate classes here too, not just records/structs. Drop
                        // compiler-generated backing fields (`<Prop>k__BackingField`, F#'s
                        // `Name@`) so an auto-property is not duplicated by its hidden field.
                        try
                            if entity.IsFSharpRecord || entity.IsValueType || entity.IsClass then
                                let isBackingField (f: FSharpField) =
                                    try
                                        f.IsCompilerGenerated
                                        || f.IsNameGenerated
                                        || (let n = f.Name
                                            not (isNull n)
                                            && (n.Contains "k__BackingField" || n.Contains "@"))
                                    with _ ->
                                        false

                                for f in entity.FSharpFields do
                                    if passesFieldAccessibility f && not (isBackingField f) then
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
        // issue #131: a new set_project (or any explicit cache clear) must drop the
        // memoized find sweeps too, so a project switch never serves a prior project's
        // symbol uses.
        projectUsesCache.Clear()

    /// Returns configuration flags captured at checker creation time, for use by RuntimeStatus.
    member _.CheckerConfig: FcsCheckerConfig =
        { KeepAssemblyContents = keepAssemblyContents
          KeepAllBackgroundResolutions = keepAllBackgroundResolutions
          KeepAllBackgroundSymbolUses = keepAllBackgroundSymbolUses
          ProjectCacheSize = defaultProjectCacheSize }

    /// Returns the number of entries currently held in the project-results cache.
    member _.ProjectResultsCacheCount = projectResultsCache.Count

    /// Number of entries in the find sweep use-cache (issue #131). One per (project,
    /// source-stamp); unchanged between sweeps of the same projects, grows by one per
    /// project when a swept source file is edited. Exposed for cache-behaviour tests.
    member _.ProjectUsesCacheCount = projectUsesCache.Count

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
