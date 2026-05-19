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
    { path: string
      line: int
      character: int
      text: string option
      triggerCharacter: string option }

type PositionArgs =
    { path: string
      line: int
      character: int
      text: string option }

type ReferencesArgs =
    { path: string
      line: int
      character: int
      includeDeclaration: bool option
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
    { projectPath: string
      workspacePath: string option
      restartLsp: bool option }

type FSharpCompileArgs =
    { projectPath: string option
      workspacePath: string option
      timeoutMs: int option }

type ProjectHealthArgs =
    { projectPath: string option
      workspacePath: string option
      scope: string option
      compileCheck: string option }

type FcsParseAndCheckArgs =
    { path: string
      text: string option
      projectPath: string option
      projectOptions: string list option }

type FcsFileSymbolsArgs =
    { path: string
      text: string option
      projectPath: string option
      projectOptions: string list option
      includeAllUses: bool option
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
    { path: string
      text: string option
      projectPath: string option
      projectOptions: string list option
      includePrivate: bool option
      includeLocal: bool option
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
    { path: string
      line: int
      word: string option
      occurrence: int option
      text: string option
      projectPath: string option
      projectOptions: string list option
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
    { projectPath: string option
      workspacePath: string option
      scope: string option
      includeGeneratedFiles: bool option
      includePackageDetails: bool option
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
    { path: string
      line: int
      character: int
      text: string option
      projectPath: string option
      projectOptions: string list option }

type FormattingArgs = { path: string; text: string option }

type CodeActionArgs =
    { path: string
      line: int
      character: int
      text: string option }

type RenameArgs =
    { path: string
      line: int
      character: int
      newName: string
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
    { includeFcsCacheStats: bool option
      includeAssemblyCounts: bool option
      includeChildProcesses: bool option
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

let normalizePath (path: string) = Path.GetFullPath(path)
