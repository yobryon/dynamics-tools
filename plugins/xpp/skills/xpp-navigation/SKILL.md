---
name: xpp-navigation
description: Use when an AOT object is too big to read or rewrite whole — a large form (SalesTable is ~1.5MB), a wide table (100s of fields/relations), a class with hundreds of methods. Teaches path-addressable navigation: orient with an outline, locate a path with find, zoom one subtree, and edit it surgically — without ever pulling or resending the untouched rest.
---

# Path-addressable navigation (large objects)

Some AOT objects are huge. `xpp_get_form SalesTable` is ~1.5 MB of JSON; a core
table has 180+ fields and 80+ relations; a framework class has hundreds of
methods. Reading one whole — or worse, reading it, mutating it, and patching the
*entire* thing back to change one control — is slow, token-hungry, and risky.

Navigate them the way you navigate a large codebase: **orient → locate → zoom →
edit**, touching only the part you care about. One address vocabulary spans all
four steps.

## The address: a path

Every node has a slash path built from the JSON shape:

- `/<collection>/<member>` — a member of a collection, keyed by its identity
  (its `name`, or `dataField` when it has no name, e.g. table-index fields and
  form-datasource fields).
- `/<singleton>` — a structural wrapper addressed by its property name
  (`/design`, `/sourceCode`).
- nest freely: `/design/controls/Grid/controls/Grid_Name`,
  `/relations/PaymTerm/constraints/PaymTermId`, `/sourceCode/methods/validateWrite`.

Paths are stable across reads (unlike the rendered form's runtime ref-ids) and
are the *same* vocabulary for reading and writing — a path from `find` drops
straight into a `get(atPath=…)` zoom or a `patch_by_path(atPath=…)` edit.

## 1. Orient — outline

`xpp_get_form` / `xpp_get_table` / `xpp_get_table_extension` / `xpp_get_class`
take `outline=true` (+ optional `depth`):

- **depth=0 (default): collection counts only** — the compact orient. Bounded
  even on a 1.5 MB form: e.g. `design{controls:3}`, `sourceCode{methods:175,
  dataSources:29, dataControls:105}`, `{dataSources:37}`; or a table's
  `{fields:185, relations:81, fieldGroups:87, indexes:16, …}`. Structural
  wrappers (`design`, `sourceCode`) show their inner counts inline.
- **depth=1**: list the members one level down (the controls / fields /
  methods themselves, each with its kind and child counts).
- **depth=2**: members + their sub-counts.

On a big object, **don't raise depth to dig — `atPath` into the one area**.
`get_form(name, outline=true, atPath='/design/controls/MainTab', depth=1)`
outlines just that subtree.

## 2. Locate — find

`xpp_find_in_object(axType, name, query?, kind?, dataSource?, dataField?)` walks
the whole tree and returns matching **paths** (breadcrumbs), not content. It's
generic across every AOT type. Provide at least one filter (an unconstrained
find returns nothing — it won't dump the tree):

- `query` — case-insensitive substring of a node's name / dataField.
- `kind` — control kind (`Grid`, `ReferenceGroup`), field type, relationship type.
- `dataSource` / `dataField` — binding filters.

e.g. `find_in_object(AxForm, SalesTable, query='DeliveryAddress', kind='Group')`
→ the handful of delivery-address group paths, 8–10 levels deep, each ready to
zoom or patch.

## 3. Zoom — get at a path

`get_<type>(name, atPath='<path>')` (no `outline`) returns the **full typed
subtree** at that path — the one control group, the one relation with its
constraints, the one method's source. This is how you read local conventions
before editing (naming, which datasource, control kinds).

## 4. Edit — patch by path

`xpp_patch_by_path(axType, name, atPath, op, value?, dryRun?)` edits one node.
You send only `{atPath, op, value}`; the service splices it into current state
and writes — the untouched 95% is never resent. Four ops:

| op | atPath points at | effect |
|----|------------------|--------|
| `set` | the node | replace it wholesale with `value` |
| `merge` | the node (an object) | overlay `value`'s top-level properties; keep the rest. To change something nested, target that deeper path instead |
| `append` | the **collection** | add `value` (a new member object) to it |
| `remove` | the node | delete it (`value` omitted) |

`value` is the node/member in the same domain shape the get tools return (e.g. a
control object with `name`/`kind`/`dataField`, a field with `name`/`fieldType`).

**Dry-run first for anything non-trivial.** `dryRun=true` returns the edited
subtree *without writing*, so you can confirm the splice landed where you
intended (right collection, right siblings) before committing. The real commit
still runs full bridge validation and, for forms, pattern conformance.

## Worked examples

Add a field to a wide table without resending its other 184 fields:
```
xpp_patch_by_path(AxTable, MyTable, atPath='/fields', op='append',
  value={ name:'Memo', fieldType:'String', stringSize:60 })
```

Add a control into a form container (find the container first, then append):
```
xpp_find_in_object(AxForm, MyForm, query='DetailsTab', kind='TabPage')
xpp_patch_by_path(AxForm, MyForm,
  atPath='/design/controls/.../DetailsTab/controls', op='append',
  value={ name:'MyField', kind:'String', dataField:'MyField', dataSource:'MyTable' })
```

Tweak one property on an existing control, leaving its children intact:
```
xpp_patch_by_path(AxForm, MyForm, atPath='/design/controls/Grid/controls/Grid_Name',
  op='merge', value={ allowEdit:false })
```

Read one method of a 600-method class:
```
xpp_get_class(MyClass, atPath='/sourceCode/methods/validateWrite')
```

## Coverage

- **Outline + atPath reads**: AxForm, AxTable, AxTableExtension, AxClass today.
  Other types still read whole (small enough that it rarely matters); the same
  knobs roll onto them incrementally.
- **find** and **patch_by_path** are **generic across all AOT types** already.

## When NOT to use this

Small objects (an enum, an EDT, a menu item) — just `get`/`patch` them whole;
the addressing overhead isn't worth it. Reach for this when size or surgical
precision is the actual problem.
