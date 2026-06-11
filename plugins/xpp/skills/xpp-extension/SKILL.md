---
name: xpp-extension
description: Use when authoring extensions to D365 F&O AOT objects — AxTableExtension, AxFormExtension, AxEdtExtension, AxEnumExtension, AxViewExtension, AxDataEntityViewExtension, AxMenuExtension — or X++ class extensions via [ExtensionOf] (Chain of Command). Extensions are the only legal way to modify Microsoft-shipped objects since release 8.0; this skill covers what each type can/can't do and the naming/structure conventions.
---

# Authoring extensions

In F&O, you almost never modify a Microsoft-shipped object directly.
Instead, you author an **extension** in your own model that adds to
the base. This skill covers when to extend, the four metadata
extension types (Table/Form/Edt/Enum), class extensions via Chain of
Command (which use a different mechanism), and what's legal vs not on
each.

Load `dynamics-xpp:xpp-language` first if you haven't, and the matching base-object
skill (`dynamics-xpp:xpp-table`, `dynamics-xpp:xpp-form`, `dynamics-xpp:xpp-edt`, `dynamics-xpp:xpp-enum`, `dynamics-xpp:xpp-class`) for
the type you're extending.

---

## Why extensions exist

D365 ships with thousands of base objects. Customers and ISVs need to
customize them without taking ownership of the original — otherwise
every platform update would conflict with every customization.
Extensions solve this: your additions live in a separate metadata
file, named for the base, layered at runtime.

**Microsoft application models are sealed as of release 8.0.** You
*cannot* over-layer a shipped object by re-declaring it in your model.
Extensions are the only legal modification path.

### Rules of thumb

- **Adding to a Microsoft-shipped object** → extension, always.
- **Adding to your own object in your own model** → modify the base
  directly.
- **Adding to a shipped object that already has a custom extension in
  your model** → modify your existing extension; don't create a second
  one for the same purpose.
- **Adding to another team's/module's extension** → not supported.
  Extensions chain off the base only, not off other extensions.

---

## Naming convention

For the metadata extension types (Table/Form/Edt/Enum), the
extension's `Name` follows:

```
<BaseObjectName>.<ExtensionSuffix>
```

The `ExtensionSuffix` is conventionally the **model name** (per MS
naming guidelines and our `.dynamics-xpp/config.json`'s
`naming.extensionSuffix`). With a `ContosoRetail` model:

- `CustTable.ContosoRetail` (AxTableExtension)
- `SalesTable.ContosoRetail` (AxFormExtension)
- `CustAccount.ContosoRetail` (AxEdtExtension)
- `NoYes.ContosoRetail` (AxEnumExtension — requires base
  `IsExtensible=Yes`)

The on-disk file mirrors the `Name`:
`CustTable.ContosoRetail.xml` under the corresponding
`Ax*Extension` directory (`AxTableExtension`, `AxFormExtension`,
etc.).

Class-style extensions use a different mechanism and a different
name pattern — see `dynamics-xpp:xpp-project` for the full conventions
catalog, including how to extend code on forms / datasources /
controls / tables (always via an `AxClass` with `[ExtensionOf(...)]`
and the `_Extension` suffix).

---

## Authoring through dynamics-xpp

**Seven extension types have typed tools:**

| Extension | Tools |
|---|---|
| AxTableExtension | `xpp_create_table_extension`, `xpp_get_table_extension`, `xpp_patch_table_extension` |
| AxEdtExtension | `xpp_create_edt_extension`, `xpp_get_edt_extension`, `xpp_patch_edt_extension` |
| AxEnumExtension | `xpp_create_enum_extension`, `xpp_get_enum_extension`, `xpp_patch_enum_extension` |
| AxFormExtension | `xpp_create_form_extension`, `xpp_get_form_extension`, `xpp_patch_form_extension` |
| AxViewExtension | `xpp_create_view_extension`, `xpp_get_view_extension`, `xpp_patch_view_extension` |
| AxDataEntityViewExtension | `xpp_create_entity_extension`, `xpp_get_entity_extension`, `xpp_patch_entity_extension` |
| AxMenuExtension | `xpp_create_menu_extension`, `xpp_get_menu_extension`, `xpp_patch_menu_extension` |

The typed records reuse the base-type sub-records — adding new
fields to a table extension uses the same `TableField` polymorphic
shape as `xpp_create_table`; adding new controls to a form extension
uses the same `FormControl` polymorphic shape as `xpp_create_form`
(wrapped in a `FormExtensionControl` entry that pairs the control
with its `Parent` — the existing control on the base form where the
new control is inserted). Extension-specific concepts
(`FieldGroupExtensions` for extending an existing field group,
`FieldModifications` / `RelationModifications` /
`ControlModifications` / `DataSourceModifications` /
`ValueModifications` for changing properties on existing elements,
`PropertyModifications` for changing the host object's own
properties, `DataSourceReferences` on form extensions for binding
new controls to existing data sources) are dedicated typed records.

**Remaining extension types stay on the raw escape hatch.**
AxMenuItemExtension, AxSecurity*Extension, and the Workflow*
extensions all use:

- **New extension:** construct XML →
  `xpp_create_object("Ax<Type>Extension", model, xml)`.
- **Modify existing extension:**
  `xpp_get_object_xml("Ax<Type>Extension", name)` → edit →
  `xpp_update_object("Ax<Type>Extension", model, xml)`.

These will get typed tools in a follow-up batch. The write tools
pre-flight against the matching extension XSD
(`xpp://schema/AxTableExtension`, etc.).

### Patch semantics — read before patching an existing extension

**Collections on `xpp_patch_*` requests replace wholesale.** If the
extension already has 30 elements and you want to add 1, sending
`{ elements: [<new element>] }` deletes the other 29. To add one
element while preserving the rest, the canonical recipe is:

1. `xpp_get_<type>_extension` to fetch the current typed JSON.
2. Append your new entry to the relevant collection in-process.
3. Send the full collection back via `xpp_patch_<type>_extension`.

This is also true for `controls` (form extensions), `elements` (menu
extensions), `fields` / `fieldGroups` / `indexes` / `relations`
(table extensions), and `values` (enum extensions). Modifications
collections (`FieldModifications`, `ControlModifications`,
`ValueModifications`, `PropertyModifications`) behave the same way.

**Enum case is forgiving on input.** `xpp_get_*` returns enum values
in camelCase (`"menuItem"`, `"begin"`); `xpp_patch_*` accepts either
camelCase or PascalCase — the bridge normalizes. The get→mutate→patch
round-trip is symmetric; you do not need to re-case anything.

**Don't escape to raw `Edit` on the on-disk XML.** When you write
through `Edit` or `xpp_update_object` instead of the typed tools,
the changeset isn't auto-updated, the `.rnrproj` isn't auto-added,
and the indexer doesn't get a write-through signal. The typed patch
loop is supported end-to-end; the escape hatch is for shapes the
typed tools don't model yet (the remaining extension types listed
above).

---

## AxTableExtension

The biggest extension surface. Table extensions let you add to a
shipped (or any) table without modifying the base.

### Capabilities

**Can:**

- Add new fields (any field type — String, Int, Int64, Enum,
  UtcDateTime, Container, etc.).
- Add new field groups.
- Extend existing field groups (add fields to them).
- Add new indexes.
- Add new relations.
- Extend existing relations (`RelationExtensions`).
- Apply limited modifications to existing fields (labels, help text)
  via `FieldModifications`.
- Apply property modifications to table-level properties via
  `PropertyModifications`.

**Cannot:**

- Remove or rename existing fields.
- Change an existing field's data type.
- Remove indexes.
- Remove relations.
- Remove field groups.

### Structure

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxTableExtension xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
  <Name>CustTable.MyModuleExtension</Name>
  <Fields>
    <!-- new fields, same shape as in AxTable -->
  </Fields>
  <FieldGroups>
    <!-- new field groups -->
  </FieldGroups>
  <FieldGroupExtensions>
    <!-- additions to existing field groups -->
  </FieldGroupExtensions>
  <Indexes>
    <!-- new indexes -->
  </Indexes>
  <Relations>
    <!-- new relations -->
  </Relations>
  <RelationExtensions>
    <!-- additions to existing relations -->
  </RelationExtensions>
  <FieldModifications>
    <!-- label/help overrides on base fields -->
  </FieldModifications>
  <PropertyModifications>
    <!-- table-level property overrides -->
  </PropertyModifications>
</AxTableExtension>
```

Sections omitted may be left out entirely — unlike the base AxTable,
extension elements don't require empty placeholders.

### Minimum viable table extension

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxTableExtension xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
  <Name>CustTable.MyModuleExtension</Name>
  <Fields>
    <AxTableField xmlns="" i:type="AxTableFieldString">
      <Name>MyModule_TrackingCode</Name>
      <ExtendedDataType>MyModuleTrackingCode</ExtendedDataType>
      <Label>@MyLabels:TrackingCodeLabel</Label>
      <Mandatory>No</Mandatory>
      <AllowEdit>Yes</AllowEdit>
      <SaveContents>Yes</SaveContents>
      <AllowEditOnCreate>Yes</AllowEditOnCreate>
      <Visible>Yes</Visible>
      <AosAuthorization>None</AosAuthorization>
      <MinReadAccess>Auto</MinReadAccess>
      <IgnoreEDTRelation>Yes</IgnoreEDTRelation>
      <Null>Yes</Null>
      <IsSystemGenerated>No</IsSystemGenerated>
      <IsManuallyUpdated>No</IsManuallyUpdated>
      <IsObsolete>No</IsObsolete>
      <GeneralDataProtectionRegulation>None</GeneralDataProtectionRegulation>
      <SysSharingType>Duplicate</SysSharingType>
    </AxTableField>
  </Fields>
</AxTableExtension>
```

### Field-name prefix convention

**Always prefix extension-added fields with your module name** to
avoid collisions with other extensions of the same table:

- `MyModule_TrackingCode` — good (collision-safe).
- `TrackingCode` — bad (will collide if another module adds the same
  name).

Microsoft enforces the `<ModulePrefix>_` convention for ISV solutions
and recommends it for everyone. Two extensions adding fields with the
same name to the same base table is a compile error.

### Extending an existing field group

To add fields (already declared elsewhere — either on the base or by
your own extension) to a base table's field group:

```xml
<FieldGroupExtensions>
  <AxTableFieldGroupExtension>
    <Name>Identification</Name>
    <Fields>
      <AxTableFieldGroupField>
        <DataField>MyModule_TrackingCode</DataField>
      </AxTableFieldGroupField>
    </Fields>
  </AxTableFieldGroupExtension>
</FieldGroupExtensions>
```

The `<Name>` matches the existing group on the base table. The
referenced field must exist (either added by this extension or
already on the base).

---

## AxFormExtension

Form extensions are how you customize a shipped form. The lever is
slightly different: forms use **event handlers** on base controls
rather than overriding their methods.

### Capabilities

**Can:**

- Add datasources (typically joined to existing datasources).
- Add controls (new groups, new buttons, new field controls).
- Modify properties of existing controls via control-extension nodes.
- Add methods at the form level.
- Wire **event handler classes** that subscribe to base-form
  delegates.

**Cannot:**

- Remove base controls.
- Rename base controls.
- Fundamentally restructure the design tree (you can't move a
  control out of its parent container).
- Override base-form methods directly — use event handlers.

### Event-handler pattern

Form extension code typically lives in a **separate class**
(`MyForm_EventHandler`) with static methods marked
`[FormDataSourceEventHandler(...)]`, `[FormControlEventHandler(...)]`,
or `[FormEventHandler(...)]`. The form extension XML adds the
declarative structure (new controls, new datasource); the class adds
the behavior.

Look up the form's published events with `xpp_get_object_methods` on
the base form filtered to delegates, or read the base form's source
and look for delegate declarations.

### Skeleton

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxFormExtension xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
  <Name>CustTable.MyModuleExtension</Name>
  <DataSources>
    <!-- new datasources -->
  </DataSources>
  <ControlModifications>
    <!-- property overrides on existing controls -->
  </ControlModifications>
  <Controls>
    <!-- new controls -->
  </Controls>
</AxFormExtension>
```

See `dynamics-xpp:xpp-form` for the namespace rules — form extensions inherit the
same `xmlns="Microsoft.Dynamics.AX.Metadata.V6"` + `xmlns=""` reset
pattern for inner elements.

---

## AxEdtExtension

Adds to an existing EDT.

### Capabilities

**Can:**

- Add `TableReferences` (extend the lookup capability).
- Modify display/help labels via override.
- Add custom relations (when the relation model supports it).
- Extend properties that the base EDT didn't set.

**Cannot:**

- Change the underlying primitive type.
- Shrink the string size (`StringSize` can grow, not shrink).
- Add `ArrayElements` if the base doesn't have them.

### Structure

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxEdtExtension xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
  <Name>CustAccount.MyModuleExtension</Name>
  <TableReferences>
    <AxEdtTableReference>
      <Name>MyTrackingTable</Name>
      <Table>MyTrackingTable</Table>
      <RelatedField>AccountNum</RelatedField>
    </AxEdtTableReference>
  </TableReferences>
</AxEdtExtension>
```

---

## AxEnumExtension

Adds values to an existing base enum. Requires the base enum to be
marked `IsExtensible="Yes"`.

### Capabilities

**Can:**

- Add new enum values.

**Cannot:**

- Remove, rename, or renumber existing values.
- Modify the base enum's properties.
- Extend an enum that has `IsExtensible="No"` (or unspecified, which
  defaults to no).

### Structure

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxEnumExtension xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
  <Name>NoYes.MyModuleExtension</Name>
  <EnumValues>
    <AxEnumValue>
      <Name>MyModule_Maybe</Name>
      <Label>@MyLabels:MaybeLabel</Label>
      <Value>2</Value>
    </AxEnumValue>
  </EnumValues>
</AxEnumExtension>
```

### Picking `Value` integers

- The base enum's max value + 1 is the conventional starting point.
- Pick integers that won't collide with values added by other
  extensions. If multiple modules extend the same enum, **coordinate
  on value-integer ranges** to avoid collisions. The convention for
  enterprise installs is to reserve ranges per module
  (`MyModule` uses 100-199, `OtherModule` uses 200-299, etc.).
- Renumbering after the fact is a breaking change (see `dynamics-xpp:xpp-enum`).

### Value-name prefix convention

Same rule as table extensions — prefix with your module name to avoid
collisions:

- `MyModule_Maybe` — good.
- `Maybe` — bad if another extension adds `Maybe` too.

---

## AxMenuExtension

Menu extensions splice new elements into an MS-shipped menu's tree
or override properties on existing elements.

### Capabilities

**Can:**

- Add new elements at named insertion points: `MenuItem` (reference
  to an AxMenuItem*), `MenuReference` (link to another AxMenu),
  `Separator`, `SubMenu` (recursive), `Tile`.
- Position the insertion via `PositionType` + `PreviousSibling`:
  - `End` (default) — append to the parent's children.
  - `Begin` — prepend to the parent's children.
  - `AfterItem` / `BeforeItem` — position relative to a named sibling
    (set `PreviousSibling`).
- Modify properties on existing menu elements
  (`MenuElementModifications`).
- Modify the host menu's own properties (`PropertyModifications`).

**Cannot:**

- Remove base elements.
- Restructure the base tree (move an element across parents).

### Shape

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxMenuExtension xmlns:i="..." xmlns="Microsoft.Dynamics.AX.Metadata.V1">
  <Name>CustomerRelations.MyModuleExtension</Name>
  <Customizations />
  <Elements>
    <AxMenuExtensionElement xmlns="">
      <Parent>Common</Parent>
      <PositionType>AfterItem</PositionType>
      <PreviousSibling>SomeBaseElement</PreviousSibling>
      <MenuElement xmlns="" i:type="AxMenuElementMenuItem">
        <Name>MyModuleItem</Name>
        <MenuItemName>MyModuleItem</MenuItemName>
      </MenuElement>
    </AxMenuExtensionElement>
  </Elements>
  <MenuElementModifications />
  <PropertyModifications />
</AxMenuExtension>
```

The `<MenuElement>` child is polymorphic via `i:type` (same five
subtypes as `AxMenu`'s direct `Elements` children) but uses the
element name `MenuElement` instead of `AxMenuElement`. SubMenu's
inner `Elements` collection uses the regular `<AxMenuElement>`
shape.

### Default-strip pattern

The bridge strips default property values on round-trip — most
commonly `MenuItemType=Display` on `MenuItem` elements. Don't be
surprised when the read-back JSON omits properties you set to a
default value; the on-disk XML reflects MS's canonical form, not
your authored shape.

---

## Class extensions (different mechanism)

Class extensions exist but use a **different mechanism** — there is
**no `AxClassExtension` metadata type**. Class extensions are pure
X++: you write a separate AxClass with `[ExtensionOf(classStr(...))]`
and the runtime weaves it into the base class.

### Why different

Tables, forms, EDTs, and enums are heavily declarative — adding a
field/control/value is structural metadata. Classes are pure code,
and X++ already had Chain of Command (`[Hookable]` + `next`) for
intercepting method calls. The CoC mechanism subsumed what would have
been a class-extension metadata format.

### Pattern

```xpp
[ExtensionOf(classStr(SomeProcessor))]
final class SomeProcessor_Extension
{
    public int extensionMethod(int _arg) // New method on SomeProcessor
    {
        return _arg + 1;
    }

    protected boolean overrideFindOrCreateCustomer() // Wrap a base method
    {
        // Pre-call work
        next overrideFindOrCreateCustomer();
        // Post-call work
        return true;
    }
}
```

This class is a regular `AxClass` artifact stored at
`PackagesLocalDirectory\<Model>\AxClass\SomeProcessor_Extension.xml`.
It's NOT in an `AxClassExtension` directory; there is no such
directory.

### Requirements

- Must be marked `final`.
- Must use `[ExtensionOf(classStr(SomeClass))]` attribute.
- Should have `_Extension` suffix in the name (convention, not
  enforced).
- Cannot override methods from the base class with a new implementation
  — only wrap them via `next`.

### Capabilities

**Can:**

- Add new public methods (become part of the effective class — accessible
  on `someProcessor.extensionMethod(...)`).
- Add instance and static state (yes — class extensions can add fields).
- Wrap base methods via `next` (Chain of Command).

**Cannot:**

- Replace a base method's implementation (CoC requires `next`).
- Make existing public methods private.
- Add a `new()` constructor with parameters (the base's `new` is the
  only public one).

See `dynamics-xpp:xpp-class` for deeper coverage of the CoC mechanism, the `next`
keyword's sequencing semantics, and the override patterns.

---

## Extension chain ordering — be careful

If two extensions of the same base touch the same property, the
resolved value depends on **model load order**, which is non-
deterministic across deployments. Concretely:

- Two `AxTableExtension`s both adding fields with the same `Name`:
  compile error.
- Two `AxFormExtension`s both adding controls with the same `Name`:
  compile error.
- Two `AxClass` extensions wrapping the same method via `next`: order
  of execution is undefined.

**Don't rely on extension ordering.** Partition extensions by what
they touch — if two modules need to extend the same form, each module
should only modify its own controls/datasources, not race on shared
state.

---

## Things the XSDs can't tell you

- **Extension targets must exist.** Schema doesn't verify the base
  object referenced in `<Name>` actually exists. Missing base = build
  error.
- **Field-name collisions** with other extensions: schema passes; the
  build fails when both extensions load.
- **`IsExtensible="No"` on the base enum** — your enum extension
  validates the XSD and fails at load time.
- **BPC checks are stricter for extensions.** Field-name prefixes,
  label references (no inline literals), GDPR classification are all
  enforced more strictly than on base objects.
- **Chain-of-Command method ordering** across multiple class
  extensions: undefined. Don't write code that assumes a specific
  ordering.
- **Form-extension control-modification limits.** You can change
  properties on existing controls but can't move them across
  containers. Schema doesn't tell you which property changes are
  legal.

---

## See also

- The matching base-object skills: `dynamics-xpp:xpp-table`, `dynamics-xpp:xpp-form`, `dynamics-xpp:xpp-edt`,
  `dynamics-xpp:xpp-enum`, `dynamics-xpp:xpp-class`.
- `xpp://schema/AxTableExtension`, `xpp://schema/AxFormExtension`,
  `xpp://schema/AxEdtExtension`, `xpp://schema/AxEnumExtension` —
  authoritative XSDs.
- `dynamics-xpp:xpp-class` — Chain of Command details for class extensions.
