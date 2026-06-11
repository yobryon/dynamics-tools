using System.ComponentModel;
using System.Text.Json.Serialization;
using Xpp.Service.Domain.Entities;
using Xpp.Service.Domain.Enums;
using Xpp.Service.Domain.Forms;
using Xpp.Service.Domain.Menus;
using Xpp.Service.Domain.Tables;
using Xpp.Service.Domain.Views;

namespace Xpp.Service.Domain.Extensions;

// ----------------------------------------------------------------------------
// Shared sub-records used across multiple extension families.
// ----------------------------------------------------------------------------

public sealed record PropertyModification
{
    [Description("Name of the property on the target object being modified (e.g. 'CacheLookup', 'ModifiedDateTime').")]
    public string Name { get; init; } = string.Empty;

    [Description("New value for the property. Stringly-typed — same convention as the base property's on-disk text representation (Yes/No for bools, enum-value-name for enums, raw number for ints, etc.).")]
    public string? Value { get; init; }

    public string? Tags { get; init; }
}

public sealed record FieldModification
{
    [Description("Name of the field on the target object being modified.")]
    public string Name { get; init; } = string.Empty;
    [Description("Property modifications applied to this field (e.g. change AllowEdit or Label on an existing field).")]
    public List<PropertyModification>? PropertyModifications { get; init; }
    public string? Tags { get; init; }
}

public sealed record RelationModification
{
    public string Name { get; init; } = string.Empty;
    public List<PropertyModification>? PropertyModifications { get; init; }
    public string? Tags { get; init; }
}

public sealed record ValueModification
{
    [Description("Name of the enum value being modified.")]
    public string Name { get; init; } = string.Empty;
    public List<PropertyModification>? PropertyModifications { get; init; }
    public string? Tags { get; init; }
}

public sealed record FieldGroupExtension
{
    [Description("Name of the existing field group being extended (e.g. 'AutoReport').")]
    public string Name { get; init; } = string.Empty;
    [Description("Field names to add to the existing group.")]
    public List<string>? Fields { get; init; }
    public string? Tags { get; init; }
}

public sealed record RelationExtension
{
    [Description("Name of the existing relation being extended.")]
    public string Name { get; init; } = string.Empty;
    [Description("Additional constraints to add to the existing relation.")]
    public List<RelationConstraint>? RelationConstraints { get; init; }
    public string? Tags { get; init; }
}

// ----------------------------------------------------------------------------
// AxTableExtension
// ----------------------------------------------------------------------------

public sealed record CreateTableExtensionRequest
{
    [Description("Extension name. Convention: '<TargetTable>.<Suffix>' (e.g. 'CustTable.ContosoRetail').")]
    public string Name { get; init; } = string.Empty;
    [Description("Marks the extension obsolete.")]
    public bool? IsObsolete { get; init; }
    [Description("Free-form tag string.")]
    public string? Tags { get; init; }
    [Description("Form-ref override on the target table.")]
    public string? FormRef { get; init; }
    [Description("New fields to add to the target table. Same shape as AxTable.Fields (polymorphic FieldType discriminator).")]
    public List<TableField>? Fields { get; init; }
    [Description("New field groups to add to the target table.")]
    public List<TableFieldGroup>? FieldGroups { get; init; }
    [Description("Extensions to existing field groups (add fields into them).")]
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    [Description("Modifications to existing fields' properties.")]
    public List<FieldModification>? FieldModifications { get; init; }
    [Description("New indexes to add.")]
    public List<TableIndex>? Indexes { get; init; }
    [Description("New relations to add.")]
    public List<TableRelation>? Relations { get; init; }
    [Description("Extensions to existing relations (add constraints into them).")]
    public List<RelationExtension>? RelationExtensions { get; init; }
    [Description("Modifications to existing relations' properties.")]
    public List<RelationModification>? RelationModifications { get; init; }
    [Description("Modifications to the table's own properties.")]
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record PatchTableExtensionRequest
{
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? FormRef { get; init; }
    public List<TableField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    public List<FieldModification>? FieldModifications { get; init; }
    public List<TableIndex>? Indexes { get; init; }
    public List<TableRelation>? Relations { get; init; }
    public List<RelationExtension>? RelationExtensions { get; init; }
    public List<RelationModification>? RelationModifications { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record GetTableExtensionResponse
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? FormRef { get; init; }
    public List<TableField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    public List<FieldModification>? FieldModifications { get; init; }
    public List<TableIndex>? Indexes { get; init; }
    public List<TableRelation>? Relations { get; init; }
    public List<RelationExtension>? RelationExtensions { get; init; }
    public List<RelationModification>? RelationModifications { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

// ----------------------------------------------------------------------------
// AxEdtExtension
// ----------------------------------------------------------------------------

public sealed record CreateEdtExtensionRequest
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    [Description("New array elements to add. Reuses the AxEdt EdtArrayElement record from the Edts namespace.")]
    public List<Xpp.Service.Domain.Edts.EdtArrayElement>? ArrayElements { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record PatchEdtExtensionRequest
{
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public List<Xpp.Service.Domain.Edts.EdtArrayElement>? ArrayElements { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record GetEdtExtensionResponse
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public List<Xpp.Service.Domain.Edts.EdtArrayElement>? ArrayElements { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

// ----------------------------------------------------------------------------
// AxEnumExtension
// ----------------------------------------------------------------------------

public sealed record CreateEnumExtensionRequest
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    [Description("New enum values to add. Reuses the AxEnum EnumValueRequest record from the Enums namespace.")]
    public List<EnumValueRequest>? EnumValues { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    [Description("Modifications to existing enum values.")]
    public List<ValueModification>? ValueModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record PatchEnumExtensionRequest
{
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public List<EnumValueRequest>? EnumValues { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public List<ValueModification>? ValueModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record GetEnumExtensionResponse
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public List<EnumValueRequest>? EnumValues { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public List<ValueModification>? ValueModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

// ----------------------------------------------------------------------------
// AxFormExtension
// ----------------------------------------------------------------------------

public sealed record CreateFormExtensionRequest
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? ConfigurationKey { get; init; }
    [Description("New data sources to add to the form.")]
    public List<FormDataSource>? DataSources { get; init; }
    [Description("References to existing data sources (so new controls can bind to them).")]
    public List<FormDataSourceReference>? DataSourceReferences { get; init; }
    [Description("Modifications to existing data sources' properties.")]
    public List<FormDataSourceModification>? DataSourceModifications { get; init; }
    [Description("New controls to add into the form's design tree. Each entry is a wrapper (FormExtensionControl) that pairs the actual FormControl with the Name of the parent control on the base form (where to insert it).")]
    public List<FormExtensionControl>? Controls { get; init; }
    [Description("Modifications to existing controls' properties.")]
    public List<FormControlModification>? ControlModifications { get; init; }
    [Description("New parts (factbox refs) to add to the form.")]
    public List<FormPart>? Parts { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record PatchFormExtensionRequest
{
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? ConfigurationKey { get; init; }
    public List<FormDataSource>? DataSources { get; init; }
    public List<FormDataSourceReference>? DataSourceReferences { get; init; }
    public List<FormDataSourceModification>? DataSourceModifications { get; init; }
    public List<FormExtensionControl>? Controls { get; init; }
    public List<FormControlModification>? ControlModifications { get; init; }
    public List<FormPart>? Parts { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record GetFormExtensionResponse
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? ConfigurationKey { get; init; }
    public List<FormDataSource>? DataSources { get; init; }
    public List<FormDataSourceReference>? DataSourceReferences { get; init; }
    public List<FormDataSourceModification>? DataSourceModifications { get; init; }
    public List<FormExtensionControl>? Controls { get; init; }
    public List<FormControlModification>? ControlModifications { get; init; }
    public List<FormPart>? Parts { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record FormDataSourceReference
{
    [Description("Name of the existing data source being referenced from this extension.")]
    public string Name { get; init; } = string.Empty;
    public string? Tags { get; init; }
}

public sealed record FormDataSourceModification
{
    public string Name { get; init; } = string.Empty;
    public List<PropertyModification>? PropertyModifications { get; init; }
    public string? Tags { get; init; }
}

public sealed record FormControlModification
{
    [Description("Name of the existing control being modified (may be deeply nested in the original form's design tree).")]
    public string Name { get; init; } = string.Empty;
    public List<PropertyModification>? PropertyModifications { get; init; }
    public string? Tags { get; init; }
}

// ----------------------------------------------------------------------------
// AxViewExtension
// ----------------------------------------------------------------------------

public sealed record CreateViewExtensionRequest
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    [Description("New view fields to add. Reuses the AxView ViewField record from the Views namespace.")]
    public List<ViewField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    public List<FieldModification>? FieldModifications { get; init; }
    [Description("Backing query / data-source-tree extensions. Reuses the AxQuery QueryDataSource shape.")]
    public List<Xpp.Service.Domain.Queries.QueryDataSource>? DataSources { get; init; }
    [Description("Range additions to the view's data sources.")]
    public List<Xpp.Service.Domain.Queries.QueryRange>? Ranges { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record PatchViewExtensionRequest
{
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public List<ViewField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    public List<FieldModification>? FieldModifications { get; init; }
    public List<Xpp.Service.Domain.Queries.QueryDataSource>? DataSources { get; init; }
    public List<Xpp.Service.Domain.Queries.QueryRange>? Ranges { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record GetViewExtensionResponse
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public List<ViewField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    public List<FieldModification>? FieldModifications { get; init; }
    public List<Xpp.Service.Domain.Queries.QueryDataSource>? DataSources { get; init; }
    public List<Xpp.Service.Domain.Queries.QueryRange>? Ranges { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

// ----------------------------------------------------------------------------
// AxDataEntityViewExtension
// ----------------------------------------------------------------------------

public sealed record CreateEntityExtensionRequest
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    [Description("New entity fields to add. Reuses the AxDataEntityView EntityField record from the Entities namespace.")]
    public List<EntityField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    public List<FieldModification>? FieldModifications { get; init; }
    [Description("Backing data-source extensions. Reuses the AxQuery QueryDataSource shape.")]
    public List<Xpp.Service.Domain.Queries.QueryDataSource>? DataSources { get; init; }
    public List<EntityRelation>? Relations { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record PatchEntityExtensionRequest
{
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public List<EntityField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    public List<FieldModification>? FieldModifications { get; init; }
    public List<Xpp.Service.Domain.Queries.QueryDataSource>? DataSources { get; init; }
    public List<EntityRelation>? Relations { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record GetEntityExtensionResponse
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public List<EntityField>? Fields { get; init; }
    public List<TableFieldGroup>? FieldGroups { get; init; }
    public List<FieldGroupExtension>? FieldGroupExtensions { get; init; }
    public List<FieldModification>? FieldModifications { get; init; }
    public List<Xpp.Service.Domain.Queries.QueryDataSource>? DataSources { get; init; }
    public List<EntityRelation>? Relations { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record AdvancedExtensionOptions
{
    public Visibility Visibility { get; init; } = Visibility.Public;
}

// ----------------------------------------------------------------------------
// AxMenuExtension
// ----------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MenuExtensionPosition
{
    [Description("Append the new element at the end of the parent's Elements (default).")]
    End,
    [Description("Insert the new element at the beginning of the parent's Elements.")]
    Begin,
    [Description("Insert the new element immediately after the named PreviousSibling.")]
    AfterItem,
    [Description("Insert the new element immediately before the named PreviousSibling.")]
    BeforeItem,
}

public sealed record MenuExtensionElement
{
    [Description("Name of the parent menu element on the base menu where this element is inserted. Required.")]
    public string Parent { get; init; } = string.Empty;

    [Description("Position relative to the parent's existing children. Default End.")]
    public MenuExtensionPosition? PositionType { get; init; }

    [Description("Name of the sibling the new element is positioned against. Required when PositionType is AfterItem or BeforeItem.")]
    public string? PreviousSibling { get; init; }

    [Description("The polymorphic menu element to insert. Full MenuElement shape (MenuItem / MenuReference / Separator / SubMenu / Tile).")]
    public MenuElement MenuElement { get; init; } = new();

    public string? Tags { get; init; }
}

public sealed record MenuElementModification
{
    [Description("Name of the existing menu element on the base menu being modified.")]
    public string Name { get; init; } = string.Empty;
    public List<PropertyModification>? PropertyModifications { get; init; }
    public string? Tags { get; init; }
}

public sealed record CreateMenuExtensionRequest
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? ConfigurationKey { get; init; }
    [Description("New elements (MenuItem / MenuReference / Separator / SubMenu / Tile) to insert into the base menu's tree.")]
    public List<MenuExtensionElement>? Elements { get; init; }
    [Description("Property modifications applied to existing menu elements on the base menu.")]
    public List<MenuElementModification>? MenuElementModifications { get; init; }
    [Description("Property modifications applied to the base menu's own properties.")]
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record PatchMenuExtensionRequest
{
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? ConfigurationKey { get; init; }
    public List<MenuExtensionElement>? Elements { get; init; }
    public List<MenuElementModification>? MenuElementModifications { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record GetMenuExtensionResponse
{
    public string Name { get; init; } = string.Empty;
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? ConfigurationKey { get; init; }
    public List<MenuExtensionElement>? Elements { get; init; }
    public List<MenuElementModification>? MenuElementModifications { get; init; }
    public List<PropertyModification>? PropertyModifications { get; init; }
    public AdvancedExtensionOptions? Advanced { get; init; }
}

public sealed record FormExtensionControl
{
    [Description("Wrapper name. PascalCase. MS-generated wrappers look like 'FormExtensionControl<hash>'; agent-authored ones can use any unique name.")]
    public string Name { get; init; } = string.Empty;

    [Description("The actual control to insert. Full FormControl polymorphic shape.")]
    public FormControl FormControl { get; init; } = new();

    [Description("Name of the parent control on the base form where this new control should be inserted as a child.")]
    public string? Parent { get; init; }

    public string? Tags { get; init; }
    public Dictionary<string, string>? OtherProperties { get; init; }
}
