using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain.Edts;

// Visibility / SqlLiteralMode / EnumStyle live in Xpp.Service.Domain
// (SharedEnums.cs) — they share the same conceptual identity across AOT
// types so we keep them in one place. Available here without extra using
// because the parent namespace is in scope.

// =============================================================================
// AxEdt domain shape — polymorphic at the file root (xsi:type discriminator).
// On disk: <AxEdt xmlns="" i:type="AxEdtString">...</AxEdt>
//
// One CreateEdtRequest with a BaseType discriminator + per-subtype nested
// options blocks. Each subtype options block carries ONLY its own properties;
// the agent never has to know which options apply to which subtype — the
// schema gates it.
// =============================================================================

/// <summary>
/// Domain shape for authoring an <c>AxEdt</c>. The <see cref="BaseType"/>
/// drives the polymorphism: the matching nested options block (e.g.
/// <see cref="String"/> for BaseType=String) is read; the others are
/// ignored.
///
/// When extending a base EDT (<see cref="Extends"/> set), most physical
/// properties are inherited and you only need to override what differs.
/// Pure inheritance EDTs can be created with just Name + BaseType + Extends.
/// </summary>
public sealed record CreateEdtRequest
{
    [Description("The EDT's AOT name. Convention: <prefix><Function> (e.g. ChCustomerSpecificId). Must be unique within the model.")]
    public string Name { get; init; } = string.Empty;

    [Description("Primitive base type. Drives which nested options block (String / Numeric / Real / Enum / Date / Time / Utc) applies. \"Container\" and \"Guid\" carry no subtype-specific options.")]
    public EdtBaseType BaseType { get; init; }

    [Description("Optional display label. Use a label reference (@LabelFile:LabelId) for translatability.")]
    public string? Label { get; init; }

    [Description("Optional help text. Label-ref preferred.")]
    public string? HelpText { get; init; }

    [Description("Optional name of a base EDT to extend. The base's physical properties (e.g. StringSize for a String EDT) are inherited; override only what differs.")]
    public string? Extends { get; init; }

    // ---- Per-subtype options. Exactly one applies per BaseType. -----------

    [Description("Options for BaseType=String. StringSize, ChangeCase, DisplayHeight, etc. Null when BaseType isn't String.")]
    public StringEdtOptions? String { get; init; }

    [Description("Options for BaseType=Int / Int64 / Real (numeric basics: AllowNegative, ShowZero, SignDisplay, ...). Null when BaseType isn't numeric.")]
    public NumericEdtOptions? Numeric { get; init; }

    [Description("Options specific to BaseType=Real (NoOfDecimals, Scale, separators). Combine with Numeric for the basics. Null when BaseType isn't Real.")]
    public RealEdtOptions? Real { get; init; }

    [Description("Options for BaseType=Enum. EnumType is required when BaseType=Enum.")]
    public EnumEdtOptions? Enum { get; init; }

    [Description("Options for BaseType=Date (DateFormat, separator, etc.). Null when BaseType isn't Date.")]
    public DateEdtOptions? Date { get; init; }

    [Description("Options for BaseType=Time (TimeFormat, TimeHours, etc.). Null when BaseType isn't Time.")]
    public TimeEdtOptions? Time { get; init; }

    [Description("Options for BaseType=UtcDateTime — superset of Date + Time + TimezonePreference. Null when BaseType isn't UtcDateTime.")]
    public UtcDateTimeEdtOptions? Utc { get; init; }

    // ---- Collections (apply to all subtypes). -----------------------------

    [Description("Named array elements. Most EDTs have none; used for fixed-size collection-like fields (e.g. address line 1, address line 2). Null = no array elements.")]
    public List<EdtArrayElement>? ArrayElements { get; init; }

    [Description("Relations to other tables — \"this EDT's value joins to TableX.FieldY\". Use a FixedValue when the relation is a constant filter rather than a join. Null = no relations.")]
    public List<EdtRelation>? Relations { get; init; }

    [Description("Table references — similar to relations but used for the modern foreign-key pattern. Set FilterValue for a filtered reference. Null = no table refs.")]
    public List<EdtTableReference>? TableReferences { get; init; }

    [Description("Advanced / less-common properties (UI hints, presence-indicator, country region codes, configuration key, obsolete flag, etc.).")]
    public AdvancedEdtOptions? Advanced { get; init; }
}

// ---- Subtype options ------------------------------------------------------

public sealed record StringEdtOptions
{
    [Description("Max character length. F&O common defaults: 10 (short id), 20 (id), 60 (medium), 250 (longer text). Inherited from base EDT when Extends is set.")]
    public int? StringSize { get; init; }

    [Description("Whether downstream models can extend the string size beyond this declaration. Default false.")]
    public bool? StringSizeIsExtensible { get; init; }

    [Description("Case transform on entry: Upper, Lower, Auto (no transform). Auto is the default.")]
    public StringChangeCase? ChangeCase { get; init; }

    [Description("Multi-line field height in rows. >1 makes the field render as a multi-line textarea on forms.")]
    public int? DisplayHeight { get; init; }

    [Description("Text alignment within the field's display: Auto / Left / Right / Center. Auto by default.")]
    public Alignment? Adjustment { get; init; }

    [Description("Physical SQL column size when different from StringSize. Rarely set; usually equals StringSize.")]
    public int? DatabaseStringSize { get; init; }
}

public sealed record NumericEdtOptions
{
    [Description("Whether negative values are accepted. Default true for signed numerics. Set false for unsigned-style quantities.")]
    public bool? AllowNegative { get; init; }

    [Description("Whether zero values are displayed (No) or hidden (Yes). Default Yes for most fields.")]
    public bool? ShowZero { get; init; }

    [Description("Sign display: Auto, Yes (always show +/-), No (hide). Auto by default.")]
    public SignDisplay? SignDisplay { get; init; }

    [Description("Display formatting hint that shifts negative values to a separate column on forms. Default false.")]
    public bool? DisplaceNegative { get; init; }

    [Description("Reverse the displayed sign without changing storage. Used in journal-entry style flows where credits are displayed positive. Default false.")]
    public bool? RotateSign { get; init; }
}

public sealed record RealEdtOptions
{
    [Description("Decimal places displayed. Common values: 2 (currency), 4 (rates), 6 (high precision quantities).")]
    public int? NoOfDecimals { get; init; }

    [Description("Whether downstream models can change NoOfDecimals. Default false.")]
    public bool? NoOfDecimalsIsExtensible { get; init; }

    [Description("Scale factor — typically 1 (display as stored).")]
    public int? Scale { get; init; }

    [Description("Override the locale's decimal separator (e.g. \".\" or \",\"). Almost never set explicitly — locale handles it.")]
    public string? DecimalSeparator { get; init; }

    [Description("Override the locale's thousands separator. Same locale-handles-it caveat.")]
    public string? ThousandSeparator { get; init; }

    [Description("Auto-insert thousands separator on entry. Default true.")]
    public bool? AutoInsSeparator { get; init; }

    [Description("Currency-style formatting (no thousands separator, fixed decimals). Used for accounting amount fields. Default false.")]
    public bool? FormatMST { get; init; }
}

public sealed record EnumEdtOptions
{
    [Description("The AxEnum's Name. Required when BaseType=Enum.")]
    public string EnumType { get; init; } = string.Empty;

    [Description("How the enum renders on forms: ComboBox (default) or RadioButton.")]
    public EdtEnumStyle? Style { get; init; }
}

public sealed record DateEdtOptions
{
    [Description("Date format: Auto, Short, Long, etc.")]
    public DateFormat? DateFormat { get; init; }

    [Description("Day display: Auto / Yes / No. Default Auto.")]
    public AutoNoYes? DateDay { get; init; }

    [Description("Month display: Auto / Yes / No. Default Auto.")]
    public AutoNoYes? DateMonth { get; init; }

    [Description("Year display: Auto / Yes / No. Default Auto.")]
    public AutoNoYes? DateYear { get; init; }

    [Description("Override the locale's date separator (e.g. \"/\" or \".\"). Rarely set.")]
    public string? DateSeparator { get; init; }

    [Description("Label shown when the date is the system's max-date sentinel (i.e., \"forever\"). Useful for retention/expiry semantics.")]
    public string? MaxDateLabel { get; init; }
}

public sealed record TimeEdtOptions
{
    [Description("Time format: Auto, Short, Long, AmPm, etc.")]
    public TimeFormat? TimeFormat { get; init; }

    [Description("Hours display flag.")]
    public AutoNoYes? TimeHours { get; init; }

    [Description("Minutes display flag.")]
    public AutoNoYes? TimeMinute { get; init; }

    [Description("Seconds display flag.")]
    public AutoNoYes? TimeSeconds { get; init; }

    [Description("Override the locale's time separator (e.g. \":\"). Rarely set.")]
    public string? TimeSeparator { get; init; }
}

public sealed record UtcDateTimeEdtOptions
{
    // Date parts
    public DateFormat? DateFormat { get; init; }
    public AutoNoYes? DateDay { get; init; }
    public AutoNoYes? DateMonth { get; init; }
    public AutoNoYes? DateYear { get; init; }
    public string? DateSeparator { get; init; }
    public string? MaxDateLabel { get; init; }

    // Time parts
    public TimeFormat? TimeFormat { get; init; }
    public AutoNoYes? TimeHours { get; init; }
    public AutoNoYes? TimeMinute { get; init; }
    public AutoNoYes? TimeSeconds { get; init; }
    public string? TimeSeparator { get; init; }

    [Description("Timezone resolution: User (display in the user's TZ), Company (display in the company's TZ), UTC (display literal).")]
    public TimezonePreference? TimezonePreference { get; init; }
}

// ---- Collections ----------------------------------------------------------

public sealed record EdtArrayElement
{
    [Description("Array slot name. Convention: PascalCase, often Line1 / Line2 / Line3 for address-style EDTs.")]
    public string Name { get; init; } = string.Empty;

    [Description("Slot index. Usually omitted (auto-assigned by ordinal).")]
    public int? Index { get; init; }

    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? CollectionLabel { get; init; }
    public string? Tags { get; init; }

    [Description("Per-slot relations override the parent EDT's relations.")]
    public List<EdtRelation>? Relations { get; init; }

    [Description("Per-slot table references override the parent EDT's table references.")]
    public List<EdtTableReference>? TableReferences { get; init; }
}

public sealed record EdtRelation
{
    [Description("The related table's Name.")]
    public string Table { get; init; } = string.Empty;

    [Description("The field on the related table that this EDT's value matches.")]
    public string RelatedField { get; init; } = string.Empty;

    [Description("Optional fixed-value filter — when set, the relation matches RelatedField against this literal instead of joining on the EDT's column. Maps to AxEdtRelationFixed on the wire.")]
    public string? FixedValue { get; init; }

    public string? Tags { get; init; }
}

public sealed record EdtTableReference
{
    [Description("The related table's Name.")]
    public string Table { get; init; } = string.Empty;

    [Description("The field on the related table.")]
    public string RelatedField { get; init; } = string.Empty;

    [Description("Optional filter value — when set, this is an AxEdtTableReferenceFilter that constrains RelatedField to this literal.")]
    public string? FilterValue { get; init; }

    public string? Tags { get; init; }
}

// ---- Advanced (the long tail) --------------------------------------------

public sealed record AdvancedEdtOptions
{
    public string? CollectionLabel { get; init; }
    public string? FormHelp { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public string? Tags { get; init; }
    public bool IsObsolete { get; init; }
    public Visibility Visibility { get; init; } = Visibility.Public;
    public ButtonImage? ButtonImage { get; init; }
    public string? ControlClass { get; init; }
    public string? DataInteractorFactory { get; init; }
    public string? PresenceClass { get; init; }
    public string? PresenceMethod { get; init; }
    public bool? PresenceIndicatorAllowed { get; init; }
    public Alignment? Alignment { get; init; }
    public TextDirection? Direction { get; init; }
    public int? DisplayLength { get; init; }
    public bool? EnforceHierarchy { get; init; }

    [Description("For 'reference' EDTs, the target table whose surrogate-key field is being referenced. Sets up the EDT as a foreign-key alias.")]
    public string? ReferenceTable { get; init; }

    public SqlLiteralMode? Literals { get; init; }
}

// ---- Get / Patch ----------------------------------------------------------

public sealed record GetEdtResponse
{
    public string Name { get; init; } = string.Empty;
    public EdtBaseType BaseType { get; init; }
    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? Extends { get; init; }
    public StringEdtOptions? String { get; init; }
    public NumericEdtOptions? Numeric { get; init; }
    public RealEdtOptions? Real { get; init; }
    public EnumEdtOptions? Enum { get; init; }
    public DateEdtOptions? Date { get; init; }
    public TimeEdtOptions? Time { get; init; }
    public UtcDateTimeEdtOptions? Utc { get; init; }
    public List<EdtArrayElement>? ArrayElements { get; init; }
    public List<EdtRelation>? Relations { get; init; }
    public List<EdtTableReference>? TableReferences { get; init; }
    public AdvancedEdtOptions? Advanced { get; init; }
}

/// <summary>
/// Merge-patch shape — every field nullable, null = leave current.
/// BaseType is intentionally NOT patchable: changing the discriminator
/// is a different operation (effectively deleting + re-creating the
/// EDT). Use xpp_create_edt for that.
/// </summary>
public sealed record PatchEdtRequest
{
    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? Extends { get; init; }
    public StringEdtOptions? String { get; init; }
    public NumericEdtOptions? Numeric { get; init; }
    public RealEdtOptions? Real { get; init; }
    public EnumEdtOptions? Enum { get; init; }
    public DateEdtOptions? Date { get; init; }
    public TimeEdtOptions? Time { get; init; }
    public UtcDateTimeEdtOptions? Utc { get; init; }
    public List<EdtArrayElement>? ArrayElements { get; init; }
    public List<EdtRelation>? Relations { get; init; }
    public List<EdtTableReference>? TableReferences { get; init; }
    public AdvancedEdtOptions? Advanced { get; init; }
}

// ---- Enums ----------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EdtBaseType { String, Int, Int64, Real, Enum, Date, Time, UtcDateTime, Container, Guid }

/// <summary>F&O on-disk values: Auto, None, UpperCase, LowerCase, SentenceCase.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StringChangeCase { Auto, None, UpperCase, LowerCase, SentenceCase }

/// <summary>F&O on-disk values: Auto, Left, Right, Center.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Alignment { Auto, Left, Right, Center }

/// <summary>F&O on-disk values: Auto, None, Prefixed, Suffixed, Parentheses. NOT a Yes/No flag.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignDisplay { Auto, None, Prefixed, Suffixed, Parentheses }

/// <summary>AxEdtEnum's Style differs from AxEnum's Style: F&O uses Auto / Combobox / Radiobutton (lowercase second word + has Auto). For the AxEnum-level Style, use the shared EnumStyle (ComboBox / RadioButton).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EdtEnumStyle { Auto, Combobox, Radiobutton }

/// <summary>F&O on-disk values: Auto, YMD, YDM, MYD, DYM, MDY, DMY.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DateFormat { Auto, YMD, YDM, MYD, DYM, MDY, DMY }

/// <summary>F&O on-disk values: Auto, Hour24, AMPM (the last in all caps).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeFormat { Auto, Hour24, AMPM }

/// <summary>F&O AutoNoYes enum — used for the Day / Month / Year / Hours / Minute / Seconds display flags.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutoNoYes { Auto, No, Yes }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimezonePreference { User, Company, UTC, None }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextDirection { Auto, LTR, RTL }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ButtonImage { Arrow, Mail, URL, ThreeDots, OpenFile, Calendar, Phone, RightArrow }

// SqlLiteralMode, Visibility live in SharedEnums.cs (Xpp.Service.Domain).
