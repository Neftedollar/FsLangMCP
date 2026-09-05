module FsLangMcp.Types

open System
open System.IO
open System.Text.Encodings.Web
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
      /// auto/workspace sweep every member project of the active solution; the response
      /// echoes this value verbatim (see resolution.scopeResolved / projectsSwept for what
      /// actually happened, and the top-level scopeNote for how many projects were swept
      /// and how to widen/narrow).
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
      /// Source-context lines requested per site. Default 0 — compact, one bounded
      /// lineText snippet only. Positive values emit before/after, capped at 8 lines
      /// per side; every returned source line is capped at 512 UTF-16 code units and
      /// carries source-offset/truncation metadata.
      contextLines: int option
      /// Include declaration sites among results. Default true.
      includeDeclaration: bool option
      /// Include Info/Hint diagnostics in projectDiagnostics. Default false.
      includeInfo: bool option
      /// Include the per-project sweep breakdown (`perProject`). Default true. Even when
      /// true, projects with zero matches and no error are omitted to cut token noise;
      /// pass false to drop the array entirely on repeated sweeps.
      includePerProject: bool option
      /// Add `siteType` — the field's type as the CURRENT typecheck resolves it at that
      /// site — to every record-field site row, so a field-type change can be planned
      /// without opening each file. Opt-in (per-site type resolution costs FCS work) and
      /// scoped to field sites, i.e. kind='field' or kind='auto'. Never speculates about
      /// the type AFTER an edit: make the edits, then run `check`. Default false. (#207)
      includeSiteTypes: bool option
      /// .fsproj / .sln / .slnx / directory to sweep. Falls back to active set_project.
      projectPath: string option
      /// Maximum sites considered per page. Default 80; valid range 1..1000. The final
      /// production-serialized response has a 60,000 UTF-16-code-unit hard ceiling, so
      /// fewer sites may be delivered; nextCursor advances by delivered sites only.
      maxResults: int option
      /// Overall wall-clock budget in ms for the whole multi-project sweep. Default
      /// 120000 (120 s). Each project's FCS type-check is cancelled at the remaining
      /// budget so a huge/cold solution surfaces a partial result instead of hanging.
      /// Must be non-negative; 0 requests an immediate, typed timeout result.
      timeoutMs: int option
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
      /// Maximum test sites returned per page. Default 100.
      maxResults: int option
      /// Overall wall-clock budget in ms for the whole multi-project test sweep.
      /// Default 120000 (120 s). Must be non-negative; 0 requests an immediate,
      /// typed timeout result.
      timeoutMs: int option
      /// Opaque cursor from a prior call's nextCursor. Omit for the first page.
      cursor: string option }

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
      /// Compile-order placement for scope=snippet: "start" checks before every
      /// project source file; "end" (default) checks after all project source files.
      /// The effective normalized placement is echoed as snippetPosition.
      snippetPosition: string option
      /// scope=workspace fast-mode glob over workspace-relative evaluated source paths
      /// (e.g. "src/Adapters/*.fs"); absolute paths and file-URI globs are also accepted.
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
      nameContains: string list option
      /// End-to-end timeout in milliseconds, including FCS queue admission and project
      /// evaluation. Must be non-negative. Default: 60000.
      timeoutMs: int option }

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

/// Arguments for fcs_signature_status — a read-only ".fsi drift" preview. Type-checks an
/// implementation .fs WITHOUT its sibling signature file, enumerates the public surface,
/// and diffs it against the paired .fsi (if present) to surface members hidden from the
/// signature (missingFromSig) and stale .fsi entries with no impl match (staleInSig).
type FcsSignatureStatusArgs =
    { /// The implementation .fs file whose public surface to inspect (NOT the .fsi).
      path: string
      /// .fsproj for project context. Falls back to active set_project; else the nearest .fsproj.
      projectPath: string option
      /// Unsaved buffer content for the .fs; when omitted the file is read from disk.
      text: string option }

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

/// Arguments for fcs_analyzer_setup_preview (#75) — a read-only "what do I need to add to
/// turn on F# analyzers?" planner. Reads the target .fsproj + the nearest
/// Directory.Build.props/.targets + dotnet-tools.json (textual, no FCS), diffs the present
/// wiring against the required set (analyzer package refs + GeneratePathProperty,
/// FSharp.Analyzers.Build, the FSharpAnalyzersOtherFlags property, and a local
/// fsharp-analyzers tool manifest), and emits the exact XML/JSON snippets to add. Applies
/// nothing. Pairs with fcs_analyzer_diagnostics (run AFTER applying the changes).
type FcsAnalyzerSetupPreviewArgs =
    { /// .fsproj / .sln / .slnx / directory to plan analyzer setup for. Falls back to the
      /// active set_project when omitted.
      projectPath: string option
      /// Analyzer NuGet packages to wire up. Defaults to
      /// ["G-Research.FSharp.Analyzers"; "Ionide.Analyzers"] when omitted or empty.
      analyzerPackages: string list option }

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

/// Arguments for fcs_review_scan — a read-only, AST-based review inventory. Surfaces
/// structurally interesting spots (review CANDIDATES, not bugs) for an agent/human to
/// eyeball during F# review. Parse-only; writes nothing.
type FcsReviewScanArgs =
    { /// Single F# source file to scan. Use this OR projectPath; when both are set, path wins.
      path: string option
      /// .fsproj whose compiled files to scan. Falls back to the active set_project when
      /// both path and projectPath are omitted.
      projectPath: string option
      /// Restrict to these categories (e.g. ["try_with"; "mutable_binding"]). Omit for all.
      /// Known: match_wildcard, try_with, raise_or_failwith, mutable_binding, blocking_call,
      /// cast_or_box, reflection, large_function.
      categories: string list option
      /// Maximum candidates returned. Default 200, hard ceiling 1000. counts.byCategory
      /// still reports true totals across every scanned file even when the list is capped.
      maxResults: int option }

/// Arguments for fcs_dead_code — a conservative dead-code candidate pass. Sweeps the
/// project (ParseAndCheckProject → GetAllUsesOfAllSymbols) and reports module/type-level
/// value &amp; function bindings whose ONLY recorded use is their own definition. These are
/// candidates, NOT deletions: the tool writes nothing and always emits caveats.
type FcsDeadCodeArgs =
    { /// .fsproj / .sln / .slnx to sweep. Falls back to the active set_project when omitted.
      projectPath: string option
      /// Include public symbols too. Default false — public API is presumed reachable by
      /// external callers, so flagging it would over-report. Set true for a leaf executable
      /// or a closed codebase with no external consumers.
      includePublic: bool option
      /// Max candidates returned in one page. Default 100, hard ceiling 500. The full count
      /// is reported in candidateCount; the `truncated` flag marks when the page was capped.
      maxResults: int option }

/// Arguments for fcs_analyzer_diagnostics (#72) — reports F# *analyzer* diagnostics
/// (distinct from compiler diagnostics) for a project, grouped for agents. Detects the
/// analyzer configuration the same way project_health does, then runs the fsharp-analyzers
/// CLI when one is available and parses its SARIF; otherwise it reports what is configured.
/// Read-only; writes nothing. Pairs with project_health (which reports analyzer SETUP).
type FcsAnalyzerDiagnosticsArgs =
    { /// .fsproj / .sln / .slnx to inspect. Falls back to the active set_project when omitted.
      projectPath: string option
      /// Filter diagnostics by normalized severity: "error" | "warning" | "info" | "hint".
      /// When omitted, every severity passes through.
      severity: string option
      /// Maximum diagnostics returned in one page. Default 200, hard ceiling 1000.
      /// counts.byAnalyzer / counts.bySeverity still report true totals across the full
      /// (severity-filtered) set even when the returned list is capped.
      maxResults: int option }

type FcsNugetTypesArgs =
    { /// NuGet package id OR the assembly SimpleName it ships (case-insensitive, exact —
      /// never a prefix). The two differ for many packages, and either is accepted:
      /// "Microsoft.Orleans.Core.Abstractions" and "Orleans.Core.Abstractions" both resolve.
      /// Example: "Spectre.Console", "Newtonsoft.Json", "System.Text.Json".
      packageId: string
      projectPath: string option
      /// When true, include `private` / `internal` types. Default false.
      includeNonPublic: bool option
      /// Maximum entries per page. Default 500, hard ceiling 2000.
      maxResults: int option
      cursor: string option }

type FcsNugetMembersArgs =
    { /// NuGet package id OR the assembly SimpleName it ships (case-insensitive, exact match).
      /// Same matching logic as fcs_nuget_types — "System.Text.Json" resolves only to that
      /// assembly, never to "System.Text.Json.Nodes" or any other prefix relative.
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
type internal CliStartOptions =
    { ProjectPath: string option }

[<Struct>]
type internal CliParseResult =
    | Start of options: CliStartOptions
    | BootstrapTools
    | ShowHelp of message: string
    | ShowVersion
    | Fail of error: string

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

// ─── Shared MCP response rendering (#206) ───────────────────────────────────────
//
// Defined HERE, not in `Tools.fs` (which owns the actual response transport) and not
// in `FcsBridge.fs` (which needs it for response-size budget checks), because
// `Types.fs` compiles before both in `FsLangMcp.fsproj`, and both already
// `open FsLangMcp.Types` — one definition, nothing to duplicate or drift. `Tools.fs`'s
// `renderToken` (every MCP tool response goes out through it) and `FcsBridge.fs`'s
// `find` / `fcs_public_api` / `fcs_file_outline` response-size budgets MUST measure against the
// exact same options, or a budget that believes it fits can still ship over the real
// MCP token ceiling (#206 review round 1, Imp-1: a prior version had FcsBridge measure
// with its own duplicate options, which round-1 fixed by duplicating `Tools.renderOpts`
// field-for-field — correct but a drift risk the review round-2 adjudication called
// out as unnecessary, since this shared location was reachable the whole time).

/// Serialization options every MCP response actually ships with: `WriteIndented` for
/// readability, relaxed JSON escaping. The single source of truth for "what the wire
/// format looks like" — `Tools.renderToken` and any response-size budget check must
/// both use this, never a private copy.
let mcpRenderOptions =
    JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true)

/// Serialized length of `node` in the SAME shape an MCP response actually ships in
/// (`mcpRenderOptions`), not `JsonNode.ToJsonString()`'s compact default — the two
/// differ by ~1.46-1.48x (#206 review round 1, Imp-1).
let renderedLength (node: JsonNode) : int =
    JsonSerializer.Serialize(node, mcpRenderOptions).Length

/// Shared policy for `find` source snippets and its final, fully assembled response.
/// The size unit is deliberately `System.String.Length`: UTF-16 code units in the
/// exact indented JSON produced by `mcpRenderOptions` and shipped by `Tools.renderToken`.
/// Keeping the planner here lets production shaping and focused tests call the same
/// serializer without introducing a second set of JSON options.
module internal FindResponseBudget =
    [<Literal>]
    let MaxSerializedChars = 60_000

    [<Literal>]
    let MaxSnippetChars = 512

    [<Literal>]
    let MaxContextLines = 8

    [<Literal>]
    let SizeUnit = "UTF-16 code units in production JSON"

    /// Preserve the total row count while materializing only the bounded prefix that
    /// can participate in response planning. Keep the cap ahead of `map`: individual
    /// rows may contain large caller/project-controlled strings, so mapping an
    /// unbounded tail and truncating afterwards defeats the response budget's memory
    /// and latency bound even though those rows can never be returned.
    let internal materializeCappedPrefix maxCount mapRow (rows: 'T array) =
        if maxCount < 0 then
            invalidArg (nameof maxCount) "prefix cap must be non-negative"

        rows.Length, rows |> Array.truncate maxCount |> Array.map mapRow

    type BoundedSnippet =
        { Text: string
          SourceStartColumn: int
          SourceEndColumn: int
          SourceLength: int
          Truncated: bool }

    [<RequireQualifiedAccess>]
    type FitPlan =
        | Fits of deliveredSites: int * deliveredDiagnostics: int * deliveredPerProject: int
        | FirstSiteOverflow
        | FixedMetadataOverflow

    /// Apply the hard production ceiling to one fully formed find result, including
    /// validation, position-resolution, project-discovery, deadline, and planned
    /// success envelopes. The replacement deliberately contains no caller-controlled
    /// data and is returned directly (never recursively guarded), so an oversized
    /// error cannot produce another oversized error or a non-advancing cursor loop.
    /// Keep this function reusable by both Find and any outer FindWithinDeadline path.
    let guardFinalResponse (response: JsonNode) : JsonNode =
        if renderedLength response <= MaxSerializedChars then
            response
        else
            let recovery =
                jobj
                    [ "action", jstr "restart_with_narrower_find_request"
                      "instruction",
                      jstr
                          "Restart find without a cursor, using shorter string/path arguments and narrower response-shaping options. Do not reuse a prior cursor after changing the query shape."
                      "recommendedContextLines", jint 0
                      "recommendedMaxResults", jint 1
                      "recommendedIncludeInfo", jbool false
                      "recommendedIncludePerProject", jbool false
                      "reuseOriginalCursor", jbool false ]
                :> JsonNode

            jobj
                [ "status", jstr "aborted"
                  "outcome", jstr "indeterminate"
                  "deliveryStatus", jstr "blocked"
                  "errorCode", jstr "find_response_exceeds_budget"
                  "message",
                  jstr
                      "The complete find result exceeded the hard production-serialized response ceiling. Caller-controlled details were omitted; retry with narrower inputs."
                  "retryable", jbool true
                  "responseTruncatedByBudget", jbool true
                  "responseBudgetChars", jint MaxSerializedChars
                  "responseSizeUnit", jstr SizeUnit
                  "cursorAdvancedBy", jint 0
                  "nextCursor", null
                  "recovery", recovery ]
            :> JsonNode

    let private clampOffset length value =
        max 0 (min length value)

    /// Return one contiguous source slice centred on the semantic match whenever the
    /// match itself fits. Offsets and lengths are UTF-16 columns, matching FCS/LSP and
    /// `System.String`; slice boundaries are moved inward rather than splitting a
    /// surrogate pair.
    let boundedSnippet (focusStart: int) (focusEnd: int) (source: string) : BoundedSnippet =
        let source = if isNull source then "" else source
        let sourceLength = source.Length

        if sourceLength <= MaxSnippetChars then
            { Text = source
              SourceStartColumn = 0
              SourceEndColumn = sourceLength
              SourceLength = sourceLength
              Truncated = false }
        else
            let boundedFocusStart = clampOffset sourceLength focusStart
            let boundedFocusEnd = max boundedFocusStart (clampOffset sourceLength focusEnd)
            let focusLength = boundedFocusEnd - boundedFocusStart

            let desiredStart =
                if focusLength >= MaxSnippetChars then
                    boundedFocusStart + focusLength / 2 - MaxSnippetChars / 2
                else
                    boundedFocusStart - (MaxSnippetChars - focusLength) / 2

            let mutable sourceStart = clampOffset (sourceLength - MaxSnippetChars) desiredStart
            let mutable sourceEnd = min sourceLength (sourceStart + MaxSnippetChars)

            // JSON can encode isolated surrogates, but returning one would make the visible
            // snippet disagree with its source offsets after consumer decoding. Keep every
            // returned slice valid Unicode while retaining UTF-16 coordinate semantics.
            if
                sourceStart > 0
                && sourceStart < sourceLength
                && Char.IsLowSurrogate(source[sourceStart])
                && Char.IsHighSurrogate(source[sourceStart - 1])
            then
                sourceStart <- sourceStart + 1

            if
                sourceEnd > sourceStart
                && sourceEnd < sourceLength
                && Char.IsHighSurrogate(source[sourceEnd - 1])
                && Char.IsLowSurrogate(source[sourceEnd])
            then
                sourceEnd <- sourceEnd - 1

            { Text = source.Substring(sourceStart, sourceEnd - sourceStart)
              SourceStartColumn = sourceStart
              SourceEndColumn = sourceEnd
              SourceLength = sourceLength
              Truncated = true }

    /// Plan the largest in-order site prefix whose COMPLETE response fits. Diagnostics
    /// and per-project detail remain intact unless even the first site (or a zero-site
    /// response) cannot fit; only then are those secondary arrays reduced, with the
    /// delivered counts returned for explicit truncation metadata. The callback must
    /// build the exact production response for the requested prefix counts.
    let planResponse
        (budget: int)
        (siteCount: int)
        (diagnosticCount: int)
        (perProjectCount: int)
        (buildResponse: int -> int -> int -> JsonNode)
        : FitPlan =
        if budget <= 0 then
            invalidArg (nameof budget) "response budget must be positive"

        if siteCount < 0 || diagnosticCount < 0 || perProjectCount < 0 then
            invalidArg "counts" "response section counts must be non-negative"

        let fits sites diagnostics projects =
            renderedLength (buildResponse sites diagnostics projects) <= budget

        // Response size is monotone within a section prefix once the already-tested full
        // response is known not to fit: each smaller candidate carries the same truncation
        // metadata and differs only by an in-order array prefix. Probe that exact response
        // logarithmically rather than serializing it once per removed row.
        let largestFittingBelow upperExclusive probe =
            let mutable low = 0
            let mutable high = upperExclusive - 1
            let mutable accepted = None

            while low <= high do
                let middle = low + (high - low) / 2

                if probe middle then
                    accepted <- Some middle
                    low <- middle + 1
                else
                    high <- middle - 1

            accepted

        let minimumSites = if siteCount = 0 then 0 else 1
        let mutable diagnostics = diagnosticCount
        let mutable projects = perProjectCount
        let mutable minimumFits = fits minimumSites diagnostics projects

        if not minimumFits && diagnosticCount > 0 then
            // Preserve the established priority: retain every per-project row if ANY
            // diagnostics prefix can coexist with the minimum site prefix.
            match
                largestFittingBelow diagnosticCount (fun candidateDiagnostics ->
                    fits minimumSites candidateDiagnostics projects)
            with
            | Some candidateDiagnostics ->
                diagnostics <- candidateDiagnostics
                minimumFits <- true
            | None -> diagnostics <- 0

        if not minimumFits && perProjectCount > 0 then
            // No diagnostics prefix fits with full per-project detail. Match the old loop by
            // dropping diagnostics completely, then retaining the largest project prefix.
            match
                largestFittingBelow perProjectCount (fun candidateProjects ->
                    fits minimumSites diagnostics candidateProjects)
            with
            | Some candidateProjects ->
                projects <- candidateProjects
                minimumFits <- true
            | None -> projects <- 0

        if not minimumFits then
            if siteCount > 0 && fits 0 diagnostics projects then
                FitPlan.FirstSiteOverflow
            else
                FitPlan.FixedMetadataOverflow
        elif siteCount = 0 then
            FitPlan.Fits(0, diagnostics, projects)
        elif fits siteCount diagnostics projects then
            FitPlan.Fits(siteCount, diagnostics, projects)
        else
            // The full page did not fit, so every candidate considered here retains a
            // continuation cursor; response size is monotone as the site prefix grows.
            let mutable low = 1
            let mutable high = siteCount - 1
            let mutable accepted = 1

            while low <= high do
                let middle = low + (high - low) / 2

                if fits middle diagnostics projects then
                    accepted <- middle
                    low <- middle + 1
                else
                    high <- middle - 1

            FitPlan.Fits(accepted, diagnostics, projects)

/// True as soon as the cumulative rendered length of `nodes` would exceed `budget`.
/// Stops calling `renderedLength` (a real `JsonSerializer.Serialize` call) on further
/// nodes the moment the answer is known, rather than summing every node regardless of
/// how early the budget was already crossed (#206 review round 2, N6).
let isOverRenderedBudget (budget: int) (nodes: JsonNode seq) : bool =
    let mutable total = 0
    let mutable over = false
    let enumerator = nodes.GetEnumerator()

    while not over && enumerator.MoveNext() do
        total <- total + renderedLength enumerator.Current
        over <- total > budget

    over

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
