---
name: xpp-workspace-charts
description: Use when a workspace (or any form) needs a CHART — a chart tile in the Summary tiles section, a chart section below the tabbed lists (Section Stacked Chart), or a chart on an ordinary form. Covers the SysChart control's XML/typed shape (data sets, measures, secondary axes, chart types), the HubPartChart form part, the FormPartControl host, data population idioms (temp table / view / query / cache), wiring the workspace page filter to a chart, drill-through, and the pitfalls MS code works around.
---

# Charts in workspaces (and elsewhere)

Everything here was reverse-engineered from shipped forms on a 10.0.4x box
(`RetailITWorkspace` + `RetailITDownloadSessionsChart`, `CostAnalysisWorkspace` +
`CostInventoryFlowChart`, `CustomerCollectionManagerWorkspace`,
`BankTreasurerWorkspace`, `EcoResProductMaintainWorkspace`, `Tutorial_ChartControlForm`
and about a dozen more). When in doubt, read one of those with `xpp_get_form`
before inventing a shape — the chart control is an extensible control whose
configuration is a tree of extension components, and the deserializer is
unforgiving about names.

Load `dynamics-xpp:xpp-pattern-workspace-operational` first if the chart lives in a
workspace, and `dynamics-xpp:xpp-form` for the form envelope.

---

## The three facts that shape everything

1. **There is exactly one chart control: `SysChart`.** There is no
   `AxFormChartControl`. In XML it is an ordinary
   `AxFormContainerControl` (`Type=Container`) whose `FormControlExtension`
   is named `SysChart`; all chart configuration lives in that extension's
   properties and components. Rendered client-side with Highcharts.
2. **A workspace never hosts the chart control directly.** Workspace forms
   have no data source and a chart needs one, so the chart lives in its own
   **form part** (`Design.Style=FormPart`, `Design.Pattern=HubPartChart` 1.0 —
   MS docs call it "Section Chart"). The workspace references the part through
   a **`FormPartControl`** (a Container control with
   `FormControlExtension Name=FormPartControl`) whose `targetName` is an
   **`AxMenuItemDisplay`** pointing at the part form — never the form name.
3. **"Chart tile" and "chart section" are the same mechanism in a different
   tab page.** Put the `FormPartControl` on the `SectionTiles` page and it
   flows in the tile grid as a chart tile; put it on a `SectionStackedChart`
   page and it renders as a full-width chart below the tabbed lists. There is
   no `AxTile` and no `TileButtonControl` involved in a chart tile.

| Placement | Workspace tab page | Children allowed | Chart control `ExtendedStyle` (set INSIDE the part) |
|---|---|---|---|
| Chart tile | `SectionTiles` 1.1 (Summary) | `TileButtonControl` buttons and `FormPartControl` containers, interleaved in any order | `chart_2x2` (tile-sized; matches a large tile) |
| Chart section | `SectionStackedChart` 1.1 | 1 or 2 `FormPartControl` containers only | `chart_4x4` or `chart_2x4` (or none) |
| Anywhere else | any form, `Pattern=Custom` or inside a pattern that allows a Container | the `SysChart` container itself | optional |

`SysChartExtendedStyle` values: `chart_1x2`, `chart_2x2`, `chart_2x3`,
`chart_2x4`, `chart_2x6`, `chart_4x4`, `chart_4x6`, `chart_4x8` (tile
multiples). The style goes on the chart control inside the part, not on the
`FormPartControl` in the workspace.

---

## Build order (the part must exist before the workspace references it)

1. **Data shape.** One of:
   - a temp table (`TableType=TempDB` or `InMemory`) with a category field
     plus one numeric field per measure and an optional group-by field (the
     most common idiom: `CostFlowTmp` has `Month`, `Name`, `Amount`);
   - an `AxView` or `AxQuery` over existing tables (zero X++ —
     `HcmInjuriesByDepartmentChart` sets `Design.DataSourceQuery`;
     `VendInvoiceJourCountChart` binds a view);
   - a regular table with `group by` + `count` added to the data source query
     in `init()` (`EngChgEcmPriorityChart`);
   - a `SysDataCache` cache table + `SysIDataCacheConsumer` class for
     expensive aggregates (`RetailCDXDownloadSummaryCache`; advanced).
2. **Chart form part** `XxxChart` — `examples/chart-part-domain.json` is a
   complete typed `CreateFormRequest`. Key values: `design.style="FormPart"`,
   `design.pattern="HubPartChart"`, `design.patternVersion="1.0"`,
   `design.viewEditMode="View"`, a caption, ONE data source, ONE Container
   control carrying the `SysChart` extension. HubPartChart allows an
   optional header `Group` with `Pattern=FiltersAndToolbarInline` 1.0
   (`ExtendedStyle=hubPartGrid_header`) before the chart for part-local
   filters (`CustCollectionsAgedBalancesChart`, `EngChgEcmPriorityChart`).
3. **X++ in the part** when the data source is a temp table (see "Feeding the
   chart").
4. **`AxMenuItemDisplay`** `XxxChartMenuItem` with `Object=XxxChart`. A
   label is optional; MS ships menu items with nothing but Name + Object.
5. **Security.** Add that menu item as an entry point (View) to the privilege
   that grants the workspace; a part whose menu item the user cannot access
   renders empty.
6. **Workspace host control** — `examples/workspace-chart-hosts.json` shows
   both placements. The `FormPartControl` needs `targetName` (the menu item),
   `parmRunMode=Local`, `autoRefreshInterval` (seconds; `0` = off; MS uses
   `86400` on the Retail IT charts and recommends a refresh for cached data),
   and the empty `dataLinks` composite. Set `autoDeclaration=true` only if the
   workspace's X++ will call `<control>.getPartFormRun()`.

Steps 1–5 are identical for a chart tile and a chart section; only step 6
differs.

---

## The chart control shape

Three composites under `extensionComponents`, always in this order:

- **`parmDataSets`** → 1..N leaves, `componentType=SysBuildChartDataSet`:
  - `parmDataSource` — the **form data-source NAME** (not the table). Two
    data sets may bind two differently named data sources on the same table
    (`CostInventoryFlowChart` does this for period-change vs ending-balance).
  - `parmCategoryField` — the X-axis field. Its base type picks the
    categories type automatically: string, date, utcDateTime, or enum (enum
    labels resolve server-side). **The first data set's category field fixes
    the categories type for the whole chart**; do not mix a date category in
    one data set with a string category in another.
  - `parmGroupBy` — optional; each distinct value becomes its own series
    (stacked segments for `StackedColumn`).
- **`parmMeasures`** → 1..N leaves, `componentType=SysBuildChartMeasure`:
  `parmChartType` (enum `SysChartType`), `parmTitle` (series label),
  `parmDataLabels` (`None` / `Inside` / `Outside`), `parmDataSet` (the NAME of a
  leaf under `parmDataSets`), `parmMeasureField` (the Y field),
  `parmUseSecondaryYAxis` + `parmSecondaryYAxisName` (the NAME of a leaf under
  `parmSecondaryYAxes`), and the drill-through quartet
  `parmClickMenuItemType` / `parmClickMenuItemName` / `parmKeyField` /
  `parmParameter1` / `parmParameter2`. Newer builds also carry `parmColor` and
  `parmDataLabelsFormat`.
- **`parmSecondaryYAxes`** → 0..N leaves, `componentType=SysBuildChartYAxis`:
  `parmTitle`, `parmVisible`, `parmLabelsHidden`. The composite must be
  present even when empty.

Chart-level `extensionProperties` (all optional; the control defaults what you
omit): `parmText` (chart title; label id or literal), `parmLegendEnabled`,
`parmLegendAlignment` (`SysChartHorizontalAlignment`: Left/Center/Right/Auto),
`parmLegendPosition` (`SysChartVerticalAlignment`: Top/Middle/Bottom/Auto),
`parmLegendLayout` (Horizontal/Vertical), `parmTitleAlignment`,
`parmTitlePosition`, `parmYAxisTitle`, `parmYAxisVisible`, `parmXAxisTitle`,
`parmSerializedButtons` (`Line;Bar;Column;Pie;Funnel` — chart-type switch
buttons, shown only when `parmToolbarEnabled=True`), `parmToolbarEnabled`,
`parmCrosshairsEnabled`, `parmTooltipShared`, `parmInteractionsDisabled`,
`parmZoomLimit` (`SysChartDateTimeInterval`, for date/time categories),
`parmPadMargin`, and chart-level `parmClickMenuItemType/Name` (a whole-chart
click-through, distinct from per-measure drill-through).

**Typed-JSON encoding rule:** every enum-typed property is
`{"name": "...", "type": "Enum", "value": "...", "otherProperties": {"TypeName": "<EnumName>"}}`;
booleans use `TypeName` `boolean` with `True` / `False`; menu-item names use
`"type": "ExtendedDataType"` with `TypeName` `MenuItemName`; counts use
`"type": "Int32"`. Composites are `{"name": ..., "kind": "Composite", "components": [...]}`;
leaves are `{"name": ..., "kind": "Leaf", "componentType": ..., "extensionProperties": [...]}`.
Copy the example rather than typing this from memory.

`SysChartType`: `Bar`, `Line`, `Column`, `StackedColumn`, `StackedBar`,
`StackedPercentageColumn`, `StackedPercentageBar`, `Pie`, `Funnel`, `Doughnut`,
`Scatter`, `Hidden`, `SolidGauge`, `MasterDetailLine`. Mixing types across
measures is normal (three `Column` series plus a `Line` on a secondary axis is
the Retail IT download chart; `StackedColumn` + `Line` is the cost flow chart).
Pie and Doughnut charts use one measure and usually `parmDataLabels=Outside`.

---

## Feeding the chart

The chart binds to the form data source; X++ only (re)populates it. There is
no `addSeries` / `setData` API. Idioms, in order of preference:

- **View or query, no code.** Bind the data source to a view (or set
  `Design.DataSourceQuery`). Cheapest to author and to review.
- **Temp table filled in the part.** Declare the buffer, fill it, then bind
  and query:

  ```xpp
  public void init()
  {
      super();
      this.populate();
  }

  private void populate()
  {
      MyChartTmp tmp;            // TempDB / InMemory table: Category, Amount, (GroupName)
      // ... insert rows ...
      MyChartTmp_ds.setTmpData(tmp);      // or MyChartTmp_ds.cursor().setTmpData(tmp)
      MyChartTmp_ds.executeQuery();       // or research() after a refill
  }
  ```

  When refilling (filter change, period change), `delete_from` the data
  source's own buffer first (`CostInventoryFlowChart.setPeriod`).
  Alternatively override the data source's `executeQuery()`: fill the buffer,
  `setTmpData`, then `super()` (`EcoResProductReleasedStoppedAllChartPart`).
- **Aggregate in the query.** In the data source `init()`:
  `qbds.addGroupByField(...)`, `qbds.addSelectionField(fieldNum(T, RecId), SelectionField::Count)`
  and chart `parmMeasureField=RecId` (`EngChgEcmPriorityChart`).
- **SysDataCache.** For expensive aggregates: a cache table with
  `SysDataCacheContextId`, a `SysIDataCacheConsumer` class whose
  `prepareDataSet()` refreshes it, and a range on the context id. Pair with
  `autoRefreshInterval` on the host. Only when a plain query is too slow.

Page size: `SysChart` bumps the data source's `clientPageSize` to 1000 only if
it is still 1; rows beyond one page are not charted. Set it yourself in the
data source `init()` when you know the count (`this.clientPageSize(5)`).

---

## Wiring the workspace page filter to a chart

Two idioms, both in shipped code:

**Framework filter (prefer):** the workspace `implements SysIFilterProvider`
(`parmFilter()` builds a `SysIFilter`; `parmChangeEvent()` returns a
`SysFilterChangeEvent` created in `init()` via
`SysFilterChangeEvent::newFromFormControl(<filter control>)`). The part
`implements SysIFilterEventHandler` and in `onFilterChanged()` reads
`this.parmFilterProvider().parmFilter()`, applies a range, and re-queries.
`RetailITWorkspace` / `RetailITDownloadSessionsChart` is the reference pair.
This is the same mechanism `dynamics-xpp:xpp-pattern-workspace-operational`
describes for list parts.

**View-model delegate (when the filter is a composite value, e.g. a period):**
the workspace holds a view-model class with
`delegate void onPeriodChange(State _s)`, parts implement a tiny interface
(`setPeriod(State)`), and the workspace's `init()` wires them:

```xpp
var part = MyChartPartControl.getPartFormRun() as MyISetPeriod;   // FormPartControl autoDeclaration=Yes, parmRunMode=Local
if (part) { viewModel.onPeriodChange += eventhandler(part.setPeriod); }
```

The part must guard with `hasExecutedInit()` — the host may push state before
the part's `init()` has run — and stash the value in a "to be set on init"
field (`CostInventoryFlowChart`). `getPartFormRun()` returns null for
`parmRunMode=Remote` parts.

---

## Drill-through (clicking a point)

Set on the **measure** (chart-level `parmClickMenuItemName` is a whole-chart
click-through instead): `parmClickMenuItemType` (`Display` / `Action` /
`Output`), `parmClickMenuItemName`, and optionally `parmKeyField`,
`parmParameter1`, `parmParameter2` — fields of the same data source whose
values for the clicked row travel with the click. The target receives an
`Args` whose `parmObject()` is a `SysChartDrillThruParameters` map with keys
`measureName`, `keyField`, `parameter1`, `parameter2`, `xValue`, `yValue`:

- a **Display** target form reads `element.args().parmObject()` in `init()`
  (`CustCollectionsAgedBucketsChartClick`);
- an **Action** target is a class with `main(Args _args)` reading
  `_args.parmObject()` (`InventAgingStorageDrillThrough`).

The part form can `implements IChartClickable` and override
`drillThroughClicked(Args _args, MenuFunction _mf)` to enrich the args
(`_args.record(...)`) or return `false` to cancel
(`BankTreasurerWorkspaceBalanceChartFormPart`). Override the control's
`DrillThrough(str _contextObject)` to rewrite the JSON context before `super()`
when the key must be computed (`CustCollectionsAgedBalancesChart`).

There is no "See more" convention on chart tiles; drill-through and the
part's own `FiltersAndToolbarInline` header are the only interactions.

---

## Pitfalls MS code works around (do the same)

- **Single-row data sets can throw in the client.** `InventAgingStorageChart`
  inserts a zero dummy row ("each period needs at least two records");
  `JmgJobsByStatusChart` pads missing enum categories with zero rows so every
  category renders. Pad when the data can be sparse.
- **Names, not tables.** `parmDataSource`, `parmDataSet`,
  `parmSecondaryYAxisName` reference sibling names in the form; a table name
  there binds nothing and fails silently.
- **`targetName` is a menu item.** The form name compiles clean and fails at
  runtime.
- **Height and width.** The chart control inside the part should be
  `HeightMode=SizeToAvailable` and `WidthMode=SizeToAvailable` (plus the
  `chart_NxM` style). The host `FormPartControl` in a stacked-chart section
  can also carry `HeightMode=SizeToAvailable`; the UX guideline in the
  workspace skill about `WidthMode` on list hosts applies here too.
- **Property order varies and empties can be omitted.** MS forms disagree on
  the order of `targetName` / `parmRunMode` / `autoRefreshInterval` and of
  measure properties; the deserializer is order-tolerant for extension
  properties. Properties with no value may be left out; older forms lack the
  newer ones (`parmCrosshairsEnabled`, `parmPlotLineEnabled`, `parmColor`).
- **Pattern versions seen on this box:** `HubPartChart` 1.0,
  `SectionTiles` 1.1, `SectionStackedChart` 1.1, `FiltersAndToolbarInline` 1.0,
  `WorkspacePageFilterGroup` 1.0. The write-time conformance check squawks
  on a wrong version; a wrong version still compiles, so trust the squawk.
- **Max two charts in a stacked-chart section** (MS guideline; the pattern
  model does not enforce it).
- **Cross-company charts** set `Design.SetCompany=No`, the data source's
  `CrossCompanyAutoQuery=Yes`, and fill with `changecompany`
  (`CustCollectionsAgedBalancesChart`).
- **Labels.** `parmText` and `parmTitle` accept label ids; several MS forms ship
  literals — use labels in yours.

---

## Supporting files

- `examples/chart-part-domain.json` — **start here.** Typed `CreateFormRequest`
  for a `HubPartChart` part with one data set, two `Column` measures and the
  empty secondary-axes composite, plus the X++ that fills a temp table.
- `examples/workspace-chart-hosts.json` — the two host fragments to splice
  into a workspace with `xpp_patch_by_path` (append): a chart tile on the
  `SectionTiles` page and a `SectionStackedChart` page holding two charts.
- Shipped references to read with `xpp_get_form`: `RetailITDownloadSessionsChart`
  (column + line, secondary axis, filter handler), `CostInventoryFlowChart`
  (two data sets, group-by, stacked column + line, view-model delegate),
  `CustCollectionsAgedBalancesChart` (pie, header filters, drill-through,
  view-backed temp table), `HcmInjuriesByDepartmentChart` (query-backed, no
  code), `Tutorial_ChartControlForm` (date/time categories, pie, clickable).

## See also

- `dynamics-xpp:xpp-pattern-workspace-operational` — the workspace shell, the
  page filter, list parts, build order.
- `dynamics-xpp:xpp-form-subpatterns` — `SectionTiles`, `SectionStackedChart`,
  `FiltersAndToolbarInline`.
- `dynamics-xpp:xpp-tile` — count / KPI tiles (which charts are not).
- `dynamics-xpp:xpp-menuitem`, `dynamics-xpp:xpp-security` — the part's menu
  item and its entry point.
