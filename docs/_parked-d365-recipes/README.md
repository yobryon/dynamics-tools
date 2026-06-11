# D365 F&O Object-Creation Recipes

Last verified against D365 F&O docs: 2026-05-18

Practical, parameter-level guidance for calling this MCP server's object-creation and
modification tools (`create_xpp_object`, `create_form`, `execute_object_modification`,
`delete_xpp_object`). Audience: a future Claude instance or developer who is about
to issue one of those calls and wants to get the parameter set right on the first try.

These recipes are grounded in:

- The actual MCP server code under `J:/Tools/dynamics-tools/ms-api-server/` (which uses
  reflection over the real `Microsoft.Dynamics.AX.Metadata.dll`, so concept names
  map 1:1 to D365 F&O AOT property names).
- Existing canonical XML in `J:/AosService/PackagesLocalDirectory/ApplicationSuite/`
  (e.g. `CustTable.xml`) for default property values.
- Current Microsoft Learn documentation (cited inline).

> Many concepts in D365 F&O carry over verbatim from AX 2012, but several have
> evolved. Where a once-recommended approach is now discouraged (e.g. EDT
> relations, over-layering, certain caching modes), the recipe calls it out
> explicitly. Look for **AX 2012 vs F&O** callouts inside each file.

## Recipes

| Recipe | Use when |
| --- | --- |
| [table-creation.md](./table-creation.md) | Creating an `AxTable` end-to-end (properties, indexes, title fields). |
| [table-field-defaults.md](./table-field-defaults.md) | Calling `execute_object_modification` with `AddField` — the ~17-parameter boilerplate, plus per-`concreteType` extras. |
| [form-creation.md](./form-creation.md) | Calling `create_form` — picking a pattern, datasource handling, auto field-control behavior. |
| [class-creation.md](./class-creation.md) | Creating an `AxClass` and adding methods / event handlers via `AddMethod`. |
| [enum-creation.md](./enum-creation.md) | Creating an `AxEnum` and adding values (with `IsExtensible` / `UseEnumValue`). |
| [edt-creation.md](./edt-creation.md) | Creating Extended Data Types (`AxEdtString`, `AxEdtInt`, `AxEdtEnum`, etc.). |
| [data-entity-with-staging.md](./data-entity-with-staging.md) | Creating a `AxDataEntityView` paired with a DMF staging table. |
| [common-property-gotchas.md](./common-property-gotchas.md) | Read this FIRST. Pitfalls (Yes/No vs true/false, `Name` vs `fieldName`, array format, etc.). |

## Suggested reading order for a new caller

1. [common-property-gotchas.md](./common-property-gotchas.md) — calibrate expectations.
2. The recipe matching your immediate task.
3. The matching MCP tool docs in `J:/Tools/dynamics-tools/README.md`.
4. If parameters feel off, call `discover_modification_capabilities` against the
   target object type — it queries the live `Microsoft.Dynamics.AX.Metadata`
   reflection model, which is always authoritative.
