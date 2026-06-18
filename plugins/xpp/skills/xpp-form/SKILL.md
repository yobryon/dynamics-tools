---
name: xpp-form
description: Use when authoring or modifying a D365 F&O AxForm — the envelope (datasources, design, controls, parts), the typed authoring surface, pattern-first design, form classes, dependencies, and dispatch into the per-pattern skills. After this skill, load the matching xpp:xpp-pattern-{name} for the form's UX pattern.
---

# Authoring forms (`AxForm`)

Forms are the largest authoring surface in the AOT. A real form is dense
with data sources, controls, methods, and pattern-specific structure.

This skill teaches:

1. The typed authoring surface — `xpp_create_form` / `xpp_get_form` / `xpp_patch_form` and the shape you pass to them.
2. The pattern-first rule and dispatch into the per-pattern skills.
3. Data sources, controls, the form class, field-group reuse.
4. Dependencies to gather before authoring.
5. The raw-XML escape hatch and when to reach for it.

Load `dynamics-xpp:xpp-language` and `dynamics-xpp:xpp-table` first if you haven't — forms depend
on tables for their data, and the class-side reads heavily from the
language foundations.

---

## The canonical authoring path: typed tools

**Use the typed `xpp_create_form` / `xpp_get_form` / `xpp_patch_form`
tools.** They produce on-disk XML that matches Microsoft's canonical
shape, handle the namespace choreography, and guarantee the
property-emission order MS's deserializer requires. The mapper also
runs a round-trip drift detector after every write — any property you
specify that doesn't survive the round-trip surfaces as
`sideEffectWarnings` on the tool response, so silent drops can't hide.

| Tool | Purpose |
|---|---|
| `xpp_create_form(request)` | Create a new AxForm from a typed `CreateFormRequest`. |
| `xpp_get_form(name)` | Read a form as its domain shape — round-trip lossless, including unknown properties via `otherProperties`. |
| `xpp_patch_form(name, patch)` | Apply a partial update. Merge-patch semantics. Collections (`controls`, `dataSources`, `parts`) replace wholesale; to add to a collection, read the form, mutate in-process, and patch back. |

> **Large or unfamiliar form?** Don't read or rewrite the whole control tree.
> Load `dynamics-xpp:xpp-navigation`: `xpp_get_form(outline=true)` to orient,
> `xpp_find_in_object` to locate a control's path, `xpp_get_form(atPath=…)` to
> zoom one subtree, and `xpp_patch_by_path(op='append'|'merge'|'set'|'remove')`
> to add/change/drop a single control without resending the other ~95%. Use
> `dryRun=true` to preview the edit first. (Pattern conformance below still runs
> on a by-path commit.)

### Pattern conformance is automatic

On every form create/patch the tool runs Microsoft's own form-pattern
engine — the one behind the VS designer's "Pattern" tab — against the
form's declared `pattern`. Two things happen for you:

- **Prescribed control properties are stamped automatically.** The
  pattern's required property values — a read-only, single-select
  navigation grid; `FrameType=None` on the framing groups; the grid and
  header sizing modes; and the rest — are applied to the controls before
  the write. **Don't hand-set these.** Authoring them is redundant, and
  if you set one to a value the pattern forbids, the pattern wins (it's
  stamped to the prescribed value and reported back to you).
- **What the stamp can't fix is reported** on the response as
  `patternConformance`:
  - `missing` — required controls the engine can't synthesise (e.g. a
    Quick Filter). **This is the real authoring work — you must add
    these.** Also echoed into `sideEffectWarnings` so it can't be missed.
  - `overrides` — properties you set explicitly that the pattern
    overrode, with the value it won. Informational, not an error.
  - `mismatches` — rare residual the auto-stamp can't resolve.
  `ok: true` with no `missing`/`mismatches` means the form conforms.

So when authoring, **focus on getting the structure right — the correct
controls in the correct slots** — not on low-level layout/styling
property conformance. The engine owns the latter; `missing` tells you
where the former is incomplete. (Full forms only, and only when the form
declares a known pattern. Form *extensions* don't get this yet — the
pattern applies to the merged form, not the extension in isolation.)

### Minimum viable typed request

```json
{
  "name": "MyForm",
  "dataSources": [
    {
      "name": "MyTable",
      "kind": "Root",
      "table": "MyTable",
      "fields": [{"dataField": "MyTableId"}, {"dataField": "Name"}]
    }
  ],
  "design": {
    "caption": "@MyLabels:MyFormCaption",
    "pattern": "SimpleList",
    "patternVersion": "1.1",
    "controls": [
      {"name": "ActionPane", "kind": "ActionPane", "dataSource": "MyTable"},
      {
        "name": "Grid",
        "kind": "Grid",
        "dataSource": "MyTable",
        "style": "Tabular",
        "controls": [
          {"name": "Grid_MyTableId", "kind": "String", "dataField": "MyTableId", "dataSource": "MyTable"},
          {"name": "Grid_Name",      "kind": "String", "dataField": "Name",       "dataSource": "MyTable"}
        ]
      }
    ]
  }
}
```

The pattern skill (`dynamics-xpp:xpp-pattern-{name}`) ships an
`examples/example-domain.json` showing the typed shape for that
pattern — start from there for any non-trivial form.

### Typed control kinds

Control children are polymorphic on `kind`. The typed kinds cover the
common control types:

`Group`, `Tab`, `TabPage`, `Grid`, `Container`, `ActionPane`,
`ActionPaneTab`, `ButtonGroup`, `String`, `Integer`, `Int64`, `Real`,
`Date`, `DateTime`, `Time`, `ComboBox`, `CheckBox`, `RadioButton`,
`ReferenceGroup`, `Button`, `MenuFunctionButton`, `CommandButton`,
`MenuButton`, `DropDialogButton`, `ButtonSeparator`, `StaticText`,
`SegmentedEntry`, `Image`, `Tree`, `ListView`, `ListBox`.

Genuinely uncommon controls (ActiveX, Animate, ManagedHost, Progress,
HTML, TableControl, Guid, etc.) map to `kind: "Other"` with `rawType`
preserving the original on-disk `xsi:type` — round-trip is still lossless.

Every control, data source, design block, and part also carries an
`otherProperties` dict that captures any element we don't explicitly
model — round-trip stays lossless even on the long tail of
layout/styling properties (ElementPosition, FilterExpression,
ArrangeMethod, FrameType, ...).

### Container-vs-leaf rule

Container kinds (`Group`, `Tab`, `TabPage`, `Grid`, `Container`,
`ActionPane`, `ActionPaneTab`, `ButtonGroup`) **carry children via
`controls`**. Data-bound leaf kinds (`String`, `Integer`, `CheckBox`,
etc.) don't — they bind to a `dataSource` + `dataField` and have no
children.

A `Grid` is the only kind that bridges both: it's a container, but its
children are typically data-bound column controls (not other containers).

### Skill dispatch

The per-pattern skills tell you which control hierarchy is required:

| Pattern | Skill |
|---|---|
| `SimpleList` | `dynamics-xpp:xpp-pattern-simple-list` |
| `SimpleListDetails` | `dynamics-xpp:xpp-pattern-simple-list-details` |
| `ListPage` | `dynamics-xpp:xpp-pattern-list-page` |
| `DetailsFormMaster` | `dynamics-xpp:xpp-pattern-details-master` |
| `DetailsFormTransaction` | `dynamics-xpp:xpp-pattern-details-transaction` |
| `WorkspaceOperational` | `dynamics-xpp:xpp-pattern-workspace-operational` |
| `TableOfContents` | `dynamics-xpp:xpp-pattern-table-of-contents` |
| `Wizard` | `dynamics-xpp:xpp-pattern-wizard` |
| `Task` (legacy) | `dynamics-xpp:xpp-pattern-task` |
| `TaskParentChild` (legacy) | `dynamics-xpp:xpp-pattern-task-parent-child` |

`xpp_get_form` also returns a `patternHints` array on the response —
when reading an existing form, the response tells you which skill(s)
to load.

For container sub-patterns inside any of the above (`SidePanel`,
`CustomAndQuickFilters`, `Section Related Links`, etc.), load
`dynamics-xpp:xpp-form-subpatterns`.

---

## The pattern-first rule

**Decide the form's pattern before constructing the request.** A
form's `design.pattern` is not cosmetic — it determines:

- Which controls are required (an action pane, a grid, a header tab).
- Which container nesting is legal.
- Which BPC validators run against the form.
- Which `FormRun`-derived base class is the right inheritance target.

Authoring controls first and trying to slot them into a pattern later
produces forms that look right but fail pattern-conformance validation
on build.

The 10 named F&O UX patterns and when to use each:

| Pattern | Use when |
|---|---|
| `SimpleList` | A read-mostly grid with no detail pane. Pickers, embedded subforms, small lookup-edit screens. Use this when the user just needs to browse and CRUD rows. |
| `SimpleListDetails` | Master/detail with the master pane as a simple list and a details pane for the selected row. Common for lookup/setup forms where each row has more than 2-3 fields. |
| `ListPage` | Read-mostly grid of records with an action pane on top. The default "browse this table" form for transactional/business entities. Often the most-launched form for a given table. |
| `DetailsMaster` | Header-only detail form. Single record, full edit surface, no lines. Used for entity-master records (customer master, vendor master). |
| `DetailsTransaction` | Header + lines. The canonical "transaction document" shape — sales order, purchase order, journal. |
| `Task` | Dialog-style form for a single user action. Modal feel even when hosted in a workspace. Use for "do this one thing" workflows. |
| `TaskParentChild` | Two-step task with a parent record producing/owning child records inline. |
| `Wizard` | Multi-step guided form. Reserved for genuinely sequential workflows; users find wizards heavyweight, so prefer Task when possible. |
| `TableOfContents` | Tabbed parameter/setup form (AR parameters, GL parameters). Tabs map to functional areas. |
| `WorkspaceOperational` | Role-center workspace. Tiles, lists, charts, navigation. Highest-effort pattern to author correctly — only use when building a true workspace landing page. |

Once you've picked, load `xpp:xpp-pattern-{name}` for the per-pattern
typed-request example, structural shape, and gotchas.

---

## Data sources

A single-table form's data source:

```json
{
  "name": "MyLogTable",
  "kind": "Root",
  "table": "MyLogTable",
  "allowCreate": true,
  "allowEdit": true,
  "allowDelete": true,
  "insertIfEmpty": false,
  "fields": [
    {"dataField": "LogId"},
    {"dataField": "Message"}
  ]
}
```

Key properties:

- `name` — usually the same as the `table` for the primary data source.
- `table` — the underlying AxTable.
- `kind` — `Root` (default top-level), `Concrete` (joined child),
  `Derived` (derived-table flavor), `Referenced` (references another
  form's data source).
- `allowCreate` / `allowEdit` / `allowDelete` — CRUD permissions on
  this data source within the form.
- `insertIfEmpty` — should the form pre-populate one empty row?
  Default `true`. Set `false` for setup forms.
- `fields` — explicit list of fields the form pulls. Including a field
  in this list doesn't render a control; it just makes the field
  available to data-bound controls. The form runtime already handles
  predefined fields (`RecId`, `TableId`, `Partition`, `DataAreaId`)
  internally; you don't need to list them.

### Multi-table forms (joins)

For master/detail or header/line shapes, add a child data source with
`kind: "Concrete"` and a `joinSource`. That's it:

```json
{
  "name": "MyLogDetail",
  "kind": "Concrete",
  "table": "MyLogDetail",
  "joinSource": "MyLogTable"
}
```

**F&O forms have no field-pair links.** The join *fields* come from the
**table relation** between the child table and the parent table — so the
prerequisite is that `MyLogDetail` has a relation to `MyLogTable` (a foreign
key relation, normally). With `joinSource` set and that relation present, the
detail grid filters to the selected parent row automatically. You do **not**
specify `this.field = parent.field` anywhere; there is no such surface in F&O
forms (that was an AX 2012 concept).

`linkType` is the join *mode* and defaults to `Delayed`, which is what a
normal master/detail grid wants — you rarely set it. Options: `Delayed`
(lazy, the default), `Active` (re-query on parent change), `Passive` (manual
via X++), `InnerJoin` / `OuterJoin` / `ExistJoin` / `NotExistJoin` (query
joins).

The `links` collection exists only for the advanced case of pinning a
specific relation when a child joins its parent on more than one — each entry
names a *relation*, not a field pair. Standard master/detail never needs it.

---

## The form class

The X++ class declaration lives in `sourceCode.declaration`. Default
when omitted:

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

The `[Form]` attribute marks the class as a form runtime. `extends FormRun`
is the universal default. Specialized base classes exist for specific
patterns — the per-pattern skill identifies the right base.

Common form-class methods you'll override — declared via
`sourceCode.methods`:

```json
{
  "sourceCode": {
    "methods": [
      {"name": "init", "source": "public void init() { super(); /* ... */ }"},
      {"name": "run",  "source": "public void run()  { super(); /* ... */ }"}
    ]
  }
}
```

- `init()` — runs once before the form is displayed. Set up data
  sources, modify control properties based on args.
- `run()` — runs after `init()`, just before display.
- `close()` — runs when the form is closing.

Per-data-source method handlers go in `sourceCode.dataSources`
(entries name a data source by `name` and provide its methods like
`active`, `executeQuery`, `validateWrite`). Per-control handlers go in
`sourceCode.dataControls` (entries name a control by `name` and
provide its methods like `clicked`, `modified`).

Inside the form class:

- `element` is the reserved identifier for the form runtime.
  Methods: `element.args()`, `element.closeOk()`,
  `element.closeCancel()`.
- A data source named `MyDs` is reachable as `MyDs` (the record buffer)
  and `MyDs_DS` (the runtime `FormDataSource` object).
- Controls are reachable by their `Name` property.

See `dynamics-xpp:xpp-class` for deeper class-side details.

---

## Field-group reuse

When you're adding multiple fields from the same table, check whether
the table has a field group containing those fields. If so, bind a
`Group` control to the group with `otherProperties.DataGroup`:

```json
{
  "name": "HeaderGroup",
  "kind": "Group",
  "dataSource": "MyLogTable",
  "otherProperties": {"DataGroup": "Identification"}
}
```

The form runtime materializes one control per field in the group, in
the group's declared order, using each field's EDT-derived label.

**Always prefer field groups** when:

- You're adding 3+ fields from the same data source.
- An existing field group contains all (or most) of those fields.
- The order in the group is the order you want on the form.

If no existing group matches, **author the group on the table first**
(see `dynamics-xpp:xpp-table`), then bind a `Group` control to it.
Don't add controls one-by-one when a group will do.

---

## Form parts (FactBoxes, embedded forms)

Embedded smaller forms go in `parts`:

```json
{
  "parts": [
    {
      "name": "CustomerInfoPart",
      "kind": "Reference",
      "partName": "CustomerInfoPart"
    }
  ]
}
```

Parts are typically `SimpleList` or `DetailsMaster` forms reused as
FactBoxes on the right pane of a list form or as embedded panels on a
detail form.

---

## Dependencies — gather before authoring

A form has hard dependencies on the artifacts it references. Before
authoring, verify:

- **Tables** referenced by data sources exist (`xpp_find_object` with
  `axType="AxTable"`).
- **Labels** referenced by `caption` and control labels exist (or are
  about to be created via `dynamics-xpp:xpp-labelfile`).
- **EDTs** used by table fields exist (the form inherits their labels
  and lookups).
- **Field groups** referenced by `DataGroup` exist on the source
  table.
- **Menu items** referenced by `MenuFunctionButton` controls exist.

If a dependency doesn't exist, **stop and ask** whether to create it
before continuing — silently building a form against missing
dependencies produces a form that fails compile in surprising ways.

`xpp_get_table` against any referenced table is the quickest way to
get the full picture of its fields, field groups, and indexes — which
in turn tells you what the form can reach.

---

## Modifying existing forms — extension vs. patch

For **modifying an existing Microsoft-shipped form**, prefer writing
an `AxFormExtension` over editing the base form directly. Microsoft
application models are sealed; user/customer-layer edits to shipped
forms don't survive platform updates. The `xpp_patch_form` tool will
reject an attempt to patch a form in a different model with an
`out_of_model_update` error pointing at the right extension shape.

For **modifying a form in your own model**, use `xpp_patch_form` with
the partial shape. Merge-patch semantics:

- Scalars (`caption`, `pattern`, etc.) — null means "leave alone",
  non-null replaces.
- Collections (`controls`, `dataSources`, `parts`) — null means "leave
  alone", non-null **replaces wholesale**.

To add one control to an existing collection: read the form with
`xpp_get_form`, append the new control in-process, send the full
collection back via `xpp_patch_form`.

See `dynamics-xpp:xpp-extension` for `AxFormExtension` authoring.

---

## What the typed surface won't catch

The typed mapper handles namespace choreography, property ordering,
and the polymorphic xsi:type discriminator. What it can't catch on its
own:

- **Structural pattern completeness.** The write *does* run the pattern
  engine now (see "Pattern conformance is automatic" above) — it stamps
  the prescribed properties and *reports* missing required controls — but
  it can't author those controls for you. A `ListPage` without an action
  pane, a `DetailsMaster` without its header tab structure: the engine's
  `missing` list flags the gap, but you still have to build it. Consult
  the per-pattern skill before authoring so the structure is right the
  first time.
- **Label resolution.** A `caption` referencing `@MyLabels:Foo` where
  the label doesn't exist passes the write and surfaces as a
  missing-label warning at compile. Create labels first.
- **Data-source/table coherence.** The mapper doesn't verify the
  table named in `table` actually exists, nor that the fields named in
  `dataField` exist on that table.
- **Join validity.** A `joinSource` pointing at a missing data source
  passes the write and fails at form-load.
- **Field-group existence.** A `DataGroup` reference is just a string;
  the mapper doesn't verify the group exists on the source table.
- **Control event-handler signatures.** Wrong signature on a `clicked`
  override is an X++ compile error, not a write error.

---

## Gotchas (typed surface)

- **Pattern + PatternVersion together.** `design.pattern` must carry
  a matching `design.patternVersion` (e.g. `SimpleList` with
  `patternVersion: "1.1"`). The per-pattern skill states the current
  version.
- **Caption is plural for entity master forms.** "Customers" not
  "Customer". Per MS UX guidelines.
- **Data-bound controls need both `dataSource` and `dataField`.**
  Omitting either produces a control that exists but doesn't bind.
- **Display/edit-method columns bind via `dataMethod`, not `dataField`.**
  A grid column whose value comes from a `display` (or `edit`) method on
  the data source's table sets `dataSource` + `dataMethod` (the method
  name, no parens) and **no `dataField`**:
  ```json
  {"name": "EventAge", "kind": "String", "dataSource": "MyDs", "dataMethod": "displayAge"}
  ```
  Pair with `cacheDataMethod: "No"` when the value is volatile (must
  recompute each render). The value-binding cluster — `dataMethod`,
  `cacheDataMethod`, `extendedDataType` and `enumType` (for **unbound**
  controls — those with a `dataSource` but no underlying table field,
  e.g. a workspace page filter), and `arrayIndex` (field-array element)
  — are all first-class fields on the control. Author them through
  `xpp_create_form` / `xpp_patch_form`; don't hand-write control XML into
  `xpp_update_object` — the on-disk element order is contract-significant
  and a misordered element is silently dropped on the round-trip.
- **A `dataMethod` can resolve against THREE method homes — pick the form
  the validator accepts.** Form metadata validation (and so the compiler:
  `DataMethodNotFoundOnDataSource` when you get it wrong) resolves a grid
  column's `dataMethod` against:
  1. **A display/edit method on the bound datasource's TABLE** — bare method
     name; `this` inside the method IS the record. `dataMethod: "displayAge"`.
  2. **A display method authored on the FORM DATASOURCE itself** — also a bare
     method name (named as on the datasource), `dataSource` = that datasource.
     Critical signature difference: a datasource display method's `this` is the
     **datasource**, not the record, so F&O injects the record as a
     **parameter** — the method must accept the table buffer and use it instead
     of `this`:
     ```
     public display DirPartyName consigneeName(ConShipmentTable shipmentTable)
     { return DirPartyTable::find(shipmentTable.ConsigneePartyNumber).Name; }
     ```
     `dataMethod: "consigneeName"`, `dataSource: "ConShipmentTable"`.
     (A datasource method written table-style — no parameter, reaching for
     `this` — won't bind.)
  3. **A display method added by a table-augmentation `[ExtensionOf]` class** —
     write the **qualified `ClassName.methodName`** in `dataMethod`, e.g.
     `dataMethod: "ConTable_InventTransferLine_Extension.conHasMarkedTransOrigin"`,
     `dataSource: "InventTransferLine"`. The bare method name fails (the
     validator looks on the table and the augmentation method isn't there
     unqualified); the `Class.method` form binds it.

  So when the root table is MS-sealed you do NOT have to re-home onto a table
  you own — a form-datasource display method (form #2, with the record
  parameter) or an `[ExtensionOf]` method (form #3, qualified) both work.
- **Don't redeclare controls or data sources in the X++ class.** The
  form runtime adds them to the class scope automatically. See
  `dynamics-xpp:xpp-class`.
- **`Name` matches the file name and the class declaration.**
  Three-place match — `name`, file name on disk, class declaration in
  `sourceCode.declaration`. The mapper enforces the file-name match;
  declaration is on you.

---

## See also

- Per-pattern skills (above) — load the one matching your form's pattern.
- `dynamics-xpp:xpp-form-subpatterns` — catalog of container-level
  sub-patterns used inside the top-level form patterns.
- `dynamics-xpp:xpp-table` — data sources reference tables; field
  groups live there.
- `dynamics-xpp:xpp-extension` — for editing an existing form, write
  an `AxFormExtension` rather than modifying the base.
- `dynamics-xpp:xpp-labelfile` — captions and control labels need
  labels first.
- `dynamics-xpp:xpp-class` — form-class methods and the form runtime.

---

## Raw XML — the escape hatch

If the typed surface can't express what you need (rare — file a
feedback note via `dynamics-xpp:xpp-feedback` so the typed coverage
can be extended), the raw `xpp_create_object("AxForm", xml)` /
`xpp_update_object("AxForm", xml)` tools accept hand-authored XML.

Two things you must know to use the raw path successfully:

### Namespace pattern

The root `<AxForm>` element declares the F&O metadata default namespace:

```xml
<AxForm xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
        xmlns="Microsoft.Dynamics.AX.Metadata.V6">
```

Every element under `<AxForm>` is in the `V6` namespace **unless
explicitly reset to the empty namespace** via `xmlns=""`. The
elements that must reset:

- Direct children of `<SourceCode>` (`Methods`, `DataSources`,
  `DataControls`, `Members`)
- Direct children of `<Design>` (`Caption`, `Pattern`, `PatternVersion`,
  `Style`, `DataSource`, `TitleDataSource`, `Controls`)
- `AxFormDataSource` elements inside top-level `<DataSources>`
- `AxFormControl` elements inside `Design/Controls`, at any nesting

### Property emission order

The MS DataContract deserializer is strict about element order within
each class level. Properties on the base AxFormControl class emit
**before** `FormControlExtension` and `Controls`; properties on the
derived control class (Caption on TabPage, Style on Grid, DataField
on data-bound controls) emit **after** Controls. Get this wrong and
nested controls are silently dropped on the round-trip.

The typed mapper handles both correctly; you only need to reason
about them on the raw path.

`xpp_get_object_xml("AxForm", name)` returns the on-disk shape of any
existing form — useful as a structural reference when the raw path
is unavoidable.
