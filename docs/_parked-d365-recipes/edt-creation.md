# Extended Data Type Creation (AxEdt)

**When to use:** Defining a reusable typed value (a string with a specific size
+ label, an int with formatting, a domain-specific Real). Strongly preferred
over typing each field's properties individually.

Last verified against D365 F&O docs: 2026-05-18

## Concrete EDT types

`AxEdt` is abstract — always supply `concreteType`:

| `concreteType` | Underlying primitive | Use for |
| --- | --- | --- |
| `AxEdtString` | string | Account numbers, codes, names, descriptions. |
| `AxEdtInt` | int32 | Quantities, ordinals, small numeric IDs. |
| `AxEdtInt64` | int64 | RecId surrogate keys, large numeric IDs. |
| `AxEdtReal` | real (decimal) | Amounts, ratios, percentages. |
| `AxEdtDate` | date | Calendar dates. |
| `AxEdtUtcDateTime` | utcdatetime | Timestamps. Prefer over the older `AxEdtDateTime`. |
| `AxEdtEnum` | enum | A reusable enum-typed value with extra label/help. |
| `AxEdtGuid` | guid | |
| `AxEdtContainer` | container | Rare — typed containers. |
| `AxEdtTime` | timeofday | |

## Create

```json
{
  "objectName": "AcmeProjectStatusCode",
  "objectType": "AxEdtString",
  "layer": "usr",
  "properties": {
    "StringSize": 20,
    "Label": "Status code",
    "HelpText": "Code identifying a project status.",
    "Extends": "",
    "ConfigurationKey": ""
  }
}
```

> TODO: Verify whether the MCP tool dispatches concrete EDT types via
> `objectType: "AxEdtString"` directly or via `objectType: "AxEdt"` +
> `concreteType` in `properties`. Empirically `AxEdtString` works; the C#
> factory determines concrete types from properties when given the abstract
> base (`D365ReflectionService.cs:921`). When in doubt, run
> `discover_modification_capabilities` against `AxEdt`.

### Extending another EDT

The proper way to specialize an EDT is via the `Extends` property. The child
inherits all of the parent's properties unless overridden.

```json
{
  "objectName": "AcmeCustomerNote",
  "objectType": "AxEdtString",
  "layer": "usr",
  "properties": {
    "Extends": "Notes",
    "Label": "Customer note"
  }
}
```

`AcmeCustomerNote` now has whatever StringSize / formatting `Notes` carries,
plus a more specific label.

### Enum EDT

```json
{
  "objectName": "AcmeProjectStatusTypeEdt",
  "objectType": "AxEdtEnum",
  "layer": "usr",
  "properties": {
    "EnumType": "AcmeProjectStatusType",
    "Label": "Project status"
  }
}
```

`EnumType` is required for `AxEdtEnum`.

## Properties that matter

| Property | Applies to | Notes |
| --- | --- | --- |
| `StringSize` | `AxEdtString` | Default 10. Set explicitly. |
| `Label`, `HelpText` | all | Inherited if `Extends` is set; specify here to override. |
| `Extends` | all | Parent EDT. Empty for root. |
| `EnumType` | `AxEdtEnum` | Required. |
| `NoOfDecimals` | `AxEdtReal` | Default 2. |
| `DisplayLength` | all | Visual width hint on forms. |
| `DisplayHeight` | string, container | Multi-line text height. |
| `Alignment` | numeric, string | `Left` / `Right` / `Center` / `Auto`. |
| `ReferenceTable` | `AxEdtString`, `AxEdtInt64` (legacy) | Target table for the legacy EDT relation. **Discouraged in F&O** — see below. |
| `Relations` | all (legacy) | Legacy EDT relation node. **Discouraged.** |
| `FormHelp` | all | Form reference for lookups (rarely set on new EDTs). |
| `ConfigurationKey` | all | Hides values when the key is disabled. |
| `CountryRegionCodes` | all | Localized visibility. |

## AX 2012 vs F&O divergence points

- **EDT relations are discouraged.** AX 2012 routinely modeled the foreign-key
  relation on the EDT itself; F&O guidance is to put the relation on the
  *table*, and to set `IgnoreEDTRelation: "Yes"` on table fields
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-relation>).
  Don't add a `Relations` node to a new EDT unless you're matching legacy
  behavior intentionally.
- **EDT extensions support a narrow property set.** When *extending* an
  existing EDT (`AxEdtExtension`), the only modifiable properties are: Form
  help, Label, String size, Help text
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/customization-overlayering-extensions#edt-extensions>).
  Changing `Extends` or adding relations through an EDT extension is not
  supported.
- **Use the right financial-dimension EDT.** Mistaking
  `LedgerDimensionAccount`, `LedgerDimensionDefaultAccount`,
  `DimensionDefault`, `DimensionDynamicAccount`, and
  `DimensionDynamicDefaultAccount` causes silent data corruption when the
  dimension framework scanners run
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/financial/dimension-fk-edt-usage>).
  This is one of the few places EDT choice matters for correctness, not just
  style.
- **`AxEdtDateTime` is legacy; use `AxEdtUtcDateTime`** for new timestamps,
  consistent with `AxTableFieldUtcDateTime` on tables.

## Pitfalls

- The factory **infers** `concreteType` from `properties` when given the
  abstract `AxEdt`, but inference can be wrong. Always be explicit with
  `objectType: "AxEdtString"` (or pass `concreteType` in `properties` if your
  tool wrapper expects that).
- Setting `Extends` to an EDT that itself has `Extends` works (chains
  arbitrarily) but inheritance order matters: a chain of overrides can produce
  surprising final values. Use `inspect_xpp_object` to confirm the resolved
  property set after creation.

## Sources

- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/customization-overlayering-extensions#edt-extensions>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-relation>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/financial/dimension-fk-edt-usage>
- `J:/Tools/dynamics-tools/ms-api-server/Services/D365ReflectionService.cs` (concrete-type inference)
