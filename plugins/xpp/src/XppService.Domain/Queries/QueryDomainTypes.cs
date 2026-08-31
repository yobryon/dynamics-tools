using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain.Queries;

/// <summary>
/// Domain shape for AxQuery authoring. Scope: AxQuerySimple — the
/// modern join/filter shape that covers ~95% of authored queries.
/// AxQueryComposite (union/aggregate over other queries) is deferred
/// to the raw xpp_update_object escape hatch.
///
/// On-disk root carries i:type="AxQuerySimple" (polymorphic root, same
/// pattern as AxEdt). The bridge owns that translation — the domain
/// shape doesn't expose the type discriminator to the agent.
///
/// Data sources are recursive: a RootDataSource contains a tree of
/// EmbeddedDataSources (joins), which can themselves contain more
/// embeddeds — depth unbounded. DerivedDataSource is a separate
/// flavor for derived-table references.
/// </summary>
public sealed record CreateQueryRequest
{
    [Description("The query's AOT name. PascalCase. Must be unique within the model.")]
    public string Name { get; init; } = string.Empty;

    [Description("Display title — label reference @File:Id preferred. Shown in the query lookup UI and on forms that consume the query.")]
    public string? Title { get; init; }

    [Description("Free-form description for documentation. Label reference preferred.")]
    public string? Description { get; init; }

    [Description("Query kind: Join (default — joined tables + ranges) or Union (use UnionType on each data source to combine row sets).")]
    public QueryType? QueryType { get; init; }

    [Description("Whether queries built from this template see data across all companies. Default false.")]
    public bool? AllowCrossCompany { get; init; }

    [Description("Whether the query participates in the check-permission framework.")]
    public bool? AllowCheck { get; init; }

    [Description("Whether the query can be imported/exported by the data management framework. Default true.")]
    public bool? Importable { get; init; }

    [Description("Whether the query supports interactive filtering at runtime. Default true.")]
    public bool? Interactive { get; init; }

    [Description("Whether the query is exposed to global search.")]
    public bool? Searchable { get; init; }

    [Description("Whether end users can save modified versions of this query. Default true for most queries.")]
    public bool? UserUpdate { get; init; }

    [Description("Reference form used when the query opens a UI (e.g. SysQueryForm). Usually unset (defaults).")]
    public string? Form { get; init; }

    [Description("SQL literal-handling policy: Default / Yes / No. Controls how the kernel rewrites WHERE clause literals into parameters.")]
    public QueryLiterals? Literals { get; init; }

    [Description("Marks the query obsolete. Tooling warns on new references.")]
    public bool? IsObsolete { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("X++ source attached to the query — typically just a classDeclaration carrying the [Query] attribute. Omit to inherit a minimal default.")]
    public QuerySourceCode? SourceCode { get; init; }

    [Description("Top-level data sources. For a Simple query, exactly one Root data source is conventional; it carries the join tree as nested Embedded children plus optional Derived siblings.")]
    public List<QueryDataSource>? DataSources { get; init; }

    [Description("Advanced / less-common scalar properties.")]
    public AdvancedQueryOptions? Advanced { get; init; }
}

public sealed record PatchQueryRequest
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public QueryType? QueryType { get; init; }
    public bool? AllowCrossCompany { get; init; }
    public bool? AllowCheck { get; init; }
    public bool? Importable { get; init; }
    public bool? Interactive { get; init; }
    public bool? Searchable { get; init; }
    public bool? UserUpdate { get; init; }
    public string? Form { get; init; }
    public QueryLiterals? Literals { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public QuerySourceCode? SourceCode { get; init; }
    public List<QueryDataSource>? DataSources { get; init; }
    public AdvancedQueryOptions? Advanced { get; init; }
}

public sealed record GetQueryResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Description { get; init; }
    public QueryType? QueryType { get; init; }
    public bool? AllowCrossCompany { get; init; }
    public bool? AllowCheck { get; init; }
    public bool? Importable { get; init; }
    public bool? Interactive { get; init; }
    public bool? Searchable { get; init; }
    public bool? UserUpdate { get; init; }
    public string? Form { get; init; }
    public QueryLiterals? Literals { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public QuerySourceCode? SourceCode { get; init; }
    public List<QueryDataSource>? DataSources { get; init; }
    public AdvancedQueryOptions? Advanced { get; init; }
}

public sealed record AdvancedQueryOptions
{
    public Visibility Visibility { get; init; } = Visibility.Public;
}

public sealed record QuerySourceCode
{
    [Description("X++ methods on the query class. Typically a single 'classDeclaration' carrying the [Query] attribute and class header. Each entry is opaque source text preserved verbatim.")]
    public List<QueryMethod>? Methods { get; init; }
}

public sealed record QueryMethod
{
    public string Name { get; init; } = string.Empty;
    public string? Source { get; init; }
}

// ---- Data sources -----------------------------------------------------------

public sealed record QueryDataSource
{
    [Description("Data-source name. PascalCase. Used as the join alias and referenced from child DataSources / Ranges / OrderBy / GroupBy / Having entries.")]
    public string Name { get; init; } = string.Empty;

    [Description("Data-source kind. Root: top-level query data source (carries OrderBy / GroupBy / Having). Embedded: nested join (carries JoinMode + Relations). Derived: derived-table reference.")]
    public QueryDataSourceKind Kind { get; init; }

    [Description("Backing AOT table or view name. Required for Root and Embedded.")]
    public string? Table { get; init; }

    [Description("Display label for the data source (label ref preferred).")]
    public string? Label { get; init; }

    [Description("Whether new fields added to the underlying table appear automatically. Default true.")]
    public bool? DynamicFields { get; init; }

    [Description("Whether the data source can have records appended at runtime. Default true.")]
    public bool? AllowAdd { get; init; }

    [Description("Whether the data source is active. Default true. Disabled data sources are skipped at query-build time.")]
    public bool? Enabled { get; init; }

    [Description("Limit to the first matching row only (firstOnly). Useful when the data source's role is existence-check.")]
    public bool? FirstOnly { get; init; }

    [Description("Fetch as many rows in one round-trip as the connection allows (firstFast semantic).")]
    public bool? FirstFast { get; init; }

    [Description("Block updates against rows fetched through this data source.")]
    public bool? IsReadOnly { get; init; }

    [Description("Allow update statements against this data source's rows.")]
    public bool? Update { get; init; }

    [Description("Company-scope override (specific company code) when AllowCrossCompany is true.")]
    public string? Company { get; init; }

    [Description("How this data source combines with siblings in a Union query: Union / UnionAll.")]
    public string? UnionType { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Date-filter-aware companion fields when querying valid-time-state tables.")]
    public bool? ApplyDateFilter { get; init; }

    [Description("Whether this data source participates in change tracking when the underlying table supports it.")]
    public bool? ChangeTrackingEnabled { get; init; }

    [Description("Concurrency model override (Optimistic / Pessimistic / Auto).")]
    public string? ConcurrencyModel { get; init; }

    [Description("Policy context for security-policy resolution.")]
    public string? PolicyContext { get; init; }

    [Description("Hint that selects should use SQL Server's repeatable-read isolation.")]
    public bool? SelectWithRepeatableRead { get; init; }

    [Description("Whether updates honor valid-time-state semantics.")]
    public bool? ValidTimeStateUpdate { get; init; }

    [Description("Nested data sources (joins). Each is itself a QueryDataSource with Kind=Embedded — depth unbounded.")]
    public List<QueryDataSource>? DataSources { get; init; }

    [Description("Derived-table data sources at this level.")]
    public List<QueryDataSource>? DerivedDataSources { get; init; }

    [Description("Output field list. Each entry projects a single field from the underlying table.")]
    public List<QueryField>? Fields { get; init; }

    [Description("WHERE-clause predicates: each entry pins a field to a value (or range pattern).")]
    public List<QueryRange>? Ranges { get; init; }

    [Description("Order-by clauses. Only valid when Kind=Root.")]
    public List<QueryOrderBy>? OrderBy { get; init; }

    [Description("Group-by clauses. Only valid when Kind=Root.")]
    public List<QueryGroupBy>? GroupBy { get; init; }

    [Description("Having clauses (aggregate predicates). Only valid when Kind=Root.")]
    public List<QueryHaving>? Having { get; init; }

    [Description("Join mode: InnerJoin / OuterJoin / ExistsJoin / NoExistsJoin / ExtendedRelations. Only valid when Kind=Embedded.")]
    public JoinMode? JoinMode { get; init; }

    [Description("Hint controlling how the kernel materializes join rows (1:1 / 1:N). Only valid when Kind=Embedded.")]
    public string? FetchMode { get; init; }

    [Description("If true, the join uses table-level relations; if false, Relations[] must spell out the predicates explicitly. Only valid when Kind=Embedded.")]
    public bool? UseRelations { get; init; }

    [Description("Explicit join predicates. Only valid when Kind=Embedded. When UseRelations=true, a single Relation often suffices that names a Relation on the parent table; otherwise spell out Field / RelatedField / JoinDataSource explicitly.")]
    public List<QueryRelation>? Relations { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryDataSourceKind { Root, Embedded, Derived }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JoinMode { InnerJoin, OuterJoin, ExistsJoin, NoExistsJoin, ExtendedRelations }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryType { Join, Union }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryLiterals { Default, Yes, No }

// ---- Sub-records ------------------------------------------------------------

public sealed record QueryField
{
    [Description("Logical field-projection name. Conventionally the same as Field.")]
    public string Name { get; init; } = string.Empty;

    [Description("The backing field name on the data source's table.")]
    public string? Field { get; init; }

    [Description("When the data source has derived tables, names which derived table this field comes from.")]
    public string? DerivedTable { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

public sealed record QueryRange
{
    [Description("Range name. Conventionally PascalCase + describes the predicate (e.g. 'CustomerActive').")]
    public string Name { get; init; } = string.Empty;

    [Description("Field on the data source's table that this range filters.")]
    public string? Field { get; init; }

    [Description("Range expression. Examples: '!Yes', '0..100', '\"USD\"', 'Project*', or X++ formula expressions. The value is opaque to the mapper — the kernel parses it at query-build time.")]
    public string? Value { get; init; }

    [Description("Range status: Open (default, user-editable), Locked (set by code, hidden from users), Hidden (set by code, not shown in UI).")]
    public RangeStatus? Status { get; init; }

    [Description("Whether the range is active. Default true.")]
    public bool? Enabled { get; init; }

    [Description("Display label.")]
    public string? Label { get; init; }

    [Description("Names the derived table when the data source has multiple.")]
    public string? DerivedTable { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RangeStatus { Open, Locked, Hidden }

public sealed record QueryRelation
{
    [Description("Relation name. Often a description of what's joined (e.g. 'CustomerToOrder') OR — when UseRelations=true and pointing at a table-level relation — the relation name on the related table.")]
    public string Name { get; init; } = string.Empty;

    [Description("Field on the JOINED data source's table — the one named in JoinDataSource (typically the FK column on the parent). Required when UseRelations=false. NOTE: this is the join partner's field, NOT this data source's; the compiler resolves Field against JoinDataSource. (See a shipped example, e.g. SystemSecurityRoleDutyEntity.)")]
    public string? Field { get; init; }

    [Description("Field on THIS data source's table — the data source this relation is declared under (often RecId). Required when UseRelations=false. The compiler resolves RelatedField against the embedded data source itself, not the join partner.")]
    public string? RelatedField { get; init; }

    [Description("Name of the data source to join AGAINST. References a sibling or parent data source by Name. Field (above) is resolved against THIS data source.")]
    public string? JoinDataSource { get; init; }

    [Description("When UseRelations=true, names the relation on the JOINED data source's table to consume.")]
    public string? JoinRelationName { get; init; }

    [Description("Derived-table override on the joined side.")]
    public string? JoinDerivedTable { get; init; }

    [Description("Derived-table override on this side.")]
    public string? DerivedTable { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

public sealed record QueryOrderBy
{
    [Description("Order-by entry name. Conventionally the field name being sorted.")]
    public string Name { get; init; } = string.Empty;

    [Description("Field to sort by.")]
    public string? Field { get; init; }

    [Description("Name of the data source the field lives on (may be a nested data source, not necessarily the Root). Defaults to the Root data source.")]
    public string? DataSource { get; init; }

    [Description("Sort direction: Ascending (default) / Descending.")]
    public OrderDirection? Direction { get; init; }

    [Description("Derived-table override.")]
    public string? DerivedTable { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderDirection { Ascending, Descending }

public sealed record QueryGroupBy
{
    [Description("Group-by entry name. Conventionally the field name being grouped.")]
    public string Name { get; init; } = string.Empty;

    [Description("Field to group by.")]
    public string? Field { get; init; }

    [Description("Name of the data source the field lives on.")]
    public string? DataSource { get; init; }

    [Description("Derived-table override.")]
    public string? DerivedTable { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

public sealed record QueryHaving
{
    [Description("Having entry name.")]
    public string Name { get; init; } = string.Empty;

    [Description("Field the aggregate applies to.")]
    public string? Field { get; init; }

    [Description("Name of the data source the field lives on.")]
    public string? DataSource { get; init; }

    [Description("Aggregate type: Sum / Avg / Min / Max / Count.")]
    public HavingType? Type { get; init; }

    [Description("Comparison value or expression. Same range-expression conventions as QueryRange.Value.")]
    public string? Value { get; init; }

    [Description("Status: Open / Locked / Hidden.")]
    public RangeStatus? Status { get; init; }

    [Description("Whether the predicate is active. Default true.")]
    public bool? Enabled { get; init; }

    [Description("Display label.")]
    public string? Label { get; init; }

    [Description("Derived-table override.")]
    public string? DerivedTable { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HavingType { Sum, Avg, Min, Max, Count }
