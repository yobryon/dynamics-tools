# Table Field Defaults (execute_object_modification → AddField)

**When to use:** Whenever you call `execute_object_modification` with
`methodName: "AddField"` against an `AxTable`. This is the boilerplate
parameter set the C# reflection layer demands, plus per-`concreteType` extras.

Last verified against D365 F&O docs: 2026-05-18

## The 17-parameter boilerplate

Every `AddField` call must pass these on the `parameters` object regardless of
field type. Missing any of them surfaces as `"Parameter validation failed"`.
Values are XML-style `NoYes` strings — not JSON booleans
(see [common-property-gotchas.md](./common-property-gotchas.md#1-enum-string-values-not-booleans)).

| Parameter | Sensible default | Notes |
| --- | --- | --- |
| `concreteType` | (required, no default) | The exact field-type class. See table below. |
| `Name` | (required) | The field's metadata name. PascalCase, no spaces. |
| `Label` | `"@SYS<id>"` or literal | If `ExtendedDataType` is set, the EDT's label is used; this can be left as an empty string in that case. |
| `HelpText` | `""` or `"@SYS<id>"` | Inherited from EDT if set. |
| `SaveContents` | `"Yes"` | `"No"` only for non-persisted display fields. |
| `Mandatory` | `"No"` | Set `"Yes"` for required business fields. |
| `AllowEditOnCreate` | `"Yes"` | |
| `AllowEdit` | `"Yes"` | Set `"No"` for surrogate keys / system-managed fields. |
| `Visible` | `"Yes"` | |
| `AosAuthorization` | `"None"` | Use `"CreateRead"` etc. only when row-level security needs it. |
| `MinReadAccess` | `"Auto"` | |
| `IgnoreEDTRelation` | `"Yes"` | **Modern default.** See AX-2012 callout below. |
| `Null` | `"Yes"` | Whether the SQL column allows NULL. Almost always `"Yes"`. |
| `IsSystemGenerated` | `"No"` | |
| `IsManuallyUpdated` | `"No"` | |
| `IsObsolete` | `"No"` | Set `"Yes"` to deprecate without removing. |
| `GeneralDataProtectionRegulation` | `"None"` | Set to `"PersonalData"`, `"SensitivePersonalData"`, or `"EndUserIdentifiableInformation"` for GDPR-tracked fields. |
| `SysSharingType` | `"Duplicate"` | Cross-company sharing classification. `"Reference"` for natural-key columns that must match cross-company. `"None"` for company-local data. |

## concreteType reference

| `concreteType` | Required extras | Common optional extras |
| --- | --- | --- |
| `AxTableFieldString` | (none beyond boilerplate) | `StringSize` (int, default 10), `ExtendedDataType` (preferred — inherits size + label + relations) |
| `AxTableFieldInt` | (none) | `ExtendedDataType` |
| `AxTableFieldInt64` | (none) | `ExtendedDataType` (use this for RecId-typed FK columns) |
| `AxTableFieldReal` | (none) | `ExtendedDataType`, `NoOfDecimals` |
| `AxTableFieldDate` | (none) | `ExtendedDataType` |
| `AxTableFieldUtcDateTime` | (none) | `ExtendedDataType`. Use this — not `AxTableFieldDateTime` — for new timestamp columns. |
| `AxTableFieldEnum` | `EnumType` (the `AxEnum` name) | `ExtendedDataType` |
| `AxTableFieldGuid` | (none) | `ExtendedDataType` |
| `AxTableFieldContainer` | (none) | `ExtendedDataType` (rare — containers are typed by usage). |
| `AxTableFieldMemo` | (none) | `ExtendedDataType` (e.g. `Notes`). Large text, no `StringSize`. |
| `AxTableFieldTime` | (none) | `ExtendedDataType` |

> **Prefer `ExtendedDataType` over per-field property soup.** Setting
> `ExtendedDataType: "CustAccount"` brings in StringSize, label, help text,
> formatting, and (legacy) lookup form references in one go. This matches how
> `CustTable.xml` and other Microsoft-shipped tables declare fields — most
> fields in `CustTable.xml` set only `Name` + `ExtendedDataType` + optional
> overrides. See `J:/AosService/PackagesLocalDirectory/ApplicationSuite/Foundation/AxTable/CustTable.xml`
> lines ~7486–7560 for canonical examples.

## Worked examples

### A string field reusing an EDT

```json
{
  "concreteType": "AxTableFieldString",
  "Name": "AccountNum",
  "ExtendedDataType": "CustAccount",
  "Mandatory": "Yes",
  "AllowEdit": "No",
  "SaveContents": "Yes",
  "AllowEditOnCreate": "Yes",
  "Visible": "Yes",
  "AosAuthorization": "None",
  "MinReadAccess": "Auto",
  "IgnoreEDTRelation": "Yes",
  "Null": "Yes",
  "IsSystemGenerated": "No",
  "IsManuallyUpdated": "No",
  "IsObsolete": "No",
  "GeneralDataProtectionRegulation": "None",
  "SysSharingType": "Reference"
}
```

### An enum field

```json
{
  "concreteType": "AxTableFieldEnum",
  "Name": "Blocked",
  "EnumType": "CustVendorBlocked",
  "Label": "Blocked",
  "SaveContents": "Yes",
  "Mandatory": "No",
  "AllowEditOnCreate": "Yes",
  "AllowEdit": "Yes",
  "Visible": "Yes",
  "AosAuthorization": "None",
  "MinReadAccess": "Auto",
  "IgnoreEDTRelation": "Yes",
  "Null": "Yes",
  "IsSystemGenerated": "No",
  "IsManuallyUpdated": "No",
  "IsObsolete": "No",
  "GeneralDataProtectionRegulation": "None",
  "SysSharingType": "Duplicate"
}
```

### A raw string field without an EDT

```json
{
  "concreteType": "AxTableFieldString",
  "Name": "ExternalRef",
  "StringSize": 60,
  "Label": "External reference",
  "HelpText": "Free-text reference supplied by the external system.",
  "SaveContents": "Yes",
  "Mandatory": "No",
  "AllowEditOnCreate": "Yes",
  "AllowEdit": "Yes",
  "Visible": "Yes",
  "AosAuthorization": "None",
  "MinReadAccess": "Auto",
  "IgnoreEDTRelation": "Yes",
  "Null": "Yes",
  "IsSystemGenerated": "No",
  "IsManuallyUpdated": "No",
  "IsObsolete": "No",
  "GeneralDataProtectionRegulation": "None",
  "SysSharingType": "Duplicate"
}
```

### A UTC datetime field

```json
{
  "concreteType": "AxTableFieldUtcDateTime",
  "Name": "ProcessedDateTime",
  "Label": "Processed",
  "SaveContents": "Yes",
  "Mandatory": "No",
  "AllowEditOnCreate": "No",
  "AllowEdit": "No",
  "Visible": "Yes",
  "AosAuthorization": "None",
  "MinReadAccess": "Auto",
  "IgnoreEDTRelation": "Yes",
  "Null": "Yes",
  "IsSystemGenerated": "Yes",
  "IsManuallyUpdated": "No",
  "IsObsolete": "No",
  "GeneralDataProtectionRegulation": "None",
  "SysSharingType": "Duplicate"
}
```

## AX 2012 vs F&O divergence points

- **`IgnoreEDTRelation: "Yes"` is now the recommended default.** In AX 2012,
  EDTs commonly carried relations and you let them flow through. In F&O,
  guidance is to put the relation on the *table* and ignore the EDT one
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-relation>).
  Set this to `"No"` only when you knowingly want the legacy EDT relation.
- **`GeneralDataProtectionRegulation` is new (GDPR-era).** AX 2012 had no
  equivalent. Always set it explicitly — `"None"` for non-personal data.
- **`AxTableFieldDateTime` is legacy.** Use `AxTableFieldUtcDateTime` for new
  timestamp fields. UTC handling is built in.
- **`AssetClassification` is the modern audit-classification property.** You'll
  see it everywhere in `CustTable.xml` (e.g. `"Customer Content"`). It is
  optional from the metadata layer's perspective but recommended for any field
  storing customer or employee data.

> TODO: Per-property defaults beyond the list above (e.g. specific defaults
> for `AssetClassification`) are not in Microsoft Learn. Call
> `discover_modification_capabilities` against `AxTable` for the live required
> set, or inspect `AxTableField*` types directly in the loaded metadata DLL.

## Sources

- `J:/Tools/dynamics-tools/README.md` (canonical 17-parameter example)
- `J:/Tools/dynamics-tools/ms-api-server/Handlers/CreateFormHandler.cs:683-696`
  (the concreteType → form-control mapping confirms the supported set)
- `J:/Tools/dynamics-tools/ms-api-server/Services/D365ReflectionService.cs:920-1000`
- `J:/AosService/PackagesLocalDirectory/ApplicationSuite/Foundation/AxTable/CustTable.xml`
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-relation>
- <https://learn.microsoft.com/training/modules/build-tables-finance-operations/3-add-fields>
