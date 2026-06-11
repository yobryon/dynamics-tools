# Path-addressable domain navigation — design (P0)

Status: **P0 complete** (design finalized + spiked on forms & tables, 2026-06-05).
Backlog: `backlog-path-addressable-navigation`. Absorbs the surgical-extension-patch ask.

One addressing model, four ops, layered onto the EXISTING `get_<type>` / `patch_<type>`
tools plus one new locate tool — so an agent can navigate a 1.5 MB form (or a 200-field
table) the way it navigates a large codebase: **orient → locate → zoom → edit**, never
regurgitating the untouched 95% in either direction.

## The four ops

| Op | Surface | Returns |
|----|---------|---------|
| **orient** | `get_<type>(name, outline=true, atPath?, depth?)` | structural tree, path-rooted, depth-bounded; scalars elided |
| **locate** | `xpp_find_in_object(axType, name, query)` | matching **paths** (breadcrumbs), not subtrees |
| **zoom** | `get_<type>(name, atPath, full)` | the full typed subtree at one path |
| **edit** | `patch_<type>(name, atPath, value)` | surgical edit of that subtree; siblings untouched |

No fleet of new tools: `outline` / `atPath` / `depth` are parameters on the existing
`get_`/`patch_`. No-arg behavior is preserved (full read / full merge-patch) for
back-compat.

## The pivotal P0 decision: derive the skeleton from the BRIDGE JSON, not the C# records

The backlog plan assumed the structural skeleton would derive from the typed domain
records in `XppService.Domain` (collection property = node, scalar = leaf). **The spike
disproved that mechanism.** `xpp_get_table CustGroup` returns `mappings`, `createdBy`,
`modifiedBy`, `createdTransactionId` — **none of which exist in `GetTableResponse`**.
`DomainHandlers.GetDomainObject` is a *raw passthrough* of the bridge's JSON
(`_bridgeClient.GetDomainObjectViaMetaclassAsync` → returned verbatim); the C# records
are an **input-only** shape (create/patch) and an *incomplete subset* of what the bridge
emits on read. Driving the skeleton from records would make `mappings` — a genuine
right-click-add node — **invisible**.

So the skeleton derives from the **JSON value shape**. This is strictly better:

- **Correct** — it sees everything the bridge emits, including richer-than-record fields.
- **Generic** — zero per-type code, zero per-type manifest. Works for every AxType the
  bridge can serialize, today and future.
- **Self-consistent** — the same walk produces the outline an agent reads and resolves
  the paths it then addresses, so the agent never guesses an address.

## Node-vs-leaf classification (the whole model)

Classify each JSON value purely by shape:

| JSON shape | Class | In outline |
|------------|-------|------------|
| primitive (string/number/bool/null) | **scalar** | elided |
| array of primitives (`fieldGroup.fields`) | **scalarList** | elided (could show count) |
| object, all values primitive (`advanced`, `otherProperties`, `subscriberAccessLevel`) | **leafGroup** | elided |
| array of objects (`controls`, `fields`, `relations`, `methods`, `mappings`, `connections`) | **collection** | a node; each element a child |
| object with ≥1 array/object child (`design`, `sourceCode`, `formControlExtension`) | **singleton** | a structural node; recurse |

The leafGroup-vs-singleton split is what makes the "right-click-add belongs / properties
don't" rule mechanical: `advanced` is all-primitive → a property group (elided);
`formControlExtension` carries an `extensionProperties` array → a structural node (kept).
**No hardcoded per-type list is needed** — the distinction is `any(child is array|object)`.

### otherProperties audit — PASS

Across the real CustGroup form + table, *every* `otherProperties` / `advanced` /
`subscriberAccessLevel` object held **only primitive scalars**. Structural children
*always* surface as arrays-of-objects (or the array-bearing singletons `design` /
`sourceCode` / `formControlExtension`). Conclusion: **nothing an agent would right-click-add
is hidden inside an opaque bag.** The skeleton is complete from the JSON shape alone.

## Addressing scheme

`/<segment>/<segment>/…`, two segment kinds, both produced by the same walk:

- **singleton** → its property name: `/design`, `/sourceCode`.
- **collection member** → `/<collectionProp>/<identity>`.

Identity within a collection: first present of **`name` → `dataField` → `field` →
`mapField`**, else ordinal **`#n`**. Covers every observed collection:
`controls`(name), `dataSources`(name), form-ds `fields`(dataField), table `fields`(name),
`indexes`(name) → `fields`(dataField), `relations`(name) → `constraints`(name),
`fieldGroups`(name), `methods`(name), `mappings`(name) → `connections`(mapField),
`extensionProperties`(name). The wrapper singleton is kept explicit (e.g.
`/design/controls/Grid`, not a hoisted `/controls/Grid`) so addressing is mechanical with
zero per-type hoisting rules — the outline shows `design` as a node and the agent
addresses through it.

Examples:
- Form: `/design/controls/CustomFilterGroup/controls/QuickFilterControl/formControlExtension`
- Form: `/sourceCode/methods/init`, `/sourceCode/dataSources/CustGroup/fields/CustAccountNumSeq/methods/lookupReference`
- Table: `/relations/PaymTerm/constraints/ClearingPeriod`, `/fieldGroups/AutoReport`,
  `/mappings/CustVendGroup/connections/GroupId`, `/fields/CustGroup`

### Stability

Identities are stable across reads (unlike the ephemeral runtime `ref_id`s of the
rendered accessibility tree). Ordinal `#n` fallback is stable only as long as list order
is — acceptable for P0; the anonymous-element case (`connections` with no `mapField`) is
rare. Methods reduce to **name + signature** (first non-blank `source`/`declaration`
line); body elided.

## Spike

`misc/path_nav_spike.py` — a language-agnostic JSON walker proving all four ops + the
audit against synthetic specimens that reproduce every structural case from the real
CustGroup form & table. Validated:
- depth-bounded outline on both the deep-recursive (form) and broad-flat (table) shapes;
- `atPath`-rooted outline (zoom);
- `find` returning breadcrumb paths by attribute predicate;
- path resolution to a subtree, including `mappings`/`connections` (record-invisible) and
  `mapField` identity;
- method-body elision to signature;
- the node-vs-leaf audit (no third category).

The C# port (P1) mirrors this exactly over `System.Text.Json.Nodes` — `DomainHandlers`
already imports it; the walk runs **service-side** on the bridge JSON before the gRPC
return, so the wire ships only the reduced outline/subtree (saves payload AND agent tokens).

## Depth semantics (settled by dogfooding, 2026-06-05)

`depth=0` (the default) = **collection COUNTS only** — the compact, always-bounded orient.
`depth=1` = list members one level. `depth=2` = members + their sub-counts. Structural
singletons (`design`, `sourceCode`) are **transparent to depth**: they cost no level, so
even `depth=0` surfaces their collection counts inline ("design has 3 controls"). Depth
bounds only the collection-member expansion that actually explodes.

Two earlier variants were both wrong and dogfooding caught it: (a) "singletons transparent
+ default depth 1" blew depth=1 to 46 KB on the SalesTable form; (b) "singletons consume a
level + default depth 1" fixed forms but left wide *tables* (no wrapper layer) dumping all
~200 members at the default. Counts-at-depth-0 + transparent singletons is the synthesis —
the default is compact for **both** shapes:

| read | result |
|------|--------|
| `get_table SalesTable` outline (depth=0) | ~300 B — `{relations:81, fieldGroups:87, fields:185, indexes:16, deleteActions:15, mappings:21}` + `sourceCode{methods:621}` |
| `get_form SalesTable` outline (depth=0) | ~400 B — `design{controls:3}`, `sourceCode{methods:175, dataSources:29, dataControls:105}`, `{dataSources:37, parts:2}` |
| `get_form SalesTable` atPath=/design depth=1 | 3 top controls w/ child counts |
| `find` "DeliveryAddress" kind=Group | 4 deep addressable paths (8–10 levels) |
| `get_form` atPath=`<deep path>` (no outline) | the full subtree (zoom) |

Loop: outline (depth=0) to orient → `atPath`+depth=1 to descend, or `find` to jump → zoom
(atPath, no outline) → patch. Raising depth on a monster is possible but `atPath` into the
one area is the intended move. An `atPath` rooted directly at a collection (`/relations`)
lists its members even at depth 0 (it's an explicit "show me these").

## P1 validation (read / forms) — DONE 2026-06-05

`DomainSkeleton` walker (`XppService/Services/DomainSkeleton.cs`) + `GetDomainObject`
outline/at_path/depth + new `FindInObject` RPC, wired to `xpp_get_form`
(outline/atPath/depth) + new generic `xpp_find_in_object` tool. Validated live via
`XppService.PingProbe --nav` against CustGroup (small) and SalesTable (1.5 MB): outline
(depth-bounded, atPath-rooted), zoom (full subtree), find (deep addressable paths),
bogus-path→NotFound. All four ops correct on both the deep-recursive and broad shapes.
(MCP-side tools take effect on the next MCP reconnect; server-side proven now.)

## P2 validation (read / table + table-extension) — DONE 2026-06-05

Wired outline/atPath/depth onto `xpp_get_table` + `xpp_get_table_extension` (via a shared
`DomainGetNav.ReadAsync` helper, also retrofitted to `xpp_get_form` and the extension
`Get` helper — every get tool now plumbs nav identically). Walker needed no per-type work
(it's JSON-shape driven). Added one capability the table case demanded: an `atPath` rooted
directly at a **collection** (e.g. `/relations`) now lists its members rather than
returning a bare node.

Validated on SalesTable (the *table*: 184 fields, 107 relations, mappings, sourceCode):
all 7 collections walked (incl. `mappings`, which `GetTableResponse` doesn't model);
`atPath=/relations` outline (19 KB, members listed); zoom `/relations` (35 KB full);
bogus→NotFound; `find "Currency"` spanning a relation + a nested constraint
(`/relations/Currency/constraints/CurrencyCode`) + field groups + methods.

Shape note: tables are flat (no `design`/`sourceCode` wrapper), so an outline at depth=1
lists every field/relation/method member (~36 KB for SalesTable) — still ~10× the full
table and fully addressable, but the right orient move for a wide table is `atPath` into
one collection (`/fields`, `/relations`) or `find`, not a whole depth=1 read. Forms get the
compact 4.9 KB depth=1 because their wrapper layer absorbs the breadth.

## P3 (write / surgical patch-by-path) — DONE 2026-06-05

`PatchDomainObjectByPath` RPC + generic `xpp_patch_by_path` MCP tool. The agent sends only
`{atPath, op, value}`; the service GETs current state, splices the value at the path, and
sends **only the changed top-level branch** through the existing, validated
`PatchDomainObject` metaclass path — no bridge changes.

Ops (`DomainSkeleton.ApplyOp`, in-place JSON splice): **set** (replace node), **merge**
(shallow-overlay value's top-level props onto the object — change nested by targeting the
deeper path), **append** (add a member to the collection at atPath), **remove** (delete the
node). Bad path/op/shape → `InvalidArgument` with a caller-facing reason.

**dryRun** (`dry_run`): splice + return the edited subtree in `preview_json` WITHOUT
writing — a genuine agent affordance (confirm the edit landed where intended before
committing) and the safe validator used here.

Why the commit path is sound without inventing new write machinery: it decomposes into
three independently-validated links — the splice (dry-run-proven below), the branch patch
(`{design: …}` is an ordinary partial `PatchDomainObject` merge), and GET-shape→patch
(exactly the fuzz round-trip, 132K+ probes). For a form the existing pattern-conformance
pass still runs on the patched result.

Validated (dry-run, real CustGroup form): **append** a control → new member at the
collection tail, 12 existing preserved; **merge** `allowEdit:false` onto Grid_Name → that
prop changed, all others intact; **set** → wholesale node replace; **remove** → member
gone, siblings intact; **bad path** → clean `InvalidArgument`. One real commit+verify+delete
on a throwaway is the remaining check (deferred to an MCP reconnect, where `delete_object`
gives clean teardown — the service has no DeleteObject RPC yet for probe-side cleanup).

## Phasing

- **P0 — design + spike.** DONE. derive-from-JSON model finalized; otherProperties audit
  PASS; forms + tables spiked (opposite shapes, no boxing-in found beyond the
  records→JSON pivot, which this caught).
- **P1 — read / forms.** DONE (see "P1 validation" above).
- **P2 — read / table + table-extension.** DONE (see "P2 validation" above).
- **P3 — write / surgical patch-by-path.** DONE (see "P3" above). Generic across all types
  (one `xpp_patch_by_path` tool), so this covers forms + tables + everything else at once.
- **P4 — ship.** DONE 2026-06-05. New `dynamics-xpp:xpp-navigation` skill (the loop, the
  path vocabulary, depth semantics, the four ops, dryRun-before-commit, worked examples,
  coverage); cross-linked from xpp-form + xpp-table. Real commit cycle verified on a
  throwaway (append/merge/remove, drift=0). Folded outline/atPath onto `xpp_get_class`
  (big method bags) on top of form/table/table-extension. find + patch_by_path are generic
  across all types already; remaining get_ tools (enum/edt/query/view/entity/menu/security/
  service/tile/resource) take the same knobs incrementally — small enough that whole-reads
  rarely hurt. Advisory drafted for Sprint 3.

## Open questions carried into P1

- Default `depth` for outline (lean 1; collections always show their immediate members so
  depth=1 already lists children-as-nodes).
- `find` query surface: attribute predicates (name/kind/dataSource/dataField/caption/
  method-name) — expose as structured filters vs. a single substring? Lean structured.
- patch-by-path verbs: replace / merge / addChild / removeChild — finalize in P3 against
  the bridge's incremental-patch metaclass path.
- scalarList rendering (`fieldGroup.fields`): elide entirely vs. show `fields[N]`. Lean
  show count, since membership is the interesting bit for a field group.
