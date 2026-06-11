---
name: xpp-edt
description: Use when authoring or modifying a D365 F&O Extended Data Type (AxEdt). EDTs are the typedef layer — every well-authored table field references one. Covers all EDT primitive variants (string/int/int64/real/date/utcdatetime/timeofday/enum/guid/container/boolean), lookup EDTs with table references, array elements, inheritance via `Extends`, and the reuse-first rule.
---

# Authoring Extended Data Types (`AxEdt`)

Extended Data Types are the typedef layer of D365 F&O. Every
well-authored field on a table references one. Every form control that
displays a field inherits its label, help, length, formatting, and
lookup relations from the EDT. Get EDTs right and the rest of the
surface is much cheaper to maintain.

Load `dynamics-xpp:xpp-language` first if you haven't.

---

## What an AxEdt is

An EDT is a named, reusable specification for a column-shaped value.
It extends a primitive type (boolean, int, int64, real, str, date,
enum, guid, timeOfDay, utcDateTime, container) and carries metadata
about that value: label, help text, length/precision, allowed range,
display formatting, and optionally a lookup relation to a referenced
table.

The metadata lives in `PackagesLocalDirectory\<Model>\AxEdt\<Name>.xml`.

The XSD is at `xpp://schema/AxEdt`.

---

## The reuse-first rule

**Strong default: reuse, don't author.** Before creating a new EDT,
search for an existing one that matches your semantic intent. F&O
ships thousands of EDTs.

Use `xpp_find_object` with `axType="AxEdt"` plus the entity name
fragment (e.g. `"Customer"`, `"Account"`, `"Quantity"`, `"Date"`).
Existing EDTs that frequently fit:

- `Description`, `Description255` — generic description fields.
- `Name` — generic name fields.
- `AccountNum`, `CustAccount`, `VendAccount`, `ItemId` — domain
  identifiers with built-in lookup relations.
- `Amount`, `AmountMST`, `Qty`, `Unit` — financial / quantity fields.
- `TransDate`, `FromDate`, `ToDate` — date fields with conventional
  labels.
- `NoYes` (as an enum EDT) — pick from a list of yes/no values.

**Create a new EDT only when:**

- The semantic is genuinely new (a domain identifier no one's modeled
  before).
- You need a typed lookup to a new table you're creating (the lookup
  EDT pattern below).
- An existing EDT is close but you need a stricter or longer version
  that doesn't map cleanly via `Extends`.

**Don't create one when:**

- You just need a string of length N. Find a `Description<N>` or
  `Name`.
- The EDT would only be used in one place — that's a sign you're
  over-typing.
- The field is genuinely a primitive without business semantics (a
  raw counter, a flag).

---

## Authoring through dynamics-xpp

AxEdt uses **typed domain tools** — you work with a small JSON
shape and the service generates the XML. Three tools, mirroring
CRUD:

| Tool | Purpose |
|---|---|
| `xpp_create_edt(request)` | Create a new AxEdt from a typed request. |
| `xpp_get_edt(name)` | Read an existing AxEdt as its domain shape. The response can be passed straight back into `xpp_create_edt` to clone. |
| `xpp_patch_edt(name, patch)` | Apply a partial update. Null patch fields preserve current state, non-null overwrite. `BaseType` is **not** patchable — change discriminator = re-create. |

For an MS-shipped EDT, write an `AxEdtExtension` instead — see
`dynamics-xpp:xpp-extension`.

The raw `xpp_create_object` / `xpp_update_object` tools remain
available as an escape hatch, but the domain tools are the path
for EDT authoring.

---

## BaseType discriminator

The primitive variant is the `BaseType` field on the request (which
maps to the on-disk `i:type` on the root element):

| `BaseType` | Underlying primitive | Options block |
|---|---|---|
| `String` | string | `string` |
| `Int` | 32-bit integer | `numeric` |
| `Int64` | 64-bit integer | `numeric` |
| `Real` | decimal number | `numeric` + `real` |
| `Date` | calendar date | `date` |
| `Time` | time of day | `time` |
| `UtcDateTime` | UTC date+time | `utc` (date + time + tz) |
| `Enum` | typed enumeration | `enum` (EnumType required) |
| `Guid` | GUID | — |
| `Container` | X++ container | — |

Each `BaseType` gates which nested options block applies. Passing
the wrong block (e.g. `string` options with `BaseType=Int`) is
silently ignored — the mapper only reads the block that matches.

(No `Boolean` variant — F&O strongly prefers the `NoYes` enum
EDT over a raw boolean for persisted values. See the note in the
`AxEdtBoolean` subsection.)

---

## Common elements (all EDT types)

Every EDT can carry these properties regardless of primitive:

- **`Name`** (required) — Unique identifier, PascalCase.
- **`Label`** — `@<LabelFile>:<LabelId>` reference. The display label
  used wherever this EDT shows up (form captions, report headers).
- **`HelpText`** — `@<LabelFile>:<LabelId>` reference. Tooltip /
  field-level help.
- **`Extends`** — Parent EDT name. **Must be the same base primitive.**
  Inherits all properties of the parent; overrides apply on top.
- **`ConfigurationKey`** — License/configuration-key restriction. Field
  disappears when the key is disabled.
- **`CountryRegionCodes`** — Comma-separated ISO country codes. Field
  visible only in those legal entities.
- **`FormHelp`** — Reference form name for lookups. Usually
  inherited from the base or specified by the lookup pattern below.
- **`CollectionLabel`** — Plural form of the label (e.g. "Customers"
  for `CustAccount`'s "Customer"). Used in collection-shaped UIs.

---

## Per-primitive properties and examples

### BaseType=String

```jsonc
xpp_create_edt({
  "name": "MyStringType",
  "baseType": "String",
  "label": "@SYS12345",
  "helpText": "@SYS12346",
  "string": {
    "stringSize": 20,
    "changeCase": "UpperCase",
    "adjustment": "Left"
  },
  "advanced": { "displayLength": 10 }
})
```

- **`string.stringSize`** — Max length. Default: 10. **This is a
  column type change in SQL** — growing on an existing EDT is fine;
  shrinking is dangerous (truncation).
- **`string.changeCase`** — `Auto` / `None` / `UpperCase` /
  `LowerCase` / `SentenceCase`. Applied on data entry.
- **`string.adjustment`** — Text alignment: `Auto` / `Left` /
  `Right` / `Center`.
- **`string.displayHeight`** — Multi-line height in rows (>1 makes
  the field render as a textarea).
- **`advanced.displayLength`** — UI display width in chars.

### BaseType=Int / Int64

```jsonc
xpp_create_edt({
  "name": "LineNumber",
  "baseType": "Int",
  "label": "@SYS12346",
  "numeric": {
    "allowNegative": false,
    "showZero": true
  }
})
```

- **`numeric.allowNegative`** — Default: true.
- **`numeric.showZero`** — Default: true. If false, zero displays
  as empty.
- **`numeric.signDisplay`** — `Auto` / `None` / `Prefixed` /
  `Suffixed` / `Parentheses`. Controls how negative values render.

Use `BaseType=Int64` for RecId-typed surrogate-foreign-key columns
and any counter that may exceed 2^31.

### BaseType=Real

```jsonc
xpp_create_edt({
  "name": "MyAmount",
  "baseType": "Real",
  "label": "@SYS12347",
  "numeric": { "allowNegative": true, "signDisplay": "Parentheses" },
  "real": { "noOfDecimals": 2, "formatMST": true }
})
```

- **`real.noOfDecimals`** — Decimal places. Common: 2 (currency),
  4 (rates), 6 (high-precision quantities).
- **`real.formatMST`** — Currency-style formatting (no thousands
  separator, fixed decimals).
- **`real.autoInsSeparator`** — Auto-insert thousands separator on
  entry. Default: true.

Real EDTs combine the `numeric` block (sign-display etc.) with the
`real` block (decimals / formatting).

### BaseType=Date / UtcDateTime / Time

```jsonc
xpp_create_edt({
  "name": "BirthDate",
  "baseType": "Date",
  "label": "@DAT:BirthDate",
  "date": { "dateFormat": "DMY", "dateSeparator": "-" }
})
```

- **`date.dateFormat`** — `Auto` / `YMD` / `YDM` / `MYD` / `DYM` /
  `MDY` / `DMY`.
- Day/month/year display flags are `Auto` / `Yes` / `No`.
- **`time.timeFormat`** — `Auto` / `Hour24` / `AMPM`.
- **`utc`** is a superset combining `date` + `time` +
  `timezonePreference` (`User` / `Company` / `UTC` / `None`).

Most often, you'll leave these unset and let the EDT inherit
formatting from a base EDT like `TransDate`.

### BaseType=Enum

```jsonc
xpp_create_edt({
  "name": "MyCustomerType",
  "baseType": "Enum",
  "label": "@LIT:CustomerType",
  "enum": { "enumType": "CustType" }
})
```

- **`enum.enumType`** (required) — The base `AxEnum` name. The EDT
  is a typed alias of that enum.
- **`enum.style`** — `Auto` / `Combobox` / `Radiobutton`. UI
  rendering hint. (Note the lowercase second word — F&O distinguishes
  AxEdtEnum's `Style` from AxEnum's `Style`, which uses `ComboBox` /
  `RadioButton`.)

Enum EDTs **cannot** carry `arrayElements` or string/numeric
options. The mapper ignores those blocks when `BaseType=Enum`.

### BaseType=Guid / Container

Minimal structure — primarily `name`, `label`, `helpText`. No
subtype options block; these are pure base-type EDTs.

```jsonc
xpp_create_edt({
  "name": "MyExternalId",
  "baseType": "Guid",
  "label": "@LIT:ExternalIdLabel"
})
```

### AxEdtBoolean (not supported in the domain layer — by design)

There is no `Boolean` `BaseType`. F&O convention is to use the
`NoYes` enum EDT for all persisted booleans (checkbox UI, integer
storage, room to extend later). For an existing `AxEdtBoolean`,
read it via `xpp_get_object_xml` and use the raw `xpp_update_object`
escape hatch; new code should use `BaseType=Enum` with
`enumType="NoYes"`.

---

## Lookup EDTs (the powerful pattern)

A lookup EDT carries `referenceTable` + `tableReferences` so any
field that uses it **auto-renders as a typed lookup**:

```jsonc
xpp_create_edt({
  "name": "MyLogRefId",
  "baseType": "String",
  "label": "@MyLabels:LogRefIdLabel",
  "string": { "stringSize": 20 },
  "advanced": { "referenceTable": "MyLogTable" },
  "tableReferences": [
    { "table": "MyLogTable", "relatedField": "LogId" }
  ]
})
```

A table field declared with
`<ExtendedDataType>MyLogRefId</ExtendedDataType>` will automatically
present a lookup to `MyLogTable.LogId` on every form that displays
it. This is the lever that makes form authoring cheap.

> **Set the TableReference whenever the EDT has a standard backing table —
> including for UNBOUND controls.** The lookup affordance comes from the EDT's
> `tableReferences`, NOT from a form datasource. So an unbound control whose
> `ExtendedDataType` is set directly (e.g. an Operational Workspace **page
> filter**, where the form has no datasources at all) still gets a working
> dropdown — *only* if the EDT carries the TableReference. Without it the filter
> renders but the dropdown is silently dead, with no error to explain why. If
> you author a custom id-style EDT and a standard table holds its valid values,
> add the `referenceTable` + `tableReferences` (table + relatedField) as a
> matter of course.

### Filtered references

You can also constrain lookups by a related field's value — set
`filterValue` on the `tableReferences` entry:

```jsonc
"tableReferences": [
  { "table": "CustTable", "relatedField": "PartyType", "filterValue": "Customer" },
  { "table": "VendTable", "relatedField": "PartyType", "filterValue": "Vendor" }
]
```

This produces a polymorphic lookup that resolves to either
`CustTable` or `VendTable` depending on `PartyType`. The mapper
emits `xsi:type="AxEdtTableReferenceFilter"` on the wire for any
entry with a non-null `filterValue`. Used for things like a
party-reference EDT that can point at customer or vendor.

### Relations vs TableReferences

`relations` (the legacy AX-2012 pattern) and `tableReferences` (the
modern F&O pattern) both exist on the EDT shape. New code should
prefer `tableReferences` for lookups and put the actual foreign-key
constraint on the table (see "modern relation strategy" below).
Use `relations` only when extending or matching an existing EDT
that already does it that way. For a fixed-value relation, set
`fixedValue` on the relations entry (mapper emits
`xsi:type="AxEdtRelationFixed"`).

### Note on modern relation strategy

In F&O the recommended pattern is to put the relation on the **table**
(via `<Relations>` on the consuming table) AND set
`<IgnoreEDTRelation>Yes</IgnoreEDTRelation>` on the field. The EDT
keeps the lookup-form/reference information for UI purposes; the
actual foreign-key constraint lives on the table. This is a
divergence from AX 2012 where EDT relations were the primary
mechanism.

---

## Array elements

EDTs can declare multiple "columns" of the same type. This produces
composite EDTs (e.g. an address EDT with three street lines):

```jsonc
xpp_create_edt({
  "name": "MyAddressLines",
  "baseType": "String",
  "string": { "stringSize": 60 },
  "arrayElements": [
    { "name": "Line1", "label": "@LIT:Part1" },
    { "name": "Line2", "label": "@LIT:Part2" }
  ]
})
```

Each array element gets its own label and can override the parent
EDT's relations / tableReferences. The consuming table column
materializes as N SQL columns (named `MyField[1]`, `MyField[2]`, ...).

Array EDTs are uncommon in modern F&O — most arrays got refactored
into separate tables. Use only for genuinely tightly-coupled
composite values where a separate table would be overkill.

---

## Inheritance via `extends`

EDTs can extend other EDTs to inherit properties:

```jsonc
xpp_create_edt({
  "name": "MyCustomerAccount",
  "baseType": "String",
  "extends": "CustAccount",
  "label": "@MyLabels:CustomerAccountLabel"
})
```

`MyCustomerAccount` inherits `stringSize`, `helpText`, lookup
references, and formatting from `CustAccount`, and overrides only
the `label`. This is how F&O ships domain-specific variants of
common EDTs. The `BaseType` of the new EDT **must** match the base
EDT's primitive.

**Inheritance is constrained:**

- The `Extends` target must have the **same base primitive**. A
  string EDT can only extend another string EDT.
- You **cannot shrink** size constraints (e.g., narrow `StringSize`).
- You can add properties, change labels, add table references.

---

## Key validation rules

1. **`name` is required.** All EDTs.
2. **`enum.enumType` is required when `BaseType=Enum`.** The
   domain mapper rejects the request before the bridge hop.
3. **`extends` target must match base primitive.** Compile fails
   otherwise.
4. **Referenced tables/fields must exist.** A `tableReferences`
   entry pointing at a missing table passes XSD and fails at
   compile.
5. **Size constraints cannot shrink in inheritance.**
6. **Enum EDTs cannot have `arrayElements` or string/numeric
   options.** The mapper ignores out-of-band option blocks.

---

## Common defaults

When the property is omitted, the metadata layer applies these:

- `string.stringSize`: 10
- `real.noOfDecimals`: 2
- `numeric.allowNegative`: true
- `numeric.showZero`: true
- `string.adjustment`: Left (default for text)
- `real.autoInsSeparator`: true
- `string.changeCase`: Auto (no transform)
- `enum.style`: Auto (renders as combobox)
- `advanced.visibility`: Public

Specify only when you want a non-default value. The mapper omits
defaulted properties from the on-disk XML, which keeps diffs tight
and signals intent.

---

## When the agent should suggest a new EDT

When reading existing X++ code or table XML and you see:

- A method returning or accepting `str`, `int`, `real`, `Date`,
  `utcdatetime`:
  1. Analyze the variable name, surrounding logic, and what the
     value represents (an account number? a quantity?).
  2. Check for an existing EDT that matches — `xpp_find_object` with
     `axType="AxEdt"` plus the inferred domain term.
  3. If a matching EDT exists, suggest updating the method/field to
     use it.
  4. If no match, suggest creating one with the appropriate primitive
     and properties.
  5. **Preserve method semantics** — don't change the underlying
     type unless the inferred EDT matches logically.

- A table field with a raw type:
  - Same flow. Check for an existing EDT; suggest replacing
    `StringSize`+`Label`+`HelpText` triples with `<ExtendedDataType>`
    references.
  - Preserve all non-EDT metadata (indexes, mandatory settings,
    relations).

---

## Things the XSD can't tell you

- **EDT changes propagate to consumers.** Altering an EDT's
  `StringSize` triggers a table sync on every consuming table.
  `Description255` changing to `Description500` is a deployment event,
  not a local change.
- **`StringSize` shrink is destructive.** SQL Server stores the column
  at that width; growing is fine, shrinking truncates existing data.
- **Lookup-form inheritance.** `FormHelp` chains through `Extends` — if
  the base EDT has a `FormHelp`, you inherit unless you explicitly
  override.
- **Modern relation strategy.** XSD allows EDT-level `<Relations>`,
  but F&O guidance is to put relations on the table and use the EDT
  for lookup/UI metadata only.
- **Inheritance from a Microsoft-shipped EDT.** You can `Extends` a
  shipped EDT from your model. The base is sealed (you can't modify
  it), but you can derive new EDTs from it freely.

---

## See also

- `dynamics-xpp:xpp-table` — table fields consume EDTs via `<ExtendedDataType>`.
- `dynamics-xpp:xpp-enum` — `BaseType=Enum`'s `enumType` references an `AxEnum`.
- `dynamics-xpp:xpp-extension` — `AxEdtExtension` for modifying shipped EDTs.
- `dynamics-xpp:xpp-labelfile` — EDT labels and help text reference labels.
- `xpp://schema/AxEdt` — authoritative XSD (used by the
  `xpp_create_object` escape hatch when the domain shape doesn't
  cover what you need).

---

## Note on the typed authoring surface

AxEdt is the **second AOT type** (after AxEnum) to use typed domain
tools instead of the raw XML round-trip. The agent-facing API lives
in `Xpp.Service.Domain.Edts.CreateEdtRequest` (and friends) — every
property carries a description that surfaces through the MCP tool
schema. The polymorphic root (`<AxEdt i:type="AxEdtString">` etc.)
is collapsed into the single `BaseType` discriminator + per-subtype
nested options blocks, so the agent never has to deal with the
xsi:type machinery directly.

If you find yourself needing a property the domain shape doesn't
cover, you can:

1. Use `xpp_get_object_xml` + `xpp_update_object` for that one case
   as an escape hatch.
2. Or surface the gap so the property can be added — the domain
   layer is meant to cover ~100% of authoring; gaps are bugs.
