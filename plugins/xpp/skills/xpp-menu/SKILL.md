---
name: xpp-menu
description: Use when authoring or modifying a D365 F&O navigation menu (AxMenu). Menus group menu items, sub-menus, separators, and tiles into navigable trees — they back module entry pages, navigation pane sections, and workspace tile grids. Different from menu items (the leaf "open this form/report/class" objects) — menus are the containers.
---

# Authoring menus (`AxMenu`)

A menu is a navigable tree of elements. Each element is one of:

- **`MenuItem`** — references an `AxMenuItem*` (Display / Output /
  Action) by name. The clickable leaf that opens a form / report /
  runs a class.
- **`MenuReference`** — links to another `AxMenu` (composes one
  menu into another).
- **`Separator`** — visual divider in the rendered tree.
- **`SubMenu`** — nested menu (carries its own Elements list,
  recursive). Used to model hierarchical navigation.
- **`Tile`** — references an `AxTile` (workspace count tiles
  embedded directly in the menu).

Menus back module entry pages, the navigation pane's per-module
sections, and workspace tile grids. They're the container layer
that aggregates menu items into a navigable structure.

Load `dynamics-xpp:xpp-menuitem` first — most menus exist primarily
to list menu items, so the leaf concepts come first.

---

## Authoring through dynamics-xpp

AxMenu uses **typed domain tools**:

| Tool | Purpose |
|---|---|
| `xpp_create_menu(request)` | Create a new AxMenu from a typed CreateMenuRequest. |
| `xpp_get_menu(name)` | Read a menu as its domain shape — scalars + the recursive element tree. |
| `xpp_patch_menu(name, patch)` | Apply a partial update. Merge-patch semantics; non-null `elements` replaces the whole tree. |

Elements are polymorphic on `kind` (MenuItem / MenuReference /
Separator / SubMenu / Tile). The mapper handles the on-disk
`<AxMenuElement xsi:type="...">` polymorphism — the agent never
authors xsi:type directly.

The raw `xpp_create_object` / `xpp_update_object` tools remain
available as an escape hatch.

---

## XML shape

The on-disk format lives in the
`Microsoft.Dynamics.AX.Metadata.V1` namespace (unique among the
typed-authoring types so far — most AOT XML is no-namespace):

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxMenu xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
        xmlns="Microsoft.Dynamics.AX.Metadata.V1">
  <Name>ContosoRetailSales</Name>
  <Label>@MyLabels:SalesMenu</Label>
  <Elements>
    <AxMenuElement xmlns="" i:type="AxMenuElementMenuItem">
      <Name>OpenCustomer</Name>
      <MenuItemName>CustTableDisplay</MenuItemName>
      <MenuItemType>Display</MenuItemType>
    </AxMenuElement>
    <AxMenuElement xmlns="" i:type="AxMenuElementSeparator">
      <Name>Sep1</Name>
    </AxMenuElement>
    <AxMenuElement xmlns="" i:type="AxMenuElementSubMenu">
      <Name>Admin</Name>
      <Label>@MyLabels:Admin</Label>
      <Elements>
        <AxMenuElement xmlns="" i:type="AxMenuElementMenuItem">
          <Name>AdminDash</Name>
          <MenuItemName>AdminDashboard</MenuItemName>
        </AxMenuElement>
      </Elements>
    </AxMenuElement>
  </Elements>
</AxMenu>
```

Notable conventions:

- **Root namespace** is `Microsoft.Dynamics.AX.Metadata.V1`. The
  typed mapper emits this; raw-XML authors must include it.
- **Element children use `xmlns=""`** to reset to no-namespace.
  This is the polymorphic-xsi:type pattern we use across the
  codebase, but combined with a namespaced root.
- **`<MenuItemType>` defaults to `Display`** and is stripped by
  the bridge on round-trip when default. Don't be surprised if
  set+read doesn't show it.

---

## Minimum viable menu (domain shape)

```jsonc
xpp_create_menu({
  "name": "ContosoRetailSales",
  "label": "@MyLabels:SalesMenu",
  "elements": [
    { "name": "OpenCustomer", "kind": "MenuItem",
      "menuItemName": "CustTableDisplay",
      "menuItemType": "Display" },
    { "name": "Sep1", "kind": "Separator" },
    { "name": "Admin", "kind": "SubMenu",
      "label": "@MyLabels:Admin",
      "elements": [
        { "name": "AdminDash", "kind": "MenuItem",
          "menuItemName": "AdminDashboard",
          "menuItemType": "Display" }
      ] }
  ]
})
```

Key points:

- **Top-level `name`** is the AOT name; must match the file name
  and be unique within the model.
- **Element `name`** is the slot identifier within the menu —
  doesn't have to match `menuItemName`, but conventionally does.
- **SubMenu is recursive**: each SubMenu has its own `elements`
  list, depth unbounded.
- **MenuItemType** is required to disambiguate when the same name
  exists across Display / Output / Action menu items. Default
  Display.

---

## Element kinds in detail

### MenuItem

References an existing `AxMenuItem*` by name:

| Field | Notes |
|---|---|
| `menuItemName` | Required. Name of the AxMenuItemDisplay / Output / Action. |
| `menuItemType` | Display / Output / Action. Default Display. |
| `displayInContentArea` | Whether the launched form replaces the workspace content area. Default true for Display. |
| `parameters` | Optional parameter blob passed via Args::parm(). |
| `shortCut` | Keyboard shortcut hint (e.g. `Ctrl+Alt+C`). |
| `showParentModule` | Whether the parent module surfaces alongside this item in breadcrumb / search. |

### MenuReference

Composes another `AxMenu` into this one:

| Field | Notes |
|---|---|
| `menuName` | Required. Name of the referenced AxMenu. |

Useful for shared sub-menus that several modules need.

### Separator

No additional fields. Renders as a divider line.

### SubMenu

A nested menu with its own elements:

| Field | Notes |
|---|---|
| `label` | Display label for the sub-menu header. |
| `elements` | Recursive — the SubMenu carries its own elements list. |
| `image` | Optional icon (NormalImage, DisabledImage, etc.). |
| `configurationKey` | Restricts the sub-menu's visibility. |
| `featureClass` | Feature-flag gating. |
| `menuItemName` / `menuItemType` | If the SubMenu header is itself clickable, references the underlying menu item. |

### Tile

References a workspace tile (`AxTile`):

| Field | Notes |
|---|---|
| `tile` | Required. Name of the AxTile. |

---

## Property checklist for the menu itself

| Property | Typical | Notes |
|---|---|---|
| `name` | (required) | PascalCase, unique. |
| `label` | `@<File>:<Id>` | Display label. |
| `configurationKey` | (optional) | Restricts visibility. |
| `countryRegionCodes` | (optional) | ISO codes. |
| `featureClass` | (optional) | Feature-flag gating. |
| `image` | (optional) | Menu icon. |
| `menuItemTarget` | usually null | If the menu is itself clickable (launches a target), `{ menuItemName, menuItemType }`. |
| `setCompany` | false | If true, opening sets user's company context. |
| `shortCut` | (optional) | Keyboard shortcut. |
| `elements` | (the meat) | The tree of child elements. |

---

## Common patterns

### Module entry menu

```jsonc
xpp_create_menu({
  "name": "ContosoRetailModule",
  "label": "@CONEcom:ModuleName",
  "elements": [
    { "name": "Common", "kind": "SubMenu", "label": "@SYS:Common",
      "elements": [/* … list items …  */] },
    { "name": "Inquiries", "kind": "SubMenu", "label": "@SYS:Inquiries",
      "elements": [/* … */] },
    { "name": "Periodic", "kind": "SubMenu", "label": "@SYS:Periodic",
      "elements": [/* … */] },
    { "name": "Setup", "kind": "SubMenu", "label": "@SYS:Setup",
      "elements": [/* … */] }
  ]
})
```

The conventional five sections (Common, Journals, Inquiries,
Reports, Periodic, Setup) anchor the navigation-pane layout.

### Workspace tile menu

```jsonc
xpp_create_menu({
  "name": "ContosoRetailWorkspaceTiles",
  "elements": [
    { "name": "OpenOrders", "kind": "Tile", "tile": "ContosoRetailOpenOrders" },
    { "name": "Sep1", "kind": "Separator" },
    { "name": "TodaysShipments", "kind": "Tile", "tile": "ContosoRetailTodaysShipments" }
  ]
})
```

---

## Common gotchas

- **Element `name` vs `menuItemName` mismatch is allowed but
  confusing.** Convention is to match. The `name` is the slot
  identifier within the menu; `menuItemName` is the target
  reference.
- **`MenuItemType` strip on default**. The bridge strips
  `<MenuItemType>Display</MenuItemType>` on round-trip because
  Display is the default. Don't be surprised if a read shows
  `kind: MenuItem` without `menuItemType` — assume Display.
- **Cycles via MenuReference**. Menu A referencing Menu B
  referencing Menu A is a runtime crash. The platform doesn't
  detect this at compile time. Test the navigation path.
- **Forgetting the V1 namespace** when authoring raw XML. The
  typed tool handles it; raw-XML authoring must include
  `xmlns="Microsoft.Dynamics.AX.Metadata.V1"` at the root.

---

## See also

- `dynamics-xpp:xpp-menuitem` — the leaf objects this menu's
  MenuItem elements reference.
- `dynamics-xpp:xpp-tile` — the workspace count-tile objects
  referenced by Tile elements.
- `dynamics-xpp:xpp-security` — security privileges grant access
  to menu items; the menu's visibility flows through them.
- `xpp://schema/AxMenu` — authoritative XSD (used by the
  `xpp_create_object` escape hatch).

---

## Note on the typed authoring surface

AxMenu is one of the **eighth and ninth** AOT types on the
typed-authoring layer (paired with the AxMenuItem* family).
The agent-facing API lives in
`Xpp.Service.Domain.Menus.CreateMenuRequest`.

This is the first type that uses a **non-empty default
namespace** at the root (`Microsoft.Dynamics.AX.Metadata.V1`).
The mapper handles namespace setup automatically; polymorphic
element children reset to `xmlns=""` for the standard
xsi:type-discriminator pattern. The SubscriberAccessLevel block
(on menu items) goes a step further — its children individually
reset to `xmlns=""` while the container stays in the V1
namespace.

Element children are polymorphic on `kind`, collapsing the five
on-disk subtypes (AxMenuElementMenuItem / MenuReference /
Separator / SubMenu / Tile) into a single enum on the request.
The `SubMenu` variant is recursive — its `elements` list lets a
menu nest indefinitely.

If you need a property the domain shape doesn't cover,
escape-hatch via `xpp_get_object_xml` + `xpp_update_object`.
See `plugins/xpp/docs/domain-coverage.md`.
