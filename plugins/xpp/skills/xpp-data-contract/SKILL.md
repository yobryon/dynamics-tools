---
name: xpp-data-contract
description: Use when writing an X++ data contract — a [DataContract] class whose parm methods carry [DataMember] — for a SysOperation batch/dialog, a business event payload, a custom service operation, or JSON serialization. Covers the parm shape, every attribute that matters (DataMember names, DataCollection / AifCollectionType for lists, SysOperation label/help/order/group/visibility, AifQueryType for packed queries), the Validatable / Initializable interfaces, how dialogs are generated from the contract, and the serialization rules per consumer.
---

# Data contracts

A data contract is a plain X++ class that carries values across a boundary:
into a SysOperation service (and onto its auto-generated dialog), out of the
AOS as a business-event payload, in and out of a custom service, or through
`FormJsonSerializer`. One shape serves all of them; the attributes decide
what each consumer sees.

Load `dynamics-xpp:xpp-class` for the class mechanics. For the consumer
specifics see `dynamics-xpp:xpp-business-event`, `dynamics-xpp:xpp-batch`
(SysOperation) and `dynamics-xpp:xpp-service`.

---

## The shape

```xpp
[DataContract]
public final class MyContract
{
    private TransDate fromDate;
    private CustAccount custAccount;
    private List items = new List(Types::Class);
}

    [DataMember("FromDate")]
    public TransDate parmFromDate(TransDate _fromDate = fromDate)
    {
        fromDate = _fromDate;
        return fromDate;
    }

    [DataMember("Items"), DataCollection(Types::Class, classStr(MyItemContract))]
    public List parmItems(List _items = items)
    {
        items = _items;
        return items;
    }
```

- `[DataContract]` on the class (long form `[DataContractAttribute]`; both
  appear in shipped code). The optional name argument is stored and unused
  by the JSON serializer.
- One `parmXxx` per value, always `public T parmX(T _x = x) { x = _x; return x; }`.
  The parameter default makes the same method a getter and a setter. Use an
  EDT as the type whenever one exists: in a SysOperation dialog the EDT
  supplies the control type, label, lookup and formatting.
- `[DataMember]` marks a parm as part of the contract. Give it an explicit
  name (`"FromDate"` or `identifierStr(FromDate)`); with no name the member
  is named after the method, which is `parmFromDate` in JSON and in a
  service schema. A parm without `DataMember` is invisible to every
  consumer, which is the standard way to carry internal state.
- Private fields, a `private void new() {}` when construction must go
  through a factory, and `public static newFromXxx(...)` / `construct()`
  factories, as everywhere else in X++.
- Lists: declare `List` (or `List<T>`), initialize it in the declaration so
  an empty contract serializes as `[]` instead of `null`, and tell consumers
  the item type with `DataCollection(Types::Class, classStr(Item))` (the
  modern attribute; what `FormJsonSerializer::deserializeObject`, OData and
  the newer frameworks read) or the older pair
  `AifCollectionType('return', Types::Class, classStr(Item))` plus
  `AifCollectionType('_items', Types::Class, classStr(Item))` (custom
  services, SysOperation). Item types can be `Types::String`, `Types::Int64`,
  `Types::Enum` with `enumStr(E)`, or a class.
- Nested objects are contracts too: every nested class needs its own
  `[DataContract]` or JSON serialization throws.

---

## Per consumer

| Consumer | What it reads | Extra attributes and interfaces |
|---|---|---|
| **SysOperation** (`SysOperationServiceController` runs `Service.method(Contract)`; `SysOperationAutomaticUIBuilder` builds the dialog; batch packs the contract) | every `DataMember` parm becomes a dialog field, control type from the EDT or enum | `SysOperationLabel("@Label")`, `SysOperationHelpText("@Label")`, `SysOperationDisplayOrder('2')`, `SysOperationGroup(name, "@Label", order)` on the class + `SysOperationGroupMember(name)` on parms, `SysOperationControlVisibility(false)` to keep a member off the dialog, `SysOperationContractProcessing(classStr(MyUIBuilder))` for custom dialog logic, `SysOperationAlwaysInitialize`, interfaces `SysOperationValidatable` (`boolean validate()`, use `checkFailed`), `SysOperationInitializable` (`void initialize()` for defaults), `SysPackable` when you pack by hand |
| **Business events** | `FormJsonSerializer` writes JSON; the catalog grid reads a second attribute | `BusinessEventsDataMember("@Label")` on every payload parm; base class `BusinessEventsContract`; see `dynamics-xpp:xpp-business-event` |
| **Custom services** (`AxService` + `AxServiceGroup` over a class) | operation parameters and returns are contracts; collections need item types on the OPERATION as well | `AifCollectionType('return', Types::Class, classStr(X))` on the service method returning a `List`; `AifFault::faultList` for errors; see `dynamics-xpp:xpp-service` |
| **JSON in general** (`FormJsonSerializer::serializeClass` / `deserializeObject`, OData actions, Dataverse) | `DataMember` parms, nested `[DataContract]` classes, `DataCollection` for lists | serialization rules below |

### Serialization rules (`FormJsonSerializer`)

- `str`, `int`, `int64`, `real` as expected; `boolean` as `true`/`false`;
  `guid` as a string; a nested class as an object; `List` and arrays as JSON
  arrays; `null` object as `null`.
- **Enums** serialize as the element name (`"Sales"`), not the label and not
  the integer. Business events convert them to `str` with `enum2Symbol`
  first; SysOperation contracts keep typed enums because the dialog needs
  them.
- **`date` and `utcdatetime`** serialize as `"/Date(<ms since 1970>)/"`.
  For ISO-8601 the parm's declared return type must be `DateTimeIso8601` or
  `DateTimeIso8601WithMilliseconds` (or an EDT extending them). Dates inside
  lists are never ISO.
- **`container` is not serializable** (throws). It is fine in SysOperation
  contracts that are only packed, not serialized.
- Members are emitted in alphabetical order of their member names, not
  declaration order.

---

## SysOperation specifics, from shipped contracts

- A packed query travels as a base64 string:
  `[DataMember, AifQueryType('_query', queryStr(MyQuery))] public str parmQuery(str _query = query)`;
  the service does `new Query(SysOperationHelper::base64Decode(contract.parmQuery()))`.
- Defaults belong in `initialize()` (with `SysOperationInitializable`, and
  `SysOperationAlwaysInitialize` when they must be recomputed on every run),
  not in the declaration, so batch-saved parameters still win.
- Validation belongs in `validate()` returning `checkFailed(...)` results;
  the dialog refuses to close while it returns false.
- A contract is usually **not** `final`, because other models extend it
  with Chain of Command; business-event and service contracts are usually
  `final`.
- Members the service needs but the user must not edit: either no
  `DataMember` (then the controller sets them in code), or `DataMember`
  plus `SysOperationControlVisibility(false)`.
- `Hookable(false)` on parms is common in MS code to keep the contract's
  surface out of CoC; use it when the member is an implementation detail.

Shipped references to read with `xpp_get_class`: `BankPaymAdviceContract`
(groups, order, label, validatable, UI builder), `AssetAcquisitionStmtContract`
(long attribute names, hidden members), `BankCurrencyRevalContract`
(initializable, validatable, a list of int64 with `AifCollectionType`),
`CFMPlannedPaymentUpdateContract` with its controller and service (the full
trio, `SysPackable`, container members), `ReconciliationAutomationBulkEvaluationContract`
(lists of contracts with `DataCollection`), `DimensionValueService` and its
`AxService` node (service operations returning collections).

---

## Checklist

1. Name it `<Prefix><Noun>Contract`; one contract per boundary.
2. `[DataContract]`, private EDT-typed fields, `parm` methods with explicit
   `DataMember` names.
3. Lists: initialized in the declaration, item type declared.
4. Add the consumer's attributes: SysOperation label/order/group/visibility,
   or `BusinessEventsDataMember`, or the service's collection attributes.
5. `validate()` / `initialize()` where the dialog needs them.
6. No `container` in anything that is serialized; no `RecId` in anything
   that leaves the box.
7. Read a shipped contract of the same kind first; the schema cannot tell
   you which attributes the consumer actually honors, the source can.

## See also

- `dynamics-xpp:xpp-business-event` — payload contracts.
- `dynamics-xpp:xpp-batch` — SysOperation controllers and services that
  consume contracts.
- `dynamics-xpp:xpp-service` — custom services.
- `dynamics-xpp:xpp-class` — class authoring.
