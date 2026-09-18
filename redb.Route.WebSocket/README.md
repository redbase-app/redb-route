# redb.Route.WebSocket

WebSocket transport for redb.Route. ClientWebSocket producer and Kestrel-based WebSocket server consumer with text/binary frames, ping/pong, reconnect, and subprotocol support.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.WebSocket?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.WebSocket)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.WebSocket
```

## Usage

### Fluent DSL

```csharp
using redb.Route.WebSocket.Fluent;

// WebSocket server (consumer)
From(Ws.Listen("0.0.0.0:8080/ws")
        .MaxConnections(500)
        .InOut())
    .Process(async (e, ct) =>
    {
        var msg = e.Message.GetBody<string>();
        e.Message.SetBody($"Echo: {msg}");
    });

// WebSocket client (producer)
From("direct://push")
    .To(Ws.Connect("wss://stream.example.com/feed")
        .SubProtocol("json")
        .ConnectTimeout(5000)
        .Reconnect(intervalMs: 3000, maxAttempts: 10));

// Binary mode
From(Ws.Listen("0.0.0.0:8080/binary")
        .Binary()
        .ReceiveBufferSize(65536))
    .To("direct://binary-handler");

// TLS
From(Ws.Listen("0.0.0.0:8443/secure")
        .Ssl()
        .SslCertPath("/certs/server.pfx")
        .SslCertPassword("password"))
    .To("direct://secure-handler");
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Server** | `Ws.Listen(hostPortPath)`, `.MaxConnections()`, `.InOut()` |
| **Client** | `Ws.Connect(hostPortPath)`, `.ConnectTimeout()`, `.Reconnect(interval, max)` |
| **Framing** | `.Binary()`, `.Encoding()`, `.SubProtocol()` |
| **Socket** | `.ReceiveBufferSize()`, `.SendBufferSize()`, `.KeepAliveInterval()` |
| **TLS** | `.Ssl()`, `.SslCertPath()`, `.SslCertPassword()`, `.TrustAllCertificates()` |

## One port for REST and WebSocket

The consumer serves on the **shared Kestrel host** (`redb.Route.Http.Hosting`), the same listener
HTTP, gRPC, SOAP and AS2 routes use, so the typical production layout works:

```csharp
From("http://0.0.0.0:8080/api/orders")   // REST
From("ws://0.0.0.0:8080/stream")         // live updates, same port, same proxy
```

## Pushing to connected clients

A route can send frames to its own clients instead of only answering them
(`mode=Server` / `Ws.Broadcast`):

```csharp
// broadcast to everyone on /stream
From("timer://ticks?period=1000")
    .To(Ws.Broadcast("0.0.0.0:8080/stream"));

// answer one client later, by the id the incoming exchange carried
From(Ws.Listen("0.0.0.0:8080/stream"))
    .To("direct://slow-work");

From("direct://slow-work-done")
    .SetHeader(WsHeaders.TargetConnection, Simple("${header.redbWs.ConnectionId}"))
    .To(Ws.Broadcast("0.0.0.0:8080/stream"));
```

The consumer serving that address has to be running; a server-mode producer that finds none
refuses to start rather than pushing into nothing.

## Authenticating the handshake

The route host is a generic host, not an ASP.NET application, so the host supplies a delegate:

```csharp
services.AddRedbRouteWebSocket(o => o.Authenticate = async ctx =>
    await myJwtValidator.ValidateAsync(ctx.Request.Query["access_token"]));
```

Null rejects the upgrade with 401. The principal's `NameIdentifier` reaches the route as the
`redbWs.UserId` header, and the principal itself is on every exchange the socket produces
(`ExchangePrincipal.Get(exchange)`). Build the identity with an authentication type
(`new ClaimsIdentity(claims, "Bearer")`): code that reads the principal treats an identity that is not
authenticated as anonymous.

Without this delegate, a caller identified by the shared host
(`AddRedbRouteHttpHosting(o => o.ResolvePrincipal = ...)`) is used in the same way, but an anonymous
handshake is not rejected. When the delegate is set, it takes precedence.

## Schemes

Both `ws` and `wss` schemes are supported for plain and TLS connections. `wss://` on a consumer
**requires** `sslCertPath`: without it the listener would be a plain socket while the log said
`wss://`, so it refuses to start. On a producer, reaching a server with a self-signed certificate
(staging) needs an explicit `trustAllCertificates=true`.

## Behaviour worth knowing

- **`maxConnections` queues, it does not refuse.** A client arriving over the limit waits for a
  slot instead of being rejected, and gives up when it disconnects.
- **`inOut` has no correlation id.** WebSocket is duplex, so the first frame that arrives after a
  send is taken as its answer; an unrelated server push landing at that moment would be taken
  instead. The send lock is held while waiting, which also serialises senders.
- **`reconnect=true` retries forever by default** (`maxReconnectAttempts=0`). Against a server
  that stays down that means an exchange never returns and dead-letter never fires — set
  `reconnectTimeout` (ms) to cap the attempts by time and let the send fail.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new WsConnectionFactory
{
    Ssl = true,
    SslCertPath = "/secrets/client.pfx",
});
// wss://feed.internal/ticks?connectionFactory=prod
```
