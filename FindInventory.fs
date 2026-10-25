/// Count-only, per-query inventory collected before find's site pagination.
module FsLangMcp.FindInventory

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FsLangMcp.Types

/// The testable, source-free boundary between FCS and the inventory accumulator.
type InventorySite =
    { File: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int
      IsDefinition: bool
      SymbolId: string
      FullName: string }

[<Struct>]
type private SiteKey =
    { File: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int }

/// A physical range contributes once to uniqueSites and once to each observed
/// category. Linked contexts can classify one range differently: preserving both
/// categories keeps every partial count monotonic (a genuine lower bound).
type private Counts() =
    let sites = Dictionary<SiteKey, int>()
    let mutable definitions = 0
    let mutable references = 0

    member _.Observe(key, isDefinition) =
        let previous =
            match sites.TryGetValue key with
            | true, categories -> categories
            | false, _ -> 0

        let category = if isDefinition then 1 else 2

        if previous &&& category = 0 then
            sites[key] <- previous ||| category

            if isDefinition then
                definitions <- definitions + 1
            else
                references <- references + 1

    member _.Count = sites.Count

    member _.Fields =
        [ "definitions", jint definitions
          "references", jint references
          "uniqueSites", jint sites.Count ]

type private QueryCounts() =
    member val Total = Counts()
    // Keep order incrementally while classification is deadline-checked, so
    // response construction never has to sort an unbounded collected tail.
    member val ByProject = SortedDictionary<int, Counts>()
    member val Symbols = SortedDictionary<string, string>(StringComparer.Ordinal)

[<Literal>]
let MaxIllustrativeSymbols = 8

[<Literal>]
let MaxIllustrativeNameChars = 256

let private normalizeFile (path: string) =
    let normalized = Path.GetFullPath path

    if OperatingSystem.IsWindows() then
        normalized.ToUpperInvariant()
    else
        normalized

let private symbolIdentity (symbol: FSharpSymbol) =
    let fullName =
        try
            symbol.FullName |> Option.ofObj |> Option.defaultValue symbol.DisplayName
        with _ ->
            symbol.DisplayName

    let assembly =
        try
            symbol.Assembly.SimpleName
        with _ ->
            ""

    let declaration =
        match symbol.DeclarationLocation with
        | Some location ->
            JsonArray(
                jstr (normalizeFile location.FileName),
                jint location.StartLine,
                jint location.StartColumn,
                jint location.EndLine,
                jint location.EndColumn
            )
            :> JsonNode
        | None -> null

    let signature =
        match symbol with
        | :? FSharpMemberOrFunctionOrValue as value ->
            try
                value.FullType.Format(FSharpDisplayContext.Empty)
            with _ ->
                ""
        | _ -> ""

    let identity =
        JsonArray(jstr assembly, jstr (symbol.GetType().Name), jstr fullName, declaration, jstr signature)
            .ToJsonString()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    identity, fullName

/// One collector is owned by one find request. Project indices reference the
/// caller's shared, normalized-fsproj ledger (never project display basenames).
type Collector
    (
        queries: string array,
        exact: bool,
        definitionsOnly: bool,
        includeDeclaration: bool,
        fileScopePath: string option
    ) =
    let queries = Array.copy queries
    let counts = queries |> Array.map (fun _ -> QueryCounts())
    let projectComplete = Dictionary<int, bool>()
    let fileScopePath = fileScopePath |> Option.map normalizeFile

    let includeSite (site: InventorySite) =
        (not definitionsOnly || site.IsDefinition)
        && (definitionsOnly || includeDeclaration || not site.IsDefinition)
        && (fileScopePath |> Option.forall (fun file -> file = normalizeFile site.File))

    member _.HasMatches = counts |> Array.exists (fun query -> query.Total.Count > 0)

    /// Observe only a query match. Scope/declaration filters run before either
    /// counter, and overlapping queries never share their deduplication sets.
    member _.ObserveSite(queryIndex: int, projectIndex: int, site: InventorySite) =
        if queryIndex < 0 || queryIndex >= counts.Length then
            invalidArg (nameof queryIndex) "Query index is outside this inventory."

        if projectIndex < 0 then
            invalidArg (nameof projectIndex) "Project indices must be non-negative."

        if includeSite site then
            let key =
                { File = normalizeFile site.File
                  StartLine = site.StartLine
                  StartColumn = site.StartColumn
                  EndLine = site.EndLine
                  EndColumn = site.EndColumn }

            let queryCounts = counts[queryIndex]
            queryCounts.Total.Observe(key, site.IsDefinition)
            if not (queryCounts.Symbols.ContainsKey site.SymbolId) then
                queryCounts.Symbols.Add(site.SymbolId, site.FullName)

            let projectCounts =
                match queryCounts.ByProject.TryGetValue projectIndex with
                | true, existing -> existing
                | false, _ ->
                    let created = Counts()
                    queryCounts.ByProject.Add(projectIndex, created)
                    created

            projectCounts.Observe(key, site.IsDefinition)

    /// Generic loop seam lets regression tests exercise deadlines and membership
    /// without mocking FCS symbol objects. Projection happens once per matched use.
    member this.ObserveUses
        (
            projectIndex: int,
            uses: 'Use array,
            matches: string -> bool -> 'Use -> bool,
            projectSite: 'Use -> InventorySite,
            canContinue: unit -> bool
        ) =
        if projectIndex < 0 then
            invalidArg (nameof projectIndex) "Project indices must be non-negative."

        let priorComplete =
            match projectComplete.TryGetValue projectIndex with
            | true, prior -> prior
            | false, _ -> true

        // If matching or FCS identity projection throws, retained partial rows
        // must not be presented as a fully classified project.
        projectComplete[projectIndex] <- false
        let mutable useIndex = 0
        let mutable complete = true

        while useIndex < uses.Length && complete do
            if not (canContinue ()) then
                complete <- false
            else
                let symbolUse = uses[useIndex]
                let mutable projected = None
                let mutable queryIndex = 0

                while queryIndex < queries.Length && complete do
                    if not (canContinue ()) then
                        complete <- false
                    elif matches queries[queryIndex] exact symbolUse then
                        let site =
                            match projected with
                            | Some existing -> existing
                            | None ->
                                let created = projectSite symbolUse
                                projected <- Some created
                                created

                        this.ObserveSite(queryIndex, projectIndex, site)

                    queryIndex <- queryIndex + 1

                useIndex <- useIndex + 1

        if complete && not (canContinue ()) then
            complete <- false

        // A later completed observation must not conceal an earlier partial pass.
        projectComplete[projectIndex] <- priorComplete && complete
        complete

    /// Called once immediately after the shared project's FCS allUses sweep.
    member this.ObserveProject
        (
            projectIndex: int,
            uses: FSharpSymbolUse array,
            matches: string -> bool -> FSharpSymbol -> bool,
            canContinue: unit -> bool
        ) =
        this.ObserveUses(
            projectIndex,
            uses,
            (fun query isExact symbolUse -> matches query isExact symbolUse.Symbol),
            (fun symbolUse ->
                let location = symbolUse.Range
                let identity, fullName = symbolIdentity symbolUse.Symbol

                { File = location.FileName
                  StartLine = location.StartLine
                  StartColumn = location.StartColumn
                  EndLine = location.EndLine
                  EndColumn = location.EndColumn
                  IsDefinition = symbolUse.IsFromDefinition
                  SymbolId = identity
                  FullName = fullName }),
            canContinue
        )

    /// Coverage comes from find's existing project/discovery ledger. These arrays
    /// contain counts, never source rows. The caller must apply the production
    /// 60k serializer guard to this entire response; v1 has no inventory pagination.
    member _.ToJson(coverageComplete: bool, ?canContinue: unit -> bool) =
        let canContinue = canContinue |> Option.defaultValue (fun () -> true)

        let ensureCanContinue () =
            if not (canContinue ()) then
                raise (TimeoutException("The find inventory response-construction deadline expired."))

        ensureCanContinue ()

        let countsComplete =
            coverageComplete
            && (projectComplete.Values
                |> Seq.forall (fun complete ->
                    ensureCanContinue ()
                    complete))

        let rows =
            counts
            |> Array.mapi (fun queryIndex queryCounts ->
                ensureCanContinue ()
                let matched = queryCounts.Total.Count > 0

                let projects =
                    queryCounts.ByProject
                    |> Seq.map (fun project ->
                        ensureCanContinue ()
                        let complete =
                            match projectComplete.TryGetValue project.Key with
                            | true, complete -> complete
                            | false, _ -> coverageComplete

                        jobj
                            ([ "projectIndex", jint project.Key
                               "countsComplete", jbool complete
                               "countsAreLowerBounds", jbool (not complete) ]
                             @ project.Value.Fields)
                        :> JsonNode)
                    |> Seq.toArray

                let symbols =
                    queryCounts.Symbols
                    |> Seq.truncate MaxIllustrativeSymbols
                    |> Seq.map (fun symbol ->
                        ensureCanContinue ()
                        let fullName = symbol.Value
                        let truncated = fullName.Length > MaxIllustrativeNameChars

                        jobj
                            [ "symbolId", jstr symbol.Key
                              "fullName", jstr (if truncated then fullName[.. MaxIllustrativeNameChars - 1] else fullName)
                              "fullNameTruncated", jbool truncated ]
                        :> JsonNode)
                    |> Seq.toArray

                jobj
                    ([ "queryIndex", jint queryIndex
                       "query", jstr queries[queryIndex]
                       "matched", (if matched then jbool true elif countsComplete then jbool false else null)
                       "outcome", jstr (if matched then "found" elif countsComplete then "not_found" else "indeterminate")
                       "countsComplete", jbool countsComplete
                       "countsAreLowerBounds", jbool (not countsComplete)
                       "matchedSymbolCount", jint queryCounts.Symbols.Count
                       "ambiguous", (if queryCounts.Symbols.Count > 1 then jbool true elif countsComplete then jbool false else null)
                       "symbols", JsonArray(symbols) :> JsonNode
                       "symbolsTruncated", jbool (queryCounts.Symbols.Count > symbols.Length)
                       "perProject", JsonArray(projects) :> JsonNode ]
                     @ queryCounts.Total.Fields)
                :> JsonNode)

        ensureCanContinue ()

        jobj
            [ "aggregation", jstr "per_query"
              "countsOnly", jbool true
              "countsComplete", jbool countsComplete
              "countsAreLowerBounds", jbool (not countsComplete)
              "categoryCountsMayOverlap", jbool true
              "zeroProjectsOmitted", jbool true
              "queries", JsonArray(rows) :> JsonNode ]
