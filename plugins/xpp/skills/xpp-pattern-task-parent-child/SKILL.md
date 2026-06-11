---
name: xpp-pattern-task-parent-child
description: Legacy pattern. Use ONLY for migrating existing AX 2012 forms — DO NOT use for new forms. The TaskParentChild (a.k.a. "Task Double") pattern is legacy in F&O; new functionality should use Details Transaction, Dialog, or a refactored workflow instead. This skill documents the pattern for migration / inspection only.
---

# Authoring a `TaskParentChild` form

> **⚠ LEGACY — do not use for new forms.**
>
> Per Microsoft's official guidance:
> *"This legacy form pattern is used to display transaction entities.
> It should be used only for migration, not for new forms."*
>
> If you're authoring a new form for what would have been a Task
> Double in AX 2012, pick a modern equivalent:
> - **Details Transaction** — for any genuine "header + lines"
>   business document. This is almost always the right answer.
> - **Dialog** — when both the parent and children fit in a dialog
>   shape (collecting input + acting).
> - **Refactor the workflow** — sometimes the right answer is to
>   split parent and children into separate user flows instead of
>   forcing them into one form.
>
> See [[dynamics-xpp:xpp-pattern-task]] for the same legacy callout on the
> single-record sibling.

The `TaskParentChild` pattern is a legacy pattern. Historically it
was the **two-stage task**: a parent record is created/selected,
and child records belonging to it are managed inline, with the form
split horizontally — parent on top, children on the bottom.

You'll encounter it on legacy F&O forms during migration work. For
new authoring, use the modern equivalents above.

Load `dynamics-xpp:xpp-form` first if you haven't.

---

## When to pick TaskParentChild vs. siblings

| Use TaskParentChild when... | Use ... instead |
|---|---|
| Two related grids: parent + children | — |
| Action involves creating both parent and children | — |
| Both records are persisted by the task | — |
| Single focused action without child records | `dynamics-xpp:xpp-pattern-task` |
| Full header + lines business document | `dynamics-xpp:xpp-pattern-details-transaction` |
| Multi-step guided workflow | `dynamics-xpp:xpp-pattern-wizard` |

The differentiator from `DetailsTransaction`: TaskParentChild is
**purpose-built and modal-feeling**, not the primary edit surface for
the entity. DetailsTransaction is the main document form for sales
orders / POs; TaskParentChild is for a focused task that happens to
need both parent and child rows.

---

## Structural shape

Top-level under `Design/Controls`:

1. **`AxFormActionPaneControl`** — OK/Cancel-style buttons.
2. **`AxFormTabControl`** containing the parent section — typically
   a single tab page hosting a grid with `Pattern="ToolbarList"`.
3. **`AxFormGroupControl`** with `Style="SplitterHorizontalContainer"`
   — the splitter that separates parent (top) from children (bottom).
4. **`AxFormTabControl`** containing the child section — another
   single tab page with a `ToolbarList` grid for the children.

Key `Design` properties:

- `Pattern` — `TaskParentChild`
- `PatternVersion` — `1.1`
- `Style` — typically inherits.
- `Caption`, `DataSource`, `TitleDataSource` — set normally.

---

## Sub-pattern names used inside

- **`ToolbarList`** — toolbar above a grid; used on both parent and
  child grids.
- **`SplitterHorizontalContainer`** — the visual splitter between
  parent and child panes.

---

## Form class

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

Standard `FormRun`.

---

## Datasources

Two datasources at minimum:

- **Parent datasource** — the parent table.
- **Child datasource** — the child table, with `JoinSource=Parent`
  and `LinkType` set (usually `Active` for tight parent-child
  navigation, since the user is creating both records in one
  session).

---

## Closing the task

Same as plain Task — `element.closeOk()` on success,
`element.closeCancel()` on abort. Validate that both parent and
children are consistent before `closeOk()`.

---

## Gotchas

- **The splitter is required.** The visual separation between parent
  and child panes is provided by `SplitterHorizontalContainer`.
  Without it, the panes don't divide cleanly.
- **Child datasource relation.** The child table must carry a
  relation back to the parent; the form just names it via
  `JoinSource`.
- **OK enables only when both sides are valid.** Wire the OK
  button's enabled-state to the parent's `validateWrite` and the
  presence of at least one valid child if business rules require it.
- **All `xmlns=""` rules from `dynamics-xpp:xpp-form` apply.**

---

## Supporting files

- `examples/example-domain.json` -- **start here.** Typed `CreateFormRequest` for a minimal TaskParentChild. Substitute the backing table and field set, then pass to `xpp_create_form`.
- `examples/example.xml` -- structural reference for reading existing forms. Don't hand-author from it; use the typed example above.


---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-pattern-task` — single-stage variant.
- `dynamics-xpp:xpp-pattern-details-transaction` — header+lines for primary
  business documents.
- `dynamics-xpp:xpp-pattern-wizard` — when the workflow is genuinely multi-step.
- `xpp://schema/AxForm` — authoritative XSD.
