using System.Text.Json.Serialization;

namespace Xpp.Service.Domain;

// =============================================================================
// Cross-AOT-type shared enums. Live here (not in per-AOT-type files) when the
// values are conceptually identical across types — e.g. CompilerVisibility,
// SqlLiteralMode, EnumStyle. AOT-type-unique enums stay in the per-type file
// so the domain shape for that type is self-contained.
// =============================================================================

/// <summary>Compiler visibility for AOT artifacts. Public is the everyday default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Visibility { Private, Protected, Public, Internal, InternalProtected }

/// <summary>SQL literal mode — applies to most numeric/string-keyed AOT types.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlLiteralMode { Default, ForceLiterals, ForcePlaceholders }

/// <summary>How an enum-typed field renders on forms.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnumStyle { ComboBox, RadioButton }
