# Class Creation (AxClass)

**When to use:** Creating a new X++ class (business logic, event-handler
container, helper). Adding methods to existing classes also goes through the
same `AddMethod` modification.

Last verified against D365 F&O docs: 2026-05-18

## Create the class shell

```json
{
  "objectName": "AcmeProjectStatusManager",
  "objectType": "AxClass",
  "layer": "usr",
  "properties": {
    "IsStatic": "No",
    "IsAbstract": "No",
    "IsFinal": "No",
    "Extends": ""
  }
}
```

Common class-level properties:

| Property | Values | Notes |
| --- | --- | --- |
| `IsStatic` | `"Yes"` / `"No"` | Set `"Yes"` for utility/helper holders. |
| `IsAbstract` | `"Yes"` / `"No"` | |
| `IsFinal` | `"Yes"` / `"No"` | `"Yes"` seals the class against extension. |
| `Extends` | base class name | E.g. `"RunBaseBatch"` for batch jobs. Leave empty for a root class. |
| `RunOn` | `"Called"`, `"Server"`, `"Client"` | Default `"Called"` — runs where called. New code rarely overrides this. |
| `IsRunbaseBatch` | `"Yes"` / `"No"` | Only relevant if `Extends = "RunBaseBatch"`. |

## AddMethod — the modification call

```json
{
  "objectType": "AxClass",
  "objectName": "AcmeProjectStatusManager",
  "modifications": [
    {
      "methodName": "AddMethod",
      "parameters": {
        "concreteType": "AxMethod",
        "Name": "isStatusActive",
        "Source": "public boolean isStatusActive(AcmeProjectStatus _status)\n{\n    return _status.RecId != 0 && !_status.IsBlocked;\n}"
      }
    }
  ]
}
```

The `Source` parameter is the **entire** X++ method including its signature,
return type, modifiers, and body. The metadata layer parses it; you do not pass
`returnType`, `isStatic`, or `parameters` separately on `AxMethod`.

> The README's older `addMethod` example with `returnType` / `source` (lowercase
> property names) is **stale**. Use `Source` (PascalCase) and embed the signature
> inside it. The required parameter is `Name` (matches the X++ method name).

### Static method

```json
{
  "concreteType": "AxMethod",
  "Name": "find",
  "Source": "public static AcmeProjectStatus find(AcmeProjectStatusCode _code, boolean _forUpdate = false)\n{\n    AcmeProjectStatus status;\n    status.selectForUpdate(_forUpdate);\n    select firstonly status where status.StatusCode == _code;\n    return status;\n}"
}
```

The `static` keyword in the signature drives the metadata flag; no separate
`IsStatic` property on `AxMethod` is needed.

### Event handler (the modern integration point)

For F&O, **prefer event handlers over over-layering**. They live as static
public methods on any class and bind via the `SubscribesTo` attribute:

```json
{
  "concreteType": "AxMethod",
  "Name": "CustTable_onValidatedWrite",
  "Source": "[DataEventHandler(tableStr(CustTable), DataEventType::ValidatedWrite)]\npublic static void CustTable_onValidatedWrite(Common _sender, DataEventArgs _e)\n{\n    CustTable custTable = _sender as CustTable;\n    // ...\n}"
}
```

For delegate subscription:

```json
{
  "concreteType": "AxMethod",
  "Name": "FMRentalCheckoutProcessor_onFinalized",
  "Source": "[SubscribesTo(classStr(FMRentalCheckoutProcessor), delegateStr(FMRentalCheckoutProcessor, RentalTransactionAboutTobeFinalizedEvent))]\npublic static void FMRentalCheckoutProcessor_onFinalized(FMRental _rental, Struct _confirmation)\n{\n    // handler logic\n}"
}
```

For form / data-source / control event handlers, use
`FormEventHandler`, `FormDataSourceEventHandler`, `FormControlEventHandler` —
all in `Microsoft.Dynamics.AX.Application` /
`Microsoft.Dynamics.AX.Foundation`. The MCP tool does not validate the
attribute name; the X++ compiler does. See
<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-events#event-handlers-and-pre-post-methods>.

### Class declaration (the `classDeclaration` method)

The class header itself is a "method" named `classDeclaration` whose `Source`
contains the `class` line and member variable declarations. The shell created
by `create_xpp_object` includes a minimal one; replace it with a new
`AddMethod` (the factory treats `classDeclaration` as upsert).

```json
{
  "concreteType": "AxMethod",
  "Name": "classDeclaration",
  "Source": "public class AcmeProjectStatusManager extends RunBaseBatch\n{\n    AcmeProjectStatusCode statusCode;\n}"
}
```

## AX 2012 vs F&O divergence points

- **Method-signature-changing over-layering is deprecated**
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/migrate-overlayer-extension>).
  In AX 2012 you could redefine `validateWrite` on `CustTable` with a different
  signature; in F&O you must extend via class extension + Chain-of-Command
  (`[ExtensionOf(tableStr(CustTable))]`) or via event handlers.
- **`Pre`/`Post` method attributes (`PreHandlerFor` / `PostHandlerFor`) are
  legacy.** Microsoft notes they "can easily break as the result of added or
  removed parameters" and recommends delegates + `SubscribesTo`
  (<https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-events#event-handlers-and-pre-post-methods>).
  They still compile, but for new code use delegates.
- **Class extensions use Chain-of-Command (`next foo()`)** — this is the
  preferred way to "wrap" a Microsoft method without over-layering it. Model
  the extension class with `[ExtensionOf(classStr(...))]`.
- **`RunBase` is largely superseded by `SysOperation`** for batch-friendly
  business operations. New batch jobs should target the `SysOperation`
  framework (controller / data-contract / service-class triad) rather than
  extending `RunBaseBatch`. `RunBaseBatch` still works.

## Pitfalls

- `Name` on the method **must** match the X++ method name inside `Source`. Mismatches
  produce a method whose AOT name and source disagree.
- The `Source` payload includes a *single* method body. Do not pack multiple
  methods into one `AddMethod` call; batch them as multiple modifications
  instead.
- Newlines inside JSON `Source` must be `\n`, not literal line breaks. (JSON
  doesn't allow raw newlines in strings.)
- Comments must be inside the `Source` body, not separate parameters.

## Sources

- `J:/Tools/dynamics-tools/README.md`
- `J:/Tools/dynamics-tools/ms-api-server/Handlers/ExecuteObjectModificationHandler.cs`
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-events>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/customization-overlayering-extensions>
- <https://learn.microsoft.com/dynamics365/fin-ops-core/dev-itpro/extensibility/migrate-overlayer-extension>
