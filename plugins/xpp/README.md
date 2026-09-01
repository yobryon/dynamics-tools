# dynamics-xpp

A Claude Code plugin for **Microsoft Dynamics 365 Finance & Operations
X++ development**.

Pairs a fleet of skills that teach Claude how to read, author, and
modify F&O AOT objects with a local MCP server that talks to the
F&O metadata store via the official `Microsoft.Dynamics.AX.Metadata.*`
APIs.

## What this plugin actually does for you

You're a D365 F&O developer. You're already running Visual Studio
2022 with the D365 extension and you author X++ for a living. This
plugin lets Claude work alongside you in the same codebase:

- **Read the AOT.** Search forms, tables, classes, EDTs, enums.
  Pull full XML for any object. Get a single method's source
  without opening VS. Find every reference to a method or table.
- **Author new objects.** Tables, classes, forms, EDTs, enums,
  label files, and all the extension variants. Claude prefers
  typed `xpp_create_{type}` / `xpp_patch_{type}` tools where
  available, with the bridge's `FromFile` deserializer as the
  authoritative validator — the same `Microsoft.Dynamics.AX.Metadata.*`
  API that VS uses.
- **Pick the right form pattern.** 10 dedicated skills for the
  named F&O UX patterns (SimpleListDetails, DetailsMaster,
  DetailsTransaction, ListPage, Workspace, Wizard, etc.), each
  grounded in current Microsoft Learn guidance with the variant
  selection rules and the legacy callouts (Task forms are
  deprecated — use Dialog or Drop Dialog instead; ListPage is
  discouraged for 1:1 list/detail; etc.).
- **Wireframe before authoring.** When designing new forms, Claude
  can produce annotated SVG wireframes faithful to F&O's patterns
  so you can review the structure before any XML is written.
- **Stay in convention.** Your repo's `.dynamics-xpp/config.json`
  carries your object-prefix and extension-suffix; the plugin uses
  them when proposing names so new objects feel native to your
  module.
- **Coordinate with your VS project.** Successful creates and
  updates auto-add to your `.rnproj`. When you flip back to VS,
  the changes are already in your project — no manual "add to
  project" step.

The skill fleet is **23 skills today**:

| Family | Skills |
|---|---|
| Onboarding | `dynamics-xpp:xpp-setup` (once per machine), `dynamics-xpp:xpp-project` (once per repo) |
| Anchor + language | `dynamics-xpp:xpp-language`, `dynamics-xpp:xpp-data` |
| Per-AOT-type | `dynamics-xpp:xpp-class`, `dynamics-xpp:xpp-table`, `dynamics-xpp:xpp-form`, `dynamics-xpp:xpp-edt`, `dynamics-xpp:xpp-enum`, `dynamics-xpp:xpp-labelfile`, `dynamics-xpp:xpp-extension` |
| Per-form-pattern (10) | `dynamics-xpp:xpp-pattern-simple-list`, `dynamics-xpp:xpp-pattern-simple-list-details`, `dynamics-xpp:xpp-pattern-details-master`, `dynamics-xpp:xpp-pattern-details-transaction`, `dynamics-xpp:xpp-pattern-list-page`, `dynamics-xpp:xpp-pattern-task`, `dynamics-xpp:xpp-pattern-task-parent-child`, `dynamics-xpp:xpp-pattern-wizard`, `dynamics-xpp:xpp-pattern-table-of-contents`, `dynamics-xpp:xpp-pattern-workspace-operational` |
| Sub-patterns catalog | `dynamics-xpp:xpp-form-subpatterns` |
| Wireframing | `dynamics-xpp:xpp-wireframe` |

You don't typically invoke these directly. They self-activate when
you ask Claude to do something relevant ("create a table for X",
"why is this BP warning showing up", "wireframe a customer detail
form"). You can also `/dynamics-xpp:<name>` them explicitly if you want a
particular topic in context.

## Prerequisites

This plugin only makes sense on a **Dynamics 365 F&O developer
machine** (Tier 1 VM deployed from LCS, or an equivalent dev
environment). Bring your own:

1. **Visual Studio 2022** (any edition — Enterprise / Professional
   / Community / BuildTools).
2. **The "Dynamics 365 Finance and Operations Tools" extension**
   for VS 2022 (from the VS Marketplace). The plugin uses this
   extension's `Microsoft.Dynamics.AX.Metadata.*` DLLs.
3. **.NET 9 SDK** (any 9.0.x). Verify with `dotnet --list-sdks`.
4. **A working F&O dev environment** —
   `PackagesLocalDirectory` accessible, your model deployed,
   AOS running.

The plugin doesn't install or configure any of these. If you're
not already a working F&O dev, this isn't the right tool.

## Getting started

There are two installation steps you do once, and one per-repo
init you do whenever you start using the plugin in a new F&O
codebase.

### Step 1 — Add this plugin marketplace

In any Claude Code session:

```
/plugin marketplace add https://github.com/<owner>/dynamics-xpp
```

(Or use the local-directory form if you've cloned this repo.)

### Step 2 — Install the plugin

```
/plugin install xpp@dynamics-tools
```

### Step 3 — First-time setup (once per dev machine)

After installing, in any Claude Code session on your dev box,
just ask:

> "Help me set up the dynamics-xpp plugin."

Claude loads the `dynamics-xpp:xpp-setup` skill and walks through:

1. Finding your VS 2022 D365 extension on disk.
2. Writing a per-machine `BridgeReferences.props` so the plugin's
   bridge can resolve the D365 metadata DLLs.

That's it — no build step. The plugin's `.mcp.json` launches the
MCP server via `dotnet run`, which auto-builds on first invocation
and incrementally rebuilds whenever source changes (after a plugin
update). You only need to re-run setup if you change machines or
the VS 2022 D365 extension gets reinstalled.

If you prefer to drive it from a script, use the `dt` CLI, which
does the discovery, the build, and puts itself on your PATH:

```powershell
cd <plugin install dir>
.\tools\dt.cmd setup
```

See [The `dt` CLI](#the-dt-cli) below for what else it does.

### Step 4 — Reload the plugin

After setup runs, ask Claude Code to refresh:

```
/reload-plugins
```

(Or fully restart Claude Code — either works.) The reload triggers
Claude to pick up the new `.mcp.json` config and start the MCP
server. The very first launch will take 15-30 seconds because
`dotnet run` is restoring NuGet packages and compiling the
projects. Subsequent launches are instant (the build is cached).

After the reload, run `/mcp` — `dynamics-xpp` should appear as
connected.

### Step 5 — Use the read surface freely

You can start using the plugin immediately for **read-only work**
— no project context required. Try:

> "Find the CustTable table and show me its XML."
>
> "Search for any X++ that calls `runBaseBatchRun`."
>
> "What does the `init()` method do on the SalesTable form?"
>
> "Wireframe a list page for customer payment terms."

These work in any directory, any session, no further setup.

### Step 6 — Per-repo init (once per F&O codebase)

When you want to **author or modify objects** in a specific F&O
codebase, start Claude Code in that repo's root (the directory
containing your `.sln`) and ask:

> "Help me set up dynamics-xpp for this repo."

Claude loads `dynamics-xpp:xpp-project` and walks you through:

1. **Finding the `.rnproj`** in the current directory.
2. **Reading the target model** from the `.rnproj` so it knows
   which model new objects belong to.
3. **Asking for your object name prefix** — the short identifier
   you prefix onto new objects in this codebase (e.g., `con` for
   Contoso Retail → `conCustomerProcessor`, `conSomeTable`).
4. **Asking for your extension suffix** — defaults to the model
   name. Used for metadata extensions like `CustTable.ContosoRetail`.
5. **Writing `.dynamics-xpp/config.json`** at the repo root so the
   plugin remembers.

After this, the write surface unlocks for the repo. The config
file is meant to be committed — when a teammate clones the repo,
they don't redo step 6.

## What "the write surface" includes today

Functional now:

- `xpp_create_object` — write a new AOT object from XML (escape hatch
  for types without a typed `xpp_create_{type}`).
- `xpp_update_object` — overwrite an existing object's XML (escape
  hatch for changes that the typed `xpp_patch_{type}` can't express).
- `xpp_get_object_xml` — round-trip the same XML back for editing.
- `xpp_create_{type}` / `xpp_patch_{type}` — typed authoring tools
  for AxClass, AxTable, AxForm, AxEnum, AxEdt, AxView, AxQuery,
  AxMenu, plus their `*Extension` variants. Prefer these — they're
  lossless against the bridge's deserializer and let you skip
  hand-authoring XML envelopes.

The bridge's `Microsoft.Dynamics.AX.Metadata` `FromFile` deserializer
is the validator. Failures come back as structured JSON naming the
offending property.

In progress (on the backlog, will land soon):

- `xpp_project_status` and friends — inspect the active project,
  add/remove objects in the `.rnproj`, etc.
- `xpp_bp_check` — run Best Practice checks on a scoped set of
  objects (per-element, cheap, fast feedback).
- `xpp_compile` — scoped compile (changeset / project / module).
- `xpp_db_sync` — scoped database sync.

When these land, the loop closes: write → validate → BP-check →
compile → sync, all driven by Claude without needing to flip to VS.

## The `dt` CLI

A small command-line companion for the things you do *outside* a
Claude session: setting the plugin up, updating it, and seeing what
the background service is doing. `dt setup` installs it to
`~/.local/bin/dt.cmd`, so afterwards just `dt` works from anywhere.

```
dt setup             One-time machine setup: locate the D365 assemblies,
                     build, and put dt on your PATH.
dt update            Update the marketplace + plugin, then rebuild.
dt version           Installed plugin version vs. the running service.
dt status            Live service and index status.

dt service status    Same as dt status.
dt service stop      Ask the service to stop gracefully.
dt service restart   Stop it, then start the current build.

dt cache clear       Delete the index cache (costs a full re-index).
```

`dt status` is the one to reach for when something feels wrong —
it reports whether the metadata bridge is healthy, how far the
index has got, and how many embeddings are built:

```
  service        : plugin 0.1.0, pid 18912
  bridge         : healthy
  index          : sweeping (sweep in progress)
  objects        : 269,605
  methods        : 971,442
  embeddings     : ready - 1,325,081 / 1,325,081 (100.0%)
```

**You rarely need `dt service restart`.** One service instance is
shared by every session on the box, and it upgrades itself: a
session running a newer plugin build asks the older service to
stand down and starts its own. So after `dt update`, existing
sessions keep using the old service until they end, and the next
new session picks up the new one automatically.

**Avoid `dt cache clear` unless you're told to.** The index is
hard-won — a full rebuild takes a long time and re-runs embedding,
which costs real money if semantic search is enabled. The usual
reason to reach for it (the service refusing to start against a
cache written by a newer build) is better fixed by closing the
stale session or running `dt update`.

Maintainer-facing actions — building one project, running a
component in the foreground, smoke tests — stay in
`tools/dev.ps1`.

## When the write tools refuse

A few guardrails the plugin enforces because doing the wrong thing
silently is worse than refusing:

- **Microsoft-shipped models are sealed** since release 8.0. You
  can't modify a base Microsoft object directly. If you try
  `xpp_update_object` against `CustTable` from a non-Microsoft
  model, the tool refuses and proposes the right extension shape:
  *"CustTable is in ApplicationSuite. Create `CustTable.YourModel`
  as an AxTableExtension instead — load `dynamics-xpp:xpp-extension` for the
  structural shape."*

- **No writes without a project.** The MCP doesn't know which model
  to target until you've done step 6 above. If you ask for a write
  before that, you'll see *"No project configured — load
  `dynamics-xpp:xpp-project` to set up this repo."* Cheap to recover from; you
  do step 6 once and the tools work from then on.

- **Read tools have neither restriction.** You can search, inspect,
  and learn freely in any directory at any time.

## When in doubt about how something works

Each skill is comprehensive on its concern; ask Claude any of:

- *"What's the difference between Task and Dialog patterns?"*
  → `dynamics-xpp:xpp-pattern-task` (legacy callout) plus the roadmap entry for
  the eventual `xpp:pattern-dialog`.
- *"How do I write a Chain of Command extension?"*
  → `dynamics-xpp:xpp-class` (CoC section) + `dynamics-xpp:xpp-extension`.
- *"What's set-based vs per-record DML in X++?"*
  → `dynamics-xpp:xpp-data`.
- *"Wireframe a workspace for sales-order processors."*
  → `dynamics-xpp:xpp-wireframe` (pairs with the pattern skill it implies).

Or just describe what you're trying to do; the relevant skills
auto-load based on the conversation.

## How the MCP server works (under the hood)

Three co-located processes on one Windows box, no network exposure:

- **`XppMetadataBridge`** (net48) — quarantines the
  `Microsoft.Dynamics.AX.Metadata.*` calls (which require net48).
  Stdio JSON-RPC.
- **`XppService`** (net9, Windows-only) — gRPC server over named
  pipes. Owns the SQLite + sqlite-vec index, the file-system
  watcher, the embedding model. One instance per box, shared
  across Claude sessions.
- **`XppService.Mcp`** (net9) — the MCP server itself. Speaks MCP
  over stdio to Claude; translates each tool call into a gRPC RPC
  against the service. Auto-spawns the service if the pipe is dead.

All three are built from this plugin's `src/` directory; the build
produces them under `bin/Release/...` where `.mcp.json` points.

## What's NOT in this plugin

- No telemetry; no calls outside your box.
- No model migration, version bumping, or LCS coordination.
- No automatic refactoring — the plugin gives Claude the surface
  to do refactoring; the agent supplies the judgment.
- No replacement for the form designer, table designer, or any
  other VS-side authoring tool. Flip to VS for those when you
  need them. The plugin keeps your project in sync so you can
  hand off freely.

## License

MIT. See [LICENSE](../../LICENSE) in the repo root.
