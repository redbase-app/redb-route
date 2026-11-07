# redb.Route.TestKit

Test a route without its brokers, without changing the route. Works with xUnit, NUnit and MSTest:
assertions throw `MockAssertionException`, no test-framework dependency.

```csharp
await using var ctx = new RouteContext().AddRoutes(new OrdersRoutes());

ctx.AdviceRoute("orders", a => a
    .ReplaceFrom("direct://test-in")            // instead of kafka://orders
    .MockEndpoints("kafka://*", "sql:*"));      // every matching To(...) goes to a mock

await ctx.Start();

var vip = ctx.Mock("kafka://orders-vip");
vip.ExpectMessageCount(1).ExpectHeader("priority", "high");

await ctx.SendBody("direct://test-in", order);
await vip.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
```

## What is inside

| API | Purpose |
|---|---|
| `ctx.AdviceRoute(routeId, a => ...)` | rewrite a route definition before `Start()`: `ReplaceFrom`, `MockEndpoints`, `MockEndpointsAndSkip`, `WeaveById`, `WeaveByToUri`, `WeaveAddFirst`, `WeaveAddLast` |
| `ctx.Mock(uri)` | the `MockEndpoint` standing in for `uri` (original URI or `mock://name`) |
| `MockEndpoint.Expect*` | `ExpectMessageCount`, `ExpectMinimumMessageCount`, `ExpectBodies`, `ExpectBodiesInAnyOrder`, `ExpectHeader`, `ExpectHeaderReceived`, `ExpectProperty`, `Expect(predicate / condition string)` |
| `MockEndpoint.AssertIsSatisfiedAsync(timeout)` | waits, then fails with an "Expected / but was" diff and the bodies received |
| `MockEndpoint.Whenever(n) / WheneverAny()` | scripted replies: `SetBody`, `SetHeader`, `Delay`, `Throw`, `Do` — for `Enrich` and request-reply through `mock://` |
| `ctx.Notify().FromRoute("orders").WhenDone(3).Create()` | wait for route events (`WhenReceived`, `WhenCompleted`, `WhenFailed`, `WhenDone`, `From(uriMask)`, `Filter(condition)`) without a mock at the end |
| `ctx.SendBody`, `ctx.SendBodyAndHeaders`, `ctx.RequestBody<T>` | one-line sends instead of endpoint/producer plumbing |

URI masks (`MockEndpoints`, `WeaveByToUri`, `Notify().From`): exact URI, prefix with a trailing `*`
(`kafka://*`), or `regex:<expression>`. Query strings are ignored for matching.

`MockEndpoints` rewrites the static `To(...)` steps of the advised route; a dynamic `ToD(...)`
resolves its URI per message and is not rewritten — point it at a `mock://` URI through the
message instead.
