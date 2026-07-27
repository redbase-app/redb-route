# redb.Route.Tcp

TCP transport for redb.Route. Socket-based producer (client) and consumer (server) with text-line, length-prefixed, and raw framing modes, TLS, and InOut request-reply patterns.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Tcp?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Tcp)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Tcp
```

No additional dependencies.

## Usage

### Fluent DSL

```csharp
using redb.Route.Tcp.Fluent;

// TCP server (consumer)
From(TcpDsl.Listen("0.0.0.0:9000")
        .TextLine()
        .MaxConnections(100)
        .Backlog(128))
    .Log("Received: ${body}")
    .To("direct://process");

// TCP client (producer)
From("direct://send")
    .To(TcpDsl.Connect("server.local:9000")
        .TextLine()
        .ConnectTimeout(5000)
        .Reconnect(intervalMs: 3000, maxAttempts: 5));

// Length-prefixed binary protocol
From(TcpDsl.Listen("0.0.0.0:9001")
        .LengthPrefixed()
        .InOut())
    .Process(async (e, ct) =>
    {
        var request = e.Message.GetBody<byte[]>();
        e.Message.SetBody(HandleRequest(request));
    });

// TLS
From(TcpDsl.Listen("0.0.0.0:9443")
        .TextLine()
        .Ssl()
        .SslCertPath("/certs/server.pfx")
        .SslCertPassword("password"))
    .To("direct://secure-handler");
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Server** | `TcpDsl.Listen(hostPort)`, `.Backlog()`, `.MaxConnections()`, `.InOut()` |
| **Client** | `TcpDsl.Connect(hostPort)`, `.ConnectTimeout()`, `.Reconnect(interval, max)` |
| **Framing** | `.TextLine()`, `.LengthPrefixed()`, `.Delimiter()`, `.Encoding()` |
| **Socket** | `.KeepAlive()`, `.NoDelay()`, `.ReceiveBufferSize()`, `.SendBufferSize()` |
| **TLS** | `.Ssl()`, `.SslCertPath()`, `.SslCertPassword()`, `.SslTargetHost()` |

## Framing Modes

| Mode | Description |
|------|-------------|
| `Raw` | No framing — raw byte stream |
| `TextLine` | Messages delimited by newline (`\n`) |
| `LengthPrefixed` | 4-byte big-endian length header + payload |

## URI Format

```
tcp://host:port?param=value&...
```

### URI Examples

```csharp
// Server — listen on all interfaces, text-line framing
From("tcp://0.0.0.0:9000?textLine=true&maxConnections=100")

// Client — connect with auto-reconnect
.To("tcp://server.local:9000?textLine=true&reconnect=true&reconnectInterval=3000&maxReconnectAttempts=5")

// Binary protocol with request-reply
From("tcp://0.0.0.0:9001?lengthPrefixed=true&inOut=true")

// TLS
From("tcp://0.0.0.0:9443?textLine=true&ssl=true&sslCertPath=/certs/server.pfx&sslCertPassword=secret")
```

### URI Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `textLine` | bool | `false` | Use newline-delimited framing |
| `lengthPrefixed` | bool | `false` | Use 4-byte length-prefixed framing |
| `delimiter` | string | `\n` | Line delimiter (TextLine mode) |
| `encoding` | string | `utf-8` | Character encoding |
| `keepAlive` | bool | `true` | TCP keep-alive |
| `noDelay` | bool | `true` | Disable Nagle's algorithm |
| `receiveBufferSize` | int | `8192` | Socket receive buffer (bytes) |
| `sendBufferSize` | int | `8192` | Socket send buffer (bytes) |
| `connectTimeout` | int | `10000` | Client connect timeout (ms) |
| `reconnect` | bool | `false` | Auto-reconnect on connection loss |
| `reconnectInterval` | int | `5000` | Delay between reconnect attempts (ms) |
| `maxReconnectAttempts` | int | `0` | Max reconnect attempts (0 = unlimited) |
| `backlog` | int | `128` | Server listen backlog |
| `maxConnections` | int | `0` | Max concurrent connections (0 = unlimited) |
| `inOut` | bool | `false` | Request-reply mode (wait for / send response) |
| `ssl` | bool | `false` | Enable TLS |
| `sslCertPath` | string | — | Path to PFX certificate file |
| `sslCertPassword` | string | — | Certificate password |
| `sslTargetHost` | string | — | Expected server hostname (client TLS validation) |

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET
