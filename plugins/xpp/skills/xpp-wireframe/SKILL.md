---
name: xpp-wireframe
description: Use when designing a D365 F&O form and you want to visualize the layout BEFORE writing any XML — produce an SVG wireframe of the form, faithful to the chosen UX pattern and sub-patterns, with annotated callouts explaining the structure. Pairs with the per-pattern skills (xpp:pattern-*) and dynamics-xpp:xpp-form-subpatterns; the pattern decisions should be made first, then wireframed, then implemented.
---

# Wireframing D365 F&O forms

Before any XML, before any X++ — when you're designing a new form
or a substantial redesign of an existing one, **a wireframe lets
stakeholders see the structure and react** while the cost of change
is still close to zero. F&O forms are heavily pattern-constrained,
which means a faithful wireframe is also a design contract: it
commits the team to a specific pattern + sub-pattern choices and
the spatial composition those entail.

Claude can write SVG. The pattern skills tell you what's allowed.
This skill is the bridge — visual vocabulary, proportions,
annotation conventions, and worked composition guidance — so the
SVG you produce reads like an F&O form rather than a generic
wireframe.

---

## When to use this skill

- A new form is being designed and the layout needs review before
  build.
- An existing form is being redesigned (more controls, new
  sub-pattern, workflow change) and stakeholders want to see the
  proposed shape.
- A workflow involves multiple forms and the team needs a visual
  storyboard of the flow.

When NOT to use:

- The form already exists and you just need to inspect it — read
  the XML via `xpp_get_object_xml` and describe what's there. A
  wireframe is the wrong artifact when the form is already built.
- For UI mockups with real visual design (colors, typography,
  iconography) — wireframes are intentionally lo-fi to keep
  attention on structure, not styling.

---

## Prerequisites — decide these BEFORE wireframing

Wireframing a pattern you haven't chosen is wasted effort. Before
producing the SVG, lock in:

1. **The top-level form pattern.** SimpleList? DetailsMaster?
   DetailsTransaction? Workspace? Use the appropriate
   `xpp:pattern-*` skill's "when to pick" table to make this call,
   then carry it forward.
2. **Sub-patterns for inner containers.** What does each FastTab
   look like — Fields and Field Groups? Toolbar and List? See
   `dynamics-xpp:xpp-form-subpatterns`.
3. **The data this form will display.** Which table(s), which
   fields per section, what relationships (header→lines, joined
   datasources).
4. **Actions on the action pane.** Which buttons/menus, grouped
   into which Action Pane tabs.
5. **Status indicators.** Workflow state? Posting status? Anything
   that lives in the upper-right entity-status area.

If any of these are unsettled, surface that **before** drawing —
the wireframe becomes useful only when there's something concrete
to commit to.

---

## Canvas, scale, and proportions

### Canvas size

Use a viewBox sized roughly to a modern web client. A safe default:

```xml
<svg xmlns="http://www.w3.org/2000/svg"
     viewBox="0 0 1440 900"
     font-family="Segoe UI, system-ui, sans-serif">
```

- **1440×900** approximates a typical laptop viewport.
- **Segoe UI** is F&O's default font; falling back to system-ui
  keeps it readable across rendering targets.
- `viewBox` (not fixed `width`/`height`) lets the SVG scale to
  whatever container displays it.

### Standard regions and approximate dimensions

These aren't exact pixel values — wireframes are about relative
proportion, not literal measurement. But they should be
recognizable to anyone familiar with F&O:

| Region | Approx. dimensions | Notes |
|---|---|---|
| Top action pane | full width × ~80px (one tab row) to ~120px (action-pane-tab style with tabs + button rows) | Heavy on ListPage; lighter on SimpleList; absent on Wizard |
| Side panel (navigation list) | ~280–320px wide on master/detail/transaction forms | Left side; collapses on small viewports |
| Vertical splitter | 4–8px wide; only on SLD Tree / Tabular Grid variants | |
| FastTab header strip | ~36–44px tall | Includes expand/collapse glyph, label, and the summary line |
| FastTab body (expanded) | variable, depends on content | First FastTab shows full content without scroll |
| Title strip (Details forms) | ~64–80px tall, large font, top of content area | `ID : Description` format |
| Entity-status group | upper-right of title strip | Status fields stacked or inline |
| Field row | ~32px tall (label + input) | Captions left of inputs by default |
| Grid row | ~28–32px tall | Variable; tables typically denser than fields |
| Workspace tile | 280px wide × 280px tall (large) or 280×144 (medium) | `HorizontalWrap` default column width is 280px exact |

### Color discipline

**Stay monochrome.** A wireframe is a structural document; the
moment you introduce color, viewers start commenting on the
palette rather than the layout. Use:

- White / `#fff` — form canvas background.
- Light gray / `#f3f3f3` — section background bands (FastTab header
  strips, action pane background).
- Medium gray / `#cccccc` to `#999999` — borders, dividers, field
  outlines.
- Dark gray / `#333333` — text, labels.
- One accent — pick ONE color for callouts and annotation arrows,
  e.g. coral / `#d9534f` or blue / `#1f77b4`. Use sparingly.

Avoid filled colored shapes for status indicators in the wireframe
itself — use a labeled gray box with a text annotation like
"[status: Approved]" instead.

### Visual placeholders

| Element | Placeholder | Notes |
|---|---|---|
| Field input | `[___________]` text inside a thin gray bordered rectangle | Or a labeled gray-filled rectangle |
| Field caption | Left-aligned text outside the input box | Sentence case authored; renderer caps groups |
| Grid cell | empty rectangle on a row-divided rectangle grid | Show 3–5 rows + ellipsis "..." |
| Tile | gray-filled 280×~144px rectangle with title + count placeholder | "[count]" or actual representative numbers |
| Chart | gray rectangle with diagonal lines (axis hint) and a label "[chart: <topic>]" | Don't try to render real chart data |
| Image / icon | gray square with a centered label "[icon]" or "[image]" | |
| Action pane button | gray-bordered rounded rectangle with text | Group with vertical divider lines |

---

## Per-pattern visual recipes

For each top-level pattern, the canonical wireframe composition.
These are starting points — adapt to the specific form's content.

### SimpleList

```
┌───────────────────────────────────────────────────────────┐
│  [Action Pane]   New  Delete  Edit  Save  ...             │
├───────────────────────────────────────────────────────────┤
│  [Quick Filter: ID ▾] [_____________________]             │
├───────────────────────────────────────────────────────────┤
│  ID       │ Name           │ Status     │ Field4 │ ...   │
│  ─────────┼────────────────┼────────────┼────────┼──────  │
│  REC001   │ Example 1      │ Active     │ ...    │ ...   │
│  REC002   │ Example 2      │ Active     │ ...    │ ...   │
│  ...      │ ...            │ ...        │ ...    │ ...   │
│                                                           │
│                                                           │
├───────────────────────────────────────────────────────────┤
│  [Optional Footer]                                        │
└───────────────────────────────────────────────────────────┘
```

- Single full-width grid below filter row.
- Action pane is light: no tab structure required.
- Up to 15 columns in the grid.
- Optional footer for totals / supplementary info.

### SimpleListDetails (List Grid variant)

```
┌───────────────────────────────────────────────────────────┐
│  [Action Pane]   ┌─ Manage ─┐                             │
│                  New Delete Save                          │
├───────────────────────────────────────────────────────────┤
│  [Quick Filter]            │                              │
│  ──────────────────────────│  ──────────────────────────  │
│  ID       Name      [▸]    │  [Details — selected record] │
│  REC001   Example 1   ▸    │                              │
│  REC002   Example 2        │  Field 1: [____________]     │
│  REC003   Example 3        │  Field 2: [____________]     │
│  ...                       │  Field 3: [____________]     │
│                            │                              │
│  (List Grid — 2-3 fields   │  ▼ FastTab 1 (expanded)      │
│   per row, multi-line)     │     Field 4: [_____]         │
│                            │     Field 5: [_____]         │
│                            │                              │
│                            │  ▸ FastTab 2 (collapsed)     │
└────────────────────────────┴──────────────────────────────┘
   Navigation list (~280px wide)    Details pane (~SidePanel)
```

- Left list ~25% of canvas; right detail pane ~75%.
- List shows 2–3 fields per row (List Grid variant).
- Detail pane has a header group (the same fields as the list, in
  list order) followed by FastTabs.
- The Tabular Grid variant turns the left side into a full grid
  with the VerticalSplitter between; Tree variant uses a tree
  control there.

### DetailsMaster

```
┌───────────────────────────────────────────────────────────┐
│  [Action Pane]  ┌─ Home ┐ ┌─ Manage ┐ ┌─ Related ┐        │
│                   New Save  Apply Verify  Activities      │
├────────────┬──────────────────────────────────────────────┤
│ [Nav List] │  ┌─ Title Strip ─────────────────[Status]─┐  │
│            │  │  CUST001 : Contoso Retail              │  │
│  [Quick    │  └────────────────────────────────────────┘  │
│   Filter]  │                                              │
│            │  ▼ General (expanded)                        │
│  REC001 ▸  │     Account number: [CUST001    ]            │
│  REC002    │     Name:           [Contoso Retail        ] │
│  REC003    │     Customer group: [Manufacturing ▾]        │
│  ...       │     Currency:       [USD ▾]                  │
│            │                                              │
│            │  ▸ Address (collapsed — summary: HQ, USA)    │
│            │  ▸ Financial (collapsed)                     │
│            │  ▸ Sales (collapsed)                         │
│            │                                              │
└────────────┴──────────────────────────────────────────────┘
```

- Side panel left (~280px), main content right.
- Title strip ALWAYS at the top of the content area, large font.
- Status group upper-right of title strip.
- FastTabs vertically stacked under the title; first FastTab fully
  visible without scrolling.
- Modern variant carries BOTH this view and a grid view — toggled
  via the parent MainTab. You can wireframe both as separate frames
  or as a single composition with the grid as a faded "alternate"
  panel.

### DetailsTransaction

```
┌────────────────────────────────────────────────────────────────┐
│  [Action Pane]  Sell  Manage  Pick and pack  Invoice  ...      │
├────────────┬───────────────────────────────────────────────────┤
│ [Nav List] │  ┌─ Title ──────────────────────[Posting status]─┐│
│            │  │  SO-00001234 : Customer name                  ││
│            │  └───────────────────────────────────────────────┘│
│            │                                                   │
│            │  [Header view] ◯ ● [Lines view]                   │
│            │                                                   │
│            │  ─────── LINES VIEW ───────                       │
│            │  Line# │ Item    │ Qty │ Price │ Total │ Status   │
│            │  ──────┼─────────┼─────┼───────┼───────┼────────  │
│            │  1     │ ITM-001 │ 5   │ 100   │ 500   │ Open     │
│            │  2     │ ITM-002 │ 3   │  50   │ 150   │ Open     │
│            │  ...   │ ...                                      │
│            │                                                   │
│            │  ─────── Line details (selected) ───────          │
│            │  ▼ Delivery (expanded for selected line)          │
│            │     Address:  [_________________]                 │
│            │     Mode:     [Standard ▾]                        │
└────────────┴───────────────────────────────────────────────────┘
```

- Same side panel + title strip as DetailsMaster.
- The body has a Header/Lines toggle (radio-buttons-as-tabs in
  older versions; native tabs in 10.0.23+). Wireframe BOTH if
  showing how a user moves between them.
- Lines grid above; selected line's details below.
- Header view (separate sibling) shows the document's header-only
  FastTabs.

### ListPage (when not merged with Details)

```
┌─────────────────────────────────────────────────────────────────┐
│  [Action Pane]                                                  │
│  ┌─ Sales order ─┐ ┌─ Sell ─┐ ┌─ Pick ─┐ ┌─ Invoice ─┐ ┌─ ... ─┐│
│  │ New Edit Del  │ │ Confirm│ │ Pick   │ │ Generate  │          │
│  └───────────────┘ └────────┘ └────────┘ └───────────┘          │
├─────────────────────────────────────────────────────────────────┤
│  [Quick Filter: Sales order ▾] [____________]   [More filters ▾]│
├─────────────────────────────────────────────────────────────────┤
│  Sales order ▸│ Customer    │ Status   │ Date       │ Amount   │
│  ──────────────┼─────────────┼──────────┼────────────┼────────  │
│  SO-001        │ Contoso     │ Open     │ 2026-05-19 │ $1,250   │
│  SO-002        │ Northwind   │ Open     │ 2026-05-18 │ $   500  │
│  SO-003        │ ...         │ ...      │ ...        │ ...      │
│                                                                 │
│  [FactBox 1] │ [FactBox 2] │ [FactBox 3] (right side, optional) │
└─────────────────────────────────────────────────────────────────┘
```

- Heavy action pane with multiple tabs of button groups.
- Custom filter group above the grid.
- Up to 15 grid columns; first column = ID/name with hyperlink
  styling (opens detail form).
- FactBoxes on the right pane are optional but common.

### Workspace (Operational, vertical post-10.0.25)

```
┌─────────────────────────────────────────────────────────────────┐
│  [Action pane (optional)]  ┌─ Activities ─┐                     │
├─────────────────────────────────────────────────────────────────┤
│  [Workspace page filter (optional, single field)]               │
├─────────────────────────────────────────────────────────────────┤
│  ▼ SUMMARY (Section Tiles)                                      │
│  ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐                │
│  │ 23  │ │  7  │ │ 142 │ │ $4K │ │  3  │ │ ... │                │
│  │Pend.│ │Late │ │Open │ │Out  │ │To   │ │     │                │
│  │     │ │     │ │     │ │stand│ │post │ │     │                │
│  └─────┘ └─────┘ └─────┘ └─────┘ └─────┘ └─────┘                │
├─────────────────────────────────────────────────────────────────┤
│  ▼ MY WORK (Section Tabbed List)                                │
│  [Tab 1: My pending orders] [Tab 2: My open invoices] [...]     │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │ Order#   Customer   Status   Due     Amount                 ││
│  │ SO-...   ...        ...      ...     ...                    ││
│  └─────────────────────────────────────────────────────────────┘│
├─────────────────────────────────────────────────────────────────┤
│  ▼ ANALYTICS (Section Stacked Chart — optional)                 │
│  [chart: revenue by month]   [chart: top customers]             │
├─────────────────────────────────────────────────────────────────┤
│  ▼ POWER BI (Section PowerBI — optional)                        │
├─────────────────────────────────────────────────────────────────┤
│  ▼ RELATED (Section Related Links)                              │
│  → Customers   → Items   → Vendors   → ...                      │
└─────────────────────────────────────────────────────────────────┘
```

- **Vertically stacked sections** (post-10.0.25). DO NOT wireframe
  with horizontal panorama scrolling — that's the obsolete pattern.
- Each section is a collapsible FastTab with the section
  sub-pattern dictating contents.
- Tiles in the Summary section use the 280px standard width.
- Tabbed list section: only one tab visible at a time.

### Wizard

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                 │
│       Step title: Choose source data                            │
│       (Main instruction text — what the user should do here)    │
│                                                                 │
│       Source type:    ◯ Existing file                           │
│                       ◉ Cloud bucket                            │
│                       ◯ Live database                           │
│                                                                 │
│       Path:           [________________________________]        │
│                                                                 │
│                                                                 │
│                                                                 │
│  ─────────────────────────────────────────────────────────────  │
│                          [< Back]  [Next >]  [Cancel]           │
└─────────────────────────────────────────────────────────────────┘
   (Step 2 of 5)
```

- Tab headers are hidden — users navigate via Back/Next.
- One question per step.
- Show the progress (e.g. "Step 2 of 5") and Back/Next controls.
- No FactBoxes, no FastTabs.

### TableOfContents

```
┌────────────┬────────────────────────────────────────────────────┐
│ Vertical   │  ┌─ Title strip ───────────────────────────────┐   │
│ tabs       │  │  General                                    │   │
│            │  └─────────────────────────────────────────────┘   │
│ ▸ General  │  (Main instruction text describing this section)   │
│ ◉ Setup    │                                                    │
│ ▸ Number   │  ┌─ Body group (Fields and Field Groups) ──────┐   │
│   seqs.    │  │  Setting 1:      [Enabled ▾]                │   │
│ ▸ Updates  │  │  Setting 2:      [50          ]             │   │
│ ▸ Posting  │  │  Setting 3:      [_________]                │   │
│ ▸ Defaults │  │                                             │   │
│            │  │  (more settings, grouped responsively)      │   │
│            │  └─────────────────────────────────────────────┘   │
│            │                                                    │
└────────────┴────────────────────────────────────────────────────┘
   Vertical tabs           Tab page content
```

- Vertical tab strip on the left.
- Each tab page has Title group + Body (or FastTabs).
- App actions are NOT on the form-level action pane — bury them on
  the relevant tab page's toolbar instead.

### Task (legacy — DO NOT use for new forms)

If you find yourself wireframing a Task form for new work, you're
authoring the wrong pattern. See `dynamics-xpp:xpp-pattern-task` for the modern
replacements (Dialog, Drop Dialog, Simple Details). Wireframe one
of those instead.

---

## SVG composition guide

### File skeleton

```xml
<svg xmlns="http://www.w3.org/2000/svg"
     viewBox="0 0 1440 900"
     font-family="Segoe UI, system-ui, sans-serif"
     font-size="13">

  <!-- Backdrop -->
  <rect x="0" y="0" width="1440" height="900" fill="#ffffff"/>

  <!-- Title bar -->
  <rect x="0" y="0" width="1440" height="32" fill="#f3f3f3" stroke="#cccccc"/>
  <text x="16" y="20" fill="#333">Form title here</text>

  <!-- Action pane -->
  <g id="action-pane">
    <rect x="0" y="32" width="1440" height="80" fill="#fafafa" stroke="#cccccc"/>
    <!-- buttons, tabs, etc. -->
  </g>

  <!-- ... rest of the form ... -->

  <!-- Annotations layer -->
  <g id="annotations" stroke="#d9534f" fill="#d9534f">
    <!-- callout circles + arrows + legend text -->
  </g>

</svg>
```

### Composition tips

- **Layer in `<g>` groups** by region: action pane, side panel,
  main content, FastTabs, annotations. Makes the SVG readable and
  easy to edit.
- **Use `<rect>` for everything boxy** with `fill` + `stroke`. Most
  wireframe elements are gray-filled rectangles.
- **`text-anchor="middle"`** for centered labels (tile counts,
  button captions). Default left-anchor for field captions.
- **Set `font-size` once on the root** and inherit. Override
  selectively (12px for grid cells, 16px+ for titles).
- **`stroke-dasharray="4,2"`** for "this part is optional" or
  "alternate view" rectangles. Useful when wireframing two states
  in one image.

### Reusable SVG primitives

Field input:
```xml
<g transform="translate(80, 200)">
  <text x="0" y="14" fill="#333">Field name:</text>
  <rect x="100" y="2" width="200" height="20" fill="white" stroke="#999"/>
  <text x="106" y="16" fill="#999" font-style="italic">[value]</text>
</g>
```

Grid row:
```xml
<g transform="translate(0, 300)">
  <line x1="0" y1="0" x2="1440" y2="0" stroke="#ddd"/>
  <text x="16" y="22" fill="#333">SO-001234</text>
  <text x="200" y="22" fill="#333">Contoso Retail</text>
  <text x="400" y="22" fill="#333">Open</text>
  <text x="540" y="22" fill="#333">2026-05-19</text>
  <text x="640" y="22" fill="#333" text-anchor="end">$1,250.00</text>
</g>
```

Tile:
```xml
<g transform="translate(40, 200)">
  <rect x="0" y="0" width="280" height="144" fill="#eaeaea" stroke="#ccc"/>
  <text x="140" y="60" text-anchor="middle" font-size="42" fill="#333">23</text>
  <text x="140" y="100" text-anchor="middle" fill="#666">Pending orders</text>
</g>
```

FastTab (collapsed):
```xml
<g transform="translate(20, 400)">
  <rect x="0" y="0" width="1100" height="40" fill="#f3f3f3" stroke="#ccc"/>
  <text x="16" y="25" fill="#333">▸ Address</text>
  <text x="160" y="25" fill="#999" font-style="italic">123 Main St, Seattle WA</text>
</g>
```

---

## Annotation conventions

Annotated wireframes communicate design intent, not just structure.
Standardize on:

### Numbered callouts + legend

Drop small circled numbers next to interesting elements; provide a
legend at the bottom or side. This keeps the wireframe itself clean
and lets viewers see the structure first, then read the
annotations.

```xml
<!-- Callout dot at position -->
<g transform="translate(400, 180)">
  <circle cx="0" cy="0" r="10" fill="#d9534f"/>
  <text x="0" y="4" text-anchor="middle" fill="white" font-weight="bold">1</text>
</g>

<!-- Legend at the bottom -->
<g transform="translate(40, 820)" font-size="12" fill="#333">
  <text x="0" y="0" font-weight="bold">Annotations</text>
  <text x="0" y="20">① Quick filter — defaults to Customer ID column</text>
  <text x="0" y="40">② FactBoxes — show open balance + recent activity</text>
  <text x="0" y="60">③ Grid hyperlink — opens DetailsMaster for selected row</text>
</g>
```

### Arrow callouts (for spatial relationships)

When pointing at something specific (a control, a region):

```xml
<g stroke="#d9534f" fill="none">
  <path d="M 600 180 L 700 130" stroke-width="1.5"/>
  <polygon points="700,130 696,134 696,126" fill="#d9534f" stroke="none"/>
</g>
<text x="704" y="128" fill="#d9534f" font-size="12">
  Title strip — "ID : Description" format
</text>
```

### Design-rationale annotations

For decisions stakeholders should know about:

```xml
<g transform="translate(20, 760)" font-size="11" fill="#555">
  <text x="0" y="0" font-weight="bold">Design rationale</text>
  <text x="0" y="18">• Pattern: SimpleListDetails (List Grid). Each record has 8 fields,</text>
  <text x="0" y="32">  so a SimpleList would be too dense; SLD pane carries detail.</text>
  <text x="0" y="46">• Navigation list uses Identification field group (Code + Name).</text>
  <text x="0" y="60">• FastTab order: General → Address → Compliance. First FastTab</text>
  <text x="0" y="74">  fits without scroll on 1440px viewport per MS guidelines.</text>
</g>
```

This lets the wireframe stand on its own — the viewer doesn't need
the design conversation to understand WHY.

### Multi-state wireframes

If the form has meaningfully different states (Edit vs View, Header
vs Lines panel, expanded vs collapsed FastTab), consider:

- **Two SVGs side by side**, labeled with the state.
- **One SVG with both states**, the secondary drawn with dashed
  borders or opacity 0.6 to indicate "alternate."
- **Sequential frames** for a workflow (state A → state B → state
  C), each in its own SVG.

---

## Output conventions

When producing a wireframe, deliver:

1. **The SVG itself** — directly in the conversation as a code
   block, or written to a file the user can open.
2. **A brief written summary** of the pattern + sub-pattern
   choices and the rationale. The wireframe shows the *what*; the
   summary explains the *why*. 3-6 sentences.
3. **Open questions** — anything the wireframe surfaces that
   wasn't decided yet. ("What status fields go in the upper-right?"
   "Should the FactBox show open balance or recent activity?")

The user typically wants to iterate. Expect to revise. Don't try
to make the first wireframe perfect — make it good enough to drive
a productive conversation.

---

## Supporting files

- `examples/simple-list-details-wireframe.svg` — full worked
  example: a SimpleListDetails wireframe with callouts, legend,
  and design-rationale annotations. Read it as a reference for
  composition, scale, and annotation style.

---

## See also

- The per-pattern skills (`xpp:pattern-*`) — pick the pattern
  FIRST, then wireframe.
- `dynamics-xpp:xpp-form-subpatterns` — pick sub-patterns for each container
  BEFORE composing the wireframe.
- `dynamics-xpp:xpp-form` — envelope + namespace rules. Don't need them for the
  wireframe but you'll need them when you move to implementation.
- MS Learn — for the authoritative visual reference:
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/general-form-guidelines
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/page-layout
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/select-form-pattern (catalog with screenshots of each pattern)
