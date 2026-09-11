# redb.Route.Http

HTTP/HTTPS transport for redb.Route. HttpClient-based producer (outbound requests) and Kestrel-based consumer (webhook receiver) with CORS, auth, and streaming.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Http?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Http)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Http
```

## Usage

### Fluent DSL

```csharp
using redb.Route.Http.Fluent;

// Outbound HTTP call (producer)
From("direct://send")
    .To(Http.Post("api.example.com/orders")
        .Timeout(5000)
        .BearerAuth()
        .ContentType("application/json"));

// Webhook receiver (consumer)
From(Http.Listen("/webhooks/orders")
        .Host("0.0.0.0").Port(8080)
        .Methods("POST")
        .Cors("https://app.example.com")
        .MaxRequestBodySize(1_048_576))
    .Log("Webhook received: ${body}")
    .To("direct://process");

// REST methods shorthand
From("direct://get-data")
    .To(Http.Get("api.example.com/status").NoThrowOnError());

From("direct://update")
    .To(Http.Put("api.example.com/orders/${header.orderId}"));

From("direct://remove")
    .To(Http.Delete("api.example.com/orders/${header.orderId}"));

// HTTPS
From("direct://secure-call")
    .To(Https.Post("api.example.com/data")
        .BearerAuth()
        .AuthToken("${property.jwt}"));

// Named parameters — {name} in URL resolved from .Param() at runtime
From("direct://get-order")
    .To(Http.Get("api.example.com/orders/{orderId}")
        .Param("orderId", Header("orderId")));

// Multiple named parameters + IExpression values
From("direct://user-orders")
    .To(Http.Get("api.example.com/users/{userId}/orders/{status}")
        .Param("userId", Header("userId"))
        .Param("status", Constant("active")));
```

> `${...}` expressions in URL and options are resolved per message at runtime.
> `{name}` placeholders are resolved from `.Param()` bindings — values are URL-encoded automatically.
> In fluent DSL, pass the path **without** `http://` / `https://` — the scheme is set by `Http.` vs `Https.`.

### Raw URI (non-fluent)

```csharp
// Raw URI strings include the full scheme — ${...} resolved per message
From("direct://update")
    .To("https://api.example.com/orders/${header.orderId}?method=PUT");

// Fully dynamic URL — host, port, path all from expressions
From("direct://proxy")
    .To("https://${header.targetHost}:${header.targetPort}/api/${header.resource}?method=POST");
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **HTTP Methods** | `Http.Get()`, `Http.Post()`, `Http.Put()`, `Http.Delete()`, `Http.Patch()`, `Http.Head()` |
| **Consumer** | `Http.Listen()`, `.Host()`, `.Port()`, `.Methods()`, `.Cors()`, `.CorsCredentials()`, `.MaxRequestBodySize()`, `.Protocol()`, `.ResponseCode()`, `.InOut()`, `.StreamRequest()` |
| **Auth** | `.BasicAuth(user, pass)`, `.BearerAuth()`, `.AuthToken()` |
| **SSL** | `.SslCert(path, pass?)` |
| **Producer** | `.Timeout()`, `.ContentType()`, `.NoThrowOnError()`, `.NoBridgeHeaders()`, `.NoFollowRedirects()`, `.MaxRedirects()`, `.NoCopyResponseHeaders()`, `.PreserveHostHeader()` |
| **Parameters** | `.Param(name, value)`, `.Param(name, IExpression)` — bind `{name}` URL placeholders |

> Most builder methods (Timeout, BasicAuth, AuthToken, MaxRedirects, Host, Port, MaxRequestBodySize, SslCert, ResponseCode) accept both constant values and `IExpression` for runtime resolution.

## Schemes

Both `http` and `https` schemes are supported. Use `Https.Get(...)` / `Https.Post(...)` for TLS endpoints.

## Routing precedence (shared server)

Multiple consumers can register routes on the same `(host, port)` — they share one
Kestrel server. When several routes match the same request path, the most **specific**
path wins, not the first one registered:

1. Concrete/literal paths (`/api/echo`) before route-parameter paths (`/api/{id}`).
2. Fewer route parameters before more.
3. Catch-all templates (`/{**path}`) are tried **last**.
4. Registration order breaks ties (first-registered wins among equal specificity).

This means a concrete path and a catch-all fallback can coexist on one port — e.g. a
dispatcher mounted on `/{**path}` plus a dedicated `/api/echo` route — and `/api/echo`
is routed to the specific handler. The same ordering drives per-route CORS dispatch.


## REST DSL

A declarative facade over the same consumer and shared host (Apache Camel `rest()` parity):

```csharp
this.Rest("/api/orders", o => { o.Port = 8080; o.BindingMode = RestBindingMode.Json; })
    .Get("/{id}").Produces("application/json").OutType<Order>().To("direct:get-order")   // header.id, header.query.page
    .Post().Consumes("application/json").Type<Order>().To("direct:create-order")          // 415 on another Content-Type
    .Put("/{id}/status").To("direct:set-status")
    .Delete("/{id}").Route().Process(e => e.In.Body = null);                              // inline steps, 204
```

Every verb is an ordinary route `From("http://host:port/base/path?methods=GET&inOut=true")`. Path
parameters arrive as `header.id`, query as `header.query.*`; the status code is the consumer's
`redbHttp.ResponseCode` header; no body and no status is 204. An OpenAPI 3.0.3 document is served at
`{basePath}/openapi.json` (`RestOptions.OpenApi` / `OpenApiPath`). Several `Rest(...)` declarations
share a port.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new HttpConnectionFactory
{
    AuthScheme = "Bearer",
    AuthToken = secrets.ApiToken,
});
// http://api.internal/orders?connectionFactory=prod
```

## Concurrency limits

Kestrel executes as many handlers as requests arrive; without a limit a route has no ceiling.
The admission limit caps concurrent pipeline executions per endpoint and sheds the overflow
BEFORE any pipeline work (load shedding, not backpressure):

| Parameter | Default | Description |
|---|---|---|
| `maxConcurrentRequests` | `0` (unlimited) | Max concurrent pipeline executions |
| `requestQueueLimit` | `0` | Requests over the limit that WAIT (FIFO) instead of being rejected |
| `rejectStatusCode` | `429` | Status for a shed request |
| `retryAfterSeconds` | `1` | `Retry-After` header value; `0` = do not send |

A shed request is answered before an exchange exists: it appears in the endpoint's `Rejected`
counter, not in `MessagesIn` or `Errors`. The limit is strictly per endpoint — other routes on
the same listener keep their own budget. For "slow down but do not drop" semantics use
`.Threads(n)` in the route instead; the two compose (the limit sheds at the door, Threads
paces inside).
