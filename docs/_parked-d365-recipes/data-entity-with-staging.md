# Data Entity with Staging Table (AxDataEntityView)

**When to use:** Exposing a table (or a join of tables) as a data entity for
OData consumption and/or DMF (Data Management Framework) import/export. The
staging table is required if the entity will be used asynchronously through
DMF.

Last verified against D365 F&O docs: 2026-05-18

## Concepts

The DMF pipeline for asynchronous integration is:

```
Source file/queue  →  Staging table  →  Target data entity  →  Underlying tables
```

The **staging table** is a regular `AxTable` whose structure mirrors the data
entity's fields. The data management framework auto-generates one when you set
`DataManagementEnabled: "Yes"` on the entity *in Visual Studio*, but the MCP
factory does not currently auto-generate staging — you must create it
explicitly and wire it up via the entity's `DataManagementStagingTable` property.

See
<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-management-integration-data-entity>.

## Step 1 — create the staging table

The staging table is a normal `AxTable` (see [table-creation.md](./table-creation.md))
with these conventions:

- `TableGroup` = `"Miscellaneous"` (per DMF convention).
- `SaveDataPerCompany` = `"Yes"` (staging is per-company).
- Field set = the entity's fields **plus** four DMF-specific columns:
  `DefinitionGroup` (string), `ExecutionId` (string), `TransferStatus` (enum
  `DMFTransferStatus`), `IsSelected` (enum `NoYes`).

```json
{
  "objectName": "AcmeProjectStatusStaging",
  "objectType": "AxTable",
  "layer": "usr",
  "properties": {
    "Label": "Project status staging",
    "TableGroup": "Miscellaneous",
    "SaveDataPerCompany": "Yes",
    "CacheLookup": "None",
    "CreatedDateTime": "Yes",
    "ModifiedDateTime": "Yes"
  }
}
```

Then `AddField` for each entity field plus the four DMF columns. Naming
convention: append `_Original` to native fields if the entity exposes both
typed and stringized versions.

> TODO: Confirm whether the MCP factory creates the DMF system fields
> (`DefinitionGroup`, `ExecutionId`, `TransferStatus`, `IsSelected`)
> automatically when `DataManagementStagingTable` is set on the entity, or
> whether they must be added manually. Empirically, Visual Studio adds them via
> the wizard; the MCP route likely does not. Add them manually for safety.

## Step 2 — create the data entity

```json
{
  "objectName": "AcmeProjectStatusEntity",
  "objectType": "AxDataEntityView",
  "layer": "usr",
  "properties": {
    "Label": "Project statuses",
    "PublicEntityName": "AcmeProjectStatus",
    "PublicCollectionName": "AcmeProjectStatuses",
    "EntitySetName": "AcmeProjectStatuses",
    "IsPublic": "Yes",
    "DataManagementEnabled": "Yes",
    "DataManagementStagingTable": "AcmeProjectStatusStaging",
    "EntityCategory": "Reference",
    "PrimaryKey": "StatusCodeIdx"
  }
}
```

### Properties that always matter

| Property | Values | Notes |
| --- | --- | --- |
| `PublicEntityName` | PascalCase singular | Used as the entity name in OData metadata. Required for `IsPublic=Yes`. |
| `PublicCollectionName` | PascalCase plural | The OData collection URL segment (`/data/AcmeProjectStatuses`). Required for `IsPublic=Yes`. |
| `EntitySetName` | usually same as `PublicCollectionName` | Internal name for the entity set. |
| `IsPublic` | `"Yes"` | Required to expose the entity via OData. |
| `DataManagementEnabled` | `"Yes"` | Turns on the staging-based asynchronous pipeline. Required for DMF import/export. |
| `DataManagementStagingTable` | staging table name | The `AxTable` from Step 1. |
| `EntityCategory` | `Parameters`, `Reference`, `Master`, `Document`, `Transaction`, `Configuration` | Drives default sequencing in DMF projects. Master data → `Master`; setup lookups → `Reference`. |
| `PrimaryKey` | index name on the primary datasource | The natural-key index. |

### Add a datasource (the underlying table)

```json
{
  "objectType": "AxDataEntityView",
  "objectName": "AcmeProjectStatusEntity",
  "modifications": [
    {
      "methodName": "AddDataSource",
      "parameters": {
        "concreteType": "AxDataEntityViewDataSource",
        "Name": "AcmeProjectStatus",
        "Table": "AcmeProjectStatus",
        "IsReadOnly": "No"
      }
    }
  ]
}
```

For composite entities, add multiple datasources and link them via
`AddDataSourceField` (parent/child) — see the data-entity-wizard rules at
<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-entity-wizard-rules>.

### Map fields to underlying-table columns

```json
{
  "objectType": "AxDataEntityView",
  "objectName": "AcmeProjectStatusEntity",
  "modifications": [
    {
      "methodName": "AddField",
      "parameters": {
        "concreteType": "AxDataEntityViewMappedField",
        "Name": "StatusCode",
        "DataSource": "AcmeProjectStatus",
        "DataField": "StatusCode",
        "Mandatory": "Yes",
        "IsReadOnly": "No"
      }
    },
    {
      "methodName": "AddField",
      "parameters": {
        "concreteType": "AxDataEntityViewMappedField",
        "Name": "Description",
        "DataSource": "AcmeProjectStatus",
        "DataField": "Description",
        "Mandatory": "No",
        "IsReadOnly": "No"
      }
    }
  ]
}
```

## AX 2012 vs F&O divergence points

- **Data entities replace AIF document services entirely.** AX 2012 used AIF
  (Application Integration Framework) document services with custom queries
  and class-based serialization. F&O routes everything through data entities
  for both synchronous OData and asynchronous DMF
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-management-integration-data-entity>).
  Do not model AIF artifacts in new code.
- **DIXF (the AX 2012 Data Import/Export Framework) is the predecessor to
  DMF.** Microsoft has consolidated DIXF + Excel add-in + AIF into the unified
  DMF
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-entities-data-packages#data-entities>).
- **Avoid tracking `DimensionAttributeValueCombination`,
  `DimensionAttributeValueSet`, and `InventDim` for incremental export.**
  These three cross-company tables are insert-heavy and change-tracking them
  slows exports dramatically; instead track related tables
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-export-perform>).
- **`IsPublic` defaults to `No` in newer platform versions** as a
  defense-in-depth measure. Always set it explicitly to `"Yes"` when you want
  OData exposure; don't rely on inferred behavior.

## Pitfalls

- Staging table conventions are not enforced by the MCP factory. A staging
  table missing the four DMF columns will create successfully but DMF
  imports/exports against the entity will fail at runtime with cryptic
  "staging structure mismatch" errors.
- **The staging table's field set must match the entity's field set by name
  and type.** Renaming an entity field without renaming the staging column
  breaks the mapping.
- `EntityCategory` is not just informational — it drives DMF's recommended
  execution sequence when an entity is added to a data project. Wrong
  category = wrong default sequence = FK violations on import.
- Public collection names should be plural (`AcmeProjectStatuses`, not
  `AcmeProjectStatus`); OData consumers expect this.
- After creating the entity, run database sync. The MCP tool does not trigger
  sync automatically.

## Sources

- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-management-integration-data-entity>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-entities-data-packages>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-entity-wizard-rules>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/staging-tables>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/data-export-perform>
- `J:/Tools/dynamics-tools/ms-api-server/Services/D365ReflectionService.cs`
