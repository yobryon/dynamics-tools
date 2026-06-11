# Domain-layer coverage ledger

What the typed-authoring layer **deliberately omits** from each
AOT type's XSD, and why. This is the source of truth that
disambiguates three different "missing property" situations:

1. **Intentional omission** — we chose not to surface this in the
   domain shape (long-tail, escape-hatch territory, deprecated).
2. **Not yet implemented** — on the roadmap, just hasn't shipped.
3. **MS model drift** — MS removed / renamed / repurposed it
   between releases; our XSD or canonical-order snapshot is stale.

If a property "missing" from a domain shape is in category 1 it's
fine; category 2 is backlog work; category 3 is a bug. This doc
keeps the categories straight.

For the typed-authoring architecture itself, see
`memory/design_domain_authoring_layer.md`. For the migration
recipe, see `memory/process_domain_type_migration.md`.

---

## AxResource

Status: shipped 2026-05-22. Full coverage of the on-disk surface.

### In scope

- Name, FileName, RelativeUriInModelStore, TypeOfResource
  (typed enum: XmlDoc / Data / Html / Styles / Scripts / Text /
  PowerBIReport / PCFControl).

### Architectural notes

- No-namespace root (like AxClass / AxService / AxSecurity*).
- Trivial flat shape — four fields, no nesting, no polymorphism.
- **Content file must exist before manifest write.** The bridge
  writes the AxResource XML; it does not copy file content.
  The file at `RelativeUriInModelStore` must already be on disk
  or the runtime resource won't resolve. The `dynamics-xpp:xpp-resource`
  skill calls this out.
- Smoke-tested against ContosoRetail samples (`CONRetailCDXSeedDataAX7`
  XmlDoc, `CONCurrentInventoryPBIX` PowerBIReport) + write/read/patch
  in CH_ECOM.

### Intentional omissions

None — surveyed all distinct on-disk property names across PLD;
every one is modeled.

---

## AxService + AxServiceGroup

Status: shipped 2026-05-22. Full coverage of the on-disk surface.

### In scope

- **AxService**: Name, Class (backing X++ class), Description,
  ExternalName, Namespace, OperationalDomain, IsObsolete,
  SubscriberAccessLevel (service-wide, optional),
  ServiceOperations (Name + Method + EnableIdempotence +
  optional per-operation SubscriberAccessLevel).
- **AxServiceGroup**: Name, AutoDeploy, Description, IsObsolete,
  Services (list of {Name + Service ref}).

### Architectural notes

- Both roots use no-namespace (like AxClass and the AxSecurity*
  family). xmlns:i declared at root; children unprefixed.
- **Cross-namespace reuse**: `AxServiceOperation.SubscriberAccessLevel`
  imports `SecurityGrant` from `Xpp.Service.Domain.Security`. The
  per-CRUD grant shape is identical to what AxSecurity* uses; the
  only difference is the wrapping element name
  (`SubscriberAccessLevel` here vs `Grant` on Security).
- MS strips `EnableIdempotence=No` on read (it's the default).
  Same default-strip pattern as JoinMode=InnerJoin / MenuItemType=
  Display / AllowUserCacheRefresh=Yes.
- Smoke-tested against MS samples (AxManagementPackService 4-op
  service, AxClient 11-service group) and write/read/patch in
  CH_ECOM including a per-operation SubscriberAccessLevel.

### Intentional omissions

None — surveyed all distinct on-disk property names for both
types; every one is modeled.

---

## AxTile

Status: shipped 2026-05-22. Full coverage of the on-disk surface.

### In scope

- Name, Label, HelpText, ConfigurationKey, IsObsolete.
- `Type` enum: Link / Count / KPI.
- `Size` enum: Small / Medium / Wide / Large / ShortWide.
- `TileDisplay` enum: TextOnly / TextAndImage / BackgroundImage.
- `FormViewOption` enum: Grid / Details.
- `OpenMode` enum: View / New.
- `MenuItemName` + `MenuItemType` (reused MenuItemKind enum from
  the Menus namespace).
- `RefreshFrequency` enum: AsFastAsPermissible / OneMinute /
  FiveMinutes / FifteenMinutes / OneHour / FourHours /
  TwentyFourHours.
- Parameters, CopyCallerQuery, ApplyFilter, AllowUserCacheRefresh.
- Query, KPI (for Count / KPI tiles).
- NormalImage, ImageLocation, URL.

### Architectural notes

- V1 default namespace at root (same family as AxMenu / AxMenuItem).
- Flat shape — no children, no polymorphism. AxProp order is
  straight alphabetical after Name.
- Validated against MS-shipped samples: BankAccountTableTile (4
  lines, minimal), DirPartyTable (visual-rich Link), and
  EcoResReleasedProductVariantsMissingActiveRouteVersion (Count
  with ConfigurationKey). Write + read + patch round-trips clean
  in CH_ECOM.

### Intentional omissions

None — surveyed all 22 distinct on-disk property names across
ApplicationSuite tiles; every one is modeled.

---

## AxSecurity* (Privilege / Duty / Role / Policy)

Status: shipped 2026-05-22. Whole RBAC + XDS surface in one batch.

### In scope

- **AxSecurityPrivilege**: Name, Label, Description,
  DataEntityPermissions (per-entity Grant + Fields + Methods),
  DirectAccessPermissions, EntryPoints (Name + Grant + ObjectName
  + ObjectType + Forms), FormControlOverrides (per-form
  per-control grants).
- **AxSecurityDuty**: Name, Label, Description, Privileges (refs).
- **AxSecurityRole**: Name, Label, Description,
  DirectAccessPermissions, Duties (refs), Privileges (refs),
  SubRoles (refs).
- **AxSecurityPolicy**: Name, Label, Description,
  ConstrainedTable, Enabled, PrimaryTable, Query, ConstrainedTables
  (polymorphic — Table entries name a relation back to the
  primary; Expression entries group further restrictions;
  recursive).
- **`SecurityGrant`** shared record: per-CRUD access levels
  (Read / Update / Create / Delete / Correct / Invoke), each
  optional, each one of `NoAccess` / `Allow` / `Grant` / `Deny`.

### Architectural notes

- No-namespace root (unlike menus' V1). `xmlns:i="..."` declared at
  root; child elements unprefixed.
- `EntryPoint.ObjectType` typed via enum
  (MenuItemDisplay / Action / Output / Form / Tile) with `Other`
  + `RawObjectType` escape hatch.
- `ConstrainedTables` polymorphic via `i:type=
  AxSecurityPolicyConstrainedTable | AxSecurityPolicyConstrainedExpression`
  with `xmlns=""` reset on each child.
- Validated: read of MS-shipped `AccountantInformationMaintain_BR`
  (privilege), `AccountingDistCustFreeInvoiceMaintain` (duty),
  `AnonymousApplicant` (role), `DirRestrictViewPartyInAddressBook`
  (policy — 40+ tables with deeply nested expressions). Write +
  read + patch round-trips for all four in CH_ECOM.

### Intentional omissions

| Element | Reason |
|---|---|
| Field-level and method-level grants on DataEntityPermission | The records exist but MS-shipped samples never populate them. Surface stays typed for completeness; in practice you can leave them empty. |
| `AxSecurity*Extension` types (extending MS-shipped roles/duties) | Tier-2 backlog. Use raw `xpp_create_object` for those until they're typed. |
| Strip-on-read defaults (e.g. `Grant` with all CRUDs=Allow on DataEntityPermission collapses to Read+Update only) | MS's serializer canonicalizes to the minimal on-disk form. The typed surface returns whatever's actually on disk. |

### Not yet covered

- `AxSecurity*Extension` family — extending MS-shipped duties /
  roles. Deferred.

---

## AxEnum

Status: shipped `ff1a91d` (2026-05-21). Full XSD coverage.

Nothing intentionally omitted at this time. If something appears
missing, treat as bug or MS drift.

---

## AxEdt

Status: shipped `6df99c0`. Pragmatic coverage of all 10 BaseType
subtypes (String, Int, Int64, Real, Date, Time, UtcDateTime, Enum,
Container, Guid).

### Intentional omissions

- **`AxEdtBoolean` BaseType.** F&O convention is the `NoYes` enum
  EDT for all persisted booleans (checkbox UI, integer storage,
  room to extend later). Existing `AxEdtBoolean` instances are
  rare and reachable via the raw `xpp_get_object_xml` /
  `xpp_update_object` escape hatch.

### Not yet covered (backlog)

- None known.

---

## Extensions — Tier 1 + AxMenuExtension

Status: Tier 1a (Table / Edt / Enum) shipped 2026-05-22 (`cde34bf`). Tier 1b
(Form / View / DataEntity) shipped 2026-05-22 (`c0c4131`).
AxMenuExtension shipped 2026-05-22 (this commit).

### In scope

- **AxTableExtension**: Name, IsObsolete, Tags, Visibility, FormRef,
  Fields (reusing TableField polymorphic shape), FieldGroups
  (reusing TableFieldGroup), FieldGroupExtensions (add fields to
  an existing field group), FieldModifications, Indexes, Relations
  (reusing TableRelation), RelationExtensions (add constraints to
  existing relation), RelationModifications, PropertyModifications.
- **AxEdtExtension**: Name, IsObsolete, Tags, Visibility,
  ArrayElements (reusing EdtArrayElement), PropertyModifications.
- **AxEnumExtension**: Name, IsObsolete, Tags, Visibility,
  EnumValues (reusing EnumValueRequest), PropertyModifications,
  ValueModifications. EnumValues lives at DM Order=2 (between
  Name and the Order=3 modification blocks).
- **AxFormExtension** (V6 namespace at root): Name, IsObsolete,
  Tags, ConfigurationKey, Visibility, Controls (wrapped in
  `FormExtensionControl` entries pairing a polymorphic FormControl
  with its Parent control on the base form),
  ControlModifications, DataSources (reusing FormDataSource),
  DataSourceModifications, DataSourceReferences (so new controls
  can bind to existing data sources), Parts (reusing FormPart),
  PropertyModifications.
- **AxViewExtension**: Name, IsObsolete, Tags, Visibility,
  Fields (reusing ViewField with Bound + Computed* kinds),
  FieldGroups, FieldGroupExtensions, FieldModifications,
  DataSources (reusing QueryDataSource), Ranges (reusing
  QueryRange), PropertyModifications.
- **AxDataEntityViewExtension**: Name, IsObsolete, Tags,
  Visibility, Fields (reusing EntityField with Mapped + Unmapped*
  kinds), FieldGroups, FieldGroupExtensions, FieldModifications,
  DataSources (reusing QueryDataSource), Relations (reusing
  EntityRelation polymorphic constraints, including the
  ForeignKey subtype), PropertyModifications.
- **AxMenuExtension** (V1 namespace at root): Name, IsObsolete,
  Tags, ConfigurationKey, Visibility, Elements (typed
  `MenuExtensionElement` wrapper pairing Parent +
  PositionType + PreviousSibling + a polymorphic MenuElement
  reused from AxMenu's domain shape), MenuElementModifications,
  PropertyModifications. Smoke-tested against
  InventoryManagement.AdvancedQualityManagement (33-element MS
  sample mixing MenuItem / SubMenu / Tile children).

### Intentional architecture choices

- **Reuse, don't reinvent.** The extension domain shapes import
  the base type's records (TableField, EnumValueRequest,
  EdtArrayElement). New typed records are limited to the
  delta-only concepts: PropertyModification, FieldModification,
  RelationModification, ValueModification, FieldGroupExtension,
  RelationExtension.
- **Internal facade methods on base mappers.** Each base mapper
  (AxTableMapper, AxEdtMapper, AxEnumMapper) exposes a small set
  of `internal static` `*ForExtension` wrappers around its
  private Build/Parse helpers, so the extension mapper reuses
  the polymorphic field / relation / constraint emission logic
  without duplicating it.

### Tier 1b architecture notes

- **AxFormExtension** carries the V6 default namespace at the
  root; polymorphic children (FormControl, FormDataSource,
  FormPart) reset to `xmlns=""` for the `xsi:type` discriminator
  pattern, matching AxForm itself.
- **AxFormExtensionControl wrapper.** New form-extension controls
  use a wrapper element `<AxFormExtensionControl>` with
  `<Name>` + `<FormControl xsi:type="...">` + `<Parent>` (the
  base-form control to insert under) — different from AxForm's
  direct `<AxFormControl>` children. The mapper renames the
  FormControl element on emit and back on parse.
- **Reused helpers.** `AxFormMapper`, `AxViewMapper`,
  `AxDataEntityViewMapper`, `AxQueryMapper`, and `AxMenuMapper`
  expose `internal static *ForExtension` wrappers around their
  polymorphic build/parse helpers (controls, data sources,
  parts, view fields, entity fields/relations, query data
  sources/ranges, menu elements) so the extension mappers reuse
  them without duplication.
- **AxMenuExtension rename trick.** Same pattern as the
  FormExtensionControl wrapper: the polymorphic child element
  uses the name `MenuElement` (not `AxMenuElement`); the mapper
  builds with `AxMenuMapper.BuildElementForExtension` and
  renames the root element on emit, undoing it on parse. The
  nested `Elements` inside a SubMenu keep the regular
  `<AxMenuElement>` shape since they're parsed by the same
  helper recursively.

### Out of scope for this work

- `AxMenuItem*Extension` — covered by the existing AxMenuItem typed
  surface for new items; extensions to MS-shipped menu items are
  niche.
- `AxSecurityDutyExtension`, `AxSecurityRoleExtension` — security
  authoring is a separate concern.
- `AxWorkflowApprovalExtension` / `AxWorkflowTaskExtension` /
  `AxWorkflowTemplateExtension` — workflow extensions.
- `AxMapExtension` — map extensions (rare).
- `AxQuerySimpleExtension` — query extensions.

These can be promoted from the escape hatch in future passes if
real authoring needs surface.

---

## AxForm

Status: shipped 2026-05-22. Pragmatic + typed common controls.
Largest typed surface in the plugin. Read path validated against
CONSHShipmentTable (370-control real-world form, 4 data sources,
37 form-level methods + 41 control event handlers, 22 typed
control kinds present, ~5% of controls fell back to `kind: Other`).

### In scope

- Form metadata: Name, FormTemplate, IsObsolete, Tags,
  DataSourceQuery, DataSourceChangeGroupMode, AllowPreLoading,
  AutoCacheUpdate, InteractionClass, Visibility.
- Data sources with 4 kinds (Root, Concrete, Derived, Referenced),
  Fields with per-field overrides (AllowEdit/Visible/Skip/Mandatory),
  DataSourceLinks (parent-child join), ReferencedDataSources +
  DerivedDataSources (recursive).
- Design block: Caption, Pattern, PatternVersion, Style,
  HeaderPattern, ViewEditMode, TitleDataSource, plus the
  recursive Controls tree.
- Controls — 22 typed kinds via `FormControlKind` enum:
  Group, Tab, TabPage, Grid, Container, ActionPane,
  ActionPaneTab, ButtonGroup, String, Integer, Int64, Real,
  Date, DateTime, ComboBox, CheckBox, ReferenceGroup, Button,
  MenuFunctionButton, CommandButton, StaticText, SegmentedEntry,
  + `Other` for the long tail.
- Common control properties (Name, Visible, Enabled, AllowEdit,
  Skip, AutoDeclaration, Pattern, PatternVersion, HelpText,
  WidthMode, HeightMode, ConfigurationKey, Tags) plus type-gated
  data-binding properties (DataField, DataSource, Label,
  Mandatory, Caption, Style, ViewEditMode, Text, Command,
  MenuItemName, MenuItemType).
- FormControlExtension (typed Name + ExtensionProperties +
  ExtensionComponents — supports QuickFilter, SegmentedEntry,
  etc.).
- Parts (factbox references with DataSource / DataSourceRelation /
  PartLocation / Caption).
- SourceCode: Declaration + Methods (form-level) + DataSources
  (per-data-source event handlers including Field-level methods) +
  DataControls (per-control event handlers) + Members (form-level
  field declarations). Method bodies are opaque X++ preserved
  verbatim.

### Round-trip safety mechanism

Every element (control, data source, design, part, extension,
extension property/component) carries an `OtherProperties`
`Dictionary<string, string>` field. The parser routes any
on-disk element-name → value pair we don't explicitly model into
this dict; the builder merges typed properties + OtherProperties
on emit. Lossless round-trip even for properties beyond the
typed surface (e.g. Font / FontSize / Bold / Color* / Margin* /
LabelFont* etc.).

### Per-level canonical order for data sources

AxFormDataSource → Concrete → Root three-tier inheritance. The
mapper hard-codes the canonical AxProp order at each level:
- Base AxProp (alpha): Table, Tags
- Base Order=3 (alpha): Fields, ReferencedDataSources
- Concrete AxProp (alpha): AutoNotify, AutoQuery, AutoSearch,
  CrossCompanyAutoQuery, DelayActive, JoinSource, LinkType,
  MaxRecordsToLoad, OnlyFetchActive
- Root AxProp (alpha): AllowCheck, AllowCreate, AllowDelete,
  AllowEdit, CounterField, Index, InsertAtEnd, InsertIfEmpty,
  MaxAccessRight, OptionalRecordMode, StartPosition,
  ValidTimeStateAutoQuery, ValidTimeStateUpdate
- Root Order=3 (alpha): DataSourceLinks, DerivedDataSources

Out-of-level emit causes MS to silently drop the offender (hit
this in initial smoke: AllowEdit appearing before Table in alpha
sort dropped Table entirely).

### Intentional omissions

| Element | Reason |
|---|---|
| Most layout/styling properties on each control (Font, FontSize, Bold, Italic, BackgroundColor, ForegroundColor, ColorScheme, Margin*, Label* family, etc.) | Modern F&O patterns + the style system handle these. Preserved through `OtherProperties` so authored values round-trip, but not surfaced as typed fields. |
| Less-common control types (ActiveX, Animate, HTML, Image, ListBox, ListView, ManagedHost, Progress, RadioButton, Table, Tree, ButtonSeparator, DropDialogButton, Guid, ComboBoxOption, etc.) | Fall through `kind: Other` with `rawType` preserving original xsi:type. Author via `OtherProperties` dict or escape-hatch via `xpp_update_object`. |
| AxFormPart subtype variants beyond Reference | The `kind` field on FormPart preserves any subtype string, but only Reference is conventionally typed. Other subtypes round-trip via the unknown-property path. |
| Form-level Attributes / CompilerMetadata / Members / TypeParameters | Not part of the canonical authoring surface (Attributes is metamodel-only; Members declarations are X++ source preserved as `FormMember.Declaration`). |
| `AxFormExtension` (separate AOT type) | Shipped on the typed layer 2026-05-22 — see the Extensions Tier 1 section above. |

### Not yet covered

- AxFormExtension (the parallel extension type).
- The remaining unimplemented control types as typed kinds —
  promote from `Other` to typed when a real authoring need
  surfaces.

---

## AxMenu / AxMenuItem*

Status: shipped 2026-05-22. Full coverage of the on-disk surface.

### In scope

- **AxMenu**: Name, Label, ConfigurationKey, CountryRegionCodes,
  FeatureClass, IsObsolete, Tags, Visibility, image options
  (NormalImage / DisabledImage / ImageLocation /
  DisabledImageLocation), optional MenuItem target (when the menu
  itself is clickable), Parameters, SetCompany, ShortCut.
- **Polymorphic Elements (`kind` enum)**:
  - `MenuItem` — references AxMenuItem* (MenuItemName +
    MenuItemType, DisplayInContentArea, Parameters, ShortCut,
    ShowParentModule).
  - `MenuReference` — references another AxMenu by name.
  - `Separator` — no extra fields.
  - `SubMenu` — recursive nested menu with full scalar set
    (Label, ConfigurationKey, CountryRegionCodes, FeatureClass,
    image, MenuItemName/Type, Parameters, SetCompany, ShortCut,
    ShowParentModule, recursive Elements).
  - `Tile` — references an AxTile.
- **AxMenuItem* (Display / Output / Action)**: single typed
  surface with `kind` discriminator dispatching to the right
  on-disk ax_type. ~37 shared scalars (Object, ObjectType,
  Label, HelpText, Parameters, EnumTypeParameter/EnumParameter,
  Query, ReportDesign, NeedsRecord, MultiSelect, OpenMode,
  FormViewOption, CopyCallerQuery, AllowRootNavigation,
  ConfigurationKey, CountryConfigurationKey, CountryRegionCodes,
  OperationalDomain, IsObsolete, FeatureClass, Tags,
  MaintainUserLicense, ViewUserLicense, LinkedPermission* trio,
  ExtendedDataSecurity, full CRUD permission set,
  SubscriberAccessLevel, image options).
- **Action-specific scalars**: StateMachine, StateMachineDataSource,
  StateMachineTransitionTo.

### Intentional omissions

None of practical concern.

### Architectural note

This is the **first AOT type** on the typed-authoring layer that
uses a non-empty default namespace at the root
(`Microsoft.Dynamics.AX.Metadata.V1`). The mapper handles
namespace setup; polymorphic children reset to `xmlns=""` for the
xsi:type discriminator pattern. SubscriberAccessLevel's children
individually reset to `xmlns=""` while the container stays in the
V1 namespace — that's an MS-shipped on-disk convention preserved
by the mapper.

---

## AxDataEntityView

Status: shipped 2026-05-22. Pragmatic 80%.

### In scope

- Entity metadata: OData (PublicEntityName, PublicCollectionName,
  PrimaryKey, PrimaryCompanyContext, IsPublic, IsReadOnly), DMF
  (DataManagementEnabled, DataManagementStagingTable, EntityCategory,
  SupportsSetBasedSqlOperations, EnableSetBasedSqlOperations),
  Dataverse (AutoCreateDataverse, EnableDataverseSearch),
  archival/retention (AllowArchival, AllowRetention,
  AllowRowVersionChangeTracking, ValidTimeStateEnabled),
  cross-company (AosAuthorization, MessagingRole), Modules,
  Version handling, plus all AxDataEntity scalars inherited from
  the base.
- Polymorphic Fields: `Mapped` (DataField + DataSource + optional
  Aggregation / DimensionLegalEntityContextField /
  DynamicDimensionEnumerationField / EnableDataverseSearch) and
  `Unmapped<Type>` for each primitive (UnmappedString /
  UnmappedInt / UnmappedInt64 / UnmappedReal / UnmappedDate /
  UnmappedEnum / UnmappedUtcDateTime / UnmappedTime /
  UnmappedGuid / UnmappedContainer). UnmappedString
  additionally exposes StringSize / Adjustment.
- Common field scalars (AccessModifier, AllowEdit/OnCreate,
  ConfigurationKey, CountryRegionCodes/CtxField, FeatureClass,
  GroupPrompt, HelpText, IsObsolete, Label, Mandatory,
  RelationContext, Tags) from `AxDataEntityViewBaseField`.
- Keys (`AxDataEntityViewKey` + `AxDataEntityViewKeyField`).
- Ranges (`AxDataEntityViewRange`).
- Relations with polymorphic constraints (Field / Fixed /
  RelatedFixed), AxDataEntityViewRelationForeignKey via
  IsForeignKey flag.
- Field groups — REUSED from `Tables.TableFieldGroup` (on-disk
  element name is `<AxTableFieldGroup>`, shared across tables,
  views, and entities).
- DeleteActions.
- ViewMetadata block — supports Methods + DataSources. The
  DataSources tree REUSED from `Queries.QueryDataSource` (the
  on-disk elements are `<AxQuerySimpleRootDataSource>` /
  `<AxQuerySimpleEmbeddedDataSource>`).
- SubscriberAccessLevel.
- SourceCode (Declaration + Methods, opaque X++).

### Intentional omissions

| Element | Reason |
|---|---|
| `AxDataEntityViewReference` family (References, Root/Embedded subtypes, EmbeddedDataEntities recursion) | Nested entity composition for parent-child / hierarchical entities. Separate authoring concept; most public entities don't use it. Defer to `xpp_update_object` escape hatch. |
| `Mappings` collection (always emits empty) | Entity mappings are a separate concept (composite entity routing). Out of scope for the simple entity surface. |
| `StateMachines` (always emits empty) | F&O state machines are a separate authoring concept; rare on data entities. |
| `Attributes`, `Conflicts`, `CompilerMetadata`, `TypeParameters` | No AxPropertyAttribute or DataMember — not part of the on-disk surface. |

### Not yet covered

- References / EmbeddedDataEntities (above). If a real authoring
  need for composite entities surfaces, this is the obvious
  next addition.

### Related types not yet on the typed layer

- `AxDataEntityViewExtension` — DE view extensions (parallel to
  AxTableExtension / AxEdtExtension).
- `AxCompositeDataEntityView` — composite-entity wrapper for
  parent-child orchestration.
- `AxAggregateDataEntity` — DMF aggregate-data-entity family.

---

## AxView

Status: shipped 2026-05-22. Pragmatic 80%; the backing query
shape lives in AxQuery (referenced by name), so AxView's
authoring surface is intentionally smaller than AxTable.

### In scope

- View metadata: Label, SingularLabel, DeveloperDocumentation,
  Query (reference), TableGroup, IsPublic, IsStaged, Updatable,
  ValidTimeStateEnabled, CollectionName, ReplacementKey,
  AosAuthorization, MessagingRole, Version, TitleField1/2,
  ConfigurationKey, CountryRegionCodes, Modules(?), Tags,
  IsObsolete, FormRef, ListPageRef, PreviewPartRef, ReportRef,
  OperationalDomain, EntityRelationshipType.
- SubscriberAccessLevel (Read/Create/Update/Delete/Correct/Invoke).
- View fields, polymorphic on Kind:
  - `Bound` — projects DataField from DataSource on the backing
    query. Carries optional Aggregation.
  - `ComputedString` / `ComputedInt` / `ComputedInt64` /
    `ComputedReal` / `ComputedDate` / `ComputedEnum` /
    `ComputedUtcDateTime` — X++ method synthesizes the value
    (Method, ViewMethod, ExtendedDataType, IsVirtual; plus
    StringSize / Adjustment on `ComputedString`).
- View indexes (Name + Fields + AllowDuplicates / AlternateKey /
  Enabled / ConfigurationKey).
- View relations with constraints (Field / Fixed / RelatedFixed;
  Cardinality / RelationshipType / Role etc.).
- Field groups — reuses the AxTableFieldGroup shape from
  AxTable since they share the on-disk element name.
- ViewMetadata designer-helper block (emitted as an empty shell
  when omitted, as MS does).
- SourceCode methods (opaque X++; rare on views).

### Intentional omissions

| Element | Reason |
|---|---|
| `ViewMetadata.DataSources` | Designer-cached duplicate of the backing query's data sources. Authoring-irrelevant; emitted as an empty `<DataSources />` shell on write. If you need the designer-side data-source cache populated, use the escape hatch. |
| `Mappings`, `StateMachines` (collections always emit empty) | View mappings and state machines are unusual; deferred until a real need surfaces. |
| `AggregateView` (Y/N flag on legacy aggregate-view family) | Separate metamodel family (`AxAggregateView`, `AxAggregateViewDataSource`) — not the same as a regular `AxView`. Out of scope. |
| `AxDataEntityView`, `AxCompositeDataEntityView` | Different AOT types entirely (the data-entity layer above views). Will be addressed as their own typed surfaces. |
| `Attributes`, `CompilerMetadata`, `Conflicts`, `TypeParameters` | No AxPropertyAttribute or DataMember — not part of the on-disk surface. |

### Not yet covered

None of practical concern beyond the omissions above.

---

## AxQuery

Status: shipped 2026-05-22. Pragmatic 80%, scoped to
`AxQuerySimple`.

### In scope

- AxQuerySimple root (the modern join/filter query type).
- Recursive data sources: `Root` (with OrderBy / GroupBy /
  Having), `Embedded` (with JoinMode / Relations / UseRelations /
  FetchMode), `Derived`. Depth unbounded.
- Ranges (with Value, Status, Enabled, Label, DerivedTable).
- Relations (with Field / RelatedField / JoinDataSource /
  JoinRelationName / JoinDerivedTable / DerivedTable).
- OrderBy / GroupBy / Having predicates (Having includes Type =
  Sum / Avg / Min / Max / Count).
- Fields projection.
- Query-level scalars: Title, Description, QueryType,
  AllowCrossCompany, AllowCheck, Importable, Interactive,
  Searchable, UserUpdate, Form, Literals, IsObsolete, Tags.
- SourceCode methods (opaque X++; defaults to a minimal
  classDeclaration with the `[Query]` attribute if omitted).

### Intentional omissions

| Element | Reason |
|---|---|
| `AxQueryComposite` | Union/aggregate queries over other queries — rare and conceptually distinct from the join query shape. Escape-hatch via `xpp_update_object`. |
| `AxQuerySimpleDataSourceFieldAvg` / `Count` / `Max` / `Min` / `Sum` (aggregate field projections at the data-source level, distinct from Having predicates) | The Having block already exposes aggregate functions where they're typically authored. Aggregate-field projections are niche; defer if needed. |
| `AxQuerySimpleExtension` | Query extensions are a separate authoring concept covered by AxQueryExtension (parallel to AxTableExtension). Not in this AxQuery surface. |
| `Attributes`, `CompilerMetadata`, `Conflicts`, `TypeParameters` | Reflection on AxQuerySimple shows these but they don't have AxPropertyAttribute or DataMember — not part of the on-disk surface. |

### Not yet covered

- None of practical concern beyond the omissions above.

---

## AxClass

Status: shipped 2026-05-21. Pragmatic 80%, but the on-disk
surface itself is tiny so coverage is effectively complete.

### In scope

- `Name`, `SourceCode { Declaration, Methods[] }`.
- Top-level `IsObsolete`, `Tags`.
- All `AdvancedClassOptions` flags (IsAbstract, IsFinal,
  IsInterface, IsInternal, IsPrivate, IsPublic, IsStatic,
  Extends, RunOn) — present but rarely used in practice; X++
  keywords in Declaration drive these semantics.
- Method bodies as opaque X++ text preserved verbatim through
  round-trip.

### Intentional omissions

- **X++ parsing.** Methods are opaque text on both read and write
  paths. We don't parse parameter lists, return types, modifier
  keywords, or attributes from the X++ source. The `xpp_get_object_methods`
  + `xpp_get_method_source` tools (older, separate API) parse the
  X++ for the read side when method-level structure is needed —
  no equivalent on the write side.
- **`Attributes`, `Conflicts`, `CompilerMetadata`, `Implements`,
  `Members`, `TypeParameters`.** Reflection on AxClass shows these
  properties exist on the metamodel but none have
  `AxPropertyAttribute` or `DataMember` — they're not serialized
  at the XML level. `Implements` lives inside the Declaration X++
  source.

### Not yet covered

- None known. If a real authoring need surfaces for a property
  in this list, escape-hatch via raw `xpp_get_object_xml` +
  `xpp_update_object`.

---

## AxTable

Status: shipped 2026-05-21. Scope: **pragmatic 80%**.

### In scope

- All 10 polymorphic field subtypes (`AxTableFieldString`,
  `AxTableFieldInt`, `AxTableFieldInt64`, `AxTableFieldReal`,
  `AxTableFieldDate`, `AxTableFieldTime`, `AxTableFieldUtcDateTime`,
  `AxTableFieldEnum`, `AxTableFieldGuid`, `AxTableFieldContainer`)
  with the common field properties (Name, ExtendedDataType,
  Label, HelpText, Mandatory, AllowEdit, AllowEditOnCreate,
  Visible, AssetClassification, ConfigurationKey, etc.).
- Indexes (incl. IndexFields, AllowDuplicates, AlternateKey,
  IndexType, Enabled).
- Relations (incl. AxTableRelationForeignKey subtype) with
  constraint subtypes (Field, Fixed, RelatedFixed) — the two
  table-level constraint subtypes (Table, RelatedTable) are
  legacy and shipped to escape hatch.
- Field groups + their fields.
- Delete actions.
- Methods (opaque X++ source bodies, preserved verbatim).
- ~25 common table-level scalar properties: Label, Extends,
  TableGroup, TableType, TableContents, CacheLookup, PrimaryIndex,
  ClusteredIndex, SaveDataPerCompany, SaveDataPerPartition,
  TitleField1/2, ConfigurationKey, CountryRegionCodes, Modules,
  IsObsolete, Tags, DeveloperDocumentation, OperationalDomain,
  SingularLabel, ReplacementKey, FormRef, ListPageRef, PreviewPartRef,
  CreateRecIdIndex, Visibility.
- `SubscriberAccessLevel` (read-only / RBAC sub-block).

### Intentional omissions (escape-hatch territory)

| Element | Reason |
|---|---|
| `StateMachines` | F&O state machines are a separate authoring surface with their own tooling; rare to author through bulk-create flows; complex nested shape (states → transitions). Escape hatch via `xpp_update_object`. |
| `Mappings` | View / map / inheritance composition. Mostly authored on view-like derived tables, not pure tables. |
| `FullTextIndexes` | Rare; ships on a handful of system tables. The XSD's `<xs:any>` for index fields confirms even MS treats this as freeform. |
| `CompilerMetadata` | Auto-generated by the compiler; never user-authored. |
| `AxTableRelationConstraintTable` / `AxTableRelationConstraintRelatedTable` | Legacy table-level constraints (AX 2012 era). Modern F&O uses field-level Field/Fixed/RelatedFixed constraints. |
| `AllowArchival`, `AllowChangeTracking`, `AllowOverride`, `AllowRetention`, `AllowRowVersionChangeTracking` | Specialized; defaults are right for 99% of cases. Surface via `advanced` block if frequently needed. |
| `Created*` / `Modified*` audit columns at the table level | Configured per-column elsewhere; not direct authoring inputs. |
| `Durability` / `OccEnabled` / `StorageMode` / `DataSharingType` / `InstanceRelationType` | Infrequent advanced concerns; defaults are correct. |
| `EntityRelationshipType` / `ReportRef` / `SearchLinkRefName` / `SearchLinkRefType` | Cross-reference metadata; rarely authored manually. |
| `Field-level` `Modules` / `CorrectionFlagField` / `CurrencyCode*` / `CurrencyDate*` | Currency-aware-field machinery; tied to specific framework usage. |
| `AliasFor` / `FieldUpdate` / `FeatureClass` (field-level) | Specialized framework hooks. |
| `IgnoreEDTRelation` (field-level) | The modern guidance is to set this, but it's a footgun if accidentally inverted; covered via `advanced` block instead of top-level. |
| `IsManuallyUpdated` / `IsSystemGenerated` on any sub-element | Authored by tooling, not humans. |

### Not yet covered (backlog)

None currently scheduled — all known gaps are in the
"intentional" column. If a real authoring need comes up for an
escape-hatched property, promote it from this table into the
shape.

---

## Adding a new entry

When the next type ships:

1. Section heading with status + commit SHA.
2. **In scope** — what got modeled.
3. **Intentional omissions** — table of `Element | Reason`.
4. **Not yet covered** — backlog items, with a memory link if
   tracked.

The point is that a year from now, when someone notices a
property missing from the domain shape and asks "why isn't this
there?", the answer is in one of three places: here (intentional),
the backlog (planned), or it's a real bug.
