# Metrics: measuring routes, and reading the measurements back

redb.Route measures itself on two layers. This guide covers what each layer holds, how to read it
— from your application, and from inside a route — and how to log the numbers for a whole route or
for one section of it.

## The two layers

**Layer 1 — OpenTelemetry export.** One ActivitySource and one Meter, both named `redb.Route`.
Traces cover consumers, producers and processors; metrics cover the engine and the EIPs:

| Instrument | Kind | What it counts |
|---|---|---|
| `redb.route.exchanges.processed` / `.failed` | counter | exchanges through all routes |
| `redb.route.exchange.duration` | histogram | end-to-end processing, ms |
| `redb.route.exchanges.inflight` | up-down counter | currently processing |
| `redb.route.step.processed` / `.failed` / `.duration` | counter / histogram | `.Metered()` sections |
| `redb.route.throttle.delayed` | counter | exchanges delayed by rate limiting |
| `redb.route.circuitbreaker.tripped` / `.rejected` | counter | breaker opens; exchanges refused |
| `redb.route.filter.dropped`, `.debounce.*`, `.splitter.parts`, `.timeout.expired`, `.wiretap.*`, `.multicast.*`, `.retry.*` | counters | per-EIP events |

Measurements are tagged with `redb.route.id`, and step metrics also with `redb.route.step`.
A .NET `Meter` is push-only: this layer goes to a subscriber or nowhere. The standard subscriber
is an OpenTelemetry backend:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("redb.Route"))
    .WithTracing(t => t.AddSource("redb.Route"));
```

**Layer 2 — endpoint statistics.** Every endpoint tracks its own counters, readable in-process
through `IEndpointStatistics`: `MessagesIn`, `MessagesOut`, `Errors`, `Warnings`, `Rejected`
(shed by an admission limit before the pipeline ran), `Cancelled` (abandoned by a cooperative
cancellation — not the route's fault, and not an error), `BytesIn`/`BytesOut`,
`ThroughputPerSecond`, `AverageProcessingTime` (last 100 messages), `LastErrorMessage`/`Time`,
`HealthStatus`/`HealthReason`. A consumer endpoint's completed count is
`MessagesIn − Errors − Cancelled`.

## Reading measurements from inside a route

### `stats()` in the expression language

Endpoint statistics are values in the expression language, so a route can **branch** on its own
measurements:

```csharp
// Turn traffic away from a degraded target.
.Filter("stats('direct:orders', 'health') != 'Critical'")

// Alarm on a cancellation storm.
.When("stats('current', 'cancelled') > 100")

// Log your own counters, no lambda captures.
.SetHeader("err", Expr("${stats('current', 'errors')}"))
.SetHeader("avg", Expr("${stats('current', 'averageProcessingTimeMs')}"))
.To("log:orders?showHeaders=true&showBody=false")
```

`stats(target, metric)`: the target is an endpoint URI or `'current'` (the route processing the
exchange). Metric names, case-insensitive: `messagesIn`, `messagesOut`, `errors`, `warnings`,
`rejected`, `cancelled`, `bytesIn`, `bytesOut`, `throughputPerSecond`, `averageProcessingTimeMs`,
`health`, `healthReason`, `lastError`. A typo in a literal metric name fails the route **build**
with the list of known names, not the first message.

The same function reads the **OpenTelemetry layer** when the target starts with `otel:` — the EIP counters and
`.Metered()` durations live only there, and this is the only way a route (or markup, which has no lambdas) can
see them:

```csharp
// The slowest run of one metered step of this route.
.SetHeader("worst", Expr("${stats('otel:redb.route.step.duration/enrich', 'max')}"))

// How often the throttle delayed another route.
.When("stats('otel:redb.route.throttle.delayed@orders', 'sum') > 100")
```

`otel:instrument[@route][/step]`: without `@route` the instrument is read for the route processing the exchange,
`/step` matches the `redb.route.step` tag. The field is `count`, `sum`, `min`, `max` or `last`, and a literal
unknown field fails the build like an unknown metric. This branch needs the in-process subscriber
(`context.UseMetricsSnapshot()`); without it the call fails and says so. An instrument nobody has measured yet
reads as zero.


### `IExchange.Context`

Any processor can reach the route context directly — Apache Camel's `exchange.getContext()`:

```csharp
.Process(e =>
{
    var s = (IEndpointStatistics)e.Context!.GetEndpoint("direct:orders");
    log.LogInformation("in={In} err={Err} cancelled={C} avg={Avg:F1}ms",
        s.MessagesIn, s.Errors, s.Cancelled, s.AverageProcessingTime.TotalMilliseconds);
})
```

The context is stamped when the exchange enters a route and restored when it leaves, so inside a
`direct:` sub-route it is the inner route's context, and `null` on an exchange you built by hand.

### The control bus

`controlbus:route?action=stats` puts a `<routeStats>` XML into the exchange body — one route by
`routeId` (or `current`), or the whole context without one. It carries the full counter surface,
XML-escaped. A timer-driven monitoring route is the classic shape:

```csharp
r.From("timer:stats?period=60000")
 .To("controlbus:route?action=stats")
 .To("log:route-stats?level=Info");
```

Mind one trap: from inside a wire-tap branch, `routeId=current` names the *branch* route, not the
one you tapped — name the route explicitly there.

## The trail of one exchange: `messageHistory()`

Message History records every node an exchange passed, with the time it took, and the failure dump prints it.
The same trail is a value in the expression language, so a route can log it or branch on its cost:

```csharp
.Log("${messageHistory()}")                                    // the table, as the failure dump prints it
.SetHeader("trail", Expr("${messageHistory('compact')}"))       // log > choice > to(http)
.When("messageHistory('slowestMs') > 500")                     // only the exchanges that cost something
```

Kinds: `table` (the default), `compact`, `json`, `count`, `totalMs`, `slowestMs`, `slowest`, `lastNode`. The
numbers make it a predicate, which is what markup needs: `<when expr="messageHistory('slowestMs') &gt; 500">`.

Message history is opt-in (`RouteEngineOptions.EnableMessageHistory`, or `.MessageHistory()` on one route), so
an exchange that recorded nothing reads as an empty string and zero. That is the one measurement in this
language that answers instead of failing: a diagnostic log must not break a route where history is simply off.
An unknown kind is still an authoring error and fails the build.


## Measuring a section of a route

Two ways, different trade-offs:

**`.Metered("name") … EndMetered()`** wraps a section in step metrics — precise duration
histograms tagged `redb.route.id` + `redb.route.step`, at the cost of living in layer 1
(readable in-route only through the snapshot below):

```csharp
.Metered("enrich")
    .Xslt("enrich.xsl")
    .To("sql:INSERT ...?dataSource=main")
.EndMetered()
```

**Extract the section into a `direct:` sub-route.** The section becomes an endpoint with its own
full `IEndpointStatistics` — readable via `stats('direct:enrich', ...)`, the control bus, and
`e.Context`, with no extra setup:

```csharp
r.From("direct:enrich").RouteId("enrich").Xslt("enrich.xsl").To("sql:...");
// main route: .To("direct:enrich")
```

## Reading the OpenTelemetry layer in-process: the metrics snapshot

The EIP counters and `.Metered()` step durations exist only in layer 1. To read them without an
external backend, opt in to the in-process subscriber:

```csharp
context.UseMetricsSnapshot();          // or services.AddMetricsSnapshot() with DI

// later, from a processor or from application code:
var snap = context.GetMetricsSnapshot()!;   // or e.Context.GetMetricsSnapshot()
double? delayed = snap.Value("redb.route.throttle.delayed", routeId: "orders");
var work = snap.Point("redb.route.step.duration", routeId: "orders", step: "enrich");
// work: Count, Sum, Min, Max, Last — current values, nothing more
```

Design limits, on purpose: **opt-in** (a `MeterListener` gets a callback for every measurement —
nobody pays for that without asking), **current values only** (no windows, no percentiles, no
history — that is an OpenTelemetry backend's job), **exact tag matching** (a point either was
recorded under `(instrument, routeId, step)` or there is nothing to return). The listener is
disposed when the context stops; collected points stay readable.

## Choosing, in one table

| You want | Use |
|---|---|
| Dashboards, alerting, history, percentiles | OpenTelemetry backend: `AddMeter("redb.Route")` |
| Branch a route on an endpoint's health or counters | `stats(...)` in a condition |
| Branch a route, or markup, on EIP counters and step durations | `stats('otel:…', field)` + `UseMetricsSnapshot()` |
| See which steps one exchange went through, and what each cost | `messageHistory()` in a log or a condition |
| Log a route's own numbers periodically | timer route → `controlbus:...?action=stats` → `log:` |
| Full counter surface from a processor | `e.Context.GetEndpoint(...)` as `IEndpointStatistics` |
| Numbers for one section of a route | `direct:` sub-route (statistics) or `.Metered()` (histograms) |
| EIP counters / step durations without a backend | `context.UseMetricsSnapshot()` |
