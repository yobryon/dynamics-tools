# Predefined X++ classes

The X++ runtime ships with several built-in classes that show up in
nearly any non-trivial AOT artifact. This file is a quick reference;
the per-AOT-type skills (`dynamics-xpp:xpp-table`, `dynamics-xpp:xpp-form`, ...) go deeper for
the artifact types they cover.

## `Common` — base class for all tables

Tables represent tabular objects in the business SQL database. In X++
they are classes that derive from `Common`:

```xpp
public class MyTable extends Common
{
    public void myTableMethod()
    {
        MyTable t; // t is a record variable; it gets populated, not new'd
    }
}
```

Table classes may contain methods, but **the user cannot define any
instance or static state for the table class.** Fields are defined in
the AOT XML (see `dynamics-xpp:xpp-table` for how), not as X++ class members.

Tables are not instantiated like other classes — records are created
when needed via inserts, or loaded via `select` / `find`. Each
populated value of a `MyTable t;` variable represents one record in
the database.

All tables have some predefined fields:

- `RecId` — `int64` unique identifier (auto-generated).
- `TableId` — int identifying the table type.
- `Partition` — int64 partition key.
- `DataAreaId` — F&O "company" discriminator (only on per-company
  tables; absent on `SaveDataPerCompany=No` / shared tables).
- `CreatedDateTime`, `ModifiedDateTime`, `CreatedBy`, `ModifiedBy` —
  audit fields (presence governed by table properties).

To check whether a tabular variable holds a record, test `RecId != 0`.

## `FormRun` — base class for all forms

Forms represent a page in which a user can provide information into
controls. They are classes that extend `FormRun` and are adorned with
the `[Form]` attribute:

```xpp
[Form]
public class MyForm extends FormRun
{
    // Data sources (defined in the AOT XML, not declared here)
    // Methods   - init, run, close, plus any custom logic
    // Controls  - event handlers for control activity (also defined in XML)
}
```

The `FormRun`-derived X++ class is only half of a form. The other half
is the AOT XML envelope (datasources, design, controls, pattern). The
two halves are unified at runtime by the form runtime. See `dynamics-xpp:xpp-form`
for the envelope.

Common `FormRun` overrides:

- `init()` — runs once, before data is loaded. Set up datasources,
  modify control properties, read parameters (`element.args()`).
- `run()` — runs after `init()`, before the form is displayed.
- `close()` — runs when the form is closing.

Controls' event handlers (e.g. `MyButton_clicked()`) are typically
declared in the form's X++ source even though the controls themselves
are defined in XML.

## Other framework classes worth knowing

These come up often enough that you'll want to recognize them when
reading code, even if `dynamics-xpp:xpp-language` is not the place to learn them in
depth:

- `Args` — parameter-passing object between forms / menu items.
  `element.args()` gets the current form's args.
- `FormDataSource` — runtime representation of a datasource declared in
  the form XML. Use `MyDataSource_DS` inside a form to refer to the
  datasource (note the `_DS` suffix convention).
- `Query`, `QueryRun`, `QueryBuildDataSource` — programmatic query
  composition. Forms can use composed queries via `Query` instead of
  static datasources.
- `SysOperationFramework`, `SysOperationServiceController` — modern
  batch-job framework (replaces the old `RunBase` pattern).
- `DirParty`, `DirPartyTable` — the global "party" model that unifies
  customers, vendors, contacts, etc.
- `Common::buf2Buf(src, dst)` — copy-fields helper between two record
  variables of the same table type. Bypasses `validateWrite` and
  business logic; use carefully.

When you encounter unfamiliar framework types while reading code, use
`xpp_find_object` to pull their definitions and `xpp_get_object_methods`
to see what they expose.
