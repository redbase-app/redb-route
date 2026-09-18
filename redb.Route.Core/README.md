# redb.Route.Core

Bridge package connecting redb.Route ESB with [redb.Core](https://github.com/redbase-app/redb) props storage. Provides persistent idempotent repository backed by redb.Core and typed access to `IRedbService` from route pipelines.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Core?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Core)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Core
```

## Usage

### Access IRedbService from Routes

```csharp
using redb.Route.RedbCore.Extensions;

r.From("direct://save")
    .ProcessWithRedb(async (redb, exchange, ct) =>
    {
        var order = (RedbObject<OrderProps>)exchange.In.Body!;
        await redb.SaveAsync(order, ct);
    });
```

The service is scoped to the exchange: parallel exchanges never share a connection. `ProcessWithRedb("orders-db", ...)`
does the same for a named database registered with `context.RegisterRedbService`.

Code that runs **without** an exchange and may run in parallel (an observer, a background job) opens a scope of its own
per call:

```csharp
await using var scope = context.CreateRedbScope("audit-db");
await scope.Service.SaveAsync(record, ct);
```

**Which service you get where, hosts, transactions: docs/REDB_SERVICE_GUIDE.md.**

### Persistent Idempotent Repository

Store idempotent message keys in redb.Core instead of in-memory:

```csharp
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;

var context = new RouteContext();
context.AddRedbIdempotentRepository("orders-inbound", ttl: TimeSpan.FromDays(7));

context.AddRoutes(r =>
{
    r.From("kafka://orders?groupId=svc&brokers=localhost:9092")
        .IdempotentConsumer(ex => ex.In.GetHeader<string>("messageId")!, "orders-inbound")
            .To("direct://process")
        .EndIdempotentConsumer();
});
```

The repository opens a scope of its own for every check, so it is safe under parallel consumers; inside
`.Transacted()` the key commits with the work.

## Key Classes

| Class | Description |
|-------|-------------|
| `RedbRouteExtensions` | `ProcessWithRedb`, `SetBodyFromRedb`, `GetRedbService`, `CreateRedbScope`, `RegisterRedbService` |
| `RedbIdempotentRepository` | `IIdempotentRepository` backed by redb.Core props storage |
| `RedbIdempotentOptions` | Configuration for repository scheme name and TTL |
| `IdempotentEntryProps` | redb.Core scheme for idempotent entries |

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET
