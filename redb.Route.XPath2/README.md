# redb.Route.XPath2

XPath 2.0 expressions for redb.Route, usable in every position that takes an expression.

```csharp
using static redb.Route.XPath2.XPath2Dsl;

From("direct:orders")
    .Filter(XPath2("some $o in /orders/order satisfies number($o/total) > 100"))
        .SetHeader("ids", XPath2("string-join(/orders/order[number(total) > 100]/@id, ',')"))
        .To("direct:large-orders")
    .EndFilter();
```

## What it adds over the built-in XPath 1.0

XPath 1.0 has no regular expressions, no sequences, no date arithmetic and no conditional, so a
route that needs one of those has to leave the language. XPath 2.0 has them:

| | |
|---|---|
| regular expressions | `matches`, `replace`, `tokenize` |
| sequences | `distinct-values`, `reverse`, `index-of`, `empty`, `string-join` |
| aggregates | `avg`, `max`, `min` |
| conditional | `if (…) then … else …` |
| iteration | `for $o in /orders/order return …` |
| quantifiers | `some $o in … satisfies …`, `every $o in … satisfies …` |
| types and dates | `xs:date('2020-01-01')`, `instance of`, `treat as`, value comparisons `eq` / `gt` |
| strings | `upper-case`, `lower-case`, `substring-after`, `substring-before` |

## What it is not

**This is XPath 2.0, not XQuery.** `let`, `where` and `order by` are XQuery clauses and the engine
rejects them — when the route is built, not on the first message. So there is **no sorting** and
**no grouping** here. Filtering goes in a predicate rather than in a `where`:

```csharp
XPath2("for $o in /orders/order[number(total) > 8] return string($o/@id)")   // yes
XPath2("for $o in /orders/order where number($o/total) > 8 return $o")       // rejected: XQuery
```

A full XQuery processor is not shipped because every .NET implementation of one is commercial. If
you have a licence for one, the XSLT engine seam (`IXsltEngineFactory`) is the place to plug it in
for transformation; see `docs/V4/12-XQUERY.md`.

There is also no `xpath2()` function in the `${…}` expression language: that language lives in the
core assembly, which does not know this package exists. Use the expression object.

## The rest of the surface

Everything the XPath 1.0 expression grew is here too, with one deliberate difference.

```csharp
XPath2("/invoice/id").From(Header("original-request"))        // read something other than the body
XPath2("/orders/order[@id=$id]").WithParameters(("id", Header("orderId")))   // bound, never concatenated
XPath2("/order/name").Trimmed()                                // trim extracted text
XPath2("/s:Envelope/s:Body", ("s", "http://schemas.xmlsoap.org/soap/envelope/"))  // namespaces
```

Namespaces are given at construction rather than through a fluent `WithNamespaces(...)`, because
XPath 2.0 resolves prefixes while **compiling**, and this expression compiles as soon as it exists
so that a bad path fails the route build. A fluent method could only run after the constructor had
already rejected the path.

Bound parameters are compared, never parsed: a header holding `B-2' or '1'='1` selects an order
with that literal id and matches nothing.

## A namespace trap

If your own code lives in a namespace under `redb.Route`, the simple name `XPath2` resolves to this
package's namespace and shadows the imported factory. Spell it `XPath2Dsl.XPath2(…)` there. Code in
any other namespace is unaffected.

## Licence and dependencies

The package is Apache-2.0, like the rest of redb.Route. It depends on
[XPath2.Net](https://github.com/StefH/XPath2.Net) (`XPath2`, **MS-PL**), which has no dependencies
of its own — so the whole graph is two assemblies. `XPath2.Extensions` is deliberately **not**
referenced: it is MIT but pulls in Newtonsoft.Json, which this framework removed on purpose, and it
adds only non-official extras.

MS-PL is a permissive, OSI-approved licence: it allows commercial and closed-source use and grants
patent rights. It is not GPL-compatible, which matters only if you intend to combine it with
GPL-licensed code. XPath2.Net reports conformance of 12954 of 15133 XQTS 1.0.2 cases (85.6%).
