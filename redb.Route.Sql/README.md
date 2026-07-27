# redb.Route.Sql

SQL database transport for redb.Route. Pure ADO.NET polling consumer, query/batch producer, and stored procedure support. Provider-agnostic — works with PostgreSQL, SQL Server, SQLite, MySQL, and any `DbConnection`.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Sql?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Sql)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Sql
```

No additional dependencies — uses `System.Data.Common` abstractions only.

## URI Format

One scheme — `sql:` — and the mode is chosen by the `mode=` parameter, not by the scheme.

```
sql:<sql-text-or-procedure-name>?mode=Poll|Execute|Procedure&dataSource=<name>&...
```

| Mode | Role | Required |
|------|------|----------|
| `Execute` (default) | Producer — `To(...)` | `dataSource` or `connectionString` |
| `Procedure` | Producer — `To(...)` | `dataSource` + `procedureName` |
| `Poll` | Consumer — `From(...)` | `dataSource`; **`mode=Poll` is mandatory** |

The path is the SQL text itself and is taken verbatim (it is *not* URL-decoded), so it must not
contain a `?` — the parser splits on the first one.

### Consumer — poll rows

```csharp
From("sql:SELECT id, payload FROM outbox WHERE processed = 0"
    + "?mode=Poll"
    + "&dataSource=#main"
    + "&delay=5000"
    + "&maxMessagesPerPoll=100"
    + "&transacted=true"
    + "&onSuccess=UPDATE outbox SET processed = 1 WHERE id = @id")
    .Log("Processing outbox row ${header.id}")
    .To("direct://handle");
```

One Exchange per row; body is a `Dictionary<string, object?>`, and **every column is also copied
into headers** — which is why `onSuccess=... WHERE id = @id` binds without any extra configuration.

### Producer — execute a statement

```csharp
From("direct://save")
    .To("sql:INSERT INTO audit(message, status) VALUES(@message, @status)"
        + "?dataSource=#main"
        + "&param.message=${body}"
        + "&param.status=${header.mode}");
```

### Producer — call a stored procedure or function

```csharp
// PostgreSQL function: SELECT maintain_partitions(@tbl, @keep_days)
From("direct://maintenance")
    .To("sql:maintain_partitions"
        + "?mode=Procedure"
        + "&dataSource=#main"
        + "&procedureName=maintain_partitions"
        + "&asFunction=true"
        + "&procedureParams=IN:tbl:String,IN:keep_days:Int32"
        + "&param.keep_days=90");
```

`asFunction=true` builds `SELECT name(@p1, @p2, ...)` from the `IN`/`INOUT` params **in declaration
order** and executes it as a scalar — the result lands in the body. `asFunction=false` (default)
uses `CommandType.StoredProcedure` (`CALL`/`EXEC` semantics of the driver) and executes non-query.
`OUT`/`INOUT` values are written back into headers under their parameter names.

> The procedure name must be given in `procedureName=`. The URI path is ignored in `Procedure` mode
> — it exists only so the parser has a non-empty path.

`procedureParams` format: `DIR:name:DbType[:expression]`, comma-separated. `DIR` is `IN`, `OUT`
or `INOUT`; `DbType` is a `System.Data.DbType` name (`Int32`, `Int64`, `String`, `Decimal`, …).

### Batch

```csharp
From("direct://bulk")
    .To("sql:INSERT INTO logs(message) VALUES(@message)?dataSource=#main&batchSize=500");
```

Batch mode engages only when the body is an `IList`. The statement runs once per element.
`breakBatchOnError=true` stops at the first failure and rolls back; otherwise errors are collected
into the `redbSql.error` header and the transaction commits.

## Parameters and binding

SQL placeholders are **`@name` only**. There is no `:name` support, and there is no implicit
`@body` — a scalar body (string, POCO) never binds itself into a parameter; use `param.x=${body}`.

Binding priority for each `@name` found in the statement:

| # | Source |
|---|--------|
| 0 | `param.<name>=...` from the URI (constant or `${...}` expression) |
| 1 | Exchange header with the same name |
| 2 | Body, if it is a `Dictionary<string, object?>` / `IDictionary<string, object>` — by key |
| 3 | *(nothing matched)* → `DBNull.Value`, silently |

In `onFailure` the special parameter `@redbError` resolves to `exchange.Exception.Message`.

In a poll consumer, `param.*` values are resolved **without an Exchange** — constants only,
`${header...}` expressions will not resolve there.

## URI parameters

Names are the property names of `SqlEndpointOptions`, case-insensitive.

| Parameter | Type | Default |
|---|---|---|
| `mode` | `Poll` \| `Execute` \| `Procedure` | `Execute` |
| `dataSource` | registered data source name (a leading `#` is stripped) | — |
| `connectionString` / `provider` | inline connection instead of `dataSource` | — |
| `commandTimeout` | seconds | `30` |
| `transacted` | **consumer only** — SELECT + `onSuccess`/`onFailure` in one transaction | `false` |
| `isolationLevel` | `System.Data.IsolationLevel` | provider default |
| `outputType` | `Auto` \| `SelectList` \| `SelectOne` \| `StreamList` \| `Scalar` \| `None` | `Auto` |
| `noop` | skip execution (dry run) | `false` |
| `delay` / `initialDelay` | poll interval / first delay, ms | `500` / `1000` |
| `fixedRate` | measure delay from cycle start | `false` |
| `repeatCount` | 0 = forever | `0` |
| `maxMessagesPerPoll` | −1 = no limit | `-1` |
| `routeEmptyResultSet` / `sendEmptyMessageWhenIdle` | emit an Exchange on empty polls | `false` |
| `onSuccess` / `onFailure` / `onBatchComplete` | SQL run after each row / after a failed row / after the cycle | — |
| `batchSize` | > 0 enables batch mode (flag, not a chunk size) | `0` |
| `breakBatchOnError` | stop and roll back on the first batch error | `false` |
| `param.<name>` | explicit parameter value or `${...}` expression | — |
| `procedureName` | required for `mode=Procedure` | — |
| `asFunction` | `SELECT fn(...)` instead of `CALL`/`EXEC` | `false` |
| `procedureParams` | `DIR:name:DbType[:expr],…` | — |

`outputType=Auto` inspects the statement: `SELECT`/`WITH` → `SelectList`, anything else → `None`
(body untouched, only `redbSql.updateCount` is set).

> `outputClass` and `outputHeader` are accepted by the option binder but **not implemented** —
> the result always goes to the body via `DictionaryRowMapper`. See
> docs/SQL_PROCEDURE_MODE_REGRESSION.md.

## Headers written back

All prefixed with `redbSql.`: `query`, `updateCount`, `rowCount`, `dataSource`, `outputType`,
`error`, `transactionId`, `storedProcedure`, `executionTime` (ms).

## Transactions

Producers (`Execute` and `Procedure`) **always** open a local transaction when there is no ambient
one — write atomicity does not need `transacted=true`, and the option is a no-op there. The consumer
is the only place that reads `transacted`. A route-level `.Transacted()` wraps the pipeline in a
`TransactionScope`; the connector then detects the ambient transaction, skips its local one, and
enlists the connection.

## Register data sources

```csharp
DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);   // required

services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteSql(sql =>
    {
        sql.AddDataSource("main", opts =>
        {
            opts.ConnectionString = "Host=localhost;Database=demo;Username=postgres;Password=***";
            opts.ProviderName = "Npgsql";
        });

        sql.AddNamedQuery("pendingOrders", "SELECT * FROM orders WHERE processed = 0");
        // → "sql:ref:pendingOrders?mode=Poll&dataSource=#main"
    });
});
```

Inside a Tsak module the same thing without DI:

```csharp
context.AddComponent(new SqlComponent());
context.AddToRegistry("main", (ISqlConnectionFactory)new SqlConnectionFactory(
    new SqlConnectionOptions { ConnectionString = conn, ProviderName = "Npgsql" }));
```

`SqlConnectionOptions` also carries `ReadConnectionString` (a replica used for read-only SELECTs),
`TestOnBorrow` / `ValidationQuery`, and `EnableRetryOnFailure` / `MaxRetries` / `RetryDelay`.

## Fluent DSL

`Sql.Poll(...)` / `Sql.Execute(...)` compile to exactly the URI strings above and are equivalent:

```csharp
using redb.Route.Sql.Fluent;

From(Sql.Poll("SELECT * FROM orders WHERE processed = 0")
        .DataSource("main")
        .Delay(5000)
        .OnSuccess("UPDATE orders SET processed = 1 WHERE id = @id")
        .MaxMessagesPerPoll(100))
    .To("direct://handle");
```

> **`Sql.Procedure(...)` is currently broken:** `SqlBuilder.Build()` does not emit `procedureName`,
> so the resulting URI fails validation. Use the string URI form shown above until this is fixed —
> see docs/SQL_PROCEDURE_MODE_REGRESSION.md.

Most builder methods (`DataSource`, `CommandTimeout`, `Delay`, `InitialDelay`, `RepeatCount`,
`MaxMessagesPerPoll`, `Batch`, `Param`) accept both constants and `IExpression` for runtime resolution.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET
