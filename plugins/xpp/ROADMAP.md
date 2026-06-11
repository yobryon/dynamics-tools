# dynamics-xpp plugin — roadmap

What's planned next for the plugin's skill fleet. Sourced from a
2026-05-19 audit of Microsoft Learn against our skill coverage.

## Coverage today

### Anchor + per-AOT-type (8 skills)
- dynamics-xpp:xpp-language, dynamics-xpp:xpp-class, dynamics-xpp:xpp-table, dynamics-xpp:xpp-form, dynamics-xpp:xpp-edt, dynamics-xpp:xpp-enum,
  dynamics-xpp:xpp-labelfile, dynamics-xpp:xpp-extension

### Per-form-pattern (10 skills)
- dynamics-xpp:xpp-pattern-simple-list, dynamics-xpp:xpp-pattern-simple-list-details,
  dynamics-xpp:xpp-pattern-details-master, dynamics-xpp:xpp-pattern-details-transaction,
  dynamics-xpp:xpp-pattern-list-page, dynamics-xpp:xpp-pattern-task (legacy),
  dynamics-xpp:xpp-pattern-task-parent-child (legacy), dynamics-xpp:xpp-pattern-wizard,
  dynamics-xpp:xpp-pattern-table-of-contents, dynamics-xpp:xpp-pattern-workspace-operational

### Sub-patterns (1 catalog skill)
- dynamics-xpp:xpp-form-subpatterns — covers 17+ sub-patterns from the MS catalog

## Missing top-level form patterns (roadmap)

These are documented top-level form patterns in MS Learn that we
don't yet have dedicated skills for. Listed in rough priority for
new work.

### High priority

#### `xpp:pattern-dialog` — Dialog (6 variants)

The modern replacement for the legacy Task pattern. Used to gather
or show a set of information in a dialog form.

Variants:
- **Dialog – Basic** (default) — input collection. MS reference:
  `ProjTableCreate`.
- **Dialog – Read Only** — display-only, has only a Close button.
  MS reference: `SalesTablePostings`.
- **Dialog – FastTabs** — content grouped into FastTabs (no current
  in-product example).
- **Dialog – Tabs** — content grouped into standard tabs. MS
  reference: `CaseDetailCreate`.
- **Dialog – Double Tabs** — two stacked tab sections. MS reference:
  `PurchTableReferences`.

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/dialog-form-pattern

#### `xpp:pattern-drop-dialog` — Drop Dialog (2 variants)

Small dialogs (<5 fields) used to initiate actions. The modern
choice for what would have been simple Task forms in AX 2012.

Variants:
- **Drop Dialog** (default) — action-initiating with <5 fields. MS
  reference: `CustCollectionsNewActivityAction`.
- **Drop Dialog – Read Only** — display-only fields, no OK/Close
  button.

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/drop-dialog-form-pattern

#### `xpp:pattern-simple-details` — Simple Details (4 variants)

A focused single-record form. Used inside Table of Contents tab
pages and standalone for record detail surfaces lighter than
Details Master.

Variants:
- **Simple Details w/Toolbar and Fields** — single base record with
  toolbar + fields layout. MS reference: `AgreementLine`.
- **Simple Details w/FastTabs** — record info organized into
  FastTabs. MS reference: `PlanActivityServiceDetails`.
- **Simple Details w/Standard Tabs** — record info organized into
  traditional tabs. MS reference: `HcmEmploymentDateManager`.
- **Simple Details w/Panorama** — horizontally scrolling panorama
  (legacy; same caveats as the old Workspace pattern). MS reference:
  `PdsMRCEventTracker`.

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/simple-details-form-pattern

### Medium priority

#### `xpp:pattern-fact-box` — FactBox (2 variants)

The Dynamics AX 2012 FactBox that displays information about a
related record or set of records. Embedded in the right pane of
list pages and detail pages.

Variants:
- **FactBox Grid** — shows a child collection. MS reference:
  `ContactsInfoPart`.
- **FactBox Card** — shows a set of related fields. MS reference:
  `CustStatisticsStatistics`.

Most FactBoxes are embedded into other forms via the `<Parts>` block
(see dynamics-xpp:xpp-form). A dedicated skill covers authoring the FactBox
forms themselves.

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/factbox-form-patterns

#### `xpp:pattern-lookup` — Lookup (3 variants)

Forms used as lookups (e.g. the dropdown that opens when clicking
the lookup arrow on an input field).

Variants:
- **Lookup Basic** (default) — grid or tree with optional filters
  and buttons. MS reference: `SysLanguageLookup`.
- **Lookup w/Preview** — basic + preview of current record. MS
  reference: `HcmWorkerLookup`.
- **Lookup w/Tabs** — multiple views (e.g. grid view + tree view).
  MS reference: `CaseCategoryLookup`.

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/lookup-form-pattern

#### `xpp:pattern-form-part-section-list` — Form Part Section List (2 variants)

Workspace-specific patterns for showing filtered lists inside
workspaces. Used by the tabbed list sections of an Operational
Workspace.

Variants:
- **Form Part Section List** — single list of data with optional
  header group for filters/actions.
- **Form Part Section List – Double** — second list to the right
  (hidden by default; toggled via toolbar button).

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/section-list-form-pattern

### Lower priority

#### `xpp:pattern-section-chart` — Section Chart

Workspace section that hosts a chart, rendered via a Form Part
Control. The form-level pattern (not the sub-pattern).

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/section-chart-form-pattern

#### `xpp:pattern-advanced-selection` — Advanced Selection

A Dialog form pattern variant for filtering and selecting items
from a large, wide list. Resembles List Panel sub-pattern but lets
users see the full set they're selecting, with custom filters and
wide-list layout.

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/advanced-selection-form-pattern

## Other roadmap items

### Per-pattern templates

Today every pattern skill ships `examples/example.xml` (MS's
working form verbatim) but only `dynamics-xpp:xpp-pattern-simple-list` has a
separate `template.xml` with `{{PLACEHOLDERS}}`. Extracting
templates from each pattern's example would let agents start from
a clean placeholder skeleton rather than substituting names into a
concrete example.

Effort: ~15 min per pattern, low complexity. Defer until we see
empirical demand.

### Sub-pattern fetches

`dynamics-xpp:xpp-form-subpatterns` cites MS Learn URLs for each sub-pattern but
doesn't fetch their full content (deferred to avoid context bloat).
A future pass could deepen each sub-pattern's section using the
authoritative doc — particularly:

- **Toolbar and List / Toolbar and Fields** — fetch and add the
  high-level model + BP checks.
- **Custom Filter Group** — same.
- **Workspace sub-patterns** (Section Tiles, Section Tabbed List,
  etc.) — the workspace migration depends on these being correct.

### General Form Guidelines

MS consolidates standard form guidelines into a single document
(`general-form-guidelines`) referenced from every pattern. We don't
have a dedicated skill for these; they're scattered across our
pattern skills. A consolidated `dynamics-xpp:xpp-form-guidelines` skill could
cover ActionPane, FastTabs, Grid, FactBox, label, Button Image,
and accessibility rules in one place.

MS Learn:
https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/general-form-guidelines

### Modernization callouts already captured

These were surfaced during the 2026-05-19 audit and the relevant
pattern skills now warn about them:

- **Task / TaskParentChild are legacy** — `dynamics-xpp:xpp-pattern-task` and
  `dynamics-xpp:xpp-pattern-task-parent-child` carry prominent legacy warnings.
- **List Page + Details merged into one form** for 1:1 entities —
  `dynamics-xpp:xpp-pattern-list-page` and `dynamics-xpp:xpp-pattern-details-master` both
  carry the merged-form guidance.
- **Workspace pattern reorganized to vertical** as of 10.0.25 —
  `dynamics-xpp:xpp-pattern-workspace-operational` carries the migration steps.
- **Tabbed workspace deprecated** in favor of Operational workspace
  w/Tabs — captured in `dynamics-xpp:xpp-pattern-workspace-operational`.
- **Old `Workspace` pattern obsolete** post-10.0.25 — same.
