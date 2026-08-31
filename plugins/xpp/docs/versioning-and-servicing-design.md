# Versioning, servicing, and the `dt` CLI — design & build plan

Status: agreed design, implementation in progress (increment 1). This note is
the roadmap; each increment is an independent commit.

## The problem

Distribution is source via `dotnet run`. One **service per box** (global mutex +
fixed pipe `xpp-service-v2`); many MCP sessions (one per agent) share it. When a
user upgrades the plugin, Claude Code installs the new version into its own cache
dir; new sessions launch the new MCP, which — seeing the pipe already alive —
**just connects to whatever service is running**, even if that's an *older*
version from a still-open session. Result: version-skewed MCP↔service. Fine when
version wasn't part of the equation; a latent bug now that we're versioning.

## The model: one shared service, newest-wins, additive contract

Keep a single shared service (the index is the expensive, stateful crown jewel —
never duplicate or rebuild it across a bump). Make it **version-negotiated**:

- The box runs exactly one service, and it's the **newest** version anyone has
  launched. Every MCP — old or new — connects to it.
- This is correct **iff** the service stays **backward-compatible with older MCP
  clients**: additive-only proto (never remove/renumber an RPC or field),
  forward-only schema migrations. (Breaking compat is allowed eventually, but as
  a deliberate choice, never an accident.)
- Newer MCP finds an **older** service → **take over** (it has the newer binary):
  ask the old service to drain + exit, spawn its own.
- Newer-or-equal service already running → **just connect** (subset of what it
  supports).
- Older MCP finds a **newer** service → **just connect** (never downgrade the
  running service).

No thrash: takeover only ever moves the service *up*, once per version increase.
At an upgrade moment, the first new session takes over; lingering old sessions
reconnect to the newer (compatible) service and keep working.

### Decisions (locked)

1. **Take over on every version bump** (not just contract changes). Straight
   versioning; don't invite "contract-compatible but functionally wrong." Users
   should be on the newest ASAP.
2. **Downgrade fails loudly, keyed on the DB SCHEMA version** (not plugin
   version — schema changes are far rarer). If a service starts and the cache DB
   is at a schema version **newer than this service knows**, it **refuses to
   launch** with a clear message telling the user to run `dt cache clear` — an
   intentional user action, never automated. (This is almost never a valid
   intentional scenario; if it is, the user should be in the loop.)
3. Takeover blip for a lingering session is acceptable (it self-heals; the index
   survives the handoff).
4. Retire the force-reinstall script; ship a first-class **`dt` CLI**.

## Increments

1. **Version handshake foundation** *(done)*. Single source of truth =
   `plugin.json` version, stamped into every assembly via a root
   `Directory.Build.props` (regex-read of plugin.json). `PingResponse` gains a
   clean, comparable `plugin_version` field (bare semver). Service reports it;
   the MCP's `EagerConnectionPrimer` reads its own version + the service's and
   logs whether they're in sync. **Observe-only — no behavior change yet.**
2. **Newest-wins takeover** *(done)*. New `RequestShutdown` RPC: the service
   answers FIRST, then stops, so the caller can tell "accepted" from "crashed";
   the host's normal graceful stop then drains in-flight calls, disposes the
   bridge pool, checkpoints the DB, and releases the pipe + global mutex.
   `EagerConnectionPrimer`: if running `plugin_version` < mine →
   `ServiceTakeover.SupersedeAsync` (request shutdown → wait for the pid to
   exit, escalating to a kill after a 20s grace, guarded by a process-name
   check against recycled pids → wait for the pipe to disappear) → re-Ping,
   which takes the connection factory's auto-spawn path and brings up our
   build. The re-Ping *confirms* the resulting version rather than assuming it.
   A newer service is left alone and used as-is (additive contract), so two
   sessions can't ping-pong. Every failure path is non-fatal: we log loudly and
   keep using the incumbent.

   Verified end-to-end against a purpose-built 0.0.9 service
   (`dotnet build -p:Version=0.0.9 -o misc/oldsvc`): 0.1.0 MCP superseded it,
   the old exited cleanly without needing the kill, the new one spawned from
   the expected path, and exactly one service remained. The reverse (0.0.8 MCP
   vs 0.1.0 service) correctly connected as a client and left it running.
3. **Loud downgrade refusal** *(done)*. Migrations are forward-only, so a cache
   written by a newer build is unreadable to an older one — the single failure
   mode that silently corrupts the index. `SchemaInstaller.PeekStoredVersion`
   reads the stored version straight off the file, read-only, and startup makes
   the call BEFORE building the host: stored > `CurrentVersion` → print the two
   ways forward and exit 78 (EX_CONFIG, so callers can tell it from a crash).
   Nothing is touched. Deliberately not self-healing — clearing the cache costs
   a full re-index plus a real embedding bill, and the usual cause is a stale
   session the user can simply close, so it stays a user decision.

   `EnsureSchema` keeps its own guard (`SchemaDowngradeException`) as a
   backstop for a cache swapped between the peek and the first open, and
   `Program` unwraps it to the same message.

   *Why the pre-host probe rather than just catching the throw:* the first
   attempt did exactly that and the result was unusable. `EnsureSchema` runs
   inside a hosted service, by which point the lifecycle, embedder and DB
   initializer are all opening the cache concurrently — the console filled with
   duplicate stack traces, the bridge pool had already spawned, and the process
   died on an unrelated teardown bug (`IndexWriter.DisposeAsync` cancelling an
   already-disposed CTS) that MASKED the real error entirely. That bug is fixed
   too — stop/dispose are now idempotent — because it turned *any* host-start
   failure into an unhandled crash with the cause buried.
4. **`dt` CLI** (PowerShell, Windows-only, `~/.local/bin/dt.cmd` shim forwarding
   to the marketplace-clone script — mirrors `mcc`). v1 surface: `setup`,
   `update`, `version`, `status`, `service restart|stop`, `cache clear`. Fast
   follow: `project init|status`. Maintainer commands stay in `dev.ps1`.
   - **Live read** (`status`/`version`): **shell out** to an extended
     `XppService.PingProbe` (a `status` mode doing the `GetStatus` RPC over the
     named pipe, printing JSON) — do NOT assembly-load gRPC into PowerShell (the
     named-pipe HTTP/2 transport is bespoke to replicate, and net10 assemblies
     can't load into Windows PowerShell 5.1). The probe is contract-only (no
     bridge deps), so it builds without the D365 extension located. `dt setup`
     builds it; `dt status` invokes the prebuilt exe; graceful when
     absent ("run `dt setup`") or pipe-dead ("service not running").

## Related musing (deferred)

Shipping **compiled** bins instead of source-`dotnet run` would remove first-run
build latency and make `dt status` always have an exe to invoke. It interacts
with versioning (a version becomes a signed bin set). Nothing in the shell-out
CLI design blocks it; revisit separately.
