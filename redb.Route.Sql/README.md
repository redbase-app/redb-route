# redb.Route.Sql

SQL database transport for redb.Route. Pure ADO.NET polling consumer, query/batch producer, and stored procedure support. Provider-agnostic — tested end to end against PostgreSQL, SQL Server, SQLite, MySQL, MariaDB, Oracle and Firebird, and works with any `DbConnection` (see [Providers](#providers)).

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
    + "&onSuccess=UPDATE outbox SET processed = 1 WHERE id = :#id")
    .Log("Processing outbox row ${header.id}")
    .To("direct://handle");
```

One Exchange per row; body is a `Dictionary<string, object?>`, and **every column is also copied
into headers** — which is why `onSuccess=... WHERE id = :#id` binds without any extra configuration.

### Producer — execute a statement

```csharp
From("direct://save")
    .To("sql:INSERT INTO audit(message, status) VALUES(:#message, :#status)"
        + "?dataSource=#main"
        + "&param.message=${body}"
        + "&param.status=${header.mode}");
```

### Producer — call a stored procedure or function

```csharp
// PostgreSQL function: SELECT maintain_partitions(:#tbl, :#keep_days)
From("direct://maintenance")
    .To("sql:maintain_partitions"
        + "?mode=Procedure"
        + "&dataSource=#main"
        + "&procedureName=maintain_partitions"
        + "&asFunction=true"
        + "&procedureParams=IN:tbl:String,IN:keep_days:Int32"
        + "&param.keep_days=90");
```

`asFunction=true` builds `SELECT name(:#p1, :#p2, ...)` from the `IN`/`INOUT` params **in declaration
order** and executes it as a scalar — the result lands in the body. `asFunction=false` (default)
uses `CommandType.StoredProcedure` (`CALL`/`EXEC` semantics of the driver) and executes non-query.
`OUT`/`INOUT` values are written back into headers under their parameter names.

> In `Procedure` mode the URI path is the procedure name; an explicit `procedureName=` wins over it.

`procedureParams` format: `DIR:name:DbType[:expression]`, comma-separated. `DIR` is `IN`, `OUT`
or `INOUT`; `DbType` is a `System.Data.DbType` name (`Int32`, `Int64`, `String`, `Decimal`, …).

### Batch

```csharp
From("direct://bulk")
    .To("sql:INSERT INTO logs(message) VALUES(:#message)?dataSource=#main&batchSize=500");
```

With `batchSize` above zero, a list body is a batch: the statement runs once per item, and all items
share one transaction — a local one, or the route's when the route is transacted. Each item binds its
own values (see [Batch item binding](#batch-item-binding)). An empty list writes nothing
(`redbSql.updateCount` is `0`), and a `byte[]` body is a single value, not a batch.

A batch source is any list or other sequence (`HashSet`, a LINQ query, a `yield` method), an `IAsyncEnumerable` of a
reference type — a `StreamList` result from another `sql:` endpoint, say — or a JSON array (`JsonElement`, a `JsonDocument`
whose root is an array, `JsonArray`; what `Unmarshal<object>("application/json")` produces). A string or any other sequence
of characters, a `byte[]` or any other sequence of bytes (`ArraySegment<byte>`, `List<byte>`), a dictionary or JSON
object, an XML document and an `IAsyncEnumerable` of a value type (`IAsyncEnumerable<int>`) are one value each. Items are
read as they are written — a chunk at a time for `DbBatch`, one at a time otherwise — and the first item is read before a
connection is opened, so an empty stream costs none. A failure of the source itself ends the batch in either error mode, with
the number of items read in `redbSql.batchFailedIndex`. A streamed body is consumed by the batch. On SQLite, a stream read
from a file cannot feed a batch into the same file: the open reader blocks the writer.

`batchSize` is the number of statements sent in one round trip; it does not split the transaction. How the
statements travel is decided by the driver, as in Apache Camel — there is no option to pick it:

| When | Strategy (`redbSql.batchStrategy`) | Over the wire |
|---|---|---|
| `breakBatchOnError=true` and the connection can create a `DbBatch` (PostgreSQL, SQL Server, MySQL, MariaDB) | `DbBatch` | chunks of `batchSize` statements, one round trip each (`redbSql.batchChunkCount`) |
| `breakBatchOnError=true`, no `DbBatch` (SQLite, Oracle, Firebird) | `Commands` | one command, prepared once, runs per item with its parameters reused |
| `breakBatchOnError=false` | `Savepoints` | the same reused command, one item at a time under its savepoint |

What a failing item does is set by `breakBatchOnError`:

| `breakBatchOnError` | On a failing item |
|---|---|
| `true` (default) | The batch stops, the transaction rolls back, and the provider's exception (`SqlException`, `PostgresException`, `SqliteException`, …) is thrown as is, so `OnException<DbException>` matches it. The item's index is in `exception.Data["redbSql.batchFailedIndex"]` and in the `redbSql.batchFailedIndex` header, with `redbSql.batchStrategy` saying how to read it. |
| `false` | Each item runs under a savepoint. A failed item is undone and listed in `redbSql.batchErrors` (`IReadOnlyList<SqlBatchItemError>`: index, message, SQLSTATE); the other items commit. If the error ended the server transaction (SQL Server conversion errors or any error under `XACT_ABORT ON`, SQLite `OR ROLLBACK`), the batch stops with that error and nothing is committed. Refused before any write inside a transacted route, and on a driver that does not implement savepoints (`NotSupportedException` from the first savepoint). Savepoints are tried, not looked up: SqlClient and MySqlConnector report `SupportsSavepoints=false` and support them. |

A batch reports only its own run: the result headers an earlier attempt on the same exchange left (a redelivery through
`OnException`, an earlier `sql:` step) are removed before it writes its own. A returned row that `outputClass` cannot hold
is the endpoint's configuration, not an item's failure: the batch ends with a `SqlRowMappingException`
(an `InvalidOperationException`) in either error mode and nothing is committed.

To split a large volume into several transactions, aggregate in front of the batch
(`Aggregate(..., completionSize: 500, completionTimeout: ...)`), the same way as with Apache Camel.

Rows the statements return — `INSERT … RETURNING id` on PostgreSQL, SQLite, MariaDB and Firebird,
`INSERT … OUTPUT inserted.id VALUES …` on SQL Server — are collected when `outputType` reads rows (`SelectList` and the other row modes; under
`Auto` a batch collects nothing, as Apache Camel's `executeBatch` collects no result sets). `redbSql.generatedKeys` holds them in item order, as `List<Dictionary<string, object?>>` or
`List<T>` with `outputClass`, and `redbSql.generatedKeysRowCount` their number; the body stays the list that was written,
as with Apache Camel's `CamelSqlGeneratedKeyRows`. A failed item of a batch that continues past errors returns none.
MySQL's `LAST_INSERT_ID()` and Oracle's `RETURNING … INTO` need a separate statement or OUT parameters and are not
collected in a batch.

The `sql.execute` span of a batch carries `db.operation.batch.size` (from two items, as the OpenTelemetry database
conventions count a batch), `redb.sql.batch.strategy`, `redb.sql.batch.chunks` for a `DbBatch` and
`redb.sql.batch.failed_index` when an item ends the batch. Parameter values are never tagged.

## Parameters and binding

SQL placeholders are written **`:#name`**, as in Apache Camel. `@` belongs to the database — T-SQL variables
(`DECLARE @n int = :#value`), `EXEC proc @arg = :#value` argument names, MySQL user variables, `@@ROWCOUNT` — and reaches
it as written. Placeholders are found only in the SQL itself: `':#x'`, `"col:#x"`, `-- :#x`, `/* :#x */` and PostgreSQL
`$$ … $$` bodies are left alone. There is no implicit `:#body` — a scalar body (string, number) never binds itself into a
parameter; use `param.x=${body}`. Apache Camel's inline expressions `:#${...}` and `:#in:name` lists are refused with a
message; use `param.name=${...}`.

What the provider receives is set by `placeholderStyle`:

| `placeholderStyle` | Sent to the provider | For |
|---|---|---|
| `At` (default) | `@name`, one parameter per name | Npgsql, SqlClient, Microsoft.Data.Sqlite, MySqlConnector, Firebird |
| `Colon` | `:name`, one parameter per occurrence | Oracle (ODP.NET binds by name or by position) |
| `Question` | `?`, one parameter per occurrence, in order | ODBC, OleDb |

On SQL Server with `placeholderStyle=At`, a `:#name` parameter becomes `@name`: do not also declare a variable `@name` in
the same statement. `Colon` adds a parameter for every occurrence, which is what ODP.NET's default positional binding
needs; a provider that binds `:name` by name and refuses two parameters with one name (Microsoft.Data.Sqlite) keeps `At`.

MySQL and MariaDB escape a quote inside a literal with a backslash by default (`'it\'s'`); standard SQL does not, and
`'C:\'` is a complete literal on PostgreSQL, SQL Server, Oracle and SQLite. The connector does not guess from the provider:
where the SQL text uses backslash escapes, set `backslashEscapes=true`, or double the quote (`'it''s'`), which every
database reads. Without the option a `:#name` after `\'` is not found and the provider rejects the statement. MySqlConnector
also reads every `@var` of a statement with parameters as a parameter: MySQL user variables need `AllowUserVariables=true` in
the connection string.

Binding priority for each `:#name` found in the statement:

| # | Source |
|---|--------|
| 0 | `param.<name>=...` from the URI (constant or `${...}` expression) |
| 1 | Exchange header with the same name |
| 2 | Body, if it is a map — a dictionary (`IDictionary<string, object?>`, `IReadOnlyDictionary<string, object?>`, non-generic `IDictionary` such as a CSV row) or a JSON object (`JsonElement`, `JsonDocument`, `JsonObject`) — by key: the key `name`, failing that the one key equal to it ignoring case. As in Apache Camel, a POCO, XML or JSON-text body is not read by name |
| — | *(nothing matched)* → `InvalidOperationException` naming the parameter and the statement |

As in Apache Camel, a placeholder with no value is an error, not a silent `NULL`. A source that is present
binds its value even when the value is null: a header or key set to `null` / `DBNull.Value`, an empty
string, or a `param.*` expression that evaluates to null all bind `NULL`.

Values are normalised before binding: `null` and `""` become `NULL`; JSON values (`JsonElement`,
`JsonNode` — what unmarshalling JSON to `object` produces) become a string, `long` / `decimal` / `double`,
`bool` or `NULL`, and a JSON object or array becomes its JSON text.

In `onSuccess` / `onFailure` / `onBatchComplete` the sources are `param.*`, the polled row's columns and
the exchange headers, under the same rule. The special parameter `:#redbError` always has a value:
`exchange.Exception.Message` in `onFailure`, `NULL` where nothing failed.

In a poll consumer, the poll query's placeholders take their values from `param.*` only, resolved **without an Exchange**
— constants only, `${header...}` expressions will not resolve there — and a placeholder without a `param.*` value is an
error.

### Batch item binding

Each item of a batch binds its own values. Sources, first match wins:

| # | Source |
|---|--------|
| 0 | `param.<name>=...` — a `${...}` expression is evaluated per item: `${body}` is the item, `${header.x}` sees the carrying exchange's headers and the keys of a dictionary item, `${property.x}` the exchange properties |
| 1 | The item's own value for `name`, by item shape (below) |
| 2 | Header of the exchange that carries the batch |
| — | *(nothing matched)* → the item fails: with `breakBatchOnError=true` the batch stops and rolls back (`InvalidOperationException`, index in `redbSql.batchFailedIndex`); with `false` the item is listed in `redbSql.batchErrors` |

| Item shape | How `name` is found |
|---|---|
| `IDictionary<string, object?>`, `IReadOnlyDictionary<string, object?>`, `IDictionary` (CSV rows are `Dictionary<string, string>`) | the key `name`; failing that, the one key equal to it ignoring case — two such keys match nothing |
| JSON object (`JsonElement`, `JsonObject`, `JsonDocument` with an object root) | the property `name`, same case rule; values normalised as above |
| POCO — a type of the application, anonymous types and records included | public property: the same name ignoring case, `[Column("name")]`, or `snake_case` → `PascalCase` — the rule `outputClass` uses for rows |
| `IExchange` (`AggregationStrategies.GroupedExchange()`) | its own headers, then its body by the shapes above; `param.*` expressions are evaluated on the item exchange; the carrying exchange's headers are not used |
| XML node (`XElement`, `XmlNode` — e.g. a `List<XElement>` from XPath) | no named values, as in Apache Camel — bind with an expression relative to the item: `param.id=${xpath('@id')}`, `param.name=${xpath('name')}` |
| scalar, string, array, list, any other .NET type (`Uri`, `Stream`, …) | no named values — bind with `param.x=${body}` or an expression; the properties of a .NET type are not record fields |

CSV values are text and are sent as text: a database that does not convert a text parameter to a numeric
column (PostgreSQL) needs a cast in the statement, `CAST(:#id AS int)`, or a typed item (POCO).

## URI parameters

Names are the property names of `SqlEndpointOptions`, case-insensitive. Numeric and enum options take
constants or `{{property}}` placeholders; a `${...}` expression there fails endpoint creation.

| Parameter | Type | Default |
|---|---|---|
| `mode` | `Poll` \| `Execute` \| `Procedure` | `Execute` |
| `dataSource` | registered data source name (a leading `#` is stripped); fixed when the endpoint is created — a dynamic target is a dynamic endpoint (`ToD`), as in Apache Camel | — |
| `connectionString` / `provider` | inline connection instead of `dataSource` | — |
| `commandTimeout` | seconds | `30` |
| `transacted` | **consumer only** — SELECT + `onSuccess`/`onFailure` in one transaction | `false` |
| `isolationLevel` | `System.Data.IsolationLevel` | provider default |
| `readOnly` | `true`: run on the data source's read replica (`ReadConnectionString`) — declared by the endpoint's author, never guessed from the SQL; refused with `batchSize` and for a poll with `onSuccess`/`onFailure`/`onBatchComplete` or `transacted` | `false` |
| `placeholderStyle` | how `:#name` reaches the provider: `At` (`@name`) \| `Colon` (`:name`, Oracle) \| `Question` (`?`, ODBC) — see [Parameters and binding](#parameters-and-binding) | `At` |
| `backslashEscapes` | `true`: inside `'…'` and `"…"` a backslash escapes the next character, as MySQL and MariaDB read them by default; `false`: standard SQL, `\` is an ordinary character — see [Parameters and binding](#parameters-and-binding) | `false` |
| `outputType` | `Auto` \| `SelectList` \| `SelectOne` \| `StreamList` \| `Scalar` \| `None` | `Auto` |
| `outputClass` | POCO type name for mapped rows (`SelectList` → `List<T>`, `SelectOne` → `T`, `StreamList` → `IAsyncEnumerable<T>`) | — |
| `outputHeader` | put the result into this header and leave the body untouched; a `${...}` expression names the header per exchange | — |
| `noop` | skip execution (dry run) | `false` |
| `delay` / `initialDelay` | poll interval / first delay, ms | `500` / `1000` |
| `fixedRate` | measure delay from cycle start | `false` |
| `repeatCount` | 0 = forever | `0` |
| `maxMessagesPerPoll` | −1 = no limit | `-1` |
| `routeEmptyResultSet` / `sendEmptyMessageWhenIdle` | emit an Exchange on empty polls | `false` |
| `onSuccess` / `onFailure` / `onBatchComplete` | SQL run after each row / after a failed row / after the cycle | — |
| `pollDelivery` | `PerRow` (an exchange per row) \| `List` (one exchange with every polled row, Apache Camel `useIterator=false`); `List` is refused with `outputType=Scalar` or `SelectOne` | `PerRow` |
| `batchSize` | > 0 turns a list body into a batch, all items in one transaction | `0` |
| `breakBatchOnError` | `true`: stop and roll back on the first failing item; `false`: undo failed items to savepoints and commit the rest | `true` |
| `param.<name>` | explicit parameter value or `${...}` expression | — |
| `procedureName` | required for `mode=Procedure` | — |
| `asFunction` | `SELECT fn(...)` instead of `CALL`/`EXEC`; the result is the scalar, so `OUT`/`INOUT` parameters are refused | `false` |
| `procedureParams` | `DIR:name:DbType[:expr],…` | — |

`outputType=Auto` is decided by what the statement returns when it runs, as Apache Camel's `execute()` asks the driver —
the text is never parsed: a result set (the first one, even an empty one) is delivered as `SelectList`, and a statement
without one leaves the body untouched and sets `redbSql.updateCount` (`None`). A comment before `SELECT`, a `WITH` that
writes and an `INSERT … RETURNING` all come out as what they return; `redbSql.outputType` reports the resolved type. In a
batch `Auto` collects no rows (above).

`outputType=StreamList` gives the route an `IAsyncEnumerable` that reads rows from an open reader. The reader, its
command, transaction and connection are released when the rows are read to the end, when reading stops early, or — as in
Apache Camel — when the exchange ends, whether or not anyone read the stream. The rows can be read once. Inside a route
transaction `StreamList` is refused: the transaction cannot commit while the reader is open, and a later SQL step in the same
transaction would need a second connection and a distributed transaction; use `SelectList` there.

The stream belongs to the exchange that ran the endpoint. A copy of that exchange (WireTap, RecipientList, Threads, a
`seda:` hand-off) shares the body but not the release: disposing the copy leaves the stream alone, and once the original
ends the copy finds the stream released — read it in the segment that produced it, or use `SelectList` where the exchange
is handed to another thread. `Enrich` hands the stream over to the exchange it enriches, as Apache Camel hands over the
resource exchange's completions, so `.Enrich("sql:…?outputType=StreamList")` is read by the enriched route.

A poll consumer with `outputType=StreamList` keeps its reader open while the route processes each row. Where its
`onSuccess` / `onFailure` run follows Apache Camel, which runs `onConsume` through its `JdbcTemplate`:

| Poll | `onSuccess` / `onFailure` run on | PostgreSQL, SQL Server | SQLite (default journal mode) |
|---|---|---|---|
| outside a transaction | a connection of their own to the primary database | works | the open reader blocks the write: use `transacted=true` or WAL |
| `transacted=true` (or an ambient transaction) | the reader's connection, in its transaction | the driver refuses a second command while the reader is open: the failure is logged and the transaction rolls back — use the default mode | works |

The default mode reads the rows first and closes the reader before processing, so SELECT and `onSuccess` share one
connection and transaction on every provider. A failing `onSuccess` is logged in every mode.

With `pollDelivery=List` the poll is one exchange, as Apache Camel's `useIterator=false`: the body is the list of rows
(`List<Dictionary<string, object?>>`, or `List<T>` with `outputClass`), limited by `maxMessagesPerPoll`; with
`outputType=StreamList` it is the open stream itself, for the route to read — straight into a `sql:` batch, say.
`onSuccess` / `onFailure` and `onBatchComplete` run once, after the route and after the reader is closed, on the
consumer's connection and transaction; their values come from headers and `param.*`, since a list has no row columns.
`routeEmptyResultSet=true` delivers an empty list.

`outputClass` resolves an assembly-qualified or loaded type name and maps columns to properties (case-insensitive,
`[Column]`, `snake_case` → `PascalCase`); a column without a property is skipped, and without `outputClass` rows are
`Dictionary<string, object?>`. A value reaches a property only when the conversion is lossless and unambiguous — as Apache
Camel's `BeanPropertyRowMapper` refuses a type mismatch. Anything else fails the exchange with an
`InvalidOperationException` naming the column, the property and both types (never the value):

| Property | Accepted column values |
|---|---|
| any | a value of the property's own type |
| nullable (`int?`) or reference (`string`) | `NULL` sets `null`; for any other value type `NULL` is an error |
| integer types | integers in range, `REAL` / `decimal` without a fractional part, integer text |
| `decimal`, `double`, `float` | numbers the property holds exactly, numeric text |
| `bool` | `0` / `1`, `true` / `false` text |
| `enum` | the number or the name (case-insensitive) of a defined member |
| `Guid` | text |
| `string` | text, numbers, `bool`, `Guid`; a date or time is an error — use a date/time property |
| `DateTime` | ISO 8601 text; a `DateTimeOffset` is an error — the offset would be lost |
| `DateTimeOffset` | a UTC `DateTime` (PostgreSQL `timestamptz`), text with an offset; a timestamp without a time zone (`timestamp`, `datetime2`) is an error — use `DateTime` |
| `DateOnly` | a `DateTime` at midnight (`date`), date text |
| `TimeOnly`, `TimeSpan` | a `TimeSpan` (`time`), time text |

Text is parsed with the invariant culture, whatever the culture of the process. `ScalarMapper<T>` follows the same rules.

## Headers written back

All prefixed with `redbSql.`: `query`, `updateCount`, `rowCount`, `dataSource`, `outputType` (the type `Auto` resolved
to), `error`, `transactionId`, `storedProcedure`, `executionTime` (ms: resolving, binding and executing the statement and
reading its result; for `StreamList`, until the reader is open). Batch mode adds `batchStrategy`
(`None` / `DbBatch` / `Commands` / `Savepoints`, on failure too), `batchItemCount`, `batchChunkCount` (round trips of a
`DbBatch`), `batchFailedIndex` and `batchErrors`, and — with an `outputType` that reads rows — `generatedKeys` and
`generatedKeysRowCount`. When the driver does not name the failed command of a `DbBatch`,
`batchFailedIndex` is the first item of the failed chunk and `exception.Data["redbSql.batchFailedChunk"]` holds its
range (`"500..999"`).

## Transactions

Producers (`Execute` and `Procedure`) **always** open a local transaction when there is no ambient
one — write atomicity does not need `transacted=true`, and the option is a no-op there. The consumer
is the only place that reads `transacted`. A route-level `.Transacted()` wraps the pipeline in a
`TransactionScope`; the connector then detects the ambient transaction, skips its local one, and
enlists the connection.

The connector opens its own connection. Inside a transacted route that also writes through `redb` to the
**same** database that is a second connection in the transaction, which SQL Server refuses (a distributed
transaction), PostgreSQL cannot commit and SQLite runs in autocommit — so `sql:` and `redb` against one database in
one `.Transacted()` block is not supported. Raw SQL that must commit together with `redb` work runs through
`redb.Context` (`ExecuteAsync` / `QueryAsync` inside `ProcessWithRedb`), on the transaction's own connection.

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

`SqlConnectionOptions` also carries `ReadConnectionString` (a read replica, used only by endpoints with `readOnly=true`),
`TestOnBorrow` / `ValidationQuery`, and `EnableRetryOnFailure` / `MaxRetries` / `RetryDelay`. Instead of a registered
`ProviderName`, `ProviderFactory` takes the driver's factory directly (`opts.ProviderFactory = MySqlConnectorFactory.Instance`)
and wins when both are set.

## Providers

The connector itself has no provider-specific code: what differs is the driver and the SQL dialect, and the options below
are how a route states it. Every database in the table is covered by end-to-end tests against a real server — batches that
stop and that go on past errors, returned keys, parameters, literals and polling.

| Database (tested) | Driver, factory | `placeholderStyle` | Batch with `breakBatchOnError=true` | Keys of a batch |
|---|---|---|---|---|
| PostgreSQL 18 | Npgsql 9.0.3, `NpgsqlFactory.Instance` | `At` | `DbBatch`; the driver names the failed item | `RETURNING` |
| SQL Server | Microsoft.Data.SqlClient 7.0.3, `SqlClientFactory.Instance` | `At` | `DbBatch`; the driver names the failed item | `OUTPUT inserted.*` |
| SQLite | Microsoft.Data.Sqlite 10.0.0, `SqliteFactory.Instance` | `At` | `Commands` | `RETURNING` (SQLite 3.35+) |
| MySQL 8.4 | MySqlConnector 2.6.2, `MySqlConnectorFactory.Instance` | `At` | `DbBatch`; the driver does not name the failed item | not collected: no `RETURNING` |
| MariaDB 11 | MySqlConnector 2.6.2, `MySqlConnectorFactory.Instance` | `At` | as MySQL | `RETURNING` |
| Oracle Database 23 (Free) | Oracle.ManagedDataAccess.Core 23.26.301, `OracleClientFactory.Instance` | **`Colon`** | `Commands` | not collected: `RETURNING … INTO` needs OUT parameters |
| Firebird 5 | FirebirdSql.Data.FirebirdClient 10.3.4, `FirebirdClientFactory.Instance` | `At` | `Commands` | `RETURNING` |
| any other `DbConnection` | `ProviderFactory`, or a factory registered under `ProviderName` | `At`; `Question` for ODBC / OleDb | `DbBatch` when `CanCreateBatch`, otherwise `Commands` | the rows the statement returns |

`breakBatchOnError=false` runs with savepoints on every tested database.

What a failed statement does to its transaction decides how much of a batch survives it:

| Database | A failed statement inside a transaction |
|---|---|
| PostgreSQL | aborts the transaction block, whatever the error; each item's savepoint brings it back, so a batch that goes on past errors still commits the other items |
| SQL Server | a duplicate key is undone alone; a conversion error (245), any error under `XACT_ABORT ON` and a deadlock victim end the transaction — a batch that goes on past errors stops there, nothing committed |
| SQLite | a constraint violation is undone alone; `INSERT OR ROLLBACK` ends the transaction |
| MySQL, MariaDB | undone alone (duplicate key, a value strict `sql_mode` refuses); a deadlock ends the transaction |
| Oracle, Firebird | undone alone |

What to know per database:

- **PostgreSQL** — `$$ … $$` and `$tag$ … $tag$` bodies are not searched for placeholders; `$1` is not a dollar quote.
  CSV text needs a cast into a numeric column: `CAST(:#id AS int)`.
- **SQL Server** — `:#name` becomes `@name` with `At`: do not declare a variable of the same name in the statement.
  T-SQL variables and `EXEC proc @arg = :#value` work as written.
- **SQLite** — the driver binds `:name` by name and refuses two parameters with one name, so a repeated placeholder needs
  `At`, not `Colon`. An open reader blocks writers in the default journal mode: a stream read from a file cannot feed a
  batch into the same file, and a streaming poll outside a transaction needs `transacted=true` or WAL for `onSuccess`.
- **MySQL, MariaDB** — the driver does not report which command of a `DbBatch` failed: `redbSql.batchFailedIndex` is the
  first item of the failed chunk and `exception.Data["redbSql.batchFailedChunk"]` holds the chunk's range; a smaller
  `batchSize` narrows it. Literals written with backslash escapes (`'it\'s'`) need `backslashEscapes=true`, or double the
  quote (`'it''s'`). User variables (`SET @u = 5`) in a statement with parameters need `AllowUserVariables=true` in the
  connection string, or MySqlConnector refuses `@u` as an undefined parameter. `#` comments are not recognised by the
  placeholder scanner: a comment that contains `:#` is written with `--` or `/* */`.
- **Oracle** — set `placeholderStyle=Colon`. ODP.NET binds by position by default, so the connector sends one parameter
  per occurrence and a repeated `:#x` works. A `SELECT` without a table needs `FROM DUAL`.
- **Firebird** — `SELECT` without a table needs `FROM RDB$DATABASE`; a repeated `:#x` is bound by name.

## Fluent DSL

`Sql.Poll(...)` / `Sql.Execute(...)` compile to exactly the URI strings above and are equivalent:

```csharp
using redb.Route.Sql.Fluent;

From(Sql.Poll("SELECT * FROM orders WHERE processed = 0")
        .DataSource("main")
        .Delay(5000)
        .OnSuccess("UPDATE orders SET processed = 1 WHERE id = :#id")
        .MaxMessagesPerPoll(100))
    .To("direct://handle");
```

`Sql.Procedure("name")` writes the name into the URI path, which is the procedure name in `Procedure` mode.

A batch, and a database that needs its own options:

```csharp
From("direct://bulk")
    .To(Sql.Execute("INSERT INTO logs(id, message) VALUES(:#id, :#message)")
        .DataSource("oracle")
        .PlaceholderStyle(SqlPlaceholderStyle.Colon)
        .Batch(500)
        .BreakBatchOnError(false));
```

`CommandTimeout`, `Delay`, `InitialDelay`, `RepeatCount`, `MaxMessagesPerPoll`, `Batch`, `Param` and `OutputHeader` accept
both constants and `IExpression` for runtime resolution; `DataSource` and `ConnectionString` take a constant only — an
`IExpression` that is not a constant is refused, since the endpoint is fixed when it is created. A constant given to `Param`
as a number or a date is written invariantly (`12.5`, `2026-09-16T10:00:00.0000000Z`), whatever the culture of the process.

| Builder method | URI parameter |
|---|---|
| `Batch(n)`, `BreakBatchOnError()` / `BreakBatchOnError(bool)` | `batchSize`, `breakBatchOnError` |
| `ReadOnly()` | `readOnly=true` |
| `PlaceholderStyle(SqlPlaceholderStyle)` | `placeholderStyle` |
| `BackslashEscapes()` | `backslashEscapes=true` |
| `PollDelivery(SqlPollDelivery)` | `pollDelivery` |
| `Param("x", value)` / `Param("x", expression)` | `param.x` — the name is written `x` or `:#x`; `@x` is refused with an `ArgumentException` |
| `AsFunction()`, `In(...)`, `Out(...)`, `InOut(...)` | `asFunction`, `procedureParams` |

A default is not written into the URI: an option left unset keeps the default of `SqlEndpointOptions`.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET
