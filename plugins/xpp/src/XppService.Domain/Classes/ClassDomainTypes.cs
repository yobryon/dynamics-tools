using System.ComponentModel;

namespace Xpp.Service.Domain.Classes;

/// <summary>
/// Domain shape for AxClass authoring. AxClass is small at the XML
/// root — the class semantics (extends, implements, abstract, final,
/// visibility, static, type parameters) live in the X++ Declaration
/// source. Methods are opaque X++ source bodies preserved verbatim.
///
/// On-disk surface: Name + SourceCode (Declaration + Methods) +
/// occasionally IsObsolete/Tags. The metamodel exposes more
/// properties (IsAbstract, RunOn, etc.) via reflection but MS-shipped
/// classes don't use them at the XML level — they're inferred from
/// the parsed X++. We expose those in AdvancedClassOptions as
/// opt-in for the rare case the on-disk override is needed.
/// </summary>
public sealed record CreateClassRequest
{
    [Description("The class's AOT name. PascalCase. Must be unique within the model AND must match the class identifier in the Declaration source.")]
    public string Name { get; init; } = string.Empty;

    [Description("X++ source: Declaration (the class header through closing brace) plus Methods (each one a Name + opaque X++ Source body). Omit Declaration to inherit a minimal default of 'public class <Name> { }'.")]
    public ClassSourceCode? SourceCode { get; init; }

    [Description("Marks the class obsolete. Tooling warns on new references.")]
    public bool? IsObsolete { get; init; }

    [Description("Free-form tag string. Used by the platform to mark elements (customizations, telemetry, etc.).")]
    public string? Tags { get; init; }

    [Description("Advanced metadata flags. Almost never set — X++ keywords in the Declaration source drive class semantics in modern F&O.")]
    public AdvancedClassOptions? Advanced { get; init; }
}

/// <summary>Merge-patch shape. Null = leave current. Non-null = overwrite. SourceCode replacement replaces both Declaration and Methods wholesale; to patch just methods, read with xpp_get_class, mutate, patch back.</summary>
public sealed record PatchClassRequest
{
    public ClassSourceCode? SourceCode { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public AdvancedClassOptions? Advanced { get; init; }
}

/// <summary>Read shape. Same surface as CreateClassRequest; the response can be passed straight back into xpp_create_class to clone.</summary>
public sealed record GetClassResponse
{
    public string Name { get; init; } = string.Empty;
    public ClassSourceCode? SourceCode { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public AdvancedClassOptions? Advanced { get; init; }
}

public sealed record ClassSourceCode
{
    [Description("The X++ class declaration block: the class header (including modifiers, name, extends, implements clauses) followed by class-level field declarations, all the way through the closing brace. The mapper preserves this verbatim.")]
    public string? Declaration { get; init; }

    [Description("Methods on this class. Each entry is a method name + opaque X++ source (signature + body). The Source field includes ALL of the method text — modifiers, return type, parameters, doc comments, and body. Preserved verbatim.")]
    public List<ClassMethod>? Methods { get; init; }
}

public sealed record ClassMethod
{
    [Description("Method name. Must match the identifier parsed from Source.")]
    public string Name { get; init; } = string.Empty;

    [Description("Full X++ method source including signature, doc comments, and body. Preserved verbatim through round-trip.")]
    public string? Source { get; init; }
}

/// <summary>
/// Advanced AxClass options. These properties exist on the metamodel
/// but are almost never set at the XML root in MS-shipped classes —
/// the X++ Declaration carries class semantics (modifiers, extends,
/// implements). Set these only when you specifically need an
/// XML-level override.
/// </summary>
public sealed record AdvancedClassOptions
{
    [Description("Marks the class abstract at the metadata level. Usually set via 'abstract' keyword in the Declaration source instead.")]
    public bool? IsAbstract { get; init; }

    [Description("Marks the class final (sealed) at the metadata level. Usually set via 'final' keyword in the Declaration source instead.")]
    public bool? IsFinal { get; init; }

    [Description("Marks this as an interface (vs concrete class). Usually set via 'interface' keyword in the Declaration.")]
    public bool? IsInterface { get; init; }

    [Description("Marks the class as internal (assembly-scoped). Usually set via 'internal' keyword in the Declaration.")]
    public bool? IsInternal { get; init; }

    [Description("Marks the class private. Usually set via 'private' keyword in the Declaration.")]
    public bool? IsPrivate { get; init; }

    [Description("Marks the class public. Usually set via 'public' keyword in the Declaration. Default for classes.")]
    public bool? IsPublic { get; init; }

    [Description("Marks the class static. Usually set via 'static' keyword in the Declaration.")]
    public bool? IsStatic { get; init; }

    [Description("Name of a parent class to extend at the metadata level. Usually set via 'extends Foo' in the Declaration.")]
    public string? Extends { get; init; }

    [Description("Execution tier hint: Called / Server / Client / ClientOrServer. Default is unset (no hint).")]
    public string? RunOn { get; init; }
}
