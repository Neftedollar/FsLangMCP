# Contributing to FsLangMcp

For the user-facing pitch and install instructions, see [README.md](README.md). This guide is for contributors who want to **add tools, fix bugs, or improve docs in FsLangMcp itself**.

## Build & test

```bash
# Build (Release, warnings-as-errors)
dotnet build FsLangMcp.fsproj -c Release --nologo

# Run the full test suite
dotnet test tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj -c Release --nologo
```

- **Target framework**: `net10.0` (set in `FsLangMcp.fsproj`).
- **Warnings-as-errors**: enforced via `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props`. A clean build is mandatory before opening a PR.
- **Test baseline**: 289 passing at `v0.9.0`. Every PR must keep the suite green and add tests for any new behaviour.

Optional helpers via the `Justfile`:

```bash
just restore   # restore tools + packages
just check     # build + test
just analyze   # run F# analyzers (Ionide.Analyzers, G-Research.FSharp.Analyzers)
```

## Adding a new MCP tool

The codebase ships ~32 tools that all follow the same registration shape. To add another:

1. **Define the args record in `Types.fs`** with `///` doc-comments on every field. Use existing records as the style template (e.g. `FcsValidateSnippetArgs`, `FcsRecordFieldAuditArgs`). Defaults stated in `///` text must match the actual `defaultArg` call site in the handler.

2. **Implement the handler** as a `member` on `FcsBridge` (in-process FCS work) or `FsAutoCompleteBridge` (LSP-shaped proxies). Return `Task<JsonNode>`. Reuse `jobj` / `jstr` / `jint` / `jbool` helpers from `Types.fs` for response construction.

3. **Validate required string args** at the top of the handler:

   ```fsharp
   match ArgsValidation.requireNonBlank "symbolName" args.symbolName with
   | Error envelope -> Task.FromResult envelope
   | Ok symbolName -> ...
   ```

   This returns the standard `{ "status": "invalid_args", "message": "…" }` envelope (standardised v0.8.2, #120).

4. **Register in `Program.fs`** inside the `mcpServer { ... }` builder block:

   ```fsharp
   tool (
       TypedTool.define<MyToolArgs>
           "tool_name"
           "[FCS in-process] Short routing description …"
           (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.MyTool args)))
       |> unwrapResult
   )
   ```

   Use `fcsGate` for FCS-backed work, `lspGate` for fsautocomplete proxies. The semaphores cap concurrent FCS work at `FSLANGMCP_MAX_CONCURRENT_FCS` (default 2) and concurrent LSP work at `FSLANGMCP_MAX_CONCURRENT_LSP` (default 1).

5. **Write the description** following `docs/tool-description-schema.md`. Target 250–400 chars across the 5 slots (Tag + What + Prefer-X-over-Y + Key-params + Caveat + Cross-ref). The schema doc explains why and shows anti-patterns.

6. **Add regression tests** in `tests/FsLangMcp.Tests/FcsBridgeTests.fs` (or the bridge-appropriate file). Cover both the success path AND failure modes (invalid args, missing project, etc.). For refactors of existing tools, prefer **proof-by-breaking** — write the test against a behaviour the old implementation cannot satisfy, so the test fails deterministically if the production code is reverted. See the v0.9.0 CHANGELOG entry for #124 for a worked example.

7. **Document any new response field** under README's **"Notable response fields"** subsection and in the originating Args/response record's `///` docs.

## Repo conventions

- **No `Co-Authored-By` trailers** on commits (per maintainer's global rule).
- **F# style**: 4-space indent, pipeline operators preferred, idiomatic discriminated unions for response shapes where they help.
- **`Option.ofObj`** is only appropriate for genuinely nullable returns from .NET APIs. For non-optional `string` args coming from the MCP wire (which can still be blank), use `ArgsValidation.requireNonBlank` in `Types.fs` — that is the standard contract.
- **Warnings-as-errors** is enforced — `dotnet build -c Release` must complete with `0 Warning(s)`.
- **Tests must include both positive and failure-mode cases.** Smoke tests that only verify happy paths get caught in review.

## PR process

- Keep PRs small and focused. One conceptual change per PR.
- Link to a GitHub issue in the PR description. For ad-hoc UX feedback without a dedicated issue, comment on the catch-all tracking issue (see "Reporting bugs" below) and link it from the PR.
- Include test coverage for new behaviour. Reviewers will ask for proof-by-breaking on refactors.
- Maintainer review uses subagent loops with `engineering-code-reviewer` and `engineering-fsharp-developer` agents. See recent CHANGELOG entries (v0.8.1, v0.8.2, v0.9.0) for examples of the iterate-until-approved pattern. Expect reviewer iter-1 to catch test theatre, scope creep, and stale comments — re-spin until the gate passes clean.

## Reporting bugs / requesting features

- **Usage-pattern UX feedback** (subagent observations, tool-discipline notes, missing-feature signals from real multi-agent sessions) → comment on the catch-all tracking issue [`Neftedollar/FsLangMCP#100`](https://github.com/Neftedollar/FsLangMCP/issues/100). Batch low-signal issues there to avoid maintainer thread fatigue.
- **Concrete bugs or actionable feature requests** → open a dedicated issue with a minimal repro (commit SHA, MCP request payload, observed response, expected response).
- **Security-sensitive reports** → see `SECURITY.md` if present; otherwise email the maintainer directly via the repo owner contact on GitHub.

See also: [AGENT_INTEGRATION.md](AGENT_INTEGRATION.md) for integration patterns that contributors writing about FsLangMcp should align with, and [`docs/architecture.md`](docs/architecture.md) for a codebase tour.
