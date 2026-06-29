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
   `which fsautocomplete`. If missing, run `fslangmcp --bootstrap-tools`.
3. Inspect the readiness flags in the `set_project` response: if `readiness.lsp`
   stays `false`, the fsautocomplete child process is failing to start — check
   `fsharp_runtime_status` to see if it appears in `children`.

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

## `set_project` required before `textDocument_*` / `workspace_*` tools

The raw LSP-proxy tools (`textDocument_completion`, `textDocument_formatting`,
`textDocument_codeAction`, `textDocument_rename`, `fsharp_signature_data`) and
workspace tools require an initialized FSAC workspace. If called before
`set_project` completes, they return `{"status": "not_ready"}`.

**Remediation:**

Call `set_project` first and wait until `readiness.lsp=true` before invoking
any LSP-proxy tool. The `check` and `find` tools are more tolerant and will
wait for partial readiness automatically.

---

## `symbolIndex` is `false` right after `set_project`

`set_project` reports `readiness.symbolIndex=false` immediately after load. The
symbol index warms in the background while FSAC loads the project — this is
expected, not an error.

**What to do:** Start using `find`, `check`, and outline tools normally. They
trigger on-demand type-checking per file and don't depend on a fully warmed
global index. If `symbolIndex` stays `false` for more than a few minutes on a
medium-sized project, run `project_health` to check for underlying issues.

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

---

## `check` or `find` reports stale results after an edit

FCS caches project-wide results in memory. After editing a file, you may see
a brief window where `check` returns a cached snapshot.

**Remediation:** Call `check` with `speed: "trusted default"` (the default) rather
than `speed: "fast"`. The trusted default always performs a fresh in-process
type-check; `fast` returns the cached FSAC snapshot and may reflect
pre-edit state. If stale results persist, call `set_project` again to clear
FCS caches.

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

## For anything else

Open an issue at https://github.com/Neftedollar/FsLangMCP/issues and include:

- Output of the `fslangmcp_version` tool
- The exact tool call (name + args) that triggered the symptom
- The response you got (or absence thereof)
