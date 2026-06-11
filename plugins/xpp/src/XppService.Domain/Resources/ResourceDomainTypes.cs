using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain.Resources;

// ----------------------------------------------------------------------------
// AxResource — an arbitrary file resource shipped with a model.
//
// The XML manifest is tiny (Name + FileName + RelativeUriInModelStore +
// TypeOfResource). The actual file content lives under the model's
// ResourceContent/<Subdir>/ tree on disk. Bridge handles the file copy
// when the manifest is written.
//
// Heavy use in retail/commerce: CDX seed-data XML manifests, JavaScript
// extensions, custom HTML/CSS for form controls, PBIX reports.
// ----------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceType
{
    [Description("XML document — CDX seed data, configuration manifests, anything XML.")]
    XmlDoc,
    [Description("JSON / CSV / binary data file.")]
    Data,
    [Description("HTML fragment, typically for embedded form controls.")]
    Html,
    [Description("CSS stylesheet.")]
    Styles,
    [Description("JavaScript file.")]
    Scripts,
    [Description("Plain-text file.")]
    Text,
    [Description("Power BI report (.pbix).")]
    PowerBIReport,
    [Description("Power Apps Component Framework control.")]
    PCFControl,
}

public sealed record CreateResourceRequest
{
    [Description("Resource AOT name. PascalCase. Conventionally describes the contents (e.g. 'CONRetailCDXSeedDataAX7').")]
    public string Name { get; init; } = string.Empty;

    [Description("On-disk file name (basename with extension), e.g. 'CONRetailCDXSeedDataAX7.xml' or 'Current Inventory.pbix'. Goes into ResourceContent/<Subdir>/<FileName>.")]
    public string FileName { get; init; } = string.Empty;

    [Description("Full path of the content file relative to PackagesLocalDirectory. Example: 'ContosoRetail\\\\ContosoRetail\\\\AxResource\\\\ResourceContent\\\\XmlDoc\\\\CONRetailCDXSeedDataAX7.xml'.")]
    public string RelativeUriInModelStore { get; init; } = string.Empty;

    [Description("Resource kind. Determines the ResourceContent subdirectory.")]
    public ResourceType TypeOfResource { get; init; }
}

public sealed record PatchResourceRequest
{
    public string? FileName { get; init; }
    public string? RelativeUriInModelStore { get; init; }
    public ResourceType? TypeOfResource { get; init; }
}

public sealed record GetResourceResponse
{
    public string Name { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RelativeUriInModelStore { get; init; } = string.Empty;
    public ResourceType TypeOfResource { get; init; }
}
