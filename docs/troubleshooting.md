# Troubleshooting

This page lists common failure modes by **what the user sees**, with the remediation
chain to follow. If your symptom isn't here, open an issue at
https://github.com/Neftedollar/FsLangMCP/issues with the output of
`fslangmcp_version` (call the tool) so we can match it to a release.

## `{"status": "not_ready", "message": "..."}` after `set_project`

The LSP layer (fsautocomplete) hasn't finished its initial workspace scan yet. For
medium projects this can take 5-30 seconds.

**Remediation:**

1. Wait a few seconds and retry.
2. If it persists past 60 seconds, check that `fsautocomplete` is on PATH:
   `which fsautocomplete`. If missing, run `fslangmcp --bootstrap-tools`.
3. Inspect the readiness flags in the `set_project` response: if `readiness.lsp`
   stays `false`, the fsautocomplete child process is failing to start — check
   `fsharp_runtime_status` to see if it appears in `children`.

## `FileNotFoundException: Microsoft.VisualStudio.Threading, Version=17.14.0.0`

Affects versions ≤ 0.8.1 only. The transitive bind via StreamJsonRpc couldn't
resolve at startup probing, breaking `set_project` in fresh subagent / multi-process
contexts.

**Remediation:** upgrade to 0.8.2 or later via
`dotnet tool update -g FsLangMcp`. The fix pins
`Microsoft.VisualStudio.Threading.Only 17.14.15` as a direct PackageReference and
adds `rollForward: LatestMajor` to the runtimeconfig.

## `workspace_diagnostics` returns empty / stale results right after an edit

FCS may have cached state from before your last `Edit`. The `workspace_diagnostics`
response includes per-URI `analyzedAt` timestamps (since 0.8.0) — compare against
your edit time to see whether the result is stale.

**Remediation:** call `fcs_check_file` with the path you just edited. This drops
cached project-options + project-results entries for THIS project (other loaded
projects keep their warm caches) and calls `checker.InvalidateConfiguration` before
re-running parse+check. For absolute ground truth across project boundaries, fall
back to `dotnet build`.

## `fcs_find_symbol` returns 0 matches but the symbol exists

Two possible causes:

1. **Symbol is field-set in a record literal or with-update expression.**
   `fcs_find_symbol` searches by symbol name; field-set sites are recorded by FCS
   as uses of the **field**, not the **type**. Use `fcs_record_field_audit` with
   the type name and field name to find every `{ Field = expr }` and `{ x with
   Field = expr }` construction site.

2. **Project has compile errors that prevented the symbol table from being built.**
   When the matched-file set is empty, `fcs_find_symbol` falls back to surfacing
   Error-severity diagnostics from the whole project, with
   `projectDiagnosticsScope: "errors-only-no-matches"`. Read the `projectDiagnostics`
   array — fix the compile errors first, then re-run the search.

---

For anything else not covered here, open an issue with:
- Output of the `fslangmcp_version` tool
- The exact tool call (name + args) that triggered the symptom
- The response you got (or absence thereof)
