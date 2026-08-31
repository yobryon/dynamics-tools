---
name: xpp-custom-control
description: Use when authoring a D365 F&O custom form control — a FormTemplateControl (or FormSegmentedEntryControl / FormReferenceGroupControl) subclass paired with a FormBuildControl, DataContract classes, and Html/Styles/Scripts AxResources. Covers the X++ half, the $dyn client half, the React path via FormReactControlAttribute, how host forms extend the control, and the design-time wiring on the consuming form.
---

# Authoring a D365 F&O custom form control

Custom controls are how you put UI in F&O that the standard control set can't
express — a board, a timeline, a chart, a bespoke editor. They are a
well-trodden surface (the metadata store carries ~221,000 `FormControlExtension`
usages across ~3,470 forms) but a thinly documented one, and almost every
convention in this skill is load-bearing in a way the XSD and the tool schemas
cannot tell you.

Load `dynamics-xpp:xpp-language` first. Load `dynamics-xpp:xpp-class` before
authoring the classes and `dynamics-xpp:xpp-resource` before the resources.

---

## The object set

A custom control is never one object. The minimum viable set:

| Object | Type | Role |
|---|---|---|
| `<Prefix><Name>Control` | AxClass : `FormTemplateControl` | Runtime behaviour, server↔client properties, commands |
| `<Prefix><Name>ControlBuild` | AxClass : `FormBuildControl` | Design-time properties shown in the VS property grid |
| `<Prefix><Name>Contract` | AxClass `[DataContract]` | One per shape you serialize to the client |
| `<Prefix><Name>ControlHTM` | AxResource (Html) | The view the control mounts into |
| `<Prefix><Name>ControlJS` | AxResource (Scripts) | Client behaviour |
| `<Prefix><Name>ControlCSS` | AxResource (Styles) | Styling |

Optionally a `<Prefix><Name>ControllerBase` AxClass — see **Extensibility**.

### Finding a reference implementation

Before authoring, read a shipped control. These are the useful ones:

- **`SegmentedEntryControl`** (Ledger, on disk) — the best all-round model. React,
  not sealed, uses a controller class, and not in preview.
- **`ChippedEntryControl`** (ApplicationFoundation, on disk) — the cleanest
  React example, but `final` and marked preview by Microsoft.

`xpp_get_class(name, atPath='/sourceCode/declaration')` on either gives you the
attribute shape in one call.

---

## The X++ half

### The control class

```xpp
[FormControlAttribute('ConKanbanBoardControl', '/resources/html/ConKanbanBoardControl', classStr(ConKanbanBoardControlBuild))]
[FormReactControlAttribute('/resources/Scripts/ConKanbanBoardControl.js')]  // React only
public class ConKanbanBoardControl extends FormTemplateControl
{
    FormProperty cardsProperty;
    ConKanbanBoardControlBuild buildControl;
}
```

`FormControlAttribute` takes `(templateId, viewPath, buildClass)`. The view path
accepts either form — `'/resources/html/MyControl'` (SegmentedEntry, and most
customer controls) or a bare `'MyControl.htm'` (ChippedEntry). Both ship; it is
a non-decision.

> **DO NOT mark the control class `final`.**
>
> This is the single highest-consequence decision in the whole skill, and it is
> invisible until someone tries to extend your control and finds they can't.
> `final` blocks subclassing, which blocks host-form method overrides — see
> **Extensibility** below. `ChippedEntryControl` is `final`, which is precisely
> *why* Microsoft had to give it a controller class.

### The build class

```xpp
[FormDesignControlAttribute('ConKanbanBoardControl')]
public class ConKanbanBoardControlBuild extends FormBuildControl
{
    #define.DataCategory('Data')
    private str cardDataSourceName;
}
```

Each design-time property is a `parm`-style method carrying attributes:

```xpp
[FormDesignProperty('Card data source', #DataCategory), FormDesignPropertyDataSource]
public str parmCardDataSourceName(str _value = cardDataSourceName) { ... }

[FormDesignProperty('Card title method', #DataCategory)]
public str parmCardTitleMethod(str _value = cardTitleMethod) { ... }
```

`FormDesignPropertyDataSource` makes the property a data-source picker — use it
freely. There is also a `FormDesignPropertyDataMethod(<method naming the DS
property>)` that turns a property into a method picker, but it carries a
validation cost that usually outweighs the convenience — see **Data methods**
below before reaching for it.

> **A build class is dozens of near-identical short methods.** Sending them all
> in one `xpp_create_class` risks a truncated payload. Create the class with the
> declaration plus a handful, then
> `xpp_patch_by_path(op='append', atPath='/sourceCode/methods', value=[{...}, {...}])`
> for the rest — `append` accepts an array, so a wide build class is one call,
> not one-per-method. `xpp_patch_class` replaces the whole method list
> wholesale, so it is the wrong tool for incremental additions.

### Contracts

```xpp
[DataContract]
public class ConKanbanCardContract
{
    str title;
}

[DataMember("Title")]
public str parmTitle(str _title = title) { ... }
```

**The `[DataMember]` string is the JSON key the client sees.** If it drifts from
the client's property name you get `undefined` at runtime with no error
anywhere. Keep a client-side type declaration mirroring each contract and say so
in a comment on both sides.

### Server → client: properties

Register in `new()`, expose with `[FormPropertyAttribute]`:

```xpp
protected void new(FormBuildControl _build, FormRun _formRun)
{
    super(_build, _formRun);
    this.setTemplateId('ConKanbanBoardControl');
    this.setResourceBundleName('/resources/html/ConKanbanBoardControl');
    cardsProperty = this.addProperty(methodStr(ConKanbanBoardControl, parmCards), Types::Class);
}

[FormPropertyAttribute(FormPropertyKind::Value, 'Cards')]
public List parmCards(List _value = cardsProperty.parmValue())
{
    if (!prmIsDefault(_value))
    {
        cardsProperty.setValueOrBinding(_value);
        return cardsProperty.parmValue();
    }
    return _value;
}
```

`Types::Class` carries a `List` of contract objects or a single contract object;
both serialize to JSON. Bundle related booleans into one options contract rather
than registering a property each — one observable is cheaper to observe and
easier to extend.

> **Make the constructor `protected`, not `private`.** BP wants it non-public.
> `private` would block the derived class that a host-form override generates,
> silently removing the extensibility tier.

### Client → server: commands

```xpp
[FormCommand('MoveCard')]
protected void moveCard(str cardRecId, str toColumnId)
{
    this.cardMoved(str2int64(cardRecId), toColumnId);
}
```

> **Command parameters must NOT carry the leading underscore that X++ convention
> and BP otherwise want.** The client binds its argument object to the method's
> parameters **by name**: `$dyn.function(self.MoveCard)({ cardRecId: '...' })`
> matches a parameter literally named `cardRecId`. Name it `_cardRecId` and the
> arguments arrive empty, at runtime, with no error. Put a comment on the method
> saying why, or someone will "fix" it to satisfy BP.

Pass everything as `str`. The channel carries strings, and a `RecId` is an
`int64` whose upper range is not safely representable as a JavaScript number —
never convert one to a JS number anywhere in the round trip.

### Reacting to data changes

Subscribe in `applyBuild()`, after `super()`:

```xpp
cardDataSource = this.formRun().dataSource(cardDataSourceName);
cardDataSource.OnQueryExecuted += eventhandler(this.dataSourceQueryExecuted);
```

If the control uses two independent data sources, **validate that they are not
joined**. A `DataSourceLink` between them means iterating one re-ranges the
other, and the symptom is a control that renders almost-but-not-quite right —
extremely hard to diagnose from the UI. Check
`ds.joinSourceDataSource()` in `applyBuild` and throw a clear error.

---

## Extensibility — three tiers, one dispatch path

This is the part with the least prior art and the most leverage.

### Host-form method overrides DO work on custom controls

A host form extends a custom control by overriding its public methods in a
nested class in the form's X++:

```xpp
[Form]
public class ConKanbanBoardDemo extends FormRun
{
    [Control("Custom")]
    class KanbanBoard
    {
        public container eligibleColumns(Common _record)
        {
            return element.myEligibilityRule(_record);
        }
    }
}
```

Visual Studio offers these in the override picker and **they dispatch at
runtime** — verified. Two conditions:

1. The control class is not `final`.
2. The method is `public` or `protected` (not `private`) and the class exposes it
   deliberately.

Microsoft's own forms never do this — a full scan of the metadata store finds
zero control nodes combining `FormControlExtension` with method overrides. That
absence is not evidence it's unsupported; it's because Microsoft seals its own
custom controls and routes extensibility through controller classes instead.

### Overrides beat delegates — X++ delegates cannot return a value

Every delegate in ApplicationPlatform + ApplicationFoundation (496 of them) is
`void`, and the compiler enforces it. So any extension point that must **answer a
question** — *which targets are legal for this record?*, *may this operation
proceed?* — cannot be a delegate. An overridden method returns a value and gets
`super()` for free.

Reach for a delegate only for genuine fire-and-forget multicast notification.

### The three tiers

Give each extension point one public virtual method whose base implementation
degrades:

```xpp
public container eligibleColumns(Common _record)
{
    if (controller)                    // tier 2 — optional controller class
    {
        return controller.eligibleColumns(_record);
    }
    return conNull();                  // tier 0 — declarative default
}
```

- **Tier 0 — declarative.** Design properties alone produce a working control.
  No code on the host form.
- **Tier 1 — override on the host form.** Where a consumer will naturally reach.
  `super()` still reaches tiers 2 and 0.
- **Tier 2 — controller class.** An abstract base named in a design property,
  instantiated via `DictClass::makeObject()`. For logic shared across forms or
  unit-tested in isolation.

All three coexist because there is exactly one virtual method per concern.
Microsoft ships this combination (`SegmentedEntryControl` is non-final *and*
carries a `controllerClassName`), so it is a sanctioned shape, not an invention.

### Signal capability explicitly — you have no kernel introspection

Kernel-implemented controls can ask the framework whether `jumpRef` was
overridden and light the link only when it was. **A custom control cannot do
this.** There is no way to ask "did the host override this method?"

So make capability an explicit, per-item flag on the contract:

```xpp
card.parmCanJumpRef(dataMethods.exists('card.jumpRef'));
```

The presence of a configured design property becomes the signal, and the client
renders the affordance only when the flag is set. Applies to any optional
behaviour whose affordance should not appear when unconfigured.

### Put expensive per-item resolution in populate, not in the interaction

If an interaction needs an answer the server owns — legal drop targets, whether
an action applies — compute it during populate and ship it **on the item's
contract**. A round trip at interaction time makes the UI feel broken. Re-check
authoritatively on the server when the interaction completes; the client-side
copy is for affordance, not enforcement.

---

## Data methods — naming them, and the attribute trap

The declarative tier works by naming methods in design properties. The control resolves a
name at runtime by probing, in order: the backing **table**, then the **form data source**,
then the **`FormRun`**. So a data method may live in any of the three.

Author them as **data source display methods**, which take the record as a parameter
because `this` is the data source, not the buffer:

```xpp
public display str kanbanTitle(ConSpecialOrderRequestLine record)
{
    return record.CaseId;
}
```

Return a displayable type. For a list-shaped value, return a comma-separated `str` and
normalise on the server — a helper accepting a `container`, a `List`, or a delimited
string keeps data-method authors from having to care.

> **Think hard before decorating the property with `FormDesignPropertyDataMethod`.**
>
> The attribute gives the VS property grid a method picker — but it also makes metadata
> validation resolve the named method **against the backing table only**. A data source
> display method does *not* satisfy it, and every configured property then emits
> `DataMethodNotFoundOnDataSource` on every build.
>
> This is **not** the same rule as a grid column's `dataMethod`, which does accept a form
> datasource display method (see `dynamics-xpp:xpp-form`). The two surfaces look identical
> and behave differently.
>
> Verified by experiment: repointing one property from a data source method to a table
> display method drops the error count by exactly one.
>
> The diagnostics are **non-fatal** — the build succeeds and the control works, because
> the control does its own resolution. But they never go away. Since the picker enumerates
> table methods (usually the set you *can't* use here) while generating false errors,
> plain `[FormDesignProperty]` is generally the better trade: a free-text property, a clean
> build, and no misleading candidate list.

**For action-style methods** — `void method(TableBuffer record)`, invoked for a side effect
such as navigation — never use the attribute; it is for value-returning display methods
only. Precedent: `ConActivityTimelineControlBuild.parmAvailableActionsDataMethod`.

Navigation is the implementor's responsibility, never the control's. The control invokes
the named method; the method runs whatever `MenuFunction` is right.

## The client half

### The view

For a `$dyn`-templated control, the `.htm` carries the markup and `data-dyn-bind`
expressions. For a React control it is nearly empty — a mount point and the two
bindings the platform needs:

```html
<link href="/resources/styles/MyControl.css" rel="stylesheet" type="text/css" />
<div id="MyControl" class="MyControl" data-dyn-bind="
     visible: $control.Visible,
     sizing: $dyn.layout.sizing($data)">
</div>
```

### Registering the control

```js
$dyn.ui.defaults.MyControl = { Cards: [], MoveCard: function () {} };

function MyControl(data, element) {
    var self = this;
    $dyn.ui.Control.apply(self, arguments);
    $dyn.ui.applyDefaults(self, data, $dyn.ui.defaults.MyControl);

    $dyn.observe(self.Cards, function () { self.render(); });
    self.render();
}

$dyn.controls.MyControl = MyControl;
MyControl.prototype = $dyn.extendPrototype($dyn.ui.Control.prototype, {});
```

Every property registered with `addProperty` must appear in `$dyn.ui.defaults`,
and every `[FormCommand]` must appear there as a no-op function — the framework
replaces it with the real binding.

### Invoking a command

```js
$dyn.function(self.MoveCard)(
    { cardRecId: String(id), toColumnId: String(col) },
    undefined, undefined,
    function (interaction) { interaction.ShouldBlockOnExecution = true; }
);
```

`ShouldBlockOnExecution` prevents a second invocation racing the first — set it
for anything that writes.

---

## The React path

F&O ships **React 17 and ReactDOM as globals on the shell page**
(`DefaultCurrent.htm` loads `Scripts/ext/react.17.0.1.min.js` before everything
else), and the form page compiler understands `FormReactControlAttribute` — it
exports `get_ReactBundleName` alongside `get_ResourceBundleName`.

```xpp
[FormControlAttribute('MyControl', '/resources/html/MyControl', classStr(MyControlBuild))]
[FormReactControlAttribute('/resources/Scripts/MyControlReact.js')]
public class MyControl extends FormTemplateControl
```

Then render into the control's own element:

```js
ReactDOM.render(React.createElement(Board, props), element);
```

### Never bundle React

```js
externals: { react: 'React', 'react-dom': 'ReactDOM' }
```

Bundling your own copy puts two Reacts on the page and breaks hooks in ways that
are miserable to debug. This also pins you to the React 17 API — no `createRoot`,
no concurrent features.

Unmount on dispose, or every open/close of the host form leaks a React root:

```js
dispose: function () { ReactDOM.unmountComponentAtNode(this.element); }
```

**Fallback:** if the attribute misbehaves, load the bundle with a plain
`<script src>` in the `.htm` and register through `$dyn.controls` as usual — the
non-React path. One line of `.htm`, no X++ change.

---

## Theming

Do not borrow a platform CSS class to inherit fonts or colours — that works until
Microsoft restyles the class you borrowed.

`$dyn.ui.theme.get()` returns a flat map of the active theme's colours, units and
fonts, collected from probe elements the theme stylesheet styles. Read it once at
mount and project it into CSS custom properties on your root:

```js
var theme = $dyn.ui.theme.get();
element.style.setProperty('--my-accent', theme.accentColor);
```

Give every custom property a literal fallback in the stylesheet so the control
degrades to something legible if the probe fails.

---

## Hosting the control on a form

The consuming form's control node has **no `<Type>`** — it carries a
`FormControlExtension` instead. In the typed form surface:

```jsonc
{
  "name": "KanbanBoard",
  "kind": "Other",
  "rawType": "AxFormControl",
  "autoDeclaration": true,
  "formControlExtension": {
    "name": "ConKanbanBoardControl",
    "extensionProperties": [
      { "name": "parmCardDataSourceName", "type": "String", "value": "MyTable" },
      { "name": "parmShowCounts", "type": "Enum", "value": "True",
        "otherProperties": { "TypeName": "boolean" } }
    ]
  }
}
```

Booleans are `type: "Enum"` with `otherProperties.TypeName = "boolean"`.
`xpp_get_form(name, atPath='/design/controls/<Control>')` on any form already
hosting a custom control gives you the exact shape to copy.

Host-form overrides go in `sourceCode.dataControls` keyed by the control's name
on the form, with `type: "Custom"`.

---

## The development loop

The build copies `AxResource` content to `J:\AosService\WebRoot\Resources\{Html,Styles,Scripts}`,
and **that is what IIS actually serves.** So during prototyping:

1. Point your bundler's output at `WebRoot\Resources\Scripts` and edit the
   `.htm` / `.css` in `WebRoot\Resources\{Html,Styles}` directly.
2. Reload the browser. No X++ build, no AOT round trip.
3. Once the shape settles, copy the files into the model's
   `AxResource\ResourceContent\{Html,Styles,Scripts}\` folder so they survive the
   next build and get checked in.

> **Hard-reload when iterating on CSS/JS.** Resource URLs carry no cache-buster,
> so the browser will happily serve a stale stylesheet while the server has the
> new one. If a change appears to have no effect, fetch the URL with a cache-
> busting query and compare before you start debugging the change itself.

Remember step 3. Edits that live only in `WebRoot` are destroyed by the next
build and are invisible to source control.

---

## Verification checklist

Metadata validation and BP will not catch most of what breaks a custom control.
Before declaring one done, confirm in the browser:

- [ ] The control renders at all (a registration typo yields an empty div, silently).
- [ ] Each server property arrives — check a value, not just presence.
- [ ] Each command reaches X++ **with its arguments populated** (the underscore trap).
- [ ] A write round-trips and survives a full page reload.
- [ ] Host-form overrides are actually being hit.
- [ ] The control themes correctly and sizes correctly inside its host container.

---

## Gotchas

- **`final` on the control class silently removes the override tier.** No error,
  no warning; the VS override picker simply offers nothing.
- **`private` methods are not extension points.** If you intend a method to be
  overridable, it must be `public` or `protected`.
- **`[FormCommand]` parameter names bind by name and must not start with `_`.**
- **`[DataMember]` names are the client's JSON keys.**
- **`RecId` crosses the wire as a string.** `int64` exceeds JS safe-integer range.
- **`SortedMap` does not exist in X++.** `Set` *is* ordered — pair a `Set` of
  zero-padded composite keys with a `Map` when you need a sorted collection.
- **Two data sources used by one control must not be joined to each other.**
- **`FormDesignPropertyDataMethod` validates against the table only** — see above.
- **BP flags literal strings** (`BPErrorLabelIsText`) — labelise user-facing text.
- **A menu item needs a privilege, and its privilege needs a duty, and the duty needs a role** (`BPErrorMenuItemNotCoveredByPrivilege`, `BPErrorPrivilegeNotCoveredByDuty`, `BPErrorDutyNotCoveredByRole`) — wire all four or none of the warnings clear.

---

## See also

- `dynamics-xpp:xpp-class` — the control, build and contract classes
- `dynamics-xpp:xpp-resource` — the Html / Styles / Scripts resources
- `dynamics-xpp:xpp-form` — hosting the control; the data-method resolution rule
- `dynamics-xpp:xpp-menuitem`, `dynamics-xpp:xpp-security` — making the host form reachable
