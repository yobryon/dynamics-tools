---
name: xpp-form-subpatterns
description: Use whenever a pattern skill references a sub-pattern by name — Fields and Field Groups, Toolbar and List, Toolbar and Fields, Custom Filter Group, Nested Simple List and Details, the Workspace section sub-patterns, etc. Sub-patterns apply to container controls INSIDE a form (groups, FastTabs, tab pages) and dictate the legal contents and layout of that container.
---

# Form sub-patterns

In D365 F&O the **form pattern** describes the top-level shape of a
form (SimpleList, DetailsMaster, ListPage, etc.). The **sub-pattern**
describes the shape of a *container inside* the form (a group, a
FastTab, a tab page). Pick a sub-pattern that matches what the
container does — fields, toolbar+grid, filters, etc. — and the F&O
form runtime enforces the right defaults and constraints.

Sub-patterns are a real, first-class concept in F&O — they're
selectable in Visual Studio just like top-level patterns (search
"unspecified" in the form designer to find containers that still
need one).

This skill is a reference for every sub-pattern our pattern skills
reference by name. Load it any time a pattern skill says
"see `dynamics-xpp:xpp-form-subpatterns`."

---

## How sub-patterns relate to top-level patterns

- Form-level `Design.Pattern` = the top-level pattern (e.g.
  `SimpleListDetails`).
- Container-level `Pattern` (on `AxFormGroupControl`,
  `AxFormTabPageControl`, etc.) = the sub-pattern (e.g.
  `FieldsFieldGroups`).
- Both pattern + sub-pattern carry a `PatternVersion` (typically
  `1.1`).

Pattern conformance is checked at both levels: form pattern
validation **plus** every container's sub-pattern validation.

---

## The sub-pattern catalog

Microsoft groups sub-patterns into classes:

### Custom Filters (2 variants)

For containers that display QuickFilters and any other modeled
custom filters.

- **Custom Filters** — used when custom filters are modeled and no
  QuickFilter is required.
  - MS reference: `LedgerJournalTable (TopFields)`.
- **Custom and Quick Filters** — used when a QuickFilter IS required
  alongside custom filters.
  - MS reference: `CustTable (CustomFilterGroup)`.

Both render horizontally as a filter strip above a grid. Used by
SimpleList, ListPage, and the navigation lists in DetailsMaster /
DetailsTransaction / SimpleListDetails.

### Fields (5 variants)

For containers that primarily display individual fields.

#### Fields and Field Groups

**The most common data entry sub-pattern.** Dynamic column count
that adapts to viewport width to present multiple fields or groups
of fields.

- MS reference: `InventLocation (LocationNames)`.

**Typical contents:**
- Groups or fields as immediate children of the FastTab.
- Groups containing fields.
- Can contain other sub-patterns: Horizontal Fields and Button Group.

**Not used with** controls that have dynamic height/width (Grid,
Tree, RadioButton, ListBox, ListView) or larger height/width (Chart).

**High-level structure:**
```
[Container] (Columns=Fill)
├── FieldGroups (Group) [0..N]
│   ├── Fields ($Field) [1..N]
│   └── ActionableFields (Group) [0..N]   ← mimics Horizontal Fields and Button Group
├── Fields ($Field) [0..N]
└── ActionableFields (Group) [0..N]
```

**Constraints (these bite — read carefully):**

- **NO static text** allowed directly. Use field `HelpText` or
  form-level Help content instead. If you must show static text,
  the container falls to `Pattern:Custom`.
- **NO images** inside the container (or any group nested under it).
- **One level of group depth maximum.** Refactor any deeper nesting.
- **No `WidthMode=Manual` on fields.** Only `SizeToContent` is
  honored, which maps `DisplayLength` to one of 4 discrete sizes:
  extra small / small / medium / large. `SizeToAvailable` is also
  disallowed inside this sub-pattern (use `Fill Text` for a
  full-width field instead).
- **No `HeightMode=Manual`.** Same `SizeToContent` rule.

**UX guidelines:**
- Fields in groups should flow across the entire page.
- Remove unnecessary field group labels.
- Either ALL fields are in labeled groups, or NO group labels are
  shown (don't mix).

**Used by patterns:** SimpleListDetails, TableOfContents,
DetailsMaster, DetailsTransaction.

#### Tabular Fields

A structured layout of fields. Intended primarily for totals.

- MS reference: `LedgerJournalTransVendPaym (Balances)`.

Used when the fields need a tabular look (label + value in a
two-column structure) rather than the flowing layout of Fields and
Field Groups.

#### Fill Text

For containers where a single input control needs full width.

- MS reference: `FmRental (Notes)`.

Use when a `Memo` field or wide string field should span the
container width. Currently supports one full-width field per
container; MS has stated intent to expand this to multiple.

#### Horizontal Fields and Button Group

For when a field has an inline action (e.g. a calculated field with
a "Recalculate" button next to it).

- MS reference: `SalesTable (GroupHeaderAddressHeaderOverview)`.

Used inline within Fields and Field Groups containers (as
`ActionableFields` group above).

#### Image Preview

For containers that have image controls (and optional related
fields).

- MS reference: `RetailVisualProfile (Login)`.

---

### Toolbar and List (2 variants)

For containers that display actions above grids.

- **Toolbar and List** — single grid with a toolbar above.
  - MS reference: `VendTable (TabCommunication)`.
- **Toolbar and List – Double** — two grids with a toolbar.
  - MS reference: `SalesQuickQuote (TabPageExistingItems)`.

**Used by patterns:** SimpleListDetails (line/detail grids inside),
DetailsMaster (FastTab grids), DetailsTransaction (line FastTabs),
TableOfContents (per-tab grids), Wizard (steps with selection
grids).

### Toolbar and Fields

For containers that have actions above a set of fields.

- MS reference: `HcmPosition (WorkerAssignmentTabPage)`.

Sometimes shown inside Fields and Field Groups containers when an
action button needs to appear above the field set.

**Used by patterns:** Same as Toolbar and List — anywhere a FastTab
needs field content + an action toolbar.

### List Panel

For containers where users must move items back and forth between
two lists.

- MS reference: `CLIControls_ListPanel (FormTabPageControl1)`.

Used by Wizard pattern (transfer-style step) and Dialog/Drop
Dialog patterns (selection dialogs).

### Nested Simple List and Details

For embedding a simpler Simple List and Details form inside a
section (tab page or group) of a larger form.

- MS reference: `HcmJob (TaskTabPage)`.

**Used by patterns:** DetailsMaster, DetailsTransaction,
TableOfContents, SimpleListDetails (recursive nesting).

---

## Workspace sub-patterns (8 variants)

These apply to tab pages inside an `WorkspaceOperational` form.

### Section Tiles

A set of tiles/charts in a workspace section. Tiles launch into
related forms via menu items; charts are defined via Form Part
Controls.

- MS reference: `SalesOrderProcessingWorkspace`.

### Section Related Links

A set of hyperlinks in a workspace section. Modeled on a tab page
inside the workspace form. Each link is a `MenuFunctionButton` whose
properties must match a specific shape — neither the XSD nor
`xpp_bp_check` catches violations; only metadata-validation at
compile-time does, and runtime failures (the F&O "No object specified
on menu item" warning) compile cleanly.

**Required button shape:**
- `Style=Link` — required by the Related Links pattern itself.
- `ButtonDisplay=TextOnly` — required by the Related Links pattern.
- `FormControlExtension i:nil="true"` — even when there's no real
  extension. Omitting this compiles, but produces the runtime
  resolution failure.
- **Omit `MenuItemType` for Display menu items** — specifying it
  explicitly (even as `Display`, which is supposedly the default)
  triggers the "No object specified" runtime warning. Specify
  `MenuItemType` only for `Action` / `Output`.

**Known-good shape** (matches MS-shipped `CostAdminWorkspace`):

```xml
<AxFormExtensionControl>
  <Name>FormExtensionControl_myLink</Name>
  <FormControl xmlns="" i:type="AxFormMenuFunctionButtonControl">
    <Name>myMenuItemLink</Name>
    <Type>MenuFunctionButton</Type>
    <Style>Link</Style>
    <ButtonDisplay>TextOnly</ButtonDisplay>
    <FormControlExtension i:nil="true" />
    <MenuItemName>myMenuItemName</MenuItemName>
  </FormControl>
  <Parent>SetupAndConfiguration</Parent>
</AxFormExtensionControl>
```

For `Action` / `Output` items, add `<MenuItemType>Action</MenuItemType>`
(or `Output`) inside `FormControl`. Do not add it for `Display`.

- MS reference: `SalesOrderProcessingWorkspace`, `CostAdminWorkspace`.

### Section Tabbed List

Multiple list variants in one section. Only one list is shown at a
time (the user switches via inner tabs).

Used for "active work" sections (Pending orders, Open invoices,
etc. in one tabbed-list section).

### Section Stacked Chart

Up to two charts in an Operational Workspace section.

### Section PowerBI

An embedded Power BI section. Used to integrate Power BI reports
directly into the workspace.

### Workspace Page Filter Group

A single filter at the top of a workspace, above the FastTabs.
Limited to one field; remaining filters must go into a workspace
configuration dialog.

### Filters and Toolbar – Stacked / Inline

Used inside the **Form Part Section List** pattern (a separate
top-level pattern, see `ROADMAP.md`):

- **Filters and Toolbar – Stacked** — actions appear *below* filters.
- **Filters and Toolbar – Inline** — filters and actions on the same
  line.

---

## Specialty sub-patterns

### Dimension Entry Control

For tab pages that have only a Dimension Entry Control (financial
dimension entry — used heavily in finance modules).

- MS reference: `CustTable (TabFinancialDimensions)`.

### Dimension Expression Builder

For containers that include a Dimension Expression Builder control.
Used in ledger-rule and account-structure configuration.

---

## How to pick a sub-pattern for an "unspecified" container

When VS shows a container with `Pattern: <unselected>`:

1. **Look at what's inside the container.**
   - Just fields → Fields and Field Groups (or Tabular Fields for
     totals layout).
   - Grid only → Toolbar and List (add toolbar if needed; even an
     empty one keeps you BPC-clean).
   - Grid + button bar above → Toolbar and List.
   - Fields + button bar above → Toolbar and Fields.
   - QuickFilter + custom filters → Custom and Quick Filters.
   - Custom filters only → Custom Filters.
   - Two transfer lists → List Panel.
   - Embedded SL+D form → Nested Simple List and Details.
   - Image + related fields → Image Preview.
   - One full-width input → Fill Text.
   - Tab page of workspace → one of the workspace sub-patterns
     above, based on content (tiles / list / chart / Power BI /
     links).
2. **Apply the sub-pattern via the designer**: right-click the
   container, **Apply pattern**, pick from the list.
3. **Fix any "unmatched" controls** the validator flags.

---

## Why "unmatched" warnings appear

Common reasons under Fields and Field Groups:

- **More than one level of group depth.** Refactor to single-level
  groups inside the container.
- **Image or static text in the group.** Remove or relocate.

For other sub-patterns the rules vary, but the pattern's documented
"high-level structure" is the canonical reference — anything outside
that structure shows as unmatched.

---

## Sub-pattern + form pattern combination matrix (quick reference)

| Form pattern | Common sub-patterns on its containers |
|---|---|
| SimpleList | Custom Filter Group |
| SimpleListDetails | Fields and Field Groups, Toolbar and List, Toolbar and Fields, Nested SL+D |
| DetailsMaster | Fields and Field Groups, Toolbar and List, Toolbar and Fields, Nested SL+D, Custom Filter Group |
| DetailsTransaction | Fields and Field Groups, Toolbar and List, Toolbar and Fields, Nested SL+D, Custom Filter Group |
| ListPage | Custom Filter Group |
| TableOfContents | Fields and Field Groups, Toolbar and List, Toolbar and Fields, Nested SL+D, Tabular Fields, List Panel |
| Wizard | Fields and Field Groups, Toolbar and List, Toolbar and Fields, List Panel |
| WorkspaceOperational | Workspace Page Filter Group, Section Tiles, Section Tabbed List, Section Stacked Chart, Section PowerBI, Section Related Links |

---

## See also

- The pattern skills (`xpp:pattern-*`) — each names its sub-patterns
  and points back here.
- `dynamics-xpp:xpp-form` — envelope basics shared across all patterns.
- `xpp://schema/AxForm` — authoritative XSD; sub-patterns appear
  as `<Pattern>` and `<PatternVersion>` properties on container
  controls.
- MS Learn — for the per-sub-pattern detail pages:
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/fields-field-groups-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/toolbar-list-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/toolbar-fields-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/custom-filter-group-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/nested-simple-list-details-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/list-panel-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/section-tiles-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/section-tabbed-list-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/section-stacked-chart-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/section-powerbi-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/section-related-links-subpattern
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/workspace-filter-group-subpattern

---

## Legal pattern names + versions (authoritative — from the shipped catalog)

These are the **exact** `Pattern` / `PatternVersion` strings the F&O pattern
catalog accepts. A name or version not in these tables fails compile with
`UnableToValidatePattern: Pattern '<name> <version>' not found` — and
`xpp_create_form` / `xpp_patch_form` now flag it in `patternConformance.mismatches`
(`op: NotInCatalog`) before you ever compile. Copy the strings **verbatim**
(casing and the `UX7` prefix matter). Regenerate after a platform update with
`misc/probe_subpattern_ref.ps1`.

### Form patterns (set on `design.pattern`)
| Pattern | Version(s) |
|---|---|
| AdvancedSelection | 1.1 |
| DetailsMaster | 1.4 |
| DetailsMasterTabs | 1.4 |
| DetailsTransaction | 1.4 |
| Dialog / DialogTabs / DialogDoubleTabs / DialogFastTabs / DialogReadOnly | 1.2 / 1.3 / 1.3 / 1.0 / 1.2 |
| DropDialog / DropDialogReadOnly | 1.2 / 1.2 |
| FormPartFactboxCard / FormPartFactboxGrid | UX7 1.0 / 1.1 |
| FormPartSectionList / FormPartSectionListDouble | 1.2 / 1.2 |
| ListPage | UX7 1.0 |
| LookupGridOnly / LookupPreview / LookupTab | 1.1 / 1.0 / 1.0 |
| SimpleDetails-FastTabsContainer / -StandardTabsContainer / -ToolbarFields / -Panorama | 1.4 / 1.5 / 1.3 / 1.1 |
| SimpleList | 1.1 |
| SimpleListDetails | 1.3 |
| SimpleListDetails-Grid / SimpleListDetails-Tree | 1.4 / 1.3 |
| TableOfContents | 1.1 |
| Task / TaskParentChild | 1.2 / 1.2 |
| Wizard | 1.2 |
| WorkspaceOperational / WorkspaceOperationalTabs / TabbedWorkspace | 1.1 / 1.0 / 1.0 |
| HubPartChart | 1.0 |

### Sub-patterns (set on a container control's `pattern`)
| Sub-pattern | Version(s) |
|---|---|
| CustomAndQuickFilters | 1.1 |
| CustomFilters | 1.1 |
| FieldsFieldGroups | 1.1 |
| FiltersAndToolbarInline / FiltersAndToolbarStacked | 1.0 / 1.1 |
| HorizontalFieldsButtonsGroup | UX7 1.2 |
| ToolbarList | 1.2 |
| ToolbarFields | 1.1 |
| TabularFields / TabPageTabularFields | 1.1 / 1.0 |
| NestedSimpleListDetails | UX7 1.1 |
| ListPanel | 1.3 |
| ImagePreview | 1.1 |
| EntityHeader | 1.0 |
| DimensionEntryControl / DimensionExpressionBuilder | 1.1 / 1.0 |
| SectionTiles / SectionTabbedList / SectionStackedChart / SectionPowerBI / SectionRelatedLinks | 1.1 / 1.1 / 1.1 / 1.0 / 1.1 |
| BusinessCardIndicator / BusinessCardStatus / BusinessCardThreeFields | 1.1 / 1.1 / 1.0 |
| TileCard | 1.0 |
| WorkspacePageFilterGroup | 1.0 |

**Common traps (real compile failures):** `ToolbarAndList` (does not exist — it's
`ToolbarList`); `NavigationListSimpleListAndDetails` (does not exist — the SL+D nav
group declares **no** sub-pattern; its Quick Filter + Grid are required *direct
children*); a `ToolbarList` at version `1.0`/`1.1` (the active version is `1.2`).

## The Quick Filter control shape

A Quick Filter is **not** a normal typed control — it's a base `AxFormControl`
(typed-shape `kind: "Other"`, no `rawType`) carrying a `FormControlExtension`
named `QuickFilterControl`. Required as a **direct child** of the SimpleListDetails
navigation group. Typed shape:

```json
{
  "name": "QuickFilterControl",
  "kind": "Other",
  "formControlExtension": {
    "name": "QuickFilterControl",
    "extensionProperties": [
      {"name": "targetControlName", "type": "String", "value": "Grid"},
      {"name": "defaultColumnName", "type": "String", "value": "Grid_Name"}
    ]
  }
}
```

`targetControlName` = the grid it filters; `defaultColumnName` = the grid column
(`<GridName>_<field>`). See `dynamics-xpp:xpp-pattern-simple-list-details`
`examples/example-domain.json` for it in context.
