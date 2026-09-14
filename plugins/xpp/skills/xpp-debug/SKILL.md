---
name: xpp-debug
description: Use when reasoning from code and data alone has stalled and you need to SEE what the running system does — attach the X++ debugger to the AOS (w3wp) or the batch host, break in a method, read the call stack, locals and record buffers, step, evaluate expressions, then release. Covers the xpp_debug_* tools, how to trigger a hit, what to inspect, and the safety rules (a paused AOS freezes every session on the box).
---

# Debugging live X++ (xpp_debug_*)

Static reading tells you what the code *should* do. When the behaviour you see
does not follow from the code and the data you can query — a price that comes
out wrong, a path you are sure runs but apparently does not, a value that is
different by the time it reaches the line you care about — stop theorising and
watch it run. The `xpp_debug_*` tools attach Visual Studio's managed debugger
(hosted invisibly by the plugin service) to the F&O process on this dev box and
give you breakpoints, the X++ call stack, locals, record buffers, stepping and
expression evaluation.

Debugging is the *second* tool, not the first. Before attaching, make sure you
have already: read the methods on the path (`xpp_get_method_source`), checked
who calls what (`xpp_find_references`), and queried the data. The debugger
answers "what is the value **here, now**" — it does not replace knowing where
"here" is.

## What you are attaching to

| target  | process    | what runs there                                                        |
|---------|------------|------------------------------------------------------------------------|
| `aos`   | w3wp.exe   | everything the web client and OData/service calls do (default)         |
| `batch` | Batch.exe  | scheduled batch jobs (SysOperation / RunBaseBatch running in batch)    |

Both run as NETWORK SERVICE, so the plugin service must be **elevated**: Claude
Code started as administrator. If it is not, `xpp_debug_attach` says so — tell
the user to restart Claude Code elevated and run `dt service restart`.

One debugger session per machine. `xpp_debug_status` shows whether another
session owns it.

## The loop

1. **Attach.** `xpp_debug_attach` (`target: "aos"` or `"batch"`). The first
   attach starts a hidden Visual Studio (~10–20 s) and attaches (~5–20 s).
   Later attaches reuse it.
2. **Set breakpoints by method**, not by line: `xpp_debug_breakpoint` with
   `axType`, `name`, `method` — e.g. `AxClass` / `PriceDisc` / `findPrice`,
   or `AxTable` / `SalesLine` / `modifiedField`. It lands on the first
   executable statement; `boundLine` and `sourceLine` show exactly where. Use
   `offset` to go further into the method (lines below the declaration).
   - Supported: `AxClass`, `AxTable`, `AxDataEntityView`, `AxView`, `AxQuery`,
     `AxMap`. **Not yet:** forms and extension elements — break in a class or
     table method on the same call path instead (form code almost always ends
     up in one).
   - Hot methods (`CustTable.find`, `SalesLine.modifiedField`) fire constantly.
     Give them a **condition** so only the case you care about breaks:
     `condition: "_salesLine.SalesId == 'SO-000123'"` (X++ expression; the
     method's parameters and `this` are in scope).
   - `bound: false` is normal for a module the target has not loaded yet; it
     binds when the module loads. If it never binds, the element is probably a
     form/extension or the name is wrong (`xpp_find_object`).
3. **Trigger the path while waiting.** `xpp_debug_wait` blocks for up to
   `timeoutSec`. The AOS only runs X++ when a session asks it to, so someone
   must perform the action: the user in the F&O client, the browser tools if you
   have them, an OData call, or — for batch — the job's next scheduled run.
   Say what you are waiting for *before* you wait, so the user can act.
   Pass `watch` expressions to have them evaluated at the moment of the hit.
4. **On a hit, the target is PAUSED.** Every AOS session on the box is waiting
   on you. Work quickly and deliberately:
   - the hit payload already has the X++ stack (file:line + source text),
     `this`, arguments, locals and your watches — read it before asking for more;
   - `xpp_debug_eval` for anything else: fields (`salesLine.SalesPrice`), method
     calls (`this.parmItemId()`), statics (`CustTable::find('C-1').Name`);
     `expand: true` on a record buffer lists its fields;
   - `xpp_debug_step` `over` / `into` / `out` to follow the value a few
     statements; each step returns a fresh location with locals and watches.
5. **Release.** `xpp_debug_continue` resumes with the breakpoints armed (call
   `xpp_debug_wait` again for the next hit). The service's watchdog resumes a
   hit left paused longer than `maxPauseSeconds` (default 300) no matter what —
   that is a backstop, not a workflow.
6. **Detach when done.** `xpp_debug_detach` resumes, deletes every breakpoint
   and detaches. Do this before you report findings. Breakpoints left armed keep
   freezing the AOS for whoever hits them next.

## Reading what you get back

- `stack[].xpp` marks X++ frames; the rest is kernel/C#. `file` is
  `<Model>\AxClass_<Name>.xpp` — the generated source the debugger works from;
  `line` refers to it and `sourceLine` is the text there, so you rarely need to
  open anything.
- Locals of a value type show their value; objects show `{Type}` — `eval` with
  `expand` to look inside. Record buffers (`SalesLine`, `CustTable`) are
  objects: `expand` shows every field, or evaluate a field directly.
- At the first statement of a method, locals are still unassigned (`null`, `0`,
  `""`). That is not a bug: step over the assignments, or break further in with
  `offset`.
- Function names carry a leading backtick in some frames
  (``MinActiveRowVersionUpdateBatchJob.`run``) — that is the compiler's
  hookable-method wrapper; it is the method you asked for.

## Choosing a breakpoint that will actually be hit

- Prefer the **deepest method whose input you want to see** over the top of the
  call. For "why is this price wrong": `PriceDisc.findPrice` (or wherever the
  agreement lookup happens), not `SalesLine.insert`.
- For extension / CoC code of your own, break in *your* extension class method
  — it compiles like any class.
- If a path may run in **batch** (posting, workflow, cleanup), attach to
  `batch`, not `aos`. The every-minute system job
  `MinActiveRowVersionUpdateBatchJob.run` is a handy proof that the debugger is
  working on a quiet box.

## Guard rails (read once)

- **Pausing the AOS pauses everyone.** On a single-developer Tier-1 VM that is
  the developer's own client; still, keep pauses short and never leave a hit
  parked while you go read code.
- **Always detach**, including on the way out of an unrelated error. Check
  `xpp_debug_status` if you are unsure whether a session is still attached.
- The first attach in a machine session sets Visual Studio's
  "DisableAttachSecurityWarning" option (needed to attach to a NETWORK SERVICE
  process from a hidden VS). It is the standard D365 dev-box setting; mention it
  if the user asks why their VS no longer prompts.
- Do not attach to `aos` while the user is running a build or a DB sync; the
  app-pool recycle at the end of a build kills the debug session.
- Runtime-only elements (compiled DLLs without on-disk XML) cannot be
  source-debugged; the tools say so.

## When a trace is the better tool

If the question is "**which** code ran and in what order" rather than "what was
the value at this line", a debugger is slow going. That is trace territory
(execution traces / Trace Parser on the dev box), which never pauses the AOS.
Reach for the debugger when you already know *where* to look.
