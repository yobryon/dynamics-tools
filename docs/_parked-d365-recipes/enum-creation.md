# Enum Creation (AxEnum)

**When to use:** Creating a new base enum, or a status / type discriminator.
For *extending* a Microsoft enum, use an `AxEnumExtension` (not covered here —
the same `AddEnumValue` modification call applies, just against the extension).

Last verified against D365 F&O docs: 2026-05-18

## Create the enum shell

```json
{
  "objectName": "AcmeProjectStatusType",
  "objectType": "AxEnum",
  "layer": "usr",
  "properties": {
    "Label": "Project status type",
    "HelpText": "Lifecycle state of an Acme project.",
    "IsExtensible": "Yes",
    "UseEnumValue": "No"
  }
}
```

Key properties:

| Property | Value | Why |
| --- | --- | --- |
| `Label` | string | Lookup display. |
| `HelpText` | string | Tooltips. |
| `IsExtensible` | `"Yes"` (recommended) | Allows downstream models to add values. Required as `"Yes"` for any enum you ship to AppSource or expect customers to extend. |
| `UseEnumValue` | `"No"` (recommended, paired with `IsExtensible=Yes`) | Tells the platform that integer values may be non-deterministic across deployments. Don't write code that compares integer values. |
| `StyledImageInfo` | `"Yes"` / `"No"` | Defaults `"No"`. Set `"Yes"` only if values map to icons. |

> **Always set both `IsExtensible: "Yes"` and `UseEnumValue: "No"` for new enums.**
> Microsoft's guidance: "Set `IsExtensible` to Yes, and `UseEnumValue` to No"
> (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/extensible-enums>).
> The flip side: you can no longer rely on the integer value, range comparisons,
> or modeled query ranges on the enum.

## Add values

```json
{
  "objectType": "AxEnum",
  "objectName": "AcmeProjectStatusType",
  "modifications": [
    {
      "methodName": "AddEnumValue",
      "parameters": {
        "concreteType": "AxEnumValue",
        "Name": "Draft",
        "Label": "Draft",
        "Value": 0
      }
    },
    {
      "methodName": "AddEnumValue",
      "parameters": {
        "concreteType": "AxEnumValue",
        "Name": "Active",
        "Label": "Active",
        "Value": 1
      }
    },
    {
      "methodName": "AddEnumValue",
      "parameters": {
        "concreteType": "AxEnumValue",
        "Name": "Closed",
        "Label": "Closed",
        "Value": 2
      }
    }
  ]
}
```

### Notes on values

- The **first element should be `0`**. Microsoft guidance: "The first element
  in the enum gets a value of 0 (zero). Therefore, you can still use an
  extensible enum with the `not` operator. The only exception is when the first
  element of the enum had a non-zero value before you made the enum extensible."
- `Value` is informational once `UseEnumValue=No`; at deployment time the
  platform may assign different integers. Do not rely on it.
- `Name` must be a valid X++ identifier (no spaces, no leading digit).

## AX 2012 vs F&O divergence points

- **`IsExtensible` did not exist in AX 2012.** All enums were sealed at the
  metadata level and could only be extended via over-layering. F&O introduced
  `IsExtensible` + `UseEnumValue` as the supported pattern
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/changes-80#extensible-enumerations>).
- **Range comparisons on extensible enums are unsupported.** `if (myEnum.A > myEnum.B)`
  worked in 2012; in F&O with `UseEnumValue=No` the integer values are not
  deterministic, so this is undefined behavior. Use `switch` statements with
  a `default` that either subscribes to a delegate or does nothing.
- **`switch` blocks should not `throw` in the default case.** Microsoft
  refactored the standard application to remove these throws so extension enum
  values could be handled by post-event subscribers
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-enum-value>).
  Mirror this in your own code.
- **Enum extensions add elements at the *end*** (the IDE auto-suggests the next
  free ordinal as of BC 2023 wave 1 — same pattern in F&O).

## Pitfalls

- Setting `IsExtensible: "Yes"` while leaving `UseEnumValue: "Yes"` is
  contradictory; the second flag asks the platform to honor explicit integers,
  which defeats extension. Pair them as documented.
- Don't include `Value` if you want the platform to auto-assign — but the MCP
  factory currently expects it. Pass `0`, `1`, `2`, ... as a hint and accept
  that deployments may renumber.
- An enum's `Name` ends up in the form-control type (`AxFormComboBoxControl`)
  that the form layer derives — see
  `ms-api-server/Handlers/CreateFormHandler.cs:686`. Renaming an enum after
  the fact breaks every consuming form.

## Sources

- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/extensible-enums>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-enum-value>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/changes-80#extensible-enumerations>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/customization-overlayering-extensions#enum-extensions>
