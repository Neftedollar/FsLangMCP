module FsLangMcp.Types

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

// ─── Tool error DU ─────────────────────────────────────────────────────────────

[<NoComparison>]
type ToolError =
    | InvalidArgs of string
    | NotReady of string
    | InfraFailure of exn
    | FcsAborted of string
    | FileNotFound of string

// ─── Shared arg types ──────────────────────────────────────────────────────────

type CompletionArgs =
    { /// Absolute path to an existing F# source file (.fs or .fsi).
      path: string
      /// 0-based line number (LSP convention).
      line: int
      /// 0-based column number (LSP convention).
      character: int
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option
      /// Single-character trigger that opened the completion list (e.g. "."). Omit for manual invocation.
      triggerCharacter: string option }

type PositionArgs =
    { /// Absolute path to an existing F# source file (.fs or .fsi).
      path: string
      /// 0-based line number (LSP convention).
      line: int
      /// 0-based column number (LSP convention).
      character: int
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option }

type ReferencesArgs =
    { /// Absolute path to an existing F# source file (.fs or .fsi).
      path: string
      /// 0-based line number (LSP convention).
      line: int
      /// 0-based column number (LSP convention).
      character: int
      /// When true, include the declaration site in the results. Default false.
      includeDeclaration: bool option
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option }

type WorkspaceSymbolArgs = { query: string }
type DiagnosticsArgs =
    { /// Single-file diagnostics. When set, fileGlob is ignored.
      path: string option
      /// Glob over file URIs (e.g. "src/Foo/*.fs"). When path is None, only files
      /// whose URI matches the glob are returned. Use "**" to span directories.
      fileGlob: string option
      /// Filter the diagnostic list inside each file payload by LSP severity:
      /// "error" → 1, "warning" → 2, "information" → 3, "hint" → 4.
      /// When None, all severities pass through.
      severity: string option }

type SetProjectArgs =
    { /// Path to the .fsproj, .sln, .slnx, or directory to load. Required.
      projectPath: string
      /// Override the workspace root; when omitted, inferred from projectPath.
      workspacePath: string option
      /// When true, restarts the FSAC process before loading. Default true.
      restartLsp: bool option }

type FSharpCompileArgs =
    { /// .fsproj to compile. Falls back to active set_project when omitted.
      projectPath: string option
      /// Override workspace root for project resolution; rarely needed.
      workspacePath: string option
      /// Timeout in milliseconds for ParseAndCheckProject. Default 60000 (60 s).
      timeoutMs: int option }

type ProjectHealthArgs =
    { /// .fsproj, .sln, .slnx, or directory to inspect. Falls back to active set_project when omitted.
      projectPath: string option
      /// Override workspace root for resolution; rarely needed.
      workspacePath: string option
      /// Reserved for future sub-report scoping; currently unused.
      scope: string option
      /// Controls compile validation: "Skip" (default) | "UseCached". "UseCached" reports the last FCS parse result without re-running.
      compileCheck: string option }

type FcsParseAndCheckArgs =
    { /// Absolute path to the F# source file to parse and typecheck.
      path: string
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option
      /// .fsproj for project context. Falls back to active set_project when omitted.
      projectPath: string option
      /// Raw FCS OtherOptions list; overrides projectPath-derived options when provided.
      projectOptions: string list option }

type FcsFileSymbolsArgs =
    { /// Absolute path to the F# source file to extract symbols from.
      path: string
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option
      /// .fsproj for project context. Falls back to active set_project when omitted.
      projectPath: string option
      /// Raw FCS OtherOptions list; overrides projectPath-derived options when provided.
      projectOptions: string list option
      /// When true, return all uses (locals, parameters, usages) in addition to definitions. Default false.
      includeAllUses: bool option
      /// Maximum symbols returned. Default 200.
      maxResults: int option }

type FcsProjectSymbolUsesArgs =
    { path: string
      text: string option
      projectPath: string option
      projectOptions: string list option
      symbolQuery: string
      exact: bool option
      /// Maximum uses returned per page. Default: 500.
      maxResults: int option
      /// Opaque cursor from a prior call's `nextCursor`. Omit for the first page.
      cursor: string option }

type FcsRecordFieldAuditArgs =
    { /// Record type name. Matched against FSharpEntity.DisplayName (e.g. "TraderRole")
      /// or FullName ending at a segment boundary (e.g. "LlmTrader.Domain.Ports.TraderRole").
      typeName: string
      /// Field name on the record, e.g. "Propose". Exact-match against FSharpField.Name.
      fieldName: string
      /// File context used to derive project options when projectPath is absent.
      path: string option
      text: string option
      projectPath: string option
      projectOptions: string list option
      /// Maximum sites returned per page. Default 200, hard ceiling 1000.
      maxResults: int option
      /// Opaque cursor from a prior call's `nextCursor`. Omit for the first page.
      cursor: string option }

type FcsFindMemberUsagesArgs =
    { /// Type that declares the member. Match against DisplayName (e.g. "Style")
      /// or FullName (e.g. "MyApp.Theme.Style").
      typeName: string
      /// Member name (DisplayName), e.g. "Foreground" or "GetForeground".
      memberName: string
      /// File path used to derive project context when projectPath is absent.
      path: string option
      text: string option
      projectPath: string option
      projectOptions: string list option
      /// When true, typeName / memberName must match exactly. Default: false (substring match).
      exact: bool option
      /// Maximum uses returned per page. Default: 500.
      maxResults: int option
      /// Opaque cursor from a prior call's `nextCursor`. Omit for the first page.
      cursor: string option }

type FcsFileOutlineArgs =
    { /// Absolute path to the F# source file to outline.
      path: string
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option
      /// .fsproj for project context. Falls back to active set_project when omitted.
      projectPath: string option
      /// Raw FCS OtherOptions list; overrides projectPath-derived options when provided.
      projectOptions: string list option
      /// When true, include private members. Default true.
      includePrivate: bool option
      /// When true, include local bindings and parameters (noisy). Default false.
      includeLocal: bool option
      /// When true (default), return module/type headers + per-kind member counts
      /// only (no per-member signatures) — safe on very large files that would
      /// otherwise overflow the MCP token ceiling. Set false for full signatures.
      summaryOnly: bool option
      /// Maximum symbols returned. Default 200.
      maxResults: int option }

type FcsFindSymbolArgs =
    { path: string
      text: string option
      projectPath: string option
      projectOptions: string list option
      symbolQuery: string
      exact: bool option
      /// Maximum symbol groups returned per page. Default: 500.
      maxResults: int option
      contextLines: int option
      includeDeclaration: bool option
      /// When true, include Info/Hint severity diagnostics in projectDiagnostics.
      /// Default false — reduces noise like FS3520 XML-comment chatter that
      /// drowns out actionable warnings. (#116)
      includeInfo: bool option
      /// Opaque cursor from a prior call's `nextCursor`. Omit for the first page.
      cursor: string option }

type FindArgs =
    { /// Symbol, type, or member name to find. The ONLY required argument.
      query: string
      /// Resolution mode: "auto" (default) | "symbol" | "members" | "field" | "definition" | "position".
      /// auto unions definitions + references + record-field sites + member-usage sites.
      kind: string option
      /// Sweep breadth: "auto" (default) | "file" | "project" | "workspace".
      /// auto/workspace sweep every member project of the active solution.
      scope: string option
      /// When true (default), match query exactly; false = case-insensitive substring.
      exact: bool option
      /// Restrict the member-usage union to this member name (used with kind=members).
      ``member``: string option
      /// Restrict the record-field union to this field name (used with kind=field).
      field: string option
      /// File context: derives project options and anchors kind=position / scope=file.
      path: string option
      /// 0-based line (LSP convention) for kind=position.
      line: int option
      /// Identifier near the position to resolve (kind=position); pairs with line.
      word: string option
      /// 0-based occurrence index of `word` on the line. Default -1 (first match).
      occurrence: int option
      /// 0-based column (LSP convention) for kind=position.
      character: int option
      /// Source-context lines emitted per site. Default 0 — compact, one line per
      /// site (lineText only, no before/after arrays). Pass > 0 for surrounding code.
      contextLines: int option
      /// Include declaration sites among results. Default true.
      includeDeclaration: bool option
      /// Include Info/Hint diagnostics in projectDiagnostics. Default false.
      includeInfo: bool option
      /// .fsproj / .sln / .slnx / directory to sweep. Falls back to active set_project.
      projectPath: string option
      /// Maximum sites returned per page. Default 40 (keeps the compact payload well
      /// under the MCP token ceiling on a hot symbol); cursor pages the rest.
      maxResults: int option
      /// Opaque cursor from a prior call's nextCursor. Omit for the first page.
      cursor: string option }

type FcsTestsForSymbolArgs =
    { /// Symbol, type, or member name whose covering tests to find. Required.
      symbolQuery: string
      /// When true (default), match symbolQuery exactly; false = case-insensitive substring.
      exact: bool option
      /// File context: derives the sweep target (nearest .fsproj) when projectPath is absent.
      path: string option
      /// Unsaved buffer content for `path`; when omitted, the file is read from disk.
      text: string option
      /// .fsproj / .sln / .slnx / directory to sweep. Falls back to active set_project.
      projectPath: string option
      /// Maximum test sites returned. Default 100.
      maxResults: int option }

type CheckArgs =
    { /// What to check: "auto" (default) | "file" | "project" | "workspace" | "snippet".
      /// auto picks snippet when `snippet` is set, file when `path` is set, else the
      /// active project (or whole solution when it spans >1 .fsproj).
      scope: string option
      /// Single F# file to check. Implies scope=file.
      path: string option
      /// Inline F# source to type-check against the project's references. Implies
      /// scope=snippet. (Consistent rename of fcs_validate_snippet's `content`.)
      snippet: string option
      /// scope=workspace fast-mode glob over file URIs (e.g. "src/Adapters/*.fs").
      fileGlob: string option
      /// Snippet parse mode: "fs" (default) | "fsi". Pick "fsi" for a signature sketch.
      mode: string option
      /// "trusted" (default) runs a FRESH in-process FCS check — the verdict is never
      /// a stale `{}` false-clean. "fast" reads the cheap cached FSAC snapshot instead.
      speed: string option
      /// Minimum severity surfaced in `diagnostics`: "error" (default) | "warning" |
      /// "information" | "hint" | "all". errorCount/warningCount are unaffected.
      severity: string option
      /// .fsproj / .sln / .slnx context. Falls back to active set_project.
      projectPath: string option
      /// Timeout in milliseconds for the project/workspace type-check. Default 60000.
      timeoutMs: int option }

type FcsSymbolAtWordArgs =
    { /// Absolute path to the F# source file containing the word.
      path: string
      /// 0-based line number where the word appears (LSP convention).
      line: int
      /// Identifier to locate on the line; when omitted, falls back to column-based scan.
      word: string option
      /// 0-based index among multiple occurrences of `word` on the line. Default -1 (first match).
      occurrence: int option
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option
      /// .fsproj for project context. Falls back to active set_project when omitted.
      projectPath: string option
      /// Raw FCS OtherOptions list; overrides projectPath-derived options when provided.
      projectOptions: string list option
      /// When true, include XML-doc text in the response. Default false.
      includeDocumentation: bool option }

type FcsProjectOutlineArgs =
    { projectPath: string option
      workspacePath: string option
      includePrivate: bool option
      includeTests: bool option
      includeGeneratedFiles: bool option
      /// Maximum number of files to include in one page. Default: 50.
      maxFiles: int option
      /// Maximum number of symbol entries per file. Default: 30.
      maxResultsPerFile: int option
      /// When true (default), return module/type headers + member counts only.
      /// Set to false to get full per-member signatures (legacy behaviour).
      summaryOnly: bool option
      /// Opaque pagination cursor returned by a prior call. Agents must not construct this.
      cursor: string option
      /// Regex applied to member names/signatures before truncation.
      filter: string option
      /// OR-joined substring list applied to member names before truncation.
      nameContains: string list option }

type FSharpProjectInspectArgs =
    { /// .fsproj to inspect. Falls back to active set_project when omitted.
      projectPath: string option
      /// Override workspace root for resolution; rarely needed.
      workspacePath: string option
      /// Reserved for future sub-report scoping; currently unused.
      scope: string option
      /// When true, include generated/build-artifact source files in the compile order. Default false.
      includeGeneratedFiles: bool option
      /// When true, include version, includeAssets, and privateAssets per package reference. Default true.
      includePackageDetails: bool option
      /// When true, include the raw FCS OtherOptions array in the response. Default false.
      includeResolvedOptions: bool option }

type FcsTypeAtPositionArgs =
    { path: string
      line: int
      character: int
      text: string option
      projectPath: string option
      projectOptions: string list option
      /// When true and the exact (line, character) misses, snap to the nearest
      /// symbol within ±2 lines / ±5 columns and return its type info plus the
      /// resolved coordinates. Default false (exact-position behavior).
      fuzzy: bool option }

type FcsReferencedSymbolsArgs =
    { /// Substring matched against DisplayName / FullName (case-insensitive).
      query: string
      /// .fsproj for type context. Falls back to active set_project.
      projectPath: string option
      /// When true, include `private` / `internal` symbols. Default false.
      includeNonPublic: bool option
      /// Maximum entries per page. Default 200, hard ceiling 1000.
      maxResults: int option
      /// Opaque cursor from a prior call's `nextCursor`. Omit for first page.
      cursor: string option }

type FcsPublicApiArgs =
    { /// .fsproj whose own public API surface to emit. Falls back to active set_project.
      projectPath: string option
      /// When true, include `internal` entities and members alongside public ones.
      /// `private` is never emitted. Default false (public-only surface).
      includeInternal: bool option
      /// Case-insensitive substring filter applied to each entity's FullName.
      /// Omit to emit the whole surface.
      namespaceFilter: string option
      /// Maximum entities per page. Default 100, hard ceiling 1000. Members are
      /// never split across pages — an entity carries its full member list.
      maxResults: int option
      /// Opaque cursor from a prior call's `nextCursor`. Omit for the first page.
      cursor: string option }

type FcsSuggestOpenArgs =
    { /// Bare unresolved symbol name from an FS0039-style diagnostic (e.g. "File", "List", "Encoding").
      symbolName: string
      /// .fsproj for project context. Falls back to active set_project when omitted.
      projectPath: string option
      /// File context used to derive project options when projectPath is absent.
      path: string option
      text: string option
      projectOptions: string list option
      /// When true (default), also search referenced assemblies (BCL + NuGet). Set false to search project-local only.
      includeReferences: bool option
      /// Maximum candidates per source. Default 20, hard ceiling 100.
      maxResults: int option }

type FcsExplainDiagnosticArgs =
    { /// Numeric error code WITHOUT the "FS" prefix (e.g. 39 for FS0039). Use this OR `code`.
      errorNumber: int option
      /// Full diagnostic code WITH the "FS" prefix (e.g. "FS0039"). Takes precedence over `errorNumber`.
      code: string option
      /// The raw FCS diagnostic message. Used to enrich repairHints — e.g. the undefined
      /// name is extracted from an FS0039 message to suggest `fcs_suggest_open`.
      message: string option
      /// File context: when neither `code` nor `errorNumber` is given, the diagnostic at
      /// (line, character) is auto-fetched by type-checking this file via FCS.
      path: string option
      /// 0-based line (LSP convention) for the position-based auto-fetch. Pairs with `path`.
      line: int option
      /// 0-based column (LSP convention) for the position-based auto-fetch. Pairs with `path`.
      character: int option
      /// Unsaved buffer content for the auto-fetch path; when omitted, the file is read from disk.
      text: string option
      /// .fsproj for project context on the auto-fetch path. Falls back to active set_project.
      projectPath: string option }

type FcsCheckCompileOrderArgs =
    { /// .fsproj / .sln / .slnx to scan. Falls back to the active set_project when omitted.
      projectPath: string option
      /// When set, only report compile-order problems for this unresolved name (the
      /// leftmost identifier in an FS0039 "X is not defined" error). Omit to check all.
      symbol: string option }

/// Arguments for fcs_create_file_plan (#66) — a read-only "where should this new .fs file
/// go, and how?" planner. PLANNING ONLY: it never creates files, writes source, or edits
/// the .fsproj. It loads the project's resolved <Compile> order, recommends an insertion
/// index, infers the namespace/module convention from neighbouring files, and spells out
/// the exact <Compile Include=...> edit. Pairs with fcs_check_compile_order (run AFTER).
type FcsCreateFilePlanArgs =
    { /// Proposed new file name (e.g. "Validation.fs"). The leaf name drives matching and the
      /// suggested module name; a directory prefix, if any, is preserved verbatim in fsprojOp.
      fileName: string
      /// Existing sibling the new file should compile AFTER (by name or path). When matched in
      /// the compile order, the recommended index sits right after it. Omit to let the
      /// namespace heuristic (or end-of-project insertion) decide.
      afterFile: string option
      /// Intended namespace or module for the new file. Compared against the neighbour
      /// convention to seed the namespace-grouping heuristic; advisory, never enforced.
      namespaceOrModule: string option
      /// .fsproj / .sln / .slnx to plan against. Falls back to the active set_project.
      projectPath: string option }

/// Arguments for fcs_refactor_impact — a read-only "what will this change affect, and
/// what should I verify?" planning preview. Orchestrates the existing find sweep,
/// tests-for-symbol, compile-order, public-api, and (optionally) rename-preview backends
/// into one blast-radius + verification-checklist synthesis. Writes nothing.
type FcsRefactorImpactArgs =
    { /// The symbol about to change (by name). Use this OR a position (path + line + character).
      symbol: string option
      /// File context for a position-anchored target (e.g. the symbol under the cursor for a
      /// rename). Pairs with line/character; the symbol name is resolved at that position.
      path: string option
      /// 0-based line (LSP convention) of the position-anchored target. Pairs with path.
      line: int option
      /// 0-based column (LSP convention) of the position-anchored target. Pairs with path.
      character: int option
      /// New identifier, when a rename is contemplated. Enables the best-effort rename-preview
      /// edit set (requires a position) and selects kind=rename under kind="auto".
      newName: string option
      /// Intended change: "rename" | "signature" | "move" | "delete" | "auto" (default).
      /// auto infers from inputs (newName ⇒ rename, else a generic blast-radius sweep).
      kind: string option
      /// .fsproj / .sln / .slnx to sweep. Falls back to the active set_project.
      projectPath: string option }

type FcsNugetTypesArgs =
    { /// Package id (matched against referenced assembly SimpleName, case-insensitive).
      /// Example: "Spectre.Console", "Newtonsoft.Json", "System.Text.Json".
      packageId: string
      projectPath: string option
      /// When true, include `private` / `internal` types. Default false.
      includeNonPublic: bool option
      /// Maximum entries per page. Default 500, hard ceiling 2000.
      maxResults: int option
      cursor: string option }

type FcsNugetMembersArgs =
    { /// Package id matched against assembly SimpleName (case-insensitive, exact match).
      /// Same matching logic as fcs_nuget_types — "System.Text.Json" resolves only to that assembly.
      packageId: string
      /// Type to look up within the matched assembly. Matched case-insensitively against
      /// DisplayName and FullName. Example: "String", "FSharpList", "JsonSerializer".
      typeName: string
      projectPath: string option
      /// When true, include private/internal members. Default false.
      includeNonPublic: bool option
      /// Maximum entries per page. Default 500, hard ceiling 2000.
      maxResults: int option
      cursor: string option }

type FcsValidateSnippetArgs =
    { /// F# source text to validate against the project's references.
      content: string
      /// "fs" (default) or "fsi". Affects how FCS parses the snippet — pick "fsi"
      /// when validating a signature-file sketch.
      mode: string option
      /// .fsproj to use as the type-context. Falls back to the active set_project
      /// when absent. Required to resolve types declared in the project itself.
      projectPath: string option }

type FcsSignatureHelpArgs =
    { /// Absolute path to the F# source file containing the call site.
      path: string
      /// 0-based line number of the call site (LSP convention).
      line: int
      /// 0-based column number inside or just after the opening parenthesis (LSP convention).
      character: int
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option
      /// .fsproj for project context. Falls back to active set_project when omitted.
      projectPath: string option
      /// Raw FCS OtherOptions list; overrides projectPath-derived options when provided.
      projectOptions: string list option }

type FormattingArgs = { path: string; text: string option }

type CodeActionArgs =
    { /// Absolute path to the F# source file at which to request code actions.
      path: string
      /// 0-based line number of the target position (LSP convention).
      line: int
      /// 0-based column number of the target position (LSP convention).
      character: int
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option }

type DiagnosticFixesArgs =
    { /// Absolute path to an existing F# source file (.fs or .fsi).
      path: string
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option
      /// 0-based line (LSP convention). With character, narrows to diagnostics
      /// covering that exact position; with line alone, to diagnostics on that line.
      /// Omit both to report every diagnostic in the file.
      line: int option
      /// 0-based column (LSP convention). Pairs with line to pin one position.
      character: int option }

type RenameArgs =
    { /// Absolute path to the F# source file containing the symbol to rename.
      path: string
      /// 0-based line number of the symbol (LSP convention).
      line: int
      /// 0-based column number of the symbol (LSP convention).
      character: int
      /// New identifier to assign across the workspace. Required.
      newName: string
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option }

type RenamePreviewArgs =
    { /// Absolute path to the F# source file containing the symbol to preview a rename for.
      path: string
      /// 0-based line number of the symbol (LSP convention).
      line: int
      /// 0-based column number of the symbol (LSP convention).
      character: int
      /// New identifier the preview pretends to assign across the workspace. Required.
      newName: string
      /// Unsaved buffer content; when omitted, file is read from disk.
      text: string option }

[<CLIMutable>]
type FcsGetProjectOptionsArgs = { projectPath: string option }

type FcsCheckerConfig =
    { KeepAssemblyContents: bool
      KeepAllBackgroundResolutions: bool
      KeepAllBackgroundSymbolUses: bool
      ProjectCacheSize: int }

type FcsMakeInternalVisibleArgs =
    { /// File containing the declaration. Must exist on disk; pass `text` for
      /// unsaved buffers.
      path: string
      /// 0-based line (LSP convention).
      line: int
      /// 0-based column.
      character: int
      text: string option
      projectPath: string option }

[<CLIMutable>]
type FslangmcpVersionArgs =
    { /// Reserved for forward compat — currently no fields are read.
      _placeholder: bool option }

type RuntimeStatusArgs =
    { /// When true, include FCS checker configuration flags and project-results cache size. Default true.
      includeFcsCacheStats: bool option
      /// When true, include the count of loaded assemblies in the process. Default true.
      includeAssemblyCounts: bool option
      /// When true, include the FSAC child-process working set stats. Default true.
      includeChildProcesses: bool option
      /// When true, include the MCP server and FSAC process IDs. Default true.
      includeProcessIds: bool option }

// ─── CLI parse result ──────────────────────────────────────────────────────────

[<Struct>]
type internal CliParseResult =
    | Start
    | BootstrapTools
    | ShowHelp of string
    | Fail of string

// ─── Helper utility functions ──────────────────────────────────────────────────

let jobj (props: (string * JsonNode) list) =
    let result = JsonObject()

    for (key, value) in props do
        result[key] <- value

    result

let jstr (value: string) : JsonNode = JsonValue.Create(value)
let jint (value: int) : JsonNode = JsonValue.Create(value)
let jint64 (value: int64) : JsonNode = JsonValue.Create(value) :> JsonNode
let jbool (value: bool) : JsonNode = JsonValue.Create(value)

let toFileUri (path: string) =
    let fullPath = Path.GetFullPath(path)
    Uri(fullPath).AbsoluteUri

// ─── Argument validation helpers ──────────────────────────────────────────────

module ArgsValidation =
    /// Returns the trimmed value if non-blank, otherwise a JSON <c>invalid_args</c>
    /// envelope matching the existing wire shape
    /// <c>{ status: "invalid_args"; message: "..." }</c>.
    /// Use at the top of MCP handlers to standardise required-string validation.
    let requireNonBlank (fieldName: string) (value: string) : Result<string, JsonNode> =
        let trimmed = if isNull value then "" else value.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            let payload =
                jobj
                    [ "status", jstr "invalid_args"
                      "message", jstr $"{fieldName} must be non-empty" ]
                :> JsonNode

            Error payload
        else
            Ok trimmed

let normalizePath (path: string) = Path.GetFullPath(path)

// ─── Reference-resolution probe ────────────────────────────────────────────────

/// Deterministic "is this project restored/built?" probe over an FCS OtherOptions
/// list. Counts how many `-r:`/`--reference:` target assemblies actually exist on
/// disk. An unrestored/unbuilt project evaluates its .fsproj (so OtherOptions are
/// populated) but its external reference assemblies (FSharp.Core.dll, BCL refs,
/// NuGet packages) are absent — yielding total > 0 with existing far below total.
/// Shared by check (FcsBridge) and project_health/set_project readiness so both can
/// tell "not restored" apart from "no symbols".
module ReferenceResolution =

    /// Returns (existing, total): how many `-r:`/`--reference:` targets resolve on
    /// disk, out of how many were requested.
    let probe (otherOptions: string seq) : int * int =
        let refTargets =
            otherOptions
            |> Seq.choose (fun opt ->
                if isNull opt then None
                elif opt.StartsWith("-r:", StringComparison.Ordinal) then Some(opt.Substring(3))
                elif opt.StartsWith("--reference:", StringComparison.Ordinal) then Some(opt.Substring(12))
                else None)
            |> Seq.toArray

        let existing = refTargets |> Array.filter File.Exists |> Array.length
        existing, refTargets.Length

    /// Fraction of references resolved on disk. Returns 1.0 when total = 0 (nothing
    /// to resolve) so callers can gate the "unrestored" verdict on `total > 0`.
    let fraction (existing: int) (total: int) : float =
        if total <= 0 then 1.0 else float existing / float total

    /// True when the project looks effectively unrestored/unbuilt: it declares
    /// external references but fewer than 20% of them exist on disk.
    let looksUnrestored (existing: int) (total: int) : bool =
        total > 0 && fraction existing total < 0.2

/// Resolved project-options summary shared by ProbeProjectOptions (FcsBridge) and
/// createReport (ProjectHealth). Carries the reference-resolution counts so readiness
/// reporting can surface "unrestored" instead of a bare "available".
type ProjectOptionsInfo =
    { Source: string
      ReferencesExisting: int
      ReferencesTotal: int }
