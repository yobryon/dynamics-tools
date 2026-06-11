using System.ComponentModel;
using Xpp.Service.Domain.Security;

namespace Xpp.Service.Domain.Services;

// ----------------------------------------------------------------------------
// AxService — a SOAP/REST service endpoint exposing X++ class methods.
// AxServiceGroup — a deployment unit collecting related services.
//
// Both are no-namespace roots (like AxClass). AxService's
// ServiceOperations entries reuse SecurityGrant for the per-operation
// SubscriberAccessLevel.
// ----------------------------------------------------------------------------

public sealed record ServiceOperation
{
    [Description("Operation name as exposed by the service. Conventionally matches Method.")]
    public string Name { get; init; } = string.Empty;

    [Description("Name of the X++ method on the backing Class that implements this operation.")]
    public string Method { get; init; } = string.Empty;

    [Description("Whether the operation is idempotent (safe to retry without side-effect duplication). Default true.")]
    public bool? EnableIdempotence { get; init; }

    [Description("Per-operation access grant. Reused from the Security namespace.")]
    public SecurityGrant? SubscriberAccessLevel { get; init; }
}

public sealed record CreateServiceRequest
{
    [Description("Service name. PascalCase. Conventionally ends with 'Service'.")]
    public string Name { get; init; } = string.Empty;

    [Description("X++ class that backs the service operations. Required.")]
    public string? Class { get; init; }

    public string? Description { get; init; }

    [Description("External name exposed via SOAP/REST. Often the same as Name.")]
    public string? ExternalName { get; init; }

    [Description("XML namespace for the SOAP endpoint. Usually 'http://schemas.microsoft.com/dynamics/<year>/services'.")]
    public string? Namespace { get; init; }

    public string? OperationalDomain { get; init; }
    public bool? IsObsolete { get; init; }

    [Description("Per-operation access grant applied service-wide. Operations may override via their own SubscriberAccessLevel.")]
    public SecurityGrant? SubscriberAccessLevel { get; init; }

    [Description("Operations exposed by the service — each names a Method on the backing Class.")]
    public List<ServiceOperation>? ServiceOperations { get; init; }
}

public sealed record PatchServiceRequest
{
    public string? Class { get; init; }
    public string? Description { get; init; }
    public string? ExternalName { get; init; }
    public string? Namespace { get; init; }
    public string? OperationalDomain { get; init; }
    public bool? IsObsolete { get; init; }
    public SecurityGrant? SubscriberAccessLevel { get; init; }
    public List<ServiceOperation>? ServiceOperations { get; init; }
}

public sealed record GetServiceResponse
{
    public string Name { get; init; } = string.Empty;
    public string? Class { get; init; }
    public string? Description { get; init; }
    public string? ExternalName { get; init; }
    public string? Namespace { get; init; }
    public string? OperationalDomain { get; init; }
    public bool? IsObsolete { get; init; }
    public SecurityGrant? SubscriberAccessLevel { get; init; }
    public List<ServiceOperation>? ServiceOperations { get; init; }
}

// ----------------------------------------------------------------------------
// AxServiceGroup
// ----------------------------------------------------------------------------

public sealed record ServiceGroupMember
{
    [Description("Member name. Conventionally matches Service.")]
    public string Name { get; init; } = string.Empty;
    [Description("Name of the AxService included in this group.")]
    public string Service { get; init; } = string.Empty;
}

public sealed record CreateServiceGroupRequest
{
    [Description("Service-group name. PascalCase. Conventionally a deployment-bundle name.")]
    public string Name { get; init; } = string.Empty;

    [Description("Auto-deploy when the model is deployed. Default true.")]
    public bool? AutoDeploy { get; init; }

    public string? Description { get; init; }
    public bool? IsObsolete { get; init; }

    [Description("Services included in this group.")]
    public List<ServiceGroupMember>? Services { get; init; }
}

public sealed record PatchServiceGroupRequest
{
    public bool? AutoDeploy { get; init; }
    public string? Description { get; init; }
    public bool? IsObsolete { get; init; }
    public List<ServiceGroupMember>? Services { get; init; }
}

public sealed record GetServiceGroupResponse
{
    public string Name { get; init; } = string.Empty;
    public bool? AutoDeploy { get; init; }
    public string? Description { get; init; }
    public bool? IsObsolete { get; init; }
    public List<ServiceGroupMember>? Services { get; init; }
}
