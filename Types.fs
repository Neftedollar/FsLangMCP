module FsLangMcp.Types

open System
open System.IO
open Newtonsoft.Json.Linq

// ─── Tool error DU ─────────────────────────────────────────────────────────────

type ToolError =
    | InvalidArgs  of string
    | NotReady     of string
    | InfraFailure of exn
    | FcsAborted   of string
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

// ─── New arg types ─────────────────────────────────────────────────────────────

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

type FormattingArgs =
    { path: string
      text: string option }

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

// ─── CLI parse result ──────────────────────────────────────────────────────────

type internal CliParseResult =
    | Start
    | BootstrapTools
    | ShowHelp of string
    | Fail of string

// ─── Helper utility functions ──────────────────────────────────────────────────

let jobj (props: (string * JToken) list) =
    let result = JObject()
    for (key, value) in props do
        result[key] <- value
    result

let jstr (value: string) = JValue(value) :> JToken
let jint (value: int) = JValue(value) :> JToken
let jbool (value: bool) = JValue(value) :> JToken

let toFileUri (path: string) =
    let fullPath = Path.GetFullPath(path)
    Uri(fullPath).AbsoluteUri

let normalizePath (path: string) = Path.GetFullPath(path)
