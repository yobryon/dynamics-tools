# Domain-model authoring layer

Status: v0.1 design, 2026-05-21.

## Why

Full-AOT-XML round-trip is workable for small objects but breaks
down for big ones (CONSHShipmentTable: 8,659 lines, ~24-min wall
to even read). The agent's UX for typical authoring should be:

```
xpp_create_enum({
  name: "ChApprovalState",
  values: [{ name: "Pending" }, { name: "Approved" }, { name: "Rejected" }]
})
```

— not "construct 80 lines of XML with the right namespace
declarations." The MCP tool's parameter shape becomes the
authoring contract; tool descriptions self-document the
surface; defaults eliminate boilerplate.

## Architecture

```
XppService.Mcp                    XppService                    XppMetadataBridge
  |                                  |                              |
  | tool(CreateEnumRequest)          |                              |
  | serialize to JSON string         |                              |
  +----CreateDomainObject(           |                              |
       axType, model, domainJson) -->|                              |
                                     | parse JSON -> CreateEnumRequest
                                     | mapper.ToXml(request)
                                     +----UpdateObjectAsync(axType, |
                                          model, xml) ------------->|
                                                                    | FromFile + save
```

### Three layers

1. **`XppService.Domain` (net9, NEW)** — pure C# records, no
   logic. The agent-facing API. Every property carries a
   `[Description]` attribute that the LLM reads through the MCP
   tool surface.

2. **`XppService.Mcp`** — tool methods accept domain records as
   parameters. Implementation serializes to JSON and calls one
   of three generic gRPC RPCs.

3. **`XppService`** — gRPC handlers parse JSON, dispatch by
   ax_type to a typed mapper (`IDomainMapper<TCreate>`), produce
   AOT XML, ship to the existing bridge write surface.

The bridge is unchanged. It still receives XML via the existing
`CreateObject` / `UpdateObject` RPCs. Domain-layer additive,
non-breaking.

### Three generic gRPC RPCs cover every domain type

```proto
rpc CreateDomainObject(CreateDomainObjectRequest) returns (WriteObjectResponse);
rpc PatchDomainObject(PatchDomainObjectRequest) returns (WriteObjectResponse);
rpc GetDomainObject(GetDomainObjectRequest) returns (GetDomainObjectResponse);

message CreateDomainObjectRequest {
  string ax_type = 1;       // "AxEnum"
  string domain_json = 2;   // serialized C# domain record
}
message PatchDomainObjectRequest {
  string ax_type = 1;
  string name = 2;          // identifier of the object to patch
  string patch_json = 3;
}
message GetDomainObjectRequest {
  string ax_type = 1;
  string name = 2;
}
message GetDomainObjectResponse {
  string ax_type = 1;
  string name = 2;
  string domain_json = 3;   // serialized C# domain record
}
```

Adding a new domain type is a Domain-types-plus-mapper change —
no proto changes.

### Mapper interface

```csharp
public interface IDomainMapper
{
    string AxType { get; }                         // "AxEnum"
    string ToAotXml(string domainJson);            // for Create/Patch
    string FromAotXml(string xml);                 // for Get
}
```

Service holds a `Dictionary<string, IDomainMapper>` keyed by
ax_type. Dispatch in the RPC handler.

### JSON conventions

System.Text.Json with:
- `PropertyNamingPolicy.CamelCase` — agent sees `name`, `isExtensible`
- `JsonStringEnumConverter` — enums as strings, not integers
- `DefaultIgnoreCondition.WhenWritingNull` — omit nulls on Get
  responses (less noise in agent context)

### Why JSON-on-the-wire instead of typed proto messages

Either approach works. JSON-on-the-wire trades a tiny perf cost
(serialize / deserialize at the gRPC boundary) for substantially
less maintenance:
- One proto change per domain type avoided.
- Domain shapes evolve in C# only; no regen friction.
- Three RPCs cover N domain types forever.

For our scale (low call rate, small payloads), perf is a
non-factor. The maintenance win dominates.

## Patch design — deferred to second iteration

Two viable shapes:

**(a) Op-based** — caller sends `[{op: "set", path: "/label", value: "..."}, {op: "add", path: "/values", value: {...}}]`.
Precise; matches RFC 6902.

**(b) Merge-patch** — caller sends a partial domain record with
nulls meaning "leave unchanged"; mapper diffs against current
state. Simpler for the agent ("describe what you want it to
look like").

I lean toward (b) — let agents think in terms of desired state,
not diff operations. But this is a real decision I want to lock
after we've shipped Create + Get for AxEnum and lived with the
shape for a beat.

## Backward compatibility

The existing `xpp_create_object` / `xpp_update_object` tools
stay. Agents authoring types we don't yet have domain coverage
for use the XML round-trip. Once domain coverage exists for a
type, the per-type tools (`xpp_create_enum`, etc.) become the
recommended path — but the XML fallback never goes away.

## Roadmap

1. Ship infrastructure + AxEnum (Create + Get).
2. Ship AxEdt (Create + Get) — validates polymorphism shape.
3. Lock the Patch design based on lessons.
4. Backfill Patch for AxEnum + AxEdt.
5. Iterate through the rest of the prioritized type list,
   one Create + Get + Patch per type.
