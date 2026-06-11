---
name: xpp-language
description: TRIGGER when about to call any xpp_* MCP tool, or when the user mentions dynamics-xpp, dynamics-xpp, X++, AOT, or D365 F&O. Anchor skill for the plugin — without it, MCP tool calls miss the X++ language foundations, naming conventions, and dispatch to per-AOT-type and per-pattern sub-skills. (The dynamics-xpp:xpp-setup skill is once-per-machine; this skill is needed every session that uses the plugin.)
---

# Authoring X++ in Dynamics 365 F&O via dynamics-xpp

This is the anchor skill for the `dynamics-xpp` plugin. It teaches:

1. What X++ is and the syntax fundamentals you can't bluff your way past.
2. How AOT objects are represented and where they live on disk.
3. The dynamics-xpp tool surface and the canonical patterns of use.
4. Which other skill in this plugin to read next, depending on the task.

Read it through once. The per-type and per-pattern skills assume the
foundations from this skill are in your context.

---

## The X++ language

X++ is the object-oriented language used to develop applications for the
Microsoft Dynamics 365 Finance & Operations platform. It is a managed
language that resembles C# but **does not provide namespaces, generics,
or lambdas**. C# code can be called easily from X++ using normal X++
syntax.

### Predefined types

These types are native to X++:

- `int` — 32-bit signed integer.
- `uint` — unsigned 32-bit integer.
- `int64` — 64-bit signed integer.
- `real` — decimal number.
- `str` — string. Literals may use single OR double quotes. Prefix with
  `@` for multiline strings (e.g. `@"line one\nline two"`).
- `container` — tuples of values. Literals use brackets: `[1, 2, "string"]`.
- `guid` — globally unique identifier.
- `enumeration` — type with named literals (defined as `AxEnum` objects).
- `anytype` — late-bound type.
- `void` — no type.

In addition, X++ uses two pseudo-types that look like primitives but
are really `extends`-derivable:

- `Date` — calendar date; null date is `1/1/1900` (use `dateNull()` to test).
- `utcdatetime` — UTC date+time; null is `1/1/1900T00:00:00`
  (use `utcDateTimeNull()`).

### Expressions and truthiness

Expressions look much like C#, but with one mandatory habit:

> **In X++, `&&` and `||` have the SAME precedence. Always parenthesize
> mixed boolean expressions.**

Any value can be interpreted as boolean:
- numeric zero is false; any other number is true.
- empty string is false; any other string is true.
- objects can be null.
- to check whether a tabular object contains a record, test whether
  `RecId != 0`.

```xpp
if (myTable.RecId == 0)
{
    // No record was selected; need to read one or insert a new one.
}
```

### Naming conventions (best-practice)

Microsoft's own authoring tooling enforces these, and BPC (Best Practice
Checks) will warn on violations:

- **Method names** start with a lowercase letter.
- **Class names** start with an uppercase letter.
- **Method parameters** start with an underscore (e.g. `_customerId`).

All code generated through the write tools must follow these conventions
to pass BPC cleanly.

### Tabular objects and `Common`

Tables represent business data stored in SQL. In X++ they are classes
that extend `Common`:

```xpp
public class MyTable extends Common
{
    public void myMethod()
    {
        MyTable t; // t is a record variable, not instantiated like a regular object
    }
}
```

Two important consequences:

- **Tables cannot define instance or static state**, only methods. The
  fields are defined by the AOT (the XML), not in the X++ source.
- **Records are not `new`'d.** A `MyTable t;` declaration creates a
  variable that gets populated by `select`, `find`, or insertion. Each
  populated value represents one database row.

All tables carry a set of predefined fields — `RecId` (`int64` unique
identifier), `TableId`, `CreatedDateTime`, `ModifiedDateTime`,
`CreatedBy`, `ModifiedBy`, plus `DataAreaId` (the F&O "company"
discriminator) for non-shared tables.

### Forms and `FormRun`

Forms represent a page that users interact with. They are classes that
extend `FormRun` and are adorned with the `[Form]` attribute:

```xpp
[Form]
public class MyForm extends FormRun
{
    // Data sources defined in XML
    // Methods (init, run, close, custom)
    // Control event handlers
}
```

The X++ class is only one half of a form. The other half is the AOT
XML — datasources, design (controls), data fields, pattern. See
`dynamics-xpp:xpp-form` for the envelope and `xpp:xpp-pattern-{name}` for the per-pattern
shape.

### Literals

Literals are mostly like C#. Notable specifics:

- Container literals use brackets: `[1, 2, "three"]`.
- String literals may use `"` or `'` interchangeably.
- Multiline strings: prefix with `@` and use double quotes.

### Standard library — see supporting files

- **`predefined-classes.md`** — the X++ built-in classes you'll encounter
  (Common, FormRun, plus framework helpers). Read when constructing X++
  source that interacts with the runtime.
- **`predefined-functions.md`** — the X++ global functions (string
  manipulation, type conversion, container ops, date/time helpers).
  X++ has ~90 globals you can call without a class context; many feel
  like C# static methods on `System.*` but they aren't. Read when
  writing logic.

---

## AOT objects on disk

The Application Object Tree (AOT) is the catalog of every metadata
artifact in F&O. Each AOT object lives as an XML file in the metadata
store, organized by model and type:

```
<MetadataStore>\<Model>\<AxType>\<ObjectName>.xml

Example:
J:\AosService\PackagesLocalDirectory\ApplicationSuite\Foundation\AxTable\CustTable.xml
```

The XML for an object carries **everything** about it: the X++ source
for methods, the declaration (class signature + member variables), the
fields/indexes/relations (for tables), the datasources/controls/design
(for forms), plus dozens of metadata properties (cache mode, security,
GDPR classification, etc.).

This single self-contained XML representation IS the authoring contract
Microsoft chose for its own AI integration (the VS2022 GitHub Copilot
extension uses the same shape). The dynamics-xpp write surface follows suit:
you author objects by constructing the full XML and posting it through
the write tools.

### Two authoring representations, briefly

Microsoft's metadata layer accepts two interchangeable inputs:

1. **Full AOT XML** — the on-disk format. Best for new objects or
   wholesale rewrites. Primary path through dynamics-xpp.
2. **X++ source text** — the `.xpp`-style string a developer would
   type in VS. Best for touch-up edits to a single method.

We follow MS's lead and prefer full XML. For method-level edits where
constructing a fresh envelope is wasteful, the future
`xpp_update_method_source` tool will splice X++ source into an existing
object via the parser. (Not implemented yet; use get → edit XML →
update for now.)

---

## The dynamics-xpp tool surface

The plugin's MCP server exposes a small, focused tool set. Read each
tool's own description (Claude shows them automatically) for parameters
and exact semantics; this section teaches the **patterns of use**.

### Read tools

- `xpp_find_object` — exact-name lookup. The starting point when you
  know the name.
- `xpp_search_pattern` — wildcard/partial-name discovery. When you only
  know a fragment.
- `xpp_search_code` — full-text search over indexed method bodies
  (SQLite FTS5). Phrase queries, boolean operators, prefix matches,
  proximity all supported. Faster than reading objects to look for code.
  Use when you know a literal token/identifier.
- `xpp_search_semantic` — meaning-based (vector) search over method
  bodies or label text. Finds conceptually-related code even when the
  wording differs ("reverse a posted invoice" → cancellation/credit-note
  logic that shares no keywords). Default `mode=hybrid` fuses it with FTS,
  so it's strictly additive to `xpp_search_code`; reach for it when you
  know *what* you want but not the exact identifier. (Backed by local
  embeddings; `xpp_status.embeddingState` reports readiness — until ready,
  hybrid falls back to full-text.)
- `xpp_find_references` — graph search. Declared structural edges
  (extends, implements, datasource, relations) and optionally source
  mentions (set `includeSourceMentions=true`). Use when you need "who
  uses X" or "what's affected if I change Y".
- `xpp_get_object_xml` — the full on-disk XML for an object. The
  envelope every write tool expects on the way back in.
- `xpp_get_object_methods` — lightweight method listing (signatures
  only, no bodies). Use to map an object before pulling specific
  methods.
- `xpp_get_method_source` — pull a single method body. Pairs with
  `xpp_get_object_methods` for surgical reading.

### Write tools

- `xpp_create_object` — write a NEW object from XML. Fails if the name
  already exists in the target model.
- `xpp_update_object` — overwrite an EXISTING object's XML. The XML
  must be the FULL object representation, not a patch.

> **The `request` envelope — typed create tools.** Every typed `xpp_create_*`
> tool (create_class / create_table / create_form / create_enum / …) takes a
> SINGLE parameter named **`request`**. When a per-type skill shows
> `xpp_create_X({ "name": …, … })`, that object is the **value of `request`** —
> the actual call is `xpp_create_X({ "request": { "name": …, … } })`. A flat,
> unwrapped object fails to bind (and currently surfaces only as a contentless
> "An error occurred invoking 'xpp_create_X'"). Typed `xpp_patch_*` tools instead
> take two params: `name` + `patch`. Get tools take `name` (+ optional
> `outline`/`atPath`/`depth`).

### Label tools (per-entry CRUD on `.label.txt` resource files)

- `xpp_label_search` — regex over one or many label files (case-insensitive).
- `xpp_label_read` — pull a single label by `(label_file_id, language, label_id)`.
- `xpp_label_add` — append one or many labels (batch).
- `xpp_label_update` — change one or many label values/descriptions.
- `xpp_label_delete` — remove one or many labels.

Use these instead of reading whole label files into context; the
files routinely run to hundreds of KB. See `dynamics-xpp:xpp-labelfile`
for the full pattern.

### Project / changeset tools (require `.dynamics-xpp/config.json`)

- `xpp_project_status` — resolved project state: rnrproj, module,
  model, naming conventions, changeset summary, project object
  count. Call this first when orienting in an unfamiliar repo.
- `xpp_project_add_object` / `xpp_project_remove_object` /
  `xpp_project_list_objects` — manage the active `.rnrproj`'s
  `<Content>` references. Create/update auto-add already; these
  are for explicit fix-ups.
- `xpp_changeset_clear` — reset the persistent `(axType, name)`
  list the MCP maintains across sessions. Use after a successful
  compile + check-in to start fresh.

### Build / validate tools (require `.dynamics-xpp/config.json`)

- `xpp_bp_check` — run F&O Best Practice checks (xppbp.exe).
  Scopes: `changeset` (default) | `project` | `explicit`.
  Summary-by-default output; errors full, warnings per-moniker.
  `monikers=[...]` drills into specific rules at full detail.
  Project policy lives in `bestPractices.suppress/escalate` in
  `config.json`. See `plugins/xpp/docs/bp-rules-reference.md`
  for the 184-rule roster.
- `xpp_compile` — VS-equivalent project build via devenv.com.
  Replicates metadata validation → X++ compile (xppcAgent) →
  BP check → CopyReferences → app pool recycle. Cold-start tax
  ~14s, build steps measured in seconds. `rebuild=true` forces
  /Rebuild.

### Other

- `xpp_status` — health / state of the indexer and bridge pool.
- `xpp_rebuild_index` — force a re-scan of the metadata store. Rarely
  needed manually.

### MCP resources

The MCP server also exposes lazy-loadable resources, addressed by URI:

- `xpp://schema/{type}` — the authoritative MS-authored XSD for an AOT
  type. Returns the formal grammar. Validators use it; humans can read
  it to know exactly which child elements are valid. Always treat as
  ground truth.

### Canonical patterns of use

#### Pattern: modify an existing object

```
1. xpp_get_object_xml(axType, name, model?) → full XML
2. edit XML locally
3. xpp_update_object(axType, model, xml)
```

The write tools pre-flight the XML against the matching XSD before
sending anything to the metadata layer. If validation fails you get
back a structured response with the exact line/column/element of every
violation — fix and retry without burning a full round-trip.

#### Pattern: create a new object

```
1. (optional) xpp_find_object on the intended name to make sure
   there's no collision
2. construct the XML using the relevant per-type skill (dynamics-xpp:xpp-class,
   dynamics-xpp:xpp-table, dynamics-xpp:xpp-form, ...) and xpp://schema/{type} as ground truth
3. xpp_create_object(axType, model, xml)
```

For forms specifically, also load the matching `xpp:xpp-pattern-{name}`
skill before constructing — the per-pattern skill carries the
working examples and the named-controls/conventions that the
generic `dynamics-xpp:xpp-form` skill can't.

#### Pattern: surgical method read

```
1. xpp_find_object → locate the class/table
2. xpp_get_object_methods → see what's there
3. xpp_get_method_source → pull just the method(s) you care about
```

Use this instead of `xpp_get_object_xml` when you only need code
behavior and the envelope (properties, fields, relations) is noise
for your task.

#### Pattern: impact / "what calls this"

```
1. xpp_find_references(targetName, targetType?, includeSourceMentions=true)
2. for each result, decide whether you need to inspect the caller's
   source (xpp_get_method_source on the context method)
```

Set `targetType` when the name is ambiguous across types (e.g.
`CustTable` exists as both an AxTable and an AxForm).

#### Pattern: code search

```
1. xpp_search_code(query) — FTS5 over indexed method bodies
   - exact phrase: "select forUpdate"
   - boolean: tax AND withholding
   - prefix: cust*
   - proximity: NEAR(foo bar, 5)
   OR xpp_search_semantic(query) — when you know the INTENT but not the
   tokens ("post and settle a vendor payment"). hybrid mode fuses both.
2. follow up with xpp_get_method_source on interesting hits
```

Faster than reading whole objects to grep them. Rule of thumb: known
identifier → search_code; described behavior → search_semantic.

#### Pattern: validate after authoring

```
1. xpp_create_object / xpp_update_object  → object on disk
2. xpp_bp_check                            → BP feedback (scope=changeset)
3. xpp_compile (at checkpoints)            → real build
```

`xpp_bp_check` against the changeset is the cheap inner-loop
signal — runs xppbp on just what you touched, returns summary
counts so you're not drowned. Use it freely between edits.

`xpp_compile` is the "did I really break the build?" gate. Has a
~14s devenv cold-start tax, so it's not for every edit — reserve
it for checkpoints (end of a work item, before asking the user
to test). It runs the same VS Build pipeline (X++ compile +
AppChecker BP + app pool recycle) and surfaces compiler-level
findings BP can't catch (type-conversion narrowing, missing CoC
`next` calls, ExtensibleEnum hazards).

> **`xpp_bp_check` is NOT a compile — a clean BP result does NOT mean the code
> compiles.** BP runs its rule set over parsed source; it does **not** do full
> type resolution, so it will pass code that references a **non-existent EDT /
> type / identifier** (e.g. typing a field as `InventColorId` when the real EDT
> is `EcoResItemColorName`). The compiler rejects that; BP doesn't. So never
> report "BP clean ⇒ validated/compiles" — `xpp_compile` (and a `success:true`
> with **≥1 project built**) is the ONLY authoritative "does it compile" gate.
> Until you've run it, verify any non-obvious type/EDT/table name actually
> exists with `xpp_find_object` before relying on it.

The two tools are complementary, not duplicative. See
`dynamics-xpp:xpp-project` for the full workflow including the
`<DBSyncInBuild>` rnrproj property that controls whether
`xpp_compile` also runs a database sync.

---

## Skill dispatch — when to load which sub-skill

| Working on... | Load next |
|---|---|
| First-time plugin install / MCP not working | `dynamics-xpp:xpp-setup` |
| First-time repo setup / MCP says "no project configured" / out-of-model-update rejection | `dynamics-xpp:xpp-project` |
| An X++ class (incl. CoC extensions) | `dynamics-xpp:xpp-class` |
| A table | `dynamics-xpp:xpp-table` |
| A form, envelope-level questions | `dynamics-xpp:xpp-form`, then a `dynamics-xpp:xpp-pattern-{name}` |
| A form's inner-section conventions (workspace Related Links, FastTab Header, etc.) | `dynamics-xpp:xpp-form-subpatterns` (in addition to the pattern skill) |
| Wireframing a form's layout before XML | `dynamics-xpp:xpp-wireframe` |
| An EDT (Extended Data Type) | `dynamics-xpp:xpp-edt` |
| An enum | `dynamics-xpp:xpp-enum` |
| A label file or labels | `dynamics-xpp:xpp-labelfile` |
| Any `*Extension` object (AxTableExtension, AxFormExtension, AxMenuExtension, etc.) | **BOTH** `dynamics-xpp:xpp-extension` AND the host-type skill (`dynamics-xpp:xpp-table` for AxTableExtension, `dynamics-xpp:xpp-form` + the form's pattern skill + `dynamics-xpp:xpp-form-subpatterns` for AxFormExtension, `dynamics-xpp:xpp-menu` for AxMenuExtension, ...). The extension skill covers what's legal to add; the host-type skill covers the shapes of what you're adding. |
| Writing X++ data access (select, while select, update_recordset, insert_recordset, delete_from, ttsBegin) | `dynamics-xpp:xpp-data` |
| A menu item (Display / Action / Output) — making forms / runnables / reports reachable | `dynamics-xpp:xpp-menuitem` |
| Security wiring — privileges, roles, duties, XDS policies | `dynamics-xpp:xpp-security` |
| A workspace tile (count tile / KPI tile) | `dynamics-xpp:xpp-tile` |
| An AOT query (substrate for forms, tiles, views, XDS policies) | `dynamics-xpp:xpp-query` |
| An AxView (read-only table-like, backed by a query) | `dynamics-xpp:xpp-view` |
| A custom service (SOAP/REST endpoint over X++ class methods) | `dynamics-xpp:xpp-service` |

The pattern skills (`dynamics-xpp:xpp-pattern-simple-list`,
`dynamics-xpp:xpp-pattern-details-master`, etc. — kebab-case names) exist for
the 10 named F&O form UX patterns. When you're authoring a form,
identify the pattern FIRST (from the task description, the existing
form, or by consulting `dynamics-xpp:xpp-form` for the catalog), then load the
matching pattern skill. If the pattern skill references a sub-pattern
on its inner containers, also load `dynamics-xpp:xpp-form-subpatterns`.

### Feature-completion loops (typical multi-skill workflows)

Several user-facing features require multiple AOT objects
working together. Authoring just one part leaves the feature
unreachable. The canonical loops:

**New UI feature** (form + menu item + security):
1. `dynamics-xpp:xpp-form` + pattern — the form itself
2. `dynamics-xpp:xpp-menuitem` — Display menu item targeting the form
3. `dynamics-xpp:xpp-security` — privilege referencing the menu item
4. `dynamics-xpp:xpp-security` — add the privilege to a duty / role

**Workspace tile**:
1. `dynamics-xpp:xpp-query` — backing query for the count
2. `dynamics-xpp:xpp-menuitem` — Display for click-through
3. `dynamics-xpp:xpp-tile` — the AxTile wiring query + menu item
4. Tile button on workspace form (`dynamics-xpp:xpp-form`)

**Custom integration service**:
1. `dynamics-xpp:xpp-class` — contract classes + service class
2. `dynamics-xpp:xpp-service` — AxService + AxServiceGroup
3. `dynamics-xpp:xpp-security` — privilege with Invoke grant

**Read-shaped data exposure (denormalized view / report substrate)**:
1. `dynamics-xpp:xpp-query` — the join + filter shape
2. `dynamics-xpp:xpp-view` — view wrapping the query, with any computed columns

The dispatch table above is for "I'm working on X type." These
loops are for "I'm building feature Y end-to-end" — they remind
the agent not to stop after step 1.

---

## Things that bite (cross-cutting)

- **X++ is case-insensitive** for identifiers. `next getsalesid()` and
  `next getSalesId()` are the same call. Cosmetic typos in CoC method
  calls compile fine; don't waste effort "fixing" them.
- **The metadata model is XML-backed.** Boolean-looking properties are
  actually `NoYes` enum values serialized as `"Yes"` / `"No"` strings.
  Don't write `true` / `false` in the AOT XML. (X++ source code uses
  `true`/`false` normally — this rule is for the AOT envelope only.)
- **Microsoft application models are sealed (since release 8.0).** You
  cannot modify a Microsoft-shipped property by duplicating it in your
  model. All modifications go through extension objects
  (`AxTableExtension`, `AxFormExtension`, ...) or class extensions with
  Chain of Command (`[ExtensionOf]`). See `dynamics-xpp:xpp-extension`.
- **CDATA-wrap X++ source.** The parser tolerates entity-encoded `<` and
  `&`, but every other tool in the chain (VS designer, BP checks,
  Microsoft's serializer) assumes CDATA. Don't deviate.
- **Names are load-bearing.** The `Name` element inside the XML and the
  on-disk filename must match. Method `Name` inside `<Method>` must
  match the method declared inside the CDATA source. Mismatches silently
  corrupt the AOT.
- **Models matter for visibility.** A class in model A can only see
  types from model A plus model A's declared references. The MCP write
  tools require an explicit `model` parameter; pick deliberately, not
  by default.
- **Some modules ship binary-only.** A subset of Microsoft and ISV
  modules ship as compiled DLLs with no on-disk XML. The dynamics-xpp
  indexer surfaces them too — `xpp_find_object` and `xpp_search_pattern`
  return them, tagged with `source: "runtime"` and `binaryModule: true`.
  What changes when an object is in a binary module:
  - **Metadata is fully visible** — form designs, table schemas, class
    method signatures, EDT properties, menu structure, security
    objects, etc. all read normally through `xpp_get_*` tools.
  - **X++ source bodies are empty** — method names show up but their
    bodies don't. `xpp_get_method_source` returns an empty string for
    runtime methods. `xpp_search_code` silently skips them (no source
    to text-scan).
  - **Writes are rejected** — you cannot mutate an object in a binary
    module. The bridge will fail with a structured error. To customize
    a binary-module object, author an extension in your own model
    (`AxTableExtension`, `AxFormExtension`, [ExtensionOf] CoC class)
    just like you would for any sealed Microsoft object — the host
    being binary doesn't change that pattern.
  - Use `xpp_list_modules` to see the binary catalog (pass
    `binaryOnly=true` to filter). Often-binary publishers include
    Microsoft ISV modules and some customer-purchased AppSource items.

---

## Things the XSD can't tell you

The XSDs at `xpp://schema/{type}` are the ground-truth formal grammar
but they have known limits:

- They declare many semantically-typed properties (CacheLookup,
  TableType, FormStyle, ...) as `xs:string`. The schema validates
  shape, not enum values. Invalid enum values fall through to the
  metadata layer's deserializer, which produces less-informative
  errors. Trust your per-type skill's enum lists.
- They don't carry default values. A property marked `minOccurs="0"`
  means "optional," not "if omitted you get X." Defaults are encoded
  in the .NET domain types. The per-type skills call out the defaults
  that matter; specify only non-defaults to keep your XML minimal.
- They don't validate cross-references. An `ExtendedDataType` element
  pointing at a non-existent EDT, or a `Relation` constraining against
  a missing field, will validate against the XSD and fail at compile.

These are real but bounded — the XSDs catch most malformed XML before
it hits the metadata layer, and the structured validation errors that
come back are the fastest feedback loop in the chain. Trust them, but
don't expect them to validate D365 semantics.

---

## See also

- `predefined-classes.md` (in this skill) — X++ built-in classes
- `predefined-functions.md` (in this skill) — X++ global functions
- `xpp://schema/{type}` — authoritative XSD per AOT type
- The per-type skills (`dynamics-xpp:xpp-class`, `dynamics-xpp:xpp-table`, `dynamics-xpp:xpp-form`, ...)
- The per-pattern skills (`xpp:xpp-pattern-{name}`)
