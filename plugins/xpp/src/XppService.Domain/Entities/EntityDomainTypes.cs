using System.ComponentModel;
using System.Text.Json.Serialization;
using Xpp.Service.Domain.Queries; // reuse QueryDataSource for ViewMetadata.DataSources
using Xpp.Service.Domain.Tables; // reuse TableFieldGroup for AxTableFieldGroup
using Xpp.Service.Domain.Views;  // reuse Views.SubscriberAccessLevel / ViewAccessLevel

namespace Xpp.Service.Domain.Entities;

/// <summary>
/// Domain shape for AxDataEntityView authoring. Data entities are
/// the OData/data-management writable layer over a table or join of
/// tables. Three-tier inheritance at the metamodel level
/// (AxDataEntity → AxDataEntityViewBase → AxDataEntityView), with
/// shape that mostly overlaps AxView (fields, indexes-via-keys,
/// relations, field groups, view metadata) plus entity-specific
/// scalars (public name/collection name, set-based operations,
/// data-management staging, primary key/company context).
///
/// Polymorphism lives on the Fields collection: each entry is either
/// Mapped (DataField+DataSource into the backing query/data sources)
/// or Unmapped<Type> (X++-method computed value, per-primitive).
///
/// Pragmatic 80% scope. The AxDataEntityViewReference family
/// (nested entity composition for parent-child entities) is deferred
/// to the xpp_update_object escape hatch.
/// </summary>
public sealed record CreateEntityRequest
{
    [Description("Data entity's AOT name. PascalCase. Must be unique within the model AND must match the file name on disk AND the class declaration in SourceCode.Declaration. Conventionally ends with 'Entity' (e.g. CustomerEntity).")]
    public string Name { get; init; } = string.Empty;

    [Description("Display label.")]
    public string? Label { get; init; }

    [Description("Singular form of the label.")]
    public string? SingularLabel { get; init; }

    [Description("Developer-facing description. Label ref preferred.")]
    public string? DeveloperDocumentation { get; init; }

    [Description("AxQuery this entity derives from (optional — many DE views inline the data sources into ViewMetadata.DataSources rather than referencing a separate query).")]
    public string? Query { get; init; }

    // ---- AxDataEntityViewBase scalars ----------------------------------

    [Description("OData public entity name (singular). Used in the URL: /data/<PublicEntityName>.")]
    public string? PublicEntityName { get; init; }

    [Description("OData collection name (plural). Used in the URL: /data/<PublicCollectionName>.")]
    public string? PublicCollectionName { get; init; }

    [Description("Name of the key (in Keys[]) that serves as the entity's OData primary key. Used for natural-key resolution.")]
    public string? PrimaryKey { get; init; }

    [Description("Field name that provides the company-discriminator context for cross-company queries. Default DataAreaId.")]
    public string? PrimaryCompanyContext { get; init; }

    [Description("Marks the entity public — exposed over OData / DMF. Default true for entities meant to be consumed externally.")]
    public bool? IsPublic { get; init; }

    [Description("If true, the entity is read-only — no writes allowed. Used for OData reporting views.")]
    public bool? IsReadOnly { get; init; }

    [Description("Whether the entity is exposed via the Data Management Framework (DMF) for import/export. Default true when the entity is meant for staging/import.")]
    public bool? DataManagementEnabled { get; init; }

    [Description("DMF staging table name. Required when DataManagementEnabled=true. Conventionally <EntityName>Staging.")]
    public string? DataManagementStagingTable { get; init; }

    [Description("Whether the entity supports set-based SQL operations (bulk insert/update/delete) instead of row-by-row.")]
    public bool? SupportsSetBasedSqlOperations { get; init; }

    [Description("Whether set-based SQL ops are enabled at this entity (must also support per above).")]
    public bool? EnableSetBasedSqlOperations { get; init; }

    [Description("Entity classification: Master, Reference, Document, Parameters, Transaction. Drives priority in DMF projects.")]
    public string? EntityCategory { get; init; }

    [Description("AOS-side authorization mode override.")]
    public string? AosAuthorization { get; init; }

    [Description("Messaging role for service-bus integration.")]
    public string? MessagingRole { get; init; }

    [Description("Module ownership. Comma-separated functional area names.")]
    public string? Modules { get; init; }

    [Description("Whether the entity participates in valid-time-state queries.")]
    public bool? ValidTimeStateEnabled { get; init; }

    [Description("Whether the entity supports archival.")]
    public bool? AllowArchival { get; init; }

    [Description("Whether the entity supports row-version change tracking.")]
    public bool? AllowRowVersionChangeTracking { get; init; }

    [Description("Whether the entity supports retention policies.")]
    public bool? AllowRetention { get; init; }

    [Description("Auto-create the entity in Dataverse on deployment.")]
    public bool? AutoCreateDataverse { get; init; }

    [Description("Expose the entity to Dataverse search.")]
    public bool? EnableDataverseSearch { get; init; }

    // ---- AxDataEntity (base) scalars ----------------------------------

    [Description("AOT cross-reference grouping: Main / Reference / Group / Setup / Transaction / etc.")]
    public string? TableGroup { get; init; }

    [Description("Field name used as the primary title in lookups.")]
    public string? TitleField1 { get; init; }

    [Description("Secondary title field.")]
    public string? TitleField2 { get; init; }

    [Description("Restricts entity visibility to a configuration key.")]
    public string? ConfigurationKey { get; init; }

    [Description("Comma-separated ISO country codes that restrict where this entity is visible.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Marks the entity obsolete.")]
    public bool? IsObsolete { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Reference form for record lookups.")]
    public string? FormRef { get; init; }

    [Description("Reference list page.")]
    public string? ListPageRef { get; init; }

    [Description("Reference preview part.")]
    public string? PreviewPartRef { get; init; }

    [Description("Cross-company / sharing scope.")]
    public string? OperationalDomain { get; init; }

    [Description("Reference report.")]
    public string? ReportRef { get; init; }

    [Description("Entity-relationship-diagram type.")]
    public string? EntityRelationshipType { get; init; }

    [Description("Per-operation access for cross-company subscribers.")]
    public Views.SubscriberAccessLevel? SubscriberAccessLevel { get; init; }

    // ---- Source code + collections ------------------------------------

    [Description("X++ source: Declaration + Methods. Entity classes typically override insertEntityDataSource / updateEntityDataSource / deleteEntityDataSource for write-through customization. Methods are opaque text preserved verbatim.")]
    public EntitySourceCode? SourceCode { get; init; }

    [Description("Entity columns. Each entry is either Mapped (writes back to a DataField on a DataSource in the backing query/ViewMetadata.DataSources) or Unmapped<Type> (X++-method computed value).")]
    public List<EntityField>? Fields { get; init; }

    [Description("Field groups — reuses the AxTableFieldGroup shape (literally the same on-disk element). Conventional names: AutoReport, AutoLookup, AutoIdentification, AutoSummary, AutoBrowse.")]
    public List<TableFieldGroup>? FieldGroups { get; init; }

    [Description("Natural keys for entity identity in OData. Conventionally one named 'EntityKey' that serves as PrimaryKey. Each key composes one or more field references.")]
    public List<EntityKey>? Keys { get; init; }

    [Description("WHERE-clause range predicates applied to the entity (independent of the backing query's ranges).")]
    public List<EntityRange>? Ranges { get; init; }

    [Description("Relations from this entity to other tables/entities. Same constraint subtypes as AxTable relations (Field / Fixed / RelatedFixed).")]
    public List<EntityRelation>? Relations { get; init; }

    [Description("Delete actions on related tables.")]
    public List<EntityDeleteAction>? DeleteActions { get; init; }

    [Description("Designer-helper metadata block. Unlike AxView, DE views often DO populate ViewMetadata.DataSources with the backing query's data-source tree. Reuses the AxQuery QueryDataSource shape (typically a Root with nested Embedded joins). If omitted, the mapper emits a minimal shell.")]
    public EntityViewMetadata? ViewMetadata { get; init; }

    [Description("Advanced / less-common scalar properties.")]
    public AdvancedEntityOptions? Advanced { get; init; }
}

public sealed record PatchEntityRequest
{
    public string? Label { get; init; }
    public string? SingularLabel { get; init; }
    public string? DeveloperDocumentation { get; init; }
    public string? Query { get; init; }
    public string? PublicEntityName { get; init; }
    public string? PublicCollectionName { get; init; }
    public string? PrimaryKey { get; init; }
    public string? PrimaryCompanyContext { get; init; }
    public bool? IsPublic { get; init; }
    public bool? IsReadOnly { get; init; }
    public bool? DataManagementEnabled { get; init; }
    public string? DataManagementStagingTable { get; init; }
    public bool? SupportsSetBasedSqlOperations { get; init; }
    public bool? EnableSetBasedSqlOperations { get; init; }
    public string? EntityCategory { get; init; }
    public string? AosAuthorization { get; init; }
    public string? MessagingRole { get; init; }
    public string? Modules { get; init; }
    public bool? ValidTimeStateEnabled { get; init; }
    public bool? AllowArchival { get; init; }
    public bool? AllowRowVersionChangeTracking { get; init; }
    public bool? AllowRetention { get; init; }
    public bool? AutoCreateDataverse { get; init; }
    public bool? EnableDataverseSearch { get; init; }
    public string? TableGroup { get; init; }
    public string? TitleField1 { get; init; }
    public string? TitleField2 { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? FormRef { get; init; }
    public string? ListPageRef { get; init; }
    public string? PreviewPartRef { get; init; }
    public string? OperationalDomain { get; init; }
    public string? ReportRef { get; init; }
    public string? EntityRelationshipType { get; init; }
    public Views.SubscriberAccessLevel? SubscriberAccessLevel { get; init; }
    public EntitySourceCode? SourceCode { get; init; }
    public List<EntityField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<EntityKey>? Keys { get; init; }
    public List<EntityRange>? Ranges { get; init; }
    public List<EntityRelation>? Relations { get; init; }
    public List<EntityDeleteAction>? DeleteActions { get; init; }
    public EntityViewMetadata? ViewMetadata { get; init; }
    public AdvancedEntityOptions? Advanced { get; init; }
}

public sealed record GetEntityResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? SingularLabel { get; init; }
    public string? DeveloperDocumentation { get; init; }
    public string? Query { get; init; }
    public string? PublicEntityName { get; init; }
    public string? PublicCollectionName { get; init; }
    public string? PrimaryKey { get; init; }
    public string? PrimaryCompanyContext { get; init; }
    public bool? IsPublic { get; init; }
    public bool? IsReadOnly { get; init; }
    public bool? DataManagementEnabled { get; init; }
    public string? DataManagementStagingTable { get; init; }
    public bool? SupportsSetBasedSqlOperations { get; init; }
    public bool? EnableSetBasedSqlOperations { get; init; }
    public string? EntityCategory { get; init; }
    public string? AosAuthorization { get; init; }
    public string? MessagingRole { get; init; }
    public string? Modules { get; init; }
    public bool? ValidTimeStateEnabled { get; init; }
    public bool? AllowArchival { get; init; }
    public bool? AllowRowVersionChangeTracking { get; init; }
    public bool? AllowRetention { get; init; }
    public bool? AutoCreateDataverse { get; init; }
    public bool? EnableDataverseSearch { get; init; }
    public string? TableGroup { get; init; }
    public string? TitleField1 { get; init; }
    public string? TitleField2 { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? FormRef { get; init; }
    public string? ListPageRef { get; init; }
    public string? PreviewPartRef { get; init; }
    public string? OperationalDomain { get; init; }
    public string? ReportRef { get; init; }
    public string? EntityRelationshipType { get; init; }
    public Views.SubscriberAccessLevel? SubscriberAccessLevel { get; init; }
    public EntitySourceCode? SourceCode { get; init; }
    public List<EntityField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<EntityKey>? Keys { get; init; }
    public List<EntityRange>? Ranges { get; init; }
    public List<EntityRelation>? Relations { get; init; }
    public List<EntityDeleteAction>? DeleteActions { get; init; }
    public EntityViewMetadata? ViewMetadata { get; init; }
    public AdvancedEntityOptions? Advanced { get; init; }
}

public sealed record AdvancedEntityOptions
{
    public Visibility Visibility { get; init; } = Visibility.Public;
    public bool? Visible { get; init; }
}

public sealed record EntitySourceCode
{
    [Description("X++ class declaration.")]
    public string? Declaration { get; init; }

    [Description("X++ methods. Entity classes typically override insertEntityDataSource / updateEntityDataSource / deleteEntityDataSource. Opaque source preserved verbatim.")]
    public List<EntityMethod>? Methods { get; init; }
}

public sealed record EntityMethod
{
    public string Name { get; init; } = string.Empty;
    public string? Source { get; init; }
}

// ---- Entity fields (polymorphic on Kind) -----------------------------------

public sealed record EntityField
{
    [Description("Column name as exposed by the entity (PascalCase). Used in OData URLs (case-preserved).")]
    public string Name { get; init; } = string.Empty;

    [Description("Field kind. Mapped: writes back to a DataField on a DataSource in the backing query / ViewMetadata.DataSources. Unmapped<Type>: synthesized by an X++ method.")]
    public EntityFieldKind Kind { get; init; }

    [Description("Display label.")]
    public string? Label { get; init; }

    [Description("Help text.")]
    public string? HelpText { get; init; }

    [Description("Group-prompt label override.")]
    public string? GroupPrompt { get; init; }

    [Description("Access modifier: Public / Private / Internal / Protected.")]
    public string? AccessModifier { get; init; }

    [Description("If true, the field must be populated for write-back. Default false.")]
    public bool? Mandatory { get; init; }

    [Description("Allow editing after the row is created. Default true.")]
    public bool? AllowEdit { get; init; }

    [Description("Allow setting during the initial create.")]
    public bool? AllowEditOnCreate { get; init; }

    [Description("Restricts the field by configuration key.")]
    public string? ConfigurationKey { get; init; }

    [Description("Country-code restriction.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Country-context field.")]
    public string? CountryRegionContextField { get; init; }

    [Description("Feature-class binding for runtime feature-flag gating.")]
    public string? FeatureClass { get; init; }

    [Description("Relation context override.")]
    public string? RelationContext { get; init; }

    [Description("Marks the field obsolete.")]
    public bool? IsObsolete { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    // ---- Kind=Mapped ----

    [Description("For Kind=Mapped: the field name on the data source's table. Required when Kind=Mapped.")]
    public string? DataField { get; init; }

    [Description("For Kind=Mapped: the data-source name in the backing query / ViewMetadata.DataSources. Required when Kind=Mapped.")]
    public string? DataSource { get; init; }

    [Description("Aggregation on the mapped field (Sum / Avg / Min / Max / Count / GroupBy). Only valid when Kind=Mapped.")]
    public string? Aggregation { get; init; }

    [Description("Field used as the legal-entity context when the entity surfaces financial-dimension columns. Mapped-only.")]
    public string? DimensionLegalEntityContextField { get; init; }

    [Description("Field that enumerates the dimensions exposed on the entity (financial-dimension scenarios). Mapped-only.")]
    public string? DynamicDimensionEnumerationField { get; init; }

    [Description("Expose this mapped column to Dataverse search.")]
    public bool? EnableDataverseSearch { get; init; }

    // ---- Kind=Unmapped<Type> ----

    [Description("For Kind=Unmapped<Type>: name of the X++ method that returns the computed value. Required when Kind starts with 'Unmapped'.")]
    public string? ComputedFieldMethod { get; init; }

    [Description("For Kind=Unmapped<Type>: EDT typing the field.")]
    public string? ExtendedDataType { get; init; }

    [Description("For Kind=Unmapped<Type>: whether the value is computed at query time (true) or set explicitly by the consumer (false).")]
    public bool? IsComputedField { get; init; }

    [Description("AxEnum name typing the column when Kind=UnmappedEnum. Required for UnmappedEnum.")]
    public string? EnumType { get; init; }

    // ---- Kind=UnmappedString only ----

    [Description("Max characters when Kind=UnmappedString.")]
    public int? StringSize { get; init; }

    [Description("Text alignment when Kind=UnmappedString.")]
    public string? Adjustment { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EntityFieldKind
{
    Mapped,
    UnmappedString,
    UnmappedInt,
    UnmappedInt64,
    UnmappedReal,
    UnmappedDate,
    UnmappedEnum,
    UnmappedUtcDateTime,
    UnmappedTime,
    UnmappedGuid,
    UnmappedContainer,
}

// ---- Keys -----------------------------------------------------------

public sealed record EntityKey
{
    [Description("Key name. Conventionally 'EntityKey' for the primary natural key.")]
    public string Name { get; init; } = string.Empty;

    [Description("Tags string.")]
    public string? Tags { get; init; }

    [Description("Ordered list of field references making up the composite key.")]
    public List<EntityKeyField>? Fields { get; init; }
}

public sealed record EntityKeyField
{
    [Description("Name of the entity column.")]
    public string DataField { get; init; } = string.Empty;
}

// ---- Ranges ----------------------------------------------------------

public sealed record EntityRange
{
    [Description("Range name.")]
    public string Name { get; init; } = string.Empty;

    [Description("Field this range filters.")]
    public string? Field { get; init; }

    [Description("Range expression / value. Same conventions as AxQuery range Value (opaque to the mapper).")]
    public string? Value { get; init; }

    [Description("Range status: Open / Locked / Hidden.")]
    public RangeStatus? Status { get; init; }

    [Description("Whether the range is active. Default true.")]
    public bool? Enabled { get; init; }

    [Description("Display label.")]
    public string? Label { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

// ---- Relations -------------------------------------------------------

public sealed record EntityRelation
{
    [Description("Relation name.")]
    public string Name { get; init; } = string.Empty;

    [Description("Related table name.")]
    public string? RelatedTable { get; init; }

    [Description("Relation kind: Association / Composition / Aggregation.")]
    public string? RelationshipType { get; init; }

    [Description("Cardinality of this side: ZeroOne / ExactlyOne / ZeroMore / OneMore.")]
    public string? Cardinality { get; init; }

    [Description("Cardinality of the related side.")]
    public string? RelatedTableCardinality { get; init; }

    [Description("Role label for this side.")]
    public string? Role { get; init; }

    [Description("Role label for the related side.")]
    public string? RelatedTableRole { get; init; }

    [Description("Whether to use default-generated role names.")]
    public bool? UseDefaultRoleNames { get; init; }

    [Description("Related DATA ENTITY name (entity-to-entity navigation). Entities relate to other entities via this, not RelatedTable.")]
    public string? RelatedDataEntity { get; init; }

    [Description("Role label for the related data entity.")]
    public string? RelatedDataEntityRole { get; init; }

    [Description("Cardinality of the related data entity: ZeroOne / ExactlyOne / ZeroMore / OneMore.")]
    public string? RelatedDataEntityCardinality { get; init; }

    [Description("Name of the key on the related entity this relation targets.")]
    public string? Key { get; init; }

    [Description("If true, emit AxDataEntityViewRelationForeignKey instead of the base AxDataEntityViewRelation.")]
    public bool? IsForeignKey { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Join predicates. Each constraint is Field / Fixed / RelatedFixed.")]
    public List<EntityRelationConstraint>? Constraints { get; init; }
}

public sealed record EntityRelationConstraint
{
    [Description("Constraint name.")]
    public string Name { get; init; } = string.Empty;

    [Description("Constraint shape: Field / Fixed / RelatedFixed.")]
    public EntityConstraintType Type { get; init; }

    [Description("Field on THIS entity (Field / Fixed).")]
    public string? Field { get; init; }

    [Description("Field on the RELATED table (Field / RelatedFixed).")]
    public string? RelatedField { get; init; }

    [Description("Fixed value (Fixed / RelatedFixed).")]
    public string? Value { get; init; }

    [Description("EDT source.")]
    public string? SourceEDT { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EntityConstraintType { Field, Fixed, RelatedFixed }

// ---- Delete actions --------------------------------------------------

public sealed record EntityDeleteAction
{
    [Description("Action name.")]
    public string? Name { get; init; }

    [Description("What happens to dependent rows on the related table.")]
    public string? DeleteAction { get; init; }

    [Description("Name of the relation on the related table.")]
    public string? Relation { get; init; }

    [Description("Related table name.")]
    public string? Table { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

// ---- View metadata (with AxQuery-shaped data sources) --------------

public sealed record EntityViewMetadata
{
    [Description("Always 'Metadata' in MS-shipped entities.")]
    public string? Name { get; init; }

    [Description("Designer-side methods. Typically a single classDeclaration carrying the [Query] attribute, identical to an AxQuery's classDeclaration.")]
    public List<EntityMethod>? Methods { get; init; }

    [Description("Data-source tree for the entity. Reuses the AxQuery QueryDataSource shape — typically a single Root with nested Embedded joins. Same recursion as AxQuery.")]
    public List<QueryDataSource>? DataSources { get; init; }
}
