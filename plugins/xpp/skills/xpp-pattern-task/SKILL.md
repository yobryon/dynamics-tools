---
name: xpp-pattern-task
description: Legacy pattern. Use ONLY for migrating existing AX 2012 forms — DO NOT use for new forms. The Task (a.k.a. "Task Single") pattern is legacy in F&O; new equivalent functionality should use Dialog, Drop Dialog, Simple Details, or Details Master instead. This skill documents the pattern for migration / inspection only.
---

# Authoring a `Task` form

> **⚠ LEGACY — do not use for new forms.**
>
> Per Microsoft's official guidance:
> *"This legacy form pattern is used to display entities. It should
> be used only for migration, not for new forms."*
>
> If you're authoring a new form for what would have been a Task in
> AX 2012, pick a modern equivalent:
> - **Dialog pattern** — for gathering input. (Roadmap: future
>   `xpp:pattern-dialog` skill; consult MS Learn at
>   `https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/dialog-form-pattern`
>   in the meantime.)
> - **Drop Dialog** — for small (<5 fields) action-triggering forms.
> - **Simple Details** — for focused single-record edit surface.
> - **Details Master** — for richer single-entity edit surface.
>
> This skill remains in the fleet because some user-installed code
> still uses Task forms — you'll see them while reading and
> migrating. Use this skill to understand what you're looking at;
> use the modern siblings for new authoring.

The `Task` pattern is a legacy pattern from the AX 2012 era,
historically used for the **focused single-purpose action form** —
modal feel, focused interaction.

Examples in F&O still using Task: Task Recorder framework, some
internal admin tools. Most user-facing tasks have been migrated to
Dialog or Drop Dialog.

Load `dynamics-xpp:xpp-form` first if you haven't.

---

## When to pick Task vs. siblings

| Reading Task code? | Authoring new? |
|---|---|
| You'll see Task on legacy AX 2012-era forms | DO NOT use Task |
| Single focused user action | Use Dialog or Drop Dialog |
| 1-2 input fields + Submit/Cancel | Use Drop Dialog |
| Multi-step guided workflow | Use `dynamics-xpp:xpp-pattern-wizard` |
| Two-stage form with parent + children | DO NOT use Task Double either — pick Dialog or rethink the workflow |
| Full record-editing surface | Use `dynamics-xpp:xpp-pattern-details-master` |

Task forms are **legacy**. When you see Task code, understand it;
when you need new functionality, pick a modern pattern.

---

## Structural shape

Top-level under `Design/Controls`:

1. **`AxFormActionPaneControl`** — usually carrying OK/Cancel-style
   confirmation buttons.
2. **`AxFormTabControl`** — the body. Task often uses tabs for
   logical sections of the action; even one-tab tasks use this
   container for consistency.
3. **`AxFormTabPageControl`** under the tab control — typically a
   group with `Pattern="FieldsFieldGroups"` carrying the input
   fields, or a `Pattern="ToolbarList"` carrying a small grid if the
   task is "pick from a list and act."

Key `Design` properties:

- `Pattern` — `Task`
- `PatternVersion` — `1.1`
- `Style` — typically inherited; check the example.
- `Caption`, `DataSource`, `TitleDataSource` — set normally.

---

## Sub-pattern names used inside

- **`FieldsFieldGroups`** — fields-only content groups.
- **`ToolbarList`** — toolbar above a list/grid (used when the task
  involves picking from a small set).

---

## Form class

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

Standard `FormRun`. Custom logic for the action lives in the
form's methods (typically `OK` button's `clicked()`) and the
datasource's `validateWrite` or write methods.

---

## Datasources

Often **no datasource** if the task is purely transient (collecting
user input, then acting on it via a static method call). When the
task does write data, a single datasource matching the target table
is typical.

---

## Closing the task

A Task form is expected to close itself on completion. Use:

- `element.closeOk()` — task completed successfully; caller treats as
  success.
- `element.closeCancel()` — task aborted by the user.

These are called from the OK/Cancel button's `clicked()` overrides.

---

## Gotchas

- **Don't make tasks too long.** If the user needs 5+ fields or
  multiple stages, switch to `dynamics-xpp:xpp-pattern-wizard` (or split into
  multiple smaller tasks).
- **Wire close handlers.** A task without `closeOk()` / `closeCancel()`
  wired to its OK/Cancel buttons traps the user.
- **No grids unless the task is "pick from a list."** Task is action-
  focused, not browse-focused.
- **All `xmlns=""` rules from `dynamics-xpp:xpp-form` apply.**

---

## Supporting files

- `examples/example-domain.json` -- **start here.** Typed `CreateFormRequest` for a minimal Task. Substitute the backing table and field set, then pass to `xpp_create_form`.
- `examples/example.xml` -- structural reference for reading existing forms. Don't hand-author from it; use the typed example above.


---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-pattern-task-parent-child` — two-stage task with parent +
  children.
- `dynamics-xpp:xpp-pattern-wizard` — multi-step guided sequence.
- `xpp://schema/AxForm` — authoritative XSD.
