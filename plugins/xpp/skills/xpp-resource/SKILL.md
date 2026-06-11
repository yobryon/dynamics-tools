---
name: xpp-resource
description: TRIGGER when authoring AxResource — file resources shipped with a model. Heavy use in retail/commerce for CDX seed-data XML manifests, Power BI reports (.pbix), Power Apps Component Framework controls, custom JS/CSS/HTML for form-control extensions, and CSV/JSON data files.
---

# Authoring AxResource

`AxResource` is the AOT type for **arbitrary file resources** that
ship inside a model. The XML manifest is tiny — it just registers a
file by AOT name and tells the platform what kind of resource it is.
The actual file content lives under
`ResourceContent/<Subdir>/<FileName>` inside the model folder.

## When you use this

- **Retail / Commerce CDX seed-data manifests** — `*.xml` files
  registered under `TypeOfResource=XmlDoc`, the canonical use case
  in retail/commerce database-sync customizations.
- **Power BI embedded reports** (`.pbix`) under
  `TypeOfResource=PowerBIReport`.
- **Power Apps Component Framework (PCF) controls** under
  `TypeOfResource=PCFControl` — bundled JS/HTML/CSS.
- **Custom HTML / CSS / JavaScript** for embedded form controls
  (Html / Styles / Scripts).
- **Plain data files** (CSV, JSON, etc.) under `Data` or `Text`.

## Typed authoring tools

- `xpp_create_resource` — author a new AxResource manifest from
  a typed CreateResourceRequest.
- `xpp_get_resource` — read an existing manifest.
- `xpp_patch_resource` — partial update (FileName /
  RelativeUriInModelStore / TypeOfResource).

## Shape

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxResource xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONRetailCDXSeedDataAX7</Name>
    <FileName>CONRetailCDXSeedDataAX7.xml</FileName>
    <RelativeUriInModelStore>ContosoRetail\ContosoRetail\AxResource\ResourceContent\XmlDoc\CONRetailCDXSeedDataAX7.xml</RelativeUriInModelStore>
    <TypeOfResource>XmlDoc</TypeOfResource>
</AxResource>
```

Four fields, no nesting:

- **`Name`** — AOT name (PascalCase, conventional prefix per
  project naming).
- **`FileName`** — basename with extension.
- **`RelativeUriInModelStore`** — full path relative to
  `PackagesLocalDirectory`. The convention is
  `<Model>\<Module>\AxResource\ResourceContent\<Subdir>\<FileName>`
  where `<Subdir>` matches `TypeOfResource`:

  | TypeOfResource | Subdir |
  |---|---|
  | `XmlDoc` | `XmlDoc` |
  | `Data` | `Data` |
  | `Html` | `Html` |
  | `Styles` | `Styles` |
  | `Scripts` | `Scripts` |
  | `Text` | `Text` |
  | `PowerBIReport` | `PowerBIReport` |
  | `PCFControl` | `PCFControl` |

- **`TypeOfResource`** — discriminator: `XmlDoc` / `Data` / `Html` /
  `Styles` / `Scripts` / `Text` / `PowerBIReport` / `PCFControl`.

## Important gotcha — plant the content file first

`xpp_create_resource` writes the **manifest** XML only. The
**content file** at `RelativeUriInModelStore` must already exist
on disk before you create the manifest — the bridge does not
copy or stage content for you.

Workflow:

1. Drop your file (e.g. `MyModelCDXSeedData.xml`) at
   `PackagesLocalDirectory\<Model>\<Module>\AxResource\ResourceContent\XmlDoc\MyModelCDXSeedData.xml`.
2. Call `xpp_create_resource` with the matching FileName +
   RelativeUriInModelStore + TypeOfResource.

If you call create-resource without the content file in place,
the manifest still writes successfully but the runtime resource
won't resolve at runtime — and the next compile will flag the
missing file.

## CDX seed-data workflow (the common retail case)

When extending Commerce database-sync to push custom fields /
tables out to the channel database, you typically:

1. Author the table extension (`xpp_create_table_extension`) or
   table (`xpp_create_table`) that adds the fields you want
   synced.
2. Write the CDX seed-data XML by hand (or generate via X++
   `RetailCDXSeedDataAX7::generate*`) under
   `ResourceContent\XmlDoc\<Model>RetailCDXSeedDataAX7.xml`.
3. Call `xpp_create_resource` to register the manifest:
   ```json
   {
       "name": "MyModelRetailCDXSeedDataAX7",
       "fileName": "MyModelRetailCDXSeedDataAX7.xml",
       "relativeUriInModelStore": "MyModel\\MyModel\\AxResource\\ResourceContent\\XmlDoc\\MyModelRetailCDXSeedDataAX7.xml",
       "typeOfResource": "XmlDoc"
   }
   ```
4. The `RetailCDXSeedDataAX7` resource family is picked up by
   the platform's seed-data loader on next CDX deployment.

## See also

- `dynamics-xpp:xpp-table-extension` for the table-side of the CDX
  story.
- The platform's `RetailCDXSeedDataAX7` class for the seed-data
  XML format itself (this skill covers the manifest, not the
  format).
