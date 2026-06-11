---
name: xpp-security
description: TRIGGER when authoring AxSecurityPrivilege / AxSecurityRole / AxSecurityDuty / AxSecurityPolicy (or their extensions). The role-based security model wires menu items, data entities, tables, and form controls to user-assignable roles. Required to make any new feature actually usable by non-admin users — without security wiring, the menu item exists but nobody can click it.
---

# Security — Privileges, Roles, Duties, Policies

F&O's role-based access control (RBAC) is a four-level hierarchy
the agent will touch every time it ships a feature:

```
Role ─┬── Duties ──── Privileges ──── EntryPoints
      └── Privileges ──── EntryPoints   (table / menu item / data entity / form control)
```

Two orthogonal axes:
- **RBAC** — who can access what (this skill, mostly)
- **XDS** (Extensible Data Security) — row-level filters via security policies (the AxSecurityPolicy section)

This skill covers all four AOT types because they form one
coherent design: a privilege defines a single permission grant,
duties bundle privileges into business functions, roles assign
duties to users. Policies layer row-level filters on top.

---

## Read this skill when

- You wrote a menu item / form / class / data entity and need
  users to be able to use it (the normal "I made a thing, wire
  the permissions" flow).
- The compile output flags `BPErrorPrivilegeNotCoveredByDuty`
  or `BPErrorMenuItemNotCoveredByPrivilege` — these are BP
  rules complaining about exactly this gap.
- You're extending MS-shipped duties / roles to grant access to
  your custom objects (use `AxSecurityDutyExtension` /
  `AxSecurityRoleExtension`).
- The user asks for row-level security (filter customers / sites
  / companies that a user can see) — that's an `AxSecurityPolicy`.

---

## The conceptual model

### Privilege — the atomic permission

A privilege says "this set of menu items / data entities / tables
can be accessed at THESE permission levels." It's the smallest
useful unit. By convention, one privilege ≈ one user-facing
capability (e.g., "view customers", "post invoices").

**Naming convention**: `<Object>View` for read-only, `<Object>Maintain`
for full CRUD. So you'd create `CONCustomerView` and `CONCustomerMaintain`
as a pair for any customer-related feature you add.

### Duty — a business function

A duty groups multiple privileges that together cover a job
function. E.g., `CONShipmentClerkProcess` might bundle
`CONShipmentView`, `CONShipmentMaintain`, `CONContainerView`,
`CONContainerMaintain`. Duties exist so roles can be composed
from reusable function blocks instead of pasting privilege
lists everywhere.

### Role — assigned to users

A role is what user administrators actually assign in F&O's
**System administration > Users** workspace. A role contains
duties + (optionally) privileges directly. Roles can have
sub-roles (`SubRoles` element) to inherit from a parent role.

### Policy (XDS) — row-level filtering

`AxSecurityPolicy` is a different beast. It uses a Query to
filter records on a Primary table; records on Constrained
tables that join to the Primary are filtered to match. Applied
per-role (`ContextType=RoleName`) or per-application-context.
Caution: poorly-designed policies tank query performance. See
the gotchas section.

---

## Permission grants — the five-value matrix

Every privilege's `<Grant>` block (and every entry point's grant)
uses five permission levels, each with an `Allow` / `NoAccess` /
`Unset` value:

| Level | Means |
|---|---|
| `Read` | Can view the record |
| `Update` | Can modify existing records |
| `Create` | Can insert new records |
| `Delete` | Can remove records |
| `Correct` | Can use the "correct" / reversal flow (financial-system specific; typically same as Update for non-fin scenarios) |

For menu item entry points specifically, you may also see
`Invoke` instead of the five-level grant — that's for Action
menu items where there's no record-level operation, just "can
fire this action."

---

## Typed authoring tools

All four security types ship as first-class on the typed
authoring layer — prefer these over the raw `xpp_create_object`
escape hatch:

| Type | Tools |
|---|---|
| AxSecurityPrivilege | `xpp_create_privilege`, `xpp_get_privilege`, `xpp_patch_privilege` |
| AxSecurityDuty | `xpp_create_duty`, `xpp_get_duty`, `xpp_patch_duty` |
| AxSecurityRole | `xpp_create_role`, `xpp_get_role`, `xpp_patch_role` |
| AxSecurityPolicy | `xpp_create_policy`, `xpp_get_policy`, `xpp_patch_policy` |

`SecurityGrant` is the common per-CRUD access bag (Read /
Update / Create / Delete / Correct / Invoke, each `Allow` /
`Grant` / `Deny` / `NoAccess`). MS strips default
`Allow`-everywhere grants on DataEntityPermission on read — the
on-disk XML reflects MS's canonical form, not the authored
shape; that's expected.

`AxSecurityPolicy.ConstrainedTables` is polymorphic on
`Kind=Table | Expression` with recursive children — see the
typed `PolicyConstrainedEntity` shape.

The XML shapes below remain useful as a reference for the
underlying on-disk form (and for the rare cases the typed
surface doesn't cover — fall back to `xpp_create_object` then).

---

## XML shapes

All four security AOT types live in **no-namespace** (unlike
menu items which use V1). The wrapper is just `xmlns:i="..."`.

### `AxSecurityPrivilege`

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityPrivilege xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentMaintain</Name>
    <Description>The privilege for maintaining shipments</Description>
    <Label>@MyLabels:ShipmentMaintain</Label>
    <DataEntityPermissions />
    <DirectAccessPermissions />
    <EntryPoints>
        <AxSecurityEntryPointReference>
            <Name>CONShipmentMaintainMenuItem</Name>
            <Grant>
                <Correct>Allow</Correct>
                <Create>Allow</Create>
                <Delete>Allow</Delete>
                <Read>Allow</Read>
                <Update>Allow</Update>
            </Grant>
            <ObjectName>CONShipment</ObjectName>
            <ObjectType>MenuItemDisplay</ObjectType>
            <Forms />
        </AxSecurityEntryPointReference>
    </EntryPoints>
    <FormControlOverrides />
</AxSecurityPrivilege>
```

Four child collections:
- **`EntryPoints`** — references to menu items, forms, services.
  This is the most common case for a UI feature.
- **`DataEntityPermissions`** — references to AxDataEntityView
  (for OData/DMF integration permissions). See worked example.
- **`DirectAccessPermissions`** — references to tables directly
  (rare; usually access is gated by entry points).
- **`FormControlOverrides`** — fine-grained per-control overrides
  (hide / disable a specific button for users with this privilege).

### `AxSecurityDuty`

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityDuty xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentClerkProcess</Name>
    <Label>@MyLabels:ShipmentClerkDuty</Label>
    <Privileges>
        <AxSecurityPrivilegeReference>
            <Name>CONShipmentView</Name>
        </AxSecurityPrivilegeReference>
        <AxSecurityPrivilegeReference>
            <Name>CONShipmentMaintain</Name>
        </AxSecurityPrivilegeReference>
    </Privileges>
</AxSecurityDuty>
```

Just a list of `<AxSecurityPrivilegeReference><Name>...</Name></AxSecurityPrivilegeReference>`.

### `AxSecurityRole`

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityRole xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentClerk</Name>
    <Label>@MyLabels:ShipmentClerkRole</Label>
    <DirectAccessPermissions />
    <Duties>
        <AxSecurityDutyReference>
            <Name>CONShipmentClerkProcess</Name>
        </AxSecurityDutyReference>
    </Duties>
    <Privileges>
        <AxSecurityPrivilegeReference>
            <Name>CONReadOnlyDashboard</Name>
        </AxSecurityPrivilegeReference>
    </Privileges>
    <SubRoles />
</AxSecurityRole>
```

Roles can reference duties, privileges directly, and sub-roles
(role-hierarchy inheritance). Usual pattern: duties for grouped
function, direct privileges only for cross-cutting concerns.

### `AxSecurityPolicy` (XDS)

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityPolicy xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONRetailAssortmentsByStore</Name>
    <ConstrainedTable>Yes</ConstrainedTable>
    <ContextType>RoleName</ContextType>
    <Label>@MyLabels:RetailAssortmentsByStore</Label>
    <PrimaryTable>RetailAssortmentTable</PrimaryTable>
    <Query>CONRetailXDSAssortments</Query>
    <RoleName>CONRetailStoreManager</RoleName>
    <ConstrainedTables />
</AxSecurityPolicy>
```

Different shape entirely — see the XDS section below.

---

## Property checklists

### `AxSecurityPrivilege`

| Property | Notes |
|---|---|
| **`Name`** | `<prefix><Function>(View\|Maintain)` is the convention. |
| **`Label`** | What user-administrators see in security workspace. Label-ref. |
| `Description` | Free text. Useful for explaining intent to security admins. |
| `Enabled` | `Yes` (default). Set `No` to disable without deleting. |
| `IsObsolete` | Mark deprecated privileges. |
| `Visibility` | Usually omitted (default Public). |
| **`EntryPoints`** | The main payload — references to what this privilege grants access to. See entry point shape below. |
| `DataEntityPermissions` | For OData / DMF integration. See worked example. |
| `DirectAccessPermissions` | Rare — direct table access. |
| `FormControlOverrides` | Fine-grained per-control overrides. |

#### `<AxSecurityEntryPointReference>` shape

| Property | Notes |
|---|---|
| `Name` | Local name within this privilege. Can match `ObjectName` for simplicity. |
| `Grant` | The five-value matrix above. |
| `ObjectName` | The AOT name of the menu item / form / service this entry point references. |
| `ObjectType` | `MenuItemDisplay`, `MenuItemAction`, `MenuItemOutput`, `Form`, `Service`, `Table`, `DataEntity`. |
| `Forms` | When `ObjectType=Form`, optional drilldown to specific controls; usually empty. |

### `AxSecurityDuty`

| Property | Notes |
|---|---|
| **`Name`** | `<prefix><Function>(Process\|Inquire\|Approve\|Maintain)` — verb-as-suffix is conventional. |
| **`Label`** | What admins see. Label-ref. |
| **`Privileges`** | List of privilege references. The whole point of the duty. |

### `AxSecurityRole`

| Property | Notes |
|---|---|
| **`Name`** | Human-readable role identifier. The convention varies — both `CONShipmentClerk` and `CH_ShipmentClerk_Role` are seen. Pick a convention per project. |
| **`Label`** | What admins see in the Users page. Label-ref. |
| `Description` | Free text. |
| `IsObsolete` | Mark deprecated roles. |
| `Duties` | List of duty references — the usual content. |
| `Privileges` | Direct privilege refs — for one-off grants outside the duty structure. |
| `DirectAccessPermissions` | Rarely used at role level. |
| `SubRoles` | Inherit from parent role(s). Useful for role hierarchies. |

### `AxSecurityPolicy` (XDS)

| Property | Notes |
|---|---|
| **`Name`** | `<prefix><PolicyName>` — describe what's being filtered (e.g., `CONRetailAssortmentsByStore`). |
| **`Label`** | Admin-facing label. |
| **`PrimaryTable`** | The table whose rows define the filter (e.g., `CustTable` if filtering by customer). |
| **`Query`** | An AxQuery that selects allowed rows from the primary table. Filter conditions go here. |
| **`ConstrainedTable`** | `Yes` to apply this policy automatically to the constrained tables. |
| `ConstrainedTables` | Tables filtered by joining through the primary. Often empty when the policy is auto-derived. |
| **`ContextType`** | `RoleName` (per-role), `RoleProperty` (multi-role by property), or `ApplicationContext` (set programmatically via `XDS::SetContext`). |
| **`RoleName`** | When `ContextType=RoleName`, the role this policy applies to. |
| `Operation` | What operation type this policy filters (defaults to all). |

---

## Extensions — extending MS-shipped duties and roles

Adding YOUR privilege to an MS-shipped duty:

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityDutyExtension xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CustomerInquire.ContosoRetail</Name>
    <Privileges>
        <AxSecurityPrivilegeReference>
            <Name>CONCustomerExtraInfoView</Name>
        </AxSecurityPrivilegeReference>
    </Privileges>
</AxSecurityDutyExtension>
```

Same `.ExtensionSuffix` naming convention as other extensions
(see `dynamics-xpp:xpp-extension`). The extension targets the MS
duty by name (the part before the dot).

`AxSecurityRoleExtension` works the same way.

**Use extensions instead of modifying MS roles directly.** Per
the model-sealing rule (see `dynamics-xpp:xpp-project`), you cannot
modify MS-shipped AOT objects — only extend them.

---

## Common workflows

### Wiring a new feature (the canonical sequence)

```
1. Create the AOT object (form / class / data entity)
2. Create the menu item (dynamics-xpp:xpp-menuitem)
3. Create the privilege referencing the menu item under EntryPoints
4. Add the privilege to a new duty (or extend an MS duty)
5. Add the duty to a new role (or extend an MS role)
6. Compile + Build — BP rules verify the wire-up
```

Skip 4-5 only if the user just wants the menu item to exist for
later wiring. Don't skip in checked-in code — the BP rules
(`BPErrorPrivilegeNotCoveredByDuty`,
`BPErrorMenuItemNotCoveredByPrivilege`) will fire.

### Read vs Maintain pair

Most features need both. Create:

- `<Object>View` — Grant `Read=Allow`, `Update=NoAccess`, others NoAccess
- `<Object>Maintain` — Grant all five `Allow`

Reach Inquire roles get the `View`; clerk / processor roles
get the `Maintain`. Don't merge them — security admins want
the read/write distinction.

### Securing a data entity for integration

If you author a new data entity, security depends on
`IntegrationMode`:
- **DataServices** (OData) — needs a privilege with `Grant=Read`
  (view) and `Grant=Delete` (full CRUD), in DataEntityPermissions.
- **DataManagement** (DMF import/export) — extends the
  `DataManagementApplication<Category>EntitiesMaintain` /
  `View` duties (e.g., `MasterEntitiesMaintain` for master data).

See worked example below.

---

## Worked examples

### Privilege referencing a Display menu item

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityPrivilege xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentView</Name>
    <Label>@MyLabels:ShipmentView</Label>
    <EntryPoints>
        <AxSecurityEntryPointReference>
            <Name>CONShipment</Name>
            <Grant>
                <Read>Allow</Read>
                <Update>NoAccess</Update>
                <Create>NoAccess</Create>
                <Delete>NoAccess</Delete>
                <Correct>NoAccess</Correct>
            </Grant>
            <ObjectName>CONShipment</ObjectName>
            <ObjectType>MenuItemDisplay</ObjectType>
        </AxSecurityEntryPointReference>
    </EntryPoints>
</AxSecurityPrivilege>
```

Read-only access via a Display menu item. The MS BP rule
`BPErrorPrivilegeNotCoveredByDuty` will fire on this until you
add it to a duty.

### Privilege for a data entity (OData integration)

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityPrivilege xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONCustomerEntityMaintain</Name>
    <Label>@MyLabels:CustomerEntityMaintain</Label>
    <DataEntityPermissions>
        <AxSecurityDataEntityPermission>
            <Grant>
                <Read>Allow</Read>
                <Update>Allow</Update>
                <Create>Allow</Create>
                <Delete>Allow</Delete>
                <Correct>Allow</Correct>
            </Grant>
            <Name>CONCustomerEntity</Name>
            <Fields />
            <Methods />
        </AxSecurityDataEntityPermission>
    </DataEntityPermissions>
    <EntryPoints />
</AxSecurityPrivilege>
```

`DataEntityPermissions` instead of `EntryPoints` — this gates
the OData endpoint, not a UI menu item. Pair with a
`<DataEntity>View` privilege for read-only access.

### Duty bundling related privileges

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityDuty xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentClerkProcess</Name>
    <Label>@MyLabels:ShipmentClerkDuty</Label>
    <Privileges>
        <AxSecurityPrivilegeReference><Name>CONShipmentView</Name></AxSecurityPrivilegeReference>
        <AxSecurityPrivilegeReference><Name>CONShipmentMaintain</Name></AxSecurityPrivilegeReference>
        <AxSecurityPrivilegeReference><Name>CONContainerView</Name></AxSecurityPrivilegeReference>
        <AxSecurityPrivilegeReference><Name>CONContainerMaintain</Name></AxSecurityPrivilegeReference>
    </Privileges>
</AxSecurityDuty>
```

### Role assigning a duty

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityRole xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONShipmentClerk</Name>
    <Label>@MyLabels:ShipmentClerkRole</Label>
    <Description>Processes shipments and manages containers.</Description>
    <Duties>
        <AxSecurityDutyReference><Name>CONShipmentClerkProcess</Name></AxSecurityDutyReference>
    </Duties>
    <Privileges />
    <SubRoles />
</AxSecurityRole>
```

### XDS policy filtering customers by sales rep

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityPolicy xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONCustomersBySalesRep</Name>
    <Label>@MyLabels:CustomersBySalesRep</Label>
    <PrimaryTable>CustTable</PrimaryTable>
    <Query>CONXDSCustomerBySalesRep</Query>
    <ConstrainedTable>Yes</ConstrainedTable>
    <ContextType>RoleName</ContextType>
    <RoleName>CONSalesRep</RoleName>
    <ConstrainedTables />
</AxSecurityPolicy>
```

The query `CONXDSCustomerBySalesRep` selects customers where the
SalesRep field matches the current user. The constrained
tables (CustInvoiceJour, CustTrans, etc. that join to CustTable)
inherit the filter automatically because `ConstrainedTable=Yes`.

### Extending an MS duty to include your privilege

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityDutyExtension xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CustCustomerMaster.ContosoRetail</Name>
    <Privileges>
        <AxSecurityPrivilegeReference><Name>CONCustomerExtraInfoView</Name></AxSecurityPrivilegeReference>
        <AxSecurityPrivilegeReference><Name>CONCustomerExtraInfoMaintain</Name></AxSecurityPrivilegeReference>
    </Privileges>
</AxSecurityDutyExtension>
```

The `.ContosoRetail` suffix marks this as a model extension. The
extension targets `CustCustomerMaster` (an MS-shipped duty)
and adds your privileges to its existing list.

---

## Common gotchas

### `BPErrorPrivilegeNotCoveredByDuty`

The privilege exists but isn't referenced by any duty. Fix:
add it to a duty (or to a role directly via `Privileges`). If
intentional (e.g., a stub privilege), suppress via
`bestPractices.suppress` in `.dynamics-xpp/config.json`.

### `BPErrorMenuItemNotCoveredByPrivilege`

The menu item exists but no privilege references it under
`EntryPoints`. Users can't reach it. Fix: add an
`AxSecurityEntryPointReference` to a privilege.

### Grant levels for Display menu items

A Display menu item only opens a form — `Read=Allow` is the
useful grant. The OTHER four levels (Update/Create/Delete/Correct)
gate operations that happen INSIDE the form against the form's
data sources. They're meaningful even on a Display menu item.

### Forgetting to set `NoAccess` explicitly

If you omit a permission level, it defaults to `Unset`, which
**inherits** from any other privilege the user has. For a
strict read-only privilege, set `Update`, `Create`, `Delete`,
`Correct` to `NoAccess` explicitly — otherwise another
privilege the user holds may grant write access through the
union.

### XDS policy on financial dimensions

**Don't.** MS docs explicitly warn against XDS policies on
financial dimensions — corrupts data. Filter at the backing
entity instead (Customers, Vendors, Operating Units).

### XDS policy performance

The policy's query is appended to the WHERE clause of every
SELECT/UPDATE/DELETE on constrained tables. Many joins =
significant slowdown. Test thoroughly under load. The MS
guidance is: prefer lookup tables to deep joins; index the
filter columns.

### `XDSDataAccessPolicyBypassRole` for debugging

If users complain about missing data, assign them
`XDSDataAccessPolicyBypassRole` temporarily. If the data
appears, an XDS policy is the cause. Useful debugging move
when investigating "the customer list looks wrong."

### Role name collisions across models

Roles are globally named — you can't have two `ShipmentClerk`
in the same deployment. Use your project prefix. The convention
matches the rest of dynamics-xpp:xpp-project's `objectPrefix`.

---

## See also

- `dynamics-xpp:xpp-menuitem` — entry points referenced by
  `AxSecurityEntryPointReference`. Almost always written before
  the privilege.
- `dynamics-xpp:xpp-extension` — `AxSecurityDutyExtension` /
  `AxSecurityRoleExtension` follow the same `.ExtensionSuffix`
  pattern as other metadata extensions.
- `dynamics-xpp:xpp-query` — XDS policies need a backing query that
  filters the primary table.
- `dynamics-xpp:xpp-project` — `bestPractices.suppress` is where
  you silence intentional gaps like `BPErrorPrivilegeNotCoveredByDuty`
  for stub privileges.
- [MS: Extensible data security policies](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/sysadmin/extensible-data-security-policies)
- [MS: Security and data entities](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/security-data-entities)
- [MS Learn module: Implement role-based security in finance and operations apps](https://learn.microsoft.com/training/modules/role-security-finance-operations/)
