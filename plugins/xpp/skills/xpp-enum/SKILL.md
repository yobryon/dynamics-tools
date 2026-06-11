---
name: xpp-enum
description: Use when authoring or modifying a D365 F&O base enum (AxEnum). Enums are the discrete-value typedefs of the platform — combo boxes on forms, filter values in queries, integer columns in SQL. Covers values, labels, persistence semantics, and the durable-API treatment of enum names.
---

# Authoring base enums (`AxEnum`)

Base enums are the discrete-value typedefs of D365. They show up as:

- Combo boxes on forms.
- Filter values in queries.
- Integer (or string) columns under the hood in SQL.
- `MyEnum::ValueName` literals in X++ code.

Microsoft's Copilot extension shipped an empty prompt for AxEnum
(loader stub only). The substantive content below is from our
experience plus the AxEnum XSD plus the parked F&O recipes.

Load `dynamics-xpp:xpp-language` first if you haven't.

---

## What an AxEnum is

A base enum is a small, ordered set of named values with shared
metadata. The metadata lives in
`PackagesLocalDirectory\<Model>\AxEnum\<Name>.xml`. The XSD is at
`xpp://schema/AxEnum`.

Each enum carries:

- `Name` — PascalCase, globally unique.
- `Label` / `HelpText` — display metadata for the enum as a whole
  (shown in property panes, dev tools).
- `UseEnumValue` — persistence mode (see below).
- `EnumValues` — the ordered set of values.

Each value carries:

- `Name` — the X++-side symbol (PascalCase).
- `Label` — the user-facing display text.
- `Value` — the underlying integer (stored in SQL when
  `UseEnumValue="Yes"`).

---

## The reuse-first rule (same as EDTs)

**Search before authoring.** F&O ships base enums for the common cases:

- `NoYes` / `NoYesId` — boolean-shaped.
- `CustVendBlocked` — block status for customer/vendor.
- `Gender`, `MaritalStatus`, days of week, months — common
  enumerations.
- `LedgerJournalACType` — account-type discriminators.
- Many module-specific status enums.

Use `xpp_find_object` with `axType="AxEnum"` plus the domain term.
Author new only when the semantic is genuinely new.

---

## Authoring through dynamics-xpp

AxEnum is the first AOT type with **typed domain tools** — you
work with a small JSON shape and we generate the XML for you.
Three tools, mirroring CRUD:

| Tool | Purpose |
|---|---|
| `xpp_create_enum(request)` | Create a new AxEnum from a typed request. |
| `xpp_get_enum(name)` | Read an existing AxEnum as its domain shape. |
| `xpp_patch_enum(name, patch)` | Apply a partial update; null patch fields preserve current state, non-null overwrite. |

For an MS-shipped enum, write an `AxEnumExtension` instead —
the base enum's `IsExtensible` must be true for that to work.
See `dynamics-xpp:xpp-extension`.

The raw `xpp_create_object` / `xpp_update_object` tools are
still available as escape hatch if you need a property the
domain shape doesn't cover, but for enum authoring the domain
tools are the path.

---

## Minimum viable enum (domain shape)

```jsonc
xpp_create_enum({
  "name": "MyLogSeverity",
  "label": "@MyLabels:LogSeverityLabel",
  "help": "@MyLabels:LogSeverityHelp",
  "values": [
    { "name": "Information", "label": "@MyLabels:SeverityInformation" },
    { "name": "Warning",     "label": "@MyLabels:SeverityWarning" },
    { "name": "Error",       "label": "@MyLabels:SeverityError" }
  ]
})
```

Notes:

- Listing order = display order in combo boxes AND the persisted
  integer position (ordinal 0, 1, 2, ...). See "Persistence and
  durability" below for why ordering is load-bearing.
- Every value should have a `label` reference. Inline literals
  compile but don't translate.
- `isExtensible` defaults to **true** in the domain shape — the
  modern F&O convention. Set explicitly to `false` only when you
  have a reason to seal the enum against future extension.
- `useExplicitValues` defaults to **false** (auto-assign by
  ordinal). Set to `true` only when you need to preserve specific
  integers (e.g., the enum must align with an external system's
  numeric scheme).
- `style` defaults to **ComboBox**. Use `RadioButton` for small
  enums (2-4 values) where you want all options always visible.

### Patching an existing enum

```jsonc
xpp_patch_enum("MyLogSeverity", {
  "label": "@MyLabels:UpdatedLabel",
  "help":  "Patched help text"
})
```

Merge-patch semantics — null/omitted fields are left untouched.
Non-null fields overwrite. `values` non-null replaces the
WHOLE values list (collection-level mutation is intentionally
explicit; the agent can read with `xpp_get_enum`, mutate the
list in-process, and patch back).

---

## Enum-level properties

| Property | Typical | Notes |
|---|---|---|
| `Name` | (required) | PascalCase, globally unique. |
| `Label` | `@LabelFile:Id` | Display name for the enum as a whole. |
| `HelpText` | `@LabelFile:Id` | Tooltip/dev-help. |
| `UseEnumValue` | `Yes` (default) / `No` | See below. |
| `IsExtensible` | **`true`** (modern default) / `false` | Whether AxEnumExtension can add values. The domain tool defaults to true (current F&O best practice). The raw XML omits the tag historically; setting it explicit `true` in new authoring is preferred. |
| `EnumValues` | (required) | At least one `AxEnumValue`. |

### `UseEnumValue` — the persistence-mode dial

This is the most important property and the most commonly
misunderstood:

- **`UseEnumValue="Yes"` (default).** SQL stores the integer `Value`.
  Renaming a value's `Name` is safe (X++ code that references the
  symbol moves with it). Changing the `Value` is **breaking** —
  existing rows now decode to a different symbol.
- **`UseEnumValue="No"`.** SQL stores the value's `Name` as a string.
  Renaming a value's `Name` is **breaking** for existing data.
  Reordering values is safe; `Value` doesn't matter for persistence.

The default `Yes` is appropriate for almost every enum — integer
storage is compact, indexable, and SQL-fast. Only flip to `No` for
special cases (e.g. enums that need to be human-readable in raw SQL
queries).

### `IsExtensible`

Set true on enums you want third parties or extension models to be
able to add values to. F&O ships many enums as extensible by default,
and the modern guidance is to make YOUR enums extensible too unless
there's a specific reason to seal them. The `xpp_create_enum` tool
defaults to `isExtensible: true` accordingly.

Set false only when you're absolutely sure no downstream model
should be able to add values (e.g., the enum drives a code path with
exhaustive switch statements that you don't want forced to change).

#### Extensible enum values are NOT design-time constants

For an extensible enum, a member's integer is **allocated by the dbsync
engine at deployment**, not fixed in code — and it differs between
environments. Other models' added values can land before yours, so a value
that's `22` on your dev box may deploy as `23` in production. **Never hardcode
or compare an extensible enum's numeric value** (in X++, in stored data, in a
delimited int list). Always dereference the symbol: `enum2int(MyEnum::Member)`
at runtime, or `switch` on the symbol. `xpp_get_enum` reflects this — for an
extensible enum it returns `valuesAuthoritative: false` and omits the
per-member integers, because the numbers on this box are not authoritative.
Numbers are only meaningful for a **non-extensible** enum, where the AOT
dictates them.

---

## Value-level properties

| Property | Notes |
|---|---|
| `Name` | (required) PascalCase. The X++-side symbol (`MyEnum::Name`). |
| `Label` | `@LabelFile:Id`. User-facing display text. |
| `Value` | (required) Integer. Conventionally sequential from 0. |
| `IsHidden` | `Yes` / `No`. Hidden values don't appear in UI dropdowns but are still valid for code/data. Used to deprecate values gracefully. |
| `ConfigurationKey` | Restricts visibility by configuration key. |
| `CountryRegionCodes` | Restricts visibility by ISO country code. |

---

## Persistence and durability — treat enum names as a public API

Enum value names are part of the **public API** of your model. Code in
other models, customer code, integrations, and data extracts all
reference `MyEnum::Information` by name. Treat them carefully:

### Safe changes

- **Adding new values at the end** with the next sequential integer.
- **Adding `Label` translations** for existing values.
- **Updating `HelpText`** on values.

### Breaking changes (audit consumers first)

- **Reordering existing values.** Changes the display order on combo
  boxes and the position of any "first declared value" defaults.
- **Hiding a value** (`IsHidden="Yes"`). Code still references the
  symbol but users can't pick it.

### Hard breaks (avoid)

- **Renaming a value's `Name`.** X++ code that references the symbol
  fails to compile. Worse: under `UseEnumValue="No"`, persisted SQL
  rows now have stale strings.
- **Changing a value's `Value` integer.** Under `UseEnumValue="Yes"`
  (default), existing SQL rows decode to a different symbol — silent
  data corruption.
- **Deleting a value.** Same surfaces — `MyEnum::Foo` references break
  compile; persisted rows decode to nothing.

**To deprecate a value:**

1. Add `IsHidden="Yes"` so it disappears from UI.
2. Leave the symbol in place so existing code still compiles and
   persisted rows still decode.
3. Migrate consumers off the value over time.
4. Eventually remove (only after audit shows no consumers).

---

## Use a default

Most consuming tables specify an initial value via `AxTableFieldEnum`:

```xml
<AxTableField xmlns="" i:type="AxTableFieldEnum">
  <Name>Severity</Name>
  <EnumType>MyLogSeverity</EnumType>
  <EnumValue>Information</EnumValue>
</AxTableField>
```

Without an `<EnumValue>`, the field defaults to the **first declared
value** in the enum. This makes the order of `EnumValues` in the XML
load-bearing for tables that don't specify a default. Pick the first
value deliberately.

---

## `NoYes` already exists — use it

F&O ships `NoYes` (and `NoYesId`) as the canonical yes/no enum:

```
NoYes::No = 0
NoYes::Yes = 1
```

Every boolean-shaped table field should use `<EnumType>NoYes</EnumType>`
on `AxTableFieldEnum`, not `AxTableFieldBoolean`. The convention is
strong enough that most senior F&O developers will flag a raw
boolean field for review.

---

## Best-practice naming

- **Enum name** — PascalCase, singular noun-ish (`Gender`,
  `OrderStatus`, `Severity`).
- **Value names** — PascalCase, no prefix. Don't repeat the enum
  name (`MyLogSeverity::Severity_Warning` is wrong; just `Warning`).
- **Avoid generic words** that collide across models (`Status`,
  `Type`, `Mode`) — prefix or suffix with domain (`OrderStatus`,
  `PaymentType`, `CalculationMode`).

---

## Things the XSD can't tell you

- **Persistence semantics.** XSD validates the value list shape but
  doesn't tell you which mode (`UseEnumValue`) you should pick.
- **API stability.** XSD treats every value as equally mutable; in
  reality renames/reorderings break consumers.
- **Default-value mechanics.** XSD doesn't tell you the first declared
  value is the implicit default for fields without `<EnumValue>`.
- **Extension constraints.** An `AxEnumExtension` adding values to a
  base enum requires the base enum's `IsExtensible="Yes"`. XSD passes
  the extension regardless; load-time fails if the base isn't
  extensible.

---

## See also

- `dynamics-xpp:xpp-edt` — `AxEdtEnum` wraps an enum into the EDT layer with
  additional metadata.
- `dynamics-xpp:xpp-table` — `AxTableFieldEnum` references enums via `EnumType`
  and optionally `EnumValue` for the default.
- `dynamics-xpp:xpp-extension` — `AxEnumExtension` for adding values to a shipped
  enum.
- `dynamics-xpp:xpp-labelfile` — enum labels reference labels.
- `xpp://schema/AxEnum` — authoritative XSD (used by the
  `xpp_create_object` escape hatch when the domain shape doesn't
  cover what you need).

---

## Note on the typed authoring surface

AxEnum is the **first AOT type** to use typed domain tools instead
of the raw XML round-trip. The agent-facing API lives in
`Xpp.Service.Domain.Enums.CreateEnumRequest` (and friends) — every
property carries a description that surfaces to LLMs through the
MCP tool schema. Sensible defaults eliminate boilerplate
(IsExtensible=true, Style=ComboBox, etc.). The service maps the
domain request to the AOT XML the bridge expects.

If you find yourself needing a property that the domain tool
doesn't expose (e.g., a corner case in `AdvancedEnumOptions`),
you can:

1. Use `xpp_get_object_xml` + `xpp_update_object` for that specific
   case as a one-off escape hatch.
2. Or surface the gap so the property can be added to the domain
   shape — the domain layer is meant to cover ~100% of authoring;
   gaps are bugs.

More AOT types will move to typed domain tools over time.
