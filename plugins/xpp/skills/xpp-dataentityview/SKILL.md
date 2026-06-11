---
name: xpp-dataentityview
description: Use when authoring or modifying a D365 F&O data entity (AxDataEntityView). Data entities are the OData/DMF writable layer over tables — they project columns from a backing query (or inlined data sources), expose natural keys for OData URL identity, and support write-through to underlying tables. Covers mapped vs computed columns, natural keys, the relationship between Query/ViewMetadata.DataSources, and DMF staging.
---

# Authoring data entities (`AxDataEntityView`)

A data entity is the writable counterpart to an `AxView`. It
projects columns from one or more tables, exposes a natural-key
identity for OData, and (unless `IsReadOnly`) supports write-back
into the underlying tables via the DataEntity runtime contract.

Data entities are how F&O surfaces data to:

- **OData** (the public REST API at `/data/<PublicCollectionName>`)
- **DMF** (the Data Management Framework — import/export, staging
  tables, data projects)
- **Dataverse virtual entities** (when `AutoCreateDataverse` is set)
- **Dual-write integrations**

Load `dynamics-xpp:xpp-language` if you haven't, and skim
`dynamics-xpp:xpp-query` and `dynamics-xpp:xpp-view` — entities reuse a lot
of their shape.

---

## Authoring through dynamics-xpp

AxDataEntityView uses **typed domain tools**. Three tools:

| Tool | Purpose |
|---|---|
| `xpp_create_entity(request)` | Create a new data entity from a typed CreateEntityRequest. |
| `xpp_get_entity(name)` | Read a data entity — entity metadata, Mapped/Unmapped fields, keys, ranges, relations, field groups, ViewMetadata.DataSources. |
| `xpp_patch_entity(name, patch)` | Apply a partial update. Collections (Fields / Keys / Ranges / Relations / FieldGroups) replace the whole list when non-null. |

Two key differences from `AxView`:

1. **Fields are polymorphic on `kind`**:
   - `Mapped` — writes back to a `DataField` on a `DataSource` in
     the backing data-source tree. Carries optional `Aggregation`,
     `DimensionLegalEntityContextField`,
     `DynamicDimensionEnumerationField`, and Dataverse-search opt-in.
   - `UnmappedString` / `UnmappedInt` / `UnmappedInt64` /
     `UnmappedReal` / `UnmappedDate` / `UnmappedEnum` /
     `UnmappedUtcDateTime` / `UnmappedTime` / `UnmappedGuid` /
     `UnmappedContainer` — synthesized by an X++ method
     (`computedFieldMethod`). `UnmappedString` additionally takes
     `stringSize` and `adjustment`.
2. **`ViewMetadata.DataSources` is populated**. Unlike `AxView`
   (which references a separate `AxQuery` and leaves its
   `ViewMetadata.DataSources` empty), data entities typically
   inline the data sources directly inside `ViewMetadata.DataSources`.
   That sub-tree uses the same recursive `QueryDataSource` shape
   from the AxQuery domain — a `Root` with nested `Embedded`
   joins. The agent never authors the `AxQuerySimpleRootDataSource`
   xsi:type directly.

The raw `xpp_create_object` / `xpp_update_object` tools remain
available as an escape hatch.

---

## Minimum viable data entity

```jsonc
xpp_create_entity({
  "name": "MyCustomerEntity",
  "label": "@MyLabels:CustomerEntity",
  "developerDocumentation": "@MyLabels:CustomerEntityDev",
  "publicEntityName": "MyCustomer",
  "publicCollectionName": "MyCustomers",
  "primaryKey": "EntityKey",
  "isPublic": true,
  "dataManagementEnabled": true,
  "dataManagementStagingTable": "MyCustomerEntityStaging",
  "supportsSetBasedSqlOperations": true,
  "entityCategory": "Master",
  "titleField1": "AccountNum",

  "fields": [
    { "name": "AccountNum", "kind": "Mapped",
      "dataField": "AccountNum", "dataSource": "CustTable" },
    { "name": "Name",       "kind": "Mapped",
      "dataField": "Name", "dataSource": "CustTable" },
    { "name": "DisplayName", "kind": "UnmappedString",
      "computedFieldMethod": "displayNameSQL", "stringSize": 250,
      "isComputedField": true }
  ],

  "keys": [
    { "name": "EntityKey", "fields": [{ "dataField": "AccountNum" }] }
  ],

  "fieldGroups": [
    { "name": "AutoReport",         "fields": ["AccountNum", "Name"] },
    { "name": "AutoLookup",         "fields": ["AccountNum"] },
    { "name": "AutoIdentification", "autoPopulate": true },
    { "name": "AutoSummary" },
    { "name": "AutoBrowse" }
  ],

  "viewMetadata": {
    "dataSources": [
      { "name": "CustTable", "kind": "Root", "table": "CustTable",
        "dynamicFields": true }
    ]
  }
})
```

Key points:

- **`PrimaryKey` references a `Keys[].name`** — the OData URL
  `/data/MyCustomers(AccountNum='1001')` resolves through the
  named key.
- **`PublicEntityName` (singular) vs `PublicCollectionName`
  (plural)** — both appear in OData URLs; pick consistent names.
- **`SourceCode` is optional**. If omitted, the mapper emits a
  default `public class <Name> extends common {}`. Most entities
  override `insertEntityDataSource` / `updateEntityDataSource` /
  `deleteEntityDataSource` to add validation or feature-flag
  gating — provide those via `sourceCode.methods`.
- **ViewMetadata.DataSources** is where joins / ranges /
  derived data sources live. The mapper handles the recursive
  Root → Embedded structure (same shape AxQuery uses).

---

## Mapped vs Unmapped — when to use which

**`Mapped`** is the default. Use for every entity column that
projects a backing-table column:

- Read: the value is selected from `dataSource.dataField`.
- Write: the value flows back to `dataSource.dataField` on insert/update.
- Round-trip: cleanly bidirectional.

**`Unmapped<Type>`** is for computed / derived values:

- The named `computedFieldMethod` returns the SQL expression at
  query-build time (or, when `isComputedField: false`, you compute
  it in X++ at runtime).
- Convention: the method is `public static server str
  <methodName>()` for `UnmappedString`, returning a SQL fragment
  like `T1.AccountNum + ' (' + T1.Name + ')'`.
- Read-only: no write-back path. Don't put an Unmapped field in
  a Key.

**Picking the type**: match the underlying SQL expression's
return type. `UnmappedString` is the most common; the other
primitives mirror the corresponding `EDT` BaseTypes.

---

## Natural keys (`Keys[]`)

Every public entity needs at least one key — the URL identity:

```jsonc
"keys": [
  { "name": "EntityKey",
    "fields": [{ "dataField": "AccountNum" }] }
]
```

Composite keys are common for child entities (e.g. order line
keyed by `[SalesOrderNum, LineNum]`):

```jsonc
"keys": [
  { "name": "EntityKey",
    "fields": [
      { "dataField": "SalesOrderNum" },
      { "dataField": "LineNum" }
    ] }
]
```

`PrimaryKey` on the entity must name one of the keys.

`Mapped` columns can be in keys; `Unmapped` columns cannot.

---

## DMF integration

Setting `dataManagementEnabled: true` makes the entity available
in data projects (Import/Export workspaces). When enabled:

- **`dataManagementStagingTable`** is required. Conventionally
  `<EntityName>Staging`. The staging table holds rows mid-import
  before the actual entity write; the framework handles the
  transition.
- **`entityCategory`** drives the DMF priority order:
  - `Master` — customers, vendors, items (high priority,
    referenced by transactions).
  - `Reference` — currency codes, configuration tables
    (low priority, prerequisites for masters).
  - `Document` — invoices, orders (depend on masters).
  - `Parameters` — module configurations.
  - `Transaction` — historical movements.
- **`supportsSetBasedSqlOperations: true`** enables bulk SQL
  operations during import. Default. Disable only when the entity
  has per-row business logic that can't run set-based.

---

## ViewMetadata.DataSources

Unlike AxView (which references an external AxQuery), data
entities almost always inline their data-source tree inside
`ViewMetadata.DataSources`. The shape is identical to AxQuery's
data-source shape:

```jsonc
"viewMetadata": {
  "dataSources": [
    { "name": "CustTable", "kind": "Root", "table": "CustTable",
      "dynamicFields": true,
      "dataSources": [
        { "name": "CustGroup", "kind": "Embedded",
          "table": "CustGroup",
          "joinMode": "OuterJoin", "useRelations": true,
          "relations": [
            { "name": "Group", "field": "CustGroup",
              "relatedField": "CustGroup",
              "joinDataSource": "CustTable" }
          ] }
      ] }
  ]
}
```

When the entity has a separate `query` set, the backing AxQuery
owns the data-source tree and `ViewMetadata.DataSources` is
typically left empty / minimal.

---

## Common entity-class methods to override

These show up in entity SourceCode often. All are optional:

- **`insertEntityDataSource(_entityCtx, _dataSourceCtx)`** —
  called per data source on insert. Override for feature-flag
  gating or extra validation.
- **`updateEntityDataSource(_entityCtx, _dataSourceCtx)`** —
  same, on update.
- **`deleteEntityDataSource(_entityCtx, _dataSourceCtx)`** —
  same, on delete.
- **`postLoad()`** — called after read; populate Unmapped
  fields here when `IsComputedField=false`.
- **`mapEntityToDataSource(_entityCtx, _dataSourceCtx)`** —
  override the default field-to-field mapping for special cases.

---

## Property checklist

| Property | Typical | Notes |
|---|---|---|
| `name` | (required) | PascalCase, ends with `Entity`. Matches file name and class name in `sourceCode.declaration`. |
| `publicEntityName` | singular form | OData public name. PascalCase, no `Entity` suffix. |
| `publicCollectionName` | plural form | OData URL segment. |
| `primaryKey` | `EntityKey` | Names one of the entries in `keys[]`. |
| `primaryCompanyContext` | `DataAreaId` | Company-discriminator field; default is correct for most entities. |
| `isPublic` | true | Required for OData exposure. |
| `isReadOnly` | false | Set true for reporting-only entities. |
| `dataManagementEnabled` | true | Required for DMF import/export. |
| `dataManagementStagingTable` | `<EntityName>Staging` | Required when DMF-enabled. |
| `supportsSetBasedSqlOperations` | true | Default. Disable only when there's per-row business logic. |
| `entityCategory` | `Master`/`Reference`/`Document`/`Parameters`/`Transaction` | Drives DMF priority. |
| `label` | `@<File>:<Id>` | Display label. |
| `keys[]` | one `EntityKey` | At minimum the natural key referenced by `primaryKey`. |
| `fields[]` | mostly `Mapped` | Plus the occasional `UnmappedString` for derived columns. |
| `fieldGroups[]` | 5 standard | AutoReport / AutoLookup / AutoIdentification / AutoSummary / AutoBrowse. |
| `viewMetadata.dataSources[]` | one `Root` | Inline data-source tree (joins/ranges live here). |

---

## Common gotchas

- **`Name` mismatch across `name` / file name / `declaration`
  identifier**. All three must agree. The MCP enforces this at
  validation; raw XML hand-authoring is where it bites.
- **`PublicEntityName` collision across models**. The OData
  public namespace is global. Picking `Customer` will collide
  with the platform `Customer` entity. Prefix with your module:
  `MyCustomer`, `ChCustomer`, etc.
- **Forgetting the staging table**. `DataManagementEnabled=true`
  without `DataManagementStagingTable` will compile but fail at
  DMF runtime.
- **Putting an Unmapped field in `Keys`**. The kernel can't
  resolve OData URLs to row identity if the key value comes from
  a computed method. Keys must be Mapped columns only.
- **Empty fields list**. A public entity with zero fields will
  build but expose nothing over OData. The runtime hides such
  entities silently.

---

## See also

- `dynamics-xpp:xpp-table` — the substrate of entity data sources.
- `dynamics-xpp:xpp-view` — the simpler read-only counterpart.
- `dynamics-xpp:xpp-query` — the data-source / join shape that
  `ViewMetadata.DataSources` reuses.
- `dynamics-xpp:xpp-edt` — entity-field types via EDT.
- `dynamics-xpp:xpp-extension` — `AxDataEntityViewExtension` for
  extending MS-shipped entities (separate AOT type).
- [MS: Data entities](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-entities) — authoritative reference.
- [MS: Data entity extensions](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/extensible-data-entities-overview)
- [MS: Computed columns on entities](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/compute-and-virtual-fields)

---

## Note on the typed authoring surface

AxDataEntityView is the **seventh AOT type** on the typed-authoring
layer. The agent-facing API lives in
`Xpp.Service.Domain.Entities.CreateEntityRequest`.

The metamodel has three-tier inheritance
(`AxDataEntity → AxDataEntityViewBase → AxDataEntityView`), and
the canonical element order honors that hierarchy:
`Name → SourceCode (Order=1) → AxDataEntity AxProp scalars
(alphabetical) → AxDataEntityViewBase AxProp scalars
(alphabetical) → AxDataEntityView Order=3 collections
(alphabetical)`.

Reuse: `Tables.TableFieldGroup` (the on-disk
`<AxTableFieldGroup>` is shared between tables, views, and
entities), and `Queries.QueryDataSource` (for
`ViewMetadata.DataSources`).

**Scope:** pragmatic 80%. The `AxDataEntityViewReference` family
(nested entity composition for parent-child / hierarchical
entities) is deferred to the raw `xpp_update_object` escape
hatch. See `plugins/xpp/docs/domain-coverage.md`.
