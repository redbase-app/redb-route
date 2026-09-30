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

### Request validation

Parameters are declared on the verb with `Param(...)` and always go to the OpenAPI document. They are
enforced only when `ClientRequestValidation` is on (Camel's `clientRequestValidation`), for the whole
declaration or per verb, so parameters written down for the document alone never start refusing
requests:

```csharp
this.Rest("/api/items", o => { o.Port = 8080; o.ClientRequestValidation = true; o.ErrorHandler = "#restErrors"; })
    .Get("/{id}").Produces("application/json")
        .Param("id", RestParamType.Path, dataType: RestParamDataType.Integer)
        .Param("limit", RestParamType.Query, required: true, dataType: RestParamDataType.Integer, description: "Page size")
        .Param("X-Tenant", RestParamType.Header, required: true)
        .To("direct:get-item");
```

In order, before the route runs:

| Check | Answer |
|---|---|
| `Content-Type` against `Consumes` (runs with validation off too) | 415 |
| `Accept` against `Produces`: no header admits everything; the most specific matching range decides; `q=0` refuses | 406 |
| a required parameter is missing, or a value does not convert to its `dataType` (`integer`, `number`, `boolean`; invariant culture) | 400, naming the parameter |

A path parameter must be a segment of the template and is always required. The refusal body is the
reason as `text/plain`. `ErrorHandler` names a registered `IProcessor` that writes it instead: it
finds the code, reason and parameter in the `RestErrorProperties` exchange properties, and may change
the status. A name that is not registered stops the start.

In Route-XML:

```xml
<rest path="/api/items" port="8080" clientRequestValidation="true" errorHandler="#restErrors">
  <get path="/{id}" produces="application/json" to="direct:get-item">
    <param name="id" type="path" dataType="integer"/>
    <param name="limit" required="true" dataType="integer" description="Page size"/>
    <param name="X-Tenant" type="header" required="true"/>
  </get>
</rest>
```

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

## Credentials: outbound and inbound

`authScheme`, `username`, `password` and `authToken` are what a **producer** sends. Written on a consumer URI they
are refused at startup — naming the parameters, never their values — instead of starting an endpoint that looks
protected and accepts everyone. A connection factory a consumer references for its TLS certificate may still carry
producer credentials; only what the consumer URI writes itself is taken as intent.

A **consumer** checks its callers with `inboundAuth` (refused on a producer the same way):

| `inboundAuth` | Also needs | A refused request |
|---|---|---|
| `basic` | `inboundUsername`, `inboundPassword` (from configuration: `{{api.password}}`) | 401, `WWW-Authenticate: Basic realm="…", charset="UTF-8"` |
| `bearer` | `tokenValidator=#name`, a registered `IHttpTokenValidator` | 401, `WWW-Authenticate: Bearer realm="…"`, with `error="invalid_token"` when the validator refused the token |

```csharp
context.AddToRegistry("tokens", new MyTokenValidator());     // IHttpTokenValidator
r.From("http://0.0.0.0:8080/orders?inboundAuth=bearer&tokenValidator=#tokens&inboundRealm=orders")
    .Process(e => { var who = ExchangePrincipal.Get(e)?.Identity?.Name; /* ... */ });
```

A refused request never reaches the route. An accepted one reaches it with the principal on the exchange
(`ExchangePrincipal.Get`) and **without** the `Authorization` header, so a route that logs or forwards its headers does
not carry the credentials on. Basic compares both halves in constant time. `inboundRealm` (default `redb`) is the realm
of the challenge. A half-declared check — credentials without `inboundAuth`, `basic` without a password, `bearer`
without a validator, an unregistered validator name — stops the start.

**Boundary.** The connector does not read tokens. JWT parsing, signing keys and their rotation, audiences, scopes and
roles belong to the identity provider's library (redb.Identity, or any OpenID Connect client), plugged in through
`IHttpTokenValidator`; access policies beyond "who is this" belong to the route. Do not add a token parser here.

`Rest(...)` takes the same check for every route of the declaration (`RestOptions.InboundAuth`, `InboundUsername`,
`InboundPassword`, `InboundRealm`, `TokenValidator`; in Route-XML the attributes of the same names on `<rest>`). The
OpenAPI document is behind it too.

## Request headers and the response

A consumer puts the request's headers on the exchange and, for `inOut=true`, writes the message's headers into the
response. Three rules keep a client from shaping that response:

1. **Response fields never come in.** `Set-Cookie`, `Location`, `WWW-Authenticate`, `Proxy-Authenticate`,
   `Authentication-Info`, `Proxy-Authentication-Info`, `Retry-After`, `Server`, `Age`, `ETag`, `Accept-Ranges` and
   `Vary` in a request are dropped before the exchange is built (`HttpHeaders.ResponseOnlyHeaders`). Stricter than
   Camel's inbound filter, which removes only its own headers: a request has no use for them.
2. **The request is not echoed.** A header still holding the value the client sent is not written back.
3. **What the route wrote goes out.** A header the route set is written even when the client sent one with the same
   name: a request carrying `Cache-Control` cannot remove the route's `Cache-Control: no-store`.

A processor that builds a response header from a request value — copies a header, reflects a parameter — answers for
that value itself: validate or encode it before writing. Writing the request's own value back under its own name is
an echo and is held back by rule 2.

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

## Tracing

On the `redb.Route` activity source (`AddSource("redb.Route")`):

- **Consumer.** One `Server` span per request, named `{method} {path}`, over the whole request, refused ones included.
  Its parent is the host's ASP.NET Core span when the application instruments ASP.NET Core, otherwise the caller's
  `traceparent`; without one it is a root. The caller's baggage is back on it, and the route's spans are its children.
  It carries `redb.route.endpoint` and `http.response.status_code`, and is an error for a 5xx.
- **Producer.** One `Client` span per call, named `HTTP {method}`, with `http.response.status_code`; an error for a
  4xx, a 5xx or a failed call. The request carries the context of this span: a `traceparent` the header bridge copied
  from an incoming request is replaced, as it names the previous hop.
- `RouteEngineOptions.EnableTelemetry = false` opens neither span. A context that came in still goes out.
