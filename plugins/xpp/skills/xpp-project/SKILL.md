---
name: xpp-project
description: Use whenever a dynamics-xpp write operation is about to happen and the project context hasn't been established yet — first-time setup of a repo, or when the MCP returns "no project configured." Also covers the .dynamics-xpp/config.json convention, the naming conventions (object prefix + extension suffix), the .rnrproj coordination, the changeset tracking, and the out-of-model-update rejection pattern.
---

# The dynamics-xpp project convention

This skill teaches you how to set up a repository for writing F&O AOT
objects through the `dynamics-xpp` plugin, and the conventions that
flow from that setup.

**Read this skill any time:**
- The MCP returns an error like *"No `.dynamics-xpp/config.json`
  found"* or *"Project not configured."*
- The user is starting a new repo / VS solution and wants to drive
  F&O work through Claude Code.
- You need to know the object-prefix or extension-suffix conventions
  for the current project (e.g., when proposing new object names).
- The MCP returns an *"out_of_model_update"* error and you need to
  guide the user toward creating an extension instead.

---

## Why this exists

F&O development happens in a Visual Studio project (`.rnrproj`) that
bounds:
- **Which model** new objects belong to (the `.rnrproj` references
  exactly one model).
- **Which objects** the user considers "their work" — the
  `<ItemGroup>` in the `.rnrproj` lists the AOT objects the project
  is responsible for.
- **What gets compiled and DB-synced** when the user hits Build in
  VS (only what the project references).

The `dynamics-xpp` plugin coordinates with this convention. It
maintains a small per-repo config file pointing at the user's
`.rnrproj`, reads the model from there, and adds objects we create
to that project automatically. When the user flips to VS to inspect
or build, the changes we made are already there — no manual
add-to-project step.

---

## The `.dynamics-xpp/` convention

In the user's repo (the directory the MCP is launched from), the
plugin maintains:

```
.dynamics-xpp/
├── config.json         ← project pointer + naming conventions
└── changeset.json      ← objects created/modified this session and across
```

### `config.json` shape

```jsonc
{
  "version": 1,
  "rnprojPath": "ContosoRetail/ContosoRetail.rnrproj",
  "moduleName": "ContosoRetail",         // optional — defaults to <Model>
  "slnPath": "ContosoRetail.sln",        // REQUIRED — must reference rnprojPath
  "naming": {
    "objectPrefix": "con",
    "extensionSuffix": "ContosoRetail"
  },
  "bestPractices": {                    // optional — empty by default
    "suppress": ["BPXmlDocNoDocumentationComments", "BPXmlDocNoHelpfulInformation"],
    "escalate": []
  }
}
```

Fields:

- **`version`** — `1` for now. Lets us evolve the shape later
  without flying blind.
- **`rnprojPath`** — path to the `.rnrproj` file. Relative paths
  resolve from the repo root (where `.dynamics-xpp/` lives) or
  absolute. The MCP reads this on every write to know which project
  to mutate. The model name is read from inside the `.rnrproj`'s
  `<Model>` element — not duplicated here.
- **`moduleName`** — optional. The F&O module/package that hosts
  the model. Required by `xppbp.exe` / `xppc.exe`. When omitted,
  the MCP uses the model name — true for the common
  one-model-per-module convention. Set explicitly when your module
  hosts multiple models or when the module name differs from the
  model name.
- **`slnPath`** — **required.** Path to the `.sln` that `xpp_compile`
  hands to `devenv.com`. The MCP validates that this file exists AND
  that its text references the `rnprojPath`'s file name — otherwise
  the build would silently target the wrong project. There's no
  auto-discovery: if the .sln you want isn't obvious (multiple .sln
  files in the tree, or the existing .sln references several rnrprojs
  and only one is yours), create a project-local .sln that references
  ONLY the active `.rnrproj` and point `slnPath` at it. The "right"
  .sln is the one whose Project(...) lines list this rnrproj.
- **`naming.objectPrefix`** — short identifier the user prefixes
  onto new object names they author (e.g., `con` for Contoso Retail).
  Customary F&O convention; ISV solutions and customer code prefix
  everything to avoid name collisions with Microsoft and other
  parties.
- **`naming.extensionSuffix`** — what goes after the dot when
  authoring metadata extensions (`AxTableExtension` /
  `AxFormExtension` / `AxEdtExtension` / `AxEnumExtension`).
  Conventionally the model name. Also used as the infix in
  class-style extension names.
- **`bestPractices.suppress`** — optional list of BP rule monikers
  (e.g., `BPXmlDocNoDocumentationComments`) to silence in both
  `xpp_bp_check` and the BP findings surfaced by `xpp_compile`.
  Suppressed diagnostics still get counted but land in a
  `suppressed` bucket so the agent knows what's hidden. Use this
  for rules you've decided don't apply to your project. See
  `plugins/xpp/docs/bp-rules-reference.md` for the 184-rule
  roster.
- **`bestPractices.escalate`** — optional list of monikers to
  promote from Warning to Error via xppbp's
  `-TreatWarningsAsErrors`. Use for style rules you treat as
  non-negotiable. Edits to either list are picked up on the next
  tool call (no MCP restart needed; the MCP invalidates its
  config cache on file mtime change).

### `changeset.json` shape

```json
{
  "version": 1,
  "objects": [
    {
      "axType": "AxClass",
      "name": "conFooProcessor",
      "firstTouchedAt": "2026-05-20T14:30:00Z",
      "lastTouchedAt": "2026-05-20T15:00:00Z",
      "createdHere": true
    }
  ]
}
```

Maintained by the MCP. Adds/refreshes an entry on every successful
`xpp_create_object` / `xpp_update_object`. Keyed by `(axType, name)`
— multiple updates collapse to one entry. `createdHere: true`
indicates we created the object from scratch (vs. modifying an
existing one). No auto-clear; user resets explicitly via
`xpp_changeset_clear`.

---

## Naming conventions — the patterns to follow

### New objects (anything you're creating from scratch)

Prefix the object name with `naming.objectPrefix`. Examples for a
project with `objectPrefix: "con"`:

- New class: `conFooProcessor`
- New table: `conSomeTable`
- New form: `conCustomerLookup`
- New EDT: `conSpecialNumber`
- New enum: `conProcessState`
- New label file: `conLabels_en-US`

### Extending Microsoft-shipped (or other-model) objects

This is where the conventions split into two patterns — one for
metadata extensions and a different one for class-style extensions.

#### Pattern A: metadata extensions (AxTableExtension / AxFormExtension / AxEdtExtension / AxEnumExtension)

Use `<TargetName>.<extensionSuffix>`:

| Extending | Suggested name (suffix = `ContosoRetail`) |
|---|---|
| `CustTable` (table)             | `CustTable.ContosoRetail` |
| `SalesTable` (form)             | `SalesTable.ContosoRetail` |
| `CustAccount` (EDT)             | `CustAccount.ContosoRetail` |
| `NoYes` (enum, must be Extensible) | `NoYes.ContosoRetail` |

This is the MS convention for metadata-only extensions. See the
[MS naming guidelines](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/naming-guidelines-extensions).

#### Pattern B: class-style extensions (Chain of Command)

This pattern is used whenever you write **X++ code** that augments
something — whether the target is a class, a form's class, a form's
datasource, a form's control, or a table's methods. The artifact
itself is always an `AxClass` with the `[ExtensionOf(...)]`
attribute.

**Required by BP (Best Practice):** the class name MUST end with
`_Extension`. No exceptions — this is a 10-character suffix that
MS's BP-check enforces hard. Plus the class must be marked `final`.

Recommended shape: `<objectPrefix><Target>_Extension` (or with
explicit type info when the target is something other than a plain
class):

| Extending | `[ExtensionOf(...)]` token | Suggested class name (prefix = `con`) |
|---|---|---|
| Class `SomeProcessor` | `classStr(SomeProcessor)` | `conSomeProcessor_Extension` |
| Code on form `CustTable` | `formStr(CustTable)` | `conFormCustTable_Extension` |
| Code on form datasource | `formDataSourceStr(SomeForm, SomeDs)` | `conFormDataSource_SomeForm_SomeDs_Extension` |
| Code on form control | `formControlStr(SomeForm, SomeControl)` | `conFormControl_SomeForm_SomeControl_Extension` |
| Code on table (CoC methods) | `tableStr(CustTable)` | `conTableCustTable_Extension` |

Why the extra type information (`Form`, `FormDataSource`,
`FormControl`, `Table`) in the name? Because plain artifact names
aren't unique across types — a class, a form, and an enum can all
be named `SomeEntity`. Per MS: *"Don't name the extension just
`<Element>_Extension`, because the risk of conflicts is too high."*
The type token disambiguates.

Real example you can read in this codebase:
`CONFormDataSource_CaseDetail_CONSpecialOrderRequestHeader_Extension`
— Contoso Retail' extension on the `CaseDetail` form's
`CONSpecialOrderRequestHeader` datasource. Same shape as the table
above.

For the structural details of CoC (the `[ExtensionOf]` attribute,
`next` keyword, `final` requirement, sequencing patterns), see
`dynamics-xpp:xpp-class` (CoC section) and `dynamics-xpp:xpp-extension` (covers the
non-class metadata extension types).

---

## First-time setup (run this when the MCP errors with "no project")

When you encounter a `.dynamics-xpp/config.json`-missing error, walk
the user through this flow. Do it as a conversation, not a single
elicitation form.

### Step 1 — find the `.rnrproj`

Glob `*.rnrproj` in the current directory (and one level down for
the common `Solution/ProjectName/ProjectName.rnrproj` layout).
(Note: the file extension is `.rnrproj` — *not* `.rnproj`. VS 2022's
D365 extension renamed it.)

- **Exactly one found**: propose it to the user.
  *"I found `ContosoRetail/ContosoRetail.rnrproj`. Use this as the
  active project?"*
- **Zero found**: the user must either create a `.rnrproj` in VS
  first, or point at an existing one elsewhere. Surface this clearly.
- **Multiple found**: list them; let the user choose.

### Step 2 — read the model from the `.rnrproj`

Open the chosen `.rnrproj`, look for `<Model>` (it's near the top in
the project's `<PropertyGroup>`). Read its value. That's the
model the project builds against. Display it back to the user so
they can confirm it's what they expected:

> *"This project builds against the `ContosoRetail` model. Is that
> correct?"*

If the user says no, they're pointing at the wrong project — go
back to step 1.

### Step 3 — ask for the object prefix

> *"What prefix should I use when naming new objects in this
> project? This is a short identifier that goes at the start of
> every new object you create — for ContosoRetail, this is usually
> `con`. (Convention: lowercase 2-4 characters.)"*

Capture the answer.

### Step 4 — ask for the extension suffix

> *"What suffix should I use for metadata extensions
> (AxTableExtension, AxFormExtension, etc.)? This goes after the
> dot — e.g., `CustTable.<suffix>`. Conventionally the model name —
> `ContosoRetail` based on what you chose. Use that, or supply
> a different value."*

Default to the model name. Capture the answer.

### Step 5 — write `.dynamics-xpp/config.json`

Use the Write tool. Path is `.dynamics-xpp/config.json` relative
to the current directory:

```json
{
  "version": 1,
  "rnprojPath": "<from step 1>",
  "naming": {
    "objectPrefix": "<from step 3>",
    "extensionSuffix": "<from step 4>"
  }
}
```

The `.dynamics-xpp/` directory may not exist yet — create it as
part of the Write.

### Step 6 — confirm

Call `xpp_project_status`. The MCP reads the new config and reports
back the resolved project, module, model, and naming conventions.
Show this to the user so they can confirm everything looks right
before doing any actual write operations.

### Step 7 — proceed with the original task

The MCP write operation that triggered this setup can now
succeed. Retry it (or whatever the user originally asked).

---

## Write-path boundaries (enforced by the MCP)

These rules are non-negotiable; the tools refuse to do things
outside them. Understand the rules before authoring:

### Rule 1: Create only in the current model

`xpp_create_object` writes to the current project's model. No way
to override — the tool doesn't accept a `model` parameter, and the
value comes from the `.rnrproj`. If you need to create something in
a different model, the user has the wrong project active; help them
switch.

### Rule 2: Update only objects in the current model

`xpp_update_object` checks the target's model before proceeding. If
the target is in a different model (most often: a Microsoft-shipped
object in ApplicationSuite, etc.), the call is rejected with a
structured `out_of_model_update` error.

### Rule 3: Every create/update auto-adds to the active `.rnrproj`

The MCP mutates the project file's `<ItemGroup>` to include the new
or modified object. Idempotent — if the object is already
referenced, no change. The user's VS, when refocused, will see the
project diff and reload.

---

## The `out_of_model_update` rejection — how to act on it

When `xpp_update_object` rejects an out-of-model update, the
response is structured. Example:

```json
{
  "error": "out_of_model_update",
  "message": "CustTable is in 'ApplicationSuite' (a Microsoft-shipped model). The dynamics-xpp write tools only modify objects in your current project's model ('ContosoRetail'). Microsoft application models are sealed since release 8.0 — modifications go through extensions.",
  "proposed_action": {
    "approach": "Create a table extension in ContosoRetail.",
    "tool": "xpp_create_object",
    "axType": "AxTableExtension",
    "suggested_name": "CustTable.ContosoRetail",
    "name_pattern": "<original>.<extensionSuffix> — see dynamics-xpp:xpp-extension skill",
    "constraints": [
      "AxTableExtension can add fields, indexes, relations, field groups, FieldGroupExtensions, FieldModifications (label/help only). It cannot remove/rename/retype base fields.",
      "Load the dynamics-xpp:xpp-extension skill for the structural shape."
    ]
  },
  "target": { "axType": "AxTable", "name": "CustTable", "in_model": "ApplicationSuite", "current_model": "ContosoRetail" }
}
```

What you do:

1. **Read the proposed_action.** It tells you the right `axType`,
   the suggested name, and any structural constraints.
2. **Tell the user** what you're switching to and why. The user
   needs to understand the model-sealing rule and the extension
   approach.
3. **Construct the new XML** using the relevant skill — load
   `dynamics-xpp:xpp-extension` for metadata extensions or `dynamics-xpp:xpp-class` (CoC
   section) for code extensions.
4. **Call `xpp_create_object`** with the suggested `axType` and
   name (or a name the user prefers — the suggestion is mechanical;
   feel free to adjust if there's a domain reason).
5. **The XML body needs to express your intent as additions to
   the base** — fields you want to add, relations, etc. — NOT a
   full rewrite of the base object.

The MCP can compute the *header* of the proposed call (correct
axType, suggested name, constraint reminders). It can't compute
the *content* — that's your reasoning step.

---

## After-the-write workflow — `xpp_bp_check` + `xpp_compile`

The plugin ships two project-aware feedback tools the agent uses
to close the loop on changes:

### `xpp_bp_check` — cheap per-change feedback

Runs F&O's `xppbp.exe` against the requested scope and returns
structured diagnostics. The same checker MS Build runs; output is
identical to what VS Build's BPC step surfaces.

Scopes:
- `scope: "changeset"` (default) — every object the MCP has
  touched this session and across sessions. Use this in the inner
  loop after an `xpp_update_object` / `xpp_create_object`.
- `scope: "project"` — every `<Content>` in the active
  `.rnrproj`. Heavier; use when you suspect drift from manual VS
  edits or before a release.
- `scope: "explicit"` — caller-supplied `objects: [{axType, name}]`.
  Use for surgical re-checks.

Output is **summary-by-default**: Errors come through in full,
Warnings + Informational collapse to per-moniker counts with
distinct-element counts and a sample message. Drill into a
specific rule with `monikers: ["BPLocalVariableNotUsed", ...]` —
xppbp filters at the source via `-rules=`, runs faster, and the
project's suppress list is bypassed for those monikers. Or pass
`verbosity: "full"` for the polish pass.

The `suppressed` bucket is always non-empty when the project's
`bestPractices.suppress` list is in play — that's a feature: the
agent should know what was hidden by policy.

### `xpp_compile` — VS-equivalent build

Drives `devenv.com /Build` on the `.sln` configured as `slnPath`
in `.dynamics-xpp/config.json`. The config validator enforces
that the configured `.sln` references the active `.rnrproj`, so
the compile is always targeting the right project. Mirrors the
user's VS Build pipeline exactly: metadata validation → X++
compile (via the long-running `xppcAgent` VS already uses) → BP
check → CopyReferences → app pool recycle.

Confirm the resolved `slnPath` via `xpp_project_status` before
the first compile in a new repo — if it's pointing at an
unrelated .sln, the .dynamics-xpp config needs a fix before
`xpp_compile` will produce meaningful output.

```
xpp_compile()                  // /Build, incremental
xpp_compile(rebuild=true)      // /Rebuild, forces fresh diagnostics
```

Output shape mirrors `xpp_bp_check` (errors full, warnings
per-moniker, `verbosity: "full"` for everything) and adds
per-step timings:

```jsonc
"timing": {
  "metadataValidationMs": 3073,
  "xppCompileMs": 8488,
  "bpCheckMs": 2952,
  "appPoolRecycleMs": 3098,
  "elapsedMs": 33866
}
```

Cold-start tax is ~14s (devenv loading). With nothing to compile
the build is `upToDate: true` and finishes in ~17s; a real
/Rebuild on a small project is ~30-35s.

**When to call:** at meaningful checkpoints, not after every edit.
Use `xpp_bp_check(scope=changeset)` as the cheap inner-loop signal;
reserve `xpp_compile` for "I'm done with this work item — does it
actually build?"

### Complementarity

The two tools surface different things and you typically want
both:

- **`xpp_bp_check`** runs the full 184-rule BP roster.
- **`xpp_compile`** runs the X++ compiler (which catches things
  BP can't, like type-conversion narrowing, ExtensibleEnum
  numerical hazards, ChainOfCommand violations, missing CoC
  `next` calls) plus a SUBSET of BP rules (looks like just the
  AppChecker set). Also emits `TaskListItem` diagnostics for
  TODO comments in method bodies.

A clean BP check doesn't mean the build will pass; a clean
compile doesn't mean BP is happy. Run both.

---

## Database sync via `<DBSyncInBuild>` in the `.rnrproj`

There is no separate `xpp_db_sync` tool. The `.rnrproj` carries a
property that controls whether `Build` triggers a database sync:

```xml
<PropertyGroup>
  ...
  <DBSyncInBuild>False</DBSyncInBuild>
</PropertyGroup>
```

When this is `True`, `xpp_compile` (which goes through
`devenv.com /Build`) also runs the DB sync step automatically as
part of the build pipeline — same behavior the user gets in VS.
Tables/views that have schema changes get their SQL re-created;
no separate command needed.

**When to recommend flipping it:**

- **`True`** when the agent is iterating on tables, views, or
  anything else with a SQL schema footprint. The compile + sync
  combo means schema changes land immediately, surfaced as
  build-time errors if anything's broken.
- **`False`** for pure X++ / class / form work that doesn't
  touch schema. DBSync is the slowest step in a Build; skipping
  it is a meaningful speedup when you don't need it.

The user controls this via VS's project Properties dialog, or by
editing the `.rnrproj` directly (it's MSBuild XML — the property
lives in the first `<PropertyGroup>` block). If they want the
agent to manage it, suggest they flip it deliberately based on
the kind of work they're starting.

---

## When the user wants to operate without a project

The read-side surface (`xpp_find_object`, `xpp_search_code`,
`xpp_get_object_xml`, `xpp_get_object_methods`,
`xpp_get_method_source`, `xpp_find_references`, etc.) does NOT
require a project. The user can boot the MCP anywhere — in their
home directory, in a research scratch folder, wherever — and use
the read tools freely. This is the right way to use the plugin for
investigation, learning, or read-only inspection.

Only the **write path** requires a project:
`xpp_create_object`, `xpp_update_object`, `xpp_project_*`,
`xpp_changeset_clear`, `xpp_bp_check`, `xpp_compile`, and the
label CRUD tools all key off the resolved config. If the user
wants to switch from research mode to authoring mode, that's
when this skill's setup flow kicks in.

---

## `xpp_project_status` — what it tells you

Call this any time you need to know the project state. It returns
JSON with:

- **`configured`** — boolean. False if `.dynamics-xpp/config.json`
  is missing.
- **`rnprojPath`** — resolved path (absolute) to the active
  `.rnrproj`.
- **`module`** — F&O module/package the project lives under.
- **`model`** — model the project targets (read from the
  `.rnrproj`).
- **`naming.objectPrefix`** / **`naming.extensionSuffix`** —
  current conventions.
- **`changeset`** — summary: `created` count, `modified` count,
  and a list of the most-recently-touched object names.
- **`projectObjectCount`** — how many AOT items the `.rnrproj`
  currently references.

Show the user a digestible summary when they ask "what's the state
of things?"

---

## Changing the project mid-stream

If the user wants to switch projects (e.g., they were working in
ContosoRetail, now they want to switch to a different model in the
same repo):

1. Have them tell you which `.rnrproj` to switch to.
2. **Don't auto-clear the changeset.** Ask: *"Switching from
   `ContosoRetail.rnrproj` to `Other.rnrproj`. You have N objects in
   the current changeset for ContosoRetail. Should I clear them?"*
3. Write the new `rnprojPath` to `config.json`. Re-read the model;
   if it differs from before, the `extensionSuffix` should
   probably change too — ask the user to confirm or supply a new
   one.

This is uncommon but worth knowing how to handle.

---

## Things that aren't tracked

- **Manual edits made in VS.** If the user opens the form
  designer and changes a field, the MCP doesn't see that — only
  agent-driven changes via `xpp_create_object` / `xpp_update_object`
  hit the changeset. When in doubt, run `xpp_bp_check(scope="project")`
  rather than the default changeset scope so VS-side edits are
  included. `xpp_compile` is project-scoped by design so manual
  VS edits never get missed there.
- **Deletes and renames.** Use `xpp_delete_object` to delete (removes
  on-disk XML + .rnrproj entry + changeset entry; runs `tf delete`
  when TFVC is configured). Refuses by default when inbound references
  are present in the index; pass `force=true` to override. Use
  `xpp_rename_object` to rename (moves XML, updates the inner `<Name>`
  element AND the within-file class declaration, rewrites the .rnrproj,
  records the rename in the changeset). **Neither tool rewrites X++
  source-code references in OTHER files** — that's still on the
  caller. Both tools refuse with `out_of_model_mutation` when the
  target lives in a model different from the active project.
- **Source-control state.** v1 doesn't coordinate with TFS or git
  for change-set discovery. The MCP's tracking is the truth for
  v1. Backlog item: integrate with source-control to enrich
  scope-of-work understanding.

---

## See also

- `dynamics-xpp:xpp-language` — anchor skill. Has a dispatch entry pointing
  back here when the MCP isn't configured.
- `dynamics-xpp:xpp-class` — Chain of Command extension section (Pattern B
  above). Required reading when proposed_action points at an
  AxClass extension.
- `dynamics-xpp:xpp-extension` — non-class metadata extensions (Pattern A
  above). Required reading when proposed_action points at an
  AxTableExtension / AxFormExtension / AxEdtExtension /
  AxEnumExtension.
- `dynamics-xpp:xpp-setup` — once-per-machine plugin install (VS extension
  discovery, BridgeReferences.props, build). Different lifecycle
  stage than this skill.
- [MS naming guidelines for extensions](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/naming-guidelines-extensions)
  — authoritative source for the conventions above.
