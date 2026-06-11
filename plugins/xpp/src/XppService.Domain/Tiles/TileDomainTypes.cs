using System.ComponentModel;
using System.Text.Json.Serialization;
using Xpp.Service.Domain.Menus;

namespace Xpp.Service.Domain.Tiles;

// ----------------------------------------------------------------------------
// AxTile — workspace / dashboard tile.
//
// Root namespace is V1 (same as AxMenu). Single flat shape, no children.
// The tile usually points at an AxMenuItemDisplay / AxMenuItemAction (the
// thing clicked when the user clicks the tile) and renders a count, KPI,
// or static link.
// ----------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileType
{
    [Description("Static tile that opens the linked target on click.")]
    Link,
    [Description("Renders the count of matching rows from Query.")]
    Count,
    [Description("Renders a KPI gauge.")]
    KPI,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileSize
{
    Small,
    Medium,
    Wide,
    Large,
    ShortWide,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileDisplay
{
    TextOnly,
    TextAndImage,
    BackgroundImage,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileFormViewOption
{
    Grid,
    Details,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileOpenMode
{
    View,
    New,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileRefreshFrequency
{
    AsFastAsPermissible,
    OneMinute,
    FiveMinutes,
    FifteenMinutes,
    OneHour,
    FourHours,
    TwentyFourHours,
}

public sealed record CreateTileRequest
{
    [Description("Tile name. PascalCase. Conventionally matches the target menu item / data entity.")]
    public string Name { get; init; } = string.Empty;

    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? ConfigurationKey { get; init; }
    public bool? IsObsolete { get; init; }

    [Description("Tile kind: Link (static), Count (row-count of Query), or KPI (gauge). Default Link.")]
    public TileType? Type { get; init; }

    [Description("Tile size on the workspace grid.")]
    public TileSize? Size { get; init; }

    [Description("Visual rendering: TextOnly / TextAndImage / BackgroundImage.")]
    public TileDisplay? TileDisplay { get; init; }

    [Description("Menu item invoked when the tile is clicked. Used together with MenuItemType to dispatch to the AxMenuItem* family.")]
    public string? MenuItemName { get; init; }

    [Description("Type of the target menu item: Display / Output / Action. Defaults to Display when MenuItemName is set.")]
    public MenuItemKind? MenuItemType { get; init; }

    [Description("Form-view mode when the click opens a list page: Grid / Details.")]
    public TileFormViewOption? FormViewOption { get; init; }

    [Description("Whether the click opens the form for new-record entry (New) or for viewing existing rows (View).")]
    public TileOpenMode? OpenMode { get; init; }

    [Description("Parameter blob forwarded to the target menu item.")]
    public string? Parameters { get; init; }

    [Description("Whether to copy the caller's existing query/filter when invoking the target.")]
    public bool? CopyCallerQuery { get; init; }

    [Description("Whether the user-pinned filter applies (vs. forcing the tile's own filter).")]
    public bool? ApplyFilter { get; init; }

    [Description("Backing AxQuery for Count / KPI tiles.")]
    public string? Query { get; init; }

    [Description("KPI definition name when Type=KPI.")]
    public string? KPI { get; init; }

    [Description("How often the count/KPI value is recomputed. Cached at that frequency per user.")]
    public TileRefreshFrequency? RefreshFrequency { get; init; }

    [Description("Whether the user can force-refresh the cached value from the tile UI.")]
    public bool? AllowUserCacheRefresh { get; init; }

    [Description("Normal-state image resource (AOT resource name when ImageLocation=AOTResource).")]
    public string? NormalImage { get; init; }

    [Description("Where the image lives. Common: AOTResource.")]
    public string? ImageLocation { get; init; }

    [Description("External URL when the tile is a hyperlink rather than a menu-item target.")]
    public string? URL { get; init; }
}

public sealed record PatchTileRequest
{
    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? ConfigurationKey { get; init; }
    public bool? IsObsolete { get; init; }
    public TileType? Type { get; init; }
    public TileSize? Size { get; init; }
    public TileDisplay? TileDisplay { get; init; }
    public string? MenuItemName { get; init; }
    public MenuItemKind? MenuItemType { get; init; }
    public TileFormViewOption? FormViewOption { get; init; }
    public TileOpenMode? OpenMode { get; init; }
    public string? Parameters { get; init; }
    public bool? CopyCallerQuery { get; init; }
    public bool? ApplyFilter { get; init; }
    public string? Query { get; init; }
    public string? KPI { get; init; }
    public TileRefreshFrequency? RefreshFrequency { get; init; }
    public bool? AllowUserCacheRefresh { get; init; }
    public string? NormalImage { get; init; }
    public string? ImageLocation { get; init; }
    public string? URL { get; init; }
}

public sealed record GetTileResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? HelpText { get; init; }
    public string? ConfigurationKey { get; init; }
    public bool? IsObsolete { get; init; }
    public TileType? Type { get; init; }
    public TileSize? Size { get; init; }
    public TileDisplay? TileDisplay { get; init; }
    public string? MenuItemName { get; init; }
    public MenuItemKind? MenuItemType { get; init; }
    public TileFormViewOption? FormViewOption { get; init; }
    public TileOpenMode? OpenMode { get; init; }
    public string? Parameters { get; init; }
    public bool? CopyCallerQuery { get; init; }
    public bool? ApplyFilter { get; init; }
    public string? Query { get; init; }
    public string? KPI { get; init; }
    public TileRefreshFrequency? RefreshFrequency { get; init; }
    public bool? AllowUserCacheRefresh { get; init; }
    public string? NormalImage { get; init; }
    public string? ImageLocation { get; init; }
    public string? URL { get; init; }
}
