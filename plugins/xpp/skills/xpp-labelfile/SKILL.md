---
name: xpp-labelfile
description: Use when authoring or modifying labels in D365 F&O — label files (AxLabelFile), label resource text files, individual labels, multi-language label sets. Labels are the localization layer; every user-facing string in F&O should reference a label rather than embedding literal text. Use the xpp_label_* MCP tools for all reads and mutations — never Read or Edit .label.txt files directly.
---

# Authoring labels (`AxLabelFile`)

Labels are the localization layer of D365 F&O. Every user-facing
string — field captions, button text, info/warning/error messages,
help text, form titles — should reference a label, not embed literal
text. The label system is what lets F&O ship in 30+ languages from a
single codebase.

Load `dynamics-xpp:xpp-language` first if you haven't.

---

## Use the MCP tools — do not touch .label.txt files directly

**Do not `Read`, `Edit`, or `Write` `.label.txt` resource files.**
They routinely contain thousands of entries (the larger module files
run to hundreds of KB), Edit/Write requires a prior full Read, and a
single misplaced newline corrupts the format silently.

The MCP server exposes a dedicated tool surface for every label
operation. Use these instead:

| You want to… | Tool |
|---|---|
| Find a label across the codebase by content | `xpp_search_labels` (indexed global search) |
| Find a label in a specific file by regex | `xpp_label_search` (case-insensitive regex, scoped) |
| Read a specific label's value + description | `xpp_label_read` |
| Add one or many labels to an existing file | `xpp_label_add` (batch-capable) |
| Change one or many existing labels | `xpp_label_update` (batch-capable) |
| Remove one or many labels | `xpp_label_delete` (batch-capable) |

The `xpp_label_*` tools route through the bridge: format, encoding
(UTF-8 with BOM), and ordering are preserved; the labels index is
updated synchronously so subsequent searches see your changes.

If you find yourself reaching for `Read` on a `.label.txt`, stop —
you want `xpp_label_read` or `xpp_label_search`. If you find
yourself reaching for `Edit`/`Write`, stop — you want
`xpp_label_add` / `_update` / `_delete`.

### Batch every time you have more than one label

`xpp_label_add`, `_update`, and `_delete` all accept an array of
entries. Always batch when the user's request involves more than one
label — four "From country / From state / To country / To state"
labels go through as **one** `xpp_label_add` call, not four.

### Decision tree: search before you add

1. User wants to label something? Call `xpp_label_search` (or
   `xpp_search_labels` if the file is unknown) for the proposed text
   first. Reusing an existing label is preferable to creating a
   near-duplicate.
2. No match? Call `xpp_label_add` with the new entry (or entries).
3. Reference the label as `@<LabelFileId>:<LabelId>` everywhere
   (table field labels, EDT labels, form captions, info messages).

---

## The two-file model

A label file is **two files on disk**, not one (these paths are FYI
so you understand the model; do not navigate to them directly — use
the tools):

1. **The metadata XML** at
   `PackagesLocalDirectory\<Model>\AxLabelFile\<LabelFileId>_<Lang>.xml`
2. **The resource text file** at
   `PackagesLocalDirectory\<Model>\AxLabelFile\LabelResources\<Lang>\<LabelFileId>.<Lang>.label.txt`

The XML is essentially a pointer; the resource text file holds the
actual label content. The `xpp_label_*` tools hide this split — you
work with `(label_file_id, language, label_id)` triples and the tool
handles both files.

A single label file typically contains hundreds of labels. Group
labels by model/feature/module, not one file per label.

---

## The four IDs (this trips up everyone)

Working with labels involves four different identifiers:

| ID | Description | Example | Used by |
|---|---|---|---|
| **Name** | AxLabelFile artifact name. Format `<LabelFileId>_<Lang>`. | `MyLabels_en-US` | `xpp_find_object`, `xpp_get_object_xml` |
| **LabelFileId** | Logical file identifier. Same on XML and resource sides. | `MyLabels` | The `label_file_id` parameter on every `xpp_label_*` tool |
| **LabelId** | Per-label key within the resource file. | `LogSavedSuccess` | The `label_id` parameter on `xpp_label_read/add/update/delete` |
| **LabelSearchId** | Lookup token. Format `@<LabelFileId>:<LabelId>`. | `@MyLabels:LogSavedSuccess` | Every reference in code/XML/metadata |

The `@File:Id` form (`LabelSearchId`) is the canonical reference shape.
You'll write `@MyLabels:Foo` everywhere — table field labels, form
captions, info messages, EDT labels, button text — to point at a
specific label.

---

## Resource file format (for understanding, not for editing)

Each label is two lines on disk:

```
<LabelId>=<Label text>
 ;<Description>
```

The second line starts with a single space then a semicolon. The
description is shown in tooling (label search, the VS designer) to
help find labels. Encoding is **UTF-8 with BOM**.

The `xpp_label_*` tools produce and consume this format for you;
you do not need to construct lines by hand.

### Examples (illustrative — not something you'd hand-author)

```
@FLM42=Red
 ;Fleet colors
AADDataPrivacyNotice=By enabling and using this feature, you consent to share your data with external systems...
 ;Label for Microsoft Entra ID data privacy notice.
AboutBoxPreviewLabel=Preview
 ;{Locked} appended to the product name in sysabout for preview versions.
```

The `{Locked}` annotation in a description tells the localization
team not to translate certain text fragments. Pass it through the
`description` field on `xpp_label_add` like any other text.

---

## Metadata XML shape (for the rare case you author one)

The `xpp_label_*` tools do not create new label files — that is
deliberately deferred to avoid accidental proliferation. When you
genuinely need a new label file, author the XML through
`xpp_create_object("AxLabelFile", ...)` after confirming with the
user that no existing file fits.

All four elements are required:

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxLabelFile xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
  <Name>LabelFile1_en-US</Name>
  <LabelContentFileName>LabelFile1.en-US.label.txt</LabelContentFileName>
  <LabelFileId>LabelFile1</LabelFileId>
  <RelativeUriInModelStore>MyTestModel\MyTestModel\AxLabelFile\LabelResources\en-US\LabelFile1.en-US.label.txt</RelativeUriInModelStore>
</AxLabelFile>
```

Broken down with placeholders:

```xml
<AxLabelFile xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
  <Name>{LabelFileId}_{Language}</Name>
  <LabelContentFileName>{LabelFileId}.{Language}.label.txt</LabelContentFileName>
  <LabelFileId>{LabelFileId}</LabelFileId>
  <RelativeUriInModelStore>{Model}\{Model}\AxLabelFile\LabelResources\{Language}\{LabelFileId}.{Language}.label.txt</RelativeUriInModelStore>
</AxLabelFile>
```

When creating a label file through `xpp_create_object`, you must
also seed at least one label in the corresponding resource file —
use `xpp_label_add` immediately after the create succeeds.

### Naming-convention shift to watch

Look carefully at the separators:

- **`Name`** uses **underscore**: `LabelFile1_en-US`.
- **`LabelContentFileName`** uses **period**: `LabelFile1.en-US.label.txt`.
- The on-disk **resource file name** also uses period:
  `LabelFile1.en-US.label.txt`.

This inconsistency catches everyone. The XML's `Name` is the AOT
artifact identifier (where underscore is the safe separator); the
file names follow .NET resource-file convention (period-separated
locale parts).

---

## Multi-language labels

Each language gets its own pair of files. They share the same
`LabelFileId` but differ in `Name` and the resource filename:

- `MyLabels_en-US.xml` + `MyLabels.en-US.label.txt`
- `MyLabels_fr.xml` + `MyLabels.fr.label.txt`
- `MyLabels_de.xml` + `MyLabels.de.label.txt`

Always **author `en-US` first**; it's the fallback when a label is
missing in the user's preferred language. The `language` parameter
on every `xpp_label_*` tool selects which language file you target.

Each language file's labels must use **the same `LabelId`s** for
parallel translation. A reference to `@MyLabels:LogSavedSuccess`
resolves to the en-US text in en-US sessions and to the fr text in
fr sessions — but the `LogSavedSuccess` key must be present in both
files.

---

## Typical workflows

### Add labels for a new feature (the canonical case)

User: *"Add labels on the table for From country, From state, To
country, To state."*

1. Identify the target label file. Often the model has a
   conventional one (`CONL` for ContosoRetail, `Fleet` for the Fleet
   demo, etc.). If unsure, use `xpp_find_object` with type
   `AxLabelFile` to enumerate what exists in the model — don't
   `Glob` the metadata directory.
2. Optionally check for prior art: `xpp_label_search(label_file_ids=
   ["CONL"], pattern="from\s+country")` — if a matching label exists,
   reuse it.
3. One batched `xpp_label_add` call with all four entries.
4. Reference each label as `@CONL:FromCountry`, `@CONL:FromState`,
   etc. on the table fields (via `xpp_update_object` on the table).

### Change wording on an existing label

1. `xpp_label_read({label_file_id, label_id, language})` to confirm
   current value.
2. `xpp_label_update` with the new value (and description if
   changing). Single call, even for one label.

### Remove an obsolete label

1. Optional: search for references to `@MyLabels:Foo` via the
   MCP code-search tool to confirm nothing still points at it.
2. `xpp_label_delete({label_file_id, language, label_id})`.

---

## Best practices

> `xpp_bp_check` surfaces two actionable label-related rules:
> `BPErrorLabelIsText` (a property has an inline literal where
> it should reference a label) and `BPErrorLabelNotDefined` (a
> property references no label at all). Both are warnings; if
> your project has a "no hardcoded text" policy, add neither to
> `bestPractices.suppress` — let them surface and fix them.
> `BPErrorUnknownLabel` is fired when a property references a
> label that doesn't exist in any loaded label file (typo or
> deleted label).

### Identify hardcoded text and replace it

When reading or generating code, look for hardcoded text strings that
appear to be user-facing:

- `info()`, `warning()`, `error()` calls with literal strings.
- Form control `Label` properties with inline literals.
- Button `Text` properties with inline literals.
- Help text and validation messages with inline literals.

These should all be replaced with label references for translatability.

### When you find hardcoded text

1. **Search for an existing label.** `xpp_label_search` (scoped to
   the model's label files) or `xpp_search_labels` (global indexed).
2. **If a label already exists** that matches the intent and text,
   reuse it. Don't proliferate near-duplicate labels.
3. **If no match, add a new label** with `xpp_label_add`. Pick a
   meaningful `LabelId` that describes the purpose, not the text.
4. **Replace the hardcoded text** with a `@<LabelFile>:<LabelId>`
   reference.

Example transformation:

```xpp
// Before (hardcoded — won't translate, can't be reused)
info("Customer record saved successfully");

// After (using label — translates, reusable)
info("@Fleet:CustomerSavedSuccess");
```

The corresponding entry (added via `xpp_label_add`):

```
label_file_id: Fleet
language: en-US
labels: [
  { label_id: "CustomerSavedSuccess",
    value: "Customer record saved successfully",
    description: "Confirmation message shown after a customer record is persisted." }
]
```

### LabelId naming

- **Meaningful, not text-cribbed.** `CustomerSavedSuccess` is
  meaningful; `CustomerRecordSavedSuccessfully` cribs the text and
  becomes wrong if the text changes.
- **PascalCase or descriptive prefix-style.** Both are used in the
  wild — the F&O Application module uses descriptive prefixes
  (`AAD_PrivacyNotice`, `ABC_ButtonText`), customer code typically
  uses PascalCase (`LogSavedSuccess`).
- **Unique within the LabelFileId.** `xpp_label_add` will reject a
  duplicate `LabelId` rather than silently overwrite — use
  `xpp_label_update` if you mean to change an existing one.

### Description usage

The description is for the human running the localization tools.
Good descriptions:

- Explain *where* the label appears ("Caption for the Severity field
  on the log entry form").
- Identify locked text fragments via `{Locked}` annotations.
- Reference Jira/work-item IDs when the label was created for a
  specific feature.

---

## Things the tools (and the XSD) can't tell you

- **Labels referenced by code don't exist.** A `@MyLabels:Foo`
  reference where `Foo` isn't in the resource file shows up at
  runtime as the literal `@MyLabels:Foo` string in the UI. Not a
  load-time failure. After bulk renames, search for stale references
  via the MCP code-search tool.
- **Language parity.** If `MyLabels_en-US.label.txt` has `Foo` and
  `MyLabels_fr.label.txt` doesn't, French users see the en-US
  fallback. No load-time warning. When adding a label, also add the
  corresponding entry in each language file the model ships.
- **Semantic duplication.** Two distinct `LabelId`s with identical
  text are valid but wasteful. `xpp_label_search` before adding.
- **`xpp_bp_check` lags behind freshly-added labels.** `xppbp.exe`
  loads its label index from the metadata cache; labels added via
  `xpp_label_add` are on disk immediately, but BP-check may still
  report `BPErrorUnknownLabel` until a subsequent `xpp_compile` (or
  the metadata cache refreshes on its own). If you see this error
  on a label you just added, run `xpp_compile` first, then re-run
  `xpp_bp_check`. Not a blocker — the label IS authored — but
  expect the lag.

---

## See also

- `dynamics-xpp:xpp-table` — table field labels reference labels via
  `<Label>@File:Id</Label>`.
- `dynamics-xpp:xpp-edt` — EDT labels and help text reference labels.
- `dynamics-xpp:xpp-enum` — enum value labels reference labels.
- `dynamics-xpp:xpp-form` — form captions and control labels reference labels.
- `xpp://schema/AxLabelFile` — authoritative XSD (for the rare new-file case).
