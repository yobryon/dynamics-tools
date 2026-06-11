# xsdgen-poc

**Parked 2026-05-21.** Not wired into the plugin build; kept for potential future use.

## What this is

A proof-of-concept showing that the F&O MetaModel CLR types
(loaded by the bridge from `Microsoft.Dynamics.AX.Metadata.dll`)
can be reflected into XSD schemas at runtime via
`System.Runtime.Serialization.XsdDataContractExporter`, then
post-processed into a "lean pedagogical" form for any AOT type.

Two pieces:

- **`Program.cs` (+ `xsdgen-poc.csproj`)** — net48 console app that
  loads the MetaModel assembly, runs `XsdDataContractExporter` over
  a named type (e.g. `AxClass`, `AxSecurityPrivilege`), and writes
  one `.xsd` file per emitted schema fragment to a target dir.
- **`lean.py`** — post-processor. Takes the multi-fragment output
  and produces a single "lean" XSD: no-namespace fragment only,
  CDataString refs rewritten to `xs:string`, annotation noise
  stripped, DataContract collection wrapper names cleaned up
  (`KeyedObjectCollectionOfFooNcCATIYq` → `FooCollection`), BFS-
  pruned to a depth limit with subtype closure included for
  validation correctness.

## Why it's parked

The POC was investigated against a real big-form scenario
(`CONSHShipmentTable` — 8,659 lines / 355KB). Even just *reading*
that XML through the MCP/bridge round-trip is impractical
(~24-minute wall time before the test was cancelled). The
underlying tension is that the agent's authoring contract is
"round-trip the entire XML" — and that contract doesn't scale to
big objects regardless of how good the validation schemas are.

The design decision was to pivot toward a surgical patch-style
tool surface for big-object edits, where the agent never holds
the whole envelope in context. Validation in that world is
runtime — the bridge applies the patch against its in-memory
typed object graph and reports any failure with structured
detail.

So the XSD generation work — which would have improved the
validation feedback for the round-trip workflow — became
investment in the wrong layer. It's preserved here because:

1. The algorithm works (proven against AxClass, AxTable,
   AxSecurityPrivilege; lean output strictly more correct +
   pedagogical than MS's curated XSDs).
2. We may want it later for a different consumer (e.g., a
   code-generator that emits typed C# wrappers from the
   MetaModel; tooling that exports schema metadata; etc.).
3. The post-processor in `lean.py` is the non-trivial piece —
   getting back here cold would take 2-3 hours.

## How to run

```powershell
cd plugins/xpp/tools/xsdgen-poc

# Generates one .xsd per schema fragment into the target dir
dotnet run -c Release -- AxClass C:\Path\To\Output

# Then post-process to a single lean XSD
python lean.py C:\Path\To\Output AxClass 3 > AxClass.lean.xsd
```

The csproj imports `BridgeReferences.props` to pick up the
MetaModel DLLs from the user's VS Dynamics extension dir — same
mechanism the production bridge uses. Add `<Private>true</Private>`
overrides via the existing target if `dotnet run` can't resolve
the assemblies (they're not copy-local by default).

## Related history

- `backlog-schema-enrichment` — the original idea this came from
  (enriching the bundled XSDs with enum domains + defaults from
  reflection).
- The patch-tool direction is captured in conversations on the
  v2-rewrite branch; not yet a formal backlog entry.
