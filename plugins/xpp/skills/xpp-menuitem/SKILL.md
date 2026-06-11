---
name: xpp-menuitem
description: TRIGGER when authoring an AxMenuItemDisplay / AxMenuItemAction / AxMenuItemOutput. These are the entry points that make forms, runnable classes, and reports reachable from the UI. Required whenever you create a form (display), a runnable (action), or an SSRS report controller (output) — without a menu item, the object is unreachable.
---

# Menu items — Display, Action, Output

Menu items are the wire that connects a target AOT object (a
form, a runnable class, an SSRS report controller) to the F&O
UI. Every form a user opens, every "Run" button, every "Print"
command resolves through a menu item. They also carry the
security gate — `AxSecurityPrivilege` references menu items
under its `<EntryPoints>`, not the underlying object.

Three flavors, **same shape**:

| Type | Targets (typical `Object` is...) | Used for |
|---|---|---|
| `AxMenuItemDisplay` | a Form (or a class that opens a form) | "Open" any UI form |
| `AxMenuItemAction` | a Class with a main method (Runnable / Controller) | Buttons that DO something (post, validate, run) |
| `AxMenuItemOutput` | an SSRS report controller Class | Print menu / report buttons |

Because the three share most properties, this skill covers all
three together. Differences are called out inline.

---

## Read this skill when

- You created a form and need it reachable from a menu / button —
  write a Display menu item.
- You created a Runnable class (extends `RunBaseBatch`,
  `SysOperationServiceBase`, or has `main()`) — write an Action.
- You created an SSRS report controller — write an Output.
- You're wiring `AxSecurityPrivilege.EntryPoints` and need the
  menu item to point at.
- The MCP wrote a class/form for you and you want it actually
  callable from F&O UI without flipping to VS.

---

## Authoring through dynamics-xpp

The three menu-item types collapse into **one typed tool** with a
`kind` discriminator. The mapper dispatches to the right on-disk
ax_type (AxMenuItemDisplay / Output / Action):

| Tool | Purpose |
|---|---|
| `xpp_create_menuitem(request)` | Create. Set `kind` to Display / Output / Action; mapper picks the right ax_type. |
| `xpp_get_menuitem(name, kind)` | Read. Provide `kind` so the right ax_type is loaded. |
| `xpp_patch_menuitem(name, kind, patch)` | Patch. Provide `kind`. Merge-patch semantics. |

```jsonc
xpp_create_menuitem({
  "name": "conMyForm",
  "kind": "Display",
  "object": "conMyForm",
  "label": "@MyLabels:OpenMyForm",
  "subscriberAccessLevel": { "read": "Allow" }
})
```

Action-only fields (`stateMachine` / `stateMachineDataSource` /
`stateMachineTransitionTo`) emit only when `kind: "Action"`.

The raw `xpp_create_object` / `xpp_update_object` tools remain
available as an escape hatch.

---

## XML shape — same envelope, three roots

All three menu-item types live in the
`Microsoft.Dynamics.AX.Metadata.V1` default namespace (unlike
classes/tables/edts which sit in no-namespace). Don't forget
the `xmlns="Microsoft.Dynamics.AX.Metadata.V1"` declaration on
the root, or the bridge's DataContract pipeline will reject.

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxMenuItemDisplay xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
                   xmlns="Microsoft.Dynamics.AX.Metadata.V1">
    <Name>conMyForm</Name>
    <Label>@MyLabels:OpenMyForm</Label>
    <Object>conMyForm</Object>
    <SubscriberAccessLevel>
        <Read xmlns="">Allow</Read>
    </SubscriberAccessLevel>
</AxMenuItemDisplay>
```

Note the `<Read xmlns="">` child of `<SubscriberAccessLevel>` —
it drops out of the V1 namespace via empty xmlns. That's the
on-disk convention for that block; preserve it.

---

## Property checklist

Bold = required or near-required. Strikethrough where the
default works in 95% of cases and you can omit.

### Shared by Display / Action / Output

| Property | Notes |
|---|---|
| **`Name`** | The AOT item name. Convention `<prefix><Function>` (e.g., `conCustomerLookup`). Must be unique within the model. |
| **`Label`** | The text the user sees on the menu / button. Use a label reference `@LabelFile:LabelId` — BPC errors on hardcoded text. |
| **`Object`** | The target AOT element's `Name`. For Display, a form or controller class. For Action, the Runnable / Controller class. For Output, the report controller class. |
| `ObjectType` | What kind of element `Object` is. Common values: **`Class`**, `Form` (Display only), `SSRSReport` (Output only — but most teams use a controller Class instead). When omitted on a Display, `Object` is taken to be a Form name directly. |
| `HelpText` | Tooltip. Same label-ref rules as `Label`. |
| `ConfigurationKey` | Gate visibility on a feature-key. Useful for in-progress features. |
| `CountryRegionCodes` | Comma-separated ISO country codes. Hide the menu item except in those regions. |
| `LinkedPermissionObject` + `LinkedPermissionType` | "Use the same permissions as this other object." When set, the menu item's own permissions are ignored. Useful for "this menu item is just another way to reach FormX — apply FormX's permissions." |
| `NeededAccessLevel` | Minimum access (Read / Update / Create / Delete / Correct). Default is sensible — set only if you want to require more than the entry's natural permission. |
| `NeedsRecord` | `Yes` disables the button when the calling list page has no current record. Useful for "open detail" buttons. Default `No`. |
| `NormalImage` / `DisabledImage` | Icon name. Look up MS-shipped names; or reference your AxResource. Optional. |
| `Parameters` | Arbitrary string passed to the target's `main(Args _args)` via `_args.parm()`. The target reads with `args.parm()`. Useful for typed dispatches; preferred over `EnumParameter` for non-enum hints. |
| `SubscriberAccessLevel` | Subscriber license access. Almost always: `<Read xmlns="">Allow</Read>`. Required for the object to be reachable by external integrations. |
| `EnumParameter` + `EnumTypeParameter` | The pair that lets a menu item open a generic form with an enum-typed mode. `EnumTypeParameter` is the enum name (e.g., `LogisticsLocationRoleType`); `EnumParameter` is the enum value (e.g., `Consignee`). Target reads with `element.args().parmEnumType()` / `element.args().parm()`. |

### Display-specific

| Property | Notes |
|---|---|
| `OpenMode` | `Auto` (default), `View`, `Edit`, `New`. `View` opens read-only — important for security and for "look-but-don't-touch" menu items. |
| `Query` | Pre-canned query for the form's `InitialQuery` — restricts the records the form shows when opened from this menu. |
| `MultiSelect` | `Yes` lets the form open with multiple records pre-selected (used when this menu item is reached from a multi-select list page). Set to `Yes` on detail-from-list menu items where bulk actions apply. |
| `RunOn` | `Auto` / `Server` / `Called from` — controls execution tier. Default Auto. |

### Action-specific

| Property | Notes |
|---|---|
| `RunOn` | Where the class's `main()` executes. `Server` for batch/posting; `Called from` for caller-context UI. Default Auto. |
| `WaitForExecution` | `Yes` blocks UI until the action completes. `No` is fire-and-forget. Default Yes. |
| `ReturnClass` | Optional name of the return type the action produces — informational. |
| `CopyCallerQuery` | `Auto` (default) — pass the calling form's query into the action so it operates over the same record set. |

### Output-specific

| Property | Notes |
|---|---|
| `ReportDesign` | When `ObjectType=SSRSReport`, names the specific design within the report. Modern pattern instead uses a Controller class (`ObjectType=Class`); the controller picks the design. |
| `PromptPage` | If `Yes`, show the report's prompt page before running. Default Yes for interactive reports. |

---

## ObjectType — when to use which

This is the most-confused property. Decision tree:

- **Display menu item targeting a plain form** with no controller class:
  Omit `ObjectType` entirely (or set `Form`). `Object` is the form's `Name`.
- **Display menu item targeting a form via a class** (controller pattern, mode-driven form opening, dialog handling):
  `ObjectType=Class`, `Object` = the class name (which has `main()` that constructs the form).
- **Action menu item**: almost always `ObjectType=Class`, `Object` = the Runnable / Controller class.
- **Output menu item**: prefer `ObjectType=Class` pointing at an `SrsReportRunController`-derived controller. Use `ObjectType=SSRSReport` only for the rare bare-report case (and as MS notes, this is on its way out).

---

## Naming conventions

Standard F&O prefix from the dynamics-xpp:xpp-project config plus a
function name. Common patterns:

- `conCustomerLookup` — a Display targeting a customer-lookup form.
- `conPostShipment` — an Action targeting a posting class.
- `conPrintShippingLabel` — an Output for a shipping label report.

Match the menu item's `Name` to the `Object` it points at when
practical — the security tooling and skill skim more cleanly
when the entry point name and target name align.

---

## Security wiring (the part agents most often forget)

A menu item alone doesn't grant access. To make it usable by a
non-admin user, you need:

1. **The menu item exists** (this skill).
2. **An `AxSecurityPrivilege` references it** under `<EntryPoints>`
   with a permission level (typically `Read=Allow` for display
   buttons; higher for action buttons that mutate data).
3. **A duty or role contains the privilege**, granted to the user.

If you finish writing a menu item, your next step is usually a
privilege. See `dynamics-xpp:xpp-security` for the privilege XML
shape — specifically the `<EntryPoint>` child that holds the
back-reference.

`SubscriberAccessLevel` is a separate axis (it controls the
subscriber-license-tier gate, not RBAC). Always include
`<Read xmlns="">Allow</Read>` unless you have a specific reason
not to.

---

## Common gotchas

### Forgetting the V1 namespace declaration

`<AxMenuItemDisplay>` without `xmlns="Microsoft.Dynamics.AX.Metadata.V1"`
is rejected by the bridge's DataContract pipeline with a generic
"Expecting element ... from namespace ..." error. Always start
from `xpp_get_object_xml` on an existing one rather than
hand-typing the envelope.

### `<Read>` outside the V1 namespace

The `<SubscriberAccessLevel>` block uses `<Read xmlns="">Allow</Read>`
— note the empty default namespace on the child. Without that
attribute the validator and serializer both choke. This is on
disk in every real menu item file; preserve when round-tripping.

### Object name typos

`Object` is a free-text string — the bridge accepts unknown
target names. Errors surface only at compile (`xpp_compile`):
`BPErrorMenuItemReferenceNotFound`. Run `xpp_find_object` on
your intended target name before write to catch typos early.

### Linked permissions and self-permissions

When `LinkedPermissionObject`/`LinkedPermissionType` are set,
the menu item's OWN security properties (NeededAccessLevel, the
permission grants) are ignored. Don't waste effort filling them
in — the linked target's permissions apply instead. Common when
you have multiple navigation paths to the same form.

### EnumParameter for parameterized lookups

When opening a "generic" form (e.g., `LogisticsLocationSelectForm`)
that takes a role enum to know which kind of address to manage,
the pattern is:

```xml
<EnumParameter>Consignee</EnumParameter>
<EnumTypeParameter>LogisticsLocationRoleType</EnumTypeParameter>
<Object>LogisticsLocationSelectForm</Object>
<ObjectType>Class</ObjectType>
```

The target reads via `element.args().parmEnumType()` (returns
the EnumType ID) and `element.args().parm()` (returns the
specific value). Useful for sharing one form across many
context-specific menu items.

---

## Worked examples

### A Display menu item opening a form

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxMenuItemDisplay xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
                   xmlns="Microsoft.Dynamics.AX.Metadata.V1">
    <Name>conCustomerLookup</Name>
    <Label>@MyLabels:CustomerLookup</Label>
    <HelpText>@MyLabels:CustomerLookupHelp</HelpText>
    <MultiSelect>No</MultiSelect>
    <NeedsRecord>No</NeedsRecord>
    <Object>conCustomerLookup</Object>
    <SubscriberAccessLevel>
        <Read xmlns="">Allow</Read>
    </SubscriberAccessLevel>
</AxMenuItemDisplay>
```

`Object` matches the form name. No `ObjectType` — it's a form.

### An Action menu item triggering a Runnable

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxMenuItemAction xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
                  xmlns="Microsoft.Dynamics.AX.Metadata.V1">
    <Name>conRebuildPriceCache</Name>
    <Label>@MyLabels:RebuildPriceCache</Label>
    <Object>conPriceCacheRebuilder</Object>
    <ObjectType>Class</ObjectType>
    <RunOn>Server</RunOn>
    <SubscriberAccessLevel>
        <Read xmlns="">Allow</Read>
    </SubscriberAccessLevel>
</AxMenuItemAction>
```

`ObjectType=Class`, `RunOn=Server` because the rebuild is heavy
and shouldn't tie up the client. The Runnable has a `main()`
that runs.

### An Output menu item via a Controller class

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxMenuItemOutput xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
                  xmlns="Microsoft.Dynamics.AX.Metadata.V1">
    <Name>conPrintShippingLabel</Name>
    <Label>@MyLabels:PrintShippingLabel</Label>
    <Object>conShippingLabelController</Object>
    <ObjectType>Class</ObjectType>
    <NeedsRecord>Yes</NeedsRecord>
    <SubscriberAccessLevel>
        <Read xmlns="">Allow</Read>
    </SubscriberAccessLevel>
</AxMenuItemOutput>
```

Controller class drives the SSRS report — modern preferred
pattern. `NeedsRecord=Yes` because you can't print a label
without a shipment record selected.

### A parameterized Display for a role-driven generic form

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxMenuItemDisplay xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
                   xmlns="Microsoft.Dynamics.AX.Metadata.V1">
    <Name>conSHLogisticsLocationSelect_Forwarder</Name>
    <Label>@SYS312927</Label>
    <EnumParameter>CONSHTradePartyForwarder</EnumParameter>
    <EnumTypeParameter>LogisticsLocationRoleType</EnumTypeParameter>
    <LinkedPermissionObject>LogisticsLocationSelect</LinkedPermissionObject>
    <LinkedPermissionType>Form</LinkedPermissionType>
    <NormalImage>Map</NormalImage>
    <Object>LogisticsLocationSelectForm</Object>
    <ObjectType>Class</ObjectType>
</AxMenuItemDisplay>
```

Note `LinkedPermissionObject` — this menu item shares
permissions with `LogisticsLocationSelect`. No explicit
`SubscriberAccessLevel` since it inherits from the linked
target.

---

## Workflow

1. Author the target object first (form, class, report controller).
2. `xpp_create_object` the menu item with the shape above —
   match `Object` to the target's `Name`.
3. The menu item now exists. Add it to a privilege's
   `<EntryPoints>` (see `dynamics-xpp:xpp-security`).
4. Add the privilege to a duty / role.
5. Run `xpp_compile` — `BPErrorMenuItemReferenceNotFound` and
   `BPErrorMenuItemNotCoveredByPrivilege` flag issues; both are
   warnings that will surface in the build pipeline.

---

## See also

- `dynamics-xpp:xpp-security` — the privilege/role/duty side of the
  feature-completion loop. Almost always written after a menu
  item.
- `dynamics-xpp:xpp-class` — the Runnable / Controller / form-opener
  classes that menu items target.
- `dynamics-xpp:xpp-form` — when authoring the form that a Display
  menu item points at.
- `dynamics-xpp:xpp-project` — `xpp_compile` will surface BP findings
  like `BPErrorMenuItemNotCoveredByPrivilege`; use the suppress
  list there to silence rules you've decided don't apply.
- [MS: Application Explorer properties — Menu item properties](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/application-explorer-aot-properties#menu-item-properties) — authoritative reference.
- `dynamics-xpp:xpp-menu` — the parent container that groups menu
  items into navigable trees.

---

## Note on the typed authoring surface

The three menu-item types (AxMenuItemDisplay / Output / Action)
collapse to a single typed surface with a `kind` enum
discriminator. The domain shape lives in
`Xpp.Service.Domain.Menus.CreateMenuItemRequest`. All three
on-disk types share ~37 properties from the AxMenuItem base
class; only AxMenuItemAction adds StateMachine / DataSource /
TransitionTo. Single tool covers all three CRUD ops.

Like AxMenu, the root carries the
`Microsoft.Dynamics.AX.Metadata.V1` default namespace.
SubscriberAccessLevel's children individually reset to
`xmlns=""` — the typed mapper handles this.
