# Changelog

Notable changes to the `dynamics-xpp` plugin.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the
version is below 1.0, the tool and skill surfaces may still shift between minor
releases.

## [Unreleased]

### Fixed

- **Missing required arguments are named.** A tool call that omits a
  required argument now returns `missing_argument` with the required and
  optional lists, instead of the SDK's contentless "An error occurred
  invoking".
- **`xpp_get_object_methods` / `xpp_get_method_source` no longer require
  `model`.** It is resolved from the index; the tools ask for it only when
  the same name exists in more than one model, and a wrong `axType` kind
  returns `not_found` with guidance.
- **`xpp_search_code` auto-quotes code fragments.** A query FTS5 rejects
  (`fieldNum(CustTable, AccountNum)`, `a, b`) is re-run as a phrase and the
  response says so in `note`; if even that fails the error explains the
  remedy.
- **Language skill:** `str2con` converts all-digit segments to int64 by
  default, so `conPeek(...) == '1'` is silently never true.

## [0.4.1] - 2026-10-08

### Fixed

- **Unknown argument and property names are refused instead of ignored.**
  Every tool call is now checked against the tool's parameters and, for
  domain requests, against the record types at every nesting depth. A
  guessed key (`menuItemType` for `kind`, `relation` for a form link's
  `name`, `id` for `labelId`) returns `unknown_argument` /
  `unknown_property` naming the offender and the valid names, and nothing
  is written. Previously the key was dropped silently and the value fell
  back to its default; the failure then surfaced at runtime in the
  browser. Keys starting with `_` (the examples' `_doc`) are ignored.
- **`xpp_create_query` now emits the default `classDeclaration`** its
  description always promised. A query without one does not fail
  compilation, it crashes the X++ compiler.
- **Computed view fields:** the schema pointed authors at `method`
  ("required") and away from `viewMethod` ("rarely used"). The reality is
  the reverse; both descriptions now say which class each resolves
  against, and a write-time warning fires when a computed field binds
  through `method`.
- **Form data-source field-level methods** (`modified()` on a field under
  `sourceCode.dataSources[].fields[].methods`) were dropped when the field
  had no metadata entry. The mapper now creates the entry.
- **`xpp_find_in_object` on an `AxFormExtension`** never matched the real
  control nested under each `controls[].formControl` envelope, so a zero
  read as "absent". Identifiable singleton nodes are matched now.
- **False drift reports:** the typed drift detector compared collection
  items by position while the mapper re-sorts data controls into design
  order, so an append accused unrelated controls of losing methods they
  still had. Items are now matched by identity (name / dataField).
- **Whole-file re-indentation on form patch:** every pre-existing method
  used to pass through the indentation normalizer, turning a one-method
  append into a 400-line diff. Only methods the request supplied are
  normalized now.
- **Drift is failure-shaped:** a write with non-empty `drift` now leads
  with `incomplete: true` and a warning, instead of `created: true` with a
  note at the end.
- **`xpp_create_menuitem`** requires `kind` and refuses Display/Output items
  that target a class.
- **`xpp_compile`** gains `recycleAppPool` (recycle the AOSService app pool
  after a successful build) and two hints: validation diagnostics on a
  successful build may be ordering artifacts (metadata validation runs
  before the X++ compile), and metadata-only changes are not live in the
  AOS until the app pool recycles.
- **Workspace example** (`xpp-pattern-workspace-operational`) rewritten
  from a live read of a shipped workspace; the old one used pattern names
  that exist nowhere on disk.

### Added

- **Workspace charts skill.** New `dynamics-xpp:xpp-workspace-charts`
  covers chart tiles in the Summary section and the Section Stacked Chart
  page, reverse-engineered from shipped workspaces: the `SysChart`
  control's extension-component shape (data sets, measures, secondary
  axes, chart types), the `HubPartChart` form part, the `FormPartControl`
  host, data-population idioms, page-filter wiring, drill-through, and the
  pitfalls Microsoft's own forms work around. Ships two typed examples
  ready for `xpp_create_form` / `xpp_patch_by_path`.

## [0.4.0] - 2026-10-08

### Added

- **AxDB SQL access.** The plugin's `.mcp.json` now also registers
  Methodify.SqlMcp as a standard MCP server connected to the local AxDB,
  so an agent can query the database next to the metadata tools.

## [0.3.1] - 2026-09-14

### Fixed

- **Debug bridge never built by `dt setup` / plugin install.** The installed
  plugin runs from Claude Code's cache copy, which is built only by the MCP
  server's `dotnet run`; `XppDebugBridge` was missing from that build graph,
  so every first `xpp_debug_attach` failed with "XppDebugBridge.exe is not
  built". It is now a build-only reference of the MCP project, and the error
  (should it ever recur) names the one `dotnet build` command that works on
  a stock D365 box. `xpp_debug_status` reports `bridgeAvailable` up front.
- **Bridge deadlock while paused.** Setting a breakpoint while the target
  was paused opened its document in the hidden Visual Studio, which never
  returned in break mode; every later call (detach included) queued behind
  it and the AOS stayed frozen. Breakpoints now bind without opening a
  document, the bridge serves requests concurrently, every automation call
  is bounded, and the watchdog runs independently of the request path.
  `xpp_debug_detach force=true` is a separate short path that never waits
  behind a stuck call: one 5-second graceful attempt, then the hidden VS is
  killed. Killing the debugger also terminates the target process (IIS or the
  batch service restarts it, sessions on the AOS are lost); the result says
  so with `targetTerminated` and a `note`, and the skill tells the agent to
  relay the cost.

## [0.3.0] - 2026-09-14

### Added

- **Live X++ debugging.** New `xpp_debug_*` tools attach Visual Studio's
  managed debugger — hosted invisibly by the plugin service — to the AOS worker
  (`aos`) or the batch host (`batch`), set breakpoints by *method*
  (`AxClass PriceDisc findPrice`, with an optional X++ condition), wait for a
  hit, and return the X++ call stack with file:line and source text, `this`,
  arguments, locals and watch expressions; then evaluate expressions, step
  over/into/out, continue, and detach. The service generates the `.xpp`
  source the debugger binds against straight from the AOT XML, so any class,
  table, view, entity, query or map can be debugged whether or not anyone has
  opened it in VS. Guard rails: one session per box, breakpoints deleted on
  detach, and a watchdog that resumes a hit left paused too long so the AOS is
  never left frozen. Requires Claude Code to run elevated (the F&O processes
  run as NETWORK SERVICE). Forms and extension elements can't take breakpoints
  yet — break in a class/table method on the same path. See the new
  `dynamics-xpp:xpp-debug` skill.

## [0.2.0] - 2026-09-01

The headline is that **the plugin now keeps itself current**. Before this
release, one machine ran one background service, and whichever Claude session
started first owned it — so after updating the plugin you could keep running
last week's code indefinitely, with nothing to tell you. There is also a new
`dt` command-line companion for the things you do outside a session.

### Added

- **`dt`, a command-line companion.** `dt setup` does the whole first run —
  locates your D365 metadata assemblies, builds, and puts `dt` on your PATH.
  After that: `dt status` (is the bridge healthy, how far has the index got,
  how many embeddings are built), `dt version`, `dt update`,
  `dt service stop|restart`, `dt cache clear`. See the README for details.
- **Automatic service upgrades ("newest wins").** A session running a newer
  plugin build asks the older background service to stand down — gracefully,
  draining work in flight and checkpointing the index — then starts its own.
  An older session leaves a newer service alone and simply uses it. In
  practice: after `dt update`, existing sessions keep working as they are, and
  your next new session picks up the new build on its own. You should rarely
  need `dt service restart`.
- **A skill for authoring custom form controls** (`dynamics-xpp:custom-control`):
  the `FormTemplateControl` + `FormBuildControl` + data-contract + HTML/JS/CSS
  resource set, the React path, the three-tier extensibility model, host-form
  overrides, and the design-time wiring.
- **A skill for batch jobs** (`dynamics-xpp:batch`): the SysOperation
  contract/service/controller triple as the modern replacement for
  `RunBaseBatch`, execution modes and `mustGoBatch`, and parallel workers via
  `BatchHeader.addRuntimeTask` / `addDependency`.
- **Service-operation privilege entry points** are now expressible in the typed
  surface: `ObjectType` gains `ServiceOperation`, alongside a new
  `objectChildName` for the operation. Previously this dead-ended in raw XML.

### Changed

- **The service refuses to start against an index cache written by a newer
  build**, instead of running against it and corrupting the index. It names
  both versions and gives you the two ways forward. This is deliberately not
  self-healing: discarding the cache costs a full re-index and re-embedding, and
  the usual cause (a stale session) is much cheaper to fix.
- **`xpp_compile` distinguishes fatal from advisory diagnostics.** It used to
  report `success: true` next to `errorCount: 5` with no way to tell which
  errors actually failed the build. Now `buildErrors` are fatal,
  `validationDiagnostics` are advisory, and `errorsFailedBuild` says which
  happened. `errorCount` is unchanged, for compatibility.
- **`patch_by_path` with `op=append` accepts an array of members.** Building a
  wide class or a control tree used to cost one round trip per member. A bad
  member still rejects the whole batch, so you can't get a partial append.
- **Skills now take a position where the platform offers a legacy and a modern
  way of doing something.** Where a skill was silent, agents defaulted to
  whatever is most common in the X++ corpus — which on a decade-old platform is
  reliably the older approach. Most consequentially, table delete behaviour now
  leads with relation `OnDelete` and demotes the legacy `DeleteActions` block,
  and new batch jobs steer to SysOperation.

### Fixed

- **The metadata store is now discovered, not assumed to be on `J:`.** The
  packages directory was hardcoded, so the plugin simply did not work on a dev
  box whose LCS deployment put it on another drive. Setup now finds it from the
  AOS's own `web.config`, from `DynamicsDevConfig.xml`, or by scanning fixed
  drives, validates the candidate before accepting it, and records the answer
  where the service reads it. The service can also discover it unaided if that
  record is missing. Every rung is reported, so a failure says what was tried.
- **`xpp_delete_object` reported success while leaving the file on disk.** When
  a project had no `scm` block, the delete silently did nothing but claimed it
  had worked. `fileRemoved` is now a true post-condition, and if the file
  survives, the object is no longer stripped from the project, changeset and
  index — which used to leave it invisible but live.
- **Label files landed half-declared in the `.rnrproj`**, forcing an
  exclude-and-re-add-from-AOT dance in Visual Studio. The paired
  `<id>.<lang>.label.txt` entry is now written (and removed) alongside the
  descriptor.
- **`viewMetadata` relation fields were documented backwards.** `Field` resolves
  against the `JoinDataSource` and `RelatedField` against the embedded data
  source, not the other way round. Affects `create_entity` and `create_query`.
- **An invalid privilege `objectType` is now rejected rather than silently
  dropped**, which used to produce a typeless entry point.
- **A service startup failure could crash with the real cause buried.** An
  internal double-dispose turned any failure during startup into an unhandled
  exception that masked whatever actually went wrong.
- **A form's `FormDesignPropertyDataMethod`** validates against the table only —
  the three-method-homes rule does not extend to it. Corrected in the form skill.

## [0.1.0] - 2026-06-11

Initial public release: the `dynamics-xpp` plugin for D365 F&O X++ development —
the skill fleet plus the MCP server's read and write surfaces.

[0.3.0]: https://github.com/yobryon/dynamics-tools/compare/4679d76...main
[0.2.0]: https://github.com/yobryon/dynamics-tools/compare/102b587...4679d76
[0.1.0]: https://github.com/yobryon/dynamics-tools/commit/b8d2aed
