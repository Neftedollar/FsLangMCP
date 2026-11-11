/// Conservative, optional constructor context for check diagnostics (#322).
module internal FsLangMcp.ConstructorDiagnostics

open System
open System.IO
open System.Collections.Generic
open System.Text.Json.Nodes
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open FsLangMcp.Types

/// Exact compiler diagnostic identity, including the file and unchanged message.
type DiagnosticKey = string * int * string * int * int * int * int * string

[<NoEquality; NoComparison>]
type ConstructorResolution =
    {
        /// FCS's fully qualified declaring type name, including generic arity when present.
        /// Emitted as diagnostics[].resolvedType; this identifies a definition, not an
        /// inferred replacement or a claim that another same-named type was intended.
        ResolvedType: string
        /// Exact constructor identifier span; emitted as resolvedConstructorRange using
        /// the same 1-based lines / 0-based columns as the original diagnostic range.
        IdentifierRange: range
    }

// A linked source can be checked under several projects with different resolutions.
// Keep its diagnostic instances distinct rather than merging by source/message text.
type Enrichment = IReadOnlyDictionary<FSharpDiagnostic, ConstructorResolution>

let empty: Enrichment =
    Dictionary<FSharpDiagnostic, ConstructorResolution>(HashIdentity.Reference)

let combine (enrichments: Enrichment seq) : Enrichment =
    let combined =
        Dictionary<FSharpDiagnostic, ConstructorResolution>(HashIdentity.Reference)

    for enrichment in enrichments do
        for entry in enrichment do
            combined[entry.Key] <- entry.Value

    combined

let private key (d: FSharpDiagnostic) : DiagnosticKey =
    normalizePath d.FileName,
    d.ErrorNumber,
    string d.Severity,
    d.StartLine,
    d.StartColumn,
    d.EndLine,
    d.EndColumn,
    d.Message

let private spanKey (r: range) =
    normalizePath r.FileName, r.StartLine, r.StartColumn, r.EndLine, r.EndColumn

let private isRelevant (d: FSharpDiagnostic) =
    d.Severity = FSharpDiagnosticSeverity.Error
    && (d.ErrorNumber = 1 || d.ErrorNumber = 193 || d.ErrorNumber = 41)

let rec private calleeRange expression =
    match expression with
    | SynExpr.Ident ident -> Some ident.idRange
    | SynExpr.LongIdent(longDotId = ident) -> Some ident.Range
    | SynExpr.TypeApp(expr = inner)
    | SynExpr.Paren(expr = inner) -> calleeRange inner
    | _ -> None

let rec private typeRange typ =
    match typ with
    | SynType.LongIdent ident -> Some ident.Range
    | SynType.App(typeName = inner)
    | SynType.Paren(innerType = inner) -> typeRange inner
    | _ -> None

// Only complete argument expressions, not arbitrary descendants. A mismatch in
// `Widget(f "bad")` must not attribute f's argument error to Widget. Parentheses
// and individual tuple arguments are transparent, because FCS reports both forms.
let rec private argumentRanges (expression: SynExpr) =
    seq {
        yield expression.Range

        match expression with
        | SynExpr.Paren(expr = inner) -> yield! argumentRanges inner
        | SynExpr.Tuple(exprs = items) ->
            for item in items do
                yield! argumentRanges item
        | _ -> ()
    }

let private collect
    (parseTree: ParsedInput)
    (symbolUses: FSharpSymbolUse seq)
    (diagnostics: FSharpDiagnostic array)
    : Enrichment =
    let constructors =
        symbolUses
        |> Seq.choose (fun symbolUse ->
            match symbolUse.Symbol with
            | :? FSharpMemberOrFunctionOrValue as value when value.IsConstructor && not symbolUse.IsFromDefinition ->
                try
                    value.DeclaringEntity
                    |> Option.bind _.TryFullName
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.map (fun name ->
                        spanKey symbolUse.Range,
                        { ResolvedType = name
                          IdentifierRange = symbolUse.Range })
                with _ ->
                    None
            | _ -> None)
        |> Seq.groupBy fst
        |> Seq.choose (fun (span, entries) ->
            let distinct =
                entries |> Seq.map snd |> Seq.distinctBy _.ResolvedType |> Seq.toArray

            if distinct.Length = 1 then
                Some(span, distinct[0])
            else
                None)
        |> Map.ofSeq

    let diagnosticSpans =
        diagnostics
        |> Array.filter isRelevant
        |> Array.groupBy (fun d -> spanKey d.Range)
        |> Map.ofArray

    let candidates = ResizeArray<DiagnosticKey * ConstructorResolution>()

    let addCall identifierRange symbolRange argument callRange =
        match
            symbolRange
            |> Option.bind (spanKey >> fun span -> Map.tryFind span constructors)
        with
        | None -> ()
        | Some symbolResolution ->
            let resolution =
                { symbolResolution with
                    IdentifierRange = defaultArg identifierRange symbolResolution.IdentifierRange }

            seq {
                yield callRange
                yield! argumentRanges argument
            }
            |> Seq.distinctBy spanKey
            |> Seq.iter (fun span ->
                match Map.tryFind (spanKey span) diagnosticSpans with
                | None -> ()
                | Some matches ->
                    for diagnostic in matches do
                        candidates.Add(key diagnostic, resolution))

    ((), parseTree)
    ||> ParsedInput.fold (fun () _path node ->
        match node with
        | SyntaxNode.SynExpr(SynExpr.App(funcExpr = func; argExpr = argument; range = callRange)) ->
            let identifierRange = calleeRange func
            addCall identifierRange identifierRange argument callRange
        | SyntaxNode.SynExpr(SynExpr.New(targetType = typ; expr = argument; range = callRange)) ->
            // With `new`, FCS binds the complete type application (including
            // <T>); without it, it binds the callee identifier. Both lookups use
            // exact syntax ranges, never a nearby or contained symbol.
            addCall (typeRange typ) (Some typ.Range) argument callRange
        | _ -> ())

    let resolutions =
        candidates
        |> Seq.groupBy fst
        |> Seq.choose (fun (diagnostic, entries) ->
            // `Outer(Inner(...))` can place one diagnostic at both the outer argument
            // and the inner call. Neither is proven to be its target: abstain, even
            // when both constructors happen to belong to the same type.
            let distinct =
                entries
                |> Seq.map snd
                |> Seq.distinctBy (fun candidate -> candidate.ResolvedType, spanKey candidate.IdentifierRange)
                |> Seq.toArray

            if distinct.Length = 1 then
                Some(diagnostic, distinct[0])
            else
                None)
        |> Map.ofSeq

    let enriched =
        Dictionary<FSharpDiagnostic, ConstructorResolution>(HashIdentity.Reference)

    for diagnostic in diagnostics do
        match Map.tryFind (key diagnostic) resolutions with
        | Some resolution -> enriched[diagnostic] <- resolution
        | None -> ()

    enriched

let fromFileResults (parseResults: FSharpParseFileResults) (checkResults: FSharpCheckFileResults) : Enrichment =
    if not (checkResults.Diagnostics |> Array.exists isRelevant) then
        empty
    else
        try
            collect parseResults.ParseTree (checkResults.GetAllUsesOfAllSymbolsInFile()) checkResults.Diagnostics
        with _ ->
            // Error-recovery symbols can be incomplete. Context must never replace
            // the compiler's original diagnostics or turn a verdict into a failure.
            empty

/// Reuse the completed project check's symbol identities. Only files with eligible
/// diagnostics are parsed, without a second type-check; the caller owns the checked
/// input snapshot and deadline. Fast FSAC snapshots cannot supply this evidence.
let fromProjectResults
    (checker: FSharpChecker)
    (options: FSharpProjectOptions)
    (results: FSharpCheckProjectResults)
    (shouldContinue: unit -> bool)
    : Async<Enrichment> =
    async {
        let diagnostics = results.Diagnostics |> Array.filter isRelevant

        if diagnostics.Length = 0 || not (shouldContinue ()) then
            return empty
        else
            try
                let usesByFile =
                    results.GetAllUsesOfAllSymbols()
                    |> Array.groupBy (fun symbolUse -> normalizePath symbolUse.FileName)
                    |> Map.ofArray

                let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions options
                let sourceFiles = options.SourceFiles |> Array.map normalizePath |> Set.ofArray
                let enrichments = ResizeArray<Enrichment>()

                for file, fileDiagnostics in diagnostics |> Array.groupBy (fun d -> normalizePath d.FileName) do
                    if shouldContinue () && Set.contains file sourceFiles && File.Exists file then
                        try
                            let source = SourceText.ofString (File.ReadAllText file)
                            let! parseResults = checker.ParseFile(file, source, parsingOptions)

                            if shouldContinue () then
                                let uses = Map.tryFind file usesByFile |> Option.defaultValue [||]
                                let entries = collect parseResults.ParseTree uses fileDiagnostics
                                enrichments.Add entries
                        with _ ->
                            ()

                return combine enrichments
            with _ ->
                return empty
    }

let enrich (enrichment: Enrichment) (diagnostic: FSharpDiagnostic) (node: JsonNode) : JsonNode =
    match enrichment.TryGetValue diagnostic with
    | false, _ -> node
    | true, resolution ->
        let span = resolution.IdentifierRange
        node["resolvedType"] <- jstr resolution.ResolvedType

        node["resolvedConstructorRange"] <-
            jobj
                [ "startLine", jint span.StartLine
                  "startColumn", jint span.StartColumn
                  "endLine", jint span.EndLine
                  "endColumn", jint span.EndColumn ]

        node
