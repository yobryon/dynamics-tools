---
name: xpp-pattern-details-master
description: Use when authoring a DetailsMaster-pattern form in D365 F&O — the canonical pattern for entity master records (customer, vendor, item). In modern F&O this pattern ALSO subsumes the old ListPage when there's a 1:1 list-to-detail correspondence — the form carries both grid view and details view in one. Two variants — basic and "w/ Standard Tabs" (for >15 FastTabs grouped into categories).
---

# Authoring a `DetailsMaster` form

The `DetailsMaster` pattern is the **primary method for entering
data** for an entity (per MS Learn). Each record is shown one at a
time on a tabbed detail surface; in modern F&O, the same form also
hosts the grid view (the merged-ListPage model — see below).

Examples in F&O: customer master, vendor master, item master,
parameter records.

Load `dynamics-xpp:xpp-form` first if you haven't.

---

## Modern: list + details merged into ONE form

Per MS official guidance:

> *"Merged List Page and Details Master into a single form. Improves
> performance when moving between a list and details. Enables bulk
> editing in the initial list. Allows for elimination of the list
> page preview pane."*

When the entity has a single corresponding list page, **don't author
a separate ListPage form** — author a DetailsMaster form that carries
both modes (grid view + details view) in its `MainTab` structure.
See the high-level model below.

If your list backs multiple detail forms (e.g. project quotations +
sales quotations in one list), then `dynamics-xpp:xpp-pattern-list-page` is still
the right pick for the list side.

---

## When to pick DetailsMaster vs. siblings

| Use DetailsMaster when... | Use ... instead |
|---|---|
| Entity master (customer, vendor, item, parameters) | — |
| 1:1 list-to-detail (the form carries both views) | — |
| Single record, full edit surface, no lines | — |
| Header + lines (transactional document) | `dynamics-xpp:xpp-pattern-details-transaction` |
| Master/detail with persistent side list | `dynamics-xpp:xpp-pattern-simple-list-details` |
| List backs MULTIPLE detail forms | `dynamics-xpp:xpp-pattern-list-page` (separate from the detail) |
| Tabbed parameter setup form | `dynamics-xpp:xpp-pattern-table-of-contents` |

---

## Two variants

### Variant 1: Details Master (basic — default)

Use as the default. FastTabs for grouping the fields. The form
arrives into a specific record from the grid view; the user moves
between records via the navigation list on the side.

### Variant 2: Details Master w/ Standard Tabs

Use when the entity requires **more than 15 FastTabs** that can be
grouped into categories. Adds a `CategoryTab` layer above the
FastTabs — traditional tabs at the top, FastTabs within each category.

MS reference: `HcmWorker` (the worker master, which has dozens of
FastTabs grouped into "Personal", "Employment", "Compensation",
etc.).

Per MS: *"Master Details forms that previously used the TOC
extension should now use the Master Details w/Standard Tabs
pattern."* — i.e. don't reach for `TableOfContents` to add tabs to
a master form; use this variant instead.

---

## Structural shape (per MS official model)

### Variant 1: Details Master (basic)

```
Design
├── ActionPane (ActionPane)
├── SidePanel (Group)              ← navigation list
│   ├── QuickFilter
│   ├── CustomFilters (Group) [Optional]
│   └── NavigationList (Grid, Style=List)
└── MainTab (Tab ShowTabs=No)      ← carries both modes
    ├── DetailsTabPage (TabPage)   ← details view
    │   ├── TitleGroup (Group)
    │   │   ├── HeaderTitle (String)
    │   │   └── EntityStatus (Group) [Optional]
    │   │       └── StatusFields (1..N)
    │   └── DetailsTab (Tab Style=FastTabs)
    │       └── DetailsTabPage (TabPages, repeats 1..N)
    └── GridTabPage (TabPage)      ← list view (merged ListPage)
        ├── CustomFilterGroup (Group)
        │   ├── QuickFilter
        │   └── OtherFilters ($Field) [0..N]
        ├── MainGrid (Grid)
        └── MainGridDefaultAction (CommandButton)
```

### Variant 2: Details Master w/ Standard Tabs

Same shape, but the FastTabs are wrapped in `CategoryTab` (Standard
Tabs) so they group into 3..N categories:

```
DetailsTabPage (TabPage)
├── TitleGroup (Group)
└── CategoryTab (Tab Style=Tabs)
    └── CategoryTabPage (TabPages repeats 3..N)
        ├── TabHeader (Group)
        └── DetailsTab (Tab Style=FastTabs)
            └── DetailsTabPage (TabPages repeats 1..N)
```

### Required BP-warning resolutions

1. `Design.Caption` not empty.
2. Form referenced by at least one menu item.
3. `TabPage.Caption` not empty.

Key `Design` properties:

- `Pattern` — `DetailsMaster`
- `PatternVersion` — `1.1`
- `Style` — `DetailsFormMaster` (note: not `DetailsMaster` — the
  style name differs from the pattern name)
- `Caption`, `DataSource`, `TitleDataSource` — set normally.

---

## Title fields and the detail-title container

DetailsMaster forms get a title strip at the top showing the record's
identity. The XML carries:

- A control with `<Style>TitleField</Style>` — the visible title
  text (typically the record's name or ID).
- A container with `<Style>DetailTitleContainer</Style>` wrapping the
  title-field control(s).

Without these, the form loads but the title bar is empty.

---

## Sub-pattern names used inside

- **`FieldsFieldGroups`** — the per-tab-page content group; signals
  it will host field/field-group references.
- **`SidePanel`** — the left navigation pane.
- **`List`** — the inner list style.
- **`DetailTitleContainer`**, **`TitleField`** — title strip components.

---

## Form class

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

Standard `FormRun`. Some specialized master forms in shipped F&O use
`FormDetailsTransactionExtended` even though they're "master" forms
(legacy naming). For new code, plain `FormRun` is the default.

---

## Datasources

A single master datasource is the norm. Add joined datasources for
fields displayed in tabs that come from related tables (address,
contact info, etc.). The joins use `JoinSource` + `LinkType` on the
child datasources (see `dynamics-xpp:xpp-form`).

---

## UX guidelines (from MS Learn)

- **No duplicate `New`/`Delete` buttons** — the framework provides
  View/Edit, New, Delete, Save, Refresh, Attachments, and Export to
  Excel actions; don't add explicit app buttons for these unless
  you've removed the foundation-provided one.
- **Use FastTabs** to group fields, not traditional tabs (unless
  you're using the Standard Tabs variant for >15 FastTabs).
- **First FastTab's content fully visible without scrolling** in the
  default state.
- **Page title format:** `<ID> : <Description>`. Plural in the menu;
  individual record title in the form.
- **Navigation list:**
  - Don't exceed 3 lines per row.
  - Typically just ID + Description.
  - At least 2 fields.
- **Grid view:**
  - 2-15 fields. Include all mandatory fields so records can be
    created in the grid.
  - First column: ID (transactional) or Name (master).
  - A linked field opens the details for the selected record.
  - Quick Filter defaults to the most likely filter field.

---

## Commonly used sub-patterns (from MS)

- **Fields and Field Groups** — per-FastTab fields-only sections.
- **Toolbar and List** — FastTabs containing a grid + actions.
- **Toolbar and Fields** — FastTabs containing fields + actions.
- **Nested Simple List and Details** — embedding SL+D in a FastTab.
- **Custom Filter Group** — the grid view's filter section.

See `dynamics-xpp:xpp-form-subpatterns` for the full reference.

---

## Gotchas

- **`Style=DetailsFormMaster`, not `DetailsMaster`.** Pattern name and
  style name diverge here.
- **Title strip is required.** Pattern validators fail without the
  `TitleGroup` with `HeaderTitle`-styled controls.
- **Navigation list is a small aid**, not the main UI. Most user
  interaction is in the detail FastTabs.
- **Modern form carries BOTH views.** The merged-ListPage model
  is the default for new entity-master forms.
- **Don't add app buttons for foundation actions.** New/Delete/Save/
  Refresh/Attachments/Export are provided by the framework.
- **`PatternVersion=1.1`.**

---

## Supporting files

- `examples/example-domain.json` -- **start here.** Typed `CreateFormRequest` for a minimal DetailsMaster. Substitute the backing table and field set, then pass to `xpp_create_form`.
- `examples/example.xml` -- structural reference for reading existing forms. Don't hand-author from it; use the typed example above.


---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-pattern-details-transaction` — header + lines variant.
- `dynamics-xpp:xpp-pattern-simple-list-details` — when a list pane is the
  primary UI.
- `dynamics-xpp:xpp-pattern-list-page` — entry-point list before the detail form.
- `xpp://schema/AxForm` — authoritative XSD.
