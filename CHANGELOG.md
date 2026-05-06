# Changelog

All notable changes to **FsLangMCP** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
(pre-1.0: minor bumps may include breaking surface changes; see release notes).

## [Unreleased]

### Changed

- **Bumped `FsMcp.Server` and `FsMcp.TaskApi` from 1.0.1 to 1.1.1.** No source changes required; build passes with 0 warnings at WarningLevel 5. New surface in 1.1.x (resources/subscribe, subscribeToolsChanged, HTTP RunSessionHandler) is not consumed by FsLangMCP — our tool list is static and we run on stdio only.
- **Workstation GC + Concurrent GC defaults** baked into the published tool via `runtimeconfig.template.json`. Aligned with FsMcp 1.1.0's runtime-tuning guide for stdio servers. Operators can override with `DOTNET_gcServer=1` if they want Server GC. Reduces apparent RSS growth on dev laptops without affecting throughput at typical agent request rates.

## [0.5.0] - 2026-05-06

### Added

- **`fsharp_runtime_status`** — observational MCP tool reporting runtime, FCS checker config, cache stats, child processes, and assembly counts. Useful for diagnosing FCS resource pressure during long agent sessions. (#79)
- **Cursor pagination across project-scoped tools** — opaque, Base64-encoded cursor returned as `nextCursor` alongside `truncated` / `pageOffset` / `pageSize` / `totalEstimate`. Adopted in:
  - `fcs_project_outline` (paginates files) (#78)
  - `fcs_project_symbol_uses` (paginates uses) (#80)
  - `fcs_find_symbol` project-wide variant (paginates symbol groups) (#80)
- **`memberCounts` map per file** in `fcs_project_outline` summaryOnly mode — top-level `{ kind: count }` reflecting the *full* per-file outline, independent of `maxResultsPerFile`. Replaces the unreleased `_summary` sentinel node. (#82)
- **Filter args** for `fcs_project_outline`: `filter` (regex with `NonBacktracking` + 250 ms timeout) and `nameContains` (substring list). Applied before truncation. (#78)

### Changed

- `fcs_project_outline` defaults are now **conservative**: `summaryOnly = true`, `maxFiles = 50`, `maxResultsPerFile = 30` — keeps responses inside typical 25–50k token windows on ~50-file projects. Callers wanting full detail must opt in. (#78)
- Tool descriptions sharpened to be **agent-facing**: clearer scope, when-to-use guidance, and prerequisites across the FCS tool surface. (#77, #81)
- Internal: `Cursor.paginationFields` takes a `unitName` parameter so each tool's `totalEstimate` carries its own paginated unit (`files` / `uses` / `symbols`).

### Removed

- The synthetic `{ "kind": "_summary", "memberCount": N }` node previously appended inside `fcs_project_outline` summaryOnly entries. **Not breaking** for any released consumer — the sentinel was introduced in #78 between 0.4.0 and 0.5.0 and never shipped to NuGet. (#82)

## [0.4.0] - 2026-05-04

- Added agent-friendly navigation tools: `fcs_file_outline`, `fcs_project_outline`, `fcs_find_symbol`, `fcs_symbol_at_word`.
- Added `fsharp_project_inspect` for read-only `.fsproj` structure, compile order, references, and `.fsi`/`.fs` pairing.
- Added shared project file filtering for generated/build/test artifacts used by project-wide scans.
- Added `fsharp_signature_data` as a structured FSAC signature-data helper.
- Improved `set_project` workspace selection for directories: single solutions/projects are selected explicitly; ambiguous directories return candidates instead of guessing.
- Removed `textDocument_hover` from the exposed MCP tool surface; use `fcs_symbol_at_word` or exact-position FCS helpers instead.

## [0.3.1] - 2026-05-04

- Updated `FSharp.Compiler.Service` to `43.12.203` and aligned the implicit `FSharp.Core` package version to `10.1.203`.
- Centralized FCS / `FSharp.Core` package version settings in `Directory.Build.props`.
- Adjusted signature-help type formatting for the latest FCS API shape.
- Kept `fsharp_compile` on the FCS project typecheck path introduced in 0.3.0.

## [0.3.0] - 2026-05-04

- Added `project_health` for read-only project/tooling preflight.
- Added `fsharp_compile` as an FCS-backed project parse+typecheck tool using `FSharpChecker.ParseAndCheckProject`.
- Removed reliance on the unavailable `fsautocomplete 0.83` `fsharp/compile` endpoint and the `dotnet build` fallback.
- Added F# analyzer wiring and `just analyze`.
- Added tests for project health, compile validation, cache behavior, and invalid compile inputs.

## [0.2.0] - 2026-04-09

- **Breaking**: all LSP proxy responses now wrapped in `{status, result}` envelope. (#39)
- Added `fcs_get_project_options` tool wrapping proj-info. (#27, #37)
- Migrated from `Newtonsoft.Json` to `System.Text.Json`. (#13, #43)
- Performance: offload `LoadProjectOptionsFromFsproj` to `Task.Run`. (#32, #36)
- Refactor: split `Program.fs` into logical modules. (#22, #40)
- Structured `ToolError` DU with `errorKind` field. (#20, #34)

<!--
  Compare links: only versions that exist as git tags are linked.
  Earlier releases (0.2.0, 0.3.0, 0.3.1, 0.4.0) shipped without tags;
  backfilling them would point at synthetic refs.
-->
[Unreleased]: https://github.com/Neftedollar/FsLangMCP/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.5.0
