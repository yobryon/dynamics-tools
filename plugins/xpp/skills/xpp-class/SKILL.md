---
name: xpp-class
description: Use when authoring or modifying an X++ class in Dynamics 365 F&O — base classes, classes that extend/implement, CoC (Chain of Command) extension classes, attribute decorators, methods, X++ statements. Covers both the AOT XML envelope and the X++ source inside it.
---

# Authoring X++ classes (`AxClass`)

An X++ class is the most common AOT artifact you'll write. The same
file holds the class declaration, its methods, and the X++ source for
each method. The MCP write surface treats the whole `.xml` file as one
unit; you read it in, edit it, and write it back.

Load `dynamics-xpp:xpp-language` if you haven't already — this skill assumes that
foundation.

---

## What an AxClass is, in two parts

Every X++ class has two interlocking halves:

1. **AOT XML envelope** — the on-disk `<ClassName>.xml` file. It carries
   the class name, a handful of properties (`IsObsolete`, `Tags`,
   `SubscriberAccessLevel`), and the X++ source code split into:
   - `SourceCode/Declaration` — the class signature and any
     class-level member variables.
   - `SourceCode/Methods/Method[*]` — one entry per method, each with
     its own `<Name>` and `<Source>` (CDATA-wrapped X++ text).
2. **X++ source text** — the language code inside the `Declaration`
   and `Source` elements. This is what a developer would type into the
   VS X++ editor.

The metadata layer is XML-first: the file IS the class. The Microsoft
metadata service even round-trips between XML and the parsed object
graph via `MetadataSerializer` (for serialize) and the disk provider's
`FromFile` (for deserialize). You can author either by emitting full
XML or, when adding a method only, by using the dedicated method-source
tool (planned; not yet shipped — use full-XML round-trip for now).

On disk, the file lives at:

```
<MetadataStore>\<Model>\AxClass\<ClassName>.xml
```

---

## Authoring through dynamics-xpp

AxClass uses **typed domain tools** — you provide X++ source
directly and the service wraps it in the AOT XML envelope. Three
tools, mirroring CRUD:

| Tool | Purpose |
|---|---|
| `xpp_create_class(request)` | Create a new AxClass from a typed CreateClassRequest. |
| `xpp_get_class(name)` | Read a class as its domain shape (Declaration + Methods). The response can be sent straight back into `xpp_create_class` to clone. |
| `xpp_patch_class(name, patch)` | Apply a partial update. Null fields preserve current state; non-null overwrite. SourceCode replacement replaces both Declaration AND the entire Methods list wholesale — to patch just methods, read with `xpp_get_class`, mutate the methods list in-process, and patch back. |

> **Big class (100s of methods)?** Don't read or rewrite the whole method list.
> Load `dynamics-xpp:xpp-navigation`: `xpp_get_class(outline=true)` lists every
> method by signature (bodies elided), `xpp_find_in_object` locates one by name,
> `xpp_get_class(atPath='/sourceCode/methods/<m>')` reads just its source, and
> `xpp_patch_by_path(op='append'|'set'|'remove', atPath='/sourceCode/methods'…)`
> adds/replaces/drops a single method without resending the rest.

Class-level semantics (`extends`, `abstract`, `final`, `public`/
`private`, `static`, `interface`) live in X++ keywords inside the
Declaration source, not as separate XML properties — same as
writing a class in VS X++.

Method bodies are opaque X++ text. The `name` field on each method
must match the method identifier inside the `source` body. Same
silent-corruption rule for the class name: the request's `name`,
the file name on disk, and the class identifier in `declaration`
must all agree. There is no validator that catches all three
drifting.

The raw `xpp_create_object` / `xpp_update_object` tools remain
available as an escape hatch.

For method-only reads on existing classes there are also
lightweight read tools: `xpp_get_object_methods` for the method
list (signature/return/access only), and `xpp_get_method_source`
for individual bodies — useful when you don't need the whole class.

---

## Minimum viable AxClass

The typed create tool takes a single **`request`** parameter — wrap the object
in `{ "request": { ... } }` (see "the request envelope" in `dynamics-xpp:xpp-language`).
A flat `{ name, sourceCode }` fails to bind.

```jsonc
xpp_create_class({
  "request": {
    "name": "MyHelperClass",
    "sourceCode": {
      "declaration": "\npublic class MyHelperClass\n{\n}\n",
      "methods": [
        {
          "name": "doSomething",
          "source": "\n    public void doSomething(int _value)\n    {\n        info(strFmt(\"Value is %1\", _value));\n    }\n\n"
        }
      ]
    }
  }
})
```

`Declaration` is optional — omit it and the mapper emits a default
`public class <Name> { }`. The on-disk shape is:

```xml
<?xml version="1.0" encoding="utf-8"?>
<AxClass xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
  <Name>MyHelperClass</Name>
  <SourceCode>
    <Declaration><![CDATA[ ... ]]></Declaration>
    <Methods>
      <Method>
        <Name>doSomething</Name>
        <Source><![CDATA[ ... ]]></Source>
      </Method>
    </Methods>
  </SourceCode>
</AxClass>
```

---

## X++ class syntax (the source-text side)

### Class signature

Classes in X++ are optionally derived from a superclass (using
`extends`) and may implement interfaces (using `implements`).
Classes are instantiated using the `new` operator.

```xpp
// Attribute applied to the class.
[SysObsolete("Do not use this class")]
class MyClass extends BaseClass implements MyInterface, AnotherInterface
{
    // Fields are protected by default. The initializer is optional.
    // Fields can be static or instance.
    protected int MyField = 10;

    // Methods can be public, private, protected, or internal.
    // They can be either static or instance methods.
    // The last parameters can have default values.
    public static int myMethod(real _parm = 0.0) { return 1; }

    // This is the constructor, called 'new'.
    public void new() {}

    // This is the static constructor, called before any other methods.
    // No access modifier may be provided, and it may not be called explicitly.
    static void TypeNew() {}
}
```

Key points:

- **All methods are virtual.** No `virtual` keyword needed; no `final`
  needed to seal individual methods (you can mark the whole class
  `final` to prevent inheritance).
- **Methods can be public, private, protected, or internal**, with the
  same semantics as C#.
- **Methods can be adorned with attributes** (e.g. `[SysObsolete]`,
  `[Hookable]`).
- **`new()` is the instance constructor**, lowercase by convention.
- **`TypeNew()` is the static constructor.** No access modifier; the
  runtime calls it once before any instance is constructed; you cannot
  call it explicitly.

### Modifiers in the modern style

The contemporary D365 default (matches what VS templates emit) is:

```xpp
internal final class MyHelperClass
{
}
```

`internal` restricts visibility to the model; `final` prevents
inheritance. Use this baseline unless you have a reason to deviate
(public API, designed for inheritance).

### Refactor-safe identifier tokens

X++ provides built-in functions that yield identifier strings checked
at compile time. Prefer these over raw string literals when referring
to AOT artifacts:

- `classStr(MyClass)` → `"MyClass"`
- `methodStr(MyClass, myMethod)` → `"myMethod"`
- `tableStr(CustTable)` → `"CustTable"`
- `tableFieldStr(CustTable, AccountNum)` → `"AccountNum"`
- `enumStr(NoYes)` → `"NoYes"`
- `enumValueStr(NoYes::Yes)` → `"Yes"`
- `edtStr(CustAccount)` → `"CustAccount"`
- `formStr(CustTable)` → `"CustTable"`
- `formControlStr(CustTable, AccountNum)` → `"AccountNum"`

If the referenced artifact gets renamed and you used the strongly-typed
token, the compile fails — exactly what you want. Using `"CustTable"`
as a raw string silently breaks.

---

## X++ statements

| Statement | Explanation | Example |
|---|---|---|
| Assignment | Assigns a value to a variable. | `int a; a = 10;` |
| `if` | Conditional. Block executes if condition is true. | `if (a > 5) { info("greater"); }` |
| `if`/`else` | Conditional with alternate branch. | `if (a > 5) { info("greater"); } else { info("not"); }` |
| `while` | Loops while condition is true. | `while (a < 15) { a++; }` |
| `do`/`while` | Loops at least once, then checks. | `do { a++; } while (a < 15);` |
| `for` | Counted loop. | `for (int i = 1; i <= 10; i++) { info(int2str(i)); }` |
| `switch` | Multi-way branch. | `switch (a) { case 1: info("One"); break; case 2: info("Two"); break; default: info("Other"); }` |
| `select` | Retrieves one or more records from the database. | `select * from custTable where custTable.AccountNum == "0001";` |
| `while select` | Iterates over query results. | `while select custTable where custTable.Blocked == NoYes::No { /* ... */ }` |
| `insert_recordset` | Bulk insert from a select. | `insert_recordset grownups (Name, Age) select Name, Age from custTable where custTable.Age > 18;` |
| `update_recordset` | Bulk update. | `update_recordset custTable setting Blocked = NoYes::No where custTable.AccountNum == "0001";` |
| `delete_from` | Bulk delete. | `delete_from custTable where custTable.AccountNum == "0001";` |
| Compound | Block of statements. | `{ int x = 0; x = x + 1; info(int2str(x)); }` |
| `break` | Exits a loop or `switch`. | `while (true) { if (a == 5) break; }` |
| `continue` | Skips to next loop iteration. | `for (int i = 0; i < 10; i++) { if (i % 2 == 0) continue; info(int2str(i)); }` |
| `return` | Exits a method, optionally with a value. | `return value;` |
| `throw` | Raises an exception. | `throw error("An error occurred");` |
| `try`/`catch` | Handles exceptions. | `try { /* may throw */ } catch (Exception::Error) { /* recover */ }` |
| `ttsBegin` / `ttsCommit` / `ttsAbort` | Transaction control around DB writes. | `ttsBegin; record.insert(); ttsCommit;` |

### Database statement notes

- `select` defaults to **first matching record**. To get all, use
  `while select`. To force the first explicitly, `select firstOnly ...`.
- `forUpdate` is required before any `.update()` or modification of a
  record: `select forUpdate custTable where ...`.
- `crossCompany` adds cross-`DataAreaId` scope to a query.
- `_recordset` variants (`insert_recordset`, `update_recordset`,
  `delete_from`) skip row-level business logic (no per-record
  `validateWrite`, no `doUpdate` events). They're fast but bypass
  table-level methods — use only when the speedup is justified.
- All writes that mutate state must be inside `ttsBegin`/`ttsCommit`.
  Throwing inside the transaction triggers a rollback when execution
  unwinds out of the scope.

### Exception handling

```xpp
try
{
    // ... code that may throw ...
}
catch (Exception::Error)
{
    // Generic application error
}
catch (Exception::Deadlock)
{
    // Deadlock - retryable
}
catch (Exception::UpdateConflict)
{
    // Optimistic-concurrency conflict
}
```

X++ exceptions are `Exception::*` enum values rather than thrown class
instances. The runtime sets `errorMessage()` and friends as side
channels.

---

## Extension classes (Chain of Command)

Microsoft application models are **sealed** as of release 8.0 — you
cannot directly modify a shipped class. Instead, you augment classes
through **extension classes** that use the Chain of Command (CoC)
mechanism. Extensions are the F&O analog of partial classes + method
hooks.

**For naming**: extension class names MUST end with `_Extension` (10
chars, BP-enforced). Recommended shape is
`<objectPrefix><Target>_Extension` (e.g., `conSomeProcessor_Extension`)
where `objectPrefix` comes from your project's
`.dynamics-xpp/config.json`. The same class-style extension pattern
applies when augmenting code on forms, datasources, controls, or
tables — see `dynamics-xpp:xpp-project` for the full naming conventions and the
type-info-in-name rule for non-class targets.

### Requirements

- Must be marked `final`.
- Must use `[ExtensionOf(classStr(MyClass))]` attribute.
- Should have `_Extension` suffix in the name (convention, not
  enforced).
- Cannot override methods from the base class with a new implementation
  — CoC instead "wraps" them.

### Example

```xpp
[ExtensionOf(classStr(MyClass))]
final class MyClass_Extension
{
    public int state;                // Instance state (yes — extensions CAN add state)
    public static int staticState;   // Static state

    private void new() {}            // Instance constructor — no parameters allowed
                                     // (the base class's `new` is the only public one)

    static void typeNew()            // Static constructor
    {
        staticState = 77;
    }

    public int extensionMethod(int _arg) // Instance method
    {
        // Can access public/protected members of MyClass
        return _arg;
    }

    public static real celsiusToFahrenheit(real _celsius) // Static method
    {
        return (_celsius * 9.0 / 5.0) + 32.0;
    }
}
```

### Usage

```xpp
MyClass c = new MyClass();
c.state = 12;                                  // Access extension state
print c.extensionMethod(32);                   // Call extension method
var temp = MyClass::celsiusToFahrenheit(20.0); // Call static extension method
```

### Important properties of extensions

- **Only public methods become part of the effective class.** Private
  extension methods aren't reachable from outside the extension.
- **Extension constructors are called automatically by the runtime.**
  No `new` call into the extension is needed (or allowed).
- **Classes inheriting from an augmented class also inherit extension
  methods.** Adding to `CustTable` adds for `CustTable`-derived too.
- **Static extension methods on the `Global` class become available as
  free functions** (no class prefix needed). This is how F&O's "global
  function" set is extended.

### Wrapping a method with `next`

The most common CoC pattern is "wrap" — your extension method has the
same signature as the base, and calls `next someMethod(args)` to
invoke the base implementation. You can run code before, after, or
both:

```xpp
[ExtensionOf(classStr(SomeProcessor))]
final class SomeProcessor_Extension
{
    protected boolean overrideFindOrCreateCustomer()
    {
        // Pre-call work (e.g. pre-populating a field the base reads)
        next overrideFindOrCreateCustomer();
        // Post-call work (e.g. logging, follow-up updates)
        return true;
    }
}
```

The `next` call delegates to the base class's implementation OR to the
next extension in the chain (if multiple extensions exist). Order
across multiple extensions is non-deterministic — never rely on a
specific ordering.

#### Sequencing gotcha

The position of `next` matters and the canonical patterns are:

- **"Wrap and adjust": work before AND after `next`** — your most
  common shape. Pre-stage state, let base run, fix up afterward.
- **"Set then skip base"**: when the base is a no-op or override-gated.
  Common in F&O `override*` methods where the base just returns false
  and your job is to fully implement.
- **"Let base run, then override"**: assign after `next` so the base's
  value is overwritten. Risky if other extensions read the result in
  between.

A frequent bug pattern: an extension assigns a value BEFORE `next`,
expecting the base to honor it, but the base actually overwrites with
its own logic (e.g. a number-sequence allocation). Verify by reading
the base method (`xpp_get_method_source`) when sequencing matters.

For deeper extension patterns (CoC over tables, forms, and EDTs;
delegate/event-handler classes; how `[ExtensionOf]` interacts with
inheritance), see `dynamics-xpp:xpp-extension`.

---

## Form-control references inside form classes

When the class is a `[Form]` class (extends `FormRun`), don't
re-declare controls or datasources that already exist in the form's
XML — they're imported into the class scope automatically.

```xpp
public void savePlane()
{
    // DON'T redeclare: str Question1;
    // DO use directly: Question1.text()

    Plane plane;
    ttsBegin;
    plane.Id = guid2str(newGuid());
    plane.Description = strFmt("Q1: %1", Question1.text());
    plane.doInsert();
    ttsCommit;
    info("Plane saved.");
    element.closeOk();
}
```

A datasource named `MyDataSource` is reachable as `MyDataSource` (the
buffer) and `MyDataSource_DS` (the `FormDataSource` runtime object).
Controls are reachable by their name property.

`element` is a reserved identifier inside a `FormRun`-derived class
referring to the form runtime. `element.args()` returns the launching
`Args` object; `element.closeOk()`, `element.closeCancel()`,
`element.closeStateInteresting()` are the canonical close calls.

---

## Best practices

> After authoring or modifying a class, run
> `xpp_bp_check(scope="changeset")` for fast feedback against
> F&O's 184 BP rules. For class-specific rules likely to fire,
> see `plugins/xpp/docs/bp-rules-reference.md` (Code Style +
> Maintainability assemblies). Run `xpp_compile` at meaningful
> checkpoints — it catches X++-compiler hazards BP can't, like
> `NotCoCProtectedMethodInExtensionClass` (visibility violations
> on CoC extensions), `TypeConversionLosesRange`, and missing
> `next` calls on chained methods.

### Do not use macros — use typed constants

`#define` / `#macro` are legacy. Replace with strongly-typed constants:

```xpp
// Instead of: #define.MaxCustomerCredit(1000)
public static class Constants
{
    public const int MaxCustomerCredit = 1000;
    public const str SalesTableName = "SalesTable";
}
```

When refactoring code that uses macros:

1. Extract all macro definitions.
2. Create equivalent typed constants in a Constants class.
3. Update references where possible.
4. If references can't be updated cleanly, still output the constants
   so a human can finish the migration.

### Use EDTs instead of primitives for cross-AOT values

When method parameters or table fields would naturally be a domain
value (an account number, a date interval, a quantity in a unit), use
the matching EDT (Extended Data Type) rather than a primitive:

- For methods: if a method returns or accepts `str`, `int`, `real`,
  `Date`, check whether an EDT exists for that domain concept and use
  it instead.
- For table fields: prefer `<ExtendedDataType>CustAccount</ExtendedDataType>`
  over raw `<Type>String</Type>` + `<StringSize>...</StringSize>`. The
  EDT carries the label, help text, length, relations, and lookup form
  — you inherit all of them.

Use `xpp_find_object` with `axType=AxEdt` to locate existing EDTs
before defining new ones. See `dynamics-xpp:xpp-edt` for EDT authoring.

### Infolog safety in catch blocks

Never expose sensitive internals — file paths, internal method names,
stack traces, raw exception text — to the end-user infolog:

```xpp
// BAD - leaks internal information to the user
catch (Exception::Error)
{
    info(strFmt("Error: %1", errorMessage()));
}

// GOOD - generic user-safe message, log the detail server-side
catch (Exception::Error)
{
    infolog.clear(infologLine());
    error("An unexpected error occurred. Please contact support.");
}
```

If you need the detail for diagnosis, write to an event log or a
dedicated tracing table, not to the user-facing infolog.

### Concurrent delete protection on tables

Always implement `shouldThrowExceptionOnZeroDelete()` returning `true`
on table classes that support deletes:

```xpp
public boolean shouldThrowExceptionOnZeroDelete()
{
    return true;
}
```

This causes a concurrent delete (two processes both deciding to delete
the same row) to raise an exception instead of silently succeeding on
one and silently no-op'ing on the other. Default `false` is data-loss
prone; only set `false` with a documented business reason.

---

## Gotchas

- **Identifiers are case-insensitive.** `nextMethodName()` and
  `NextMethodName()` resolve to the same method. Cosmetic case
  inconsistencies in CoC `next` calls compile fine. Don't waste time
  "fixing" pure-case typos.
- **Method `Name` element vs. method name in `<Source>` CDATA.** Both
  must match or you silently corrupt the AOT.
- **CDATA-wrap X++ source.** The parser tolerates entity-encoded `<`
  and `&` but the rest of the tool chain (VS designer, BP, MS
  serializer) assumes CDATA.
- **`Source` indentation matters cosmetically, not semantically.** The
  X++ compiler is whitespace-tolerant, but committed source convention
  is 4-space indent inside the method body. Match the existing style
  in the file you're editing.
- **`element` is a reserved identifier inside form classes** — don't
  use as a variable name in form methods.
- **`new()` is lowercase, `TypeNew()` is uppercase.** Legacy
  inconsistency baked into the language.

---

## Things the XSD can't tell you

The `xpp://schema/AxClass` XSD is the formal grammar but won't catch:

- **X++ syntax errors inside `<Source>` CDATA.** The XSD treats the
  body as opaque text. Bad syntax compiles when you try to build in VS,
  not at metadata-write time.
- **Type references to non-existent objects.** Referring to a class
  that doesn't exist in your model (or its references) succeeds at
  write time and fails at compile.
- **BP rule violations.** "Method names should start with lowercase",
  "Public method should have HelpText", "Class should have
  SubscriberAccessLevel" — these are BP-check warnings/errors, not
  schema violations.
- **Sealed-class violation.** Trying to modify a Microsoft-shipped
  class (rather than extending it) passes the metadata-write but
  fails the AOS build.

---

## See also

- `dynamics-xpp:xpp-language` — the language foundations
- `dynamics-xpp:xpp-extension` — deeper coverage of CoC, table extensions, form
  extensions, delegates and event handlers
- `dynamics-xpp:xpp-table` — for table authoring (the other big surface)
- `dynamics-xpp:xpp-form` — for form classes specifically
- `xpp://schema/AxClass` — authoritative XSD (used by the
  `xpp_create_object` escape hatch)

---

## Note on the typed authoring surface

AxClass is the **fourth AOT type** on the typed-authoring layer
(after AxEnum, AxEdt, AxTable). The agent-facing API lives in
`Xpp.Service.Domain.Classes.CreateClassRequest`. The XML root is
small — just `Name + SourceCode + Tags + IsObsolete` — because the
class semantics encode in X++ keywords inside the Declaration
source rather than as separate metadata elements. The
`AdvancedClassOptions` block (IsAbstract, IsFinal, IsInterface,
Extends, RunOn, etc.) exists for completeness but MS-shipped
classes universally don't set these at the XML root.

The mapper preserves method bodies as opaque text — no X++
parsing on either the write or read path. The `name` of each
method must match the method identifier inside `source`; the
mapper trusts the caller on that pairing.
