---
name: xpp-data
description: Use when writing X++ code that reads from or modifies the D365 F&O database — select statements, while select, joins, set-based operations (update_recordset / insert_recordset / delete_from), the per-record insert/update/delete methods, transaction scoping with ttsBegin / ttsCommit / ttsAbort, and the integrity checks that govern them. Covers the syntax differences from ANSI SQL and the foot-guns (doInsert/doUpdate, set-based fallback, missing forUpdate) that bite developers most often.
---

# X++ data selection and manipulation

X++ has SQL-style statements baked into the language — `select`,
`while select`, `update_recordset`, `insert_recordset`, `delete_from` —
that look like SQL but aren't quite. The differences mostly matter:
syntax order, set-based fallback rules, mandatory transactions, and
the difference between `insert()` / `update()` (which run business
logic) vs `doInsert()` / `doUpdate()` (which skip it and are usually
wrong).

Load `dynamics-xpp:xpp-language` and `dynamics-xpp:xpp-table` first if you haven't. The
identifier and table conventions there underpin everything below.

> **Ground non-trivial syntax against the real codebase, not docs.** For exact
> clause ordering / legal shapes of anything past a basic statement (set-based
> aggregates with join + group by + exists-join, etc.), search real compiling
> method bodies with `xpp_search_code` (FTS over the corpus) rather than
> assembling it from ms-learn fragments and your own reasoning — the codebase is
> the authoritative, compiling source of truth. e.g.
> `xpp_search_code("insert_recordset" "exists join" "group by")` surfaces precedents
> that pin the ordering in one pass. Doc-reconstruction here produces
> plausible-but-non-compiling shapes.

---

## The two-level mental model

X++'s data layer has two surfaces. Knowing which you're in keeps you
out of trouble:

1. **Per-record API on the table buffer.** `myTable.insert()`,
   `myTable.update()`, `myTable.delete()`, plus the validation hooks
   `validateWrite()`, `validateField()`. Runs business logic. Triggers
   events. Honors Chain of Command. **This is the default.**
2. **Set-based statements.** `update_recordset ... setting ...`,
   `insert_recordset ... select ...`, `delete_from ...`. One round-trip
   to SQL. Fast on large operations. **May silently fall back to the
   per-record path** under specific conditions (see below) — when it
   does, the perf benefit evaporates.

Choose the per-record API when you need business logic to run.
Choose set-based when you have many rows and the per-record overhead
matters. Verify which one you're actually getting (see
"Set-based fallback" below).

---

## `select` — fetching one record

The simplest form:

```xpp
CustTable custTable;
select * from custTable;
info("AccountNum: " + custTable.AccountNum);
```

Key behaviors:

- **One record per `select`.** To traverse many, use `while select`.
- **The buffer variable IS the result.** `custTable.AccountNum` reads
  the selected row's value. There is no separate "result set"
  object.
- **`RecId == 0` means no record was found.** Test
  `if (myTable.RecId != 0)` (or just `if (myTable)`, which checks the
  same thing).
- **Order is unpredictable without `order by`.** Don't assume the
  database returns rows in any specific order.

### Syntax skeleton

```
select { findOption } [ fieldList from ] tableBufferVariable
    [ index { indexName | hint indexName } ]
    [ order by | group by ]
    [ where ... ]
    [ join ... ]
```

### Find options (qualifiers before the table)

- `forUpdate` — exclusive update lock (required before `.update()`).
- `firstOnly` / `firstOnly10` / `firstOnly100` / `firstOnly1000` —
  cap the row count. Use `firstOnly` whenever you only need one
  record; it tells the runtime to allocate a single buffer rather
  than an iterable result set.
- `crossCompany` — read from all companies the user has access to.
  Optional container to restrict: `select crossCompany:['dat','dmo'] ...`.
- `validTimeState` — for tables with `ValidTimeStateFieldType` set;
  selects rows valid at a given time.
- `forceLiterals` / `forcePlaceholders` — control how the kernel
  reveals `where`-clause values to SQL Server during optimization.
  **`forceLiterals` is an SQL-injection risk** — don't use it on
  caller-supplied values.
- `forceNestedLoop` / `forceSelectOrder` — join algorithm and table
  order hints. Combine; use rarely, only after profiling.
- `firstFast` — return the first row faster (total time may be
  slower).
- `noFetch` — defer the actual fetch to the first `next` call.
  Useful for passing a query to another object.
- `optimisticLock` / `pessimisticLock` — concurrency mode overrides.
- `repeatableRead` — current transaction must complete before other
  transactions can modify what this read.

### Aggregate functions

`sum`, `avg`, `count`, `minof`, `maxof`. Result lands in the field
you aggregated:

```xpp
CustTable custTable;
select sum(CreditMax) from custTable;
info(strFmt('%1', custTable.CreditMax));  // sum is in CreditMax
```

- Only integer and real fields can be aggregated.
- `sum` returning `null` in standard SQL returns **no row at all**
  in X++ (X++ has no `null` for database values).

---

## `where` clauses — syntax that differs from ANSI SQL

Critical syntax differences from standard SQL — every X++ author
trips on these:

| Standard SQL | X++ | Note |
|---|---|---|
| `=` | `==` | Equality is double-equals (single-equals is assignment). |
| `<>` or `!=` | `!=` | Inequality. |
| `NOT` | `!` | Logical negation. `!(a == b)` not `not a == b`. |
| `LIKE 'foo%bar'` | `like 'foo*bar'` | Wildcards: `*` = many, `?` = exactly one. |
| `IN (a, b, c)` | `in containerVar` | The right-hand side is an X++ container variable. |
| `AND` / `OR` | `&&` / `\|\|` | C-style logical operators. **Same precedence — always parenthesize.** |

```xpp
// where clause example
while select custTable
    where custTable.CreditMax > 0
       && (custTable.AccountStatement == CustAccountStatement::Always
        || custTable.AccountStatement == CustAccountStatement::Periodical)
       && !custTable.Blocked
{
    // ...
}
```

`in` with a container is the idiomatic way to express "one of these
values":

```xpp
container statementModes = [CustAccountStatement::Always, CustAccountStatement::Periodical];
while select custTable
    where custTable.AccountStatement in statementModes
{
    // ...
}
```

This is much cleaner than a chain of `||`s.

---

## `join` — syntax that differs from ANSI SQL

X++ joins look like ANSI SQL joins but the syntactic position
differs:

- **No `on` keyword.** Join criteria go in the `where` clause.
- **The `join` clause comes AFTER `order by` / `group by` / `where`**
  in some forms — actually the rule is `from`/`join` first, then
  `order by`/`group by`, then `where`. The `where` filter sits at
  the end. (Re-read MS docs if you're unsure; this trips people up.)
- **Default join is INNER.** There's no `inner` keyword; just `join`.
- **`outer join` is LEFT outer.** There is no `right` outer join in
  X++; reorder the tables instead.
- **`exists join` / `notexists join`** are first-class — semi-joins
  baked into the syntax. Don't write subqueries with `EXISTS` SQL
  style; use these.

```xpp
// Inner join
while select custTable
    join custGroup
    where custTable.CustGroup == custGroup.CustGroup
{
    // ...
}

// Left outer join
while select custTable
    outer join custGroupTable
    where custTable.CustGroup == custGroupTable.CustGroup
{
    // ...
}

// Existence semi-join
while select custTable
    exists join ctrTable
    where ctrTable.AccountNum == custTable.AccountNum
{
    // custTable rows that HAVE a matching ctrTable row
}

// Non-existence anti-join
while select custTable
    notexists join ctrTable
    where ctrTable.AccountNum == custTable.AccountNum
{
    // custTable rows that DO NOT have a matching ctrTable row
}
```

The columns in the field list (between `select` and `from`) **must
come from the table named in the `from` clause**, not from any
joined table. You can't qualify columns in the field list with their
table name. To get a joined column's value, read it off the joined
buffer variable.

---

## `while select` — looping over many rows

```xpp
while select custTable
    order by custTable.AccountNum
    where custTable.AccountNum >= '4010'
       && custTable.AccountNum <= '4100'
{
    info(strFmt("%1: %2", custTable.AccountNum, custTable.Name));
}
```

Key behaviors:

- **The `select` part runs once**, immediately before the loop body.
- **Boolean expressions in the where are tested once** at query time,
  not per iteration. This is different from `while` in C/C#. The
  example below loops **multiple** times even though `iCounter < 1`
  looks per-iteration:

  ```xpp
  int iCounter = 0;
  CustTable custTable;
  while select custTable where iCounter < 1
  {
      iCounter++;  // iCounter goes 1, 2, 3, ... but the loop keeps running
      info(strFmt("%1", custTable.AccountNum));
  }
  ```

  This is a real trap — code review for it.
- **For updates inside the loop**, use `while select forUpdate ...`.
  Without `forUpdate`, calling `.update()` on the buffer throws.
- Always inside `ttsBegin/ttsCommit` when modifying data inside the
  loop.

---

## Transactions — `ttsBegin` / `ttsCommit` / `ttsAbort`

Two integrity checks govern every modification:

1. **`forUpdate` check** — you can only `update()` or `delete()` a
   record you selected `forUpdate` (or with `selectForUpdate()`).
2. **`ttsLevel` check** — you can only `update()` or `delete()` in
   the same transaction scope where you selected the record for
   update.

The canonical pattern:

```xpp
ttsBegin;
    CustTable custTable;
    select forUpdate custTable
        where custTable.AccountNum == '2000';
    if (custTable)
    {
        custTable.CreditMax = 5000;
        custTable.update();
    }
ttsCommit;
```

Nesting: `ttsBegin` blocks can nest. Nothing commits to the database
until the outermost `ttsCommit` runs. If any `ttsAbort` runs (or any
exception escapes the block), the whole nested chain rolls back.

### Use `throw error()` instead of `ttsAbort`

X++'s preferred pattern for rollback is to throw an exception
rather than call `ttsAbort` explicitly. `throw` automatically aborts
the current transaction as it unwinds:

```xpp
ttsBegin;
    if (someCheck == false)
    {
        throw error("@MyLabels:InvalidStateMessage");  // rolls back automatically
    }
    // ... do work ...
ttsCommit;
```

This is cleaner because the rollback is a side effect of the error
handling rather than an extra statement to remember.

---

## Per-record CUD: `insert`, `update`, `delete`

### Insert

```xpp
ttsBegin;
    CustTable custTable;
    custTable.AccountNum = '2000';
    custTable.CustGroup = '1';
    custTable.insert();
ttsCommit;
```

- **Don't `select` before `insert`.** The buffer should be a fresh,
  field-populated record.
- `insert()` triggers `validateWrite()`, the database event handlers
  (`onInserting`, `onInserted`), Chain of Command extensions, and
  any table-level overrides.
- `RecId` and system fields are auto-populated.

### Update

```xpp
ttsBegin;
    CustTable custTable;
    select forUpdate custTable where custTable.AccountNum == '4000';
    if (custTable)
    {
        custTable.CreditMax = 5000;
        custTable.update();
    }
ttsCommit;
```

`update()` triggers `validateWrite()`, `onUpdating`/`onUpdated`,
Chain of Command, and the table's `update()` override.

### Delete

```xpp
ttsBegin;
    CustTable custTable;
    while select forUpdate custTable where custTable.AccountNum == '2000'
    {
        custTable.delete();
    }
ttsCommit;
```

`delete()` honors `DeleteActions` defined on the table (cascades,
restricts), triggers `onDeleting`/`onDeleted`, CoC, and the
`delete()` override.

### `doInsert` / `doUpdate` / `doDelete` — usually wrong

These bypass **everything** — event handlers, Chain of Command, the
`insert()`/`update()`/`delete()` override itself.

```xpp
// AVOID unless you have a specific, documented reason
custTable.doInsert();  // skips validateWrite, events, CoC
custTable.doUpdate();  // same
custTable.doDelete();  // same — also skips DeleteActions
```

Per MS Learn: *"It's generally considered bad practice to use
**doInsert**, and you shouldn't use it."* Same for `doUpdate` /
`doDelete`. The legitimate uses are very narrow (e.g. seeding system
data during upgrade scripts where running the full logic would be
incorrect). When in doubt, use the regular method.

---

## Set-based operations

### `update_recordset`

```xpp
ttsBegin;
    update_recordset custTable
        setting CreditMax = custTable.CreditMax + 1000
        where custTable.CreditMax > 0;
ttsCommit;
```

Multiple columns:

```xpp
update_recordset custTable
    setting
        CreditMax = custTable.CreditMax + 1000,
        AccountStatement = CustAccountStatement::Always
    where custTable.CreditMax > 0;
```

With joins (data from joined tables feeds the `setting`):

```xpp
update_recordset tabEmpl
    setting currentStatusDescription = tabDept.DeptName + ", " + tabProj.ProjName
join tabDept where tabDept.DeptId == tabEmpl.DeptId
join tabProj where tabProj.ProjId == tabEmpl.ProjId;
```

### `insert_recordset`

Copies rows from one or more source tables into a destination
table, in one DB trip:

```xpp
insert_recordset valueSumName (Name, ValueSum)
    select Name, sum(Value)
    from nameValuePair
    group by Name;
```

Constraints:
- **Source field list must match destination field list** in order
  and count.
- **Cannot use literals** like `128` or `"text"` in the source
  position. Bind them to local variables and reference the variables.
- Variables of compatible type can be sourced (the example above
  uses local `id_var`, `name_var`).
- **`DuplicateKeyException` can NOT be caught** when thrown by a set-
  based `insert_recordset`. To catch and handle dup-key conflicts,
  fall back to the per-record `.insert()` loop.

### `delete_from`

```xpp
ttsBegin;
    delete_from custTable
        where custTable.AccountNum == '2000';
ttsCommit;
```

Same general shape as the others. Like the rest, falls back to
per-record under the conditions below.

### Set-based fallback — the silent perf trap

Set-based operations **silently downgrade to per-record loops** when
any of these conditions hold for the target table:

| Condition | `delete_from` | `update_recordset` | `insert_recordset` | RecordInsertList/SortedList | Override flag |
|---|---|---|---|---|---|
| Non-SQL tables | Falls back | Falls back | Falls back | Falls back | (n/a) |
| Delete actions defined | Falls back | — | — | — | `skipDeleteActions` |
| Database log enabled | Falls back | Falls back | Falls back | — | `skipDatabaseLog` |
| Method override (insert/update/delete) | Falls back | Falls back | Falls back | Falls back | `skipDataMethods` |
| Alerts configured on table | Falls back | Falls back | Falls back | — | `skipEvents` |
| `ValidTimeStateFieldType != None` | Falls back | Falls back | Falls back | Falls back | (no skip) |

Use the `skip*` setters BEFORE the set-based statement to suppress
the relevant cause:

```xpp
custTable.skipDataMethods(true);
custTable.skipEvents(true);
update_recordset custTable
    setting CH_ECommOrder = NoYes::Yes
    where custTable.RecId == someRecId;
```

**Important caveats:**
- The `skip*` flags only suppress the fallback when the fallback was
  caused by *that specific factor*. If multiple conditions apply,
  you need each corresponding skip.
- **`skip*` settings are IGNORED when the operation downgrades to
  record-by-record for any other reason.** Setting
  `skipDataMethods` doesn't help if alerts are configured (alerts
  trigger the downgrade independently).
- `ValidTimeStateFieldType` has no skip — set-based always falls
  back on those tables.
- Always verify the perf gain you expected actually materialized
  (database trace or profiler) on a representative table.

---

## Other inserters: `RecordInsertList` and `RecordSortedList`

When you need to insert many records but want to handle them in X++
before the DB hit:

```xpp
RecordInsertList insertList = new RecordInsertList(myTable.TableId, true);
int i;
for (i = 1; i <= 100; i++)
{
    myTable.value = i;
    insertList.add(myTable);
}
insertList.insertDatabase();
```

- `RecordInsertList` — batched insert, no sort order.
- `RecordSortedList` — batched insert with custom sort key. Use when
  the data doesn't already have an index matching the order you want.

Both are subject to the same fallback rules as `insert_recordset`.

---

## Aggregates with `group by` and `order by`

```xpp
while select count(CreditMax) from custTable
    join custGroup
    order by custGroup.Name
    group by custGroup.CustGroup
    where custTable.CustGroup == custGroup.CustGroup
        && custGroup.Name like "*Days*"
{
    info(custTable.CustGroup + ": " + int642Str(custTable.CreditMax));
}
```

- A query can have multiple `group by` clauses; only one can use
  table-qualified field names.
- `order by` follows the same syntactic position rules as `group by`.
- Both must appear AFTER the `from`/`join` and BEFORE any `where`
  clause on the same join level.
- Aggregate result lands in the field you aggregated on the buffer
  (not in a separate result variable).

---

## Coalesced "one row per entity" temp surface (set-based, for a FormPart)

A common need: a grid showing **one aggregated row per case/order/entity**
(count of child rows, oldest/newest timestamp, an "any X" flag), often scoped by
a filter. Build it **set-based** into a temp table — do NOT enumerate child rows
client-side.

**Anti-pattern (do not):** aggregating into an in-memory `Map` in a `while
select` loop, then enumerating into a temp table. It pulls every matching child
row to the client — fine at tens of rows, catastrophic at production volume. The
scale problem is invisible at dev-data volume, so it passes review and bites
later.

**Backing table: a TempDB temp table** (`TableType=TempDB`), not InMemory.
TempDB is the MS go-forward preference and — crucially — it can be **joined to
real tables in standard set-based statements** (InMemory can't). The FormPart
datasource shares the buffer with `<Table>.linkPhysicalTableInstance(formBuffer)`
in the datasource `init()`, and `executeQuery()` does:
`delete_from formBuffer; <Builder>::buildInto(formBuffer, <filter>); super();`.
(InMemory's `setTmpData` is the older path.)

**The build, set-based, in passes** (clause order verified against the codebase —
ground it with `xpp_search_code`, see below):

1. **Aggregate into the temp, grouped on the child's OWN key.** `insert_recordset`
   aggregates can only group by the FROM-table's own field — you cannot aggregate
   the from-table while grouping by a *joined* table's field:
   ```xpp
   insert_recordset tmp (CaseId, Cnt, Oldest)
       select CaseId, count(RecId), minof(CreatedDateTime) from childLog
       group by childLog.CaseId
       where <child conds>
       exists join filterTbl where filterTbl.Key == childLog.ScopeKey;
   ```
   Clause order: `group by` → `where` → chained `exists join`.
2. **Enrich attributes from the parent via a set-based join update:**
   `update_recordset tmp setting Name = parent.Name join parent where parent.CaseId == tmp.CaseId;`
3. **Flags whose condition spans tables: one `exists join` update per disjunct.**
   An "any urgent" flag where urgent = (cfg.IsUrgent || child.UrgentComment)
   can't reduce to one `maxof`; run two `update_recordset tmp setting Flag = NoYes::Yes exists join ... where <disjunct>` passes.
4. **Optional narrowing:** `delete_from tmp where tmp.Col != value` on the
   now-enriched columns.

Net: zero client-side row enumeration, a handful of SQL round-trips, scales to
production. (If the surface is STATIC — no per-interaction re-scoping — consider
a GROUP BY aggregate **AxView** instead, which views fully support; see
`dynamics-xpp:xpp-view`. Reach for this temp-table provider when the surface is
rebuilt dynamically, e.g. on a workspace page-filter change.)

---

## Cross-company queries

```xpp
container companies = ['dat', 'dmo'];
select crossCompany:companies * from custTable;
```

- Without `:container`, reads from all companies the user has access
  to.
- Container variable lets you restrict.
- The expression can be inline: `crossCompany:(['dat'] + ['dmo'])`.
- Crosscompany queries do NOT auto-filter by `DataAreaId`; the
  result carries records from multiple companies.
- Be careful: the buffer's `DataAreaId` changes as you iterate. If
  you `.update()` on a crosscompany loop, the right company's row
  gets updated.

---

## Time-versioned tables

When a table's `ValidTimeStateFieldType != None`, it carries
`ValidFrom`/`ValidTo` columns and rows are time-versioned:

```xpp
utcDateTime asOf = DateTimeUtil::utcNow();
select validTimeState(asOf) * from history;
```

`validTimeState` selects rows valid at the supplied point (or range)
in time. Without it, you only get current rows.

---

## Index hints — use sparingly

```xpp
custTable.allowIndexHint(true);  // required before using index hint
while select forUpdate custTable
    index hint AccountIdx
    where custTable.AccountNum == accountNum
{
    // ...
}
```

- **Must call `allowIndexHint(true)` on the table** before the
  `index hint` keyword does anything. Default is ignored.
- Apply only to statements without dynamic where/order — otherwise
  the hint can backfire when the data shape shifts.
- The `index` keyword (without `hint`) is a *sort* hint, not a
  *physical-index* hint. They look similar but mean different things.

Per MS: *"Use index hint sparingly and with caution, and only when
you're sure that it improves performance."*

---

## Handling `DuplicateKeyException` on per-record inserts

```xpp
ttsBegin;
try
{
    while select sourceTable order by SourceKeyField asc
    {
        destinationTable.clear();
        destinationTable.DestinationKeyField = sourceTable.SourceKeyField + numberAdjust;
        destinationTable.insert();
    }
    ttsCommit;
}
catch (Exception::DuplicateKeyException, destinationTable)
{
    // Recover: maybe retry with a different key, log, etc.
    numberAdjust++;
    retry;  // erases infolog and restarts the try block
}
```

- `Exception::DuplicateKeyException` is the catch — note the optional
  second argument naming the table for which the dup-key fired.
- `retry` keyword restarts the `try` block from the top (clears the
  infolog). Useful for retry-with-adjustment patterns.
- **Cannot be caught from a set-based `insert_recordset`** — only
  from per-record `.insert()` calls.

---

## Common foot-guns in one place

- **Forgetting `forUpdate`** before `.update()` / `.delete()`. The
  runtime throws.
- **Forgetting `ttsBegin`/`ttsCommit`** around modifications. The
  `ttsLevel` check rejects.
- **Using `=` instead of `==`** in a `where` clause. The compiler
  catches simple cases but you can write subtler bugs.
- **`&&` / `||` precedence is the same.** Parenthesize mixed boolean
  expressions or you get the wrong shape.
- **Boolean condition in `while select`'s `where` is tested once.**
  If you want a loop with a per-iteration condition, structure
  differently.
- **`doInsert` / `doUpdate` / `doDelete` skip everything.** Only
  used in narrow cases (upgrade scripts, controlled data seed).
- **Set-based silent fallback.** Verify the perf you expected by
  tracing or profiling, not by assuming.
- **`skip*` flags don't always help.** If the fallback has multiple
  causes, you need each corresponding skip.
- **Aggregates lose `null` semantics.** A `sum` query with no
  matching rows returns NO row in X++ (vs. a row with NULL in
  ANSI SQL). Test `if (myTable)` after.
- **`insert_recordset` field lists must match.** Source columns
  positional, destination columns named, must align.
- **Don't catch `DuplicateKeyException` from set-based inserts.**
  It won't work. Use per-record + try/catch.
- **Cross-company iterations mutate `DataAreaId`** as they walk
  rows. The buffer's company changes each iteration.
- **Time-versioned tables** require `validTimeState` to see anything
  but current rows.
- **`index hint` requires `allowIndexHint(true)`** first.
- **`forceLiterals` is an SQL-injection risk** for caller-supplied
  values.

---

## See also

- `dynamics-xpp:xpp-language` — language foundations, predefined types,
  control flow, classes.
- `dynamics-xpp:xpp-table` — table authoring, including how the field/index/
  relation structure affects query options.
- `dynamics-xpp:xpp-class` — Chain of Command extension patterns that wrap
  insert/update/delete and which set-based operations fall back
  to honor.
- MS Learn — for the authoritative reference:
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-data-home-page
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-select-statement
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-while-select
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-insert
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-update
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-delete
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-transaction
  - https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-data-perf
