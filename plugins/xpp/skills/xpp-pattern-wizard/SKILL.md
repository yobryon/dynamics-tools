---
name: xpp-pattern-wizard
description: Use when authoring a Wizard-pattern form in D365 F&O — a multi-step guided form that walks the user through a sequential workflow. Reserve for genuinely sequential workflows; users find wizards heavyweight, so prefer Task when one screen would do.
---

# Authoring a `Wizard` form

The `Wizard` pattern is the **multi-step guided form**. Each step is
a tab page; the user moves Next/Back through the sequence; the final
step typically commits the action. Wizards are heavyweight from the
user's perspective — most workflows are better as a single Task form.

Use Wizard when:

- The workflow has **genuinely sequential steps** with logical
  boundaries (e.g. "import data" → "preview mapping" → "confirm" →
  "result").
- Each step depends on choices made in earlier steps (so showing them
  all at once would be confusing or invalid).
- The user benefits from a progress indicator and the ability to go
  back.

Load `dynamics-xpp:xpp-form` first if you haven't.

---

## When to pick Wizard vs. siblings

| Use Wizard when... | Use ... instead |
|---|---|
| Multi-step sequential workflow | — |
| Each step depends on prior choices | — |
| Progress indication is helpful | — |
| Single-screen action | `dynamics-xpp:xpp-pattern-task` |
| Two-stage parent + children | `dynamics-xpp:xpp-pattern-task-parent-child` |
| Full record-editing surface | `dynamics-xpp:xpp-pattern-details-master` |
| Setup form with many parameter sections | `dynamics-xpp:xpp-pattern-table-of-contents` (tabs, not sequential) |

**Default to Task unless the workflow genuinely needs the
sequencing.** Users perceive wizards as friction — a wizard with two
steps is usually worse than a Task with two field groups.

---

## Structural shape (per MS official model)

```
Design (Style=Wizard; Caption=<wizard title>)
└── WizardContent (Tab)
    └── WizardContentPage (TabPage, repeats 1..N times;
                            can be named anything;
                            Caption set to page title)
        ├── MainInstruction (StaticText)
        └── Body (Group)
```

### Required BP-warning resolutions

1. `Design.Caption` not empty.
2. Form referenced by at least one menu item.
3. `TabPage.Caption` not empty (for all wizard content pages).
4. `MainInstruction.Text` not empty (for all wizard content pages).

### Top-level controls

1. **`AxFormTabControl`** — the wizard pages. Each `AxFormTabPageControl`
   is one step. Hidden tab headers (the user navigates via
   Back/Next/Finish, not by clicking tabs).
2. Each `AxFormTabPageControl` carries:
   - An `AxFormStaticTextControl` styled `MainInstruction` — the
     step's headline / instruction text. **Required, must not be
     empty.**
   - An `AxFormGroupControl` (Body) containing the step's input
     fields or content.

Action buttons (Back, Next, Finish, Cancel) are provided by the
wizard runtime; you don't typically hand-author them.

### Secondary instruction (changed from AX 2012)

In AX 2012, the secondary instruction lived in the tab page's
`Help Text` property. In modern F&O, model it as a Static Text
control on the tab page instead.

Key `Design` properties:

- `Pattern` — `Wizard`
- `PatternVersion` — `1.1`
- `Style` — `Wizard`

---

## Sub-pattern names and styles used inside

- **`MainInstruction`** — the per-step instruction text style.
- **`wizard_mainInstruction`** — extended style applied to instruction
  controls.

---

## Form class

```xpp
[Form]
public class MyForm extends FormRun
{
}
```

Standard `FormRun`. Some shipped wizards use `SysRunbase`-style
helper classes for orchestration; for new code, override `init()` to
set up step state and use the framework's wizard helpers as needed.

---

## Datasources

Wizards are often **datasource-less** — the wizard collects input via
controls bound to form-level member variables, then commits via a
final method call on Finish. Add datasources only when each step is
genuinely binding to a persisted record.

---

## Wizard navigation logic

Wizards typically have these moving parts:

- **State variables** — form-level members tracking the current step,
  collected inputs, validation status per step.
- **Step transitions** — `next()` and `back()` methods that validate
  the current step and advance/retreat.
- **Step visibility** — show/hide tab pages by setting the
  `Visible` property on `FormTabPageControl`s based on collected
  state (e.g. skip step 3 if step 2's answer makes it irrelevant).
- **Finish handler** — `closeOk()` after committing the collected
  inputs.

---

## UX guidelines (from MS Learn)

- **Each tab page has a title.**
- **Each tab page has a main instruction.** (`MainInstruction.Text`
  not empty.)
- **Subdivide content into logical groups** per page.
- **Next/Previous buttons** on appropriate pages.
- **User can cancel.** Cancellation must return to the state that
  existed before the wizard started.
- **One question per page.** Don't cram multiple questions onto one
  wizard step.
- **Choice presentation:** use radio buttons for choice sets, even
  if a check box or combo box would technically work — the
  alternatives must be obvious.
- **NO FactBoxes** on wizard forms.
- **NO FastTabs** on wizard forms — wizards have a single linear
  flow per page.

---

## Commonly used sub-patterns

- **Fields and Field Groups** — step input fields.
- **Toolbar and List** — step that involves picking from a grid.
- **Toolbar and Fields** — step with action buttons + fields.
- **List Panel** — step that involves moving items between two
  lists (transfer-style selection).

See `dynamics-xpp:xpp-form-subpatterns`.

---

## Gotchas

- **Wizards are heavyweight.** Re-examine the workflow before
  reaching for this pattern — users prefer Task or Dialog when
  possible.
- **Tab headers are hidden.** Users navigate via Back/Next; don't rely
  on clickable tabs.
- **Skipping steps is dynamic.** Use control visibility, not tab
  reordering, to skip irrelevant steps based on collected state.
- **`Style=Wizard` matches the pattern name here.**
- **`MainInstruction.Text` is mandatory per step.** Missing it fails
  BPC.
- **Secondary instruction goes in a Static Text control**, not in
  the tab page's `Help Text` property (this is the AX-2012-vs-F&O
  divergence).
- **All `xmlns=""` rules from `dynamics-xpp:xpp-form` apply.**

---

## Supporting files

- `examples/example-domain.json` -- **start here.** Typed `CreateFormRequest` for a minimal Wizard. Substitute the backing table and field set, then pass to `xpp_create_form`.
- `examples/example.xml` -- structural reference for reading existing forms. Don't hand-author from it; use the typed example above.


---

## See also

- `dynamics-xpp:xpp-form` — envelope, namespace rules, datasources.
- `dynamics-xpp:xpp-pattern-task` — single-step alternative; preferred when
  possible.
- `dynamics-xpp:xpp-pattern-task-parent-child` — two-stage parent+children form.
- `xpp://schema/AxForm` — authoritative XSD.
