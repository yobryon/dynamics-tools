---
name: xpp-scm-tfvc
description: TRIGGER when the user is doing D365 work in a TFVC (Azure DevOps-backed Team Foundation Version Control) workspace and asks anything about pending changes, check-in workflow, "did my changes get saved to SCM," merging, or .tfignore curation. D365 F&O dev is overwhelmingly TFVC because LCS / DevBox tooling assumes it; this skill captures the conventions, the agent-visible tools, and how the write-side MCP integration auto-checks-out files when configured.
---

# TFVC integration

D365 F&O developer workspaces are almost always **TFVC** — Team Foundation
Version Control, the centralized SCM that Azure DevOps still backs even
though MS pushed git for everything else. F&O tooling (LCS, the VS D365
extension) writes the metadata directly into the workspace folder, and
authoring tools expect the SCM to be aware of it.

## When to load this skill

- The user asks about pending changes, check-in, conflicts, or "did the
  agent's writes land in source control."
- The user mentions `tf`, TFVC, "the workspace," `.tfignore`, or DevOps.
- You're about to author code in a project that has SCM configured in
  `.dynamics-xpp/config.json` and want to understand what the MCP is
  going to do behind the scenes.
- The user asks for help curating `.tfignore`.

## The mental model

- **Workspace mapping**: a server path (e.g.
  `$/CON D365 Finance and Operations/Trunk/Dev/Metadata`) is mapped to a
  local folder (e.g. `J:\AosService\PackagesLocalDirectory`). That local
  folder IS the F&O metadata path — the same directory the bridge writes
  into when the agent authors an object.
- **Files are read-only by default**. TFVC marks every tracked file
  read-only on disk until you run `tf checkout`. Trying to overwrite a
  read-only file via the bridge fails with an access-denied error. The
  agent-driven workflow has to call `tf checkout` first.
- **New files need `tf add`**. Creating a new `.xml` puts it on disk but
  doesn't put it in source control. `tf add` registers it.
- **No branching here**. D365 dev usually works directly against a Dev
  branch; there's no "feature branch / merge to main" git ritual.
  Check-in is the unit of work.

## Agent-visible tools

The plugin ships three TFVC tools alongside the auto-actions:

| Tool | What it does |
|---|---|
| `xpp_scm_status` | Wraps `tf status` for the metadata workspace. Returns the pending-changes list (add / edit / delete) as structured JSON. Side-effect-free. |
| `xpp_scm_audit` | Diffs the agent's `.dynamics-xpp/changeset.json` against `tf status`. Surfaces (a) TFS-side changes the agent doesn't know about (VS-edits, pre-existing) and (b) changeset entries that have no matching TFS pending change (writes that didn't land). |
| `xpp_scm_checkout` | Explicit checkout for a batch of paths. Use when about to author through tools that don't auto-checkout (rare — `xpp_create_*` / `xpp_patch_*` auto-checkout when SCM is configured). |

## How write-side auto-actions work

When `.dynamics-xpp/config.json` declares an `scm` block, every domain
mutation (`xpp_create_*`, `xpp_patch_*`) automatically:

1. **Computes the on-disk file path** from convention:
   `<metadataPath>\<Model>\<Module>\<AxType>\<Name>.xml`.
2. **Before the bridge writes** (patch only — for create the file doesn't
   exist yet): runs `tf checkout <path>`. Idempotent — already-checked-out
   is a no-op.
3. **After the bridge writes successfully** (create only): runs
   `tf add <path>` if the file isn't already tracked. Idempotent.

The MCP tool response carries `sideEffectWarnings` whenever an SCM op
fails non-fatally — auth cache stale, file locked by another user,
workspace not mapped, etc. The bridge write itself isn't blocked — the
agent sees the structured warning and can reason about it.

## Configuring SCM

Add to `.dynamics-xpp/config.json`:

```json
{
  "rnprojPath": "...",
  "scm": {
    "kind": "tfvc",
    "metadataPath": "J:\\AosService\\PackagesLocalDirectory",
    "tfExePath": null
  }
}
```

- `kind`: `"tfvc"` is the only supported value today. Future flavors
  (`"git"`, `"none"`) may be added.
- `metadataPath`: the local root of the TFVC workspace, i.e. the F&O
  `PackagesLocalDirectory`. Required for SCM ops.
- `tfExePath`: optional override. When `null`, the MCP discovers
  `tf.exe` under the latest VS2022 install.

Omitting the `scm` block entirely disables all auto-actions. SCM tools
(`xpp_scm_status` etc.) still error cleanly, telling the agent to
configure SCM before retrying.

## Authentication

`tf.exe` shares its credential cache with Visual Studio. If the user has
opened VS recently and signed in to Azure DevOps, `tf.exe` invoked from
the MCP rides on the same cache. Auth failures surface as:

> TF30063: You are not authorized to access dev.azure.com/&lt;org&gt;.

When this happens, the typical remedy is:
1. Open Visual Studio.
2. Open Team Explorer ("Manage Connections").
3. Connect to the DevOps org and authenticate (browser prompt).
4. Re-run the agent's operation.

The MCP doesn't manage credentials itself.

## Error-handling philosophy

Auto-actions follow **try-then-surface**: transient errors are retried
(or treated as no-op when idempotent), real failures surface as
structured warnings without blocking the underlying write. The agent
reasons about them.

Common cases:

| Situation | Behavior |
|---|---|
| File already checked out by THIS user | No-op (success). |
| File checked out by another user, locked | `sideEffectWarnings: ["tfvc_checkout_locked: <user> since <date>"]`. The bridge write still happens (it'll fail with access-denied; the agent sees that too). |
| File checked out by another user, NOT locked | TFVC's "shared checkout" model: still allowed. Proceeds, with a warning so the agent knows there's a parallel edit. |
| `tf.exe` not found | `sideEffectWarnings: ["tfvc_not_configured: tf.exe not located"]`. Bridge write proceeds; no auto-checkout. |
| Auth failure (TF30063) | `sideEffectWarnings: ["tfvc_auth_failed: <user> not signed in to <collection>"]`. Bridge write proceeds. |
| Workspace not mapped | `sideEffectWarnings: ["tfvc_workspace_not_mapped: <path>"]`. Bridge write proceeds. |
| File didn't exist when adding | `sideEffectWarnings: ["tfvc_add_missing_file: <path>"]`. Indicates the bridge write thinks it created the file but it isn't on disk yet — unusual, worth surfacing. |

## .tfignore curation — agent-driven, not tool-driven

This plugin **does not** ship a `xpp_scm_init_tfignore` tool. Tfignore
patterns are too context-sensitive for a one-size template, and the
patterns benefit from agent reasoning more than automation. Use the
existing Read / Glob / Write tools to author the file directly, guided
by the patterns below.

### The three pattern families to capture

1. **Whole-module exclusions** — every Microsoft-shipped model whose
   contents we DON'T own. F&O VMs deploy these wholesale; nothing we do
   touches them, so they shouldn't be in our SCM history.
2. **Per-developed-model noise pruning** — for modules we actively
   develop (the customer's own module, ISV modules with source), the
   subdirectories that hold transient compilation / packaging artifacts
   should be excluded so they don't pollute pending-changes.
3. **Generated-file patterns** — files dropped anywhere by the platform
   that should never be checked in (build receipts, etc.).

### How to think about each module

Walk the metadata path with `Glob`/`Read` and classify each top-level
directory:

| Classification | Examples | .tfignore action |
|---|---|---|
| **Microsoft-shipped** | ApplicationSuite, Foundation, GeneralLedger, Retail, Tax, WarehouseManagement, ApplicationFoundation, ApplicationPlatform, etc. | Add the directory name as a line. |
| **ISV-with-source** | Atl* (CIS/Atlas verticals), bundled third-party modules where source is provided but we don't author into | Add the directory name (we don't modify it, so we don't track it). Confirm with the user. |
| **ISV-binary-only** | Most third-party paid modules — deployable-package only, no source on disk | Usually no directory present in the metadata path; nothing to do. |
| **Customer-developed** | Customer's own module(s) (e.g. ContosoRetail, Fabrikam) | DO NOT add to .tfignore; instead, drill into the model's subdirectories and exclude transient ones (see below). |

### Subdirectories to exclude inside actively-developed modules

The following land under `<Model>/<Module>/<Subdir>` and are transient:

- **`XppMetadata`** — compiled-metadata cache; regenerated on every build.
- **`Resources`** — deployment-time resources baked from BuildModelResult / packaging.
- **`bin`** — module binaries from compile output.
- **`Reports`** — SSRS deployable artifacts.

Add per-model:
```
<Model>/Reports
<Model>/Resources
<Model>/XppMetadata
<Model>/bin
```

### Generated file patterns to exclude anywhere

```
BuildModelResult.*
BuildProjectResult.*
CompileLabels.xml
DBsynchronization.xml
GeneratedXppSource
```

### Putting it together

A representative `.tfignore` at the metadata path root looks like:

```
# === Microsoft-shipped modules — never tracked ===
AccountsPayableMobile
AdvancedQualityManagement
ApplicationCommon
ApplicationFoundation
... (~150 lines total)

# === ISV-with-source — confirmed with user as read-only ===
AtlApplicationSuite
AtlCostAccounting
... (per-vertical)

# === Customer modules — noise pruning ===
ContosoRetail/Reports
ContosoRetail/Resources
ContosoRetail/XppMetadata
ContosoRetail/bin

# === Universal transient artifacts ===
BuildModelResult.*
BuildProjectResult.*
CompileLabels.xml
DBsynchronization.xml
GeneratedXppSource
```

### Validation workflow

After editing `.tfignore`:

1. Run `xpp_scm_status` to see pending changes.
2. The .tfignore takes effect immediately — any newly-excluded path drops
   out of the status.
3. If a path you EXPECTED to be ignored is still listed: TFVC ignores
   `.tfignore` patterns for files that are ALREADY tracked. Use
   `tf delete /keep` on the local-only side to untrack without deleting.
4. Confirm the user is happy with the diff before suggesting check-in.

## Common workflows

### Daily authoring

The agent doesn't need to think about SCM at all when authoring through
typed tools (`xpp_create_*`, `xpp_patch_*`). The MCP auto-checks-out
before edits and auto-adds after creates. Surface
`sideEffectWarnings` from tool responses to the user when non-empty.

### "Show me what's pending"

```
xpp_scm_status
```

Returns the structured pending changes. Format the response as a table
grouped by action (add / edit / delete) for human consumption.

### Reconciliation: agent vs TFS

```
xpp_scm_audit                     # audit-only
xpp_scm_audit(autoFix=true)       # tf-add anything missing
```

Surfaces drift between the agent's `changeset.json` and TFS pending
state. Common causes of drift:

- **Orphan on-disk files**: a session ran with SCM unconfigured (no
  `scm` block in `.dynamics-xpp/config.json`), so file creates landed
  on disk but were never `tf add`'d. The `orphanFiles` array in the
  audit response lists them. Pass `autoFix=true` to tf-add the lot.
- **Agent-tracked but not pending**: the agent's `changeset.json`
  records a write but TFS has no pending change. Same root cause
  (unconfigured SCM) or a bridge error the agent didn't surface.
  Same fix.
- **VS-side edits**: the user opened a form in the designer and saved
  changes. Not in the agent's changeset (`unknownToAgent` array).
  Offer to fold them in via `xpp_project_add_object`.
- **Pre-agent pending changes**: the user already had pending changes
  before the agent session began. Same remediation as VS-side edits.

`autoFix=true` calls `tf add` for every file in `agentTrackedButNotPending`
and `orphanFiles`. It also folds canonical-layout on-disk files into
`changeset.json` so subsequent audits see them consistently. Safe to
re-run — `tf add` is idempotent.

### What happens if SCM isn't configured at all

Every `xpp_create_*` / `xpp_patch_*` / raw `xpp_create_object` returns
a **loud `scm_not_configured` warning in `sideEffectWarnings`** when
the project's config lacks an `scm` block. The agent should treat
this as a setup error — files are being written but NOT tracked.
Add the `scm` block to `.dynamics-xpp/config.json` and run
`xpp_scm_audit(autoFix=true)` to recover the existing writes.

### Resolving a locked file

```
tf status /collection:https://dev.azure.com/<org> <localpath>
```

(via Bash / shell, not an MCP tool) — shows who has the file locked. A
stale 2-year-old lock is usually safe to bypass (the locking user is no
longer active); a recent lock from a current team member is a
coordination call.

Use `tf undo /workspace:<workspace>;<user>` (with admin rights) only
after confirming with the user.

## See also

- `dynamics-xpp:xpp-project` — `.dynamics-xpp/config.json` schema and the
  changeset.json contract that the agent maintains for compile / BPC
  scope. SCM tools layer on top of this without replacing it.
- `tf.exe` documentation: https://learn.microsoft.com/en-us/azure/devops/repos/tfvc/use-team-foundation-version-control-commands
