# Live X++ debugging — design and findings

*2026-09-14. Shipped as the `xpp_debug_*` tools + the `xpp-debug` skill.*

## What we set out to do

Let an agent attach a debugger to the F&O process on the dev box — the AOS
worker (`w3wp.exe`, app pool `AOSService`) or the batch host (`Batch.exe`) —
set breakpoints in X++, catch a hit, inspect the stack and values, step, and
release. Headless: no VS window, no human at a keyboard.

## What X++ debugging actually is

X++ compiles to ordinary .NET IL: `Dynamics.AX.<Package>.dll` plus ~240
`.netmodule`s per package, each with a full Windows PDB alongside. Classes land
in the `Dynamics.AX.Application` namespace with their X++ names. So the engine
is Visual Studio's stock **"Managed (.NET Framework 4.x)"** engine; nothing
exotic.

Two things the Dynamics 365 VS extension adds are what make it *usable*:

1. an **X++ expression evaluator** registered against that engine (so `this`,
   `salesLine.SalesPrice`, `CustTable::find('C1').Name` evaluate as X++), and
2. a **source resolver**: the PDBs reference no on-disk files. Their documents
   are virtual — `xppSource://Source/<Model>\AxClass_<Name>.xpp` — and the
   extension maps them to `<PackagesLocalDirectory>\bin\XppSource\<Model>\
   AxClass_<Name>.xpp`, generating the file the first time someone opens the
   element's code in VS.

Consequences that shaped everything:

- **Function breakpoints never bind** (`Dynamics.AX.Application.PriceDisc.
  findPrice` stays pending forever, even with symbols loaded). Only file/line
  breakpoints on the generated `.xpp` bind, and only once the D365 package is
  loaded in that VS instance.
- The generated `.xpp` is **deterministic from the AOT XML**: the class
  declaration with blank edges trimmed and its closing brace dropped, then each
  method's source (blank edges trimmed; the XML already carries the indentation)
  followed by a blank line, then `}`. Validated byte-for-byte against a
  VS-generated file. So the bridge materializes the source itself and can debug
  an element nobody has ever opened in VS. (Forms and extensions have a
  different layout — not supported yet.)

## Architecture

```
MCP (per session)  --gRPC-->  XppService (one per box)  --stdio JSON-RPC-->  XppDebugBridge (net48)
                                                                                    |  DTE / COM, STA thread
                                                                                    v
                                                                            hidden devenv.exe -Embedding
                                                                                    |  Managed (.NET Framework 4.x) engine
                                                                                    v
                                                                            w3wp.exe / Batch.exe
```

- The debug bridge is a **net48** child like the metadata bridge (COM/STA
  interop is happiest there; keeps a heavyweight stateful thing out of the
  service). Same JSON-RPC plumbing, linked from `XppMetadataBridge/Rpc`.
- The **service** owns exactly one bridge (`DebugBridgeHost`), started on the
  first debug RPC and disposed with the host, and resolves an element to its
  XML + model from the index for the bridge.
- **Typed interop** (`Microsoft.VisualStudio.Interop.dll`, referenced from the
  installed VS — nuget.org is disabled on these boxes, and VS is required
  anyway) on a dedicated **STA thread with an IOleMessageFilter**. Late-bound
  COM lied repeatedly during the experiments: `CurrentMode` reported Design
  while typed reads said Run, `StackFrames` came back empty, `Attach2` was
  unreachable. Don't script DTE late-bound for anything that matters.

## Rules learned the hard way

Each of these cost at least one failed run.

1. **Spawn devenv yourself, elevated, with `-Embedding`.** COM activation
   (`CreateInstance`) launches it *unelevated*; the D365 package then pops
   "Visual Studio must run as administrator" — a WPF modal a hidden VS can never
   dismiss — and everything downstream flakes (`LocalProcesses` returns empty,
   attaches never complete). The Bash tool's process was not elevated; the
   PowerShell tool's was. The service inherits whatever Claude Code has.
2. **Find the DTE in the ROT by pid** (`!VisualStudio.DTE.17.0:<pid>`).
   `GetActiveObject` returns the user's own VS.
3. **Only a real `devenv.exe`.** `vswhere -products *` also lists VS-shell
   products; it once returned SQL Server Management Studio, and
   `SSMS.exe -Embedding` never yields a DTE (400 s hang). Prefer the install
   that carries the D365 extension.
4. **Take `LocalProcesses` once** per instance; it enumerated correctly on the
   first call and returned empty on every later call.
5. **Attach with the engine by exact name.** A loose match (`'4'`) selected
   "Managed (.NET Core, .NET 5+)" because its GUID contained a 4 — the attach
   "succeeds", nothing binds, no thread has frames, `Break` fails with
   0x89710051.
6. **Suppress the Attach Security Warning** (attaching to a NETWORK SERVICE
   process). It is a WPF modal; the only automation-reachable switch is a
   settings import (`DisableAttachSecurityWarning=1`) — which lands in the
   user's shared VS settings. Documented in the skill.
7. **Load the D365 package before binding anything** — opening any `.xpp`
   does it (the bridge opens a generated `Global.xpp`). Reaching the package's
   options page instead cost 280 s once and blocked outright another time.
8. **`Attach2` returns before the session exists.** `DTE.Debugger` can read
   null for a few seconds; wait for `DebuggedProcesses` to be non-empty.
9. **Recovery**: an orphaned attached VS is recoverable — reconnect via the
   ROT and `DetachAll`. Killing an attached devenv **does** take the debuggee
   with it (measured); it is the last resort, and every path that does it
   says so.
10. **Never open a document in VS while the debuggee is paused**, and never
    let one automation call hold the session lock for minutes. See "The
    wedge" below.
11. **Build with `dotnet build`, not msbuild/VS**: stock D365 boxes lack the
    .NET Framework 4.8 targeting pack; the SDK supplies it.
12. **The installed plugin runs from Claude Code's cache copy**, not the
    marketplace clone `dt` builds. Anything the service spawns must be in the
    MCP project's build-only `ProjectReference`s or it is never built there.

## Safety posture

- A paused AOS pauses every client session on the box. The bridge's watchdog
  resumes a hit left paused longer than `maxPauseSeconds` (default 300).
- Detach always resumes first and deletes every breakpoint. The bridge's own
  exit path (service shutdown closes stdin) does the same and quits its VS.
- IIS's app-pool ping timeout for AOSService is 600 s here; short pauses are
  well inside it.

### The wedge (0.3.0) and what it taught

The first field use hit a full freeze: a breakpoint set *while paused* opened
its document in VS (`ItemOperations.OpenFile`) and that call never returned
in break mode. It held the session lock and the single STA thread; the
JSON-RPC loop was strictly sequential so every later request -- detach
included -- queued behind it; and the watchdog `TryEnter`ed the same lock, so
the safety floor was behind the wedge too. The AOS sat frozen 13 minutes.

Fixes, each general:
- No document is opened while the target is paused; breakpoints bind by
  file/line against the generated source without the file open (verified:
  a second breakpoint while paused binds in 0.0s).
- The debug bridge dispatches requests concurrently (`ConcurrentJsonRpcServer`);
  the STA worker knows when a call is stuck past its deadline and fails later
  calls fast; every automation call is bounded in seconds.
- `Detach` takes the lock with a 2s `TryEnter`, tries a bounded graceful
  detach, then kills the hidden VS. `force` skips the queue entirely. The
  service has its own fallback that kills the bridge and the VS pid it
  recorded at attach.
- The watchdog runs lock-free on its own thread and escalates to the kill.

**Measured, not assumed: killing the debugger kills the debuggee.** A .NET
Framework process does not survive losing its VS debugger, paused *or*
running -- `Batch.exe` was gone after every kill test. The batch service's
recovery restarts it after 30s; IIS respawns a dead AOS worker within
seconds but every session on it is lost. So the kill is the last resort,
every path reports `targetTerminated` when it happens, and the skill tells
the agent to relay the cost. The reporter's "kill the hidden devenv releases
the AOS immediately" was IIS restarting the worker.

## Measured on this box

- cold VS start to automation ready: ~7–10 s; D365 package load ~5 s
- attach (batch): ~5–20 s; breakpoint bind: < 1 s once the module is loaded
- hit capture (stack + `this` + locals + watches): ~1–6 s
- step: < 1 s

## Not done / next

- Forms and extension elements as breakpoint targets (different `.xpp`
  layout to reverse-engineer; the extension's generator is the reference).
- Function-name breakpoints (would remove the source generation, but VS won't
  bind them for X++).
- A trace-first tool (ETW execution traces / Trace Parser) for "which code
  ran" questions — never pauses the AOS.
- Headless request triggering against `w3wp` (the dev box's offline
  developer-auth certificate is what VS uses to run forms without a browser
  login) so an agent can hit its own breakpoints without a human in the
  client.

## Alternatives considered

- **vsdbg** (VS Code's engine, speaks DAP — the ideal machine protocol):
  licensed only for use with Visual Studio Code. Not shippable.
- **WinDbg/cdb + SOS**: legitimate and scriptable, but no X++ source mapping
  and no usable managed stepping.
- **Own ICorDebug client** (`mscordbi.dll` is in-box): licensable and
  headless, weeks of work, loses the X++ expression evaluator. The fallback if
  hosting VS proves too fragile.
