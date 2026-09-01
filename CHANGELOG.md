# Changelog

Notable changes to the `dynamics-xpp` plugin.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the
version is below 1.0, the tool and skill surfaces may still shift between minor
releases.

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

[0.2.0]: https://github.com/yobryon/dynamics-tools/compare/102b587...main
[0.1.0]: https://github.com/yobryon/dynamics-tools/commit/b8d2aed
