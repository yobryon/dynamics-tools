# Table Creation (AxTable)

**When to use:** Creating a new `AxTable` end-to-end. For adding fields to an
existing table, see [table-field-defaults.md](./table-field-defaults.md).

Last verified against D365 F&O docs: 2026-05-18

## Minimum viable creation

`create_xpp_object` only creates the table shell; meaningful tables need
follow-up `execute_object_modification` calls for fields, indexes, and
relations.

```json
{
  "objectName": "AcmeProjectStatus",
  "objectType": "AxTable",
  "layer": "usr",
  "publisher": "Acme",
  "version": "1.0.0.0",
  "properties": {
    "Label": "Project status",
    "TableGroup": "Main",
    "SaveDataPerCompany": "Yes",
    "CacheLookup": "Found"
  }
}
```

`properties` is passed through to the metadata layer and applied directly to the
`AxTable` instance. Any property valid on `AxTable` works here; the names below
are the ones that matter most often.

## Properties that always matter

| Property | Typical values | Why it matters |
| --- | --- | --- |
| `Label` | `"@SYS11307"` or literal | Shown on lookups and report references. Almost always wanted. |
| `TableGroup` | `Main`, `Group`, `Parameter`, `Miscellaneous`, `Transaction`, `WorksheetHeader`, `WorksheetLine`, `Framework`, `Reference`, `TransactionHeader`, `TransactionLine` | Drives default form-update prompts and DIXF behavior. `Main` for master data, `Transaction` / `TransactionLine` for posted business events, `Parameter` for setup, `Reference` for small lookup tables. |
| `SaveDataPerCompany` | `Yes` (default) / `No` | If `Yes`, a `DataAreaId` column is added and rows are partitioned per legal entity. Set to `No` only for genuinely global data (number sequences, postal codes). See <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/cross-company-behavior#review-of-tables-and-views-for-cross-company>. |
| `CacheLookup` | `None`, `NotInTTS`, `Found`, `FoundAndEmpty`, `EntireTable` | `Found` is the default for most master/reference tables. `FoundAndEmpty` for very small reference tables where misses are common. `EntireTable` for tiny static lookups (and **never on inheritance root tables**). `NotInTTS` for parameter tables that change inside a transaction. |
| `TitleField1`, `TitleField2` | field names | Used for the form caption and the lookup display. |
| `ClusteredIndex` | index name | Physical storage order. Usually the primary surrogate or natural key. |
| `PrimaryIndex` | index name | Unique key used as the caching key and as the default lookup. |
| `ReplacementKey` | index name | The natural key used by surrogate-foreign-key replacement on forms. |
| `CreatedDateTime`, `ModifiedDateTime`, `CreatedBy`, `ModifiedBy` | `Yes` / `No` | Adds the system audit columns. Default `No`. Recommended `Yes` for master and transaction tables. |
| `DataSharingType` | `None`, `Duplicate`, `Reference` | Cross-company data sharing classification. Most master tables: `Duplicate`. |
| `Modules` | comma-separated module names | Drives the "owning module" reporting; informational. |
| `ConfigurationKey` | a configuration key | If set, the table is hidden when the configuration key is disabled. |

## Worked example — small master table

Three-step sequence: create shell, add fields, add index. Steps 2 and 3 batch
into a single modification call.

### Step 1 — create the table

```json
{
  "objectName": "AcmeProjectStatus",
  "objectType": "AxTable",
  "layer": "usr",
  "properties": {
    "Label": "Project status",
    "TableGroup": "Reference",
    "SaveDataPerCompany": "Yes",
    "CacheLookup": "Found",
    "CreatedDateTime": "Yes",
    "ModifiedDateTime": "Yes",
    "ModifiedBy": "Yes",
    "DataSharingType": "Duplicate",
    "TitleField1": "StatusCode",
    "TitleField2": "Description"
  }
}
```

### Step 2 — add fields (single batch)

See [table-field-defaults.md](./table-field-defaults.md) for the full
parameter set per field type. Brief version:

```json
{
  "objectType": "AxTable",
  "objectName": "AcmeProjectStatus",
  "modifications": [
    {
      "methodName": "AddField",
      "parameters": {
        "concreteType": "AxTableFieldString",
        "Name": "StatusCode",
        "ExtendedDataType": "Name",
        "Label": "Status code",
        "Mandatory": "Yes",
        "AllowEdit": "No",
        "SaveContents": "Yes", "AllowEditOnCreate": "Yes", "Visible": "Yes",
        "AosAuthorization": "None", "MinReadAccess": "Auto",
        "IgnoreEDTRelation": "Yes", "Null": "Yes",
        "IsSystemGenerated": "No", "IsManuallyUpdated": "No",
        "IsObsolete": "No", "GeneralDataProtectionRegulation": "None",
        "SysSharingType": "Duplicate"
      }
    },
    {
      "methodName": "AddField",
      "parameters": {
        "concreteType": "AxTableFieldString",
        "Name": "Description",
        "ExtendedDataType": "Description",
        "Label": "Description",
        "Mandatory": "No",
        "SaveContents": "Yes", "AllowEditOnCreate": "Yes", "AllowEdit": "Yes",
        "Visible": "Yes", "AosAuthorization": "None", "MinReadAccess": "Auto",
        "IgnoreEDTRelation": "Yes", "Null": "Yes",
        "IsSystemGenerated": "No", "IsManuallyUpdated": "No",
        "IsObsolete": "No", "GeneralDataProtectionRegulation": "None",
        "SysSharingType": "Duplicate"
      }
    }
  ]
}
```

### Step 3 — add a primary index (and reference it from the table)

```json
{
  "objectType": "AxTable",
  "objectName": "AcmeProjectStatus",
  "modifications": [
    {
      "methodName": "AddIndex",
      "parameters": {
        "concreteType": "AxTableIndex",
        "Name": "StatusIdx",
        "AllowDuplicates": "No",
        "Fields": [ { "concreteType": "AxTableIndexField", "DataField": "StatusCode" } ]
      }
    }
  ]
}
```

> TODO: confirm whether the MCP `AddIndex` accepts the nested `Fields` array or
> requires a separate `AddIndexField` follow-up call — behavior depends on
> `PopulateChildCollections` (see
> `ms-api-server/Services/D365ReflectionService.cs:974`). When in doubt, call
> `discover_modification_capabilities` with `objectType: "AxTable"`.

Then patch the table with `PrimaryIndex` / `ClusteredIndex` /
`ReplacementKey` set to `"StatusIdx"`.

## AX 2012 vs F&O divergence points

- **Over-layering is gone.** You cannot create a table that "extends" a
  Microsoft table by re-declaring it; use an `AxTableExtension` instead. All
  Microsoft application models are hard-sealed since release 8.0
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/changes-80>).
- **`CacheLookup = EntireTable` on inheritance roots is no longer allowed**
  through Application Explorer; this is a behavioral tightening from AX 2012
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/application-explorer-aot-properties#table-properties>).
- **Prefer foreign-key table relations over EDT-derived relations.** New fields
  should set `IgnoreEDTRelation: "Yes"` and the table itself should carry the
  relation
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-relation>).
- **`SupportInheritance = Yes` is destructive.** Per Microsoft, "if you set this
  property to Yes, any fields on the table are dropped and must be created
  again." Set it on the first create, never after fields exist.
- **Number-sequence and `RecId` field allocation is automatic** — don't model
  `RecId` or `RecVersion` yourself.

## Sources

- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/application-explorer-aot-properties#table-properties>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/cross-company-behavior#review-of-tables-and-views-for-cross-company>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/sysadmin/drs-srs-dev#guidelines-to-enable-data-sharing-on-tables>
- <https://learn.microsoft.com/training/modules/build-tables-finance-operations/2-properties>
- `J:/AosService/PackagesLocalDirectory/ApplicationSuite/Foundation/AxTable/CustTable.xml` (canonical example)
- `J:/Tools/dynamics-tools/ms-api-server/Services/D365ReflectionService.cs`
