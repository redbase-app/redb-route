using System.Data.Common;
using System.Diagnostics;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql.Batch;
using redb.Route.Sql.Connection;
using redb.Route.Sql.Mapping;
using redb.Route.Telemetry;

namespace redb.Route.Sql;

/// <summary>
/// Producer for SQL Execute mode. Executes INSERT/UPDATE/DELETE/SELECT statements
/// with auto-bind parameters from Exchange headers and body.
/// Supports all <see cref="SqlOutputType"/> mappings; a list body with <c>batchSize</c> set is written as a batch
/// by <see cref="SqlBatchProcessor"/>.
/// </summary>
/// <remarks>
/// <b>Transaction behavior:</b> always wraps execution in a local <see cref="System.Data.Common.DbTransaction"/>.
/// If an ambient <see cref="System.Transactions.Transaction.Current"/> exists (e.g. from
/// <c>BeginTransaction()</c> DSL), the local transaction is skipped and the connection
/// auto-enlists in the ambient <see cref="System.Transactions.TransactionScope"/>.
/// This guarantees write atomicity (like EF <c>SaveChanges</c>) without requiring
/// an explicit <c>?transacted=true</c> option.
/// </remarks>
internal sealed class SqlProducer : IProducer
{
    private readonly SqlEndpoint _endpoint;
    private readonly SqlEndpointOptions _options;
    private ILogger? _logger;

    internal SqlProducer(SqlEndpoint endpoint, SqlEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("SQL producer started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("SQL producer stopped");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            "sql.execute", ActivityKind.Client,
            "db.system", _endpoint.Component.Scheme,
            _endpoint.Uri.NormalizedKey);

        if (_options.Noop)
        {
            exchange.In.Headers[SqlHeaders.Query] = ResolveQuery(exchange);
            return;
        }

        // The statement that runs: the URI path, the query option or the named query either refers to. It is resolved before
        // anything is opened, so a failing query leaves a streamed body unread and released with the exchange.
        var sql = ResolveQuery(exchange);

        // Batch mode: a list, sequence, stream or JSON array body is written item by item through one statement.
        if (_options.BatchSize > 0 && BatchSource.TryOpen(exchange.In.Body, ct, out var items))
        {
            await using (items.ConfigureAwait(false))
            {
                await new SqlBatchProcessor(_endpoint, _options, _logger)
                    .ProcessAsync(exchange, items, sql, activity, ct)
                    .ConfigureAwait(false);
            }
            return;
        }

        var factory = ResolveConnectionFactory();
        var sw = Stopwatch.StartNew();

        // Auto is decided by what the statement returns when it runs — as Apache Camel's execute() asks the driver — never by
        // its text; it resolves below, and until then it is not a stream.
        var outputType = _options.OutputType;

        // Measured on PostgreSQL and SQL Server: a transaction does not commit while a reader is open on its connection, and
        // a second connection in the same transaction needs a distributed one. A stream would outlive this endpoint inside
        // the route's transaction, so it is refused before any connection is opened.
        if (outputType == SqlOutputType.StreamList && Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "outputType=StreamList keeps a reader open after the endpoint, and this exchange runs inside a transaction " +
                "(a transacted route). The transaction cannot commit while that reader is open, and a later SQL step in the " +
                "same transaction would need a second connection and a distributed transaction. Use outputType=SelectList " +
                "inside the transaction, or read the stream outside it.");
        }

        // The replica only when the endpoint's author declares the statement read-only: the SQL text cannot tell.
        var connection = await factory.CreateConnectionAsync(readOnly: _options.ReadOnly, ct).ConfigureAwait(false);

        // From here the connection is owned resource: everything that can throw — starting the
        // transaction included, since a dropped socket or a cancellation surfaces exactly there —
        // runs under the finally that returns it to the pool. This is the hot path for audit and
        // usage writes, and those failure modes come in bursts, so a leak here drains the pool
        // rather than dripping.
        var hasAmbientTx = Transaction.Current != null;
        DbTransaction? tx = null;
        DbCommand? cmd = null;
        var streamOwnsResources = false;

        try
        {
            // If ambient TransactionScope exists (route-level .Transacted()), the connection
            // auto-enlists — no local DbTransaction needed. Otherwise always wrap in a
            // local transaction for atomicity (like EF SaveChanges).
            if (!hasAmbientTx)
            {
                tx = _options.IsolationLevel.HasValue
                    ? await connection.BeginTransactionAsync(_options.IsolationLevel.Value, ct).ConfigureAwait(false)
                    : await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            }

            cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeout;
            if (tx != null) cmd.Transaction = tx;

            SqlParameterBinder.Bind(cmd, exchange, _options.ExplicitParameters, _options.PlaceholderStyle, _options.BackslashEscapes);

            // redbSql.executionTime covers executing the statement and reading its result; for a stream, opening the reader.
            switch (outputType)
            {
                case SqlOutputType.Auto:
                    outputType = await ExecuteAuto(cmd, exchange, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.SelectList:
                    await ExecuteSelectList(cmd, exchange, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.SelectOne:
                    await ExecuteSelectOne(cmd, exchange, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.Scalar:
                    await ExecuteScalar(cmd, exchange, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.StreamList:
                    // StreamList hands connection, cmd and tx to the stream, which disposes them
                    // as the enumerator is disposed (fully consumed or abandoned early — the
                    // iterators hold them in `await using`). The flag, not the option value,
                    // decides who cleans up: an error before this line — the reader failing to
                    // open, say — means ownership never left this method.
                    await ExecuteStreamList(cmd, exchange, connection, tx, ct).ConfigureAwait(false);
                    streamOwnsResources = true;
                    SqlExchangeHeaders.SetCommon(exchange, _options, sql, sw.ElapsedMilliseconds, outputType);
                    return;

                case SqlOutputType.None:
                default:
                    await ExecuteNonQuery(cmd, exchange, ct).ConfigureAwait(false);
                    break;
            }

            SqlExchangeHeaders.SetCommon(exchange, _options, sql, sw.ElapsedMilliseconds, outputType);

            if (tx != null)
            {
                exchange.In.Headers[SqlHeaders.TransactionId] = tx.GetHashCode().ToString();
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SQL execution failed: dataSource={DataSource}, outputType={OutputType}",
                _options.DataSource, _options.OutputType);
            if (tx != null)
            {
                // The rollback is not interruptible: a cancelled token would only turn it into a "rollback failed" log.
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rbEx) { _logger?.LogError(rbEx, "SQL transaction rollback failed"); }
            }
            throw;
        }
        finally
        {
            if (!streamOwnsResources)
                await ReleaseAsync(cmd, tx, connection).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the command, the transaction and the connection, each even when the one before it failed (the first failure
    /// propagates): a transaction whose disposal throws must not leave the connection out of the pool.
    /// </summary>
    private static async ValueTask ReleaseAsync(DbCommand? cmd, DbTransaction? tx, DbConnection connection)
    {
        try
        {
            if (cmd != null)
                await cmd.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (tx != null)
                    await tx.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <c>outputType=Auto</c>, as Apache Camel's <c>execute()</c>: the statement runs as a reader and the driver says what it
    /// is — a result set (its rows, as <see cref="SqlOutputType.SelectList"/>) or none (the rows affected, as
    /// <see cref="SqlOutputType.None"/>). The text is never parsed: a comment before SELECT, a WITH that writes, an
    /// <c>INSERT … RETURNING</c> all come out as what they return.
    /// </summary>
    private async Task<SqlOutputType> ExecuteAuto(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (reader.FieldCount > 0)
        {
            await ReadRowsAsync(reader, poco, exchange, ct).ConfigureAwait(false);
            return SqlOutputType.SelectList;
        }

        // No result set: the statement is read to its end, and the rows affected are taken once the reader is closed — the
        // point at which every driver has them, as the batch reads them.
        while (await reader.NextResultAsync(ct).ConfigureAwait(false))
        {
        }

        await reader.CloseAsync().ConfigureAwait(false);
        exchange.In.Headers[SqlHeaders.UpdateCount] = reader.RecordsAffected;
        return SqlOutputType.None;
    }

    private async Task ExecuteSelectList(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await ReadRowsAsync(reader, poco, exchange, ct).ConfigureAwait(false);
    }

    /// <summary>Reads every row of <paramref name="reader"/> into the result: a <c>List&lt;T&gt;</c> with <c>outputClass</c>, dictionaries otherwise.</summary>
    private async Task ReadRowsAsync(DbDataReader reader, SqlRowMapperFactory.PocoMapping? poco, IExchange exchange, CancellationToken ct)
    {
        if (poco != null)
        {
            // outputClass is set → accumulate into a typed List<T>, not List<Dictionary>.
            var typedRows = poco.CreateList();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                typedRows.Add(poco.Map(reader));

            exchange.In.Headers[SqlHeaders.RowCount] = typedRows.Count;
            SetResult(exchange, typedRows);
            return;
        }

        var mapper = new DictionaryRowMapper();
        var rows = new List<Dictionary<string, object?>>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(mapper.Map(reader));

        exchange.In.Headers[SqlHeaders.RowCount] = rows.Count;
        SetResult(exchange, rows);
    }

    private async Task ExecuteSelectOne(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);
        var mapper = new DictionaryRowMapper();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = poco != null ? poco.Map(reader) : mapper.Map(reader);
            exchange.In.Headers[SqlHeaders.RowCount] = 1;
            SetResult(exchange, row);
        }
        else
        {
            exchange.In.Headers[SqlHeaders.RowCount] = 0;
            SetResult(exchange, null);
        }
    }

    private async Task ExecuteScalar(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result == DBNull.Value) result = null;

        exchange.In.Headers[SqlHeaders.RowCount] = result != null ? 1 : 0;
        SetResult(exchange, result);
    }

    private async Task ExecuteStreamList(DbCommand cmd, IExchange exchange, DbConnection connection, DbTransaction? tx, CancellationToken ct)
    {
        // StreamList differs from SelectList: rows are streamed lazily as IAsyncEnumerable, so the reader, command,
        // transaction and connection outlive this method. The streamed result takes them over, and — as in Apache Camel,
        // which closes the result set on exchange completion — it is registered with the exchange: whatever the route does
        // with the body, the connection is returned when the exchange ends.
        // The row type is resolved before the reader is open: a bad outputClass must not leave a reader behind.
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);
        var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        object body;
        IAsyncDisposable release;
        try
        {
            (body, release) = poco != null
                ? StreamedQueryResult.Create(poco.ElementType, reader, poco.Map, cmd, connection, tx)
                : StreamedQueryResult.Create(typeof(Dictionary<string, object?>), reader, new DictionaryRowMapper().Map, cmd, connection, tx);
        }
        catch
        {
            // The stream never took the reader over: close it here, the caller releases the rest.
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        ExchangeResources.ReleaseWithExchange(exchange, release);
        SetResult(exchange, body);
        exchange.In.Headers[SqlHeaders.RowCount] = -1; // unknown until fully iterated
    }

    private static async Task ExecuteNonQuery(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        exchange.In.Headers[SqlHeaders.UpdateCount] = affected;
    }

    /// <summary>
    /// Delivers the query result. With <c>outputHeader</c> set, the result goes to that header
    /// and the body is left untouched — which is what makes it possible to enrich a message
    /// with a lookup without destroying the payload the route is already carrying.
    /// Without it, the result replaces the body.
    /// </summary>
    private void SetResult(IExchange exchange, object? value)
    {
        if (!string.IsNullOrEmpty(_options.OutputHeader))
            exchange.In.Headers[SqlExchangeHeaders.ResolveOutputHeader(_options.OutputHeader, exchange)] = value;
        else
            exchange.In.Body = value;
    }

    private string ResolveQuery(IExchange exchange)
    {
        var rawSql = _endpoint.QueryOrProcedure;

        if (_options.Query is { } dv)
        {
            var resolved = dv.Resolve(exchange);
            if (resolved != null)
                rawSql = resolved;
        }

        return ResolveNamedQuery(rawSql);
    }

    private string ResolveNamedQuery(string sql)
    {
        if (!sql.StartsWith(SqlNamedQueryRegistry.RefPrefix, StringComparison.OrdinalIgnoreCase))
            return sql;

        var registry = _endpoint.SqlComponent.Context?.GetService<ISqlNamedQueryRegistry>();
        if (registry != null)
            return registry.ResolveRef(sql);

        throw new InvalidOperationException(
            $"Named query reference '{sql}' cannot be resolved — no ISqlNamedQueryRegistry is registered.");
    }

    private ISqlConnectionFactory ResolveConnectionFactory() => _endpoint.ResolveConnectionFactory();

}
