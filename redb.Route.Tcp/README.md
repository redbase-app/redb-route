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

A TLS **consumer** needs its certificate — from the endpoint or from a named
`TcpConnectionFactory`, which is where the password belongs. It is loaded once when the listener
starts: a missing file, a wrong password or no path at all fails the start with a message naming
the listener, instead of killing every accepted connection one by one.

A TLS **producer** needs no certificate of its own; `ssl=true` there means "speak TLS to the
server", with `sslTargetHost` for SNI when the name differs from the host you dial. To reach a
server behind a self-signed certificate (staging), say so out loud:

```csharp
.To(TcpDsl.Connect("staging:9443").Ssl().SslTargetHost("staging.internal").TrustAllCertificates())
```

It is never implied by anything else.

For a server that requires mTLS, the producer presents a client certificate — a different thing
from `sslCertPath`, which is the certificate a *consumer* serves:

```csharp
.To(TcpDsl.Connect("partner:9443").Ssl().ClientCert("/certs/client.pfx", "password"))
```

It is loaded once at start, so a reconnect does not re-read the PFX and a bad path or password is
a start-time error. The **consumer** does not request client certificates
(`AuthenticateAsServerAsync` with the server certificate alone), so mTLS here means "we present one
to someone else", not "we demand one".

### Bind address

The consumer's host may be an IP, `0.0.0.0` for every interface, `localhost`, or a resolvable
name; a name that resolves to nothing fails the start with a message naming it. Note that a
`TcpListener` binds exactly one address, so `localhost` here means the **IPv4 loopback** — a client
that dials `[::1]` explicitly needs `::1` in the URI.

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Server** | `TcpDsl.Listen(hostPort)`, `.Backlog()`, `.MaxConnections()`, `.InOut()` |
| **Client** | `TcpDsl.Connect(hostPort)`, `.ConnectTimeout()`, `.Reconnect(interval, max)` |
| **Framing** | `.TextLine()`, `.LengthPrefixed()`, `.Delimiter()`, `.Encoding()` |
| **Socket** | `.KeepAlive()`, `.NoDelay()`, `.ReceiveBufferSize()`, `.SendBufferSize()` |
| **TLS** | `.Ssl()`, `.SslCertPath()`, `.SslCertPassword()`, `.SslTargetHost()`, `.TrustAllCertificates()`, `.ClientCert()` |

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

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new TcpConnectionFactory
{
    Ssl = true,
    SslCertPath = "/secrets/client.pfx",
    SslCertPassword = secrets.CertPassword,
});
// tcp://gateway.internal:7000?connectionFactory=prod
```
