---
name: xpp-business-event
description: Use when something that happens in X++ (a document posted, a status reached, a record created) must be pushed to external subscribers — Power Automate, Service Bus, Event Grid, HTTPS. Covers the business-event class ([BusinessEvents] attribute, BusinessEventsBase), its payload contract (BusinessEventsContract, DataMember + BusinessEventsDataMember), where and how to raise it (send(), transactions, guards), labels and categories, the catalog rebuild and activation, versioning, and the pitfalls read out of the framework source.
---

# Business events

A business event is the platform's way to say "this just happened" to the
outside world: the AOS writes a JSON payload to a commit log inside the
business transaction, and a batch job delivers it to whatever endpoints the
event is activated for (HTTPS, Azure Service Bus, Event Grid, Event Hub, Blob
storage, Power Automate, Dataverse). Endpoint plumbing is configuration;
what you write is **two classes and one raise site**.

Load `dynamics-xpp:xpp-class` for the class mechanics and
`dynamics-xpp:xpp-data-contract` for the contract shape; this skill assumes
both. Everything here was read from the framework source
(`BusinessEventsBase`, `BusinessEventsContract`, `BusinessEventsAttribute`,
`BusinessEventsPublisher`, `BusinessEventsCatalogHelper`,
`FormJsonSerializer`) and from shipped events in ApplicationSuite,
ApplicationFoundation, PersonnelManagement and Leave.

---

## The three pieces

1. **Event class** `XxxBusinessEvent extends BusinessEventsBase`, decorated
   `[BusinessEvents(classStr(XxxBusinessEventContract), 'LabelFile:NameLabel', 'LabelFile:DescriptionLabel', ModuleAxapta::Category)]`.
   Holds the source buffer(s), exposes `public static newFromYyy(buffer)`,
   has a `private void new() {}`, and implements `buildContract()`.
2. **Contract class** `XxxBusinessEventContract extends BusinessEventsContract`,
   decorated `[DataContract]`. Private EDT-typed fields, `newFromYyy(buffer)`
   calling `initialize(buffer)`, and one `parmXxx` per payload field with
   `[DataMember('JsonName'), BusinessEventsDataMember("@Label")]`.
3. **Raise site**: `XxxBusinessEvent::newFromYyy(buffer).send();` at the point
   where the fact has been persisted.

What `send()` does, from the source: it reads the activation configuration
for `<class name> x <current legal entity or all>`; if nothing matches the
event is **dropped silently** (telemetry only) and `buildContract()` is never
called. If active, it builds the contract, stamps the base fields,
serializes to JSON, and inserts a `BusinessEventsCommitLog` row **on the
caller's connection**, so the row commits or aborts with the surrounding
`ttsbegin/ttscommit`. A batch job (or dedicated capacity) later delivers it.
Delivery order is not guaranteed; `ControlNumber` and `EventId` are the
de-duplication keys.

---

## Naming and labels

- Event: `<Prefix><NounPhrase><PastTenseAction>BusinessEvent`
  (`SalesInvoicePostedBusinessEvent`, `VendorInvoiceMatchedBusinessEvent`).
  **The class name is the event id** in the catalog, the configuration and
  the payload (`BusinessEventId`); renaming the class orphans every
  activation and subscription.
- Contract: event name + `Contract`.
- Labels in your model's label file: `<Event>BusinessEventName` (short
  noun phrase shown in the catalog and in Power Automate, e.g. "Invoice is
  created from a sales order"), `<Event>BusinessEventDescription` ("This
  business event is triggered when ..."), and one label per payload field
  (reuse the EDT's label where it fits). See `dynamics-xpp:xpp-labelfile`.
- Attribute label arguments are label ids **without `@`**
  (`'AccountsReceivable:SalesOrderInvoicePostedBusinessEventName'`). The
  attribute prefixes `@` itself when the id resolves. A `'@...'` form
  compiles but stores compile-time-localized text, and a plain string is
  shown verbatim; both lose per-user localization.
- Category = the fourth attribute argument, a `ModuleAxapta` value. There is
  no category attribute. For a custom category, add a value to
  `ModuleAxapta` with an enum extension (`dynamics-xpp:xpp-extension`).

---

## The event class

```xpp
/// <summary>
/// Raised when a sales order invoice has been posted.
/// </summary>
[BusinessEvents(classStr(MyInvoicePostedBusinessEventContract),
    'MyLabels:MyInvoicePostedBusinessEventName',
    'MyLabels:MyInvoicePostedBusinessEventDescription',
    ModuleAxapta::SalesOrder)]
public final class MyInvoicePostedBusinessEvent extends BusinessEventsBase
{
    private CustInvoiceJour custInvoiceJour;
}

    private CustInvoiceJour parmCustInvoiceJour(CustInvoiceJour _custInvoiceJour = custInvoiceJour)
    {
        custInvoiceJour = _custInvoiceJour;
        return custInvoiceJour;
    }

    public static MyInvoicePostedBusinessEvent newFromCustInvoiceJour(CustInvoiceJour _custInvoiceJour)
    {
        MyInvoicePostedBusinessEvent businessEvent = new MyInvoicePostedBusinessEvent();
        businessEvent.parmCustInvoiceJour(_custInvoiceJour);
        return businessEvent;
    }

    private void new()
    {
    }

    [Wrappable(true), Replaceable(true)]
    public BusinessEventsContract buildContract()
    {
        return MyInvoicePostedBusinessEventContract::newFromCustInvoiceJour(custInvoiceJour);
    }

    public Common getContextRecord()
    {
        return custInvoiceJour;
    }
```

Notes, each from the source or a shipped event:

- `buildContract()` is abstract on the base; MS implements it `public` with
  `[Wrappable(true), Replaceable(true)]` on application events so others can
  extend the payload through Chain of Command. It runs lazily and **only
  when the event is active**; never rely on it for side effects.
- `getContextRecord()` returning the main buffer fills
  `ContextRecordSubject` in the payload, which Dataverse and Power Platform
  use to correlate the event with the record. Override it.
- Some MS events use `construct(a, b)` instead of `newFromX` and populate
  the contract through its setters inside `buildContract()`
  (`PurchaseOrderConfirmedBusinessEvent`). Either is fine; keep the
  expensive work in `initialize`/`buildContract`, not in the static factory,
  which always runs.
- The legal entity is captured from `curExt()` at construction. To raise in
  another company wrap construction in `changecompany(le) { ... }`; the
  fluent `withLegalEntity` is internal to the framework.
- Put the attribute on a concrete class. The catalog rebuild instantiates
  every attributed class; an abstract one is skipped with a warning. A
  shared abstract base with attributed leaf classes is the pattern for one
  contract behind several ids (`BatchJobStarted/Finished/Failed`).

---

## The contract

```xpp
[DataContract]
public final class MyInvoicePostedBusinessEventContract extends BusinessEventsContract
{
    private CustInvoiceAccount invoiceAccount;
    private CustInvoiceId invoiceId;
    private SalesIdBase salesId;
    private TransDate invoiceDate;
    private AmountMST invoiceAmount;
    private str invoiceType;
    private LegalEntityDataAreaId legalEntity;
}

    public static MyInvoicePostedBusinessEventContract newFromCustInvoiceJour(CustInvoiceJour _custInvoiceJour)
    {
        var contract = new MyInvoicePostedBusinessEventContract();
        contract.initialize(_custInvoiceJour);
        return contract;
    }

    protected void initialize(CustInvoiceJour _custInvoiceJour)
    {
        invoiceAccount = _custInvoiceJour.InvoiceAccount;
        invoiceId = _custInvoiceJour.InvoiceId;
        salesId = _custInvoiceJour.SalesId;
        invoiceDate = _custInvoiceJour.InvoiceDate;
        invoiceAmount = _custInvoiceJour.InvoiceAmountMST;
        invoiceType = enum2Symbol(enumNum(SalesType), _custInvoiceJour.SalesType);
        legalEntity = _custInvoiceJour.DataAreaId;
        this.parmMajorVersion(1);
    }

    private void new()
    {
    }

    [DataMember('InvoiceAccount'), BusinessEventsDataMember("@AccountsReceivable:InvoiceAccount")]
    public CustInvoiceAccount parmInvoiceAccount(CustInvoiceAccount _invoiceAccount = invoiceAccount)
    {
        invoiceAccount = _invoiceAccount;
        return invoiceAccount;
    }

    [DataMember('InvoiceType'), BusinessEventsDataMember("@SYS7984")]
    public str parmInvoiceType(str _invoiceType = invoiceType)
    {
        invoiceType = _invoiceType;
        return invoiceType;
    }
    // ... one parm per field, same shape
```

Rules that come from `FormJsonSerializer` and the catalog builder:

- **Both attributes on every payload parm.** `DataMember` is what the
  serializer reads; `BusinessEventsDataMember` is what the catalog's field
  grid reads. A parm with only `DataMember` is in the JSON but invisible in
  the catalog. Always give `DataMember` an explicit name; an empty name
  makes the JSON property `parmXxx`.
- **Enums** serialize as the element name. MS still converts them to `str`
  with `enum2Symbol(enumNum(E), value)` so the catalog shows `String` and the
  payload cannot change if the enum does. Do the same.
- **Dates and times** serialize as `"/Date(<ms>)/"`. For ISO-8601, declare
  the parm's return type as `DateTimeIso8601` or `DateTimeIso8601WithMilliseconds`
  (or an EDT extending them); the serializer keys on the parm's declared
  return EDT. Dates inside lists never get ISO treatment.
- **No `container` members** (serialization throws). No `RecId`s in the
  payload; use natural keys. Include `LegalEntity` from `DataAreaId` as MS
  does. Keep it small: targeted facts, never a data export.
- **Nested objects and lists** work: a nested class must itself be
  `[DataContract]` or serialization throws inside your transaction; a `List`
  becomes a JSON array. Initialize list fields in the declaration
  (`private List lines = new List(Types::Class);`) so the catalog's sample
  shows `[]` rather than `null`, and decorate the parm with
  `DataCollection(Types::Class, classStr(Item))`. Know the limits: the
  catalog lists such a field as `System.Object`, and "Download schema" is a
  serialized empty instance, so subscribers never see the nested shape
  there. No shipped MS event uses nested contracts; keep nesting shallow.
- Keep `initialize` **protected** so a Chain-of-Command extension can
  `next initialize(...)` and add fields. Several shipped AppSuite contracts
  made it private; when you extend one of those, wrap the event's
  `buildContract` instead (it is `Wrappable`) and set your extension's parms
  there.
- **Base fields you get for free** in every payload: `BusinessEventId`,
  `BusinessEventLegalEntity`, `ControlNumber`, `EventId` (GUID per instance),
  `EventTime` (`/Date/`), `EventTimeIso8601`, `MajorVersion`, `MinorVersion`,
  `InitiatingUserAADObjectId`, `ContextRecordSubject`,
  `ParentContextRecordSubjects`.
- **Versioning is yours.** Nothing in the framework sets `MajorVersion` /
  `MinorVersion`; every shipped payload says 0.0. Set them in `initialize`
  (as above) and bump: additive field = minor; rename, remove, retype or
  change a `DataMember` name = major (subscribers' parse steps break).

---

## Where to raise it

In order of preference, all from shipped code:

1. **The source-document framework hook** when the document uses it:
   override `getBusinessEvent()` on the document's
   `SourceDocumentStateInProcessXxx` class and return the event for the
   Completed transition; the framework sends it (purchase order confirmed,
   vendor invoice posted, free-text invoice posted).
2. **The posting or business class, after the fact is committed**:
   `SalesInvoiceJournalPostBase` raises `SalesInvoicePostedBusinessEvent`
   right after the journal is written; `LedgerJournalCheckPostCustomerPayment`
   has a `sendBusinessEvents()` that loops the journal lines.
3. **A Chain-of-Command extension of that method** when the class is not
   yours: `next postJournal(); MyEvent::newFromX(x).send();`.
4. **An event handler** on a framework table or delegate:
   `[DataEventHandler(tableStr(WorkflowWorkItemTable), DataEventType::Inserted)]`
   is how Leave raises `LeaveRequestWorkItemAssignedBusinessEvent`: filter to
   your document, guard, construct, `send()` inside `try/catch` so an event
   failure never rolls back the business transaction.
5. **Table `insert` / `update` after `super()`** as the last resort. MS
   discourages table-level capture, and it has a trap: condition on the
   **transition** (`this.orig().Status != X && this.Status == X`), not on the
   current state, or every later save of the record raises the event again.

Always call `send()` inside the transaction that persists the fact; the
commit-log row joins it. Raising outside a transaction autocommits; raising
inside one that later aborts discards the event, which is what you want.

Guard with `BusinessEventsConfigurationReader::isBusinessEventEnabled(classStr(MyEvent))`
only when the raise site itself does extra work (selects, loops, deep
links). `send()` already skips inactive events without building the
contract, so a bare `newFromX(x).send()` costs almost nothing when nothing
is subscribed.

Raising needs no privilege; the commit-log insert runs in the raising
code's context.

---

## Authoring with the tools

Both classes are ordinary `AxClass` objects: `xpp_create_class` with the
declaration and methods as X++ text (`examples/business-event-domain.json`
is the pair for the invoice example). Labels first
(`dynamics-xpp:xpp-labelfile`), then the contract, then the event, then the
raise site (a CoC class via `dynamics-xpp:xpp-extension`, or the method on
your own class). Compile with `xpp_compile`.

A shipped event is the best template: read `SalesInvoicePostedBusinessEvent`
and `SalesInvoicePostedBusinessEventContract` with `xpp_get_class` before
writing yours; read `PurchaseOrderConfirmedBusinessEvent` for the
setter-populated contract plus `getContextRecord`, and
`LeaveWorkflowWorkItemTableEventHandlers` for the event-handler raise.

---

## After the build: catalog, activation, test

1. **Rebuild the catalog.** The catalog table is rebuilt only during a
   database sync or by hand: System administration > Setup > Business
   events > Business events catalog > Manage > **Rebuild business events
   catalog** (about two minutes). Until then a new event is invisible.
   Check the category, name, description and the field list (only parms
   carrying both attributes appear). "Download schema" gives a sample
   payload of an empty instance, not a JSON Schema.
2. **Activate** the event: select it > Activate > legal entity (blank = all)
   > endpoint. Endpoints are created under Setup > Business events >
   Endpoints; Power Automate's "When a Business Event occurs" trigger
   creates its endpoint and activates the event by itself.
3. **Test** by performing the business action, then watch the Active
   events tab, the **Errors** tab (`BusinessEventsExceptionLog`, with Resend
   and Download payload), and confirm the "Business events" batch job is
   running (or dedicated capacity is enabled in Business events parameters)
   on the dev box, otherwise nothing leaves the commit log. For a table-level
   raise, re-save the record and confirm no duplicate event.

---

## Pitfalls, each read out of the source

- **Silent drop when inactive.** `send()` returns normally and writes
  nothing unless an activation row matches the event id and the legal entity
  (or all). The only trace is telemetry.
- **Invisible until the catalog is rebuilt.** Compile, then rebuild.
- **The class name is the event id.** Renaming it is a new event.
- **Attribute on an abstract class** is skipped by the rebuild.
- **Missing `BusinessEventsDataMember`** hides the field from the catalog
  grid while it is still in the payload; **missing `[DataContract]` on a
  nested class** throws at serialization, inside your transaction;
  a **`container`** member throws the same way.
- **`DataMember` with no name** yields a `parmXxx` property.
- **Enums and dates** come out as element names and `/Date(ms)/`; see the
  contract rules above.
- **Version fields are 0.0 forever** unless your contract sets them.
- **`withLegalEntity` and `parmUserId` are internal**; use `changecompany`,
  and accept that the initiating user is the current user.
- **200 MB hard cap** on a payload; the design intent is far smaller.
- **Private `initialize` on a shipped contract** cannot be extended by CoC;
  wrap the event's `buildContract` instead.
- **Catalog security feature** (off by default) hides events from non-admins
  until roles are mapped on the Security tab.

## See also

- `dynamics-xpp:xpp-data-contract` — the contract shape shared with
  SysOperation and custom services.
- `dynamics-xpp:xpp-class`, `dynamics-xpp:xpp-extension` — the classes and
  the CoC raise site.
- `dynamics-xpp:xpp-labelfile` — the name, description and field labels.
- `dynamics-xpp:xpp-enum` — extending `ModuleAxapta` for a custom category.
