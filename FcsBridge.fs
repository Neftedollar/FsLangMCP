module FsLangMcp.FcsBridge

open System
open System.IO
open System.Collections.Concurrent
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
open FsLangMcp.MetadataAccessibility
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

/// Find-only nearest-project walk. The general helper above keeps its existing
/// behavior for other tools; find can stop its retained discovery worker between
/// individual directory entries when the shared deadline or caller ends.
let private findNearestFsprojWithContinuation
    (filePath: string)
    (beforeStep: string -> int -> unit)
    (shouldContinue: unit -> bool)
    : string option =
    let ensure phase index =
        beforeStep phase index

        if not (shouldContinue ()) then
            raise (TimeoutException($"Find nearest-project discovery expired during {phase}."))

    let rec walk directoryIndex fileIndex (dir: string) =
        if isNull dir then
            None
        else
            ensure "nearest-project-directory" directoryIndex
            use fsprojs = Directory.EnumerateFiles(dir, "*.fsproj").GetEnumerator()
            let mutable currentFileIndex = fileIndex
            let mutable found = None
            let mutable reading = true

            while reading && Option.isNone found do
                ensure "nearest-project-file" currentFileIndex

                if fsprojs.MoveNext() then
                    found <- Some fsprojs.Current
                    currentFileIndex <- currentFileIndex + 1
                else
                    reading <- false

            match found with
            | Some project -> Some project
            | None -> walk (directoryIndex + 1) currentFileIndex (Path.GetDirectoryName(dir))

    walk 0 0 (Path.GetDirectoryName(Path.GetFullPath(filePath)))

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

/// A (line, column) pair used as a dictionary key for field-name ranges.
/// FCS range has [<NoComparison>], so it cannot be used directly as a map key.
type private FieldFormKey = int * int

/// The syntactic form a record-field use site takes. Each needs a DIFFERENT edit when
/// the field's type changes, which is why `find` reports them as distinct site kinds
/// rather than lumping everything that is not a construction site into "read" (#207).
[<RequireQualifiedAccess>]
type private FieldSiteForm =
    /// `{ Field = expr }` — record construction literal.
    | Literal
    /// `{ x with Field = expr }` — copy-and-update.
    | Update
    /// `x.Field <- expr` — assignment to a mutable field.
    | Mutation
    /// `| { Field = binding } ->` — destructuring in a pattern.
    | Pattern

/// Per-file field-site classification produced by one parse-tree walk.
[<NoComparison; NoEquality>]
type private FieldSiteForms =
    { /// Keyed by the START position of the field identifier, which is what FCS reports
      /// as the symbol-use range start for record literals, copy-and-updates, patterns,
      /// and the `x.Field <- v` (SynExpr.LongIdentSet) shape of a mutation.
      ByStart: System.Collections.Generic.Dictionary<FieldFormKey, FieldSiteForm>
      /// Mutation sites keyed by the END position of the field identifier.
      /// SynExpr.DotSet (`arr[0].Field <- v`) breaks start-position alignment: FCS
      /// reports the symbol use over the WHOLE target expression (verified on
      /// `arr.[0].Attempts <- 5`: use range col 17-33, DotSet lid range col 25-33),
      /// so only the end position lines up for every mutation shape. Consulted only
      /// when the start lookup misses, so it can never override a construction form.
      MutationEnds: System.Collections.Generic.HashSet<FieldFormKey> }

/// Module that walks a FCS ParsedInput and tags every record-field use site with the
/// syntactic form it appears in.
module private FieldFormClassifier =

    let private tagExprFields
        (form: FieldSiteForm)
        (fields: SynExprRecordField list)
        (d: System.Collections.Generic.Dictionary<FieldFormKey, FieldSiteForm>)
        =
        for field in fields do
            match field with
            | SynExprRecordField((lid, _), _, _, _, _) ->
                let r = lid.Range
                d[(r.StartLine, r.StartColumn)] <- form

    /// Walk the full parse tree using ParsedInput.fold (FCS 43.12+).
    /// ParsedInput.fold is a position-independent full-tree accumulator that visits
    /// every SyntaxNode in the tree, including those inside type member bodies,
    /// for-loops, CE binds, object expressions, and all other expression-containing arms.
    let classifySites (input: ParsedInput) : FieldSiteForms =
        let empty () =
            { ByStart = System.Collections.Generic.Dictionary<FieldFormKey, FieldSiteForm>()
              MutationEnds = System.Collections.Generic.HashSet<FieldFormKey>() }

        // SigFile (.fsi): signature files declare types but contain no expression
        // use-sites — there are no record literals/updates here to classify. Empty
        // result is the correct return; do not "complete" this arm.
        match input with
        | ParsedInput.SigFile _ -> empty ()
        | ParsedInput.ImplFile _ ->
            let acc = empty ()

            (acc, input)
            ||> ParsedInput.fold (fun state _path node ->
                match node with
                | SyntaxNode.SynExpr(SynExpr.Record(_, copyInfo, fields, _)) ->
                    tagExprFields
                        (if copyInfo.IsSome then
                             FieldSiteForm.Update
                         else
                             FieldSiteForm.Literal)
                        fields
                        state.ByStart
                | SyntaxNode.SynExpr(SynExpr.LongIdentSet(lid, _, _))
                | SyntaxNode.SynExpr(SynExpr.DotSet(_, lid, _, _)) ->
                    let r = lid.Range
                    state.ByStart[(r.StartLine, r.StartColumn)] <- FieldSiteForm.Mutation
                    state.MutationEnds.Add((r.EndLine, r.EndColumn)) |> ignore
                | SyntaxNode.SynPat(SynPat.Record(fieldPats, _)) ->
                    for fieldPat in fieldPats do
                        match fieldPat with
                        | NamePatPairField(fieldName = lid) ->
                            let r = lid.Range
                            state.ByStart[(r.StartLine, r.StartColumn)] <- FieldSiteForm.Pattern
                | _ -> ()

                state)

    /// Back-compat projection for RecordFieldAudit, whose `form` field has always been
    /// exactly literal / with-update / unknown. Mutation and pattern sites are NOT record
    /// construction sites, so they are dropped here and fall through to that tool's
    /// textual fallback — byte-identical to the behaviour before the four-way split.
    let classify (input: ParsedInput) : System.Collections.Generic.Dictionary<FieldFormKey, bool> =
        let d = System.Collections.Generic.Dictionary<FieldFormKey, bool>()

        for entry in (classifySites input).ByStart do
            match entry.Value with
            | FieldSiteForm.Literal -> d[entry.Key] <- false
            | FieldSiteForm.Update -> d[entry.Key] <- true
            | FieldSiteForm.Mutation
            | FieldSiteForm.Pattern -> ()

        d


// ─── find: per-site field type resolution (issue #207) ──────────────────────────
//
// `find(kind=field, includeSiteTypes=true)` prints, for every record-field site, the
// field's type AS THE CURRENT TYPECHECK RESOLVES IT AT THAT SITE — rendered with the
// site's own FSharpDisplayContext, so it reads the way code at that site would write it
// (`string`, not `Microsoft.FSharp.Core.string`) and honours the `open`s in scope there.
// Across a sweep spanning several fields — or several declaring types under exact=false —
// that string differs per row: it is the "old type" column an agent needs to plan a
// field-type change without opening every file.
//
// Explicit non-goal: this is NEVER the type the field would have AFTER the edit. Knowing
// that requires compiling the modified code — i.e. `check`, run once the edits land.
//
// Established empirically against FCS 43.12.400 while designing this (see the #207 PR):
//   • FSharpSymbolUse.GenericArguments is EMPTY for record-field uses, so a field on a
//     generic record renders as its type PARAMETER (`'T`), not the instantiation at the
//     site. FSharpType.Instantiate therefore buys nothing here; documented as a limit.
//   • FSharpSymbolUse.IsFromPattern is FALSE for the field in `| { Code = c } ->` — it
//     describes the BINDING (`c`), not the field — which is why `field-pattern` is
//     classified from the parse tree (SynPat.Record) rather than from the symbol use.
//   • Type resolution survives a parse error elsewhere in the file AND an undefined field
//     type (FCS recovers the latter to `obj`), so `siteType: null` is a genuine but RARE
//     defensive outcome rather than a routine one. Review of this feature independently
//     re-tested two more candidate degradations (a file excluded from compile order; a
//     field whose type comes from an unreferenced project) and both collapsed to ABSENCE
//     of the symbol use rather than degradation. The reachable-in-production degraded arm
//     is therefore the DEADLINE one — see FieldSiteTypes below for how it is tested.

/// Longest `siteType` string emitted per site. A pathological generic signature must not
/// be able to blow `find`'s page budget: a full default page of 80 sites is ~43k chars
/// today, and this cap bounds the growth to 80 × (cap + ~15 chars of JSON overhead).
let private siteTypeMaxChars = 200

/// Format a field symbol use's type for the site it occurs at. Returns None when FCS
/// cannot produce one — the caller degrades that single row to `siteType: null` and
/// counts it, rather than failing the whole call.
let private tryFormatFieldSiteType (symbolUse: FSharpSymbolUse) : string option =
    try
        match symbolUse.Symbol with
        | :? FSharpField as field ->
            let formatted = field.FieldType.Format(symbolUse.DisplayContext)

            if String.IsNullOrWhiteSpace formatted then None
            elif formatted.Length > siteTypeMaxChars then
                Some(formatted.Substring(0, siteTypeMaxChars) + "...")
            else
                Some formatted
        | _ -> None
    with _ ->
        // FCS throws on synthetic / unresolved field symbols; a per-site miss must never
        // propagate out of the sweep.
        None

/// The per-site (siteType, typeStatus) decision and its JSON projection, factored OUT of
/// the Find state machine so BOTH halves are unit-testable without an FCS session.
///
/// Why they live here: every naturally-occurring field site in every fixture resolves
/// (`degraded = 0`, including the parse-error fixture and a 3434-site sweep of this repo),
/// so inline in Find the degraded arms would never execute in a test — and a dropped key,
/// the STRING `"null"` instead of JSON null, or a wrong status string silently breaking the
/// `typed + degraded == fieldSites` identity would all pass the whole suite. As free
/// functions over primitives they are exercised directly. The one degraded arm that is
/// genuinely reachable in production — the deadline — is additionally driven end-to-end
/// through `Find` via `findSiteTypeDeadlineExpiredOverride`.
module internal FieldSiteTypes =

    /// Status recorded on a successfully typed site.
    [<Literal>]
    let Typed = "typed"

    /// FCS produced no type for this site; the row carries `siteType: null`.
    [<Literal>]
    let Unresolved = "unresolved"

    /// The site was reached after find's wall-clock budget was exhausted; the row carries
    /// `siteType: null` rather than stretching the sweep.
    [<Literal>]
    let TimedOut = "timeout"

    /// Decide one site's (siteType, typeStatus). `tryFormat` is a THUNK on purpose: past
    /// the deadline it is never invoked, so an exhausted budget costs no further FCS
    /// formatting work on the tail of a large sweep.
    let outcome (deadlineExpired: bool) (tryFormat: unit -> string option) : string * string =
        if deadlineExpired then
            null, TimedOut
        else
            match tryFormat () with
            | Some formatted -> formatted, Typed
            | None -> null, Unresolved

    /// Fold a later project's answer for the SAME physical site into the alternatives set,
    /// carrying WHICH project produced it.
    ///
    /// #207 review: `find` de-duplicates sites by physical location, so a `.fs` linked into
    /// several `.fsproj` files is swept once per project. Those projects can resolve the same
    /// field to different types (different conditional symbols, a different generic
    /// instantiation), and the row used to be overwritten by whichever project the sweep
    /// visited last — the payload then claimed a single type with no sign that the sweep had
    /// seen another. The FIRST resolved type stays in `siteType` (with the project that
    /// produced it); every other (type, project) pair lands here so the disagreement is
    /// visible, attributable, and independent of sweep order.
    let mergeAlternatives
        (keptType: string)
        (candidateType: string)
        (candidateProject: string)
        (alternatives: (string * string) list)
        : (string * string) list =
        let pair = (candidateType, candidateProject)

        if
            isNull candidateType
            || isNull keptType
            || String.Equals(candidateType, keptType, StringComparison.Ordinal)
            || alternatives |> List.contains pair
        then
            alternatives
        else
            alternatives @ [ pair ]
            |> List.sortWith (fun (leftType, leftProject) (rightType, rightProject) ->
                match String.CompareOrdinal(leftType, rightType) with
                | 0 -> String.CompareOrdinal(leftProject, rightProject)
                | typeOrder -> typeOrder)

    /// Per-row caps: distinct alternative types, and projects listed under one type. They
    /// make one site's serialized alternatives deterministic and bounded in cardinality.
    /// These caps are deliberately independent of cursor position, maxResults, and the
    /// response budget: a physical site has one canonical row on every page that contains it.
    [<Literal>]
    let AlternativeTypesPerSite = 3

    [<Literal>]
    let AlternativeProjectsPerType = 3

    /// Group (type, project) pairs into per-type entries under the per-row caps. Returns the
    /// entries — each as (siteType, projects shown, projects omitted) — plus how many distinct
    /// types the cap left out. Pure and page-invariant by construction.
    let alternativeEntries (alternatives: (string * string) list) : (string * string list * int) list * int =
        let byType =
            alternatives
            |> List.groupBy fst
            |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))

        let entries =
            byType
            |> List.truncate AlternativeTypesPerSite
            |> List.map (fun (siteType, pairs) ->
                let projects =
                    pairs
                    |> List.map snd
                    |> List.distinct
                    |> List.sortWith (fun left right -> String.CompareOrdinal(left, right))

                siteType,
                List.truncate AlternativeProjectsPerType projects,
                max 0 (projects.Length - AlternativeProjectsPerType))

        entries, max 0 (byType.Length - AlternativeTypesPerSite)

    /// True when this row's alternatives were capped in ANY way — distinct types dropped or
    /// projects under a type dropped. Drives the `alternativesTruncatedRows` ledger entry,
    /// which would otherwise have to be inferred by sniffing the rendered JSON and would miss
    /// the projects-only case.
    let alternativesTruncated (alternatives: (string * string) list) : bool =
        if List.isEmpty alternatives then
            false
        else
            let entries, typesOmitted = alternativeEntries alternatives

            typesOmitted > 0
            || entries |> List.exists (fun (_, _, projectsOmitted) -> projectsOmitted > 0)

    /// The `siteTypeAlternatives` row fields — emitted ONLY when another swept project
    /// resolved the same physical site to a different type, so the common row shape is
    /// byte-identical to a single-project sweep. Nothing is ever dropped silently — every
    /// per-site omission is a number in the row. The response-size planner never reshapes
    /// this projection; it can only omit the whole site by choosing a shorter page prefix.
    let alternativesFields
        (includeSiteTypes: bool)
        (alternatives: (string * string) list)
        : (string * JsonNode) list =
        if not includeSiteTypes || List.isEmpty alternatives then
            []
        else
            let entries, typesOmitted = alternativeEntries alternatives

            let entryNodes =
                entries
                |> List.map (fun (siteType, projects, projectsOmitted) ->
                    jobj (
                        [ "siteType", jstr siteType
                          "projects", JsonArray(projects |> List.map jstr |> List.toArray) :> JsonNode ]
                        // Parenthesised on purpose: a one-element list of an
                        // UNparenthesised tuple raises FS3536 ("did you mean ';'?"), and
                        // FcsBridgeTests type-checks this very file expecting zero
                        // diagnostics at every severity — informational included.
                        @ (if projectsOmitted > 0 then
                               [ ("projectsOmitted", jint projectsOmitted) ]
                           else
                               [])
                    )
                    :> JsonNode)

            [ ("siteTypeAlternatives", JsonArray(entryNodes |> List.toArray) :> JsonNode) ]
            @ (if typesOmitted > 0 then
                   [ ("siteTypeAlternativesOmitted", jint typesOmitted) ]
               else
                   [])

    /// Project a site's stored (siteType, typeStatus) into the JSON fields its row carries.
    /// Present on EVERY field site under includeSiteTypes — an explicit JSON null for a
    /// degraded one, so a consumer never has to distinguish "absent key" from "could not
    /// resolve". Absent entirely on non-field sites (typeStatus = null) and when the flag
    /// is off, keeping the default row byte-identical to before the feature.
    let rowFields (includeSiteTypes: bool) (siteType: string) (typeStatus: string) : (string * JsonNode) list =
        if includeSiteTypes && not (isNull typeStatus) then
            let value =
                match siteType with
                | null -> null
                | v -> jstr v

            [ ("siteType", value) ]
        else
            []


// ─── find: typed FSAC fallback result (issue #168, P1-01) ───────────────────────

/// Result of the optional FSAC workspace-symbol fallback used by `find`.
/// A non-success state is deliberately distinct from `Available 0`: transport,
/// readiness, and project-context failures must never become authoritative
/// evidence that a symbol is absent.
[<RequireQualifiedAccess>]
type internal FindFsacProbeResult =
    | Available of hitCount: int
    | NotReady of reason: string
    | ContextMismatch of reason: string
    | Failed of reason: string
    | Unavailable of reason: string

/// One monotonic request deadline shared by Program.fs admission and every find phase.
/// Semantic work stops early enough to leave a small, explicit allowance for constructing
/// the typed response. The optional signal is a deterministic test seam: production expiry
/// is always derived from Stopwatch's monotonic clock.
type internal FindRequestDeadline
    (timeoutMs: int, ?semanticExpirySignal: Task, ?responseExpirySignal: Task) =
    let elapsed = System.Diagnostics.Stopwatch.StartNew()

    let responseAllowanceMs =
        if timeoutMs <= 0 then
            0
        else
            min 250 (max 1 (timeoutMs / 20))

    let semanticBudgetMs = max 0 (timeoutMs - responseAllowanceMs)
    let mutable semanticExpired = if semanticBudgetMs = 0 then 1 else 0
    let mutable responseStartedAt: TimeSpan option = None

    let remainingFrom budgetMs =
        let remaining = TimeSpan.FromMilliseconds(float budgetMs) - elapsed.Elapsed
        if remaining <= TimeSpan.Zero then TimeSpan.Zero else remaining

    member _.TimeoutMs = timeoutMs
    member _.ResponseAllowanceMs = responseAllowanceMs
    member _.ElapsedMilliseconds = int elapsed.ElapsedMilliseconds
    member _.SemanticExpirySignal = semanticExpirySignal
    member _.ResponseExpirySignal = responseExpirySignal

    member _.MarkSemanticExpired() =
        Interlocked.Exchange(&semanticExpired, 1) |> ignore

    member _.RemainingSemanticBudget() =
        let signalled =
            semanticExpirySignal
            |> Option.exists _.IsCompleted

        if Volatile.Read(&semanticExpired) <> 0 || signalled then
            TimeSpan.Zero
        else
            remainingFrom semanticBudgetMs

    member _.BeginResponseConstruction() =
        if responseStartedAt.IsNone then
            responseStartedAt <- Some elapsed.Elapsed

    member _.RemainingResponseBudget() =
        let signalled =
            responseExpirySignal
            |> Option.exists _.IsCompleted

        if signalled then
            TimeSpan.Zero
        else
            let overallRemaining = remainingFrom timeoutMs

            match responseStartedAt with
            | None -> TimeSpan.Zero
            | Some startedAt ->
                let allowanceRemaining =
                    TimeSpan.FromMilliseconds(float responseAllowanceMs) - (elapsed.Elapsed - startedAt)

                let boundedAllowance =
                    if allowanceRemaining <= TimeSpan.Zero then
                        TimeSpan.Zero
                    else
                        allowanceRemaining

                if overallRemaining <= boundedAllowance then overallRemaining else boundedAllowance

    member this.SemanticExpired = this.RemainingSemanticBudget() <= TimeSpan.Zero
    member this.ResponseExpired = this.RemainingResponseBudget() <= TimeSpan.Zero

exception private FindProjectNotStartedException

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type private FindTargetDiscoveryResult =
    | Paths of string array
    | Projects of SolutionParsing.FindProjectDiscovery

[<RequireQualifiedAccess>]
module internal FindDeadlineResponse =

    let private phaseNode phase status =
        jobj [ "phase", jstr phase; "status", jstr status ] :> JsonNode

    let private knownProjectsBeforeDiscovery (args: FindArgs) =
        args.projectPath
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.filter (fun path -> path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun path ->
            try
                normalizePath path
            with _ ->
                path)
        |> Option.toArray

    /// Typed response used when the shared request deadline expires before find has a
    /// discovered project set. An explicit .fsproj is already known without filesystem
    /// discovery and is therefore reported as not_started; a solution/directory remains
    /// unknown rather than being scanned after expiry merely to improve accounting.
    let beforeDiscovery
        (args: FindArgs)
        (deadline: FindRequestDeadline)
        (phase: string)
        (phaseStatus: string)
        (errorKind: string)
        (message: string)
        : JsonNode =
        let kind = (args.kind |> Option.defaultValue "auto").Trim().ToLowerInvariant()
        let scope = (args.scope |> Option.defaultValue "auto").Trim().ToLowerInvariant()
        let exact = args.exact |> Option.defaultValue true
        let projects = knownProjectsBeforeDiscovery args
        let projectsRequested = projects.Length
        let projectsNotStarted = projectsRequested

        let perProject =
            projects
            |> Array.map (fun project ->
                let projectName =
                    try
                        Path.GetFileNameWithoutExtension project
                    with _ ->
                        project

                jobj
                    [ "project", jstr projectName
                      "fsproj", jstr project
                      "status", jstr "not_started"
                      "errorKind", jstr "deadline_not_started"
                      "retryable", jbool true
                      "error", jstr message
                      "elapsedMs", jint 0 ]
                :> JsonNode)

        let admissionStatus =
            if phase = "admission" then phaseStatus else "complete"

        let positionStatus =
            if phase = "position_resolution" then
                phaseStatus
            elif phase = "admission" then
                "not_started"
            elif kind = "position" then
                "complete"
            else
                "not_needed"

        let targetDiscoveryStatus =
            if phase = "target_discovery" then
                phaseStatus
            else
                "not_started"

        let phases =
            JsonArray(
                [| phaseNode "admission" admissionStatus
                   phaseNode "position_resolution" positionStatus
                   phaseNode "target_discovery" targetDiscoveryStatus
                   phaseNode "project_sweep" "not_started"
                   phaseNode "fsac_fallback" "not_started"
                   phaseNode "response_construction" "complete" |]
            )
            :> JsonNode

        let coverage =
            jobj
                [ "complete", jbool false
                  "projectsRequested", jint projectsRequested
                  "projectsAnalyzed", jint 0
                  "projectsFailed", jint 0
                  "projectsMissing", jint 0
                  "projectsTimedOut", jint 0
                  "projectsBusy", jint 0
                  "projectsNotStarted", jint projectsNotStarted
                  "phases", phases ]
            :> JsonNode

        let kindResolved = if kind = "position" then "position" else kind

        let scopeResolved =
            if projectsRequested = 1 then
                if scope = "file" then "file" else "project"
            elif scope = "workspace" then
                "workspace"
            else
                scope

        let resolution =
            jobj
                [ "matched", null
                  "outcome", jstr "indeterminate"
                  "complete", jbool false
                  "kindResolved", jstr kindResolved
                  "scopeResolved", jstr scopeResolved
                  "projectsSwept", jint 0
                  "projectsRequested", jint projectsRequested
                  "projectsAnalyzed", jint 0
                  "projectsFailed", jint 0
                  "projectsMissing", jint 0
                  "projectsTimedOut", jint 0
                  "projectsBusy", jint 0
                  "projectsNotStarted", jint projectsNotStarted
                  "via", jstr "deadline"
                  "fcsSiteCount", jint 0
                  "fsacFallbackHits", jint 0
                  "fsacFallbackState", jstr "not_started"
                  "fsacFallbackReason", jstr message ]
            :> JsonNode

        jobj
            [ "status", jstr "unknown"
              "errorKind", jstr errorKind
              "message", jstr message
              "timeoutMs", jint deadline.TimeoutMs
              "responseConstructionAllowanceMs", jint deadline.ResponseAllowanceMs
              "outcome", jstr "indeterminate"
              "query", jstr args.query
              "kind", jstr kind
              "kindResolved", jstr kindResolved
              "scope", jstr scope
              "exact", jbool exact
              "resolution", resolution
              "coverage", coverage
              "projectsSwept", jint 0
              "projectsRequested", jint projectsRequested
              "projectsAnalyzed", jint 0
              "projectsFailed", jint 0
              "projectsMissing", jint 0
              "projectsTimedOut", jint 0
              "projectsBusy", jint 0
              "projectsNotStarted", jint projectsNotStarted
              "totalSites", jint 0
              "matchedUseCount", jint 0
              "breakdownComplete", jbool false
              "breakdown",
              jobj
                  [ "definitions", jint 0
                    "references", jint 0
                    "fieldSetLiteral", jint 0
                    "fieldSetUpdate", jint 0
                    "fieldSetMutation", jint 0
                    "fieldPattern", jint 0
                    "fieldRead", jint 0
                    "memberUsages", jint 0 ]
              :> JsonNode
              "sites", JsonArray() :> JsonNode
              "perProject", JsonArray(perProject) :> JsonNode
              "sweepElapsedMs", jint deadline.ElapsedMilliseconds
              "projectDiagnostics", JsonArray() :> JsonNode
              "resultSetComplete", jbool false
              "truncated", jbool false
              "nextCursor", null
              "totalEstimate", jobj [ ("sites", jint 0) ] :> JsonNode
              "totalEstimateIsLowerBound", jbool true
              "paginationRestartRequired", jbool true
              "scopeNote",
              jstr
                  $"find timed out during {phase}; no later semantic phase was started. Retry with a larger timeoutMs after any retained worker completes." ]
        :> JsonNode

let rec private findFailureIsTimeout (ex: exn) =
    match ex with
    | :? TimeoutException -> true
    // A worker can report cancellation without the caller token being cancelled;
    // that remains timeout-equivalent. FindWithinDeadline propagates an actual
    // caller cancellation before invoking this classifier.
    | :? OperationCanceledException -> true
    | :? AggregateException as aggregate ->
        aggregate.Flatten().InnerExceptions |> Seq.exists findFailureIsTimeout
    | _ when not (isNull ex.InnerException) -> findFailureIsTimeout ex.InnerException
    | _ -> false

// ─── check: cached FSAC diagnostics snapshot (issue #128, Stage 1) ──────────────
//
// The `check` tool's speed="fast" path reads the cheap cached FSAC publishDiagnostics
// snapshot instead of re-type-checking in FCS. The FSAC dictionary lives in
// FsAutoCompleteBridge, so — exactly like `find` injecting its fsacProbe — the
// dispatcher projects a context-bound diagnostics response into this struct and hands
// it to the FCS-only substrate, which therefore stays LSP-agnostic.
//
// Honesty contract: a clean fast verdict requires one current publishDiagnostics
// result (including an explicit empty array) for every evaluated in-scope SourceFile.
// Project mismatch, incomplete expectation derivation, missing publications, and stale
// open-document versions are all distinct from a complete zero-error snapshot.
[<NoComparison; NoEquality>]
type internal CheckFsacExpectation =
    { /// Effective project/solution context the snapshot must belong to.
      RequestedProjectPath: string option
      /// Resolved check scope (file/project/workspace).
      Scope: string
      /// Evaluated FCS SourceFiles in the requested scope, normalized to full paths.
      ExpectedFiles: string array
      /// Content-addressed identity of the whole owning project/options/reference
      /// closure, not merely the files returned by this scope.
      ContextFingerprint: string option
      /// False when one or more project options could not be evaluated.
      Complete: bool
      /// Why expectation derivation was incomplete, when applicable.
      FailureReason: string option
      /// Structured causes preserved while deriving evaluated SourceFiles.
      BlockingReasons: JsonNode array
      /// Workspace-only URI glob preserved from CheckArgs.
      FileGlob: string option }

[<NoComparison; NoEquality>]
type internal CheckFsacSnapshot =
    { /// Typed status returned by the context-bound diagnostics bridge.
      Status: string
      /// FSAC workspace reached the "ready" state (lspState = "ready").
      Ready: bool
      /// The active FSAC session contains the requested project/solution context.
      ContextMatched: bool
      /// Every expected file has a current diagnostics publication.
      Complete: bool
      /// Evaluated files requested from FSAC after scope/glob filtering.
      ExpectedFiles: string array
      /// Expected files for which any diagnostics publication was received.
      ReceivedFiles: string array
      /// Expected files with no diagnostics publication in the current session.
      MissingFiles: string array
      /// Expected open files whose publication version is not current.
      StaleFiles: string array
      /// FSAC lifecycle generation that produced this snapshot.
      SessionGeneration: int64 option
      /// Typed failure/readiness/context explanation, when present.
      FailureReason: string option
      /// Structured causes preserved from expectation derivation or FSAC startup.
      BlockingReasons: JsonNode array
      /// Number of expected files FSAC currently holds diagnostics for.
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

let private checkGenericBlockingReason errorKind message retryable =
    jobj
        [ "errorKind", jstr errorKind
          "message", jstr message
          "retryable", jbool retryable ]
    :> JsonNode

[<RequireQualifiedAccess>]
module internal CheckFsacSnapshot =

    let empty: CheckFsacSnapshot =
        { Status = "unavailable"
          Ready = false
          ContextMatched = false
          Complete = false
          ExpectedFiles = [||]
          ReceivedFiles = [||]
          MissingFiles = [||]
          StaleFiles = [||]
          SessionGeneration = None
          FailureReason = Some "No context-bound FSAC diagnostics snapshot was supplied."
          BlockingReasons = [||]
          AnalyzedFileCount = 0
          ErrorCount = 0
          WarningCount = 0
          MostRecentAnalyzedAt = None
          Diagnostics = JsonArray() }

    let unavailable (expectation: CheckFsacExpectation) (reason: string) : CheckFsacSnapshot =
        { empty with
            ExpectedFiles = Array.copy expectation.ExpectedFiles
            MissingFiles = Array.copy expectation.ExpectedFiles
            FailureReason = Some reason
            BlockingReasons = expectation.BlockingReasons |> Array.map _.DeepClone() }

    let unavailableWithBlockingReason
        (expectation: CheckFsacExpectation)
        (reason: string)
        (blockingReason: JsonNode)
        : CheckFsacSnapshot =
        { unavailable expectation reason with
            BlockingReasons =
                Array.append
                    (expectation.BlockingReasons |> Array.map _.DeepClone())
                    [| blockingReason.DeepClone() |] }

    let unavailableWithTypedFailure
        (expectation: CheckFsacExpectation)
        (reason: string)
        (errorKind: string)
        (retryable: bool)
        : CheckFsacSnapshot =
        unavailableWithBlockingReason expectation reason (checkGenericBlockingReason errorKind reason retryable)

    /// Projects the context-bound DiagnosticsForContext envelope into a snapshot.
    /// Tolerant of missing fields — anything it cannot read degrades toward `empty`,
    /// which the fast path reads as "unknown".
    let ofDiagnosticsResponse (resp: JsonNode) : CheckFsacSnapshot =
        try
            let readString (key: string) =
                match resp[key] with
                | :? JsonValue as v ->
                    let mutable value = ""
                    if v.TryGetValue(&value) && not (String.IsNullOrWhiteSpace value) then Some value else None
                | _ -> None

            let readBool (key: string) =
                match resp[key] with
                | :? JsonValue as v ->
                    let mutable value = false
                    if v.TryGetValue(&value) then Some value else None
                | _ -> None

            let readInt (key: string) =
                match resp[key] with
                | :? JsonValue as v ->
                    let mutable value = 0
                    if v.TryGetValue(&value) then Some value else None
                | _ -> None

            let readInt64 (key: string) =
                match resp[key] with
                | :? JsonValue as v ->
                    let mutable value = 0L
                    if v.TryGetValue(&value) then Some value else None
                | _ -> None

            let readStringArray (key: string) =
                match resp[key] with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.choose (fun node ->
                        match node with
                        | :? JsonValue as value ->
                            let mutable text = ""

                            if value.TryGetValue(&text) && not (String.IsNullOrWhiteSpace text) then
                                Some text
                            else
                                None
                        | _ -> None)
                    |> Seq.toArray
                | _ -> [||]

            let status = readString "status" |> Option.defaultValue "infrastructure_error"

            let ready =
                match resp["lspState"] with
                | :? JsonValue as v ->
                    match v.GetValue<string>() with
                    | "ready" -> true
                    | _ -> false
                | _ -> false

            let contextMatched = readBool "contextMatched" |> Option.defaultValue false
            let complete = readBool "complete" |> Option.defaultValue false
            let expectedFiles = readStringArray "expectedFiles"
            let receivedFiles = readStringArray "receivedFiles"
            let missingFiles = readStringArray "missingFiles"
            let staleFiles = readStringArray "staleFiles"
            let sessionGeneration = readInt64 "sessionGeneration"

            let fileCount =
                readInt "diagnosticsFileCount" |> Option.defaultValue receivedFiles.Length

            let responseReason = readString "reason"

            let failureReason =
                readString "message" |> Option.orElse responseReason

            let blockingReasons =
                match resp["blockingReasons"], resp["blockingReason"], readString "errorKind" with
                | (:? JsonArray as reasons), _, _ when reasons.Count > 0 ->
                    reasons |> Seq.map _.DeepClone() |> Seq.toArray
                | _, reason, _ when not (isNull reason) -> [| reason.DeepClone() |]
                | _, _, Some _ -> [| resp.DeepClone() |]
                | _ ->
                    match responseReason with
                    | Some "lsp_lifecycle_gate_busy" ->
                        let message =
                            failureReason
                            |> Option.defaultValue
                                "The diagnostics snapshot was not admitted because another LSP operation is in progress."

                        [| checkGenericBlockingReason "fcs_worker_busy" message true |]
                    | _ ->
                        failureReason
                        |> Option.map (fun message ->
                            [| checkGenericBlockingReason "fsac_unavailable" message true |])
                        |> Option.defaultValue [||]

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

            { Status = status
              Ready = ready
              ContextMatched = contextMatched
              Complete = complete
              ExpectedFiles = expectedFiles
              ReceivedFiles = receivedFiles
              MissingFiles = missingFiles
              StaleFiles = staleFiles
              SessionGeneration = sessionGeneration
              FailureReason = failureReason
              BlockingReasons = blockingReasons
              AnalyzedFileCount = fileCount
              ErrorCount = errorCount
              WarningCount = warningCount
              MostRecentAnalyzedAt = analyzedAt
              Diagnostics = diags }
        with _ ->
            empty

// ─── Curated F# diagnostic explanations (issue #61) ─────────────────────────────
// Plain-language explanation + actionable repair context for the compiler
// diagnostics agents hit most. Keyed by numeric ErrorNumber (FS0039 -> 39).
// Titles and explanations are grounded in the real FCS resource messages —
// the wording for the verified codes was captured from live FCS output.

type internal DiagnosticExplanation =
    { Title: string
      Explanation: string
      LikelyCauses: string list
      RepairHints: string list
      RelatedTools: string list }

let internal curatedDiagnostics: Map<int, DiagnosticExplanation> =
    Map.ofList
        [ 1,
          { Title = "Type mismatch"
            Explanation =
              "An expression has a different type than the surrounding context requires. F# is statically typed and inserts no implicit conversions, so the inferred type and the expected type must line up exactly."
            LikelyCauses =
              [ "A value of the wrong type passed to a function, operator, or binding"
                "A missing conversion (int vs float, string vs char, list vs array)"
                "A type annotation that contradicts the inferred type"
                "A unit-of-measure or generic-parameter mismatch" ]
            RepairHints =
              [ "Read the 'Expecting X but given Y' line — it names both the expected and actual type"
                "Add an explicit conversion (e.g. `float`, `string`, `int`) or correct the annotation"
                "Inspect the offending sub-expression's inferred type with fcs_symbol_at_word" ]
            RelatedTools = [ "check"; "fcs_symbol_at_word"; "fcs_signature_help" ] }

          3,
          { Title = "Value applied as if it were a function"
            Explanation =
              "You wrote `f x` where `f` is a value, not a function, so it cannot be applied to an argument. F# reads juxtaposition as function application."
            LikelyCauses =
              [ "Too many arguments passed to a function"
                "A name shadowed by a non-function value"
                "A missing operator or comma between expressions"
                "Indexing with `xs i` instead of `xs[i]`" ]
            RepairHints =
              [ "Check the arity of the function you meant to call"
                "Use fcs_signature_help at the call site to see the expected parameters" ]
            RelatedTools = [ "fcs_signature_help"; "fcs_symbol_at_word"; "check" ] }

          10,
          { Title = "Syntax error — unexpected token or incomplete construct"
            Explanation =
              "The parser hit a token it did not expect, or a structured construct (let/match/type/module) was left incomplete. This is a syntax error, not a type error."
            LikelyCauses =
              [ "Incorrect indentation (the offside rule) under let/match/if/module"
                "A missing keyword such as `then`, `->`, `=`, `in`, or `done`"
                "Unbalanced parentheses, brackets, or quotation marks"
                "A `let`/`member` placed at the wrong scope" ]
            RepairHints =
              [ "Check the indentation of the line the error points at — F# is whitespace-sensitive"
                "Run textDocument_formatting to normalize layout and reveal the structural mistake" ]
            RelatedTools = [ "check"; "textDocument_formatting"; "fcs_file_outline" ] }

          20,
          { Title = "Expression result is implicitly ignored"
            Explanation =
              "A non-unit expression sits in statement position (e.g. its own line in a sequence), so its result is discarded. F# flags this because a silently dropped value is usually a bug."
            LikelyCauses =
              [ "Calling a function for a side effect but forgetting it returns a value"
                "Writing `=` (comparison) where `<-` (assignment) was intended"
                "A forgotten `let` binding, or a missing `return`/`return!` in a computation expression" ]
            RepairHints =
              [ "If the result is genuinely unwanted, discard it explicitly: `expr |> ignore`"
                "If you meant to keep it, bind it: `let result = expr`"
                "If you meant assignment, use `<-` not `=`" ]
            RelatedTools = [ "check"; "fcs_symbol_at_word" ] }

          25,
          { Title = "Incomplete pattern match"
            Explanation =
              "A `match` (or `function`) does not cover every possible case of the value's type. At runtime an uncovered value raises MatchFailureException."
            LikelyCauses =
              [ "A missing DU case, None/Some arm, or empty/non-empty list arm"
                "A `when` guard that makes an arm non-total"
                "A newly added DU case that left existing matches stale" ]
            RepairHints =
              [ "Add the missing case(s) the message names (e.g. 'None may indicate a case not covered')"
                "Add a catch-all `| _ ->` only if a default is truly intended — otherwise enumerate cases explicitly"
                "List the type's cases with fcs_file_outline or find" ]
            RelatedTools = [ "check"; "fcs_file_outline"; "find" ] }

          26,
          { Title = "Unreachable pattern-match rule"
            Explanation =
              "A match arm can never be reached because an earlier arm already matches everything it would. The dead arm is almost always a logic error."
            LikelyCauses =
              [ "A catch-all `| _ ->` or variable pattern placed before more specific arms"
                "Duplicate or subsumed patterns"
                "An overly broad guard on an earlier arm" ]
            RepairHints =
              [ "Reorder arms so specific patterns precede general ones"
                "Remove the duplicate arm, or tighten the earlier pattern" ]
            RelatedTools = [ "check" ] }

          30,
          { Title = "Value restriction"
            Explanation =
              "A top-level value was inferred to be generic, but the value restriction forbids generalizing something that is not a syntactic function. F# cannot safely make it polymorphic."
            LikelyCauses =
              [ "A partially applied function bound to a value (e.g. `let f = List.map id`)"
                "An empty collection or `None` bound without enough type information"
                "A point-free definition the compiler cannot generalize" ]
            RepairHints =
              [ "Add a type annotation pinning the generic parameter (e.g. `let f : int list -> int list = ...`)"
                "Make the argument explicit: `let f x = List.map id x`" ]
            RelatedTools = [ "check"; "fcs_symbol_at_word" ] }

          35,
          { Title = "Deprecated construct"
            Explanation =
              "The code uses a construct marked deprecated (via an Obsolete attribute or a legacy language form). It still compiles but should be replaced."
            LikelyCauses =
              [ "Calling an API annotated [<Obsolete>]"
                "Using a legacy F# syntax form kept only for compatibility" ]
            RepairHints =
              [ "Read the deprecation message — it usually names the replacement"
                "Find the recommended API with fcs_referenced_symbols or fcs_nuget_members" ]
            RelatedTools = [ "fcs_referenced_symbols"; "fcs_nuget_members"; "check" ] }

          39,
          { Title = "Name is not defined"
            Explanation =
              "An identifier (value, constructor, namespace, module, type, field, or record label) could not be resolved in the current scope. The name is unknown to the compiler here."
            LikelyCauses =
              [ "A missing `open` for the namespace/module that declares the name"
                "A typo or wrong casing in the identifier"
                "Using a name before its declaration (F# resolves top-to-bottom)"
                "A missing project or package reference" ]
            RepairHints =
              [ "Run fcs_suggest_open with the unresolved name to get the exact `open` directive"
                "Check spelling and that the declaration appears earlier in compile order"
                "Confirm the defining project/package is referenced" ]
            RelatedTools = [ "fcs_suggest_open"; "find"; "fcs_referenced_symbols"; "check" ] }

          40,
          { Title = "Recursive object reference checked at runtime"
            Explanation =
              "You defined one or more recursive objects (not functions) with `let rec`, so the compiler inserts a runtime initialization check: a recursive value can be observed before it is fully constructed."
            LikelyCauses =
              [ "A `let rec` binding a value (recursive record/closure) rather than a function"
                "Mutually recursive values that reference each other during construction" ]
            RepairHints =
              [ "Restructure into recursive functions instead of recursive values where possible"
                "Defer the self-reference with `lazy`/`Lazy<_>` or a function indirection" ]
            RelatedTools = [ "check"; "fcs_file_outline" ] }

          41,
          { Title = "No overload matches the arguments"
            Explanation =
              "A method has several overloads but none accepts the argument types (or count) you supplied, so overload resolution failed."
            LikelyCauses =
              [ "An argument of the wrong type for every overload"
                "The wrong number of arguments"
                "An ambiguous numeric literal that fits no overload without an annotation" ]
            RepairHints =
              [ "Read the listed candidate overloads and annotate the offending argument's type"
                "Use fcs_signature_help at the call site to see all overloads and their parameters" ]
            RelatedTools = [ "fcs_signature_help"; "fcs_nuget_members"; "check" ] }

          49,
          { Title = "Uppercase identifier used as a pattern variable"
            Explanation =
              "An uppercase identifier in a pattern is being bound as a fresh variable (capturing everything) rather than matched against an existing case or literal. F# warns because this usually means a case name is misspelled or its module is not opened."
            LikelyCauses =
              [ "A DU case or literal whose module is not `open`ed, so the name binds as a variable"
                "A misspelled pattern or case name"
                "Intending to match a constant but writing it as a bare identifier" ]
            RepairHints =
              [ "`open` the module that declares the case, or qualify it (e.g. `MyDu.CaseName`)"
                "Match a constant via a `[<Literal>]` value or a `when` guard"
                "Run fcs_suggest_open for the intended case name" ]
            RelatedTools = [ "fcs_suggest_open"; "find"; "check" ] }

          64,
          { Title = "Construct is less generic than annotated"
            Explanation =
              "A type annotation promises a generic type parameter, but the body forces it to a concrete type, so the value is not as generic as written. The message names the variable and the type it was constrained to."
            LikelyCauses =
              [ "Using a type-specific operation (e.g. `+`, `.Length`) on a value annotated as generic `'a`"
                "An annotation promising more polymorphism than the implementation delivers" ]
            RepairHints =
              [ "Either drop the generic annotation and let the type be concrete, or use `inline` + SRTP to genuinely generalize the operation"
                "Replace `'a` with the concrete type the message reports" ]
            RelatedTools = [ "check"; "fcs_symbol_at_word" ] }

          66,
          { Title = "Unnecessary upcast"
            Explanation =
              "An upcast (`:>`) converts a value to a type it already has, so the coercion does nothing. F# flags it as redundant."
            LikelyCauses =
              [ "An explicit `:> SomeType` where the expression is already that type"
                "A leftover coercion after a refactor changed the inferred type" ]
            RepairHints = [ "Remove the `:>` coercion" ]
            RelatedTools = [ "check" ] }

          67,
          { Title = "Type test or downcast that always succeeds"
            Explanation =
              "A runtime type test (`:?`) or downcast checks for a type the value statically already has, so it is always true and therefore redundant."
            LikelyCauses =
              [ "A `:?` test against the value's own static type"
                "A downcast the type system already guarantees" ]
            RepairHints =
              [ "Remove the redundant test/cast, or widen the static type if a real test was intended" ]
            RelatedTools = [ "check"; "fcs_symbol_at_word" ] }

          72,
          { Title = "Member lookup on a value of indeterminate type"
            Explanation =
              "You accessed a member (`x.Foo`) before the compiler knew the type of `x`. F# infers types top-to-bottom and left-to-right, so at this point the object's type is still unknown and the member cannot be resolved."
            LikelyCauses =
              [ "A lambda or function parameter whose type is only inferred from a later use"
                "Pipelining into a member access before the type is fixed"
                "A missing type annotation on a parameter" ]
            RepairHints =
              [ "Annotate the parameter (e.g. `fun (x: string) -> x.Length`)"
                "Reorder so the type-determining use comes first" ]
            RelatedTools = [ "check"; "fcs_symbol_at_word"; "fcs_signature_help" ] }

          193,
          { Title = "Type constraint mismatch"
            Explanation =
              "A type was used where it does not satisfy a required constraint — for example an interface, default-constructor, comparison, or byref constraint demanded by a generic parameter or member. The supplied type is incompatible with the constraint."
            LikelyCauses =
              [ "A type argument that does not implement the required interface or constraint"
                "An SRTP/member constraint not satisfied by the concrete type"
                "A byref or struct constraint violation" ]
            RepairHints =
              [ "Check the constraint the message names and supply a type that satisfies it"
                "Confirm which interfaces a type implements with fcs_referenced_symbols" ]
            RelatedTools = [ "fcs_referenced_symbols"; "fcs_signature_help"; "check" ] }

          493,
          { Title = "Member is static, not an instance method"
            Explanation =
              "You called a member through an instance (`obj.Member`), but the member is declared `static`. Static members are invoked on the type, not on a value."
            LikelyCauses =
              [ "Calling a static member via an instance variable"
                "Confusing a static factory/helper with an instance method" ]
            RepairHints =
              [ "Call it on the type: `TypeName.Member(...)` instead of `instance.Member(...)`"
                "Confirm whether the member is static with fcs_nuget_members or fcs_referenced_symbols" ]
            RelatedTools = [ "fcs_nuget_members"; "fcs_referenced_symbols"; "fcs_signature_help" ] }

          505,
          { Title = "Wrong number of arguments to a member"
            Explanation =
              "A method or constructor was called with an argument count no overload accepts. The message reports the arity you supplied and an arity that exists."
            LikelyCauses =
              [ "Passing too many or too few arguments to a .NET method"
                "Forgetting that a member takes a tuple `(a, b)` vs curried arguments"
                "Calling a parameterless member with arguments, or vice-versa" ]
            RepairHints =
              [ "Match the call to one of the overloads the message reports"
                "Use fcs_signature_help at the call site to see the exact parameter lists" ]
            RelatedTools = [ "fcs_signature_help"; "fcs_nuget_members"; "check" ] }

          588,
          { Title = "Block after 'let' is not indented enough"
            Explanation =
              "The expression that should follow a `let` binding is indented at or before the `let`, so the offside rule does not treat it as the binding's body or continuation."
            LikelyCauses =
              [ "The line after `let x = ...` is indented less than the `let`"
                "A dedented continuation in a sequence of bindings"
                "Mixed tabs and spaces breaking the indentation" ]
            RepairHints =
              [ "Indent the following block more than the `let` keyword"
                "Run textDocument_formatting to normalize indentation" ]
            RelatedTools = [ "textDocument_formatting"; "check" ] }

          759,
          { Title = "Cannot create an instance of an abstract type"
            Explanation =
              "You tried to construct a type marked abstract (or one with unimplemented abstract members), so no instances can be created directly."
            LikelyCauses =
              [ "`new` on an [<AbstractClass>] type"
                "Instantiating an interface, or a type with unimplemented abstract members" ]
            RepairHints =
              [ "Use (or define) a concrete subclass that implements the abstract members"
                "Supply the members inline with an object expression: `{ new AbstractType with ... }`" ]
            RelatedTools = [ "fcs_referenced_symbols"; "fcs_file_outline"; "check" ] }

          760,
          { Title = "Create IDisposable with 'new'"
            Explanation =
              "An object whose type implements IDisposable was created without the `new` keyword. F# recommends `new Type(args)` for disposables to make clear the value owns a resource that should be disposed."
            LikelyCauses = [ "Constructing a disposable as `Type(args)` instead of `new Type(args)`" ]
            RepairHints =
              [ "Add the `new` keyword: `use x = new Type(args)`"
                "Bind it with `use` (not `let`) so it is disposed at scope exit" ]
            RelatedTools = [ "check" ] }

          1182,
          { Title = "Unused value"
            Explanation =
              "A bound value or function parameter is never used. This warning is off by default (enabled with --warnon:1182) and flags likely dead code or a typo."
            LikelyCauses =
              [ "A `let` binding or parameter that is never referenced"
                "A parameter kept only for signature compatibility"
                "A typo that references a different name than the one bound" ]
            RepairHints =
              [ "Remove the unused binding, or prefix it with `_` (e.g. `_unused`) to signal intent"
                "If it is a typo, fix the reference to match the bound name" ]
            RelatedTools = [ "find"; "check" ] }

          3261,
          { Title = "Nullness warning"
            Explanation =
              "With nullable reference types enabled, a possibly-null value is used where a non-null value is expected (or vice-versa). The nullability annotations do not line up."
            LikelyCauses =
              [ "Passing a `T | null` value where a non-null `T` is required"
                "Dereferencing a value that may be null without a check"
                "Interop with a .NET API whose nullability annotations differ" ]
            RepairHints =
              [ "Null-check before use (pattern-match on null, or `Option.ofObj`)"
                "Adjust the annotation (`T?`) to reflect the true nullability"
                "Wrap external boundaries that may return null in `Option.ofObj`" ]
            RelatedTools = [ "check"; "fcs_symbol_at_word" ] } ]

// ── Resolution + enrichment helpers for fcs_explain_diagnostic ───────────────────

/// Parse a diagnostic code such as "FS0039", "fs39", or "39" into its numeric part.
let internal parseDiagnosticCode (raw: string) : int option =
    if String.IsNullOrWhiteSpace raw then
        None
    else
        let trimmed = raw.Trim()

        let digits =
            if trimmed.StartsWith("FS", StringComparison.OrdinalIgnoreCase) then
                trimmed.Substring(2)
            else
                trimmed

        match Int32.TryParse digits with
        | true, n -> Some n
        | _ -> None

/// Render a numeric error number back into canonical "FS0039" form.
let internal formatDiagnosticCode (n: int) : string = $"FS%04d{n}"

/// First single-quoted token in an FCS message, e.g. 'Encoding' from an FS0039 text.
let internal firstQuotedToken (message: string) : string option =
    let m = System.Text.RegularExpressions.Regex.Match(message, "'([^']+)'")
    if m.Success then Some m.Groups[1].Value else None

/// Enrich the base repair hints from the raw message. For name-resolution diagnostics
/// (FS0039 / FS0049) the offending name is extracted and a fcs_suggest_open hint is
/// prepended as the most actionable next step.
let internal enrichRepairHints (errorNumber: int) (message: string option) (baseHints: string list) : string list =
    match errorNumber, message with
    | (39 | 49), Some msg ->
        match firstQuotedToken msg with
        | Some name ->
            $"The unresolved name is '{name}' — run fcs_suggest_open with symbolName=\"{name}\" to get the right `open` directive."
            :: baseHints
        | None -> baseHints
    | _ -> baseHints

/// Build a one-line-per-string JSON array.
let internal jstrArray (xs: string list) : JsonNode =
    JsonArray(xs |> List.map jstr |> List.toArray) :> JsonNode

/// Render the final explain-diagnostic envelope from a resolved code, or pass an
/// already-built error envelope straight through. Pure and synchronous so the task
/// continuation that calls it stays a statically compilable state machine (FS3511).
let internal renderExplanation
    (explicitMessage: string option)
    (resolved: Result<int * string option, JsonNode>)
    : JsonNode =
    match resolved with
    | Error envelope -> envelope
    | Ok(errorNumber, fetchedMessage) ->
        let effectiveMessage = explicitMessage |> Option.orElse fetchedMessage
        let codeText = formatDiagnosticCode errorNumber
        let messageNode = effectiveMessage |> Option.map jstr |> Option.defaultValue null

        match Map.tryFind errorNumber curatedDiagnostics with
        | Some entry ->
            let repairHints = enrichRepairHints errorNumber effectiveMessage entry.RepairHints

            jobj
                [ "status", jstr "ok"
                  "code", jstr codeText
                  "title", jstr entry.Title
                  "explanation", jstr entry.Explanation
                  "likelyCauses", jstrArray entry.LikelyCauses
                  "repairHints", jstrArray repairHints
                  "relatedTools", jstrArray entry.RelatedTools
                  "message", messageNode ]
            :> JsonNode
        | None ->
            jobj
                [ "status", jstr "unknown_code"
                  "code", jstr codeText
                  "title", jstr ""
                  "explanation", jstr "No curated entry for this diagnostic code."
                  "likelyCauses", jstrArray []
                  "repairHints",
                  jstrArray
                      [ $"Run `check` to see the full diagnostic, then consult the F# error reference for {codeText}." ]
                  "relatedTools", jstrArray [ "check" ]
                  "message", messageNode ]
            :> JsonNode

// ─── ReviewScanner ──────────────────────────────────────────────────────────────
// AST-based review-candidate inventory backing fcs_review_scan. Walks the FCS untyped
// parse tree with ParsedInput.fold (FCS 43.12+, the same full-tree accumulator used by
// FieldFormClassifier) and tags structurally interesting sites — review CANDIDATES, never
// "bugs". Each tag carries a category, the site range, and a short neutral note. Parse-only:
// no type-checking, no project resolution, no IO beyond the source already in hand.

module private ReviewScanner =

    /// A single review candidate: a category, the source range, and a neutral note.
    [<NoComparison; NoEquality>]
    type Candidate =
        { Category: string
          Range: range
          Note: string }

    // Category names — these are exactly the values accepted by the `categories` filter.
    [<Literal>]
    let MatchWildcard = "match_wildcard"

    [<Literal>]
    let TryWith = "try_with"

    [<Literal>]
    let RaiseOrFailwith = "raise_or_failwith"

    [<Literal>]
    let MutableBinding = "mutable_binding"

    [<Literal>]
    let BlockingCall = "blocking_call"

    [<Literal>]
    let CastOrBox = "cast_or_box"

    [<Literal>]
    let Reflection = "reflection"

    [<Literal>]
    let LargeFunction = "large_function"

    /// Bindings whose RHS spans more than this many source lines are flagged.
    [<Literal>]
    let LargeFunctionLineThreshold = 60

    /// Every category this scanner can emit, in a stable display order.
    let allCategories =
        [ MatchWildcard
          TryWith
          RaiseOrFailwith
          MutableBinding
          BlockingCall
          CastOrBox
          Reflection
          LargeFunction ]

    let private allCategorySet = Set.ofList allCategories

    /// Is this a category this scanner knows how to emit?
    let isKnownCategory (category: string) = allCategorySet.Contains category

    /// Functions/operators that raise instead of returning a Result.
    let private raiseNames =
        set [ "failwith"; "failwithf"; "raise"; "reraise"; "invalidArg"; "invalidOp"; "nullArg" ]

    /// Member names whose access typically blocks an async path.
    let private blockingMembers = set [ "Result"; "Wait"; "GetResult" ]

    /// Reflection entry points reached through a `.` member access.
    let private reflectionMembers =
        set
            [ "GetType"
              "GetProperty"
              "GetProperties"
              "GetMethod"
              "GetMethods"
              "GetField"
              "GetFields"
              "GetMember"
              "GetMembers"
              "InvokeMember"
              "GetCustomAttributes"
              "GetCustomAttribute"
              "MakeGenericType"
              "GetConstructor"
              "GetConstructors" ]

    /// Identifiers that box/unbox.
    let private boxNames = set [ "box"; "unbox" ]

    /// Identifiers that reflect over a type.
    let private reflectionIdents = set [ "typeof"; "typedefof" ]

    /// Last identifier segment of a long identifier (e.g. `task.Result` → "Result").
    let private lastIdent (lid: SynLongIdent) : string option =
        lid.LongIdent |> List.tryLast |> Option.map (fun ident -> ident.idText)

    /// True when the pattern is a bare `_` wildcard, looking through parentheses, type
    /// annotations, and attributes but NOT through `as`/`|` (which bind or branch and so
    /// are not a plain catch-all).
    let rec private isWildcardPat (pat: SynPat) : bool =
        match pat with
        | SynPat.Wild _ -> true
        | SynPat.Paren(inner, _) -> isWildcardPat inner
        | SynPat.Typed(inner, _, _) -> isWildcardPat inner
        | SynPat.Attrib(inner, _, _) -> isWildcardPat inner
        | _ -> false

    /// Ranges of the bare-wildcard clauses among a match/function clause list.
    let private wildcardClauseRanges (clauses: SynMatchClause list) : range list =
        clauses
        |> List.choose (fun (SynMatchClause(pat, _, _, _, _, _)) ->
            if isWildcardPat pat then Some pat.Range else None)

    /// Walk one parsed input, accumulating candidates whose category is in `wanted`.
    let scan (wanted: Set<string>) (input: ParsedInput) : Candidate list =
        let acc = ResizeArray<Candidate>()

        let add (category: string) (range: range) (note: string) =
            if wanted.Contains category then
                acc.Add { Category = category; Range = range; Note = note }

        let scanExpr (expr: SynExpr) =
            match expr with
            | SynExpr.TryWith(_, _, range, _, _, _) ->
                add TryWith range "try/with handler — confirm it surfaces (or deliberately swallows) the error"
            | SynExpr.Match(_, _, clauses, _, _)
            | SynExpr.MatchBang(_, _, clauses, _, _) ->
                for r in wildcardClauseRanges clauses do
                    add MatchWildcard r "wildcard `_` branch — confirm the collapsed cases are intentional"
            | SynExpr.MatchLambda(_, _, clauses, _, _) ->
                for r in wildcardClauseRanges clauses do
                    add MatchWildcard r "wildcard `_` branch — confirm the collapsed cases are intentional"
            | SynExpr.Ident ident ->
                let name = ident.idText

                if raiseNames.Contains name then
                    add
                        RaiseOrFailwith
                        ident.idRange
                        "raises instead of returning Result — fine for invariants, reconsider for business errors"
                elif boxNames.Contains name then
                    add CastOrBox ident.idRange "box/unbox — confirm the runtime type round-trips"
                elif reflectionIdents.Contains name then
                    add Reflection ident.idRange "typeof/typedefof — reflection can resist AOT/trimming"
            | SynExpr.LongIdent(_, lid, _, range) ->
                // A dotted value access like `task.Result` or `o.GetType` parses as a
                // LongIdent (not DotGet) when the receiver is a simple identifier, so the
                // member-access categories are matched here on the trailing segment too.
                match lastIdent lid with
                | Some name when raiseNames.Contains name ->
                    add
                        RaiseOrFailwith
                        range
                        "raises instead of returning Result — fine for invariants, reconsider for business errors"
                | Some name when blockingMembers.Contains name ->
                    add
                        BlockingCall
                        range
                        "blocking call (.Result/.Wait/.GetResult) — confirm it isn't blocking an async path"
                | Some name when reflectionMembers.Contains name ->
                    add Reflection range "reflection member access — reflection can resist AOT/trimming"
                | _ -> ()
            | SynExpr.DotGet(_, _, lid, range) ->
                match lastIdent lid with
                | Some name when blockingMembers.Contains name ->
                    add
                        BlockingCall
                        range
                        "blocking call (.Result/.Wait/.GetResult) — confirm it isn't blocking an async path"
                | Some name when reflectionMembers.Contains name ->
                    add Reflection range "reflection member access — reflection can resist AOT/trimming"
                | _ -> ()
            | SynExpr.Downcast(_, _, range) -> add CastOrBox range ":?> downcast — confirm the cast holds at runtime"
            | SynExpr.InferredDowncast(_, range) -> add CastOrBox range "downcast — confirm the cast holds at runtime"
            | _ -> ()

        let scanBinding (binding: SynBinding) =
            let (SynBinding(_, _, _, isMutable, _, _, _, headPat, _, _, _, _, _)) = binding

            if isMutable then
                add MutableBinding headPat.Range "mutable binding — confirm the mutation stays local and is necessary"

            let rhs = binding.RangeOfBindingWithRhs
            let span = rhs.EndLine - rhs.StartLine + 1

            if span > LargeFunctionLineThreshold then
                add LargeFunction headPat.Range $"large binding (~{span} lines) — consider decomposing for readability"

        (acc, input)
        ||> ParsedInput.fold (fun acc _path node ->
            match node with
            | SyntaxNode.SynExpr expr -> scanExpr expr
            | SyntaxNode.SynBinding binding -> scanBinding binding
            | _ -> ()

            acc)
        |> ignore

        List.ofSeq acc


// Shared linear-time regexes for fcs_tests_for_symbol. The explicit timeout is a
// second safety boundary around the synchronous enclosing-test scan: the outer
// stopwatch is cooperative and cannot interrupt a Regex.Match already in flight.
let private enclosingTestRegexOptions =
    System.Text.RegularExpressions.RegexOptions.NonBacktracking

let private enclosingTestRegexTimeout = TimeSpan.FromSeconds 1.0

let private testAttrRegex =
    System.Text.RegularExpressions.Regex(
        """\[<\s*(?:[\w.]+\.)?(?:Fact|Theory|Test|TestCase|TestMethod|Property)(?:Attribute)?\b""",
        enclosingTestRegexOptions,
        enclosingTestRegexTimeout
    )

let private expectoLabelRegex =
    System.Text.RegularExpressions.Regex(
        "\\b(?:ftestCaseAsync|ptestCaseAsync|testCaseAsync|ftestCase|ptestCase|testCase|ftestAsync|ptestAsync|testAsync|ftestProperty|ptestProperty|testProperty|test)\\s+\"([^\"]*)\"",
        enclosingTestRegexOptions,
        enclosingTestRegexTimeout
    )

let private bindingNameRegex =
    System.Text.RegularExpressions.Regex(
        """\b(?:let|member)\s+(?:rec\s+|inline\s+|mutable\s+|private\s+|internal\s+|this\.|_\.)*(``[^`]+``|[A-Za-z_][\w']*)""",
        enclosingTestRegexOptions,
        enclosingTestRegexTimeout
    )

// A test binding cannot own source that has crossed into a later declaration
// scope, even when no intervening let/member exists. This is deliberately
// line-oriented and conservative: module/namespace/type/exception declarations
// mutually-recursive `and` declarations, and module/type initializers (`do`/`do!`)
// at the same or shallower indentation terminate the enclosing-test candidate.
// The `do` alternative consumes any F# token delimiter but excludes identifier
// continuations, so `do`, `do(...)`, `do(*comment*)`, and `do//comment` match while
// `double` and an identifier such as `do'` do not.
let private enclosingTestScopeBoundaryRegex =
    System.Text.RegularExpressions.Regex(
        """^\s*(?:(?:module|namespace)\s+(?:rec\s+)?|(?:type|exception|and)\s+|do(?:[^\w']|$))""",
        enclosingTestRegexOptions,
        enclosingTestRegexTimeout
    )

type private EnclosingTestIdentity =
    { Name: string
      StartLine: int
      StartColumn: int }

let private fsharpTypeNameRegex =
    System.Text.RegularExpressions.Regex(
        @"(?<![\w.])Microsoft\.FSharp\.(?:Core|Collections)\.(?<name>[\w']+)(?![\w])",
        System.Text.RegularExpressions.RegexOptions.Compiled
    )


// ─── FcsBridge ─────────────────────────────────────────────────────────────────

[<Struct>]
type private ProjectOptionsInputKind =
    | FileInput
    | DirectoryInput

[<Struct>]
type private ProjectOptionsInputStamp =
    { Kind: ProjectOptionsInputKind
      Path: string
      Exists: bool
      LastWriteTimeUtcTicks: int64
      Length: int64
      ContentHash: string
      Reliable: bool }

[<NoComparison; NoEquality>]
type private ProjectOptionsFingerprint =
    { Inputs: ProjectOptionsInputStamp array }

[<NoComparison; NoEquality>]
type private ProjectOptionsCacheEntry =
    { Options: FSharpProjectOptions
      Source: string
      Fingerprint: ProjectOptionsFingerprint option
      ScriptSourceHash: string option
      EvaluatedSnapshot: EvaluatedProjectSnapshot option }

/// Builds the content identity shared by every project-analysis cache.
///
/// The aggregate is length-framed rather than delimiter-joined: source paths and
/// compiler options may legally contain punctuation such as `|`, so concatenating
/// them admits collisions. Source order is deliberately preserved because it is
/// compile order in F#. File metadata remains part of the identity to retain the
/// existing rebuild signal, but source and reference contents are hashed as well;
/// an editor can replace bytes while restoring the original mtime and length.
module internal AnalysisSnapshotKey =

    [<NoComparison; NoEquality>]
    type private FileSnapshot =
        { State: string
          LastWriteTimeUtcTicks: int64
          Length: int64
          ContentHash: string }

    let private pathComparer =
        if OperatingSystem.IsWindows() then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    let private normalizeFrom (baseDirectory: string) (path: string) =
        let normalized =
            try
                if String.IsNullOrWhiteSpace(path) then
                    ""
                else
                    let candidate = path.Trim().Trim('"')

                    if Path.IsPathFullyQualified(candidate) then
                        normalizePath candidate
                    elif String.IsNullOrWhiteSpace(baseDirectory) then
                        normalizePath candidate
                    else
                        normalizePath (Path.Combine(baseDirectory, candidate))
            with _ ->
                if isNull path then "" else path

        // Windows paths are case-insensitive but GetFullPath preserves caller casing.
        // Canonicalize it in the serialized identity as well as in dictionary lookup,
        // otherwise the same input path can spuriously produce two snapshot keys.
        if OperatingSystem.IsWindows() then
            normalized.ToUpperInvariant()
        else
            normalized

    let private tryReferencePath baseDirectory (optionText: string) =
        if optionText.StartsWith("-r:", StringComparison.Ordinal) then
            Some(normalizeFrom baseDirectory (optionText.Substring 3))
        elif optionText.StartsWith("--reference:", StringComparison.Ordinal) then
            Some(normalizeFrom baseDirectory (optionText.Substring 12))
        else
            None

    let private captureFile (path: string) =
        let unavailable state =
            { State = state
              LastWriteTimeUtcTicks = -1L
              Length = -1L
              ContentHash = "" }

        let rec capture attemptsRemaining =
            try
                let before = FileInfo(path)
                before.Refresh()

                if not before.Exists then
                    unavailable "missing"
                else
                    let beforeTicks = before.LastWriteTimeUtc.Ticks
                    let beforeLength = before.Length

                    use stream =
                        new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite ||| FileShare.Delete
                        )

                    use sha = System.Security.Cryptography.SHA256.Create()
                    let contentHash = sha.ComputeHash(stream) |> Convert.ToHexString
                    let after = FileInfo(path)
                    after.Refresh()

                    if
                        attemptsRemaining > 0
                        && (after.LastWriteTimeUtc.Ticks <> beforeTicks || after.Length <> beforeLength)
                    then
                        capture (attemptsRemaining - 1)
                    else
                        { State =
                            if after.LastWriteTimeUtc.Ticks = beforeTicks && after.Length = beforeLength then
                                "present"
                            else
                                "changed-during-read"
                          LastWriteTimeUtcTicks = after.LastWriteTimeUtc.Ticks
                          Length = after.Length
                          ContentHash = contentHash }
            with _ ->
                unavailable "unreadable"

        capture 1

    let create (projectOptions: FSharpProjectOptions) =
        use aggregate =
            System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256
            )

        let append (value: string) =
            let bytes = System.Text.Encoding.UTF8.GetBytes(if isNull value then "" else value)

            let header =
                System.Text.Encoding.ASCII.GetBytes(
                    bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                )

            aggregate.AppendData(header)
            aggregate.AppendData(bytes)

        let appendInt (value: int) =
            append (value.ToString(System.Globalization.CultureInfo.InvariantCulture))

        let appendInt64 (value: int64) =
            append (value.ToString(System.Globalization.CultureInfo.InvariantCulture))

        let fileSnapshots = System.Collections.Generic.Dictionary<string, FileSnapshot>(pathComparer)

        let appendFile role index path =
            let normalized = normalizeFrom "" path

            let snapshot =
                match fileSnapshots.TryGetValue(normalized) with
                | true, existing -> existing
                | false, _ ->
                    let captured = captureFile normalized
                    fileSnapshots[normalized] <- captured
                    captured

            append role
            appendInt index
            append normalized
            append snapshot.State
            appendInt64 snapshot.LastWriteTimeUtcTicks
            appendInt64 snapshot.Length
            append snapshot.ContentHash

        let visitedProjects = System.Collections.Generic.HashSet<string>(pathComparer)

        let rec appendProject relation parentDirectory (options: FSharpProjectOptions) =
            let projectPath = normalizeFrom parentDirectory options.ProjectFileName
            let projectDirectory = Path.GetDirectoryName(projectPath)

            append "project"
            append relation
            append projectPath
            append (options.ProjectId |> Option.defaultValue "")
            append (if options.UseScriptResolutionRules then "script" else "project")
            append (if options.IsIncompleteTypeCheckEnvironment then "incomplete" else "complete")

            append "compiler-options"
            appendInt options.OtherOptions.Length

            for index, optionText in options.OtherOptions |> Array.indexed do
                appendInt index
                append optionText

                match tryReferencePath projectDirectory optionText with
                | Some referencePath -> appendFile "assembly-reference" index referencePath
                | None -> ()

            append "ordered-sources"
            appendInt options.SourceFiles.Length

            for index, sourcePath in options.SourceFiles |> Array.indexed do
                appendFile "source" index (normalizeFrom projectDirectory sourcePath)

            append "referenced-projects"
            appendInt options.ReferencedProjects.Length

            for index, referencedProject in options.ReferencedProjects |> Array.indexed do
                appendInt index

                match referencedProject with
                | FSharpReferencedProject.FSharpReference(outputFile, referencedOptions) ->
                    let referencedProjectPath =
                        normalizeFrom projectDirectory referencedOptions.ProjectFileName

                    append "fsharp-project-reference"
                    appendFile "project-output" index (normalizeFrom projectDirectory outputFile)
                    append referencedProjectPath

                    if visitedProjects.Add(referencedProjectPath) then
                        appendProject "referenced" projectDirectory referencedOptions
                    else
                        append "already-visited"
                | _ ->
                    // PE/IL references have no source options. Their evaluated -r:
                    // inputs and file contents are already included above.
                    append "binary-project-reference"

        append "fslangmcp-analysis-snapshot-v1"
        visitedProjects.Add(normalizeFrom "" projectOptions.ProjectFileName) |> ignore
        appendProject "root" "" projectOptions
        aggregate.GetHashAndReset() |> Convert.ToHexString

/// Adapts Ionide.ProjInfo's evaluated MSBuild result to the small, stable model
/// shared by project_health and fsharp_project_inspect.
module private EvaluatedProjectModel =

    let private nonBlank (value: string) =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private normalizeFrom (baseDirectory: string) (path: string) =
        try
            if Path.IsPathFullyQualified path then
                Path.GetFullPath path
            else
                Path.GetFullPath(Path.Combine(baseDirectory, path))
        with _ ->
            path

    let private splitProjectList (values: string list) =
        values
        |> List.collect (fun value ->
            value.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.toList)

    let private tryMapValueIgnoreCase name (values: Map<string, string>) =
        values
        |> Map.toSeq
        |> Seq.tryPick (fun (key, value) ->
            if String.Equals(key, name, StringComparison.OrdinalIgnoreCase) then
                nonBlank value
            else
                None)

    let private evaluatedProperties (project: Ionide.ProjInfo.Types.ProjectOptions) =
        let fromAllProperties =
            project.AllProperties
            |> Map.toList
            |> List.choose (fun (name, values) ->
                values
                |> Set.toList
                |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> List.tryLast
                |> Option.bind nonBlank
                |> Option.map (fun value -> name, value))
            |> Map.ofList

        project.Properties
        |> List.fold (fun properties property -> Map.add property.Name property.Value properties) fromAllProperties

    let private tryProperty (names: string list) (properties: Map<string, string>) =
        names |> List.tryPick (fun name -> tryMapValueIgnoreCase name properties)

    let private inferSdk (importedProjects: string list) =
        importedProjects
        |> List.tryPick (fun path ->
            let segments =
                path.Split(
                    [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                    StringSplitOptions.RemoveEmptyEntries
                )

            segments
            |> Array.tryFindIndex (fun segment -> String.Equals(segment, "Sdks", StringComparison.OrdinalIgnoreCase))
            |> Option.bind (fun index ->
                if index + 1 < segments.Length then nonBlank segments[index + 1] else None))

    let private outputType (project: Ionide.ProjInfo.Types.ProjectOptions) =
        match project.ProjectOutputType with
        | Ionide.ProjInfo.Types.ProjectOutputType.Library -> Some "Library"
        | Ionide.ProjInfo.Types.ProjectOutputType.Exe -> Some "Exe"
        | Ionide.ProjInfo.Types.ProjectOutputType.Custom value -> nonBlank value

    let private compileFiles (project: Ionide.ProjInfo.Types.ProjectOptions) =
        let projectDirectory = Path.GetDirectoryName(Path.GetFullPath project.ProjectFileName)

        let pathComparer =
            if OperatingSystem.IsWindows() then
                StringComparer.OrdinalIgnoreCase
            else
                StringComparer.Ordinal

        let compileItems =
            project.Items
            |> List.map (function
                | Ionide.ProjInfo.Types.ProjectItem.Compile(name, fullPath, metadata) ->
                    let normalized = normalizeFrom projectDirectory fullPath

                    let link =
                        metadata
                        |> Option.bind (tryMapValueIgnoreCase "Link")

                    normalized, name, link)

        let allEvaluatedCompileItems =
            project.AllItems
            |> Map.toSeq
            |> Seq.tryPick (fun (itemType, items) ->
                if String.Equals(itemType, "Compile", StringComparison.OrdinalIgnoreCase) then
                    Some(Set.toList items)
                else
                    None)
            |> Option.defaultValue []
            |> List.map (fun (includePath, metadata) ->
                let fullPath =
                    tryMapValueIgnoreCase "FullPath" metadata
                    |> Option.defaultValue includePath
                    |> normalizeFrom projectDirectory

                fullPath, includePath, tryMapValueIgnoreCase "Link" metadata)

        let metadataByPath =
            System.Collections.Generic.Dictionary<string, string * string option>(pathComparer)

        for path, includePath, link in allEvaluatedCompileItems @ compileItems do
            metadataByPath[path] <- includePath, link

        let orderedItems = ResizeArray<string * string * string option>()
        let seenPaths = System.Collections.Generic.HashSet<string>(pathComparer)

        let add path includePath link =
            if seenPaths.Add(path) then
                orderedItems.Add(path, includePath, link)

        // ProjectItem.Compile is the evaluated MSBuild item order and carries Link
        // metadata. FCS SourceFiles is retained as a fallback/completeness check;
        // AllItems covers imported/default items omitted by older ProjInfo shapes.
        for path, includePath, link in compileItems do
            add path includePath link

        for sourceFile in project.SourceFiles do
            let path = normalizeFrom projectDirectory sourceFile

            match metadataByPath.TryGetValue(path) with
            | true, (includePath, link) -> add path includePath link
            | false, _ -> add path (Path.GetRelativePath(projectDirectory, path)) None

        for path, includePath, link in allEvaluatedCompileItems do
            add path includePath link

        orderedItems
        |> projectFilesFromEvaluatedItems

    let private packageReferences (project: Ionide.ProjInfo.Types.ProjectOptions) =
        let explicitByName =
            project.PackageReferences
            |> List.map (fun reference -> reference.Name, reference)
            |> Map.ofList

        let evaluatedItems =
            project.AllItems
            |> Map.toSeq
            |> Seq.tryPick (fun (itemType, items) ->
                if String.Equals(itemType, "PackageReference", StringComparison.OrdinalIgnoreCase) then
                    Some(Set.toList items)
                else
                    None)
            |> Option.defaultValue []

        let fromEvaluatedItem (packageId, metadata) =
            let explicit = Map.tryFind packageId explicitByName

            let version =
                tryMapValueIgnoreCase "Version" metadata
                |> Option.orElseWith (fun () -> tryMapValueIgnoreCase "VersionOverride" metadata)
                |> Option.orElseWith (fun () -> explicit |> Option.bind (fun reference -> nonBlank reference.Version))

            let fullPath =
                tryMapValueIgnoreCase "FullPath" metadata
                |> Option.orElseWith (fun () -> explicit |> Option.bind (fun reference -> nonBlank reference.FullPath))

            { PackageId = packageId
              Version = version
              FullPath = fullPath
              IncludeAssets = tryMapValueIgnoreCase "IncludeAssets" metadata
              PrivateAssets = tryMapValueIgnoreCase "PrivateAssets" metadata }

        let evaluated = evaluatedItems |> List.map fromEvaluatedItem
        let evaluatedNames = evaluated |> List.map _.PackageId |> Set.ofList

        let unresolvedSpecific =
            project.PackageReferences
            |> List.filter (fun reference -> not (evaluatedNames.Contains reference.Name))
            |> List.map (fun reference ->
                { PackageId = reference.Name
                  Version = nonBlank reference.Version
                  FullPath = nonBlank reference.FullPath
                  IncludeAssets = None
                  PrivateAssets = None })

        evaluated @ unresolvedSpecific
        |> List.distinctBy (fun reference -> reference.PackageId.ToUpperInvariant())

    let create
        (evaluationSource: string)
        (project: Ionide.ProjInfo.Types.ProjectOptions)
        (fcsOptions: FSharpProjectOptions)
        =
        let projectPath = Path.GetFullPath project.ProjectFileName
        let projectDirectory = Path.GetDirectoryName projectPath
        let properties = evaluatedProperties project

        let importedProjects =
            [ yield! project.ProjectSdkInfo.MSBuildAllProjects

              match tryProperty [ "MSBuildAllProjects" ] properties with
              | Some allProjects -> yield allProjects
              | None -> ()

              for propertyName in
                  [ "DirectoryBuildPropsPath"
                    "DirectoryBuildTargetsPath"
                    "DirectoryPackagesPropsPath" ] do
                  match tryProperty [ propertyName ] properties with
                  | Some importPath -> yield importPath
                  | None -> () ]
            |> splitProjectList
            |> List.map (normalizeFrom projectDirectory)
            |> List.filter (fun path -> not (String.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase)))
            |> List.distinct

        let targetFrameworks =
            match project.ProjectSdkInfo.TargetFrameworks |> List.filter (String.IsNullOrWhiteSpace >> not) with
            | [] -> project.TargetFramework |> nonBlank |> Option.toList
            | frameworks -> frameworks

        let projectReferences =
            project.ReferencedProjects
            |> List.map (fun reference ->
                { IncludePath = reference.RelativePath
                  ProjectPath = normalizeFrom projectDirectory reference.ProjectFileName
                  TargetFramework = nonBlank reference.TargetFramework })

        let existingReferences, totalReferences = ReferenceResolution.probe fcsOptions.OtherOptions

        { ProjectPath = projectPath
          ProjectDirectory = projectDirectory
          ProjectName = Path.GetFileNameWithoutExtension projectPath
          EvaluationSource = evaluationSource
          Sdk =
            tryProperty [ "MSBuildProjectSdk"; "ProjectSdk"; "Sdk" ] properties
            |> Option.orElseWith (fun () -> inferSdk importedProjects)
          TargetFramework = nonBlank project.TargetFramework
          TargetFrameworks = targetFrameworks
          OutputType = outputType project
          AssemblyName =
            tryProperty [ "AssemblyName" ] properties
            |> Option.defaultValue (Path.GetFileNameWithoutExtension projectPath)
          Configuration = nonBlank project.ProjectSdkInfo.Configuration
          IsTestProject = project.ProjectSdkInfo.IsTestProject
          RestoreSucceeded = project.ProjectSdkInfo.RestoreSuccess
          TargetPath = nonBlank project.TargetPath
          Properties = properties
          Files = compileFiles project
          PackageReferences = packageReferences project
          ProjectReferences = projectReferences
          ImportedProjects = importedProjects
          OtherOptions = Array.copy fcsOptions.OtherOptions
          ReferencesExisting = existingReferences
          ReferencesTotal = totalReferences }

type internal ProjectEvaluationBusyException(projectPath: string) =
    inherit
        InvalidOperationException(
            $"project evaluation busy: another Ionide/MSBuild evaluation is active; retry '{projectPath}' after it completes."
        )

    member _.ProjectPath = projectPath

/// Admission control for Ionide/MSBuild evaluation. This deliberately has no
/// cross-key wait queue: same-key sharing happens in optionsInFlight, while a
/// distinct key is rejected promptly instead of accumulating an unbounded set
/// of non-cancellable waiters behind a hung MSBuild load.
type internal ProjectEvaluationAdmission(capacity: int) =
    do
        if capacity < 1 then
            invalidArg (nameof capacity) "Project evaluation capacity must be at least one."

    let slots = new SemaphoreSlim(capacity, capacity)
    let mutable activeCount = 0
    let mutable startedCount = 0L
    let mutable rejectedCount = 0L
    let mutable maxObservedConcurrency = 0

    let updateMaximum candidate =
        let mutable observed = Volatile.Read(&maxObservedConcurrency)

        while candidate > observed do
            let prior = Interlocked.CompareExchange(&maxObservedConcurrency, candidate, observed)

            if prior = observed then
                observed <- candidate
            else
                observed <- prior

    member _.TryRun(projectPath: string, work: unit -> Task<'T>) : Task<'T> =
        if not (slots.Wait(0)) then
            Interlocked.Increment(&rejectedCount) |> ignore
            Task.FromException<'T>(ProjectEvaluationBusyException(projectPath))
        else
            let active = Interlocked.Increment(&activeCount)
            Interlocked.Increment(&startedCount) |> ignore
            updateMaximum active

            task {
                try
                    return! work ()
                finally
                    Interlocked.Decrement(&activeCount) |> ignore
                    slots.Release() |> ignore
            }

    member _.ActiveCount = Volatile.Read(&activeCount)
    member _.StartedCount = Volatile.Read(&startedCount)
    member _.RejectedCount = Volatile.Read(&rejectedCount)
    member _.MaxObservedConcurrency = Volatile.Read(&maxObservedConcurrency)

type internal BoundedCheckWorkBusyException(operation: string, target: string) =
    inherit
        InvalidOperationException(
            $"{operation} busy: all admitted workers are still active; retry '{target}' after one completes."
        )

    member _.Operation = operation
    member _.Target = target

/// Machine-readable reason why a trusted Check could not produce a semantic
/// verdict. Keep this typed until the response boundary: collapsing an SDK
/// pre-flight exception to `ex.Message` made `check` the odd tool out (#244).
[<NoComparison; NoEquality>]
type internal CheckBlockingFailure =
    | CheckTimedOut
    | CheckCancelled of message: string
    | CheckBusy of message: string
    | CheckSdkNotFound of failure: SdkPreflight.SdkNotFoundException
    | CheckProjectFailure of message: string

let rec private boundedCheckWorkFailureIsBusy (ex: exn) =
    match ex with
    | :? ProjectEvaluationBusyException -> true
    | :? BoundedCheckWorkBusyException -> true
    | :? AggregateException as aggregate ->
        aggregate.Flatten().InnerExceptions |> Seq.exists boundedCheckWorkFailureIsBusy
    | _ when not (isNull ex.InnerException) -> boundedCheckWorkFailureIsBusy ex.InnerException
    | _ -> false

/// No-queue admission for Check work that cannot be cancelled once it has started.
/// Exact-key callers share a Lazy task outside this type; a distinct key is rejected
/// immediately rather than becoming an abandoned waiter after its caller times out.
type internal BoundedCheckWorkAdmission(operation: string, capacity: int) =
    do
        if capacity < 1 then
            invalidArg (nameof capacity) "Check work capacity must be at least one."

    let slots = new SemaphoreSlim(capacity, capacity)
    let mutable activeCount = 0
    let mutable startedCount = 0L
    let mutable rejectedCount = 0L
    let mutable maxObservedConcurrency = 0

    let updateMaximum candidate =
        let mutable observed = Volatile.Read(&maxObservedConcurrency)

        while candidate > observed do
            let prior = Interlocked.CompareExchange(&maxObservedConcurrency, candidate, observed)

            if prior = observed then
                observed <- candidate
            else
                observed <- prior

    member _.TryRun(target: string, work: unit -> Task<'T>) : Task<'T> =
        if not (slots.Wait(0)) then
            Interlocked.Increment(&rejectedCount) |> ignore
            Task.FromException<'T>(BoundedCheckWorkBusyException(operation, target))
        else
            let active = Interlocked.Increment(&activeCount)
            Interlocked.Increment(&startedCount) |> ignore
            updateMaximum active

            task {
                try
                    return! work ()
                finally
                    Interlocked.Decrement(&activeCount) |> ignore
                    slots.Release() |> ignore
            }

    member _.ActiveCount = Volatile.Read(&activeCount)
    member _.StartedCount = Volatile.Read(&startedCount)
    member _.RejectedCount = Volatile.Read(&rejectedCount)
    member _.MaxObservedConcurrency = Volatile.Read(&maxObservedConcurrency)

/// One exact-key actual worker with caller-owned waiters. The worker never captures
/// the first caller's deadline: it only learns whether at least one caller still owns
/// an interest in continuing. Once a worker observes zero waiters, the entry closes to
/// late joiners and is removed only after that exact worker has settled.
[<Sealed>]
type private WaiterAwareSingleFlight<'T>
    (
        start: (unit -> bool) -> Task<'T>,
        onCompleted: WaiterAwareSingleFlight<'T> -> unit
    ) as this =
    let gate = obj ()
    let waiters = System.Collections.Generic.Dictionary<int, unit -> bool>()
    let mutable nextWaiterId = 0
    let mutable acceptingWaiters = true

    let close () =
        lock gate (fun () -> acceptingWaiters <- false)

    let operation =
        lazy
            (task {
                try
                    return! start this.HasActiveWaiters
                finally
                    close ()
                    onCompleted this
            })

    // Liveness predicates run under gate: they must be fast, non-blocking, and must
    // not call back into this flight. Only cancellation is interpreted as inactivity;
    // other exceptions indicate a broken caller contract and are not hidden.
    member _.TryAddWaiter(isActive: unit -> bool) =
        lock gate (fun () ->
            if acceptingWaiters then
                nextWaiterId <- nextWaiterId + 1
                waiters.Add(nextWaiterId, isActive)
                Some nextWaiterId
            else
                None)

    member _.ReleaseWaiter(waiterId: int) =
        lock gate (fun () -> waiters.Remove(waiterId) |> ignore)

    member _.HasActiveWaiters() =
        lock gate (fun () ->
            let expired = ResizeArray<int>()

            for waiter in waiters do
                let active =
                    try
                        waiter.Value()
                    with :? OperationCanceledException ->
                        false

                if not active then
                    expired.Add(waiter.Key)

            for waiterId in expired do
                waiters.Remove(waiterId) |> ignore

            if waiters.Count > 0 then
                true
            else
                acceptingWaiters <- false
                false)

    member _.Operation = operation.Value
    member _.WaiterCount = lock gate (fun () -> waiters.Count)

[<Sealed>]
type private WaiterAwareSingleFlightWaiter<'T>(flight: WaiterAwareSingleFlight<'T>, waiterId: int) =
    let mutable released = 0

    member _.Operation = flight.Operation

    member _.Release() =
        if Interlocked.Exchange(&released, 1) = 0 then
            flight.ReleaseWaiter(waiterId)

    interface IDisposable with
        member this.Dispose() = this.Release()

[<RequireQualifiedAccess>]
type private CheckTargetDiscoveryResult =
    | Scope of string
    | Project of string option
    | Projects of string array
    | Busy of string

// ─── NugetPackageMap (issue #191) ───────────────────────────────────────────────
// A NuGet package id and the assembly it ships are NOT the same string.
// `Microsoft.Orleans.Core.Abstractions` ships `Orleans.Core.Abstractions.dll`; this repo's own
// `Microsoft.VisualStudio.Threading.Only` ships `Microsoft.VisualStudio.Threading.dll`.
// fcs_nuget_types / fcs_nuget_members matched on assembly SimpleName ALONE, so every such
// package resolved to zero assemblies and returned an empty — but `status: "ok"` — payload,
// which reads as "the type does not exist" (#100 field report).
//
// The fix is a real package→assembly map built from the project's OWN restore output, not
// fuzzier string matching: prefix matching stays rejected in both directions ("System" must
// not match every System.* assembly, "Newtonsoft.Json.Schema" must not fall back to
// "Newtonsoft.Json").
module internal NugetPackageMap =

    /// packageId (lower-cased — NuGet ids are case-insensitive) → assembly SimpleNames in
    /// their shipped casing. An EMPTY set is meaningful and distinct from an absent key: the
    /// package is in the restore graph but ships no compile/runtime assembly (analyzer,
    /// build-only, content-only). Keeping those ids lets the miss payload say "restored, but
    /// contributes no reference" instead of the false "not in this project's restore graph".
    type PackageAssemblies = Map<string, Set<string>>

    let private fileNameOf (path: string) =
        let index = path.LastIndexOfAny [| '/'; '\\' |]
        if index >= 0 then path.Substring(index + 1) else path

    /// The assembly SimpleName a package payload path contributes, if any. `_._` is NuGet's
    /// "this package intentionally contributes nothing for this TFM" placeholder, and satellite
    /// resources / xml docs / native payloads are not assembly references either — only `.dll`
    /// entries name an assembly.
    let private simpleNameOfPath (path: string) =
        let fileName = fileNameOf path

        if
            fileName.Length > 4
            && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        then
            Some(fileName.Substring(0, fileName.Length - 4))
        else
            None

    /// Record that the package exists in the restore graph, without claiming it ships anything.
    let private addPackage (packageId: string) (map: PackageAssemblies) =
        if String.IsNullOrWhiteSpace packageId then
            map
        else
            let key = packageId.ToLowerInvariant()

            if Map.containsKey key map then
                map
            else
                Map.add key Set.empty map

    let private addAssembly (packageId: string) (assemblyName: string) (map: PackageAssemblies) =
        if String.IsNullOrWhiteSpace packageId || String.IsNullOrWhiteSpace assemblyName then
            map
        else
            let key = packageId.ToLowerInvariant()
            let existing = map |> Map.tryFind key |> Option.defaultValue Set.empty
            Map.add key (Set.add assemblyName existing) map

    /// Parse a `project.assets.json` PAYLOAD (the file's text, not its path) into
    /// packageId → assembly SimpleNames. Pure — no I/O, no network — so it is unit-testable
    /// against a synthetic assets file.
    ///
    /// Shape, verified against real restores (assets schema versions 3 and 4):
    ///   targets: { "<tfm>" or "<tfm>/<rid>": { "<Id>/<Version>": { compile: { path: {} },
    ///                                                              runtime: { path: {} } } } }
    /// Every target is folded in, so a RID-qualified graph contributes the same package ids.
    /// `compile` and `runtime` can list different assemblies (`ref/` vs `lib/`); both count,
    /// because FCS references whichever one the SDK put on the compile line.
    /// `type: "project"` entries (ProjectReference, payload `bin/placeholder/<AssemblyName>.dll`)
    /// are kept deliberately: they give the same project-name → assembly-name mapping for a
    /// project whose <AssemblyName> was renamed, and their key is `<Name>/<Version>` too.
    /// Every well-formed library entry contributes its id, even when it names no `.dll` at all —
    /// see the `PackageAssemblies` doc for why an empty set is not the same as an absent key.
    let packageAssembliesFromAssets (assetsJson: string) : PackageAssemblies =
        try
            match JsonNode.Parse assetsJson with
            | null -> Map.empty
            | root ->
                match root["targets"] with
                | :? JsonObject as targets ->
                    let mutable map = Map.empty

                    for target in targets do
                        match target.Value with
                        | :? JsonObject as libraries ->
                            for library in libraries do
                                // "<Id>/<Version>" — only the id half matters here.
                                let packageId = library.Key.Split('/')[0]

                                match library.Value with
                                | :? JsonObject as sections ->
                                    // Register the id first: a package that ships no assembly at
                                    // all (analyzer, build-only, content-only) is still restored,
                                    // and the miss payload must be able to say so rather than
                                    // claim it is absent from the graph (#191 review I2).
                                    map <- addPackage packageId map

                                    for sectionName in [ "compile"; "runtime" ] do
                                        match sections[sectionName] with
                                        | :? JsonObject as files ->
                                            for file in files do
                                                match simpleNameOfPath file.Key with
                                                | Some assemblyName -> map <- addAssembly packageId assemblyName map
                                                | None -> ()
                                        | _ -> ()
                                | _ -> ()
                        | _ -> ()

                    map
                | _ -> Map.empty
        with _ ->
            Map.empty

    /// Roots the NuGet global-packages cache can live under, most specific first.
    let private globalPackagesRoots () =
        [ Environment.GetEnvironmentVariable "NUGET_PACKAGES"
          Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages") ]
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> List.map (fun root -> root.TrimEnd([| '/'; '\\' |]))

    let private splitSegments (path: string) =
        path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

    /// Fallback for projects whose `project.assets.json` is unavailable (relocated
    /// MSBuildProjectExtensionsPath, packages.config, unreadable file): derive the same map
    /// from the `-r:` reference paths, which for NuGet-resolved assemblies live under the
    /// global packages cache as `<root>/<idLower>/<version>/<...>/<Assembly>.dll`.
    let packageAssembliesFromReferencePaths (otherOptions: string seq) : PackageAssemblies =
        let roots = globalPackagesRoots ()

        // A NuGet version directory always starts with a digit ("9.0.0", "1.0.0-rc.2").
        // Requiring that stops an unrelated "…/packages/…" directory from inventing ids.
        let looksLikeVersion (segment: string) =
            segment.Length > 0 && Char.IsDigit segment[0]

        let packageIdOf (path: string) =
            let segments = splitSegments path

            let idIndex =
                roots
                |> List.tryPick (fun root ->
                    if
                        path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                    then
                        Some (splitSegments root).Length
                    else
                        None)
                |> Option.orElseWith (fun () ->
                    // Not under a known root (RestorePackagesPath, CI cache mount, …) — fall
                    // back to the layout itself, taking the LAST "packages" segment so a root
                    // like /home/packages/agent/packages/<id>/… resolves to the inner one.
                    segments
                    |> Array.tryFindIndexBack (fun segment ->
                        String.Equals(segment, "packages", StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun index -> index + 1))

            match idIndex with
            | Some index when index + 1 < segments.Length && looksLikeVersion segments[index + 1] ->
                Some segments[index]
            | _ -> None

        otherOptions
        |> Seq.choose (fun option ->
            if isNull option then
                None
            elif option.StartsWith("-r:", StringComparison.Ordinal) then
                Some(option.Substring 3)
            elif option.StartsWith("--reference:", StringComparison.Ordinal) then
                Some(option.Substring 12)
            else
                None)
        |> Seq.fold
            (fun map path ->
                match packageIdOf path, simpleNameOfPath path with
                | Some packageId, Some assemblyName -> addAssembly packageId assemblyName map
                | _ -> map)
            Map.empty

    /// Default assets location. MSBuild can relocate it via MSBuildProjectExtensionsPath, but
    /// that property is not carried on FSharpProjectOptions — a relocated (or missing) assets
    /// file falls through to the `-r:` derivation, which needs no file at all.
    let assetsFilePath (projectFileName: string) =
        try
            let directory = Path.GetDirectoryName(Path.GetFullPath projectFileName)

            if String.IsNullOrEmpty directory then
                ""
            else
                Path.Combine(directory, "obj", "project.assets.json")
        with _ ->
            ""

    /// #191 review: `project.assets.json` describes EVERY target the project restores, but
    /// `EnsureProjectResults` evaluates exactly ONE. A package referenced only under another
    /// TFM would otherwise keep claiming the assemblies it ships there — and when one of those
    /// SimpleNames reaches the evaluated compile line through a DIFFERENT package,
    /// `fcs_nuget_types`/`fcs_nuget_members` would answer for the wrong package entirely.
    ///
    /// So an assets entry keeps an assembly only while the evaluated compile line does not
    /// attribute that assembly to some OTHER package. An assembly the compile line cannot
    /// attribute at all (shared-framework ref pack, ProjectReference output, a restore layout
    /// `packageIdOf` cannot parse) is kept: framework unification legitimately serves a
    /// package's assembly from a non-package path, and dropping it would lose a real answer.
    ///
    /// Package IDS survive either way — an id mapped to an empty set is exactly the
    /// "restored, but contributes no compile reference here" signal the miss payload reads.
    let internal restrictToCompileLine (fromPaths: PackageAssemblies) (fromAssets: PackageAssemblies) : PackageAssemblies =
        let owners =
            fromPaths
            |> Map.toSeq
            |> Seq.collect (fun (packageId, names) ->
                names |> Seq.map (fun name -> name.ToLowerInvariant(), packageId))
            |> Seq.fold (fun map (name, packageId) -> Map.add name packageId map) Map.empty

        if Map.isEmpty owners then
            fromAssets
        else
            fromAssets
            |> Map.map (fun packageId names ->
                names
                |> Set.filter (fun name ->
                    match Map.tryFind (name.ToLowerInvariant()) owners with
                    | Some owner -> String.Equals(owner, packageId, StringComparison.Ordinal)
                    | None -> true))

    let forProject (projectFileName: string) (otherOptions: string seq) : PackageAssemblies =
        let fromAssets =
            try
                let path = assetsFilePath projectFileName

                if path <> "" && File.Exists path then
                    packageAssembliesFromAssets (File.ReadAllText path)
                else
                    Map.empty
            with _ ->
                Map.empty

        let fromPaths = packageAssembliesFromReferencePaths otherOptions

        if Map.isEmpty fromAssets then
            fromPaths
        else
            restrictToCompileLine fromPaths fromAssets

    /// An assembly matches `packageId` when its SimpleName IS the packageId (pre-#191
    /// behaviour, kept — callers who already know the assembly name keep working even when the
    /// restore graph is unreadable) OR when the restore graph says that package ships that
    /// assembly. Both arms are exact, case-insensitive comparisons: prefix matching stays
    /// rejected in both directions.
    let matches (map: PackageAssemblies) (packageId: string) (assemblySimpleName: string) =
        if
            String.IsNullOrWhiteSpace assemblySimpleName
            || String.IsNullOrWhiteSpace packageId
        then
            false
        elif String.Equals(assemblySimpleName, packageId, StringComparison.OrdinalIgnoreCase) then
            true
        else
            match Map.tryFind (packageId.ToLowerInvariant()) map with
            | Some names ->
                names
                |> Set.exists (fun name -> String.Equals(name, assemblySimpleName, StringComparison.OrdinalIgnoreCase))
            | None -> false

    /// Packages from the restore graph whose id or shipped assembly names relate to `query` by
    /// case-insensitive containment in EITHER direction (so both "I passed the assembly name"
    /// and "I passed the package id" miss modes self-correct), best first, capped at `limit`.
    let candidates (map: PackageAssemblies) (query: string) (limit: int) : (string * string list) list =
        let normalized =
            query
            |> Option.ofObj
            |> Option.defaultValue ""
            |> (fun s -> s.Trim().ToLowerInvariant())

        if normalized = "" || limit <= 0 then
            []
        else
            let unrelated = Int32.MaxValue

            map
            |> Map.toList
            |> List.choose (fun (packageId, assemblyNames) ->
                let idScore =
                    if packageId = normalized then 0
                    elif packageId.Contains normalized then 1
                    elif normalized.Contains packageId then 3
                    else unrelated

                let assemblyScores =
                    assemblyNames
                    |> Set.toList
                    |> List.map (fun name ->
                        let lowered = name.ToLowerInvariant()

                        if lowered = normalized then 0
                        elif lowered.Contains normalized then 2
                        elif normalized.Contains lowered then 4
                        else unrelated)

                let score = List.min (idScore :: assemblyScores)

                if score = unrelated then
                    None
                else
                    Some(score, packageId, assemblyNames |> Set.toList |> List.sort))
            // Shortest id first inside a score bucket: "Orleans.Core" beats
            // "Orleans.Core.Abstractions.Extras" as the likelier intent.
            |> List.sortBy (fun (score, packageId, _) -> score, packageId.Length, packageId)
            |> List.truncate limit
            |> List.map (fun (_, packageId, assemblyNames) -> packageId, assemblyNames)

    let private candidatesToJson (candidates: (string * string list) list) : JsonNode =
        candidates
        |> List.map (fun (packageId, assemblyNames) ->
            jobj
                [ "packageId", jstr packageId
                  "assemblies", JsonArray(assemblyNames |> List.map jstr |> List.toArray) :> JsonNode ]
            :> JsonNode)
        |> List.toArray
        |> JsonArray
        :> JsonNode

    /// Additive miss payload — emitted ONLY when zero assemblies matched, so the happy-path
    /// response shape is unchanged. Turns the #100 field failure ("empty result, no idea why")
    /// into a one-turn self-correction.
    let missFields (map: PackageAssemblies) (packageId: string) : (string * JsonNode) list =
        let closest = candidates map packageId 5
        let restored = Map.containsKey (packageId.ToLowerInvariant()) map

        let hint =
            if restored then
                // Reachable for BOTH shapes since #191 review I2: a package that ships assemblies
                // none of which are on the compile line (runtime-only, ExcludeAssets=compile), and
                // one that ships none at all (analyzer, build-only) — the latter arrives here with
                // an empty assembly set, and `candidatePackages` shows it as `assemblies: []`.
                $"'%s{packageId}' is in this project's restore graph, but none of the assemblies it ships are on the compile line of the target framework that was evaluated — analyzer/build-only/runtime-only packages contribute no compile-time reference, and on a multi-targeted project the package may be referenced only under a DIFFERENT target. candidatePackages shows which assemblies it ships here, if any; fcs_referenced_symbols searches the assemblies that ARE loaded."
            elif not (List.isEmpty closest) then
                $"No referenced assembly matches packageId '%s{packageId}'. packageId accepts EITHER the NuGet package id OR the assembly SimpleName it ships, and the two differ for many packages (Microsoft.Orleans.Core.Abstractions ships Orleans.Core.Abstractions.dll). See candidatePackages for the closest entries in this project's restore graph."
            else
                $"No referenced assembly matches packageId '%s{packageId}', and nothing similar is in this project's restore graph. Note that a NuGet package id and the assembly SimpleName it ships are often different names (Microsoft.Orleans.Core.Abstractions ships Orleans.Core.Abstractions.dll); packageId accepts either. Confirm the package is referenced by this project, or use fcs_referenced_symbols to search all loaded assemblies by type name."

        [ "hint", jstr hint; "candidatePackages", candidatesToJson closest ]

type internal FcsBridge
    (
        ?projectEvaluationBeforeLoadOverride: (string -> Task),
        ?analysisSnapshotKeyBeforeComputeOverride: (unit -> unit),
        ?checkTargetDiscoveryBeforeComputeOverride: (unit -> unit),
        ?checkProjectDiscoveryBeforeFallbackOverride: (unit -> unit),
        ?checkTargetDiscoveryDeadlineTokenOverride: (unit -> CancellationToken),
        ?freshProjectCheckWorkerOverride: (FSharpProjectOptions -> Task<FSharpDiagnostic array>),
        ?freshProjectCheckConcurrencyOverride: int,
        ?freshProjectCheckBeforeAdmissionOverride: (unit -> Task),
        ?freshProjectCheckBeforeFcsStartOverride: (unit -> Task),
        ?projectSweepWorkerOverride: (string -> Task<FSharpSymbolUse array * FSharpDiagnostic array>),
        ?testsForSymbolSiteScanBeforeUseOverride: (unit -> unit),
        ?testsForSymbolFailureDeadlineExpiredOverride: (unit -> bool),
        ?testsForSymbolProjectSweepWaitMsOverride: (int -> int),
        ?projectOutlineFileOutlineOverride: (FcsFileOutlineArgs -> Task<JsonNode>),
        ?checkFastSnapshotDeadlineExpiredOverride: (unit -> bool),
        ?findPositionResolutionBeforeComputeOverride: (unit -> Task),
        ?findTargetDiscoveryBeforeComputeOverride: (string -> Task),
        ?findTargetDiscoveryBeforeStepOverride: (string -> int -> unit),
        ?findDeadlineSignalOverride: (unit -> Task),
        ?findResponseDeadlineSignalOverride: (unit -> Task),
        ?findResponseConstructionBeforeStartOverride: (unit -> Task),
        ?findResponseConstructionBeforeStepOverride: (string -> int -> unit),
        ?findFinalResponseBeforeMeasureOverride: (unit -> unit),
        // #207: test-only seam for find's PER-SITE siteType deadline. The production check
        // reads the shared FindRequestDeadline, which cannot target one site deterministically
        // without a seam: timeoutMs=0 is exhausted BEFORE the sweep and yields no sites.
        // This override makes the reachable
        // degraded arm (a large solution whose budget expires between a project's sweep
        // returning and its site loop finishing) deterministically testable.
        ?findSiteTypeDeadlineExpiredOverride: (unit -> bool),
        ?referenceResolutionProbeOverride: (string array -> int * int),
        ?projectOptionsCacheValidationBeforeComputeOverride: (string -> unit),
        ?trustedFileCheckAnswerOverride: (FSharpCheckFileAnswer -> FSharpCheckFileAnswer),
        // Deterministic test seam for the otherwise fixed production find ceiling.
        ?findResponseBudgetCharsOverride: int
    ) =
    let findResponseBudgetChars =
        match findResponseBudgetCharsOverride with
        | Some value when value > 0 -> value
        | Some value -> invalidArg (nameof findResponseBudgetCharsOverride) $"find response budget must be positive; got {value}."
        | None -> FindResponseBudget.MaxSerializedChars

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
    let optionsCache = BoundedCache<string, ProjectOptionsCacheEntry>(50)
    let projectResultsCache = BoundedCache<string, FSharpCheckProjectResults>(3)

    // FCS also owns an incremental project cache beneath our bounded caches. A new
    // content snapshot must invalidate that layer before a fresh ParseAndCheckProject;
    // otherwise a same-mtime edit can miss our cache correctly and still receive FCS's
    // old result. Remember only the most recently observed key per logical project.
    let analysisSnapshotGate = obj ()
    let mutable analysisSnapshotComputeCount = 0L
    let mutable analysisSnapshotCommitCount = 0L

    let lastAnalysisSnapshotByProject =
        System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)

    let optionsInFlight =
        ConcurrentDictionary<string, WaiterAwareSingleFlight<ProjectOptionsCacheEntry>>()

    // Project-cache validation performs recursive hashing and Ionide/MSBuild loads
    // race on process-global SDK state. Keep the entire exact-fsproj resolve behind
    // one admitted worker, with no distinct-key queue; exact-key callers share it.
    let projectEvaluationAdmission = ProjectEvaluationAdmission(1)

    let mutable projectOptionsLoadCount = 0L
    let mutable projectOptionsStaleReloadCount = 0L
    let mutable projectOptionsCacheValidationCount = 0L
    let mutable projectTypeCheckStartCount = 0L
    let mutable freshProjectCheckInvalidationCount = 0L
    let mutable checkProjectDiscoveryFallbackCount = 0L

    let freshProjectCheckCapacity =
        match freshProjectCheckConcurrencyOverride with
        | Some capacity -> max 1 capacity
        | None ->
            match Int32.TryParse(Environment.GetEnvironmentVariable("FSLANGMCP_MAX_CONCURRENT_FCS")) with
            | true, capacity when capacity > 0 -> capacity
            | _ -> 2

    // Check has a hard caller deadline, but FCS project checking, source-closure
    // hashing, and directory discovery are not reliably cancellable. Each category
    // therefore owns an actual-worker admission slot plus exact-key single-flight.
    // Caller WaitAsync timeouts never release these slots; only real completion does.
    let freshProjectCheckAdmission =
        BoundedCheckWorkAdmission("project type-check", freshProjectCheckCapacity)

    let freshProjectChecksInFlight =
        ConcurrentDictionary<string, Lazy<Task<FSharpDiagnostic array * string * string>>>()

    let snapshotComputationAdmission =
        BoundedCheckWorkAdmission("project snapshot computation", 1)

    let snapshotComputationsInFlight =
        ConcurrentDictionary<string, WaiterAwareSingleFlight<string>>()

    let checkTargetDiscoveryAdmission =
        BoundedCheckWorkAdmission("check target discovery", 1)

    let checkTargetDiscoveriesInFlight =
        ConcurrentDictionary<string, WaiterAwareSingleFlight<CheckTargetDiscoveryResult>>()

    // Find has the same uncancellable boundaries as Check, plus position resolution.
    // Exact-key callers share the active worker; distinct keys are rejected immediately.
    // Caller/deadline expiry never releases these slots before the real worker settles.
    let findPositionResolutionAdmission =
        BoundedCheckWorkAdmission("find position resolution", freshProjectCheckCapacity)

    let findPositionResolutionsInFlight =
        ConcurrentDictionary<string, WaiterAwareSingleFlight<Result<string, JsonNode>>>()

    let findTargetDiscoveryAdmission = BoundedCheckWorkAdmission("find target discovery", 1)

    let findTargetDiscoveriesInFlight =
        ConcurrentDictionary<string, WaiterAwareSingleFlight<FindTargetDiscoveryResult>>()

    let mutable findPositionResolutionComputeCount = 0L
    let mutable findTargetDiscoveryComputeCount = 0L

    let referenceResolutionProbeAdmission =
        BoundedCheckWorkAdmission("reference resolution probe", 1)

    let referenceResolutionProbesInFlight =
        ConcurrentDictionary<string, Lazy<Task<Result<int * int, CheckBlockingFailure>>>>()

    let mutable freshProjectCheckGeneration = 0L

    // find sweep (issue #131): memoize each project's whole-symbol-use enumeration +
    // diagnostics. FCS's project cache keeps ParseAndCheckProject warm, but
    // GetAllUsesOfAllSymbols() is NOT memoized — it re-walks every recorded symbol use
    // (~16-18k/project) on EVERY call, so a warm `find` re-paid ~3s/project. The cache
    // key is the same content-addressed AnalysisSnapshotKey used by projectResultsCache:
    // evaluated options, ordered normalized source paths + bytes, referenced assemblies,
    // and transitive referenced-project sources. It detects same-mtime/same-length edits
    // and compile-order/path changes that the former hand-built mtime vectors conflated.
    // Every MISS invalidates FCS's project configuration before the original
    // ParseAndCheckProject + GetAllUsesOfAllSymbols path, so FCS cannot serve a stale
    // same-mtime incremental result underneath a correctly missed application cache.
    // Cleared by ClearAnalysisCaches() (so set_project invalidates it). Sized to the same
    // solution scale as optionsCache so a whole solution stays warm between sweeps.
    let projectUsesCache =
        BoundedCache<string, FSharpSymbolUse array * FSharpDiagnostic array>(50)

    // A timed-out GetAllUsesOfAllSymbols call cannot be preempted by FCS once its
    // synchronous walk starts. Keep one shared computation per cache key so retries
    // observe the same task instead of accumulating abandoned worker threads.
    let projectUsesInFlight =
        ConcurrentDictionary<string, Lazy<Task<FSharpSymbolUse array * FSharpDiagnostic array>>>()

    // A caller deadline can expire while ParseAndCheckProject or the synchronous
    // GetAllUsesOfAllSymbols walk is still running. Keep a separate actual-worker
    // admission around that underlying work: the caller may return a typed timeout,
    // but a distinct snapshot cannot accumulate another uncancellable sweep once all
    // real worker slots are occupied. Exact-key callers still share projectUsesInFlight.
    let projectUsesAdmission =
        BoundedCheckWorkAdmission("project symbol-use sweep", freshProjectCheckCapacity)

    let mutable projectUsesCacheGeneration = 0L

    let asTask (workflow: Async<'T>) : Task<'T> =
        Async.StartAsTask(workflow, cancellationToken = CancellationToken.None)

    let observeFault (operation: Task) =
        operation.ContinueWith(
            (fun (faulted: Task) -> faulted.Exception |> ignore),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted ||| TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

    let removeExactSingleFlight
        (registry: ConcurrentDictionary<string, WaiterAwareSingleFlight<'T>>)
        (key: string)
        (flight: WaiterAwareSingleFlight<'T>)
        =
        let entries =
            registry
            :> System.Collections.Generic.ICollection<
                System.Collections.Generic.KeyValuePair<string, WaiterAwareSingleFlight<'T>>
             >

        entries.Remove(System.Collections.Generic.KeyValuePair(key, flight)) |> ignore

    let acquireSingleFlightWhileNeeded
        (registry: ConcurrentDictionary<string, WaiterAwareSingleFlight<'T>>)
        (key: string)
        (remainingBudget: (unit -> TimeSpan) option)
        (callerIsNeeded: unit -> bool)
        (start: (unit -> bool) -> Task<'T>)
        =
        let ensureCallerCanWait () =
            if not (callerIsNeeded ()) then
                raise (TimeoutException("The parent worker has no active callers."))

            match remainingBudget with
            | Some getRemaining when getRemaining () <= TimeSpan.Zero -> raise (TimeoutException())
            | Some _
            | None -> ()

        let callerIsActive () =
            callerIsNeeded ()
            && (match remainingBudget with
                | Some getRemaining ->
                    try
                        getRemaining () > TimeSpan.Zero
                    with :? OperationCanceledException ->
                        false
                | None -> true)

        let rec acquire () =
            ensureCallerCanWait ()

            let flight =
                registry.GetOrAdd(
                    key,
                    fun _ ->
                        WaiterAwareSingleFlight<'T>(
                            start,
                            removeExactSingleFlight registry key
                        )
                )

            match flight.TryAddWaiter(callerIsActive) with
            | Some waiterId ->
                new WaiterAwareSingleFlightWaiter<'T>(flight, waiterId)
            | None ->
                // A zero-waiter worker has already committed to stopping. Its exact
                // completion callback removes the retained entry; a late caller must
                // retry instead of inheriting that worker's terminal timeout.
                Thread.Yield() |> ignore
                acquire ()

        acquire ()

    let acquireSingleFlight registry key remainingBudget start =
        acquireSingleFlightWhileNeeded registry key remainingBudget (fun () -> true) start

    let normalizedCheckWorkPath (path: string) =
        let normalized =
            try
                normalizePath path
            with _ ->
                path

        if OperatingSystem.IsWindows() then
            normalized.ToUpperInvariant()
        else
            normalized

    let checkDiscoveryKey scope target path =
        let pathPart =
            path
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map normalizedCheckWorkPath
            |> Option.defaultValue ""

        let frame (value: string) = $"{value.Length}:{value}"

        // `|` is a legal POSIX filename character. Delimiter concatenation made
        // (target="/a", path="/b|/c") alias (target="/a|/b", path="/c"), causing
        // unrelated callers to share the same Lazy discovery task. Length framing
        // makes each component boundary unambiguous without restricting valid paths.
        String.Concat(frame scope, frame (normalizedCheckWorkPath target), frame pathPart)

    let runCheckTargetDiscovery
        (key: string)
        (remainingBudget: (unit -> TimeSpan) option)
        (work: (unit -> bool) -> CheckTargetDiscoveryResult)
        : Task<CheckTargetDiscoveryResult> =
        task {
            // Test-only deadline signal: production uses the caller's remaining budget.
            // Both paths are synchronously visible to the worker, even before the
            // caller's wait continuation has run and disposed its waiter.
            let deadlineToken =
                checkTargetDiscoveryDeadlineTokenOverride
                |> Option.map (fun getToken -> getToken ())
                |> Option.defaultValue CancellationToken.None

            let callerBudget () =
                if deadlineToken.IsCancellationRequested then
                    TimeSpan.Zero
                else
                    match remainingBudget with
                    | Some getRemaining -> getRemaining ()
                    | None -> TimeSpan.MaxValue

            use waiter =
                acquireSingleFlight
                    checkTargetDiscoveriesInFlight
                    key
                    (Some callerBudget)
                    (fun hasActiveWaiters ->
                        task {
                            try
                                return!
                                    checkTargetDiscoveryAdmission.TryRun(
                                        key,
                                        fun () ->
                                            Task.Run(fun () ->
                                                let ensureWorkerNeeded () =
                                                    if not (hasActiveWaiters ()) then
                                                        raise (TimeoutException("The check discovery worker has no active callers."))

                                                ensureWorkerNeeded ()
                                                checkTargetDiscoveryBeforeComputeOverride
                                                |> Option.iter (fun hook -> hook ())
                                                ensureWorkerNeeded ()
                                                work hasActiveWaiters)
                                    )
                            with :? BoundedCheckWorkBusyException as ex ->
                                return CheckTargetDiscoveryResult.Busy ex.Message
                        })

            let operation = waiter.Operation
            observeFault operation

            try
                match remainingBudget with
                | None -> return! operation.WaitAsync(deadlineToken)
                | Some _ ->
                    let remaining = callerBudget ()

                    if remaining <= TimeSpan.Zero then
                        return raise (TimeoutException("Check discovery budget was exhausted."))
                    else
                        return! operation.WaitAsync(remaining, deadlineToken)
            with :? OperationCanceledException when deadlineToken.IsCancellationRequested ->
                return raise (TimeoutException("Check discovery budget was exhausted."))
        }

    let findPositionResolutionKey (args: FindArgs) =
        let frame (value: string) = $"{value.Length}:{value}"

        let valueOrEmpty value =
            value
            |> Option.defaultValue ""

        let intOrEmpty value =
            value
            |> Option.map string
            |> Option.defaultValue ""

        String.Concat(
            frame (valueOrEmpty args.projectPath),
            frame (valueOrEmpty args.path),
            frame (intOrEmpty args.line),
            frame (valueOrEmpty args.word),
            frame (intOrEmpty args.occurrence),
            frame (intOrEmpty args.character)
        )

    let runFindPositionResolution
        (args: FindArgs)
        (remainingBudget: unit -> TimeSpan)
        (work: (unit -> bool) -> Task<Result<string, JsonNode>>)
        =
        let key = findPositionResolutionKey args

        acquireSingleFlight
            findPositionResolutionsInFlight
            key
            (Some remainingBudget)
            (fun hasActiveWaiters ->
                findPositionResolutionAdmission.TryRun(
                    key,
                    fun () ->
                        task {
                            let ensureWorkerNeeded () =
                                if not (hasActiveWaiters ()) then
                                    raise (
                                        TimeoutException(
                                            "The find position-resolution worker has no active callers."
                                        )
                                    )

                            do! Task.Yield()
                            ensureWorkerNeeded ()

                            match findPositionResolutionBeforeComputeOverride with
                            | Some beforeCompute -> do! beforeCompute ()
                            | None -> ()

                            ensureWorkerNeeded ()
                            Interlocked.Increment(&findPositionResolutionComputeCount)
                            |> ignore
                            return! work hasActiveWaiters
                        }
                ))

    let runFindTargetDiscovery
        (phase: string)
        (target: string)
        (remainingBudget: unit -> TimeSpan)
        (work: (unit -> bool) -> FindTargetDiscoveryResult)
        =
        let key = checkDiscoveryKey $"find-{phase}" target None

        acquireSingleFlight
            findTargetDiscoveriesInFlight
            key
            (Some remainingBudget)
            (fun hasActiveWaiters ->
                findTargetDiscoveryAdmission.TryRun(
                    key,
                    fun () ->
                        task {
                            let ensureWorkerNeeded () =
                                if not (hasActiveWaiters ()) then
                                    raise (
                                        TimeoutException(
                                            $"The find {phase} worker has no active callers."
                                        )
                                    )

                            do! Task.Yield()
                            ensureWorkerNeeded ()

                            match findTargetDiscoveryBeforeComputeOverride with
                            | Some beforeCompute -> do! beforeCompute phase
                            | None -> ()

                            ensureWorkerNeeded ()
                            Interlocked.Increment(&findTargetDiscoveryComputeCount)
                            |> ignore
                            return work hasActiveWaiters
                        }
                ))

    let resolveSingleCheckProject
        (target: string)
        (sourcePath: string option)
        (hasActiveWaiters: unit -> bool)
        =
        let ensureWorkerNeeded () =
            if not (hasActiveWaiters ()) then
                raise (TimeoutException("The check discovery worker has no active callers."))

        ensureWorkerNeeded ()

        if target.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
            Some target
        else
            let nearest =
                sourcePath
                |> Option.bind (fun path ->
                    ensureWorkerNeeded ()
                    findNearestFsproj path)
                |> Option.map normalizePath

            match nearest with
            | Some fsproj -> Some fsproj
            | None ->
                // Nearest lookup may outlive every caller. Only current live waiters
                // authorize the next scan; the leader's deadline does not own it.
                ensureWorkerNeeded ()
                checkProjectDiscoveryBeforeFallbackOverride |> Option.iter (fun hook -> hook ())
                ensureWorkerNeeded ()
                Interlocked.Increment(&checkProjectDiscoveryFallbackCount) |> ignore
                SolutionParsing.listProjects target |> Array.tryHead

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

    // Range WITHOUT the `file` field, for sites whose enclosing object already
    // carries `file` (find sites, symbol-uses, record-field audit, diagnostics) —
    // dropping the duplicate trims payload on hot symbols. Standalone ranges that
    // are the sole carrier of the path (declaration locations) keep rangeToJson. (#139)
    let rangeToJsonNoFile (r: range) : JsonNode =
        jobj
            [ "startLine", jint r.StartLine
              "startColumn", jint r.StartColumn
              "endLine", jint r.EndLine
              "endColumn", jint r.EndColumn ]
        :> JsonNode

    let positionToJson (p: Position) : JsonNode =
        jobj [ "line", jint p.Line; "column", jint p.Column ] :> JsonNode

    let typeName (typ: FSharpType) =
        typ.BasicQualifiedName
        |> Option.defaultWith (fun () -> typ.Format(FSharpDisplayContext.Empty))

    /// FSharpAccessibility's predicates overlap for imported IL. In particular,
    /// an assembly-internal member can also satisfy IsPrivate, so testing private
    /// first turns `internal` into a lie. Preserve the externally meaningful CLR
    /// combinations before falling back to the narrower predicates (#223).
    let fsharpAccessibilityString (accessibility: FSharpAccessibility) : string =
        try
            let isPublic = accessibility.IsPublic
            let isProtected = accessibility.IsProtected
            let isInternal = accessibility.IsInternal
            let isPrivate = accessibility.IsPrivate

            if isPublic then "public"
            elif isProtected && isInternal then "protected internal"
            elif isProtected && isPrivate then "private protected"
            elif isProtected then "protected"
            elif isInternal then "internal"
            elif isPrivate then "private"
            else "unknown"
        with _ ->
            "unknown"

    let shortenFSharpTypeNames (formatted: string) =
        fsharpTypeNameRegex.Replace(
            formatted,
            System.Text.RegularExpressions.MatchEvaluator(fun m -> m.Groups["name"].Value)
        )

    let rec publicApiTypeName (typ: FSharpType) =
        try
            let basicName = typ.BasicQualifiedName |> Option.defaultValue ""
            let arityMarker = basicName.LastIndexOf('`')
            let genericArguments = typ.GenericArguments |> Seq.toArray

            if arityMarker >= 0 && genericArguments.Length > 0 then
                let genericName = basicName.Substring(0, arityMarker).Replace('+', '.')

                match genericName, genericArguments with
                | ("Microsoft.FSharp.Core.FSharpOption" | "Microsoft.FSharp.Core.option"), [| argument |] ->
                    $"{publicApiTypeName argument} option"
                | ("Microsoft.FSharp.Collections.FSharpList" | "Microsoft.FSharp.Collections.list"), [| argument |] ->
                    $"{publicApiTypeName argument} list"
                | _ ->
                    let name = shortenFSharpTypeNames genericName
                    let arguments = genericArguments |> Array.map publicApiTypeName |> String.concat ", "
                    $"{name}<{arguments}>"
            else
                typ.Format(FSharpDisplayContext.Empty) |> shortenFSharpTypeNames
        with _ ->
            typeName typ

    let diagnosticToJson (d: FSharpDiagnostic) : JsonNode =
        jobj
            [ "file", jstr (normalizePath d.FileName)
              "message", jstr d.Message
              "severity", jstr (d.Severity.ToString())
              "errorNumber", jint d.ErrorNumber
              "errorNumberText", jstr d.ErrorNumberText
              "subcategory", jstr d.Subcategory
              "range", rangeToJsonNoFile d.Range
              "start", positionToJson d.Start
              "end", positionToJson d.End ]
        :> JsonNode

    let accessibilityString (symbol: FSharpSymbol) : string =
        try
            fsharpAccessibilityString symbol.Accessibility
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
              "range", rangeToJsonNoFile symbolUse.Range
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

    let assemblySimpleName (asm: FSharpAssembly) =
        try
            asm.SimpleName |> Option.ofObj |> Option.defaultValue ""
        with _ ->
            ""

    /// Exact matching only (case-insensitive), on the assembly SimpleName OR on the
    /// packageId→assembly map built from the project's restore graph (#191). We still reject
    /// prefix matching in both directions: "System" must NOT match every System.* assembly,
    /// and "Newtonsoft.Json.Schema" must NOT silently fall back to "Newtonsoft.Json".
    let assemblyMatchesPackageId
        (packageAssemblies: NugetPackageMap.PackageAssemblies)
        (asm: FSharpAssembly)
        (packageId: string)
        =
        NugetPackageMap.matches packageAssemblies packageId (assemblySimpleName asm)

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
            fsharpAccessibilityString entity.Accessibility
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
            fsharpAccessibilityString m.Accessibility
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

    let genericParameterName (parameter: FSharpGenericParameter) =
        try
            parameter.Name.TrimStart('\'', '^')
        with _ ->
            "T"

    let genericParameterConstraintsWith
        (formatType: FSharpType -> string)
        (parameter: FSharpGenericParameter)
        : string array =
        try
            let constraints = parameter.Constraints |> Seq.toArray

            let has predicate =
                constraints
                |> Array.exists (fun genericConstraint ->
                    try predicate genericConstraint with _ -> false)

            let isUnmanaged = has (fun genericConstraint -> genericConstraint.IsUnmanagedConstraint)
            let isStruct = has (fun genericConstraint -> genericConstraint.IsNonNullableValueTypeConstraint)
            let isClass = has (fun genericConstraint -> genericConstraint.IsReferenceTypeConstraint)

            [| if isUnmanaged then
                   yield "unmanaged"
               elif isStruct then
                   yield "struct"
               elif isClass then
                   yield "class"

               if not isUnmanaged && not isStruct && not isClass then
                   if has (fun genericConstraint -> genericConstraint.IsNotSupportsNullConstraint) then
                       yield "notnull"
                   elif has (fun genericConstraint -> genericConstraint.IsSupportsNullConstraint) then
                       yield "null"

               for genericConstraint in constraints do
                   try
                       if genericConstraint.IsCoercesToConstraint then
                           yield formatType genericConstraint.CoercesToTarget
                       elif genericConstraint.IsEnumConstraint then
                           yield $"enum<{formatType genericConstraint.EnumConstraintTarget}>"
                       elif genericConstraint.IsDelegateConstraint then
                           let delegateData = genericConstraint.DelegateConstraintData

                           yield
                               $"delegate<{formatType delegateData.DelegateTupledArgumentType}, {formatType delegateData.DelegateReturnType}>"
                       elif genericConstraint.IsSimpleChoiceConstraint then
                           let choices =
                               genericConstraint.SimpleChoices
                               |> Seq.map formatType
                               |> String.concat " | "

                           if not (String.IsNullOrWhiteSpace choices) then
                               yield $"choice<{choices}>"
                   with _ ->
                       ()

               if has (fun genericConstraint -> genericConstraint.IsEqualityConstraint) then
                   yield "equality"

               if has (fun genericConstraint -> genericConstraint.IsComparisonConstraint) then
                   yield "comparison"

               if has (fun genericConstraint -> genericConstraint.IsRequiresDefaultConstructorConstraint) then
                   yield "new()"

               if has (fun genericConstraint -> genericConstraint.IsAllowsRefStructConstraint) then
                   yield "allows ref struct" |]
            |> Array.distinct
        with _ ->
            [||]

    let memberGenericParametersWith
        (formatType: FSharpType -> string)
        (m: FSharpMemberOrFunctionOrValue)
        =
        try
            m.GenericParameters
            |> Seq.map (fun parameter ->
                let constraints = genericParameterConstraintsWith formatType parameter

                jobj
                    [ "name", jstr (genericParameterName parameter)
                      "constraints", JsonArray(constraints |> Array.map jstr) :> JsonNode ]
                :> JsonNode)
            |> Seq.toArray
        with _ ->
            [||]

    let memberSignatureWith (formatType: FSharpType -> string) (m: FSharpMemberOrFunctionOrValue) : string =
        try
            let paramGroups = m.CurriedParameterGroups

            let paramStr =
                paramGroups
                |> Seq.collect id
                |> Seq.map (fun p ->
                    let pName = p.Name |> Option.defaultValue "_"
                    let pType = try formatType p.Type with _ -> "?"
                    $"{pName}: {pType}")
                |> String.concat ", "

            let returnType =
                try
                    formatType m.ReturnParameter.Type
                with _ ->
                    "?"

            let constraintsSuffix =
                try
                    m.GenericParameters
                    |> Seq.choose (fun parameter ->
                        let constraints = genericParameterConstraintsWith formatType parameter

                        if constraints.Length = 0 then
                            None
                        else
                            let renderedConstraints = String.concat ", " constraints
                            Some $"where {genericParameterName parameter} : {renderedConstraints}")
                    |> String.concat " "
                    |> function
                        | "" -> ""
                        | constraints -> $" {constraints}"
                with _ ->
                    ""

            $"{m.DisplayName}({paramStr}) -> {returnType}{constraintsSuffix}"
        with _ ->
            try
                m.DisplayName
            with _ ->
                "<unknown>"

    let memberSignature (m: FSharpMemberOrFunctionOrValue) : string =
        memberSignatureWith typeName m

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

    let referencedMemberToJson
        (metadata: MemberMetadata option)
        (m: FSharpMemberOrFunctionOrValue)
        : JsonNode =
        let xmlDocNode : JsonNode =
            try tryExtractXmlSummary m.XmlDoc with _ -> null

        let genericParameters = memberGenericParametersWith typeName m

        let isAbstract =
            metadata
            |> Option.map _.IsAbstract
            |> Option.defaultWith (fun () -> try m.IsDispatchSlot with _ -> false)

        jobj
            [ "name", jstr (try m.DisplayName with _ -> "<unknown>")
              "kind", jstr (memberKindString m)
              "signature", jstr (memberSignature m)
              "accessibility",
              jstr (metadata |> Option.map _.Accessibility |> Option.defaultWith (fun () -> memberAccessibilityString m))
              "isAbstract", jbool isAbstract
              "genericParameters", JsonArray(genericParameters) :> JsonNode
              "isObsolete", jbool (isObsoleteMember m)
              "xmlDocSummary", xmlDocNode ]
        :> JsonNode

    let fieldAccessibilityString (f: FSharpField) : string =
        try
            fsharpAccessibilityString f.Accessibility
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
                fsharpAccessibilityString uc.Accessibility
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

    let attributesOfSymbol (symbol: FSharpSymbol) : FSharpAttribute array =
        try
            match symbol with
            | :? FSharpEntity as entity -> entity.Attributes |> Seq.toArray
            | :? FSharpMemberOrFunctionOrValue as memberOrValue ->
                memberOrValue.Attributes |> Seq.toArray
            | :? FSharpField as field -> field.Attributes |> Seq.toArray
            | :? FSharpUnionCase as unionCase -> unionCase.Attributes |> Seq.toArray
            | _ -> [||]
        with _ ->
            [||]

    let attributeFullName (attribute: FSharpAttribute) =
        try
            let fullName = attribute.AttributeType.FullName

            if String.IsNullOrWhiteSpace fullName then
                attribute.AttributeType.DisplayName
            else
                fullName
        with _ ->
            "<unresolved-attribute>"

    let attributesToJson (attributes: FSharpAttribute array) : JsonNode =
        attributes
        |> Array.map (attributeFullName >> jstr)
        |> JsonArray
        :> JsonNode

    let isCustomOperationAttribute (attribute: FSharpAttribute) =
        let fullName = attributeFullName attribute

        String.Equals(fullName, "Microsoft.FSharp.Core.CustomOperationAttribute", StringComparison.Ordinal)
        || String.Equals(fullName, "CustomOperationAttribute", StringComparison.Ordinal)

    let customOperationName (attribute: FSharpAttribute) =
        try
            attribute.ConstructorArguments
            |> Seq.tryPick (fun (_, value) ->
                match value with
                | :? string as operationName when not (String.IsNullOrWhiteSpace operationName) ->
                    Some operationName
                | _ -> None)
        with _ ->
            None

    let sourceLines (source: string) =
        source.Split('\n') |> Array.map (fun line -> line.TrimEnd('\r'))

    let lineContextToJson
        (linesByFile: System.Collections.Generic.Dictionary<string, string array>)
        contextLines
        filePath
        startLine
        =
        let contextLines = max 0 contextLines
        let cacheKey = normalizePath filePath

        let lines =
            match linesByFile.TryGetValue(cacheKey) with
            | true, cached -> cached
            | _ ->
                let loaded = if File.Exists cacheKey then File.ReadAllLines(cacheKey) else [||]
                linesByFile[cacheKey] <- loaded
                loaded

        if lines.Length = 0 then
            jobj [ "lineText", jstr ""; "before", JsonArray() :> JsonNode; "after", JsonArray() :> JsonNode ]
        else
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

    /// `find`-specific source context. Every source line is a bounded, contiguous
    /// UTF-16 slice; the semantic range remains full-source coordinates and these
    /// offsets tell consumers how to map the visible snippet back to that source.
    /// Surrounding line count is capped independently from the response budget so
    /// `contextLines=Int32.MaxValue` cannot allocate an unbounded intermediate array.
    let findLineContextToJson
        (linesByNumber: System.Collections.Generic.IReadOnlyDictionary<int, string>)
        requestedContextLines
        startLine
        startColumn
        endLine
        endColumn
        =
        let requestedContextLines = max 0 requestedContextLines

        let appliedContextLines =
            min requestedContextLines FindResponseBudget.MaxContextLines

        let snippetNode lineNumber focusStart focusEnd text =
            let snippet = FindResponseBudget.boundedSnippet focusStart focusEnd text

            jobj
                [ "line", jint lineNumber
                  "text", jstr snippet.Text
                  "sourceStartColumn", jint snippet.SourceStartColumn
                  "sourceEndColumn", jint snippet.SourceEndColumn
                  "sourceLength", jint snippet.SourceLength
                  "truncated", jbool snippet.Truncated ]
            :> JsonNode

        let targetLine = max 1 startLine

        let matchedLine =
            match linesByNumber.TryGetValue(targetLine) with
            | true, text -> text
            | _ -> ""

        let matchedFocusEnd =
            if startLine = endLine then endColumn else startColumn

        let matchedSnippet =
            FindResponseBudget.boundedSnippet startColumn matchedFocusEnd matchedLine

        let contextRange first last =
            [| first..last |]
            |> Array.choose (fun line ->
                match linesByNumber.TryGetValue(line) with
                | true, text -> Some(snippetNode line startColumn startColumn text)
                | _ -> None)

        let before = contextRange (max 1 (targetLine - appliedContextLines)) (targetLine - 1)
        let after = contextRange (targetLine + 1) (targetLine + appliedContextLines)

        jobj
            [ "lineText", jstr matchedSnippet.Text
              "lineTextSourceStartColumn", jint matchedSnippet.SourceStartColumn
              "lineTextSourceEndColumn", jint matchedSnippet.SourceEndColumn
              "lineTextSourceLength", jint matchedSnippet.SourceLength
              "lineTextTruncated", jbool matchedSnippet.Truncated
              "contextLinesRequested", jint requestedContextLines
              "contextLinesApplied", jint appliedContextLines
              "contextLinesTruncated", jbool (requestedContextLines > appliedContextLines)
              "before", JsonArray(before) :> JsonNode
              "after", JsonArray(after) :> JsonNode ]

    let isDoubleBacktickIdentifier (value: string) =
        not (isNull value)
        && value.Length > 4
        && value.StartsWith("``", StringComparison.Ordinal)
        && value.EndsWith("``", StringComparison.Ordinal)

    /// Return the length of the query suffix that spells this FCS source name.
    /// FCS preserves double backticks in DisplayName, while callers naturally ask
    /// for both the plain name and its double-backtick-delimited spelling. Compare
    /// only the semantic identifier suffix; never scan source text or strip interior quotes.
    let trySourceIdentifierSuffixLength comparison (sourceName: string) (query: string) =
        if isNull sourceName || isNull query then
            None
        elif query.EndsWith(sourceName, comparison) then
            Some sourceName.Length
        elif isDoubleBacktickIdentifier sourceName then
            let contentLength = sourceName.Length - 4

            if
                query.Length >= contentLength
                && String.Compare(
                    query,
                    query.Length - contentLength,
                    sourceName,
                    2,
                    contentLength,
                    comparison
                ) = 0
            then
                Some contentLength
            else
                None
        else
            let quotedLength = sourceName.Length + 4
            let quotedStart = query.Length - quotedLength

            if
                quotedStart >= 0
                && query[quotedStart] = '`'
                && query[quotedStart + 1] = '`'
                && query.EndsWith("``", StringComparison.Ordinal)
                && String.Compare(query, quotedStart + 2, sourceName, 0, sourceName.Length, comparison) = 0
            then
                Some quotedLength
            else
                None

    let symbolMatches query exact (symbol: FSharpSymbol) =
        let displayName = symbol.DisplayName
        let fullName = symbol.FullName

        // A module-qualified query ("Roles.appRole" for App.Roles.appRole) is a dot-boundary
        // SUFFIX of the full name, not equal to it and not equal to the unqualified DisplayName,
        // so plain equality silently misses it (#100). Accept a dotted suffix on a '.' boundary —
        // gated on the query actually containing a '.' so bare-name matching stays byte-identical.
        let dottedSuffixMatch () =
            not (isNull (query: string))
            && query.Contains('.')
            && not (isNull fullName)
            && fullName.EndsWith(query, StringComparison.Ordinal)
            && fullName.Length > query.Length
            && fullName[fullName.Length - query.Length - 1] = '.'

        // #267: GetAllUsesOfAllSymbols returns backtick-bound values and test methods
        // with the delimiters in DisplayName/FullName. Match quoted and unquoted query
        // spellings against that FCS identity, including a module-qualified final name.
        // Qualifiers remain exact dot-delimited suffixes, so a punctuation near-miss
        // cannot become a hit and duplicate source names continue to return every site.
        let sourceNameMatch comparison =
            match trySourceIdentifierSuffixLength comparison displayName query with
            | None -> false
            | Some suffixLength when suffixLength = query.Length -> true
            | Some suffixLength when isNull fullName -> false
            | Some suffixLength ->
                let querySeparator = query.Length - suffixLength - 1
                let fullDisplayStart = fullName.Length - displayName.Length

                if
                    querySeparator < 0
                    || query[querySeparator] <> '.'
                    || fullDisplayStart <= 0
                    || not (fullName.EndsWith(displayName, StringComparison.Ordinal))
                    || fullName[fullDisplayStart - 1] <> '.'
                then
                    false
                else
                    let queryQualifierLength = querySeparator
                    let fullQualifierLength = fullDisplayStart - 1

                    (fullQualifierLength = queryQualifierLength
                     && String.Compare(fullName, 0, query, 0, queryQualifierLength, comparison) = 0)
                    || (fullQualifierLength > queryQualifierLength
                        && fullName[fullQualifierLength - queryQualifierLength - 1] = '.'
                        && String.Compare(
                            fullName,
                            fullQualifierLength - queryQualifierLength,
                            query,
                            0,
                            queryQualifierLength,
                            comparison
                        ) = 0)

        if exact then
            String.Equals(displayName, query, StringComparison.Ordinal)
            || String.Equals(fullName, query, StringComparison.Ordinal)
            || dottedSuffixMatch ()
            || sourceNameMatch StringComparison.Ordinal
        else
            displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (if isNull fullName then
                    false
                else
                    fullName.Contains(query, StringComparison.OrdinalIgnoreCase))
            || sourceNameMatch StringComparison.OrdinalIgnoreCase

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
                          $"%s{toolName} expects 'path' to be a source file (.fs/.fsi/.fsx), not a directory. To search project-wide, pass any source file in the project as 'path' and the .fsproj as 'projectPath'." ]
                :> JsonNode
            )
        elif
            not (
                String.Equals(Path.GetExtension(fullPath), ".fs", StringComparison.OrdinalIgnoreCase)
                || String.Equals(Path.GetExtension(fullPath), ".fsi", StringComparison.OrdinalIgnoreCase)
                || String.Equals(Path.GetExtension(fullPath), ".fsx", StringComparison.OrdinalIgnoreCase)
            )
        then
            Some(
                jobj
                    [ "status", jstr "error"
                      "errorKind", jstr "InvalidArgument"
                      "message", jstr $"%s{toolName} expects 'path' to be an F# source file (.fs/.fsi/.fsx): %s{fullPath}" ]
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

    let computeAnalysisSnapshotKey (projectOptions: FSharpProjectOptions) =
        let key = AnalysisSnapshotKey.create projectOptions
        Interlocked.Increment(&analysisSnapshotComputeCount) |> ignore
        key

    let analysisProjectIdentity (projectOptions: FSharpProjectOptions) =
        match projectOptions.ProjectId with
        | Some projectId -> $"id:%s{projectId}"
        | None -> $"path:%s{normalizedCheckWorkPath projectOptions.ProjectFileName}"

    // Cheap, in-memory identity for sharing the expensive content snapshot walk.
    // The final AnalysisSnapshotKey still hashes every source/reference byte; this
    // structural prefix only decides which concurrent callers may share that walk.
    let snapshotComputationKey (projectOptions: FSharpProjectOptions) =
        use aggregate =
            System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256
            )

        let append (value: string) =
            let bytes = System.Text.Encoding.UTF8.GetBytes(if isNull value then "" else value)
            aggregate.AppendData(bytes)
            aggregate.AppendData([| 0uy |])

        append (analysisProjectIdentity projectOptions)

        for sourceFile in projectOptions.SourceFiles do
            append sourceFile

        for compilerOption in projectOptions.OtherOptions do
            append compilerOption

        $"{analysisProjectIdentity projectOptions}|{Convert.ToHexString(aggregate.GetHashAndReset())}"

    let referenceResolutionProbeKey (projectOptions: FSharpProjectOptions) =
        use aggregate =
            System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256
            )

        for compilerOption in projectOptions.OtherOptions do
            let bytes =
                System.Text.Encoding.UTF8.GetBytes(if isNull compilerOption then "" else compilerOption)

            aggregate.AppendData(bytes)
            aggregate.AppendData([| 0uy |])

        $"{analysisProjectIdentity projectOptions}|{Convert.ToHexString(aggregate.GetHashAndReset())}"

    let runReferenceResolutionProbe
        (workKey: string)
        (otherOptions: string array)
        (remainingBudget: (unit -> TimeSpan) option)
        : Task<Result<int * int, CheckBlockingFailure>> =
        let pending =
            referenceResolutionProbesInFlight.GetOrAdd(
                workKey,
                fun _ ->
                    Lazy<Task<Result<int * int, CheckBlockingFailure>>>(
                        (fun () ->
                            task {
                                try
                                    try
                                        let! result =
                                            referenceResolutionProbeAdmission.TryRun(
                                                workKey,
                                                fun () ->
                                                    Task.Run(fun () ->
                                                        match remainingBudget with
                                                        | Some getRemaining when getRemaining () <= TimeSpan.Zero ->
                                                            raise (TimeoutException())
                                                        | _ -> ()

                                                        match referenceResolutionProbeOverride with
                                                        | Some probe -> probe otherOptions
                                                        | None -> ReferenceResolution.probe otherOptions)
                                            )

                                        return Ok result
                                    with
                                    | :? OperationCanceledException as ex -> return Error(CheckCancelled ex.Message)
                                    | :? BoundedCheckWorkBusyException as ex -> return Error(CheckBusy ex.Message)
                                    | :? TimeoutException -> return Error CheckTimedOut
                                    | ex -> return Error(CheckProjectFailure ex.Message)
                                finally
                                    referenceResolutionProbesInFlight.TryRemove(workKey) |> ignore
                            }),
                        LazyThreadSafetyMode.ExecutionAndPublication
                    )
            )

        let operation = pending.Value
        observeFault operation
        operation

    let acquireAnalysisSnapshotKey
        (projectOptions: FSharpProjectOptions)
        (remainingBudget: (unit -> TimeSpan) option)
        =
        let workKey = snapshotComputationKey projectOptions

        acquireSingleFlight
            snapshotComputationsInFlight
            workKey
            remainingBudget
            (fun hasActiveWaiters ->
                snapshotComputationAdmission.TryRun(
                    workKey,
                    fun () ->
                        Task.Run(fun () ->
                            let ensureWorkerNeeded () =
                                if not (hasActiveWaiters ()) then
                                    raise (
                                        TimeoutException(
                                            "The analysis-snapshot worker has no active callers."
                                        )
                                    )

                            ensureWorkerNeeded ()

                            analysisSnapshotKeyBeforeComputeOverride
                            |> Option.iter (fun hook -> hook ())

                            ensureWorkerNeeded ()
                            computeAnalysisSnapshotKey projectOptions)
                ))

    let resolveAnalysisSnapshotKey
        (projectOptions: FSharpProjectOptions)
        (remainingBudget: (unit -> TimeSpan) option)
        =
        task {
            use waiter = acquireAnalysisSnapshotKey projectOptions remainingBudget
            let operation = waiter.Operation
            observeFault operation

            match remainingBudget with
            | Some getRemaining ->
                let remaining = getRemaining ()

                if remaining <= TimeSpan.Zero then
                    return raise (TimeoutException("Analysis snapshot budget was exhausted."))
                else
                    return! operation.WaitAsync(remaining)
            | None -> return! operation
        }

    let acquireAnalysisSnapshotKeyActual
        (projectOptions: FSharpProjectOptions)
        (remainingBudget: unit -> TimeSpan)
        =
        acquireAnalysisSnapshotKey projectOptions (Some remainingBudget)

    let commitAnalysisSnapshotKey (projectOptions: FSharpProjectOptions) (key: string) =
        let projectIdentity = analysisProjectIdentity projectOptions

        Interlocked.Increment(&analysisSnapshotCommitCount) |> ignore

        lock analysisSnapshotGate (fun () ->
            let invalidate =
                match lastAnalysisSnapshotByProject.TryGetValue(projectIdentity) with
                | true, previous when previous <> key ->
                    lastAnalysisSnapshotByProject[projectIdentity] <- key
                    true
                | true, _ -> false
                | false, _ ->
                    lastAnalysisSnapshotByProject.Add(projectIdentity, key)
                    // This bridge may already have populated FCS through a file-level
                    // operation that does not use a project-results cache. Establish a
                    // fresh project boundary on the first snapshot too, so a same-mtime
                    // edit made between that operation and this one cannot survive in
                    // FCS's incremental cache.
                    true

            if invalidate then
                // Keep invalidation inside the gate: a concurrent caller observing the
                // just-recorded key must not proceed before the FCS cache is actually
                // invalidated.
                checker.InvalidateConfiguration(projectOptions))

    let analysisSnapshotKey (projectOptions: FSharpProjectOptions) =
        let key = computeAnalysisSnapshotKey projectOptions
        commitAnalysisSnapshotKey projectOptions key

        key

    let makeFsprojOptionsCacheKey (fsprojPath: string) =
        $"fsproj::%s{normalizePath fsprojPath}"

    let makeOptionsCacheEntry options source fingerprint scriptSourceHash evaluatedSnapshot =
        { Options = options
          Source = source
          Fingerprint = fingerprint
          ScriptSourceHash = scriptSourceHash
          EvaluatedSnapshot = evaluatedSnapshot }

    let scriptSourceHash (source: string) =
        source
        |> System.Text.Encoding.UTF8.GetBytes
        |> System.Security.Cryptography.SHA256.HashData
        |> Convert.ToHexString

    let splitProjectInputList (value: string) =
        value.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

    let directorySourceListingHash (directoryPath: string) =
        let ignoredDirectoryNames =
            set [ "bin"; "obj"; ".git"; ".fslangmcp"; ".vs" ]

        let isFSharpSourceFile (path: string) =
            match Path.GetExtension(path) with
            | extension when String.Equals(extension, ".fs", StringComparison.OrdinalIgnoreCase) -> true
            | extension when String.Equals(extension, ".fsi", StringComparison.OrdinalIgnoreCase) -> true
            | extension when String.Equals(extension, ".fsx", StringComparison.OrdinalIgnoreCase) -> true
            | _ -> false

        let rec collect (directory: DirectoryInfo) (files: ResizeArray<string>) =
            for file in directory.EnumerateFiles() do
                if isFSharpSourceFile file.Name then
                    let relativePath =
                        Path.GetRelativePath(directoryPath, file.FullName).Replace(Path.DirectorySeparatorChar, '/')

                    files.Add(
                        if OperatingSystem.IsWindows() then
                            relativePath.ToUpperInvariant()
                        else
                            relativePath
                    )

            for child in directory.EnumerateDirectories() do
                let ignored = ignoredDirectoryNames.Contains(child.Name.ToLowerInvariant())
                let isReparsePoint = (child.Attributes &&& FileAttributes.ReparsePoint) <> enum 0

                if not ignored && not isReparsePoint then
                    collect child files

        let files = ResizeArray<string>()
        collect (DirectoryInfo(directoryPath)) files

        files
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        |> String.concat "\u0000"
        |> fun listing -> $"fslangmcp-project-source-list-v1\u0000%s{listing}"
        |> System.Text.Encoding.UTF8.GetBytes
        |> System.Security.Cryptography.SHA256.HashData
        |> Convert.ToHexString

    let missingInputIsReliable (path: string) =
        let parent = Path.GetDirectoryName path

        if String.IsNullOrWhiteSpace parent then
            true
        else
            try
                // FileInfo/DirectoryInfo.Exists intentionally collapse access errors
                // into false. Probe the parent so an unreadable input is not cached as
                // a stable "missing" sentinel.
                Directory.EnumerateFileSystemEntries(parent, Path.GetFileName path, SearchOption.TopDirectoryOnly)
                |> Seq.isEmpty
            with
            | :? DirectoryNotFoundException -> true
            | :? FileNotFoundException -> true
            | _ -> false

    let captureProjectOptionsInputStamp kind path =
        try
            match kind with
            | FileInput ->
                let rec capture attemptsRemaining =
                    let before = FileInfo(path)
                    before.Refresh()

                    if not before.Exists then
                        { Kind = kind
                          Path = path
                          Exists = false
                          LastWriteTimeUtcTicks = -1L
                          Length = -1L
                          ContentHash = ""
                          Reliable = missingInputIsReliable path }
                    else
                        use stream =
                            new FileStream(
                                path,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite ||| FileShare.Delete
                            )

                        let contentHash =
                            System.Security.Cryptography.SHA256.HashData(stream)
                            |> Convert.ToHexString

                        let after = FileInfo(path)
                        after.Refresh()

                        if
                            attemptsRemaining > 0
                            && (not after.Exists
                                || before.LastWriteTimeUtc.Ticks <> after.LastWriteTimeUtc.Ticks
                                || before.Length <> after.Length)
                        then
                            capture (attemptsRemaining - 1)
                        else
                            { Kind = kind
                              Path = path
                              Exists = after.Exists
                              LastWriteTimeUtcTicks = after.LastWriteTimeUtc.Ticks
                              Length = after.Length
                              ContentHash = contentHash
                              Reliable =
                                after.Exists
                                && before.LastWriteTimeUtc.Ticks = after.LastWriteTimeUtc.Ticks
                                && before.Length = after.Length }

                capture 1
            | DirectoryInput ->
                let info = DirectoryInfo(path)
                info.Refresh()

                if info.Exists then
                    { Kind = kind
                      Path = path
                      Exists = true
                      LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks
                      Length = -1L
                      ContentHash = directorySourceListingHash path
                      Reliable = true }
                else
                    { Kind = kind
                      Path = path
                      Exists = false
                      LastWriteTimeUtcTicks = -1L
                      Length = -1L
                      ContentHash = ""
                      Reliable = missingInputIsReliable path }
        with _ ->
            { Kind = kind
              Path = path
              Exists = false
              LastWriteTimeUtcTicks = -1L
              Length = -1L
              ContentHash = ""
              Reliable = false }

    let projectOptionsInputStampIsCurrent expected =
        let current = captureProjectOptionsInputStamp expected.Kind expected.Path

        expected.Reliable
        && current.Reliable
        && current.Exists = expected.Exists
        && current.LastWriteTimeUtcTicks = expected.LastWriteTimeUtcTicks
        && current.Length = expected.Length
        && String.Equals(current.ContentHash, expected.ContentHash, StringComparison.Ordinal)

    let projectOptionsCacheEntryIsCurrent entry =
        match entry.Fingerprint with
        | None -> true
        | Some fingerprint -> fingerprint.Inputs |> Array.forall projectOptionsInputStampIsCurrent

    let captureProjectOptionsFingerprint (projects: Ionide.ProjInfo.Types.ProjectOptions list) =
        let pathComparer =
            if OperatingSystem.IsWindows() then
                StringComparer.OrdinalIgnoreCase
            else
                StringComparer.Ordinal

        let fileInputs = System.Collections.Generic.HashSet<string>(pathComparer)
        let directoryInputs = System.Collections.Generic.HashSet<string>(pathComparer)

        let tryResolvePath baseDirectory (path: string) =
            try
                if String.IsNullOrWhiteSpace(path) then
                    None
                else
                    let candidate = path.Trim().Trim('"')

                    if Path.IsPathFullyQualified(candidate) then
                        Some(Path.GetFullPath(candidate))
                    else
                        Some(Path.GetFullPath(Path.Combine(baseDirectory, candidate)))
            with _ ->
                None

        let addFile baseDirectory path =
            match tryResolvePath baseDirectory path with
            | Some fullPath -> fileInputs.Add(fullPath) |> ignore
            | None -> ()

        let addDirectory path =
            try
                if not (String.IsNullOrWhiteSpace(path)) then
                    directoryInputs.Add(Path.GetFullPath(path)) |> ignore
            with _ ->
                ()

        let ancestorSentinelNames =
            [| "Directory.Build.props"
               "Directory.Build.targets"
               "Directory.Packages.props"
               "global.json"
               "NuGet.config"
               "NuGet.Config"
               "nuget.config" |]

        let addAncestorSentinels projectDirectory =
            try
                let mutable directory = DirectoryInfo(projectDirectory)

                while not (isNull directory) do
                    for fileName in ancestorSentinelNames do
                        fileInputs.Add(Path.Combine(directory.FullName, fileName)) |> ignore

                    directory <- directory.Parent
            with _ ->
                ()

        for project in projects do
            let projectPath =
                tryResolvePath (Directory.GetCurrentDirectory()) project.ProjectFileName
                |> Option.defaultValue project.ProjectFileName

            let projectDirectory = Path.GetDirectoryName(projectPath)
            addFile projectDirectory projectPath
            addDirectory projectDirectory
            addAncestorSentinels projectDirectory

            // NuGet creates these after the first restore. Keep explicit missing
            // sentinels so a cache entry captured before restore is invalidated when
            // generated imports appear (existing imports are also in MSBuildAllProjects).
            let projectFileName = Path.GetFileName(projectPath)
            addFile projectDirectory (Path.Combine("obj", $"%s{projectFileName}.nuget.g.props"))
            addFile projectDirectory (Path.Combine("obj", $"%s{projectFileName}.nuget.g.targets"))

            for importedProjectList in project.ProjectSdkInfo.MSBuildAllProjects do
                for importedProject in splitProjectInputList importedProjectList do
                    addFile projectDirectory importedProject

            addFile projectDirectory project.ProjectSdkInfo.ProjectAssetsFile

            for projectReference in project.ReferencedProjects do
                addFile projectDirectory projectReference.ProjectFileName

            for sourceFile in project.SourceFiles do
                match tryResolvePath projectDirectory sourceFile with
                | Some fullPath -> addDirectory (Path.GetDirectoryName(fullPath))
                | None -> ()

        let inputs =
            seq {
                for path in fileInputs do
                    yield FileInput, path

                for path in directoryInputs do
                    yield DirectoryInput, path
            }
            |> Seq.sortBy (fun (kind, path) ->
                let kindOrder =
                    match kind with
                    | FileInput -> 0
                    | DirectoryInput -> 1

                kindOrder, path)
            |> Seq.map (fun (kind, path) -> captureProjectOptionsInputStamp kind path)
            |> Seq.toArray

        { Inputs = inputs }

    let countDiagnosticsBySeverity (diagnostics: FSharpDiagnostic array) =
        let errors =
            diagnostics
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

        let warnings =
            diagnostics
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Warning)

        errors.Length, warnings.Length

    // #190: a genuine tally, not a `total - error - warning` remainder — kept as a
    // separate counter (like `countDiagnosticsBySeverity` above) so a future call-site
    // bug that scopes error/warning/total over mismatched arrays produces a mismatching
    // identity in the tests, instead of being silently absorbed by subtraction.
    let countInfoDiagnostics (diagnostics: FSharpDiagnostic array) =
        diagnostics
        |> Array.filter (fun d ->
            d.Severity = FSharpDiagnosticSeverity.Info || d.Severity = FSharpDiagnosticSeverity.Hidden)
        |> Array.length

    // #206: shared serialized-size ceiling for FileOutline and PublicApi. Both emit
    // per-member signature detail whose per-entry cost is data-dependent (unlike a
    // uniform "site", an API-dense type's member list can run to hundreds of chars),
    // so a type-count/entry-count cap alone (maxResults) still lets a page or a
    // single-file outline overflow the MCP token ceiling — the #100 field failure
    // (2.5k-line project, default call, client-side spill).
    //
    // Measured via `Types.renderedLength` / `Types.isOverRenderedBudget` — the SAME
    // options `Tools.renderToken` ships every response with (`Types.mcpRenderOptions`,
    // one shared definition; see its doc comment for why it lives in `Types.fs`), never
    // `JsonNode.ToJsonString()`'s compact default (~1.46-1.48x smaller — #206 review
    // round 1, Imp-1).
    //
    // The 45k value itself (not 60k) accounts for a SECOND, separate gap: each node is
    // measured standalone at depth 0, but a shipped entity actually sits two levels
    // deeper — inside `entities`/`entries` inside the root response object — so every
    // line of its rendered form carries 4 more leading spaces than the depth-0
    // measurement counted, plus the ~600-char envelope (status/project/pagination
    // fields) on top. That per-LINE penalty (not per-char) hits hardest on
    // signature-*sparse* shapes that pack many short lines per node — a record with a
    // handful of short-typed fields (`F0: int`), or a member-less module — where #206
    // review round 2 measured the real shipped response at 1.14-1.28x the depth-0
    // sum, worst case 76,526 shipped chars from a page whose depth-0 sum was under
    // 60,000. 45,000 x 1.28 = 57,600 — comfortably under the ~25k-token / ~72k-char MCP
    // ceiling even on that worst-case shape, with the same reasoning `Find`'s `pageSize`
    // comment below applies to its own (already-shipped-measured) budget.
    let responseCharBudget = 45_000

    // FileOutline has several independently variable arrays (entries, custom operations,
    // parse diagnostics, and check diagnostics). `responseCharBudget` is the conservative
    // allocation for their standalone nodes; this second ceiling is checked against the
    // fully assembled JSON using the exact serializer used on the wire. The exact guard
    // closes the remaining indentation/envelope gap and makes diagnostic-heavy malformed
    // files safe too (#216 follow-up review).
    let outlineShippedResponseCharBudget = 60_000

    let takeWithinRenderedBudget
        (budget: int)
        (alreadyUsed: int)
        (nodes: JsonNode array)
        : JsonNode array * int * bool =
        let accepted = ResizeArray<JsonNode>()
        let mutable renderedCharsUsed = alreadyUsed
        let mutable index = 0
        let mutable truncated = false

        while index < nodes.Length && not truncated do
            let nodeChars = renderedLength nodes[index]

            if renderedCharsUsed + nodeChars > budget then
                truncated <- true
            else
                accepted.Add(nodes[index])
                renderedCharsUsed <- renderedCharsUsed + nodeChars
                index <- index + 1

        accepted.ToArray(), renderedCharsUsed, truncated

    member private _.LoadProjectOptionsFromFsproj
        (fsprojPath: string, ensureCanContinue: unit -> unit)
        : Task<(FSharpProjectOptions * ProjectOptionsFingerprint * EvaluatedProjectSnapshot) option> =
        task {
            match projectEvaluationBeforeLoadOverride with
            | Some beforeLoad -> do! beforeLoad fsprojPath
            | None -> ()

            // A caller deadline may expire while a test/probe/cache phase is awaiting.
            // Recheck immediately before the non-cancellable MSBuild worker starts.
            ensureCanContinue ()

            // Offload to thread pool — MSBuild/SDK probing is CPU+IO bound. The
            // caller owns the outer admission slot across this actual completion.
            return!
                Task.Run(fun () ->
                    ensureCanContinue ()
                    let projectDir = Path.GetDirectoryName(fsprojPath)

                    // #192: outside the try on purpose. Init.init inherits this
                    // directory's global.json, and an unsatisfiable `rollForward:
                    // "disable"` pin is not a load failure to degrade into None —
                    // it is a machine-configuration problem the agent must be told
                    // about by name. Raising here also keeps MSBuild untouched.
                    SdkPreflight.ensure [ projectDir ]
                    InstallationHealth.ensureCurrent ()
                    ensureCanContinue ()

                    try
                        let toolsPath = Init.init (DirectoryInfo(projectDir)) None
                        let loader = WorkspaceLoader.Create(toolsPath, [])
                        ensureCanContinue ()
                        Interlocked.Increment(&projectOptionsLoadCount) |> ignore
                        let projects = loader.LoadProjects([ fsprojPath ]) |> Seq.toList

                        match projects with
                        | proj :: _ ->
                            let fcsOpts = FCS.mapToFSharpProjectOptions proj (projects |> Seq.map id)
                            let fingerprint = captureProjectOptionsFingerprint projects
                            let snapshot = EvaluatedProjectModel.create "ionide-proj-info" proj fcsOpts
                            Some(fcsOpts, fingerprint, snapshot)
                        | [] -> None
                    with
                    | :? TimeoutException as ex -> raise ex
                    | :? OperationCanceledException as ex -> raise ex
                    | ex ->
                        Console.Error.WriteLine($"[proj-info] Failed to load %s{fsprojPath}: %s{ex.Message}")
                        None)
        }

    member private this.AcquireFsprojEntryWithinBudget
        (fsprojPath: string, remainingBudget: (unit -> TimeSpan) option, ?callerIsNeeded: unit -> bool)
        =
        let fullPath = normalizePath fsprojPath
        let fsprojKey = makeFsprojOptionsCacheKey fullPath

        acquireSingleFlightWhileNeeded
            optionsInFlight
            fsprojKey
            remainingBudget
            (defaultArg callerIsNeeded (fun () -> true))
            (fun hasActiveWaiters ->
                projectEvaluationAdmission.TryRun(
                    fullPath,
                    fun () ->
                        task {
                            let ensureWorkerNeeded () =
                                if not (hasActiveWaiters ()) then
                                    raise (
                                        TimeoutException(
                                            "The project-options worker has no active callers."
                                        )
                                    )

                            ensureWorkerNeeded ()

                            // Cache-hit validation hashes imported files and recursively
                            // inventories source directories. It belongs inside the same
                            // admitted actual worker as a cache miss, not before single-flight.
                            let! cached =
                                Task.Run(fun () ->
                                    ensureWorkerNeeded ()

                                    match optionsCache.TryGet(fsprojKey) with
                                    | Some entry ->
                                        projectOptionsCacheValidationBeforeComputeOverride
                                        |> Option.iter (fun hook -> hook fullPath)

                                        ensureWorkerNeeded ()

                                        Interlocked.Increment(
                                            &projectOptionsCacheValidationCount
                                        )
                                        |> ignore

                                        if projectOptionsCacheEntryIsCurrent entry then
                                            Some entry
                                        else
                                            // A precise reload signal: this key had a
                                            // cached ProjInfo result, its full input
                                            // fingerprint changed, and the single-flight
                                            // owner will evaluate it again below. Cold
                                            // first loads and bounded-cache evictions are
                                            // deliberately not mislabeled as reloads.
                                            Interlocked.Increment(
                                                &projectOptionsStaleReloadCount
                                            )
                                            |> ignore

                                            None
                                    | None -> None)

                            match cached with
                            | Some entry -> return entry
                            | None ->
                                ensureWorkerNeeded ()
                                let! projInfoResult =
                                    this.LoadProjectOptionsFromFsproj(fullPath, ensureWorkerNeeded)

                                match projInfoResult with
                                | Some(projOpts, fingerprint, evaluatedSnapshot) ->
                                    let entry =
                                        makeOptionsCacheEntry
                                            projOpts
                                            "ionide-proj-info"
                                            (Some fingerprint)
                                            None
                                            (Some evaluatedSnapshot)

                                    optionsCache.Set(fsprojKey, entry)
                                    return entry
                                | None ->
                                    return
                                        raise (
                                            InvalidOperationException(
                                                $"Unable to load F# project options from explicit projectPath: {fullPath}"
                                            )
                                        )
                        }
                ))

    member private this.ResolveFsprojEntryWithinBudget
        (fsprojPath: string, remainingBudget: (unit -> TimeSpan) option, ?callerIsNeeded: unit -> bool)
        : Task<ProjectOptionsCacheEntry> =
        task {
            let fullPath = normalizePath fsprojPath
            use waiter =
                this.AcquireFsprojEntryWithinBudget(fullPath, remainingBudget, ?callerIsNeeded = callerIsNeeded)
            let work = waiter.Operation
            observeFault work

            match remainingBudget with
            | None -> return! work
            | Some getRemaining ->
                let remaining = getRemaining ()

                if remaining <= TimeSpan.Zero then
                    return raise (TimeoutException("Project-options evaluation budget was exhausted."))
                else
                    try
                        return! work.WaitAsync(remaining)
                    with :? TimeoutException ->
                        return
                            raise (
                                TimeoutException(
                                    $"Project-options evaluation for '{Path.GetFileName fullPath}' exceeded the remaining {int remaining.TotalMilliseconds}ms budget."
                                )
                            )
        }

    member private this.ResolveFsprojEntry(fsprojPath: string) : Task<ProjectOptionsCacheEntry> =
        this.ResolveFsprojEntryWithinBudget(fsprojPath, None)

    member private this.ResolveFsprojOptionsWithinBudget
        (fsprojPath: string, remainingBudget: (unit -> TimeSpan) option, ?callerIsNeeded: unit -> bool)
        : Task<FSharpProjectOptions * string> =
        task {
            let! entry =
                this.ResolveFsprojEntryWithinBudget(fsprojPath, remainingBudget, ?callerIsNeeded = callerIsNeeded)
            return entry.Options, entry.Source
        }

    member private this.ResolveFsprojOptions(fsprojPath: string) : Task<FSharpProjectOptions * string> =
        this.ResolveFsprojOptionsWithinBudget(fsprojPath, None)

    member private this.ResolveProjectOptions
        (
            path: string,
            text: string,
            projectPath: string option,
            projectOptions: string list option,
            ?ensureCanContinue: unit -> unit
        )
        : Task<FSharpProjectOptions * string> =
        task {
            let continuation = ensureCanContinue
            let ensureCanContinue = defaultArg continuation ignore

            // This nested waiter represents the entire parent position worker, not
            // its first caller. Do not invent a new deadline or permanently-live
            // waiter: a surviving follower may still need the shared evaluation.
            let callerIsNeeded () =
                try
                    ensureCanContinue ()
                    true
                with
                | :? TimeoutException
                | :? OperationCanceledException -> false

            ensureCanContinue ()
            let fullPath = normalizePath path
            let cacheKey = makeCacheKey projectPath projectOptions

            match projectOptions with
            | Some options when not options.IsEmpty ->
                let projectFileName =
                    projectPath
                    |> Option.defaultValue (Path.ChangeExtension(fullPath, ".fsproj"))
                    |> normalizePath

                match optionsCache.TryGet(cacheKey) with
                | Some cached -> return cached.Options, cached.Source
                | None ->
                    ensureCanContinue ()
                    let resolvedOptions =
                        checker.GetProjectOptionsFromCommandLineArgs(projectFileName, options |> List.toArray)

                    optionsCache.Set(cacheKey, makeOptionsCacheEntry resolvedOptions "commandLineArgs" None None None)
                    return resolvedOptions, "commandLineArgs"
            | _ ->
                let requestedFsproj = explicitFsproj projectPath

                let resolvedFsproj =
                    requestedFsproj
                    |> Option.orElseWith (fun () ->
                        match continuation with
                        | Some _ -> findNearestFsprojWithContinuation fullPath (fun _ _ -> ()) callerIsNeeded
                        | None -> findNearestFsproj fullPath)

                ensureCanContinue ()

                match resolvedFsproj with
                | Some fsprojPath ->
                    try
                        return!
                            this.ResolveFsprojOptionsWithinBudget(fsprojPath, None, callerIsNeeded = callerIsNeeded)
                    with
                    | :? TimeoutException as ex -> return raise ex
                    | :? OperationCanceledException as ex -> return raise ex
                    | ex ->
                        match requestedFsproj with
                        | Some _ -> return raise ex
                        | None ->
                            // Fall back to script inference with honest labelling.
                            ensureCanContinue ()
                            let sourceText = SourceText.ofString text

                            ensureCanContinue ()
                            let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask
                            ensureCanContinue ()

                            let discovered =
                                { scriptOptions with
                                    ProjectFileName = fsprojPath }

                            return discovered, "auto-discovered-script-fallback"
                | None ->
                    ensureCanContinue ()
                    let scriptKey = $"script::%s{fullPath}"
                    let contentHash = scriptSourceHash text

                    match optionsCache.TryGet(scriptKey) with
                    | Some cached when cached.ScriptSourceHash = Some contentHash ->
                        return cached.Options, cached.Source
                    | _ ->
                        let sourceText = SourceText.ofString text
                        ensureCanContinue ()
                        let! scriptOptions, _ = checker.GetProjectOptionsFromScript(fullPath, sourceText) |> asTask
                        ensureCanContinue ()
                        optionsCache.Set(
                            scriptKey,
                            makeOptionsCacheEntry scriptOptions "scriptInference" None (Some contentHash) None
                        )
                        return scriptOptions, "scriptInference"
        }

    member private this.PrepareCheckContextCore
        (
            path: string,
            text: string option,
            projectPath: string option,
            projectOptions: string list option,
            resolvedOptions: (FSharpProjectOptions * string) option,
            ensureCanContinue: unit -> unit
        ) : Task<
                string * string * string * FSharpProjectOptions * FSharpParseFileResults * FSharpCheckFileResults option
             >
        =
        task {
            ensureCanContinue ()
            let fullPath = normalizePath path
            let source = text |> Option.defaultWith (fun () -> File.ReadAllText(fullPath))
            let sourceText = SourceText.ofString source
            ensureCanContinue ()

            let! options, optionsSource =
                match resolvedOptions with
                | Some context -> Task.FromResult context
                | None ->
                    this.ResolveProjectOptions(
                        fullPath, source, projectPath, projectOptions, ensureCanContinue = ensureCanContinue
                    )

            ensureCanContinue ()
            let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)
            let! parseResults = checker.ParseFile(fullPath, sourceText, parsingOptions) |> asTask
            ensureCanContinue ()
            let! _, checkAnswer = checker.ParseAndCheckFileInProject(fullPath, 0, sourceText, options) |> asTask
            ensureCanContinue ()

            let checkedResults =
                match checkAnswer with
                | FSharpCheckFileAnswer.Succeeded results -> Some results
                | FSharpCheckFileAnswer.Aborted -> None

            return fullPath, source, optionsSource, options, parseResults, checkedResults
        }

    member private this.PrepareCheckContext
        (path: string, text: string option, projectPath: string option, projectOptions: string list option)
        : Task<string * string * string * FSharpProjectOptions * FSharpParseFileResults * FSharpCheckFileResults option> =
        this.PrepareCheckContextCore(path, text, projectPath, projectOptions, None, ignore)

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

            // Resolve source/options without checking first: the only parse+check below
            // must run after invalidation, otherwise every fcs_check_file pays for a
            // throwaway baseline check before doing the fresh one.
            let path = normalizePath args.path
            let source = args.text |> Option.defaultWith (fun () -> File.ReadAllText(path))
            let sourceText = SourceText.ofString source

            let! projectOptions, optionsSource =
                this.ResolveProjectOptions(path, source, args.projectPath, args.projectOptions)

            // Drop the cached semantic results for THIS project only. Resolved project
            // options remain reusable: their own input fingerprint invalidates them when
            // the project graph changes, while source edits are handled by FCS below.
            let projectResultsKey = analysisSnapshotKey projectOptions
            projectResultsCache.TryRemove(projectResultsKey) |> ignore

            // Ask FCS to drop its incremental-build cache for this project so
            // transitively-checked files are re-read from disk. Per-project, not
            // global — keeps other projects' caches warm.
            checker.InvalidateConfiguration(projectOptions)

            // One fresh operation returns both parse and type-check results.
            let! parseResults, checkAnswer =
                checker.ParseAndCheckFileInProject(path, 0, sourceText, projectOptions) |> asTask

            let checkedResults =
                match checkAnswer with
                | FSharpCheckFileAnswer.Succeeded results -> Some results
                | FSharpCheckFileAnswer.Aborted -> None

            let parseDiagnostics = parseResults.Diagnostics |> Array.map diagnosticToJson

            let checkDiagnostics =
                checkedResults
                |> Option.map (fun r -> r.Diagnostics |> Array.map diagnosticToJson)
                |> Option.defaultValue [||]

            let hasTypeCheckInfo =
                checkedResults |> Option.map _.HasFullTypeCheckInfo |> Option.defaultValue false

            let status =
                if checkedResults.IsSome then "succeeded" else "aborted"

            // #190: errorCount used to be read back off the JSON `severity` field via
            // JsonValue.TryGetValue<int>, but diagnosticToJson serializes severity as text
            // (e.g. "Error") — the int read never matched, so errorCount was silently 0
            // even with real type errors. Count off the raw FCS diagnostics instead, the
            // same way every other totalDiagnostics emitter in this file does. infoCount is
            // a genuine tally via countInfoDiagnostics, not a `total - error - warning`
            // remainder, so a future scoping bug in any one of the three counters shows up
            // as a broken identity in the tests instead of being silently absorbed.
            let rawDiagnostics =
                match checkedResults with
                | Some r -> Array.append parseResults.Diagnostics r.Diagnostics
                | None -> parseResults.Diagnostics

            let errorCount, warningCount = countDiagnosticsBySeverity rawDiagnostics
            let totalDiagnosticsCount = rawDiagnostics.Length
            let infoCount = countInfoDiagnostics rawDiagnostics

            // No belowSeverityFloorCount/diagnosticsNote here: this payload has no
            // severity floor — parseDiagnostics/checkDiagnostics below return every
            // diagnostic, unfiltered. Emitting a hardcoded belowSeverityFloorCount: 0
            // would advertise a filtering mechanism this member doesn't have.
            return
                jobj
                    [ "status", jstr status
                      "file", jstr path
                      "optionsSource", jstr optionsSource
                      "projectFileName", jstr projectOptions.ProjectFileName
                      "parseHadErrors", jbool parseResults.ParseHadErrors
                      "hasFullTypeCheckInfo", jbool hasTypeCheckInfo
                      "errorCount", jint errorCount
                      "warningCount", jint warningCount
                      "infoCount", jint infoCount
                      "totalDiagnostics", jint totalDiagnosticsCount
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

                    // #190: counted the same way every other totalDiagnostics emitter in
                    // this file does — countDiagnosticsBySeverity for error/warning,
                    // countInfoDiagnostics as an independent tally (not a
                    // `total - error - warning` remainder) so the three counters can
                    // never silently paper over a future scoping mismatch.
                    let errorCount, warningCount = countDiagnosticsBySeverity allDiagnostics
                    let infoCount = countInfoDiagnostics allDiagnostics

                    // No belowSeverityFloorCount/diagnosticsNote here: this payload has no
                    // severity floor — `diagnostics` below returns every diagnostic,
                    // unfiltered. Emitting a hardcoded belowSeverityFloorCount: 0 would
                    // advertise a filtering mechanism this member doesn't have.
                    return
                        jobj
                            [ "status", jstr (if checkSucceeded then "succeeded" else "aborted")
                              "mode", jstr mode
                              "projectFileName", jstr options.ProjectFileName
                              "optionsSource", jstr optionsSource
                              "parseHadErrors", jbool parseResults.ParseHadErrors
                              "errorCount", jint errorCount
                              "warningCount", jint warningCount
                              "infoCount", jint infoCount
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
            let cacheKey = analysisSnapshotKey projectOptions

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

    member private this.FileOutlineCore
        (
            args: FcsFileOutlineArgs,
            resolvedOptions: (FSharpProjectOptions * string) option,
            ensureCanContinue: unit -> unit
        ) : Task<JsonNode> =
        task {
            match validateSourcePath "fcs_file_outline" args.text args.path with
            | Some err -> return err
            | None ->

                let! path, _, optionsSource, _, parseResults, checkedResults =
                    this.PrepareCheckContextCore(
                        args.path,
                        args.text,
                        args.projectPath,
                        args.projectOptions,
                        resolvedOptions,
                        ensureCanContinue
                    )

                ensureCanContinue ()

                match checkedResults with
                | None ->
                    let parseDiagnosticsAll = parseResults.Diagnostics |> Array.map diagnosticToJson

                    let parseDiagnostics, _, parseDiagnosticsTruncatedByBudget =
                        takeWithinRenderedBudget responseCharBudget 0 parseDiagnosticsAll

                    let parseDiagnosticsArray = JsonArray(parseDiagnostics)

                    let response =
                        jobj
                            [ "status", jstr "aborted"
                              "file", jstr path
                              "optionsSource", jstr optionsSource
                              "parseHadErrors", jbool parseResults.ParseHadErrors
                              "message", jstr "Type checking was aborted. Outline is unavailable."
                              "parseDiagnosticCount", jint parseDiagnosticsAll.Length
                              "parseDiagnostics", parseDiagnosticsArray :> JsonNode
                              "parseDiagnosticsTruncated", jbool parseDiagnosticsTruncatedByBudget
                              "responseTruncatedByBudget", jbool parseDiagnosticsTruncatedByBudget
                              "responseBudgetChars", jint outlineShippedResponseCharBudget
                              "responseSizeHint", null ]

                    let updateAbortedBudgetMetadata () =
                        let truncated = parseDiagnosticsArray.Count < parseDiagnosticsAll.Length
                        response["parseDiagnosticsTruncated"] <- jbool truncated
                        response["responseTruncatedByBudget"] <- jbool truncated

                        response["responseSizeHint"] <-
                            if truncated then
                                jstr
                                    $"Returned %d{parseDiagnosticsArray.Count} of %d{parseDiagnosticsAll.Length} parse diagnostics to keep the complete response within %d{outlineShippedResponseCharBudget} serialized characters. Use check(scope=\"file\") for a diagnostic-focused result."
                            else
                                null

                    updateAbortedBudgetMetadata ()

                    while renderedLength response > outlineShippedResponseCharBudget
                          && parseDiagnosticsArray.Count > 0 do
                        parseDiagnosticsArray.RemoveAt(parseDiagnosticsArray.Count - 1)
                        updateAbortedBudgetMetadata ()

                    if renderedLength response > outlineShippedResponseCharBudget then
                        return
                            jobj
                                [ "status", jstr "aborted"
                                  "errorCode", jstr "outline_response_budget_exceeded"
                                  "message",
                                  jstr
                                      "The fixed outline response metadata exceeded its serialized-size ceiling after all variable arrays were removed."
                                  "responseTruncatedByBudget", jbool true
                                  "responseBudgetChars", jint outlineShippedResponseCharBudget ]
                            :> JsonNode
                    else
                        return response :> JsonNode
                | Some checkResults ->
                    let includeLocal = args.includeLocal |> Option.defaultValue false
                    let includePrivate = args.includePrivate |> Option.defaultValue true
                    let summaryOnly = args.summaryOnly |> Option.defaultValue true
                    let maxResults = args.maxResults |> Option.defaultValue 200

                    // Full, untruncated definition set (lightweight symbol uses — no node
                    // building yet). memberCounts is derived from THIS so it reports true
                    // per-kind totals, while only the truncated slice pays for signature
                    // formatting — mirrors fcs_project_outline's count-vs-truncate split.
                    let allUses =
                        checkResults.GetAllUsesOfAllSymbolsInFile()
                        |> Seq.filter _.IsFromDefinition
                        |> Seq.filter (fun symbolUse -> includeLocal || not (isNoisyLocalSymbol symbolUse))
                        |> Seq.distinctBy (fun symbolUse ->
                            let r = symbolUse.Range
                            symbolUse.Symbol.FullName, r.StartLine, r.StartColumn, r.EndLine, r.EndColumn)
                        |> Seq.sortBy (fun symbolUse ->
                            let r = symbolUse.Range
                            r.StartLine, r.StartColumn)
                        |> Seq.toArray

                    // memberCounts: kind → count over the FULL (untruncated) definition set,
                    // so an agent sees true totals (e.g. "this 5k-line file has 320 functions")
                    // even when summaryOnly drops signatures and maxResults caps the array.
                    // Kind classification is cheap — no signature strings are formatted here.
                    let memberCounts =
                        allUses
                        |> Array.countBy (fun symbolUse -> symbolKind symbolUse.Symbol)
                        |> Array.sortBy fst
                        |> Array.map (fun (kind, n) -> kind, jint n)
                        |> Array.toList
                        |> jobj

                    // entries: only the surfaced slice is mapped to full nodes, so signature
                    // formatting cost stays bounded by maxResults.
                    let entries =
                        allUses
                        |> Array.truncate maxResults
                        |> Array.map (fun symbolUse ->
                            let attributes = attributesOfSymbol symbolUse.Symbol

                            jobj
                                [ "name", jstr symbolUse.Symbol.DisplayName
                                  "fullName", jstrOrNull symbolUse.Symbol.FullName
                                  "kind", jstr (symbolKind symbolUse.Symbol)
                                  "accessibility", symbolAccessibility symbolUse.Symbol
                                  "attributes", attributesToJson attributes
                                  "range", rangeToJson symbolUse.Range
                                  "signature", jstr (symbolTypeString symbolUse.Symbol)
                                  "declarationRange", tryDeclarationRange symbolUse.Symbol ]
                            :> JsonNode)

                    // Computation-expression operation names live in
                    // CustomOperationAttribute constructor arguments and are not implied by
                    // the CLR/F# member name. Keep this compact index in BOTH summary modes,
                    // so agents can answer "which operations does this builder declare?"
                    // without requesting every signature from a large file.
                    let customOperationsAll =
                        allUses
                        |> Array.choose (fun symbolUse ->
                            let customOperation =
                                attributesOfSymbol symbolUse.Symbol |> Array.tryFind isCustomOperationAttribute

                            customOperation
                            |> Option.map (fun attribute ->
                                jobj
                                    [ "operationName",
                                      customOperationName attribute |> Option.map jstr |> Option.defaultValue null
                                      "memberName", jstr symbolUse.Symbol.DisplayName
                                      "fullName", jstrOrNull symbolUse.Symbol.FullName
                                      "range", rangeToJson symbolUse.Range ]
                                :> JsonNode))

                    let containerKinds =
                        [| "module"
                           "record"
                           "union"
                           "class"
                           "interface"
                           "enum"
                           "delegate"
                           "namespace" |]

                    let headersOf (fullEntries: JsonNode array) : JsonNode array =
                        fullEntries
                        |> Array.filter (fun e ->
                            match e["kind"] with
                            | null -> false
                            | k -> containerKinds |> Array.contains (k.GetValue<string>()))
                        |> Array.map (fun e ->
                            jobj
                                [ "name", e["name"].DeepClone()
                                  "kind", e["kind"].DeepClone()
                                  "fullName",
                                  (match e["fullName"] with
                                   | null -> null
                                   | fn -> fn.DeepClone())
                                  "attributes", e["attributes"].DeepClone()
                                  "range",
                                  (match e["range"] with
                                   | null -> null
                                   | r -> r.DeepClone()) ]
                            :> JsonNode)

                    let headerEntries = headersOf entries

                    // The compact index is derived from the full symbol set so its total is
                    // truthful, but the surfaced rows obey BOTH maxResults and the same
                    // rendered-size budget as outline entries. Without this bound, a file
                    // with hundreds of CustomOperation attributes could make summary mode
                    // larger than the full outline guard was designed to permit.
                    let requestedCustomOperations = customOperationsAll |> Array.truncate maxResults

                    // #206: summaryOnly=false asked for full per-member signatures, but a
                    // large file's full outline can still cross the shared response-char
                    // budget (54KB/65KB outlines observed in the field — issue #100 batch 6)
                    // even though maxResults already bounds entry COUNT; a handful of
                    // signature-heavy entries is enough. Measure the would-be full payload
                    // BEFORE committing to it and downgrade to the same header-only shape
                    // summaryOnly=true produces, rather than ever emitting an over-budget
                    // outline. Only measured when summaryOnly=false was actually requested —
                    // an explicit summaryOnly=true request is already small by construction.
                    // #206 review round 1 Imp-1 / round 2 N6: measured via the shared
                    // `Types.isOverRenderedBudget` — same shipped serialization as
                    // `responseCharBudget`'s comment describes, and it stops serializing
                    // further entries the moment the budget is already crossed rather than
                    // summing every one of `entries` (up to `maxResults`) regardless.
                    let overBudget =
                        not summaryOnly
                        && isOverRenderedBudget responseCharBudget (Seq.append entries requestedCustomOperations)

                    let downgradedToSummary = overBudget

                    // summaryOnly (default) OR a budget downgrade: module/type headers with
                    // name/kind/fullName/range only (no per-member signatures). Otherwise the
                    // full per-member output requested.
                    let surfacedEntryNodesAll =
                        if summaryOnly || downgradedToSummary then
                            headerEntries
                        else
                            entries

                    // Allocate one shared node budget across every variable-size array.
                    // Diagnostics used to sit outside this accounting, so a malformed file
                    // with thousands of errors could exceed one megabyte even in summary mode.
                    let surfacedEntryNodes, renderedAfterEntries, entriesTruncatedByBudget =
                        takeWithinRenderedBudget responseCharBudget 0 surfacedEntryNodesAll

                    let customOperations, renderedAfterOperations, customOperationsTruncatedByBudgetInitial =
                        takeWithinRenderedBudget responseCharBudget renderedAfterEntries requestedCustomOperations

                    let parseDiagnosticsAll = parseResults.Diagnostics |> Array.map diagnosticToJson

                    let parseDiagnostics, renderedAfterParseDiagnostics, parseDiagnosticsTruncatedByBudgetInitial =
                        takeWithinRenderedBudget responseCharBudget renderedAfterOperations parseDiagnosticsAll

                    let checkDiagnosticsAll = checkResults.Diagnostics |> Array.map diagnosticToJson

                    let checkDiagnostics, _, checkDiagnosticsTruncatedByBudgetInitial =
                        takeWithinRenderedBudget responseCharBudget renderedAfterParseDiagnostics checkDiagnosticsAll

                    let outEntriesArray = JsonArray(surfacedEntryNodes)
                    let customOperationsArray = JsonArray(customOperations)
                    let parseDiagnosticsArray = JsonArray(parseDiagnostics)
                    let checkDiagnosticsArray = JsonArray(checkDiagnostics)

                    let mutable customOperationsTruncatedByBudget =
                        customOperationsTruncatedByBudgetInitial

                    let mutable parseDiagnosticsTruncatedByBudget =
                        parseDiagnosticsTruncatedByBudgetInitial

                    let mutable checkDiagnosticsTruncatedByBudget =
                        checkDiagnosticsTruncatedByBudgetInitial

                    // Additive-only (#206): present exactly when the requested summaryOnly=false
                    // was downgraded to headers because the full outline exceeded the budget —
                    // absent on every other path, so a small file with summaryOnly=false is a
                    // byte-for-byte unchanged response.
                    let downgradeFields =
                        if downgradedToSummary then
                            let hint =
                                $"Full outline for this file exceeds the ~%d{responseCharBudget}-char response budget; returning summary-level headers (name/kind/fullName/attributes/range, no signatures) instead. Narrow with a smaller maxResults (currently %d{maxResults}) so the full per-member signatures for that slice fit within budget."

                            [ "downgradedToSummary", jbool true; "hint", jstr hint ]
                        else
                            []

                    let response =
                        jobj (
                            [ "status", jstr "succeeded"
                              "file", jstr path
                              "optionsSource", jstr optionsSource
                              "includePrivate", jbool includePrivate
                              "includeLocal", jbool includeLocal
                              "summaryOnly", jbool summaryOnly
                              "count", jint entries.Length
                              "totalDefinitionCount", jint allUses.Length
                              "entriesComplete", jbool false
                              "returnedEntryCount", jint outEntriesArray.Count
                              "entriesTruncatedByBudget", jbool entriesTruncatedByBudget
                              "memberCounts", memberCounts
                              "entries", outEntriesArray :> JsonNode
                              "customOperationCount", jint customOperationsAll.Length
                              "customOperations", customOperationsArray :> JsonNode
                              "customOperationsTruncated", jbool false
                              "customOperationsTruncatedByBudget", jbool false
                              "customOperationsHint", null ]
                            @ downgradeFields
                            @ [ "parseDiagnosticCount", jint parseDiagnosticsAll.Length
                                "parseDiagnostics", parseDiagnosticsArray :> JsonNode
                                "parseDiagnosticsTruncated", jbool false
                                "checkDiagnosticCount", jint checkDiagnosticsAll.Length
                                "checkDiagnostics", checkDiagnosticsArray :> JsonNode
                                "checkDiagnosticsTruncated", jbool false
                                "diagnosticsHint", null
                                "responseTruncatedByBudget", jbool false
                                "responseBudgetChars", jint outlineShippedResponseCharBudget
                                "responseSizeHint", null ]
                        )

                    let updateBudgetMetadata () =
                        let entriesWereTruncated = outEntriesArray.Count < surfacedEntryNodesAll.Length

                        let customOperationsTruncated =
                            customOperationsArray.Count < customOperationsAll.Length

                        let parseDiagnosticsTruncated =
                            parseDiagnosticsArray.Count < parseDiagnosticsAll.Length

                        let checkDiagnosticsTruncated =
                            checkDiagnosticsArray.Count < checkDiagnosticsAll.Length

                        response["returnedEntryCount"] <- jint outEntriesArray.Count
                        response["entriesTruncatedByBudget"] <- jbool entriesWereTruncated

                        response["entriesComplete"] <-
                            jbool (
                                not summaryOnly
                                && not downgradedToSummary
                                && outEntriesArray.Count = allUses.Length
                            )

                        response["customOperationsTruncated"] <- jbool customOperationsTruncated
                        response["customOperationsTruncatedByBudget"] <- jbool customOperationsTruncatedByBudget
                        response["parseDiagnosticsTruncated"] <- jbool parseDiagnosticsTruncated
                        response["checkDiagnosticsTruncated"] <- jbool checkDiagnosticsTruncated

                        response["customOperationsHint"] <-
                            if customOperationsTruncated then
                                jstr
                                    $"Returned %d{customOperationsArray.Count} of %d{customOperationsAll.Length} CustomOperation rows, bounded by maxResults=%d{maxResults} and the shared outline response budget."
                            else
                                null

                        response["diagnosticsHint"] <-
                            if parseDiagnosticsTruncated || checkDiagnosticsTruncated then
                                jstr
                                    $"Returned %d{parseDiagnosticsArray.Count} of %d{parseDiagnosticsAll.Length} parse diagnostics and %d{checkDiagnosticsArray.Count} of %d{checkDiagnosticsAll.Length} check diagnostics. Use check(scope=\"file\") for a diagnostic-focused result."
                            else
                                null

                        let responseTruncated =
                            entriesWereTruncated
                            || customOperationsTruncatedByBudget
                            || parseDiagnosticsTruncatedByBudget
                            || checkDiagnosticsTruncatedByBudget

                        response["responseTruncatedByBudget"] <- jbool responseTruncated

                        response["responseSizeHint"] <-
                            if responseTruncated then
                                jstr
                                    $"Variable-size outline arrays were truncated to keep the complete serialized response within %d{outlineShippedResponseCharBudget} characters. Full counts remain available in memberCounts, customOperationCount, parseDiagnosticCount, and checkDiagnosticCount."
                            else
                                null

                    updateBudgetMetadata ()

                    // The conservative per-node budget above avoids almost all retries. This
                    // exact final check measures the assembled root with its real indentation
                    // and metadata. Trim diagnostics first, then the auxiliary operation index,
                    // and only then structural entries. An impossible fixed-envelope overflow
                    // is still bounded by the compact typed fallback below.
                    let tryTrimOneArrayItem () =
                        if checkDiagnosticsArray.Count > 0 then
                            checkDiagnosticsArray.RemoveAt(checkDiagnosticsArray.Count - 1)
                            checkDiagnosticsTruncatedByBudget <- true
                            true
                        elif parseDiagnosticsArray.Count > 0 then
                            parseDiagnosticsArray.RemoveAt(parseDiagnosticsArray.Count - 1)
                            parseDiagnosticsTruncatedByBudget <- true
                            true
                        elif customOperationsArray.Count > 0 then
                            customOperationsArray.RemoveAt(customOperationsArray.Count - 1)
                            customOperationsTruncatedByBudget <- true
                            true
                        elif outEntriesArray.Count > 0 then
                            outEntriesArray.RemoveAt(outEntriesArray.Count - 1)
                            true
                        else
                            false

                    let mutable canTrim = true

                    while renderedLength response > outlineShippedResponseCharBudget && canTrim do
                        canTrim <- tryTrimOneArrayItem ()
                        updateBudgetMetadata ()

                    if renderedLength response > outlineShippedResponseCharBudget then
                        return
                            jobj
                                [ "status", jstr "aborted"
                                  "errorCode", jstr "outline_response_budget_exceeded"
                                  "message",
                                  jstr
                                      "The fixed outline response metadata exceeded its serialized-size ceiling after all variable arrays were removed."
                                  "responseTruncatedByBudget", jbool true
                                  "responseBudgetChars", jint outlineShippedResponseCharBudget ]
                            :> JsonNode
                    else
                        return response :> JsonNode
        }

    member this.FileOutline(args: FcsFileOutlineArgs) : Task<JsonNode> =
        this.FileOutlineCore(args, None, ignore)

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
            let cacheKey = analysisSnapshotKey projectOptions

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

            let cacheKey = analysisSnapshotKey projectOptions

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

            let cacheKey = analysisSnapshotKey projectOptions

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

            let lineContextCache = System.Collections.Generic.Dictionary<string, string array>()

            let siteToJson (symbolUse: FSharpSymbolUse) =
                let r = symbolUse.Range
                let context = lineContextToJson lineContextCache 2 symbolUse.FileName r.StartLine
                let form = formOf symbolUse

                jobj
                    [ "file", jstr (normalizePath symbolUse.FileName)
                      "range", rangeToJsonNoFile r
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

            let cacheKey = analysisSnapshotKey projectOptions

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

            let lineContextCache = System.Collections.Generic.Dictionary<string, string array>()

            let useToJson (symbolUse: FSharpSymbolUse) =
                let r = symbolUse.Range
                let context = lineContextToJson lineContextCache contextLines symbolUse.FileName r.StartLine

                jobj
                    [ "file", jstr (normalizePath symbolUse.FileName)
                      "range", rangeToJsonNoFile r
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
    member private this.ResolveQueryAtPosition
        (args: FindArgs, ensureCanContinue: unit -> unit)
        : Task<Result<string, JsonNode>> =
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
                        ensureCanContinue ()

                        let! _, source, _, _, _, checkedResults =
                            this.PrepareCheckContextCore(
                                path,
                                None,
                                args.projectPath,
                                None,
                                None,
                                ensureCanContinue
                            )

                        return this.ResolvePositionInFile(path, line, source, checkedResults, args)
        }

    // ── find: multi-project union sweep (issue #128, Stage 1) ───────────────────
    // Productionized from spike/find-multiproject-sweep. Sweeps every member
    // .fsproj of the active solution, unions each project's GetAllUsesOfAllSymbols()
    // (de-duped by FULL source range), and auto-unions record-field and member
    // usage sites the single-project fcs_find_symbol / fcs_record_field_audit miss.
    //
    // HEADLINE TRUST PROPERTY: matched=false is reported ONLY after every requested
    // FCS project completed successfully. A timeout/load failure makes the result
    // partial or indeterminate; an unavailable/mismatched FSAC probe is never folded
    // into an authoritative zero.
    //
    // FCS CROSS-COMPILATION INVARIANT (do NOT "fix" with ==): FSharpSymbol instances
    // from DIFFERENT ParseAndCheckProject compilations are NOT reference-equal for
    // the same logical symbol. We match the target by stable strings (DisplayName /
    // FullName via symbolMatches; DeclaringEntity.DisplayName for fields/members)
    // and de-dup by source LOCATION, never by symbol identity. FSharpSymbolUse is a
    // struct, so we bind `let r = u.Range` before reading r.FileName (FS0052).

    // issue #131/#168 P1-07: cache-or-compute one project's whole-symbol-use
    // enumeration + diagnostics, keyed by the shared analysis content snapshot. Kept in its own method
    // so Find's per-project loop awaits a plain Task instead of nesting a task CE
    // (which is not statically compilable under Release optimization → FS3511).
    // FCS does not expose cancellation for GetAllUsesOfAllSymbols once the synchronous
    // walk starts. Find races the actual task so its outer admission lifetime can retain
    // the real worker; legacy callers use the timeout wrapper below. In both cases
    // projectUsesInFlight deduplicates retries.
    member private _.ProjectSweepUsesActual
        (usesKey: string, options: FSharpProjectOptions)
        : Task<FSharpSymbolUse array * FSharpDiagnostic array> =
        task {
            match projectUsesCache.TryGet(usesKey) with
            | Some cached -> return cached
            | None ->
                let generation = Volatile.Read(&projectUsesCacheGeneration)

                let pending =
                    projectUsesInFlight.GetOrAdd(
                        usesKey,
                        fun _ ->
                            Lazy<Task<FSharpSymbolUse array * FSharpDiagnostic array>>(
                                (fun () ->
                                    task {
                                        try
                                            return!
                                                projectUsesAdmission.TryRun(
                                                    options.ProjectFileName,
                                                    fun () ->
                                                        task {
                                                            let! value =
                                                                match projectSweepWorkerOverride with
                                                                | Some worker -> worker options.ProjectFileName
                                                                | None ->
                                                                    task {
                                                                        let! results =
                                                                            checker.ParseAndCheckProject(options)
                                                                            |> asTask

                                                                        let uses = results.GetAllUsesOfAllSymbols()
                                                                        return uses, results.Diagnostics
                                                                    }

                                                            // set_project may clear caches while this
                                                            // uncancellable FCS walk is still running. Do
                                                            // not repopulate a new generation with the old
                                                            // workspace's result.
                                                            if Volatile.Read(&projectUsesCacheGeneration) = generation then
                                                                projectUsesCache.Set(usesKey, value)

                                                            return value
                                                        }
                                                )
                                        finally
                                            projectUsesInFlight.TryRemove(usesKey) |> ignore
                                    }),
                                LazyThreadSafetyMode.ExecutionAndPublication
                            )
                    )

                let work = pending.Value
                observeFault work
                return! work
        }

    member private this.ProjectSweepUses
        (usesKey: string, options: FSharpProjectOptions, timeoutMs: int)
        : Task<FSharpSymbolUse array * FSharpDiagnostic array> =
        task {
            let work = this.ProjectSweepUsesActual(usesKey, options)

            try
                return! work.WaitAsync(TimeSpan.FromMilliseconds(float (max 1 timeoutMs)))
            with :? TimeoutException ->
                return
                    raise (
                        TimeoutException(
                            $"FCS sweep of '{Path.GetFileNameWithoutExtension options.ProjectFileName}' timed out after %d{timeoutMs}ms (cold cache or very large project). Run `dotnet build` to warm it, retry, or raise timeoutMs."
                        )
                    )
        }

    member private this.FindCoreWithinDeadline
        (
            args: FindArgs,
            deadline: FindRequestDeadline,
            cancellationToken: CancellationToken,
            retainUntil: Task -> unit,
            fsacProbe: (string -> Task<FindFsacProbeResult>) option,
            recordMeasuredResponse: JsonNode -> unit
        )
        : Task<JsonNode> =
        task {
            let ensureCanStart phase =
                cancellationToken.ThrowIfCancellationRequested()

                if deadline.SemanticExpired then
                    deadline.MarkSemanticExpired()
                    raise (TimeoutException($"The find deadline was exhausted before {phase} started."))

            let remainingWorkerBudget () =
                if cancellationToken.IsCancellationRequested then
                    TimeSpan.Zero
                else
                    deadline.RemainingSemanticBudget()

            let awaitWithinDeadline
                (phase: string)
                (retainIncompleteOperation: bool)
                (operation: Task<'T>)
                : Task<'T> =
                task {
                    let remaining = deadline.RemainingSemanticBudget()

                    if remaining <= TimeSpan.Zero then
                        deadline.MarkSemanticExpired()
                        observeFault operation

                        if not operation.IsCompleted then
                            if retainIncompleteOperation then
                                retainUntil (operation :> Task)

                        return raise (TimeoutException($"The find deadline was exhausted during {phase}."))
                    else
                        use deadlineWaitCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                        let timeout = Task.Delay(remaining, deadlineWaitCancellation.Token)

                        let contenders =
                            match deadline.SemanticExpirySignal with
                            | Some signal -> [| operation :> Task; timeout; signal |]
                            | None -> [| operation :> Task; timeout |]

                        let! winner = Task.WhenAny contenders
                        deadlineWaitCancellation.Cancel()

                        let operationWon =
                            Object.ReferenceEquals(winner, operation :> Task)
                            && not deadline.SemanticExpired
                            && not cancellationToken.IsCancellationRequested

                        if operationWon then
                            return! operation
                        else
                            observeFault operation

                            if not operation.IsCompleted then
                                if retainIncompleteOperation then
                                    retainUntil (operation :> Task)

                            // Cancellation may be observed while an admitted MSBuild/FCS
                            // operation is already uncancellable. Register its real task
                            // with the outer lifetime before propagating cancellation, or
                            // Program.fs would release the shared slot too early.
                            deadline.MarkSemanticExpired()
                            cancellationToken.ThrowIfCancellationRequested()

                            return raise (TimeoutException($"The find deadline was exhausted during {phase}."))
                }

            let awaitRetainedSingleFlight
                (phase: string)
                (waiter: WaiterAwareSingleFlightWaiter<'T>)
                : Task<'T> =
                task {
                    use waiter = waiter
                    let operation = waiter.Operation
                    return! awaitWithinDeadline phase true operation
                }

            let incompleteBeforeDiscovery phase phaseStatus errorKind message =
                FindDeadlineResponse.beforeDiscovery args deadline phase phaseStatus errorKind message

            match ArgsValidation.requireNonBlank "query" args.query with
            | Error _ ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "find requires a non-empty query (symbol, type, or member name). Expected parameters: query (required); kind, scope, exact, member, field, path, line, word, occurrence, character, contextLines, includeDeclaration, includeInfo, includePerProject, includeSiteTypes, projectPath, maxResults, timeoutMs, cursor (optional)." ]
                    :> JsonNode
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
            let includePerProject = args.includePerProject |> Option.defaultValue true
            // #207: opt-in per-site field types. Off by default — resolving and formatting
            // a type for every site is FCS work the common `find` call has no use for.
            let includeSiteTypes = args.includeSiteTypes |> Option.defaultValue false
            // #255: timeoutMs is one end-to-end monotonic budget (default 120s), shared
            // with outer admission and every resolution/discovery/FCS/fallback phase.
            // Semantic work reserves a bounded allowance for constructing typed evidence;
            // projects not begun before the cutoff are reported separately.
            let requestTimeoutMs = args.timeoutMs |> Option.defaultValue 120000
            // Default page size keeps the compact payload well under the MCP token
            // ceiling on a cap-case hit. Each site is now serialized ONCE — the flat
            // `sites` list — after the grouped definitions/references/fieldSites/
            // memberSites buckets (which duplicated every site) were dropped, so the
            // per-site cost dropped from ~1030 to ~527 chars (measured on
            // find("FindArgs")). With one representation the default is raised from 40
            // to 80: a typical 80-site page is ~43k chars, and a 69-site hot symbol fits one
            // page at ~37k chars instead of truncating at 40. breakdown + totalSites
            // still report the FULL set, and cursor/nextCursor pages the rest, so a
            // complete refactor list stays reachable past the default.
            //
            // #207 re-derivation — READ THIS BEFORE CHANGING EITHER NUMBER. includeSiteTypes
            // adds one `siteType` string per FIELD row, so the envelope above is no longer
            // the whole story. The increment is bounded by construction at
            // `siteTypeMaxChars` (200) + a "..." marker, giving a capped worst case of
            // 80 × (527 + 203 + ~15 chars of JSON key overhead) ≈ 59.6k chars. Measured real
            // growth is far smaller: +18.5 chars/site on the field fixture, and a 58-char
            // longest siteType across a 3434-site sweep of this repo. That arithmetic is
            // ASSERTED in FindTests ("the type column stays inside find's documented page
            // budget"), so raising siteTypeMaxChars without redoing this math fails there.
            // This arithmetic now guides only the default; bounded snippets plus the exact
            // final production-serializer guard below provide the actual hard ceiling.
            let pageSize = args.maxResults |> Option.defaultValue 80

            let invalidArgs message =
                jobj [ "status", jstr "invalid_args"; "message", jstr message ] :> JsonNode

            let mutable validationError =
                if
                    not (
                        Set.ofList [ "auto"; "symbol"; "members"; "field"; "definition"; "position" ]
                        |> Set.contains kind
                    )
                then
                    Some
                        $"kind must be one of: auto, symbol, members, field, definition, position; got '%s{kind}'."
                elif not (Set.ofList [ "auto"; "file"; "project"; "workspace" ] |> Set.contains scope) then
                    Some $"scope must be one of: auto, file, project, workspace; got '%s{scope}'."
                elif pageSize < 1 || pageSize > 1000 then
                    Some $"maxResults must be between 1 and 1000; got %d{pageSize}."
                elif contextLines < 0 then
                    Some $"contextLines must be non-negative; got %d{contextLines}."
                elif requestTimeoutMs < 0 then
                    Some $"timeoutMs must be non-negative; got %d{requestTimeoutMs}."
                elif args.line |> Option.exists (fun value -> value < 0) then
                    Some "line must be non-negative (0-based)."
                elif args.character |> Option.exists (fun value -> value < 0) then
                    Some "character must be non-negative (0-based)."
                elif args.occurrence |> Option.exists (fun value -> value < -1) then
                    Some "occurrence must be -1 (first match) or a non-negative 0-based index."
                elif
                    scope = "file"
                    && (args.path |> Option.forall String.IsNullOrWhiteSpace)
                then
                    Some "scope='file' requires a non-empty path."
                else
                    None

            let mutable pageOffset = 0

            match args.cursor with
            | None -> ()
            | Some cursorStr ->
                match Cursor.tryDecode cursorStr with
                | Ok payload -> pageOffset <- payload.offset
                | Error reason -> validationError <- Some $"Invalid cursor: %s{reason}"

            // kind=position resolves the symbol under the cursor, then sweeps it as a symbol.
            let! queryResult =
                task {
                    match validationError with
                    | Some message -> return Error(invalidArgs message)
                    | None ->
                        if kind = "position" then
                            try
                                ensureCanStart "position resolution"

                                let waiter =
                                    runFindPositionResolution
                                        args
                                        remainingWorkerBudget
                                        (fun hasActiveWaiters ->
                                            this.ResolveQueryAtPosition(
                                                args,
                                                fun () ->
                                                    if not (hasActiveWaiters ()) then
                                                        raise (
                                                            TimeoutException(
                                                                "The find position-resolution worker has no active callers."
                                                            )
                                                        )
                                            ))

                                let! result = awaitRetainedSingleFlight "position resolution" waiter
                                return result
                            with
                            | :? TimeoutException as ex ->
                                return
                                    Error(
                                        incompleteBeforeDiscovery
                                            "position_resolution"
                                            "timed_out"
                                            "find_timeout"
                                            ex.Message
                                    )
                            | :? BoundedCheckWorkBusyException as ex ->
                                return
                                    Error(
                                        incompleteBeforeDiscovery
                                            "position_resolution"
                                            "busy"
                                            "fcs_worker_busy"
                                            ex.Message
                                    )
                        else
                            if deadline.SemanticExpired then
                                return
                                    Error(
                                        incompleteBeforeDiscovery
                                            "admission"
                                            "timed_out"
                                            "find_timeout"
                                            "The find end-to-end deadline was already exhausted."
                                    )
                            else
                                return Ok query0
                }

            match queryResult with
            | Error envelope -> return envelope
            | Ok query ->

            let kindResolved = if kind = "position" then "symbol" else kind

            let findNearestProjectForRequest path shouldContinue =
                let beforeStep =
                    findTargetDiscoveryBeforeStepOverride
                    |> Option.defaultValue (fun _ _ -> ())

                findNearestFsprojWithContinuation
                    path
                    beforeStep
                    shouldContinue

            // Resolve the sweep target inside the same deadline. The nearest-project
            // filesystem walk is admitted single-flight work, never a synchronous prelude.
            let explicitSweepTarget =
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath

            let! sweepTargetResult =
                task {
                    match explicitSweepTarget with
                    | Some target -> return Ok(Some target)
                    | None ->
                        match args.path |> Option.filter (String.IsNullOrWhiteSpace >> not) with
                        | None -> return Ok None
                        | Some path ->
                            try
                                ensureCanStart "nearest-project discovery"

                                let waiter =
                                    runFindTargetDiscovery
                                        "nearest-project"
                                        path
                                        remainingWorkerBudget
                                        (fun shouldContinue ->
                                            findNearestProjectForRequest path shouldContinue
                                            |> Option.map normalizePath
                                            |> Option.toArray
                                            |> FindTargetDiscoveryResult.Paths)

                                let! discovered =
                                    awaitRetainedSingleFlight "nearest-project discovery" waiter

                                match discovered with
                                | FindTargetDiscoveryResult.Paths paths -> return Ok(Array.tryHead paths)
                                | FindTargetDiscoveryResult.Projects _ ->
                                    return raise (InvalidOperationException("Unexpected find discovery result."))
                            with
                            | :? TimeoutException as ex ->
                                return
                                    Error(
                                        incompleteBeforeDiscovery
                                            "target_discovery"
                                            "timed_out"
                                            "find_timeout"
                                            ex.Message
                                    )
                            | :? BoundedCheckWorkBusyException as ex ->
                                return
                                    Error(
                                        incompleteBeforeDiscovery
                                            "target_discovery"
                                            "busy"
                                            "fcs_worker_busy"
                                            ex.Message
                                    )
                }

            match sweepTargetResult with
            | Error envelope -> return envelope
            | Ok None ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "find needs a project context: pass projectPath (.fsproj/.sln/.slnx) or path, or call set_project first." ]
                    :> JsonNode
            | Ok(Some sweepTarget) ->

            let! projectDiscoveryResult =
                task {
                    try
                        ensureCanStart "project discovery"

                        let waiter =
                            runFindTargetDiscovery
                                "projects"
                                sweepTarget
                                remainingWorkerBudget
                                (fun shouldContinue ->
                                    let beforeStep =
                                        findTargetDiscoveryBeforeStepOverride
                                        |> Option.defaultValue (fun _ _ -> ())

                                    SolutionParsing.discoverProjectsForFind
                                        sweepTarget
                                        beforeStep
                                        shouldContinue
                                    |> FindTargetDiscoveryResult.Projects)

                        let! discovery = awaitRetainedSingleFlight "project discovery" waiter

                        match discovery with
                        | FindTargetDiscoveryResult.Projects projects -> return Ok projects
                        | FindTargetDiscoveryResult.Paths _ ->
                            return raise (InvalidOperationException("Unexpected find discovery result."))
                    with
                    | :? TimeoutException as ex ->
                        return
                            Error(
                                incompleteBeforeDiscovery
                                    "target_discovery"
                                    "timed_out"
                                    "find_timeout"
                                    ex.Message
                            )
                    | :? BoundedCheckWorkBusyException as ex ->
                        return
                            Error(
                                incompleteBeforeDiscovery
                                    "target_discovery"
                                    "busy"
                                    "fcs_worker_busy"
                                    ex.Message
                            )
                }

            match projectDiscoveryResult with
            | Error envelope -> return envelope
            | Ok projectDiscovery ->

            let loadableMemberProjects = projectDiscovery.LoadableProjects

            let sourcePathComparison =
                if OperatingSystem.IsWindows() then
                    StringComparison.OrdinalIgnoreCase
                else
                    StringComparison.Ordinal

            let fileScopePath =
                if scope = "file" then
                    args.path
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.map normalizePath
                else
                    None

            let isMemberProject (candidate: string) =
                let fullCandidate = normalizePath candidate
                projectDiscovery.MemberProjectPaths.Contains(fullCandidate)

            // scope=file/project narrows to the single owning project; workspace/auto
            // sweeps every member project of the solution.
            let mutable projectResolutionError = None

            let mutable scopedProjectEarlyResponse: JsonNode option = None

            let! scopedProject =
                task {
                    if
                        (scope = "file" || scope = "project")
                        && not (sweepTarget.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                    then
                        match args.path |> Option.filter (String.IsNullOrWhiteSpace >> not) with
                        | None -> return None
                        | Some path ->
                            try
                                ensureCanStart "scoped-project discovery"

                                let waiter =
                                    runFindTargetDiscovery
                                        "scoped-project"
                                        path
                                        remainingWorkerBudget
                                        (fun shouldContinue ->
                                            findNearestProjectForRequest path shouldContinue
                                            |> Option.map normalizePath
                                            |> Option.toArray
                                            |> FindTargetDiscoveryResult.Paths)

                                let! projects =
                                    awaitRetainedSingleFlight "scoped-project discovery" waiter

                                match projects with
                                | FindTargetDiscoveryResult.Paths paths -> return Array.tryHead paths
                                | FindTargetDiscoveryResult.Projects _ ->
                                    return raise (InvalidOperationException("Unexpected find discovery result."))
                            with
                            | :? TimeoutException as ex ->
                                scopedProjectEarlyResponse <-
                                    Some(
                                        incompleteBeforeDiscovery
                                            "target_discovery"
                                            "timed_out"
                                            "find_timeout"
                                            ex.Message
                                    )

                                return None
                            | :? BoundedCheckWorkBusyException as ex ->
                                scopedProjectEarlyResponse <-
                                    Some(
                                        incompleteBeforeDiscovery
                                            "target_discovery"
                                            "busy"
                                            "fcs_worker_busy"
                                            ex.Message
                                    )

                                return None
                    elif sweepTarget.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                        return Some sweepTarget
                    else
                        return None
                }

            let projectsToSweep =
                match scope with
                | "file"
                | "project" ->
                    let single =
                        // An explicit .fsproj is authoritative even when scope=file points
                        // at a linked Compile item below another project directory.
                        if sweepTarget.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                            Some sweepTarget
                        else
                            scopedProject

                    match single with
                    | Some project when isMemberProject project -> ResizeArray([ normalizePath project ])
                    | Some project ->
                        projectResolutionError <-
                            Some
                                $"scope='%s{scope}' resolved '%s{normalizePath project}', which is not a member of the requested workspace '%s{sweepTarget}'."

                        ResizeArray<string>()
                    | None ->
                        projectResolutionError <-
                            Some
                                $"scope='%s{scope}' requires projectPath to be a single .fsproj or path to resolve to one member project; a whole solution cannot be used as a single-project scope."

                        ResizeArray<string>()
                | _ -> loadableMemberProjects

            // Narrow file/project searches intentionally cover one selected project.
            // Workspace/auto searches retain every missing declared solution member as
            // a typed discovery failure while only real files enter the FCS loop.
            let missingProjects =
                match scope with
                | "file"
                | "project" -> ResizeArray<string>()
                | _ -> projectDiscovery.MissingProjects

            if projectsToSweep.Count = 0 && missingProjects.Count = 0 then
                match scopedProjectEarlyResponse with
                | Some envelope -> return envelope
                | None ->
                    let message =
                        projectResolutionError
                        |> Option.defaultValue $"find could not resolve any .fsproj to sweep from: %s{sweepTarget}"

                    return invalidArgs message
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

                        let memberMatchesQuery =
                            if exact then
                                String.Equals(m.DisplayName, query, StringComparison.Ordinal)
                                || fullNameBoundaryMatch m.FullName
                            else
                                (not (isNull m.DisplayName)
                                 && m.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                                || (not (isNull m.FullName)
                                    && m.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))

                        let declaringEntityMatchesQuery =
                            match m.DeclaringEntity with
                            | Some e -> entityMatchesQuery e
                            | None -> false

                        // `query` is documented as a symbol, TYPE, or MEMBER name.
                        // Keep the original type-qualified form
                        //   query="GrainContract", member="Resolve"
                        // and also accept the documented member-name form
                        //   query="Resolve", member="Resolve".
                        // The previous entity-only predicate made the latter return
                        // not_found even though FCS supplied all internal-member uses.
                        nameOk && (memberMatchesQuery || declaringEntityMatchesQuery)
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
                       FullName: string
                       /// #207: the field's type at this site, or null when the site is
                       /// not a field site, includeSiteTypes was off, or resolution
                       /// degraded (see TypeStatus).
                       SiteType: string
                       /// "typed" | "unresolved" | "timeout" | null (not applicable).
                       TypeStatus: string
                       /// #207 review: (type, project) pairs for every OTHER answer swept
                       /// projects gave this same physical site (a linked .fs compiled by
                       /// more than one .fsproj). Empty in the common single-project case.
                       TypeAlternatives: (string * string) list |}
                 >()

            let perProject = JsonArray()
            // Lockstep with perProject: false for a project that matched nothing and didn't
            // error, so the output can drop pure-noise entries (one per swept project on a
            // large solution) without losing the matched/errored ones (#100 token-tax).
            let perProjectKeep = ResizeArray<bool>()
            let aggregatedDiagnostics = ResizeArray<FSharpDiagnostic>()
            let sweepSw = System.Diagnostics.Stopwatch.StartNew()
            let mutable projectsAnalyzed = 0
            let mutable projectsFailed = 0
            let mutable projectsTimedOut = 0
            let mutable projectsBusy = 0
            let mutable projectsNotStarted = 0
            let mutable stopStartingProjects = false
            let projectsMissing = missingProjects.Count
            let projectsSwept = projectsToSweep.Count
            let projectsRequested = projectsSwept + projectsMissing
            let mutable targetDiscoveryMaterializationTimedOut = false

            let coverageFailureSummary () =
                let notStarted =
                    if projectsNotStarted = 0 then
                        ""
                    else
                        $", %d{projectsNotStarted} not started"

                if projectsMissing = 0 then
                    $"%d{projectsFailed} failed, %d{projectsTimedOut} timed out, %d{projectsBusy} busy%s{notStarted}"
                else
                    $"%d{projectsFailed} failed, %d{projectsMissing} missing, %d{projectsTimedOut} timed out, %d{projectsBusy} busy%s{notStarted}"

            let mutable missingProjectIndex = 0
            let mutable missingProjectRowsExpired = false

            while missingProjectIndex < missingProjects.Count && not missingProjectRowsExpired do
                findTargetDiscoveryBeforeStepOverride
                |> Option.iter (fun beforeStep -> beforeStep "missing-project-row" missingProjectIndex)

                if deadline.SemanticExpired then
                    deadline.MarkSemanticExpired()
                    missingProjectRowsExpired <- true
                else
                    let normalizedProject = normalizePath missingProjects[missingProjectIndex]

                    perProject.Add(
                        jobj
                            [ "project", jstr (Path.GetFileNameWithoutExtension normalizedProject)
                              "fsproj", jstr normalizedProject
                              "status", jstr "missing"
                              "errorKind", jstr "project_not_found"
                              "retryable", jbool false
                              "error", jstr $"Declared solution member does not exist: %s{normalizedProject}"
                              "elapsedMs", jint 0 ]
                        :> JsonNode
                    )

                    perProjectKeep.Add(true)
                    missingProjectIndex <- missingProjectIndex + 1

            if missingProjectRowsExpired then
                targetDiscoveryMaterializationTimedOut <- true
                stopStartingProjects <- true

            let locationKey (r: range) =
                $"%s{normalizePath r.FileName}:%d{r.StartLine}:%d{r.StartColumn}:%d{r.EndLine}:%d{r.EndColumn}"

            // Per-project sweep. Sequential by design: parallel Ionide.ProjInfo option
            // resolution races on MSBuild's *.nuget.g.props for sibling projects that
            // share a P2P reference; FCS also serializes ParseAndCheckProject internally.
            let mutable sweepProjectIndex = 0

            while
                sweepProjectIndex < projectsToSweep.Count
                && not stopStartingProjects
                && not deadline.SemanticExpired
                do
                let fsproj = projectsToSweep[sweepProjectIndex]
                let projSw = System.Diagnostics.Stopwatch.StartNew()
                let projDisplay = Path.GetFileNameWithoutExtension fsproj

                try
                    if stopStartingProjects || deadline.SemanticExpired then
                        stopStartingProjects <- true
                        raise FindProjectNotStartedException

                    ensureCanStart $"project-options resolution for '{projDisplay}'"

                    let optionsWaiter =
                        this.AcquireFsprojEntryWithinBudget(
                            fsproj,
                            Some remainingWorkerBudget
                        )

                    let! optionsEntry =
                        awaitRetainedSingleFlight
                            $"project-options resolution for '{projDisplay}'"
                            optionsWaiter

                    let options = optionsEntry.Options

                    // issue #131/#168 P1-07: memoize the whole-symbol-use enumeration by
                    // the shared content-addressed analysis snapshot. A cache HIT skips BOTH
                    // ParseAndCheckProject AND the ~3s GetAllUsesOfAllSymbols re-walk; a
                    // MISS (first sweep, any own-source edit, any rebuild of a referenced
                    // project/assembly [Codex P1], OR any source edit of a referenced F#
                    // project without a rebuild [Codex P2]) runs the identical original path,
                    // so results are never served stale. The cache-or-compute lives in its
                    // own method (ProjectSweepUses) so this outer state machine stays
                    // statically compilable under Release optimization (a nested task CE
                    // inside the loop trips FS3511).
                    ensureCanStart $"snapshot computation for '{projDisplay}'"

                    let snapshotWaiter =
                        acquireAnalysisSnapshotKeyActual options remainingWorkerBudget

                    let! usesKey =
                        awaitRetainedSingleFlight
                            $"snapshot computation for '{projDisplay}'"
                            snapshotWaiter

                    commitAnalysisSnapshotKey options usesKey
                    ensureCanStart $"FCS sweep for '{projDisplay}'"

                    let sweepOperation = this.ProjectSweepUsesActual(usesKey, options)

                    let! allUses, projDiagnostics =
                        awaitWithinDeadline $"FCS sweep for '{projDisplay}'" true sweepOperation

                    aggregatedDiagnostics.AddRange(projDiagnostics)

                    // Per-file record-field form classifier (literal vs with-update).
                    let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)

                    let formCache =
                        System.Collections.Generic.Dictionary<string, FieldSiteForms option>()

                    let classifyForms (filePath: string) =
                        match formCache.TryGetValue filePath with
                        | true, cached -> cached
                        | _ ->
                            let parsed =
                                try
                                    if File.Exists filePath then
                                        let st = SourceText.ofString (File.ReadAllText filePath)
                                        let p = checker.ParseFile(filePath, st, parsingOptions) |> Async.RunSynchronously
                                        Some(FieldFormClassifier.classifySites p.ParseTree)
                                    else
                                        None
                                with _ ->
                                    None

                            formCache[filePath] <- parsed
                            parsed

                    // #207: four-way site classification. `x.Field <- v` used to land in
                    // field-read, which is not merely imprecise — it labelled a WRITE as a
                    // read, and a field-type change edits the two differently. Record
                    // patterns (`| { Field = x } ->`) were likewise indistinguishable from
                    // an expression read. Both now have their own kind.
                    let fieldKind (symbolUse: FSharpSymbolUse) =
                        let r = symbolUse.Range

                        match classifyForms (normalizePath r.FileName) with
                        | Some forms ->
                            match forms.ByStart.TryGetValue((r.StartLine, r.StartColumn)) with
                            | true, FieldSiteForm.Update -> "field-set-update"
                            | true, FieldSiteForm.Literal -> "field-set-literal"
                            | true, FieldSiteForm.Mutation -> "field-set-mutation"
                            | true, FieldSiteForm.Pattern -> "field-pattern"
                            | _ ->
                                // SynExpr.DotSet mutations only line up on the END position
                                // (see FieldSiteForms.MutationEnds).
                                if forms.MutationEnds.Contains((r.EndLine, r.EndColumn)) then
                                    "field-set-mutation"
                                else
                                    "field-read"
                        | None -> "field-read"

                    let mutable nameCount = 0
                    let mutable fieldCount = 0
                    let mutable memberCount = 0

                    // #207: type resolution shares `find`'s ONE wall-clock budget. Past the
                    // deadline a site is recorded untyped (status "timeout") instead of
                    // stretching the sweep — the caller still gets every site, just without
                    // the extra column on the tail of a very large sweep.
                    let siteTypeDeadlineExpired () =
                        match findSiteTypeDeadlineExpiredOverride with
                        | Some hook -> hook ()
                        | None -> deadline.SemanticExpired

                    let resolveSiteType (symbolUse: FSharpSymbolUse) =
                        if not includeSiteTypes then
                            null, null
                        else
                            FieldSiteTypes.outcome (siteTypeDeadlineExpired ()) (fun () ->
                                tryFormatFieldSiteType symbolUse)

                    let add (symbolUse: FSharpSymbolUse) (siteKind: string) (overwrite: bool) (siteType: string) (typeStatus: string) =
                        let r = symbolUse.Range
                        let normalizedFile = normalizePath r.FileName

                        let withinRequestedFile =
                            match fileScopePath with
                            | Some requestedFile ->
                                String.Equals(normalizedFile, requestedFile, sourcePathComparison)
                            | None -> true

                        if withinRequestedFile then
                            let key = locationKey r

                            if overwrite || not (siteByKey.ContainsKey key) then
                                // #207 review: kind precedence still lets a later pass overwrite the
                                // row, but a TYPE another project already resolved for this same
                                // physical site is never silently replaced — a linked .fs swept by
                                // several projects can resolve the field differently in each. The
                                // first resolved type keeps the `siteType` column AND the project
                                // label that produced it (otherwise the row would report one
                                // project's name beside another project's type); the rest are kept
                                // as alternatives. Without includeSiteTypes every SiteType is null,
                                // so this reduces exactly to the previous last-writer-wins behaviour.
                                // `typeStatus` is non-null ONLY on a field-pass row under
                                // includeSiteTypes, so the merge is confined to field-vs-field
                                // across projects. A member pass overwriting the same range still
                                // nulls the type exactly as before — otherwise a member-usage row
                                // could carry a type and break `typed + degraded = fieldSites`.
                                let keptType, keptStatus, keptProject, alternatives =
                                    match siteByKey.TryGetValue key with
                                    | true, prior when not (isNull prior.SiteType) && not (isNull typeStatus) ->
                                        prior.SiteType,
                                        prior.TypeStatus,
                                        prior.Project,
                                        FieldSiteTypes.mergeAlternatives
                                            prior.SiteType
                                            siteType
                                            projDisplay
                                            prior.TypeAlternatives
                                    | _ -> siteType, typeStatus, projDisplay, []

                                siteByKey[key] <-
                                    {| File = normalizedFile
                                       StartLine = r.StartLine
                                       StartCol = r.StartColumn
                                       EndLine = r.EndLine
                                       EndCol = r.EndColumn
                                       Kind = siteKind
                                       Project = keptProject
                                       FullName =
                                        (match symbolUse.Symbol.FullName with
                                         | null -> null
                                         | s -> s)
                                       SiteType = keptType
                                       TypeStatus = keptStatus
                                       TypeAlternatives = alternatives |}

                    // Field/member sites first so their richer kind wins over a generic
                    // "reference" tag when a non-exact name-match overlaps the same range.
                    let mutable classificationExpired = false

                    let canClassifyNext () =
                        if classificationExpired || deadline.SemanticExpired then
                            classificationExpired <- true
                            false
                        else
                            true

                    if wantField then
                        let mutable fieldIndex = 0

                        while fieldIndex < allUses.Length && canClassifyNext () do
                            let u = allUses[fieldIndex]

                            if not u.IsFromDefinition && isQueriedField u then
                                fieldCount <- fieldCount + 1
                                let siteType, typeStatus = resolveSiteType u
                                add u (fieldKind u) true siteType typeStatus

                            fieldIndex <- fieldIndex + 1

                    if wantMember then
                        let mutable memberIndex = 0

                        while memberIndex < allUses.Length && canClassifyNext () do
                            let u = allUses[memberIndex]

                            if not u.IsFromDefinition && isQueriedMember u then
                                memberCount <- memberCount + 1
                                add u "member-usage" true null null

                            memberIndex <- memberIndex + 1

                    if wantName then
                        let mutable nameIndex = 0

                        while nameIndex < allUses.Length && canClassifyNext () do
                            let u = allUses[nameIndex]

                            if symbolMatches query exact u.Symbol then
                                let passDefsOnly = (not wantDefsOnly) || u.IsFromDefinition
                                let passDecl = wantDefsOnly || includeDeclaration || not u.IsFromDefinition

                                if passDefsOnly && passDecl then
                                    nameCount <- nameCount + 1
                                    let k = if u.IsFromDefinition then "definition" else "reference"
                                    add u k false null null

                            nameIndex <- nameIndex + 1

                    if classificationExpired then
                        deadline.MarkSemanticExpired()

                        raise (
                            TimeoutException(
                                $"The find deadline was exhausted while classifying FCS sites for '{projDisplay}'."
                            )
                        )

                    projSw.Stop()
                    projectsAnalyzed <- projectsAnalyzed + 1

                    perProject.Add(
                        jobj
                            [ "project", jstr projDisplay
                              "fsproj", jstr (normalizePath fsproj)
                              "status", jstr "analyzed"
                              "totalProjectSymbolUses", jint allUses.Length
                              "nameMatchUses", jint nameCount
                              "fieldMatchUses", jint fieldCount
                              "memberMatchUses", jint memberCount
                              "elapsedMs", jint (int projSw.ElapsedMilliseconds) ]
                        :> JsonNode
                    )

                    perProjectKeep.Add(nameCount > 0 || fieldCount > 0 || memberCount > 0)
                with
                | FindProjectNotStartedException ->
                    projSw.Stop()
                    projectsNotStarted <- projectsNotStarted + 1
                    stopStartingProjects <- true

                    perProject.Add(
                        jobj
                            [ "project", jstr projDisplay
                              "fsproj", jstr (normalizePath fsproj)
                              "status", jstr "not_started"
                              "errorKind", jstr "deadline_not_started"
                              "retryable", jbool true
                              "error",
                              jstr
                                  "The end-to-end find deadline expired before this project started."
                              "elapsedMs", jint 0 ]
                        :> JsonNode
                    )

                    perProjectKeep.Add(true)
                | ex ->
                    projSw.Stop()
                    cancellationToken.ThrowIfCancellationRequested()

                    let timedOut = findFailureIsTimeout ex
                    let busy = not timedOut && boundedCheckWorkFailureIsBusy ex

                    let typedFailureFields =
                        match ex with
                        | SdkPreflight.SdkPinUnsatisfiable failure ->
                                            [ ("blockingReason",
                                               SdkPreflight.toBlockingReason failure.Pin failure.InstalledSdks) ]
                        | _ -> []

                    if timedOut then
                        projectsTimedOut <- projectsTimedOut + 1
                        stopStartingProjects <- deadline.SemanticExpired
                    elif busy then
                        projectsBusy <- projectsBusy + 1
                    else
                        projectsFailed <- projectsFailed + 1

                    perProject.Add(
                        jobj (
                            [ "project", jstr projDisplay
                              "fsproj", jstr (normalizePath fsproj)
                              "status",
                              jstr (
                                  if timedOut then "timed_out"
                                  elif busy then "busy"
                                  else "failed"
                              )
                              "errorKind",
                              // #192: a per-project entry cannot become the whole
                              // response envelope, but it can still name the real
                              // cause instead of a generic project_failure.
                              jstr (
                                  if timedOut then
                                      "timeout"
                                  elif busy then
                                      "fcs_worker_busy"
                                  else
                                      match ex with
                                      | SdkPreflight.SdkPinUnsatisfiable _ -> "sdk_not_found"
                                      | _ -> "project_failure"
                              )
                              "retryable", jbool (timedOut || busy)
                              "error", jstr ex.Message
                              "elapsedMs", jint (int projSw.ElapsedMilliseconds) ]
                            @ typedFailureFields
                        )
                        :> JsonNode
                    )

                    perProjectKeep.Add(true)

                sweepProjectIndex <- sweepProjectIndex + 1

            if sweepProjectIndex < projectsToSweep.Count then
                projectsNotStarted <-
                    projectsNotStarted + (projectsToSweep.Count - sweepProjectIndex)

            sweepSw.Stop()

            // Declared coverage and actual semantic sweep breadth are intentionally
            // separate. Missing solution members count as requested evidence, but they
            // never enter the FCS loop and therefore must not inflate projectsSwept.
            let coverageComplete =
                projectsAnalyzed = projectsRequested
                && projectsFailed = 0
                && projectsMissing = 0
                && projectsTimedOut = 0
                && projectsBusy = 0
                && projectsNotStarted = 0

            // HEADLINE: a positive site is always useful, but absence is conclusive
            // only when every requested FCS project completed. FSAC outcomes remain
            // typed so not-ready/mismatch/failure cannot masquerade as zero hits.
            // scope=file sites are filtered while they enter siteByKey, so determining
            // whether the semantic sweep hit anything is O(1) and happens before the
            // response-construction allowance begins. No post-sweep materialization is
            // allowed to hide before that boundary.
            let totalSites = siteByKey.Count
            let fcsMatched = totalSites > 0

            let mutable fsacFallbackTimedOut = false
            let mutable fsacFallbackNotStarted = false

            let! fsacProbeResult =
                task {
                    if fcsMatched then
                        return FindFsacProbeResult.Unavailable "not_needed"
                    elif targetDiscoveryMaterializationTimedOut then
                        fsacFallbackNotStarted <- true

                        return
                            FindFsacProbeResult.Unavailable
                                "The find deadline expired while materializing discovered projects; the zero-hit FSAC fallback was not started."
                    elif deadline.SemanticExpired then
                        fsacFallbackTimedOut <- true

                        return
                            FindFsacProbeResult.Unavailable
                                "The end-to-end find deadline expired before the zero-hit FSAC fallback started."
                    else
                        match fsacProbe with
                        | Some probe ->
                            try
                                ensureCanStart "the zero-hit FSAC fallback"
                                let operation = probe query
                                return! awaitWithinDeadline "the zero-hit FSAC fallback" false operation
                            with
                            | :? TimeoutException as ex ->
                                fsacFallbackTimedOut <- true
                                return FindFsacProbeResult.Unavailable ex.Message
                            | ex ->
                                cancellationToken.ThrowIfCancellationRequested()
                                return FindFsacProbeResult.Failed ex.Message
                        | None ->
                            return FindFsacProbeResult.Unavailable "No FSAC probe was supplied."
                }

            // Semantic work ends with the zero-hit fallback. Everything below is response
            // shaping and shares one fixed allowance, beginning before even the fallback
            // result is normalized. A test-only asynchronous seam can deterministically
            // occupy that allowance; production construction remains synchronous and
            // checks the monotonic response deadline between bounded rows/steps.
            deadline.BeginResponseConstruction()
            let mutable responseConstructionTimedOut = deadline.ResponseExpired

            match findResponseConstructionBeforeStartOverride with
            | Some beforeStart when not responseConstructionTimedOut ->
                let operation = beforeStart ()
                observeFault operation
                let remaining = deadline.RemainingResponseBudget()

                if remaining <= TimeSpan.Zero then
                    responseConstructionTimedOut <- true
                else
                    use responseWaitCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                    let contenders =
                        match deadline.ResponseExpirySignal with
                        | Some signal -> [| operation; Task.Delay(remaining, responseWaitCancellation.Token); signal |]
                        | None -> [| operation; Task.Delay(remaining, responseWaitCancellation.Token) |]

                    let! winner =
                        Task.WhenAny(contenders)

                    responseWaitCancellation.Cancel()

                    if Object.ReferenceEquals(winner, operation) then
                        do! operation
                        responseConstructionTimedOut <- deadline.ResponseExpired
                    else
                        cancellationToken.ThrowIfCancellationRequested()
                        responseConstructionTimedOut <- true
            | _ -> ()

            let responseStep phase index =
                cancellationToken.ThrowIfCancellationRequested()

                findResponseConstructionBeforeStepOverride
                |> Option.iter (fun beforeStep -> beforeStep phase index)

                if deadline.ResponseExpired then
                    responseConstructionTimedOut <- true
                    false
                else
                    true

            let fsacHits, fsacFallbackState, fsacFallbackReason =
                match fsacProbeResult with
                | FindFsacProbeResult.Available hits when hits >= 0 -> hits, "available", None
                | FindFsacProbeResult.Available _ ->
                    0, "failed", Some "FSAC returned a negative workspace-symbol hit count."
                | FindFsacProbeResult.NotReady reason -> 0, "not_ready", Some reason
                | FindFsacProbeResult.ContextMismatch reason -> 0, "context_mismatch", Some reason
                | FindFsacProbeResult.Failed reason -> 0, "failed", Some reason
                | _ when fsacFallbackTimedOut ->
                    0,
                    "timed_out",
                    Some "The zero-hit FSAC fallback did not complete inside find's shared deadline."
                | _ when fsacFallbackNotStarted ->
                    0,
                    "not_started",
                    Some "The zero-hit FSAC fallback was not started after target discovery expired."
                | FindFsacProbeResult.Unavailable "not_needed" -> 0, "not_needed", None
                | FindFsacProbeResult.Unavailable reason -> 0, "unavailable", Some reason

            let matched: bool option =
                if fcsMatched || fsacHits > 0 then
                    Some true
                elif coverageComplete && not fsacFallbackTimedOut then
                    Some false
                else
                    None

            let outcome =
                match matched with
                | Some true -> "matched"
                | Some false -> "not_found"
                | None -> "indeterminate"

            let responseStatus =
                if coverageComplete && not fsacFallbackTimedOut then
                    "succeeded"
                elif matched = Some true then
                    "partial"
                else
                    "unknown"

            let via =
                if fcsMatched then "fcs-multiproject-sweep"
                elif fsacHits > 0 then "fsac-symbol-index"
                elif targetDiscoveryMaterializationTimedOut then "incomplete-target-discovery"
                elif fsacFallbackTimedOut then "incomplete-fsac-fallback"
                elif coverageComplete then "none"
                else "incomplete-fcs-sweep"

            let completenessMessage =
                let failureSummary = coverageFailureSummary ()

                match responseStatus with
                | "partial" ->
                    Some
                        $"Matches were found, but only %d{projectsAnalyzed}/%d{projectsRequested} requested project(s) were analyzed; the result set may be incomplete."
                | "unknown" ->
                    if targetDiscoveryMaterializationTimedOut then
                        Some
                            "Cannot confirm absence: the find deadline expired while materializing discovered projects, before the FCS sweep started."
                    elif fsacFallbackTimedOut then
                        Some
                            "Cannot confirm absence: the FCS sweep returned zero sites, but the zero-hit FSAC fallback did not complete inside the shared deadline."
                    else
                        Some
                            $"Cannot confirm absence: only %d{projectsAnalyzed}/%d{projectsRequested} requested project(s) were analyzed (%s{failureSummary})."
                | _ -> None

            // Response shaping begins at the boundary above. Build the ordered index and
            // all counters in ONE pass, checking the response deadline before every bounded
            // dictionary/ordered-set step. This replaces materialize + sort + eleven full
            // array scans that previously ran before BeginResponseConstruction.
            let siteKeyComparer =
                System.Collections.Generic.Comparer<
                    struct (string * int * int * int * int * string)
                 >.Create(fun
                              (struct (leftFile, leftStartLine, leftStartCol, leftEndLine, leftEndCol, leftKind))
                              (struct (rightFile, rightStartLine, rightStartCol, rightEndLine, rightEndCol, rightKind)) ->
                    let byFile = StringComparer.Ordinal.Compare(leftFile, rightFile)

                    if byFile <> 0 then
                        byFile
                    else
                        let byStartLine = compare leftStartLine rightStartLine

                        if byStartLine <> 0 then
                            byStartLine
                        else
                            let byStartCol = compare leftStartCol rightStartCol

                            if byStartCol <> 0 then
                                byStartCol
                            else
                                let byEndLine = compare leftEndLine rightEndLine

                                if byEndLine <> 0 then
                                    byEndLine
                                else
                                    let byEndCol = compare leftEndCol rightEndCol

                                    if byEndCol <> 0 then
                                        byEndCol
                                    else
                                        StringComparer.Ordinal.Compare(leftKind, rightKind))

            let sortedSiteIndex =
                System.Collections.Generic.SortedDictionary<
                    struct (string * int * int * int * int * string),
                    _
                 >(siteKeyComparer)

            let mutable defCount = 0
            let mutable refCount = 0
            let mutable fLit = 0
            let mutable fUpd = 0
            let mutable fMut = 0
            let mutable fPat = 0
            let mutable fRead = 0
            let mutable memCount = 0
            let mutable typedSites = 0
            let mutable unresolvedSites = 0
            let mutable timedOutTypeSites = 0
            let mutable multiTypedSites = 0

            if not responseConstructionTimedOut then
                use sites = (siteByKey.Values :> seq<_>).GetEnumerator()
                let mutable shapingIndex = 0
                let mutable keepShaping = true

                while keepShaping && not responseConstructionTimedOut do
                    if not (responseStep "post-sweep-shaping" shapingIndex) then
                        keepShaping <- false
                    elif sites.MoveNext() then
                        let site = sites.Current

                        sortedSiteIndex.Add(
                            struct (
                                site.File,
                                site.StartLine,
                                site.StartCol,
                                site.EndLine,
                                site.EndCol,
                                site.Kind
                            ),
                            site
                        )

                        match site.Kind with
                        | "definition" -> defCount <- defCount + 1
                        | "reference" -> refCount <- refCount + 1
                        | "field-set-literal" -> fLit <- fLit + 1
                        | "field-set-update" -> fUpd <- fUpd + 1
                        | "field-set-mutation" -> fMut <- fMut + 1
                        | "field-pattern" -> fPat <- fPat + 1
                        | "field-read" -> fRead <- fRead + 1
                        | "member-usage" -> memCount <- memCount + 1
                        | _ -> ()

                        match site.TypeStatus with
                        | status when String.Equals(status, FieldSiteTypes.Typed, StringComparison.Ordinal) ->
                            typedSites <- typedSites + 1
                        | status when String.Equals(status, FieldSiteTypes.Unresolved, StringComparison.Ordinal) ->
                            unresolvedSites <- unresolvedSites + 1
                        | status when String.Equals(status, FieldSiteTypes.TimedOut, StringComparison.Ordinal) ->
                            timedOutTypeSites <- timedOutTypeSites + 1
                        | _ -> ()

                        if not (List.isEmpty site.TypeAlternatives) then
                            multiTypedSites <- multiTypedSites + 1

                        shapingIndex <- shapingIndex + 1
                    else
                        keepShaping <- false

            // Counts already accumulated remain useful lower bounds. Capture their
            // completeness here: later context/planner expiry cannot undo a full count.
            let breakdownComplete = not responseConstructionTimedOut

            if responseConstructionTimedOut then
                sortedSiteIndex.Clear()

            // #207: these counts cover the full scoped result, not just this cursor page.
            let fieldSiteCount = fLit + fUpd + fMut + fPat + fRead
            let degradedSites = unresolvedSites + timedOutTypeSites

            let requestedPageSites = ResizeArray<_>()

            if not responseConstructionTimedOut then
                use orderedSites = (sortedSiteIndex.Values :> seq<_>).GetEnumerator()
                let mutable sortedIndex = 0
                let mutable keepSelecting = true

                while
                    keepSelecting
                    && requestedPageSites.Count < pageSize
                    && not responseConstructionTimedOut
                    do
                    if not (responseStep "page-selection" sortedIndex) then
                        keepSelecting <- false
                    elif orderedSites.MoveNext() then
                        if sortedIndex >= pageOffset then
                            requestedPageSites.Add(orderedSites.Current)

                        sortedIndex <- sortedIndex + 1
                    else
                        keepSelecting <- false

            if responseConstructionTimedOut then
                requestedPageSites.Clear()

            // Union this page's source windows before reading. Reopening a file for each
            // site would rescan every earlier line O(sites * file length) and consume the
            // bounded response allowance on large files. Each file is streamed once;
            // only requested lines survive, and each site's own columns shape its snippet.
            let appliedContextLines = min (max 0 contextLines) FindResponseBudget.MaxContextLines
            let requestedSourceLines =
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.SortedSet<int>>(StringComparer.Ordinal)

            let mutable sourceWindowIndex = 0

            while sourceWindowIndex < requestedPageSites.Count && not responseConstructionTimedOut do
                if responseStep "line-context-selection" sourceWindowIndex then
                    let site = requestedPageSites[sourceWindowIndex]
                    let filePath = normalizePath site.File
                    let selected =
                        match requestedSourceLines.TryGetValue(filePath) with
                        | true, lines -> lines
                        | false, _ ->
                            let lines = System.Collections.Generic.SortedSet<int>()
                            requestedSourceLines.Add(filePath, lines)
                            lines

                    let targetLine = max 1 site.StartLine

                    for lineNumber in max 1 (targetLine - appliedContextLines) .. targetLine + appliedContextLines do
                        selected.Add(lineNumber) |> ignore

                    sourceWindowIndex <- sourceWindowIndex + 1

            let lineContextCache =
                System.Collections.Generic.Dictionary<
                    string,
                    System.Collections.Generic.IReadOnlyDictionary<int, string>
                 >(StringComparer.Ordinal)

            use sourceFiles = (requestedSourceLines :> seq<_>).GetEnumerator()
            let mutable sourceFileIndex = 0

            while not responseConstructionTimedOut && sourceFiles.MoveNext() do
                if responseStep "line-context-file" sourceFileIndex then
                    let filePath = sourceFiles.Current.Key
                    let requested = sourceFiles.Current.Value
                    let lastLine = requested.Max
                    let selected = System.Collections.Generic.Dictionary<int, string>()

                    if File.Exists filePath then
                        use lines = File.ReadLines(filePath).GetEnumerator()
                        let mutable lineNumber = 1
                        let mutable reachedEnd = false

                        while lineNumber <= lastLine && not reachedEnd && not responseConstructionTimedOut do
                            if responseStep "line-context" lineNumber then
                                if lines.MoveNext() then
                                    if requested.Contains(lineNumber) then
                                        selected.Add(lineNumber, lines.Current)

                                    lineNumber <- lineNumber + 1
                                else
                                    reachedEnd <- true

                    lineContextCache.Add(filePath, selected)
                    sourceFileIndex <- sourceFileIndex + 1

            let tryLineContextToJson filePath startLine startColumn endLine endColumn =
                if not (responseStep "line-context-json" startLine) then
                    None
                else
                    match lineContextCache.TryGetValue(normalizePath filePath) with
                    | true, lines ->
                        let context = findLineContextToJson lines contextLines startLine startColumn endLine endColumn

                        if responseStep "line-context-json-complete" startLine then
                            Some context
                        else
                            None
                    | false, _ -> None

            // Canonical row projection: every field of one site is derived only from that site
            // and the request's content options. In particular, alternatives use deterministic
            // per-site caps rather than a shared page allowance, so maxResults, cursor position,
            // and the final response-budget boundary cannot reshape a row.
            let siteToJson (s: {| File: string
                                  StartLine: int
                                  StartCol: int
                                  EndLine: int
                                  EndCol: int
                                  Kind: string
                                  Project: string
                                  FullName: string
                                  SiteType: string
                                  TypeStatus: string
                                  TypeAlternatives: (string * string) list |})
                (ctx: JsonNode)
                =
                let rangeNode =
                    jobj
                        [ "startLine", jint s.StartLine
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
                        [ "contextLinesRequested", ctx["contextLinesRequested"].DeepClone()
                          "contextLinesApplied", ctx["contextLinesApplied"].DeepClone()
                          "contextLinesTruncated", ctx["contextLinesTruncated"].DeepClone()
                          "before", ctx["before"].DeepClone()
                          "after", ctx["after"].DeepClone() ]
                    else
                        []

                // #207: see FieldSiteTypes.rowFields — extracted so its degraded (JSON null)
                // arm is unit-testable, which inline here it was not.
                let alternativeFields = FieldSiteTypes.alternativesFields includeSiteTypes s.TypeAlternatives

                let alternativesWereTruncated =
                    includeSiteTypes
                    && FieldSiteTypes.alternativesTruncated s.TypeAlternatives

                let siteTypeFields =
                    FieldSiteTypes.rowFields includeSiteTypes s.SiteType s.TypeStatus
                    @ alternativeFields

                let node =
                    jobj
                        ([ "file", jstr s.File
                           "range", rangeNode
                           "kind", jstr s.Kind
                           "project", jstr s.Project
                           "symbolFullName",
                           (match s.FullName with
                            | null -> null
                            | v -> jstr v)
                           "lineText", ctx["lineText"].DeepClone()
                           "lineTextSourceStartColumn", ctx["lineTextSourceStartColumn"].DeepClone()
                           "lineTextSourceEndColumn", ctx["lineTextSourceEndColumn"].DeepClone()
                           "lineTextSourceLength", ctx["lineTextSourceLength"].DeepClone()
                           "lineTextTruncated", ctx["lineTextTruncated"].DeepClone() ]
                         @ siteTypeFields
                         @ contextFields)
                    :> JsonNode

                node, alternativesWereTruncated

            // One self-describing representation per site: each node carries
            // file / range / kind / project / symbolFullName / lineText, so an agent
            // filters the flat list by `kind` and reads `breakdown` for per-kind
            // counts. The grouped definitions/references/fieldSites/memberSites buckets
            // were dropped — they re-emitted every site a second time and doubled the
            // payload (the reason the default page cap had been forced down to 40).
            let siteNodeBuffer = ResizeArray<JsonNode>()
            let alternativesTruncatedBuffer = ResizeArray<bool>()
            let mutable responseSiteIndex = 0

            while responseSiteIndex < requestedPageSites.Count && not responseConstructionTimedOut do
                if not (responseStep "site-json" responseSiteIndex) then
                    responseConstructionTimedOut <- true
                else
                    let site = requestedPageSites[responseSiteIndex]

                    match tryLineContextToJson site.File site.StartLine site.StartCol site.EndLine site.EndCol with
                    | None -> responseConstructionTimedOut <- true
                    | Some context ->
                        let node, alternativesTruncated = siteToJson site context

                        if responseStep "site-json-complete" responseSiteIndex then
                            siteNodeBuffer.Add(node)
                            alternativesTruncatedBuffer.Add(alternativesTruncated)
                            responseSiteIndex <- responseSiteIndex + 1

            if deadline.ResponseExpired then
                responseConstructionTimedOut <- true

            let candidatePageSites = requestedPageSites.ToArray()
            let siteNodes = siteNodeBuffer.ToArray()
            let alternativesTruncatedBySite = alternativesTruncatedBuffer.ToArray()

            let mutable resolutionComplete =
                coverageComplete
                && not fsacFallbackTimedOut
                && not responseConstructionTimedOut
                && pageOffset = 0
                && siteNodes.Length = totalSites

            // Count every eligible raw diagnostic within the deadline, retaining at most
            // 200 rows before JSON projection. A timed-out count is explicitly incomplete.
            let diagnosticPrefix = ResizeArray<FSharpDiagnostic>(200)
            let mutable projectDiagnosticsTotalCount = 0
            let mutable diagnosticIndex = 0

            while diagnosticIndex < aggregatedDiagnostics.Count && not responseConstructionTimedOut do
                if not (responseStep "diagnostic-collection" diagnosticIndex) then
                    responseConstructionTimedOut <- true
                else
                    let diagnostic = aggregatedDiagnostics[diagnosticIndex]

                    if
                        diagnostic.Severity = FSharpDiagnosticSeverity.Error
                        || (includeInfo
                            && (diagnostic.Severity = FSharpDiagnosticSeverity.Warning
                                || diagnostic.Severity = FSharpDiagnosticSeverity.Hidden
                                || diagnostic.Severity = FSharpDiagnosticSeverity.Info))
                    then
                        projectDiagnosticsTotalCount <- projectDiagnosticsTotalCount + 1

                        if diagnosticPrefix.Count < 200 then
                            diagnosticPrefix.Add(diagnostic)

                    diagnosticIndex <- diagnosticIndex + 1

            let ensureResponseStep phase index =
                if not (responseStep phase index) then
                    raise (TimeoutException("The find response-construction deadline expired."))

            let initialDiagNodes =
                if responseConstructionTimedOut then
                    [||]
                else
                    try
                        let mutable jsonIndex = 0
                        let mapDiagnostic diagnostic =
                            ensureResponseStep "diagnostic-json" jsonIndex
                            jsonIndex <- jsonIndex + 1
                            diagnosticToJson diagnostic

                        let _, nodes =
                            FindResponseBudget.materializeCappedPrefix
                                200
                                mapDiagnostic
                                (diagnosticPrefix.ToArray())

                        nodes
                    with :? TimeoutException ->
                        responseConstructionTimedOut <- true
                        [||]

            // Apply metadata filtering before freezing the phase ledger. Cloning happens
            // only for the bounded prefixes selected by the response planner below.
            let isErrorEntry (node: JsonNode) =
                match node with
                | :? JsonObject as o -> o.ContainsKey("error")
                | _ -> false

            let retainedPerProject = ResizeArray<JsonNode>()
            let mutable perProjectIndex = 0

            while perProjectIndex < perProject.Count && not responseConstructionTimedOut do
                if not (responseStep "per-project" perProjectIndex) then
                    responseConstructionTimedOut <- true
                else
                    let node = perProject[perProjectIndex]
                    let keep = if includePerProject then perProjectKeep[perProjectIndex] else isErrorEntry node

                    if keep then
                        retainedPerProject.Add(node)

                    perProjectIndex <- perProjectIndex + 1

            if deadline.ResponseExpired then
                responseConstructionTimedOut <- true
                resolutionComplete <- false

            let scopeResolved =
                if projectsSwept <= 1 then
                    if scope = "file" then "file" else "project"
                else
                    "workspace"

            let matchedNode =
                match matched with
                | Some value -> jbool value
                | None -> null

            let coverage =
                let projectSweepStatus =
                    if targetDiscoveryMaterializationTimedOut then
                        "not_started"
                    elif coverageComplete then
                        "complete"
                    elif projectsTimedOut > 0 && deadline.SemanticExpired then
                        "timed_out"
                    else
                        "partial"

                let fsacFallbackPhaseStatus =
                    if fcsMatched then
                        "not_needed"
                    elif fsacFallbackNotStarted then
                        "not_started"
                    elif fsacFallbackTimedOut then
                        "timed_out"
                    else
                        "complete"

                let phases =
                    JsonArray(
                        [| jobj [ "phase", jstr "admission"; "status", jstr "complete" ] :> JsonNode
                           jobj
                               [ "phase", jstr "position_resolution"
                                 "status", jstr (if kind = "position" then "complete" else "not_needed") ]
                           :> JsonNode
                           jobj
                               [ "phase", jstr "target_discovery"
                                 "status",
                                 jstr (
                                     if targetDiscoveryMaterializationTimedOut then
                                         "timed_out"
                                     else
                                         "complete"
                                 ) ]
                           :> JsonNode
                           jobj [ "phase", jstr "project_sweep"; "status", jstr projectSweepStatus ] :> JsonNode
                           jobj
                               [ "phase", jstr "fsac_fallback"
                                 "status", jstr fsacFallbackPhaseStatus ]
                           :> JsonNode
                           jobj
                               [ "phase", jstr "response_construction"
                                 "status",
                                 jstr (if responseConstructionTimedOut then "timed_out" else "complete") ]
                           :> JsonNode |]
                    )
                    :> JsonNode

                jobj
                    [ "complete", jbool coverageComplete
                      "projectsRequested", jint projectsRequested
                      "projectsAnalyzed", jint projectsAnalyzed
                      "projectsFailed", jint projectsFailed
                      "projectsMissing", jint projectsMissing
                      "projectsTimedOut", jint projectsTimedOut
                      "projectsBusy", jint projectsBusy
                      "projectsNotStarted", jint projectsNotStarted
                      "phases", phases ]
                :> JsonNode

            let resolution =
                jobj
                    [ "matched", matchedNode
                      "outcome", jstr outcome
                      // Finalized after the serialized-size planner chooses the delivered
                      // site prefix. Coverage and delivery completeness are independent.
                      "complete", jbool false
                      "kindResolved", jstr kindResolved
                      "scopeResolved", jstr scopeResolved
                      "projectsSwept", jint projectsSwept
                      "projectsRequested", jint projectsRequested
                      "projectsAnalyzed", jint projectsAnalyzed
                      "projectsFailed", jint projectsFailed
                      "projectsMissing", jint projectsMissing
                      "projectsTimedOut", jint projectsTimedOut
                      "projectsBusy", jint projectsBusy
                      "projectsNotStarted", jint projectsNotStarted
                      "via", jstr via
                      "fcsSiteCount", jint totalSites
                      "fsacFallbackHits", jint fsacHits
                      "fsacFallbackState", jstr fsacFallbackState
                      "fsacFallbackReason",
                      (fsacFallbackReason |> Option.map jstr |> Option.defaultValue null) ]
                :> JsonNode

            let breakdown =
                jobj
                    [ "definitions", jint defCount
                      "references", jint refCount
                      "fieldSetLiteral", jint fLit
                      "fieldSetUpdate", jint fUpd
                      // #207: `x.Field <- v` and `| { Field = x } ->` used to be counted as
                      // fieldRead. They are separate edit shapes, so they get their own keys
                      // — fieldRead now means "an expression that reads the field", nothing
                      // more. Additive: existing keys keep their meaning.
                      "fieldSetMutation", jint fMut
                      "fieldPattern", jint fPat
                      "fieldRead", jint fRead
                      "memberUsages", jint memCount ]
                :> JsonNode

            // #207: the honesty ledger for includeSiteTypes. `typed + degraded` must always
            // equal `fieldSites`, so a caller can tell "every site carries its type" from
            // "some rows are blank" without diffing the sites array.
            let siteTypesNote alternativesTruncatedRows =
                if fieldSiteCount = 0 && not coverageComplete then
                    let failureSummary = coverageFailureSummary ()

                    // Do not blame the caller's `kind` for an empty result the sweep
                    // never got far enough to produce — coverage is the real story.
                    $"No field site was typed because the sweep is incomplete: %d{projectsAnalyzed} of %d{projectsRequested} project(s) were analyzed (%s{failureSummary}). See coverage/message; restore missing projects, retry busy work, or re-run with a larger timeoutMs before reading anything into the empty result."
                elif fieldSiteCount = 0 then
                    // Advice must name a NEXT step the caller has not already taken.
                    // Telling a kind='auto'/'field' caller to "use kind='field' or
                    // kind='auto'" is circular, and a kind='position' caller sees
                    // kindResolved='symbol' (position folds into the symbol sweep),
                    // so each entry point gets the step that actually differs.
                    let recipe =
                        match kind, kindResolved with
                        | "position", _ ->
                            "kind='position' resolves the symbol under the cursor and then sweeps it as kind='symbol', which never unions field sites. Re-run with kind='field' and the resolved name as query (echoed above as `query`)."
                        | _, ("field" | "auto") ->
                            $"The query already unioned field sites — '%s{query}' simply matches no record field here. Check the declaring type name (find matches fields by their DECLARING type, not the field name), drop field='…' if it is over-restricting, or pass exact=false for a substring match on the type."
                        | _ ->
                            "Re-run with kind='field' (optionally with field='Name') or kind='auto', which union record-field sites; this kind does not."

                    $"includeSiteTypes annotates record-field sites only, and this sweep produced none (kindResolved='%s{kindResolved}'). %s{recipe}"
                else
                    let degradedClause =
                        if degradedSites = 0 then
                            "Every field site is typed."
                        else
                            $"%d{degradedSites} of %d{fieldSiteCount} field sites could not be typed (%d{unresolvedSites} unresolved, %d{timedOutTypeSites} past the timeoutMs budget) and carry siteType: null."

                    let multiClause =
                        if multiTypedSites = 0 then
                            ""
                        else
                            let truncationClause =
                                if alternativesTruncatedRows = 0 then
                                    ""
                                else
                                    $" On {alternativesTruncatedRows} delivered row(s) the column hit its deterministic per-site cap and carries siteTypeAlternativesOmitted and/or projectsOmitted counts instead of the full list — narrow with scope/projectPath to reduce the competing project interpretations."

                            $" {multiTypedSites} site(s) are compiled by more than one swept project and resolved to DIFFERENT types: siteType/project report the first project's answer and siteTypeAlternatives names each other type with the project(s) that resolved it — plan those sites per project, not from one type.{truncationClause}"

                    $"siteType is the field's type as the CURRENT typecheck resolves it at that site, rendered with that site's own opens — the BEFORE half of a field-type change. {degradedClause}{multiClause} find deliberately does NOT typecheck a hypothetical new record shape: edit every site listed here, then run check(scope='project') for the AFTER verdict. On a generic record the type shows as the type PARAMETER (e.g. 'T), not its instantiation at the site."

            let siteTypesFields alternativesTruncatedRows =
                if responseConstructionTimedOut || not includeSiteTypes then
                    []
                else
                    let summary =
                        jobj
                            [ "requested", jbool true
                              "fieldSites", jint fieldSiteCount
                              "typed", jint typedSites
                              "degraded", jint degradedSites
                              "degradedUnresolved", jint unresolvedSites
                              "degradedTimedOut", jint timedOutTypeSites
                              // Subset of `typed`; see multiTypedSites.
                              "typedDifferentlyByAnotherProject", jint multiTypedSites
                              // Rows on THIS page where the alternatives column hit its
                              // deterministic per-site cap — a truncation the caller can see,
                              // not infer. Page boundaries never reshape a site's alternatives.
                              "alternativesTruncatedRows", jint alternativesTruncatedRows ]
                        :> JsonNode

                    [ ("siteTypes", summary)
                      ("siteTypesNote", jstr (siteTypesNote alternativesTruncatedRows)) ]

            let perProjectNodes = retainedPerProject.ToArray()
            let emitPerProject = includePerProject || perProjectNodes.Length > 0

            // F1 (#100): a dotted query that resolved to nothing reads like "symbol absent".
            // symbolMatches now accepts a dotted suffix, so this only fires for a genuine miss
            // — point the caller at the bare identifier rather than leaving a silent empty.
            let hintField =
                if matched = Some false && not (String.IsNullOrEmpty query) && query.Contains('.') then
                    let bare = query.Substring(query.LastIndexOf('.') + 1)

                    [ ("hint",
                      jstr
                          $"No match for the module-qualified query '{query}'. find matches by simple name or a dotted suffix on a module boundary — try the bare identifier '{bare}'.") ]
                else
                    []

            let completenessFields =
                if responseConstructionTimedOut then
                    [ ("errorKind", jstr "find_response_timeout")
                      ("message",
                       jstr
                           "Semantic analysis completed, but find's reserved response-construction allowance expired; retry after narrowing maxResults/contextLines.") ]
                elif
                    targetDiscoveryMaterializationTimedOut
                    || projectsTimedOut > 0
                    || projectsNotStarted > 0
                    || fsacFallbackTimedOut
                then
                    [ ("errorKind", jstr "find_timeout")
                      ("message",
                       completenessMessage
                       |> Option.defaultValue
                           "The end-to-end find deadline expired before all semantic work completed."
                       |> jstr) ]
                else
                    completenessMessage
                    |> Option.map (fun message -> [ ("message", jstr message) ])
                    |> Option.defaultValue []

            // #193: a top-level (never buried in perProject) recall-vs-speed guardrail on
            // every response, naming the ACTUAL sweep outcome rather than the requested
            // `scope` string. Review of the first cut of this feature established that
            // SolutionParsing.listProjects on a bare .fsproj is already just [| itself |] at
            // BASE — so a mechanism that "narrows scope=auto when projectPath is an explicit
            // .fsproj" is unreachable dead code, and worse, an agent told to retry with
            // scope='workspace' + the SAME .fsproj projectPath would re-sweep the identical
            // one project and mistakenly believe it now had cross-project coverage. The only
            // thing that actually changes what gets swept is what sweepTarget resolves to
            // (a .fsproj vs. a .sln/.slnx/directory). Missing declared solution members are
            // coverage failures, not semantic sweeps, so this note uses projectsSwept for
            // breadth and projectsRequested/projectsMissing for completeness.
            //
            // Round 2 (post-re-review): the note must never overstate coverage. This same
            // code path is reached by `partial`/`unknown` responses too (e.g. the
            // timeoutMs=0 exhausted-budget path), where projectsAnalyzed < projectsRequested
            // — "find swept N" would be a lie about work that timed out or failed before it
            // ran. Below, `coverageComplete` (already computed for `resolution`/`coverage`)
            // gates between the confident "swept" wording and an honest "analyzed K of N"
            // wording. Separately, scope='file' has a SECOND blind spot beyond siblings:
            // the one project that IS swept has its sites additionally post-filtered to a
            // single file (see `allSites` above), so the single-project note names that too.
            let buildScopeNoteField () =
                let failureSummary = coverageFailureSummary ()
                let widenRecipe =
                    "To sweep the whole solution, pass its .sln/.slnx as projectPath (or set_project it) with scope='workspace'."

                let narrowRecipe =
                    "To narrow to just one project (faster, but misses cross-project usages), pass its .fsproj as projectPath."

                if projectsSwept = 0 then
                    [ ("scopeNote",
                       jstr
                           $"find swept no projects: %d{projectsMissing} declared solution member(s) are missing. This response is incomplete; restore those projects or pass a loadable .fsproj before trusting an absence of matches.") ]
                elif projectsSwept = 1 then
                    let filterPath =
                        if scope = "file" then
                            match args.path with
                            | Some p when not (String.IsNullOrWhiteSpace p) -> Some(normalizePath p)
                            | _ -> None
                        else
                            None

                    let text =
                        match coverageComplete, filterPath with
                        | true, None ->
                            $"find swept only this one project — cross-project usages in sibling projects are not visible. {widenRecipe}"
                        | true, Some path ->
                            $"find swept only this one project, and kept only sites in '{path}' — other files in this project, and all sibling projects, are not visible. {widenRecipe}"
                        | false, None when projectsAnalyzed = projectsSwept && projectsMissing > 0 ->
                            $"find swept only this one loadable project, but %d{projectsMissing} declared solution member(s) are missing and were not swept. This response is incomplete; restore them before trusting an absence of matches. Cross-project usages in other siblings are not visible. %s{widenRecipe}"
                        | false, None ->
                            $"find could not fully analyze this project (%s{failureSummary}) — this response is incomplete; see coverage/message before trusting an absence of matches. Cross-project usages in sibling projects are also not visible. %s{widenRecipe}"
                        | false, Some path ->
                            $"find could not fully analyze this project (%s{failureSummary}) — this response is incomplete; see coverage/message before trusting an absence of matches. Sites, where present, are also filtered to '%s{path}'; other files in this project, and all sibling projects, are not visible. %s{widenRecipe}"

                    [ ("scopeNote", jstr text) ]
                else
                    let text =
                        if coverageComplete then
                            $"find swept %d{projectsSwept} member projects of '%s{sweepTarget}'. %s{narrowRecipe}"
                        else
                            $"find analyzed %d{projectsAnalyzed} of %d{projectsRequested} member projects of '%s{sweepTarget}' (%s{failureSummary}) — this response is incomplete; see coverage/message before trusting an absence of matches. %s{narrowRecipe}"

                    [ ("scopeNote", jstr text) ]

            let scopeNoteField =
                if responseConstructionTimedOut then [] else buildScopeNoteField ()

            let finalResponseStatus =
                if responseConstructionTimedOut then
                    if matched = Some true then "partial" else "unknown"
                else
                    responseStatus

            let perProjectField =
                if emitPerProject then
                    [ ("perProject", JsonArray() :> JsonNode) ]
                else
                    []

            let paginationFields =
                Cursor.paginationFields "sites" totalSites pageOffset pageSize 0

            let baseFields =
                [ "status", jstr finalResponseStatus
                  "timeoutMs", jint deadline.TimeoutMs
                  "responseConstructionAllowanceMs", jint deadline.ResponseAllowanceMs
                  "elapsedMs", jint deadline.ElapsedMilliseconds
                  "outcome", jstr outcome
                  "query", jstr query
                  "kind", jstr kind
                  "kindResolved", jstr kindResolved
                  "scope", jstr scope
                  "exact", jbool exact
                  "resolution", resolution
                  "coverage", coverage
                  "projectsSwept", jint projectsSwept
                  "projectsRequested", jint projectsRequested
                  "projectsAnalyzed", jint projectsAnalyzed
                  "projectsFailed", jint projectsFailed
                  "projectsMissing", jint projectsMissing
                  "projectsTimedOut", jint projectsTimedOut
                  "projectsBusy", jint projectsBusy
                  "projectsNotStarted", jint projectsNotStarted
                  "totalSites", jint totalSites
                  "matchedUseCount", jint totalSites
                  "breakdownComplete", jbool breakdownComplete
                  "breakdown", breakdown
                  "returnedSiteCount", jint 0
                  "sitesTruncatedByBudget", jbool false
                  "sites", JsonArray() :> JsonNode
                  "resultSetComplete", jbool resolutionComplete ]
                @ completenessFields
                @ perProjectField
                @ [ "perProjectTotalCount", jint perProjectNodes.Length
                    "perProjectReturnedCount", jint 0
                    "perProjectTruncatedByBudget", jbool false
                    "sweepElapsedMs", jint (int sweepSw.ElapsedMilliseconds)
                    "projectDiagnosticsTotalCount", jint projectDiagnosticsTotalCount
                    "projectDiagnosticsCountComplete", jbool (diagnosticIndex = aggregatedDiagnostics.Count)
                    "projectDiagnosticsReturnedCount", jint 0
                    "projectDiagnosticsTruncated", jbool false
                    "projectDiagnosticsTruncatedByBudget", jbool false
                    "projectDiagnostics", JsonArray() :> JsonNode ]
                @ hintField
                @ siteTypesFields 0
                @ scopeNoteField
                @ [ "responseTruncatedByBudget", jbool false
                    "responseBudgetChars", jint findResponseBudgetChars
                    "responseSizeUnit", jstr FindResponseBudget.SizeUnit
                    "responseSizeHint", null
                    "cursorAdvancedBy", jint 0 ]

            let responseTemplate = jobj (baseFields @ paginationFields)

            let jsonArrayPrefix (nodes: JsonNode array) count =
                Array.init count (fun index ->
                    ensureResponseStep "response-json-copy" index
                    nodes[index].DeepClone())
                |> JsonArray
                :> JsonNode

            let mutable plannerProbe = 0

            let buildResponse deliveredSites deliveredDiagnostics deliveredProjects =
                ensureResponseStep "response-planning" plannerProbe
                plannerProbe <- plannerProbe + 1
                let response = responseTemplate.DeepClone() :?> JsonObject
                response["sites"] <- jsonArrayPrefix siteNodes deliveredSites
                response["projectDiagnostics"] <- jsonArrayPrefix initialDiagNodes deliveredDiagnostics

                if emitPerProject then
                    response["perProject"] <- jsonArrayPrefix perProjectNodes deliveredProjects

                let alternativesTruncatedRows =
                    alternativesTruncatedBySite
                    |> Array.take deliveredSites
                    |> Array.filter id
                    |> Array.length

                if includeSiteTypes then
                    let siteTypes = response["siteTypes"] :?> JsonObject
                    siteTypes["alternativesTruncatedRows"] <- jint alternativesTruncatedRows
                    response["siteTypesNote"] <- jstr (siteTypesNote alternativesTruncatedRows)

                // Project coverage and response delivery are separate dimensions. A
                // complete sweep can still deliver only a serialized-budget prefix.
                let resolutionComplete =
                    coverageComplete
                    && not fsacFallbackTimedOut
                    && pageOffset = 0
                    && deliveredSites = totalSites

                let responseResolution = response["resolution"] :?> JsonObject
                responseResolution["complete"] <- jbool resolutionComplete
                response["resultSetComplete"] <- jbool resolutionComplete

                let sitesTruncatedByBudget = deliveredSites < siteNodes.Length
                let diagnosticsTruncatedByBudget = deliveredDiagnostics < initialDiagNodes.Length
                let diagnosticsTruncated = deliveredDiagnostics < projectDiagnosticsTotalCount
                let perProjectTruncatedByBudget = deliveredProjects < perProjectNodes.Length

                let responseTruncatedByBudget =
                    sitesTruncatedByBudget
                    || diagnosticsTruncatedByBudget
                    || perProjectTruncatedByBudget

                response["returnedSiteCount"] <- jint deliveredSites
                response["sitesTruncatedByBudget"] <- jbool sitesTruncatedByBudget
                response["perProjectReturnedCount"] <- jint deliveredProjects
                response["perProjectTruncatedByBudget"] <- jbool perProjectTruncatedByBudget
                response["projectDiagnosticsReturnedCount"] <- jint deliveredDiagnostics
                response["projectDiagnosticsTruncated"] <- jbool diagnosticsTruncated
                response["projectDiagnosticsTruncatedByBudget"] <- jbool diagnosticsTruncatedByBudget
                response["responseTruncatedByBudget"] <- jbool responseTruncatedByBudget
                response["cursorAdvancedBy"] <- jint deliveredSites

                response["responseSizeHint"] <-
                    if responseTruncatedByBudget then
                        jstr
                            $"The complete production-serialized find response is capped at %d{findResponseBudgetChars} UTF-16 code units. This page delivered %d{deliveredSites} site(s), %d{deliveredDiagnostics} diagnostic(s), and %d{deliveredProjects} per-project row(s); full scalar coverage and match counts remain available. Follow nextCursor when present."
                    else
                        null

                let nextOffset = pageOffset + deliveredSites
                let truncated = nextOffset < totalSites
                response["truncated"] <- jbool truncated

                response["nextCursor"] <-
                    if truncated && deliveredSites > 0 then
                        jstr (Cursor.encode nextOffset)
                    else
                        null

                ensureResponseStep "response-planning-complete" plannerProbe
                response :> JsonNode

            let responseTimeout () =
                let response = responseTemplate.DeepClone() :?> JsonObject
                response["status"] <- jstr (if matched = Some true then "partial" else "unknown")
                response["errorKind"] <- jstr "find_response_timeout"
                response["message"] <-
                    jstr "The find response-construction deadline expired. Restart without a cursor after narrowing the request."
                response["retryable"] <- jbool true
                response["elapsedMs"] <- jint deadline.ElapsedMilliseconds
                response["resultSetComplete"] <- jbool false
                response["paginationRestartRequired"] <- jbool true
                response["truncated"] <- jbool true
                response["nextCursor"] <- null
                response["projectDiagnosticsTruncated"] <- jbool (projectDiagnosticsTotalCount > 0)
                let responseResolution = response["resolution"] :?> JsonObject
                responseResolution["complete"] <- jbool false
                let responseCoverage = response["coverage"]
                let phases = responseCoverage["phases"] :?> JsonArray

                for phase in phases do
                    if phase["phase"].GetValue<string>() = "response_construction" then
                        phase["status"] <- jstr "timed_out"

                response :> JsonNode

            let budgetFailure errorCode message =
                let failureMatchedNode =
                    match matched with
                    | Some value -> jbool value
                    | None -> null

                let failureResolution =
                    jobj
                        [ "matched", failureMatchedNode
                          "outcome", jstr outcome
                          "complete", jbool false
                          "kindResolved", jstr kindResolved
                          "scopeResolved", jstr scopeResolved
                          "projectsSwept", jint projectsSwept
                          "projectsRequested", jint projectsRequested
                          "projectsAnalyzed", jint projectsAnalyzed
                          "projectsFailed", jint projectsFailed
                          "projectsMissing", jint projectsMissing
                          "projectsTimedOut", jint projectsTimedOut
                          "projectsBusy", jint projectsBusy
                          "projectsNotStarted", jint projectsNotStarted
                          "via", jstr via
                          "fcsSiteCount", jint totalSites
                          "fsacFallbackHits", jint fsacHits
                          "fsacFallbackState", jstr fsacFallbackState
                          // A potentially unbounded infrastructure message is omitted from
                          // this last-resort envelope; its typed state remains available.
                          "fsacFallbackReason", null ]
                    :> JsonNode

                // The planner reaches this envelope only after a one-site response (or
                // fixed metadata alone) has already failed to fit. Reducing maxResults
                // therefore cannot change the serialized payload, so retaining a cursor
                // would only send the caller into a non-progressing retry loop.
                let canRetrySameCursor = false

                let sameCursorRetry =
                    jobj
                        [ "allowed", jbool canRetrySameCursor
                          "action", jstr "retry_same_cursor"
                          "requiresUnchangedResultIdentity", jbool true
                          "allowedChangedInputs", JsonArray([| jstr "maxResults" |]) :> JsonNode
                          "recommendedMaxResults", jint 1
                          "reuseOriginalCursor", jbool canRetrySameCursor ]
                    :> JsonNode

                let changedIdentityRetry =
                    jobj
                        [ "action", jstr "restart_without_cursor"
                          "requiredBeforeChangingResultIdentity", jbool true
                          "recommendedContextLines", jint 0
                          "recommendedIncludeInfo", jbool false
                          "recommendedIncludePerProject", jbool false
                          "recommendedMaxResults", jint 1
                          "reuseOriginalCursor", jbool false ]
                    :> JsonNode

                let recovery =
                    jobj
                        [ "action", jstr "restart_without_cursor"
                          "instruction",
                          jstr
                              "Reducing maxResults cannot make this blocked response fit because the planner already tested one site or fixed metadata alone. Restart without a cursor before reducing contextLines or metadata, or before changing query, kind, exact, member, field, scope, projectPath, path, line, character, word, occurrence, includeDeclaration, or includeSiteTypes."
                          "reuseOriginalCursor", jbool false
                          "reuseOriginalCursorCondition", jstr "never_for_budget_failure"
                          "sameCursorRetry", sameCursorRetry
                          "changedIdentityRetry", changedIdentityRetry ]
                    :> JsonNode

                jobj
                    [ "status", jstr "aborted"
                      "timeoutMs", jint deadline.TimeoutMs
                      "responseConstructionAllowanceMs", jint deadline.ResponseAllowanceMs
                      "elapsedMs", jint deadline.ElapsedMilliseconds
                      "outcome", jstr outcome
                      "deliveryStatus", jstr "blocked"
                      "errorCode", jstr errorCode
                      "message", jstr message
                      "retryable", jbool true
                      "resolution", failureResolution
                      "coverage", coverage.DeepClone()
                      "projectsSwept", jint projectsSwept
                      "projectsRequested", jint projectsRequested
                      "projectsAnalyzed", jint projectsAnalyzed
                      "projectsFailed", jint projectsFailed
                      "projectsMissing", jint projectsMissing
                      "projectsTimedOut", jint projectsTimedOut
                      "projectsBusy", jint projectsBusy
                      "projectsNotStarted", jint projectsNotStarted
                      "resultSetComplete", jbool false
                      "totalSites", jint totalSites
                      "matchedUseCount", jint totalSites
                      "breakdownComplete", jbool breakdownComplete
                      "breakdown", breakdown.DeepClone()
                      "sites", JsonArray() :> JsonNode
                      "returnedSiteCount", jint 0
                      "sitesTruncatedByBudget", jbool (candidatePageSites.Length > 0)
                      "projectDiagnosticsTotalCount", jint projectDiagnosticsTotalCount
                      "projectDiagnosticsCountComplete", jbool (diagnosticIndex = aggregatedDiagnostics.Count)
                      "projectDiagnosticsReturnedCount", jint 0
                      "projectDiagnosticsTruncated", jbool (projectDiagnosticsTotalCount > 0)
                      "projectDiagnosticsTruncatedByBudget", jbool (initialDiagNodes.Length > 0)
                      "perProjectTotalCount", jint perProjectNodes.Length
                      "perProjectReturnedCount", jint 0
                      "perProjectTruncatedByBudget", jbool (perProjectNodes.Length > 0)
                      "responseTruncatedByBudget", jbool true
                      "responseBudgetChars", jint findResponseBudgetChars
                      "responseSizeUnit", jstr FindResponseBudget.SizeUnit
                      "pageOffset", jint pageOffset
                      "pageSize", jint pageSize
                      "cursorAdvancedBy", jint 0
                      "truncated", jbool (pageOffset < totalSites)
                      // Never emit a non-advancing cursor; blocked pages require restart.
                      "nextCursor", null
                      "totalEstimate", (jobj [ ("sites", jint totalSites) ] :> JsonNode)
                      "blockedSiteIndex",
                      (if candidatePageSites.Length > 0 then jint pageOffset else null)
                      "recovery", recovery ]
                :> JsonNode

            if responseConstructionTimedOut || deadline.ResponseExpired then
                return responseTimeout ()
            else
                try
                    let plan =
                        FindResponseBudget.planResponse
                            findResponseBudgetChars
                            siteNodes.Length
                            initialDiagNodes.Length
                            perProjectNodes.Length
                            buildResponse

                    ensureResponseStep "response-serialization" plannerProbe

                    match plan with
                    | FindResponseBudget.FitPlan.Fits(deliveredSites, deliveredDiagnostics, deliveredProjects) ->
                        let response = buildResponse deliveredSites deliveredDiagnostics deliveredProjects
                        let responseLength = renderedLength response
                        ensureResponseStep "response-serialization-complete" plannerProbe

                        if responseLength <= findResponseBudgetChars then
                            // Record only this exact, final, unmodified node, after
                            // both size and deadline checks. The public wrapper must
                            // not serialize it a second time outside this deadline.
                            recordMeasuredResponse response
                            return response
                        else
                            return
                                budgetFailure
                                    "find_response_budget_invariant_failed"
                                    "The final find response exceeded its production-serialized ceiling after planning. Retry with narrower response-shaping arguments."
                    | FindResponseBudget.FitPlan.FirstSiteOverflow ->
                        return
                            budgetFailure
                                "find_site_exceeds_response_budget"
                                "The next find site cannot fit within the serialized response ceiling even after optional diagnostic and per-project rows were removed. No cursor was advanced."
                    | FindResponseBudget.FitPlan.FixedMetadataOverflow ->
                        return
                            budgetFailure
                                "find_metadata_exceeds_response_budget"
                                "The fixed find response metadata cannot fit within the serialized response ceiling. No cursor was advanced."
                with :? TimeoutException ->
                    responseConstructionTimedOut <- true
                    return responseTimeout ()
        }

    /// Keep every result path behind one final exact-serializer ceiling, including
    /// early validation/position/discovery exits that never reach planResponse.
    member internal this.FindWithinDeadline
        (
            args: FindArgs,
            deadline: FindRequestDeadline,
            cancellationToken: CancellationToken,
            retainUntil: Task -> unit,
            fsacProbe: (string -> Task<FindFsacProbeResult>) option
        ) : Task<JsonNode> =
        task {
            let mutable measuredResponse: JsonNode = null

            let! response =
                this.FindCoreWithinDeadline(
                    args, deadline, cancellationToken, retainUntil, fsacProbe,
                    fun measured -> measuredResponse <- measured
                )

            if Object.ReferenceEquals(response, measuredResponse) then
                // Per-call reference identity is evidence from the core, never a
                // JSON field supplied by a caller. Nothing mutates this node after
                // its final measured serialization and deadline check.
                return response
            else
                // Early errors and timeout/budget envelopes were not measured by
                // the planner, so they still need the universal hard-size guard.
                findFinalResponseBeforeMeasureOverride |> Option.iter (fun hook -> hook ())
                return FindResponseBudget.guardFinalResponse response
        }

    member this.Find(args: FindArgs, ?fsacProbe: string -> Task<FindFsacProbeResult>) : Task<JsonNode> =
        let timeoutMs = args.timeoutMs |> Option.defaultValue 120_000

        if timeoutMs < 0 then
            // Keep argument validation in the shared core; the deadline itself accepts
            // only a non-negative duration.
            let deadline = FindRequestDeadline(0)

            this.FindWithinDeadline(
                args,
                deadline,
                CancellationToken.None,
                ignore,
                fsacProbe
            )
        else
            let expirySignal = findDeadlineSignalOverride |> Option.map (fun signal -> signal ())

            let responseExpirySignal =
                findResponseDeadlineSignalOverride
                |> Option.map (fun signal -> signal ())

            let deadline =
                FindRequestDeadline(
                    timeoutMs,
                    ?semanticExpirySignal = expirySignal,
                    ?responseExpirySignal = responseExpirySignal
                )

            this.FindWithinDeadline(
                args,
                deadline,
                CancellationToken.None,
                ignore,
                fsacProbe
            )

    // ── fcs_tests_for_symbol (#60): the test-coverage slice of `find` ───────────
    // Reuses the multi-project sweep machinery, but keeps ONLY the test projects of the
    // active solution (detected the way project_health does — ProjectHealth.isTestProjectFile,
    // i.e. <IsTestProject>true> OR an xunit/nunit/expecto package ref). Each test project's
    // GetAllUsesOfAllSymbols() is filtered to uses of the queried symbol, and every use site
    // is tagged with its nearest enclosing test ([<Fact>]/[<Theory>]/[<Test>]/testCase),
    // located by scanning the source lines upward. Shares ProjectSweepUses' (#131) per-project
    // use cache, so a `find` already run on the solution makes this nearly free.
    member this.TestsForSymbol(args: FcsTestsForSymbolArgs, ?activeProjectPath: string) : Task<JsonNode> =
        task {
            match ArgsValidation.requireNonBlank "symbolQuery" args.symbolQuery with
            | Error envelope -> return envelope
            | Ok query ->

            let exact = args.exact |> Option.defaultValue true
            let pageSize = args.maxResults |> Option.defaultValue 100
            let sweepBudgetMs = args.timeoutMs |> Option.defaultValue 120000

            let invalidArgs message =
                jobj [ "status", jstr "invalid_args"; "message", jstr message ] :> JsonNode

            let mutable validationError =
                if pageSize < 1 || pageSize > 1000 then
                    Some $"maxResults must be between 1 and 1000; got %d{pageSize}."
                elif sweepBudgetMs < 0 then
                    Some $"timeoutMs must be non-negative; got %d{sweepBudgetMs}."
                else
                    None

            let mutable pageOffset = 0

            match args.cursor with
            | None -> ()
            | Some cursorStr ->
                match Cursor.tryDecode cursorStr with
                | Ok payload -> pageOffset <- payload.offset
                | Error reason -> validationError <- Some $"Invalid cursor: %s{reason}"

            match validationError with
            | Some message -> return invalidArgs message
            | None ->

            // Resolve the sweep target like Find: explicit projectPath (Program.fs already
            // falls back to the active set_project), else the nearest .fsproj to args.path.
            let sweepTargetOpt =
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath
                |> Option.orElseWith (fun () ->
                    args.path |> Option.bind findNearestFsproj |> Option.map normalizePath)

            match sweepTargetOpt with
            | None ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "fcs_tests_for_symbol needs a project context: pass projectPath (.fsproj/.sln/.slnx) or path, or call set_project first." ]
                    :> JsonNode
            | Some sweepTarget ->

            let sweepSw = System.Diagnostics.Stopwatch.StartNew()

            let remainingBudget () =
                TimeSpan.FromMilliseconds(
                    max 0.0 (float sweepBudgetMs - sweepSw.Elapsed.TotalMilliseconds)
                )

            let ensureBudget () =
                if remainingBudget () <= TimeSpan.Zero then
                    raise (TimeoutException($"Overall tests_for_symbol budget of %d{sweepBudgetMs}ms was exhausted."))

            let isFsproj (path: string) =
                path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)

            let isSolutionOrDirectory (path: string) =
                Directory.Exists path
                || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)

            let pathComparison =
                if OperatingSystem.IsWindows() then
                    StringComparison.OrdinalIgnoreCase
                else
                    StringComparison.Ordinal

            let isRequestedSourceProject =
                isFsproj sweepTarget
                && not (FsLangMcp.ProjectHealth.isTestProjectFile sweepTarget)

            // Keep explicit projectPath as the requested SOURCE context, but use the
            // separately supplied active solution for reverse test-project discovery.
            // Program.fs passes bridge.CurrentProjectPath through this optional argument;
            // without that separate channel an explicit .fsproj would erase set_project.
            let activeSolutionContext =
                if not isRequestedSourceProject then
                    None
                else
                    activeProjectPath
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.map normalizePath
                    |> Option.filter isSolutionOrDirectory
                    |> Option.bind (fun activePath ->
                        let sourceIsMember =
                            SolutionParsing.listProjects activePath
                            |> Array.exists (fun project ->
                                String.Equals(normalizePath project, sweepTarget, pathComparison))

                        if sourceIsMember then Some activePath else None)

            let discoveryTarget =
                activeSolutionContext |> Option.defaultValue sweepTarget

            // The test-coverage slice: sweep only the discovery context's TEST projects.
            // This intentionally sweeps every test project in the active solution: reverse
            // ProjectReference filtering requires an additional evaluated-MSBuild pass and
            // is not needed for correctness. Coverage reports the exact requested set.
            let testProjects =
                SolutionParsing.listProjects discoveryTarget
                |> Array.filter FsLangMcp.ProjectHealth.isTestProjectFile

            // A production .fsproj cannot reveal projects that reference it. In particular,
            // without a separately supplied active solution, a zero-project result here is
            // a discovery gap, not proof that no tests exist. A direct TEST .fsproj is
            // discoverable and is swept normally.
            let needsSolutionWidening =
                isRequestedSourceProject && activeSolutionContext.IsNone

            // ── Enclosing-test detection (best-effort, textual) ──────────────────
            // Scan source lines upward from a use to the nearest test marker:
            //   • an Expecto label   → the quoted label string is the test name;
            //   • a test attribute   → the name of the let/member it decorates.
            // A candidate owns the use only until the next binding at the same or a
            // shallower indentation. This prevents top-level fixture/setup code after a
            // test from being attributed to the preceding test binding (#240).
            let findEnclosingTest (lines: string array) (useLine1: int) : EnclosingTestIdentity option =
                if lines.Length = 0 then
                    None
                else
                    let startIdx = min (max 0 (useLine1 - 1)) (lines.Length - 1)

                    let indentation (line: string) =
                        let mutable count = 0
                        let mutable index = 0

                        while index < line.Length && (line[index] = ' ' || line[index] = '\t') do
                            count <- count + (if line[index] = '\t' then 4 else 1)
                            index <- index + 1

                        count

                    let candidateOwnsUse candidateLine candidateIndent =
                        let mutable owns = true
                        let mutable lineIndex = candidateLine + 1

                        while owns && lineIndex <= startIdx do
                            ensureBudget ()
                            let line = lines[lineIndex]
                            let binding = bindingNameRegex.Match line
                            let expecto = expectoLabelRegex.Match line
                            let scopeBoundary = enclosingTestScopeBoundaryRegex.IsMatch line

                            if
                                (binding.Success || expecto.Success || scopeBoundary)
                                && indentation line <= candidateIndent
                            then
                                owns <- false

                            lineIndex <- lineIndex + 1

                        owns

                    let mutable result = None
                    let mutable i = startIdx

                    while result.IsNone && i >= 0 do
                        ensureBudget ()
                        let labelMatch = expectoLabelRegex.Match lines[i]

                        if labelMatch.Success && candidateOwnsUse i (indentation lines[i]) then
                            result <-
                                Some
                                    { Name = labelMatch.Groups[1].Value
                                      StartLine = i + 1
                                      StartColumn = labelMatch.Index + 1 }
                        elif testAttrRegex.IsMatch lines[i] then
                            // Scan downward from the attribute to the use for the decorated name.
                            let mutable j = i
                            let mutable binding = None

                            while binding.IsNone && j <= startIdx do
                                ensureBudget ()
                                let bm = bindingNameRegex.Match lines[j]

                                if bm.Success then
                                    binding <-
                                        Some(j, indentation lines[j], bm.Groups[1].Value.Trim('`'))
                                else
                                    j <- j + 1

                            match binding with
                            | Some(bindingLine, bindingIndent, name) when
                                candidateOwnsUse bindingLine bindingIndent
                                ->
                                let bindingMatch = bindingNameRegex.Match lines[bindingLine]

                                result <-
                                    Some
                                        { Name = name
                                          StartLine = bindingLine + 1
                                          StartColumn = bindingMatch.Index + 1 }
                            | Some _
                            | None -> ()

                        if result.IsNone then
                            i <- i - 1

                    result

            // Per-file line cache: read each test source once for lineText + enclosing test.
            let linesCache = System.Collections.Generic.Dictionary<string, string array>()

            let readLines (path: string) =
                match linesCache.TryGetValue path with
                | true, cached -> cached
                | _ ->
                    let lines =
                        try
                            if File.Exists path then File.ReadAllLines path else [||]
                        with _ ->
                            [||]

                    linesCache[path] <- lines
                    lines

            // De-dup accumulator keyed by stable source location (mirrors Find).
            let siteByKey =
                System.Collections.Generic.Dictionary<
                    string,
                    {| File: string
                       StartLine: int
                       StartCol: int
                       EndLine: int
                       EndCol: int
                       Project: string
                       Fsproj: string
                       EnclosingTest: EnclosingTestIdentity option
                       LineText: string |}
                 >()

            let perProject = ResizeArray<JsonNode>()
            let mutable scannedOk = 0
            let mutable projectsFailed = 0
            let mutable projectsTimedOut = 0
            let mutable projectsBusy = 0

            for fsproj in testProjects do
                let projSw = System.Diagnostics.Stopwatch.StartNew()
                let projDisplay = Path.GetFileNameWithoutExtension fsproj

                try
                    if remainingBudget () <= TimeSpan.Zero then
                        raise (TimeoutException($"Overall tests_for_symbol budget of %d{sweepBudgetMs}ms was exhausted."))

                    let! options, _ =
                        this.ResolveFsprojOptionsWithinBudget(fsproj, Some remainingBudget)

                    let! usesKey = resolveAnalysisSnapshotKey options (Some remainingBudget)

                    if remainingBudget () <= TimeSpan.Zero then
                        raise (TimeoutException($"Overall tests_for_symbol budget of %d{sweepBudgetMs}ms was exhausted."))

                    // Preserve analysisSnapshotKey's cache-invalidation semantics after
                    // moving the potentially expensive hash computation behind a bounded
                    // single-flight task.
                    commitAnalysisSnapshotKey options usesKey

                    let remainingMs = int (Math.Ceiling((remainingBudget ()).TotalMilliseconds))

                    if remainingMs <= 0 then
                        raise (TimeoutException($"Overall tests_for_symbol budget of %d{sweepBudgetMs}ms was exhausted."))

                    let projectSweepWaitMs =
                        match testsForSymbolProjectSweepWaitMsOverride with
                        | Some overrideWait -> max 1 (overrideWait remainingMs)
                        | None -> remainingMs

                    let! allUses, _ = this.ProjectSweepUses(usesKey, options, projectSweepWaitMs)

                    if remainingBudget () <= TimeSpan.Zero then
                        raise (TimeoutException($"Overall tests_for_symbol budget of %d{sweepBudgetMs}ms was exhausted."))

                    let mutable matchedSites = 0

                    for u in allUses do
                        testsForSymbolSiteScanBeforeUseOverride |> Option.iter (fun hook -> hook ())
                        ensureBudget ()
                        // Definitions are declarations, not evidence that a test covers the
                        // symbol. Keep only executable/reference sites (#240).
                        if not u.IsFromDefinition && symbolMatches query exact u.Symbol then
                            matchedSites <- matchedSites + 1
                            // FSharpSymbolUse is a struct: bind Range before reading fields (FS0052).
                            let r = u.Range
                            let file = normalizePath r.FileName
                            // One physical linked file may be compiled by several test
                            // projects. Project identity is part of the coverage evidence.
                            let fsprojIdentity = normalizePath fsproj
                            let key = $"%s{fsprojIdentity}:%s{file}:%d{r.StartLine}:%d{r.StartColumn}:%d{r.EndLine}:%d{r.EndColumn}"

                            if not (siteByKey.ContainsKey key) then
                                let lines = readLines file
                                let lineIdx = r.StartLine - 1

                                let lineText =
                                    if lineIdx >= 0 && lineIdx < lines.Length then
                                        lines[lineIdx]
                                    else
                                        ""

                                siteByKey[key] <-
                                    {| File = file
                                       StartLine = r.StartLine
                                       StartCol = r.StartColumn
                                       EndLine = r.EndLine
                                       EndCol = r.EndColumn
                                       Project = projDisplay
                                       Fsproj = fsprojIdentity
                                       EnclosingTest = findEnclosingTest lines r.StartLine
                                       LineText = lineText |}

                    // A project is scanned only after every returned use has been
                    // classified. If the deadline expires inside the site loop, the
                    // same project must not appear in both scanned and timed-out buckets.
                    scannedOk <- scannedOk + 1
                    projSw.Stop()

                    perProject.Add(
                        jobj
                            [ "project", jstr projDisplay
                              "fsproj", jstr (normalizePath fsproj)
                              "status", jstr "analyzed"
                              "matchedSites", jint matchedSites
                              "elapsedMs", jint (int projSw.ElapsedMilliseconds) ]
                        :> JsonNode
                    )
                with ex ->
                    projSw.Stop()
                    // Preserve the actual no-queue admission outcome even if the
                    // request's stopwatch crosses its deadline while the exception is
                    // propagating. Busy is retryable; timeout is a different remedy.
                    let busy = boundedCheckWorkFailureIsBusy ex

                    let deadlineExpired =
                        match testsForSymbolFailureDeadlineExpiredOverride with
                        | Some probe -> probe ()
                        | None -> remainingBudget () <= TimeSpan.Zero

                    let timedOut = not busy && (findFailureIsTimeout ex || deadlineExpired)

                    if timedOut then
                        projectsTimedOut <- projectsTimedOut + 1
                    elif busy then
                        projectsBusy <- projectsBusy + 1
                    else
                        projectsFailed <- projectsFailed + 1

                    perProject.Add(
                        jobj
                            [ "project", jstr projDisplay
                              "fsproj", jstr (normalizePath fsproj)
                              "status",
                              jstr (
                                  if timedOut then "timed_out"
                                  elif busy then "busy"
                                  else "failed"
                              )
                              "errorKind",
                              jstr (
                                  if timedOut then "timeout"
                                  elif busy then "fcs_worker_busy"
                                  else "project_failure"
                              )
                              "retryable", jbool busy
                              "error", jstr ex.Message
                              "elapsedMs", jint (int projSw.ElapsedMilliseconds) ]
                        :> JsonNode
                    )

            sweepSw.Stop()

            let sortedSites =
                siteByKey.Values
                |> Seq.toArray
                |> Array.sortBy (fun s -> s.File, s.StartLine, s.StartCol, s.EndLine, s.EndCol, s.Fsproj)

            let siteCount = sortedSites.Length

            let uniqueTestCount =
                sortedSites
                |> Array.choose (fun site ->
                    site.EnclosingTest
                    |> Option.map (fun test ->
                        site.Fsproj,
                        site.File,
                        test.StartLine,
                        test.StartColumn,
                        test.Name))
                |> Array.distinct
                |> Array.length

            let pageSites =
                sortedSites
                |> Array.skip (min pageOffset siteCount)
                |> Array.truncate pageSize

            let testToJson
                (s:
                    {| File: string
                       StartLine: int
                       StartCol: int
                       EndLine: int
                       EndCol: int
                       Project: string
                       Fsproj: string
                       EnclosingTest: EnclosingTestIdentity option
                       LineText: string |})
                =
                // range carries coordinates only — `file` is the sibling field above, so it
                // is not duplicated inside the range object.
                let rangeNode =
                    jobj
                        [ "startLine", jint s.StartLine
                          "startColumn", jint s.StartCol
                          "endLine", jint s.EndLine
                          "endColumn", jint s.EndCol ]
                    :> JsonNode

                let enclosing =
                    match s.EnclosingTest with
                    | Some test -> jstr test.Name
                    | None -> null

                jobj
                    [ "file", jstr s.File
                      "range", rangeNode
                      "enclosingTest", enclosing
                      "project", jstr s.Project
                      "fsproj", jstr s.Fsproj
                      "lineText", jstr s.LineText ]
                :> JsonNode

            let testNodes = pageSites |> Array.map testToJson

            let coverageComplete =
                not needsSolutionWidening
                && scannedOk = testProjects.Length
                && projectsFailed = 0
                && projectsTimedOut = 0
                && projectsBusy = 0

            let status =
                if coverageComplete then
                    "succeeded"
                elif siteCount > 0 then
                    "partial"
                else
                    "unknown"

            let outcome =
                if siteCount > 0 then
                    "matched"
                elif coverageComplete then
                    "not_found"
                else
                    "indeterminate"

            let coverage =
                jobj
                    [ "complete", jbool coverageComplete
                      "projectsRequested", jint testProjects.Length
                      "projectsScanned", jint scannedOk
                      "projectsFailed", jint projectsFailed
                      "projectsTimedOut", jint projectsTimedOut
                      "projectsBusy", jint projectsBusy ]
                :> JsonNode

            let messageFields =
                if needsSolutionWidening then
                    [ ("message",
                       jstr
                           $"'%s{sweepTarget}' is a non-test .fsproj and cannot reveal test projects that reference it. No active .sln/.slnx containing that source project was available as the separate discovery context. Call set_project with the containing solution, or pass its .sln/.slnx (or workspace directory) as projectPath, to widen tests_for_symbol coverage.") ]
                elif not coverageComplete then
                    [ ("message",
                       jstr
                           $"Only %d{scannedOk}/%d{testProjects.Length} requested test project(s) were scanned (%d{projectsFailed} failed, %d{projectsTimedOut} timed out, %d{projectsBusy} busy). The test-site result is incomplete; retry busy work, increase timeoutMs, or fix the perProject errors before trusting zero.") ]
                else
                    []

            let paginationFields =
                Cursor.paginationFields "sites" siteCount pageOffset pageSize pageSites.Length

            return
                jobj
                    ([ "status", jstr status
                       "outcome", jstr outcome
                       "symbol", jstr query
                       "requestedProjectPath", jstr sweepTarget
                       "discoveryProjectPath", jstr discoveryTarget
                       "usedActiveSolutionContext", jbool activeSolutionContext.IsSome
                       "tests", JsonArray(testNodes) :> JsonNode
                       // Compatibility: testCount historically counted reference sites.
                       // uniqueTestCount is the additive distinct-enclosing-test metric.
                       "testCount", jint siteCount
                       "uniqueTestCount", jint uniqueTestCount
                       "siteCount", jint siteCount
                       "complete", jbool coverageComplete
                       "coverage", coverage
                       "projectsScanned", jint scannedOk
                       "projectsRequested", jint testProjects.Length
                       "projectsFailed", jint projectsFailed
                       "projectsTimedOut", jint projectsTimedOut
                       "projectsBusy", jint projectsBusy
                       "perProject", JsonArray(perProject.ToArray()) :> JsonNode
                       "sweepElapsedMs", jint (int sweepSw.ElapsedMilliseconds) ]
                     @ messageFields
                     @ paginationFields)
                :> JsonNode
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
        (fsproj: string, remainingBudget: unit -> TimeSpan)
        : Task<Result<FSharpDiagnostic array * string * string, CheckBlockingFailure>> =
        task {
            try
                let! options, optionsSource =
                    this.ResolveFsprojOptionsWithinBudget(normalizePath fsproj, Some remainingBudget)
                let remaining = remainingBudget ()

                // ResolveFsprojOptions is not cancellable. If its caller's overall
                // Check budget elapsed while it was running, do not launch a late FCS
                // type-check after the original request has already returned unknown.
                if remaining <= TimeSpan.Zero then
                    return Error CheckTimedOut
                else
                    // Hashing the full source/reference closure is synchronous and can
                    // be expensive. The single-flight worker remains admitted until the
                    // real hash completes even when this caller's WaitAsync expires.
                    let! cacheKey = resolveAnalysisSnapshotKey options (Some remainingBudget)

                    let remainingAfterSnapshot = remainingBudget ()

                    if remainingAfterSnapshot <= TimeSpan.Zero then
                        return Error CheckTimedOut
                    else
                        let workKey = $"{analysisProjectIdentity options}|{cacheKey}"
                        let generation = Volatile.Read(&freshProjectCheckGeneration)

                        let pending =
                            freshProjectChecksInFlight.GetOrAdd(
                                workKey,
                                fun _ ->
                                    Lazy<Task<FSharpDiagnostic array * string * string>>(
                                        (fun () ->
                                            task {
                                                try
                                                    if remainingBudget () <= TimeSpan.Zero then
                                                        raise (TimeoutException())

                                                    match freshProjectCheckBeforeAdmissionOverride with
                                                    | Some beforeAdmission -> do! beforeAdmission ()
                                                    | None -> do! Task.CompletedTask

                                                    return!
                                                        freshProjectCheckAdmission.TryRun(
                                                            workKey,
                                                            fun () ->
                                                                task {
                                                                    // Admission itself may happen after the caller
                                                                    // deadline. This must be the first production
                                                                    // operation in the real-work closure: no snapshot
                                                                    // commit, invalidation, counter, or FCS call may
                                                                    // start for an already-returned request.
                                                                    if remainingBudget () <= TimeSpan.Zero then
                                                                        return raise (TimeoutException())
                                                                    elif
                                                                        generation
                                                                        <> Volatile.Read(&freshProjectCheckGeneration)
                                                                    then
                                                                        return
                                                                            raise (
                                                                                InvalidOperationException(
                                                                                    "project context changed before type-check admission completed"
                                                                                )
                                                                            )
                                                                    else
                                                                        // Commit/invalidate only after admission. A
                                                                        // rejected distinct snapshot must not disturb
                                                                        // the still-running FCS worker it lost to.
                                                                        commitAnalysisSnapshotKey options cacheKey
                                                                        projectResultsCache.TryRemove(cacheKey) |> ignore
                                                                        Interlocked.Increment(
                                                                            &freshProjectCheckInvalidationCount
                                                                        )
                                                                        |> ignore
                                                                        checker.InvalidateConfiguration(options)

                                                                        match freshProjectCheckBeforeFcsStartOverride with
                                                                        | Some beforeFcsStart -> do! beforeFcsStart ()
                                                                        | None -> do! Task.CompletedTask

                                                                        // Commit/cache invalidation is synchronous but
                                                                        // may itself cross the overall Check deadline.
                                                                        // Re-check at the last possible point before the
                                                                        // real uncancellable FCS worker is counted/started.
                                                                        if remainingBudget () <= TimeSpan.Zero then
                                                                            raise (TimeoutException())

                                                                        Interlocked.Increment(&projectTypeCheckStartCount)
                                                                        |> ignore

                                                                        let! diagnostics =
                                                                            match freshProjectCheckWorkerOverride with
                                                                            | Some worker -> worker options
                                                                            | None ->
                                                                                task {
                                                                                    let! results =
                                                                                        checker.ParseAndCheckProject(options)
                                                                                        |> asTask

                                                                                    return results.Diagnostics
                                                                                }

                                                                        // A caller may have timed out while the
                                                                        // uncancellable worker continued. Keep its FCS
                                                                        // state only when both the explicit generation
                                                                        // and the byte-addressed snapshot are still the
                                                                        // ones this worker checked.
                                                                        let currentKey =
                                                                            computeAnalysisSnapshotKey options

                                                                        if
                                                                            generation
                                                                            <> Volatile.Read(&freshProjectCheckGeneration)
                                                                            || not (
                                                                                String.Equals(
                                                                                    currentKey,
                                                                                    cacheKey,
                                                                                    StringComparison.Ordinal
                                                                                )
                                                                            )
                                                                        then
                                                                            Interlocked.Increment(
                                                                                &freshProjectCheckInvalidationCount
                                                                            )
                                                                            |> ignore
                                                                            checker.InvalidateConfiguration(options)

                                                                            return
                                                                                raise (
                                                                                    InvalidOperationException(
                                                                                        "project inputs changed while the type-check was running; stale diagnostics were discarded"
                                                                                    )
                                                                                )
                                                                        else
                                                                            return
                                                                                diagnostics,
                                                                                options.ProjectFileName,
                                                                                optionsSource
                                                                }
                                                        )
                                                finally
                                                    freshProjectChecksInFlight.TryRemove(workKey)
                                                    |> ignore
                                            }),
                                        LazyThreadSafetyMode.ExecutionAndPublication
                                    )
                            )

                        let work = pending.Value
                        observeFault work
                        let! result = work
                        return Ok result
            with
            | :? OperationCanceledException as cancelled -> return Error(CheckCancelled cancelled.Message)
            | :? TimeoutException -> return Error CheckTimedOut
            | SdkPreflight.SdkPinUnsatisfiable failure -> return Error(CheckSdkNotFound failure)
            | :? ProjectEvaluationBusyException as ex -> return Error(CheckBusy ex.Message)
            | ex when boundedCheckWorkFailureIsBusy ex -> return Error(CheckBusy ex.Message)
            | ex -> return Error(CheckProjectFailure ex.Message)
        }

    /// Deterministic reference-resolution probe over a project's resolved OtherOptions
    /// (#138). Returns (existing, total) `-r:`/`--reference:` targets that exist on disk.
    /// Used to tell an unrestored/unbuilt project apart from a genuinely-erroring one
    /// before running the FCS re-check. Resolution is cached, so this warms the same
    /// options FreshProjectCheck reuses. Failures remain typed Error values so a busy,
    /// timed-out, or unloadable probe becomes an honest unknown Check verdict.
    member private this.ProbeReferenceResolution
        (fsproj: string, remainingBudget: unit -> TimeSpan)
        : Task<Result<int * int, CheckBlockingFailure>> =
        task {
            try
                let! options, _ =
                    this.ResolveFsprojOptionsWithinBudget(normalizePath fsproj, Some remainingBudget)

                if remainingBudget () <= TimeSpan.Zero then
                    return Error CheckTimedOut
                else
                    let! result =
                        runReferenceResolutionProbe
                            (referenceResolutionProbeKey options)
                            (Array.copy options.OtherOptions)
                            (Some remainingBudget)

                    return
                        match result with
                        | Ok counts -> Ok counts
                        | Error failure -> Error failure
            with ex ->
                return
                    match ex with
                    | SdkPreflight.SdkPinUnsatisfiable failure -> Error(CheckSdkNotFound failure)
                    | :? OperationCanceledException as cancelled -> Error(CheckCancelled cancelled.Message)
                    | :? TimeoutException -> Error CheckTimedOut
                    | :? ProjectEvaluationBusyException as busy -> Error(CheckBusy busy.Message)
                    | _ when boundedCheckWorkFailureIsBusy ex -> Error(CheckBusy ex.Message)
                    | _ -> Error(CheckProjectFailure ex.Message)
        }

    /// Resolve the exact evaluated FCS SourceFiles covered by a fast check. This is
    /// intentionally based on project options rather than raw XML Compile items, so
    /// imports, conditions, linked files, and SDK evaluation are reflected honestly.
    member private this.ResolveFastCheckExpectation
        (
            args: CheckArgs,
            resolvedScope: string,
            sweepTargetOpt: string option,
            remainingBudget: (unit -> TimeSpan) option
        )
        : Task<CheckFsacExpectation> =
        task {
            let comparer =
                if OperatingSystem.IsWindows() then
                    StringComparer.OrdinalIgnoreCase
                else
                    StringComparer.Ordinal

            let expected = System.Collections.Generic.HashSet<string>(comparer)
            let failures = ResizeArray<string>()
            let blockingReasons = ResizeArray<JsonNode>()
            let contextFingerprints = ResizeArray<string>()
            let mutable deadlineExpired = false

            // This inner fast-path wait may time out before the outer Check timer.
            // Publish its terminal state to discovery waiters synchronously too.
            let discoveryBudget =
                remainingBudget
                |> Option.map (fun getRemaining ->
                    fun () ->
                        if Volatile.Read(&deadlineExpired) then TimeSpan.Zero else getRemaining ())

            let addBlockingReason (reason: JsonNode) =
                let rendered = reason.ToJsonString()

                if
                    blockingReasons
                    |> Seq.exists (fun existing ->
                        String.Equals(existing.ToJsonString(), rendered, StringComparison.Ordinal))
                    |> not
                then
                    blockingReasons.Add reason

            let addGenericBlockingReason errorKind message retryable =
                checkGenericBlockingReason errorKind message retryable |> addBlockingReason

            let preserveBlockingReason (projectPath: string) (ex: exn) =
                match ex with
                | SdkPreflight.SdkPinUnsatisfiable failure ->
                    let reason =
                        SdkPreflight.toBlockingReason failure.Pin failure.InstalledSdks

                    reason["projectPath"] <- jstr (normalizePath projectPath)
                    addBlockingReason reason
                | :? OperationCanceledException as cancelled ->
                    addGenericBlockingReason "cancelled" cancelled.Message true
                | :? TimeoutException as timeout -> addGenericBlockingReason "timeout" timeout.Message true
                | :? ProjectEvaluationBusyException as busy ->
                    addGenericBlockingReason "fcs_worker_busy" busy.Message true
                | busy when boundedCheckWorkFailureIsBusy busy ->
                    addGenericBlockingReason "fcs_worker_busy" busy.Message true
                | failure -> addGenericBlockingReason "project_failure" failure.Message false

            let markDeadlineExpired description =
                if not deadlineExpired then
                    let message = $"Fast check expectation timed out while {description}."
                    failures.Add message
                    addGenericBlockingReason "timeout" message true

                Volatile.Write(&deadlineExpired, true)

            let awaitWithinDeadline description (start: unit -> Task<'T>) : Task<'T option> =
                task {
                    match remainingBudget with
                    | Some getRemaining ->
                        let remaining = getRemaining ()

                        if remaining <= TimeSpan.Zero then
                            markDeadlineExpired description
                            return None
                        else
                            let work =
                                task {
                                    do! Task.Yield()

                                    if getRemaining () <= TimeSpan.Zero then
                                        return raise (TimeoutException())
                                    else
                                        return! start ()
                                }

                            observeFault work
                            let waitBudget = getRemaining ()

                            if waitBudget <= TimeSpan.Zero then
                                markDeadlineExpired description
                                return None
                            else
                                try
                                    let! value = work.WaitAsync(waitBudget)

                                    if getRemaining () <= TimeSpan.Zero then
                                        markDeadlineExpired description
                                        return None
                                    else
                                        return Some value
                                with :? TimeoutException ->
                                    markDeadlineExpired description
                                    return None
                    | None ->
                        let! value = start ()
                        return Some value
                }

            let normalizeSource (options: FSharpProjectOptions) (sourceFile: string) =
                if Path.IsPathFullyQualified sourceFile then
                    normalizePath sourceFile
                else
                    Path.Combine(Path.GetDirectoryName(options.ProjectFileName), sourceFile) |> normalizePath

            let addProjectSources (options: FSharpProjectOptions) =
                task {
                    // See FreshProjectCheck: a cached ResolveFsprojOptions can complete
                    // synchronously, so the snapshot hash itself needs an async boundary
                    // before the caller's overall WaitAsync can be effective.
                    let! contextFingerprint = resolveAnalysisSnapshotKey options remainingBudget

                    match remainingBudget with
                    | Some getRemaining when getRemaining () <= TimeSpan.Zero ->
                        markDeadlineExpired "computing the project context fingerprint"
                    | _ ->
                        contextFingerprints.Add(contextFingerprint)

                        for sourceFile in options.SourceFiles do
                            if not (String.IsNullOrWhiteSpace sourceFile) then
                                expected.Add(normalizeSource options sourceFile) |> ignore
                }

            match resolvedScope with
            | "file" ->
                match args.path with
                | Some path when not (String.IsNullOrWhiteSpace path) ->
                    try
                        let fullPath = normalizePath path
                        let source = File.ReadAllText(fullPath)

                        // Once a real owning project is discoverable, pass it explicitly.
                        // ResolveProjectOptions deliberately falls back to script inference
                        // only for genuinely projectless files; allowing that fallback here
                        // used to erase sdk_not_found from the natural fast file-check path.
                        let effectiveProjectPath =
                            match args.projectPath with
                            | Some project when project.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ->
                                Some(normalizePath project)
                            | _ -> findNearestFsproj fullPath |> Option.map normalizePath

                        let! resolved =
                            awaitWithinDeadline $"evaluating SourceFiles for '{fullPath}'" (fun () ->
                                this.ResolveProjectOptions(fullPath, source, effectiveProjectPath, None))

                        match resolved with
                        | Some(options, _) ->
                            contextFingerprints.Add(computeAnalysisSnapshotKey options)

                            let evaluatedFiles =
                                options.SourceFiles
                                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                                |> Array.map (normalizeSource options)

                            expected.Add(fullPath) |> ignore

                            if not (evaluatedFiles |> Array.exists (fun file -> comparer.Equals(file, fullPath))) then
                                failures.Add($"File is not present in the evaluated SourceFiles: {fullPath}")
                        | None -> ()
                    with ex ->
                        preserveBlockingReason path ex
                        failures.Add($"Could not evaluate SourceFiles for file scope: {ex.Message}")
                | _ -> failures.Add("scope='file' requires a source path.")

            | "project" ->
                match sweepTargetOpt with
                | Some target ->
                    try
                        let! discovered =
                            awaitWithinDeadline
                                $"resolving a project from '{target}'"
                                (fun () ->
                                    runCheckTargetDiscovery
                                        (checkDiscoveryKey "project" target args.path)
                                        discoveryBudget
                                        (fun hasActiveWaiters ->
                                            CheckTargetDiscoveryResult.Project(
                                                resolveSingleCheckProject target args.path hasActiveWaiters
                                            )))

                        match discovered with
                        | Some(CheckTargetDiscoveryResult.Project(Some fsproj)) ->
                            let! resolved =
                                awaitWithinDeadline
                                    $"evaluating SourceFiles for '{fsproj}'"
                                    (fun () -> this.ResolveFsprojOptionsWithinBudget(fsproj, remainingBudget))

                            match resolved with
                            | Some(options, _) -> do! addProjectSources options
                            | None -> ()
                        | Some(CheckTargetDiscoveryResult.Project None) ->
                            failures.Add("Could not resolve a project for the fast check.")
                        | Some(CheckTargetDiscoveryResult.Busy reason) ->
                            failures.Add reason
                            addGenericBlockingReason "fcs_worker_busy" reason true
                        | None -> ()
                        | Some _ -> failures.Add("Project discovery returned an unexpected result.")
                    with ex ->
                        preserveBlockingReason target ex
                        failures.Add($"Could not resolve/evaluate SourceFiles from '{target}': {ex.Message}")
                | None -> failures.Add("Could not resolve a project for the fast check.")

            | "workspace" ->
                match sweepTargetOpt with
                | Some target ->
                    try
                        let! discovered =
                            awaitWithinDeadline
                                $"resolving workspace projects from '{target}'"
                                (fun () ->
                                    runCheckTargetDiscovery
                                        (checkDiscoveryKey "workspace" target None)
                                        discoveryBudget
                                        (fun hasActiveWaiters ->
                                            if not (hasActiveWaiters ()) then raise (TimeoutException())
                                            let projects = SolutionParsing.listProjects target

                                            CheckTargetDiscoveryResult.Projects(
                                                if
                                                    projects.Length = 0
                                                    && target.EndsWith(
                                                        ".fsproj",
                                                        StringComparison.OrdinalIgnoreCase
                                                    )
                                                then
                                                    [| target |]
                                                else
                                                    projects
                                            )))

                        match discovered with
                        | Some(CheckTargetDiscoveryResult.Projects projects) when projects.Length = 0 ->
                            failures.Add($"Could not resolve any projects from '{target}'.")
                        | Some(CheckTargetDiscoveryResult.Projects projects) ->
                            for fsproj in projects do
                                if not deadlineExpired then
                                    try
                                        let! resolved =
                                            awaitWithinDeadline
                                                $"evaluating SourceFiles for '{fsproj}'"
                                                (fun () ->
                                                    this.ResolveFsprojOptionsWithinBudget(fsproj, remainingBudget))

                                        match resolved with
                                        | Some(options, _) -> do! addProjectSources options
                                        | None -> ()
                                    with ex ->
                                        preserveBlockingReason fsproj ex
                                        failures.Add($"Could not evaluate SourceFiles for '{fsproj}': {ex.Message}")
                        | Some(CheckTargetDiscoveryResult.Busy reason) ->
                            failures.Add reason
                            addGenericBlockingReason "fcs_worker_busy" reason true
                        | None -> ()
                        | Some _ -> failures.Add("Workspace discovery returned an unexpected result.")
                    with ex ->
                        preserveBlockingReason target ex
                        failures.Add($"Could not resolve workspace projects from '{target}': {ex.Message}")
                | None -> failures.Add("Could not resolve a workspace for the fast check.")

            | other -> failures.Add($"Fast diagnostics do not support resolved scope '{other}'.")

            let expectedFiles =
                expected
                |> Seq.sortWith (fun left right -> comparer.Compare(left, right))
                |> Seq.toArray

            if failures.Count > 0 && blockingReasons.Count = 0 then
                addGenericBlockingReason "project_failure" (String.concat " | " failures) false

            let contextFingerprint =
                if failures.Count > 0 || contextFingerprints.Count = 0 then
                    None
                else
                    contextFingerprints
                    |> Seq.sort
                    |> String.concat "\n"
                    |> System.Text.Encoding.UTF8.GetBytes
                    |> System.Security.Cryptography.SHA256.HashData
                    |> Convert.ToHexString
                    |> Some

            return
                { RequestedProjectPath = sweepTargetOpt
                  Scope = resolvedScope
                  ExpectedFiles = expectedFiles
                  ContextFingerprint = contextFingerprint
                  Complete = failures.Count = 0
                  FailureReason =
                    if failures.Count = 0 then
                        None
                    else
                        Some(String.concat " | " failures)
                  BlockingReasons = blockingReasons.ToArray()
                  FileGlob = if resolvedScope = "workspace" then args.fileGlob else None }
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
    member this.Check
        (args: CheckArgs, ?fsacSnapshot: CheckFsacExpectation -> Task<CheckFsacSnapshot>)
        : Task<JsonNode> =
        task {
            let speed = (args.speed |> Option.defaultValue "trusted").Trim().ToLowerInvariant()
            let mode = (args.mode |> Option.defaultValue "fs").Trim().ToLowerInvariant()
            let severityFloor = (args.severity |> Option.defaultValue "error").Trim().ToLowerInvariant()
            let scopeRaw = (args.scope |> Option.defaultValue "auto").Trim().ToLowerInvariant()
            let timeoutMs = args.timeoutMs |> Option.defaultValue 60000
            let checkBudget = TimeSpan.FromMilliseconds(float (max 0 timeoutMs))
            let checkElapsed = System.Diagnostics.Stopwatch.StartNew()

            // WaitAsync's timer and Stopwatch do not have identical wake-up precision:
            // the caller can observe a timeout a fraction before Elapsed reaches the
            // requested budget. Publish that terminal state explicitly so an admitted
            // non-cancellable worker cannot see a tiny positive remainder and start
            // filesystem/FCS work after Check has already returned unknown.
            let mutable checkDeadlineExpired = 0

            let markCheckDeadlineExpired () =
                Interlocked.Exchange(&checkDeadlineExpired, 1) |> ignore

            let remainingCheckBudget () =
                if Volatile.Read(&checkDeadlineExpired) <> 0 then
                    TimeSpan.Zero
                else
                    let remaining = checkBudget - checkElapsed.Elapsed
                    if remaining <= TimeSpan.Zero then TimeSpan.Zero else remaining

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
            elif timeoutMs < 0 then
                return invalid $"timeoutMs must be non-negative (got {timeoutMs})"
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
            // already defaulted projectPath to the active set_project.) A nearest-project
            // filesystem walk is admitted discovery work; never execute Directory.GetFiles
            // synchronously while constructing this value.
            let explicitSweepTargetOpt =
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath

            let! sweepTargetOpt, targetResolutionFailure =
                match explicitSweepTargetOpt with
                | Some target -> Task.FromResult(Some target, None)
                | None when scopeRaw = "project" || scopeRaw = "workspace" ->
                    match args.path |> Option.filter (String.IsNullOrWhiteSpace >> not) with
                    | Some path ->
                        task {
                            let remaining = remainingCheckBudget ()

                            if remaining <= TimeSpan.Zero then
                                markCheckDeadlineExpired ()
                                return None, Some "timeout"
                            else
                                let work =
                                    runCheckTargetDiscovery
                                        (checkDiscoveryKey "nearest-project" path None)
                                        (Some remainingCheckBudget)
                                        (fun hasActiveWaiters ->
                                            if not (hasActiveWaiters ()) then raise (TimeoutException())
                                            CheckTargetDiscoveryResult.Project(
                                                findNearestFsproj path |> Option.map normalizePath
                                            ))

                                observeFault work

                                try
                                    let! result = work.WaitAsync(remaining)

                                    match result with
                                    | CheckTargetDiscoveryResult.Project target ->
                                        if remainingCheckBudget () <= TimeSpan.Zero then
                                            markCheckDeadlineExpired ()

                                        return
                                            target,
                                            (if remainingCheckBudget () <= TimeSpan.Zero then
                                                 Some "timeout"
                                             else
                                                 None)
                                    | CheckTargetDiscoveryResult.Busy reason -> return None, Some reason
                                    | _ ->
                                        return
                                            None,
                                            Some "Nearest-project discovery returned an unexpected result."
                                with :? TimeoutException ->
                                    markCheckDeadlineExpired ()
                                    return None, Some "timeout"
                        }
                    | None -> Task.FromResult(None, None)
                | None -> Task.FromResult(None, None)

            let! resolvedScope, autoScopeDiscoveryFailure =
                match scopeRaw with
                | "file" -> Task.FromResult("file", None)
                | "project" -> Task.FromResult("project", None)
                | "workspace" -> Task.FromResult("workspace", None)
                | "snippet" -> Task.FromResult("snippet", None)
                | _ -> // auto
                    if hasText args.snippet then
                        Task.FromResult("snippet", None)
                    elif hasText args.path then
                        Task.FromResult("file", None)
                    else
                        match sweepTargetOpt with
                        | None -> Task.FromResult("project", None)
                        | Some target ->
                            task {
                                let remaining = remainingCheckBudget ()

                                if remaining <= TimeSpan.Zero then
                                    markCheckDeadlineExpired ()
                                    return "workspace", Some "timeout"
                                else
                                    // Directory/solution discovery can itself be a large
                                    // synchronous walk. Its worker stays admitted after
                                    // caller timeout and exact-key retries share it.
                                    let work =
                                        runCheckTargetDiscovery
                                            (checkDiscoveryKey "auto-scope" target None)
                                            (Some remainingCheckBudget)
                                            (fun hasActiveWaiters ->
                                                if not (hasActiveWaiters ()) then raise (TimeoutException())
                                                CheckTargetDiscoveryResult.Scope(
                                                    if (SolutionParsing.listProjects target).Length > 1 then
                                                        "workspace"
                                                    else
                                                        "project"
                                                ))

                                    observeFault work

                                    try
                                        let! result = work.WaitAsync(remaining)

                                        match result with
                                        | CheckTargetDiscoveryResult.Scope scope ->
                                            if remainingCheckBudget () <= TimeSpan.Zero then
                                                markCheckDeadlineExpired ()

                                            return
                                                scope,
                                                (if remainingCheckBudget () <= TimeSpan.Zero then
                                                     Some "timeout"
                                                 else
                                                     None)
                                        | CheckTargetDiscoveryResult.Busy reason ->
                                            return "workspace", Some reason
                                        | _ ->
                                            return
                                                "workspace",
                                                Some "Check target discovery returned an unexpected result."
                                    with :? TimeoutException ->
                                        markCheckDeadlineExpired ()
                                        return "workspace", Some "timeout"
                            }

            let scopeDiscoveryFailure =
                targetResolutionFailure |> Option.orElse autoScopeDiscoveryFailure

            let overallDeadlineApplies = resolvedScope = "project" || resolvedScope = "workspace"
            let overallTimeoutReason = $"Check timed out after {timeoutMs}ms before analysis completed."

            let genericBlockingReason errorKind message retryable =
                checkGenericBlockingReason errorKind message retryable

            let blockingFailureMessage failure =
                match failure with
                | CheckTimedOut -> overallTimeoutReason
                | CheckCancelled message
                | CheckBusy message
                | CheckProjectFailure message -> message
                | CheckSdkNotFound sdkFailure -> sdkFailure.Message

            let blockingReasonFor failure =
                match failure with
                | CheckTimedOut -> genericBlockingReason "timeout" overallTimeoutReason true
                | CheckCancelled message -> genericBlockingReason "cancelled" message true
                | CheckBusy message -> genericBlockingReason "fcs_worker_busy" message true
                | CheckProjectFailure message -> genericBlockingReason "project_failure" message false
                | CheckSdkNotFound sdkFailure ->
                    SdkPreflight.toBlockingReason sdkFailure.Pin sdkFailure.InstalledSdks

            let blockingFields failure =
                [ ("blockingReason", blockingReasonFor failure) ]

            let awaitWithinOverallBudget (start: unit -> Task<'T>) : Task<'T option> =
                task {
                    let remaining = remainingCheckBudget ()

                    if remaining <= TimeSpan.Zero then
                        markCheckDeadlineExpired ()
                        return None
                    else
                        let work =
                            task {
                                do! Task.Yield()

                                if remainingCheckBudget () <= TimeSpan.Zero then
                                    markCheckDeadlineExpired ()
                                    return raise (TimeoutException())
                                else
                                    return! start ()
                            }

                        observeFault work
                        let waitBudget = remainingCheckBudget ()

                        if waitBudget <= TimeSpan.Zero then
                            markCheckDeadlineExpired ()
                            return None
                        else
                            try
                                let! value = work.WaitAsync(waitBudget)

                                if remainingCheckBudget () <= TimeSpan.Zero then
                                    markCheckDeadlineExpired ()
                                    return None
                                else
                                    return Some value
                            with :? TimeoutException ->
                                markCheckDeadlineExpired ()
                                return None
                }

            let incompleteFastExpectation reason =
                { RequestedProjectPath = sweepTargetOpt
                  Scope = resolvedScope
                  ExpectedFiles = [||]
                  ContextFingerprint = None
                  Complete = false
                  FailureReason = Some reason
                  BlockingReasons = [| genericBlockingReason "timeout" reason true |]
                  FileGlob = if resolvedScope = "workspace" then args.fileGlob else None }

            // A project verdict is intentionally narrow: FCS checks the selected
            // project, not projects that reference it. Make that coverage boundary
            // impossible to mistake for a workspace-wide clean result. Do not discover
            // or evaluate downstream projects here: doing so would make the cheap,
            // bounded project path perform another ProjInfo/MSBuild sweep and would
            // compound the retained-node problem tracked in #150.
            let narrowCoverageFields =
                if resolvedScope = "project" then
                    [ "downstreamProjectsChecked", jbool false
                      "coverageNote",
                      jstr
                          "Only the selected project was analyzed. Projects that reference it (for example apps or test projects) were not checked; use scope=\"workspace\" with a solution or directory projectPath to cover downstream consumers."
                      "recommendedScope", jstr "workspace" ]
                else
                    []

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
                (infoCount: int)
                (totalDiagnostics: int)
                (diagNodes: JsonNode array)
                (files: string array)
                (reason: string option)
                (extra: (string * JsonNode) list)
                : JsonNode =
                // Hard cap the surfaced `diagnostics` array so a genuinely-erroring large
                // project (or an unrestored false-error wall that slips past the probe)
                // can never overflow the MCP token ceiling. errorCount/warningCount/
                // totalDiagnostics stay FULL-set accurate — only the array is truncated.
                let diagnosticsCap = 50
                let cappedDiagNodes = diagNodes |> Array.truncate diagnosticsCap
                let diagnosticsTruncated = diagNodes.Length > diagnosticsCap

                // #190: totalDiagnostics silently disagreed with errorCount+warningCount
                // whenever the full set held Info/Hidden diagnostics — agents read the gap
                // as "hidden findings" and burned time hunting for them. `infoCount` is a
                // genuine tally the caller computes (countInfoDiagnostics on the trusted
                // paths, the >=3 LSP-code filter on the fast path) — deliberately NOT
                // derived here as `totalDiagnostics - errorCount - warningCount`, so a
                // future call site that scopes one of the three counters over a different
                // array produces a mismatching identity the tests can actually catch,
                // instead of the mismatch being silently absorbed by subtraction.
                // belowSeverityFloorCount stays a derived count: it IS a property of
                // `diagNodes` vs. `totalDiagnostics` (what the floor let through vs. the
                // full set), not an independently-observable quantity, so there is nothing
                // to tally it against — measured before the diagnosticsCap truncation above
                // so the cap and the floor never overlap in what they explain.
                let belowSeverityFloorCount = max 0 (totalDiagnostics - diagNodes.Length)

                let diagnosticsNote =
                    if belowSeverityFloorCount > 0 then
                        jstr
                            $"%d{belowSeverityFloorCount} diagnostic(s) below the current severity floor are counted in totalDiagnostics but not listed; pass severity=\"all\" to list them."
                    else
                        null

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
                      "diagnostics", JsonArray(cappedDiagNodes) :> JsonNode
                      "diagnosticsTruncated", jbool diagnosticsTruncated
                      // debug-only
                      "escalated", (match escalated with Some e -> jstr e | None -> null)
                      "via", jstr via
                      // legacy superset
                      "fresh", jbool (speed = "trusted")
                      "groundTruth", jbool (analyzed && via = "fcs")
                      "totalDiagnostics", jint totalDiagnostics
                      "infoCount", jint infoCount
                      "belowSeverityFloorCount", jint belowSeverityFloorCount
                      "diagnosticsNote", diagnosticsNote
                      "reason", (match reason with Some r -> jstr r | None -> null) ]
                    @ narrowCoverageFields
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
            | _ when scopeDiscoveryFailure.IsSome ->
                let discoveryReason =
                    match scopeDiscoveryFailure with
                    | Some "timeout" -> overallTimeoutReason
                    | Some reason -> reason
                    | None -> overallTimeoutReason

                let discoveryFailure =
                    match scopeDiscoveryFailure with
                    | Some "timeout" -> CheckTimedOut
                    | Some reason when reason.Contains("busy", StringComparison.OrdinalIgnoreCase) ->
                        CheckBusy reason
                    | Some reason -> CheckProjectFailure reason
                    | None -> CheckTimedOut

                return
                    build
                        "unknown"
                        false
                        (if speed = "fast" then "fsac" else "fcs")
                        None
                        0
                        0
                        0
                        0
                        [||]
                        [||]
                        (Some discoveryReason)
                        ([ ("projectsSwept", jint 0) ] @ blockingFields discoveryFailure)
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

                            // The caller sent text, not a file: everything below scopes the
                            // diagnostic list to the snippet's CONTENT (#187).
                            //  - keep only diagnostics attached to the temp file — the rest
                            //    (missing Compile entries, synthetic 'startup') describe the
                            //    project context, which file/project scope already reports;
                            //  - drop FS0222 (must begin with namespace/module) and FS0225
                            //    (source-file bookkeeping): both describe the temp-file
                            //    wrapper, so a valid bare `let` snippet is clean, not errors;
                            //  - dedup — FS0222-style diagnostics arrive identically in both
                            //    the parse and the check halves of the append.
                            let snippetPath = normalizePath snippetFile

                            let allDiags =
                                Array.append parseResults.Diagnostics checkDiagnostics
                                |> Array.filter (fun d -> normalizePath d.FileName = snippetPath)
                                |> Array.filter (fun d -> d.ErrorNumber <> 222 && d.ErrorNumber <> 225)
                                |> Array.distinctBy (fun d ->
                                    d.ErrorNumber, d.Severity, d.StartLine, d.StartColumn, d.EndLine, d.EndColumn, d.Message)

                            let errorCount, warningCount = countDiagnosticsBySeverity allDiags
                            let infoCount = countInfoDiagnostics allDiags

                            let verdict, analyzed, reason =
                                if not succeeded then
                                    "unknown", false, Some "Type checking was aborted; snippet verdict is indeterminate."
                                elif errorCount > 0 then
                                    "errors", true, None
                                else
                                    "clean", true, None

                            let nodes, _ = surfaceFcs allDiags

                            // Every surviving diagnostic points at the temp file; surface the
                            // logical name instead of leaking a /tmp path the caller never made.
                            for node in nodes do
                                node["file"] <- jstr "snippet"

                            return
                                build
                                    verdict
                                    analyzed
                                    "fcs"
                                    None
                                    errorCount
                                    warningCount
                                    infoCount
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
                // Resolve the requested coverage from evaluated FCS project options,
                // then ask FSAC for a context-bound snapshot of exactly those files.
                let unavailableFastSnapshot (expectation: CheckFsacExpectation) failure =
                    CheckFsacSnapshot.unavailableWithBlockingReason
                        expectation
                        (blockingFailureMessage failure)
                        (blockingReasonFor failure)

                let expectationDeadline =
                    if overallDeadlineApplies then Some remainingCheckBudget else None

                let! expectation =
                    if overallDeadlineApplies && remainingCheckBudget () <= TimeSpan.Zero then
                        Task.FromResult(incompleteFastExpectation overallTimeoutReason)
                    else
                        task {
                            let work =
                                task {
                                    do! Task.Yield()

                                    if overallDeadlineApplies && remainingCheckBudget () <= TimeSpan.Zero then
                                        return raise (TimeoutException())
                                    else
                                        return!
                                            this.ResolveFastCheckExpectation(
                                                args,
                                                resolvedScope,
                                                sweepTargetOpt,
                                                expectationDeadline
                                            )
                                }

                            observeFault work

                            match expectationDeadline with
                            | Some getRemaining ->
                                let remaining = getRemaining ()

                                if remaining <= TimeSpan.Zero then
                                    markCheckDeadlineExpired ()
                                    return incompleteFastExpectation overallTimeoutReason
                                else
                                    try
                                        let! completed = work.WaitAsync(remaining)

                                        if getRemaining () <= TimeSpan.Zero then
                                            markCheckDeadlineExpired ()
                                            return incompleteFastExpectation overallTimeoutReason
                                        else
                                            return completed
                                    with :? TimeoutException ->
                                        markCheckDeadlineExpired ()
                                        return incompleteFastExpectation overallTimeoutReason
                            | None -> return! work
                        }

                let! snap =
                    match fsacSnapshot with
                    | _ when expectation.BlockingReasons.Length > 0 ->
                        Task.FromResult(
                            CheckFsacSnapshot.unavailable
                                expectation
                                (expectation.FailureReason
                                 |> Option.defaultValue "Fast-check expectation is blocked by infrastructure preflight.")
                        )
                    | Some thunk ->
                        task {
                            if overallDeadlineApplies && remainingCheckBudget () <= TimeSpan.Zero then
                                markCheckDeadlineExpired ()
                                return unavailableFastSnapshot expectation CheckTimedOut
                            else
                                try
                                    let work =
                                        task {
                                            do! Task.Yield()

                                            if overallDeadlineApplies && remainingCheckBudget () <= TimeSpan.Zero then
                                                markCheckDeadlineExpired ()
                                                return raise (TimeoutException())
                                            else
                                                return! thunk expectation
                                        }

                                    observeFault work

                                    if overallDeadlineApplies then
                                        let remaining = remainingCheckBudget ()

                                        if remaining <= TimeSpan.Zero then
                                            markCheckDeadlineExpired ()
                                            return unavailableFastSnapshot expectation CheckTimedOut
                                        else
                                            try
                                                let! completed = work.WaitAsync(remaining)

                                                // Work and the timeout timer can become
                                                // runnable together. A successful await is
                                                // not conclusive unless the overall deadline
                                                // is still live after its continuation runs.
                                                let deadlineExpired =
                                                    remainingCheckBudget () <= TimeSpan.Zero
                                                    || (checkFastSnapshotDeadlineExpiredOverride
                                                        |> Option.exists (fun probe -> probe ()))

                                                if deadlineExpired then
                                                    markCheckDeadlineExpired ()

                                                    return unavailableFastSnapshot expectation CheckTimedOut
                                                else
                                                    return completed
                                            with :? TimeoutException ->
                                                markCheckDeadlineExpired ()
                                                return unavailableFastSnapshot expectation CheckTimedOut
                                    else
                                        return! work
                                with
                                | SdkPreflight.SdkPinUnsatisfiable failure ->
                                    return
                                        CheckFsacSnapshot.unavailableWithBlockingReason
                                            expectation
                                            failure.Message
                                            (SdkPreflight.toBlockingReason failure.Pin failure.InstalledSdks)
                                | :? TimeoutException ->
                                    return unavailableFastSnapshot expectation CheckTimedOut
                                | :? OperationCanceledException as cancelled ->
                                    return unavailableFastSnapshot expectation (CheckCancelled cancelled.Message)
                                | :? ProjectEvaluationBusyException as busy ->
                                    return unavailableFastSnapshot expectation (CheckBusy busy.Message)
                                | busy when boundedCheckWorkFailureIsBusy busy ->
                                    return unavailableFastSnapshot expectation (CheckBusy busy.Message)
                                | ex -> return unavailableFastSnapshot expectation (CheckProjectFailure ex.Message)
                        }
                    | None ->
                        Task.FromResult(
                            CheckFsacSnapshot.unavailable
                                expectation
                                "No context-bound FSAC diagnostics snapshot was supplied."
                        )

                let pathComparer =
                    if OperatingSystem.IsWindows() then
                        StringComparer.OrdinalIgnoreCase
                    else
                        StringComparer.Ordinal

                let expectedUniverse =
                    System.Collections.Generic.HashSet<string>(expectation.ExpectedFiles, pathComparer)

                let snapshotFilesBound =
                    snap.ExpectedFiles |> Array.forall expectedUniverse.Contains

                let exactExpectedSet =
                    snap.ExpectedFiles.Length = expectation.ExpectedFiles.Length && snapshotFilesBound

                let globMatchedNoFiles =
                    expectation.FileGlob.IsSome && snap.ExpectedFiles.Length = 0

                // A workspace glob intentionally narrows the FCS-derived universe; every
                // other scope must echo the exact expected set to prevent A/B leakage.
                let expectationBound =
                    if expectation.FileGlob.IsSome then
                        not globMatchedNoFiles && snapshotFilesBound
                    else
                        exactExpectedSet

                let generationCurrent = snap.SessionGeneration |> Option.exists (fun generation -> generation > 0L)

                let diagnosticsBound =
                    snap.Status = "ok" && snap.ContextMatched && generationCurrent && expectationBound

                let coverageComplete =
                    expectation.Complete
                    && diagnosticsBound
                    && snap.Ready
                    && snap.Complete
                    && snap.MissingFiles.Length = 0
                    && snap.StaleFiles.Length = 0

                // A current error is positive evidence even if another expected file is
                // missing/stale. Absence of errors becomes clean only at full coverage.
                let conclusiveErrors = expectation.Complete && diagnosticsBound && snap.ErrorCount > 0

                let verdict =
                    if conclusiveErrors then "errors"
                    elif coverageComplete then "clean"
                    else "unknown"

                let analyzed = coverageComplete

                let coverageReason =
                    if verdict = "errors" && not coverageComplete then
                        Some
                            $"Current FSAC errors were found, but diagnostics coverage is incomplete ({snap.ReceivedFiles.Length}/{snap.ExpectedFiles.Length} expected file(s) published)."
                    elif verdict <> "unknown" then
                        None
                    elif not expectation.Complete then
                        expectation.FailureReason
                        |> Option.orElse (Some "FCS could not derive the complete evaluated SourceFiles set.")
                    elif not snap.ContextMatched then
                        snap.FailureReason
                        |> Option.orElse (Some "The active FSAC session does not match the requested project context.")
                    elif not snap.Ready then
                        snap.FailureReason |> Option.orElse (Some "FSAC is still loading the requested project context.")
                    elif not generationCurrent then
                        Some "FSAC did not provide a live session generation for the diagnostics snapshot."
                    elif globMatchedNoFiles then
                        snap.FailureReason
                        |> Option.orElse (Some "fileGlob matched no evaluated source files in the requested workspace.")
                    elif not expectationBound then
                        Some "FSAC diagnostics were not bound to the evaluated in-scope SourceFiles."
                    elif snap.StaleFiles.Length > 0 then
                        Some $"FSAC diagnostics are stale for {snap.StaleFiles.Length} expected file(s)."
                    elif snap.MissingFiles.Length > 0 then
                        Some $"FSAC has not published diagnostics for {snap.MissingFiles.Length} expected file(s)."
                    else
                        snap.FailureReason
                        |> Option.orElse (Some "FSAC diagnostics coverage is incomplete.")

                let fastBlockingReasons =
                    Array.append expectation.BlockingReasons snap.BlockingReasons
                    |> Array.distinctBy (fun reason -> reason.ToJsonString())

                let fastBlockingFields =
                    match fastBlockingReasons with
                    | [||] -> []
                        | [| one |] -> [ ("blockingReason", one.DeepClone()) ]
                        | many -> [ ("blockingReasons", JsonArray(many |> Array.map _.DeepClone()) :> JsonNode) ]

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

                // #190: a genuine tally (LSP codes 3=Information/4=Hint), not a
                // `total - error - warning` remainder — mirrors countInfoDiagnostics on
                // the trusted paths.
                let infoSnapshotDiagnostics =
                    match snap.Diagnostics with
                    | :? JsonArray as arr ->
                        arr
                        |> Seq.filter (fun n ->
                            match n with
                            | :? JsonObject as obj -> severityCode obj >= 3
                            | _ -> false)
                        |> Seq.length
                    | _ -> 0

                let fastResponseFields =
                    [ "lspState", jstr (if snap.Ready then "ready" else "warming")
                      "fsacStatus", jstr snap.Status
                      "requestedProjectPath",
                      (expectation.RequestedProjectPath |> Option.map jstr |> Option.defaultValue null)
                      "fileGlob", (expectation.FileGlob |> Option.map jstr |> Option.defaultValue null)
                      "contextMatched", jbool snap.ContextMatched
                      "complete", jbool coverageComplete
                      "expectationComplete", jbool expectation.Complete
                      "expectedFileCount", jint snap.ExpectedFiles.Length
                      "receivedFileCount", jint snap.ReceivedFiles.Length
                      "missingFileCount", jint snap.MissingFiles.Length
                      "staleFileCount", jint snap.StaleFiles.Length
                      "expectedFiles", JsonArray(snap.ExpectedFiles |> Array.map jstr) :> JsonNode
                      "receivedFiles", JsonArray(snap.ReceivedFiles |> Array.map jstr) :> JsonNode
                      "missingFiles", JsonArray(snap.MissingFiles |> Array.map jstr) :> JsonNode
                      "staleFiles", JsonArray(snap.StaleFiles |> Array.map jstr) :> JsonNode
                      "sessionGeneration",
                      (snap.SessionGeneration |> Option.map jint64 |> Option.defaultValue null)
                      "mostRecentAnalyzedAt",
                      (match snap.MostRecentAnalyzedAt with
                       | Some timestamp -> jstr timestamp
                       | None -> null)
                      "analyzedFileCount", jint snap.AnalyzedFileCount ]
                    @ fastBlockingFields

                return
                    build
                        verdict
                        analyzed
                        "fsac"
                        None
                        snap.ErrorCount
                        snap.WarningCount
                        infoSnapshotDiagnostics
                        totalSnapshotDiagnostics
                        nodes
                        files
                        coverageReason
                        fastResponseFields

            | "file" ->
                match args.path with
                | Some path when not (String.IsNullOrWhiteSpace path) ->
                    match validateSourcePath "check" None path with
                    | Some err -> return err
                    | None ->
                        // Resolve options, invalidate THIS project, then re-check fresh so
                        // on-disk edits (incl. cross-file) are reflected — mirrors fcs_check_file.
                        let fullPath = normalizePath path

                        let runTrustedFileCheck () =
                            task {
                                let source = File.ReadAllText(fullPath)
                                let sourceText = SourceText.ofString source
                                let! projectOptions, optionsSource =
                                    this.ResolveProjectOptions(fullPath, source, args.projectPath, None)

                                let projectResultsKey = analysisSnapshotKey projectOptions
                                projectResultsCache.TryRemove(projectResultsKey) |> ignore
                                checker.InvalidateConfiguration(projectOptions)

                                let! parseResults, checkAnswer =
                                    checker.ParseAndCheckFileInProject(fullPath, 0, sourceText, projectOptions)
                                    |> asTask

                                let checkAnswer =
                                    match trustedFileCheckAnswerOverride with
                                    | Some overrideAnswer -> overrideAnswer checkAnswer
                                    | None -> checkAnswer

                                let checkedResults =
                                    match checkAnswer with
                                    | FSharpCheckFileAnswer.Succeeded results -> Some results
                                    | FSharpCheckFileAnswer.Aborted -> None

                                let checkDiagnostics =
                                    checkedResults
                                    |> Option.map (fun r -> r.Diagnostics)
                                    |> Option.defaultValue [||]

                                let allDiags = Array.append parseResults.Diagnostics checkDiagnostics
                                let succeeded = checkedResults.IsSome
                                let errorCount, warningCount = countDiagnosticsBySeverity allDiags
                                let infoCount = countInfoDiagnostics allDiags

                                let abortedReason =
                                    "Type checking was aborted; file verdict is indeterminate."

                                let blockingFailure =
                                    if succeeded then
                                        None
                                    else
                                        Some(CheckProjectFailure abortedReason)

                                let verdict, analyzed, reason =
                                    match blockingFailure with
                                    | Some failure -> "unknown", false, Some(blockingFailureMessage failure)
                                    | None when errorCount > 0 -> "errors", true, None
                                    | None -> "clean", true, None

                                let nodes, files = surfaceFcs allDiags

                                let responseFields =
                                    [ "projectFileName", jstr projectOptions.ProjectFileName
                                      "optionsSource", jstr optionsSource
                                      "projectsSwept", jint 1 ]
                                    @ (blockingFailure
                                       |> Option.map blockingFields
                                       |> Option.defaultValue [])

                                return
                                    build
                                        verdict
                                        analyzed
                                        "fcs"
                                        (Some "fcs-reanalyze")
                                        errorCount
                                        warningCount
                                        infoCount
                                        allDiags.Length
                                        nodes
                                        files
                                        reason
                                        responseFields
                            }

                        let blockedTrustedFile failure =
                            let message = blockingFailureMessage failure

                            build
                                "unknown"
                                false
                                "fcs"
                                (Some "fcs-reanalyze")
                                0
                                0
                                0
                                0
                                [||]
                                [||]
                                (Some $"File could not be analyzed: {message}")
                                ([ ("projectsSwept", jint 1) ] @ blockingFields failure)

                        let sdkDirectories =
                            [ match args.projectPath with
                              | Some project when
                                  project.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                                  ->
                                  yield Path.GetDirectoryName(normalizePath project)
                              | _ -> ()

                              match findNearestFsproj fullPath with
                              | Some project -> yield Path.GetDirectoryName(normalizePath project)
                              | None -> () ]
                            |> List.distinct

                        match SdkPreflight.check sdkDirectories with
                        | SdkPreflight.SdkNotFound(pin, installedSdks) ->
                            let failure = SdkPreflight.SdkNotFoundException(pin, installedSdks)

                            return
                                build
                                    "unknown"
                                    false
                                    "fcs"
                                    (Some "fcs-reanalyze")
                                    0
                                    0
                                    0
                                    0
                                    [||]
                                    [||]
                                    (Some failure.Message)
                                    ([ ("projectsSwept", jint 0) ]
                                     @ blockingFields (CheckSdkNotFound failure))
                        | SdkPreflight.Proceed ->
                            try
                                return! runTrustedFileCheck ()
                            with
                            | :? OperationCanceledException as cancelled ->
                                return blockedTrustedFile (CheckCancelled cancelled.Message)
                            | :? TimeoutException ->
                                return blockedTrustedFile CheckTimedOut
                            | :? ProjectEvaluationBusyException as busy ->
                                return blockedTrustedFile (CheckBusy busy.Message)
                            | busy when boundedCheckWorkFailureIsBusy busy ->
                                return blockedTrustedFile (CheckBusy busy.Message)
                            | ex ->
                                return blockedTrustedFile (CheckProjectFailure ex.Message)
                | _ -> return invalid "scope='file' requires 'path'."

            | "project" ->
                match sweepTargetOpt with
                | None ->
                    return
                        invalid
                            "check needs a project context: pass projectPath (.fsproj/.sln/.slnx) or path, or call set_project first."
                | Some target ->
                    let! boundedFsproj =
                        awaitWithinOverallBudget (fun () ->
                            runCheckTargetDiscovery
                                (checkDiscoveryKey "project" target args.path)
                                (Some remainingCheckBudget)
                                (fun hasActiveWaiters ->
                                    CheckTargetDiscoveryResult.Project(
                                        resolveSingleCheckProject
                                            target
                                            args.path
                                            hasActiveWaiters
                                    )))

                    match boundedFsproj with
                    | None ->
                        return
                            build
                                "unknown"
                                false
                                "fcs"
                                None
                                0
                                0
                                0
                                0
                                [||]
                                [||]
                                (Some overallTimeoutReason)
                                ([ ("projectsSwept", jint 0) ] @ blockingFields CheckTimedOut)
                    | Some(CheckTargetDiscoveryResult.Busy reason) ->
                        return
                            build
                                "unknown"
                                false
                                "fcs"
                                None
                                0
                                0
                                0
                                0
                                [||]
                                [||]
                                (Some reason)
                                ([ ("projectsSwept", jint 0) ] @ blockingFields (CheckBusy reason))
                    | Some(CheckTargetDiscoveryResult.Project None) ->
                        return invalid $"check could not resolve a single .fsproj to check from: {target}"
                    | Some(CheckTargetDiscoveryResult.Project(Some proj)) ->
                        // Restore-awareness (#138): an unrestored/unbuilt project still
                        // evaluates its .fsproj, so FCS emits HUNDREDS of spurious FS0039
                        // "is not defined" diagnostics at `open` lines (the real cause is
                        // missing external reference assemblies, not the source). Detect
                        // that deterministically and return an honest "unknown" instead of
                        // a misleading false-error wall.
                        let! boundedProbe =
                            awaitWithinOverallBudget (fun () ->
                                this.ProbeReferenceResolution(proj, remainingCheckBudget))

                        match boundedProbe with
                        | None
                        | Some(Error CheckTimedOut) ->
                            return
                                build
                                    "unknown"
                                    false
                                    "fcs"
                                    (Some "fcs-reanalyze")
                                    0
                                    0
                                    0
                                    0
                                    [||]
                                    [||]
                                    (Some overallTimeoutReason)
                                    ([ ("projectsSwept", jint 0) ] @ blockingFields CheckTimedOut)
                        | Some(Error failure) ->
                            let failureMessage = blockingFailureMessage failure

                            return
                                build
                                    "unknown"
                                    false
                                    "fcs"
                                    (Some "fcs-reanalyze")
                                    0
                                    0
                                    0
                                    0
                                    [||]
                                    [||]
                                    (Some $"Reference resolution probe incomplete: {failureMessage}")
                                    ([ ("projectsSwept", jint 0) ] @ blockingFields failure)
                        | Some(Ok(refExisting, refTotal)) when
                            ReferenceResolution.looksUnrestored refExisting refTotal
                            ->
                            let frac = ReferenceResolution.fraction refExisting refTotal

                            return
                                build
                                    "unknown"
                                    false
                                    "fcs"
                                    None
                                    0
                                    0
                                    0
                                    0
                                    [||]
                                    [||]
                                    (Some
                                        $"project not built/restored — external references unresolved (existing {refExisting}/total {refTotal}); run dotnet restore && dotnet build")
                                    [ "projectsSwept", jint 1
                                      "restoreStatus", jstr "unrestored"
                                      "referencesResolved", JsonValue.Create(Math.Round(frac, 3)) :> JsonNode
                                      "referencesExisting", jint refExisting
                                      "referencesTotal", jint refTotal ]
                        | Some(Ok _) ->
                            let! boundedResult =
                                awaitWithinOverallBudget (fun () ->
                                    this.FreshProjectCheck(proj, remainingCheckBudget))

                            let result = boundedResult |> Option.defaultValue (Error CheckTimedOut)

                            match result with
                            | Error CheckTimedOut ->
                                return
                                    build
                                        "unknown"
                                        false
                                        "fcs"
                                        (Some "fcs-reanalyze")
                                        0
                                        0
                                        0
                                        0
                                        [||]
                                        [||]
                                        (Some overallTimeoutReason)
                                        ([ ("projectsSwept", jint 1) ] @ blockingFields CheckTimedOut)
                            | Error failure ->
                                let message = blockingFailureMessage failure

                                return
                                    build
                                        "unknown"
                                        false
                                        "fcs"
                                        (Some "fcs-reanalyze")
                                        0
                                        0
                                        0
                                        0
                                        [||]
                                        [||]
                                        (Some $"Project could not be analyzed: {message}")
                                        ([ ("projectsSwept", jint 1) ] @ blockingFields failure)
                            | Ok(diags, projFileName, optionsSource) ->
                                let errorCount, warningCount = countDiagnosticsBySeverity diags
                                let infoCount = countInfoDiagnostics diags
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
                                        infoCount
                                        diags.Length
                                        nodes
                                        files
                                        None
                                        [ "projectFileName", jstr projFileName
                                          "optionsSource", jstr optionsSource
                                          "projectsSwept", jint 1 ]
                    | Some _ ->
                        return invalid "check project discovery returned an unexpected result"

            | "workspace" ->
                match sweepTargetOpt with
                | None ->
                    return
                        invalid
                            "check needs a project context: pass projectPath (.fsproj/.sln/.slnx) or path, or call set_project first."
                | Some target when target.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ->
                    return
                        invalid
                            "scope=\"workspace\" requires projectPath to be a solution (.sln/.slnx) or directory. A single .fsproj cannot prove that downstream consumers are clean; use scope=\"project\" for that project or pass the containing solution/directory."
                | Some target ->
                    let! boundedProjects =
                        awaitWithinOverallBudget (fun () ->
                            runCheckTargetDiscovery
                                (checkDiscoveryKey "workspace" target None)
                                (Some remainingCheckBudget)
                                (fun hasActiveWaiters ->
                                    if not (hasActiveWaiters ()) then raise (TimeoutException())
                                    let projects = SolutionParsing.listProjects target

                                    CheckTargetDiscoveryResult.Projects(
                                        if
                                            projects.Length = 0
                                            && target.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                                        then
                                            [| target |]
                                        else
                                            projects
                                    )))

                    let targetDiscoveryTimedOut = boundedProjects.IsNone
                    let targetDiscoveryBusy =
                        match boundedProjects with
                        | Some(CheckTargetDiscoveryResult.Busy reason) -> Some reason
                        | _ -> None

                    let projects =
                        match boundedProjects with
                        | Some(CheckTargetDiscoveryResult.Projects projects) -> projects
                        | _ -> [||]

                    if targetDiscoveryTimedOut then
                        return
                            build
                                "unknown"
                                false
                                "fcs"
                                None
                                0
                                0
                                0
                                0
                                [||]
                                [||]
                                (Some overallTimeoutReason)
                                ([ ("projectsSwept", jint 0) ] @ blockingFields CheckTimedOut)
                    elif targetDiscoveryBusy.IsSome then
                        let busyReason = targetDiscoveryBusy.Value

                        return
                            build
                                "unknown"
                                false
                                "fcs"
                                None
                                0
                                0
                                0
                                0
                                [||]
                                [||]
                                targetDiscoveryBusy
                                ([ ("projectsSwept", jint 0) ] @ blockingFields (CheckBusy busyReason))
                    elif projects.Length = 0 then
                        return invalid $"check could not resolve any .fsproj to check from: {target}"
                    else
                        let allDiags = ResizeArray<FSharpDiagnostic>()
                        let perProject = ResizeArray<JsonNode>()
                        let mutable failCount = 0
                        let mutable unrestoredCount = 0
                        let mutable timedOutCount = 0
                        let mutable deadlineExhausted = false

                        let addTimedOutProject (proj: string) =
                            failCount <- failCount + 1
                            timedOutCount <- timedOutCount + 1

                            perProject.Add(
                                jobj
                                    [ "project", jstr (Path.GetFileNameWithoutExtension proj)
                                      "fsproj", jstr (normalizePath proj)
                                      "error", jstr "timeout"
                                      "reason", jstr overallTimeoutReason
                                      "blockingReason", blockingReasonFor CheckTimedOut
                                      "analyzed", jbool false ]
                                :> JsonNode
                            )

                        for proj in projects do
                            if deadlineExhausted || remainingCheckBudget () <= TimeSpan.Zero then
                                deadlineExhausted <- true
                                addTimedOutProject proj
                            else
                                // Restore-awareness (#138): skip the FCS re-check for an
                                // unrestored project — it would only produce a spurious
                                // FS0039 false-error wall. Mark it unrestored instead.
                                let! boundedProbe =
                                    awaitWithinOverallBudget (fun () ->
                                        this.ProbeReferenceResolution(proj, remainingCheckBudget))

                                match boundedProbe with
                                | None
                                | Some(Error CheckTimedOut) ->
                                    deadlineExhausted <- true
                                    addTimedOutProject proj
                                | Some(Error failure) ->
                                    failCount <- failCount + 1
                                    let message = blockingFailureMessage failure

                                    perProject.Add(
                                        jobj
                                            [ "project", jstr (Path.GetFileNameWithoutExtension proj)
                                              "fsproj", jstr (normalizePath proj)
                                              "error", jstr message
                                              "blockingReason", blockingReasonFor failure
                                              "analyzed", jbool false ]
                                        :> JsonNode
                                    )
                                | Some(Ok(refExisting, refTotal)) when
                                    ReferenceResolution.looksUnrestored refExisting refTotal
                                    ->
                                    unrestoredCount <- unrestoredCount + 1
                                    let frac = ReferenceResolution.fraction refExisting refTotal

                                    perProject.Add(
                                        jobj
                                            [ "project", jstr (Path.GetFileNameWithoutExtension proj)
                                              "fsproj", jstr (normalizePath proj)
                                              "analyzed", jbool false
                                              "restoreStatus", jstr "unrestored"
                                              "referencesResolved",
                                              JsonValue.Create(Math.Round(frac, 3)) :> JsonNode
                                              "referencesExisting", jint refExisting
                                              "referencesTotal", jint refTotal
                                              "reason",
                                              jstr
                                                  "project not built/restored — external references unresolved; run dotnet restore && dotnet build" ]
                                        :> JsonNode
                                    )
                                | Some(Ok _) ->
                                    let! boundedResult =
                                        awaitWithinOverallBudget (fun () ->
                                            this.FreshProjectCheck(proj, remainingCheckBudget))

                                    match boundedResult with
                                    | None
                                    | Some(Error CheckTimedOut) ->
                                        deadlineExhausted <- true
                                        addTimedOutProject proj
                                    | Some(Ok(diags, _, _)) ->
                                        allDiags.AddRange diags
                                        let e, w = countDiagnosticsBySeverity diags
                                        // #205: a genuine tally via countInfoDiagnostics on this
                                        // project's own diags — mirrors the top-level infoCount
                                        // (#190/#202) so errorCount + warningCount + infoCount
                                        // reconciles per project, not just at the workspace total.
                                        let i = countInfoDiagnostics diags

                                        perProject.Add(
                                            jobj
                                                [ "project", jstr (Path.GetFileNameWithoutExtension proj)
                                                  "fsproj", jstr (normalizePath proj)
                                                  "errorCount", jint e
                                                  "warningCount", jint w
                                                  "infoCount", jint i
                                                  "analyzed", jbool true ]
                                            :> JsonNode
                                        )
                                    | Some(Error failure) ->
                                        failCount <- failCount + 1
                                        let message = blockingFailureMessage failure

                                        perProject.Add(
                                            jobj
                                                [ "project", jstr (Path.GetFileNameWithoutExtension proj)
                                                  "fsproj", jstr (normalizePath proj)
                                                  "error", jstr message
                                                  "blockingReason", blockingReasonFor failure
                                                  "analyzed", jbool false ]
                                            :> JsonNode
                                        )

                        let diags = allDiags.ToArray()
                        let errorCount, warningCount = countDiagnosticsBySeverity diags
                        let infoCount = countInfoDiagnostics diags

                        let verdict, analyzed, reason =
                            if timedOutCount > 0 then
                                "unknown",
                                false,
                                Some
                                    $"{timedOutCount} of {projects.Length} project(s) timed out or were skipped after the overall {timeoutMs}ms Check budget expired."
                            elif errorCount > 0 then
                                "errors", true, None
                            elif unrestoredCount > 0 || failCount > 0 then
                                "unknown",
                                false,
                                Some
                                    $"{unrestoredCount} unrestored + {failCount} failed of {projects.Length} project(s); run dotnet restore && dotnet build — cannot confirm clean."
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
                                infoCount
                                diags.Length
                                nodes
                                files
                                reason
                                [ "projectsSwept", jint projects.Length
                                  "unrestoredCount", jint unrestoredCount
                                  "timedOutCount", jint timedOutCount
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
                        [ (0, 0, 0) ]

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

    member internal this.ProjectOutlineWithinDeadline
        (args: FcsProjectOutlineArgs, cancellationToken: CancellationToken, retainUntil: Task -> unit)
        : Task<JsonNode> =
        let timeoutMs = args.timeoutMs |> Option.defaultValue 60_000

        let invalidTimeoutResult milliseconds =
            jobj
                [ "status", jstr "invalid_args"
                  "errorKind", jstr "invalid_timeout"
                  "message", jstr $"timeoutMs must be non-negative; got %d{milliseconds}."
                  "timeoutMs", jint milliseconds ]
            :> JsonNode

        let zeroTimeoutResult () =
            let issue =
                jobj
                    [ "phase", jstr "admission"
                      "status", jstr "timed_out"
                      "errorKind", jstr "project_outline_timeout"
                      "message", jstr "The end-to-end project-outline deadline was already exhausted." ]
                :> JsonNode

            let coverage =
                jobj
                    [ "complete", jbool false
                      "filesRequested", jint 0
                      "filesScanned", jint 0
                      "filesTimedOut", jint 0
                      "filesFailed", jint 0
                      "filesNotStarted", jint 0
                      "phases",
                      JsonArray([| jobj [ "phase", jstr "admission"; "status", jstr "timed_out" ] :> JsonNode |])
                      :> JsonNode
                      "issues", JsonArray([| issue |]) :> JsonNode
                      "issuesReturned", jint 1
                      "issuesTruncated", jbool false ]
                :> JsonNode

            jobj
                [ "status", jstr "unknown"
                  "errorKind", jstr "project_outline_timeout"
                  "message", jstr "fcs_project_outline timed out before protected work started."
                  "timeoutMs", jint 0
                  "coverage", coverage
                  "resultSetComplete", jbool false
                  "truncated", jbool false
                  "nextCursor", null
                  "files", JsonArray() :> JsonNode ]
            :> JsonNode

        let run () =
            task {
                let deadline = System.Diagnostics.Stopwatch.StartNew()

                let remainingMilliseconds () =
                    max 0L (int64 timeoutMs - deadline.ElapsedMilliseconds) |> int

                let ensureCanContinue () =
                    cancellationToken.ThrowIfCancellationRequested()

                    if remainingMilliseconds () <= 0 then
                        raise (TimeoutException("The fcs_project_outline end-to-end deadline was exhausted."))

                let awaitWithinDeadline (phase: string) (operation: Task<'T>) : Task<'T> =
                    task {
                        let mutable remaining = 0

                        try
                            // `operation` is already a hot task. Keep BOTH the pre-wait
                            // deadline/cancellation check and WaitAsync inside this try: expiry
                            // between task creation and this point must retain the actual worker.
                            ensureCanContinue ()
                            remaining <- remainingMilliseconds ()
                            return! operation.WaitAsync(TimeSpan.FromMilliseconds(float remaining), cancellationToken)
                        with
                        | :? TimeoutException ->
                            observeFault operation
                            retainUntil (operation :> Task)

                            return
                                raise (
                                    TimeoutException(
                                        $"Project outline phase '%s{phase}' exceeded the remaining %d{remaining} ms deadline."
                                    )
                                )
                        | :? OperationCanceledException ->
                            observeFault operation
                            retainUntil (operation :> Task)

                            return raise (OperationCanceledException(cancellationToken))
                    }

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
                        | Error reason -> invalidArg (nameof args.cursor) $"Invalid cursor: %s{reason}"

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
                    invalidArg
                        (nameof args.maxResultsPerFile)
                        $"maxResultsPerFile must be >= 0 (got {maxResultsPerFile})"

                let summaryOnly = args.summaryOnly |> Option.defaultValue true

                // ── Build regex / substring matchers ───────────────────────────────
                let filterRegex =
                    match args.filter with
                    | None -> None
                    | Some pattern ->
                        if pattern.Length > 1024 then
                            invalidArg
                                (nameof args.filter)
                                $"filter pattern must not exceed 1024 characters (got {pattern.Length})"

                        try
                            // NonBacktracking eliminates catastrophic-backtracking risk for
                            // user-supplied patterns like (a+)+$. A 250ms timeout is a belt-
                            // and-suspenders guard; NonBacktracking should never time out.
                            let opts =
                                System.Text.RegularExpressions.RegexOptions.NonBacktracking
                                ||| System.Text.RegularExpressions.RegexOptions.IgnoreCase

                            Some(System.Text.RegularExpressions.Regex(pattern, opts, TimeSpan.FromMilliseconds 250.0))
                        with ex ->
                            invalidArg (nameof args.filter) $"Invalid filter regex: %s{ex.Message}"

                let nameContains = args.nameContains |> Option.filter (fun lst -> not lst.IsEmpty)

                // Returns true when the entry name / signature passes the filter.
                let entryMatchesFilter (name: string) (signature: string) =
                    let matchesRegex =
                        match filterRegex with
                        | None -> true
                        | Some rx ->
                            try
                                rx.IsMatch(name) || rx.IsMatch(signature)
                            with :? System.Text.RegularExpressions.RegexMatchTimeoutException ->
                                false

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
                // A direct test-project outline is already explicitly scoped to tests. Default
                // inclusion must therefore keep its compile files; otherwise paths such as
                // /tests/.../Tests.fs make the whole project look empty (#239). Callers may
                // still opt out with includeTests=false.
                let includeTests =
                    args.includeTests
                    |> Option.defaultValue (FsLangMcp.ProjectHealth.isTestProjectFile projectPath)

                let filterOptions =
                    { defaultFilterOptions Outline with
                        IncludeGenerated = args.includeGeneratedFiles |> Option.defaultValue false
                        IncludeTests = includeTests
                        MaxFiles = None }

                let files = compileFiles projectPath doc

                // Sort deterministically by path so cursor offsets are stable.
                let allFiles =
                    filterProjectFiles workspaceRoot filterOptions files
                    |> fun result ->
                        { result with
                            Included = result.Included |> List.sortBy (fun f -> f.Path) }

                let fileEntries = ResizeArray<JsonNode>()

                let filtersActive = filterRegex.IsSome || nameContains.IsSome

                let unfilteredPageFiles =
                    if filtersActive then
                        []
                    else
                        allFiles.Included
                        |> List.skip (min pageOffset allFiles.Included.Length)
                        |> List.truncate pageSize

                let filesRequested =
                    if filtersActive then
                        allFiles.Included.Length
                    else
                        unfilteredPageFiles.Length

                let coverageIssueLimit = 50
                let coverageIssues = ResizeArray<JsonNode>()
                let mutable coverageIssueCount = 0
                let mutable filesScanned = 0
                let mutable filesTimedOut = 0
                let mutable filesFailed = 0
                let mutable terminalFailure: (string * string * string) option = None
                let mutable optionsPhaseStatus = "not_started"

                let addCoverageIssue (issue: JsonNode) =
                    coverageIssueCount <- coverageIssueCount + 1

                    if coverageIssues.Count < coverageIssueLimit then
                        coverageIssues.Add issue

                let mutable resolvedProjectOptions: (FSharpProjectOptions * string) option = None

                // The test-only file-outline seam predates this deadline contract and deliberately
                // bypasses real project evaluation. Production resolves the fsproj exactly once and
                // shares that immutable FCS context across every file in the page/sweep.
                match projectOutlineFileOutlineOverride with
                | Some _ -> optionsPhaseStatus <- "bypassed_for_test"
                | None ->
                    let optionsRemainingBudget () =
                        cancellationToken.ThrowIfCancellationRequested()
                        TimeSpan.FromMilliseconds(float (remainingMilliseconds ()))

                    use optionsWaiter =
                        this.AcquireFsprojEntryWithinBudget(projectPath, Some optionsRemainingBudget)

                    try
                        let! entry = awaitWithinDeadline "project_options" optionsWaiter.Operation
                        resolvedProjectOptions <- Some(entry.Options, entry.Source)
                        optionsPhaseStatus <- "complete"
                    with
                    | :? TimeoutException as ex ->
                        optionsPhaseStatus <- "timed_out"
                        terminalFailure <- Some("project_options", "timed_out", ex.Message)

                        addCoverageIssue (
                            jobj
                                [ "phase", jstr "project_options"
                                  "status", jstr "timed_out"
                                  "errorKind", jstr "project_options_timeout"
                                  "message", jstr ex.Message ]
                            :> JsonNode
                        )
                    | :? OperationCanceledException as ex ->
                        optionsPhaseStatus <- "cancelled"
                        terminalFailure <- Some("project_options", "cancelled", ex.Message)

                        addCoverageIssue (
                            jobj
                                [ "phase", jstr "project_options"
                                  "status", jstr "cancelled"
                                  "errorKind", jstr "project_outline_cancelled"
                                  "message", jstr "Project outline was cancelled during project evaluation." ]
                            :> JsonNode
                        )
                    | ex ->
                        optionsPhaseStatus <- "failed"
                        terminalFailure <- Some("project_options", "failed", ex.Message)

                        addCoverageIssue (
                            jobj
                                [ "phase", jstr "project_options"
                                  "status", jstr "failed"
                                  "errorKind", jstr "project_options_failed"
                                  "message", jstr ex.Message ]
                            :> JsonNode
                        )

                let outlineArgs filePath : FcsFileOutlineArgs =
                    { path = filePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      includePrivate = args.includePrivate
                      includeLocal = Some false
                      // Always request the full per-member output: ProjectOutline does
                      // its own summaryOnly shaping + memberCounts over these entries,
                      // so FileOutline's own summary default must NOT pre-collapse them.
                      summaryOnly = Some false
                      maxResults = None }

                let fileOutlineForProject args =
                    match projectOutlineFileOutlineOverride with
                    | Some overrideOutline -> overrideOutline args
                    | None -> this.FileOutlineCore(args, resolvedProjectOptions, ensureCanContinue)

                let rawEntriesOf (outline: JsonNode) : JsonNode array =
                    match outline["entries"] with
                    | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toArray
                    | _ -> [||]

                let postFilterEntriesOf (outline: JsonNode) =
                    let rawEntries = rawEntriesOf outline

                    if not filtersActive then
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

                let addFileEntry
                    (file: FsLangMcp.ProjectFiles.ProjectFile)
                    (outline: JsonNode)
                    (postFilterEntries: JsonNode array)
                    =
                    let filteredEntries = postFilterEntries |> Array.truncate maxResultsPerFile

                    // summaryOnly: strip per-member signature detail, keep headers; counts
                    // surface as a top-level memberCounts map per file (issue #82).
                    let containerKinds =
                        [| "module"
                           "record"
                           "union"
                           "class"
                           "interface"
                           "enum"
                           "delegate"
                           "namespace" |]

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

                // Entry filters determine which FILES are in the result set, so they must run
                // before maxFiles/cursor pagination (#238). The filtered path inspects every
                // candidate once and retains only files with at least one matching entry. The
                // common unfiltered path still outlines only the requested page.
                let filteredOutlines =
                    ResizeArray<FsLangMcp.ProjectFiles.ProjectFile * JsonNode * JsonNode array>()

                // Keep the aggregate bounded even when a large project has many unavailable
                // or truncated outlines. Counts cover every file; the issue rows are a sample.
                let filterIssueLimit = 50
                let filterIssues = ResizeArray<JsonNode>()
                let mutable filterIssueCount = 0
                let mutable filterAnalyzedFiles = 0
                let mutable filterIncompleteFiles = 0
                let mutable filterFailedFiles = 0

                let addFilterIssue (issue: JsonNode) =
                    filterIssueCount <- filterIssueCount + 1

                    if filterIssues.Count < filterIssueLimit then
                        filterIssues.Add issue

                let outlineStatusOf (outline: JsonNode) =
                    match outline["status"] with
                    | null -> "unknown"
                    | status -> status.GetValue<string>()

                let outlineSucceeded status =
                    String.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)

                let tryOutlineFile (file: FsLangMcp.ProjectFiles.ProjectFile) : Task<JsonNode option> =
                    task {
                        if terminalFailure.IsSome then
                            return None
                        else
                            try
                                ensureCanContinue ()
                                let operation = fileOutlineForProject (outlineArgs file.Path)
                                let! outline = awaitWithinDeadline "file_outline" operation
                                let status = outlineStatusOf outline

                                if outlineSucceeded status then
                                    filesScanned <- filesScanned + 1
                                else
                                    filesFailed <- filesFailed + 1

                                    addCoverageIssue (
                                        jobj
                                            [ "phase", jstr "file_outline"
                                              "file", jstr file.Path
                                              "status", jstr status
                                              "errorKind", jstr "outline_unavailable"
                                              "message",
                                              (match outline["message"] with
                                               | null -> null
                                               | message -> message.DeepClone()) ]
                                        :> JsonNode
                                    )

                                return Some outline
                            with
                            | :? TimeoutException as ex ->
                                filesTimedOut <- filesTimedOut + 1
                                terminalFailure <- Some("file_outline", "timed_out", ex.Message)

                                addCoverageIssue (
                                    jobj
                                        [ "phase", jstr "file_outline"
                                          "file", jstr file.Path
                                          "status", jstr "timed_out"
                                          "errorKind", jstr "file_outline_timeout"
                                          "message", jstr ex.Message ]
                                    :> JsonNode
                                )

                                return None
                            | :? OperationCanceledException ->
                                filesFailed <- filesFailed + 1
                                terminalFailure <- Some("file_outline", "cancelled", "Project outline was cancelled.")

                                addCoverageIssue (
                                    jobj
                                        [ "phase", jstr "file_outline"
                                          "file", jstr file.Path
                                          "status", jstr "cancelled"
                                          "errorKind", jstr "project_outline_cancelled"
                                          "message", jstr "Project outline was cancelled while this file was in flight." ]
                                    :> JsonNode
                                )

                                return None
                            | ex ->
                                filesFailed <- filesFailed + 1

                                addCoverageIssue (
                                    jobj
                                        [ "phase", jstr "file_outline"
                                          "file", jstr file.Path
                                          "status", jstr "failed"
                                          "errorKind", jstr "file_outline_failed"
                                          "message", jstr ex.Message ]
                                    :> JsonNode
                                )

                                return None
                    }

                if filtersActive then
                    let candidates = allFiles.Included |> List.toArray
                    let mutable candidateIndex = 0

                    while candidateIndex < candidates.Length && terminalFailure.IsNone do
                        let file = candidates[candidateIndex]
                        candidateIndex <- candidateIndex + 1
                        let! outlineResult = tryOutlineFile file

                        match outlineResult with
                        | None -> ()
                        | Some outline ->
                            let outlineStatus = outlineStatusOf outline

                            if outlineSucceeded outlineStatus then
                                let postFilterEntries = postFilterEntriesOf outline

                                if postFilterEntries.Length > 0 then
                                    filteredOutlines.Add(file, outline, postFilterEntries)

                                let entriesComplete =
                                    match outline["entriesComplete"] with
                                    | null -> false
                                    | complete -> complete.GetValue<bool>()

                                if entriesComplete then
                                    filterAnalyzedFiles <- filterAnalyzedFiles + 1
                                else
                                    filterIncompleteFiles <- filterIncompleteFiles + 1

                                    let returnedCount =
                                        match outline["returnedEntryCount"] with
                                        | null -> rawEntriesOf outline |> Array.length
                                        | count -> count.GetValue<int>()

                                    let totalCount =
                                        match outline["totalDefinitionCount"] with
                                        | null -> max returnedCount 1
                                        | count -> count.GetValue<int>()

                                    addFilterIssue (
                                        jobj
                                            [ "file", jstr file.Path
                                              "status", jstr "incomplete"
                                              "errorKind", jstr "outline_entries_incomplete"
                                              "message",
                                              jstr
                                                  $"File outline returned %d{returnedCount} of %d{totalCount} definitions; filter matches beyond the returned entries remain unknown." ]
                                        :> JsonNode
                                    )
                            else
                                filterFailedFiles <- filterFailedFiles + 1

                                addFilterIssue (
                                    jobj
                                        [ "file", jstr file.Path
                                          "status", jstr outlineStatus
                                          "errorKind", jstr "outline_unavailable"
                                          "message",
                                          (match outline["message"] with
                                           | null -> null
                                           | message -> message.DeepClone()) ]
                                    :> JsonNode
                                )

                let totalFileCount =
                    if filtersActive then
                        filteredOutlines.Count
                    else
                        allFiles.Included.Length

                let mutable returnedFileCount = 0

                if filtersActive then
                    let page =
                        filteredOutlines
                        |> Seq.skip (min pageOffset totalFileCount)
                        |> Seq.truncate pageSize
                        |> Seq.toArray

                    returnedFileCount <- page.Length

                    for (file, outline, postFilterEntries) in page do
                        addFileEntry file outline postFilterEntries
                else
                    let pageFiles = unfilteredPageFiles |> List.toArray
                    let mutable pageIndex = 0

                    while pageIndex < pageFiles.Length && terminalFailure.IsNone do
                        let file = pageFiles[pageIndex]
                        pageIndex <- pageIndex + 1
                        let! outlineResult = tryOutlineFile file

                        match outlineResult with
                        | Some outline ->
                            addFileEntry file outline (rawEntriesOf outline)
                            returnedFileCount <- returnedFileCount + 1
                        | None -> ()

                // ── Pagination envelope ─────────────────────────────────────────────
                let rawPaginationFields =
                    Cursor.paginationFields "files" totalFileCount pageOffset pageSize returnedFileCount

                let filesNotStarted =
                    max 0 (filesRequested - filesScanned - filesTimedOut - filesFailed)

                let operationalComplete =
                    terminalFailure.IsNone
                    && filesTimedOut = 0
                    && filesFailed = 0
                    && filesNotStarted = 0

                let filterDiscoveryComplete =
                    not filtersActive || (operationalComplete && filterIssueCount = 0)

                let resultSetComplete = operationalComplete && filterDiscoveryComplete

                let paginationFields =
                    if not resultSetComplete then
                        rawPaginationFields
                        |> List.filter (fun (name, _) -> name <> "truncated" && name <> "nextCursor")
                        |> fun fields ->
                            fields
                            @ [ "truncated", jbool true
                                "nextCursor", null
                                "totalEstimateIsLowerBound", jbool (filtersActive && not filterDiscoveryComplete)
                                "paginationRestartRequired", jbool true ]
                    else
                        rawPaginationFields
                        @ [ "totalEstimateIsLowerBound", jbool false
                            "paginationRestartRequired", jbool false ]

                // Aggregate over ALL in-scope files, not just the current page: per-file
                // outlineStatus errors can scroll past pagination, this cannot (#160).
                // Filter-excluded entries (generated, obj/bin, tests) are deliberately not
                // counted — absent-before-build is normal for them; the unfiltered view is
                // project_health.missingFiles.
                let unresolvedFiles =
                    allFiles.Included
                    |> List.map (fun f -> f.Path)
                    |> List.filter (File.Exists >> not)

                let filterCoverage =
                    if filtersActive then
                        let complete = filterDiscoveryComplete
                        let combinedIssues = ResizeArray<JsonNode>()

                        let combinedIssueKeys =
                            System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)

                        let addCombinedIssue (issue: JsonNode) =
                            let value (name: string) =
                                match issue[name] with
                                | null -> ""
                                | node -> node.ToJsonString()

                            let fileKey = value "file"
                            let statusKey = value "status"
                            let key = $"%s{fileKey}|%s{statusKey}"

                            if combinedIssueKeys.Add key && combinedIssues.Count < filterIssueLimit then
                                combinedIssues.Add(issue.DeepClone())

                        for issue in filterIssues do
                            addCombinedIssue issue

                        for issue in coverageIssues do
                            addCombinedIssue issue

                        let combinedIssueCount = combinedIssueKeys.Count

                        let combinedIssuesTruncated =
                            filterIssueCount > filterIssues.Count
                            || coverageIssueCount > coverageIssues.Count
                            || combinedIssueCount > combinedIssues.Count

                        jobj
                            [ "complete", jbool complete
                              "filesRequested", jint allFiles.Included.Length
                              "filesAnalyzed", jint filterAnalyzedFiles
                              "filesIncomplete", jint filterIncompleteFiles
                              "filesTimedOut", jint filesTimedOut
                              "filesFailed", jint (max filterFailedFiles filesFailed)
                              "filesNotStarted", jint filesNotStarted
                              "matchingFiles", jint totalFileCount
                              "matchingFilesIsLowerBound", jbool (not complete)
                              "issues", JsonArray(combinedIssues.ToArray()) :> JsonNode
                              "issuesReturned", jint combinedIssues.Count
                              "issuesTruncated", jbool combinedIssuesTruncated
                              "hint",
                              (if complete then
                                   null
                               else
                                   jstr
                                       "Some file outlines were unavailable or did not return every definition, so totalEstimate.files and matchingFiles are lower bounds. Inspect filterCoverage.issues and run check before treating an absent match as exhaustive.") ]
                        :> JsonNode
                    else
                        null

                let filePhaseStatus =
                    match terminalFailure with
                    | Some("project_options", _, _) -> "not_started"
                    | Some("file_outline", status, _) -> status
                    | Some(_, status, _) -> status
                    | None when filesFailed > 0 -> "partial"
                    | None -> "complete"

                let phases =
                    JsonArray(
                        [| jobj [ "phase", jstr "project_options"; "status", jstr optionsPhaseStatus ] :> JsonNode
                           jobj
                               [ "phase", jstr "file_outlines"
                                 "status", jstr filePhaseStatus
                                 "filesRequested", jint filesRequested
                                 "filesScanned", jint filesScanned ]
                           :> JsonNode |]
                    )

                let coverage =
                    jobj
                        [ "complete", jbool operationalComplete
                          "filesRequested", jint filesRequested
                          "filesScanned", jint filesScanned
                          "filesTimedOut", jint filesTimedOut
                          "filesFailed", jint filesFailed
                          "filesNotStarted", jint filesNotStarted
                          "phases", phases :> JsonNode
                          "issues", JsonArray(coverageIssues.ToArray()) :> JsonNode
                          "issuesReturned", jint coverageIssues.Count
                          "issuesTruncated", jbool (coverageIssueCount > coverageIssues.Count) ]
                    :> JsonNode

                let responseStatus =
                    if resultSetComplete then "ok"
                    elif filesScanned > 0 then "partial"
                    else "unknown"

                let terminalFields =
                    match terminalFailure with
                    | None -> []
                    | Some(phase, terminalStatus, message) ->
                        let errorKind =
                            match phase, terminalStatus with
                            | "project_options", "timed_out" -> "project_options_timeout"
                            | "project_options", "cancelled" -> "project_outline_cancelled"
                            | "project_options", _ -> "project_options_failed"
                            | "file_outline", "timed_out" -> "file_outline_timeout"
                            | "file_outline", "cancelled" -> "project_outline_cancelled"
                            | _ -> "project_outline_incomplete"

                        [ "errorKind", jstr errorKind; "message", jstr message ]

                let baseFields =
                    [ "status", jstr responseStatus
                      "projectPath", jstr projectPath
                      "workspaceRoot", jstr workspaceRoot
                      "summaryOnly", jbool summaryOnly
                      "timeoutMs", jint timeoutMs
                      "resultSetComplete", jbool resultSetComplete
                      "coverage", coverage
                      "filterCoverage", filterCoverage
                      "filterSummary", filterSummaryToJson allFiles :> JsonNode
                      "unresolvedFiles", JsonArray(unresolvedFiles |> List.map jstr |> List.toArray) :> JsonNode
                      "files", JsonArray(fileEntries.ToArray()) :> JsonNode ]
                    @ terminalFields

                return jobj (baseFields @ paginationFields) :> JsonNode
            }

        if timeoutMs < 0 then
            Task.FromResult(invalidTimeoutResult timeoutMs)
        elif timeoutMs = 0 then
            Task.FromResult(zeroTimeoutResult ())
        else
            run ()

    member this.ProjectOutline(args: FcsProjectOutlineArgs) : Task<JsonNode> =
        this.ProjectOutlineWithinDeadline(args, CancellationToken.None, ignore)

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
            | Some inputPath ->
                // These are single-project tools, but projectPath defaults to the active
                // set_project — which is the .sln/.slnx itself in solution mode. Feeding a
                // solution file straight to ResolveFsprojOptions makes WorkspaceLoader reject
                // it with a cryptic "Unable to load project options" (#100 review). Resolve a
                // solution/dir to its single .fsproj (as the sweep tools do via listProjects),
                // and give a clear, actionable error when it's genuinely ambiguous.
                let fsproj =
                    let ext = Path.GetExtension(inputPath)

                    if String.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase) then
                        inputPath
                    else
                        match SolutionParsing.listProjects inputPath with
                        | [| one |] -> one
                        | [||] -> inputPath // not a solution we can expand — let ResolveFsprojOptions surface the real error
                        | many ->
                            let names = many |> Array.map Path.GetFileName |> String.concat ", "

                            raise (
                                InvalidOperationException(
                                    $"'{Path.GetFileName inputPath}' contains multiple projects ({names}); this tool operates on ONE project — pass an explicit .fsproj as projectPath."
                                )
                            )

                let! options, optionsSource = this.ResolveFsprojOptions(fsproj)
                let cacheKey = analysisSnapshotKey options

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

            // #191: the packageId may name a package whose assembly is called something else
            // (Microsoft.Orleans.Core.Abstractions → Orleans.Core.Abstractions.dll), so match
            // against the project's own restore graph as well as the assembly SimpleName.
            let packageAssemblies =
                NugetPackageMap.forProject options.ProjectFileName options.OtherOptions

            let matchingAssemblies =
                assemblies
                |> List.filter (fun asm -> assemblyMatchesPackageId packageAssemblies asm packageId)

            let matchedAssemblyNames =
                matchingAssemblies
                |> List.map assemblySimpleName
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
                        let asmName = assemblySimpleName asm

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

            // Additive: only present on a total miss, so the ok-path shape is unchanged (#191).
            let missFields =
                if List.isEmpty matchingAssemblies then
                    NugetPackageMap.missFields packageAssemblies packageId
                else
                    []

            let paginationFields =
                Cursor.paginationFields "types" allEntities.Length pageOffset pageSize pageEntries.Length

            return jobj (baseFields @ missFields @ paginationFields) :> JsonNode
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

            // #191: see NugetTypes — packageId may name a package whose assembly is called
            // something else, so consult the project's restore graph too.
            let packageAssemblies =
                NugetPackageMap.forProject options.ProjectFileName options.OtherOptions

            let matchingAssemblies =
                assemblies
                |> List.filter (fun asm -> assemblyMatchesPackageId packageAssemblies asm packageId)

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

            // FCS reports imported F# `internal` members as private and models CLR
            // `protected internal` through F# source visibility as protected. Read the
            // assembly's ECMA-335 method flags for an exact answer when metadata is
            // available, without loading or executing the target assembly (#223).
            let referencePathByAssemblyName =
                let projectDirectory = Path.GetDirectoryName(options.ProjectFileName)

                let tryOptionReferencePath (optionText: string) =
                    let rawPath =
                        if optionText.StartsWith("-r:", StringComparison.Ordinal) then
                            Some(optionText.Substring 3)
                        elif optionText.StartsWith("--reference:", StringComparison.Ordinal) then
                            Some(optionText.Substring 12)
                        else
                            None

                    rawPath
                    |> Option.bind (fun path ->
                        try
                            let candidate = path.Trim().Trim('"')

                            if Path.IsPathFullyQualified candidate then
                                Some(Path.GetFullPath candidate)
                            else
                                Some(Path.GetFullPath(Path.Combine(projectDirectory, candidate)))
                        with _ ->
                            None)

                let binaryReferences =
                    options.OtherOptions
                    |> Array.choose tryOptionReferencePath

                let projectReferences =
                    options.ReferencedProjects
                    |> Array.choose (fun referencedProject ->
                        try
                            let path = referencedProject.OutputFile
                            if String.IsNullOrWhiteSpace path then None else Some path
                        with _ ->
                            None)

                Array.append binaryReferences projectReferences
                |> Array.map (fun path ->
                    if Path.IsPathFullyQualified path then
                        Path.GetFullPath path
                    else
                        Path.GetFullPath(Path.Combine(projectDirectory, path)))
                |> Array.filter File.Exists
                |> Array.choose (fun path ->
                    let simpleName = Path.GetFileNameWithoutExtension path

                    if String.IsNullOrWhiteSpace simpleName then
                        None
                    else
                        Some(simpleName, path))
                |> Map.ofArray

            let metadataResolvers =
                System.Collections.Generic.Dictionary<
                    string,
                    (string -> string -> MemberMetadata option) option
                 >(
                    if OperatingSystem.IsWindows() then
                        StringComparer.OrdinalIgnoreCase
                    else
                        StringComparer.Ordinal
                )

            let exactMemberMetadata (m: FSharpMemberOrFunctionOrValue) =
                try
                    let assemblyPath =
                        match m.Assembly.FileName with
                        | Some path when File.Exists path -> Some path
                        | _ ->
                            let simpleName =
                                try m.Assembly.SimpleName with _ -> ""

                            referencePathByAssemblyName
                            |> Map.tryFind simpleName

                    match assemblyPath with
                    | None -> None
                    | Some assemblyPath ->
                        let declaringTypeNames =
                            [ try
                                  match m.DeclaringEntity with
                                  | Some entity when not (String.IsNullOrWhiteSpace entity.FullName) ->
                                      yield entity.FullName
                                  | _ -> ()
                              with _ ->
                                  ()

                              try
                                  match m.ApparentEnclosingEntity with
                                  | Some entity when not (String.IsNullOrWhiteSpace entity.FullName) ->
                                      yield entity.FullName
                                  | _ -> ()
                              with _ ->
                                  () ]
                            |> List.distinct

                        let memberNames =
                            [ try yield m.CompiledName with _ -> ()
                              try yield m.DisplayName with _ -> ()
                              try
                                  if m.IsConstructor then
                                      yield ".ctor"
                              with _ ->
                                  () ]
                            |> List.filter (String.IsNullOrWhiteSpace >> not)
                            |> List.distinct

                        candidateAssemblyPaths assemblyPath
                        |> Array.tryPick (fun candidatePath ->
                            let resolver =
                                match metadataResolvers.TryGetValue candidatePath with
                                | true, cached -> cached
                                | false, _ ->
                                    let created = tryCreateMemberResolver candidatePath
                                    metadataResolvers.Add(candidatePath, created)
                                    created

                            resolver
                            |> Option.bind (fun resolve ->
                                declaringTypeNames
                                |> List.tryPick (fun typeName ->
                                    memberNames |> List.tryPick (resolve typeName))))
                with _ ->
                    None

            let passesMemberAccessibility (m: FSharpMemberOrFunctionOrValue) =
                if includeNonPublic then
                    true
                else
                    let acc = memberAccessibilityString m
                    acc = "public"
                    || acc = "protected"
                    || acc = "protected internal"
                    || acc = "unknown"

            let passesFieldAccessibility (f: FSharpField) =
                if includeNonPublic then
                    true
                else
                    let acc = fieldAccessibilityString f
                    acc = "public"
                    || acc = "protected"
                    || acc = "protected internal"
                    || acc = "unknown"

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
                                yield referencedMemberToJson (exactMemberMetadata m) m

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

            // Additive: only present on a miss, so the ok-path shape is unchanged (#191).
            // Two distinct miss modes — no assembly resolved for the packageId (the #100 field
            // failure), versus the assembly resolved but carries no such type.
            let missFields =
                if List.isEmpty matchingAssemblies then
                    NugetPackageMap.missFields packageAssemblies packageId
                elif List.isEmpty matchedEntities then
                    let assemblyNames =
                        matchingAssemblies |> List.map assemblySimpleName |> String.concat ", "

                    let hint =
                        $"packageId '%s{packageId}' resolved to assembly %s{assemblyNames}, but it exports no type named '%s{typeName}'. Run fcs_nuget_types with the same packageId to list the type names it does export, or fcs_referenced_symbols to search every loaded assembly."

                    [ ("hint", jstr hint) ]
                else
                    []

            let paginationFields =
                Cursor.paginationFields "members" dedupedMembers.Length pageOffset pageSize pageEntries.Length

            return jobj (baseFields @ missFields @ paginationFields) :> JsonNode
        }

    /// Emit the project's OWN public API surface — every public (and, when
    /// includeInternal=true, internal) type plus its public members with signatures —
    /// sorted stably by fullName then member name so two version snapshots diff cleanly.
    /// Source is FSharpCheckProjectResults.AssemblySignature (the project's own inferred
    /// signature), NOT referenced assemblies. `private` declarations are never emitted.
    member this.PublicApi(args: FcsPublicApiArgs) : Task<JsonNode> =
        task {
            if args.projectPath.IsNone then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr "projectPath is required (or call set_project first to set the active project)" ]
                    :> JsonNode
            else

            let includeInternal = defaultArg args.includeInternal false
            let requested = defaultArg args.maxResults 100
            let pageSize = min (max 1 requested) 1000

            let pageOffsetResult =
                match args.cursor with
                | None -> Ok 0
                | Some cursorStr -> Cursor.tryDecode cursorStr |> Result.map (fun p -> p.offset)

            match pageOffsetResult with
            | Error reason ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr $"Invalid cursor: %s{reason}" ]
                    :> JsonNode
            | Ok pageOffset ->

            let! results, options, optionsSource = this.EnsureProjectResults args.projectPath

            // Public-only by default; includeInternal also admits `internal`. `private`
            // never passes. "unknown" (synthetic symbols FCS throws on) is treated as
            // public-visible, matching fcs_referenced_symbols / fcs_nuget_types.
            let accPasses (acc: string) =
                if includeInternal then
                    acc = "public" || acc = "internal" || acc = "unknown"
                else
                    acc = "public" || acc = "unknown"

            let nsFilterLower =
                args.namespaceFilter
                |> Option.map (fun s -> s.Trim())
                |> Option.filter (fun s -> s.Length > 0)
                |> Option.map (fun s -> s.ToLowerInvariant())

            // ── Per-member signature/accessibility helpers (reuse the shared kind/acc
            //    helpers; only the bare signature strings are built locally) ──────────
            let fieldSignature (f: FSharpField) =
                try
                    $"{f.Name}: {publicApiTypeName f.FieldType}"
                with _ ->
                    try f.Name with _ -> "<unknown>"

            let isBackingField (f: FSharpField) =
                try
                    f.IsCompilerGenerated
                    || f.IsNameGenerated
                    || (let n = f.Name in
                        not (isNull n) && (n.Contains "k__BackingField" || n.Contains "@"))
                with _ ->
                    false

            let unionCaseSignature (uc: FSharpUnionCase) =
                try
                    if uc.Fields.Count = 0 then
                        uc.Name
                    else
                        let fieldTypes =
                            uc.Fields
                            |> Seq.map (fun f -> try publicApiTypeName f.FieldType with _ -> "?")
                            |> String.concat " * "

                        $"{uc.Name} of {fieldTypes}"
                with _ ->
                    try uc.Name with _ -> "<unknown>"

            let unionCaseAccessibility (uc: FSharpUnionCase) =
                try
                    let acc = uc.Accessibility

                    if acc.IsPrivate then "private"
                    elif acc.IsInternal then "internal"
                    elif acc.IsPublic then "public"
                    else "unknown"
                with _ ->
                    "unknown"

            // Members of one entity: methods/properties/functions/values (skipping
            // compiler-generated members and property accessors), record/struct/class
            // fields (skipping backing fields), and union cases — accessibility-filtered,
            // de-duplicated, and stably sorted by (name, kind, signature).
            let membersOf (entity: FSharpEntity) =
                // For records, the field-getter property duplicates the field row; drop
                // those synthesized properties so a record's surface is its fields.
                let recordFieldNames =
                    try
                        if entity.IsFSharpRecord then
                            entity.FSharpFields |> Seq.map (fun f -> f.Name) |> Set.ofSeq
                        else
                            Set.empty
                    with _ ->
                        Set.empty

                let fromMfvs =
                    try
                        entity.MembersFunctionsAndValues
                        |> Seq.choose (fun m ->
                            let isAccessor =
                                try m.IsPropertyGetterMethod || m.IsPropertySetterMethod with _ -> false

                            let isCompilerGenerated = try m.IsCompilerGenerated with _ -> false

                            let isRecordFieldProperty =
                                try entity.IsFSharpRecord && m.IsProperty && recordFieldNames.Contains m.DisplayName with _ -> false

                            let acc = memberAccessibilityString m

                            if not isAccessor && not isCompilerGenerated && not isRecordFieldProperty && accPasses acc then
                                Some
                                    {| name = (try m.DisplayName with _ -> "<unknown>")
                                       kind = memberKindString m
                                       signature = memberSignatureWith publicApiTypeName m
                                       accessibility = acc |}
                            else
                                None)
                        |> Seq.toList
                    with _ ->
                        []

                let fromFields =
                    try
                        if entity.IsFSharpRecord || entity.IsValueType || entity.IsClass then
                            entity.FSharpFields
                            |> Seq.choose (fun f ->
                                let acc = fieldAccessibilityString f

                                if not (isBackingField f) && accPasses acc then
                                    Some
                                        {| name = (try f.Name with _ -> "<unknown>")
                                           kind = "field"
                                           signature = fieldSignature f
                                           accessibility = acc |}
                                else
                                    None)
                            |> Seq.toList
                        else
                            []
                    with _ ->
                        []

                let fromUnionCases =
                    try
                        if entity.IsFSharpUnion then
                            entity.UnionCases
                            |> Seq.choose (fun uc ->
                                let acc = unionCaseAccessibility uc

                                if accPasses acc then
                                    Some
                                        {| name = (try uc.Name with _ -> "<unknown>")
                                           kind = "union-case"
                                           signature = unionCaseSignature uc
                                           accessibility = acc |}
                                else
                                    None)
                            |> Seq.toList
                        else
                            []
                    with _ ->
                        []

                fromMfvs @ fromFields @ fromUnionCases
                |> List.distinctBy (fun m -> m.name, m.kind, m.signature)
                |> List.sortBy (fun m -> m.name, m.kind, m.signature)

            let entityFullName (e: FSharpEntity) =
                try
                    match e.FullName |> Option.ofObj with
                    | Some fn when fn.Length > 0 -> fn
                    | _ -> e.DisplayName
                with _ ->
                    try e.DisplayName with _ -> "<unknown>"

            let topEntities =
                try
                    results.AssemblySignature.Entities :> seq<FSharpEntity>
                with _ ->
                    Seq.empty

            // walkEntities flattens nested entities, so skipping a namespace container
            // never drops the modules/types declared inside it.
            let allEntities =
                topEntities
                |> Seq.collect walkEntities
                |> Seq.choose (fun e ->
                    let isNamespace = try e.IsNamespace with _ -> false

                    if isNamespace then
                        None
                    else
                        let acc = entityAccessibilityString e

                        if not (accPasses acc) then
                            None
                        else
                            let fullName = entityFullName e

                            let nsOk =
                                match nsFilterLower with
                                | None -> true
                                | Some f -> fullName.ToLowerInvariant().Contains(f)

                            if not nsOk then
                                None
                            else
                                Some
                                    {| fullName = fullName
                                       kind = entityKindString e
                                       accessibility = acc
                                       members = membersOf e |})
                |> Seq.toList
                |> List.sortBy (fun e -> e.fullName, e.kind)

            let totalEntityCount = allEntities.Length
            let totalMemberCount = allEntities |> List.sumBy (fun e -> e.members.Length)

            // Type-count page (existing behavior, kept as the outer cap): up to
            // `pageSize` entities starting at pageOffset. An entity's member list is
            // never split across pages.
            let countPageEntities =
                allEntities
                |> List.skip (min pageOffset totalEntityCount)
                |> List.truncate pageSize

            // Built exactly as before (one node per entity in the count-page) — the
            // budget close below operates on the already-serialized nodes so it never
            // needs its own type annotation for the entity's anonymous record shape.
            let countPageNodes =
                countPageEntities
                |> List.map (fun e ->
                    let memberNodes =
                        e.members
                        |> List.map (fun m ->
                            jobj
                                [ "name", jstr m.name
                                  "kind", jstr m.kind
                                  "signature", jstr m.signature
                                  "accessibility", jstr m.accessibility ]
                            :> JsonNode)
                        |> List.toArray

                    jobj
                        [ "fullName", jstr e.fullName
                          "kind", jstr e.kind
                          "accessibility", jstr e.accessibility
                          "members", JsonArray(memberNodes) :> JsonNode ]
                    :> JsonNode)
                |> List.toArray

            // #206: close the page early on serialized size too. A handful of
            // API-dense types (long member lists) can blow responseCharBudget well
            // before pageSize entities are reached — the #100 field failure (default
            // call on a 2.5k-line project spilled to a client-side file) even though
            // maxResults left plenty of "room" by count. Walk the pre-built nodes and
            // stop once the running total would cross the budget; always keep at least
            // one entity (runningChars starts at 0, so the first node is never cut) so
            // a single oversized type can never yield an empty page.
            let mutable runningChars = 0
            let mutable budgetClosed = false

            let entityNodes =
                countPageNodes
                |> Array.takeWhile (fun node ->
                    // #206: shared `Types.renderedLength` — the SHIPPED (indented)
                    // length, not compact `ToJsonString()` — see `responseCharBudget`
                    // comment above for the full accounting, including the per-line
                    // depth-nesting penalty this constant already budgets for.
                    let nodeChars = renderedLength node

                    if runningChars > 0 && runningChars + nodeChars > responseCharBudget then
                        budgetClosed <- true
                        false
                    else
                        runningChars <- runningChars + nodeChars
                        true)

            let returnedCount = entityNodes.Length

            let baseFields =
                [ "status", jstr "ok"
                  "project", jstr options.ProjectFileName
                  "optionsSource", jstr optionsSource
                  "includeInternal", jbool includeInternal
                  "namespaceFilter",
                  (match args.namespaceFilter with
                   | Some s when not (String.IsNullOrWhiteSpace s) -> jstr s
                   | _ -> null)
                  "entityCount", jint totalEntityCount
                  "memberCount", jint totalMemberCount
                  "entities", JsonArray(entityNodes) :> JsonNode ]

            let paginationFields =
                Cursor.paginationFields "entities" totalEntityCount pageOffset pageSize returnedCount

            // Additive-only (#206): present only when this page closed BECAUSE of the
            // char budget rather than maxResults / end-of-list, so callers can tell the
            // two close reasons apart (both leave `truncated: true`).
            let budgetFields =
                if budgetClosed then [ ("truncatedByBudget", jbool true) ] else []

            // Additive-only (#206): whenever the page closed early (budget OR count)
            // with more entities remaining, name namespaceFilter with 2-3 REAL
            // namespaces pulled from the remainder — copy-pasteable, not a placeholder —
            // so the "retry narrower" answer ships before any client spill (#100).
            let hintFields =
                let remaining =
                    allEntities |> List.skip (min (pageOffset + returnedCount) totalEntityCount)

                if List.isEmpty remaining then
                    []
                else
                    let namespaceOf (fullName: string) =
                        let idx = fullName.LastIndexOf('.')
                        if idx > 0 then fullName.Substring(0, idx) else fullName

                    let topNamespaces =
                        remaining
                        |> List.countBy (fun e -> namespaceOf e.fullName)
                        |> List.sortByDescending snd
                        |> List.truncate 3
                        |> List.map (fun (ns, _) -> $"'%s{ns}'")
                        |> String.concat ", "

                    let reason =
                        if budgetClosed then
                            $"the ~%d{responseCharBudget}-char response budget"
                        else
                            $"maxResults=%d{pageSize}"

                    let entityWord = if remaining.Length = 1 then "entity" else "entities"

                    let hint =
                        $"Page closed at {reason} with {remaining.Length} more {entityWord} beyond this page. Narrow with namespaceFilter to pull just what you need — top namespaces in the remainder: {topNamespaces}. Or keep paging with the returned nextCursor for the rest."

                    [ ("hint", jstr hint) ]

            return jobj (baseFields @ paginationFields @ budgetFields @ hintFields) :> JsonNode
        }

    /// Read-only ".fsi drift" preview for one implementation file. Type-checks the .fs
    /// WITHOUT its sibling .fsi so the impl's true public surface is visible, then diffs
    /// that surface against the .fsi (parsed as a signature file): members public in the
    /// impl but absent from the .fsi are silently hidden from the public surface (the #74
    /// core pain) and land in missingFromSig; .fsi entries with no impl match → staleInSig.
    member this.SignatureStatus(args: FcsSignatureStatusArgs) : Task<JsonNode> =
        // Collect declared leaf names (val/type/module) from a parsed .fsi signature tree.
        // Defined OUTSIDE `task {}` so the resumable state machine stays statically
        // compilable (FS3511); it closes over no task-local state.
        let rec collectSigDecls (decls: SynModuleSigDecl list) : (string * string) list =
            decls
            |> List.collect (fun d ->
                match d with
                | SynModuleSigDecl.Val(valSig = SynValSig(ident = SynIdent(id, _))) -> [ (id.idText, "val") ]
                | SynModuleSigDecl.Types(types = types) ->
                    types
                    |> List.choose (fun (SynTypeDefnSig(typeInfo = SynComponentInfo(longId = lid))) ->
                        lid |> List.tryLast |> Option.map (fun i -> i.idText, "type"))
                | SynModuleSigDecl.NestedModule(moduleInfo = SynComponentInfo(longId = lid); moduleDecls = nested) ->
                    let inner = collectSigDecls nested

                    match lid |> List.tryLast with
                    | Some i -> (i.idText, "module") :: inner
                    | None -> inner
                // Open / HashDirective / Exception / ModuleAbbrev / NamespaceFragment carry
                // no module-level surface names to diff against the impl.
                | _ -> [])

        task {
            match ArgsValidation.requireNonBlank "path" args.path with
            | Error envelope -> return envelope
            | Ok rawPath ->

            let implPath = normalizePath rawPath

            // Guard: the input must be an implementation .fs, not a signature .fsi.
            let isFsi = implPath.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)
            let isFs = implPath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)

            if isFsi || not isFs then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr "path must be an implementation .fs file (not a .fsi signature file)" ]
                    :> JsonNode
            else

            let textProvided = args.text |> Option.filter (fun t -> not (String.IsNullOrEmpty t))

            if textProvided.IsNone && not (File.Exists implPath) then
                return
                    jobj
                        [ "status", jstr "file_not_found"
                          "message", jstr $"Implementation file not found: %s{implPath}"
                          "implPath", jstr implPath ]
                    :> JsonNode
            else

            let sigPath = Path.ChangeExtension(implPath, ".fsi") |> normalizePath
            let hasSignatureFile = File.Exists sigPath

            let source = textProvided |> Option.defaultWith (fun () -> File.ReadAllText implPath)
            let sourceText = SourceText.ofString source

            // Resolve options (projectPath → nearest .fsproj → script), then strip the sibling
            // .fsi from the compile inputs so the impl is checked unconstrained — that exposes
            // the public members a present .fsi would otherwise hide.
            let! options, optionsSource = this.ResolveProjectOptions(implPath, source, args.projectPath, None)

            let implOnlyOptions =
                { options with
                    SourceFiles =
                        options.SourceFiles
                        |> Array.filter (fun f ->
                            not (String.Equals(normalizePath f, sigPath, StringComparison.OrdinalIgnoreCase))) }

            let! _, checkAnswer =
                checker.ParseAndCheckFileInProject(implPath, 0, sourceText, implOnlyOptions) |> asTask

            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted ->
                return
                    jobj
                        [ "status", jstr "check_aborted"
                          "implPath", jstr implPath
                          "sigPath", (if hasSignatureFile then jstr sigPath else null)
                          "hasSignatureFile", jbool hasSignatureFile
                          "message", jstr "Type checking was aborted; the public surface is unavailable." ]
                    :> JsonNode
            | FSharpCheckFileAnswer.Succeeded checkResults ->

            // ── Local .fsi-ready preview helpers ───────────────────────────────────
            let fmtType (t: FSharpType) =
                try
                    t.Format(FSharpDisplayContext.Empty)
                with _ ->
                    try typeName t with _ -> "?"

            let valPreview (m: FSharpMemberOrFunctionOrValue) =
                let t = try fmtType m.FullType with _ -> "?"
                $"val {m.DisplayName}: {t}"

            let moduleMemberKind (m: FSharpMemberOrFunctionOrValue) =
                try
                    if m.CurriedParameterGroups |> Seq.exists (fun g -> g.Count > 0) then "function" else "value"
                with _ ->
                    "value"

            let typePreview (e: FSharpEntity) =
                try
                    if e.IsFSharpRecord then
                        let fields =
                            e.FSharpFields
                            |> Seq.filter (fun f -> not (try f.IsCompilerGenerated || f.IsNameGenerated with _ -> false))
                            |> Seq.map (fun f -> $"{f.Name}: {fmtType f.FieldType}")
                            |> String.concat "; "

                        $"type {e.DisplayName} = {{ {fields} }}"
                    elif e.IsFSharpUnion then
                        let cases =
                            e.UnionCases
                            |> Seq.map (fun uc ->
                                if uc.Fields.Count = 0 then
                                    uc.Name
                                else
                                    let ts =
                                        uc.Fields |> Seq.map (fun f -> fmtType f.FieldType) |> String.concat " * "

                                    $"{uc.Name} of {ts}")
                            |> String.concat " | "

                        $"type {e.DisplayName} = {cases}"
                    else
                        $"type {e.DisplayName}"
                with _ ->
                    $"type {e.DisplayName}"

            let isPublicAcc (acc: string) = acc = "public" || acc = "unknown"

            // ── The impl's own definitions (this file only) ────────────────────────
            let definitionUses =
                try
                    checkResults.GetAllUsesOfAllSymbolsInFile()
                    |> Seq.filter (fun su ->
                        su.IsFromDefinition
                        && String.Equals(normalizePath su.FileName, implPath, StringComparison.OrdinalIgnoreCase))
                    |> Seq.toList
                with _ ->
                    []

            // Curated public surface: top-level module values/functions + types, each with
            // a .fsi-ready preview line. Nested-module members flatten in by leaf name.
            let publicMembers =
                definitionUses
                |> List.choose (fun su ->
                    match su.Symbol with
                    | :? FSharpMemberOrFunctionOrValue as m ->
                        let isTopLevelLet =
                            try
                                m.IsModuleValueOrMember
                                && not m.IsMember
                                && not m.IsPropertyGetterMethod
                                && not m.IsPropertySetterMethod
                                && not m.IsCompilerGenerated
                                && (match m.DeclaringEntity with
                                    | Some e -> (try e.IsFSharpModule with _ -> false)
                                    | None -> false)
                            with _ ->
                                false

                        if isTopLevelLet && isPublicAcc (memberAccessibilityString m) then
                            Some
                                {| name = (try m.DisplayName with _ -> "<unknown>")
                                   kind = moduleMemberKind m
                                   preview = valPreview m |}
                        else
                            None
                    | :? FSharpEntity as e ->
                        let isType = try not e.IsNamespace && not e.IsFSharpModule with _ -> false

                        if isType && isPublicAcc (entityAccessibilityString e) then
                            Some
                                {| name = (try e.DisplayName with _ -> "<unknown>")
                                   kind = entityKindString e
                                   preview = typePreview e |}
                        else
                            None
                    // Fields, union cases, and other symbol kinds are surfaced through their
                    // enclosing type, not as standalone signature lines.
                    | _ -> None)
                |> List.distinctBy (fun m -> m.name, m.kind)
                |> List.sortBy (fun m -> m.name, m.kind)

            // implNameSet (used only for stale detection) also carries module names, so a
            // `module Foo` present in both impl and .fsi is never mis-flagged as stale.
            let implNameSet =
                definitionUses
                |> List.choose (fun su ->
                    match su.Symbol with
                    | :? FSharpMemberOrFunctionOrValue as m ->
                        if isPublicAcc (memberAccessibilityString m) then
                            Some(try m.DisplayName with _ -> "")
                        else
                            None
                    | :? FSharpEntity as e ->
                        if isPublicAcc (entityAccessibilityString e) then
                            Some(try e.DisplayName with _ -> "")
                        else
                            None
                    | _ -> None)
                |> List.filter (fun n -> n <> "")
                |> Set.ofList

            // ── Parse the .fsi (if present) and collect its declared leaf names ────
            let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)

            let! sigDecls =
                task {
                    if not hasSignatureFile then
                        return []
                    else
                        try
                            let sigText = SourceText.ofString (File.ReadAllText sigPath)
                            let! parsed = checker.ParseFile(sigPath, sigText, parsingOptions) |> asTask

                            match parsed.ParseTree with
                            | ParsedInput.SigFile(ParsedSigFileInput(contents = modules)) ->
                                return
                                    modules
                                    |> List.collect (fun (SynModuleOrNamespaceSig(decls = decls)) -> collectSigDecls decls)
                                    |> List.distinct
                            | _ -> return []
                        with _ ->
                            return []
                }

            // ── Diff impl surface against the signature ────────────────────────────
            let sigNameSet = sigDecls |> List.map fst |> Set.ofList

            let missingFromSig =
                publicMembers |> List.filter (fun m -> not (sigNameSet.Contains m.name))

            let staleInSig =
                sigDecls |> List.filter (fun (name, _) -> not (implNameSet.Contains name))

            let status =
                if not hasSignatureFile then "no_signature_file"
                elif List.isEmpty missingFromSig && List.isEmpty staleInSig then "clean"
                else "drift"

            let sigFileName = Path.GetFileName sigPath

            let suggestion =
                if not hasSignatureFile then
                    $"No signature file. Create {sigFileName} from missingFromSig ({publicMembers.Length} declaration(s)) to pin the public surface."
                elif not (List.isEmpty missingFromSig) && not (List.isEmpty staleInSig) then
                    $"{missingFromSig.Length} public member(s) hidden from {sigFileName}; {staleInSig.Length} stale .fsi entr(ies) with no impl match. Add the missing lines, remove/fix the stale ones."
                elif not (List.isEmpty missingFromSig) then
                    $"{missingFromSig.Length} public member(s) are hidden from the public surface by {sigFileName}. Add the listed val/type lines."
                elif not (List.isEmpty staleInSig) then
                    $"{staleInSig.Length} {sigFileName} entr(ies) have no matching impl member (stale signature or a compile error). Remove or correct them."
                else
                    $"{sigFileName} is in sync with the implementation's public surface ({publicMembers.Length} member(s))."

            let memberNode (m: {| name: string; kind: string; preview: string |}) =
                jobj
                    [ "name", jstr m.name
                      "kind", jstr m.kind
                      "signaturePreview", jstr m.preview ]
                :> JsonNode

            let staleNode (name: string, kind: string) =
                jobj [ "name", jstr name; "kind", jstr kind ] :> JsonNode

            return
                jobj
                    [ "status", jstr status
                      "implPath", jstr implPath
                      "sigPath", (if hasSignatureFile then jstr sigPath else null)
                      "hasSignatureFile", jbool hasSignatureFile
                      "project", jstr options.ProjectFileName
                      "optionsSource", jstr optionsSource
                      "publicMemberCount", jint publicMembers.Length
                      "publicMembers", JsonArray(publicMembers |> List.map memberNode |> List.toArray) :> JsonNode
                      "missingFromSig", JsonArray(missingFromSig |> List.map memberNode |> List.toArray) :> JsonNode
                      "staleInSig", JsonArray(staleInSig |> List.map staleNode |> List.toArray) :> JsonNode
                      "suggestion", jstr suggestion ]
                :> JsonNode
        }

    member _.ClearAnalysisCaches() =
        Interlocked.Increment(&projectUsesCacheGeneration) |> ignore
        Interlocked.Increment(&freshProjectCheckGeneration) |> ignore
        projectResultsCache.Clear()
        // issue #131: a new set_project (or any explicit cache clear) must drop the
        // memoized find sweeps too, so a project switch never serves stale symbol uses.
        // Project options intentionally survive: every fsproj entry is validated against
        // its complete ProjInfo input fingerprint before reuse (issue #150).
        projectUsesCache.Clear()

        // The next project-level analysis must establish a new FCS freshness
        // boundary as well as repopulating the application caches above.
        lock analysisSnapshotGate lastAnalysisSnapshotByProject.Clear

    /// Returns configuration flags captured at checker creation time, for use by RuntimeStatus.
    member _.CheckerConfig: FcsCheckerConfig =
        { KeepAssemblyContents = keepAssemblyContents
          KeepAllBackgroundResolutions = keepAllBackgroundResolutions
          KeepAllBackgroundSymbolUses = keepAllBackgroundSymbolUses
          ProjectCacheSize = defaultProjectCacheSize }

    /// Returns the number of entries currently held in the project-results cache.
    member _.ProjectResultsCacheCount = projectResultsCache.Count

    /// Number of actual Ionide/MSBuild project loads attempted by this bridge. Cache hits
    /// do not increment it; exposed for deterministic project-options cache tests.
    member _.ProjectOptionsLoadCount = Volatile.Read(&projectOptionsLoadCount)
    member _.ProjectOptionsStaleReloadCount = Volatile.Read(&projectOptionsStaleReloadCount)
    member _.ProjectOptionsCacheValidationCount = Volatile.Read(&projectOptionsCacheValidationCount)

    /// Admission/load counters exposed internally for deterministic containment tests.
    member _.ProjectEvaluationActiveCount = projectEvaluationAdmission.ActiveCount
    member _.ProjectEvaluationStartedCount = projectEvaluationAdmission.StartedCount
    member _.ProjectEvaluationRejectedCount = projectEvaluationAdmission.RejectedCount
    member _.ProjectEvaluationMaxObservedConcurrency = projectEvaluationAdmission.MaxObservedConcurrency
    member _.ProjectOptionsInFlightCount = optionsInFlight.Count
    member _.ProjectOptionsWaiterCount =
        optionsInFlight.Values |> Seq.sumBy (fun flight -> flight.WaiterCount)

    member _.ProjectTypeCheckStartCount = Volatile.Read(&projectTypeCheckStartCount)
    member _.FreshProjectCheckActiveCount = freshProjectCheckAdmission.ActiveCount
    member _.FreshProjectCheckStartedCount = freshProjectCheckAdmission.StartedCount
    member _.FreshProjectCheckRejectedCount = freshProjectCheckAdmission.RejectedCount
    member _.FreshProjectCheckMaxObservedConcurrency = freshProjectCheckAdmission.MaxObservedConcurrency
    member _.FreshProjectCheckInFlightCount = freshProjectChecksInFlight.Count
    member _.FreshProjectCheckInvalidationCount = Volatile.Read(&freshProjectCheckInvalidationCount)
    member _.ProjectUsesActiveCount = projectUsesAdmission.ActiveCount
    member _.ProjectUsesStartedCount = projectUsesAdmission.StartedCount
    member _.ProjectUsesRejectedCount = projectUsesAdmission.RejectedCount
    member _.ProjectUsesMaxObservedConcurrency = projectUsesAdmission.MaxObservedConcurrency
    member _.ProjectUsesInFlightCount = projectUsesInFlight.Count
    member _.SnapshotComputationActiveCount = snapshotComputationAdmission.ActiveCount
    member _.SnapshotComputationStartedCount = snapshotComputationAdmission.StartedCount
    member _.SnapshotComputationRejectedCount = snapshotComputationAdmission.RejectedCount
    member _.SnapshotComputationInFlightCount = snapshotComputationsInFlight.Count
    member _.SnapshotComputationWaiterCount =
        snapshotComputationsInFlight.Values |> Seq.sumBy (fun flight -> flight.WaiterCount)

    member _.CheckTargetDiscoveryActiveCount = checkTargetDiscoveryAdmission.ActiveCount
    member _.CheckTargetDiscoveryStartedCount = checkTargetDiscoveryAdmission.StartedCount
    member _.CheckTargetDiscoveryRejectedCount = checkTargetDiscoveryAdmission.RejectedCount
    member _.CheckTargetDiscoveryInFlightCount = checkTargetDiscoveriesInFlight.Count
    member _.CheckTargetDiscoveryWaiterCount =
        checkTargetDiscoveriesInFlight.Values |> Seq.sumBy (fun flight -> flight.WaiterCount)

    /// Observe after a discovery hook signals that the exact-key worker has started.
    member _.TryGetCheckTargetDiscoveryCompletionForTest
        (scope: string, target: string, path: string option)
        : Task option =
        let key = checkDiscoveryKey scope target path

        match checkTargetDiscoveriesInFlight.TryGetValue(key) with
        | true, flight -> Some(flight.Operation :> Task)
        | false, _ -> None // No retained worker for this key.

    /// Capture the production exact-worker cleanup to replay a stale callback after
    /// a replacement has started. This must never remove the replacement (ABA).
    member _.TryGetCheckTargetDiscoveryCleanupForTest(scope: string, target: string, path: string option) =
        let key = checkDiscoveryKey scope target path

        match checkTargetDiscoveriesInFlight.TryGetValue(key) with
        | true, flight -> Some(fun () -> removeExactSingleFlight checkTargetDiscoveriesInFlight key flight)
        | false, _ -> None // No retained worker for this key.

    member _.CheckProjectDiscoveryFallbackCount = Volatile.Read(&checkProjectDiscoveryFallbackCount)
    member _.FindPositionResolutionActiveCount = findPositionResolutionAdmission.ActiveCount
    member _.FindPositionResolutionStartedCount = findPositionResolutionAdmission.StartedCount
    member _.FindPositionResolutionRejectedCount = findPositionResolutionAdmission.RejectedCount
    member _.FindPositionResolutionInFlightCount = findPositionResolutionsInFlight.Count
    member _.FindPositionResolutionWaiterCount =
        findPositionResolutionsInFlight.Values |> Seq.sumBy (fun flight -> flight.WaiterCount)

    member _.FindPositionResolutionComputeCount = Volatile.Read(&findPositionResolutionComputeCount)
    member _.FindTargetDiscoveryActiveCount = findTargetDiscoveryAdmission.ActiveCount
    member _.FindTargetDiscoveryStartedCount = findTargetDiscoveryAdmission.StartedCount
    member _.FindTargetDiscoveryRejectedCount = findTargetDiscoveryAdmission.RejectedCount
    member _.FindTargetDiscoveryInFlightCount = findTargetDiscoveriesInFlight.Count
    member _.FindTargetDiscoveryWaiterCount =
        findTargetDiscoveriesInFlight.Values |> Seq.sumBy (fun flight -> flight.WaiterCount)

    member _.FindTargetDiscoveryComputeCount = Volatile.Read(&findTargetDiscoveryComputeCount)

    /// Pure deterministic seam for verifying collision-free discovery key framing.
    member _.CheckDiscoveryKeyForTest(scope: string, target: string, path: string option) =
        checkDiscoveryKey scope target path

    member _.ReferenceResolutionProbeActiveCount = referenceResolutionProbeAdmission.ActiveCount
    member _.ReferenceResolutionProbeStartedCount = referenceResolutionProbeAdmission.StartedCount
    member _.ReferenceResolutionProbeRejectedCount = referenceResolutionProbeAdmission.RejectedCount
    member _.ReferenceResolutionProbeInFlightCount = referenceResolutionProbesInFlight.Count
    member _.AnalysisSnapshotComputeCount = Volatile.Read(&analysisSnapshotComputeCount)
    member _.AnalysisSnapshotCommitCount = Volatile.Read(&analysisSnapshotCommitCount)

    /// Narrow deterministic seam for containment tests. Production Check obtains the
    /// same key from evaluated FSharpProjectOptions before calling this helper.
    member _.ProbeReferencesForTest(workKey: string, otherOptions: string array) =
        runReferenceResolutionProbe workKey (Array.copy otherOptions) None

    /// Number of entries in the find sweep use-cache (issue #131/#168 P1-07). One per
    /// project analysis snapshot; unchanged between identical sweeps and grows when any
    /// evaluated/source/reference input changes. Exposed for cache-behaviour tests.
    member _.ProjectUsesCacheCount = projectUsesCache.Count

    /// Return the cached MSBuild-evaluated model that backs both project_health
    /// and fsharp_project_inspect. It is produced by the same Ionide load as the
    /// FCS options, so defaults, imports, Conditions, files, and references cannot
    /// disagree between the two tools.
    member this.GetEvaluatedProjectSnapshot
        (fsprojPath: string)
        : Task<Result<EvaluatedProjectSnapshot, string>> =
        task {
            try
                let! entry = this.ResolveFsprojEntry(fsprojPath)

                match entry.EvaluatedSnapshot with
                | Some snapshot -> return Ok snapshot
                | None ->
                    return
                        Error
                            $"Evaluated project information is unavailable for '%s{Path.GetFullPath fsprojPath}'."
            with ex ->
                return Error ex.Message
        }

    member this.ProbeProjectOptions(fsprojPath: string) : Task<Result<ProjectOptionsInfo, string>> =
        task {
            try
                let! entry = this.ResolveFsprojEntry(fsprojPath)

                let existing, total =
                    match entry.EvaluatedSnapshot with
                    | Some snapshot -> snapshot.ReferencesExisting, snapshot.ReferencesTotal
                    | None -> ReferenceResolution.probe entry.Options.OtherOptions

                return
                    Ok
                        { Source = entry.Source
                          ReferencesExisting = existing
                          ReferencesTotal = total }
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

    /// Auto-fetch the diagnostic at a path+position via a fresh FCS parse+check (issue #61).
    /// Returns Ok(errorNumber, message) for the diagnostic covering the position, or an
    /// Error JSON envelope when none is found. Kept in its own member so ExplainDiagnostic's
    /// resolution `match` binds a plain Task and stays statically compilable (FS3511).
    member private this.ResolveDiagnosticFromPosition
        (path: string, line: int option, character: int option, text: string option, projectPath: string option)
        : Task<Result<int * string option, JsonNode>> =
        task {
            let! _, _, _, _, parseResults, checkedResults =
                this.PrepareCheckContext(path, text, projectPath, None)

            // LSP coordinates are 0-based; FCS diagnostic lines are 1-based.
            let fcsLine = defaultArg line -1 |> (+) 1
            let col = defaultArg character -1

            let allDiagnostics =
                Array.append
                    parseResults.Diagnostics
                    (checkedResults |> Option.map (fun r -> r.Diagnostics) |> Option.defaultValue [||])

            let covers (d: FSharpDiagnostic) =
                let sL, sC, eL, eC = d.StartLine, d.StartColumn, d.EndLine, d.EndColumn

                if fcsLine < sL || fcsLine > eL then false
                elif col < 0 then true // no column constraint requested
                elif fcsLine = sL && fcsLine = eL then sC <= col && col <= eC
                elif fcsLine = sL then sC <= col
                elif fcsLine = eL then col <= eC
                else true

            // Prefer an error covering the position; then any diagnostic covering it;
            // then any diagnostic on the same line.
            let pick =
                allDiagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                |> Array.tryFind covers
                |> Option.orElseWith (fun () -> allDiagnostics |> Array.tryFind covers)
                |> Option.orElseWith (fun () ->
                    allDiagnostics |> Array.tryFind (fun d -> d.StartLine <= fcsLine && fcsLine <= d.EndLine))

            match pick with
            | Some d -> return Ok(d.ErrorNumber, Some d.Message)
            | None ->
                return
                    Error(
                        jobj
                            [ "status", jstr "no_diagnostic_at_position"
                              "message", jstr "No FCS diagnostic was found at the given path/line/character."
                              "path", jstr (normalizePath path)
                              "line", (line |> Option.map jint |> Option.defaultValue null)
                              "character", (character |> Option.map jint |> Option.defaultValue null) ]
                        :> JsonNode
                    )
        }

    /// Explain an F# compiler diagnostic (issue #61). Resolves the diagnostic code from
    /// `code` / `errorNumber`, or auto-fetches it at a path+position via FCS, then returns
    /// a curated plain-language explanation plus repair context. Pairs with `check`.
    member this.ExplainDiagnostic(args: FcsExplainDiagnosticArgs) : Task<JsonNode> =
        task {
            // ── 1. Resolve the numeric error code from `code` when present ──
            let codeFromString =
                match args.code with
                | Some raw ->
                    match parseDiagnosticCode raw with
                    | Some n -> Ok(Some n)
                    | None -> Error raw // present but unparseable
                | None -> Ok None

            match codeFromString with
            | Error raw ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr $"code \"{raw}\" is not a recognized F# diagnostic code (expected e.g. \"FS0039\" or 39)" ]
                    :> JsonNode
            | Ok parsedCode ->

            // `code` takes precedence over `errorNumber`.
            let directNumber = parsedCode |> Option.orElse args.errorNumber

            // ── 2. No explicit number → try the path+position auto-fetch via FCS ──
            // Build the Task in a plain `let` (the FCS path lives in its own member),
            // then bind a direct identifier so the `let!` continuation stays statically
            // compilable — a `match` directly in the `let!` source trips FS3511.
            let resolveTask: Task<Result<int * string option, JsonNode>> =
                match directNumber with
                | Some n -> Task.FromResult(Ok(n, (None: string option)))
                | None ->
                    match args.path with
                    | Some path ->
                        this.ResolveDiagnosticFromPosition(path, args.line, args.character, args.text, args.projectPath)
                    | None ->
                        Task.FromResult(
                            Error(
                                jobj
                                    [ "status", jstr "invalid_args"
                                      "message",
                                      jstr
                                          "Provide one of: code (e.g. \"FS0039\"), errorNumber (e.g. 39), or path+line+character to auto-fetch the diagnostic." ]
                                :> JsonNode
                            )
                        )

            let! resolved = resolveTask

            // ── 3+4. Render the curated explanation (or pass an error envelope through).
            // The rendering is a pure synchronous helper so this continuation reduces.
            return renderExplanation args.message resolved
        }

    // ── fcs_check_compile_order (issue #58) ──────────────────────────────────────
    // Detects F#'s order-of-compilation gotcha: a symbol used in a file that is
    // DEFINED in a file appearing LATER in the project's <Compile> order is "not
    // defined" purely because of file ordering — distinct from a missing `open`.
    //
    // MECHANISM (verified against FCS 43.12.x): an out-of-order forward reference does
    // NOT resolve — FCS drops the use and emits FS0039 "X is not defined". So the use
    // never appears in GetAllUsesOfAllSymbols(); a naïve "resolved-use index vs def
    // index" comparison would find nothing. Instead we CORRELATE each FS0039 error with
    // the project's resolved DEFINITIONS: the offending file's compile index is compared
    // against the compile index of any same-named definition that lives elsewhere in the
    // SAME project. If a matching definition compiles LATER (cross-file: higher index;
    // same-file: a later line), the FS0039 is an ordering problem, not a missing open —
    // exactly the discrimination fcs_suggest_open can't make. Reuses the #131
    // ProjectSweepUses memo for the definition index.
    member this.CheckCompileOrder(args: FcsCheckCompileOrderArgs) : Task<JsonNode> =
        task {
            let targetOpt =
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath

            match targetOpt with
            | None ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "fcs_check_compile_order needs a project: pass projectPath (.fsproj/.sln/.slnx) or call set_project first." ]
                    :> JsonNode
            | Some target ->

            let symbolFilter = args.symbol |> Option.filter (String.IsNullOrWhiteSpace >> not)
            let projectsToScan = SolutionParsing.listProjects target

            if projectsToScan.Length = 0 then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr $"fcs_check_compile_order could not resolve any .fsproj from: {target}" ]
                    :> JsonNode
            else

            // Read each source file's lines at most once for lineText emission.
            let lineCache = System.Collections.Generic.Dictionary<string, string array>(StringComparer.Ordinal)

            let lineTextAt (file: string) (oneBasedLine: int) =
                let lines =
                    match lineCache.TryGetValue file with
                    | true, ls -> ls
                    | _ ->
                        let ls =
                            try
                                if File.Exists file then File.ReadAllLines file else [||]
                            with _ ->
                                [||]

                        lineCache[file] <- ls
                        ls

                let idx = oneBasedLine - 1
                if idx >= 0 && idx < lines.Length then lines[idx] else ""

            // Pull the first single-quoted identifier out of an FS0039 message. The
            // compiler emits straight quotes in the invariant culture; tolerate the
            // typographic pair as well.
            let extractUnresolvedName (message: string) : string option =
                if isNull message then
                    None
                else
                    let quotes = [| '\''; '‘'; '’' |]
                    let startIdx = message.IndexOfAny quotes

                    if startIdx < 0 then
                        None
                    else
                        let endIdx = message.IndexOfAny(quotes, startIdx + 1)

                        if endIdx <= startIdx + 1 then
                            None
                        else
                            Some(message.Substring(startIdx + 1, endIdx - startIdx - 1))

            let problems = ResizeArray<JsonNode>()
            let mutable projectsScanned = 0

            for fsproj in projectsToScan do
                try
                    let! options, _ = this.ResolveFsprojOptions fsproj
                    projectsScanned <- projectsScanned + 1

                    // <Compile> order: SourceFiles array index = compile index.
                    let fileIndex =
                        System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)

                    options.SourceFiles |> Array.iteri (fun i f -> fileIndex[normalizePath f] <- i)

                    let usesKey = analysisSnapshotKey options

                    let! allUses, diagnostics = this.ProjectSweepUses(usesKey, options, 120000)

                    // Index every IN-PROJECT definition by DisplayName → (file, declStartLine,
                    // compileIndex). An out-of-order use resolves to its definition by name;
                    // we match the unresolved FS0039 name against these. Only the declaration's
                    // line is retained (not the `range` struct) so later filtering/sorting never
                    // touches a struct field (avoids FS0052 defensive-copy warnings).
                    let defsByName =
                        System.Collections.Generic.Dictionary<string, ResizeArray<string * int * int>>(
                            StringComparer.Ordinal
                        )

                    for u in allUses do
                        if u.IsFromDefinition then
                            match u.Symbol.DeclarationLocation with
                            | Some r ->
                                let f = normalizePath r.FileName
                                let declStartLine = r.StartLine

                                match fileIndex.TryGetValue f with
                                | true, idx ->
                                    let name = u.Symbol.DisplayName

                                    if not (String.IsNullOrEmpty name) then
                                        match defsByName.TryGetValue name with
                                        | true, lst -> lst.Add(f, declStartLine, idx)
                                        | _ ->
                                            let lst = ResizeArray<string * int * int>()
                                            lst.Add(f, declStartLine, idx)
                                            defsByName[name] <- lst
                                | _ -> ()
                            | None -> ()

                    // Correlate each FS0039 "not defined" error with a later-compiling
                    // definition of the same name.
                    let seen = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)

                    for d in diagnostics do
                        if d.Severity = FSharpDiagnosticSeverity.Error && d.ErrorNumber = 39 then
                            match extractUnresolvedName d.Message with
                            | Some name when
                                symbolFilter
                                |> Option.forall (fun s -> String.Equals(s, name, StringComparison.Ordinal))
                                ->
                                // Bind the diagnostic range once: chained `d.Range.X` access
                                // would force a defensive struct copy (FS0052) under warnaserror.
                                let useRange = d.Range
                                let useStartLine = useRange.StartLine
                                let useFile = normalizePath useRange.FileName

                                match fileIndex.TryGetValue useFile with
                                | true, useIdx ->
                                    match defsByName.TryGetValue name with
                                    | true, candidates ->
                                        // Nearest definition that compiles AFTER the use
                                        // (cross-file: higher index; same-file: later line).
                                        let later =
                                            candidates
                                            |> Seq.filter (fun (_, defLine, didx) ->
                                                didx > useIdx || (didx = useIdx && defLine > useStartLine))
                                            |> Seq.sortBy (fun (_, defLine, didx) -> didx, defLine)
                                            |> Seq.tryHead

                                        match later with
                                        | Some(defFile, _, defIdx) ->
                                            let dedupKey =
                                                $"{useFile}:{useStartLine}:{useRange.StartColumn}:{name}"

                                            if seen.Add dedupKey then
                                                let defBase = Path.GetFileName defFile
                                                let useBase = Path.GetFileName useFile

                                                let problem =
                                                    jobj
                                                        [ "symbol", jstr name
                                                          "definedIn",
                                                          jobj [ "file", jstr defFile; "compileIndex", jint defIdx ]
                                                          :> JsonNode
                                                          "usedIn",
                                                          jobj
                                                              [ "file", jstr useFile
                                                                "compileIndex", jint useIdx
                                                                "range", rangeToJson useRange
                                                                "lineText", jstr (lineTextAt useFile useStartLine) ]
                                                          :> JsonNode
                                                          "fix",
                                                          jstr
                                                              $"definition compiles after use — move {defBase} before {useBase} in <Compile> order, or move the definition" ]
                                                    :> JsonNode

                                                problems.Add problem
                                        | None -> () // defined earlier/elsewhere → not an order problem
                                    | _ -> () // name not defined in this project → missing open / genuinely absent
                                | _ -> ()
                            | _ -> ()
                with _ ->
                    () // a project that fails to resolve is skipped; keep scanning the rest

            return
                jobj
                    [ "status", jstr "succeeded"
                      "projectsScanned", jint projectsScanned
                      "compileOrderProblems", JsonArray(problems.ToArray()) :> JsonNode
                      "problemCount", jint problems.Count ]
                :> JsonNode
        }

    // ── fcs_refactor_impact (#71): read-only blast-radius + verification preview ──────
    // ORCHESTRATES the existing backends — it adds no new analysis, only synthesis:
    //   • Find          → all cross-project use sites, the projects + files they touch;
    //   • TestsForSymbol → the tests that cover the symbol (run-these list);
    //   • CheckCompileOrder (kind=move) → forward-reference / <Compile>-order risk;
    //   • PublicApi (kind=signature|delete, target public) → breaking-surface flag;
    //   • RenamePreview (kind=rename, injected FSAC probe) → exact edit count, best-effort.
    // The `verify` array is a human-readable checklist distilled from the above. The
    // RenamePreview probe is injected (mirrors Find's fsacProbe) so this FCS member stays
    // LSP-agnostic and degrades cleanly when FSAC is unavailable. Writes nothing.
    member this.RefactorImpact
        (
            args: FcsRefactorImpactArgs,
            ?renamePreview: RenamePreviewArgs -> Task<JsonNode>,
            ?activeProjectPath: string
        )
        : Task<JsonNode> =
        task {
            // ── Defensive JsonNode readers (orchestrated payloads are always objects) ──
            let readStr (node: JsonNode) (key: string) : string option =
                try
                    match node[key] with
                    | null -> None
                    | v -> Some(v.GetValue<string>())
                with _ ->
                    None

            let readInt (node: JsonNode) (key: string) : int option =
                try
                    match node[key] with
                    | null -> None
                    | v -> Some(v.GetValue<int>())
                with _ ->
                    None

            let readBool (node: JsonNode) (key: string) : bool option =
                try
                    match node[key] with
                    | null -> None
                    | v -> Some(v.GetValue<bool>())
                with _ ->
                    None

            let arrayOf (node: JsonNode) (key: string) : JsonNode array =
                try
                    match node[key] with
                    | :? JsonArray as a -> a |> Seq.toArray
                    | _ -> [||]
                with _ ->
                    [||]

            // ── Resolve the target: a symbol name, or a position to resolve it from ──
            let symbolValue = args.symbol |> Option.map (fun s -> s.Trim())
            let hasSymbol = symbolValue |> Option.exists (String.IsNullOrWhiteSpace >> not)

            let hasPosition =
                (args.path |> Option.exists (String.IsNullOrWhiteSpace >> not)) && args.line.IsSome

            if not hasSymbol && not hasPosition then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr "fcs_refactor_impact needs a target: pass `symbol`, or `path` + `line` (+ `character`)." ]
                    :> JsonNode
            else

            let kindRaw = (args.kind |> Option.defaultValue "auto").Trim().ToLowerInvariant()

            let kindKnown = set [ "rename"; "signature"; "move"; "delete"; "auto" ]

            let kindResolved =
                let k = if kindKnown.Contains kindRaw then kindRaw else "auto"

                if k = "auto" then
                    if args.newName |> Option.exists (String.IsNullOrWhiteSpace >> not) then
                        "rename"
                    else
                        "auto"
                else
                    k

            let resolvedVia = if hasSymbol then "symbol" else "position"

            // Keep an explicit production .fsproj as the requested/defining context,
            // but let the workspace Find use the active containing solution. Otherwise
            // RefactorImpact could find tests in sibling projects while simultaneously
            // reporting a one-project blast radius for the same symbol.
            let findProjectPath =
                let requestedSourceProject =
                    args.projectPath
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.map normalizePath
                    |> Option.filter (fun path ->
                        path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                        && not (FsLangMcp.ProjectHealth.isTestProjectFile path))

                let pathComparison =
                    if OperatingSystem.IsWindows() then
                        StringComparison.OrdinalIgnoreCase
                    else
                        StringComparison.Ordinal

                let containingActiveWorkspace =
                    match requestedSourceProject, activeProjectPath with
                    | Some sourceProject, Some activePath when not (String.IsNullOrWhiteSpace activePath) ->
                        let active = normalizePath activePath

                        let isWorkspace =
                            Directory.Exists active
                            || active.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                            || active.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)

                        if
                            isWorkspace
                            && (SolutionParsing.listProjects active
                                |> Array.exists (fun project ->
                                    String.Equals(normalizePath project, sourceProject, pathComparison)))
                        then
                            Some active
                        else
                            None
                    | _ -> None

                containingActiveWorkspace |> Option.orElse args.projectPath

            // One Find call gives BOTH the cross-project blast radius AND (for a position
            // target) the resolved symbol name — Find echoes it back in `query`.
            let baseFind: FindArgs =
                { query = "_"
                  kind = None
                  scope = Some "workspace"
                  exact = Some true
                  ``member`` = None
                  field = None
                  path = args.path
                  line = args.line
                  word = None
                  occurrence = None
                  character = args.character
                  contextLines = Some 0
                  includeDeclaration = Some true
                  includeInfo = Some false
                  includePerProject = None
                  // #207: refactor_impact synthesises counts, not per-site rows — a
                  // per-site type column would be computed and then thrown away.
                  includeSiteTypes = None
                  projectPath = findProjectPath
                  maxResults = Some 1000
                  timeoutMs = None
                  cursor = None }

            let findArgs =
                if hasSymbol then
                    { baseFind with
                        query = symbolValue |> Option.defaultValue "_"
                        kind = Some "auto" }
                else
                    { baseFind with kind = Some "position" }

            let! findResult = this.Find(findArgs)
            let findStatus = readStr findResult "status" |> Option.defaultValue "unknown"

            if findStatus <> "succeeded" then
                return
                    jobj
                        [ "status", jstr findStatus
                          "stage", jstr "find-sweep"
                          "message",
                          (readStr findResult "message"
                           |> Option.map jstr
                           |> Option.defaultValue (jstr "could not resolve the target symbol from the given inputs")) ]
                    :> JsonNode
            else

            let resolvedName =
                readStr findResult "query"
                |> Option.orElse symbolValue
                |> Option.defaultValue ""

            let totalSites = readInt findResult "totalSites" |> Option.defaultValue 0

            let matched =
                match findResult["resolution"] with
                | null -> totalSites > 0
                | res -> readBool res "matched" |> Option.defaultValue (totalSites > 0)

            let findTruncated = readBool findResult "truncated" |> Option.defaultValue false

            let findResolutionComplete =
                match findResult["resolution"] with
                | null -> not findTruncated
                | resolution -> readBool resolution "complete" |> Option.defaultValue (not findTruncated)

            // RefactorImpact requests the first 1000 rows. Counts are full, but the
            // affected-file/project lists below are page-derived, so never present them
            // as exhaustive when Find returned a cursor.
            let findDeliveryComplete = findResolutionComplete && not findTruncated
            let findNextCursor = readStr findResult "nextCursor"

            // ── Impact: files + projects the sites touch ─────────────────────────────
            let sites = arrayOf findResult "sites"
            let perProject = arrayOf findResult "perProject"

            let fileOf (s: JsonNode) = readStr s "file" |> Option.defaultValue ""
            let projOf (s: JsonNode) = readStr s "project" |> Option.defaultValue ""
            let kindOf (s: JsonNode) = readStr s "kind" |> Option.defaultValue ""

            let affectedFiles =
                sites
                |> Array.map fileOf
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.distinct
                |> Array.sort

            let fileCount = affectedFiles.Length

            let byProject =
                sites
                |> Array.map projOf
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.countBy id
                |> Array.sortByDescending snd

            let projectCount = byProject.Length
            let crossProject = projectCount > 1

            let fsprojForProject (proj: string) =
                perProject
                |> Array.tryPick (fun p ->
                    match readStr p "project" with
                    | Some pn when String.Equals(pn, proj, StringComparison.Ordinal) -> readStr p "fsproj"
                    | _ -> None)

            let sitesByProjectNodes =
                byProject
                |> Array.map (fun (proj, n) ->
                    let baseProps = [ "project", jstr proj; "sites", jint n ]

                    let props =
                        match fsprojForProject proj with
                        | Some f -> baseProps @ [ ("fsproj", jstr f) ]
                        | None -> baseProps

                    jobj props :> JsonNode)

            // The project that DEFINES the target — used to scope the public-API check.
            let definingFsproj =
                sites
                |> Array.tryFind (fun s -> kindOf s = "definition")
                |> Option.map fileOf
                |> Option.bind (fun f -> if String.IsNullOrWhiteSpace f then None else findNearestFsproj f)
                |> Option.map normalizePath
                |> Option.orElseWith (fun () ->
                    match args.projectPath with
                    | Some p when p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) -> Some(normalizePath p)
                    | _ -> byProject |> Array.tryHead |> Option.bind (fst >> fsprojForProject))

            // ── Tests that cover the symbol (always) ─────────────────────────────────
            let! testsResult =
                this.TestsForSymbol
                    (
                        { symbolQuery = resolvedName
                          exact = Some true
                          path = None
                          text = None
                          projectPath = args.projectPath
                          maxResults = Some 100
                          timeoutMs = None
                          cursor = None },
                        ?activeProjectPath = activeProjectPath
                    )

            let testSites = arrayOf testsResult "tests"
            let testStatus = readStr testsResult "status" |> Option.defaultValue "unknown"
            let testOutcome = readStr testsResult "outcome" |> Option.defaultValue "indeterminate"
            let testCount = readInt testsResult "testCount" |> Option.defaultValue testSites.Length
            let testSiteCount = readInt testsResult "siteCount" |> Option.defaultValue testSites.Length

            let uniqueTestCount =
                readInt testsResult "uniqueTestCount" |> Option.defaultValue testSites.Length

            let testsTruncated = readBool testsResult "truncated" |> Option.defaultValue false
            let testsNextCursor = readStr testsResult "nextCursor"

            let testsCoverageNode =
                match testsResult["coverage"] with
                | null -> jobj [ ("complete", jbool false) ] :> JsonNode
                | coverage -> coverage.DeepClone()

            let testsCoverageComplete =
                readBool testsCoverageNode "complete"
                |> Option.orElseWith (fun () -> readBool testsResult "complete")
                |> Option.defaultValue false

            let testsDeliveryComplete = not testsTruncated
            let testsComplete = testsCoverageComplete && testsDeliveryComplete

            let testNodes =
                testSites
                |> Array.map (fun t ->
                    jobj
                        [ "file", jstrOrNull (readStr t "file" |> Option.defaultValue "")
                          "enclosingTest", jstrOrNull (readStr t "enclosingTest" |> Option.defaultValue "")
                          "project", jstrOrNull (readStr t "project" |> Option.defaultValue "") ]
                    :> JsonNode)

            let enclosingTestNames =
                testSites
                |> Array.choose (fun t -> readStr t "enclosingTest")
                |> Array.distinct
                |> Array.sort

            // ── Compile-order risk (kind=move) ───────────────────────────────────────
            let mutable compileOrderNode: JsonNode option = None
            let mutable compileProblemCount = 0
            let mutable compileFirstFix = ""

            if kindResolved = "move" then
                let! co = this.CheckCompileOrder { projectPath = args.projectPath; symbol = Some resolvedName }
                let probs = arrayOf co "compileOrderProblems"
                compileProblemCount <- readInt co "problemCount" |> Option.defaultValue probs.Length

                compileFirstFix <-
                    if probs.Length > 0 then
                        readStr probs[0] "fix" |> Option.defaultValue ""
                    else
                        ""

                let problemsClone =
                    match co["compileOrderProblems"] with
                    | null -> JsonArray() :> JsonNode
                    | n -> n.DeepClone()

                compileOrderNode <-
                    Some(jobj [ "problemCount", jint compileProblemCount; "problems", problemsClone ] :> JsonNode)

            // ── Public-API breaking surface (kind=signature|delete) ──────────────────
            let wantApi = kindResolved = "signature" || kindResolved = "delete"
            let mutable apiSurfaceNode: JsonNode option = None
            let mutable apiIsPublic: bool option = None
            let mutable apiComplete = not wantApi

            if wantApi then
                match definingFsproj with
                | None ->
                    apiComplete <- false
                    apiSurfaceNode <-
                        Some(
                            jobj
                                [ "complete", jbool false
                                  "isPublic", null
                                  "affectedPublicMembers", JsonArray() :> JsonNode
                                  "note", jstr "could not resolve the defining project for the target" ]
                            :> JsonNode
                        )
                | Some fsproj ->
                    let! api =
                        this.PublicApi
                            { projectPath = Some fsproj
                              includeInternal = Some false
                              namespaceFilter = None
                              maxResults = Some 1000
                              cursor = None }

                    let apiStatus = readStr api "status" |> Option.defaultValue "unknown"
                    let apiTruncated = readBool api "truncated" |> Option.defaultValue false
                    let apiNextCursor = readStr api "nextCursor"
                    let apiTruncatedByBudget = readBool api "truncatedByBudget" |> Option.defaultValue false
                    let apiScanComplete =
                        apiStatus = "ok"
                        && not apiTruncated
                        && not apiTruncatedByBudget
                        && apiNextCursor.IsNone

                    let entities = arrayOf api "entities"
                    let affected = ResizeArray<JsonNode>()

                    for e in entities do
                        let efull = readStr e "fullName" |> Option.defaultValue ""

                        let lastSeg =
                            let idx = efull.LastIndexOf '.'

                            if idx >= 0 && idx < efull.Length - 1 then
                                efull.Substring(idx + 1)
                            else
                                efull

                        if String.Equals(lastSeg, resolvedName, StringComparison.Ordinal) then
                            affected.Add(
                                jobj
                                    [ "entity", jstr efull
                                      "member", jstrOrNull ""
                                      "kind", jstr (readStr e "kind" |> Option.defaultValue "")
                                      "signature", jstr efull ]
                                :> JsonNode
                            )

                        for m in arrayOf e "members" do
                            match readStr m "name" with
                            | Some mn when String.Equals(mn, resolvedName, StringComparison.Ordinal) ->
                                affected.Add(
                                    jobj
                                        [ "entity", jstr efull
                                          "member", jstr mn
                                          "kind", jstr (readStr m "kind" |> Option.defaultValue "")
                                          "signature", jstr (readStr m "signature" |> Option.defaultValue "") ]
                                    :> JsonNode
                                )
                            | _ -> ()

                    apiIsPublic <-
                        if affected.Count > 0 then
                            Some true
                        elif apiScanComplete then
                            Some false
                        else
                            None

                    apiComplete <- apiScanComplete

                    apiSurfaceNode <-
                        Some(
                            jobj
                                [ "status", jstr apiStatus
                                  "complete", jbool apiComplete
                                  "scanComplete", jbool apiScanComplete
                                  "isPublic", (apiIsPublic |> Option.map jbool |> Option.defaultValue null)
                                  "project", jstr fsproj
                                  "truncated", jbool apiTruncated
                                  "truncatedByBudget", jbool apiTruncatedByBudget
                                  "nextCursor", (apiNextCursor |> Option.map jstr |> Option.defaultValue null)
                                  "affectedPublicMembers", JsonArray(affected.ToArray()) :> JsonNode ]
                            :> JsonNode
                        )

            // ── Rename preview (kind=rename) — best-effort, injected FSAC probe ───────
            let mutable renamePreviewNode: JsonNode option = None
            let mutable renameAvailable = false
            let mutable renameEdits = 0
            let mutable renameFiles = 0

            match renamePreview with
            | Some probe when kindResolved = "rename" ->
                match args.newName, args.path, args.line, args.character with
                | Some nn, Some p, Some ln, Some ch when
                    (not (String.IsNullOrWhiteSpace nn)) && (not (String.IsNullOrWhiteSpace p))
                    ->
                    try
                        let! rp =
                            probe
                                { path = p
                                  line = ln
                                  character = ch
                                  newName = nn
                                  text = None }

                        let st = readStr rp "status" |> Option.defaultValue "unknown"

                        if st = "ok" then
                            renameAvailable <- true
                            renameEdits <- readInt rp "totalEdits" |> Option.defaultValue 0
                            renameFiles <- readInt rp "fileCount" |> Option.defaultValue 0

                            renamePreviewNode <-
                                Some(
                                    jobj
                                        [ "status", jstr "ok"
                                          "totalEdits", jint renameEdits
                                          "fileCount", jint renameFiles
                                          "crossProject", jbool (readBool rp "crossProject" |> Option.defaultValue false) ]
                                    :> JsonNode
                                )
                        else
                            renamePreviewNode <-
                                Some(
                                    jobj
                                        [ "status", jstr st
                                          "note", jstr "rename preview returned no edits — relying on find sites" ]
                                    :> JsonNode
                                )
                    with ex ->
                        renamePreviewNode <-
                            Some(jobj [ "status", jstr "unavailable"; "note", jstr ex.Message ] :> JsonNode)
                | _ ->
                    renamePreviewNode <-
                        Some(
                            jobj
                                [ "status", jstr "skipped"
                                  "note", jstr "rename preview needs newName + path + line + character" ]
                            :> JsonNode
                        )
            | _ -> ()

            // ── verify: human-readable checklist distilled from the sections above ───
            let verify = ResizeArray<string>()

            if not findDeliveryComplete then
                verify.Add(
                    $"impact rows are paginated: this report contains %d{sites.Length}/%d{totalSites} find site(s); follow find.nextCursor before treating file/project counts as exhaustive"
                )

            if not testsCoverageComplete then
                verify.Add(
                    "test coverage is incomplete or indeterminate — inspect tests.coverage/message and widen or retry before trusting zero"
                )

            if testsTruncated then
                verify.Add(
                    $"test rows are paginated: this report contains %d{testSites.Length}/%d{testSiteCount} site(s); follow fcs_tests_for_symbol with tests.nextCursor for the remainder"
                )

            if (not matched) || totalSites = 0 then
                verify.Add(
                    $"no use sites found for '%s{resolvedName}' — it may be unused, dynamically referenced, or the name is wrong; double-check before changing it"
                )
            elif not findDeliveryComplete then
                verify.Add(
                    $"%d{totalSites} total site(s) matched, but this page cannot prove the complete file/project or cross-project breadth"
                )
            elif crossProject then
                let projectNamesStr = byProject |> Array.map fst |> String.concat ", "

                verify.Add(
                    $"%d{totalSites} cross-project site(s) across %d{projectCount} projects (%s{projectNamesStr}) — rebuild all affected projects"
                )
            else
                verify.Add($"%d{totalSites} site(s) in %d{fileCount} file(s) within one project — re-check the project after the change")

            if testSiteCount > 0 then
                let namesStr =
                    if enclosingTestNames.Length > 0 then
                        enclosingTestNames |> Array.truncate 10 |> String.concat ", "
                    else
                        "(see tests list)"

                verify.Add(
                    $"%d{testSiteCount} test reference site(s) across %d{uniqueTestCount} enclosing test(s) cover '%s{resolvedName}' — run: %s{namesStr}"
                )

            if wantApi then
                if not apiComplete then
                    verify.Add(
                        "public API evidence is incomplete — apiSurface.isPublic is indeterminate unless the target was already observed; follow apiSurface.nextCursor or narrow fcs_public_api before deciding the change is internal"
                    )

                match apiIsPublic with
                | Some true ->
                    verify.Add(
                        $"'%s{resolvedName}' is part of the public API surface — this is a BREAKING change; bump the minor version and update consumers"
                    )
                | Some false ->
                    verify.Add($"'%s{resolvedName}' is not on the public API surface — the change stays internal")
                | None ->
                    verify.Add($"public API membership for '%s{resolvedName}' is indeterminate — do not treat it as internal")

            if kindResolved = "move" then
                if compileProblemCount > 0 then
                    let fixHint =
                        if String.IsNullOrWhiteSpace compileFirstFix then
                            "reorder the <Compile> entries"
                        else
                            compileFirstFix

                    verify.Add($"compile-order risk: {compileProblemCount} forward-reference problem(s) — {fixHint}")
                else
                    verify.Add(
                        "no compile-order problems at the current file positions; re-run `check` after moving the file or definition"
                    )

            if kindResolved = "rename" then
                if renameAvailable then
                    let renameTo = args.newName |> Option.defaultValue "?"

                    verify.Add(
                        $"rename '%s{resolvedName}' -> '%s{renameTo}' touches %d{renameEdits} edit(s) in %d{renameFiles} file(s); `fcs_rename_preview` has the exact edit set"
                    )
                else
                    verify.Add("rename preview unavailable (FSAC) — use the find sites as the edit set")

            // ── Assemble ─────────────────────────────────────────────────────────────
            let targetNode =
                jobj
                    ([ ("symbol", jstr resolvedName) ]
                     @ (match args.path with
                        | Some p when not (String.IsNullOrWhiteSpace p) -> [ ("path", jstr (normalizePath p)) ]
                        | _ -> [])
                     @ (match args.line with
                        | Some l -> [ ("line", jint l) ]
                        | None -> [])
                     @ (match args.character with
                        | Some c -> [ ("character", jint c) ]
                        | None -> [])
                     @ [ ("resolvedVia", jstr resolvedVia) ])
                :> JsonNode

            let impactNode =
                jobj
                    [ "totalSites", jint totalSites
                      "returnedSites", jint sites.Length
                      "complete", jbool findDeliveryComplete
                      "truncated", jbool findTruncated
                      "nextCursor", (findNextCursor |> Option.map jstr |> Option.defaultValue null)
                      "fileCountIsLowerBound", jbool (not findDeliveryComplete)
                      "projectCountIsLowerBound", jbool (not findDeliveryComplete)
                      "fileCount", jint fileCount
                      "projectCount", jint projectCount
                      "crossProjectKnown", jbool findDeliveryComplete
                      "crossProject", (if findDeliveryComplete then jbool crossProject else null)
                      "observedCrossProject", jbool crossProject
                      "sitesByProject", JsonArray(sitesByProjectNodes) :> JsonNode
                      "affectedFiles", JsonArray(affectedFiles |> Array.map jstr) :> JsonNode ]
                :> JsonNode

            let testsNode =
                jobj
                    [ "status", jstr testStatus
                      "outcome", jstr testOutcome
                      "complete", jbool testsComplete
                      "coverage", testsCoverageNode
                      "message", (readStr testsResult "message" |> Option.map jstr |> Option.defaultValue null)
                      // count remains the compatibility reference-site count.
                      "count", jint testCount
                      "siteCount", jint testSiteCount
                      "uniqueCount", jint uniqueTestCount
                      "uniqueTestCount", jint uniqueTestCount
                      "returnedSites", jint testSites.Length
                      "truncated", jbool testsTruncated
                      "nextCursor", (testsNextCursor |> Option.map jstr |> Option.defaultValue null)
                      "tests", JsonArray(testNodes) :> JsonNode ]
                :> JsonNode

            let reportComplete = findDeliveryComplete && testsComplete && apiComplete
            let reportStatus = if reportComplete then "succeeded" else "partial"

            let baseProps =
                [ "status", jstr reportStatus
                  "complete", jbool reportComplete
                  "target", targetNode
                  "kind", jstr kindResolved
                  "impact", impactNode
                  "tests", testsNode ]

            let optionalProps =
                (compileOrderNode |> Option.map (fun n -> [ ("compileOrder", n) ]) |> Option.defaultValue [])
                @ (apiSurfaceNode |> Option.map (fun n -> [ ("apiSurface", n) ]) |> Option.defaultValue [])
                @ (renamePreviewNode |> Option.map (fun n -> [ ("renamePreview", n) ]) |> Option.defaultValue [])

            let verifyProp = [ ("verify", JsonArray(verify.ToArray() |> Array.map jstr) :> JsonNode) ]

            return jobj (baseProps @ optionalProps @ verifyProp) :> JsonNode
        }

    // ── fcs_create_file_plan (#66): read-only "where should this new .fs file go?" ────
    // PLANNING ONLY — never creates files, writes source, or edits the .fsproj. Given a
    // proposed file name (and optionally a sibling it should follow + an intended
    // namespace/module), it loads the project's resolved <Compile> order (SourceFiles),
    // recommends an insertion index, reads the top namespace/module declaration of the
    // neighbouring files to infer the house convention, and spells out the exact
    // <Compile Include=...> edit. F# compile order is load-bearing: a file may only
    // reference symbols DEFINED in earlier files, so the recommended index governs what
    // the new file can use. Pairs with fcs_check_compile_order (run it AFTER the edit).
    //
    // The async half mirrors CheckCompileOrder's proven shape (a for-loop with `let!`
    // inside try/with, then a single `return`). All the heavy synchronous planning lives
    // in the pure BuildFilePlan member so this state machine stays statically compilable
    // (sidesteps the FS3511 "await-in-loop then control flow" shape). Writes nothing.
    member this.CreateFilePlan(args: FcsCreateFilePlanArgs) : Task<JsonNode> =
        task {
            let targetOpt =
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath

            match targetOpt with
            | None ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "fcs_create_file_plan needs a project: pass projectPath (.fsproj/.sln/.slnx) or call set_project first." ]
                    :> JsonNode
            | Some target ->

            let fileNameRaw = if isNull args.fileName then "" else args.fileName.Trim()

            if fileNameRaw = "" then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr "fcs_create_file_plan requires a non-empty fileName (e.g. \"Validation.fs\")." ]
                    :> JsonNode
            else

            let projectsToScan = SolutionParsing.listProjects target

            if projectsToScan.Length = 0 then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr $"fcs_create_file_plan could not resolve any .fsproj from: {target}" ]
                    :> JsonNode
            else

            // Load options for every resolved project; a project that fails to resolve is
            // skipped (we plan against whatever loaded). Awaiting inside the loop only.
            let loaded = ResizeArray<string * FSharpProjectOptions>()

            for fsproj in projectsToScan do
                try
                    let! options, _ = this.ResolveFsprojOptions fsproj
                    loaded.Add(fsproj, options)
                with _ ->
                    ()

            return this.BuildFilePlan(args, fileNameRaw, projectsToScan, loaded)
        }

    // The pure (non-async) half of fcs_create_file_plan: given the already-loaded project
    // options, compute the recommended <Compile> position, infer the namespace convention,
    // and spell out the edit. Kept OFF the task state machine (FS3511). Writes nothing.
    member private _.BuildFilePlan
        (
            args: FcsCreateFilePlanArgs,
            fileNameRaw: string,
            projects: string array,
            loaded: ResizeArray<string * FSharpProjectOptions>
        ) : JsonNode =
        let nullNode: JsonNode = null
        let strOrNull (s: string option) : JsonNode = match s with Some v -> jstr v | None -> nullNode
        let notBlank (s: string) = not (String.IsNullOrWhiteSpace s)
        let baseNameLower (p: string) = (Path.GetFileName p).ToLowerInvariant()

        let looksLikeTest (p: string) =
            (Path.GetFileNameWithoutExtension p).IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0

        if loaded.Count = 0 then
            jobj
                [ "status", jstr "invalid_args"
                  "message",
                  jstr
                      $"fcs_create_file_plan resolved {projects.Length} project(s) but could not load options for any. Restore the project first." ]
            :> JsonNode
        else

        let afterArg = args.afterFile |> Option.filter notBlank
        let afterBaseLower = afterArg |> Option.map baseNameLower

        // Plan against the project that already contains afterFile (so the recommended
        // index is meaningful), else the first non-test project, else simply the first.
        let chosenFsproj, options =
            let byAfter =
                afterBaseLower
                |> Option.bind (fun afb ->
                    loaded
                    |> Seq.tryFind (fun (_, o) -> o.SourceFiles |> Array.exists (fun f -> baseNameLower f = afb)))

            match byAfter with
            | Some chosen -> chosen
            | None ->
                loaded
                |> Seq.tryFind (fun (p, _) -> not (looksLikeTest p))
                |> Option.defaultValue loaded[0]

        let notes = ResizeArray<string>()

        if projects.Length > 1 then
            notes.Add
                $"projectPath resolved to {projects.Length} projects; planned against {Path.GetFileName chosenFsproj}"

        // FCS SourceFiles augments the project's <Compile> items with MSBuild-injected
        // generated sources (obj/ AssemblyInfo, *.g.fs, designer files). Those never appear
        // in the .fsproj the agent will edit, so plan against the USER-VISIBLE compile set:
        // the recommended index, sibling files and namespace inference must mirror what the
        // .fsproj actually lists, not FCS's augmented view.
        let underObjOrBin (p: string) =
            let np = p.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            let sep = string Path.DirectorySeparatorChar

            np.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || np.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)

        let isUserVisible (p: string) =
            not (underObjOrBin p || isGeneratedFile p || isAssemblyInfoFile p || isDesignerFile p)

        let sourceFiles = options.SourceFiles |> Array.map normalizePath |> Array.filter isUserVisible
        let len = sourceFiles.Length

        // Read a file's first significant `namespace X` / `module X.Y` line, skipping blank
        // lines, // comments, (* block comments *), #directives and [<attributes>]. One read
        // per file, capped at 80 lines, so even a large solution stays cheap.
        let readTopDecl (file: string) : string option =
            let lines =
                try
                    if File.Exists file then File.ReadLines file |> Seq.truncate 80 |> Seq.toArray else [||]
                with _ ->
                    [||]

            let firstToken (s: string) =
                let cut = let i = s.IndexOf "//" in if i >= 0 then s.Substring(0, i) else s
                let i = cut.IndexOfAny [| ' '; '\t'; '='; '('; ')' |]
                (if i >= 0 then cut.Substring(0, i) else cut).Trim()

            let stripLeading (words: string list) (s: string) =
                let mutable cur = s
                let mutable changed = true

                while changed do
                    changed <- false

                    for w in words do
                        if cur.StartsWith(w + " ") || cur.StartsWith(w + "\t") then
                            cur <- cur.Substring(w.Length).Trim()
                            changed <- true

                cur

            let parseDecl (line: string) : string option =
                let afterKw (kw: string) =
                    if line.StartsWith(kw + " ") || line.StartsWith(kw + "\t") then
                        Some(line.Substring(kw.Length).Trim())
                    else
                        None

                match afterKw "namespace" with
                | Some rest ->
                    let nm = rest |> stripLeading [ "rec"; "global" ] |> firstToken
                    Some(if nm = "" then "namespace" else $"namespace {nm}")
                | None ->
                    match afterKw "module" with
                    | Some rest ->
                        let nm = rest |> stripLeading [ "rec"; "public"; "internal"; "private" ] |> firstToken
                        if nm = "" then None else Some $"module {nm}"
                    | None -> None

            let mutable result = None
            let mutable inBlock = false
            let mutable i = 0

            while result.IsNone && i < lines.Length do
                let line = lines[i].Trim()

                if inBlock then
                    if line.Contains "*)" then inBlock <- false
                elif line = "" || line.StartsWith "//" || line.StartsWith "#" || line.StartsWith "[<" then
                    ()
                elif line.StartsWith "(*" then
                    if not (line.Contains "*)") then inBlock <- true
                else
                    // First significant line: it is the top decl, or there is none.
                    result <- Some(parseDecl line |> Option.defaultValue "")

                i <- i + 1

            match result with
            | Some "" -> None
            | other -> other

        let topDecls = sourceFiles |> Array.map readTopDecl

        // Namespace root the neighbours share. For `module A.B.C` the namespace is `A.B`
        // (drop the module leaf); for `module C` (single segment) there is no namespace.
        let declToNamespaceParts (decl: string) : string list =
            let parts = decl.Split(' ')

            if parts.Length < 2 then
                []
            else
                let segs = parts[1].Split('.') |> Array.toList

                match parts[0] with
                | "namespace" -> segs
                | "module" -> (if segs.Length <= 1 then [] else segs |> List.take (segs.Length - 1))
                | _ -> []

        let longestCommonPrefix (lists: string list list) : string list =
            match lists with
            | [] -> []
            | first :: rest ->
                rest
                |> List.fold
                    (fun acc cur ->
                        Seq.zip acc cur |> Seq.takeWhile (fun (a, b) -> a = b) |> Seq.map fst |> Seq.toList)
                    first

        let decls = topDecls |> Array.choose id
        let kindOf (d: string) = (d.Split(' '))[0]
        let namespaceKindCount = decls |> Array.filter (fun d -> kindOf d = "namespace") |> Array.length
        let moduleKindCount = decls |> Array.filter (fun d -> kindOf d = "module") |> Array.length

        let nsPartLists =
            decls |> Array.map declToNamespaceParts |> Array.filter (List.isEmpty >> not) |> Array.toList

        let rootStr = longestCommonPrefix nsPartLists |> String.concat "."
        let moduleName = Path.GetFileNameWithoutExtension fileNameRaw
        let modulePreferred = moduleKindCount >= namespaceKindCount

        let inferredConvention =
            if decls.Length = 0 then
                "unknown — no neighbour namespace/module declarations were readable"
            elif modulePreferred && rootStr <> "" then
                $"module {rootStr}.<ModuleName> (one top-level module per file under namespace {rootStr})"
            elif modulePreferred then
                "module <ModuleName> (one top-level module per file)"
            elif rootStr <> "" then
                $"namespace {rootStr}"
            else
                "namespace <Name>"

        let suggestedDecl =
            match args.namespaceOrModule |> Option.filter notBlank with
            | Some ns -> ns.Trim()
            | None ->
                if modulePreferred && rootStr <> "" then $"module {rootStr}.{moduleName}"
                elif modulePreferred then $"module {moduleName}"
                elif rootStr <> "" then $"namespace {rootStr}"
                else ""

        // Placement: after afterFile when matched; else after the last neighbour sharing the
        // intended namespace root; else end-of-project (can reference everything).
        let afterMatchIdx =
            afterBaseLower
            |> Option.bind (fun afb -> sourceFiles |> Array.tryFindIndex (fun f -> baseNameLower f = afb))

        let nsArgFirstSeg =
            args.namespaceOrModule
            |> Option.filter notBlank
            |> Option.map (fun s ->
                let t = s.Trim()

                let stripped =
                    if t.StartsWith "namespace " then t.Substring(10).Trim()
                    elif t.StartsWith "module " then t.Substring(7).Trim()
                    else t

                (stripped.Split('.'))[0])

        let heuristicIdx =
            match afterArg, nsArgFirstSeg with
            | None, Some seg when seg <> "" ->
                let mutable found = None

                topDecls
                |> Array.iteri (fun idx d ->
                    match d |> Option.map declToNamespaceParts with
                    | Some(firstSeg :: _) when String.Equals(firstSeg, seg, StringComparison.Ordinal) ->
                        found <- Some idx
                    | _ -> ())

                found
            | _ -> None

        let segDisplay = nsArgFirstSeg |> Option.defaultValue ""

        let recIndex, insertAfterOpt, insertBeforeOpt =
            match afterMatchIdx with
            | Some i ->
                notes.Add $"placed immediately after afterFile '{Path.GetFileName sourceFiles[i]}' (compile index {i})"
                let before = if i + 1 < len then Some sourceFiles[i + 1] else None
                (i + 1), Some sourceFiles[i], before
            | None ->
                match afterArg with
                | Some af ->
                    notes.Add
                        $"afterFile '{af}' was not found in {Path.GetFileName chosenFsproj}'s compile order — defaulted to end-of-project insertion"
                | None -> ()

                match heuristicIdx with
                | Some i ->
                    notes.Add
                        $"no afterFile given — placed after the last neighbour sharing namespace root '{segDisplay}' (compile index {i})"

                    let before = if i + 1 < len then Some sourceFiles[i + 1] else None
                    (i + 1), Some sourceFiles[i], before
                | None ->
                    if len > 0 then
                        notes.Add
                            "no afterFile or namespace match — defaulted to end-of-project insertion (the new file can reference every existing file)"

                        len, Some sourceFiles[len - 1], None
                    else
                        notes.Add "project has no source files — the new file would be the first <Compile> entry"
                        0, None, None

        let proposedBaseLower = baseNameLower fileNameRaw

        match sourceFiles |> Array.tryFindIndex (fun f -> baseNameLower f = proposedBaseLower) with
        | Some i ->
            notes.Add
                $"a file named '{Path.GetFileName fileNameRaw}' already exists at compile index {i} — this plan assumes a NEW file; pick a different name to avoid a duplicate <Compile> include"
        | None -> ()

        if
            not (
                fileNameRaw.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
                || fileNameRaw.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)
            )
        then
            notes.Add $"'{fileNameRaw}' does not end in .fs/.fsi — F# <Compile> items are normally .fs source files"

        if suggestedDecl <> "" then
            notes.Add $"suggested top declaration for {Path.GetFileName fileNameRaw}: {suggestedDecl}"

        let windowLo = max 0 (recIndex - 3)
        let windowHi = min (len - 1) (recIndex + 2)

        let nearby =
            [ for idx in windowLo..windowHi ->
                  jobj
                      [ "file", jstr sourceFiles[idx]
                        "compileIndex", jint idx
                        "topNamespaceOrModule", strOrNull topDecls[idx] ]
                  :> JsonNode ]

        let earlierCount = recIndex
        let laterCount = len - recIndex

        let dependencyNote =
            $"At compile index {recIndex}, {Path.GetFileName fileNameRaw} can reference symbols from the {earlierCount} earlier file(s) but NOT the {laterCount} later file(s). F# resolves names strictly in <Compile> order, so a forward or cyclic reference is a compile error (FS0039). Run fcs_check_compile_order after editing the .fsproj to confirm the order holds."

        let projDir = Path.GetDirectoryName chosenFsproj

        let relInclude (absPath: string) =
            try
                Path.GetRelativePath(projDir, absPath)
            with _ ->
                Path.GetFileName absPath

        let fsprojOp =
            match insertAfterOpt with
            | Some after ->
                $"add <Compile Include=\"{fileNameRaw}\" /> immediately after <Compile Include=\"{relInclude after}\" /> in {Path.GetFileName chosenFsproj}"
            | None -> $"add <Compile Include=\"{fileNameRaw}\" /> as the first <Compile> item in {Path.GetFileName chosenFsproj}"

        jobj
            [ "status", jstr "succeeded"
              "project", jstr chosenFsproj
              "fileName", jstr fileNameRaw
              "recommendedCompileIndex", jint recIndex
              "totalCompileFiles", jint len
              "insertAfter", strOrNull insertAfterOpt
              "insertBefore", strOrNull insertBeforeOpt
              "nearbyFiles", JsonArray(List.toArray nearby) :> JsonNode
              "inferredNamespaceConvention", jstr inferredConvention
              "dependencyNote", jstr dependencyNote
              "fsprojOp", jstr fsprojOp
              "notes", JsonArray(notes.ToArray() |> Array.map jstr) :> JsonNode ]
        :> JsonNode

    /// fcs_analyzer_setup_preview (#75) — read-only planner that reports what to ADD to
    /// enable F# analyzers (analyzer package refs + GeneratePathProperty, FSharp.Analyzers.Build,
    /// the FSharpAnalyzersOtherFlags property, a local fsharp-analyzers tool manifest) WITHOUT
    /// applying anything. Pure .fsproj/.props/.json reads — no FCS — so it delegates to the
    /// ProjectHealth helper, which reuses project_health's analyzer detection for the current view.
    member _.AnalyzerSetupPreview(args: FcsAnalyzerSetupPreviewArgs) : Task<JsonNode> =
        Task.FromResult(FsLangMcp.ProjectHealth.analyzerSetupPreview args)

    /// fcs_review_scan — read-only, AST-based review-candidate inventory. Parses each
    /// target file (parse-only, no type-check) and emits structurally interesting sites
    /// for a human/agent to eyeball: review CANDIDATES, never asserted bugs. Writes nothing.
    member this.ReviewScan(args: FcsReviewScanArgs) : Task<JsonNode> =
        task {
            // ── Resolve the wanted-category set (None / [] ⇒ every category) ─────
            let wantedResult =
                match args.categories with
                | None
                | Some [] -> Ok(Set.ofList ReviewScanner.allCategories)
                | Some requested ->
                    let normalized =
                        requested
                        |> List.choose (fun c -> if String.IsNullOrWhiteSpace c then None else Some(c.Trim()))

                    let unknown = normalized |> List.filter (ReviewScanner.isKnownCategory >> not)

                    if not unknown.IsEmpty then
                        let unknownList = String.concat ", " unknown
                        let validList = String.concat ", " ReviewScanner.allCategories
                        Error $"Unknown categories: %s{unknownList}. Valid categories: %s{validList}."
                    else
                        Ok(Set.ofList normalized)

            match wantedResult with
            | Error message -> return jobj [ "status", jstr "invalid_args"; "message", jstr message ] :> JsonNode
            | Ok wanted ->

            let maxResults = args.maxResults |> Option.defaultValue 200 |> max 1 |> min 1000

            // ── Decide the file set: single file (path) vs whole project (projectPath) ─
            let pathArg = args.path |> Option.filter (String.IsNullOrWhiteSpace >> not)
            let projectArg = args.projectPath |> Option.filter (String.IsNullOrWhiteSpace >> not)

            // (mode, scannable files, compiled files that do not exist on disk)
            let filesResult: Result<string * string list * string list, JsonNode> =
                match pathArg with
                | Some path ->
                    match validateSourcePath "fcs_review_scan" None path with
                    | Some err -> Error err
                    | None -> Ok("file", [ normalizePath path ], [])
                | None ->
                    match projectArg with
                    | None ->
                        Error(
                            jobj
                                [ "status", jstr "invalid_args"
                                  "message",
                                  jstr
                                      "fcs_review_scan needs a target: pass `path` (one file) or `projectPath` (a .fsproj), or call set_project first." ]
                            :> JsonNode
                        )
                    | Some projectPath ->
                        let fullProject = normalizePath projectPath

                        if not (File.Exists fullProject) then
                            Error(
                                jobj
                                    [ "status", jstr "invalid_args"
                                      "message", jstr $"fcs_review_scan: project file does not exist: %s{fullProject}" ]
                                :> JsonNode
                            )
                        else
                            match tryReadProject fullProject with
                            | Error reason ->
                                Error(
                                    jobj
                                        [ "status", jstr "error"
                                          "message", jstr $"fcs_review_scan: project file cannot be read: %s{reason}" ]
                                    :> JsonNode
                                )
                            | Ok doc ->
                                let workspaceRoot = Path.GetDirectoryName fullProject

                                let resolved, unresolved =
                                    filterProjectFiles workspaceRoot (defaultFilterOptions Review) (compileFiles fullProject doc)
                                    |> fun result ->
                                        result.Included
                                        |> List.map (fun f -> f.Path)
                                        |> List.distinct
                                        |> List.sort
                                        |> List.partition File.Exists

                                Ok("project", resolved, unresolved)

            match filesResult with
            | Error err -> return err
            | Ok(mode, files, unresolvedFiles) ->

            // ── Parse each file (parse-only) and collect candidates ──────────────
            let scanned = ResizeArray<string>()
            let parseErrors = ResizeArray<JsonNode>()
            // (category, file, sortLine, sortColumn, node) — node pre-built, the rest for ordering + counting.
            let collected = ResizeArray<string * string * int * int * JsonNode>()

            for file in files do
                try
                    let source = File.ReadAllText file
                    let sourceText = SourceText.ofString source

                    let parsingOptions =
                        { FSharpParsingOptions.Default with
                            SourceFiles = [| file |] }

                    let! parseResults = checker.ParseFile(file, sourceText, parsingOptions) |> asTask
                    scanned.Add file

                    let lines = source.Replace("\r\n", "\n").Split('\n')

                    let lineTextAt (oneBasedLine: int) =
                        if oneBasedLine >= 1 && oneBasedLine <= lines.Length then
                            let raw = lines[oneBasedLine - 1].Trim()
                            if raw.Length > 200 then raw.Substring(0, 200) + "..." else raw
                        else
                            ""

                    for candidate in ReviewScanner.scan wanted parseResults.ParseTree do
                        let r = candidate.Range

                        let node =
                            jobj
                                [ "category", jstr candidate.Category
                                  "file", jstr file
                                  "range", rangeToJsonNoFile r
                                  "lineText", jstr (lineTextAt r.StartLine)
                                  "note", jstr candidate.Note ]
                            :> JsonNode

                        collected.Add(candidate.Category, file, r.StartLine, r.StartColumn, node)
                with ex ->
                    parseErrors.Add(jobj [ "file", jstr file; "message", jstr ex.Message ] :> JsonNode)

            // ── Order, count over the FULL set, then cap to maxResults ───────────
            let ordered =
                collected |> Seq.sortBy (fun (_, file, line, col, _) -> file, line, col) |> Seq.toList

            let byCategory =
                ordered
                |> List.countBy (fun (category, _, _, _, _) -> category)
                |> List.sortBy fst
                |> List.map (fun (category, n) -> category, jint n)
                |> jobj

            let total = ordered.Length

            let pageNodes =
                ordered |> List.truncate maxResults |> List.map (fun (_, _, _, _, node) -> node)

            let countsNode =
                jobj [ "total", jint total; "returned", jint pageNodes.Length; "byCategory", byCategory ]
                :> JsonNode

            // A review scan must never claim coverage it did not achieve: any IN-SCOPE
            // compiled file missing on disk downgrades the verdict to `partial` (#160).
            // Filter-excluded entries (generated, obj/bin, tests) are deliberately NOT
            // counted — their absence is expected pre-build in this parse-only mode;
            // full-list hygiene is project_health.missingFiles' job.
            return
                jobj
                    [ "status", jstr (if List.isEmpty unresolvedFiles then "succeeded" else "partial")
                      "mode", jstr mode
                      "scanned", JsonArray(scanned.ToArray() |> Array.map jstr) :> JsonNode
                      "unresolvedFiles", JsonArray(unresolvedFiles |> List.map jstr |> List.toArray) :> JsonNode
                      "candidates", JsonArray(pageNodes |> List.toArray) :> JsonNode
                      "counts", countsNode
                      "truncated", jbool (total > pageNodes.Length)
                      "parseErrors", JsonArray(parseErrors.ToArray()) :> JsonNode ]
                :> JsonNode
        }

    // ── fcs_dead_code (#70): conservative dead-code candidate analysis ────────────────
    // A CLEANUP/REVIEW pass — candidates, NOT deletions, NOT navigation. Reuses the same
    // project-sweep machinery as `find` (ResolveFsprojOptions → ProjectSweepUses →
    // GetAllUsesOfAllSymbols). For every DEFINED module/type-level value or function
    // binding it counts NON-definition uses of that symbol across the swept projects; zero
    // non-def uses → a candidate. CONSERVATIVE by construction (prefers false negatives):
    //   • only private/internal bindings by default — public is presumed reachable by
    //     external callers (includePublic widens to public too);
    //   • a definition is matched to its uses by BOTH declaration-location AND FullName, so
    //     any plausible reference (incl. a self-recursive call) keeps a symbol live;
    //   • skips compiler-generated, [<EntryPoint>], constructors, property/event accessors,
    //     overrides / interface implementations, active patterns, and every non-mfv symbol
    //     (union cases, record fields, type definitions) — categories whose real usage FCS
    //     may under-attribute.
    // Writes nothing; always emits caveats. Use `find` to verify each candidate.
    member this.DeadCode(args: FcsDeadCodeArgs) : Task<JsonNode> =
        task {
            let includePublic = args.includePublic |> Option.defaultValue false
            let maxResults = args.maxResults |> Option.defaultValue 100 |> max 0 |> min 500

            // Resolve the sweep target like Find/TestsForSymbol: explicit projectPath
            // (Program.fs already falls back to the active set_project), else nothing.
            let sweepTargetOpt =
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath

            match sweepTargetOpt with
            | None ->
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message",
                          jstr
                              "fcs_dead_code needs a project context: pass projectPath (.fsproj/.sln/.slnx) or call set_project first." ]
                    :> JsonNode
            | Some sweepTarget ->

            let projects = SolutionParsing.listProjects sweepTarget

            if projects.Length = 0 then
                return
                    jobj
                        [ "status", jstr "invalid_args"
                          "message", jstr $"fcs_dead_code could not resolve any .fsproj to sweep from: {sweepTarget}" ]
                    :> JsonNode
            else

            // ── Skip predicates over an mfv (every read guarded — FCS throws on synthetics) ──
            // `flag onError f` evaluates a boolean property; on an FCS throw it yields the
            // caller-chosen default so an unreadable symbol is treated CONSERVATIVELY
            // (positive requirement defaults false; each skip-flag defaults true).
            let flag (onError: bool) (f: unit -> bool) =
                try
                    f ()
                with _ ->
                    onError

            let isEntryPoint (m: FSharpMemberOrFunctionOrValue) =
                try
                    m.Attributes
                    |> Seq.exists (fun a ->
                        try
                            let tn = a.AttributeType.FullName

                            not (isNull tn)
                            && (tn = "Microsoft.FSharp.Core.EntryPointAttribute"
                                || tn.EndsWith(".EntryPointAttribute", StringComparison.Ordinal))
                        with _ ->
                            false)
                with _ ->
                    false

            let declaringIsInterface (m: FSharpMemberOrFunctionOrValue) =
                try
                    match m.DeclaringEntity with
                    | Some e -> e.IsInterface
                    | None -> false
                with _ ->
                    false

            // A symbol is a dead-code CANDIDATE shape iff it is a module/type-level value or
            // function binding that is none of the skip categories. Non-mfv symbols (union
            // cases, record fields, type/module entities) are excluded outright.
            let isCandidateShape (sym: FSharpSymbol) =
                match sym with
                | :? FSharpMemberOrFunctionOrValue as m ->
                    flag false (fun () -> m.IsModuleValueOrMember)
                    && not (flag true (fun () -> m.IsCompilerGenerated))
                    && not (flag true (fun () -> m.IsConstructor))
                    && not (flag true (fun () -> m.IsImplicitConstructor))
                    && not (flag true (fun () -> m.IsPropertyGetterMethod))
                    && not (flag true (fun () -> m.IsPropertySetterMethod))
                    && not (flag true (fun () -> m.IsEventAddMethod))
                    && not (flag true (fun () -> m.IsEventRemoveMethod))
                    && not (flag true (fun () -> m.IsOverrideOrExplicitInterfaceImplementation))
                    && not (flag true (fun () -> m.IsExplicitInterfaceImplementation))
                    && not (flag true (fun () -> m.IsActivePattern))
                    && not (flag true (fun () -> m.IsExtensionMember))
                    && not (declaringIsInterface m)
                    && not (isEntryPoint m)
                | _ -> false

            // Keep only the accessibilities in scope: private/internal always, public only
            // under includePublic. "unknown" (synthetic / unreadable) is never a candidate.
            let accKeep (sym: FSharpSymbol) =
                match accessibilityString sym with
                | "private"
                | "internal" -> true
                | "public" -> includePublic
                | _ -> false

            let declRangeOf (sym: FSharpSymbol) : range option =
                try
                    sym.DeclarationLocation
                with _ ->
                    None

            let fullNameOf (sym: FSharpSymbol) : string option =
                try
                    match sym.FullName with
                    | null -> None
                    | fn when String.IsNullOrWhiteSpace fn -> None
                    | fn -> Some fn
                with _ ->
                    None

            let keyOfRange (dr: range) =
                $"{normalizePath dr.FileName}:{dr.StartLine}:{dr.StartColumn}:{dr.EndLine}:{dr.EndColumn}"

            // Accumulate across ALL swept projects FIRST, then filter — so a use in a
            // later-swept project still rescues a definition swept earlier.
            let nonDefDeclKeys = System.Collections.Generic.HashSet<string>()
            let nonDefFullNames = System.Collections.Generic.HashSet<string>()

            let defByKey =
                System.Collections.Generic.Dictionary<
                    string,
                    {| Name: string
                       FullName: string
                       Kind: string
                       Accessibility: string
                       File: string
                       StartLine: int
                       StartCol: int
                       EndLine: int
                       EndCol: int
                       Project: string |}
                 >()

            // #100 review: count only projects whose sweep actually succeeded. Reporting
            // projects.Length while the catch below swallows load failures would tell an
            // agent "N scanned, 0 dead" when 0 projects were analyzed (unrestored solution).
            let mutable scannedOk = 0

            for fsproj in projects do
                try
                    let projDisplay = Path.GetFileNameWithoutExtension fsproj
                    let! options, _ = this.ResolveFsprojOptions(fsproj)

                    let usesKey = analysisSnapshotKey options

                    let! allUses, _ = this.ProjectSweepUses(usesKey, options, 120000)
                    scannedOk <- scannedOk + 1

                    for u in allUses do
                        let sym = u.Symbol

                        if u.IsFromDefinition then
                            // Record a candidate-shape definition, keyed by its own
                            // declaration location (the stable cross-compilation anchor).
                            if accKeep sym && isCandidateShape sym then
                                match declRangeOf sym with
                                | Some dr ->
                                    let key = keyOfRange dr

                                    if not (defByKey.ContainsKey key) then
                                        defByKey[key] <-
                                            {| Name = sym.DisplayName
                                               FullName = (fullNameOf sym |> Option.defaultValue "")
                                               Kind = symbolKind sym
                                               Accessibility = accessibilityString sym
                                               File = normalizePath dr.FileName
                                               StartLine = dr.StartLine
                                               StartCol = dr.StartColumn
                                               EndLine = dr.EndLine
                                               EndCol = dr.EndColumn
                                               Project = projDisplay |}
                                | None -> ()
                        else
                            // A NON-definition use marks its target symbol LIVE — by both the
                            // target's declaration location and its FullName (either match
                            // keeps the symbol off the candidate list).
                            match declRangeOf sym with
                            | Some dr -> nonDefDeclKeys.Add(keyOfRange dr) |> ignore
                            | None -> ()

                            match fullNameOf sym with
                            | Some fn -> nonDefFullNames.Add fn |> ignore
                            | None -> ()
                with _ ->
                    // A project that fails to resolve/check is skipped — the candidate scan
                    // is best-effort, never a hard failure for one bad project.
                    ()

            let candidatesAll =
                defByKey
                |> Seq.filter (fun kv ->
                    let v = kv.Value

                    not (nonDefDeclKeys.Contains kv.Key)
                    && (v.FullName = "" || not (nonDefFullNames.Contains v.FullName)))
                |> Seq.map (fun kv -> kv.Value)
                |> Seq.sortBy (fun v -> v.File, v.StartLine, v.StartCol, v.Name)
                |> Seq.toArray

            let total = candidatesAll.Length
            let page = candidatesAll |> Array.truncate maxResults
            let truncated = total > page.Length

            let candidateToJson
                (v:
                    {| Name: string
                       FullName: string
                       Kind: string
                       Accessibility: string
                       File: string
                       StartLine: int
                       StartCol: int
                       EndLine: int
                       EndCol: int
                       Project: string |})
                =
                let rangeNode =
                    jobj
                        [ "startLine", jint v.StartLine
                          "startColumn", jint v.StartCol
                          "endLine", jint v.EndLine
                          "endColumn", jint v.EndCol ]
                    :> JsonNode

                let declaredIn =
                    jobj [ "file", jstr v.File; "range", rangeNode ] :> JsonNode

                jobj
                    [ "name", jstr v.Name
                      "fullName", jstrOrNull v.FullName
                      "kind", jstr v.Kind
                      "accessibility", jstr v.Accessibility
                      "declaredIn", declaredIn
                      "note", jstr "See verificationHint." ]
                :> JsonNode

            let caveats =
                [ "candidates only — a conservative cleanup pass, NOT a deletion list; run `find` on each before removing"
                  "reflection, dynamic invocation, serialization, and DI wiring can use a symbol that has no static reference"
                  "interface implementations, overrides, and [<EntryPoint>] are excluded, but indirect reachability is not modeled"
                  "a symbol used ONLY by tests still appears as a candidate — check the test projects too"
                  (if includePublic then
                       "includePublic=true: public symbols are included; any external or published consumer makes them live"
                   else
                       "public symbols are excluded by default (presumed reachable by external callers); pass includePublic=true to include them")
                  "union cases, record fields, and type definitions are NOT analyzed (FCS may under-attribute their pattern/usage sites)" ]
                @ (if truncated then
                       [ $"results capped at {maxResults}; {total - page.Length} more candidate(s) not shown — raise maxResults" ]
                   else
                       [])

            return
                jobj
                    [ "status", jstr "succeeded"
                      // projectsScanned = projects actually analyzed; projectsRequested =
                      // total in scope. A gap means some projects failed to load (e.g.
                      // unrestored) and their symbols were NOT considered — do not read
                      // an empty candidate list as "nothing is dead" when scanned < requested.
                      "projectsScanned", jint scannedOk
                      "projectsRequested", jint projects.Length
                      "candidates", JsonArray(page |> Array.map candidateToJson) :> JsonNode
                      "candidateCount", jint total
                      "truncated", jbool truncated
                      "verificationHint",
                      jstr
                          "Each candidate has no non-definition use across the swept project(s); verify it with `find` before removing."
                      "caveats", JsonArray(caveats |> List.map jstr |> List.toArray) :> JsonNode ]
                :> JsonNode
        }

    // ── fcs_analyzer_diagnostics (#72): report F# ANALYZER diagnostics, grouped ───────────
    // F# analyzers are NOT run by FCS — they run via the fsharp-analyzers CLI / Analyzers.SDK.
    // This member detects the analyzer configuration exactly as project_health does
    // (ProjectHealth.detectAnalyzerConfig), then — when a runner is available — invokes the
    // CLI and parses its SARIF into a grouped, agent-friendly shape. When no runner is
    // available it does NOT fake diagnostics: it reports the configured analyzer packages
    // truthfully plus a clear note. Read-only; writes nothing. The SARIF parser + grouping
    // (AnalyzerDiagnostics module) carry the tested logic; the live run is environment-gated.
    // Fully synchronous (no FCS await): project-XML detection plus an optional, env-gated
    // CLI run. Returned via Task.FromResult like the other synchronous tools
    // (fsharp_project_inspect / fsharp_runtime_status) — a `task {}` with no `let!`/`do!`
    // would not statically compile to a resumable state machine (FS3511 under Release).
    member _.AnalyzerDiagnostics(args: FcsAnalyzerDiagnosticsArgs) : Task<JsonNode> =
        let invalidArgs (message: string) : JsonNode =
            jobj [ "status", jstr "invalid_args"; "message", jstr message ] :> JsonNode

        let result: JsonNode =
            match
                args.projectPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map normalizePath
            with
            | None ->
                invalidArgs
                    "fcs_analyzer_diagnostics needs a project context: pass projectPath (.fsproj/.sln/.slnx) or call set_project first."
            | Some target ->
                let projects = SolutionParsing.listProjects target

                if projects.Length = 0 then
                    invalidArgs $"fcs_analyzer_diagnostics could not resolve any .fsproj from: {target}"
                else
                    // Detect analyzer config across every resolved project; union the packages.
                    let configs =
                        projects
                        |> Array.choose (fun p ->
                            match ProjectHealth.detectAnalyzerConfig p with
                            | Ok cfg -> Some(p, cfg)
                            | Error _ -> None)

                    let configured = configs |> Array.exists (fun (_, c) -> c.Configured)

                    let packagesJson =
                        configs
                        |> Array.collect (fun (_, c) -> c.Packages |> List.toArray)
                        |> Array.distinctBy (fun p -> p.PackageId)
                        |> Array.map ProjectHealth.analyzerPackageInfoToJson

                    if not configured then
                        AnalyzerDiagnostics.noAnalyzersResponse ()
                    else
                        match AnalyzerDiagnostics.detectRunner () with
                        | None -> AnalyzerDiagnostics.configuredNotRunResponse packagesJson
                        | Some command ->
                            // Run the CLI only on projects that ACTUALLY have analyzers
                            // configured (#100 review): including Configured=false projects
                            // burned one CLI invocation each and let an analyzer-less project's
                            // run error flip a genuinely-clean configured project to run_failed.
                            let collected = ResizeArray<AnalyzerDiagnostics.AnalyzerDiagnostic>()
                            let runErrors = ResizeArray<string>()

                            for projectPath, cfg in configs do
                                if cfg.Configured then
                                    match AnalyzerDiagnostics.runAnalyzers command projectPath with
                                    | Ok sarif -> collected.AddRange(AnalyzerDiagnostics.parseSarif sarif)
                                    | Error e -> runErrors.Add e

                            if collected.Count = 0 && runErrors.Count > 0 then
                                AnalyzerDiagnostics.runFailedResponse packagesJson (String.concat "; " runErrors)
                            else
                                let maxResults =
                                    args.maxResults |> Option.defaultValue 200 |> max 1 |> min 1000

                                AnalyzerDiagnostics.okResponse
                                    packagesJson
                                    (List.ofSeq collected)
                                    args.severity
                                    maxResults

        Task.FromResult result
