# redb.Route.WebSocket

WebSocket transport for redb.Route. ClientWebSocket producer and Kestrel-based WebSocket server consumer with text/binary frames, ping/pong, reconnect, and subprotocol support.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.WebSocket?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.WebSocket)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

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
| **TLS** | `.Ssl()`, `.SslCertPath()`, `.SslCertPassword()` |

## Schemes

Both `ws` and `wss` schemes are supported for plain and TLS connections.

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
