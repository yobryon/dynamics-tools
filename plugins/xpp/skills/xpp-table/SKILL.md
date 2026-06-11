---
name: xpp-table
description: Use when authoring or modifying a D365 F&O AxTable — defining tables, fields, indexes, relations, field groups, delete actions, table-level cache/security/audit properties, and the X++ methods that live on the table class. Tables are the heaviest single-object surface in the AOT; this skill is the comprehensive reference.
---

# Authoring tables (`AxTable`)

Tables persist tabular business data to the AOS SQL database. They are
the densest AOT artifact you'll author — each table file carries
fields, indexes, relations, field groups, delete actions, dozens of
table-level properties, AND the X++ class with its methods. The
dynamics-xpp write surface treats the file as one unit.

Load `dynamics-xpp:xpp-language` first if you haven't. This skill goes deep on the
table surface but assumes the language foundations are in context.

---

## What an AxTable is

A table has two interlocking halves:

1. **AOT XML envelope** — the on-disk `<TableName>.xml` file. It carries
   the X++ class declaration plus the structural metadata (fields,
   indexes, relations, properties) the SQL DDL is derived from.
2. **X++ source text** — the methods declared inside the XML's
   `SourceCode/Methods` block. The declaration is conventionally
   `public class TableName extends common { }`. You **cannot define
   instance or static state** in the class (fields go in the XML's
   `<Fields>` block, not as X++ members).

The table class extends `common` (lowercase by convention), which is
the X++ base for tabular objects. Method overrides on the class
intercept runtime behavior (`insert`, `update`, `delete`,
`validateWrite`, `validateField`, custom static `find` methods, etc.).

On disk:

```
<MetadataStore>\<Model>\AxTable\<TableName>.xml
```

---

## Authoring through dynamics-xpp

AxTable uses **typed domain tools** — you work with a JSON shape
and the service generates the XML. Three tools, mirroring CRUD:

| Tool | Purpose |
|---|---|
| `xpp_create_table(request)` | Create a new AxTable from a typed CreateTableRequest. |
| `xpp_get_table(name)` | Read a table as its domain shape. The response can be sent straight back as a Create payload to clone, or used as the starting point for a patch. |
| `xpp_patch_table(name, patch)` | Apply a partial update. Null fields preserve current state; non-null overwrite. Collections (`fields` / `indexes` / `relations` / `fieldGroups` / `deleteActions`) replace the whole list when non-null — read with `xpp_get_table`, mutate, patch back. |

> **Wide table — reading OR writing? Don't pull the whole XML.** For a core
> table (100+ fields/relations), and ESPECIALLY for **recon** ("what fields /
> relations does this table have?"), do NOT reach for `xpp_get_object_xml` — it
> dumps the entire object as one giant line that overflows context and can't be
> chunked. Use `dynamics-xpp:xpp-navigation` instead:
> - **list the fields/relations:** `xpp_get_table(name, outline=true, atPath='/fields', depth=1)`
>   (each field's name + type + EDT) — likewise `/relations`, `/indexes`.
> - **find one by name/EDT/field:** `xpp_find_in_object` → addressable paths.
> - **zoom one:** `xpp_get_table(name, atPath='/fields/<X>')` for its full detail.
> - **edit one:** `xpp_patch_by_path(op='append'|'merge'|'remove')` — no whole-list resend.
> This works for Microsoft tables too (InventTable, etc.), not just your own.

The discriminator for `fields` is the `fieldType` enum (`String` /
`Int` / `Int64` / `Real` / `Date` / `Time` / `UtcDateTime` / `Enum`
/ `Guid` / `Container`); the mapper translates this to the on-disk
`<AxTableField xsi:type="AxTableFieldString">` etc.

Relations carry a discriminated `constraints` list where each
entry's `type` field is `Field` (column-to-column join), `Fixed`
(constant on this side), or `RelatedFixed` (constant on the
related side).

For an MS-shipped table, write an `AxTableExtension` instead of
modifying the base — see `dynamics-xpp:xpp-extension`.

The raw `xpp_create_object` / `xpp_update_object` tools remain
available as an escape hatch when the domain shape doesn't cover
what you need (state machines, mappings, full-text indexes, a
handful of advanced scalars). See `plugins/xpp/docs/domain-coverage.md`
for the full inclusion ledger.

---

## Minimum viable table (domain shape)

```jsonc
xpp_create_table({
  "name": "MyLogTable",
  "label": "@MyLabels:LogTable",
  "tableGroup": "Main",
  "titleField1": "Name",
  "primaryIndex": "NameIdx",
  "cacheLookup": "Found",
  "fields": [
    {
      "name": "Name",
      "fieldType": "String",
      "extendedDataType": "Name",
      "mandatory": true,
      "assetClassification": "Customer Content"
    },
    {
      "name": "Severity",
      "fieldType": "Enum",
      "enumType": "MyLogSeverity"
    }
  ],
  "indexes": [
    { "name": "NameIdx", "allowDuplicates": false, "alternateKey": true,
      "fields": [{ "dataField": "Name" }] }
  ],
  "fieldGroups": [
    { "name": "AutoReport",         "fields": ["Name", "Severity"] },
    { "name": "AutoLookup",         "fields": ["Name"] },
    { "name": "AutoIdentification", "autoPopulate": true },
    { "name": "AutoSummary",        "fields": ["Name"] },
    { "name": "AutoBrowse" }
  ],
  "sourceCode": {
    "methods": [
      {
        "name": "shouldThrowExceptionOnZeroDelete",
        "source": "    public boolean shouldThrowExceptionOnZeroDelete()\n    {\n        return true;\n    }\n"
      }
    ]
  }
})
```

Key points:

- The eight collection containers (`fields`, `indexes`, `relations`,
  `fieldGroups`, `deleteActions`, `fullTextIndexes`, `mappings`,
  `stateMachines`) emit as empty `<Fields />`, `<Indexes />`, etc.
  on disk even when omitted — the mapper handles this for you.
- The five "Auto" field groups (`AutoReport`, `AutoLookup`,
  `AutoIdentification`, `AutoSummary`, `AutoBrowse`) are the
  standard set every table should have. Forms and reports look
  these up by name to drive default control population.
  `AutoIdentification` carries `autoPopulate: true` — the others don't.
- `shouldThrowExceptionOnZeroDelete()` returning `true` is the
  strongly recommended default (see "Concurrent delete protection"
  below). The VS X++ table template emits this; match it.
- The class `Declaration` defaults to
  `public class <Name> extends common {}` when omitted; specify
  only when you need a custom declaration (usings, fields).

---

## Predefined fields every table has

You don't declare these — the runtime adds them:

- `RecId` — `int64` unique identifier (auto-generated, never reuse,
  never assign). The primary surrogate key.
- `TableId` — int identifying the table type.
- `Partition` — `int64` partition key.
- `DataAreaId` — `str(4)` "company" discriminator. Present only on
  tables with `SaveDataPerCompany="Yes"` (the default). Removed when
  `SaveDataPerCompany="No"` (shared tables).
- Audit fields, governed by table properties (default off; turn on
  for master/transaction tables):
  - `CreatedDateTime`, `ModifiedDateTime` — UTC timestamps.
  - `CreatedBy`, `ModifiedBy` — user IDs.
  - `CreatedTransactionId`, `ModifiedTransactionId` — internal.

Never model these fields yourself in `<Fields>`. They show up
automatically based on the table-level properties.

---

## Defining fields

With the typed domain tools, each entry in `fields` carries a
`fieldType` enum discriminator (`String`, `Int`, `Int64`, `Real`,
`Date`, `Time`, `UtcDateTime`, `Enum`, `Guid`, `Container`). The
mapper translates this to the on-disk polymorphic
`<AxTableField xmlns="" i:type="AxTableFieldString">` form — you
don't author the `xsi:type` attribute by hand.

```jsonc
"fields": [
  {
    "name": "Description",
    "fieldType": "String",
    "extendedDataType": "Description",
    "label": "@SYS9999"
  },
  {
    "name": "Severity",
    "fieldType": "Enum",
    "enumType": "NoYes"
  }
]
```

When using the raw XML escape hatch (`xpp_create_object`), the
on-disk shape is:

```xml
<Fields>
  <AxTableField xmlns="" i:type="AxTableFieldString">
    <Name>Description</Name>
    <ExtendedDataType>Description</ExtendedDataType>
    <Label>@SYS9999</Label>
  </AxTableField>
</Fields>
```

**The `xmlns=""` is mandatory** in raw XML — forget it and the
deserializer fails with a namespace error that points at the wrong
place. The typed tool handles this for you.

### Concrete field types

| `i:type` | Use for | Required extras | Common optional |
|---|---|---|---|
| `AxTableFieldString` | Text columns | (none) | `StringSize` (default 10), `ExtendedDataType` (preferred) |
| `AxTableFieldInt` | 32-bit integers | (none) | `ExtendedDataType` |
| `AxTableFieldInt64` | 64-bit integers (incl. RecId-typed FK columns) | (none) | `ExtendedDataType` |
| `AxTableFieldReal` | Decimal numbers | (none) | `ExtendedDataType`, `NoOfDecimals` |
| `AxTableFieldDate` | Calendar dates | (none) | `ExtendedDataType` |
| `AxTableFieldTime` | Time-of-day | (none) | `ExtendedDataType` |
| `AxTableFieldUtcDateTime` | Timestamps (preferred over legacy `DateTime`) | (none) | `ExtendedDataType` |
| `AxTableFieldEnum` | Typed enumerations | `EnumType` (the `AxEnum` name) | `ExtendedDataType` |
| `AxTableFieldGuid` | GUIDs | (none) | `ExtendedDataType` |
| `AxTableFieldContainer` | X++ containers | (none) | `ExtendedDataType` (rare) |
| `AxTableFieldMemo` | Large text, no fixed size | (none) | `ExtendedDataType` (e.g. `Notes`) |

**`AxTableFieldDateTime` is legacy.** Use `AxTableFieldUtcDateTime` for
new timestamp columns — it handles UTC normalization automatically.

### Prefer EDTs over per-field property soup

Set `<ExtendedDataType>` to the name of an existing EDT whenever
possible. The EDT carries the label, help text, length, formatting,
and (legacy) lookup form references — your field inherits all of
them. This is how `CustTable.xml` and other Microsoft-shipped tables
declare most of their fields:

```xml
<AxTableField xmlns="" i:type="AxTableFieldString">
  <Name>AccountNum</Name>
  <ExtendedDataType>CustAccount</ExtendedDataType>
  <Mandatory>Yes</Mandatory>
</AxTableField>
```

Compare to a bare-string field with no EDT — same logical field but
the maintenance burden is much higher:

```xml
<AxTableField xmlns="" i:type="AxTableFieldString">
  <Name>ExternalRef</Name>
  <StringSize>60</StringSize>
  <Label>External reference</Label>
  <HelpText>Free-text reference supplied by the external system.</HelpText>
</AxTableField>
```

Use `xpp_find_object` with `axType="AxEdt"` to locate existing EDTs
before defining new ones. See `dynamics-xpp:xpp-edt` for EDT authoring.

### The per-field property checklist

> After authoring or modifying a table, run
> `xpp_bp_check(scope="changeset")` to surface BP findings
> against the 184-rule roster. Table-specific rules that often
> fire: `BPErrorTableTitleField1NotDeclared`,
> `BPErrorTableMissingGroupAutoReport`,
> `BPErrorTableFieldNotInFieldGroup`,
> `BPErrorTablePrimaryKeyEditable`,
> `BPTableWithRecIdIndexMissingReplacementKey`,
> `BPErrorTableRelationshipPropertiesCompleteness`. See
> `plugins/xpp/docs/bp-rules-reference.md` for the full list.
> If the table has schema changes, set `<DBSyncInBuild>True`
> in the .rnrproj before running `xpp_compile` so the build
> pipeline picks up the DB sync — see `dynamics-xpp:xpp-project`.

Field XML elements that BPC (Best Practice Checks) expects on every
field. Specify each one explicitly to keep your XML self-documenting
and BPC-clean. Defaults shown are what you typically want — override
with intent.

| Property | Typical value | Notes |
|---|---|---|
| `Name` | (required) | PascalCase, unique within the table. |
| `ExtendedDataType` | matching EDT | Inherits StringSize/Label/HelpText. |
| `Label` | `@LabelFile:LabelId` or omit if EDT-inherited | Prefer label references over inline literals for translatability. |
| `HelpText` | `@LabelFile:LabelId` or omit if EDT-inherited | Same translatability rule. |
| `Mandatory` | `No` (default) / `Yes` | Required at the row level. |
| `AllowEdit` | `Yes` (default) / `No` | `No` for surrogate keys / system-managed values. |
| `AllowEditOnCreate` | `Yes` | |
| `Visible` | `Yes` | |
| `SaveContents` | `Yes` (default) | `No` only for non-persisted display columns. |
| `AosAuthorization` | `None` | Use stronger values only when row-level security needs it. |
| `MinReadAccess` | `Auto` | |
| `IgnoreEDTRelation` | `Yes` | **Modern default.** EDT-level relations are discouraged in F&O — put the relation on the table instead. *In the typed `xpp_create_table` / `xpp_patch_table` request, this property is nested under each field's `advanced` block (`fields[N].advanced.ignoreEDTRelation`), not at the field's top level.* |
| `Null` | `Yes` | Whether the SQL column allows NULL. Almost always Yes. |
| `IsSystemGenerated` | `No` / `Yes` for system timestamps | |
| `IsManuallyUpdated` | `No` | |
| `IsObsolete` | `No` / `Yes` to deprecate without removing | |
| `GeneralDataProtectionRegulation` | `Customer Content` / `End User Identifiable Information` / `End User Pseudonymous Identifiers` / `Organization Identifiable Information` / `Support Data` / `System Metadata` | **Optional.** Set it when the column carries personal or sensitive data. **Omit the element** when it doesn't apply — the bridge rejects `None` because that value isn't in the enum. MS-shipped tables routinely omit this element for non-personal columns; don't fabricate a value. |
| `SysSharingType` | `Duplicate` / `Reference` / `None` | Cross-company sharing classification. `Reference` for natural-key columns that must match cross-company. `None` for company-local. |
| `AssetClassification` | e.g. `Customer Content` | Modern audit-classification property; visible everywhere in MS-shipped tables. Optional in the schema but recommended for customer/employee data. |

If you skip the BPC-required ones, the create succeeds but the build
warns or errors. Always think through each one explicitly.

### Examples

#### A string field reusing an EDT

```xml
<AxTableField xmlns="" i:type="AxTableFieldString">
  <Name>AccountNum</Name>
  <ExtendedDataType>CustAccount</ExtendedDataType>
  <Mandatory>Yes</Mandatory>
  <AllowEdit>No</AllowEdit>
  <SaveContents>Yes</SaveContents>
  <AllowEditOnCreate>Yes</AllowEditOnCreate>
  <Visible>Yes</Visible>
  <AosAuthorization>None</AosAuthorization>
  <MinReadAccess>Auto</MinReadAccess>
  <IgnoreEDTRelation>Yes</IgnoreEDTRelation>
  <Null>Yes</Null>
  <IsSystemGenerated>No</IsSystemGenerated>
  <IsManuallyUpdated>No</IsManuallyUpdated>
  <IsObsolete>No</IsObsolete>
  <!-- GeneralDataProtectionRegulation: omit when not personal data; otherwise set to "Customer Content" / "End User Identifiable Information" / etc. -->
  <SysSharingType>Reference</SysSharingType>
</AxTableField>
```

#### An enum field

```xml
<AxTableField xmlns="" i:type="AxTableFieldEnum">
  <Name>Blocked</Name>
  <EnumType>CustVendorBlocked</EnumType>
  <Label>Blocked</Label>
  <SaveContents>Yes</SaveContents>
  <Mandatory>No</Mandatory>
  <AllowEditOnCreate>Yes</AllowEditOnCreate>
  <AllowEdit>Yes</AllowEdit>
  <Visible>Yes</Visible>
  <AosAuthorization>None</AosAuthorization>
  <MinReadAccess>Auto</MinReadAccess>
  <IgnoreEDTRelation>Yes</IgnoreEDTRelation>
  <Null>Yes</Null>
  <IsSystemGenerated>No</IsSystemGenerated>
  <IsManuallyUpdated>No</IsManuallyUpdated>
  <IsObsolete>No</IsObsolete>
  <!-- GeneralDataProtectionRegulation: omit when not personal data; otherwise set to "Customer Content" / "End User Identifiable Information" / etc. -->
  <SysSharingType>Duplicate</SysSharingType>
</AxTableField>
```

#### A UTC datetime field

```xml
<AxTableField xmlns="" i:type="AxTableFieldUtcDateTime">
  <Name>ProcessedDateTime</Name>
  <Label>Processed</Label>
  <SaveContents>Yes</SaveContents>
  <Mandatory>No</Mandatory>
  <AllowEditOnCreate>No</AllowEditOnCreate>
  <AllowEdit>No</AllowEdit>
  <Visible>Yes</Visible>
  <AosAuthorization>None</AosAuthorization>
  <MinReadAccess>Auto</MinReadAccess>
  <IgnoreEDTRelation>Yes</IgnoreEDTRelation>
  <Null>Yes</Null>
  <IsSystemGenerated>Yes</IsSystemGenerated>
  <IsManuallyUpdated>No</IsManuallyUpdated>
  <IsObsolete>No</IsObsolete>
  <!-- GeneralDataProtectionRegulation: omit when not personal data; otherwise set to "Customer Content" / "End User Identifiable Information" / etc. -->
  <SysSharingType>Duplicate</SysSharingType>
</AxTableField>
```

---

## Field groups

Field groups are non-persistent — they're presentation metadata used by
the form layer. A group is a named bundle of field references:

```xml
<FieldGroups>
  <AxTableFieldGroup>
    <Name>Identification</Name>
    <Fields>
      <AxTableFieldGroupField>
        <DataField>AccountNum</DataField>
      </AxTableFieldGroupField>
      <AxTableFieldGroupField>
        <DataField>Name</DataField>
      </AxTableFieldGroupField>
    </Fields>
  </AxTableFieldGroup>
</FieldGroups>
```

The five **standard auto-groups** should always exist with their
conventional purpose:

- `AutoReport` — fields to show on auto-generated reports.
- `AutoLookup` — fields to show on lookup dropdowns. Typically the
  natural key plus the description/title field.
- `AutoIdentification` — fields that identify a row. Carries
  `<AutoPopulate>Yes</AutoPopulate>` so forms pick it up automatically.
- `AutoSummary` — fields for summary displays.
- `AutoBrowse` — fields shown by default in browse views.

Add custom field groups sparingly and give them descriptive names
(e.g. `Identification`, `Address`, `Financial`). Forms refer to groups
by name; renaming a group later breaks every consuming form.

---

## Indexes

Indexes live under `<Indexes>` and reference field **names** (not
field elements):

```xml
<Indexes>
  <AxTableIndex>
    <Name>SeverityIdx</Name>
    <AllowDuplicates>Yes</AllowDuplicates>
    <Fields>
      <AxTableIndexField>
        <DataField>Severity</DataField>
      </AxTableIndexField>
    </Fields>
  </AxTableIndex>
</Indexes>
```

### Index properties

- `Name` — required, unique within the table.
- `AllowDuplicates` — `Yes` (default) / `No`. Set `No` for unique
  natural keys.
- `AlternateKey` — `Yes` / `No`. Set `Yes` for a unique-key index that
  carries surrogate-foreign-key replacement information. The
  `ReplacementKey` table-level property names which index plays this
  role.
- `Enabled` — `Yes` (default).
- `Fields` — at least one `<AxTableIndexField>` with `DataField` and
  optional `IncludedColumn` for SQL-server included columns.

If any `DataField` references a field that isn't defined on this table
(or its base, for extensions), the metadata layer silently drops the
index from build. **Verify field existence before adding the index.**

### Example: composite alternate-key index

This pattern is common for shared lookup tables (e.g. translation
tables, mapping tables) where a multi-field natural key must be
unique:

```xml
<Indexes>
  <AxTableIndex>
    <Name>IntegrationFromSet</Name>
    <AlternateKey>Yes</AlternateKey>
    <Fields>
      <AxTableIndexField>
        <DataField>IntegrationId</DataField>
      </AxTableIndexField>
      <AxTableIndexField>
        <DataField>FromCountryRegionId</DataField>
      </AxTableIndexField>
      <AxTableIndexField>
        <DataField>FromStateId</DataField>
      </AxTableIndexField>
    </Fields>
  </AxTableIndex>
</Indexes>
```

Then at the table level:

```xml
<ReplacementKey>IntegrationFromSet</ReplacementKey>
```

### Index naming

Common conventions:

- Single-column non-unique: `{FieldName}Idx` (e.g. `SeverityIdx`).
- Multi-column composite: `{Purpose}Idx` (e.g. `OrderDateIdx`).
- Unique alternate keys: descriptive name (e.g. `IntegrationFromSet`).

---

## Relations

Relations describe how this table joins to others. In F&O the modern
guidance is to define relations **on the table** (not on EDTs), and
to set `IgnoreEDTRelation="Yes"` on the field.

```xml
<Relations>
  <AxTableRelation>
    <Name>LogisticsAddressCountryRegion_From</Name>
    <RelatedTable>LogisticsAddressCountryRegion</RelatedTable>
    <Constraints>
      <AxTableRelationConstraint xmlns="" i:type="AxTableRelationConstraintField">
        <Name>FromCountryRegionId</Name>
        <Field>FromCountryRegionId</Field>
        <RelatedField>CountryRegionId</RelatedField>
      </AxTableRelationConstraint>
    </Constraints>
  </AxTableRelation>
</Relations>
```

Note the `xmlns=""` + `i:type` discriminator pattern again — relations
have several constraint subtypes:

- `AxTableRelationConstraintField` — field-to-field constraint (most
  common; the example above).
- `AxTableRelationConstraintRelatedFixed` — relate to a literal value
  on the related table.
- `AxTableRelationConstraintFixed` — pin a literal value on this side.

### Relation properties

- `Name` — required, unique within the table.
- `RelatedTable` — the target table's name.
- `Cardinality` — `ZeroOne` / `ExactlyOne` / `ZeroMore` / `OneMore`.
  Defaults are sensible for "many-to-one to lookup."
- `RelatedTableCardinality` — corresponding cardinality on the related
  table's side.
- `UseDefaultRoleNames` — `Yes` (typical). Set `No` and use
  `RoleName` / `RelatedTableRole` to override.

### Common multi-relation table

A table with several FK relations (the conECommMappingStateTranslation
example):

```xml
<Relations>
  <AxTableRelation>
    <Name>LogisticsAddressCountryRegion_From</Name>
    <RelatedTable>LogisticsAddressCountryRegion</RelatedTable>
    <Constraints>
      <AxTableRelationConstraint xmlns="" i:type="AxTableRelationConstraintField">
        <Name>FromCountryRegionId</Name>
        <Field>FromCountryRegionId</Field>
        <RelatedField>CountryRegionId</RelatedField>
      </AxTableRelationConstraint>
    </Constraints>
  </AxTableRelation>
  <AxTableRelation>
    <Name>LogisticsAddressCountryRegion_To</Name>
    <RelatedTable>LogisticsAddressCountryRegion</RelatedTable>
    <Constraints>
      <AxTableRelationConstraint xmlns="" i:type="AxTableRelationConstraintField">
        <Name>ToCountryRegionId</Name>
        <Field>ToCountryRegionId</Field>
        <RelatedField>CountryRegionId</RelatedField>
      </AxTableRelationConstraint>
    </Constraints>
  </AxTableRelation>
</Relations>
```

When two relations target the same `RelatedTable`, give them
descriptive `Name`s (e.g. `_From` / `_To` suffixes) so the form layer
can disambiguate. Otherwise the auto-lookups will fight each other.

---

## Delete actions

`DeleteActions` define cascades to related tables when a row of this
table is deleted:

```xml
<DeleteActions>
  <AxTableDeleteAction>
    <Name>CustOrders</Name>
    <DeleteAction>Cascade</DeleteAction>
    <RelatedTable>CustOrder</RelatedTable>
  </AxTableDeleteAction>
</DeleteActions>
```

### Delete action values

- `None` — no action, deletion proceeds.
- `Cascade` — also delete the matching rows in `RelatedTable`.
- `Restricted` — fail the delete if matching rows exist.
- `CascadeRestricted` — cascade unless there are deeper restricts.

**Cascades can melt performance** on heavily-related tables. Read the
related table's existing `Relations` before adding a `Cascade` — if
the cardinality is "potentially many," the cascade can take down the
AOS during peak operations.

---

## Table-level properties

These live as direct children of `<AxTable>` (no nested container).
Pick deliberately:

| Property | Typical values | Why it matters |
|---|---|---|
| `Label` | `@SYS11307` or literal | Shown on lookups and report references. Almost always wanted. |
| `TableGroup` | `Main`, `Group`, `Parameter`, `Miscellaneous`, `Transaction`, `WorksheetHeader`, `WorksheetLine`, `Framework`, `Reference`, `TransactionHeader`, `TransactionLine` | Drives default form-update prompts and DIXF behavior. `Main` for master data; `Transaction`/`TransactionLine` for posted business events; `Parameter` for setup; `Reference` for small lookup tables. |
| `SaveDataPerCompany` | `Yes` (default) / `No` | If `Yes`, a `DataAreaId` column is added and rows are partitioned per legal entity. Set `No` only for genuinely global data (number sequences, postal codes, country lookups, translation tables). |
| `CacheLookup` | `None`, `NotInTTS`, `Found`, `FoundAndEmpty`, `EntireTable` | `Found` is the default for most master/reference tables. `FoundAndEmpty` for very small reference tables where misses are common. `EntireTable` for tiny static lookups (and **never on inheritance root tables**). `NotInTTS` for parameter tables that change inside transactions. |
| `TitleField1`, `TitleField2` | field names | Used for the form caption and the lookup display. |
| `ClusteredIndex` | index name | Physical SQL storage order. Usually the primary surrogate or natural key. |
| `PrimaryIndex` | index name | Unique key used as the caching key and the default lookup. |
| `ReplacementKey` | index name | The natural-key alternate-key index used by surrogate-FK replacement on forms. |
| `CreatedDateTime`, `ModifiedDateTime`, `CreatedBy`, `ModifiedBy` | `Yes` / `No` | Adds the system audit columns. Default `No`. Recommended `Yes` for master and transaction tables. |
| `DataSharingType` | `None`, `Duplicate`, `Reference` | Cross-company data sharing classification. Most master tables: `Duplicate`. |
| `Modules` | comma-separated module names | Drives the "owning module" reporting; informational. |
| `ConfigurationKey` | a configuration key | If set, the table is hidden when the configuration key is disabled. |
| `SubscriberAccessLevel` | `<Read>Allow</Read>` block | Controls whether subscribers (data-export integrations) can read this table. |
| `AllowRowVersionChangeTracking` | `Yes` / `No` | Enables row-version tracking for change feeds. |
| `SupportInheritance` | `Yes` / `No` | **Destructive** — see callout below. |

### `SupportInheritance` is destructive

From MS: "if you set this property to `Yes`, any fields on the table
are dropped and must be created again." **Set this on the first
create, never after fields exist.** If you find yourself wanting to
turn inheritance on for an existing table, you almost certainly need
to design a fresh table and migrate the data, not toggle the property.

---

## The `shouldThrowExceptionOnZeroDelete` method

Always implement this method on tables that support deletes:

```xpp
public boolean shouldThrowExceptionOnZeroDelete()
{
    return true;
}
```

When `true`, a concurrent delete (two processes both decide to delete
the same row; one wins, the other affects zero rows) raises an
exception instead of silently succeeding on one and silently no-op'ing
on the other. The default behavior (without this method or with it
returning `false`) is data-loss-prone — only return `false` if you
have a documented business reason.

This is conventional enough that the VS table template emits it and
MS's Copilot integration includes it in the minimum viable XML.

---

## Other common methods to override

These are X++ methods on the table class. Add to `<SourceCode>/<Methods>`:

- `insert()` / `update()` / `delete()` — intercept DML. Always call
  `super()` unless you fully replace the behavior (rare).
- `validateWrite()` — called before insert/update commits. Return
  `false` and surface error messages via `error()` to block the write.
- `validateField(_fieldId)` — per-field validation. Use the
  `fieldNum(MyTable, MyField)` token for `_fieldId`.
- `find(...)` (static) — convention for "get a record by natural key."
  Most tables ship one.
- `exists(...)` (static) — convention for "is there a record with this
  key." Returns `boolean`.
- `initValue()` — set field defaults at row creation time. Called
  during the `new` event before the user can edit.
- `modifiedField(_fieldId)` — react to a single field's modification.

Refer to identifiers via the token functions (`tableStr`, `fieldStr`,
`fieldNum`) so renames don't silently break.

---

## AX 2012 vs F&O divergence points

If you have AX 2012 muscle memory, these have changed:

- **Over-layering is gone.** You cannot create a table that "extends"
  a Microsoft table by re-declaring it. Use an `AxTableExtension`
  (see `dynamics-xpp:xpp-extension`). All Microsoft application models are
  hard-sealed since release 8.0.
- **`CacheLookup = EntireTable` on inheritance roots is no longer
  allowed** through Application Explorer. Pre-existing instances may
  still compile; don't introduce new ones.
- **Prefer table-level foreign-key relations over EDT-derived
  relations.** New fields should set `IgnoreEDTRelation="Yes"` and the
  table itself should carry the relation.
- **`GeneralDataProtectionRegulation` is optional**, not required.
  Set it (`Customer Content`, `End User Identifiable Information`,
  etc.) when the field stores personal or sensitive data. **Omit the
  element entirely** for non-personal columns — the runtime enum has
  no `None` value, so writing `<GeneralDataProtectionRegulation>None</...>`
  fails deserialization. MS-shipped tables routinely omit the element.
- **`AxTableFieldDateTime` is legacy.** Use `AxTableFieldUtcDateTime`
  for new timestamp fields.
- **`AssetClassification` is the modern audit-classification
  property.** Optional in the schema; recommended for customer/employee
  data. You'll see it on every field in `CustTable.xml`.
- **`RecVersion` is automatic.** Don't model it.
- **Number-sequence allocation of `RecId` is automatic.** Don't model
  or assign `RecId` yourself.

---

## Gotchas

- **`xmlns=""` on field elements.** Required. Forgetting it produces
  a confusing namespace error.
- **`xmlns=""` on relation constraints.** Same requirement
  (`<AxTableRelationConstraint xmlns="" i:type="...">`).
- **Field names referenced by indexes/relations/field-groups are
  strings.** A typo silently drops the index/relation/group from
  build. Verify field existence before adding.
- **Adding a `Cascade` delete action is high-blast-radius.** Audit the
  related table's row volumes before committing.
- **`SaveDataPerCompany="No"` permanently removes the `DataAreaId`
  column.** Switching the property later will reorganize the SQL table
  in nontrivial ways.
- **`SupportInheritance` once-only.** Set at table creation, never
  flip later.

---

## Things the XSD can't tell you

The `xpp://schema/AxTable` XSD is the formal grammar but won't catch:

- **`CacheLookup` enum values.** The XSD declares it as `xs:string`.
  An invalid value passes XSD validation and fails at the metadata
  layer's enum-deserialization step.
- **Field references in indexes/relations.** "This index references
  `FieldX`" is just a string in the XML — XSD doesn't verify the field
  exists. Compile catches missing fields; you don't.
- **Related-table existence.** A `RelatedTable` pointing at a
  non-existent table validates against XSD and fails at AOS build.
- **Cardinality coherence.** "ZeroOne to ZeroMore" combinations the
  schema allows but business logic rejects.
- **EDT existence on `ExtendedDataType` references.** Same story —
  string in XML; XSD doesn't verify.
- **BPC rule violations.** Missing labels, missing GDPR classification
  on personal-data fields, configuration-key references with no
  matching configuration key.

---

## See also

- `dynamics-xpp:xpp-language` — language foundations.
- `dynamics-xpp:xpp-edt` — defining EDTs (which your fields should reuse).
- `dynamics-xpp:xpp-enum` — defining enums (for `fieldType: "Enum"`'s
  `enumType` references).
- `dynamics-xpp:xpp-extension` — `AxTableExtension` for modifying Microsoft-shipped
  tables.
- `dynamics-xpp:xpp-labelfile` — label authoring.
- `xpp://schema/AxTable` — authoritative XSD (used by the
  `xpp_create_object` escape hatch when the domain shape doesn't
  cover what you need).

---

## Note on the typed authoring surface

AxTable is the **third AOT type** (after AxEnum and AxEdt) on the
typed-authoring layer. The agent-facing API lives in
`Xpp.Service.Domain.Tables.CreateTableRequest`. The two-layer
polymorphism — fields (`AxTableFieldString` / `Int` / `Enum` etc.)
and relation constraints (`AxTableRelationConstraintField` /
`Fixed` / `RelatedFixed`) — is collapsed into discriminator enums
(`fieldType`, `type`) plus a few subtype-gated fields
(`stringSize`, `enumType`, `value` / `valueStr`), so the agent
never has to deal with `xsi:type` directly.

Scope: pragmatic 80% — see `plugins/xpp/docs/domain-coverage.md`
for the full inclusion ledger (state machines, mappings,
full-text indexes, and a few advanced scalars are deferred to the
raw `xpp_update_object` escape hatch).

If you find yourself needing a property the domain shape doesn't
cover:

1. Use `xpp_get_object_xml` + `xpp_update_object` for that one
   case as an escape hatch.
2. Or surface the gap so the property can be added — the domain
   layer is meant to cover the authoring needs of real tables;
   gaps in the 80% are bugs.
