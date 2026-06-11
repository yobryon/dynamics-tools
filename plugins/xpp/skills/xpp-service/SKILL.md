---
name: xpp-service
description: TRIGGER when authoring an AxService or AxServiceGroup. Services expose X++ class methods as SOAP/REST endpoints; service groups bundle services for deployment. Required when integrating F&O with external systems that need to call into AOS logic.
---

# Service & ServiceGroup

F&O exposes X++ class methods to external callers (other
systems, integration platforms, custom apps) via **services**:

- **`AxService`** — declares which X++ class is the service
  contract and which of its methods are externally callable.
  One service = one X++ class + the operations on it.
- **`AxServiceGroup`** — bundles one or more services into a
  deployable endpoint. The group is what gets exposed as a
  SOAP / REST URL.

External clients call `https://<env>/SOAPService/<ServiceGroupExternalName>?wsdl`
(or the REST equivalent) to discover and invoke operations.

These are paired in this skill because you almost always
write them together — a service without a group isn't
deployed, and a group without services has nothing to expose.

---

## Read this skill when

- You need an external system to invoke F&O logic (post an
  order, sync inventory, fire a workflow).
- You're publishing a custom integration endpoint on top of
  existing X++ business logic.
- You're moving from OData (auto-generated from data
  entities) to a custom RPC-style call because data-entity
  semantics don't fit (e.g., bulk transactional operations).

For OData/DMF-style data integration, use `AxDataEntityView`
instead — it auto-generates a service for the entity. Custom
services are for operation-style calls (verbs, not nouns).

---

## Typed authoring tools

Both types are first-class on the typed authoring layer —
prefer these over the raw `xpp_create_object` escape hatch:

| Type | Tools |
|---|---|
| AxService | `xpp_create_service`, `xpp_get_service`, `xpp_patch_service` |
| AxServiceGroup | `xpp_create_service_group`, `xpp_get_service_group`, `xpp_patch_service_group` |

`AxServiceOperation.SubscriberAccessLevel` reuses the
`SecurityGrant` record from the Security namespace (the
per-CRUD `Read / Update / Create / Delete / Correct / Invoke`
bag). MS strips `EnableIdempotence=No` on read (default-strip
pattern); other defaults pass through.

---

## XML shapes

Both `AxService` and `AxServiceGroup` use **no-namespace**.

### AxService — minimal

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxService xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CH_OrderPostingService</Name>
    <Class>CH_OrderPostingService</Class>
    <ExternalName>OrderPosting</ExternalName>
    <ServiceOperations>
        <AxServiceOperation>
            <Name>postOrder</Name>
            <Method>postOrder</Method>
        </AxServiceOperation>
    </ServiceOperations>
</AxService>
```

### AxService — with permission grants and description

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxService xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CH_SalesOrderPostingService</Name>
    <Class>CH_PostingPackingSlip</Class>
    <Description>@MyLabels:SalesOrderPostingService</Description>
    <ExternalName>SaleslineItemsPosting</ExternalName>
    <ServiceOperations>
        <AxServiceOperation>
            <Name>SaleslineItemsPosting</Name>
            <Method>SaleslineItemsPosting</Method>
            <SubscriberAccessLevel>
                <Correct>Allow</Correct>
                <Create>Allow</Create>
                <Delete>Allow</Delete>
                <Invoke>Allow</Invoke>
                <Read>Allow</Read>
                <Update>Allow</Update>
            </SubscriberAccessLevel>
        </AxServiceOperation>
    </ServiceOperations>
</AxService>
```

### AxServiceGroup — minimal

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxServiceGroup xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CH_OrderPostingServiceGroup</Name>
    <Services>
        <AxServiceGroupService>
            <Name>OrderPostingService</Name>
            <Service>CH_OrderPostingService</Service>
        </AxServiceGroupService>
    </Services>
</AxServiceGroup>
```

### AxServiceGroup — auto-deployed, with multiple services

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxServiceGroup xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONAppServices</Name>
    <AutoDeploy>Yes</AutoDeploy>
    <Description>@MyLabels:CONAppServicesGroup</Description>
    <Services>
        <AxServiceGroupService>
            <Name>LaborService</Name>
            <Service>chtLaborService</Service>
        </AxServiceGroupService>
        <AxServiceGroupService>
            <Name>DataWarehouseDocsService</Name>
            <Service>conDataWarehouseDocsService</Service>
        </AxServiceGroupService>
    </Services>
</AxServiceGroup>
```

---

## Property checklist

### AxService

| Property | Notes |
|---|---|
| **`Name`** | AOT name. Convention `<prefix><Function>Service`. |
| **`Class`** | The X++ class that backs this service. Must exist as an AxClass. |
| **`ExternalName`** | What external callers see in the WSDL / OpenAPI. Conventionally CamelCase without prefix (e.g., `OrderPosting`, not `CH_OrderPostingService`). |
| `Description` | Label-ref or free text. Documentation. |
| **`ServiceOperations`** | Required — at least one operation. |
| `ConfigurationKey` | Feature gating. |

#### AxServiceOperation

| Property | Notes |
|---|---|
| **`Name`** | The operation's external name. Often matches `Method`. |
| **`Method`** | The X++ method name on the service class. Must be a public method. |
| `SubscriberAccessLevel` | Per-operation subscriber access. The five-or-six value matrix (Read / Update / Create / Delete / Correct / Invoke). For Invoke-style operations, include `Invoke`. When omitted, defaults inherit from the service-level setting. |

### AxServiceGroup

| Property | Notes |
|---|---|
| **`Name`** | AOT name. Convention `<prefix><Function>ServiceGroup`. |
| **`Services`** | Required — at least one. |
| `AutoDeploy` | `Yes` makes the group available at AOS startup; `No` requires manual deploy. Default `No`. For prod-ready integrations, set `Yes`. |
| `Description` | Documentation. |
| `ExternalName` | If you want a custom URL segment; otherwise derived from Name. |
| `ConfigurationKey` | Feature gating. |

#### AxServiceGroupService

| Property | Notes |
|---|---|
| **`Name`** | Internal alias within the group (any string; usually matches `Service`). |
| **`Service`** | The AxService AOT name referenced. |

---

## The X++ class side

A service-backing class is just a regular X++ class. The
conventional shape:

```xpp
public class CH_OrderPostingService
{
    public CONOrderPostingResultContract postOrder(CONOrderPostingRequestContract _request)
    {
        // ... business logic here
        CONOrderPostingResultContract result = new CONOrderPostingResultContract();
        result.parmSuccess(true);
        return result;
    }
}
```

> **Do NOT add `[SysEntryPointAttribute]` or `[SysOperationServiceBaseAttribute]`
> on current app versions.** What exposes a method as a service operation is the
> AxService `<ServiceOperations>` node (below) referencing a **public** method —
> not an attribute. `[SysEntryPointAttribute]` is **deprecated**
> (`xpp_bp_check` flags `BPUpgradeCodeSysEntryPointAttribute`: "deprecated, you
> can safely remove the attribute"), and `[SysOperationServiceBaseAttribute]`
> may not exist at all in your app version — adding it breaks the compile.
> Earlier guidance prescribed both; that was stale. If `xpp_bp_check` and this
> skill ever disagree on a platform-version fact like this, **trust the live BP
> checker.** (Method-level security on a service is configured via the security
> framework / the operation's privileges, not the old entry-point attribute.)

Key conventions:

- **DataContract / DataMember** on parameter types. SOAP/REST
  needs serializable parameter classes:

```xpp
[DataContractAttribute]
public class CONOrderPostingRequestContract
{
    private str orderId;
    private int qty;

    [DataMemberAttribute('OrderId')]
    public str parmOrderId(str _value = orderId)
    {
        orderId = _value;
        return orderId;
    }

    [DataMemberAttribute('Quantity')]
    public int parmQty(int _value = qty)
    {
        qty = _value;
        return qty;
    }
}
```

- **Return types** must also be DataContract-decorated if
  complex. Primitive returns (`str`, `int`, `boolean`) work
  natively.

See `dynamics-xpp:xpp-class` for the class authoring side.

---

## Common workflows

### Brand-new custom service

```
1. Author the parameter / return contract classes (DataContract).
2. Author the service class — each operation a plain PUBLIC method.
   (No SysEntryPointAttribute / SysOperationServiceBaseAttribute — see
   the callout above; they're deprecated/absent on current versions.)
3. Author the AxService pointing at the class, declaring each
   service operation.
4. Author the AxServiceGroup wrapping the service.
   AutoDeploy=Yes if production-bound.
5. Compile + Build. The deployment infrastructure exposes the
   endpoint at AOS restart.
6. Test by hitting `https://<env>/SOAPServices/<GroupExternalName>?wsdl`
   from an external client.
```

### Wrapping existing X++ logic as a service

When you have business logic that needs to be exposed:

```
1. Identify the entry-point method; make sure it's public.
2. If parameters / return aren't DataContract-friendly, wrap
   with contract classes.
3. Author the AxService + AxServiceGroup as above (the
   <ServiceOperations> entry is what exposes the method).
```

### Adding an operation to an existing service

Add a new `<AxServiceOperation>` to the service's
`<ServiceOperations>`. The new method must exist on the
backing class as a **public** method (no entry-point attribute).

Re-deploy the service group (compile + AOS restart for
auto-deployed groups; manual deploy otherwise).

---

## Common gotchas

### Operation not callable / "method not found"

WSDL shows the operation but calls fail "method not found." On
current versions this is almost always a `<ServiceOperations>` /
class mismatch — the operation entry must reference an existing
**public** method by exact name on the class the AxService points at.
(Do NOT reach for `[SysEntryPointAttribute]` — it's deprecated and is
not what wires up the operation; see the callout in "The X++ class
side.")

### DataContract parameters with non-serializable types

If a parameter is `CommonRecord` (a table buffer) without
contract wrapping, the SOAP serializer fails. Wrap with a
DataContract class exposing primitives + sub-contracts.

### Service deployed but URL returns 404

Check:
- `AutoDeploy=Yes` on the service group.
- Service group is in the rnrproj and got built.
- The user calling the endpoint has the required RBAC
  permissions on the operation (`SubscriberAccessLevel.Invoke`
  is typically required for action-style ops).

### Mixing OData and custom services

Don't expose the same operation both ways. Pick one:
- Data entity (OData / DMF) for noun-shaped resources (Get /
  List / Create / Update / Delete on a record).
- Custom service for verb-shaped operations (PostOrder,
  ValidateShipment, ProcessRefund).

If you find yourself adding a custom service to "fix" a data
entity's limitations, reconsider whether the data entity is
the right shape.

### Service operations and transactions

A single service operation invocation runs in its own
transaction context. If the operation calls multiple data
mutations and one fails, F&O auto-rolls-back IF the method
uses `ttsBegin` / `ttsCommit` properly. Otherwise partial
writes may persist. Always wrap multi-write operations in
explicit ttsBegin/ttsCommit.

### Service group ExternalName collisions

If two service groups have the same ExternalName (across all
models / packages), deployment fails. The convention is to
prefix the name with your model identifier to avoid clashes.

### Subscriber Access Level for Invoke

For "action" operations (no record-level CRUD semantics —
just "do this thing"), the relevant SubscriberAccessLevel
grant is `Invoke`, not the CRUD five. Include it explicitly:

```xml
<SubscriberAccessLevel>
    <Invoke>Allow</Invoke>
</SubscriberAccessLevel>
```

---

## Security wiring

Service operations participate in RBAC like menu items. To
make a service callable by a non-admin user:

1. The service operation has the correct subscriber access
   level.
2. An `AxSecurityPrivilege` references the SERVICE as an
   `<AxSecurityEntryPointReference>` with `ObjectType=Service`.
3. The privilege is in a duty/role assigned to the caller.

See `dynamics-xpp:xpp-security`.

---

## Worked example: a "process refund" custom service

A service exposing a single operation to process a refund.

### Step 1: Request and response contracts

```xpp
[DataContractAttribute]
public class CONRefundRequest
{
    private str salesId;
    private real amount;

    [DataMemberAttribute('SalesId')]
    public str parmSalesId(str _value = salesId)
    {
        salesId = _value;
        return salesId;
    }

    [DataMemberAttribute('Amount')]
    public real parmAmount(real _value = amount)
    {
        amount = _value;
        return amount;
    }
}

[DataContractAttribute]
public class CONRefundResponse
{
    private boolean success;
    private str refundId;
    private str errorMessage;

    [DataMemberAttribute('Success')]
    public boolean parmSuccess(boolean _value = success) { success = _value; return success; }
    [DataMemberAttribute('RefundId')]
    public str parmRefundId(str _value = refundId) { refundId = _value; return refundId; }
    [DataMemberAttribute('ErrorMessage')]
    public str parmErrorMessage(str _value = errorMessage) { errorMessage = _value; return errorMessage; }
}
```

### Step 2: Service class

```xpp
public class CONRefundService
{
    public CONRefundResponse processRefund(CONRefundRequest _request)
    {
        CONRefundResponse response = new CONRefundResponse();
        ttsBegin;
        try
        {
            // ... actual refund logic ...
            response.parmSuccess(true);
            response.parmRefundId("R-12345");
            ttsCommit;
        }
        catch (Exception::Error)
        {
            ttsAbort;
            response.parmSuccess(false);
            response.parmErrorMessage(infolog.text());
        }
        return response;
    }
}
```

### Step 3: AxService

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxService xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONRefundService</Name>
    <Class>CONRefundService</Class>
    <Description>@MyLabels:RefundService</Description>
    <ExternalName>RefundService</ExternalName>
    <ServiceOperations>
        <AxServiceOperation>
            <Name>ProcessRefund</Name>
            <Method>processRefund</Method>
            <SubscriberAccessLevel>
                <Invoke>Allow</Invoke>
            </SubscriberAccessLevel>
        </AxServiceOperation>
    </ServiceOperations>
</AxService>
```

### Step 4: AxServiceGroup

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxServiceGroup xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONRefundServiceGroup</Name>
    <AutoDeploy>Yes</AutoDeploy>
    <Description>@MyLabels:RefundServiceGroup</Description>
    <Services>
        <AxServiceGroupService>
            <Name>RefundService</Name>
            <Service>CONRefundService</Service>
        </AxServiceGroupService>
    </Services>
</AxServiceGroup>
```

### Step 5: Security

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxSecurityPrivilege xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
    <Name>CONRefundServiceInvoke</Name>
    <Label>@MyLabels:RefundServiceInvoke</Label>
    <EntryPoints>
        <AxSecurityEntryPointReference>
            <Name>CONRefundService</Name>
            <Grant>
                <Invoke>Allow</Invoke>
            </Grant>
            <ObjectName>CONRefundService</ObjectName>
            <ObjectType>Service</ObjectType>
        </AxSecurityEntryPointReference>
    </EntryPoints>
</AxSecurityPrivilege>
```

Then add the privilege to a role / duty (see
`dynamics-xpp:xpp-security`).

---

## See also

- `dynamics-xpp:xpp-class` — the X++ class side (service
  implementation, contracts, attributes).
- `dynamics-xpp:xpp-security` — privileges granting Invoke
  permission on services.
- `dynamics-xpp:xpp-data` — for `ttsBegin` / `ttsCommit`
  transaction handling inside operations.
- [MS: Document services overview](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/services-home-page) — broader service framework context.
- [MS: SOAP services concepts](https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/data-entities/services-overview).
