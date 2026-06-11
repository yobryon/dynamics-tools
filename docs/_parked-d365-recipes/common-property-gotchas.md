# Common Property Gotchas

**When to use:** Read this before any first-time `create_xpp_object` /
`execute_object_modification` call. Pitfalls that cost the most debugging time.

Last verified against D365 F&O docs: 2026-05-18

## 1. Enum-string values, not booleans

The metadata model is XML-backed. Boolean-looking properties are actually `NoYes`
enum values serialized as the strings `"Yes"` / `"No"`. JSON `true` / `false`
will fail validation in the C# layer (which calls
`ConvertParameterValue(rawValue, property.PropertyType)` against the real
`Microsoft.Dynamics.AX.Metadata.MetaModel` types — see
`ms-api-server/Services/D365ReflectionService.cs:984`).

```json
"Mandatory": "Yes"     // correct
"Mandatory": true      // WRONG — type conversion fails
```

Same pattern for: `Visible`, `AllowEdit`, `AllowEditOnCreate`, `SaveContents`,
`IgnoreEDTRelation`, `Null`, `IsObsolete`, `IsExtensible`, `UseEnumValue`,
`IsPublic`, `DataManagementEnabled`, etc.

## 2. `Name` — not `fieldName`, `objectName`, `propertyName`

Inside the `parameters` object of a modification, the property is **always**
called `Name` (matches the `Name` property on the underlying `AxTableField*`,
`AxMethod`, `AxEnumValue` etc.). The outer tool parameter that identifies the
target object is `objectName`, which is different.

```json
{
  "objectType": "AxTable",
  "objectName": "CustTable",          // outer: target object
  "modifications": [{
    "methodName": "AddField",
    "parameters": { "Name": "MyField", ... }   // inner: the new field's name
  }]
}
```

## 3. `concreteType` is required for any abstract base

D365 base types like `AxTableField`, `AxEdt`, and `AxFormControl` are abstract.
The factory falls back to inferring a concrete type from supplied parameters
(see `D365ReflectionService.cs:921` `DetermineConcreteTypeFromParameters`), but
inference is best-effort. **Always pass `concreteType` explicitly.**

Missing `concreteType` typically surfaces as:
`"Cannot create abstract type AxTableField and no concrete type could be determined"`.

## 4. Modifications are array-only

Even for one field. The handler at
`ms-api-server/Handlers/ExecuteObjectModificationHandler.cs` only accepts the
batch shape. A bare `methodName` at the top level is silently ignored on some
versions and rejected on others.

```json
// CORRECT — even for a single op
"modifications": [ { "methodName": "AddField", "parameters": { ... } } ]
```

The README's older "Creating a New Class" example
(`methodName` + `parameters` at the top level) is **out of date** — wrap in
`modifications: [...]`.

## 5. Forms: do NOT use `create_xpp_object`

The README explicitly warns: forms must go through `create_form`. `create_form`
performs pattern application, datasource wiring, and (for `DetailsMaster`,
`SimpleListDetails`, `ListPage`) auto-injection of grid field controls — none
of which `create_xpp_object` does. A form created via `create_xpp_object` will
fail best-practice (BP) pattern validation.

## 6. `Label` / `HelpText` accept literal strings OR `@SYS` references

```json
"Label": "Customer category"            // literal
"Label": "@SYS316407"                    // label-file reference
```

Both work. For shipped objects always prefer `@Module:LabelId` to keep things
translatable. The factory does **not** validate that the label ID exists.

## 7. Case sensitivity

D365 metadata property names are PascalCase and case-sensitive (`SaveContents`,
not `savecontents`). `concreteType` values must also be exact:
`"AxTableFieldString"` (not `"AxTableFieldstring"` or `"axTableFieldString"`).

## 8. `ExtendedDataType` reuse beats per-field property soup

When a field maps to a known business concept, set `ExtendedDataType` to the
matching EDT (e.g. `CustAccount`, `LogisticsAddressing`) rather than setting
`StringSize`, `Label`, `HelpText` individually. The EDT carries those properties
plus relations, lookup form references, and modeled type semantics. This is
the convention used throughout `ApplicationSuite/Foundation/AxTable/CustTable.xml`.

## 9. AX 2012 vs F&O — EDT relations are discouraged

EDT-level relations (the `Relations` node on an `AxEdt`) still compile and run,
but Microsoft guidance is now to define **foreign-key relations on the table**
instead — see
<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-relation>.
If you set `IgnoreEDTRelation: "Yes"` on a field (as in the README example),
you are explicitly opting out of the legacy EDT relation; this is the modern
default for new fields.

## 10. AX 2012 vs F&O — over-layering is gone

Do not assume you can change a Microsoft-shipped property by adding a duplicate
property in your model. All Microsoft application models are hard-sealed as of
release 8.0
(<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/changes-80>).
Any modification must be via an extension object (`AxTableExtension`,
`AxFormExtension`, etc.) or via class extension + delegates / event handlers.
`execute_object_modification` against a Microsoft-shipped object writes to the
overlay layer (`usr` / `cus` / `var`); for AOT objects in sealed models this
will compile-error in the AOS even if the MCP call succeeds.

## 11. AX 2012 vs F&O — `CacheLookup = EntireTable` is restricted

The `EntireTable` cache mode is not allowed on tables that participate in
table-inheritance (root tables) via the Application Explorer
properties window. See
<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/application-explorer-aot-properties#table-properties>.
For most new transactional tables, use `Found` or `FoundAndEmpty`. `NotInTTS`
is appropriate for parameter tables that change inside transactions.

## 12. DetailsMaster without datasources looks fine, then BP-fails

The `create_form` tool will happily create a `DetailsMaster` form with no
datasources, but you will get BP warnings (`TabPage.DataSource isn't empty`,
`Design.Caption isn't empty`) at compile. Provide at least one datasource and a
caption when calling `create_form` for any non-Dialog pattern. See
[form-creation.md](./form-creation.md).

## 13. Optional-looking parameters that aren't

The C# reflection layer marks any property in the target type's required-property
set as required. Empirically, for `AddField` on a table you must always pass:
`SaveContents`, `Mandatory`, `AllowEdit`, `AllowEditOnCreate`, `Visible`,
`AosAuthorization`, `MinReadAccess`, `IgnoreEDTRelation`, `Null`,
`IsSystemGenerated`, `IsManuallyUpdated`, `IsObsolete`,
`GeneralDataProtectionRegulation`, `SysSharingType`, plus `Name` and
`concreteType`. Missing any of them surfaces as
`"Parameter validation failed: Missing required parameters"`. The exhaustive
list per concrete type is in [table-field-defaults.md](./table-field-defaults.md).

## 14. Run discover_modification_capabilities for the source of truth

If a property in this recipe disagrees with a real
`discover_modification_capabilities` response, **trust the live response**.
The reflection layer reads the loaded `Microsoft.Dynamics.AX.Metadata.dll`
which may have drifted between platform updates.

## Sources

- `J:/Tools/dynamics-tools/README.md` (tool surface)
- `J:/Tools/dynamics-tools/ms-api-server/Services/D365ReflectionService.cs` (validation logic)
- `J:/Tools/dynamics-tools/ms-api-server/Handlers/ExecuteObjectModificationHandler.cs`
- `J:/AosService/PackagesLocalDirectory/ApplicationSuite/Foundation/AxTable/CustTable.xml`
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/application-explorer-aot-properties#table-properties>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/changes-80>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/add-relation>
