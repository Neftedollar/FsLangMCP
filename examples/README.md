# FsLangMCP — Quickstart Example

This folder contains a minimal two-project F# solution designed to let you verify
FsLangMCP works end-to-end in under three minutes. It demonstrates the two headline
tools — `find` (cross-project semantic search) and `check` (compile verdict) — on a
case where naive text search falls short.

**Time to first success: ~3 minutes.**

---

## What is in `quickstart/`

```
quickstart/
├── Quickstart.slnx          ← two-project solution
├── Domain/
│   ├── Domain.fsproj        ← library: defines Order
│   └── Order.fs             ← type Order + module Order (describe, isLargeOrder)
└── App/
    ├── App.fsproj           ← console app, references Domain
    └── Program.fs           ← constructs Order values, calls Order.describe
```

The cross-project contrast encoded here:

```
rg "Order" examples/quickstart/Domain/    # finds the definition — misses App/
find(query="Order")                       # sweeps both projects, returns all sites
```

`Order` is defined in `Domain/Order.fs` and constructed in `App/Program.fs`. A
search scoped to one project misses the other's use sites. `find` resolves across
every `.fsproj` in the solution via the F# compiler — no text matching.

---

## Prerequisites

| Requirement | Check |
|-------------|-------|
| .NET SDK 10+ on PATH | `dotnet --version` → `10.x` |
| FsLangMCP installed | `dotnet tool install -g FsLangMcp` |
| FSAC toolchain | `fslangmcp --bootstrap-tools` (one-time) |
| An MCP-capable agent | Claude Code, Cursor, Copilot CLI, Codex |

The `--bootstrap-tools` step fetches `fsautocomplete` and `ionide.projinfo.tool`.
Run it once after install; it writes nothing into the example project.

---

## MCP server config

Add to your agent's MCP config. Claude Code (`.mcp.json` in repo root or
`~/.claude/settings.json` globally):

```json
{
  "mcpServers": {
    "fslangmcp": { "command": "fslangmcp" }
  }
}
```

Same `{ "command": "fslangmcp" }` entry for Cursor (`.cursor/mcp.json`) and any
other stdio MCP client. The server is a dotnet global tool on PATH — no working
directory, no environment variables, no wrapper script needed.

---

## First success path

Run these four tool calls in order. Response shapes below are **illustrative** —
field names are exact, numeric values will reflect your run.

### 1. Load the solution

Tool: **`set_project`**

```json
{ "projectPath": "/absolute/path/to/examples/quickstart/Quickstart.slnx" }
```

> Substitute the real absolute path. The server accepts `.slnx`, `.sln`, `.fsproj`,
> or a directory.

Response shape:

```json
{
  "status": "ready",
  "fslangmcpVersion": "0.12.1",
  "projectCount": 2,
  "lspRestarted": true,
  "loadedProjects": [
    "…/Domain/Domain.fsproj",
    "…/App/App.fsproj"
  ],
  "readiness": {
    "lsp": true,
    "projectOptions": true,
    "symbolIndex": false
  }
}
```

`readiness.symbolIndex` starts false; it warms in the background. The other two
tools work immediately once `lsp` and `projectOptions` are both true.

---

### 2. Get a compile verdict

Tool: **`check`** — no arguments needed.

```json
{}
```

Response shape:

```json
{
  "status": "succeeded",
  "verdict": "clean",
  "analyzed": true,
  "scope": "project",
  "speed": "trusted",
  "errorCount": 0,
  "warningCount": 0,
  "fresh": true,
  "groundTruth": true,
  "diagnostics": []
}
```

`verdict` is one of `clean | errors | unknown`. `fresh: true` means the check ran
a new in-process FCS type-check, not a stale cached snapshot. This solution
produces a clean verdict.

---

### 3. Find the cross-project usages of `Order`

Tool: **`find`**

```json
{ "query": "Order" }
```

Response shape (abbreviated — `sites` contains one entry per project here for
readability; your run will show all sites):

```json
{
  "status": "succeeded",
  "query": "Order",
  "kind": "auto",
  "exact": true,
  "resolution": {
    "matched": true,
    "kindResolved": "auto",
    "scopeResolved": "workspace",
    "projectsSwept": 2,
    "via": "fcs-multiproject-sweep",
    "fcsSiteCount": 6,
    "fsacFallbackHits": 0
  },
  "projectsSwept": 2,
  "totalSites": 6,
  "breakdown": {
    "definitions": 1,
    "references": 3,
    "fieldSetLiteral": 2,
    "fieldSetUpdate": 0,
    "fieldRead": 0,
    "memberUsages": 0
  },
  "sites": [
    {
      "file": "…/Domain/Order.fs",
      "range": { "startLine": 3, "startColumn": 5, "endLine": 3, "endColumn": 10 },
      "kind": "definition",
      "project": "Domain",
      "symbolFullName": "Domain.Order",
      "lineText": "type Order = {"
    },
    {
      "file": "…/App/Program.fs",
      "range": { "startLine": 4, "startColumn": 6, "endLine": 4, "endColumn": 11 },
      "kind": "field-set-literal",
      "project": "App",
      "symbolFullName": "Domain.Order",
      "lineText": "    { Id = 1; Customer = \"Alice\"; Amount = 250.00m }"
    }
  ]
}
```

Key observation: `resolution.projectsSwept = 2` and sites appear from **both**
`Domain/Order.fs` (definition) and `App/Program.fs` (field-set-literal,
field-set-literal, reference). `rg "Order" examples/quickstart/Domain/` would
return only the Domain sites.

Each site carries `file`, `range` (0-based startLine/startColumn/endLine/endColumn),
`kind` (definition | reference | field-set-literal | field-set-update | field-read |
member-usage), `project`, `symbolFullName`, and `lineText`.

---

### 4. Outline a file

Tool: **`fcs_file_outline`**

```json
{ "path": "/absolute/path/to/examples/quickstart/Domain/Order.fs" }
```

Response shape (`summaryOnly=true` is the default — returns type/module headers only,
no per-member signatures):

```json
{
  "status": "succeeded",
  "file": "…/Domain/Order.fs",
  "summaryOnly": true,
  "count": 7,
  "memberCounts": { "record": 1, "module": 1, "function_or_value": 2, "field": 3 },
  "entries": [
    {
      "name": "Order",
      "kind": "record",
      "fullName": "Domain.Order",
      "range": { "startLine": 3, "startColumn": 0, "endLine": 8, "endColumn": 1 }
    },
    {
      "name": "Order",
      "kind": "module",
      "fullName": "Domain.Order",
      "range": { "startLine": 10, "startColumn": 0, "endLine": 18, "endColumn": 28 }
    }
  ]
}
```

Pass `summaryOnly: false` to get full per-member signatures. Use `fcs_project_outline`
(no `path`) for a whole-solution overview.

---

## What just happened

The agent got compiler-resolved, cross-project answers — no grep.

FCS loaded both projects, resolved symbol references across the project boundary,
and returned structured site data (file, range, kind, lineText) for every use of
`Order` in the solution. The `check` call type-checked the code from scratch, not
from a cached or stale build output.

---

## Verify the example builds

```bash
cd examples/quickstart
dotnet restore
dotnet build   # → Build succeeded. 0 Warning(s). 0 Error(s).
dotnet run --project App/App.fsproj
# Order #1 for Alice: $250.00
# Order #2 for Bob: $1500.00 [LARGE]
```

---

## End-of-run summary

- **Cross-project find demonstrated**: `Order` is defined in `Domain/Order.fs` and
  constructed in `App/Program.fs`. `find(query="Order")` returns sites from both
  projects; `rg "Order" Domain/` returns only the definition.

- **Build result**: `dotnet build` exits 0 with 0 warnings and 0 errors on .NET 10.

- **Grep-vs-find contrast encoded**: field-set-literal sites in `App/Program.fs`
  (`{ Id = 1; ... }`) are invisible to a text search scoped to the Domain project.
  `find` resolves them via the F# compiler across the project boundary.

- **`check` gives a fresh verdict**: `verdict: "clean"`, `fresh: true`,
  `groundTruth: true` — not a stale cache read.

- **Response field names are exact**: `totalSites`, `breakdown`, `sites[].kind`,
  `sites[].symbolFullName`, `sites[].lineText`, `resolution.projectsSwept` all
  match the v0.12.1 tool implementation.

- **Time to first success: ~3 minutes** (install + bootstrap + four tool calls).
