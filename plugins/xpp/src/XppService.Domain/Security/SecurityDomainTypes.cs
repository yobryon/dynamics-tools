using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain.Security;

// ----------------------------------------------------------------------------
// AxSecurity* family — Privilege, Duty, Role, Policy.
//
// Shared concept: a "Grant" is a bag of per-CRUD access levels (Read /
// Update / Create / Delete / Correct / Invoke), each optional, each
// resolving to a SecurityAccessLevel. On disk the access level is a
// string-valued enum; we type it.
// ----------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityAccessLevel
{
    [Description("Explicitly deny.")]
    NoAccess,
    [Description("Grant the permission (allow).")]
    Allow,
    [Description("Strongest grant; overrides denies at lower layers.")]
    Grant,
    [Description("Explicit deny.")]
    Deny,
}

public sealed record SecurityGrant
{
    public SecurityAccessLevel? Read { get; init; }
    public SecurityAccessLevel? Update { get; init; }
    public SecurityAccessLevel? Create { get; init; }
    public SecurityAccessLevel? Delete { get; init; }
    public SecurityAccessLevel? Correct { get; init; }
    public SecurityAccessLevel? Invoke { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityEntryPointObjectType
{
    MenuItemDisplay,
    MenuItemAction,
    MenuItemOutput,
    Form,
    Tile,
    [Description("A single operation (method) on a custom service. Set ObjectName to the service, ObjectChildName to the operation — one entry point PER operation. (The on-disk EntryPointType value is 'ServiceOperation', NOT 'Service'.)")]
    ServiceOperation,
    [Description("Other / future entry-point kinds. The on-disk value is preserved via RawObjectType.")]
    Other,
}

public sealed record SecurityEntryPoint
{
    [Description("Entry-point name. Conventionally the same as ObjectName for menu-item references.")]
    public string Name { get; init; } = string.Empty;

    [Description("Grant block — per-CRUD access levels applied when this entry point is invoked under the host privilege/role.")]
    public SecurityGrant? Grant { get; init; }

    [Description("Target object's AOT name (the AxMenuItemDisplay / AxMenuItemAction / AxForm / Service / etc.). For ObjectType=ServiceOperation this is the SERVICE name; the operation goes in ObjectChildName.")]
    public string ObjectName { get; init; } = string.Empty;

    [Description("For ObjectType=ServiceOperation: the operation (method) name on the service. A privilege targets one entry point PER operation, so create one SecurityEntryPoint per method. Ignored for other object types.")]
    public string? ObjectChildName { get; init; }

    [Description("Target object's type discriminator. For a custom service operation use ServiceOperation (with ObjectChildName). 'Other' + RawObjectType is a last resort for kinds not modeled by the enum — but RawObjectType must be a value the platform's EntryPointType actually accepts, or the write is rejected.")]
    public SecurityEntryPointObjectType ObjectType { get; init; }

    [Description("When ObjectType=Other: the on-disk value verbatim. Must be a valid EntryPointType member; an unknown value is rejected at the boundary (it used to be silently dropped).")]
    public string? RawObjectType { get; init; }

    [Description("Form names this entry point applies to (rarely populated; usually emitted as <Forms />).")]
    public List<string>? Forms { get; init; }
}

public sealed record SecurityFieldReference
{
    public string Name { get; init; } = string.Empty;
    public SecurityGrant? Grant { get; init; }
}

public sealed record SecurityMethodReference
{
    public string Name { get; init; } = string.Empty;
    public SecurityGrant? Grant { get; init; }
}

public sealed record SecurityDataEntityPermission
{
    [Description("Grant block applied to the named data entity.")]
    public SecurityGrant? Grant { get; init; }

    [Description("Name of the data entity / table.")]
    public string Name { get; init; } = string.Empty;

    [Description("Field-level grants. Almost always empty in practice.")]
    public List<SecurityFieldReference>? Fields { get; init; }

    [Description("Method-level grants. Almost always empty in practice.")]
    public List<SecurityMethodReference>? Methods { get; init; }
}

public sealed record SecurityDataEntityReference
{
    [Description("Grant block applied directly. Used by DirectAccessPermissions on Privilege/Role.")]
    public SecurityGrant? Grant { get; init; }

    [Description("Name of the table / view / data entity referenced.")]
    public string Name { get; init; } = string.Empty;

    public List<SecurityFieldReference>? Fields { get; init; }
}

public sealed record SecurityFormControlReference
{
    [Description("Form control name (typically a control inside the named form/collection).")]
    public string Name { get; init; } = string.Empty;
    public SecurityGrant? Grant { get; init; }
}

public sealed record SecurityFormControlCollection
{
    [Description("Form (or design-tree container) name. Per-control grants live in Controls.")]
    public string Name { get; init; } = string.Empty;
    public List<SecurityFormControlReference>? Controls { get; init; }
}

public sealed record SecurityReference
{
    [Description("Name of the referenced privilege / duty / role.")]
    public string Name { get; init; } = string.Empty;
}

// ============================================================================
// AxSecurityPrivilege
// ============================================================================

public sealed record CreatePrivilegeRequest
{
    [Description("Privilege name. PascalCase. Convention: '<Area><Function>{Maintain|View}'.")]
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public List<SecurityDataEntityPermission>? DataEntityPermissions { get; init; }
    [Description("Direct access to tables/views — bypasses the entry-point indirection.")]
    public List<SecurityDataEntityReference>? DirectAccessPermissions { get; init; }
    [Description("Menu-item / form entry points this privilege grants access to.")]
    public List<SecurityEntryPoint>? EntryPoints { get; init; }
    [Description("Per-form per-control overrides (e.g. read-only access to one tab).")]
    public List<SecurityFormControlCollection>? FormControlOverrides { get; init; }
}

public sealed record PatchPrivilegeRequest
{
    public string? Label { get; init; }
    public string? Description { get; init; }
    public List<SecurityDataEntityPermission>? DataEntityPermissions { get; init; }
    public List<SecurityDataEntityReference>? DirectAccessPermissions { get; init; }
    public List<SecurityEntryPoint>? EntryPoints { get; init; }
    public List<SecurityFormControlCollection>? FormControlOverrides { get; init; }
}

public sealed record GetPrivilegeResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public List<SecurityDataEntityPermission>? DataEntityPermissions { get; init; }
    public List<SecurityDataEntityReference>? DirectAccessPermissions { get; init; }
    public List<SecurityEntryPoint>? EntryPoints { get; init; }
    public List<SecurityFormControlCollection>? FormControlOverrides { get; init; }
}

// ============================================================================
// AxSecurityDuty
// ============================================================================

public sealed record CreateDutyRequest
{
    [Description("Duty name. PascalCase. Convention: '<Area><Function>{Maintain|View|Inquire}'.")]
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    [Description("Privileges aggregated by this duty.")]
    public List<SecurityReference>? Privileges { get; init; }
}

public sealed record PatchDutyRequest
{
    public string? Label { get; init; }
    public string? Description { get; init; }
    public List<SecurityReference>? Privileges { get; init; }
}

public sealed record GetDutyResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public List<SecurityReference>? Privileges { get; init; }
}

// ============================================================================
// AxSecurityRole
// ============================================================================

public sealed record CreateRoleRequest
{
    [Description("Role name. PascalCase. Convention: '<JobTitle>' (e.g. CustOrderClerk, LedgerAccountant).")]
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    [Description("Context-policy string that scopes which policies apply to this role, e.g. 'PolicyForVendorRoles'.")]
    public string? ContextString { get; init; }
    [Description("Whether the role may be deleted from the security-configuration UI. Default Yes.")]
    public bool? CanBeDeletedFromUI { get; init; }
    [Description("Direct access to tables/views.")]
    public List<SecurityDataEntityReference>? DirectAccessPermissions { get; init; }
    [Description("Duties this role aggregates.")]
    public List<SecurityReference>? Duties { get; init; }
    [Description("Privileges this role directly grants (in addition to those via Duties).")]
    public List<SecurityReference>? Privileges { get; init; }
    [Description("Sub-roles included by this role.")]
    public List<SecurityReference>? SubRoles { get; init; }
}

public sealed record PatchRoleRequest
{
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string? ContextString { get; init; }
    public bool? CanBeDeletedFromUI { get; init; }
    public List<SecurityDataEntityReference>? DirectAccessPermissions { get; init; }
    public List<SecurityReference>? Duties { get; init; }
    public List<SecurityReference>? Privileges { get; init; }
    public List<SecurityReference>? SubRoles { get; init; }
}

public sealed record GetRoleResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public List<SecurityDataEntityReference>? DirectAccessPermissions { get; init; }
    public List<SecurityReference>? Duties { get; init; }
    public List<SecurityReference>? Privileges { get; init; }
    public List<SecurityReference>? SubRoles { get; init; }
}

// ============================================================================
// AxSecurityPolicy — row-level security policies.
// ============================================================================

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PolicyConstrainedEntityKind
{
    [Description("ConstrainedTable subtype — a single backing table.")]
    Table,
    [Description("ConstrainedExpression subtype — a grouping node that holds further tables/expressions.")]
    Expression,
}

public sealed record PolicyConstrainedEntity
{
    [Description("Subtype discriminator. Table = AxSecurityPolicyConstrainedTable, Expression = AxSecurityPolicyConstrainedExpression.")]
    public PolicyConstrainedEntityKind Kind { get; init; }

    [Description("Table or expression name.")]
    public string Name { get; init; } = string.Empty;

    [Description("Recursive children. Table entries usually have an empty list; Expression entries hold the actual restricted tables.")]
    public List<PolicyConstrainedEntity>? ConstrainedTables { get; init; }

    [Description("Table-relation name when Kind=Table. Names the relation on this table that joins back to the policy's PrimaryTable.")]
    public string? TableRelation { get; init; }

    [Description("Whether this node is actually constrained (carries a filter). Default No.")]
    public bool? Constrained { get; init; }

    [Description("The constraint expression text when Kind=Expression, e.g. '(View.AccountNum == Table.AccountNum)'. The actual row-filter logic.")]
    public string? Value { get; init; }
}

public sealed record CreatePolicyRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }

    [Description("Whether the policy applies via the framework's standard ConstrainedTable mechanism. Almost always Yes.")]
    public bool? ConstrainedTable { get; init; }

    [Description("Whether the policy is currently active. Default true.")]
    public bool? Enabled { get; init; }

    [Description("The table the policy hangs off. Filtering joins on this table propagate via relations to ConstrainedTables.")]
    public string? PrimaryTable { get; init; }

    [Description("Name of the AxQuery that defines the row-filter applied to PrimaryTable.")]
    public string? Query { get; init; }

    [Description("How the policy is contextually applied: ContextType drives whether RoleName/RoleProperty gate it. Values: RoleName / RoleProperty / Global.")]
    public string? ContextType { get; init; }

    [Description("The role-property context string (when ContextType=RoleProperty), e.g. 'PolicyForVendorRoles'.")]
    public string? ContextString { get; init; }

    [Description("The role name that activates this policy when ContextType=RoleName.")]
    public string? RoleName { get; init; }

    [Description("Which data operations the policy covers: AllOperations / Select / etc.")]
    public string? Operation { get; init; }

    [Description("Use a NOT EXISTS join instead of an inner join when applying the filter. Default No.")]
    public bool? UseNotExistJoin { get; init; }

    [Description("Tables/expressions restricted by this policy via relations to PrimaryTable.")]
    public List<PolicyConstrainedEntity>? ConstrainedTables { get; init; }
}

public sealed record PatchPolicyRequest
{
    public string? Label { get; init; }
    public string? Description { get; init; }
    public bool? ConstrainedTable { get; init; }
    public bool? Enabled { get; init; }
    public string? PrimaryTable { get; init; }
    public string? Query { get; init; }
    public string? ContextType { get; init; }
    public string? ContextString { get; init; }
    public string? RoleName { get; init; }
    public string? Operation { get; init; }
    public bool? UseNotExistJoin { get; init; }
    public List<PolicyConstrainedEntity>? ConstrainedTables { get; init; }
}

public sealed record GetPolicyResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public bool? ConstrainedTable { get; init; }
    public bool? Enabled { get; init; }
    public string? PrimaryTable { get; init; }
    public string? Query { get; init; }
    public List<PolicyConstrainedEntity>? ConstrainedTables { get; init; }
}
