# redb.Route.Sftp

SFTP transport for redb.Route. Polling consumer and atomic producer via SSH.NET — key/password auth, proxy, idempotency, glob filtering, temp-file upload, chmod, and recursive directory traversal.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Sftp?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Sftp)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Sftp
```

## Usage

### Fluent DSL

```csharp
using redb.Route.Sftp.Fluent;

// Poll remote directory
From(Sftp.Directory("/uploads")
        .Host("sftp.example.com").Port(22)
        .Username("user").PrivateKey("/keys/id_rsa")
        .Include("*.xml")
        .Delay(10000)
        .MoveTo("/uploads/archive")
        .Recursive()
        .Idempotent())
    .Log("Downloaded: ${header.CamelFileName}")
    .To("direct://process");

// Upload files atomically
From("direct://export")
    .To(Sftp.Directory("/outgoing")
        .Host("sftp.example.com")
        .Username("user").Password("secret")
        .TempPrefix(".uploading-")
        .Chmod("0644")
        .AutoCreate());

// With proxy
From(Sftp.Directory("/data")
        .Host("internal-sftp")
        .Username("svc")
        .Proxy(ProxyTypes.Http, "proxy.corp.net", 8080)
        .ProxyAuth("proxy-user", "proxy-pass"))
    .To("direct://ingest");
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Host()`, `.Port()`, `.Username()`, `.Password()`, `.PrivateKey()`, `.UseKeyboardInteractive()`, `.PreferredAuthentications()`, `.ServerFingerprint()`, `.StrictHostKeyChecking()`, `.KnownHostsFile()`, `.ConnectionTimeout()`, `.OperationTimeout()`, `.KeepAliveInterval()`, `.BufferSize()`, `.Compression()` |
| **Proxy** | `.Proxy(type, host, port)`, `.ProxyAuth(user, pass)` |
| **Reconnect** | `.MaximumReconnectAttempts()`, `.ReconnectDelay()`, `.Disconnect()` |
| **Consumer** | `.Delay()`, `.InitialDelay()`, `.Include()`, `.Exclude()`, `.Recursive()`, `.MaxDepth()`, `.MinDepth()`, `.SortBy()`, `.MaxMessagesPerPoll()`, `.MinAge()`, `.MaxAge()` |
| **Post-process** | `.Noop()`, `.Delete()`, `.MoveTo()`, `.MoveExisting()`, `.PreMove()`, `.MoveFailed()` |
| **Idempotency** | `.Idempotent()`, `.DoneFileName()` |
| **Transfer** | `.Binary()`, `.Charset()`, `.StepWise()`, `.Separator()` |
| **Producer** | `.FileExist()`, `.TempPrefix()`, `.TempFileName()`, `.Chmod()`, `.ChmodDirectory()`, `.AutoCreate()`, `.AllowNullBody()`, `.EagerDeleteTargetFile()`, `.KeepLastModified()`, `.Flatten()`, `.JailStartingDirectory()`, `.AppendChars()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
