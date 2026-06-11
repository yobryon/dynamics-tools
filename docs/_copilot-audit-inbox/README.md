# D365 VS2022 Extension Reverse-Engineering Notes

Captured from `C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\Extensions\uxkd01ri.02h`
on the v2-rewrite branch, while scoping the write surface for dynamics-xpp v2.

These are **inspiration / reference** for our skill-writing and write-surface design.
Not redistributed; on-disk only for this dev box.

## Top-level findings

1. **Two parallel authoring representations exist in the toolchain:**
   - **X++ source string** (`.xpp`-style) — what the VS editor surface produces. Round-tripped by
     `Microsoft.Dynamics.AX.Framework.Xlnt.XppParser.Pass2.XppCodeParser.ParseSourceString(name, type, source, provider, diagnostics, element)`
     and `Microsoft.Dynamics.Framework.Tools.MetaModel.MetaModelUtility.GetXppSourceText(element)`.
     Available for: AxClass, AxTable, AxMap, AxView, AxForm, AxQueryComposite, AxQuerySimple,
     AxDataEntityView, AxAggregateDataEntity, AxMacroDictionary (the `TypesWithILCode` list).
   - **AOT XML** (the on-disk format under `PackagesLocalDirectory\<Model>\<AxType>\<Name>.xml`).
     Round-tripped by `MetadataProvider[type].Create/Update(element, ModelSaveInfo)` and a temp-file
     dance through `IDesignMetadataService.LoadMetadataRootElementFromExternalFile(path)`.

2. **MS chose XML as the AI-authoring contract.** Their Copilot integration
   (`Microsoft.Dynamics.Framework.Tools.GitHubCopilot.MetadataService`) takes/returns full XML strings.
   Operations: `AddCodeContentToActiveProject`, `UpdateMetadata`, `GetMetadataXml`,
   `RunBestPracticeChecks`, plus a separate label sub-API
   (`CreateLabelResourceFile`/`UpdateLabelResourceFile`/`AddLabelToLabelResourceFile`/`UpdateFullLabelResourceFile`/`LabelSearchByText`/`LabelSearchById`).

   The rationale appears to be: one self-contained representation per object (declaration + methods +
   fields + indexes + relations + properties together), validatable against an XSD they ship, and
   already supported by the metadata provider's serializer.

3. **They ship per-type Copilot prompts and XSDs** as embedded resources inside
   `Microsoft.Dynamics.Framework.Tools.GitHubCopilot.17.0.dll`. We extracted them into `prompts/`.

4. **Item templates are inert.** All Templates/ProjectItems/.../*.zip files contain only a
   `.vstemplate` manifest pointing at `ProjectSystem.ItemCreationWizard`. The wizard reads a
   `vstemplate filename → DomainClassId Guid` map, calls
   `ConverterUtility.CreateDefaultRuntimeTypeInstance(domainClassId, name, type)` to instantiate the
   metadata object, then runs a per-type Initializer callback that, for code-bearing types
   (Class/Table/Test/Runnable), seeds source via `BuildHelper.ParseSourceCodeString(...)` with a
   string template embedded in the wizard.

5. **There's a real X++ language server**, under `LanguageServerDependencies/`. It's a Roslyn-style
   architecture: `Parsecs.CodeAnalysis` (compiler core), `.Workspaces` (document/project model),
   `.Xpp` (X++ syntax tree, walkers, rewriters), `.Features` (refactorings/code-fixes).
   This is the LSP backing IntelliSense, not the parser the metadata pipeline uses (that one is
   `Microsoft.Dynamics.AX.Framework.Xlnt.XppParser`). Two different parsers live in the box.

6. **Microsoft's "SemanticSearch" ≠ our semantic search.** Their `SemanticSearch` is structural
   AST/AOT-visitor search ("find all forms whose datasource is X") — NOT embedding-based. Naming
   collision worth avoiding in user-facing copy. Call ours "embedding search" or "similarity search."

7. **`typeWithExtensions` (the official set of extendable types)** includes anything in the AxTable
   assembly whose Name has a corresponding `*Extension` type in the same assembly, plus Enum, EdtBase,
   Query, AxQuery. The `extensionDslTypes` list (everything with `IRootExtensionElement`) is the
   authoritative source. Per-type extension authoring lives next to base authoring with the same
   shape (just a different root element + a `{Base}.{ExtName}` name convention).

## Key XSDs (in `prompts/`)

`AxClass.xsd`, `AxTable.xsd`, `AxTableExtension.xsd`, `AxForm.xsd`, `AxFormExtension.xsd`,
`AxEdt.xsd`, `AxEdtExtension.xsd`, `AxEnum.xsd`, `AxEnumExtension.xsd`, `AxLabelFile.xsd`.

These define every valid element + every valid property name and value for the corresponding AOT
type. AxClass.xsd is tiny (1.5KB — Name + SourceCode{Declaration, Methods{Method{Name, Source}}} +
IsObsolete + Tags). AxTable.xsd is 24KB. AxForm.xsd is 59KB. The XSDs are *the* authoritative
"what properties must I set" reference — better than recipes we wrote by trial.

## Key prompt files (in `prompts/`)

- **`LanguagePromptPrefix.txt`** + **`LanguagePromptSuffix.txt`** — base X++ language tutorial they
  feed Copilot. Concise (~900 bytes total). Good template for our skill intro.
- **`PredefinedClassesPrompt.txt`** — Tables (Common derivative), Forms (FormRun derivative).
- **`PredefinedFunctionsPrompt.txt`** — full X++ built-in function reference in a markdown table
  (9KB). Direct quote-fodder for skills.
- **`{Type}Prompt.txt`** — per-type instructions to the AI: overview, operations, creation, edge
  cases, examples. Their `TableExtensionPrompt.txt` even has explicit guidance like "Extensions
  cannot modify or remove existing fields" and the name-convention rule.
- **`LabelFilePrompt.txt`** — explains the four ID types (Name / LabelFileId / LabelId /
  LabelSearchId), the two-file layout, and the create order. We need this in our skill.

## Form patterns (in `prompts/`)

MS treats forms as instances of named UX patterns. Each gets its own Prompt + Examples file:
- DetailsMaster, DetailsTransaction
- ListPage, SimpleList, SimpleListDetails
- Task, TaskParentChild
- TableOfContents, Wizard, WorkspaceOperational

If we want to support form authoring beyond toy examples, our skill should teach the LLM these
patterns by name and when to use each. We have ~110KB of MS-authored examples to draw on.

## Operations MS exposes to its own AI (the MetadataService API)

This is the closest thing to a "v2 write tool surface" Microsoft itself designed:

| Operation | Purpose |
|-----------|---------|
| `GetMetadataXml(type, name)` | Read an object as XML |
| `GetMetadataXml(type, name, module, model)` | …with explicit model |
| `AddCodeContentToActiveProject(xml)` | Create a new object (fails if it exists) |
| `UpdateMetadata(xml)` | Update an existing object's XML |
| `GetAllProjectItems()` | List what's in the current project |
| `RemoveProjectItem(name)` | Remove an item from project (not delete the metadata) |
| `RunBestPracticeChecks(metadata, module, model)` | BPC validation |
| `CreateLabelResourceFile(id, content, module, model, lang)` | Create the .label.txt + XML |
| `UpdateLabelResourceFile(...)` | Edit a single label |
| `AddLabelToLabelResourceFile(...)` | Append one label |
| `UpdateFullLabelResourceFile(...)` | Replace label.txt contents |
| `LabelSearchByText(text, lang)` | Find labels by displayed text |
| `LabelSearchById(searchId, lang)` | Find a label by `@File:Id` form |

Notably absent: any per-property setter, any per-field setter, anything that resembles our v1
`execute_object_modification`. The shape is read-XML / write-XML / validate, plus a specialized
label sub-API. That's it.

## Critical API entry points (in `Microsoft.Dynamics.AX.Metadata.*` — already in our bridge)

- `IMetadataProvider[Type].Create(IMetadataNamedObject, ModelSaveInfo)` — write new
- `IMetadataProvider[Type].Update(IMetadataNamedObject, ModelSaveInfo)` — overwrite
- `IMetadataProvider[Type].Delete(string name)` — delete
- `IMetadataProvider[Type].Exists(string name)` — check
- `IMetadataProvider[Type].Read(string name)` — read

The `ModelSaveInfo(modelInfo)` ctor picks the destination model. The XML deserialization is via
`IDesignMetadataService.LoadMetadataRootElementFromExternalFile(path)` (VS extension wraps the
provider's built-in deserializer; we'd reimplement that bit on the bridge side since we don't have
`IDesignMetadataService`).

## Validation surface

`MetaModelUtility.ValidateRootElementName(name, type, out errors)` — same name-validation the
wizard uses on creation. Lives in `Microsoft.Dynamics.Framework.Tools.MetaModel.17.0.dll` (VS-side).
Worth either calling, or porting the rules to the bridge.

Best-practice rules ship in `Microsoft.Dynamics.AX.Framework.BestPracticeFramework.dll` and its
companions (`CodeStyleRules`, `DataAccessRules`, `DataEntityRules`, `MaintainabilityRules`,
`DeprecatedElementsRules`, etc.). Heavy machinery; defer.
