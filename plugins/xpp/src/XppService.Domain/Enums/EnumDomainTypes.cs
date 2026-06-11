using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain.Enums;

// EnumStyle, Visibility, SqlLiteralMode are shared across AOT types.
// They live in Xpp.Service.Domain (SharedEnums.cs) and are usable here
// without an extra using because they share the root namespace.

/// <summary>
/// Domain shape for authoring or updating an <c>AxEnum</c> via the
/// dynamics-xpp authoring surface. Maps to MS's AxEnum + AxEnumValue
/// types but with sensible defaults and progressive disclosure.
/// </summary>
public sealed record CreateEnumRequest
{
    [Description("The enum's AOT name. Convention: <prefix><Function> (e.g. ChApprovalState). Must be unique within the model.")]
    public string Name { get; init; } = string.Empty;

    [Description("The enumerated values. At least one required. Authoring order is the on-disk order; the integer Value defaults to ordinal position unless UseExplicitValues=true.")]
    public List<EnumValueRequest> Values { get; init; } = new();

    [Description("Optional display label. Use a label reference (@LabelFile:LabelId) for translatability.")]
    public string? Label { get; init; }

    [Description("Optional help text shown as tooltip / field help. Label-ref preferred.")]
    public string? Help { get; init; }

    [Description("Whether other models can add values to this enum via AxEnumExtension. Default true (modern best practice).")]
    public bool IsExtensible { get; init; } = true;

    [Description("UI rendering hint for forms that bind to this enum. Default ComboBox.")]
    public EnumStyle Style { get; init; } = EnumStyle.ComboBox;

    [Description("When true, the integer Value on each EnumValue is honored verbatim. When false (default), values auto-assign by ordinal (0, 1, 2, ...).")]
    public bool UseExplicitValues { get; init; } = false;

    [Description("Advanced / less-common properties. Omit unless you need them.")]
    public AdvancedEnumOptions? Advanced { get; init; }
}

/// <summary>
/// One literal value within an enum.
/// </summary>
public sealed record EnumValueRequest
{
    [Description("The literal name. PascalCase convention (e.g. Pending, Approved). Must be unique within the enum.")]
    public string Name { get; init; } = string.Empty;

    [Description("Optional display label for this specific value. Label-ref preferred. If omitted, the value name is used.")]
    public string? Label { get; init; }

    [Description("Explicit integer value. Honored only when the parent enum's UseExplicitValues=true. Otherwise the value is assigned by ordinal position.")]
    public int? Value { get; init; }

    [Description("Advanced / less-common per-value properties.")]
    public AdvancedEnumValueOptions? Advanced { get; init; }
}

/// <summary>
/// Enum-level properties used in &lt;10% of authoring. Each is optional.
/// </summary>
public sealed record AdvancedEnumOptions
{
    [Description("Form display-width hint, in chars. Defaults to runtime-decided width.")]
    public int? DisplayLength { get; init; }

    [Description("SQL literal mode. Default (recommended) lets the query optimizer choose; Force* values are rare overrides.")]
    public SqlLiteralMode? Literals { get; init; }

    [Description("Analytics-cube usage hint. None / Attribute / Measure / Both. Defaults to Auto.")]
    public AnalysisUsage? AnalysisUsage { get; init; }

    [Description("Feature gate. When set, the enum is hidden unless the configuration key is enabled.")]
    public string? ConfigurationKey { get; init; }

    [Description("Geo gate. Comma-separated ISO country codes (e.g. \"US,CA\"). Hides the enum outside those regions.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Arbitrary annotation tags (free text). Used by some MS tooling for categorization.")]
    public string? Tags { get; init; }

    [Description("Marks the enum as deprecated. Compiler warns on use.")]
    public bool IsObsolete { get; init; }

    [Description("Compiler-visibility. Public is the default and what 99% of enums want.")]
    public Visibility Visibility { get; init; } = Visibility.Public;
}

/// <summary>
/// Per-value properties used in &lt;5% of authoring.
/// </summary>
public sealed record AdvancedEnumValueOptions
{
    [Description("Feature gate for this specific value. The whole enum may be available but this value is hidden unless the key is on.")]
    public string? ConfigurationKey { get; init; }

    [Description("Geo gate. Comma-separated ISO country codes.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Feature-class gate (newer mechanism). When set, this value is hidden unless the named feature class reports enabled.")]
    public string? FeatureClass { get; init; }

    [Description("Arbitrary annotation tags.")]
    public string? Tags { get; init; }
}

/// <summary>
/// Domain shape returned when reading an existing AxEnum.
/// </summary>
public sealed record GetEnumResponse
{
    public string Name { get; init; } = string.Empty;
    public List<EnumValueRequest> Values { get; init; } = new();
    public string? Label { get; init; }
    public string? Help { get; init; }
    public bool IsExtensible { get; init; } = true;
    public EnumStyle Style { get; init; } = EnumStyle.ComboBox;
    public bool UseExplicitValues { get; init; }
    public AdvancedEnumOptions? Advanced { get; init; }
}

/// <summary>
/// Merge-patch shape for AxEnum: every field is nullable; null means
/// "leave the current value unchanged." Allows the agent to send a
/// tiny payload changing only what it intends.
///
/// Special cases:
/// - <c>Values</c> non-null replaces the WHOLE values list (op-based
///   collection patching is a future iteration).
/// - <c>Advanced</c> non-null replaces the WHOLE advanced block;
///   it does not deep-merge.
/// </summary>
public sealed record PatchEnumRequest
{
    [Description("Updated values list. When set, replaces the existing list wholesale. When null, the current list stays.")]
    public List<EnumValueRequest>? Values { get; init; }

    [Description("New label. Null = leave current. Empty string \"\" = clear.")]
    public string? Label { get; init; }

    [Description("New help text. Null = leave current.")]
    public string? Help { get; init; }

    [Description("Flip IsExtensible. Null = leave current.")]
    public bool? IsExtensible { get; init; }

    [Description("Flip the form rendering style. Null = leave current.")]
    public EnumStyle? Style { get; init; }

    [Description("Flip explicit-value mode. Null = leave current.")]
    public bool? UseExplicitValues { get; init; }

    [Description("Replace the advanced-options block wholesale. Null = leave current.")]
    public AdvancedEnumOptions? Advanced { get; init; }
}

// EnumStyle, SqlLiteralMode, Visibility live in SharedEnums.cs.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisUsage { Auto, None, Attribute, Measure, Both }
