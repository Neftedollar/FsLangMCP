module FsLangMcp.Tests.AnalyzerDiagnosticsTests

// ─── #72: fcs_analyzer_diagnostics — report F# ANALYZER diagnostics, grouped ────────────
//
// Two layers under test:
//   • Config detection + the bridge orchestration (no_analyzers / analyzers_configured /
//     invalid_args). Deterministic: the live fsharp-analyzers CLI is suppressed via the
//     FSLANGMCP_DISABLE_ANALYZER_RUN env override (and is absent in CI anyway), so the
//     "configured" path reports configuration without spawning a process.
//   • The pure SARIF parser + grouping/filter/counts builder, exercised directly against a
//     fixture SARIF string — this carries the agent-facing shape, independent of any run.

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// Suppress the live analyzer run for every test in this module so the bridge reports
// configuration deterministically rather than shelling out to a (possibly installed) CLI.
do Environment.SetEnvironmentVariable(FsLangMcp.AnalyzerDiagnostics.disableRunEnvVar, "1")

// ── JSON helpers ────────────────────────────────────────────────────────────────────────

let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()
let private gb (node: JsonNode) (key: string) = node[key].GetValue<bool>()
let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()

let private arr (node: JsonNode) (key: string) =
    match node[key] with
    | :? JsonArray as a -> a |> Seq.toList
    | _ -> []

// ── Fixtures ──────────────────────────────────────────────────────────────────────────

let private withTempDir (f: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_analyzers_{Guid.NewGuid():N}")
    Directory.CreateDirectory(root) |> ignore

    try
        f root
    finally
        if Directory.Exists root then
            try
                Directory.Delete(root, true)
            with _ ->
                ()

let private writeProjectWithAnalyzer (root: string) =
    File.WriteAllText(Path.Combine(root, "Library.fs"), "module Library\n\nlet value = 1\n")
    let projectPath = Path.Combine(root, "Library.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            "\n"
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
              "  <ItemGroup><Compile Include=\"Library.fs\" /></ItemGroup>"
              "  <ItemGroup>"
              "    <PackageReference Include=\"Ionide.Analyzers\" Version=\"0.15.0\">"
              "      <IncludeAssets>analyzers</IncludeAssets>"
              "      <PrivateAssets>all</PrivateAssets>"
              "    </PackageReference>"
              "  </ItemGroup>"
              "</Project>" ]
    )

    projectPath

let private writeProjectWithoutAnalyzer (root: string) =
    File.WriteAllText(Path.Combine(root, "Library.fs"), "module Library\n\nlet value = 1\n")
    let projectPath = Path.Combine(root, "Library.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            "\n"
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
              "  <ItemGroup><Compile Include=\"Library.fs\" /></ItemGroup>"
              "</Project>" ]
    )

    projectPath

let private adArgs (projectPath: string option) (severity: string option) (maxResults: int option) : FcsAnalyzerDiagnosticsArgs =
    { projectPath = projectPath
      severity = severity
      maxResults = maxResults }

/// Fixture SARIF v2.1.0: three results across two analyzers and three severities, including
/// a `note` level (→ info) and a result with a partial region (startLine only).
let private fixtureSarif =
    """
{
  "version": "2.1.0",
  "runs": [
    {
      "tool": { "driver": { "name": "Ionide.Analyzers" } },
      "results": [
        { "ruleId": "IONIDE-001", "level": "warning", "message": { "text": "prefer ignore" },
          "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///proj/Foo.fs" },
            "region": { "startLine": 10, "startColumn": 5, "endLine": 10, "endColumn": 20 } } } ] },
        { "ruleId": "IONIDE-002", "level": "error", "message": { "text": "bad thing" },
          "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///proj/Bar.fs" },
            "region": { "startLine": 3 } } } ] }
      ]
    },
    {
      "tool": { "driver": { "name": "G-Research.FSharp.Analyzers" } },
      "results": [
        { "ruleId": "GRA-STRING-001", "level": "note", "message": { "text": "a note" },
          "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///proj/Baz.fs" },
            "region": { "startLine": 1, "startColumn": 1 } } } ] }
      ]
    }
  ]
}
"""

// ── Bridge orchestration ────────────────────────────────────────────────────────────────

[<Fact>]
let ``analyzer_diagnostics reports analyzers_configured and the packages for a configured project`` () : Task =
    task {
        withTempDir (fun root ->
            let projectPath = writeProjectWithAnalyzer root
            let bridge = FcsBridge()
            let result = bridge.AnalyzerDiagnostics(adArgs (Some projectPath) None None).Result

            // Configuration is reported truthfully even though the live CLI run is suppressed.
            Assert.NotEqual<string>("no_analyzers", gs result "status")
            Assert.Equal("analyzers_configured", gs result "status")
            Assert.True(gb result "analyzersConfigured")

            let packages = arr result "analyzerPackages"
            let ids = packages |> List.map (fun p -> gs p "packageId")
            Assert.Contains("Ionide.Analyzers", ids))
    }

[<Fact>]
let ``analyzer_diagnostics returns no_analyzers for a project without analyzers`` () : Task =
    task {
        withTempDir (fun root ->
            let projectPath = writeProjectWithoutAnalyzer root
            let bridge = FcsBridge()
            let result = bridge.AnalyzerDiagnostics(adArgs (Some projectPath) None None).Result

            Assert.Equal("no_analyzers", gs result "status")
            Assert.False(gb result "analyzersConfigured")
            Assert.Empty(arr result "analyzerPackages")
            Assert.Contains("fcs_analyzer_setup_preview", gs result "note"))
    }

[<Fact>]
let ``analyzer_diagnostics returns invalid_args without a project context`` () : Task =
    task {
        let bridge = FcsBridge()
        let! result = bridge.AnalyzerDiagnostics(adArgs None None None)

        Assert.Equal("invalid_args", gs result "status")
        Assert.Contains("project context", gs result "message")
    }

// ── Pure SARIF parsing ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``parseSarif normalizes analyzer, code, severity, file and range`` () =
    let diags = FsLangMcp.AnalyzerDiagnostics.parseSarif fixtureSarif

    Assert.Equal(3, diags.Length)

    let first = diags |> List.find (fun d -> d.Code = "IONIDE-001")
    Assert.Equal("Ionide.Analyzers", first.Analyzer)
    Assert.Equal("warning", first.Severity)
    Assert.Equal(Some 10, first.StartLine)
    Assert.Equal(Some 20, first.EndColumn)
    Assert.True(first.File |> Option.exists (fun f -> f.EndsWith("Foo.fs")))

[<Fact>]
let ``parseSarif maps the SARIF note level to info`` () =
    let diags = FsLangMcp.AnalyzerDiagnostics.parseSarif fixtureSarif
    let note = diags |> List.find (fun d -> d.Code = "GRA-STRING-001")
    Assert.Equal("info", note.Severity)
    Assert.Equal("G-Research.FSharp.Analyzers", note.Analyzer)

[<Fact>]
let ``severityFromLevel maps levels and defaults a missing level to warning`` () =
    Assert.Equal("error", FsLangMcp.AnalyzerDiagnostics.severityFromLevel (Some "error"))
    Assert.Equal("warning", FsLangMcp.AnalyzerDiagnostics.severityFromLevel (Some "warning"))
    Assert.Equal("info", FsLangMcp.AnalyzerDiagnostics.severityFromLevel (Some "note"))
    Assert.Equal("warning", FsLangMcp.AnalyzerDiagnostics.severityFromLevel None)

// ── Grouping / counts / filter / pagination ─────────────────────────────────────────────

[<Fact>]
let ``okResponse groups counts by analyzer and by severity over the full set`` () =
    let diags = FsLangMcp.AnalyzerDiagnostics.parseSarif fixtureSarif
    let result = FsLangMcp.AnalyzerDiagnostics.okResponse [||] diags None 200

    Assert.Equal("ok", gs result "status")
    Assert.Equal(3, (result["diagnostics"] :?> JsonArray).Count)

    let counts = result["counts"]
    Assert.Equal(3, gi counts "total")
    Assert.Equal(3, gi counts "returned")
    Assert.False(gb result "truncated")

    let byAnalyzer = counts["byAnalyzer"]
    Assert.Equal(2, gi byAnalyzer "Ionide.Analyzers")
    Assert.Equal(1, gi byAnalyzer "G-Research.FSharp.Analyzers")

    let bySeverity = counts["bySeverity"]
    Assert.Equal(1, gi bySeverity "error")
    Assert.Equal(1, gi bySeverity "warning")
    Assert.Equal(1, gi bySeverity "info")

[<Fact>]
let ``okResponse severity filter keeps only the matching severity`` () =
    let diags = FsLangMcp.AnalyzerDiagnostics.parseSarif fixtureSarif
    let result = FsLangMcp.AnalyzerDiagnostics.okResponse [||] diags (Some "error") 200

    Assert.Equal(1, (result["diagnostics"] :?> JsonArray).Count)
    let counts = result["counts"]
    Assert.Equal(1, gi counts "total")
    Assert.Equal(1, gi counts["bySeverity"] "error")
    // The filtered-out severities must not appear in the by-severity counts.
    Assert.Null(counts["bySeverity"]["warning"])
    Assert.Null(counts["bySeverity"]["info"])

[<Fact>]
let ``okResponse caps the list at maxResults but counts reflect the full total`` () =
    let diags = FsLangMcp.AnalyzerDiagnostics.parseSarif fixtureSarif
    let result = FsLangMcp.AnalyzerDiagnostics.okResponse [||] diags None 2

    // Page is capped to 2 …
    Assert.Equal(2, (result["diagnostics"] :?> JsonArray).Count)
    // … but the counts still reflect all three diagnostics.
    let counts = result["counts"]
    Assert.Equal(3, gi counts "total")
    Assert.Equal(2, gi counts "returned")
    Assert.True(gb result "truncated")

// ── Shared config detection (ProjectHealth.detectAnalyzerConfig) ─────────────────────────

[<Fact>]
let ``detectAnalyzerConfig flags a configured project and lists the package`` () =
    withTempDir (fun root ->
        let projectPath = writeProjectWithAnalyzer root

        match FsLangMcp.ProjectHealth.detectAnalyzerConfig projectPath with
        | Error e -> Assert.Fail($"expected Ok, got Error: {e}")
        | Ok cfg ->
            Assert.True(cfg.Configured)
            Assert.Contains("Ionide.Analyzers", cfg.Packages |> List.map (fun p -> p.PackageId)))

[<Fact>]
let ``detectAnalyzerConfig reports an un-configured project as not configured`` () =
    withTempDir (fun root ->
        let projectPath = writeProjectWithoutAnalyzer root

        match FsLangMcp.ProjectHealth.detectAnalyzerConfig projectPath with
        | Error e -> Assert.Fail($"expected Ok, got Error: {e}")
        | Ok cfg ->
            Assert.False(cfg.Configured)
            Assert.Empty(cfg.Packages))
