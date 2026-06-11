---
name: xpp-pattern-simple-list
description: Use when authoring a SimpleList-pattern form in D365 F&O — a bare list (grid) with no action pane heavy lifting, used for picker dialogs, embedded subforms, and small lookup-edit screens. Pair with dynamics-xpp:xpp-form for the envelope basics. Ship a quick-filter row above the grid; field group reuse keeps the column set lean.
---

# Authoring a `SimpleList` form

The `SimpleList` pattern is the lightweight grid pattern in F&O. It is
not a list page (that's `ListPage`, much heavier) — it's a focused,
read-mostly or edit-in-place table view, typically used for:

- **Picker dialogs** — open from a parent form to choose a value.
- **Embedded subforms** — hosted inside a `DetailsMaster` /
  `DetailsTransaction` form as a child collection.
- **Small lookup/setup forms** — translation tables, mapping tables,
  enum-driven configurations. (Our `conECommMappingStateTranslation`
  form built earlier in this session is a SimpleList — see that
  form's XML on disk for a worked example.)

Load `dynamics-xpp:xpp-form` first if you haven't — it covers the form envelope,
namespace rules, datasources, and form-class basics that every pattern
shares.

---

## When to pick SimpleList vs. a sibling pattern

| Use SimpleList when... | Use ... instead |
|---|---|
| Read-mostly, one row per record, no separate detail pane | — |
| User picks a row from a list and that's the whole interaction | `SimpleListDetails` if each row needs more than 2-3 inline columns |
| Embedded in another form's UI | — |
| Genuine list page for a transactional table with action pane workflow | `dynamics-xpp:xpp-pattern-list-page` |
| Header + lines transaction document | `dynamics-xpp:xpp-pattern-details-transaction` |

If you find yourself wanting tabs, FactBoxes, multiple grids, or a
heavy action pane — that's not SimpleList anymore. Reach for a richer
pattern.

---

## Structural shape (per MS official model)

```
Design
├── ActionPane (ActionPane)
├── Custom Filter (Group)
│   ├── Quick Filter
│   └── OtherFilters ($Field) [0..N]
├── TabularGrid (Grid)
└── Footer (Group) [Optional]
```

### Required BP-warning resolutions

1. `Design.Caption` not empty.
2. `Design.DataSource` not empty.
3. `Grid.Datasource` must be set.
4. Form referenced by at least one menu item.
5. `Design.Datasource` = `Grid.Datasource`.
6. Primary key field of the primary data source's table has
   `IgnoreEDTRelation="Yes"`.
7. Grid must not contain more than 15 fields.

### Top-level controls

1. **`AxFormActionPaneControl`** — an action pane. Required by the
   pattern validator. New/Delete/Edit buttons are provided by the
   framework; **don't add duplicates**.
2. **`AxFormGroupControl`** with `Pattern="CustomAndQuickFilters"` —
   the filter row above the grid. Holds a `QuickFilterControl`.
3. **`AxFormGridControl`** — the grid itself. One row per record;
   columns bind to fields on the datasource.
4. Optional **`AxFormGroupControl`** as a footer below the grid.

`Design` properties to set:

- `Caption` — display label for the form.
- `DataSource` — the primary datasource name.
- `Pattern` — `SimpleList`.
- `PatternVersion` — `1.1` (current).
- `Style` — `SimpleList`.
- `TitleDataSource` — usually same as `DataSource`.

All four of these `<Design>` children must carry `xmlns=""` (the empty
namespace reset). See `dynamics-xpp:xpp-form` for why.

---

## Quick-filter control conventions

The `CustomAndQuickFilters` group wraps a `QuickFilterControl`
extension that auto-builds a search box bound to the grid. Its
extension properties:

- `targetControlName` — the name of the grid (e.g.
  `FormGridControl1`). Required.
- `placeholderText` — empty by default; the runtime uses a generic
  "search" placeholder.
- `defaultColumnName` — the grid-cell control name to filter against
  by default (e.g. `FormGridControl1_CarId`). The user can switch
  columns at runtime; this is just the initial.

If you forget the quick-filter control, the pattern validator will
warn. Add it even on tiny lookup tables.

---

## Datasource shape for SimpleList

Most SimpleList forms have a **single datasource**. Always include
the predefined fields in `<Fields>`:

```xml
<AxFormDataSource xmlns="">
  <Name>Car</Name>
  <Table>Car</Table>
  <Fields>
    <AxFormDataSourceField><DataField>CarId</DataField></AxFormDataSourceField>
    <AxFormDataSourceField><DataField>Description</DataField></AxFormDataSourceField>
    <!-- predefined fields the form runtime needs -->
    <AxFormDataSourceField><DataField>DataAreaId</DataField></AxFormDataSourceField>
    <AxFormDataSourceField><DataField>Partition</DataField></AxFormDataSourceField>
    <AxFormDataSourceField><DataField>RecId</DataField></AxFormDataSourceField>
    <AxFormDataSourceField><DataField>TableId</DataField></AxFormDataSourceField>
  </Fields>
  <ReferencedDataSources />
  <DataSourceLinks />
  <DerivedDataSources />
</AxFormDataSource>
```

Omit `DataAreaId` if the table is `SaveDataPerCompany="No"` (shared).

---

## Grid control + column conventions

The grid is named `FormGridControl1` by Microsoft's tooling
convention. Each column control is named
`FormGridControl1_<FieldName>` — match this so the form reads like
every other F&O list form.

```xml
<AxFormControl xmlns="" i:type="AxFormGridControl">
  <Name>FormGridControl1</Name>
  <Type>Grid</Type>
  <FormControlExtension i:nil="true" />
  <Controls>
    <AxFormControl xmlns="" i:type="AxFormInt64Control">
      <Name>FormGridControl1_CarId</Name>
      <Type>Int64</Type>
      <FormControlExtension i:nil="true" />
      <DataField>CarId</DataField>
      <DataSource>Car</DataSource>
    </AxFormControl>
    <AxFormControl xmlns="" i:type="AxFormStringControl">
      <Name>FormGridControl1_Description</Name>
      <Type>String</Type>
      <FormControlExtension i:nil="true" />
      <DataField>Description</DataField>
      <DataSource>Car</DataSource>
    </AxFormControl>
  </Controls>
  <DataGroup>Identification</DataGroup>
  <DataSource>Car</DataSource>
  <Style>Tabular</Style>
</AxFormControl>
```

The `i:type` on each column control matches the field's primitive:

| Field primitive | Column `i:type` |
|---|---|
| `str` | `AxFormStringControl` |
| `int` | `AxFormIntControl` |
| `int64` | `AxFormInt64Control` |
| `real` | `AxFormRealControl` |
| `Date` | `AxFormDateControl` |
| `utcdatetime` | `AxFormUtcDateTimeControl` |
| Enum (EDT or `AxTableFieldEnum`) | `AxFormComboBoxControl` |
| Boolean (rare; use NoYes instead) | `AxFormCheckBoxControl` |
| Foreign-key reference | `AxFormReferenceGroupControl` |

### `DataGroup` shortcut

Note `<DataGroup>Identification</DataGroup>` on the grid. This tells
the runtime to **also** materialize controls for the table's
`Identification` field group, in addition to any explicitly-listed
column controls. Most often you'll either:

- List columns explicitly (full control over order and types).
- Use a `DataGroup` reference and let the runtime expand the group.

Don't double-bind — if you reference `Identification` AND also list
controls for the same fields, you get duplicate columns.

---

## Form class

The standard skeleton, no special base class:

```xpp
[Form]
public class Form1 extends FormRun
{
}
```

Override `init()` if you need to gate access by parameter, set initial
filters from `element.args()`, or modify control properties at
runtime.

---

## Supporting files

- `examples/example-domain.json` — **start here.** Typed
  `CreateFormRequest` for a minimal SimpleList. Substitute the
  backing table name and field set, then pass to `xpp_create_form`.
- `examples/working-car.xml` — MS's complete working XML example, a
  SimpleList over a `Car` table with `CarId` + `Description`. Use as
  a structural reference when reading existing forms. Don't
  hand-author from it — use the typed example above. The comment at
  the top of the example notes its prerequisites (the
  `Car` table with an `Identification` field group, plus a `CarLabel`
  label).
- `template.xml` — the same structure with `{{PLACEHOLDERS}}` for
  form name, table name, fields, and labels. Copy and substitute.

The MS-shipped Examples file had **one** working example. We ship
that verbatim plus the extracted template — no further variants.

---

## UX guidelines (from MS Learn)

- **Quick Filter defaults to name or description column.**
- **Up to 15 columns** in the list (relaxed from AX 2012's stricter
  limit).
- **No duplicate New/Delete buttons** — framework provides them.
- **Page title in plural form.**
- **No auto-add-record on empty grid.**
- **Multiple selection is allowed** in the grid.
- **Dependent form caption.** When the SimpleList is used as a
  dependent form (launched from another form with context), the
  parent form's record context is automatically shown above the
  form caption — don't model your own page-title group for this.

---

## Commonly used sub-patterns

- **Custom Filter Group** — for the filter section above the grid.
  See `dynamics-xpp:xpp-form-subpatterns`.

---

## Gotchas

- **`xmlns=""` everywhere.** All `<AxFormControl>` elements and the
  inner `<Design>` properties carry it. Without it the deserializer
  errors confusingly. See `dynamics-xpp:xpp-form` for the full rules.
- **`PatternVersion` matches `Pattern`.** Use `1.1` with `SimpleList`.
  Mismatched versions fail pattern validation.
- **Action pane is required.** SimpleList expects an
  `AxFormActionPaneControl` to be present. Putting `<Controls />`
  inside it is fine.
- **Quick filter pointing at a non-existent grid column.** The
  extension's `defaultColumnName` must reference a real grid-cell
  control by name. Typo here causes a runtime error on first user
  interaction.
- **`SaveDataPerCompany="No"` tables.** Omit `DataAreaId` from the
  datasource `<Fields>` block — the field doesn't exist on shared
  tables.
- **15-field limit on the grid.** BPC enforces this.

---

## Worked example from this codebase

Earlier in this session we built `conECommMappingStateTranslation` as
a SimpleList. To see a real-world working SimpleList on this dev box:

```
xpp_get_object_xml("AxForm", "conECommMappingStateTranslation")
```

Compare against `examples/working-car.xml` — same structural shape,
different field set and table.

---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-pattern-simple-list-details` — when each row needs a detail
  pane.
- `dynamics-xpp:xpp-pattern-list-page` — when the form is the primary entry point
  for a transactional table.
- `dynamics-xpp:xpp-table` — the underlying table and its field groups.
- `xpp://schema/AxForm` — authoritative XSD.
