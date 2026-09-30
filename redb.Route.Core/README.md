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

### Query storage from a route: props and base fields

`RedbQuery` runs a server-side query and puts the list of `RedbObject<TProps>` into the target. A
condition is a string in the route language: members of the stored object, compared against
values that come from the message (`header.x`, `body`, `property.x`, functions):

- `where` filters on props, the same as redb `Where`;
- `whereRedb` filters on the base fields of the stored object, the same as redb `WhereRedb`:
  `Id`, `ParentId`, `SchemeId`, `OwnerId`, `WhoChangeId`, `Key`, `Name`, `Note`, `Hash`,
  `ValueLong`, `ValueString`, `ValueGuid`, `ValueBool`, `ValueDouble`, `ValueNumeric`,
  `ValueDatetime`, `ValueUnique`, `DateCreate`, `DateModify`, `DateBegin`, `DateComplete`.
  These are columns of the object table and usually indexed, so this is the condition that
  cuts the most rows before props are read.

A list of keys is one query, not a loop: `member in list` and `member not in list` become an SQL
`IN` in the storage, for props and base fields alike. The list is a collection the message carries
or a literal in parentheses:

```
whereRedb="ValueString in header.codes"
whereRedb="Id not in (7, 9)"
where="Status in ('open','held')"
```

Each element converts to the member's type exactly as a single compared value does, so a header of
strings works against an `int` field. An empty or missing list finds nothing (`not in`: keeps
everything); it is decided in the route and never reaches the storage as an empty `IN`. The member
goes on the left — a member on the right of `in` is refused when the route is built.

When both are written they combine with AND, because the storage cannot mix base fields and
props inside one OR. `orderBy` orders by a props path and `orderByRedb` by a base field; a
query takes one or the other. An unknown member, arithmetic over a member or an unsupported
function is refused when the route is built, never filtered in memory.

`outputType` picks what goes to the target:

| `outputType` | redb call | Target gets |
|---|---|---|
| `List` (default) | `ToListAsync` | `List<RedbObject<TProps>>` |
| `First` | `FirstOrDefaultAsync` | the first object in the query's order, or `null` |
| `Count` | `CountAsync` | `int` |
| `Any` | `AnyAsync` | `bool` |

`First` takes ordering and `skip`, not `take`. `Count` and `Any` answer for all the matches, so
ordering, `take` and `skip` are refused with them. Only `List` needs a condition or a limit: it
is the one output that loads every matching row.

```csharp
r.From("direct://incident-by-key")
    .RedbQuery(typeof(Incident),
        whereRedb: "ValueGuid == header.correlationId",
        where: "Status != 'closed'",
        orderByRedb: "DateCreate", descending: true,
        outputType: RedbQueryOutput.First, target: "property:incident");
```

The same in Route-XML:

```xml
<redbQuery type="Hub.Incident, Hub"
           whereRedb="ValueGuid == header.correlationId"
           where="Status != 'closed'"
           orderByRedb="DateCreate" descending="true"
           outputType="First" target="property:incident"/>
```

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
