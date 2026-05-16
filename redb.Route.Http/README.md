# redb.Route.Http

HTTP/HTTPS transport for redb.Route. HttpClient-based producer (outbound requests) and Kestrel-based consumer (webhook receiver) with CORS, auth, and streaming.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Http?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Http)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

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

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
