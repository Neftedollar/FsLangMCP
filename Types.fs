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
