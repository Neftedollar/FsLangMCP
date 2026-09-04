---
title: FsLangMCP
description: Semantic F# tools for AI coding agents, powered by the compiler rather than text search.
layout: splash
pageNav: false
toc: false
---

<div class="landing">
<section class="landing-hero">
<div class="landing-hero__copy">
<p class="landing-kicker"><span>FsLangMCP 0.17</span> · semantic tooling over MCP</p>
<h1>Stop grepping F#.<span>Give your agent the compiler.</span></h1>
<p class="landing-hero__lede">Cross-project symbol search, trustworthy type-check verdicts, refactor previews and project-aware diagnostics — shaped for an AI coding agent, backed by FCS and FsAutoComplete.</p>
<div class="landing-actions">
<a class="landing-button landing-button--primary" href="/FsLangMCP/guide/getting-started/">Install and connect</a>
<a class="landing-button" href="/FsLangMCP/tools/reference/">Explore all 35 tools</a>
</div>
<ul class="landing-signals" aria-label="Product guarantees">
<li>Compiler-resolved</li>
<li>Cross-project</li>
<li>Honest about incomplete evidence</li>
</ul>
</div>

<div class="landing-terminal" role="img" aria-label="An agent initializes a project, checks it and finds semantic Order uses across two projects">
<div class="landing-terminal__bar"><span></span><span></span><span></span><strong>agent session</strong></div>
<div class="landing-terminal__body">
<p><span class="terminal-prompt">agent›</span> understand this F# solution</p>
<p><span class="terminal-tool">set_project</span> <span class="terminal-muted">Quickstart.slnx</span></p>
<p class="terminal-result">✓ 2 projects ready</p>
<p><span class="terminal-tool">check</span> <span class="terminal-muted">workspace</span></p>
<p class="terminal-result">✓ clean · fresh compiler evidence</p>
<p><span class="terminal-tool">find</span> <span class="terminal-muted">Order · all sites</span></p>
<p class="terminal-result terminal-result--accent">17 semantic sites · 9 across project boundaries</p>
</div>
</div>
</section>

<section class="landing-section landing-section--proof">
<div class="landing-section__intro">
<p class="landing-eyebrow">The difference</p>
<h2>Text sees spelling. The compiler sees meaning.</h2>
<p>F# depends on partial application, shadowing, aliased opens, computation expressions and compile order. FsLangMCP exposes that semantic model directly to the agent.</p>
</div>
<div class="landing-card-grid">
<article class="landing-card">
<p class="landing-card__index">01 · Find</p>
<h3>Follow the real symbol</h3>
<p>Definitions, references, member calls and record-field writes across every project in the solution — without comments and same-name noise.</p>
</article>
<article class="landing-card">
<p class="landing-card__index">02 · Check</p>
<h3>Know whether the edit compiles</h3>
<p>A fresh <code>clean</code>, <code>errors</code> or <code>unknown</code> verdict. Missing coverage is reported instead of being mistaken for success.</p>
</article>
<article class="landing-card">
<p class="landing-card__index">03 · Plan</p>
<h3>Preview the blast radius</h3>
<p>Rename, public API, compile order, likely tests and dead-code candidates before the agent changes a file.</p>
</article>
</div>
</section>

<section class="landing-section landing-demo">
<div class="landing-section__intro">
<p class="landing-eyebrow">A real session</p>
<h2>One small loop: initialize, check, find.</h2>
<p>The demo runs against the repository's two-project quickstart. The result is real tool output, not a staged mock.</p>
</div>
<figure class="landing-demo__frame">
<img src="/FsLangMCP/agent-session.gif" alt="An AI agent calls set_project, check and find against the FsLangMCP quickstart project" loading="lazy" width="900" height="452" />
<figcaption><code>find</code> resolves 17 Order sites, including nine in a project that a grep scoped to Domain never sees.</figcaption>
</figure>
</section>

<section class="landing-section landing-section--measure">
<div class="landing-section__intro">
<p class="landing-eyebrow">Designed for agent routing</p>
<h2>Fewer guesses between intent and evidence.</h2>
</div>
<div class="landing-metrics">
<div><strong>35</strong><span>agent-facing tools</span></div>
<div><strong>2</strong><span>semantic bridges: FCS + FSAC</span></div>
<div><strong>3</strong><span>honest check verdicts</span></div>
</div>
<p class="landing-fineprint">The core surface stays simple: start with <code>find</code> and <code>check</code>, then reach for specialized tools only when the task calls for them.</p>
</section>

<section class="landing-cta">
<p class="landing-eyebrow">Ready when your agent is</p>
<h2>Install one .NET tool. Start with <code>find</code> and <code>check</code>.</h2>
<div class="landing-install"><code>dotnet tool install -g FsLangMcp</code><span>then</span><code>fslangmcp --bootstrap-tools</code></div>
<div class="landing-actions">
<a class="landing-button landing-button--primary" href="/FsLangMCP/guide/getting-started/">Read the quickstart</a>
<a class="landing-button" href="https://github.com/Neftedollar/FsLangMCP">View on GitHub</a>
</div>
</section>
</div>
