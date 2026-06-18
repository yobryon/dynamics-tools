---
name: xpp-view
description: TRIGGER when authoring an AxView. A view is a read-only table-like AOT element backed by a Query (or inline metadata) that can include bound columns from source tables plus computed columns derived in X++. Required when forms, reports, or data entities need a pre-joined / aggregated read shape.
---

# View — AxView

A view in F&O is a read-only table-like AOT element. It has
the same external shape as a table (extends `common`, has
field groups, methods, indexes, relations) but its data comes
from a Query plus optional computed columns. Views are used
when:

- A form needs a pre-joined or aggregated read of multiple
  underlying tables.
- A data entity (`AxDataEntityView`) needs a backing view that
  shapes the underlying tables into the entity's expected
  columns.
- A report or analytic needs a denormalized read with computed
  columns (e.g., concatenated names, calculated values).

Views are **read-only** at the F&O metadata level — you query
them, you can't insert / update through them. The underlying
tables are the writeable surface.

---

## Authoring through dynamics-xpp

AxView uses **typed domain tools**. Three tools, mirroring CRUD:

| Tool | Purpose |
|---|---|
| `xpp_create_view(request)` | Create a new AxView from a typed CreateViewRequest. |
| `xpp_get_view(name)` | Read a view as its domain shape — view metadata, Query reference, projected/computed fields, indexes, relations, field groups. |
| `xpp_patch_view(name, patch)` | Apply a partial update. Collections (Fields / Indexes / Relations / FieldGroups) replace the whole list when non-null — read with `xpp_get_view`, mutate, patch back. |

A view is a stored `AxQuery` (referenced by name via the `query`
property) plus a set of promoted/computed view columns and
view-specific metadata. Author the join/filter tree once on the
query; the view layers field projections, indexes, field groups,
and relations on top.

View fields are polymorphic on `kind`:

- `Bound` — projects `dataField` from `dataSource` on the
  backing query. The data source name must match a data-source
  name in the referenced query.
- `ComputedString` / `ComputedInt` / `ComputedInt64` /
  `ComputedReal` / `ComputedDate` / `ComputedEnum` /
  `ComputedUtcDateTime` — synthesized at query time by an X++
  method (`method` field). The method must exist on the view
  class. `ComputedString` also takes `stringSize` and
  `adjustment`.

Field groups use the **same on-disk element** (`AxTableFieldGroup`)
as `AxTable` field groups — the typed tool reuses the
`TableFieldGroup` shape directly.

The raw `xpp_create_object` / `xpp_update_object` tools remain
available as an escape hatch.

---

## Read this skill when

- You're authoring an `AxView` (typically as substrate for a
  form, report, or data entity).
- You need computed columns: SQL-evaluable expressions whose
  value comes from a static X++ method.
- You're consolidating a pre-joined read shape that's reused
  across multiple consumers.

---

## XML shape

Views use **no-namespace** at the root, but child elements
like `<AxViewField xmlns="" i:type="AxViewFieldBound">` use
the empty-default-namespace + xsi:type pattern.

### Minimal view backed by a Query

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxView xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentSummaryView</Name>
    <SourceCode>
        <Declaration><![CDATA[
public class CONShipmentSummaryView extends common
{
}
]]></Declaration>
        <Methods />
    </SourceCode>
    <SubscriberAccessLevel>
        <Read>Allow</Read>
    </SubscriberAccessLevel>
    <Query>CONShipmentSummaryQuery</Query>
    <FieldGroups>
        <AxTableFieldGroup>
            <Name>AutoReport</Name>
            <Fields />
        </AxTableFieldGroup>
        <AxTableFieldGroup>
            <Name>AutoLookup</Name>
            <Fields />
        </AxTableFieldGroup>
        <AxTableFieldGroup>
            <Name>AutoIdentification</Name>
            <AutoPopulate>Yes</AutoPopulate>
            <Fields />
        </AxTableFieldGroup>
        <AxTableFieldGroup>
            <Name>AutoSummary</Name>
            <Fields />
        </AxTableFieldGroup>
        <AxTableFieldGroup>
            <Name>AutoBrowse</Name>
            <Fields />
        </AxTableFieldGroup>
    </FieldGroups>
    <Fields />
    <Indexes />
    <Mappings />
    <Relations />
    <StateMachines />
    <ViewMetadata>
        <Name>Metadata</Name>
        <SourceCode>
            <Methods />
        </SourceCode>
        <DataSources />
    </ViewMetadata>
</AxView>
```

The minimal shape — backed by `<Query>CONShipmentSummaryQuery</Query>`
plus the standard five empty Auto* field groups (boilerplate
that ALL views/tables have on disk; preserve when round-tripping).

### Field-rich view with bound + computed columns

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxView xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONAllReservationsView</Name>
    <SourceCode>
        <Declaration><![CDATA[
public class CONAllReservationsView extends common
{
}
]]></Declaration>
        <Methods>
            <Method>
                <Name>getType</Name>
                <Source><![CDATA[
    public static str getType()
    {
        return SysComputedColumn::returnLiteral("Transfer");
    }
]]></Source>
            </Method>
        </Methods>
    </SourceCode>
    <SubscriberAccessLevel>
        <Read>Allow</Read>
    </SubscriberAccessLevel>
    <FieldGroups>
        <!-- standard Auto* boilerplate -->
    </FieldGroups>
    <Fields>
        <AxViewField xmlns="" i:type="AxViewFieldBound">
            <Name>OrderId</Name>
            <DataField>TransferId</DataField>
            <DataSource>InventTransferLine</DataSource>
        </AxViewField>
        <AxViewField xmlns="" i:type="AxViewFieldString">
            <Name>Type</Name>
            <ViewMethod>getType</ViewMethod>
        </AxViewField>
    </Fields>
    <ViewMetadata>
        <Name>Metadata</Name>
        <SourceCode>
            <Methods />
        </SourceCode>
        <DataSources>
            <!-- inline datasource declarations when not using a top-level Query -->
        </DataSources>
    </ViewMetadata>
</AxView>
```

---

## Backing the view — Query vs ViewMetadata.DataSources

A view can declare its data shape two ways:

### 1. Reference an existing AxQuery (preferred)

`<Query>CONShipmentSummaryQuery</Query>` at the top level. The
view inherits the query's tables, joins, ranges. Cleaner
because the query is reusable.

### 2. Inline `<ViewMetadata><DataSources>...`

When the data shape is view-specific and not worth a separate
AxQuery. The ViewMetadata.DataSources structure mirrors a
Simple query's DataSources (root datasource + nested embedded
datasources + ranges + relations).

Generally prefer Query-backed views — easier to reuse the
underlying query for forms, reports, etc. that also need the
same shape.

### Range placement & semantics in inline DataSources (load-bearing)

These bite when building anything past a single-table filtered view, and a
clean compile does NOT catch them — verify generated SQL + row counts:

- **A range on an OUTER-joined datasource lands in that join's `ON`, not the
  `WHERE`.** An `ON` predicate on a LEFT/outer join never filters the left rows,
  so it has zero filtering effect. Only the join-MATCH condition belongs on the
  outer DS. Put the actual FILTER on the **root** datasource — root-DS ranges
  emit to the global `WHERE` and may freely reference descendant datasources.
- **Multiple ranges on the SAME `<Field>` OR-combine; different fields AND.**
  Two ranges both anchored on `RecId` become `(a) OR (b)`, not `(a) AND (b)`.
  For an AND across conditions, use distinct `<Field>`s or collapse into one
  range with an explicit `&&` expression (escaped `&amp;&amp;`).
- **For a simple value LIST on one field, comma-separate the values in a single
  range** (`Value` = `1,2,3`) — don't reach for the extended expression syntax.

### Relation Field/RelatedField direction

Inline `ViewMetadata` datasources use the same `AxQuerySimpleDataSourceRelation`
shape as queries — including the **counterintuitive `<Field>` = JoinDataSource
(parent) / `<RelatedField>` = declaring (child) direction**. See the callout in
`dynamics-xpp:xpp-query` ("Field/RelatedField direction"); it's identical here.

---

## Fields — bound and computed

The `<Fields>` collection has multiple `xsi:type`-discriminated
variants. The two most common:

### AxViewFieldBound — column from a datasource

Maps the view's column to a field on one of the underlying
datasources (table in the query).

```xml
<AxViewField xmlns="" i:type="AxViewFieldBound">
    <Name>OrderId</Name>           <!-- view column name -->
    <DataField>TransferId</DataField>  <!-- source field -->
    <DataSource>InventTransferLine</DataSource>  <!-- which DS in the query -->
</AxViewField>
```

### GROUP BY aggregate columns — yes, views CAN do this

A view CAN project `count` / `min` / `max` / `sum` / `avg` over a grouped
query. The shape is non-obvious, so it's easy to wrongly conclude it's
impossible. Two parts that go in two different places:

1. **Put `<Aggregation>` on the BOUND view field**, and point `<DataField>` at a
   **real** table field (NOT a synthetic name). The aggregate is computed over
   that real field:
   ```xml
   <AxViewField xmlns="" i:type="AxViewFieldBound">
       <Name>EventCount</Name>
       <Aggregation>Count</Aggregation>      <!-- on the bound field itself -->
       <DataField>RecId</DataField>          <!-- a REAL field; count over it -->
       <DataSource>CONSpecialOrderRequestLineLog</DataSource>
   </AxViewField>
   ```
2. **Put the GROUP BY in `ViewMetadata` → the datasource's `<GroupBy>` block**
   (an `AxQuerySimpleGroupByField` naming DataSource + Field), NOT as an
   `IsGroupBy` flag on a datasource field:
   ```xml
   <GroupBy>
     <AxQuerySimpleGroupByField>
       <DataSource>CONSpecialOrderRequestLineLog</DataSource>
       <Field>CaseId</Field>
     </AxQuerySimpleGroupByField>
   </GroupBy>
   ```

The failure mode that makes people give up: putting `Aggregation`/`IsGroupBy` on
an inline `AxQuerySimpleDataSourceField` and then binding a view column's
`<DataField>` to that synthetic field NAME — that compiles to
`InvalidTableForViewField` ("field refers to a nonexistent field"). The bound
field's `DataField` must be a real table field; the aggregation rides on the
bound field, the grouping lives in the metadata's `<GroupBy>`. The typed
`xpp_get_view` / `xpp_create_view` round-trip this faithfully (`aggregation` on
the field, `groupBy` in `viewMetadata.dataSources`). ("Cube" views are NOT an
aggregate-view example — their aggregation lives in a separate measure/cube
layer; don't pattern off them for this.)

### AxViewFieldString / Int / Real / Date / Enum — computed

When the column's value comes from an X++ static method (a
"SysComputedColumn"), use the typed variant matching the
return type. The `<ViewMethod>` element names a static method
on the view's SourceCode.

```xml
<AxViewField xmlns="" i:type="AxViewFieldComputedString">
    <Name>FullName</Name>
    <ExtendedDataType>NameLong</ExtendedDataType>
    <ViewMethod>computeFullName</ViewMethod>
</AxViewField>
```

> **Two things the compiler is strict about (a clean `xpp_get_view`
> round-trip will NOT catch either — only `xpp_compile` does):**
> - **Bind via `viewMethod`, NOT `method`.** The computed-column method binding
>   is `<ViewMethod>`. The mapper also accepts a `method` key (it's a separate,
>   real property) and will faithfully emit `<Method>` — but the kernel then
>   tries to bind it as a SysComputedColumn class method, can't find it, and
>   fails (`ClassDoesNotContainMethod` / column doesn't bind). For a computed
>   column always use `viewMethod`.
> - **`kind` must be the `Computed<Type>` variant** — `kind: "ComputedInt64"`
>   → `i:type="AxViewFieldComputedInt64"`. A bare `kind: "Int64"` (or `"Int"`)
>   is rejected by the deserializer; there is no `AxViewFieldInt64`. The
>   computed subtypes are ComputedString / ComputedInt / ComputedInt64 /
>   ComputedReal / ComputedDate / ComputedEnum / ComputedUtcDateTime.

The method must:
- Be `public static` on the view's class.
- Return a string fragment using `SysComputedColumn::...`
  helpers (e.g., `returnField`, `returnLiteral`, `concat`).
- The fragment gets evaluated by SQL at view-build time, NOT
  at row read time. So you can't put runtime-dependent logic
  here.

Helper signatures from `SysComputedColumn`:

| Helper | What it does |
|---|---|
| `returnField(viewName, dataSource, fieldName)` | Returns a bound field reference (as a SQL expression). |
| `returnLiteral(value)` | Returns a literal value (string / number). |
| `concat(arr)` | Concatenates expressions. |
| `comparisonExpression(left, op, right)` | Builds `<expr> <op> <expr>`. |
| `if_(condition, thenExpr, elseExpr)` | CASE WHEN. |
| `cast(expr, type)` | Type cast. |

---

## Property checklist

### View level

| Property | Notes |
|---|---|
| **`Name`** | Convention `<prefix><Function>View` or just `<prefix><Function>` (the View suffix is common but optional). |
| `Label` | The user-visible label when the view appears in lookups / reports. |
| `Query` | The backing AxQuery (preferred when reusable). Omit if using ViewMetadata.DataSources. |
| **`SourceCode`** | The classDeclaration (extends `common`) + any view methods for computed columns. |
| **`SubscriberAccessLevel`** | Almost always `<Read>Allow</Read>`. Required. |
| `IsObsolete` | Mark deprecated views. |
| `ConfigurationKey` | Feature gating. |
| `CountryRegionCodes` | Region gating. |
| **`FieldGroups`** | The 5 Auto* groups (boilerplate). |
| `Fields` | Bound + computed columns. |
| `Indexes` | Like a table — useful when the view is consumed by performance-sensitive code. |
| `Mappings` | Cross-mapping to other tables (rare). |
| `Relations` | Table relations (for views used in lookups). |
| **`ViewMetadata`** | Required wrapper for the inline datasources block (when not using Query). Even an empty `<DataSources/>` block is present. |

### ViewMethod (computed column method)

| Constraint | Notes |
|---|---|
| Accessibility | `public static` |
| Return type | Matches the AxViewField type variant (String / Int / Real / Date / Enum). |
| Body | Builds an SQL expression via `SysComputedColumn` helpers. NO runtime data access — this runs at view-build / DB-sync time. |
| Naming | `<lowerCamelCase>` matching the `<ViewMethod>` ref in the field. |

### AxViewField common attributes

| Property | Notes |
|---|---|
| `Name` | View-side column name. |
| `ExtendedDataType` | Optional EDT to derive label, helpText, validation. |
| `Label` | Override label if not using EDT. |
| `Visible` | `Yes` (default). |
| `Mandatory` | Usually `No` for views. |

---

## Common workflows

### View backing a form

```
1. Author the AxQuery covering the tables + joins the form needs.
2. Author the AxView referencing that query, with explicit
   <Fields> for any computed columns.
3. On the form, set the DataSource.Table to the view name
   (not a real table).
```

### View backing a data entity

```
1. Author the view as above.
2. Author the AxDataEntityView with this view as its primary
   data source.
```

Data entities can be backed directly by tables OR by views;
views give you computed columns + pre-aggregation. (Data
entity authoring is its own thing — see future
`dynamics-xpp:xpp-dataentityview` if scoped.)

### Adding a computed column

```
1. Decide return type (String / Int / etc.).
2. Add a public static method on the view's SourceCode
   returning the SQL expression via SysComputedColumn helpers.
3. Add an AxViewField with the matching xsi:type and
   <ViewMethod>methodName</ViewMethod>.
4. Compile + dbsync (the view gets recreated with the new
   column).
```

---

## Common gotchas

### Forgetting the 5 Auto* field groups

The boilerplate FieldGroups (AutoReport, AutoLookup, AutoIdentification,
AutoSummary, AutoBrowse) are on every view by convention. The
view technically works without them but lookups, reports, and
default UIs that look for these groups won't find content.
Always include the 5 empty groups when creating a new view.

### ViewMethod return type mismatch

If your method returns a string but the AxViewField is
`AxViewFieldInt`, the dbsync fails with a type mismatch. Match
the variant to the return type.

### Trying to UPDATE through a view

Views are read-only. `update_recordset` / `delete_from` on
the view's `common`-derived class will compile but fail at
runtime. To modify the underlying data, write to the source
tables directly.

### Computed column running at row-read time

The X++ method runs at view-build time (when the view's SQL
is generated). It runs ONCE per dbsync, producing a static
SQL fragment. You can't reference runtime values like
`userId()` or per-row data in the method body — only
constants and references to source-table fields.

### Subscription access level forgotten

Without `<SubscriberAccessLevel><Read>Allow</Read></SubscriberAccessLevel>`,
the view is invisible to external consumers (OData, integrations).
Always include it.

### DB sync after view changes

Adding/removing fields requires a DB sync. The view's SQL is
recreated from scratch — set `<DBSyncInBuild>True` in the
rnrproj before running `xpp_compile`, or run dbsync via VS
manually after. Schema-only X++ changes (a new method on the
view's class) don't need dbsync.

### View vs DataEntityView

These are different AOT types serving different consumers:
- `AxView` — a SQL view; consumed by forms / X++ / reports /
  data entities-as-backing.
- `AxDataEntityView` — an OData / DMF data entity; consumed by
  external integration. Can be backed by an AxView.

They're often confused. If the consumer is "show this on a
form" or "compute these columns in SQL" → AxView. If it's
"expose this data to OData / DMF" → AxDataEntityView (backed
by an AxView or table).

---

## Worked example: shipment summary view

A view that joins shipment headers with line counts and a
computed "FormattedTotal" column.

### Step 1: Backing query

`CONShipmentSummaryQuery` — AxQuerySimple joining
CONSHShipmentTable + CONSHShipmentLine (see
`dynamics-xpp:xpp-query`).

### Step 2: The view

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxView xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentSummaryView</Name>
    <SourceCode>
        <Declaration><![CDATA[
public class CONShipmentSummaryView extends common
{
}
]]></Declaration>
        <Methods>
            <Method>
                <Name>formattedTotal</Name>
                <Source><![CDATA[
    /// <summary>
    /// Computed column - formats the line total with currency.
    /// </summary>
    public static str formattedTotal()
    {
        return SysComputedColumn::concat(
            SysComputedColumn::cast(
                SysComputedColumn::returnField(
                    viewStr(CONShipmentSummaryView),
                    identifierStr(CONSHShipmentTable),
                    fieldStr(CONSHShipmentTable, TotalAmount)),
                "varchar(30)"),
            SysComputedColumn::returnLiteral(" "),
            SysComputedColumn::returnField(
                viewStr(CONShipmentSummaryView),
                identifierStr(CONSHShipmentTable),
                fieldStr(CONSHShipmentTable, CurrencyCode)));
    }
]]></Source>
            </Method>
        </Methods>
    </SourceCode>
    <SubscriberAccessLevel>
        <Read>Allow</Read>
    </SubscriberAccessLevel>
    <Query>CONShipmentSummaryQuery</Query>
    <FieldGroups>
        <AxTableFieldGroup><Name>AutoReport</Name><Fields /></AxTableFieldGroup>
        <AxTableFieldGroup><Name>AutoLookup</Name><Fields /></AxTableFieldGroup>
        <AxTableFieldGroup><Name>AutoIdentification</Name><AutoPopulate>Yes</AutoPopulate><Fields /></AxTableFieldGroup>
        <AxTableFieldGroup><Name>AutoSummary</Name><Fields /></AxTableFieldGroup>
        <AxTableFieldGroup><Name>AutoBrowse</Name><Fields /></AxTableFieldGroup>
    </FieldGroups>
    <Fields>
        <AxViewField xmlns="" i:type="AxViewFieldBound">
            <Name>ShipmentId</Name>
            <DataField>ShipmentId</DataField>
            <DataSource>CONSHShipmentTable</DataSource>
        </AxViewField>
        <AxViewField xmlns="" i:type="AxViewFieldBound">
            <Name>Status</Name>
            <DataField>Status</DataField>
            <DataSource>CONSHShipmentTable</DataSource>
        </AxViewField>
        <AxViewField xmlns="" i:type="AxViewFieldString">
            <Name>FormattedTotal</Name>
            <ViewMethod>formattedTotal</ViewMethod>
        </AxViewField>
    </Fields>
    <Indexes />
    <Mappings />
    <Relations />
    <StateMachines />
    <ViewMetadata>
        <Name>Metadata</Name>
        <SourceCode>
            <Methods />
        </SourceCode>
        <DataSources />
    </ViewMetadata>
</AxView>
```

---

## See also

- `dynamics-xpp:xpp-query` — the backing query that defines the
  view's data shape.
- `dynamics-xpp:xpp-table` — views share the table envelope shape
  (FieldGroups, Methods, Indexes, Relations). The same checklists apply.
- `dynamics-xpp:xpp-edt` — bound view fields can reference EDTs
  for labels and validation.
- `dynamics-xpp:xpp-project` — `DBSyncInBuild=True` for schema
  changes that recreate the view's SQL.
- [MS: Views overview](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/aot-views) — authoritative reference.
- [MS: Computed columns in views](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-tools/computed-columns-in-views) — SysComputedColumn helpers.

---

## Note on the typed authoring surface

AxView is the **sixth AOT type** on the typed-authoring layer
(after AxEnum, AxEdt, AxTable, AxClass, AxQuery). The agent-facing
API lives in `Xpp.Service.Domain.Views.CreateViewRequest`.

AxView shares its base class (`AxDataEntity`) with `AxTable`, so
the canonical element order follows the same two-tier rule:
`Name → SourceCode (Order=1) → AxDataEntity AxProp scalars
(alphabetical) → AxView AxProp scalars (alphabetical) → AxView
Order=3 collections (alphabetical)`.

Field groups deliberately reuse `Tables.TableFieldGroup` — the
on-disk element name is `AxTableFieldGroup` regardless of whether
the parent is a table or a view. View fields, indexes, and
relations have their own `AxView*` element families and have
their own typed records.

If you need a property the domain shape doesn't cover, escape-
hatch via `xpp_get_object_xml` + `xpp_update_object`. See
`plugins/xpp/docs/domain-coverage.md` for the inclusion ledger.
