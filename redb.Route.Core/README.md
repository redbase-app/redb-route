# redb.Route.Core

Bridge package connecting redb.Route ESB with [redb.Core](https://github.com/redbase-app/redb) EAV storage. Provides persistent idempotent repository backed by redb.Core and typed access to `IRedbService` from route pipelines.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Core?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Core)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Core
```

## Usage

### Persistent Idempotent Repository

Store idempotent message keys in redb.Core instead of in-memory:

```csharp
using redb.Route.Core.Extensions;

builder.Services.AddRedbRoute(route =>
{
    route.AddRedbIdempotentRepository(); // register RedbIdempotentRepository

    route.AddRoutes(r =>
    {
        r.From("kafka://orders?groupId=svc&brokers=localhost:9092")
            .IdempotentConsumer(
                e => e.Message.GetHeader<string>("messageId"),
                new RedbIdempotentRepository(redb))
            .To("direct://process");
    });
});
```

### Access IRedbService from Routes

```csharp
r.From("direct://save")
    .Process(async (exchange, ct) =>
    {
        var redb = exchange.GetService<IRedbService>();
        var order = exchange.Message.GetBody<RedbObject<OrderProps>>();
        await redb.SaveAsync(order);
    });
```

## Key Classes

| Class | Description |
|-------|-------------|
| `RedbIdempotentRepository` | `IIdempotentRepository` backed by redb.Core EAV storage |
| `RedbIdempotentOptions` | Configuration for repository scheme name and TTL |
| `IdempotentEntryProps` | redb.Core scheme for idempotent entries |

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
