# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A **Claude Code plugin marketplace** that distributes the
`dynamics-xpp` plugin — a fleet of skills + an MCP server for
Microsoft Dynamics 365 Finance & Operations X++ development.

```
dynamics-tools/
├── .claude-plugin/marketplace.json   ← marketplace manifest
└── plugins/
    └── xpp/                          ← the dynamics-xpp plugin
        ├── .claude-plugin/plugin.json
        ├── .mcp.json                  ← wires the MCP server
        ├── skills/                    ← 19 skills (anchor + types + patterns + subpatterns)
        ├── src/                       ← C# source for the MCP server
        ├── tools/                     ← dev.ps1 + smoke tests
        ├── dynamics-xpp.sln
        ├── README.md
        └── ROADMAP.md
```

Distribution model: **source via `dotnet run`.** The plugin ships
C# source; `.mcp.json` launches `dotnet run --project ...` which
builds incrementally on first invocation and skips rebuild when
source is unchanged. Setup just writes the per-machine
`BridgeReferences.props` to `${CLAUDE_PLUGIN_DATA}` (Claude Code's
persistent per-plugin data directory, which survives plugin
updates). See the plugin's own `README.md` for the user-facing
install story.

## Plugin internals (`plugins/xpp/`)

Three co-located processes on one Windows box, no network exposure.
All .NET; bridge is net48, everything else net9.

- **`plugins/xpp/src/XppMetadataBridge/`** — .NET Framework 4.8
  console app. Quarantines the `Microsoft.Dynamics.AX.Metadata.*`
  library calls (they require net48). Stdio JSON-RPC. Spawned by
  the service as a child process.
- **`plugins/xpp/src/XppService/`** — .NET 9 ASP.NET Core gRPC
  server over named pipes. The orchestrator. Owns the SQLite +
  sqlite-vec index, file-system watcher, embedding model
  (Qwen3-Embedding-0.6B via ONNX Runtime), and all search/inspection
  logic. Single instance per box; many MCP clients share it.
- **`plugins/xpp/src/XppService.Mcp/`** — .NET 9 MCP server built on
  the official `ModelContextProtocol` SDK. Speaks MCP over stdio to
  the agent; translates each tool call into a gRPC RPC against the
  service. Stateless and tiny. One process per agent session.
  Auto-spawns the service if the pipe is dead (`--no-auto-start`
  opt-out). Carries the embedded XSDs as MCP resources at
  `xpp://schema/{type}`.
- **`plugins/xpp/src/XppService.Contracts/`** — `.proto` files as
  the single contract source of truth. The C# server and clients
  generate from these via `Grpc.Tools`.
- **`plugins/xpp/src/XppService.PingProbe/`** — .NET 9 dev probe.
  Small client used to smoke-test the gRPC pipe end-to-end.

Solution file `plugins/xpp/dynamics-xpp.sln` references all five projects.

### Single source of truth: skills

Instructive authoring guidance lives entirely in
`plugins/xpp/skills/` (per-AOT-type skills + per-form-pattern skills
+ the form-subpatterns catalog). The MCP server does NOT carry a
parallel `xpp://guide/{type}` resource family — schemas yes, prose
no. This was a deliberate consolidation to avoid drift between two
documentation surfaces.

## Key invariants

1. **The bridge is the *only* process that loads
   `Microsoft.Dynamics.AX.Metadata.*`.** If you find yourself adding
   those references anywhere else, stop.
2. **`.proto` is the single source of truth for the public contract.**
   Never hand-author request/response shapes on either side. Add an
   RPC by editing the proto, regenerating, then implementing.
3. **The MCP stdio protocol requires `stdout` to carry only JSON-RPC
   frames.** Application logging goes to stderr.
   `plugins/xpp/src/XppService.Mcp/Program.cs` enforces this at the
   logging configuration layer
   (`LogToStandardErrorThreshold = LogLevel.Trace`) rather than
   relying on individual call sites — keep it that way.
4. **All search results stream.** No pagination caps. gRPC streams
   handle backpressure and cancellation natively; don't reinvent
   the result-set-size problem v1 created.
5. **One service instance per box.** Acquired via a global Windows
   mutex. Multiple MCP server processes (one per agent session)
   connect to the same service.
6. **Skills are the only home for authoring prose.** Don't add
   parallel guidance to MCP resources, comments-as-documentation in
   tool descriptions, or sibling docs that overlap with a skill.

## Build / Run

All actions go through `plugins/xpp/tools/dev.ps1`:

```powershell
cd plugins/xpp
.\tools\dev.ps1 -Action setup        # locates VS2022 D365 extension, writes per-machine BridgeReferences.props
.\tools\dev.ps1 -Action build        # dotnet build dynamics-xpp.sln
.\tools\dev.ps1 -Action run-service  # foreground the XppService
.\tools\dev.ps1 -Action run-stub     # foreground the XppService.Mcp (name is historical)
.\tools\dev.ps1 -Action test         # smoke tests: bridge, service, mcp
.\tools\dev.ps1 -Action clean        # removes bin/obj for all five projects
```

For the .NET side directly: `dotnet build plugins/xpp/dynamics-xpp.sln`.

## D365 environment context

This box is a classic Tier 1 D365 developer VM (deployed from LCS).
`PackagesLocalDirectory` lives at `J:\AosService\PackagesLocalDirectory`.
The custom-metadata-path setting defaults to the same directory.

## Status

- **Read surface** — functional. Indexed find/search/inspect, XML
  round-trip, method-source pulls all work.
- **Write surface** — functional. Bridge-mediated create/update
  via MS's own disk serializer + `FromFile` deserializer. The
  bridge is the authoritative validator; errors come back as
  structured JSON naming the failing property.
- **Skill fleet** — 23 skills covering the X++ language, every
  per-AOT-type, every form pattern, the sub-patterns catalog,
  wireframing, data manipulation, project conventions. See
  `plugins/xpp/ROADMAP.md` for patterns and tools we don't yet cover.
- **Not yet implemented** (with backlog entries — see memory files):
  - **Project tools** (`xpp_project_status` + `.rnproj` mutation +
    write-path boundaries + auto-add-to-project + out_of_model_update
    rejection) — the foundation for the next batch.
  - **`xpp_bp_check`** — per-element BP checks; mirrors MS Copilot's
    `RunBestPracticeCheck`.
  - **`xpp_compile`** — scoped compile (changeset / project / module).
  - **`xpp_db_sync`** — scoped dbsync.
  - Delete-object handler, method-source-only update path (X++ parser),
    label sub-API, SCM-aware changeset coordination.

## Project convention (when authoring through write tools)

When the user wants to use the write tools, they need to point the
MCP at their VS project. The convention is:

- `.dynamics-xpp/config.json` at the repo root (the directory the
  MCP launches from). Pointer to the active `.rnproj`, plus naming
  conventions (object prefix, extension suffix).
- `.dynamics-xpp/changeset.json` — MCP-maintained list of objects
  created/modified across sessions. Used as the scope for upcoming
  compile / dbsync / bp-check operations.

The `dynamics-xpp:project` skill is the canonical reference. The MCP enforces
"no writes without config" by returning a structured error pointing
at that skill. Read-side tools work without a project — users can
boot the MCP anywhere for research.

## Conventions

- No emojis in source code.
- Generated files (per-machine `BridgeReferences.props`, etc.) are
  not tracked; templates and `.example` files are.
- Internal docs (audits, design notes) go in `docs/`. Throwaway
  design / audit docs go in `tmp/` (gitignored).
- For test scripts or ad-hoc probes, put them in `misc/` (gitignored).
- Skills are the authoring-guidance surface. If you find yourself
  documenting "how to X" outside a skill, move it into a skill.

## Before creating D365 objects via the write tools

Consult the relevant skill — load `dynamics-xpp:class` for class authoring,
`dynamics-xpp:table` for tables, `dynamics-xpp:form` + the matching `xpp:pattern-*`
for forms, etc. The skills carry property checklists, gotchas, and
the AX 2012-vs-F&O divergence callouts.

The MCP server's `xpp://schema/{type}` resource gives you the
MS-shipped XSD as reference. It is NOT used for pre-flight
validation — the MS XSDs flatten semantic enums to `xs:string`
and diverge from what the runtime `FromFile` deserializer actually
accepts (notably for AxForm), so gating against them was the
worst of both worlds. The bridge's `FromFile` is the only
validator; errors name the offending property.
