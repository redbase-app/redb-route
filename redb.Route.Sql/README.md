# redb.Route.Sql

SQL database transport for redb.Route. Pure ADO.NET polling consumer, query/batch producer, and stored procedure support. Provider-agnostic — works with PostgreSQL, SQL Server, SQLite, MySQL, and any `DbConnection`.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Sql?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Sql)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Sql
```

No additional dependencies — uses `System.Data.Common` abstractions only.

## Usage

### URI Format

```
sql://SELECT * FROM orders WHERE processed = 0?dataSource=main&delay=5000
```

### Fluent DSL

```csharp
using redb.Route.Sql.Fluent;

// Poll rows from database
From(Sql.Poll("SELECT * FROM orders WHERE processed = 0")
        .DataSource("main")
        .Delay(5000)
        .OnSuccess("UPDATE orders SET processed = 1 WHERE id = :id")
        .MaxMessagesPerPoll(100))
    .Log("Processing order ${header.id}")
    .To("direct://handle");

// Execute SQL (producer)
From("direct://save")
    .To(Sql.Execute("INSERT INTO audit (message, status) VALUES (:body, 'OK')")
        .DataSource("main")
        .Transacted());

// Stored procedure
From("direct://calc")
    .To(Sql.Procedure("sp_calculate_totals")
        .DataSource("main")
        .In("orderId", DbType.Int32)
        .Out("total", DbType.Decimal));

// Batch processing
From("direct://bulk")
    .To(Sql.Execute("INSERT INTO logs (message) VALUES (:body)")
        .DataSource("main")
        .Batch(500)
        .Transacted());

// Explicit parameter binding — override auto-bind with constant or expression
From("direct://update-status")
    .To(Sql.Execute("UPDATE orders SET status = :status WHERE id = :id")
        .DataSource("main")
        .Param("status", "completed")
        .Param("id", Header("orderId")));
```

### Register Data Sources

```csharp
builder.Services.AddSingleton(sp =>
{
    var registry = new SqlDataSourceRegistry();
    registry.Register("main", () => new NpgsqlConnection(connectionString));
    return registry;
});
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.DataSource()`, `.ConnectionString()`, `.Provider()`, `.CommandTimeout()` |
| **Transaction** | `.Transacted()`, `.WithIsolationLevel()` |
| **Output** | `.OutputType()`, `.OutputClass()`, `.OutputHeader()`, `.Noop()` |
| **Polling** | `.Delay()`, `.InitialDelay()`, `.FixedRate()`, `.RepeatCount()`, `.MaxMessagesPerPoll()`, `.RouteEmptyResultSet()`, `.SendEmptyMessageWhenIdle()` |
| **Lifecycle** | `.OnSuccess(sql)`, `.OnFailure(sql)`, `.OnBatchComplete(sql)` |
| **Batch** | `.Batch(size)`, `.BreakBatchOnError()` |
| **Parameters** | `.Param(name, value)`, `.Param(name, IExpression)` — explicit bind for `:name` SQL params |
| **Procedure** | `.In()`, `.Out()`, `.InOut()`, `.AsFunction()` |

> Most builder methods (DataSource, ConnectionString, CommandTimeout, OutputHeader, Delay, InitialDelay, RepeatCount, MaxMessagesPerPoll, Batch, Param) accept both constant values and `IExpression` for runtime resolution.

## Three Modes

| Mode | Entry Point | Use Case |
|------|-------------|----------|
| **Poll** | `Sql.Poll(query)` | Consumer — periodically query rows as messages |
| **Execute** | `Sql.Execute(sql)` | Producer — execute INSERT/UPDATE/DELETE per message |
| **Procedure** | `Sql.Procedure(name)` | Producer — call stored procedures with IN/OUT params |

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
