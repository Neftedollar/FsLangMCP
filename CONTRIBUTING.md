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
- **Test baseline**: run the full suite above and keep every test green; the count is intentionally not pinned because regression coverage grows with each release.

Optional helpers via the `Justfile`:

```bash
just restore       # restore the locked NuGet graph
just tool-restore  # restore exact development tools from dotnet-tools.json
just check     # build + test
just analyze   # run F# analyzers (Ionide.Analyzers, G-Research.FSharp.Analyzers)
```

## Documentation site

The public documentation is a [Nacara](https://github.com/MangelMaxime/Nacara) site. Its F# project and configuration live in `docs/`; the published output is generated and must not be committed.

```bash
# Validate every page, route, anchor, and generated asset without writing output
dotnet run --project docs/FsLangMcp.Docs.fsproj -- check --strict

# Preview at the URL printed by Nacara and rebuild on edits
dotnet run --project docs/FsLangMcp.Docs.fsproj -- watch
```

Every public Markdown page needs Nacara front matter with at least `title`. The public allow-list, routes, and menus are explicit in `docs/Site.fs`; operational files such as `docs/process.md`, `docs/workflows/`, and launch drafts are intentionally not published. Adding a public page therefore requires both the Markdown file and its entry in `Site.fs`.

### First production publish (one time)

The deployment job writes the generated site to the `gh-pages` branch. After that branch has been created by the first successful run, a repository administrator must open **Settings → Pages**, select **Deploy from a branch**, choose **`gh-pages`** and **`/(root)`**, then save. GitHub documents the exact steps in [Configuring a publishing source](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site). Later documentation changes on `main` publish automatically.

## Adding a new MCP tool

The v0.17.1 surface ships 35 tools that all follow the same registration shape. To add another:

1. **Define the args record in `Types.fs`** with `///` doc-comments on every field. Use current records such as `FindArgs` and `CheckArgs` as style templates. Defaults stated in `///` text must match the actual `defaultArg` call site in the handler.

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
           "Short routing description …"
           (fun args -> toolResult (runLimited fcsGate (fun () -> fcsBridge.MyTool args)))
       |> unwrapResult
   )
   ```

   Use `fcsGate` for FCS-backed work, `lspGate` for fsautocomplete proxies. The semaphores cap concurrent FCS work at `FSLANGMCP_MAX_CONCURRENT_FCS` (default 2) and concurrent LSP work at `FSLANGMCP_MAX_CONCURRENT_LSP` (default 1).

5. **Write the description** following `docs/tool-description-schema.md`. Target 250–400 chars across its 4 slots (What + Prefer/Avoid + Key params + Caveat/Cross-ref). The schema doc explains why and shows anti-patterns.

6. **Add regression tests** in `tests/FsLangMcp.Tests/FcsBridgeTests.fs` (or the bridge-appropriate file). Cover both the success path AND failure modes (invalid args, missing project, etc.). For refactors of existing tools, prefer **proof-by-breaking** — write the test against a behaviour the old implementation cannot satisfy, so the test fails deterministically if the production code is reverted. See the v0.9.0 CHANGELOG entry for #124 for a worked example.

7. **Document any new response field** under README's **"Notable response fields"** subsection and in the originating Args/response record's `///` docs.

## Repo conventions

- **No `Co-Authored-By` trailers** on commits (per maintainer's global rule).
- **F# style**: 4-space indent, pipeline operators preferred, idiomatic discriminated unions for response shapes where they help.
- **`Option.ofObj`** is only appropriate for genuinely nullable returns from .NET APIs. For non-optional `string` args coming from the MCP wire (which can still be blank), use `ArgsValidation.requireNonBlank` in `Types.fs` — that is the standard contract.
- **Warnings-as-errors** is enforced — `dotnet build -c Release` must complete with `0 Warning(s)`.
- **Tests must include both positive and failure-mode cases.** Smoke tests that only verify happy paths get caught in review.
- **Locked restore** (#167): package versions use exact NuGet ranges (`[x.y.z]`,
  not the minimum-version meaning of bare `x.y.z`) and `packages.lock.json` is
  committed. CI restores with `--locked-mode`, which fails (NU1004) if the lock
  is out of sync. After intentionally changing a `PackageReference`, run
  `just restore-update` and commit both lock files with the fsproj change;
  `--force-evaluate` regenerates locks but does not discover an update. Routine
  version bumps arrive via Dependabot PRs.
- **SDK floor and release pin**: `global.json` accepts stable .NET 10 SDKs from
  `10.0.100` onward via `latestFeature`. CI tests that minimum separately, while
  the primary, live-FSAC, and release jobs remain pinned to the reviewed SDK
  `10.0.400`. Runtime FSAC/ProjInfo/Fantomas versions come only from
  `dotnet-tools.json`. Run `just live-fsac` before changing LSP startup, project
  loading, or those pins; GitHub Actions repeats the live smoke on Linux, macOS,
  and Windows.

## PR process

- Keep PRs small and focused. One conceptual change per PR.
- Link to a GitHub issue in the PR description. One reproducible behavior belongs to one bounded issue; add evidence to an existing issue only when it is the same behavior.
- Include test coverage for new behaviour. Reviewers will ask for proof-by-breaking on refactors.
- Maintainer review uses subagent loops with `engineering-code-reviewer` and `engineering-fsharp-developer` agents. See recent CHANGELOG entries (v0.8.1, v0.8.2, v0.9.0) for examples of the iterate-until-approved pattern. Expect reviewer iter-1 to catch test theatre, scope creep, and stale comments — re-spin until the gate passes clean.

## Reporting bugs / requesting features

- **Concrete UX bugs or actionable feature requests** → open one dedicated, bounded issue per reproducible behavior. Include the FsLangMCP version, project shape, request payload, observed response/timing, and expected behavior. If an issue already tracks that exact behavior, add the evidence there instead of opening a duplicate.
- **Positive or non-actionable usage notes** → keep them in the originating task/PR report; they do not need a GitHub catch-all. Promote a note to an issue when it becomes a falsifiable behavior or a concrete request.
- **Security-sensitive reports** → see `SECURITY.md` if present; otherwise email the maintainer directly via the repo owner contact on GitHub.

See also: [AGENT_INTEGRATION.md](AGENT_INTEGRATION.md) for integration patterns that contributors writing about FsLangMcp should align with, and [`docs/architecture.md`](docs/architecture.md) for a codebase tour.
