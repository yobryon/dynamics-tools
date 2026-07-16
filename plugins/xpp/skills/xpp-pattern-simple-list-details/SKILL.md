---
name: xpp-pattern-simple-list-details
description: Use when authoring a SimpleListDetails-pattern form in D365 F&O — master/detail layout with the master pane on the left and a details pane for the selected row on the right. Three variants — List Grid (default), Tabular Grid, Tree — chosen by the master pane's content shape. Covers FactBox use, header group layout, view-mode default, and the framework-provided New/Delete/Edit buttons.
---

# Authoring a `SimpleListDetails` form

The `SimpleListDetails` (SL+D) pattern is used to **maintain data for
entities of medium complexity**. Per MS:

> *"The Simple List and Details pattern is prescribed when these
> conditions are met: the underlying data has more than six fields,
> and there are between zero and five child data collections."*

A list (master) on the left, a details pane on the right. Click a
row in the list, see/edit its fields in the details pane.

Load `dynamics-xpp:xpp-form` and `dynamics-xpp:xpp-pattern-simple-list` first if you haven't.

---

## When to pick SimpleListDetails vs. siblings

| Use SimpleListDetails when... | Use ... instead |
|---|---|
| Entity has 6+ fields, 0-5 child data collections | — |
| Each row needs a multi-field detail pane | `dynamics-xpp:xpp-pattern-simple-list` if entity has <6 fields |
| Setup / lookup data with rich record detail | `dynamics-xpp:xpp-pattern-list-page` for transactional entry points |
| Single-record edit surface (no list) | `dynamics-xpp:xpp-pattern-details-master` |
| Header + lines transactional doc | `dynamics-xpp:xpp-pattern-details-transaction` |

---

## Three variants

Pick the variant based on what the master (left) pane looks like.
Microsoft's official tooling tracks them as separate sub-patterns of
the same parent.

### Variant 1: List Grid (default — use this unless you have a specific reason to pick another)

The master pane is a **list-style grid**: 2-3 fields per record,
rendered with multiple lines per row (typically ID on the first line,
description on the second). This is the recommended default.

Use when:

- 2-3 fields per record in the navigation list is enough.
- Each row reasonably fits in a list-style cell (not so many fields
  that the rows take more than 3 lines).
- Fields in the list are of different types (string + date + amount;
  not three dates in a row).

This is what our example.xml ships — the `Form: PaymTerm` from MS
docs is the canonical pattern reference.

### Variant 2: Tabular Grid

The master pane is a **tabular grid** — a standard rows-and-columns
grid like in a ListPage. Use when:

- 4-5 fields are needed in the list part (more than the List Grid's
  preferred 2-3).
- Multiple fields of the same type that the user wants to compare
  across rows (e.g., three date fields — effective dates, route step
  numbers).
- The grid pattern carries a `VerticalSplitter` between the master
  and detail to let users resize.

Per MS UX guidelines: tabular grid is "an acceptable alternative in
some situations" but not generally recommended. If used:

- The tabular grid is read-only (the navigation grid's `AllowEdit=No`
  and the other prescribed grid properties are stamped automatically on
  write — see "Pattern conformance is automatic" in `dynamics-xpp:xpp-form`).
- Use the `VerticalSplitter` between master and detail.

MS reference form: `ExchangeRate`.

### Variant 3: Tree

The master pane is a **tree control** — hierarchical navigation
instead of a flat list. Use when the underlying data is genuinely
hierarchical (parent-child relationships in the master records).
This is rare.

MS reference form: `FiscalCalendars`.

The Tree variant also carries a `VerticalSplitter` between master
and detail.

---

## Structural shape (per MS official model)

High-level structure under `Design`:

```
Design
├── ActionPane
├── NavigationList (Group)
│   ├── Quick Filter
│   ├── CustomFilterGroup (Group) [Optional]
│   └── ListStyleGrid (Grid) | Tree | TabularGrid (Grid)   ← pick by variant
├── VerticalSplitter (Group)   ← only for Tree / TabularGrid variants
├── DetailsHeader (Group)       ← list fields appear here first, in list order
└── DetailsTab (Tab)            ← FastTabs for the rest of detail fields
```

### Required BP-check resolutions

1. `Design.Caption` = the label used on the table's `Name` property
   (plural form).
2. `Design.Datasource` = `Grid.Datasource`.
3. Primary datasource `InsertIfEmpty` = `No`.
4. Primary `ActionPane.DataSource` = `Grid.Datasource`.
5. `Grid.Datasource` = the primary data source.

### Top-level controls

1. **`AxFormActionPaneControl`** with `AxFormActionPaneTabControl`
   inside — buttons grouped into tabs.
2. **`AxFormGroupControl`** (the NavigationList) holding the
   list/tree/tabular-grid — **this is the group styled `SidePanel`**, which
   docks the master pane to the left.
3. **`DetailsHeader`** (an `AxFormGroupControl`, `FieldsFieldGroups`) and
   **`DetailsTab`** (an `AxFormTabControl`, `FastTabs`) — these are
   **top-level PEERS** of the action pane and nav group (direct children of
   `Design`). Do NOT wrap them in a `Details` group, and do NOT style them
   `SidePanel`. The pattern engine keys on this peer arrangement; wrapping
   them breaks the two-pane layout at runtime (BP + compile still pass).

Key `Design` properties:

- `Pattern` — `SimpleListDetails`
- `PatternVersion` — `1.3` (the active version at time of writing — see
  "Pattern versions move" below; trust the tool over this number)
- `Style` — `SimpleListDetails`
- `Caption`, `DataSource`, `TitleDataSource` — set normally.

---

## Sub-pattern names used inside

You'll see these in `<Pattern>` properties on inner controls:

- **`FieldsFieldGroups`** — inside detail panel groups, indicates the
  group will host field/field-group controls (vs. a custom layout).
- **`ToolbarFields`** — header strip with toolbar buttons above a
  fields section.
- **`SidePanel`** — the **navigation/master group** style (docks the list
  pane to the left). It goes on the nav group, NOT on the details.
- **`List`** — the left-pane list style.

These are pattern-conformance hints to the form runtime and BPC — set
them on the containers as the example shows.

---

## Form class

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

Standard `FormRun` base. No specialized class needed.

---

## Datasources

Typically two datasources for SimpleListDetails:

- **Master datasource** — the table backing the list/grid.
- (Optional) **Joined detail datasource** — when the right pane shows
  fields from a related table joined to the master record.

If everything is on one table, one datasource is fine; the details
pane just displays more fields of the selected row.

---

## UX guidelines (from MS Learn — verify these when building)

- **Form caption** describes the entity in **plural** form.
- **No duplicate New/Delete buttons.** The framework provides these on
  the action pane; don't add your own.
- **View mode is the default.** F&O switched away from
  edit-mode-by-default for SL+D in current versions.
- **Quick Filter defaults to name or description column.**
- **Max 2 custom filter fields** in the filter group.
- **List rows: <= 3 lines per record** in the list-style grid (typically
  just ID + Description is enough).
- **Detail header**: the list's fields are the *first* fields in the
  detail header group, in the same order as in the grid. This is
  load-bearing — it lets users edit and see the labels of the list
  fields.
- **SL+D forms must NOT have standard tabs to group fields** — use
  FastTabs in the details body.
- **No auto-add-record on empty grid/tree.**
- **FactBoxes are allowed** in SL+D (this is a difference from AX 2012).
- **No more `BodyGroup` container** — the form structure was simplified.

---

## Commonly used sub-patterns (from MS)

The detail panes inside SL+D forms typically use these sub-patterns
on their inner containers:

- **Fields and Field Groups** — for fields-only sections.
- **Toolbar and List** — when a section has a grid with action buttons
  above.
- **Toolbar and Fields** — when a section has fields with action buttons
  above.
- **Nested Simple List and Details** — to embed a simpler SL+D inside
  a tab of this form.

See `dynamics-xpp:xpp-form-subpatterns` for the full reference (when written).

---

## Gotchas

- **The action pane needs tab structure.** SimpleListDetails uses
  `AxFormActionPaneTabControl` inside the action pane (not just an
  empty `AxFormActionPaneControl`). The example shows the right shape.
- **`SidePanel` style on the nav/list group (NOT the details).** It docks
  the master list to the left so the details fill the right. Putting it on a
  details wrapper instead (or nesting `DetailsHeader`/`DetailsTab` under one)
  crams the details into a narrow strip — a runtime-only break that BP and
  compile both pass. Verify any new form's layout via chrome-mcp.
- **List binding.** The grid binds to the master datasource; detail
  controls bind to the same datasource (showing more fields) or a
  joined datasource.
- **Pattern versions move — trust the tool, not this number.** The platform
  versions its patterns, and retires old ones with each update. `1.3` is the
  active `SimpleListDetails` version at time of writing, and it's the version
  that ALLOWS a details FastTabs tab — `1.1` does not, and is retired.
  `xpp_create_form` / `xpp_patch_form` read the active version live from MS's
  catalog: if you declare a retired one, the response leads its
  `sideEffectWarnings` with "declared pattern version 'X' is NOT active
  (active: Y)". **Believe that over any version hard-coded here.**
  Corollary for compile errors: if the compiler emits
  `BPUpgradeMetadataFormPatternVersionNotActive` (a *warning*), fix that FIRST —
  the `PatternControlNotAllowed` / `PatternPropertyRequiredValue` *errors*
  alongside it are usually downstream of the stale version, and chasing them
  leads you to restructure a form that was structurally fine.
- **All namespace rules from `dynamics-xpp:xpp-form` apply** — `xmlns=""` on
  controls and inner `<Design>` properties.
- **Detail-header field order matches grid order.** First fields in
  the detail header must be the list fields, same order.
- **Prevent unintended hyperlinks**: for fields where the EDT relation
  would auto-render a hyperlink in the detail header, set
  `IgnoreEDTRelation=Yes` (on the table field) or — since Platform
  Update 17 — `EnableFormRef=No` (on the form input control).

---

## Supporting files

- `examples/example-domain.json` — **start here.** A typed
  `CreateFormRequest` for a minimal SimpleListDetails form. Substitute
  names, the backing table, and the field set for your case and pass
  it to `xpp_create_form`. The typed shape is what the dynamics-xpp
  authoring tools accept and what the mapper emits in MS canonical
  on-disk shape.
- `examples/example.xml` — MS-shipped working SimpleListDetails form
  (`CLIPatterns_SimpleListAndDetails_ListGrid`, 602 lines).
  **Reference for cross-checking the on-disk shape** when reading
  existing forms or debugging structural questions. Don't hand-author
  from it — use the typed example above. Note that this file's
  property emission order matches what `xpp_create_form` produces;
  divergences in your output XML mean something has gone wrong.

---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-pattern-simple-list` — when only a list is needed.
- `dynamics-xpp:xpp-pattern-details-master` — when only a single-record detail is
  needed (no list).
- `xpp://schema/AxForm` — authoritative XSD.
