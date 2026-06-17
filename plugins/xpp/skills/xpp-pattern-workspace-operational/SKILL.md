---
name: xpp-pattern-workspace-operational
description: Use when authoring a WorkspaceOperational-pattern form in D365 F&O — an activity-focused workspace landing page. As of 10.0.25 the pattern was reorganized to scroll VERTICALLY (no more panorama / horizontal scrolling); content sections are now stacked vertically and use restyled FastTabs. Two variants — standard Operational workspace and Operational workspace w/Tabs (10.0.25+) for organizing into multiple top-level sections.
---

# Authoring a `WorkspaceOperational` form

A **workspace** is the primary way users navigate to tasks and
specific pages. Per MS:

> *"A workspace should be created for every significant business
> 'activity' that you want to support. An 'activity' is less granular
> than a task but more granular than a legacy 'area page.' A
> workspace is intended to provide a one-page overview of the
> activity and to help users understand the current status, upcoming
> workload, and performance of the process or user."*

Load `dynamics-xpp:xpp-form` first if you haven't.

---

## Three workspace patterns — only two current

| Pattern | Status | Use it? |
|---|---|---|
| **Operational workspace** | Current default | YES — standard pattern |
| **Operational workspace w/Tabs** | Current (added 10.0.25) | YES — when you need multiple top-level tabs (e.g. embedded Power BI) |
| **Tabbed workspace** | Deprecated | NO — replace with Operational workspace w/Tabs |
| **Workspace** (old) | OBSOLETE — can't be used after 10.0.25 | NO — migrate away (see Migration below) |

The old "Workspace" pattern used panorama controls with horizontal
scrolling. The modern Operational workspace replaced that with
vertical FastTab-based layout.

---

## When to pick each variant

### Operational workspace (default)

Use for the typical case: one workspace = one activity. Single
top-level section structure: action pane → optional filter group →
FastTabs with summary tiles, tabbed lists, optional charts, optional
PowerBI, and related links.

### Operational workspace w/Tabs (10.0.25+)

Use when you need **multiple top-level sections** within one
workspace — e.g. an "Operations" tab + an "Analytics" tab + a
"Setup" tab, each containing its own Operational-workspace-style
layout, links section, or custom content like embedded Power BI
reports.

---

## When to pick WorkspaceOperational

| Use WorkspaceOperational when... | Use ... instead |
|---|---|
| Building a role-specific landing page with tiles, lists, charts | — |
| The form is a navigation hub, not a data-entry surface | — |
| Multiple disparate views need to live on one screen | — |
| Single-purpose data entry / browsing | Any other pattern |
| Generic "dashboard" without role-specific intent | Reconsider — most "dashboards" become better-shaped Task or ListPage forms |

---

## Distinguishing characteristic: no datasource

WorkspaceOperational forms usually have **no datasource**. The form
itself is purely navigation + aggregation; the data lives in the
sections it embeds (tile counts, list-fact-boxes, charts) and is
pulled by those sub-components, not by the workspace shell.

This is a strong signal: if your workspace would have a datasource,
you're probably authoring a ListPage or DetailsMaster instead.

---

## Structural shape (per MS official model)

### Operational workspace (basic)

```
Design
├── Action pane (ActionPane) [Optional]
├── Workspace page filter group (Group) [Optional]    ← Workspace Page Filter Group sub-pattern
└── FastTabs (Tab)
    ├── Section summary tiles (TabPage)               ← Section Tiles sub-pattern
    ├── Section tabbed list (TabPage)                 ← Section Tabbed List sub-pattern
    ├── Section charts (TabPage) [Optional]           ← Section Stacked Chart sub-pattern
    ├── Section PowerBI (TabPage) [Optional]          ← Section PowerBI sub-pattern
    └── Section related links (TabPage)               ← Section Related Links sub-pattern
```

### Operational workspace w/Tabs

```
Design
├── Action pane (ActionPane) [Optional]
├── Workspace page filter group (Group) [Optional]
└── StandardTab (Tab)
    ├── Operational workspace content                  ← can carry the basic shape above
    └── Other content (0..N)                           ← links section, embedded Power BI, etc.
```

### Required BP-warning resolutions

1. Form referenced by at least one menu item.
2. `TabPage.Caption` not empty (for all content sections).

---

## Sub-pattern names and styles used inside

- **`SectionTiles`** — tile grid section.
- **`SectionTabbedList`** — tabbed-list section.
- **`tab_simpleFastTab`** — extended style for tab pages.
- **`workspace_tileLayout`** — extended style for tile containers.
- **`VerticalTabs`** — tab styling.

These are the rendering hints that turn tabs/groups into the
workspace-specific visuals.

---

## The page filter — binding, placement, and propagation

The optional Workspace Page Filter Group (the filter strip above the FastTabs)
has several authoring realities that aren't obvious and that a clean create
doesn't catch — only `xpp_compile` does.

### Binding: an UNBOUND control with an EDT (workspace forms have no datasource)

A Workspace-style form may have **no data sources** (compile errors
`BPErrorDataSourceOnWorkspaceStyleForm` otherwise). So the page-filter field is
**not** datasource-bound — it's an unbound control whose `ExtendedDataType` is
set directly (e.g. a `CONRMWorkGroupId`). The EDT does double duty: its
`tableReferences` drive the lookup dropdown (set them, or the dropdown renders
silently dead — see `dynamics-xpp:xpp-edt`), and the EDT is also what the native
propagation matches against (below).

### Placement + pattern (the `PatternControlNotAllowed` trap)

The filter group must satisfy **two** constraints, and the compiler's
`PatternControlNotAllowed` names *neither* (it says only "not allowed at its
current location"):

1. a **first-class** `pattern: "WorkspacePageFilterGroup"` (v1.0) — set as the
   control's `pattern` property, NOT in `otherProperties` (where it's silently
   ignored); and
2. **placement BEFORE the FastTabs `Tab` control** — i.e. design controls in the
   order `[ActionPane, WorkspaceFilterGroup, Tabs]`.

Its prescribed group props: `Style=CustomFilter`, `FrameType=None`,
`ViewEditMode=Edit`, and an **empty** `Caption`. Note: write-time
`patternConformance.ok` can return `true` while these are still wrong — it does
not currently catch positional/sub-pattern placement, so **`xpp_compile` is the
final arbiter** for this control. If you hit `PatternControlNotAllowed`, check
*position* (before the Tab) and the first-class *pattern* property, in that
order.

### Build order: the Section Tabbed List FormParts must exist FIRST

`SectionTabbedList` → `TabbedList` (Tab) → tab pages → **each tab page needs a
"Form Part Section List"** (a Container/FormPartControl targeting a FormPart form
that must ALREADY exist). Empty/stubbed tabs fail compile
(`PatternControlMissingChild`). So you **cannot** scaffold the workspace shell
first and fill the lists later — author the FormPart forms BEFORE the workspace
form, or it won't compile.

The host `FormPartControl`'s **`targetName` resolves to a *Display menu item*,
not the form directly** — point it at the `AxMenuItemDisplay` whose Object is
the FormPart form (a bad/missing one compiles to `MenuItemDisplayDoesNotExist`).

### FormPartControl width: set `WidthMode=SizeToAvailable` the WHOLE way down

The host `FormPartControl` should set **both `HeightMode` AND `WidthMode` =
`SizeToAvailable`**. Omitting `WidthMode` renders the embedded part at auto/narrow
width (a tell-tale empty band to the right of the part). The dangerous part is
the **symptom misdirection**: if that part is a `FormPartSectionListDouble` with
a `Strip` ActionPane in its secondary group, a narrow part starves the toolbar
and the responsive layout silently collapses *every command button into the "…"
overflow flyout* — so the failure *looks* like a button/ActionPane problem when
the cause is one or two levels up in the host container's width.

Width has to be unobstructed the entire chain: **set
`WidthMode=SizeToAvailable` on BOTH (1) the host `FormPartControl` container in
the workspace tab AND (2) the Double pattern's secondary group
(`group_sideBySideSecondary`) inside the FormPart.** Fixing only one still
overflows. If buttons render only under "…" with plenty of empty horizontal
space, suspect width modes before touching the buttons or ActionPane.

### Propagating the page filter to tiles + FormParts

**Native mechanism (use this when it fits — zero code):** F&O auto-propagates
the page filter by matching the filter control's **EDT** against each consumer's
data — but **only if the matching EDT-typed field is on the ROOT table** of that
consumer's query/datasource (tiles: the topmost table of the tile's query;
FormParts: the root of the part's datasource). If your scope value naturally
sits on those root tables — or you can shape the queries so it does — the filter
just works, no code. **Prefer this.**

**Escape hatch (only when the native mechanism can't fit):** when the scoping
value must live behind an `exists join` (rooting a consumer on it would
over-count/duplicate on the join fan-out), propagate via code injection through a
session value. The working recipe (verified by live tracing; not in MS docs as a
whole):

1. **Lookup** still comes from the filter EDT's `tableReferences`.
2. **Session channel = a `public static <EDT>` field on a STANDALONE helper
   class** (not the form — a query's code can't reach a form static; it can reach
   `MyHelper::FIELD`).
3. **Set the static in the filter control's `modified()` BEFORE `super()`.**
   Embedded FormParts requery during the host workspace's `super()`, which runs
   *before* the workspace's `parmFilter()` — so priming the static pre-`super()`
   is what makes consumers read the right value on the same interaction. Also
   call `parmFilter()` in the workspace `init()` to prime on load.
4. **Tiles** read the static in their query's `QueryRun.init()` (the tile count
   infra DOES instantiate the QueryRun subclass and honor `init()`) — inject a
   range, or `qbds.enabled(false)` to drop the scope join when the filter is
   blank.
5. **Embedded FormParts** `implements SysIFilterEventHandler` and override
   `onFilterChanged()` → `<datasource>_ds.executeQuery()` (the framework-native
   subscription; cleaner than manually calling `research()`).

---

## Form class

```xpp
[Form]
public class MyWorkspace extends FormRun
{
}
```

Standard `FormRun`. Workspaces don't typically have much custom code
in the form class itself — the heavy lifting is in the menu items the
tiles launch.

---

## Tiles and counts

Workspace tiles are buttons that:

- Launch a menu item (typically a `ListPage` for the related entity).
- Display a count or KPI (e.g. "23 pending orders").
- May carry a color/style indicating urgency.

The count is computed by a **Tile Display Method** referenced from
the tile, which queries the underlying data and returns a number.
The menu item driving the tile must exist; the display method must
return an int.

---

## Lists in tabbed-list sections

`SectionTabbedList` sections host one or more **list fact boxes** —
small grids that show a focused slice of data (e.g. "My pending
sales orders" inside an order-processor workspace). Each list
references a query (often a named view) and renders a few key
columns.

---

## Composite components

Workspaces lean heavily on **composite extension components**
(`AxFormControlExtensionComponentComposite`). These are reusable
mini-controls assembled from smaller parts — charts, KPI displays,
filter strips. The example shows the verbose structure for
authoring one.

---

## UX guidelines (from MS Learn)

- **Page title:** noun phrase, avoid general words. Begin with the
  noun users would have in mind. Don't duplicate the title of an
  area page.
- **All sections must have a title.**
- **A section spans the width of 2-4 standard tiles.**
- **`FormPartControl` HeightMode=SizeToAvailable** on any section
  that uses a FormPartControl to display content.

### Actions

- Include **only frequently used commands** on the action pane.
- Actions on the action pane relate to the **whole workspace**, not
  a specific section.
  - **Exception:** A single "New" action may be a tile in the
    Summary section if very frequently used.
- Group variations of the same command on drop-down menus (e.g.
  New sales quote / New sales order / New return order).

### Filters

- **0-5 filter fields** allowed on a workspace.
- Only **one** field can be under the page title.
- Remaining filters go in a workspace configuration dialog.

---

## Sub-patterns used inside

All in the workspace family — each section binds to a specific
sub-pattern by name:

- **Workspace Page Filter Group** — the optional filter at the top.
- **Section Tiles** — tile grid with counts/KPIs.
- **Section Tabbed List** — tabbed list section (multiple list
  views in the same area, only one shown at a time).
- **Section Stacked Chart** — up to two charts in a section.
- **Section PowerBI** — embedded Power BI section.
- **Section Related Links** — links section.

See `dynamics-xpp:xpp-form-subpatterns` for each.

---

## Migration from old "Workspace" pattern (REQUIRED post-10.0.25)

As of version **10.0.25**, the workspace patterns were reorganized
so content sections stack VERTICALLY and are collapsible. The old
panorama / horizontal-scroll layout is gone. **Out-of-box workspaces
were migrated by MS**; user-installed workspaces must be migrated
manually.

### Mass-update path (preferred)

Run the BP fixer tool from the command line:

```
c:\AOSService\PackagesLocalDirectory\bin\xppbp.exe ^
  -m=<metadataPath> -mu=<moduleName> -me=<modelName> ^
  -rules=BPUpgradeMetadataFormPatternVersionNotActive ^
  -x=<logFilePath> form:* -packagesRoot=<packagePath> ^
  -runfixers
```

After the tool runs, manually set:

- **Group controls in link sections**: `FrameOptionButton=None`
  (suppress the collapse affordance and the visual line under the
  section).
- **FormPartControl controls in list sections**: `WidthMode=SizeToAvailable`
  (allow the subform to span the page width).

Finish with a fit-and-finish review.

### Manual migration path

For workspaces using the deprecated **Workspace** pattern:

1. Remove the deprecated pattern; apply **Operational workspace**.
2. Replace old sub-patterns with their counterparts:
   - `HubTiles` → `SectionTiles`
   - `HubPartLinks` → `SectionRelatedLinks`
   - `HubPartGrid` → `FormPartSectionList`
3. Ensure all workspace-related patterns are up to date (remove and
   reapply each pattern/sub-pattern).
4. For non-Operational-Workspace tabs with `Style=Panorama`:
   - Change `Style` to `FastTabs`.
   - Set `ExtendedStyle` to `tab_simpleFastTab`.
   - Set `FastTabExpanded=Yes` on each child tab page.

### Form extensions on workspaces

If the base workspace was moved to a newer pattern version, controls
added by your form extensions don't auto-pick-up the new metadata.
If compile errors appear: open the form extension, save it. For
extensions adding new lists or link groups, manually adjust per the
mass-update step.

### Fit and finish areas

- **Tiles**: all same height (ideally same size). 10.0.26+ users can
  resize personally via "Allow users to select and change tile sizes."
- **Simple lists** in list sections: consider switching to a
  tabular grid (more columns) or a card list (horizontal flow).
- **Card lists**: to enable horizontal flow + wrap, opt the form
  out of the new grid control, then set:
  - `Style=List`
  - `ExtendedStyle=cardList`
  - `VisibleColumnsMode=Fixed`
  - `VisibleColumns=0`

---

## Gotchas

- **Old "Workspace" pattern is obsolete post-10.0.25.** Migrate before
  upgrading.
- **Tabbed workspace is deprecated.** Replace with Operational
  workspace w/Tabs.
- **Workspaces require careful menu-item setup.** Every tile launches
  a menu item; missing menu items show as empty tiles.
- **Tile counts are computed by methods** — make sure they exist and
  perform reasonably. A workspace with five tiles each running a
  slow query is a slow workspace.
- **No datasource on the form itself.** If you need one, you're
  probably authoring the wrong pattern.
- **Role-tagging.** Workspaces are typically wired to specific
  security roles via menu/workspace assignments. The workspace
  exists but no one sees it without the role membership.
- **High authoring effort.** Plan to iterate.
- **`Style=Workspace`.**
- **All `xmlns=""` rules from `dynamics-xpp:xpp-form` apply.**

---

## Supporting files

- `examples/example-domain.json` -- **start here.** Typed `CreateFormRequest` for a minimal WorkspaceOperational. Substitute the backing table and field set, then pass to `xpp_create_form`.
- `examples/example.xml` -- structural reference for reading existing forms. Don't hand-author from it; use the typed example above.


---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules.
- `dynamics-xpp:xpp-pattern-list-page` — what tiles typically launch into.
- `dynamics-xpp:xpp-pattern-details-master`, `dynamics-xpp:xpp-pattern-details-transaction` —
  what list-fact-boxes ultimately drill into.
- `xpp://schema/AxForm` — authoritative XSD.
