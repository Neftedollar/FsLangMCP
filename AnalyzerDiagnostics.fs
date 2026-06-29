/// fcs_analyzer_diagnostics (#72) — report F# *analyzer* diagnostics (distinct from
/// compiler diagnostics) for a project, grouped for agents.
///
/// HONEST CONTRACT. F# analyzers are NOT run by FCS; they run via the `fsharp-analyzers`
/// CLI / FSharp.Analyzers.SDK. This module therefore splits into two parts:
///   1. A pure SARIF parser + grouping/filtering/counts builder — fully exercised by tests
///      against fixture SARIF, so the agent-facing shape is trustworthy.
///   2. A best-effort live runner (detectRunner / runAnalyzers) that invokes the CLI when
///      it is installed. This path is ENVIRONMENT-GATED and NOT exercised in CI (the CLI is
///      absent in the test image). When no runner is available the tool does NOT fake
///      diagnostics — it reports the configured analyzer packages truthfully plus a note.
module FsLangMcp.AnalyzerDiagnostics

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open FsLangMcp.Types

// ─── Normalized diagnostic ────────────────────────────────────────────────────────────

/// One analyzer diagnostic normalized from a SARIF result.
type AnalyzerDiagnostic =
    { Analyzer: string
      Code: string
      Severity: string
      Message: string
      File: string option
      StartLine: int option
      StartColumn: int option
      EndLine: int option
      EndColumn: int option
      Fix: string option }

// ─── SARIF parsing helpers (defensive — a malformed report degrades to []) ──────────────

let private prop (key: string) (node: JsonNode) : JsonNode option =
    match node with
    | :? JsonObject as o ->
        match o[key] with
        | null -> None
        | v -> Some v
    | _ -> None

let private asString (node: JsonNode) : string option =
    match node with
    | null -> None
    | _ ->
        try
            Some(node.GetValue<string>())
        with _ ->
            None

let private asInt (node: JsonNode) : int option =
    match node with
    | null -> None
    | _ ->
        try
            Some(node.GetValue<int>())
        with _ ->
            try
                Some(int (node.GetValue<double>()))
            with _ ->
                None

let private asArray (node: JsonNode option) : JsonNode list =
    match node with
    | Some(:? JsonArray as arr) -> arr |> Seq.filter (isNull >> not) |> Seq.toList
    | _ -> []

/// Convert a SARIF artifact `uri` (often `file:///...`) to a local path; pass other forms through.
let private uriToPath (uri: string) =
    if String.IsNullOrWhiteSpace uri then
        uri
    elif uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) then
        try
            Uri(uri).LocalPath
        with _ ->
            uri
    else
        uri

/// Normalize a SARIF `result.level` into the tool's severity vocabulary. SARIF levels are
/// none | note | warning | error; an omitted level defaults to "warning" (the usual rule
/// defaultConfiguration). note/none collapse to "info".
let severityFromLevel (level: string option) : string =
    match level |> Option.map (fun s -> s.Trim().ToLowerInvariant()) with
    | Some "error" -> "error"
    | Some "warning" -> "warning"
    | Some "note" -> "info"
    | Some "none" -> "info"
    | Some other when not (String.IsNullOrWhiteSpace other) -> other
    | _ -> "warning"

/// Best-effort analyzer label when the SARIF run carries no tool/driver name: take the
/// leading segment of the rule id (e.g. "GRA-STRING-001" → "GRA").
let private analyzerFromRuleId (ruleId: string) =
    match ruleId.Split([| '-'; '.' |], StringSplitOptions.RemoveEmptyEntries) with
    | [||] -> "analyzer"
    | parts -> parts.[0]

/// Parse a SARIF v2.1.0 report string into normalized diagnostics. Tolerant by design:
/// any structural surprise yields fewer rows, never an exception.
let parseSarif (sarif: string) : AnalyzerDiagnostic list =
    if String.IsNullOrWhiteSpace sarif then
        []
    else
        try
            match JsonNode.Parse(sarif) with
            | null -> []
            | root ->
                [ for run in asArray (prop "runs" root) do
                      let driverName =
                          run
                          |> prop "tool"
                          |> Option.bind (prop "driver")
                          |> Option.bind (prop "name")
                          |> Option.bind asString

                      for result in asArray (prop "results" run) do
                          let ruleId = result |> prop "ruleId" |> Option.bind asString |> Option.defaultValue "unknown"

                          let level = result |> prop "level" |> Option.bind asString

                          let message =
                              result
                              |> prop "message"
                              |> Option.bind (prop "text")
                              |> Option.bind asString
                              |> Option.defaultValue ""

                          let physical =
                              match asArray (prop "locations" result) with
                              | first :: _ -> first |> prop "physicalLocation"
                              | [] -> None

                          let file =
                              physical
                              |> Option.bind (prop "artifactLocation")
                              |> Option.bind (prop "uri")
                              |> Option.bind asString
                              |> Option.map uriToPath

                          let region = physical |> Option.bind (prop "region")
                          let regionInt k = region |> Option.bind (prop k) |> Option.bind asInt

                          yield
                              { Analyzer = driverName |> Option.defaultValue (analyzerFromRuleId ruleId)
                                Code = ruleId
                                Severity = severityFromLevel level
                                Message = message
                                File = file
                                StartLine = regionInt "startLine"
                                StartColumn = regionInt "startColumn"
                                EndLine = regionInt "endLine"
                                EndColumn = regionInt "endColumn"
                                Fix = None } ]
        with _ ->
            []

// ─── Grouping / filtering / counts (the agent-facing shape) ─────────────────────────────

let private normalizeSeverityFilter (s: string) =
    match s.Trim().ToLowerInvariant() with
    | "warn" -> "warning"
    | "information" -> "info"
    | other -> other

/// Keep only diagnostics whose normalized severity matches `severity` (case-insensitive).
/// None / blank ⇒ no filtering.
let applySeverityFilter (severity: string option) (diags: AnalyzerDiagnostic list) =
    match severity |> Option.filter (String.IsNullOrWhiteSpace >> not) with
    | None -> diags
    | Some s ->
        let want = normalizeSeverityFilter s
        diags |> List.filter (fun d -> d.Severity = want)

let private diagnosticToJson (d: AnalyzerDiagnostic) : JsonNode =
    let rangeNode: JsonNode =
        match d.StartLine with
        | None -> null
        | Some sl ->
            jobj
                [ "startLine", jint sl
                  "startColumn", d.StartColumn |> Option.map jint |> Option.defaultValue null
                  "endLine", d.EndLine |> Option.map jint |> Option.defaultValue null
                  "endColumn", d.EndColumn |> Option.map jint |> Option.defaultValue null ]
            :> JsonNode

    jobj
        [ "analyzer", jstr d.Analyzer
          "code", jstr d.Code
          "severity", jstr d.Severity
          "message", jstr d.Message
          "file", d.File |> Option.map jstr |> Option.defaultValue null
          "range", rangeNode
          "fix", d.Fix |> Option.map jstr |> Option.defaultValue null ]
    :> JsonNode

let private groupCount (selector: AnalyzerDiagnostic -> string) (diags: AnalyzerDiagnostic list) : JsonNode =
    diags
    |> List.countBy selector
    |> List.sortBy fst
    |> List.map (fun (key, n) -> key, jint n)
    |> jobj
    :> JsonNode

/// Build the `counts` node over the (severity-filtered) set; `returned` is the page size.
let countsNode (diags: AnalyzerDiagnostic list) (returned: int) : JsonNode =
    jobj
        [ "total", jint diags.Length
          "returned", jint returned
          "byAnalyzer", groupCount (fun d -> d.Analyzer) diags
          "bySeverity", groupCount (fun d -> d.Severity) diags ]
    :> JsonNode

// ─── Response builders ─────────────────────────────────────────────────────────────────

[<Literal>]
let noAnalyzersNote = "add analyzers via fcs_analyzer_setup_preview"

/// No analyzer configuration at all → the minimal "nothing to read" shape.
let noAnalyzersResponse () : JsonNode =
    jobj
        [ "status", jstr "no_analyzers"
          "analyzersConfigured", jbool false
          "analyzerPackages", JsonArray() :> JsonNode
          "note", jstr noAnalyzersNote ]
    :> JsonNode

let private emptyCounts () : JsonNode =
    jobj
        [ "total", jint 0
          "returned", jint 0
          "byAnalyzer", jobj [] :> JsonNode
          "bySeverity", jobj [] :> JsonNode ]
    :> JsonNode

/// Analyzers ARE configured, but we could not run them (no CLI / running disabled).
/// Truthfully reports the configured packages with empty diagnostics + a clear note.
let configuredNotRunResponse (packagesJson: JsonNode array) : JsonNode =
    jobj
        [ "status", jstr "analyzers_configured"
          "analyzersConfigured", jbool true
          "analyzerPackages", JsonArray(packagesJson) :> JsonNode
          "runnerAvailable", jbool false
          "diagnostics", JsonArray() :> JsonNode
          "counts", emptyCounts ()
          "note",
          jstr
              "Analyzers are configured but the fsharp-analyzers CLI is not available to run them — install it (dotnet tool install --global fsharp-analyzers) to read diagnostics. project_health reports analyzer SETUP; this tool reports DIAGNOSTICS when runnable." ]
    :> JsonNode

/// Analyzers configured and a runner was found, but the run errored. Still reports the
/// configured packages; surfaces the error in `note` rather than dropping the signal.
let runFailedResponse (packagesJson: JsonNode array) (error: string) : JsonNode =
    jobj
        [ "status", jstr "run_failed"
          "analyzersConfigured", jbool true
          "analyzerPackages", JsonArray(packagesJson) :> JsonNode
          "runnerAvailable", jbool true
          "diagnostics", JsonArray() :> JsonNode
          "counts", emptyCounts ()
          "note", jstr $"fsharp-analyzers run failed: {error}" ]
    :> JsonNode

/// A successful run: severity-filter, cap at maxResults, and group into the agent shape.
let okResponse
    (packagesJson: JsonNode array)
    (diags: AnalyzerDiagnostic list)
    (severity: string option)
    (maxResults: int)
    : JsonNode =
    let filtered = applySeverityFilter severity diags
    let page = filtered |> List.truncate (max 1 maxResults)
    let diagnosticsArray = page |> List.map diagnosticToJson |> List.toArray

    jobj
        [ "status", jstr "ok"
          "analyzersConfigured", jbool true
          "analyzerPackages", JsonArray(packagesJson) :> JsonNode
          "runnerAvailable", jbool true
          "diagnostics", JsonArray(diagnosticsArray) :> JsonNode
          "counts", countsNode filtered page.Length
          "truncated", jbool (filtered.Length > page.Length)
          "note",
          jstr
              "Analyzer findings from the fsharp-analyzers run — these are ANALYZER diagnostics, NOT compiler diagnostics (use check / workspace_diagnostics for compiler errors)." ]
    :> JsonNode

// ─── Live runner (environment-gated; UNEXERCISED in CI) ─────────────────────────────────

/// Setting this env var to "1"/"true" forces detectRunner to report no runner, so the
/// tool deterministically reports configuration without spawning a process. Tests rely on
/// it (and on the CLI simply being absent in CI).
[<Literal>]
let disableRunEnvVar = "FSLANGMCP_DISABLE_ANALYZER_RUN"

let private runIsDisabled () =
    match Environment.GetEnvironmentVariable disableRunEnvVar with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

/// Probe whether the `fsharp-analyzers` CLI is invokable. Returns the resolved command or
/// None when it is not installed or running is disabled. The probe spawns the CLI with
/// `--version`; a missing executable (Win32Exception) yields None.
let detectRunner () : string option =
    if runIsDisabled () then
        None
    else
        try
            let psi = ProcessStartInfo("fsharp-analyzers", "--version")
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use p = Process.Start(psi)

            if p.WaitForExit(15000) && p.ExitCode = 0 then
                Some "fsharp-analyzers"
            else
                None
        with _ ->
            None

/// Best-effort live invocation for ONE project: run `fsharp-analyzers --project P --report T`,
/// then return the SARIF contents of T. UNEXERCISED in CI (see module header). A fully
/// correct invocation may also need an explicit analyzers-path; here we let the CLI resolve
/// from the project's restored analyzers. Any failure → Error (the caller reports it honestly).
let runAnalyzers (command: string) (projectPath: string) : Result<string, string> =
    let reportPath =
        Path.Combine(Path.GetTempPath(), $"fslangmcp_analyzers_{Guid.NewGuid():N}.sarif")

    try
        let psi = ProcessStartInfo(command)
        psi.ArgumentList.Add("--project")
        psi.ArgumentList.Add(projectPath)
        psi.ArgumentList.Add("--report")
        psi.ArgumentList.Add(reportPath)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use p = Process.Start(psi)
        let stderr = p.StandardError.ReadToEnd()
        p.WaitForExit() |> ignore

        if File.Exists reportPath then
            let sarif = File.ReadAllText reportPath

            try
                File.Delete reportPath
            with _ ->
                ()

            Ok sarif
        else
            Error(
                if String.IsNullOrWhiteSpace stderr then
                    $"fsharp-analyzers produced no report (exit {p.ExitCode})"
                else
                    stderr.Trim()
            )
    with ex ->
        Error ex.Message
