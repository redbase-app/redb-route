# redb.Route.SignalR

SignalR transport for the **redb.Route** ESB framework.

Provides an embedded Kestrel-based SignalR Hub **consumer** (server) and two **producer** modes:
client (`HubConnection` to remote hub) and server (`IHubContext` broadcast to connected clients).

Supports JSON and MessagePack protocols, WebSocket / SSE / LongPolling transports, group management,
InOut exchange pattern, lifecycle events, auto-reconnect, and TLS.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.SignalR?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.SignalR)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

| | |
|---|---|
| **Scheme** | `signalr` |
| **NuGet** | `redb.Route.SignalR` |
| **Dependencies** | Microsoft.AspNetCore.App, Microsoft.AspNetCore.SignalR.Client 9.0.3 |
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

The consumer starts an embedded Kestrel server with a `RedbBridgeHub` — an internal bridge hub
that converts all SignalR invocations into redb.Route exchanges.

Clients call `Invoke("MethodName", arg1, arg2, ...)` on the hub. The hub creates an exchange
with the method name and arguments, processes it through the route pipeline, and optionally returns
the `Out` body as the method result (when `InOut` is enabled).

Lifecycle events (`OnConnectedAsync`, `OnDisconnectedAsync`) are also dispatched as exchanges
with the `redbSignalR.Event` header set to `"Connected"` or `"Disconnected"`.

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

---

## Authentication

JWT access token for client-mode producer:

```csharp
SignalR.Connect("host:5000/hub")
    .Method("Send")
    .AccessToken("eyJhbGciOiJIUzI1...")
```

Passed via `AccessTokenProvider` on the `HubConnection`.

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
| `messagePack` | bool | `false` | Use MessagePack protocol |
| `defaultGroup` | string | `null` | Auto-join group on connect (consumer) |
| `targetType` | string | `"All"` | Broadcast target: `All`, `Group`, `User`, `Connection` (server mode) |
| `targetGroup` | string | `null` | Default target group (server mode) |
| `ssl` | bool | `false` | Enable TLS |
| `sslCertPath` | string | `null` | PFX certificate path (consumer) |
| `sslCertPassword` | string | `null` | PFX certificate password |
| `reconnect` | bool | `false` | Auto-reconnect on disconnect (client mode) |
| `reconnectInterval` | int | `5000` | Reconnect interval in ms |
| `maxReconnectAttempts` | int | `0` | Max reconnect attempts (0 = unlimited) |
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
