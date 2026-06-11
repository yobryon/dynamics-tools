---
name: xpp-query
description: TRIGGER when authoring an AxQuery (AxQuerySimple or AxQueryComposite). Queries underpin form data sources, report data, lookups, data entities, XDS policies, view definitions, and tile counts. Required substrate for almost every UI feature that reads data.
---

# Query — AxQuerySimple and AxQueryComposite

An AOT query is a reusable, declarative SELECT specification.
Multiple things consume queries:

- **Form data sources** — a form's DataSource node can point at
  a query instead of a raw table.
- **Tile counts** — `dynamics-xpp:xpp-tile` Count tiles run the
  query and display the row count.
- **Reports** — SSRS report data sources.
- **Data entities** — `AxDataEntityView` builds atop a query.
- **Views** — `AxView` is essentially a query exposed as a
  read-only "table."
- **XDS policies** — the policy query that filters constrained
  tables.
- **X++ code** — `new QueryRun(queryStr(MyQuery))` to read the
  query results programmatically.

Queries come in two flavors:

| Subtype | Use when |
|---|---|
| **AxQuerySimple** | The query can be fully declared in metadata: tables, joins, ranges, fields, group/sort. The common case. |
| **AxQueryComposite** | The query needs runtime construction in X++ (e.g., conditional joins based on parameters). The XML carries a `classDeclaration` + `init()` method that builds the QueryBuildDataSource graph programmatically. |

Prefer Simple when possible. Composite costs a class-method
maintenance overhead and is harder to reason about.

---

## Authoring through dynamics-xpp

AxQuery uses **typed domain tools** scoped to `AxQuerySimple` —
the modern join/filter shape that covers ~95% of queries. Three
tools, mirroring CRUD:

| Tool | Purpose |
|---|---|
| `xpp_create_query(request)` | Create a new AxQuery from a typed CreateQueryRequest. |
| `xpp_get_query(name)` | Read a query as its domain shape (recursive DataSources tree, ranges, relations, orderBy/groupBy/having). |
| `xpp_patch_query(name, patch)` | Apply a partial update. Collections (DataSources, Ranges, etc.) replace the whole list when non-null — read with `xpp_get_query`, mutate the tree in-process, and patch back. |

Data sources are recursive (joins inside joins) and polymorphic
on `kind`:

- `Root` — top-level table data source. Carries `orderBy` /
  `groupBy` / `having`.
- `Embedded` — nested join. Carries `joinMode` (`InnerJoin` /
  `OuterJoin` / `ExistsJoin` / `NoExistsJoin` /
  `ExtendedRelations`), `useRelations`, `relations[]`.
- `Derived` — derived-table reference.

The mapper handles the on-disk `<AxQuery i:type="AxQuerySimple">`
discriminator and the per-data-source
`<AxQuerySimpleRootDataSource>` / `<AxQuerySimpleEmbeddedDataSource>`
element names. The agent never authors `xsi:type` directly.

**Scope note:** `AxQueryComposite` (union/aggregate over other
queries) is NOT exposed via the typed tools — fall back to
`xpp_get_object_xml` + `xpp_update_object` for those rare cases.

The raw write tools remain available as an escape hatch.

---

## Polymorphic root — important

`AxQuery` is one of the AOT families whose on-disk XML uses the
DataContract polymorphic-root pattern (the other is `AxEdt`). The
root tag is always `<AxQuery>` with an `i:type` discriminator:

```xml
<AxQuery xmlns="" i:type="AxQuerySimple" xmlns:i="...">
```

The typed tools hide this — the agent works with the domain
shape. The bridge's `xpp_get_object_xml` returns the polymorphic
shape directly when you need to inspect the raw on-disk form.

---

## Read this skill when

- You're authoring or extending an `AxQuery` to back any of the
  consumers listed above.
- A form needs a parameterized lookup or filtered data source.
- You're writing a Count tile and need the underlying query.
- You're writing an XDS policy and need the policy query.
- The compile flags `BPCheckMissingIndexOnCode` or similar
  query-related BP rules — the query needs an indexed range
  field.

---

## XML shape — AxQuerySimple

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxQuery xmlns="" i:type="AxQuerySimple"
         xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentPendingQuery</Name>
    <SourceCode>
        <Methods>
            <Method>
                <Name>classDeclaration</Name>
                <Source><![CDATA[
[Query]
public class CONShipmentPendingQuery extends QueryRun
{
}
]]></Source>
            </Method>
        </Methods>
    </SourceCode>
    <DataSources>
        <AxQuerySimpleRootDataSource>
            <Name>CONSHShipmentTable</Name>
            <Table>CONSHShipmentTable</Table>
            <DataSources />
            <DerivedDataSources />
            <Fields />
            <Ranges>
                <AxQuerySimpleDataSourceRange>
                    <Name>Status</Name>
                    <Field>Status</Field>
                    <Value>Pending</Value>
                </AxQuerySimpleDataSourceRange>
            </Ranges>
            <GroupBy />
            <Having />
            <OrderBy />
        </AxQuerySimpleRootDataSource>
    </DataSources>
</AxQuery>
```

Three top-level pieces:

1. **`<SourceCode>`** — always present. Carries the
   `classDeclaration` (and any other X++ methods for the
   QueryRun class). For a Simple query, the body is just
   `public class <Name> extends QueryRun {}` — boilerplate.
2. **`<DataSources>`** — the actual query content: tables,
   joins, ranges, fields.
3. **The polymorphic root attributes** — `xmlns=""` and
   `i:type="AxQuerySimple"`.

---

## DataSource structure

Every datasource is either:
- **`AxQuerySimpleRootDataSource`** — the top-level table in
  `<DataSources>`.
- **`AxQuerySimpleEmbeddedDataSource`** — nested under a root
  (or another embedded) for joins.

Each datasource node has:

| Child | Purpose |
|---|---|
| `Name` | The alias for this datasource within the query (usually matches Table). |
| `Table` | The AxTable being queried. |
| `JoinMode` | (Embedded only) `InnerJoin`, `OuterJoin`, `ExistsJoin`, `NoExistsJoin`, `NoYesJoin`. Default `InnerJoin`. |
| `Relations` | How this datasource joins to its parent. Either `<AxQuerySimpleDataSourceRelation>` rows explicitly listing field links, OR omit + let the relation be EDT-derived. |
| `DataSources` | Further nested embedded sources — joins. |
| `Fields` | Which columns to select. Empty = all (when DynamicFields=Yes), or explicit `<AxQuerySimpleDataSourceField>` rows. |
| `Ranges` | The WHERE-clause conditions. |
| `GroupBy` / `Having` / `OrderBy` | Aggregate-related. Often empty. |
| `DynamicFields` | `Yes` shortcut for "all fields." Use this for forms (the form needs all columns); omit for queries used by code that picks specific fields. |

---

## Ranges — the WHERE clause

The `<Ranges>` collection holds the filter conditions. Each range:

```xml
<AxQuerySimpleDataSourceRange>
    <Name>RangeName</Name>      <!-- internal name -->
    <Field>StatusField</Field>  <!-- table field -->
    <Value>Pending</Value>      <!-- the filter value -->
</AxQuerySimpleDataSourceRange>
```

The `<Value>` element supports F&O's full query-value grammar:

| Pattern | Example | Meaning |
|---|---|---|
| Literal | `Pending` | Exact match |
| Comma list | `Pending,InProgress` | OR (any of these values) |
| Range | `10..20` | Between (inclusive) |
| Comparison | `>10` | Greater than |
| Wildcard | `Cust*` | Like 'Cust%' |
| Method call | `(CustGroup::default())` | Evaluated at runtime; result becomes the literal |
| SysQuery expression | `((CONAssignedTo == HcmWorkerLookup::currentWorker()) && (CONAssignedTo != 0))` | Full expression — parenthesize, escape `&&` as `&amp;&amp;` |

The expression form is powerful but easy to break. When using
it, run `xpp_bp_check` after writing — BP catches obvious
syntax errors.

### Status flag
The `Status` element on a range can be `Open`, `Locked`, or
`Hidden`:
- `Open` (default) — user can change at runtime
- `Locked` — fixed; users see but can't override
- `Hidden` — fixed and invisible. Use for security-sensitive
  filters.

---

## Joins — explicit vs EDT-derived relations

When you add an embedded datasource, you specify how it joins.
Two styles:

### Explicit relation

```xml
<AxQuerySimpleEmbeddedDataSource>
    <Name>InventTable</Name>
    <Table>InventTable</Table>
    <JoinMode>InnerJoin</JoinMode>
    <Relations>
        <AxQuerySimpleDataSourceRelation>
            <Name>InventTable_ItemId</Name>
            <Field>ItemId</Field>
            <JoinDataSource>CONSHShipmentLine</JoinDataSource>
            <RelatedField>ItemId</RelatedField>
        </AxQuerySimpleDataSourceRelation>
    </Relations>
    ...
</AxQuerySimpleEmbeddedDataSource>
```

Lists each (Field, JoinDataSource, RelatedField) link
explicitly. Use when the join isn't covered by an EDT relation
on the table, or when you need a non-standard join.

> **Field/RelatedField direction — read this; it is counterintuitive.**
> In an `AxQuerySimpleDataSourceRelation`, `<Field>` names a field on the
> **`<JoinDataSource>`** (the *other* datasource you're joining to — usually the
> parent), and `<RelatedField>` names a field on the datasource that **declares
> this relation** (the embedded/child datasource it sits under). It is NOT
> "Field = my own field, RelatedField = the other side."
>
> This is invisible in almost every codebase example because they join on
> symmetrically-named fields (`ItemId`↔`ItemId`, `RecId`↔`...RecId`), where the
> direction can't be observed. With asymmetric names it bites — e.g. an embedded
> `CONSpecialOrderEventLogConfiguration` (child, field `Event`) joined to parent
> `CONSpecialOrderRequestLineLog` (field `EventLog`):
> ```xml
> <Field>EventLog</Field>              <!-- the JoinDataSource (parent) field -->
> <JoinDataSource>CONSpecialOrderRequestLineLog</JoinDataSource>
> <RelatedField>Event</RelatedField>   <!-- the field on THIS (declaring/child) DS -->
> ```
> Reversing them yields a two-sided `FieldDoesNotExist` at compile (each name
> resolved against the wrong table). Same rule for inline `ViewMetadata`
> datasources — it's a query-metadata fact, not a view-vs-query difference.

### EDT-derived (no `<Relations>` block)

When the field on this table has an EDT that already declares
a relation to the parent, omit `<Relations>` entirely. F&O
resolves the join from the EDT metadata.

Cleaner when the relation exists on the model side; harder to
debug when it doesn't behave as expected.

---

## Composite queries — when Simple isn't enough

If the query's structure depends on runtime conditions
(parameter-driven joins, dynamic field projections), you need
a Composite query. The XML shape is the same except for the
`i:type` and the SourceCode block:

```xml
<AxQuery xmlns="" i:type="AxQueryComposite"
         xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONDynamicQuery</Name>
    <SourceCode>
        <Methods>
            <Method>
                <Name>classDeclaration</Name>
                <Source><![CDATA[
[Query]
public class CONDynamicQuery extends QueryRun
{
}
]]></Source>
            </Method>
            <Method>
                <Name>init</Name>
                <Source><![CDATA[
    public void init()
    {
        super();
        Query q = this.query();
        QueryBuildDataSource qbds = q.dataSourceTable(tableNum(InventDistinctProduct));
        QueryBuildDataSource qbdsInventSum = qbds.addDataSource(tableNum(InventSum));
        qbdsInventSum.joinMode(JoinMode::ExistsJoin);
        qbdsInventSum.addLink(fieldNum(InventDistinctProduct, ItemId), fieldNum(InventSum, ItemId));
        qbdsInventSum.addRange(fieldNum(InventSum, AvailPhysical)).value(">0");
    }
]]></Source>
            </Method>
        </Methods>
    </SourceCode>
    <DataSources />
</AxQuery>
```

The `init()` method builds the QueryBuildDataSource graph at
runtime. `<DataSources />` stays empty — the metadata is
declared in code.

The trade-off: harder for downstream consumers (forms, tiles,
XDS policies) to introspect. Some consumers don't work with
composites at all. Prefer Simple unless dynamism is required.

---

## Property checklist

### Query level

| Property | Notes |
|---|---|
| **`Name`** | Convention `<prefix><Function>Query` (sometimes omitted suffix). |
| **`SourceCode`** | Always present. classDeclaration boilerplate. |
| **`DataSources`** | The query tree. |
| `Title` | Label-ref. Shown when the query is used in a parameter dialog. |
| `ConfigurationKey` | Gate availability on a feature key. |
| `Interactive` | `Yes` enables user-modifiable ranges via the SysQueryForm dialog. |
| `AllowCheckRel` | Permit DELETE actions to enforce check relations. |
| `UserUpdate` | Allow the query results to be modified directly (rare). |

### Root datasource

| Property | Notes |
|---|---|
| **`Name`** | Alias; usually matches `Table`. |
| **`Table`** | The AxTable. |
| `DynamicFields` | `Yes` = select all fields. Use for form-backing queries. |
| `Fetch` / `FetchModeFirst` | Performance hints. |
| `Update` | `Yes` to allow `select forUpdate`. |
| `Company` | Cross-company query when set. |

### Embedded datasource (joins)

| Property | Notes |
|---|---|
| **`Name`**, **`Table`** | As above. |
| **`JoinMode`** | `InnerJoin` (default), `OuterJoin`, `ExistsJoin`, `NoExistsJoin`. |
| `Relations` | Explicit join specification. Omit for EDT-derived. |
| `FetchMode` | `OneToOne` / `OneToMany` — affects query plan. |
| `JoinRelation` | When the relation comes from a specific table-level relation, name it here. |

### DataSource Field

| Property | Notes |
|---|---|
| **`Name`** | The field's name in the query (usually matches Field). |
| **`Field`** | The actual table field. |
| `Aggregation` | `Sum`, `Count`, `Min`, `Max`, `Avg` — for aggregate queries. |
| `IsGroupBy` | `Yes` makes this field a GROUP BY column. |

### Range

| Property | Notes |
|---|---|
| **`Name`** | Internal range name. |
| **`Field`** | The field to filter on. |
| `Value` | The filter expression. See ranges section. |
| `Status` | `Open` / `Locked` / `Hidden`. |
| `Enabled` | `Yes` (default) / `No`. |

---

## Common workflows

### Backing a Count tile

```
1. Identify the table + filter (status, date, owner, ...).
2. Write a Simple query selecting that table with the filter
   as a Range.
3. (Optional) Add `<Status>Locked</Status>` on the range if
   the filter must not be user-overridable.
4. Reference the query from:
   - The Display menu item's `Query` property (for click-through filter)
   - The AxTile's `Query` property (for the count)
```

### Backing an XDS policy

```
1. Identify the primary table (e.g., CustTable).
2. Write a Simple query against that table with a range
   that filters per current user (e.g., SalesRep == userId()).
3. Reference from AxSecurityPolicy.Query.
```

### Backing a form data source

```
1. Decide which tables the form needs (root + joins).
2. Write a Simple query covering those, DynamicFields=Yes
   on each.
3. On the form, set the DataSource's Query property.
```

---

## Common gotchas

### Polymorphic root rewrite

If you hand-construct a query XML, use root `<AxQuery i:type="...">`,
not `<AxQuerySimple>`. The bridge's WRITE path expects the
DataContract-style discriminator (we fixed this in the
xpp_update_object polymorphic-root handler — but hand-typing
the wrong root will fail with "Expecting element 'AxQuery'").
Always start from `xpp_get_object_xml` on an existing query
as a template.

### Forgetting the SourceCode block

Every query has a `classDeclaration` that declares the QueryRun
subclass. Omitting it is rejected at compile (the form / tile
consumer can't instantiate the query). For a Simple query the
body is empty boilerplate but the wrapper MUST be present.

### Range expressions and XML escaping

When using SysQuery expression syntax (the `(...)` form), `&&`
must be escaped as `&amp;&amp;` in XML. The bridge round-trip
preserves CDATA-quoted source but range Values are not CDATA
— they're attribute-style text content.

### Cross-company queries

Queries don't show cross-company data by default. Add `<AllowCrossCompany>Yes</AllowCrossCompany>`
on the datasource if you need it. Forms usually need this for
multi-company reporting.

### Joins to MS-shipped tables

If the join is via an MS-shipped relation, omit `<Relations>` —
EDT-derived. Adding explicit Relations duplicates the relation
spec and risks drift when MS changes the table.

### DynamicFields with explicit Fields

If `DynamicFields=Yes` AND `<Fields>` contains explicit fields,
behavior is undefined — the dynamic-fields selector usually
wins. Pick one. Use Fields-only when you want a strict
projection; DynamicFields-only for "give me everything."

### "Query takes too long" performance issues

Count tiles need <25ms queries for `AsFastAsPermissible`
refresh. Tools to check:
- Run the query manually via SQL profiler.
- Check that the range fields are indexed on the table.
- Avoid deep joins for tile-backing queries — flatten via
  views if needed.

`BPCheckSelectwithJoin` and `BPCheckPassiveJoinUse` are BP
rules that flag join-performance concerns.

### Composite queries and form data sources

Some form datasource consumers don't support Composite queries.
If you put a Composite query on a form's DataSource and the
form fails at runtime with "cannot initialize query," switch
to Simple or move the dynamic logic into the form's `init()`.

---

## Worked example: pending shipments for the current site

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxQuery xmlns="" i:type="AxQuerySimple"
         xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentPendingQuery</Name>
    <Title>@MyLabels:PendingShipments</Title>
    <SourceCode>
        <Methods>
            <Method>
                <Name>classDeclaration</Name>
                <Source><![CDATA[
[Query]
public class CONShipmentPendingQuery extends QueryRun
{
}
]]></Source>
            </Method>
        </Methods>
    </SourceCode>
    <DataSources>
        <AxQuerySimpleRootDataSource>
            <Name>CONSHShipmentTable</Name>
            <DynamicFields>Yes</DynamicFields>
            <Table>CONSHShipmentTable</Table>
            <DataSources>
                <AxQuerySimpleEmbeddedDataSource>
                    <Name>InventLocation</Name>
                    <JoinMode>InnerJoin</JoinMode>
                    <Table>InventLocation</Table>
                    <DataSources />
                    <DerivedDataSources />
                    <Fields />
                    <Ranges />
                </AxQuerySimpleEmbeddedDataSource>
            </DataSources>
            <DerivedDataSources />
            <Fields />
            <Ranges>
                <AxQuerySimpleDataSourceRange>
                    <Name>StatusRange</Name>
                    <Field>Status</Field>
                    <Status>Locked</Status>
                    <Value>Pending</Value>
                </AxQuerySimpleDataSourceRange>
            </Ranges>
        </AxQuerySimpleRootDataSource>
    </DataSources>
</AxQuery>
```

Notes:
- Root datasource: CONSHShipmentTable with all fields.
- Join to InventLocation via EDT-derived relation (no explicit
  Relations block — the table-level relation handles it).
- Range filters Status=Pending, Locked so users can't override.

---

## See also

- `dynamics-xpp:xpp-tile` — tiles that consume queries for count
  display.
- `dynamics-xpp:xpp-menuitem` — menu items reference queries to
  pre-filter the form they open.
- `dynamics-xpp:xpp-view` — views are essentially queries exposed
  as read-only tables.
- `dynamics-xpp:xpp-security` — XDS policies use queries to filter
  constrained tables.
- `dynamics-xpp:xpp-form` — form data sources can point at a query.
- `dynamics-xpp:xpp-data` — for the X++ side (`select`, `while
  select`, QueryRun, QueryBuildDataSource) that complements
  metadata queries.
- [MS: Query system documentation](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-tools/query-class-objects)

---

## Note on the typed authoring surface

AxQuery is the **fifth AOT type** on the typed-authoring layer
(after AxEnum, AxEdt, AxTable, AxClass). The agent-facing API
lives in `Xpp.Service.Domain.Queries.CreateQueryRequest`. Two
nested polymorphisms collapse into discriminator enums:

- **Root i:type** (`AxQuerySimple` vs `AxQueryComposite`) — the
  domain layer scopes to `AxQuerySimple` only. Composite stays
  on the escape hatch.
- **Per-data-source xsi:type** (`Root` / `Embedded` / `Derived`)
  — surfaces as the `kind` enum on each `QueryDataSource`.

Data sources are recursive: a Root contains a tree of Embedded
joins, which can themselves contain more Embedded children at
unbounded depth. The mapper walks this recursively on both
build and parse.

If you need a property the domain shape doesn't cover, escape-
hatch via `xpp_get_object_xml` + `xpp_update_object`. See
`plugins/xpp/docs/domain-coverage.md` for the full inclusion
ledger.
