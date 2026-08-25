# redb.Route.Grpc

gRPC transport for redb.Route. A gRPC **method address is a route**: the consumer registers
`/package.Service/Method` as a path route on the shared Kestrel host, so many methods live on one port as
ordinary routes with their own ids, policies and metrics. The producer calls any method address with a
`GrpcChannel`.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Grpc?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Grpc)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Grpc
```

## Usage

```csharp
using redb.Route.Grpc;

// One port, three methods — three routes.
From(GrpcDsl.Listen("0.0.0.0:5001").Method("/identity.v1.Identity/Token"))
    .RouteId("grpc-token")
    .To("direct-vm://identity-token");

From(GrpcDsl.Listen("0.0.0.0:5001").Method("/identity.v1.Identity/Introspect"))
    .RouteId("grpc-introspect")
    .To("direct-vm://identity-introspect");

// The built-in generic service (headers + payload envelope), unchanged.
From(GrpcDsl.Listen("0.0.0.0:5001"))
    .Process(async (exchange, ct) => exchange.Message.SetBody(Handle(exchange.Message.GetBody<byte[]>())));

// Client.
From("direct://send")
    .To(GrpcDsl.Call("grpc-service:5001")
        .Method("/identity.v1.Identity/Token")
        .Plaintext()
        .Deadline(5000));
```

## How it works

The connector implements the gRPC wire protocol itself (`GrpcWire`) instead of taking the
`Grpc.AspNetCore` server stack, and serves on the same `SharedHttpServerManager` as the HTTP, AS2 and SOAP
transports. That server stack would need `AddGrpc()` / `MapGrpcService()` wired into an ASP.NET host
*before* it is built, which forces one gRPC endpoint per port, a host restart on hot-reload, and a
header-based route selector instead of the method address. Owning the framing keeps a gRPC route what
every other route is: a URI, an endpoint, a consumer.

On the wire: `[1 byte compressed-flag][4 bytes length BE][message]`, content-type `application/grpc`,
HTTP status always 200, the real outcome in the `grpc-status` / `grpc-message` trailers, caller deadline in
`grpc-timeout`.

## Compression

Gzipped requests are always accepted and inflated (the size limit is re-checked after inflation, so a
small frame cannot expand into an arbitrarily large buffer). Replies are compressed only with
`.Compression(GrpcCompression.Gzip)` **and** only when the caller advertised gzip in
`grpc-accept-encoding`; we always advertise `identity,gzip` back. An unknown codec is answered with
`Unimplemented` rather than a parse failure. On the client side `.Compression(...)` gzips outgoing
requests.

## Statuses

A route answers with a status the client actually sees:

| Source on `Out` | Result |
|---|---|
| `redbGrpc.StatusCode` (+ `redbGrpc.StatusDetail`) | used as-is |
| `status.code` — what every controller dispatcher writes | mapped (401 → `Unauthenticated`, 403 → `PermissionDenied`, 404 → `NotFound`, 409 → `AlreadyExists`, 429 → `ResourceExhausted`, 503 → `Unavailable`, 504 → `DeadlineExceeded`, other 5xx → `Internal`) |
| neither | `OK` |
| unhandled exception | `RpcException` keeps its status; cancellation splits into `DeadlineExceeded` / `Cancelled`; anything else is `Internal` |

`redbGrpc.Trailer.*` headers become response trailers. A non-OK status is sent trailers-only, because gRPC
clients discard the payload of a failed call. `suppressStatusMapping=true` restores the old
always-`OK`-plus-error-document behaviour.

## Typed `.proto` services

`Envelope=Auto` (default) wraps only the built-in address in the generic `RedbMessage`; any other address
receives the caller's protobuf bytes untouched. So a client generated from a real `.proto` can call a redb
route with no generated server stubs on our side — the route decodes the message with its own codec.
`.Envelope(GrpcEnvelopeMode.Message | Raw)` forces either behaviour.

## Streaming

An `IAsyncEnumerable` reply body is written one frame per yield — the framework's own streaming shape, the
same one the HTTP consumer honours for chunked and SSE replies. Enabled by default on the built-in
`ProcessStream` address, opt-in elsewhere with `.Streaming()`. A collection body (`List<T>`) stays one
message, as it is for every other transport. A stream body on a **unary** address is a hard error naming
the address — a unary call carries exactly one message, so silently sending the enumerable's type name
would be worse.

The producer streams too: `.Streaming()` on a client endpoint makes the call server-streaming and puts an
`IAsyncEnumerable` into `Out.Body`, so a gRPC stream flows straight into a streaming consumer (the HTTP
one turns it into SSE or chunked output) without being buffered in between.

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Address** | `GrpcDsl.Listen()`, `GrpcDsl.Call()`, `.Method()`, `.Service(service, method)`, `.Host()`, `.Port()` |
| **Server** | `.InOut()`, `.Streaming()`, `.Health()`, `.MaxRequestMessageSize()`, `.EmitHttpCompatHeaders()`, `.AllowClientReservedHeaders()`, `.SuppressStatusMapping()` |
| **Client** | `.Plaintext()`, `.Deadline()`, `.ThrowOnError()`, `.MaxSendMessageSize()`, `.MaxReceiveMessageSize()`, `.ClientCertificate()` |
| **Security** | `.Ssl()`, `.SslCertPath()`, `.SslCertPassword()`, `.ClientCertificates()`, `.ConnectionFactory()`, `.NegotiationType()` |
| **Sizes** | `.MaxMessageSize()` |

## Headers

| Header | Direction | Meaning |
|---|---|---|
| `redbGrpc.Route` / `.Service` / `.Method` | in | method address the call arrived on, split |
| `redbGrpc.RemoteIp` / `.RemotePort` | in | client address, bare |
| `redbGrpc.RemotePeer` | in | gRPC-style peer (`ipv4:10.0.0.5:51234`) |
| `redbGrpc.Authority` / `.Deadline` / `.Port` | in | call metadata |
| `redbGrpc.ClientCert*` | in | client certificate, when mTLS is on |
| `redbGrpc.StatusCode` / `.StatusDetail` | out | status returned to the caller |
| `redbGrpc.Trailer.*` | out | response trailers |

Inbound headers carrying a transport-reserved prefix (`redbHttp.`, `redbGrpc.`, `redbSoap.`, …) are dropped:
a caller must not be able to forge the metadata upstream processors trust, such as the client IP that drives
rate limiting. `allowClientReservedHeaders=true` opts out, and says so in the log.

## Apache Camel parity

| camel-grpc | redb.Route.Grpc |
|---|---|
| `grpc://host:port/service?method=` | same, plus the full-address form `grpc:host:port/pkg.Service/Method` |
| `maxMessageSize` | `maxMessageSize` (or per-direction `maxSend`/`maxReceive`/`maxRequestMessageSize`) |
| `negotiationType=PLAINTEXT\|TLS` | `negotiationType`, or `plaintext` / `ssl` |
| `keyCertChainResource`, `keyResource`, `trustCertCollectionResource` | `sslCertPath` + `sslCertPassword` (PFX — the .NET idiom), `clientCertificateMode`, `allowedClientThumbprints` |
| `producerStrategy=SIMPLE` / `STREAMING` | unary by default, `.Streaming()` for a server-streaming call |
| `consumerStrategy=AGGREGATION\|PROPAGATION` | n/a — the consumer serves unary and server-streaming; client-streaming is not implemented |
| `forwardOnCompleted`, `forwardOnError` | n/a — same reason |
| `flowControlWindow`, `maxConcurrentCallsPerConnection` | not exposed: they are Kestrel listener limits on the shared host |
| gzip compression | `.Compression(GrpcCompression.Gzip)`; inbound gzip always accepted |
| gRPC server reflection | not served — reflection needs the descriptor set of the `.proto` the routes speak, which belongs to the module that owns it |

## Interop

Because the wire protocol is ours, the connector is verified against an independent stack: a Node.js
`@grpc/grpc-js` server and client in a container (`C:\Work\yaml\grpc`), both directions, cross-process,
covering statuses, server streaming, gzip and a real mTLS handshake with a client certificate. The
contract there is a typed `.proto`, so it also proves a generated client can call a redb route with no
server stubs on our side.

```bash
cd C:\Work\yaml\grpc && docker compose up -d
dotnet test tests/redb.Route.Tests.Grpc --filter Category=Interop
```

The tests are gated on the container being reachable, so the normal suite stays green without Docker.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET
