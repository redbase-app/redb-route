# redb.Route

Core ESB engine — async-first message routing with fluent C# DSL, 30+ EIP processors, unified expression engine with 17 predicates, OpenTelemetry telemetry, and built-in components (Direct, SEDA, Timer, Mock, Log).

[![NuGet](https://img.shields.io/nuget/v/redb.Route?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route
```

## Quick Start

```csharp
using redb.Route.Extensions;

builder.Services.AddRedbRoute(route =>
{
    // Inline routes
    route.AddRoutes(r =>
    {
        r.From("timer://heartbeat?period=5000")
            .SetBody("ping")
            .Log("Heartbeat: ${body}")
            .To("direct://monitor");
    });

    // Or RouteBuilder classes
    route.AddRouteBuilder<MyRoutes>();

    // Or every public, non-abstract RouteBuilder of an assembly (constructor injection works);
    // the filter is optional and leaves builders out
    route.AddRouteBuilders([typeof(MyRoutes).Assembly], type => type != typeof(DebugRoutes));
});
```

Without a host, `context.AddRoutes(typeof(MyRoutes).Assembly)` adds the builders that have a public parameterless
constructor. A builder that cannot be created, or a type already added, is an error naming it — never skipped.

### RouteBuilder — Full DSL

```csharp
public class OrderRoutes : RouteBuilder
{
    protected override void Configure()
    {
        From("direct://orders")
            .RouteId("order-pipeline")
            // Expression predicates — typed, composable
            .Filter(Header("status").isEqualTo("new"))
            // JsonPath extraction
            .SetBody(JPath("$.order"))
            .SetHeader("total", JPath<decimal>("$.order.total"))
            // Content-Based Router — chain style
            .Choice()
                .When(Header("total").isGreaterThan(10000))
                    .To("direct://large-orders")
                .When(Header("total").isBetween(1000, 10000))
                    .To("direct://medium-orders")
                .Otherwise()
                    .To("direct://small-orders")
            .EndChoice()
            .Log("Order routed: ${header.orderId}");

        // Exception handling with redelivery — for the routes of this builder only
        OnException<HttpRequestException>()
            .MaximumRedeliveries(3)
            .RedeliveryDelay(TimeSpan.FromSeconds(2))
            .UseExponentialBackOff()
            .BackOffMultiplier(2.0)
            .Handled()
            .Log("HTTP error: ${exception.message}")
            .To("seda://dead-letter")
        .EndOnException();
    }
}
```

## Built-in Components

| Component | Scheme | Direction | Description |
|-----------|--------|-----------|-------------|
| **Direct** | `direct:name` | In-process | Synchronous in-process messaging |
| **SEDA** | `seda:name` | In-process | Async queue with configurable concurrency |
| **Timer** | `timer:name` | Consumer | Periodic message generation |
| **Mock** | `mock:name` | Producer | Testing endpoint with expectations |
| **Log** | `log:name` | Producer | Logging sink at configurable levels |

### Fluent Builders

```csharp
using redb.Route.Fluent;

// Direct — sync in-process
From(Direct.Endpoint("orders"))
    .To(Direct.Endpoint("processor"));

// SEDA — async queue
From(Seda.Consume("incoming").ConcurrentConsumers(4).Size(1000))
    .To(Seda.Send("outgoing"));

// Timer — periodic
From(TimerDsl.Every("poll").Period(5000).Delay(1000))
    .To("direct://check");

// Mock — testing
From("direct://test")
    .To(MockDsl.Endpoint("result").ExpectedMessageCount(3));

// Log — sink
From("direct://data")
    .To(LogDsl.Info("audit").ShowHeaders().ShowBody());
```

## Expression Engine

Two expression systems — **unified through `IExpression`** interface:

### Typed Expressions (RouteBuilder helpers)

`protected static` helpers in `RouteBuilder` — use directly in `Configure()`:

```csharp
// Message accessors
Body()                          // message body
Header("name")                  // header value
Property("key")                 // exchange property
Constant(42)                    // constant value
Exchange(e => e.RouteId)        // delegate over IExchange

// Structured data extraction
JPath("$.order.total")          // JsonPath
JPath<decimal>("$.order.total") // typed JsonPath
XPath("/order/status/text()")   // XPath 1.0
XPath<int>("count(//item)")     // typed XPath

// String template bridge — wraps ${...} as IExpression
Expr("${header.orderId}")               // single value
Expr("${header.prefix}-${body}")        // template interpolation
```

### 17 Predicate Methods

Every expression supports predicate chaining — for `Filter()`, `When()`, `Validate()`:

```csharp
Header("status").isEqualTo("active")      // equality
Header("status").isNotEqualTo("cancelled") // inequality
Header("amount").isGreaterThan(1000)       // comparison
Header("amount").isLessThan(500)
Header("amount").isGreaterThanOrEqualTo(100)
Header("amount").isLessThanOrEqualTo(9999)
Header("amount").isBetween(100, 5000)      // range (inclusive)
Header("name").contains("Corp")            // substring / collection
Header("name").startsWith("Order-")
Header("name").endsWith(".pdf")
Header("email").regex(@"^[\w.]+@[\w.]+$")  // regex match
Header("type").In("order", "payment")      // set membership
Header("optional").isNull()
Header("required").isNotNull()

// Also works with Expr — string expression + predicates:
Expr("${header.amount}").isGreaterThan(1000)
```

### String Expression Resolver

AST-based compiled expression language with caching (used in `Log`, `Filter(string)`, `When(string)`, `Expr()`):

```csharp
// Templates — ${...} placeholder interpolation
.Log("Processing: ${header.orderId}")
.SetBody(Expr("${header.prefix}-${body}"))

// Boolean expressions — in Filter/When
.Filter("header.amount > 1000")
.Filter("header.status == 'active' AND header.amount > 0")

// Supports: header.*, body.*, property.*, exception.*
// Operators: ==, !=, >, <, >=, <=, AND, OR, XOR, !
// Functions: contains(), startsWith(), endsWith(), jpath(), xpath()
// Arithmetic: +, -, *, /
```

### Every DSL Method — Multiple Input Styles

| Method | Static | Lambda | IExpression | String `${...}` |
|--------|--------|--------|-------------|------------------|
| `SetBody` | `SetBody("hi")` | `SetBody(e => ...)` | `SetBody(Header("x"))` | `SetBody(Expr("${header.x}"))` |
| `SetHeader` | `SetHeader("k","v")` | `SetHeader("k", e => ...)` | `SetHeader("k", JPath("$.x"))` | `SetHeader("k", Expr("${body}"))` |
| `SetProperty` | `SetProperty("k",1)` | `SetProperty("k", e => ...)` | `SetProperty("k", Body())` | `SetProperty("k", Expr("${header.x}"))` |
| `Filter` | — | `Filter(e => ...)` | `Filter(Header("x").isEqualTo("y"))` | `Filter("header.x == 'y'")` |
| `Transform` | — | `Transform(e => ...)` | `Transform(JPath("$.data"))` | — |
| `Process` | — | `Process(e => ...)` | `Process(myProcessor)` | — |

## Processors (30+ EIP Patterns)

| Processor | DSL | Pattern |
|-----------|-----|---------|
| `PipelineProcessor` | (implicit) | Pipeline |
| `FilterProcessor` | `.Filter(...)` | Message Filter |
| `ChoiceProcessor` | `.Choice()` | Content-Based Router |
| `MulticastProcessor` | `.Multicast(...)` | Multicast |
| `RecipientListProcessor` | `.RecipientList(...)` | Recipient List |
| `SplitterProcessor` | `.Split(...)` | Splitter |
| `AggregatorProcessor` | `.Aggregate(...)` | Aggregator |
| `ResequencerProcessor` | `.Resequence(...)` | Resequencer |
| `DynamicRouterProcessor` | `.DynamicRouter(...)` | Dynamic Router |
| `RoutingSlipProcessor` | `.RoutingSlip(...)` | Routing Slip |
| `WireTapProcessor` | `.WireTap(...)` | Wire Tap |
| `EnrichProcessor` | `.Enrich(...)` | Content Enricher |
| `XsltProcessor` | `.Xslt(...)` / `xslt:` | Message Translator (XSLT) |
| `ToProcessor` | `.To(...)` | Message Endpoint |
| `DelegateProcessor` | `.Process(...)` | Custom Processor |
| `LogProcessor` | `.Log(...)` | Logging |
| `RichLogProcessor` | `.Log().Message(...)` | Structured Logging |
| `MessageHistoryProcessor` | `.MessageHistory()` / `EnableMessageHistory` | Message History |
| `ControlBusProducer` | `.ControlBus(...)` / `controlbus:` | Control Bus |
| `DelayProcessor` | `.Delay(...)` | Delayer |
| `LoopProcessor` | `.Loop(...)` | Loop |
| `ThrottleProcessor` | `.Throttle(...)` | Throttler |
| `CircuitBreakerProcessor` | `.CircuitBreaker(...)` | Circuit Breaker |
| `RetryProcessor` | `.Transacted().Retry(...)` | Retry (transaction scope) |
| `DeadLetterProcessor` | `.Transacted().DeadLetterChannel(...)` | Dead Letter Channel (transaction scope) |
| `OnExceptionProcessor` | `.OnException<T>()` | Exception Handler |
| `TryCatchProcessor` | `.DoTry()` | Try-Catch |
| `IdempotentConsumerProcessor` | `.IdempotentConsumer(...)` | Idempotent Consumer |
| `SagaProcessor` | `.Saga()` | Saga (compensating steps) |
| `ScatterGatherProcessor` | `.ScatterGather(...)` | Scatter-Gather |
| `ClaimCheckProcessor` | claim-check definition | Claim Check |
| `ThreadsProcessor` | `.Threads(n)` | Threads (async handoff) |
| `DebounceProcessor` | `.Debounce(...)` | Debounce |
| `KeyedThrottleProcessor` | keyed throttle definition | Throttler (per key) |
| `LoadBalancerProcessor` | `.LoadBalance(...)` | Load Balancer |
| `ChoiceProcessor` (composed) | `.Normalize(...)` | Normalizer |
| `PollEnrichProcessor` | `.PollEnrich(...)` | Polling Consumer / Enricher |
| `SamplingProcessor` | `.Sample(...)` | Sampling |
| `TimeoutProcessor` | `.ProcessingTimeout(...)` | Processing timeout |
| `ToDynamicProcessor` | `.ToD(...)` | Dynamic Endpoint |
| `CheckpointProcessor` | `.Replayable(...)` | Replay checkpoint |
| `RoutePolicyProcessor` | `.RoutePolicy(...)` | Route Policy |
| `StreamCachingProcessor` | `.StreamCaching()` | Stream Caching |
| `StreamingSplitterProcessor` | `.Split(...)` (streaming) | Splitter (streaming) |

> Counts in this file are derived from `Processors/` and `IRouteDefinition` — 37 `*Processor`
> implementations behind 30+ named EIPs. Keep them in step with the repository README and the
> package description when adding a pattern.

## Error Handling

```csharp
// Retry + Dead Letter Channel live on the transaction scope (.Transacted() / .Transaction())
.Transacted()
    .Retry(attempts: 3, delay: TimeSpan.FromSeconds(1))
    .DeadLetterChannel("seda://failed")
    .To("http://api/submit")
.End()

// Try-Catch-Finally scope
.DoTry()
    .To("http://api/submit")
.DoCatch<HttpRequestException>()
    .Log("API failed: ${exception.message}")
    .To("seda://retry")
.DoFinally()
    .Log("Attempt done")
.End()
```

## Transactions

One primitive: `.Transacted()` (alias `.Transaction()`). Inside it all `redb` work shares one connection
per (transaction, database) and commits or rolls back with the block; put `.IdempotentConsumer(...)`
**inside** `.Transacted()` so the dedup key is written in the same transaction as the work.

```csharp
.Transacted()
    .Process(async ex => await redb.SaveAsync(order))   // enlists in the ambient transaction
    .To("kafka://orders.created")
.End()
```

Keep a transacted route to one database (a second durable connection escalates to MSDTC, unsupported on
.NET/Linux), don't run parallel branches inside a transaction (open a transaction inside each sub-route
instead), and note `BeginRedbTransaction()` is `[Obsolete]` — `.Transacted()` enlists `redb` on its own.

**See the full guide: [TRANSACTIONS.md](../TRANSACTIONS.md)** — placement,
per-mode idempotency semantics, the at-most-once loss window without a transaction, parallelism, and
migration off `BeginRedbTransaction()`.

## Content-Based Routing

```csharp
// Chain style — Camel-like
.Choice()
    .When(Header("type").isEqualTo("order"))
        .To("direct://orders")
    .When(Header("type").isEqualTo("payment"))
        .To("direct://payments")
    .Otherwise()
        .To("seda://unknown")
.EndChoice()

// Lambda style — callback sub-routes
.Choice(c => c
    .When(Header("type").isEqualTo("order"), r => r.To("direct://orders"))
    .When(Header("type").isEqualTo("payment"), r => r.To("direct://payments"))
    .Otherwise(r => r.To("seda://unknown")))

// String expression style
.Choice()
    .When("header.type == 'order'")
        .To("direct://orders")
    .Otherwise()
        .To("seda://unknown")
.EndChoice()
```

## Processing

```csharp
// Async delegate with CancellationToken
.Process(async (exchange, ct) =>
{
    var order = exchange.In.Body as Order;
    exchange.In.Body = await EnrichOrder(order, ct);
})

// Sync delegate
.Process(exchange =>
{
    exchange.In.Headers["processed"] = true;
})

// Custom IProcessor class
.Process(new OrderValidationProcessor())
```

## Configuration

```csharp
builder.Services.Configure<RouteEngineOptions>(o =>
{
    o.EnableTelemetry = true;           // OpenTelemetry Activities
    o.EnableMetrics = true;             // Meters & Counters
    o.ShutdownTimeout = TimeSpan.FromSeconds(30);
    o.ThrowOnCompilationError = true;   // Fail-fast on invalid routes
});
```

### Property Placeholders

Endpoint URIs can externalise their configuration with Camel-style `{{key}}` / `{{key:default}}`
placeholders, resolved **once at route-compile time** — from `IConfiguration` (environment,
appsettings, user-secrets), then context properties (`SetProperty`) as a container-free fallback.
A URI without `{{` is untouched, and an unresolved placeholder without a default fails fast.

```csharp
From("{{orders.source}}")                         // amqp://broker/orders — from appsettings/env
    .To("http://api/{{tenant}}/pay");
From("amqp://broker:{{amqp.port:5672}}/orders");  // default used when the key is not configured
```

Because `IConfiguration` already layers environment, files and secrets, no `env:`/`sys:` prefix
functions are needed. Keeps secrets out of source and lets one route run unchanged across environments.

### Named connection factories

Every transport that opens a connection accepts `connectionFactory=<name>` on the endpoint URI,
resolved from the context registry — the canonical way to keep credentials out of URIs:

```csharp
context.AddToRegistry("prod-kafka", new KafkaConnectionFactory { Brokers = "b1:9092,b2:9092" });
// kafka://orders?connectionFactory=prod-kafka
```

The resolution contract, uniform across connectors:

1. **Explicit name** — resolved from the registry; a set-but-unknown name FAILS LOUD at
   startup (never a silent fallback: a typo and a working configuration must be
   distinguishable). Resolution happens BEFORE option validation, so the factory may be the
   only source of required values (brokers, tokens, credentials).
2. **URI options** — inline parameters when no name is set.
3. **Component/provider defaults** — where the connector has them (e.g. Firebase's
   `DefaultProjectId`/`DefaultCredentialPath`).
4. **Environment** — provider chains such as `GOOGLE_APPLICATION_CREDENTIALS` or the AWS
   default credentials chain.

### When endpoints and producers are created

A static target's endpoint — `To`, `WireTap`, `Enrich`, a load balancer's targets, a Scatter-Gather's fixed
recipients — is created when its route starts, as Camel resolves a static `to(...)` at startup: an unknown scheme, an
unknown URI parameter or a missing `{{key}}` stops the start (or skips the route, with
`ThrowOnCompilationError = false`) instead of failing the first message. A dynamic target (`ToD`, a recipient list, a
routing slip) is resolved per message. Producers are made when a message first needs one — unlike Camel, which starts
them with the route — so starting a context opens no connection to a broker a route has not used yet. A static target
matched by an intercept with `SkipSendToOriginalEndpoint()` is not created up front: the original may never be sent to
(a dry run whose real component is absent).

## Telemetry

Built-in OpenTelemetry — distributed tracing + metrics per route and step:

```csharp
// Scope style — wrap block of steps
.Traced("order-processing")
    .SetBody(JPath("$.order"))
    .Process(async (e, ct) => await Enrich(e, ct))
.EndTraced()

// Inline style — wrap single step
.Traced("validate", async (e, ct) => await Validate(e, ct))
.Metered("throughput", e => { e.In.Body = Transform(e); })
```

`RouteEngineOptions.EnableTelemetry` is on for the whole context. A single route can opt out with `.Tracing(false)`
(or `<route tracing="false">` in Route-XML), so a service route such as a health check or a metric summary opens no
span of its own.

To find a trace by a business key, put it on a span with `.SpanTag(key, value)` — a literal or an expression, as in
`SetHeader` (`<spanTag key="..." value="..."/>` or `expr="..."` in Route-XML). Jaeger and Tempo find a trace by an
attribute on any of its spans:

```csharp
.SpanTag("sap.order.id", XPath("/Ack/OrderNo"))                   // nearest Traced scope, else the route span
.SpanTag("sap.order.id", Header("orderNo"), SpanTarget.Inbound)   // the request's / message's own span
```

The key is checked when the route is built (lowercase, dot-separated, outside `redb.`, `http.`, `messaging.` and the
other reserved namespaces, never a secret's name). Nothing is set without a listener, on an unsampled span, with
`.Tracing(false)` (no fallback to the transport span), or on a span that has ended; a value that fails to evaluate is
skipped. The host's own span (ASP.NET Core) is never written. **The value is exported unfiltered** — no personal data,
no credentials; and a business key goes on a span, not in baggage, which travels to every next service.

## Behaviour Guarantees

A few behaviours are easy to get wrong from reading a single method, so they are stated here.

- **A stopped exchange.** `exchange.Stop()` stops the pipeline from running its remaining steps; it is not an
  exception. A `DoFinally()` block still runs, because it is the C# `finally` of `TryCatchProcessor`. A failure and a
  cancellation behave the same way.
- **The Out → In merge.** When a step writes `exchange.Out`, the pipeline merges it into `In` before the next step:
  the body and every header of `Out` are copied over, `ContentType` is copied only when `Out` carries one, and `Out`
  is cleared. The last step's `Out` is left in place for an InOut caller.
- **A Split branch.** It gets the part as its body, a copy of the parent's In headers plus `CamelSplitIndex`,
  `CamelSplitSize` and `CamelSplitComplete`, and the parent's properties (the dictionary is inherited, the values are
  shared). Writing a property on a branch does not reach the parent.
- **`now()` and `delay`.** `now()` returns a `DateTime` with `DateTimeKind.Utc`. `delay` accepts a `TimeSpan`, an
  `int` / `long` / `double` meaning milliseconds, a string that parses as a number of milliseconds, or a string that
  parses as `hh:mm:ss`.

## Validation

```csharp
// JSON Schema
.ValidateJsonSchema("""{"type":"object","required":["orderId"]}""")

// XSD
.ValidateXsd(xsdContent)

// Predicate
.Validate(e => e.In.Body is Order { Amount: > 0 }, "Amount must be positive")
```

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Header filter strategy


Apache Camel's `HeaderFilterStrategy`. A transport carried headers by a rule of its own, fixed in code: HTTP sent every
exchange header but a fixed list, or none (`bridgeHeaders`); the brokers sent every one. There was no way to send one
header, such as `Correlation-ID`, and keep the rest at home.

- `redb.Route.Abstractions.IHeaderFilterStrategy`: `FilterOutgoing` (an exchange header about to go into an outgoing
  message: a producer's request, a consumer's reply) and `FilterIncoming` (a header of an incoming message about to come
  into the exchange); `true` drops the header, as in Camel.
- `DefaultHeaderFilterStrategy`: per direction a list of names, a regular expression (the whole name) and name
  prefixes; `FilterOnMatch=false` turns it into a whitelist; `CaseInsensitive` (on by default, as in Camel),
  `LowerCase`, `AllowNullValues`. Declared as a bean in C# or in markup.
- An endpoint names its strategy with `headerFilterStrategy=#name`; it replaces the transport's default on every point
  of the endpoint. A name the registry does not hold fails when the producer or consumer is built.
- Every transport keeps its behaviour by default: its rule became its default strategy. The fields a transport sets
  itself (trace context, content type, the native properties of a message) and its protocol fields do not go through a
  strategy.
- Transports with the option: HTTP, Kafka, RabbitMQ, AMQP 1.0, Azure Service Bus, IBM MQ, gRPC, SQS/SNS, AS2. Each
  default is a public class (`KafkaHeaderFilterStrategy`, `RabbitMQHeaderFilterStrategy`, ...) to derive from. On a
  request-reply (RabbitMQ, AMQP, IBM MQ, gRPC) the strategy is asked about the reply too: a producer's strategy for
  the headers that come back, a consumer's for the headers it answers with.
- A transport's own guards stay outside every strategy: gRPC refuses a caller's header with a transport-reserved
  prefix unless `allowClientReservedHeaders=true`; AS2 never forwards another hop's credentials; an SQS
  `redbSqs.attr.X` header is an explicit request and is sent as attribute `X`.
- `direct-vm:` and `vm:`, the calls between contexts, take the option on the calling side (`.To`), as Apache Camel 3's
  `direct-vm` does; a consumer URI with it is refused. `direct-vm:` with a strategy runs the called context on a copy
  that shares the caller's DI scope and unit of work: the callee sees the headers the strategy lets out, and the
  caller keeps its own headers and takes the reply's body, the reply headers the strategy lets in, the properties, the
  exception and a stop. Unlike Camel, which replaces the caller's message with the reply, the caller's own headers
  survive the call. Without the option nothing changes: the callee works on the caller's own exchange. `vm:` filters
  the copy it hands over; Camel's `vm` has no such option.
- The markup package gate (`redb-route-xml check --bin`) holds a `headerFilterStrategy=#name` bean against
  `IHeaderFilterStrategy`, as it holds `repository=#name` against the repository interfaces.
- Transports that send only what a route names with a prefix (SOAP, S3, AS4, FCM, Mail, LLM, Redis Streams, MQTT) do
  not take a strategy: nothing crosses there without that consent.
  characterization tests that pin what every transport carried before (`*HeaderMappingCharacterizationTests`).
