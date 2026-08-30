# Troubleshooting

This page lists common failure modes by **what the user or agent sees**, with the remediation
chain to follow. If your symptom isn't here, open an issue at
https://github.com/Neftedollar/FsLangMCP/issues with the output of
`fslangmcp_version` (call the tool) so we can match it to a release.

---

## `{"status": "not_ready", "message": "..."}` after `set_project`

The LSP layer (fsautocomplete) hasn't finished its initial workspace scan yet. For
medium projects this can take 5–30 seconds.

**Remediation:**

1. Wait a few seconds and retry.
2. If it persists past 60 seconds, check that `fsautocomplete` is on PATH:
   `which fsautocomplete` (`Get-Command fsautocomplete` on PowerShell). If it is
   missing, install the reviewed runtime set: `fsautocomplete` `0.83.0`,
   `ionide.projinfo.tool` `0.74.2`, and `fantomas` `7.0.5`, using the exact
   commands in Getting Started.
3. Inspect the readiness flags in the `set_project` response: if `readiness.lsp`
   stays `false`, the fsautocomplete child process is failing to start — check
   `fsharp_runtime_status` to see if it appears in `children`.

If the handshake itself exceeds 60 seconds, FsLangMCP terminates and resets the
FSAC child instead of leaving the LSP slot blocked. Raise
`FSLANGMCP_LSP_STARTUP_TIMEOUT_MS` only when a known-large workspace genuinely
needs longer; ordinary live requests use `FSLANGMCP_LSP_REQUEST_TIMEOUT_MS`.

---

## `{"status": "infrastructure_error", "errorKind": "sdk_not_found"}` from `set_project`

The nearest `global.json` above the project pins an SDK version with
`rollForward: "disable"`, and that exact SDK is not installed on this machine.

`Ionide.ProjInfo` resolves the SDK by running `dotnet --version` **in the target
project's directory**, so it inherits the target's `global.json`; with an
unsatisfiable pin the muxer exits 155. `fsautocomplete` makes the same call during
its own startup, with nothing to catch the failure — in earlier releases the child died
before answering `initialize` and the only symptom reaching the agent was
`TransportError … "The JSON-RPC connection with the remote party was lost"`, which
named neither the SDK nor the file. FsLangMCP now screens the pin first and tells
you which one it is.

The response carries `requestedSdkVersion`, `globalJsonPath`, `installedSdks`, and
`remedies`.

**Remediation** (either one):

1. Install the requested SDK, then retry `set_project` — no server restart needed:
   the installed-SDK list is re-probed before any rejection, so a freshly installed
   SDK is picked up on the next call.
2. Edit the `globalJsonPath` from the response: pin an installed version, or
   replace `"rollForward": "disable"` with a policy that allows a newer SDK
   (for example `"latestMajor"`).

Confirm what the machine actually has with `dotnet --list-sdks`. Note that the
FsLangMCP server process and your shell can resolve different `dotnet` binaries —
the pre-flight uses the same order `Ionide.ProjInfo` does (`DOTNET_HOST_PATH`,
then `DOTNET_ROOT`, then `PATH`), so set `DOTNET_ROOT` in the MCP client's server
configuration if the two disagree.

The screen only ever fires on this one provable case. `rollForward` policies other
than `disable`, an `sdk.paths` redirect, an unreadable `global.json`, or a machine
whose SDK list cannot be enumerated all proceed to the normal load path.

---

## FCS tools fail with a confusing `FSharp.Core` path error or return empty results

The project hasn't been restored. FCS can't load project options when NuGet
packages are missing, which manifests as path-not-found errors referencing
`FSharp.Core.dll` or other assemblies.

**Remediation:**

```bash
dotnet restore
```

Then call `set_project` again. Starting from v0.12, `project_health` and
`set_project` both surface `restoreStatus: "unrestored"` in their responses so
you can catch this without reading FCS error messages.

---

## `set_project` required before raw LSP-proxy tools

The raw LSP-proxy tools (`textDocument_completion`, `textDocument_formatting`,
`textDocument_codeAction`, `textDocument_rename`, `fsharp_signature_data`)
require an initialized FSAC workspace. If called before `set_project` completes,
they return `{"status": "not_ready"}`. `fcs_signature_help` is in-process FCS
and does not require LSP readiness when explicit project options are supplied.

**Remediation:**

Call `set_project` first and wait until `readiness.lsp=true` before invoking
any LSP-proxy tool. The `check` and `find` tools are more tolerant and will
wait for partial readiness automatically.

---

## `symbolIndex` is `false` right after `set_project`

`set_project` can report `readiness.symbolIndex=false` immediately after load.
Read `readiness.symbolIndexState` and `readiness.symbolIndexHint`: they distinguish
a normally `warming` index from one that is `not_warmed` (past the warm-up window
with no result yet — see `docs/tools-detailed.md#set_project`), or an LSP that is
`blocked_on_lsp` or `not_started`, and state whether to wait/retry or call
`set_project` with `restartLsp=true`.

**What to do:** Start using `find`, `check`, and outline tools normally. They
trigger on-demand type-checking per file and don't depend on a fully warmed
global index — this holds for `not_warmed` too, not just `warming`.
`not_warmed` in particular can be a permanent, healthy state: the FSAC symbol
index is only queried as a fallback when `find`'s own FCS sweep comes up
empty, so a session where `find` keeps resolving normally may never warm the
index at all. The one thing a cold index does cost: that same zero-hit
fallback. A `find` whose sweep found nothing cannot get the
`via="fsac-symbol-index"` confirmation while the index is cold, so check
`fsacFallbackState` / `fsacFallbackReason` in the `find` payload before
reading its zero hits as proof the symbol does not exist. Only reach for `project_health` if a symbol-index-dependent
call has actually failed or `readiness.lsp` itself isn't `true` — `not_warmed`
by itself is not that signal.

---

## `find` returns no matches for a module-qualified name

`find` matches by simple name or dotted suffix, not by fully qualified
module path. A query like `"MyModule.processOrder"` will not match; query
`"processOrder"` or `"processOrder"` with a scope filter instead.

When `find` can't resolve a query, the response includes a `hint` field
explaining how to reformulate it.

---

## `FileNotFoundException: Microsoft.VisualStudio.Threading, Version=17.14.0.0`

Affects versions ≤ 0.8.1 only. The transitive bind via StreamJsonRpc couldn't
resolve at startup probing, breaking `set_project` in fresh subagent / multi-process
contexts.

**Remediation:** upgrade to 0.8.2 or later:

```bash
dotnet tool update -g FsLangMcp
```

The fix pins `Microsoft.VisualStudio.Threading.Only 17.14.15` as a direct
PackageReference and adds `rollForward: LatestMajor` to the runtimeconfig.

If the installed package is newer but a long-running MCP process still reports a
missing ProjInfo/VS.Threading dependency, see `stale_tool_process` below: the on-disk
package may be complete while the running process points at a version directory that
`dotnet tool update` has already replaced.

---

## `{"status":"infrastructure_error","errorKind":"stale_tool_process"}`

`dotnet tool update` replaces the global tool's versioned installation directory but
cannot replace a process an MCP client already started. If that old process lazily loads
ProjInfo/MSBuild later, its dependency directory may no longer exist and the symptom looks
like a partial NuGet package even though a clean installation is complete.

FsLangMCP now validates its own installation directory before entering semantic/project-load
paths. The response includes the running version, installation directory, missing files,
`restartRequired: true`, and a restart recommendation.

**Remediation:** restart the parent MCP client/server connection (or start a new client
session) so it spawns the currently installed binary. Re-running `dotnet tool update` does
not repair the already-running process. If a fresh process returns the same envelope, attach
the full payload to a dedicated issue because that is a genuine packaging/install failure.

---

## Version skew: tools behave differently than expected

The MCP client connects to the **installed** `fslangmcp` binary, not the local
build in your repo. If you've updated `FsLangMcp` in the repo but haven't
installed it globally, the agent is testing the old version.

**Remediation:** Call `fslangmcp_version` to confirm which version is actually
running. To update:

```bash
dotnet tool update -g FsLangMcp
```

**A second, easy-to-miss cause: the server is a long-lived process.**
`dotnet tool update -g FsLangMcp` only changes what's on disk — it does not
affect a `fslangmcp` process your MCP client already spawned and is still
running. A subagent started mid-session, or a fresh agent reusing an existing
client connection, can silently keep talking to the pre-update binary for the
rest of that session. Don't assume the on-disk version is the running
version: call `fslangmcp_version` again after updating, and if it still
reports the old version, the fix is a fresh MCP connection (restart the
client's server process, or start a new client session) — not a repeated
`dotnet tool update`.

---

## `check` or `find` reports stale results after an edit

FCS and FSAC cache project-wide results in memory. After editing a file,
`check(speed="fast")` may briefly return `verdict="unknown"` with
`status="not_ready"`, missing files, or stale files while FsLangMCP retires the
old diagnostic generation and waits for a content-bound replacement snapshot.

**Remediation:** Call `check` with `speed: "trusted"` (the default) rather
than `speed: "fast"`. Trusted mode always performs a fresh in-process
type-check. Fast mode never promotes a pre-edit empty snapshot to `clean`; retry
it after the replacement generation warms if latency matters. If `unknown`
persists, call `set_project` again and inspect the coverage fields. FsLangMCP
retains MSBuild project options only while their project, imports, restore
outputs, source contents, and source-directory inputs are unchanged.

`fsharp_runtime_status.fcs.projectOptions` exposes `loadAttempts`, `staleReloads`,
`cacheValidations`, and `inFlight`. `staleReloads` increments only when an existing
cached fingerprint became stale and was re-evaluated; cold loads and explicit evictions
do not inflate it.

If trusted `check` is `clean` but a Release build fails, first compare build profiles rather than
assuming stale FCS state. `clean` covers the current FCS/check profile; optimized Release
compilation can add configuration-specific diagnostics such as FS3511. The merge/release gate is
still `dotnet build -c Release --warnaserror`.

---

## `fsharp_runtime_status` recommends restarting because of high thread count

At 128 or more OS-visible threads, `process.threads.health.status` becomes `warning`,
`restartRecommended` becomes true, and the response recommends restarting the parent MCP
process. Retained in-process ProjInfo/MSBuild nodes are one known cause; the thread count
alone is not proof of ownership or a leak.

Use `fcs.projectOptions.staleReloads` alongside the thread count to see whether repeated
project-option re-evaluation correlates with growth. Restarting only the FSAC child cannot
reclaim threads owned by the parent `fslangmcp` process. The warning is observational: the
server never terminates itself or forces a restart.

---

## `find` returns 0 matches but the symbol exists

Two likely causes:

1. **Symbol is a record field set-site.** Use `find` with `kind=field` to search
   record-field construction and `with`-update sites:

   ```json
   find { "query": "FieldName", "kind": "field" }
   ```

2. **Project has compile errors** that prevented the symbol table from being built.
   Call `check` first and fix any errors, then retry `find`.

---

## A tool reports that an external process timed out

`proj-info`, bootstrap commands, and analyzer probes run with bounded waits. On
timeout FsLangMCP kills the whole child-process tree and releases its concurrency
slot. Relevant overrides are `FSLANGMCP_PROJ_INFO_TIMEOUT_MS` and
`FSLANGMCP_BOOTSTRAP_TIMEOUT_MS`; increase them only after confirming the child is
making progress rather than waiting indefinitely.

---

## For anything else

Open an issue at https://github.com/Neftedollar/FsLangMCP/issues and include:

- Output of the `fslangmcp_version` tool
- The exact tool call (name + args) that triggered the symptom
- The response you got (or absence thereof)
