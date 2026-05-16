# redb.Route

Core ESB engine — async-first message routing with fluent C# DSL, 24 EIP processors, unified expression engine with 17 predicates, OpenTelemetry telemetry, and built-in components (Direct, SEDA, Timer, Mock, Log).

[![NuGet](https://img.shields.io/nuget/v/redb.Route?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

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
});
```

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

        // Exception handling with redelivery
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

## Processors (24 EIP Patterns)

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
| `WireTapProcessor` | `.WireTap(...)` | Wire Tap |
| `EnrichProcessor` | `.Enrich(...)` | Content Enricher |
| `ToProcessor` | `.To(...)` | Message Endpoint |
| `DelegateProcessor` | `.Process(...)` | Custom Processor |
| `LogProcessor` | `.Log(...)` | Logging |
| `RichLogProcessor` | `.Log().Message(...)` | Structured Logging |
| `DelayProcessor` | `.Delay(...)` | Delayer |
| `LoopProcessor` | `.Loop(...)` | Loop |
| `ThrottleProcessor` | `.Throttle(...)` | Throttler |
| `CircuitBreakerProcessor` | `.CircuitBreaker(...)` | Circuit Breaker |
| `RetryProcessor` | `.Retry(...)` | Retry |
| `DeadLetterProcessor` | `.DeadLetterChannel(...)` | Dead Letter Channel |
| `OnExceptionProcessor` | `.OnException<T>()` | Exception Handler |
| `TryCatchProcessor` | `.DoTry()` | Try-Catch |
| `IdempotentConsumerProcessor` | `.IdempotentConsumer(...)` | Idempotent Consumer |

## Error Handling

```csharp
// Retry with delay
.Retry(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(1))

// Dead Letter Channel
.DeadLetterChannel("seda://failed")

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

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
