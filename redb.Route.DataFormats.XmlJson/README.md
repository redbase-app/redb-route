# redb.Route.DataFormats.XmlJson

XML to JSON and back for redb.Route when there is no class for the document, as Apache Camel's `xmljson`.

## When a class exists, use it

The typed path stays the recommended one: it validates, it names every field, and the compiler checks the mapping.

```csharp
From("file://in?include=*.xml")
    .Unmarshal<Order>("application/xml")   // XML to the class
    .Marshal("application/json")           // the class to JSON
    .To("http://partner/orders");
```

This package is for documents without a class: a partner format you only pass on, a schema too large to map by hand
(UBL, ebMS), or a gateway that turns any XML into JSON for a JavaScript consumer.

## The JSON it writes

The notation is Json.NET's, so .NET and Java consumers read it without setup:

| XML | JSON |
|---|---|
| `<a>5</a>` | `{"a": "5"}` |
| `<a/>` | `{"a": ""}` |
| `<a xsi:nil="true"/>` | `{"a": null}` |
| `<a id="1">x</a>` | `{"a": {"@id": "1", "#text": "x"}}` |
| `<o><i>1</i><i>2</i></o>` | `{"o": {"i": ["1", "2"]}}` |
| `<s:Envelope xmlns:s="urn:s">` | `{"s:Envelope": {"@xmlns:s": "urn:s", ...}}` |

- The root element is the one key of the result.
- Comments and processing instructions are left out; CDATA is text; white space between child elements is layout.
- `namespaces=Drop` writes local names (`"Envelope"`) and leaves the declarations out.
- `textKey` and `attributePrefix` change `#text` and `@` (`$` for a WSO2 Micro Integrator consumer).
- The writer escapes HTML-sensitive characters (`<` as `\u003C`): the same JSON for any parser, and safe if the JSON
  ever lands inside an HTML page.

## Which elements are arrays

One element written as an object and two as an array is the problem every hand-written mapper solves. xmljson takes
the answer from these, strongest first:

1. **`arrays=`**: element paths that are always an array, `/Order/Items/Item` from the root or `//Item` at any depth.
   A step with a prefix (`//x:Item`) compares with the name as the document writes it. It adds arrays the schema does
   not know (content under `xs:any`).
2. **The schema** (`schema=`): an element repeats when its `maxOccurs`, or that of an enclosing `sequence`, `choice`
   or group, is above 1. Groups, extension, substitution groups and recursive types are followed. With a schema,
   numbers and `xs:boolean` are JSON numbers and booleans (a string type keeps `"007"` as text), and an element of
   element content is an object even when empty.
3. **The plural rule** (`plural=true`, off by default): a parent named as the English plural of its children's name
   (`Incidents` holding only `Incident`) holds a list. Regular plurals and a short irregular list; silent on any other
   name. Limits: a singular ending in `-us` also reads as a plural without the `s` (`Status` / `Statu`), and JSON to XML
   picks one singular (`Movies` gives `Movy`).
4. **Otherwise**, the document itself: a repeat is an array, a single element an object. `check` warns about a format
   with neither a schema nor `arrays=`.

The schema reader reads a schema and its imports from the package only, never from the network: `schemaRoot=` is the
directory imports may reach, relative to the schema's own directory (`..` for UBL's `maindoc/` importing `../common/`).
A schema's own DOCTYPE is skipped (W3C's xmldsig schema, imported by WS-Security, starts with one); message documents
with a DTD are refused.

## JSON to XML

- Without a schema: a key is an element, `@` keys are attributes (`@xmlns:p` declares a namespace), `#text` the text,
  an array the element repeated, `null` an `xsi:nil` element. `root=` names the root element; without it the JSON has
  one top-level key. A key that is not an XML name, an undeclared prefix or an array inside an array is an error naming
  the JSON path: nothing is renamed or dropped silently. A `null` attribute is left out, as an attribute cannot be nil.
- With a schema, the schema decides: the namespace of every element and attribute, the order of children (whatever
  the order of the keys), the branch of a choice, numbers in the lexical form of their type (`1e3` as `1000` for a
  decimal). Prefixes come from the JSON's `@xmlns:p`, then `prefixes=`, then `ns0`, `ns1`. A key the schema has no place
  for, two branches of one choice, or `null` where not nillable is an error. Whether every required element is present
  is `validateXsd`'s job: put it after the step.

## In code

```csharp
context.AddXmlJsonDataFormat();                                   // the default instance, named "xmljson"

From("direct://in")
    .MarshalXmlJson(o => { o.Schema = "schemas/order.xsd"; o.Arrays.Add("//Tag"); })   // XML to JSON
    .UnmarshalXmlJson<string>(o => o.Schema = "schemas/order.xsd");                     // JSON to XML
```

`UnmarshalXmlJson<JsonNode>()` from an XML body gives the JSON tree. The other targets are `string`, `byte[]`,
`XDocument`, `XElement` and `XmlDocument`. After `Marshal` the content type is `application/json`, after `Unmarshal`
`application/xml`; the format itself is registered under `application/x-xmljson` and the name `xmljson`.

## In markup

A registered format is shared and takes no options on the step, so options go on a named instance in `context.xml` or
on the per-step `type=` form:

```xml
<!-- context.xml -->
<dataFormat name="orders-json" type="redb.Route.DataFormats.XmlJson.XmlJsonDataFormat, redb.Route.DataFormats.XmlJson"
            schema="schemas/order.xsd" arrays="//Tag"/>

<!-- a route -->
<marshal format="orders-json"/>
<unmarshal type="redb.Route.DataFormats.XmlJson.XmlJsonDataFormat, redb.Route.DataFormats.XmlJson"
           target="System.String" root="Order"/>
```

| Attribute | Meaning |
|---|---|
| `schema` | the XSD, a package resource as for `validateXsd` |
| `schemaRoot` | where imports may read from, relative to the schema's directory |
| `arrays` | element paths, comma-separated |
| `plural` | `true` / `false` |
| `namespaces` | `Keep` / `Drop` |
| `prefixes` | `prefix=uri` pairs, comma-separated |
| `root` | the root element of JSON to XML |
| `textKey`, `attributePrefix` | default `#text` and `@` |
| `indent` | `true` / `false` |

The schema path resolves on the first message of each route context through the context's resource resolver (in a
Tsak package, the package root), and the compiled schema is kept for that context. A missing or broken schema
therefore shows at the first message: run `check` before deploying.

## What `check` and `pack` verify

For an xmljson format in markup, always: the schema is a file the package ships, each `arrays=` path is well formed,
and a format with neither a schema nor `arrays=` is warned.

With `--bin` (the build output holds this package) the format's own check, `XmlJsonPackageCheck`, runs as well:

- the schema compiles with its imports, read the way the format reads it at run time;
- each `arrays=` path leads through the schema's structure, from a global element for `/Order/Item` and from any
  element for `//Lines/Line`; a path that breaks is warned at the step where it breaks (`'Line' is not a child of
  'Order'`), and a path into `xs:any` content passes, since that is where `arrays=` is needed;
- `xs:any` and mixed content in the schema are flagged;
- with `plural=true`, the parent/child pairs the rule reads as lists are named, and those where the schema allows one
  child only.

Not checked: an instance configured in code
(`AddXmlJsonDataFormat(o => ...)` used as `format="xmljson"`), whose options the package does not show.

## Not in this version

- Mixed content without loss (text between child elements is joined into `#text`), comments, processing
  instructions, CDATA as distinct from text.
- Polymorphism by `xsi:type`, `xs:redefine` / `xs:override`, XSD 1.1, identity constraints.
- Numbers and dates guessed without a schema.
- A lenient mode for unknown JSON keys.
- Streaming of very large documents: the conversion builds the document in memory, within the XML structure limits.
