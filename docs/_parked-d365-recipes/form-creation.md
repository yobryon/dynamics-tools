# Form Creation (create_form)

**When to use:** Any time you need a new `AxForm`. **Never** use
`create_xpp_object` for forms — the README is explicit about this and the
`create_form` tool exists specifically to handle pattern application,
datasource wiring, and field-control injection.

Last verified against D365 F&O docs: 2026-05-18

## Pattern catalog and when to pick each

`create_form({"mode": "list_patterns"})` returns the live catalog (36+
entries). The patterns that matter day-to-day:

| Pattern | Use it for | Required artifacts |
| --- | --- | --- |
| `SimpleList` | Single-grid maintenance for small entities (≤10 columns), no details FastTabs. Example: `CustGroup`. | ActionPane, Custom Filter group, Grid. |
| `SimpleListDetails` | Medium-complexity master where the list itself drives a details pane on the right. Example: `PaymTerm`. **Variants:** *ListGrid* (2-3 cols, preferred), *TabularGrid* (4-5 cols), *Tree*. | Navigation list + details pane. |
| `DetailsMaster` | The default details form for complex master data (Customer, Product). FastTabs + integrated list grid. | FastTabs, grid tab page, ActionPane. |
| `DetailsTransaction` | Header + lines (order, journal). | Header FastTabs, lines grid. |
| `ListPage` | **Discouraged for new 1:1 list-to-details scenarios.** Microsoft now merges ListPage + DetailsMaster into a single form. Use only when there is no backing details page, or when multiple details pages share one list. | ActionPane, Custom Filter, Grid. |
| `Dialog` | Modal dialog gathering a small parameter set (e.g. before running a report). | Any number of groups; no list/grid required. |
| `DropDialog` | A drop-down dialog launched from a button on another form to provide context for an action. | Compact field group. |
| `Lookup` | Custom lookup forms (replace the auto-generated lookup). | Grid sized for a lookup. |
| `FormPart` | FactBox embedded on another form. | Card or grid content. |
| `Workspace` | Operational workspace landing pages. | Workspace tile + sections. |
| `Wizard` | Multi-step task with Back/Next. | Tabbed pages. |
| `TableOfContents` | Setup-style "parameters" form. | Tabs of grouped fields. |
| `SimpleDetails` | Single-record focused, no list. | FastTabs only. |

Pattern names are case-sensitive; mismatches fail silently and the tool will
ignore the pattern. The default in `CreateFormHandler.cs:49` is
`SimpleListDetails`.

## Auto-injected field controls

For patterns that *require* a grid, the MCP `create_form` tool inspects the
first datasource and auto-injects up to 4 grid columns when fields named
`RecId`, `Name`, `Description`, or `Code` exist on the table. This logic lives
in `CreateFormHandler.cs:670-697` and applies to:

- `SimpleList`
- `SimpleListDetails`
- `DetailsMaster`
- `ListPage`

Field-control types are mapped from the table field's concrete type:

| Table field type | Generated form control |
| --- | --- |
| `AxTableFieldString` / `AxTableFieldContainer` | `AxFormStringControl` |
| `AxTableFieldEnum` | `AxFormComboBoxControl` |
| `AxTableFieldInt` | `AxFormIntegerControl` |
| `AxTableFieldInt64` | `AxFormInt64Control` |
| `AxTableFieldReal` | `AxFormRealControl` |
| `AxTableFieldDate` | `AxFormDateControl` |
| `AxTableFieldUtcDateTime` | `AxFormDateTimeControl` |
| `AxTableFieldGuid` | `AxFormGuidControl` |
| `AxTableFieldTime` | `AxFormTimeControl` |

If your target table has none of `RecId`/`Name`/`Description`/`Code` as field
names, the tool produces a pattern-valid but empty grid; you will need
follow-up `execute_object_modification` calls to add the right controls. This
is the most common reason `DetailsMaster` "looks created but BP-fails."

## Worked examples

### List-detail form on a custom table

```json
{
  "mode": "create",
  "formName": "AcmeProjectStatusList",
  "patternName": "SimpleListDetails",
  "patternVersion": "UX7 1.0",
  "dataSources": ["AcmeProjectStatus"],
  "modelName": "AcmeProjects"
}
```

The tool will: apply the pattern; add `AcmeProjectStatus` as the primary
datasource; create ActionPane and Custom Filter group; create a grid; inject
field controls for any of `RecId`/`Name`/`Description`/`Code` it finds.

### Dialog form (no datasources)

```json
{
  "mode": "create",
  "formName": "AcmeRecalcDialog",
  "patternName": "Dialog",
  "modelName": "AcmeProjects"
}
```

Dialog patterns specifically do **not** require datasources. The tool builds
the design and stops.

### DetailsMaster with explicit datasources

```json
{
  "mode": "create",
  "formName": "AcmeProjectStatusDetails",
  "patternName": "DetailsMaster",
  "patternVersion": "UX7 1.0",
  "dataSources": ["AcmeProjectStatus", "AcmeProjectStatusLine"],
  "modelName": "AcmeProjects"
}
```

The first datasource is the master; subsequent datasources are joined-by-default
on the master's primary index where a relation exists. Confirm joins with
`inspect_xpp_object` after creation.

## DataSources parameter — accepted shapes

`CreateFormHandler.cs` normalizes three shapes:

```json
"dataSources": ["CustTable"]                  // array (preferred)
"dataSources": "CustTable"                    // single string
"dataSources": "CustTable,SalesTable"         // comma-separated
```

All three are equivalent. Use the array form for clarity.

## AX 2012 vs F&O divergence points

- **`FormTemplate` / `InteractionClass` are no longer required** when building
  new pages
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/list-page-form-pattern#pattern-changes>).
  Patterns now replace templates for layout enforcement.
- **`ListPage` + `DetailsMaster` should be one form, not two.** The 2012 split
  is discouraged for 1:1 list/details. Use `DetailsMaster` and let its integrated
  grid handle the list role
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/details-master-form-pattern#pattern-changes>).
- **The list-page `Preview` pane has been eliminated.** Don't try to model it.
- **`Task Single` / `Task Double` are legacy.** Per Microsoft: "should be used
  only for migration, not for new forms." Pick `DetailsMaster` or
  `DetailsTransaction` instead
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/select-form-pattern#form-pattern-reference-guide>).
- **Foundation buttons (`View/Edit`, `New`, `Delete`, `Save`, `Refresh`,
  `Attachments`, `Export to Excel`) come for free** on `DetailsMaster`. Don't
  re-add them; they will duplicate.

## Pitfalls

- `mode` is required. Calling `create_form` without `mode` returns the patterns
  list (or an error, depending on version) — neither helps you create a form.
- `patternVersion` defaults to `"UX7 1.0"`. If your D365 platform update has
  newer pattern versions only, the call will hard-fail with "Pattern not found
  with any version" — call `mode: "list_patterns"` and pick from the live list.
- `modelName` must be a model that already exists (it isn't created). Default
  is `ApplicationSuite`. **You almost never want the default for a new form** —
  set it to your custom model.

## Sources

- `J:/Tools/dynamics-tools/README.md` (tool parameters)
- `J:/Tools/dynamics-tools/ms-api-server/Handlers/CreateFormHandler.cs`
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/select-form-pattern>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/details-master-form-pattern>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/list-page-form-pattern>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/application-explorer-aot-properties#form-design-properties>
