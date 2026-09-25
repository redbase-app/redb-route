# redb.Route.Templates

Build a payload from a template instead of concatenating strings: the analog of WSO2 PayloadFactory
and Camel's templating components, on [Scriban](https://github.com/scriban/scriban). Templates
live in files, embedded resources or inline; the result type is explicit so substituted values are
escaped correctly for JSON or XML.

```csharp
.SetBodyTemplate("Templates/order-confirm.json.sbn", MediaType.Json, a => a
    .Set("customer", "header.customerId")          // route expression language
    .Set("total", "header.amount * header.qty")
    .SetValue("channel", "email"))                 // constant
```

```scriban
{
  "id": {{ headers.orderId }},
  "customer": "{{ args.customer }}",
  "items": [
    {{~ for i in body.items ~}}
    { "sku": "{{ i.sku }}", "qty": {{ i.qty }} }{{ if !for.last }},{{ end }}
    {{~ end ~}}
  ]
}
```

## Rules

| Rule | Why |
|---|---|
| A `string` argument is a **locator**: a file path or `assembly:Name/Path/file.sbn`. Inline text is `TextSource.Inline("...")`. | A string never has to be guessed as "path or text" — the same rule the route expression language follows. |
| A relative path is looked up by the context's `IRouteResourceResolver` first, then by `RouteTemplateOptions.BaseDirectory` (default `AppContext.BaseDirectory`). A miss names both places. | One lookup order for the whole framework: the same one `validateXsd`, `xslt` and the XML context files use. A route inside a `.tpkg` keeps its template in `resources/`, which only the package resolver knows about — the base directory stays the worker's. |
| `MediaType` is mandatory: `Json`, `Xml`, `Text`. | It decides escaping of substituted values (`"`/`\`/control characters for JSON; `& < > " '` for XML) and the `ContentType` of the produced body. Template literals are never escaped — only values. `{{ x \| raw }}` writes a value verbatim. |
| Numbers and dates render culture-invariant; dates as ISO 8601. | A payload must not depend on the server locale. |
| Compiled at route build, cached by source. | A missing file or a syntax error fails `Start()` with the template name and `(line,column)`. |
| Sandbox: no `include`, no context objects, .NET objects expose public properties and fields only. | Templates are data, not code. A method call on a body object fails the render, and so does one inside `expr(...)` or an argument expression: those run under `ExpressionSandbox`, where the expression engine refuses reflection calls (its built-in helpers such as `contains`, `substring`, `length` keep working). |

## Data model in a template

| Name | What |
|---|---|
| `body` | JSON text → object tree (`body.order.id`); XML text → navigable tree (`body.order.item[0].sku`, attributes and child elements as members, repeated children as arrays, element text under `text`); POCO → its public members with their C# names; dictionaries → members |
| `headers.name`, `headers["Content-Type"]` | message headers |
| `properties.name` | exchange properties |
| `exception.message`, `.type`, `.stackTrace` | current exception or `null` |
| `args.name` | named arguments of the step |
| `expr("header.amount * header.qty")` | any route-language expression, evaluated by the same engine as `${...}` |
| `raw(value)` / `{{ value \| raw }}` | skip escaping for this value |

## Options

```csharp
services.AddRouteTemplates(o => { o.BaseDirectory = "/etc/acme/templates"; o.Liquid = false; });
// or, without DI:
context.UseTemplates(o => o.StrictVariables = true);
```

`Liquid = true` switches to Scriban's Liquid-compatible syntax. `StrictVariables` makes a missing
variable a render error instead of an empty string. `LoopLimit` (default 10 000) bounds loops.

## Targets

`SetBodyTemplate` (body + `ContentType`), `SetHeaderTemplate(name, ...)`, `SetPropertyTemplate(key, ...)`.

## XML form

The same node in an XML route ([redb.Route.Xml](../redb.Route.Xml/README.md)):

```xml
<payload template="templates/order-confirm.json.sbn" mediaType="json">
  <arg name="customer" expr="header.customerId"/>
  <arg name="channel"  value="email"/>
</payload>

<payload mediaType="xml" target="header:X-Summary"><![CDATA[
  <summary items="{{ body.items | array.size }}"/>
]]></payload>
```
