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
    // key is (resolved-options key + own source-file stamp + referenced-assembly stamp +
    // referenced-project source stamp): any own-source edit moves a file's mtime → MISS;
    // a dependency REBUILD moves the -r: DLL mtime → consumer MISS (0.10.1 Codex P1); a
    // dependency SOURCE EDIT without a rebuild doesn't move the DLL but DOES change the
    // referenced F# project's source file mtimes → consumer MISS via the new
    // referencedProjectSourcesStamp (0.10.1 Codex P2). Every MISS runs the original
    // ParseAndCheckProject + GetAllUsesOfAllSymbols path — a cached `find` is never staler
    // than an uncached one (correctness first, speed is the unchanged-project fast path).
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

    // Source-file write-time vector for the directly AND transitively referenced F# projects
    // (issue #131; 0.10.1 Codex P2). referencedAssembliesStamp (P1) stamps the -r: DLL
    // mtimes and catches a REBUILD of a dependency. But an EDIT to a dependency's source
    // WITHOUT a rebuild doesn't move the DLL mtime — yet FCS's ParseAndCheckProject for a
    // consumer reads referenced F# project sources directly via ReferencedProjects, so a
    // fresh consumer check WOULD see the edit. Without this stamp the consumer's key is
    // unchanged after a dependency source edit → ProjectSweepUses serves the consumer STALE.
    //
    // Fix: walk ReferencedProjects for each consumer project. For every FSharpReference case
    // (an F# P2P project, carrying the referenced project's FSharpProjectOptions), stamp its
    // SourceFiles the same way sourceFilesStamp does. Recurse transitively (FCS's
    // ReferencedProjects is NOT pre-flattened — only direct references appear at each level)
    // using a visited-set keyed on ProjectFileName to avoid re-stamping shared deps and
    // breaking on any hypothetical cycle. The root project itself is added to visited first
    // so it's not double-stamped from a transitive back-reference.
    //
    // PEReference and ILModuleReference carry no FSharpProjectOptions (they're prebuilt
    // binaries); those cases fall through to `| _ -> ()` — their mtime is already covered
    // by referencedAssembliesStamp. Missing/unreadable files map to -1, never throws.
    let referencedProjectSourcesStamp (projectOptions: FSharpProjectOptions) =
        let sb = System.Text.StringBuilder()
        let visited = System.Collections.Generic.HashSet<string>()
        visited.Add(projectOptions.ProjectFileName) |> ignore

        let rec stampProject (opts: FSharpProjectOptions) =
            for refProj in opts.ReferencedProjects do
                match refProj with
                | FSharpReferencedProject.FSharpReference(_outputFile, refOpts) ->
                    if visited.Add(refOpts.ProjectFileName) then
                        for f in refOpts.SourceFiles do
                            let ticks =
                                try
                                    if File.Exists f then File.GetLastWriteTimeUtc(f).Ticks else -1L
                                with _ ->
                                    -1L

                            sb.Append(ticks).Append('|') |> ignore

                        stampProject refOpts
                | _ -> ()

        stampProject projectOptions
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
                        symbolUse.Symbol.FullName,
                        r.StartLine,
                        r.StartColumn,
                        r.EndLine,
                        r.EndColumn)
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
                        jobj
                            [ "name", jstr symbolUse.Symbol.DisplayName
                              "fullName", jstrOrNull symbolUse.Symbol.FullName
                              "kind", jstr (symbolKind symbolUse.Symbol)
                              "accessibility", symbolAccessibility symbolUse.Symbol
                              "range", rangeToJson symbolUse.Range
                              "signature", jstr (symbolTypeString symbolUse.Symbol)
                              "declarationRange", tryDeclarationRange symbolUse.Symbol ]
                        :> JsonNode)

                let containerKinds =
                    [| "module"; "record"; "union"; "class"; "interface"; "enum"; "delegate"; "namespace" |]

                // summaryOnly (default): keep only module/type headers with name/kind/
                // fullName/range (no per-member signatures). summaryOnly=false restores
                // the full per-member output.
                let outEntries: JsonNode =
                    if summaryOnly then
                        let headers =
                            entries
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
                                      "range",
                                      (match e["range"] with
                                       | null -> null
                                       | r -> r.DeepClone()) ]
                                :> JsonNode)

                        JsonArray(headers) :> JsonNode
                    else
                        JsonArray(entries) :> JsonNode

                return
                    jobj
                        [ "status", jstr "succeeded"
                          "file", jstr path
                          "optionsSource", jstr optionsSource
                          "includePrivate", jbool includePrivate
                          "includeLocal", jbool includeLocal
                          "summaryOnly", jbool summaryOnly
                          "count", jint entries.Length
                          "memberCounts", memberCounts
                          "entries", outEntries
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
                    // source-stamp, referenced-assembly-stamp, referenced-project-sources-
                    // stamp). A cache HIT on an unchanged project skips BOTH
                    // ParseAndCheckProject AND the ~3s GetAllUsesOfAllSymbols re-walk; a
                    // MISS (first sweep, any own-source edit, any rebuild of a referenced
                    // project/assembly [Codex P1], OR any source edit of a referenced F#
                    // project without a rebuild [Codex P2]) runs the identical original path,
                    // so results are never served stale. The cache-or-compute lives in its
                    // own method (ProjectSweepUses) so this outer state machine stays
                    // statically compilable under Release optimization (a nested task CE
                    // inside the loop trips FS3511).
                    let usesKey =
                        $"{makeResolvedProjectCacheKey options}|{sourceFilesStamp options}|{referencedAssembliesStamp options}|{referencedProjectSourcesStamp options}"
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

    // ── fcs_tests_for_symbol (#60): the test-coverage slice of `find` ───────────
    // Reuses the multi-project sweep machinery, but keeps ONLY the test projects of the
    // active solution (detected the way project_health does — ProjectHealth.isTestProjectFile,
    // i.e. <IsTestProject>true> OR an xunit/nunit/expecto package ref). Each test project's
    // GetAllUsesOfAllSymbols() is filtered to uses of the queried symbol, and every use site
    // is tagged with its nearest enclosing test ([<Fact>]/[<Theory>]/[<Test>]/testCase),
    // located by scanning the source lines upward. Shares ProjectSweepUses' (#131) per-project
    // use cache, so a `find` already run on the solution makes this nearly free.
    member this.TestsForSymbol(args: FcsTestsForSymbolArgs) : Task<JsonNode> =
        task {
            match ArgsValidation.requireNonBlank "symbolQuery" args.symbolQuery with
            | Error envelope -> return envelope
            | Ok query ->

            let exact = args.exact |> Option.defaultValue true
            let maxResults = args.maxResults |> Option.defaultValue 100

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

            // The test-coverage slice: sweep only the solution's TEST projects.
            let testProjects =
                SolutionParsing.listProjects sweepTarget
                |> Array.filter FsLangMcp.ProjectHealth.isTestProjectFile

            // ── Enclosing-test detection (best-effort, textual) ──────────────────
            // Scan source lines upward from a use to the nearest test marker:
            //   • an Expecto label   → the quoted label string is the test name;
            //   • a test attribute   → the name of the let/member it decorates.
            let testAttrRegex =
                System.Text.RegularExpressions.Regex(
                    """\[<\s*(?:[\w.]+\.)?(?:Fact|Theory|Test|TestCase|TestMethod|Property)(?:Attribute)?\b""",
                    System.Text.RegularExpressions.RegexOptions.Compiled
                )

            let expectoLabelRegex =
                System.Text.RegularExpressions.Regex(
                    "\\b(?:ftestCaseAsync|ptestCaseAsync|testCaseAsync|ftestCase|ptestCase|testCase|ftestAsync|ptestAsync|testAsync|ftestProperty|ptestProperty|testProperty|test)\\s+\"([^\"]*)\"",
                    System.Text.RegularExpressions.RegexOptions.Compiled
                )

            let bindingNameRegex =
                System.Text.RegularExpressions.Regex(
                    """\b(?:let|member)\s+(?:rec\s+|inline\s+|mutable\s+|private\s+|internal\s+|this\.|_\.)*(``[^`]+``|[A-Za-z_][\w']*)""",
                    System.Text.RegularExpressions.RegexOptions.Compiled
                )

            let findEnclosingTest (lines: string array) (useLine1: int) : string option =
                if lines.Length = 0 then
                    None
                else
                    let startIdx = min (max 0 (useLine1 - 1)) (lines.Length - 1)
                    let mutable result = None
                    let mutable i = startIdx

                    while result.IsNone && i >= 0 do
                        let labelMatch = expectoLabelRegex.Match lines[i]

                        if labelMatch.Success then
                            result <- Some labelMatch.Groups[1].Value
                        elif testAttrRegex.IsMatch lines[i] then
                            // Scan downward from the attribute to the use for the decorated name.
                            let mutable j = i
                            let mutable name = None

                            while name.IsNone && j <= startIdx do
                                let bm = bindingNameRegex.Match lines[j]

                                if bm.Success then
                                    name <- Some(bm.Groups[1].Value.Trim('`'))
                                else
                                    j <- j + 1

                            result <- Some(name |> Option.defaultValue "<test>")
                        else
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
                       EnclosingTest: string option
                       LineText: string |}
                 >()

            for fsproj in testProjects do
                try
                    let projDisplay = Path.GetFileNameWithoutExtension fsproj
                    let! options, _ = this.ResolveFsprojOptions(fsproj)

                    let usesKey =
                        $"{makeResolvedProjectCacheKey options}|{sourceFilesStamp options}|{referencedAssembliesStamp options}|{referencedProjectSourcesStamp options}"

                    let! allUses, _ = this.ProjectSweepUses(usesKey, options)

                    for u in allUses do
                        if symbolMatches query exact u.Symbol then
                            // FSharpSymbolUse is a struct: bind Range before reading fields (FS0052).
                            let r = u.Range
                            let file = normalizePath r.FileName
                            let key = $"{file}:{r.StartLine}:{r.StartColumn}:{r.EndLine}:{r.EndColumn}"

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
                                       EnclosingTest = findEnclosingTest lines r.StartLine
                                       LineText = lineText |}
                with _ ->
                    // A test project that fails to resolve/check is skipped — the coverage
                    // slice is best-effort, never a hard failure for one bad project.
                    ()

            let sortedSites =
                siteByKey.Values
                |> Seq.toArray
                |> Array.sortBy (fun s -> s.File, s.StartLine, s.StartCol, s.EndLine, s.EndCol)

            let totalTests = sortedSites.Length
            let pageSites = sortedSites |> Array.truncate (max 0 maxResults)

            let testToJson
                (s:
                    {| File: string
                       StartLine: int
                       StartCol: int
                       EndLine: int
                       EndCol: int
                       Project: string
                       EnclosingTest: string option
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
                    | Some t -> jstr t
                    | None -> null

                jobj
                    [ "file", jstr s.File
                      "range", rangeNode
                      "enclosingTest", enclosing
                      "project", jstr s.Project
                      "lineText", jstr s.LineText ]
                :> JsonNode

            let testNodes = pageSites |> Array.map testToJson

            return
                jobj
                    [ "status", jstr "succeeded"
                      "symbol", jstr query
                      "tests", JsonArray(testNodes) :> JsonNode
                      "testCount", jint totalTests
                      "projectsScanned", jint testProjects.Length ]
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

    /// Deterministic reference-resolution probe over a project's resolved OtherOptions
    /// (#138). Returns (existing, total) `-r:`/`--reference:` targets that exist on disk.
    /// Used to tell an unrestored/unbuilt project apart from a genuinely-erroring one
    /// before running the FCS re-check. Resolution is cached, so this warms the same
    /// options FreshProjectCheck reuses. Never throws — an unloadable project yields
    /// (0, 0), which the caller treats as "could not probe" and falls through.
    member private this.ProbeReferenceResolution(fsproj: string) : Task<int * int> =
        task {
            try
                let! options, _ = this.ResolveFsprojOptions(normalizePath fsproj)
                return ReferenceResolution.probe options.OtherOptions
            with _ ->
                return 0, 0
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
                // Hard cap the surfaced `diagnostics` array so a genuinely-erroring large
                // project (or an unrestored false-error wall that slips past the probe)
                // can never overflow the MCP token ceiling. errorCount/warningCount/
                // totalDiagnostics stay FULL-set accurate — only the array is truncated.
                let diagnosticsCap = 50
                let cappedDiagNodes = diagNodes |> Array.truncate diagnosticsCap
                let diagnosticsTruncated = diagNodes.Length > diagnosticsCap

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
                        // Restore-awareness (#138): an unrestored/unbuilt project still
                        // evaluates its .fsproj, so FCS emits HUNDREDS of spurious FS0039
                        // "is not defined" diagnostics at `open` lines (the real cause is
                        // missing external reference assemblies, not the source). Detect
                        // that deterministically and return an honest "unknown" instead of
                        // a misleading false-error wall.
                        let! refExisting, refTotal = this.ProbeReferenceResolution(proj)

                        if ReferenceResolution.looksUnrestored refExisting refTotal then
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
                                    [||]
                                    [||]
                                    (Some
                                        $"project not built/restored — external references unresolved (existing {refExisting}/total {refTotal}); run dotnet restore && dotnet build")
                                    [ "projectsSwept", jint 1
                                      "restoreStatus", jstr "unrestored"
                                      "referencesResolved", JsonValue.Create(Math.Round(frac, 3)) :> JsonNode
                                      "referencesExisting", jint refExisting
                                      "referencesTotal", jint refTotal ]
                        else

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
                        let mutable unrestoredCount = 0

                        for proj in projects do
                            // Restore-awareness (#138): skip the FCS re-check for an
                            // unrestored project — it would only produce a spurious
                            // FS0039 false-error wall. Mark it unrestored instead.
                            let! refExisting, refTotal = this.ProbeReferenceResolution(proj)

                            if ReferenceResolution.looksUnrestored refExisting refTotal then
                                unrestoredCount <- unrestoredCount + 1
                                let frac = ReferenceResolution.fraction refExisting refTotal

                                perProject.Add(
                                    jobj
                                        [ "project", jstr (Path.GetFileNameWithoutExtension proj)
                                          "fsproj", jstr (normalizePath proj)
                                          "analyzed", jbool false
                                          "restoreStatus", jstr "unrestored"
                                          "referencesResolved", JsonValue.Create(Math.Round(frac, 3)) :> JsonNode
                                          "referencesExisting", jint refExisting
                                          "referencesTotal", jint refTotal
                                          "reason",
                                          jstr
                                              "project not built/restored — external references unresolved; run dotnet restore && dotnet build" ]
                                    :> JsonNode
                                )
                            else
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
                                diags.Length
                                nodes
                                files
                                reason
                                [ "projectsSwept", jint projects.Length
                                  "unrestoredCount", jint unrestoredCount
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
                          // Always request the full per-member output: ProjectOutline does
                          // its own summaryOnly shaping + memberCounts over these entries,
                          // so FileOutline's own summary default must NOT pre-collapse them.
                          summaryOnly = Some false
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
                    $"{f.Name}: {typeName f.FieldType}"
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
                            |> Seq.map (fun f -> try typeName f.FieldType with _ -> "?")
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
                                       signature = memberSignature m
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

            let pageEntities =
                allEntities
                |> List.skip (min pageOffset totalEntityCount)
                |> List.truncate pageSize

            let entityNodes =
                pageEntities
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
                Cursor.paginationFields "entities" totalEntityCount pageOffset pageSize pageEntities.Length

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

    member this.ProbeProjectOptions(fsprojPath: string) : Task<Result<ProjectOptionsInfo, string>> =
        task {
            try
                let! result = this.LoadProjectOptionsFromFsproj(fsprojPath)

                return
                    match result with
                    | Some options ->
                        let existing, total = ReferenceResolution.probe options.OtherOptions

                        Ok
                            { Source = "ionide-proj-info"
                              ReferencesExisting = existing
                              ReferencesTotal = total }
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

                    let usesKey =
                        $"{makeResolvedProjectCacheKey options}|{sourceFilesStamp options}|{referencedAssembliesStamp options}|{referencedProjectSourcesStamp options}"

                    let! allUses, diagnostics = this.ProjectSweepUses(usesKey, options)

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
        (args: FcsRefactorImpactArgs, ?renamePreview: RenamePreviewArgs -> Task<JsonNode>)
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
                  projectPath = args.projectPath
                  maxResults = Some 1000
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
                        | Some f -> baseProps @ [ "fsproj", jstr f ]
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
                    { symbolQuery = resolvedName
                      exact = Some true
                      path = None
                      text = None
                      projectPath = args.projectPath
                      maxResults = Some 100 }

            let testSites = arrayOf testsResult "tests"
            let testCount = readInt testsResult "testCount" |> Option.defaultValue testSites.Length

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
            let mutable apiIsPublic = false

            if wantApi then
                match definingFsproj with
                | None ->
                    apiSurfaceNode <-
                        Some(
                            jobj
                                [ "isPublic", jbool false
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

                    apiIsPublic <- affected.Count > 0

                    apiSurfaceNode <-
                        Some(
                            jobj
                                [ "isPublic", jbool apiIsPublic
                                  "project", jstr fsproj
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

            if (not matched) || totalSites = 0 then
                verify.Add(
                    $"no use sites found for '{resolvedName}' — it may be unused, dynamically referenced, or the name is wrong; double-check before changing it"
                )
            elif crossProject then
                let projectNamesStr = byProject |> Array.map fst |> String.concat ", "

                verify.Add(
                    $"{totalSites} cross-project site(s) across {projectCount} projects ({projectNamesStr}) — rebuild all affected projects"
                )
            else
                verify.Add($"{totalSites} site(s) in {fileCount} file(s) within one project — re-check the project after the change")

            if testCount > 0 then
                let namesStr =
                    if enclosingTestNames.Length > 0 then
                        enclosingTestNames |> Array.truncate 10 |> String.concat ", "
                    else
                        "(see tests list)"

                verify.Add($"{testCount} test reference(s) cover '{resolvedName}' — run: {namesStr}")

            if wantApi then
                if apiIsPublic then
                    verify.Add(
                        $"'{resolvedName}' is part of the public API surface — this is a BREAKING change; bump the minor version and update consumers"
                    )
                else
                    verify.Add($"'{resolvedName}' is not on the public API surface — the change stays internal")

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
                        $"rename '{resolvedName}' -> '{renameTo}' touches {renameEdits} edit(s) in {renameFiles} file(s); `fcs_rename_preview` has the exact edit set"
                    )
                else
                    verify.Add("rename preview unavailable (FSAC) — use the find sites as the edit set")

            // ── Assemble ─────────────────────────────────────────────────────────────
            let targetNode =
                jobj
                    ([ "symbol", jstr resolvedName ]
                     @ (match args.path with
                        | Some p when not (String.IsNullOrWhiteSpace p) -> [ "path", jstr (normalizePath p) ]
                        | _ -> [])
                     @ (match args.line with
                        | Some l -> [ "line", jint l ]
                        | None -> [])
                     @ (match args.character with
                        | Some c -> [ "character", jint c ]
                        | None -> [])
                     @ [ "resolvedVia", jstr resolvedVia ])
                :> JsonNode

            let impactNode =
                jobj
                    [ "totalSites", jint totalSites
                      "fileCount", jint fileCount
                      "projectCount", jint projectCount
                      "crossProject", jbool crossProject
                      "sitesByProject", JsonArray(sitesByProjectNodes) :> JsonNode
                      "affectedFiles", JsonArray(affectedFiles |> Array.map jstr) :> JsonNode ]
                :> JsonNode

            let testsNode =
                jobj [ "count", jint testCount; "tests", JsonArray(testNodes) :> JsonNode ] :> JsonNode

            let baseProps =
                [ "status", jstr "succeeded"
                  "target", targetNode
                  "kind", jstr kindResolved
                  "impact", impactNode
                  "tests", testsNode ]

            let optionalProps =
                (compileOrderNode |> Option.map (fun n -> [ "compileOrder", n ]) |> Option.defaultValue [])
                @ (apiSurfaceNode |> Option.map (fun n -> [ "apiSurface", n ]) |> Option.defaultValue [])
                @ (renamePreviewNode |> Option.map (fun n -> [ "renamePreview", n ]) |> Option.defaultValue [])

            let verifyProp = [ "verify", JsonArray(verify.ToArray() |> Array.map jstr) :> JsonNode ]

            return jobj (baseProps @ optionalProps @ verifyProp) :> JsonNode
        }

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

            let filesResult: Result<string * string list, JsonNode> =
                match pathArg with
                | Some path ->
                    match validateSourcePath "fcs_review_scan" None path with
                    | Some err -> Error err
                    | None -> Ok("file", [ normalizePath path ])
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

                                let included =
                                    filterProjectFiles workspaceRoot (defaultFilterOptions Review) (compileFiles fullProject doc)
                                    |> fun result ->
                                        result.Included
                                        |> List.map (fun f -> f.Path)
                                        |> List.filter File.Exists
                                        |> List.distinct
                                        |> List.sort

                                Ok("project", included)

            match filesResult with
            | Error err -> return err
            | Ok(mode, files) ->

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

            return
                jobj
                    [ "status", jstr "succeeded"
                      "mode", jstr mode
                      "scanned", JsonArray(scanned.ToArray() |> Array.map jstr) :> JsonNode
                      "candidates", JsonArray(pageNodes |> List.toArray) :> JsonNode
                      "counts", countsNode
                      "truncated", jbool (total > pageNodes.Length)
                      "parseErrors", JsonArray(parseErrors.ToArray()) :> JsonNode ]
                :> JsonNode
        }
