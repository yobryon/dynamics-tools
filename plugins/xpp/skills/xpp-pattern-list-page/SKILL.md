---
name: xpp-pattern-list-page
description: Use when authoring a ListPage-pattern form in D365 F&O — but FIRST check whether you should be merging list + details into a single Details Master/Transaction form. ListPage is now discouraged when there's a 1:1 correspondence between list and details; current guidance reserves it for forms with NO backing details page or with multiple backing details pages (e.g. project quotations + sales quotations on the same list).
---

# Authoring a `ListPage` form

> **⚠ Modern guidance: prefer merged list + details over a separate
> ListPage when 1:1.**
>
> Per Microsoft's official guidance:
> *"The use of this pattern is now discouraged when there is a 1:1
> correspondence between the List Page and Details page. Current
> guidance is to use this pattern only in other situations, such as
> when list pages have no backing details pages or have multiple
> backing details page (for example, when project quotations and
> sales quotations are shown together in the same List Page)."*
>
> In the modern flow, a List Page and its single corresponding
> Details Master / Details Transaction are **merged into a single
> form**. The grid is the list mode; opening a row switches to
> details mode. Benefits:
> - Better performance when moving between list and details (no
>   form navigation).
> - Bulk editing in the initial list.
>
> The classic ListPage pattern (this skill) remains for the cases
> noted above.

The `ListPage` pattern is the **primary entry-point grid** for a
business entity. Heavy action pane, big grid, custom filters, and
historically the launcher for detail forms via menu items. In
current F&O, reach for it only when:

- The list has no corresponding detail form (you stop at the list).
- The list backs **multiple** detail forms (one row → multiple
  possible details, e.g. project quotations and sales quotations
  unified in one list).

Otherwise: build a Details Master / Details Transaction with grid
mode + details mode in one form.

Load `dynamics-xpp:xpp-form` first if you haven't.

---

## When to pick ListPage vs. siblings

| Use ListPage when... | Use ... instead |
|---|---|
| Primary navigation surface for a transactional entity | — |
| Action pane carries multiple workflow operations | `dynamics-xpp:xpp-pattern-simple-list` if a single grid in a dialog/subform |
| Rows menu-launch into detail forms (Master/Transaction) | — |
| Read-mostly with light in-grid editing | — |
| Each row needs an inline details panel | `dynamics-xpp:xpp-pattern-simple-list-details` |
| Setup/parameter form | `dynamics-xpp:xpp-pattern-table-of-contents` |

ListPage is typically launched from a navigation menu node and is
**read-mostly**. Editing happens by opening a record into a detail
form, not in-place in the grid (though in-place edit is possible).

---

## Structural shape (per MS official model)

```
Design
├── ActionPane (ActionPane)
├── Custom Filter (Group)
│   ├── Quick Filter (Quick Filter)
│   └── OtherFilters ($Field) [0..N]
└── Grid (Grid)
```

### Required BP-warning resolutions

1. `Design.Caption` not empty.
2. Form referenced by at least one menu item.
3. `TabPage.Caption` not empty.
4. `TabPage.DataSource` not empty.
5. Primary datasource has `AllowEdit=No`, `AllowCreate=No`,
   `AllowDelete=Yes`.
6. `Grid.DefaultAction` references the button that opens the child
   form.
7. `Grid.DefaultLabelAction` references the label shown in the grid
   context menu.

### Top-level controls

1. **`AxFormActionPaneControl`** with an `AxFormActionPaneTabControl`
   holding multiple `AxFormButtonGroupControl`s. ListPage action panes
   are **heavy** — multiple tabs, multiple button groups per tab,
   covering navigation, posting, related views, inquiries, etc.
   Buttons are typically `AxFormMenuFunctionButtonControl`
   (menu-item-driven) and `AxFormButtonControl` (custom actions).
2. **`AxFormGroupControl`** with `Pattern="CustomAndQuickFilters"` —
   the filter row above the grid (same as SimpleList).
3. **`AxFormGridControl`** — the records grid.

Key `Design` properties:

- `Pattern` — `ListPage`
- `PatternVersion` — `1.1`
- `Style` — `ListPage`
- `Caption`, `DataSource`, `TitleDataSource` — set normally.

---

## The action-pane sub-pattern

ListPage action panes are richer than SimpleList:

```xml
<AxFormControl xmlns="" i:type="AxFormActionPaneControl">
  <Name>FormActionPaneControl1</Name>
  <Type>ActionPane</Type>
  <Controls>
    <AxFormControl xmlns="" i:type="AxFormActionPaneTabControl">
      <Name>ActionPaneTab1</Name>
      <Controls>
        <AxFormControl xmlns="" i:type="AxFormButtonGroupControl">
          <Name>NavigationGroup</Name>
          <Controls>
            <AxFormControl xmlns="" i:type="AxFormMenuFunctionButtonControl">
              <Name>OpenSalesOrder</Name>
              <MenuItemName>SalesTable</MenuItemName>
            </AxFormControl>
            <!-- ... more menu-item buttons ... -->
          </Controls>
        </AxFormControl>
        <!-- ... more button groups ... -->
      </Controls>
    </AxFormControl>
  </Controls>
</AxFormControl>
```

Button-group naming convention: descriptive (`NavigationGroup`,
`PostingGroup`, `InquiriesGroup`), not auto-generated.

---

## Sub-pattern names used inside

- **`CustomAndQuickFilters`** — the filter group above the grid
  (same as SimpleList).
- **`CustomFilter`** — style for the filter group.

---

## Form class

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

Some shipped list pages use `FormListExtended` or
`FormListPageInteraction`-related base classes; for new forms,
`FormRun` is the default unless you're integrating with the older
list-page-interaction framework.

---

## Datasources

Typically a single primary datasource — the table the list browses.
Reference-datasources may be added for displaying joined fields
(e.g. customer name on a sales-order list).

Set the master datasource's `AllowEdit="No"` if the list is genuinely
read-only; rely on the detail-form menu-item buttons for edits.

---

## Filtering and views

ListPage benefits from supporting **named filters / saved views**:

- Use the `CustomAndQuickFilters` group for the user's ad-hoc filter
  bar.
- Named filters are wired via `AxFormGridControl` properties and the
  form's `init()` method that pre-applies query ranges.

The example demonstrates a "descending sort" filter — useful as a
reference for sort/filter wiring.

---

## UX guidelines (from MS Learn — verify these)

- **Fewer than 15 fields in the grid.**
- **First textual/data column** rendered as a hyperlink to the detail
  form (grid's `DefaultAction` wires this).
- **Quick Filter above the list** by default; uses the most likely
  filter field as default column.
- **No duplicate `New`/`Delete` buttons** — the framework provides
  them. (Same rule as SimpleList / SimpleListDetails.)
- **Link to the List page from the Main Menu** (the form must be
  reachable through navigation).
- **Focus** lands in the Quick Filter when the list opens.

### Page title

- Plural form.
- Primary list pages: title = entity name.
- Secondary list pages: title = activity or status.

### Grid column ordering

- **Transactional entities:** ID field first, then master entity ID
  and Name fields.
- **Master entities:** Name field first, then ID field.

### Pattern changes from AX 2012

- `FormTemplate` / `InteractionClass` are now **optional** when
  building new pages.
- List Page + Details Master/Transaction **merged into single form**
  when there's a 1:1 correspondence (see warning at top).
- **Preview pane has been eliminated.** If migrating, either remove
  the preview or split it into FactBoxes.

---

## Commonly used sub-patterns

- **Custom Filter Group** — for the filter section above the grid.
  See `dynamics-xpp:xpp-form-subpatterns` when written.

---

## Gotchas

- **Check the 1:1 question first.** If your list backs exactly one
  detail form, the modern answer is a merged form (Details Master /
  Transaction with grid + details mode), not a ListPage.
- **Heavy action pane is the differentiator** (vs. SimpleList). A
  ListPage with only one or two buttons is closer to a SimpleList.
- **Menu-item buttons drive navigation.** Wire `MenuItemName` on the
  buttons.
- **The grid is the focus.** Don't add static text or decoration —
  ListPage layout reserves space for the grid.
- **`Style=ListPage` (matches pattern name here).**
- **All `xmlns=""` rules from `dynamics-xpp:xpp-form` apply.**

---

## Supporting files

- `examples/example-domain.json` — **start here.** Typed
  `CreateFormRequest` for a minimal ListPage. Substitute the backing
  table and field set, then pass to `xpp_create_form`.
- `examples/example.xml` — MS-shipped working ListPage
  (`CLI_DescendingSortListPage`, 320 lines). Use as a structural
  reference when reading existing forms. Don't hand-author from it —
  use the typed example above. Demonstrates the heavy action pane,
  custom filter bar, and grid.

---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-pattern-simple-list` — lighter grid pattern; use for
  dialogs/subforms.
- `dynamics-xpp:xpp-pattern-details-master` / `dynamics-xpp:xpp-pattern-details-transaction` —
  what your menu-item buttons typically launch into.
- `xpp://schema/AxForm` — authoritative XSD.
