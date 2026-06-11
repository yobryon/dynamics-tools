---
name: xpp-tile
description: TRIGGER when authoring an AxTile (workspace tile). Tiles are abstract metadata elements that get placed on workspace forms via FormTileButton controls. Required when building workspaces that show counts of pending work, KPIs, links, or navigation entries.
---

# Tile — Workspace navigation elements

A tile is a rectangular button on a workspace that combines
navigation (opens a menu item or URL) with a dynamic data
display (count of pending work, KPI value). Workspaces are the
activity-oriented pages users land on after login — they're
the F&O equivalent of a dashboard.

Tiles are decoupled into two parts:

1. **`AxTile`** — the abstract tile definition: type, query,
   label, refresh policy. **This skill.**
2. **`AxFormTileButton`** — the placement of that tile inside
   a workspace form's `<TileContainer>`. See `dynamics-xpp:xpp-form` /
   `dynamics-xpp:xpp-pattern-workspace-operational` for how to
   embed tiles on the form.

You typically need both: the AxTile defines WHAT, the form's
tile button defines WHERE.

---

## Read this skill when

- You're building a workspace and need count tiles ("12 pending
  shipments"), KPI tiles, or navigation links.
- The user wants a workspace summary view on their dashboard.
- You've authored a Display menu item and want a tile-shaped
  entry point to it on a workspace.
- You're optimizing tile refresh performance.

---

## The four tile types

| Type | Shows | Required properties (besides Name) |
|---|---|---|
| `Standard` | Static button with label/image, navigates to a menu item | `Label`, `MenuItemName`, `MenuItemType` |
| `Count` | Number of records matching a query (with the navigation behavior of a Standard tile) | `Label`, `MenuItemName`, `Query` (often optional — defaults to menu item's Query) |
| `KPI` | Summary value from a KPI definition | `Label`, `KPI` |
| `Link` | Navigates to an external URL | `Label`, `URL` |

**The dominant case is Count.** Count tiles answer "how much
unprocessed work is there?" — the central question a workspace
exists to answer.

---

## Typed authoring tools

AxTile is first-class on the typed authoring layer — prefer
these over the raw `xpp_create_object` escape hatch:

- `xpp_create_tile` — author a new tile from a typed
  CreateTileRequest. All the workspace knobs (Type, Size,
  TileDisplay, MenuItemName/Type, Query, RefreshFrequency,
  KPI, NormalImage / ImageLocation, OpenMode, etc.) are typed
  via enums where possible.
- `xpp_get_tile` — read an existing tile.
- `xpp_patch_tile` — partial update (null leaves current
  value unchanged).

MS strips default values on read (e.g. `MenuItemType=Display`,
`AllowUserCacheRefresh=Yes`) — that's the canonical-form
serializer behavior, not a bug. The XML reference below
remains useful for understanding the on-disk shape.

---

## XML shape

Tiles use the `Microsoft.Dynamics.AX.Metadata.V1` namespace
(same as menu items, unlike classes/tables):

### Count tile (the common case)

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxTile xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
        xmlns="Microsoft.Dynamics.AX.Metadata.V1">
    <Name>CONPendingShipmentsTile</Name>
    <Label>@MyLabels:PendingShipments</Label>
    <MenuItemName>CONShipmentPending</MenuItemName>
    <Query>CONShipmentPendingQuery</Query>
    <RefreshFrequency>AsFastAsPermissible</RefreshFrequency>
    <Size>ShortWide</Size>
    <Type>Count</Type>
</AxTile>
```

### KPI tile

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxTile xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
        xmlns="Microsoft.Dynamics.AX.Metadata.V1">
    <Name>CONAvgFulfillmentTime</Name>
    <Label>@MyLabels:AvgFulfillmentTime</Label>
    <ConfigurationKey>RetailBasic</ConfigurationKey>
    <KPI>CONAvgFulfillmentTimeKPI</KPI>
    <RefreshFrequency>AsFastAsPermissible</RefreshFrequency>
    <Size>Wide</Size>
    <Type>KPI</Type>
</AxTile>
```

### Standard / Link

Minimal — usually just `Name`, `Label`, navigation target.
Used less than Count in practice.

---

## Property checklist

| Property | Notes |
|---|---|
| **`Name`** | The AOT name. Convention: `<prefix><Function>Tile` (e.g., `CONPendingShipmentsTile`). |
| **`Type`** | `Count` (default), `Standard`, `KPI`, `Link`. |
| **`Label`** | What appears on the tile. Use a label reference. Keep it short — tiles are small. |
| `MenuItemName` | The Display menu item this tile navigates to. Required for Count / Standard. |
| `MenuItemType` | The menu item's type. Almost always `Display`. (Standard tile only — Count derives from MenuItemName.) |
| `Query` | The query that produces the count. For Count tiles, defaults to the menu item's Query if omitted. Explicit override here lets multiple tiles share a menu item but show different filtered counts. |
| `KPI` | When `Type=KPI`, the `AxKPI` element to summarize. |
| `URL` | When `Type=Link`, the destination URL. |
| `Size` | Tile size; affects placement. Common: `ShortWide` (Count default), `Small`, `Medium`, `Wide`. |
| `RefreshFrequency` | How often the count updates. `AsFastAsPermissible` (5s — only for fast queries <25ms), `10Minutes` (queries <250ms), `24Hours` (slow or stable data). |
| `ConfigurationKey` | Gate visibility on a feature key. |
| `NormalImage` | Icon when enabled. Same image-resource rules as menu items. |
| `NormalImageLocation` | `EmbeddedResource` / `File` / `Resource` — where the image comes from. |
| `DisabledImage` / `DisabledImageLocation` | Disabled-state icon. Usually omit — system derives from NormalImage. |
| `CopyCallerQuery` | `Auto` (default), `Yes`, `No`. Like menu items — does opening from this tile carry over the caller's query filters? |
| `TileDisplay` | Display mode hint — rarely set. |
| `Description` | Free text. |

---

## RefreshFrequency — the perf knob

| Value | When to use |
|---|---|
| `AsFastAsPermissible` | Query is fast (<25ms) AND users need near-real-time counts. The system polls every ~5 seconds. |
| `10Minutes` | Query is moderate (<250ms) OR users only need periodic updates. |
| `24Hours` | Slow query (>250ms) OR data that changes rarely. |

**Default to `10Minutes`** for most tiles. Only bump to
`AsFastAsPermissible` if the query is provably fast AND the
metric is something users actually watch second-by-second
(rare).

Sluggish workspaces are almost always caused by tiles set to
`AsFastAsPermissible` against unoptimized queries. See the
gotchas section.

---

## The Count query — what makes it fast

A Count tile runs `select count(RecId) from <table> where <range>`
on every refresh. To stay under 25ms (the threshold for
`AsFastAsPermissible`):

- **Selective WHERE conditions.** Aim for the query to return
  <500 rows, ideally <100. Add status / date / company filters.
- **Backing index.** The columns in the WHERE clause should be
  covered by an index on the table. Without an index, even
  small result sets are slow because the engine table-scans.
- **No joins if possible.** A single-table query against an
  indexed primary table is the fast path. Joins add latency.

If the query can't be fast enough, drop the RefreshFrequency
to `10Minutes` — it's better to show slightly stale counts
than to slow the workspace down.

---

## Tile → workspace placement

The tile itself doesn't appear on a workspace — a
`AxFormTileButton` control inside the workspace form's
`<TileContainer>` references it by name. The placement looks
like (inside the workspace form XML):

```xml
<AxFormControl xmlns="" i:type="AxFormTileButtonControl">
    <Name>CONPendingShipmentsTileButton</Name>
    <Tile>CONPendingShipmentsTile</Tile>
</AxFormControl>
```

When authoring a workspace, you'll:
1. Create the AxTile (this skill).
2. Place a TileButton on the workspace form's TileContainer
   referencing the AxTile.

See `dynamics-xpp:xpp-pattern-workspace-operational` for the
workspace form pattern.

---

## Common workflows

### Workspace with N count tiles

The canonical "operational workspace" has 4-8 count tiles
across the top, each showing pending work for one category.
For each tile you need:

1. A query (`AxQuerySimple`) that selects pending records.
   `dynamics-xpp:xpp-query`.
2. A Display menu item (`dynamics-xpp:xpp-menuitem`) targeting
   the list-page form for that work, ideally referencing the
   same query so click-through pre-filters.
3. The tile (this skill) wiring query + menu item.
4. The tile button on the workspace form referencing the tile.

That's 4 AOT objects per count tile. Build them in this order
— the tile depends on the menu item which depends on the query.

### KPI tile

KPI tiles need an `AxKPI` definition (separate AOT type, not
covered in tier-1 yet). Stand them up only when you have an
aggregate measurement model — they don't make sense for
arbitrary "show me a number" use cases.

### Standard tile

Used when you want a stylized button instead of a count —
e.g., a navigation tile that opens a sub-workspace or a
parameter form. Functionally identical to a regular menu
item button but rendered with the workspace tile aesthetic.

---

## Common gotchas

### Forgetting the V1 namespace

Same as menu items — without `xmlns="Microsoft.Dynamics.AX.Metadata.V1"`
on the root, the bridge rejects. Always start from a real
tile via `xpp_get_object_xml`.

### RefreshFrequency=AsFastAsPermissible on slow queries

Tile sets a refresh of 5s but the query takes 1 second.
Result: workspace stays sluggish, every user paying the cost
on every page. Always profile the query before setting to
`AsFastAsPermissible`. The MS guidance is firm: only when
<25ms.

### MenuItemName mismatch

Tile points at menu item `CONPendingShipments` but the actual
menu item is `conPendingShipments`. F&O is case-insensitive
for names so it usually works, but `xpp_compile` may flag the
inconsistency. Match the case.

### Count tile with no Query

If `Query` is omitted, the tile inherits the menu item's
`Query` property. Make sure the menu item HAS a Query —
otherwise the tile shows "0" forever.

### Tile button on a non-workspace form

`AxFormTileButtonControl` placed on a regular form (not
Style=Workspace) renders but looks wrong. Tiles are designed
for the workspace pattern; don't try to repurpose them on
detail forms.

### TileDataService::forceRefresh for manual updates

When data changes from XPP code that the system doesn't
detect (custom batch jobs, etc.), the cache won't refresh
until the timer fires. Use:

```xpp
TileDataService::forceRefresh(tilestr(CONPendingShipmentsTile), formRun)
```

from the changing code to invalidate the cache.

### KPI tiles don't have CountQuery semantics

KPI tiles read from a different infrastructure (aggregate
measurements / OLAP cubes). The refresh frequency works
differently and depends on the KPI definition, not on a query.

---

## Worked example: pending-work workspace tile

The workspace shows "Pending Shipments" with a count of
shipments where `Status == Pending` for the current site.

### Step 1: Query (`CONShipmentPendingQuery`)

See `dynamics-xpp:xpp-query`. Selects from CONSHShipmentTable where
Status==Pending.

### Step 2: Display menu item (`CONShipmentPending`)

See `dynamics-xpp:xpp-menuitem`. Targets the shipment list form,
references the query so click-through pre-filters.

### Step 3: AxTile (this skill)

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxTile xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
        xmlns="Microsoft.Dynamics.AX.Metadata.V1">
    <Name>CONPendingShipmentsTile</Name>
    <Label>@MyLabels:PendingShipments</Label>
    <MenuItemName>CONShipmentPending</MenuItemName>
    <RefreshFrequency>10Minutes</RefreshFrequency>
    <Size>ShortWide</Size>
    <Type>Count</Type>
</AxTile>
```

No explicit Query — inherits from the menu item.
RefreshFrequency=10Minutes is the safe default; bump only
after profiling.

### Step 4: Form tile button on workspace

(Inside the workspace form's `<TileContainer>`)

```xml
<AxFormControl xmlns="" i:type="AxFormTileButtonControl">
    <Name>CONPendingShipmentsTileButton</Name>
    <Tile>CONPendingShipmentsTile</Tile>
</AxFormControl>
```

---

## See also

- `dynamics-xpp:xpp-menuitem` — the Display menu item the tile
  points at.
- `dynamics-xpp:xpp-query` — the query that produces the count.
- `dynamics-xpp:xpp-pattern-workspace-operational` — workspace
  form pattern where tiles are placed.
- `dynamics-xpp:xpp-form` — for the form-side TileButton control.
- [MS: Navigation concepts — Tiles](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/page-navigation#tiles)
- [MS: Tile and list caching for workspaces](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/tile-list-caching-workspaces)
- [MS: Build a workspace tutorial](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/user-interface/build-workspace)
