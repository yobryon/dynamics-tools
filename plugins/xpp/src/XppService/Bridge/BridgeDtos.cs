namespace Xpp.Service.Bridge;

// =============================================================================
// Typed result records for bridge RPCs. The wire format is JSON; these records
// are what BridgeClient deserializes responses into so the rest of the service
// (indexer, search, etc.) never touches a JsonNode.
//
// Property names match the bridge's emitted camelCase. System.Text.Json with
// PropertyNamingPolicy.CamelCase handles the binding.
// =============================================================================

public sealed record BridgePing(
    string Echo,
    string ServerTime,
    string BridgeVersion);

public sealed record BridgeModel(
    string Name,
    string DisplayName,
    string Publisher,
    string Version,
    string Layer,
    bool IsCustom,
    bool IsBinary,
    IReadOnlyList<string> Dependencies);

/// <summary>
/// One (object name, source-tag) pair from a <c>listObjects</c> response.
/// Source is "disk" when at least one disk provider sees the object,
/// "runtime" when only the runtime (compiled-DLL) provider does.
/// </summary>
public sealed record BridgeObjectListEntry(
    string Name,
    string Source);

public sealed record BridgeMethodInfo(
    string Name,
    string Source,
    string? Signature,
    bool IsStatic,
    string? AccessLevel,
    string? ReturnType);

public sealed record BridgeReferenceEdge(
    string TargetName,
    string? TargetType,
    string Kind,
    string? Context);

/// <summary>
/// A field-level structural reference: source object's member (e.g. control
/// name, range name) references a specific field on a target table. Schema
/// v2; see Storage/Schema/002-field-refs.sql.
/// </summary>
public sealed record BridgeFieldReferenceEdge(
    string? SourceMember,
    string TargetTable,
    string TargetField,
    string Kind,
    string? Context);

/// <summary>
/// A label reference: source object's member carries a label reference
/// (@LabelFile:LabelKey or @LabelKey). Schema v3; see
/// Storage/Schema/003-label-refs.sql.
/// </summary>
public sealed record BridgeLabelReferenceEdge(
    string? SourceMember,
    string LabelFile,
    string LabelKey,
    string Kind,
    string? Context);

/// <summary>
/// A single (key, value, language) label entry harvested from an
/// AxLabelFile. Description is the optional translator-comment field.
/// </summary>
public sealed record BridgeLabelEntry(
    string Key,
    string Value,
    string Language,
    string? Description);

/// <summary>
/// Combined response for the indexer's hot path: methods + structural
/// refs from one bridge round-trip. Returned by <c>getObjectFull</c>.
/// <c>Source</c> tells us which provider satisfied the read ("disk" or
/// "runtime"); <c>BinaryModule</c> is true when source == "runtime".
/// </summary>
public sealed record BridgeObjectFull(
    IReadOnlyList<BridgeMethodInfo> Methods,
    IReadOnlyList<BridgeReferenceEdge> References,
    IReadOnlyList<BridgeFieldReferenceEdge>? FieldReferences,
    IReadOnlyList<BridgeLabelReferenceEdge>? LabelReferences,
    string Source,
    bool BinaryModule);

/// <summary>
/// Per-item shape inside a getObjectsFull response. `Error` is non-null
/// when the bridge couldn't read the object (not found, malformed
/// metadata, etc.); in that case methods/references are empty arrays.
/// </summary>
public sealed record BridgeObjectsFullItem(
    string Model,
    string AxType,
    string Name,
    IReadOnlyList<BridgeMethodInfo>? Methods,
    IReadOnlyList<BridgeReferenceEdge>? References,
    IReadOnlyList<BridgeFieldReferenceEdge>? FieldReferences,
    IReadOnlyList<BridgeLabelReferenceEdge>? LabelReferences,
    IReadOnlyList<BridgeLabelEntry>? Labels,
    string? Source,
    bool BinaryModule,
    string? Error);

// =============================================================================
// Label CRUD DTOs — mirror the JSON payloads the labelSearch / labelRead /
// labelAdd / labelUpdate / labelDelete handlers emit.
// =============================================================================

public sealed record BridgeLabelSearchResult(
    string LabelFileId,
    string Language,
    string ResourcePath,
    IReadOnlyList<BridgeLabelSearchMatch>? Matches);

public sealed record BridgeLabelSearchMatch(
    string LabelFileId,
    string Language,
    string LabelId,
    string Value,
    string? Description,
    int Line,
    string MatchedIn);

public sealed record BridgeLabelReadResult(
    string LabelFileId,
    string Language,
    string LabelId,
    string Value,
    string? Description,
    int Line,
    string ResourcePath);

public sealed record BridgeLabelMutationResult(
    string LabelFileId,
    string Language,
    string ResourcePath,
    int Affected);

public sealed record BridgeLabelMutationInput(
    string LabelId,
    string Value,
    string? Description);
