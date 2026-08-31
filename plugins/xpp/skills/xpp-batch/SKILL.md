---
name: xpp-batch
description: Use when authoring a D365 F&O batch job, long-running operation, or any "runnable" an Action menu item points at — a data sweep, a recurring process, an import/export, a parallelized worker fan-out. Covers the modern SysOperation framework (data contract + service + controller), why it replaces RunBaseBatch, batch scheduling (mustGoBatch / execution modes), and parallel worker tasks via BatchHeader.addRuntimeTask / addDependency.
---

# Authoring a batch / long-running job (SysOperation)

"Write a job" — a class that sweeps records, runs on a schedule, imports a file,
or fans work out across batch threads — is one of the most common things a D365
developer asks for, and the framework choice is the first decision. Make it
deliberately.

> **New batch/dialog jobs use SysOperation, not RunBaseBatch.**
>
> `RunBaseBatch` is legacy. It makes you hand-write `pack()`/`unpack()`,
> `dialog()`/`getFromDialog()`, and a version-migration branch in `unpack()`
> every time you add a parameter. SysOperation generates all of that from a data
> contract. `RunBaseBatch` is still everywhere in the corpus (it is a decade
> older), so pattern-matching the existing code leads you to the wrong one — use
> `RunBaseBatch` **only** when modifying an existing `RunBaseBatch` class.
>
> Parallelism is not a reason to prefer the old one: both support
> `BatchHeader.addRuntimeTask` / `addDependency` (see **Parallel workers**).

Load `dynamics-xpp:xpp-class` before authoring the classes, and
`dynamics-xpp:xpp-menuitem` for the Action menu item that launches it.

---

## The three objects

SysOperation is a **contract + service + controller** triple. The contract is
your parameters, the service does the work, the controller wires them to the UI
and the batch queue.

| Object | Type | Role |
|---|---|---|
| `<Name>Contract` | AxClass `[DataContractAttribute]` | Typed parameters — serialized and rendered as the dialog for free |
| `<Name>Service` | AxClass : `SysOperationServiceBase` | The operation method that does the work, taking the contract |
| launch point | a `SysOperationServiceController` (used directly, or a subclass) | Binds service+method+mode, builds the dialog, enqueues the batch |

### The data contract

```xpp
[DataContractAttribute]
public class ConCustomerSweepContract
{
    FromDate  fromDate;
    NoYes     includeInactive;
}

[DataMemberAttribute('FromDate')]
public FromDate parmFromDate(FromDate _fromDate = fromDate)
{
    fromDate = _fromDate;
    return fromDate;
}

[DataMemberAttribute('IncludeInactive')]
public NoYes parmIncludeInactive(NoYes _v = includeInactive)
{
    includeInactive = _v;
    return includeInactive;
}
```

Each `parm` method decorated `[DataMemberAttribute]` becomes a dialog field and a
serialized member — **no `pack`/`unpack`, and adding a parameter is one more
`parm` method, not a version branch.** Optional decorations shape the dialog:
`[SysOperationLabelAttribute(literalStr("..."))]`, `[SysOperationHelpTextAttribute(...)]`,
and `[SysOperationGroupAttribute('groupName', ...)]` to lay fields into groups.

### The service class

```xpp
public class ConCustomerSweepService extends SysOperationServiceBase
{
    public void run(ConCustomerSweepContract _contract)
    {
        DirPartyTable party;
        while select party
            where party.CreatedDateTime >= _contract.parmFromDate()
        {
            // ... the work ...
        }
    }
}
```

The operation method (any name — `run`, `process`, `execute`) takes the contract
by value. `SysOperationServiceBase` supplies progress + logging plumbing. Keep
the method idempotent and restartable — a batch can be retried.

### The controller / launch point

For the common case you don't subclass — you construct a
`SysOperationServiceController` naming the service, the method, and the execution
mode, and point the **Action menu item** at a class whose `main` starts it:

```xpp
class ConCustomerSweepController
{
    public static void main(Args _args)
    {
        SysOperationServiceController controller = new SysOperationServiceController(
            classStr(ConCustomerSweepService),
            methodStr(ConCustomerSweepService, run),
            SysOperationExecutionMode::Synchronous);
        controller.initializeFromArgs(_args);
        controller.startOperation();
    }
}
```

`startOperation()` shows the contract's dialog, then runs (or enqueues) per the
execution mode. `SysOperationExecutionMode`:

- `Synchronous` — run inline (interactive).
- `ScheduledBatch` — enqueue to the batch server (the "Run in the background" tab
  of the dialog appears; the user picks recurrence).
- `ReliableAsynchronous` — fire-and-forget on the batch server, no schedule.

**Subclass `SysOperationServiceController` only when you need to** override the
dialog, force batch, or customize the caption:

```xpp
class ConCustomerSweepController extends SysOperationServiceController
{
    protected void new()
    {
        super(classStr(ConCustomerSweepService), methodStr(ConCustomerSweepService, run),
              SysOperationExecutionMode::ScheduledBatch);
        this.parmDialogCaption("Customer sweep");
    }

    public static ConCustomerSweepController construct() { return new ConCustomerSweepController(); }

    public static void main(Args _args)
    {
        ConCustomerSweepController controller = ConCustomerSweepController::construct();
        controller.initializeFromArgs(_args);
        controller.startOperation();
    }

    // Force batch-only (the SysOperation equivalent of RunBaseBatch.mustGoBatch):
    public boolean mustGoBatch() { return true; }
}
```

The **Action menu item** targets the controller class (the one with `main`). See
`dynamics-xpp:xpp-menuitem` — its `Object` is the controller, `ObjectType` =
`Class`.

---

## Parallel workers — fan out across batch threads

To split work across batch threads (e.g. one task per data partition), add
**runtime tasks** to the running batch's header. Each task is its own controller
instance; `addDependency` sequences them.

```xpp
public void run(ConCustomerSweepContract _contract)
{
    BatchHeader batchHeader = this.getCurrentBatchHeader();   // null if run interactively
    if (batchHeader)
    {
        for (int i = 1; i <= partitions; i++)
        {
            ConCustomerSweepWorkerController worker = ConCustomerSweepWorkerController::construct();
            worker.parmPartitionId(i);                        // parm methods push per-worker state
            batchHeader.addRuntimeTask(worker, this.getCurrentBatchTask().RecId);
        }
        batchHeader.save();
    }
    else
    {
        // interactive fallback: do the work in-process
    }
}
```

- `getCurrentBatchHeader()` returns null when the operation runs interactively —
  branch on it so the job still works outside batch.
- `addRuntimeTask(controller, currentTaskRecId)` schedules a worker as part of
  the SAME batch job. `addDependency(taskA, taskB)` makes A wait for B.
- Each worker is a full SysOperation controller; push per-worker parameters
  through the worker contract's `parm` methods before `addRuntimeTask`.

Both SysOperation and RunBaseBatch expose this — it is not a reason to choose one
framework over the other.

---

## Gotchas

- **Don't hand-write `pack`/`unpack`.** If you're writing serialization, you're
  on the wrong framework — that's the RunBaseBatch tell.
- **The operation method takes the contract by value.** Don't reach for member
  state on the service; read everything from the contract so batch
  serialization is complete.
- **`getCurrentBatchHeader()` is null interactively.** Always provide the
  non-batch path, or the job breaks when a developer runs it from a menu item
  without scheduling.
- **Keep the operation restartable.** A batch task can be retried by the runtime;
  design the work to tolerate a partial previous run (set-based where possible,
  or track a high-water mark).
- **Long select loops need `forUpdate` + a transaction** only around the writes,
  not the whole scan — see `dynamics-xpp:xpp-data`.

---

## See also

- `dynamics-xpp:xpp-class` — the contract, service, and controller classes
- `dynamics-xpp:xpp-menuitem` — the Action menu item that launches the controller
- `dynamics-xpp:xpp-data` — the set-based / transactional patterns inside the operation
- `dynamics-xpp:xpp-security` — a menu item needs a privilege → duty → role to be reachable
