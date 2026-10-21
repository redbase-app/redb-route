# redb.Route.SignalR

SignalR transport for the **redb.Route** ESB framework.

Provides an embedded Kestrel-based SignalR Hub **consumer** (server) and two **producer** modes:
client (`HubConnection` to remote hub) and server (`IHubContext` broadcast to connected clients).

Supports JSON and MessagePack protocols, WebSocket / SSE / LongPolling transports, group management,
InOut exchange pattern, lifecycle events, auto-reconnect, and TLS.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.SignalR?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.SignalR)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

| | |
|---|---|
| **Scheme** | `signalr` |
| **NuGet** | `redb.Route.SignalR` |
| **Dependencies** | Microsoft.AspNetCore.App, Microsoft.AspNetCore.SignalR.Client and .Protocols.MessagePack (the ASP.NET Core line of the target framework) |
| **Backplane** | [redb.Route.SignalR.StackExchangeRedis](../redb.Route.SignalR.StackExchangeRedis/README.md) |
| **Namespace** | `redb.Route.SignalR` |

---

## Quick Start

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteSignalR();

    // Hub server — accept connections, process method invocations
    route.AddRoute("signalr-server", r => r
        .From(SignalR.Hub("0.0.0.0:5000/chatHub").InOut())
        .Process(e =>
        {
            var method = e.In.Headers["redbSignalR.Method"];
            e.Out = new Message($"Echo: {e.In.Body}");
        }));

    // Client — connect to a remote hub and send messages
    route.AddRoute("signalr-client", r => r
        .From("direct:send")
        .To(SignalR.Connect("api.example.com:5000/chatHub")
            .Method("Send").Reconnect()));

    // Server broadcast — push to all connected clients of the local hub
    route.AddRoute("signalr-broadcast", r => r
        .From("direct:notify")
        .To(SignalR.Broadcast("0.0.0.0:5000/chatHub")
            .Method("Notify")));
});
```

---

## Architecture

### Consumer (Hub Server)

The consumer serves a `RedbBridgeHub` — an internal bridge hub that converts all SignalR
invocations into redb.Route exchanges — on the **shared Kestrel host**
(`redb.Route.Http.Hosting`), the same listener HTTP, gRPC, SOAP and AS2 routes use. So the hub
and the REST API it pushes for sit on one port, behind one proxy and one TLS termination:

```csharp
From("http://0.0.0.0:8080/api/orders")   // REST
From("signalr://0.0.0.0:8080/hub")       // push, same port
From("signalr://0.0.0.0:8080/admin")     // another hub, same port again
```

Clients call `Invoke("MethodName", arg1, arg2, ...)` on the hub. The hub creates an exchange
with the method name and arguments, processes it through the route pipeline, and optionally returns
the `Out` body as the method result (when `InOut` is enabled).

Lifecycle events (`OnConnectedAsync`, `OnDisconnectedAsync`) are also dispatched as exchanges
with the `redbSignalR.Event` header set to `"Connected"` or `"Disconnected"`.

### Hubs on a shared listener

Only a consumer endpoint (`SignalR.Hub(...)`, `From("signalr:...")`) puts a hub on the listener. A broadcast endpoint
(`SignalR.Broadcast(...)`, `mode=server`) sends through the hub of the consumer on the same address and maps nothing
of its own.

Every hub is served from a **service container of its own**, built when its consumer starts and disposed when it
stops. That container holds what SignalR keeps per hub: the connection manager, the lifetime manager behind
`Clients.All`, `Group` and `User`, the protocols, the hub options and the backplane. As a result:

- **A hub joins a listener that is already serving**, whoever started it. On a Tsak worker several route contexts take
  one `SharedHttpServerManager` from the worker's container, and the order they start in does not matter: the hub of
  one context goes onto the port the HTTP routes of another context already serve, and the other way round.
- **A hub leaves without touching anything else.** When its context stops or reloads, its endpoints come off the
  listener, its own connections are closed, and the other hubs and routes on the port keep their connections. The
  hub of the restarted context is a new one; nothing answers from the mapping of the previous instance.
- **Hubs are isolated from each other.** A broadcast of `/orders` (All, Group or User) reaches the clients of `/orders`
  only, even when `/alerts` on the same port has the same users in groups of the same name.
- **Protocols and transports belong to one hub.** `messagePack=true` and `transport=LongPolling` apply to that hub;
  another hub on the same port keeps its own.
- **The hub path wins over the route table.** A hub on `/hub/ui` is served by the hub even when an HTTP route of the
  same port matches `/hub/ui/negotiate`; when the hub leaves, the route table serves the path again.

What a hub does not change is the listener itself. Its pipeline (trusted proxies, CORS, the caller's principal,
WebSocket upgrades) is built when the listener starts, and so is its TLS: the first route or hub registered on a port
decides whether it is HTTPS. Every listener accepts WebSocket upgrades.

The hub's container does **not** see the services of the application hosting the route context. A hub request runs in
a scope of the hub's container, so `Context.GetHttpContext().RequestServices` resolves from there, not from the
application; what the pipeline needs from the application it takes from the exchange (`Exchange.Create` with the
consumer's scope factory), and what the hub itself needs is registered with `ConfigureHubServices`.

### Producer — Client Mode (default)

Uses `HubConnection` to connect to a remote SignalR hub and invoke methods.

Two sub-modes:
- **Bridge** (default, `bridge=true`) — calls `Invoke(method, args)` on the bridge hub entry point.
  Used when connecting to another redb.Route hub.
- **Direct** (`bridge=false`) — calls the hub method directly by name. Used when connecting to
  external (third-party) SignalR hubs with real named methods.

### Producer — Server Mode

Uses `IHubContext<RedbBridgeHub>` to broadcast messages to clients connected to the local hub.
Requires a consumer running on the same endpoint. Target audience configurable: All, Group, User, Connection.

---

## URI Format

```
signalr:host:port/hubPath?option=value&...
```

**Consumer** (hub server):
```
signalr:0.0.0.0:5000/chatHub?inOut=true&messagePack=true&defaultGroup=lobby
```

**Producer — Client** (connect to remote):
```
signalr:api.example.com:5000/chatHub?mode=client&method=Send&reconnect=true
```

**Producer — Server** (broadcast to local clients):
```
signalr:0.0.0.0:5000/chatHub?mode=server&method=Notify&targetType=group&targetGroup=room1
```

---

## DSL

```csharp
using redb.Route.SignalR;

// Consumer — start hub server
SignalR.Hub("0.0.0.0:5000/chatHub")
    .InOut()
    .MessagePack()
    .DefaultGroup("lobby")

// Producer — connect to remote hub (client mode)
SignalR.Connect("api.example.com:5000/chatHub")
    .Method("Send")
    .Reconnect(intervalMs: 3000, maxAttempts: 10)
    .AccessToken("jwt-token")
    .Ssl()

// Producer — connect to external (non-redb) hub
SignalR.Connect("external.host:5000/hub")
    .Method("SendMessage")
    .Direct()

// Producer — broadcast to local hub clients (server mode)
SignalR.Broadcast("0.0.0.0:5000/chatHub")
    .Method("Notify")
    .Group("room1")
```

The builder implements `implicit operator string` — pass directly to `.From()` / `.To()`.

---

## Exchange Pattern

### InOnly (default)

Client invocations are fire-and-forget. The hub returns `null`.

### InOut

When `inOut=true`, the consumer returns the `exchange.Out.Body` as the hub method result:

```csharp
route.AddRoute("rpc", r => r
    .From(SignalR.Hub("0.0.0.0:5000/rpcHub").InOut())
    .Process(e =>
    {
        var input = (long)e.In.Body!;
        e.Out = new Message(input * 2);
    }));
```

Client calls `Invoke("Multiply", 21)` → receives `42`.

---

## Group Management

### Default Group

Auto-join all connections to a group on connect:

```csharp
SignalR.Hub("0.0.0.0:5000/chatHub").DefaultGroup("lobby")
```

### Dynamic Group Management

Set headers on the exchange `Out` (or `In`) to add/remove the current connection from groups:

| Header | Action |
|---|---|
| `redbSignalR.AddToGroup` | Add connection to the named group |
| `redbSignalR.RemoveFromGroup` | Remove connection from the named group |

```csharp
.Process(e =>
{
    e.Out = new Message("Joined room");
    e.Out.Headers["redbSignalR.AddToGroup"] = "premium-users";
})
```

### Groups do not survive a reconnect

A group membership belongs to a connection, and a reconnect is a new connection with a new id, so
whatever the client joined is gone. SignalR does not restore it and neither can a backplane —
nobody remembers who was in what. Two ways to put it back, both from the connector:

```csharp
// Everyone lands in the same group: declare it, and every connection joins on connect.
SignalR.Hub("0.0.0.0:5000/chatHub").DefaultGroup("lobby")

// Membership depends on who is connecting: rejoin on the Connected event, which the hub
// dispatches as an exchange like any other.
route.AddRoute("rejoin", r => r
    .From(SignalR.Hub("0.0.0.0:5000/chatHub"))
    .Filter(Header(SignalRHeaders.Event).isEqualTo("Connected"))
    .Process(async (e, ct) =>
    {
        var user = e.In.GetHeader<string>(SignalRHeaders.UserId);
        e.Out = new Message(null);
        e.Out.Headers["redbSignalR.AddToGroup"] = await groups.ResolveFor(user);
    }));
```

---

## Server-Mode Broadcasting

Target audiences for server-mode producer:

| TargetType | Header Override | Description |
|---|---|---|
| `All` (default) | `redbSignalR.Target = "All"` | Send to all connected clients |
| `Group` | `redbSignalR.Group = "name"` | Send to a specific group |
| `User` | `redbSignalR.TargetUser = "userId"` | Send to a specific user |
| `Connection` | `redbSignalR.TargetConnection = "connId"` | Send to a specific connection |

```csharp
// Broadcast to a specific group
route.AddRoute("group-notify", r => r
    .From("direct:alert")
    .SetHeader("redbSignalR.Group", "admins")
    .To(SignalR.Broadcast("0.0.0.0:5000/chatHub")
        .Method("Alert")));
```

Server mode pushes through the hub of the consumer serving the same address, so **the consumer
has to be running**: a producer that finds none refuses to start rather than pushing into
nothing. In a route context that happens by itself — consumers start with the context, producers
resolve lazily on the first message.

---

## Reconnect (Client Mode)

```csharp
SignalR.Connect("host:5000/hub")
    .Method("Send")
    .Reconnect(intervalMs: 5000, maxAttempts: 0)  // 0 = unlimited
```

Uses a fixed-interval retry policy. When connection drops and `Reconnect` is enabled,
the producer automatically re-establishes the connection before sending.

---

## TLS

### Consumer (Server)

```csharp
SignalR.Hub("0.0.0.0:5443/hub")
    .Ssl()
    .SslCertPath("/certs/server.pfx")
    .SslCertPassword("password")
```

Configures Kestrel to listen with HTTPS using the PFX certificate.

### Producer (Client)

```csharp
SignalR.Connect("host:5443/hub")
    .Ssl()
```

Switches the connection URL scheme to `https://`.

`ssl=true` on a consumer **requires** `sslCertPath`: without it the listener would be plain HTTP
while the log said `https://`, so the consumer refuses to start instead.

To reach a hub with a self-signed certificate (staging), say so out loud:

```csharp
SignalR.Connect("staging:5443/hub").Ssl().TrustAllCertificates()
```

It is never implied by anything else, and it disables certificate validation for the whole
transport — negotiate, redirects and the WebSocket upgrade alike.

---

## Authentication

### Producer (client) — sending a token

```csharp
SignalR.Connect("host:5000/hub")
    .Method("Send")
    .AccessToken("eyJhbGciOiJIUzI1...")
```

Passed via `AccessTokenProvider` on the `HubConnection`.

### Consumer (hub) — validating one

The process hosting a route context is a generic host, not an ASP.NET application, so there is no
`AddAuthentication`/`AddJwtBearer` to hang the hub off. The host supplies a delegate instead:

```csharp
services.AddRedbRouteSignalR(o =>
{
    o.Authenticate = async ctx =>
    {
        // A browser cannot set headers on a WebSocket handshake, so the token normally arrives
        // in the query string; a service client will use the header. Read both.
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ")
            ? header["Bearer ".Length..]
            : ctx.Request.Query["access_token"].ToString();

        return await myJwtValidator.ValidateAsync(token);   // null → 401
    };
});
```

Returning null rejects the handshake with 401 **before** the connection is upgraded. The
principal's `NameIdentifier` claim becomes SignalR's `UserIdentifier`, which is what makes
`Clients.User(...)` (`targetType=User`) and the `redbSignalR.UserId` header work. The principal itself
is on every exchange the hub produces (`ExchangePrincipal.Get(exchange)`); an anonymous connection
carries none. Build the identity with an authentication type (`new ClaimsIdentity(claims, "Bearer")`):
code that reads the principal treats an identity that is not authenticated as anonymous.

Without this delegate, a caller identified by the shared host
(`AddRedbRouteHttpHosting(o => o.ResolvePrincipal = ...)`) is handed to SignalR the same way, but an
anonymous handshake is not rejected. When the delegate is set, it takes precedence.

---

## Scale-out: a backplane is mandatory with more than one replica

A hub keeps its connections and groups in the memory of one process. With two replicas behind a
load balancer, a broadcast from one reaches only the clients attached to that one — the other
replica's clients hear nothing.

For Redis, add [redb.Route.SignalR.StackExchangeRedis](../redb.Route.SignalR.StackExchangeRedis/README.md):

```csharp
services.AddRedbRouteSignalR(o => o.UseRedisBackplane("redis:6379"));
```

It keys the Redis channels of every hub on its path, so the hubs of one listener stay apart on one Redis, and it
refuses to start a second hub of the process that would share the channels of another.

The connector itself does not depend on Redis or Azure SignalR. `ConfigureHubServices` is the seam for any other
backplane, protocol or service a hub needs; it runs once per hub, with that hub's description:

```csharp
services.AddRedbRouteSignalR(o => o.ConfigureHubServices((services, hub) =>
    services.AddSignalR().AddStackExchangeRedis("redis:6379", redis =>
        redis.Configuration.ChannelPrefix = RedisChannel.Literal("myapp" + hub.Path))));
```

A backplane added by hand is the host's responsibility: SignalR names its channels after the hub class, every hub of
this connector is the same class, so without a prefix that includes `hub.Path` the hubs of one listener would see each
other's broadcasts through Redis.

The container `ConfigureHubServices` fills belongs to the hub and does **not** see the services of the application
hosting the route context, so anything the hub needs must be registered here.

---

## Configuration Reference

| Parameter | Type | Default | Description |
|---|---|---|---|
| `host` | string | `"0.0.0.0"` | Bind/connect host (parsed from URI path) |
| `port` | int | `5000` | Bind/connect port (parsed from URI path) |
| `mode` | enum | `Client` | Producer mode: `Client` or `Server` |
| `method` | string | `null` | Hub method to invoke/filter |
| `inOut` | bool | `false` | Request-response exchange pattern |
| `bridge` | bool | `true` | Route through bridge hub (client mode). `false` = direct method calls |
| `transport` | enum | `WebSockets` | `WebSockets`, `ServerSentEvents`, `LongPolling` |
| `messagePack` | bool | `false` | Register the MessagePack protocol beside JSON (JSON stays the base, so existing clients keep working) |
| `defaultGroup` | string | `null` | Auto-join group on connect (consumer) |
| `targetType` | string | `"All"` | Broadcast target: `All`, `Group`, `User`, `Connection` (server mode) |
| `targetGroup` | string | `null` | Default target group (server mode) |
| `ssl` | bool | `false` | Enable TLS. On a consumer, requires `sslCertPath` |
| `sslCertPath` | string | `null` | PFX certificate path (consumer) |
| `sslCertPassword` | string | `null` | PFX certificate password |
| `trustAllCertificates` | bool | `false` | Producer: accept any server certificate (self-signed staging). Explicit, never implied |
| `reconnect` | bool | `false` | Auto-reconnect on disconnect (client mode) |
| `reconnectInterval` | int | `5000` | Reconnect interval in ms |
| `maxReconnectAttempts` | int | `0` | Max reconnect attempts (0 = unlimited: `WithAutomaticReconnect` keeps retrying in the background, which is its normal mode) |
| `accessToken` | string | `null` | JWT token for client auth |

---

## Headers Reference

### Common (set by consumer)

| Header | Description |
|---|---|
| `redbSignalR.Method` | Hub method name |
| `redbSignalR.ConnectionId` | SignalR connection ID |
| `redbSignalR.UserId` | Authenticated user ID |
| `redbSignalR.Event` | Lifecycle event: `"Connected"` / `"Disconnected"` |
| `redbSignalR.HubPath` | Hub path (e.g. `/chatHub`) |
| `redbSignalR.Protocol` | `"json"` or `"messagepack"` |
| `redbSignalR.Ssl` | `"True"` / `"False"` |
| `redbSignalR.DisconnectError` | Error message from disconnection |

### Producer Targeting

| Header | Description |
|---|---|
| `redbSignalR.Target` | Broadcast audience: `"All"`, `"Group"`, `"User"`, `"Connection"` |
| `redbSignalR.Group` | Target group name |
| `redbSignalR.TargetConnection` | Target connection ID |
| `redbSignalR.TargetUser` | Target user ID |

### Group Management (post-processing commands)

| Header | Description |
|---|---|
| `redbSignalR.AddToGroup` | Add current connection to specified group |
| `redbSignalR.RemoveFromGroup` | Remove current connection from specified group |

---

## DI Registration

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteSignalR();
    // ...
});
```

Registers the `SignalRComponent` (scheme `signalr`).

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new SignalRConnectionFactory
{
    AccessToken = secrets.HubToken,
});
// signalr://https://hub.internal/notify?connectionFactory=prod
```

## Concurrency limits

SignalR concurrency is connections × invocations, so the levers differ from plain HTTP:

| Parameter | Default | Description |
|---|---|---|
| `maxConnections` | `0` (unlimited) | Max hub connections; an over-limit connection is aborted at OnConnected — before the `Connected` lifecycle event reaches the pipeline — and counted in the endpoint's `Rejected` |
| `maxParallelInvocationsPerClient` | `0` (SignalR default = 1) | SignalR's own per-client invocation parallelism |

With the defaults one client's invocations are serial (SignalR's own default) and the number of
clients is unbounded; `maxConnections` is the coarse lever, the per-client setting the fine one.

## Tracing

On the `redb.Route` activity source (`AddSource("redb.Route")`), with `messaging.system` = `signalr` and
`redb.route.endpoint`:

- **Consumer.** One `Consumer` span, `signalr {hubPath} receive`, per exchange that reaches the route: each invocation
  and the `Connected` / `Disconnected` events. A hub message carries no headers, so the span is a root; when the client
  sent `traceparent` with its connection request, every span of that connection links that context. The connection
  itself, which may live for hours, is never the parent. A failed route marks the span an error; the connection's own
  abort cancelling the route does not.
- **Producer.** One `Producer` span per send, `signalr {mode} {method}`; an error when the send fails, unless our own
  token cancelled it. No trace context is written to the hub message.
- `RouteEngineOptions.EnableTelemetry = false` opens neither span.
