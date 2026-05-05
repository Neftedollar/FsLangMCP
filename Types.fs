module FsLangMcp.Types

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

// ─── Tool error DU ─────────────────────────────────────────────────────────────

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
type DiagnosticsArgs = { path: string option }

type SetProjectArgs =
    { projectPath: string
      workspacePath: string option
      restartLsp: bool option }

type FSharpCompileArgs =
    { projectPath: string
      workspacePath: string option
      timeoutMs: int option }

type ProjectHealthArgs =
    { projectPath: string
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
      maxResults: int option }

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
      maxResults: int option
      contextLines: int option
      includeDeclaration: bool option }

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
    { projectPath: string
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
    { projectPath: string
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
      projectOptions: string list option }

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
type FcsGetProjectOptionsArgs = { projectPath: string }

type FcsCheckerConfig =
    { KeepAssemblyContents: bool
      KeepAllBackgroundResolutions: bool
      KeepAllBackgroundSymbolUses: bool
      ProjectCacheSize: int }

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
