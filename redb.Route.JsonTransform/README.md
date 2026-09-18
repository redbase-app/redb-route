# redb.Route.JsonTransform

Declarative JSON-to-JSON transformation with [JSONata](https://jsonata.org/) — the analog of Camel's
`jslt` / `jolt` / `jsonata` components — on the native .NET engine
[Jsonata.Net.Native](https://github.com/mikhail-barg/jsonata.net.native) (no JavaScript, no JVM).
Field-to-field mapping with renaming, nesting, array unwrapping and defaults, without writing a
template for the whole document.

```csharp
.TransformJson("Transforms/order-to-shipment.jsonata")          // file, compiled once, cached
.TransformJson(TextSource.Inline("""
    { "id": orderId, "to": { "city": address.city }, "skus": items.sku, "tenant": $headers.tenant }
    """))
.TransformJson("Transforms/x.jsonata", JsonTransformOutput.Node) // JsonNode body instead of text
```

| Rule | Why |
|---|---|
| A `string` argument is a **locator** (file relative to `JsonTransformOptions.BaseDirectory`, default `AppContext.BaseDirectory`, or `assembly:Name/Path/file.jsonata`); inline text is `TextSource.Inline("...")`. | A string never has to be guessed as "path or text" — the same rule as templates and the expression language. |
| Compiled at route build. | A missing file or a syntax error fails `Start()` with the specification name and the engine's message. |
| Input: the body as JSON text, `byte[]`, `Stream`, a `System.Text.Json` tree, or a POCO (serialized camelCase). | The step sits naturally after `Unmarshal` or straight after an HTTP consumer. |
| `$headers` and `$properties` are bound in the specification. | The same names the payload templates use. |
| Output: JSON text with `ContentType = application/json` (default; `Indent` option), or a `JsonNode` (`JsonTransformOutput.Node`). | An undefined result leaves a `null` body. |

Options: `services.AddJsonTransform(o => ...)` or `context.UseJsonTransform(o => ...)` — `BaseDirectory`, `Indent`.

XML form ([redb.Route.Xml](../redb.Route.Xml/README.md)):
`<transformJson spec="transforms/order-to-shipment.jsonata"/>` or the specification as the element's text (CDATA); `output="string|node"`.
