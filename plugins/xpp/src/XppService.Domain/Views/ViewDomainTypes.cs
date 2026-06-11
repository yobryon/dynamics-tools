using System.ComponentModel;
using System.Text.Json.Serialization;
using Xpp.Service.Domain.Tables; // reuse TableFieldGroup (on-disk element is AxTableFieldGroup)

namespace Xpp.Service.Domain.Views;

/// <summary>
/// Domain shape for AxView authoring. A view is essentially a stored
/// AxQuery (referenced by name via the Query property) plus a set of
/// promoted/computed view columns and SQL-view metadata. Inherits
/// from AxDataEntity at the metamodel level (same as AxTable), so
/// the canonical scalar order follows the two-tier base/derived
/// pattern established by AxTable.
///
/// On-disk root is non-polymorphic (just &lt;AxView&gt;). Polymorphism
/// lives on the child Fields collection — view fields are either
/// AxViewFieldBound (projecting a field from a query data source)
/// or AxViewFieldComputed&lt;Type&gt; (calculated via an X++ method).
/// </summary>
public sealed record CreateViewRequest
{
    [Description("The view's AOT name. PascalCase. Must be unique within the model AND must match the file name on disk AND the class declaration in SourceCode.Declaration.")]
    public string Name { get; init; } = string.Empty;

    [Description("Display label (label ref @File:Id preferred).")]
    public string? Label { get; init; }

    [Description("Singular form of the label.")]
    public string? SingularLabel { get; init; }

    [Description("Developer-facing description. Label ref preferred.")]
    public string? DeveloperDocumentation { get; init; }

    [Description("Name of the AxQuery this view derives from. Required for non-trivial views — the query owns the data-source tree, joins, and ranges; the view layers field projections and metadata on top.")]
    public string? Query { get; init; }

    [Description("AOT cross-reference grouping: Main / Reference / Group / Setup / Transaction / etc. Same vocabulary as AxTable.TableGroup.")]
    public string? TableGroup { get; init; }

    [Description("Whether the view is publicly accessible (vs internal). Default true.")]
    public bool? IsPublic { get; init; }

    [Description("Whether the view is staged (materialized) vs computed live. Default false.")]
    public bool? IsStaged { get; init; }

    [Description("Whether updates are allowed against this view. Default false (views are typically read-only).")]
    public bool? Updatable { get; init; }

    [Description("Whether the view participates in valid-time-state queries.")]
    public bool? ValidTimeStateEnabled { get; init; }

    [Description("Collection name used in OData / data-management contexts (plural form).")]
    public string? CollectionName { get; init; }

    [Description("Name of an index that uniquely identifies a row outside of RecId.")]
    public string? ReplacementKey { get; init; }

    [Description("AOS-side authorization mode. Mirrors AxTable.AosAuthorization.")]
    public string? AosAuthorization { get; init; }

    [Description("Messaging role for service-bus integration. Rarely set.")]
    public string? MessagingRole { get; init; }

    [Description("Version string for view-versioning scenarios.")]
    public string? Version { get; init; }

    [Description("Field name used as the primary title/identifier in lookups and forms.")]
    public string? TitleField1 { get; init; }

    [Description("Secondary title field.")]
    public string? TitleField2 { get; init; }

    [Description("Restricts view visibility to a configuration key.")]
    public string? ConfigurationKey { get; init; }

    [Description("Comma-separated ISO country codes that restrict where this view is visible.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Marks the view obsolete.")]
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

    [Description("Per-operation access for cross-company subscribers. Same shape as on AxTable.")]
    public SubscriberAccessLevel? SubscriberAccessLevel { get; init; }

    [Description("X++ source: Declaration (typically 'public class <Name> extends common {}') + Methods. Methods on a view are rare — most views are pure metadata.")]
    public ViewSourceCode? SourceCode { get; init; }

    [Description("Projected view columns. Each entry is either Bound (DataField+DataSource into the backing query) or Computed (X++ method synthesizes the value).")]
    public List<ViewField>? Fields { get; init; }

    [Description("Indexes on this view. Similar shape to AxTable indexes — Name + Fields + AllowDuplicates/AlternateKey/Enabled.")]
    public List<ViewIndex>? Indexes { get; init; }

    [Description("Relations from this view to other tables/views. Similar shape to AxTable relations with constraint subtypes (Field / Fixed / RelatedFixed).")]
    public List<ViewRelation>? Relations { get; init; }

    [Description("Field groups — same shape as AxTable field groups (the on-disk element is AxTableFieldGroup, shared between tables and views). Common names: AutoReport, AutoLookup, AutoIdentification, AutoSummary, AutoBrowse.")]
    public List<TableFieldGroup>? FieldGroups { get; init; }

    [Description("Designer-helper metadata block. Usually omitted — the mapper emits an empty <ViewMetadata> shell on write because MS's serializer requires it.")]
    public ViewDesignerMetadata? ViewMetadata { get; init; }

    [Description("Advanced / less-common scalar properties.")]
    public AdvancedViewOptions? Advanced { get; init; }
}

public sealed record PatchViewRequest
{
    public string? Label { get; init; }
    public string? SingularLabel { get; init; }
    public string? DeveloperDocumentation { get; init; }
    public string? Query { get; init; }
    public string? TableGroup { get; init; }
    public bool? IsPublic { get; init; }
    public bool? IsStaged { get; init; }
    public bool? Updatable { get; init; }
    public bool? ValidTimeStateEnabled { get; init; }
    public string? CollectionName { get; init; }
    public string? ReplacementKey { get; init; }
    public string? AosAuthorization { get; init; }
    public string? MessagingRole { get; init; }
    public string? Version { get; init; }
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
    public SubscriberAccessLevel? SubscriberAccessLevel { get; init; }
    public ViewSourceCode? SourceCode { get; init; }
    public List<ViewField>? Fields { get; init; }
    public List<ViewIndex>? Indexes { get; init; }
    public List<ViewRelation>? Relations { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public ViewDesignerMetadata? ViewMetadata { get; init; }
    public AdvancedViewOptions? Advanced { get; init; }
}

public sealed record GetViewResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? SingularLabel { get; init; }
    public string? DeveloperDocumentation { get; init; }
    public string? Query { get; init; }
    public string? TableGroup { get; init; }
    public bool? IsPublic { get; init; }
    public bool? IsStaged { get; init; }
    public bool? Updatable { get; init; }
    public bool? ValidTimeStateEnabled { get; init; }
    public string? CollectionName { get; init; }
    public string? ReplacementKey { get; init; }
    public string? AosAuthorization { get; init; }
    public string? MessagingRole { get; init; }
    public string? Version { get; init; }
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
    public SubscriberAccessLevel? SubscriberAccessLevel { get; init; }
    public ViewSourceCode? SourceCode { get; init; }
    public List<ViewField>? Fields { get; init; }
    public List<ViewIndex>? Indexes { get; init; }
    public List<ViewRelation>? Relations { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public ViewDesignerMetadata? ViewMetadata { get; init; }
    public AdvancedViewOptions? Advanced { get; init; }
}

public sealed record AdvancedViewOptions
{
    public Visibility Visibility { get; init; } = Visibility.Public;
    public bool? Visible { get; init; }
}

public sealed record ViewSourceCode
{
    [Description("X++ class declaration. Default: 'public class <Name> extends common {}'.")]
    public string? Declaration { get; init; }

    [Description("X++ methods (rare on views — most views are pure metadata).")]
    public List<ViewMethod>? Methods { get; init; }
}

public sealed record ViewMethod
{
    public string Name { get; init; } = string.Empty;
    public string? Source { get; init; }
}

// ---- View fields (polymorphic on Kind) --------------------------------------

public sealed record ViewField
{
    [Description("View column name. PascalCase. The name exposed to consumers of the view (forms, queries, X++ select statements).")]
    public string Name { get; init; } = string.Empty;

    [Description("Field kind. Bound: projects DataField from DataSource on the backing query. Computed<Type>: synthesized at query time by an X++ method.")]
    public ViewFieldKind Kind { get; init; }

    [Description("Display label.")]
    public string? Label { get; init; }

    [Description("Help text.")]
    public string? HelpText { get; init; }

    [Description("For Kind=Bound: the field name on the data source's table. Required when Kind=Bound.")]
    public string? DataField { get; init; }

    [Description("For Kind=Bound: the data-source name within the backing query. Required when Kind=Bound.")]
    public string? DataSource { get; init; }

    [Description("Aggregation on the bound field: Sum / Avg / Min / Max / Count / GroupBy. Only valid when Kind=Bound.")]
    public string? Aggregation { get; init; }

    [Description("EDT typing the field. For Kind=Computed* the EDT determines the SQL type; for Bound it's typically inherited from the underlying field.")]
    public string? ExtendedDataType { get; init; }

    [Description("X++ method name that returns the computed value. Required when Kind=Computed*. Convention: lowercase. Method must exist on the view class.")]
    public string? Method { get; init; }

    [Description("Indicates the method is rendered via a view-level helper (rarely used).")]
    public string? ViewMethod { get; init; }

    [Description("If true, the computed field is virtual — not persisted even on staged views.")]
    public bool? IsVirtual { get; init; }

    [Description("Max characters when Kind=ComputedString. Only valid for ComputedString.")]
    public int? StringSize { get; init; }

    [Description("AxEnum name typing the column when Kind=ComputedEnum. Required for ComputedEnum.")]
    public string? EnumType { get; init; }

    [Description("Text alignment when Kind=ComputedString.")]
    public string? Adjustment { get; init; }

    [Description("Access modifier: Public / Private / Internal / Protected.")]
    public string? AccessModifier { get; init; }

    [Description("Configuration-key restriction.")]
    public string? ConfigurationKey { get; init; }

    [Description("Country-code restriction.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Marks the column obsolete.")]
    public bool? IsObsolete { get; init; }

    [Description("Group-prompt label override.")]
    public string? GroupPrompt { get; init; }

    [Description("Relation context override.")]
    public string? RelationContext { get; init; }

    [Description("AOS-side authorization mode override.")]
    public string? AosAuthorization { get; init; }

    [Description("Feature-class binding for runtime-feature-flag gating.")]
    public string? FeatureClass { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewFieldKind
{
    Bound,
    ComputedString,
    ComputedInt,
    ComputedInt64,
    ComputedReal,
    ComputedDate,
    ComputedEnum,
    ComputedUtcDateTime,
}

// ---- View indexes -----------------------------------------------------------

public sealed record ViewIndex
{
    [Description("Index name.")]
    public string Name { get; init; } = string.Empty;

    [Description("If true, duplicate keys allowed (default). False = unique index.")]
    public bool? AllowDuplicates { get; init; }

    [Description("Marks as a secondary alternate key.")]
    public bool? AlternateKey { get; init; }

    [Description("Restricts the index by configuration key.")]
    public string? ConfigurationKey { get; init; }

    [Description("Whether the index is materialized. Default true.")]
    public bool? Enabled { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Ordered field-key composition.")]
    public List<ViewIndexField>? Fields { get; init; }
}

public sealed record ViewIndexField
{
    [Description("Name of the view column.")]
    public string DataField { get; init; } = string.Empty;
}

// ---- View relations + constraints (mirror AxTableRelation shape) -----------

public sealed record ViewRelation
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

    [Description("Set true to emit AxViewRelationForeignKey instead of the legacy AxViewRelation shape.")]
    public bool? IsForeignKey { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Join predicates. Each constraint is one of Field / Fixed / RelatedFixed — same semantics as AxTableRelationConstraint.")]
    public List<ViewRelationConstraint>? Constraints { get; init; }
}

public sealed record ViewRelationConstraint
{
    [Description("Constraint name.")]
    public string Name { get; init; } = string.Empty;

    [Description("Constraint shape: Field / Fixed / RelatedFixed.")]
    public ViewConstraintType Type { get; init; }

    [Description("Field on THIS view (Field / Fixed).")]
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
public enum ViewConstraintType { Field, Fixed, RelatedFixed }

// ---- Subscriber access level (shared with AxTable's shape) -----------------

public sealed record SubscriberAccessLevel
{
    public ViewAccessLevel? Read { get; init; }
    public ViewAccessLevel? Create { get; init; }
    public ViewAccessLevel? Update { get; init; }
    public ViewAccessLevel? Delete { get; init; }
    public ViewAccessLevel? Correct { get; init; }
    public ViewAccessLevel? Invoke { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewAccessLevel { Allow, Deny }

// ---- ViewMetadata designer block -------------------------------------------

public sealed record ViewDesignerMetadata
{
    [Description("Always 'Metadata' in MS-shipped views.")]
    public string? Name { get; init; }

    [Description("Designer-side methods. Usually empty.")]
    public List<ViewMethod>? Methods { get; init; }
}
