---
name: xpp-pattern-table-of-contents
description: Use when authoring a TableOfContents-pattern form in D365 F&O — a tabbed parameter/setup form with vertical tabs on the left, each tab carrying a functional area's settings. Canonical for the *Parameters forms (Accounts Receivable parameters, GL parameters, Inventory parameters, etc.).
---

# Authoring a `TableOfContents` form

The `TableOfContents` pattern is the **tabbed parameter/setup form**.
Vertical tabs run down the left; each tab is one functional area of
settings; the form is typically backed by a single-row parameters
table.

Canonical examples in F&O: `CustParameters` (AR parameters),
`LedgerParameters` (GL parameters), `InventParameters` (inventory
parameters), and many module-specific setup forms.

Load `dynamics-xpp:xpp-form` first if you haven't.

---

## When to pick TableOfContents vs. siblings

| Use TableOfContents when... | Use ... instead |
|---|---|
| Setup/parameters form for a module | — |
| Backed by a single-record parameters table | — |
| Functional areas naturally divide into tabs | — |
| Master-record detail (multiple instances) | `dynamics-xpp:xpp-pattern-details-master` |
| Header + lines transaction document | `dynamics-xpp:xpp-pattern-details-transaction` |
| Multi-step workflow | `dynamics-xpp:xpp-pattern-wizard` |

The defining trait: **one record** (the parameters singleton),
**multiple tabs** (the functional sections). If your form is browsing
many records or editing line-shaped data, this isn't the right
pattern.

---

## Structural shape (per MS official model)

```
Design
└── Tab (Style=VerticalTabs)
    └── TabPage (repeats 1..N times)
        ├── Title (Group)
        │   ├── MainInstruction (StaticText)
        │   └── SecondaryInstruction (StaticText) [Optional]
        └── Body (Group) | FastTabContent (Tab)
```

Each `Body` group uses one of the container sub-patterns listed
below.

### Required BP-warning resolutions

1. `Design.Caption` not empty.
2. Form referenced by at least one menu item.
3. `TabPage.Caption` not empty.
4. `TabPage.DataSource` not empty.
5. `StaticText.Text` not empty.

### Top-level controls

1. **`AxFormTabControl`** with `Style=VerticalTabs` — vertical tabs
   on the left, one per functional area.
2. Each `AxFormTabPageControl` contains:
   - A `Title` group with a mandatory `MainInstruction` static text
     and an optional `SecondaryInstruction` static text.
   - A `Body` group OR a `FastTabContent` tab — for the section's
     actual fields/grids/etc.

Key `Design` properties:

- `Pattern` — `TableOfContents`
- `PatternVersion` — `1.1`
- `Style` — `TableOfContents`

---

## Sub-pattern names and styles used inside

- **`MainInstruction`** — section heading text style.
- **`TOCTitleContainer`** — title strip container at the top of each
  tab page.
- **`Strip`** — toolbar strip styling.
- **`ToolbarFields`** — toolbar above a fields section.
- **`FieldsFieldGroups`** — content groups hosting field/field-group
  controls.

---

## Form class

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

Standard `FormRun`. Parameter forms typically use the cache pattern
on their `init()` — load the singleton parameters record via the
table's `find()` method, then bind to it.

---

## Datasources

Single datasource backed by the **parameters table** — typically a
small table with `SaveDataPerCompany="Yes"` (per-company parameters)
and a known singleton row (RecId=1) that the form loads via `find()`
in `init()`.

```xml
<AxFormDataSource xmlns="">
  <Name>MyParameters</Name>
  <Table>MyParameters</Table>
  <AllowCreate>No</AllowCreate>
  <AllowDelete>No</AllowDelete>
  <!-- ... -->
</AxFormDataSource>
```

Disable Create/Delete — the singleton is auto-created via the table's
`find()`, never by the form.

---

## Tab structure conventions

- **First tab is usually "General"** with the most-used settings.
- **Number sequences** typically get their own tab.
- **Updates / Posting** for forms controlling document workflow.
- **Module-specific tabs** for specialized settings.

Order tabs by **user frequency of access**, not alphabetically.

---

## UX guidelines (from MS Learn)

- **Secondary instruction**: a complete, concise sentence in sentence
  case with end punctuation.
- **Tab order = typical entry sequence.** Vertical tabs imply an
  order of completion; arrange them that way.
- **First tab highlighted on open** (unless launched in a specific
  task context).
- **Content patterns per section** — pick one of:
  - **Simple List** content → use that sub-pattern's guidelines.
  - **Simple List and Details** content → use **Nested Simple List
    and Details** sub-pattern.
  - **Simple Details** content → use **Toolbar and Fields**
    sub-pattern.
  - FastTabs follow general FastTab guidelines.
- **A ToC form must NOT have:**
  - Application actions on a standard ActionPane. **Only framework
    actions** allowed on the form-level ActionPane. Module-specific
    actions belong on the tab page they relate to (or on the first
    tab's toolbar).
  - FactBoxes.
  - Standard tabs on a ToC tab page.

### Where to put "Global" buttons

Per MS FAQ: when you need a "global" button (e.g. to initialize data
or sync between services), put it:

- On the tab page the action is most closely related to, OR
- If no place exists, on a toolbar on the first tab page.

Never on the standard ActionPane — that's reserved for framework
actions in ToC forms.

---

## Commonly used sub-patterns

Each tab page's `Body` group uses one of:

- **Fields and Field Groups** — fields-only sections.
- **Toolbar and List** — grid with action buttons above.
- **Toolbar and Fields** — fields with action buttons above.
- **Nested Simple List and Details** — embed an SL+D in a section.
- **Tabular Fields** — structured layout of fields (e.g. totals).
- **List Panel** — transfer-style two-list selection.

See `dynamics-xpp:xpp-form-subpatterns`.

---

## Gotchas

- **Don't put app actions on the form-level ActionPane.** ToC reserves
  it for framework actions only.
- **Singleton enforcement.** The parameters table needs a static
  `find()` (or equivalent) that auto-creates the row if absent.
  Without it, the form opens against an empty datasource.
- **AllowCreate/AllowDelete=No.** The singleton is not user-managed.
- **`Style=TableOfContents` matches the pattern name.**
- **`StaticText.Text` is mandatory** on the `MainInstruction`. Empty
  fails BPC.
- **No FactBoxes.** Unlike DetailsMaster, ToC explicitly forbids them.
- **All `xmlns=""` rules from `dynamics-xpp:xpp-form` apply.**

---

## Supporting files

- `examples/example-domain.json` -- **start here.** Typed `CreateFormRequest` for a minimal TableOfContents. Substitute the backing table and field set, then pass to `xpp_create_form`.
- `examples/example.xml` -- structural reference for reading existing forms. Don't hand-author from it; use the typed example above.


---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-table` — the underlying parameters table needs a `find()`
  pattern.
- `dynamics-xpp:xpp-pattern-details-master` — when there are multiple records, not
  a singleton.
- `xpp://schema/AxForm` — authoritative XSD.
