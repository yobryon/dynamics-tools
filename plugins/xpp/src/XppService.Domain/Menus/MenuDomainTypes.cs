using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain.Menus;

// ----------------------------------------------------------------------------
// AxMenu — the navigation menu tree.
//
// The on-disk root carries a non-empty default namespace
// `Microsoft.Dynamics.AX.Metadata.V1`, unique among the typed-authoring
// types so far. Polymorphic child elements (AxMenuElementMenuItem etc.)
// reset to xmlns="" the way the polymorphic-child pattern works
// everywhere else.
// ----------------------------------------------------------------------------

public sealed record CreateMenuRequest
{
    [Description("AOT name. PascalCase. Conventionally matches the module + functional area (e.g. AccountsReceivable, BatchJobs).")]
    public string Name { get; init; } = string.Empty;

    [Description("Display label (label-ref preferred).")]
    public string? Label { get; init; }

    [Description("Restricts the menu by configuration key.")]
    public string? ConfigurationKey { get; init; }

    [Description("Comma-separated ISO country codes.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Feature-class binding for runtime feature-flag gating.")]
    public string? FeatureClass { get; init; }

    [Description("Marks the menu obsolete.")]
    public bool? IsObsolete { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("Image / icon settings.")]
    public MenuImageOptions? Image { get; init; }

    [Description("If this menu is itself a clickable menu item (rare), the underlying MenuItemName + MenuItemType. Usually null.")]
    public MenuItemReference? MenuItemTarget { get; init; }

    [Description("Optional parameters passed to the target menu item (string blob, kernel-parsed).")]
    public string? Parameters { get; init; }

    [Description("If true, opening this menu sets the user's company context.")]
    public bool? SetCompany { get; init; }

    [Description("Keyboard shortcut hint.")]
    public string? ShortCut { get; init; }

    [Description("Menu elements (children). Polymorphic on Kind: MenuItem (refers to an AxMenuItem*), MenuReference (refers to another AxMenu), Separator, SubMenu (recursive nested menu), Tile.")]
    public List<MenuElement>? Elements { get; init; }

    [Description("Advanced / less-common scalar properties.")]
    public AdvancedMenuOptions? Advanced { get; init; }
}

public sealed record PatchMenuRequest
{
    public string? Label { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public string? FeatureClass { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public MenuImageOptions? Image { get; init; }
    public MenuItemReference? MenuItemTarget { get; init; }
    public string? Parameters { get; init; }
    public bool? SetCompany { get; init; }
    public string? ShortCut { get; init; }
    public List<MenuElement>? Elements { get; init; }
    public AdvancedMenuOptions? Advanced { get; init; }
}

public sealed record GetMenuResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public string? FeatureClass { get; init; }
    public bool? IsObsolete { get; init; }
    public string? Tags { get; init; }
    public MenuImageOptions? Image { get; init; }
    public MenuItemReference? MenuItemTarget { get; init; }
    public string? Parameters { get; init; }
    public bool? SetCompany { get; init; }
    public string? ShortCut { get; init; }
    public List<MenuElement>? Elements { get; init; }
    public AdvancedMenuOptions? Advanced { get; init; }
}

public sealed record AdvancedMenuOptions
{
    public Visibility Visibility { get; init; } = Visibility.Public;
}

public sealed record MenuImageOptions
{
    [Description("Normal-state image / icon resource name.")]
    public string? NormalImage { get; init; }

    [Description("Disabled-state image / icon resource name.")]
    public string? DisabledImage { get; init; }

    [Description("Where the normal image lives (resource location).")]
    public string? ImageLocation { get; init; }

    [Description("Where the disabled image lives.")]
    public string? DisabledImageLocation { get; init; }
}

public sealed record MenuItemReference
{
    [Description("Name of the AxMenuItem* this references.")]
    public string MenuItemName { get; init; } = string.Empty;

    [Description("Menu-item kind the name refers to: Display / Output / Action.")]
    public MenuItemKind MenuItemType { get; init; }
}

// ---- Menu elements (polymorphic on Kind) -----------------------------------

public sealed record MenuElement
{
    [Description("Element name. PascalCase. For MenuItem references this is conventionally the same as MenuItemName.")]
    public string Name { get; init; } = string.Empty;

    [Description("Element kind. MenuItem: references an AxMenuItem* by name. MenuReference: links to another AxMenu. Separator: visual divider. SubMenu: nested menu (recursive). Tile: tile reference.")]
    public MenuElementKind Kind { get; init; }

    [Description("Whether the element is visible. Default true.")]
    public bool? Visible { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    // ---- Kind=MenuItem ----

    [Description("For Kind=MenuItem: name of the AxMenuItem* this references.")]
    public string? MenuItemName { get; init; }

    [Description("For Kind=MenuItem (and SubMenu): the menu-item kind the name refers to: Display / Output / Action.")]
    public MenuItemKind? MenuItemType { get; init; }

    [Description("For Kind=MenuItem: whether the launched form opens in the content area (true) or as a dialog.")]
    public bool? DisplayInContentArea { get; init; }

    [Description("For Kind=MenuItem / SubMenu: parameter blob passed to the target.")]
    public string? Parameters { get; init; }

    [Description("Keyboard shortcut hint.")]
    public string? ShortCut { get; init; }

    [Description("Whether the parent module name appears alongside this item in breadcrumb / search displays.")]
    public bool? ShowParentModule { get; init; }

    // ---- Kind=MenuReference ----

    [Description("For Kind=MenuReference: name of the AxMenu this links to.")]
    public string? MenuName { get; init; }

    // ---- Kind=SubMenu ----

    [Description("For Kind=SubMenu: display label.")]
    public string? Label { get; init; }

    [Description("Configuration-key restriction.")]
    public string? ConfigurationKey { get; init; }

    [Description("Country-code restriction.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Feature-class binding.")]
    public string? FeatureClass { get; init; }

    [Description("For Kind=SubMenu: image options.")]
    public MenuImageOptions? Image { get; init; }

    [Description("For Kind=SubMenu: child elements (recursive).")]
    public List<MenuElement>? Elements { get; init; }

    [Description("For Kind=SubMenu: whether opening sets the company context.")]
    public bool? SetCompany { get; init; }

    // ---- Kind=Tile ----

    [Description("For Kind=Tile: name of the AxTile this references.")]
    public string? Tile { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MenuElementKind { MenuItem, MenuReference, Separator, SubMenu, Tile }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MenuItemKind { Display, Output, Action }

// ----------------------------------------------------------------------------
// AxMenuItem (single typed shape; Kind drives Display / Output / Action).
// All three on-disk types share an identical AxProp scalar set plus
// AxMenuItemAction's 3 state-machine fields.
// ----------------------------------------------------------------------------

public sealed record CreateMenuItemRequest
{
    [Description("Menu-item AOT name. PascalCase. By convention the name ends with the kind, e.g. CustTableDisplay / CustTableEditAction.")]
    public string Name { get; init; } = string.Empty;

    [Description("Menu-item kind. Display opens an AxForm. Output runs an SSRS-style AxReport. Action invokes an AxClass main() method.")]
    public MenuItemKind Kind { get; init; }

    [Description("Target object name. For Display: AxForm. For Output: AxReport. For Action: AxClass (with a static main(Args)).")]
    public string? Object { get; init; }

    [Description("Underlying AOT object type. Usually inferred — set explicitly only when the metamodel needs a hint (e.g. Class vs Job).")]
    public string? ObjectType { get; init; }

    [Description("Display label.")]
    public string? Label { get; init; }

    [Description("Help text shown in tooltips.")]
    public string? HelpText { get; init; }

    [Description("Optional parameters passed to the target (string blob).")]
    public string? Parameters { get; init; }

    [Description("Optional EnumType + EnumValue parameter that the target receives via Args::parmEnum().")]
    public string? EnumTypeParameter { get; init; }
    public string? EnumParameter { get; init; }

    [Description("Reference AxQuery name when the target uses a parameterized query (Display menu items often pre-filter a form's data source).")]
    public string? Query { get; init; }

    [Description("Report-design ref when Kind=Output and there are multiple designs.")]
    public string? ReportDesign { get; init; }

    [Description("Whether the launched form/report needs an active record to operate on. Default false.")]
    public bool? NeedsRecord { get; init; }

    [Description("Whether the launched form accepts multi-selected records (right-click on a list).")]
    public bool? MultiSelect { get; init; }

    [Description("Form open mode when Kind=Display: View / Edit / New / Auto.")]
    public string? OpenMode { get; init; }

    [Description("Form view option (Details / Grid).")]
    public string? FormViewOption { get; init; }

    [Description("If true, the target accepts the caller's query for filtering. Default false.")]
    public bool? CopyCallerQuery { get; init; }

    [Description("Whether the menu item is reachable from the navigation root.")]
    public bool? AllowRootNavigation { get; init; }

    [Description("Restricts the menu item by configuration key.")]
    public string? ConfigurationKey { get; init; }

    [Description("Country-specific configuration key (further narrows visibility).")]
    public string? CountryConfigurationKey { get; init; }

    [Description("Comma-separated ISO country codes.")]
    public string? CountryRegionCodes { get; init; }

    [Description("Cross-company / sharing scope.")]
    public string? OperationalDomain { get; init; }

    [Description("Marks the menu item obsolete.")]
    public bool? IsObsolete { get; init; }

    [Description("Feature-class binding.")]
    public string? FeatureClass { get; init; }

    [Description("Free-form tag string.")]
    public string? Tags { get; init; }

    [Description("User-license maintenance band.")]
    public string? MaintainUserLicense { get; init; }

    [Description("User-license view band.")]
    public string? ViewUserLicense { get; init; }

    [Description("Permission-policy linkage.")]
    public string? LinkedPermissionType { get; init; }
    public string? LinkedPermissionObject { get; init; }
    public string? LinkedPermissionObjectChild { get; init; }

    [Description("Extended data-security policy when the menu item is gated by XDS.")]
    public string? ExtendedDataSecurity { get; init; }

    [Description("Required CRUD permission grants.")]
    public string? CreatePermissions { get; init; }
    public string? ReadPermissions { get; init; }
    public string? UpdatePermissions { get; init; }
    public string? DeletePermissions { get; init; }
    public string? CorrectPermissions { get; init; }

    [Description("Per-operation subscriber access overrides.")]
    public Views.SubscriberAccessLevel? SubscriberAccessLevel { get; init; }

    [Description("Image / icon settings.")]
    public MenuItemImageOptions? Image { get; init; }

    // ---- Kind=Action only -------------------------------------------------

    [Description("For Kind=Action: optional state-machine binding (table name).")]
    public string? StateMachine { get; init; }
    public string? StateMachineDataSource { get; init; }
    public string? StateMachineTransitionTo { get; init; }

    [Description("Advanced / less-common scalar properties.")]
    public AdvancedMenuItemOptions? Advanced { get; init; }
}

public sealed record PatchMenuItemRequest
{
    public string? Object { get; init; }
    public string? ObjectType { get; init; }
    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? Parameters { get; init; }
    public string? EnumTypeParameter { get; init; }
    public string? EnumParameter { get; init; }
    public string? Query { get; init; }
    public string? ReportDesign { get; init; }
    public bool? NeedsRecord { get; init; }
    public bool? MultiSelect { get; init; }
    public string? OpenMode { get; init; }
    public string? FormViewOption { get; init; }
    public bool? CopyCallerQuery { get; init; }
    public bool? AllowRootNavigation { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public string? OperationalDomain { get; init; }
    public bool? IsObsolete { get; init; }
    public string? FeatureClass { get; init; }
    public string? Tags { get; init; }
    public string? MaintainUserLicense { get; init; }
    public string? ViewUserLicense { get; init; }
    public string? LinkedPermissionType { get; init; }
    public string? LinkedPermissionObject { get; init; }
    public string? LinkedPermissionObjectChild { get; init; }
    public string? ExtendedDataSecurity { get; init; }
    public string? CreatePermissions { get; init; }
    public string? ReadPermissions { get; init; }
    public string? UpdatePermissions { get; init; }
    public string? DeletePermissions { get; init; }
    public string? CorrectPermissions { get; init; }
    public Views.SubscriberAccessLevel? SubscriberAccessLevel { get; init; }
    public MenuItemImageOptions? Image { get; init; }
    public string? StateMachine { get; init; }
    public string? StateMachineDataSource { get; init; }
    public string? StateMachineTransitionTo { get; init; }
    public AdvancedMenuItemOptions? Advanced { get; init; }
}

public sealed record GetMenuItemResponse
{
    public string Name { get; init; } = string.Empty;
    public MenuItemKind Kind { get; init; }
    public string? Object { get; init; }
    public string? ObjectType { get; init; }
    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? Parameters { get; init; }
    public string? EnumTypeParameter { get; init; }
    public string? EnumParameter { get; init; }
    public string? Query { get; init; }
    public string? ReportDesign { get; init; }
    public bool? NeedsRecord { get; init; }
    public bool? MultiSelect { get; init; }
    public string? OpenMode { get; init; }
    public string? FormViewOption { get; init; }
    public bool? CopyCallerQuery { get; init; }
    public bool? AllowRootNavigation { get; init; }
    public string? ConfigurationKey { get; init; }
    public string? CountryConfigurationKey { get; init; }
    public string? CountryRegionCodes { get; init; }
    public string? OperationalDomain { get; init; }
    public bool? IsObsolete { get; init; }
    public string? FeatureClass { get; init; }
    public string? Tags { get; init; }
    public string? MaintainUserLicense { get; init; }
    public string? ViewUserLicense { get; init; }
    public string? LinkedPermissionType { get; init; }
    public string? LinkedPermissionObject { get; init; }
    public string? LinkedPermissionObjectChild { get; init; }
    public string? ExtendedDataSecurity { get; init; }
    public string? CreatePermissions { get; init; }
    public string? ReadPermissions { get; init; }
    public string? UpdatePermissions { get; init; }
    public string? DeletePermissions { get; init; }
    public string? CorrectPermissions { get; init; }
    public Views.SubscriberAccessLevel? SubscriberAccessLevel { get; init; }
    public MenuItemImageOptions? Image { get; init; }
    public string? StateMachine { get; init; }
    public string? StateMachineDataSource { get; init; }
    public string? StateMachineTransitionTo { get; init; }
    public AdvancedMenuItemOptions? Advanced { get; init; }
}

public sealed record AdvancedMenuItemOptions
{
    public Visibility Visibility { get; init; } = Visibility.Public;
}

public sealed record MenuItemImageOptions
{
    public string? NormalImage { get; init; }
    public string? DisabledImage { get; init; }
    public string? ImageLocation { get; init; }
    public string? DisabledImageLocation { get; init; }
    public string? NormalResource { get; init; }
    public string? DisabledResource { get; init; }
}
