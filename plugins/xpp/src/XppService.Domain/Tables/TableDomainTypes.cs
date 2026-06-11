using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain.Tables;

/// <summary>
/// Domain shape for AxTable authoring. Pragmatic 80% scope —
/// covers fields (all 10 subtypes), indexes, relations w/ constraints,
/// field groups, delete actions, methods (opaque X++ source), and
/// ~25 common scalar properties. State machines / mappings /
/// full-text indexes / a handful of exotic scalars are deferred to
/// the raw xpp_update_object escape hatch — see
/// plugins/xpp/docs/domain-coverage.md for the full inclusion ledger.
/// </summary>
public sealed record CreateTableRequest
{
    [Description("The AOT name of the table. PascalCase. Must be unique within the model.")]
    public string Name { get; init; } = string.Empty;

    [Description("Display label (label ref @File:Id preferred). Used in forms, reports, cross-references.")]
    public string? Label { get; init; }

    [Description("Singular form of the label (Customer vs Customers). Used in collection-shaped UIs and data-entity scenarios.")]
    public string? SingularLabel { get; init; }

    [Description("Developer-facing description; doesn't surface to end users. Label-ref preferred.")]
    public string? DeveloperDocumentation { get; init; }

    [Description("Name of a parent table to extend (table inheritance). When set, this table inherits its parent's fields, indexes, and methods.")]
    public string? Extends { get; init; }

    [Description("AOT cross-reference grouping: Main / Reference / Group / Setup / Transaction / TransactionHeader / TransactionLine / WorksheetHeader / WorksheetLine / Parameter / Worksheet / Framework / Miscellaneous.")]
    public string? TableGroup { get; init; }

    [Description("Storage kind: Regular (default, persisted), TempDB (server-side temp), InMemory (client-side temp).")]
    public TableType? TableType { get; init; }

    [Description("Whether table is empty-by-default at install (Default), prepopulated (BaseData), prepopulated and user-locked (BaseDataLocked).")]
    public TableContents? TableContents { get; init; }

    [Description("SQL caching policy: None / NotInTTS / Found / FoundAndEmpty / EntireTable. Trade-off between staleness and SELECT latency.")]
    public CacheLookup? CacheLookup { get; init; }

    [Description("Name of the unique index used as the table's logical primary key. Used by replacement-key resolution and surrogate-key lookups.")]
    public string? PrimaryIndex { get; init; }

    [Description("Name of the SQL clustered index. Defaults to RecId if unset. Setting this changes physical row order.")]
    public string? ClusteredIndex { get; init; }

    [Description("Whether each company partition gets its own row set (true; default for regular tables) or rows are shared across companies (false; system / global tables).")]
    public bool? SaveDataPerCompany { get; init; }

    [Description("Whether each partition gets its own row set. Defaults to true.")]
    public bool? SaveDataPerPartition { get; init; }

    [Description("Field name used as the primary title/identifier when this table is referenced from forms or lookups.")]
    public string? TitleField1 { get; init; }

    [Description("Secondary title field. Combined with TitleField1 in some lookup renderings.")]
    public string? TitleField2 { get; init; }

    [Description("Restricts table visibility to a configuration key. Hides the table when key is disabled.")]
    public string? ConfigurationKey { get; init; }

    [Description("Comma-separated ISO country codes that restrict where this table is visible.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Module ownership. Comma-separated functional area names.")]
    public string? Modules { get; init; }

    [Description("Free-form tag string. Used by the platform to mark elements (e.g. customizations, telemetry).")]
    public string? Tags { get; init; }

    [Description("Marks the table obsolete. Tooling warns on new references.")]
    public bool? IsObsolete { get; init; }

    [Description("Creates a RecId-based unique index automatically. Default true.")]
    public bool? CreateRecIdIndex { get; init; }

    [Description("Auto-populate the createdBy audit field (the user who created the row). Default No.")]
    public bool? CreatedBy { get; init; }

    [Description("Auto-populate the createdDateTime audit field. Default No.")]
    public bool? CreatedDateTime { get; init; }

    [Description("Auto-populate the modifiedBy audit field (the user who last changed the row). Default No.")]
    public bool? ModifiedBy { get; init; }

    [Description("Auto-populate the modifiedDateTime audit field. Default No.")]
    public bool? ModifiedDateTime { get; init; }

    [Description("Name of an alternate-key index that uniquely identifies a row outside of RecId.")]
    public string? ReplacementKey { get; init; }

    [Description("Reference form for record lookups.")]
    public string? FormRef { get; init; }

    [Description("Reference list page used when navigating to this table.")]
    public string? ListPageRef { get; init; }

    [Description("Reference preview part for hover-preview UI.")]
    public string? PreviewPartRef { get; init; }

    [Description("Cross-company / sharing scope: Local (per-company), Global (shared), etc.")]
    public string? OperationalDomain { get; init; }

    [Description("X++ source code: class Declaration + Methods. Method bodies are opaque text preserved verbatim. Omit to inherit a minimal default declaration.")]
    public TableSourceCode? SourceCode { get; init; }

    [Description("Per-operation access for cross-company subscribers (Read / Create / Update / Delete / Correct / Invoke). Each value is Allow or Deny.")]
    public SubscriberAccessLevel? SubscriberAccessLevel { get; init; }

    [Description("Table fields. Each field carries its FieldType discriminator (String / Int / Int64 / Real / Date / Time / UtcDateTime / Enum / Guid / Container) plus shared properties (Label, Mandatory, AllowEdit, ExtendedDataType, etc.) and a few type-gated properties (StringSize, EnumType, Scale).")]
    public List<TableField>? Fields { get; init; }

    [Description("Indexes on this table. Each carries fields + flags (AllowDuplicates, AlternateKey, etc.).")]
    public List<TableIndex>? Indexes { get; init; }

    [Description("Foreign-key relations to other tables. Each carries constraints (Field / Fixed / RelatedFixed) that define the join predicate.")]
    public List<TableRelation>? Relations { get; init; }

    [Description("Field groups: ordered subsets of fields used as 'sections' on forms (AutoReport, AutoLookup, AutoSummary, custom).")]
    public List<TableFieldGroup>? FieldGroups { get; init; }

    [Description("Delete actions on related tables. Each action declares what happens to dependent rows when this row is deleted.")]
    public List<TableDeleteAction>? DeleteActions { get; init; }

    [Description("Advanced / less-common scalar properties. See AdvancedTableOptions for the full list.")]
    public AdvancedTableOptions? Advanced { get; init; }
}

/// <summary>Merge-patch shape for AxTable. Null = leave current. Non-null = overwrite.
/// Collections non-null replaces the whole list (read with xpp_get_table, mutate, patch back).</summary>
public sealed record PatchTableRequest
{
    public string? Label { get; init; }
    public string? SingularLabel { get; init; }
    public string? DeveloperDocumentation { get; init; }
    public string? Extends { get; init; }
    public string? TableGroup { get; init; }
    public TableType? TableType { get; init; }
    public TableContents? TableContents { get; init; }
    public CacheLookup? CacheLookup { get; init; }
    public string? PrimaryIndex { get; init; }
    public string? ClusteredIndex { get; init; }
    public bool? SaveDataPerCompany { get; init; }
    public bool? SaveDataPerPartition { get; init; }
    public string? TitleField1 { get; init; }
    public string? TitleField2 { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public string? Modules { get; init; }
    public string? Tags { get; init; }
    public bool? IsObsolete { get; init; }
    public bool? CreateRecIdIndex { get; init; }
    public string? ReplacementKey { get; init; }
    public string? FormRef { get; init; }
    public string? ListPageRef { get; init; }
    public string? PreviewPartRef { get; init; }
    public string? OperationalDomain { get; init; }
    public TableSourceCode? SourceCode { get; init; }
    public SubscriberAccessLevel? SubscriberAccessLevel { get; init; }
    public List<TableField>? Fields { get; init; }
    public List<TableIndex>? Indexes { get; init; }
    public List<TableRelation>? Relations { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<TableDeleteAction>? DeleteActions { get; init; }
    public AdvancedTableOptions? Advanced { get; init; }
}

/// <summary>Read shape returned by xpp_get_table. Same surface as CreateTableRequest; the response can be passed straight back into xpp_create_table to clone.</summary>
public sealed record GetTableResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? SingularLabel { get; init; }
    public string? DeveloperDocumentation { get; init; }
    public string? Extends { get; init; }
    public string? TableGroup { get; init; }
    public TableType? TableType { get; init; }
    public TableContents? TableContents { get; init; }
    public CacheLookup? CacheLookup { get; init; }
    public string? PrimaryIndex { get; init; }
    public string? ClusteredIndex { get; init; }
    public bool? SaveDataPerCompany { get; init; }
    public bool? SaveDataPerPartition { get; init; }
    public string? TitleField1 { get; init; }
    public string? TitleField2 { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public string? Modules { get; init; }
    public string? Tags { get; init; }
    public bool? IsObsolete { get; init; }
    public bool? CreateRecIdIndex { get; init; }
    public string? ReplacementKey { get; init; }
    public string? FormRef { get; init; }
    public string? ListPageRef { get; init; }
    public string? PreviewPartRef { get; init; }
    public string? OperationalDomain { get; init; }
    public TableSourceCode? SourceCode { get; init; }
    public SubscriberAccessLevel? SubscriberAccessLevel { get; init; }
    public List<TableField>? Fields { get; init; }
    public List<TableIndex>? Indexes { get; init; }
    public List<TableRelation>? Relations { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<TableDeleteAction>? DeleteActions { get; init; }
    public AdvancedTableOptions? Advanced { get; init; }
}

// ---- Fields -----------------------------------------------------------------

public sealed record TableField
{
    [Description("Field name. PascalCase. Unique within the table.")]
    public string Name { get; init; } = string.Empty;

    [Description("Primitive type of the field: String / Int / Int64 / Real / Date / Time / UtcDateTime / Enum / Guid / Container. Drives which i:type discriminator is emitted on the wire.")]
    public TableFieldType FieldType { get; init; }

    [Description("Name of an AxEdt that types this field. Most fields reference one — this is the lever that inherits label/help/size/lookup from the EDT. Set null only for primitive-typed fields.")]
    public string? ExtendedDataType { get; init; }

    [Description("AxEnum name. Required (or inherited via ExtendedDataType being an Enum EDT) when FieldType=Enum.")]
    public string? EnumType { get; init; }

    [Description("Override the EDT's label for this specific field. Usually null (inherit from EDT).")]
    public string? Label { get; init; }

    [Description("Override the EDT's help text for this specific field.")]
    public string? HelpText { get; init; }

    [Description("If true, the field must be set before save. Trigger UI validation + DB constraint.")]
    public bool? Mandatory { get; init; }

    [Description("Allow editing after the row is created. Default true.")]
    public bool? AllowEdit { get; init; }

    [Description("Allow setting during the initial create. Default true. Setting false makes the field server-set-only.")]
    public bool? AllowEditOnCreate { get; init; }

    [Description("Whether the field surfaces in default-generated forms. Default true.")]
    public bool? Visible { get; init; }

    [Description("Compliance/PII classification. Free-form string — common values: 'Customer Content', 'System Metadata', 'End User Identifiable Information' (EUII).")]
    public string? AssetClassification { get; init; }

    [Description("GDPR classification: 'Customer Content', 'End User Identifiable Information', 'End User Pseudonymous Identifiers', 'Organization Identifiable Information', 'Support Data', 'System Metadata'.")]
    public string? GeneralDataProtectionRegulation { get; init; }

    [Description("Restricts the field by configuration key.")]
    public string? ConfigurationKey { get; init; }

    [Description("Comma-separated ISO country codes that restrict where this field is visible.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Marks the field obsolete.")]
    public bool? IsObsolete { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Max characters for FieldType=String when not inherited from an EDT. F&O defaults: 10 (short id), 20 (id), 60 (medium), 250 (longer text).")]
    public int? StringSize { get; init; }

    [Description("Text alignment for FieldType=String when overriding the EDT default: Auto / Left / Right / Center.")]
    public Alignment? Adjustment { get; init; }

    [Description("Scale for FieldType=Real (decimal places). Use only to override an EDT default.")]
    public int? Scale { get; init; }

    [Description("Gates the field behind a feature (FeatureClass name). The field is only active when that feature is on.")]
    public string? FeatureClass { get; init; }

    [Description("For FieldType=Container: whether the container's contents are persisted. Default Yes.")]
    public bool? SaveContents { get; init; }

    [Description("Relation context (the relation whose role qualifies this field). Rare.")]
    public string? RelationContext { get; init; }

    [Description("Cross-company data-sharing policy for the field: Duplicate / Explicit / Never.")]
    public string? SysSharingType { get; init; }

    [Description("Whether the column is nullable at the SQL level. Default No.")]
    public bool? Null { get; init; }

    [Description("Advanced / less-common field properties: AliasFor, IgnoreEDTRelation, GroupPrompt, etc. See AdvancedFieldOptions.")]
    public AdvancedFieldOptions? Advanced { get; init; }
}

public sealed record AdvancedFieldOptions
{
    [Description("Field name this field aliases (alternate access path).")]
    public string? AliasFor { get; init; }

    [Description("Set true to suppress the EDT-level relation on this field. The table-level relation takes over. This is the modern F&O pattern.")]
    public bool? IgnoreEDTRelation { get; init; }

    [Description("Form-control prompt label override.")]
    public string? GroupPrompt { get; init; }

    [Description("Field added by tooling, not manually. Default false.")]
    public bool? IsSystemGenerated { get; init; }

    [Description("Edit window where the field is persisted differently (e.g. correction flow).")]
    public string? CorrectionFlagField { get; init; }

    [Description("Country context for country-conditional fields.")]
    public string? CountryRegionContextField { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TableFieldType { String, Int, Int64, Real, Date, Time, UtcDateTime, Enum, Guid, Container }

// ---- Indexes ----------------------------------------------------------------

public sealed record TableIndex
{
    [Description("Index name. PascalCase. Unique within the table.")]
    public string Name { get; init; } = string.Empty;

    [Description("If true, duplicate key combinations are allowed. False = unique index. Default true.")]
    public bool? AllowDuplicates { get; init; }

    [Description("Whether SQL Server may take page-level locks on this index. Default true.")]
    public bool? AllowPageLocks { get; init; }

    [Description("Marks this as a secondary alternate key (replacement-key candidate).")]
    public bool? AlternateKey { get; init; }

    [Description("Restricts the index by configuration key.")]
    public string? ConfigurationKey { get; init; }

    [Description("Whether the index is materialized. Default true.")]
    public bool? Enabled { get; init; }

    [Description("Index kind: NormalIndex / FullTextIndex.")]
    public string? IndexType { get; init; }

    [Description("Ordered list of fields that make up the index key. Use IncludedColumn=true on a field to put it in the index's INCLUDE list (covering index pattern).")]
    public List<TableIndexField>? Fields { get; init; }
}

public sealed record TableIndexField
{
    [Description("Name of the field on this table.")]
    public string DataField { get; init; } = string.Empty;

    [Description("If true, the field is in the SQL INCLUDE list rather than the index key. Used for covering indexes.")]
    public bool? IncludedColumn { get; init; }

    [Description("Optional component (lower priority for index selection).")]
    public bool? Optional { get; init; }
}

// ---- Relations --------------------------------------------------------------

public sealed record TableRelation
{
    [Description("Relation name. PascalCase. Unique within the table.")]
    public string Name { get; init; } = string.Empty;

    [Description("Name of the related (referenced) table.")]
    public string? RelatedTable { get; init; }

    [Description("Relation kind: Association / Composition / Aggregation. Composition = strong ownership, related rows die with this one.")]
    public string? RelationshipType { get; init; }

    [Description("Cardinality of this side: ZeroOne / ExactlyOne / ZeroMore / OneMore.")]
    public Cardinality? Cardinality { get; init; }

    [Description("Cardinality of the related side.")]
    public Cardinality? RelatedTableCardinality { get; init; }

    [Description("Role label for this side in the relationship (used in entity-relationship diagrams and navigation methods).")]
    public string? Role { get; init; }

    [Description("Role label for the related side.")]
    public string? RelatedTableRole { get; init; }

    [Description("Delete action: None / Cascade / Restricted / CascadeOnDelete.")]
    public OnDeleteAction? OnDelete { get; init; }

    [Description("Whether the relation participates in input validation. Default true.")]
    public bool? Validate { get; init; }

    [Description("Set true to emit AxTableRelationForeignKey (the modern F&O shape) instead of the legacy AxTableRelation.")]
    public bool? IsForeignKey { get; init; }

    [Description("Whether to auto-generate navigation property methods on the table for this relation.")]
    public bool? CreateNavigationPropertyMethods { get; init; }

    [Description("Whether the relation uses default role names (vs. explicit Role / RelatedTableRole). Defaults to true on the metaclass; set false when you author explicit roles. Round-trips faithfully via xpp_get_table / xpp_get_table_extension.")]
    public bool? UseDefaultRoleNames { get; init; }

    [Description("Name of an index that this relation aligns with (for performance hints).")]
    public string? Index { get; init; }

    [Description("EDT relation reference (the EDT this relation derives from, when applicable).")]
    public string? EDTRelation { get; init; }

    [Description("Entity-relationship role label (@Label or text) surfaced in the ER model.")]
    public string? EntityRelationshipRole { get; init; }

    [Description("Override the generated navigation-property method name for this relation.")]
    public string? NavigationPropertyMethodNameOverride { get; init; }

    [Description("Name of the key (e.g. a replacement/alternate key) this relation targets on the related table.")]
    public string? Key { get; init; }

    [Description("Join predicate. Each constraint is one of Field (FieldType.Field — column-to-column), Fixed (constant value on this side), or RelatedFixed (constant on the other side).")]
    public List<RelationConstraint>? Constraints { get; init; }
}

public sealed record RelationConstraint
{
    [Description("Constraint name. Conventionally PascalCase + describes what it joins.")]
    public string Name { get; init; } = string.Empty;

    [Description("Constraint shape: Field (this.Field = related.RelatedField), Fixed (this.Field = Value), RelatedFixed (related.RelatedField = Value).")]
    public ConstraintType Type { get; init; }

    [Description("Field on THIS table. Required for Type=Field and Type=Fixed.")]
    public string? Field { get; init; }

    [Description("Field on the RELATED table. Required for Type=Field and Type=RelatedFixed.")]
    public string? RelatedField { get; init; }

    [Description("Numeric/integer fixed value. Use for Type=Fixed / Type=RelatedFixed when the value is an integer or enum ordinal.")]
    public string? Value { get; init; }

    [Description("String fixed value. Use for Type=Fixed / Type=RelatedFixed when the value is a string.")]
    public string? ValueStr { get; init; }

    [Description("EDT name when the constraint derives from an EDT relation.")]
    public string? SourceEDT { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConstraintType { Field, Fixed, RelatedFixed, RelatedTable, Table }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Cardinality { ZeroOne, ExactlyOne, ZeroMore, OneMore }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OnDeleteAction { None, Cascade, Restricted, CascadeOnDelete }

// ---- Field groups -----------------------------------------------------------

public sealed record TableFieldGroup
{
    [Description("Field-group name. PascalCase. Common names: AutoReport, AutoLookup, AutoSummary, AutoBrowse, AutoIdentification.")]
    public string Name { get; init; } = string.Empty;

    [Description("Display label for the group.")]
    public string? Label { get; init; }

    [Description("If true, the platform auto-fills this group with relevant fields. Typical for AutoIdentification.")]
    public bool? AutoPopulate { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Ordered list of field names in the group.")]
    public List<string>? Fields { get; init; }
}

// ---- Delete actions ---------------------------------------------------------

public sealed record TableDeleteAction
{
    [Description("Action name. Conventionally describes the related table or relation.")]
    public string? Name { get; init; }

    [Description("What happens to dependent rows on the related table when a row in this table is deleted: None / Cascade / Restricted / CascadeOnDelete.")]
    public OnDeleteAction? DeleteAction { get; init; }

    [Description("Name of the relation on the related table whose direction matches this delete action.")]
    public string? Relation { get; init; }

    [Description("Related table name.")]
    public string? Table { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

// ---- Source code ------------------------------------------------------------

public sealed record TableSourceCode
{
    [Description("Class declaration block. Default: 'public class <Name> extends common {}'. Custom declarations preserve usings and field declarations.")]
    public string? Declaration { get; init; }

    [Description("Method definitions. Each entry has a method name and an opaque X++ source body (signature + body). The mapper preserves these verbatim — no X++ parsing.")]
    public List<TableMethod>? Methods { get; init; }
}

public sealed record TableMethod
{
    [Description("Method name. Must match the method name parsed from Source.")]
    public string Name { get; init; } = string.Empty;

    [Description("Full X++ method source, including signature and body. Preserved verbatim through round-trip.")]
    public string? Source { get; init; }
}

// ---- Subscriber access ------------------------------------------------------

public sealed record SubscriberAccessLevel
{
    [Description("Read access: Allow / Deny.")]
    public AccessLevel? Read { get; init; }
    public AccessLevel? Create { get; init; }
    public AccessLevel? Update { get; init; }
    public AccessLevel? Delete { get; init; }
    public AccessLevel? Correct { get; init; }
    public AccessLevel? Invoke { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessLevel { Allow, Deny }

// ---- Advanced table options -------------------------------------------------

public sealed record AdvancedTableOptions
{
    public bool? AllowChangeTracking { get; init; }
    public bool? AllowRowVersionChangeTracking { get; init; }
    public string? AosAuthorization { get; init; }
    public bool? AllowArchival { get; init; }
    public bool? AllowOverride { get; init; }
    public bool? AllowRetention { get; init; }
    public bool? DisableDatabaseLogging { get; init; }
    public bool? DisableLockEscalation { get; init; }
    public string? Durability { get; init; }
    public string? EntityRelationshipType { get; init; }
    public string? InstanceRelationType { get; init; }
    public bool? OccEnabled { get; init; }
    public string? ReportRef { get; init; }
    public string? SearchLinkRefName { get; init; }
    public string? SearchLinkRefType { get; init; }
    public string? StorageMode { get; init; }
    public bool? SupportInheritance { get; init; }
    public bool? SystemTable { get; init; }
    public string? ValidTimeStateFieldType { get; init; }
    public Visibility Visibility { get; init; } = Visibility.Public;
    public bool? Visible { get; init; }
    public bool? Abstract { get; init; }
    public string? DataSharingType { get; init; }
}

// ---- Common enums -----------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TableType { Regular, TempDB, InMemory }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TableContents { Default, BaseData, BaseDataLocked }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheLookup { None, NotInTTS, Found, FoundAndEmpty, EntireTable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Alignment { Auto, Left, Right, Center }
