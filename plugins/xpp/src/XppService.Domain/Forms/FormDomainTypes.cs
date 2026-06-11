using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain.Forms;

/// <summary>
/// Domain shape for AxForm authoring. Forms are the biggest typed
/// surface — three nested concerns wrapped in one envelope:
///
/// 1. Form-level metadata + data sources (table/view/query joined
///    into the form's data graph).
/// 2. Design: the visual control tree (recursive, polymorphic on
///    control kind across ~40 on-disk subtypes).
/// 3. Source code: form-level Methods, per-datasource event handlers,
///    per-control event handlers (DataControls), and member declarations.
///
/// Plus Parts (factbox references attached to specific data sources).
///
/// Root carries the non-empty default namespace
/// `Microsoft.Dynamics.AX.Metadata.V6` (different version than the
/// menu family's V1).
///
/// Scope: pragmatic + typed common controls. ~15 control kinds typed
/// with the binding/behavior properties (DataField, DataSource, Label,
/// etc.). All other properties round-trip safely via the per-control
/// OtherProperties dictionary. Less-common control types
/// (ActiveX, Animate, ListView, ManagedHost, Progress, Tree) fall
/// back to `kind: Other` which preserves the original xsi:type and
/// all properties via the dict.
/// </summary>
public sealed record CreateFormRequest
{
    [Description("Form's AOT name. PascalCase. Matches the file name and the class identifier in SourceCode.Declaration.")]
    public string Name { get; init; } = string.Empty;

    [Description("Form-template hint (e.g. SimpleListDetails, ListPage, DetailsTransaction). Drives default styling and behavior.")]
    public string? FormTemplate { get; init; }

    [Description("Marks the form obsolete.")]
    public bool? IsObsolete { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Backing query name when the form's data sources derive from an AxQuery.")]
    public string? DataSourceQuery { get; init; }

    [Description("Change-group mode for data-source change tracking: Implicit / NotImplicit / None.")]
    public string? DataSourceChangeGroupMode { get; init; }

    [Description("Whether the form participates in client-side pre-loading. Default false.")]
    public bool? AllowPreLoading { get; init; }

    [Description("Whether updates to backing data refresh the form's cache automatically.")]
    public bool? AutoCacheUpdate { get; init; }

    [Description("Custom interaction class for advanced form-coordination scenarios.")]
    public string? InteractionClass { get; init; }

    [Description("X++ source: Methods (form-level), DataSources (per-datasource handlers), DataControls (per-control handlers), Members (form-level field declarations). Method bodies are opaque text preserved verbatim.")]
    public FormSourceCode? SourceCode { get; init; }

    [Description("Form data sources. Each carries Table + Fields + Links to its parent data source.")]
    public List<FormDataSource>? DataSources { get; init; }

    [Description("Visual design: caption, pattern, style + recursive control tree.")]
    public FormDesign? Design { get; init; }

    [Description("Factbox / part references attached to the form.")]
    public List<FormPart>? Parts { get; init; }

    [Description("Advanced / less-common scalar properties.")]
    public AdvancedFormOptions? Advanced { get; init; }
}

public sealed record PatchFormRequest
{
    public string? FormTemplate { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? DataSourceQuery { get; init; }
    public string? DataSourceChangeGroupMode { get; init; }
    public bool? AllowPreLoading { get; init; }
    public bool? AutoCacheUpdate { get; init; }
    public string? InteractionClass { get; init; }
    public FormSourceCode? SourceCode { get; init; }
    public List<FormDataSource>? DataSources { get; init; }
    public FormDesign? Design { get; init; }
    public List<FormPart>? Parts { get; init; }
    public AdvancedFormOptions? Advanced { get; init; }
}

public sealed record GetFormResponse
{
    public string Name { get; init; } = string.Empty;
    public string? FormTemplate { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public string? DataSourceQuery { get; init; }
    public string? DataSourceChangeGroupMode { get; init; }
    public bool? AllowPreLoading { get; init; }
    public bool? AutoCacheUpdate { get; init; }
    public string? InteractionClass { get; init; }
    public FormSourceCode? SourceCode { get; init; }
    public List<FormDataSource>? DataSources { get; init; }
    public FormDesign? Design { get; init; }
    public List<FormPart>? Parts { get; init; }
    public AdvancedFormOptions? Advanced { get; init; }
}

public sealed record AdvancedFormOptions
{
    public Visibility Visibility { get; init; } = Visibility.Public;
}

// ---- SourceCode -------------------------------------------------------------

public sealed record FormSourceCode
{
    [Description("Class declaration block. Default: [Form] public class <Name> extends FormRun {}.")]
    public string? Declaration { get; init; }

    [Description("Form-level methods. Opaque X++ source preserved verbatim.")]
    public List<FormMethod>? Methods { get; init; }

    [Description("Per-data-source event handlers: entries name a DataSource and provide its methods (init, executeQuery, validateWrite, etc.). Opaque source.")]
    public List<FormSourceCodeDataSource>? DataSources { get; init; }

    [Description("Per-control event handlers: entries name a Control and provide its methods (clicked, validate, modified, etc.).")]
    public List<FormSourceCodeControl>? DataControls { get; init; }

    [Description("Form-level member declarations (private/public variables, types). Each entry has Name + opaque Declaration text.")]
    public List<FormMember>? Members { get; init; }
}

public sealed record FormMethod
{
    public string Name { get; init; } = string.Empty;
    public string? Source { get; init; }
}

public sealed record FormSourceCodeDataSource
{
    [Description("Name of a DataSource in the form's DataSources collection.")]
    public string Name { get; init; } = string.Empty;
    public List<FormMethod>? Methods { get; init; }
    public List<FormSourceCodeField>? Fields { get; init; }
}

public sealed record FormSourceCodeField
{
    public string Name { get; init; } = string.Empty;
    public List<FormMethod>? Methods { get; init; }
}

public sealed record FormSourceCodeControl
{
    [Description("Name of a control in the Design tree.")]
    public string Name { get; init; } = string.Empty;

    [Description("Control type (matches the on-disk Type element).")]
    public string? Type { get; init; }

    public List<FormMethod>? Methods { get; init; }
}

public sealed record FormMember
{
    public string Name { get; init; } = string.Empty;
    public string? Declaration { get; init; }
}

// ---- DataSources ------------------------------------------------------------

public sealed record FormDataSource
{
    [Description("Data-source name. PascalCase. Referenced from controls' DataSource property and from Links.")]
    public string Name { get; init; } = string.Empty;

    [Description("Data-source kind. Root (default top-level). Concrete (joined to parent). Derived (derived-table flavor). Referenced (references another form's data source).")]
    public FormDataSourceKind Kind { get; init; } = FormDataSourceKind.Root;

    [Description("Backing AOT table / view name.")]
    public string? Table { get; init; }

    [Description("Optional EDT-style index hint (passed to the kernel).")]
    public string? Index { get; init; }

    [Description("Whether records are inserted via the form. Default Yes.")]
    public bool? InsertIfEmpty { get; init; }

    [Description("Whether the data source allows record creation. Default Yes.")]
    public bool? AllowCreate { get; init; }

    [Description("Whether the data source allows record edit. Default Yes.")]
    public bool? AllowEdit { get; init; }

    [Description("Whether the data source allows record delete. Default Yes.")]
    public bool? AllowDelete { get; init; }

    [Description("OnlyFetchActive — load only active rows.")]
    public bool? OnlyFetchActive { get; init; }

    [Description("Parent data source name when this is a joined child. For a standard " +
                 "master/detail form this is ALL you need to set on the child data source: " +
                 "F&O derives the join fields from the table relation between the two tables. " +
                 "You do NOT specify join fields here — there is no field-pair link surface in F&O forms.")]
    public string? JoinSource { get; init; }

    [Description("Join mode for a joined child (paired with JoinSource). Defaults to Delayed, " +
                 "which is what a normal master/detail grid wants. Other modes are for query joins.")]
    public FormJoinLinkType? LinkType { get; init; }

    [Description("StartPosition behavior: First / Last / Top.")]
    public string? StartPosition { get; init; }

    [Description("Whether the data source participates in inner-join optimization.")]
    public bool? OptionalRecord { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Field overrides (per-field AllowEdit / Visible / Skip etc.).")]
    public List<FormDataSourceField>? Fields { get; init; }

    [Description("ADVANCED / rarely needed. Explicit data-source links, used only to pin a " +
                 "specific table relation or override link behavior when a data source joins on " +
                 "more than one relation. Standard master/detail does NOT use this — set JoinSource " +
                 "and rely on the table relation. There is no field/relatedField here: a link " +
                 "names a relation, not a field pair.")]
    public List<FormDataSourceLink>? Links { get; init; }

    [Description("Other data sources this one references (for ReferencedDataSources).")]
    public List<FormDataSource>? ReferencedDataSources { get; init; }

    [Description("Nested derived data sources.")]
    public List<FormDataSource>? DerivedDataSources { get; init; }

    [Description("Opaque catch-all for properties the typed shape doesn't model. Round-trip preserves these.")]
    public Dictionary<string, string>? OtherProperties { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormDataSourceKind { Root, Concrete, Derived, Referenced }

public sealed record FormDataSourceField
{
    [Description("Field name (matches table's field).")]
    public string DataField { get; init; } = string.Empty;
    public bool? AllowEdit { get; init; }
    public bool? AllowEditOnCreate { get; init; }
    public bool? Visible { get; init; }
    public bool? Skip { get; init; }
    public bool? Mandatory { get; init; }
    public string? Tags { get; init; }
    public Dictionary<string, string>? OtherProperties { get; init; }
}

/// <summary>
/// An explicit data-source link (AxFormDataSourceRootLink). ADVANCED — a standard
/// joined data source needs none of these; F&O resolves the join from the table
/// relation. A link references a RELATION by name and optionally sets a behavior;
/// F&O forms have NO field/relatedField link surface.
/// </summary>
public sealed record FormDataSourceLink
{
    [Description("The table-relation name this link binds the join to.")]
    public string? Name { get; init; }

    [Description("Per-link behavior. Default None (inherit the data source's join mode).")]
    public FormLinkBehavior? Behavior { get; init; }

    public string? Tags { get; init; }
}

/// <summary>Data-source join mode (AxFormDataSource.LinkType). Delayed is the
/// default and the normal choice for a master/detail grid.</summary>
public enum FormJoinLinkType { Passive, Delayed, Active, InnerJoin, OuterJoin, ExistJoin, NotExistJoin }

/// <summary>Per-link behavior (AxFormDataSourceRootLink.LinkType).</summary>
public enum FormLinkBehavior { None, Delayed, Active, Passive }

// ---- Design + Parts ---------------------------------------------------------

public sealed record FormDesign
{
    [Description("Form caption (title bar text). Label-ref preferred.")]
    public string? Caption { get; init; }

    [Description("Design pattern (DetailsMaster, SimpleList, ListPage, etc.) — drives default layout behavior.")]
    public string? Pattern { get; init; }

    [Description("Design-pattern version when applicable.")]
    public string? PatternVersion { get; init; }

    [Description("Visual style override.")]
    public string? Style { get; init; }

    [Description("Header pattern within the form.")]
    public string? HeaderPattern { get; init; }

    [Description("View / edit mode for the entire form: View / Edit / Auto.")]
    public string? ViewEditMode { get; init; }

    [Description("Image / icon resource for the form.")]
    public string? TitleDataSource { get; init; }

    [Description("Top control tree.")]
    public List<FormControl>? Controls { get; init; }

    [Description("Opaque catch-all for design properties the typed shape doesn't model.")]
    public Dictionary<string, string>? OtherProperties { get; init; }
}

public sealed record FormPart
{
    public string Name { get; init; } = string.Empty;
    [Description("Part kind: Reference (a named AxFormPart referenced).")]
    public string? Kind { get; init; }
    [Description("Referenced part / form name.")]
    public string? PartName { get; init; }
    public string? DataSource { get; init; }
    public string? DataSourceRelation { get; init; }
    public string? PartLocation { get; init; }
    public string? Caption { get; init; }
    public bool? Visible { get; init; }
    public string? Tags { get; init; }
    public Dictionary<string, string>? OtherProperties { get; init; }
}

// ---- Controls --------------------------------------------------------------

public sealed record FormControl
{
    [Description("Control name. PascalCase. Conventionally the same as the bound DataField for data controls.")]
    public string Name { get; init; } = string.Empty;

    [Description("Control kind. Typed kinds cover the most common ~17 controls; Other preserves the original on-disk i:type for less-common variants (ActiveX, Animate, ListView, ManagedHost, Progress, Tree, etc.).")]
    public FormControlKind Kind { get; init; }

    [Description("For Kind=Other: the original on-disk xsi:type (e.g. 'AxFormListViewControl').")]
    public string? RawType { get; init; }

    // ---- Common base properties (subset of AxFormControl) ----

    [Description("Whether the control is visible. Default true.")]
    public bool? Visible { get; init; }

    [Description("Whether the control is enabled (interactive). Default true.")]
    public bool? Enabled { get; init; }

    [Description("Whether the control accepts edits. Default true.")]
    public bool? AllowEdit { get; init; }

    [Description("Whether keyboard tabbing skips this control. Default false.")]
    public bool? Skip { get; init; }

    [Description("Emit an X++ field declaration so the control is referenceable from form methods. Default false on most controls; true on important named controls.")]
    public bool? AutoDeclaration { get; init; }

    [Description("Layout pattern hint.")]
    public string? Pattern { get; init; }

    [Description("Pattern version.")]
    public string? PatternVersion { get; init; }

    [Description("Help text shown in tooltips.")]
    public string? HelpText { get; init; }

    [Description("Width sizing mode (Auto / SizeToAvailable / Manual / etc.).")]
    public string? WidthMode { get; init; }

    [Description("Height sizing mode.")]
    public string? HeightMode { get; init; }

    [Description("Configuration-key restriction.")]
    public string? ConfigurationKey { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    // ---- Type-gated properties (common across data controls) ----

    [Description("Backing table field. Used by data-bound controls (String/Integer/Real/Date/DateTime/Enum/CheckBox/ReferenceGroup).")]
    public string? DataField { get; init; }

    [Description("Backing data source name. Required for data-bound controls.")]
    public string? DataSource { get; init; }

    [Description("Display label override. Usually inherited from the EDT.")]
    public string? Label { get; init; }

    [Description("If true, the field must be populated before save.")]
    public bool? Mandatory { get; init; }

    [Description("Container caption (Group / Tab / TabPage). For typed data controls this is often the EDT label.")]
    public string? Caption { get; init; }

    [Description("Visual style override (control-kind-specific).")]
    public string? Style { get; init; }

    [Description("ViewEditMode override on the control.")]
    public string? ViewEditMode { get; init; }

    [Description("Static text content for Kind=StaticText.")]
    public string? Text { get; init; }

    [Description("For Kind=Button / MenuFunctionButton / CommandButton: button command name (e.g. New, Delete, Save).")]
    public string? Command { get; init; }

    [Description("For Kind=MenuFunctionButton: the AxMenuItem name to invoke.")]
    public string? MenuItemName { get; init; }

    [Description("For Kind=MenuFunctionButton: Display / Output / Action.")]
    public string? MenuItemType { get; init; }

    [Description("Recursive child controls (Group / Tab / TabPage / Grid / Container hold children).")]
    public List<FormControl>? Controls { get; init; }

    [Description("Per-control extension descriptor (e.g. QuickFilter, SegmentedEntry). Almost always null; the typed extensions used in MS forms preserve through this slot.")]
    public FormControlExtension? FormControlExtension { get; init; }

    [Description("Opaque catch-all for properties the typed shape doesn't model on this control. Round-trip preserves them.")]
    public Dictionary<string, string>? OtherProperties { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormControlKind
{
    /// <summary>Container with children (Group). Carries Caption.</summary>
    Group,
    /// <summary>Tab container.</summary>
    Tab,
    /// <summary>Tab page (child of Tab).</summary>
    TabPage,
    /// <summary>Grid container — rows of column controls.</summary>
    Grid,
    /// <summary>Generic container.</summary>
    Container,
    /// <summary>ActionPane (button toolbar at the top of a form).</summary>
    ActionPane,
    /// <summary>ActionPaneTab (sub-tab within an ActionPane).</summary>
    ActionPaneTab,
    /// <summary>ButtonGroup (groups buttons within an ActionPane tab).</summary>
    ButtonGroup,
    /// <summary>String edit / display field.</summary>
    String,
    /// <summary>Integer edit.</summary>
    Integer,
    /// <summary>Int64 edit (RecId-typed).</summary>
    Int64,
    /// <summary>Real (decimal) edit.</summary>
    Real,
    /// <summary>Date edit.</summary>
    Date,
    /// <summary>DateTime edit.</summary>
    DateTime,
    /// <summary>Enum combo box.</summary>
    ComboBox,
    /// <summary>Check box (typically bound to a NoYes-typed field).</summary>
    CheckBox,
    /// <summary>Reference group (typed lookup that joins via a relation).</summary>
    ReferenceGroup,
    /// <summary>Button (form-level command button).</summary>
    Button,
    /// <summary>MenuFunctionButton — opens an AxMenuItem.</summary>
    MenuFunctionButton,
    /// <summary>CommandButton — runs a built-in form command (New, Delete, etc.).</summary>
    CommandButton,
    /// <summary>Static text label.</summary>
    StaticText,
    /// <summary>SegmentedEntry — financial-dimension-aware multi-segment field.</summary>
    SegmentedEntry,
    /// <summary>Image control.</summary>
    Image,
    /// <summary>Menu button — a button that hosts a drop menu of sub-items.</summary>
    MenuButton,
    /// <summary>Drop-dialog button — a button that opens an inline drop dialog.</summary>
    DropDialogButton,
    /// <summary>Visual separator within a button group / action pane.</summary>
    ButtonSeparator,
    /// <summary>Time-of-day edit.</summary>
    Time,
    /// <summary>Radio-button group.</summary>
    RadioButton,
    /// <summary>Tree control (hierarchical view).</summary>
    Tree,
    /// <summary>List-view control.</summary>
    ListView,
    /// <summary>List-box control.</summary>
    ListBox,
    /// <summary>Anything else — preserves the original xsi:type via RawType.</summary>
    Other,
}

public sealed record FormControlExtension
{
    [Description("Extension name (e.g. 'QuickFilterControl').")]
    public string Name { get; init; } = string.Empty;
    public List<FormControlExtensionProperty>? ExtensionProperties { get; init; }
    public List<FormControlExtensionComponent>? ExtensionComponents { get; init; }
    public Dictionary<string, string>? OtherProperties { get; init; }
}

public sealed record FormControlExtensionProperty
{
    public string Name { get; init; } = string.Empty;
    public string? Type { get; init; }
    public string? Value { get; init; }
    public Dictionary<string, string>? OtherProperties { get; init; }
}

public sealed record FormControlExtensionComponent
{
    public string Name { get; init; } = string.Empty;

    [Description("Component kind: 'Composite' (holds nested Components) or 'Leaf' (holds ComponentType + ExtensionProperties). Null/omitted = the bare base component (Name+Tags only).")]
    public string? Kind { get; init; }

    [Description("Leaf only: the component type, e.g. 'FormFieldRelationDataLink'.")]
    public string? ComponentType { get; init; }

    [Description("Leaf only: true when MS-system-generated. Omitted when false.")]
    public bool? IsSystem { get; init; }

    [Description("Leaf only: typed extension properties (dataSource/dataField/etc.).")]
    public List<FormControlExtensionProperty>? ExtensionProperties { get; init; }

    [Description("Composite only: nested child components (recursive — composites can hold composites or leaves).")]
    public List<FormControlExtensionComponent>? Components { get; init; }

    public string? Tags { get; init; }
    public Dictionary<string, string>? OtherProperties { get; init; }
}
