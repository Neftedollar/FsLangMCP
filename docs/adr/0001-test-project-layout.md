# ADR 0001 — Test project layout: linked-source vs ProjectReference

## Status

**Proposed.** Merging this ADR commits the project to the recommended option below; the actual fsproj migration is a separate change set.

## Context

FsLangMcp ships as a single-project .NET tool: `FsLangMcp.fsproj` at the repo root, packaged as a `dotnet tool` (`PackageId=FsLangMcp`, `PackAsTool=true`, `ToolCommandName=fslangmcp`). Production source files (`BoundedCache.fs`, `Types.fs`, `ProjectFiles.fs`, `Cursor.fs`, `ProjectInspection.fs`, `ProjectHealth.fs`, `LspBridge.fs`, `FcsBridge.fs`, `Tools.fs`, `RuntimeStatus.fs`, `Program.fs`) sit at the repo root.

The test project, `tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj`, does **not** declare a `<ProjectReference>` to the production fsproj. Instead it pulls every production source file in via linked compile items:

```xml
<Compile Include="../../BoundedCache.fs" Link="BoundedCache.fs" />
<Compile Include="../../Types.fs"        Link="Types.fs" />
<!-- … nine more linked files … -->
<Compile Include="RuntimeStatusTests.fs" />
```

The test assembly therefore compiles **all production source directly**; there is no separate `FsLangMcp.dll` next to `FsLangMcp.Tests.dll`. The likely original motivation is access to `internal` declarations without `[<assembly: InternalsVisibleTo>]` ceremony — and this matters here because production code is rich with internals: `BoundedCache<'K,'V>` (internal type), `FcsBridge()` (internal type), `FsAutoCompleteBridge()` (internal type), `ManagedHeapInfo` + `heapJson` (internal type and function), `CliParseResult` (internal type), the entire `WorkspaceNotification` and `WorkspaceSelection` modules (internal), and 19 internal declarations across `ProjectFiles.fs` (`ScanKind`, `ExclusionReason`, `ScanFilterOptions`, `ProjectFile`, `FilteredProjectFiles`, `tryReadProject`, `resolveProjectPath`, `isFsFile`, `isGeneratedFile`, `defaultFilterOptions`, `compileFiles`, `filterProjectFiles`, `filterSummaryToJson`, etc.). All 11 test files import at least one production module via `open FsLangMcp.X`, and several drive their assertions directly through internal symbols.

Two concrete tooling incidents in this codebase trace to this layout:

1. **PR [#88 — Stryker.NET mutation testing](https://github.com/Neftedollar/FsLangMCP/pull/88)** was closed without merge. Two independent blockers were documented on the close. The first is upstream (Stryker.NET supports C# only; tracking issue stryker-mutator/stryker-net#1216). The second is **ours**: Stryker's project-graph detector walks assembly references to map test-project ↔ project-under-test, finds none in our linked-source layout, and aborts in ~5 s with `Could not find an assembly reference to a mutable assembly`. Even if F# mutators landed upstream tomorrow, this layout would still block adoption.
2. **PR [#92 — Coverlet coverage gate](https://github.com/Neftedollar/FsLangMCP/pull/92)** ships a 70 % line-coverage gate, but only after switching from the standard `XPlat Code Coverage` collector (which silently reports 0 % for this layout) to `coverlet.msbuild` with `IncludeTestAssembly=true`. The fsproj carries an inline comment documenting this (`tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj:56–58`).

A test-quality re-audit produced this session lists "every future tooling integration (mutation testing, profilers, code-coverage UI tools) will hit this" as Recommendation 3. The recurring theme: standard .NET tooling assumes test assembly → SUT assembly via `ProjectReference`, and we silently violate that assumption.

## Decision drivers

In priority order:

1. **Tooling compatibility**. Mutation testing, IDE solution-explorer affordances, profilers, test-impact analysis, and (most pressingly) coverage tools all assume a test-assembly → SUT-assembly graph mediated by `ProjectReference`. Each surcharge adds setup friction or workarounds.
2. **Build-graph cleanliness and mental model**. The current layout makes the tests `IsTestProject=true` while simultaneously being a re-compile of the production code. This is unusual and confuses both humans and tools. A clean two-project graph is easier to reason about.
3. **Access to internals**. There are **34 internal declarations** across six production files (`BoundedCache.fs:1`, `FcsBridge.fs:1`, `LspBridge.fs:3`, `ProjectFiles.fs:19`, `RuntimeStatus.fs:2`, `Types.fs:1`). Tests genuinely depend on a non-trivial subset — `BoundedCache<_,_>`, `FcsBridge()`, `FsAutoCompleteBridge()`, `heapJson`, `WorkspaceNotification.*`, `WorkspaceSelection.*`, `filterProjectFiles`, `defaultFilterOptions`, `ScanKind`. Any change to layout must preserve this access; the only question is **how**.
4. **Migration cost**. All 11 test files import production modules via `open FsLangMcp.X` and would not need to change at the source level. The migration is fsproj-only (linked `<Compile>` items removed, `<ProjectReference>` added, internals exposed). Touch surface is small — two fsprojs and (optionally) one tiny `AssemblyInfo.fs`.
5. **NuGet package shape**. The shipped artifact today is one nupkg containing `fslangmcp` as a `PackAsTool`. Whatever we do must not change this from the user's perspective. Multi-project tool packs are possible but add complexity (`<IncludeReferencedProjects>true</IncludeReferencedProjects>` or merge-into-tool patterns).

## Options considered

### Option A — Status quo (linked `<Compile>`)

Keep things as they are. Test assembly recompiles production source.

**Costs already paid**

- PR #88 closed; Stryker.NET adoption blocked even on the day F# mutators land upstream.
- PR #92 added a non-standard coverage path with an explanatory comment in the fsproj. Anyone following standard .NET docs (`dotnet test --collect:"XPlat Code Coverage"`) sees 0 % silently.

**Projected costs**

- Every future tool that walks the assembly reference graph (test-impact analysis, profilers, IDE features) will hit the same wall and need either a workaround or escalation upstream.
- New contributors hit the layout once before learning it. Cost: small but recurring.

**Why one might keep it**: zero migration effort; tests already work; coverage already passes the 70 % gate.

### Option B — Full split (Library + Exe + Tests)

Restructure into:

```
src/FsLangMcp.Core/FsLangMcp.Core.fsproj       # SDK library, all production .fs except Program.fs
src/FsLangMcp/FsLangMcp.fsproj                 # Exe, ProjectReference→Core, contains only Program.fs, PackAsTool=true
tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj   # ProjectReference→Core
```

**Migration steps**

1. Move `BoundedCache.fs` … `RuntimeStatus.fs` (10 files) into `src/FsLangMcp.Core/` and create `FsLangMcp.Core.fsproj`.
2. Keep `Program.fs` in `src/FsLangMcp/`; reduce that fsproj to one `<Compile>` plus `<ProjectReference Include="../FsLangMcp.Core/FsLangMcp.Core.fsproj" />`.
3. Update `tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj`: drop all linked `<Compile Include="../../*.fs">`, add `<ProjectReference Include="../../src/FsLangMcp.Core/FsLangMcp.Core.fsproj" />`.
4. Add `[<assembly: InternalsVisibleTo("FsLangMcp.Tests")>]` to `src/FsLangMcp.Core/AssemblyInfo.fs` (one line; or via `<AssemblyAttribute>` MSBuild item).
5. Update `FsLangMcp.slnx`, `Justfile`, `Directory.Build.*` paths if any reference the old root layout.
6. Verify `dotnet pack src/FsLangMcp/FsLangMcp.fsproj` still produces a working `dotnet tool install --global FsLangMcp`. The tool needs `Core.dll` packed alongside the exe — `dotnet tool` does this by default for project-referenced libraries, but the resulting nupkg layout changes (now contains both DLLs in `tools/net10.0/any/`).

**NuGet shape impact**: still one nupkg (`PackageId=FsLangMcp`). Internally it carries `FsLangMcp.dll` + `FsLangMcp.Core.dll` instead of one DLL. End user runs `fslangmcp` exactly as before. Verified pattern; no F#-specific gotcha.

**Pros**

- Cleanest project graph. Standard `.NET` mental model. Stryker, profilers, coverage tools all "just work".
- Forces explicit module-API design (`internal` vs `public`) at the library boundary.
- Sets up the repo for future growth (e.g. a separate analyzer package or test harness package without restructuring again).

**Cons**

- Most ceremony of the three options. Three fsprojs to maintain, slnx changes, possible CI path adjustments, possible `nupkg` content review on first publish.
- The repo is a single tool today; a hard split is overengineering relative to current scope unless we expect the library to be consumed independently.

### Option C — Single fsproj + `ProjectReference` from tests + `InternalsVisibleTo`

Keep the production fsproj as a single project at the repo root (no library/exe split). Change only the test side:

1. In `tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj`: delete every `<Compile Include="../../*.fs" Link="..." />` line.
2. Add `<ProjectReference Include="../../FsLangMcp.fsproj" />`.
3. Expose internals to the test assembly. Two equivalent forms:

   - **Inline in the production fsproj**, no new file:
     ```xml
     <ItemGroup>
       <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
         <_Parameter1>FsLangMcp.Tests</_Parameter1>
       </AssemblyAttribute>
     </ItemGroup>
     ```
   - **Or a tiny `AssemblyInfo.fs`** at the top of the production compile order:
     ```fsharp
     module internal FsLangMcp.AssemblyInfo
     [<assembly: System.Runtime.CompilerServices.InternalsVisibleTo "FsLangMcp.Tests">]
     do ()
     ```
4. Verify `dotnet pack` is unchanged — same single nupkg, same exe, same `PackAsTool=true`.

**Pros**

- Smallest possible change. Production fsproj structure unchanged. NuGet output unchanged. Only the test fsproj and one IVT line move.
- Resolves both documented incidents:
  - Stryker's project-graph detector now sees the standard `<ProjectReference>` link → unblocks (the F# mutator gap remains an upstream issue, but our half is fixed).
  - `dotnet test --collect:"XPlat Code Coverage"` now instruments the production assembly correctly → can drop `IncludeTestAssembly=true` workaround if desired.
- Tests need no source-level change. All 11 test files keep their `open FsLangMcp.X` lines verbatim; the 34 internal declarations remain accessible because IVT propagates them to the test assembly.
- Future tooling (profilers, TIA, IDE features) finds the standard graph it expects.

**Cons**

- Slightly less architecturally pure than Option B. The exe project still contains every production source file, including `Program.fs`. This is the same shape as today, so it does not regress; it just defers the library/exe split to whenever (if ever) we want a separately-consumable library.
- IVT is sometimes called "ceremony", but in this case it's three lines, applied once, and never touched again.

## Decision

**Adopt Option C — single fsproj + `ProjectReference` from tests + `InternalsVisibleTo`.**

Rationale, in one paragraph: the two documented costs (PR #88 Stryker, PR #92 Coverlet) both stem from the missing `ProjectReference` edge in the project graph, not from the absence of a library/exe split. Option C closes that exact gap with a 4-line change and zero impact on NuGet shape, source files, or end-user experience. Option B is architecturally cleaner but pays for its cleanliness with three fsprojs, a directory restructuring, and a `dotnet pack` verification cycle — all to solve a problem we don't have today (we ship one tool, no third-party library consumers). If the repo later acquires a reason to expose a reusable library (e.g. a public `FsLangMcp.Core` for analyzers or a Cursor-only NuGet), the migration from Option C to Option B is straightforward at that time. **Option A is rejected** because the costs are recurring (every future graph-walking tool pays them) and the fix is small.

## Consequences

If accepted, the follow-up migration (separate PR) does:

- `tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj`: remove all 10 `<Compile Include="../../*.fs" Link="..." />` items; add `<ProjectReference Include="../../FsLangMcp.fsproj" />`.
- `FsLangMcp.fsproj`: add an `<AssemblyAttribute>` ItemGroup for `InternalsVisibleTo("FsLangMcp.Tests")` (preferred — keeps the tree free of an extra file).
- Optionally simplify the Coverlet configuration in CI: `IncludeTestAssembly=true` is no longer required because `coverlet.msbuild` will see the production assembly through the project reference. Leaving it on is harmless; turning it off cleans up the explanatory comment block at `tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj:56-58`.
- Re-run `dotnet build`, `dotnet test`, `dotnet pack`. All 165 tests must pass; the resulting nupkg must be byte-equivalent in shape (one DLL, `tools/net10.0/any/FsLangMcp.dll`, `PackAsTool=true`).
- The path from this state to Option B (full library/exe split) becomes a contained refactor whenever it is justified; no decision blocks that future move.

If a future change requires Option B (e.g. shipping a reusable `FsLangMcp.Core` NuGet, or a downstream analyzer pack consuming the same code), open a follow-up ADR superseding this one. The IVT line moves to the new core project unchanged.

## Cross-references

- **PR #88** — chore(testing): Stryker.NET mutation testing setup — closed without merge with explicit blocker (2): "tests/FsLangMcp.Tests.fsproj uses `<Compile Include=…>` rather than `<ProjectReference>` — Stryker's project-graph detector can't link the test project to the project-under-test through linked sources." https://github.com/Neftedollar/FsLangMCP/pull/88
- **PR #92** — test(infra): Coverlet coverage gate + FsCheck on Cursor — explanatory PR body: "The XPlat collector cannot instrument this layout and produces 0% — `coverlet.msbuild` with `IncludeTestAssembly=true` is the correct solution." https://github.com/Neftedollar/FsLangMCP/pull/92
- **Test-quality re-audit, Recommendation 3** — "Every future tooling integration (mutation testing, profilers, code-coverage UI tools) will hit this. Recommends architectural fix."
- **Internal-symbol inventory** (this session): 34 `internal` declarations across `BoundedCache.fs` (1), `FcsBridge.fs` (1), `LspBridge.fs` (3), `ProjectFiles.fs` (19), `RuntimeStatus.fs` (2), `Types.fs` (1). All 11 test files in `tests/FsLangMcp.Tests/` import production modules; several drive assertions through internal types directly.
