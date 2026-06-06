# redb.Route.Grpc

gRPC transport for redb.Route. Kestrel-based gRPC consumer (server) and GrpcChannel-based producer (client) with generic binary message exchange.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Grpc?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Grpc)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Grpc
```

## Usage

### Fluent DSL

```csharp
using redb.Route.Grpc.Fluent;

// gRPC server (consumer)
From(GrpcDsl.Listen("0.0.0.0:5001")
        .MaxRequestMessageSize(4_194_304)
        .InOut())
    .Process(async (exchange, ct) =>
    {
        var request = exchange.Message.GetBody<byte[]>();
        exchange.Message.SetBody(ProcessRequest(request));
    });

// gRPC client (producer)
From("direct://send")
    .To(GrpcDsl.Call("grpc-service:5001")
        .Plaintext()
        .Deadline(5000)
        .MaxSendMessageSize(1_048_576));
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Server** | `GrpcDsl.Listen()`, `.Host()`, `.Port()`, `.MaxRequestMessageSize()`, `.InOut()` |
| **Client** | `GrpcDsl.Call()`, `.Plaintext()`, `.Deadline()`, `.MaxSendMessageSize()`, `.MaxReceiveMessageSize()` |
| **TLS** | `.Ssl()`, `.SslCertPath()`, `.SslCertPassword()` |

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
