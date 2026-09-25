# redb.Route.Http.Hosting

Shared Kestrel HTTP hosting infrastructure for the [redb.Route](../redb.Route) ESB framework.

Provides `SharedHttpServerManager` — a multiplexing HTTP server (one Kestrel per `host:port`, many routes)
used by HTTP-based transports (`redb.Route.Http`, `redb.Route.As2`, …). Extracting it here lets those
connectors share one server manager **without depending on each other**: register it once with
`services.AddRedbRouteHttpHosting()` (idempotent), and every connector resolves the same singleton — so
an HTTP route and an AS2 route in the same worker share one Kestrel and never fight over a port.

Standalone hosting only — depends on the ASP.NET runtime, not on redb.Route core or any connector.

## How a connector finds the manager

Every HTTP-based connector — `http`, `signalr`, `ws`, `soap`, `grpc`, `as2` — resolves it in one order:

1. the manager assigned to the component (`AddRedbRouteHttp()`, `AddRedbRouteSignalR()`, … do this);
2. the `SharedHttpServerManager` the route context was given — `context.Resolve<T>()`, which reads the
   context's own services (`AddService`) first and then its DI container;
3. what the connector does on its own — a private manager for `signalr`, `ws`, `soap`, `grpc` and `as2`,
   so a hand-built component stays usable; a refusal for `http`, because an `http:` consumer binds a port
   and which port it binds is the host's decision.

Step 2 is what a module host needs: it builds a context per module and adds components by scanning, so
nothing assigns the manager, while the one shared instance is already in the container. It also works
the other way round — a host with no container at all can leave the manager on the context itself:

```csharp
context.AddService(typeof(SharedHttpServerManager), manager);
```

The lookup is lazy, so the manager may be supplied before or after the components are added.

## TLS: asking for it selects the socket

A listener registered with `ssl: true` resolves its server certificate from, in order:

1. the endpoint — `sslCertPath` / `sslCertPassword` on the route URI;
2. a named connection factory, so the password stays out of the URI;
3. the host default set here.

```csharp
services.AddRedbRouteHttpHosting(o => o.Tls.DefaultCertificatePath = "/certs/server.pfx");
// or an already-loaded certificate:
services.AddRedbRouteHttpHosting(o => o.Tls.DefaultCertificate = cert);
```

This is the shape Camel gives global `SSLContextParameters` and Spring Boot gives SSL bundles: a
certificate on the endpoint is an override, not a requirement. Nothing here turns TLS on — `ssl`
stays an explicit per-endpoint decision; the host only answers "with which certificate".

**A listener that asks for TLS and finds no certificate anywhere refuses to bind.** It does not
fall back to a plaintext socket, which is what nginx, httpd, Jetty, Spring Boot and Kestrel's own
`UseHttps()` all do, and for the same reason: an open port behind an `https://` banner is not
"TLS off", it is a silent downgrade that operators cannot see.

## Trusted proxies

Behind a reverse proxy the socket peer is the proxy, and the client's address and scheme travel in
`X-Forwarded-For` / `X-Forwarded-Proto`. Which proxies to believe is a property of the process, so it
is set once on the host and applies to every listener and every consumer on it:

```csharp
services.AddRedbRouteHttpHosting(o => o.TrustedProxies.Add("10.0.0.5").Add("10.1.0.0/16"));
// or, constructing the manager by hand:
var hosting = new HttpHostingOptions();
hosting.TrustedProxies.Add("10.0.0.5");
var manager = new SharedHttpServerManager(hosting);
```

Rules, in the order they apply:

| Situation | Outcome |
|---|---|
| No proxy listed | Headers ignored, socket peer is the client. The default. |
| Peer not in the list | Headers ignored: nothing in them was written by anyone trusted. |
| Peer trusted, `X-Forwarded-For` present | Walked from the right past every listed proxy; the first address that is not one is the client. A chain of any length resolves. |
| An entry does not parse | The walk stops and the socket peer is kept. Skipping would reach the client-controlled left part. |
| Every entry is a trusted proxy | Socket peer kept (a proxy calling through itself, a health check). |
| `X-Forwarded-Proto` present, peer trusted | `Request.Scheme` rewritten, read in step with the address; `ForwardScheme = false` turns this off. |

The originals are kept in `HttpContext.Items` under `SharedHttpServerManager.OriginalRemoteAddressItem`
and `OriginalSchemeItem`. Keep the list to the proxies you operate: a network an attacker can send from
lets them choose their own client address. `X-Forwarded-Host` and the RFC 7239 `Forwarded` header are
not handled.

The walk is `ForwardedHeaderResolver`, a pure function with no `HttpContext` in it, so it can be
unit-tested on strings and reused by a host that is not Kestrel.

## Caller identity

Who sent a request is decided the same way as which proxies to believe: once for the process, applied
to every listener and every consumer on it.

```csharp
services.AddRedbRouteHttpHosting(o => o.ResolvePrincipal = ctx =>
    myTokenValidator.ValidateAsync(ctx.Request.Headers.Authorization.ToString()));
```

The resolver returns a `ClaimsPrincipal`, or `null` for an anonymous caller. Build the identity with an
authentication type (`new ClaimsIdentity(claims, "Bearer")`): code that reads the principal, the LLM
tool claims source among it, treats an identity that is not authenticated as anonymous. The HTTP, gRPC, SOAP, AS2,
WebSocket and SignalR consumers put the result on the exchange, and a route reads it with
`ExchangePrincipal.Get(exchange)`. That is Camel's `Exchange.AUTHENTICATION` shape: an exchange property,
so a caller cannot send it as a header, and every child exchange (a split part, a sub-route, a tool
call) inherits it.

| Situation | Outcome |
|---|---|
| No resolver | Nothing runs, exchanges carry no identity. The default. |
| Resolver returns a principal | Kept in `HttpContext.Items` under `SharedHttpServerManager.PrincipalItem`, then put on the exchange. |
| Resolver returns `null` | The request is served without an identity. Turning anonymous callers away is a route's decision: one port carries routes with different requirements. |
| Resolver throws | 500, the route does not run, the error is logged, and the exception text never reaches the caller. A resolver that cannot decide must not downgrade the caller to anonymous; return `null` yourself when that is the right answer. |
| CORS preflight | Answered before the resolver runs. |
| WebSocket or SignalR with its own `Authenticate` | The transport's hook decides on its paths. |

The resolver runs after trusted-proxy resolution, so a check that looks at the address sees the client,
not the proxy. Under DI resolver failures go to the host's logger factory; a manager built by hand logs
them only when given a logger (`new SharedHttpServerManager(options, logger)`). A component added by
hand without a `ServerManager` falls back to a private manager with default options and does not see this
setting.

Part of the redb.Route family.
