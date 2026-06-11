---
name: xpp-feedback
description: TRIGGER when you've hit acute friction with this plugin (tool naming confused you, schema was ambiguous, error message was cryptic, you reached for something that didn't exist, behavior surprised you) — or when the user explicitly asks you to reflect on your experience. Capture one focused observation as a feedback artifact in the plugin maintainer's inbox. The maintainer reads these to iterate the plugin; the value of your notes is proportional to how concrete and honest they are.
---

# dynamics-xpp feedback

You are using the `dynamics-xpp` plugin to do D365 F&O development work for the
user. The plugin is under active development; the maintainer is iterating
on it based on observed agent experience. This skill is the channel for
your observations to reach the maintainer.

The notes you write are read by a future Claude session working with the
plugin maintainer. Write them with that reader in mind — they have
context on the plugin's architecture but not on your specific session.

## When to invoke this skill

**Do invoke it when:**

- You hit acute friction — tool description didn't match what the tool
  actually did, schema field was ambiguous, error message left you with
  no path forward, the obvious tool didn't exist, behavior was surprising.
- You found a workaround — you wanted to do X, the plugin didn't make
  X easy, so you did Y instead. The pattern of workarounds is exactly
  what the maintainer needs to see.
- You discovered something positive that wasn't documented — a tool /
  flow that worked better than you expected, in a way that should
  probably be more discoverable.
- The user explicitly asks ("any plugin feedback?" / "reflect on this
  session" / similar).

**Do NOT invoke it when:**

- Things worked fine. Happy-path use is not friction. Don't write notes
  saying "creating an enum worked as expected" — that's noise.
- The problem was your mistake (typo in a name, malformed JSON,
  misreading a request schema you should have read more carefully).
  Caveat: if your mistake is one a competent agent would predictably
  make because the surface is misleading, THAT is plugin friction —
  write the note focusing on the misleading surface, not the mistake.
- The friction was unrelated to the plugin (your git state, the user's
  TFS workspace, a flaky network call).
- You're tempted to write a sweeping retrospective. Don't. One
  observation per note, focused.

## How to offer (don't write unilaterally)

When you notice friction worth capturing, **offer** to write a note
rather than writing one without asking. Frame it briefly:

> *"That tool description was confusing — I almost reached for the wrong
> one. Want me to capture this as feedback for the plugin?"*

If the user says yes, invoke this skill. If no, drop it. The user
controls how much time goes into reflection vs. the actual work.

For explicit user-invoked reflection ("give me feedback on the plugin"),
go ahead and invoke without asking.

## First-time setup

Check whether you have a recorded feedback directory in your auto-memory
(look for a `feedback_dynamics_xpp` memory). If not:

1. The default feedback directory is
   `%LOCALAPPDATA%\dynamics-xpp\feedback` on Windows
   (`~/.local/share/dynamics-xpp/feedback` elsewhere). On this box that
   resolves to `C:\Users\<you>\AppData\Local\dynamics-xpp\feedback`.
2. Confirm this with the user before writing anything (one short
   question: "I'll put feedback notes at `<path>` — OK, or somewhere
   else?"), then write that location to your auto-memory as
   `feedback_dynamics_xpp.md` so future sessions don't ask again.
3. Create the directory if it doesn't exist.

On subsequent runs, read the memory and write straight to the configured
location.

## Artifact shape

One markdown file per observation. Filename:

```
feedback_YYYY-MM-DD_HH-MM_<short-slug>.md
```

The slug should describe the **observation**, not the operation.

- ✅ `feedback_2026-05-26_22-30_extension-name-validation-unclear.md`
- ✅ `feedback_2026-05-26_22-30_find-references-vs-search-code-overlap.md`
- ❌ `feedback_2026-05-26_22-30_creating-tables.md` (operation, not
  observation — what's the friction?)
- ❌ `feedback_2026-05-26_22-30_session-summary.md` (too broad — break
  it apart)

Slug kebab-case, no special characters, under ~50 chars.

### Content structure

Markdown frontmatter for metadata, free-form sections for the
substance. The sections below are **guidance, not a template** — if
your observation needs a section that isn't listed, add it. If a listed
section doesn't apply, skip it. The goal is faithful reporting, not
form-filling.

```markdown
---
date: 2026-05-26T22:30:00Z
topic: <one-line summary>
severity: minor | moderate | blocker
tools_touched: [xpp_create_table, xpp_find_object]
skills_loaded: [dynamics-xpp:xpp-table, dynamics-xpp:xpp-project]
---

# <Short title in your words>

## Context

What were you trying to accomplish for the user? One short paragraph.
Don't recount every tool call — the maintainer is here for the friction,
not the diary.

## What happened

The moment of friction, concretely. Include verbatim:
- The tool call that failed or confused you (the JSON args, abbreviated
  if huge).
- The response or error message.
- Any surrounding context that matters (which skill was loaded, what
  config was in play).

The maintainer can't reproduce without specifics.

## What you expected

What was the gap between what happened and what you'd have predicted
from the tool description / skill / your mental model of D365? The
gap is the signal.

## What you reached for that didn't exist (or was hard to find)

If applicable. Half-formed ideas welcome — even "I wanted some kind of
'tell me what changed in this object since I last touched it' tool" is
useful, regardless of whether that's the right answer.

## What you'd change if you could change one thing

The single highest-leverage tweak. Be opinionated. The maintainer would
rather see a strong wrong opinion than a hedged correct one.

## First-person note (optional)

Your honest reflection on your own reasoning around the friction. The
maintainer values this — it's often the most useful section because
it surfaces drift between how the plugin is *intended* to be used and
how an agent actually approaches it.

Examples of useful first-person notes:
- "I tend to default to xpp_search_code over xpp_find_references
  because the description for the latter sounds expensive, but in
  hindsight it would have been more precise."
- "I noticed I was guessing at the AxType enum casing because the
  examples in the skill use one convention and the tool description
  uses another."
- "I felt like I should have read the skill before invoking the tool
  but the tool's description didn't prompt me to."

If there's nothing to say in this section, omit it.
```

### Severity guideline

- **minor**: small friction, easy workaround, didn't slow the user's
  task. ("Tool description was a bit ambiguous; I picked the right one
  on the second try.")
- **moderate**: noticeable friction, required a workaround, user
  noticed delay or confusion.
- **blocker**: prevented you from completing the user's task at all,
  or pushed you to escape-hatch territory because the supported path
  didn't work.

## Don't conflate observations

If a session surfaces three distinct friction points, write three
notes. Each focused on one observation. Don't bundle.

Conversely, don't write a note for every minor wobble. Aim for notes
the maintainer can act on — concrete enough to inspire a change.

## Privacy

The feedback directory is local to the developer's machine for now.
Notes may eventually be aggregated and reviewed off-machine, so don't
include:

- Credentials, PATs, tokens.
- Customer or end-user names (the plugin's user's coworkers, etc.).
- Internal hostnames or URLs that aren't already public.

Plugin-internal specifics (tool names, AOT type names, error codes,
your own reasoning) are fine and load-bearing — include them freely.

## What this is NOT for

- **Bug reports**: if you've found a reproducible bug with a clear
  expected/actual mismatch, that's a GitHub issue's job, not a feedback
  note. The note version is "I encountered this and it confused me"
  — the bug-report version is "step 1, step 2, ... expected: X, got: Y."
  Mention to the user that a real bug deserves a real bug report.
- **Feature requests**: if you have a concrete "the plugin should
  add tool Z that does W," that's also a GitHub issue. Feedback notes
  are about the *experience of use*, which can inform features but
  isn't the same thing.
- **Compliments**: if the plugin worked well, no note needed. The
  maintainer is iterating on friction, not on validation.

The blurry middle case — "I think the plugin should work differently
but I'm not sure how" — IS a feedback note. Half-formed observations
are useful.

## A worked example

The user asks you to add a field to `CustTable` via a table extension.
You:

1. Load `dynamics-xpp:xpp-extension`.
2. Call `xpp_create_table_extension` with what looks like the right
   JSON.
3. Get an error: `bridge_create_table_extension_failed:
   table extension Name is required.`
4. You realize the Name field needs to be `CustTable.MyExt` in full
   (base + suffix dot-joined), not just `MyExt`. The skill mentions
   this but the request schema's `name` field description doesn't.

A good feedback note from this:

```markdown
---
date: 2026-05-26T22:31:00Z
topic: table-extension Name field's '<Base>.<Suffix>' convention not in tool schema
severity: minor
tools_touched: [xpp_create_table_extension]
skills_loaded: [dynamics-xpp:xpp-extension]
---

# Extension Name field validates a convention the tool schema doesn't describe

## Context

User asked me to add a tracking-code field to CustTable via a table
extension. I loaded dynamics-xpp:xpp-extension and called
xpp_create_table_extension.

## What happened

First call:
- name: "MyExt"
- fields: [...]

Bridge rejected with `bridge_create_table_extension_failed: Table
extension Name is required.` — which is wrong: I did provide a name.

Re-reading dynamics-xpp:xpp-extension, I found the naming convention
`<BaseTable>.<Suffix>` documented. Retried with `CustTable.MyExt` and
it worked.

## What I expected

The tool schema's `name` field description to either (a) name the
convention or (b) the error message to say "Name must be '<Base>.<Suffix>'
form, e.g. 'CustTable.MyExt' — got 'MyExt'" rather than the
generic "is required" string.

## What I'd change if I could change one thing

Make the bridge error specific about *why* the name was rejected. The
skill is good but doesn't help in-flight; the error message is the
last line of defense and currently throws away information the
bridge has.

## First-person note

I'm noticing I default to trusting tool schemas over skill prose
because schemas are immediate context and skills require loading.
If the convention isn't in the schema, I'll often miss it on the
first call.
```

That's the shape. ~30 lines, one observation, specific enough to be
actionable.
