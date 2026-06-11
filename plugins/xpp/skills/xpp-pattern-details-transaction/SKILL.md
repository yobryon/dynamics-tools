---
name: xpp-pattern-details-transaction
description: Use when authoring a DetailsTransaction-pattern form in D365 F&O — the canonical "header + lines" transactional document form (sales order, purchase order, journal). In modern F&O the form carries THREE views — Header view, Line view, Grid view — switched via the panel-tab structure. Header view is compulsory even if initially redundant.
---

# Authoring a `DetailsTransaction` form

A `DetailsTransaction` form is **a details form with lines**:
header + lines document, with two details views the user can switch
between — a Header view (all header-related fields) and a Line view
(lines grid + line details + a section of the most important header
fields). In modern F&O the same form also carries the grid view of
all records (the merged-ListPage model).

Canonical examples: sales orders, purchase orders, ledger journals.

Load `dynamics-xpp:xpp-form` first if you haven't, and read
`dynamics-xpp:xpp-pattern-details-master` since DetailsTransaction shares the
merged-ListPage structure.

---

## When to pick DetailsTransaction

| Use DetailsTransaction when... | Use ... instead |
|---|---|
| Document with header + lines (1-to-many) | — |
| Single record, no lines | `dynamics-xpp:xpp-pattern-details-master` |
| List + side panel for browsing | `dynamics-xpp:xpp-pattern-simple-list-details` |
| Multiple peer grids (no obvious header) | `dynamics-xpp:xpp-pattern-task-parent-child` |
| Read-mostly list entry point | `dynamics-xpp:xpp-pattern-list-page` |

---

## Structural shape (per MS official model)

```
Design
├── ActionPane (ActionPane)
├── SidePanel (Group)                  ← navigation list
│   ├── QuickFilter
│   ├── CustomFilters (Group) [Optional]
│   └── NavigationList (Grid, Style=List)
└── PanelTab (Tab ShowTabs=No)         ← carries THREE views
    ├── DetailsPanel (TabPage)
    │   ├── TitleGroup (Group)
    │   │   ├── HeaderTitle (String)
    │   │   └── EntityStatus (Group) [Optional]
    │   │       └── StatusFields (1..N)
    │   └── HeaderLinePanels (Tab ShowTabs=No)   ← switch Header/Line view
    │       ├── LinePanel (TabPage PanelStyle=Line)
    │       │   └── LineViewTab (Tab Style=FastTabs)
    │       │       ├── LineViewHeader (TabPage)
    │       │       ├── LineViewLines (TabPage)
    │       │       └── LineViewLineDetails (TabPage)
    │       │           └── LineDetailsTab (Tab Style=Standard)
    │       │               └── LineDetailsTabPages (TabPages 1..N)
    │       └── HeaderPanel (TabPage PanelStyle=Header)
    │           └── HeaderViewTab (Tab Style=FastTabs)
    │               └── HeaderViewTabPages (TabPages 1..N)
    └── GridPanel (TabPage PanelStyle=Grid)      ← list view (merged ListPage)
        ├── CustomFilterGroup (Group)
        │   ├── QuickFilter
        │   └── OtherFilters ($Field) [0..N]
        ├── MainGrid (Grid)
        └── MainGridDefaultAction (CommandButton)
```

The **Header view is compulsory** even if initially it carries no
more than the Line view's header summary — per MS, it gets extended
over time by app teams, internationalization teams, partners, and
customers; the consistent structure is required for upgradeability.

### Required BP-warning resolutions

1. `Design.Caption` not empty.
2. Form referenced by at least one menu item.
3. `TabPage.Caption` not empty.
4. `TabPage.DataSource` not empty.

Key `Design` properties:

- `Pattern` — `DetailsTransaction`
- `PatternVersion` — typically `1.1`
- `Style` — `DetailsFormTransaction` (note: differs from pattern name,
  same as DetailsMaster)
- `Caption`, `DataSource`, `TitleDataSource` — set normally.

---

## Title fields and detail-title container

Same as DetailsMaster — the form carries a `DetailTitleContainer` group
with `TitleField`-styled controls showing the header record's identity.

---

## Sub-pattern names used inside

- **`FieldsFieldGroups`** — per-section content group.
- **`Strip`** — toolbar strip styling above a fields section.
- **`List`** — inner list style for the lines grid.

---

## Form class

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

Standard `FormRun`. Some shipped transaction forms use
`FormDetailsTransactionExtended`; not required for new forms.

---

## Datasources

Two datasources at minimum:

- **Header datasource** — the document table.
- **Lines datasource** — the line table, with `JoinSource=Header`
  and `LinkType=Delayed` (or `Active` if the lines grid must update
  instantly when the user navigates the header — for most documents
  `Delayed` is fine).

```xml
<AxFormDataSource xmlns="">
  <Name>SalesTable</Name>
  <Table>SalesTable</Table>
  <!-- header datasource -->
</AxFormDataSource>
<AxFormDataSource xmlns="">
  <Name>SalesLine</Name>
  <Table>SalesLine</Table>
  <JoinSource>SalesTable</JoinSource>
  <LinkType>Delayed</LinkType>
  <!-- lines datasource -->
</AxFormDataSource>
```

The relation between header and lines must exist on the lines table
(e.g. `SalesLine` has a relation back to `SalesTable.SalesId`).

---

## Action pane: posting and line-management buttons

DetailsTransaction action panes typically have button groups for:

- **Document-level actions:** Post, Cancel, Apply payment, etc. —
  triggered on the header.
- **Line-level actions:** Add line, Remove line, Validate, etc. —
  triggered on the active line.

Use `AxFormButtonGroupControl` to group them, and
`AxFormMenuItemButtonControl` or `AxFormButtonControl` for the actions
themselves.

---

## UX guidelines (from MS Learn)

- **No duplicate New/Delete buttons** — framework provides View/Edit,
  New, Delete, Save, Refresh, Attachments, Export to Excel.
- **First FastTab's content fully visible without scrolling** in the
  default state.
- **Page title format:** `<ID> : <Description>`. Plural in the menu.
- **Navigation list:**
  - Don't exceed 3 lines per row.
  - At least 2 fields; typically ID + Description.
  - **Last field should be the total of the transaction.**
- **Grid view:**
  - 2-15 fields.
  - First column: ID, then master entity ID and Name.
  - Quick Filter defaults to the most likely filter field.
  - Focus in Quick Filter on open.

---

## Commonly used sub-patterns

- **Fields and Field Groups** — header/line FastTab fields.
- **Toolbar and List** — line-grid FastTabs.
- **Toolbar and Fields** — header FastTabs with action toolbars.
- **Nested Simple List and Details** — embedding SL+D in a FastTab.
- **Custom Filter Group** — grid view's filter section.

See `dynamics-xpp:xpp-form-subpatterns` for the full reference.

---

## Header/Lines tab switching — modern behavior

Pre-10.0.23: Header/Lines proxy buttons appeared in the title area
as radio buttons styled as tabs.

10.0.23+: The **"Removal of header/lines proxy buttons"** feature
removes those radio buttons and exposes the native tab controls
underneath. Better accessibility; same functionality. When migrating
or auditing, expect to see the native tab control as the switch
mechanism, not the legacy radio-buttons-as-tabs.

---

## Gotchas

- **`Style=DetailsFormTransaction`, not `DetailsTransaction`.** Same
  pattern/style name divergence as DetailsMaster.
- **Header view is compulsory.** Don't try to skip it even if it's
  initially identical to the Line view header.
- **The relation on the lines table is load-bearing.** A missing or
  wrong relation produces a form that loads but doesn't link the
  master/child datasources.
- **Posting buttons are typically menu items** (`AxFormMenuItemButtonControl`
  with `MenuItemName`).
- **Don't add app buttons for foundation actions** (Save, Refresh,
  Attachments, Export, View/Edit, New, Delete).
- **All namespace rules from `dynamics-xpp:xpp-form` apply.**

---

## Supporting files

- `examples/example-domain.json` -- **start here.** Typed `CreateFormRequest` for a minimal DetailsTransaction. Substitute the backing table and field set, then pass to `xpp_create_form`.
- `examples/example.xml` -- structural reference for reading existing forms. Don't hand-author from it; use the typed example above.


---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-pattern-details-master` — header-only variant; the title-strip
  and tab structure is similar.
- `dynamics-xpp:xpp-pattern-list-page` — the entry-point list usually leading into
  a DetailsTransaction form.
- `dynamics-xpp:xpp-pattern-task-parent-child` — multiple peer grids without a
  dominant header/lines hierarchy.
- `xpp://schema/AxForm` — authoritative XSD.
