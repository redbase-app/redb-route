using System.Data.Common;
using System.Diagnostics;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Sql.Connection;
using redb.Route.Sql.Mapping;

namespace redb.Route.Sql;

/// <summary>
/// Polling consumer for SQL. Periodically executes a SELECT query and emits
/// an <see cref="IExchange"/> per row (or batch). Supports OnSuccess/OnFailure
/// lifecycle SQL and named query references.
/// </summary>
/// <remarks>
/// <b>Transaction behavior:</b> by default runs without a local transaction (SELECT is
/// idempotent). Set <c>?transacted=true</c> to wrap SELECT + OnSuccess/OnFailure in a
/// single <see cref="System.Data.Common.DbTransaction"/> — useful when SELECT + OnSuccess
/// UPDATE must be atomic (e.g. outbox pattern).
/// If an ambient <see cref="System.Transactions.Transaction.Current"/> exists (e.g. from
/// <c>BeginTransaction()</c> DSL), the local transaction is skipped and the connection
/// auto-enlists in the ambient <see cref="System.Transactions.TransactionScope"/>.
/// </remarks>
internal sealed class SqlConsumer : DrainableConsumer
{
    private readonly SqlEndpoint _endpoint;
    private readonly SqlEndpointOptions _options;
    private long _pollCount;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => "sql";

    internal SqlConsumer(SqlEndpoint endpoint, IProcessor processor, SqlEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        if (_options.InitialDelay > 0)
            await Task.Delay(_options.InitialDelay, pollCt).ConfigureAwait(false);

        var delay = TimeSpan.FromMilliseconds(_options.Delay);

        while (!pollCt.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                await Poll(processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested || processingCt.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "SQL consumer poll failed: dataSource={DataSource}",
                    _options.DataSource);
            }

            _pollCount++;
            if (_options.RepeatCount > 0 && _pollCount >= _options.RepeatCount)
                break;

            try
            {
                if (_options.FixedRate)
                {
                    var remaining = delay - sw.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, pollCt).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(delay, pollCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task Poll(CancellationToken ct)
    {
        var factory = ResolveConnectionFactory();
        var sw = Stopwatch.StartNew();

        await using var connection = await factory.CreateConnectionAsync(readOnly: true, ct).ConfigureAwait(false);

        var hasAmbientTx = Transaction.Current != null;
        DbTransaction? tx = null;
        if (!hasAmbientTx && _options.Transacted)
        {
            tx = _options.IsolationLevel.HasValue
                ? await connection.BeginTransactionAsync(_options.IsolationLevel.Value, ct).ConfigureAwait(false)
                : await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var sql = ResolveQuery(null);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeout;
            if (tx != null) cmd.Transaction = tx;

            BindPollParameters(cmd);

            sw.Stop();

            switch (_options.OutputType)
            {
                case SqlOutputType.Scalar:
                    await PollScalar(cmd, connection, tx, sql, sw.ElapsedMilliseconds, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.SelectOne:
                    await PollSelectOne(cmd, connection, tx, sql, sw.ElapsedMilliseconds, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.StreamList:
                    await PollStreamPerRow(cmd, connection, tx, sql, sw.ElapsedMilliseconds, ct).ConfigureAwait(false);
                    break;

                default:
                    await PollPerRow(cmd, connection, tx, sql, sw.ElapsedMilliseconds, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "SQL consumer execution failed: dataSource={DataSource}",
                _options.DataSource);
            if (tx != null)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
                catch (Exception rbEx) { Logger?.LogError(rbEx, "SQL transaction rollback failed"); }
            }
            throw;
        }
        finally
        {
            if (tx != null)
                await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Poll strategies by OutputType ─────────────────────────────

    /// <summary>Default mode: read all rows, emit one exchange per row with lifecycle SQL.</summary>
    private async Task PollPerRow(
        DbCommand cmd, DbConnection connection, DbTransaction? tx,
        string sql, long executionMs, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        var bodies = new List<object?>();
        var mapper = new DictionaryRowMapper();
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (_options.MaxMessagesPerPoll >= 0 && rows.Count >= _options.MaxMessagesPerPoll)
                    break;

                // The dictionary is mapped even when outputClass is set: headers and the
                // OnSuccess/OnFailure parameter auto-bind are driven off the raw columns,
                // and a POCO would silently drop any column it has no property for.
                rows.Add(mapper.Map(reader));
                bodies.Add(poco?.Map(reader));
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var exchange = CreateExchange(bodies[i] ?? row, row, sql, rows.Count, executionMs);
            IncrementInflight();
            try
            {
                await Processor.Process(exchange, ct).ConfigureAwait(false);
                await ExecuteLifecycleSql(connection, tx, _options.OnSuccess, row, exchange, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exchange.Exception = ex;
                await ExecuteLifecycleSql(connection, tx, _options.OnFailure, row, exchange, ct).ConfigureAwait(false);
                if (tx != null) { await tx.RollbackAsync(ct).ConfigureAwait(false); return; }
            }
            finally
            {
                DecrementInflight();
                await exchange.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (rows.Count == 0)
        {
            await ProcessEmptyResult(sql, executionMs, ct).ConfigureAwait(false);
        }

        await ExecuteLifecycleSql(connection, tx, _options.OnBatchComplete, null, null, ct).ConfigureAwait(false);
        if (tx != null) await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streaming mode: reads rows lazily from <see cref="System.Data.Common.DbDataReader"/>
    /// without buffering all rows in memory. Each row is processed immediately with
    /// lifecycle SQL (OnSuccess/OnFailure) executed on the same connection/transaction.
    /// The reader, connection, and transaction stay alive for the entire poll cycle.
    /// </summary>
    private async Task PollStreamPerRow(
        DbCommand cmd, DbConnection connection, DbTransaction? tx,
        string sql, long executionMs, CancellationToken ct)
    {
        var mapper = new DictionaryRowMapper();
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);
        var rowCount = 0;

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (_options.MaxMessagesPerPoll >= 0 && rowCount >= _options.MaxMessagesPerPoll)
                    break;

                var row = mapper.Map(reader);
                var body = poco?.Map(reader) ?? row;
                rowCount++;

                var exchange = CreateExchange(body, row, sql, -1, executionMs);
                IncrementInflight();
                try
                {
                    await Processor.Process(exchange, ct).ConfigureAwait(false);
                    await ExecuteLifecycleSql(connection, tx, _options.OnSuccess, row, exchange, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    exchange.Exception = ex;
                    await ExecuteLifecycleSql(connection, tx, _options.OnFailure, row, exchange, ct).ConfigureAwait(false);
                    if (tx != null) { await tx.RollbackAsync(ct).ConfigureAwait(false); return; }
                }
                finally
                {
                    DecrementInflight();
                    await exchange.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        if (rowCount == 0)
        {
            await ProcessEmptyResult(sql, executionMs, ct).ConfigureAwait(false);
        }

        await ExecuteLifecycleSql(connection, tx, _options.OnBatchComplete, null, null, ct).ConfigureAwait(false);
        if (tx != null) await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Scalar: ExecuteScalarAsync → single exchange. Ideal for FOR XML / FOR JSON.</summary>
    private async Task PollScalar(
        DbCommand cmd, DbConnection connection, DbTransaction? tx,
        string sql, long executionMs, CancellationToken ct)
    {
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result == DBNull.Value) result = null;

        if (result == null)
        {
            await ProcessEmptyResult(sql, executionMs, ct).ConfigureAwait(false);
        }
        else
        {
            var exchange = CreateExchange(result, null, sql, 1, executionMs);
            IncrementInflight();
            try
            {
                await Processor.Process(exchange, ct).ConfigureAwait(false);
                await ExecuteLifecycleSql(connection, tx, _options.OnSuccess, null, exchange, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exchange.Exception = ex;
                await ExecuteLifecycleSql(connection, tx, _options.OnFailure, null, exchange, ct).ConfigureAwait(false);
                if (tx != null) { await tx.RollbackAsync(ct).ConfigureAwait(false); return; }
            }
            finally
            {
                DecrementInflight();
                await exchange.DisposeAsync().ConfigureAwait(false);
            }
        }

        await ExecuteLifecycleSql(connection, tx, _options.OnBatchComplete, null, null, ct).ConfigureAwait(false);
        if (tx != null) await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>SelectOne: first row only → single exchange.</summary>
    private async Task PollSelectOne(
        DbCommand cmd, DbConnection connection, DbTransaction? tx,
        string sql, long executionMs, CancellationToken ct)
    {
        var mapper = new DictionaryRowMapper();
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);
        Dictionary<string, object?>? row = null;
        object? body = null;

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                row = mapper.Map(reader);
                body = poco?.Map(reader) ?? row;
            }
        }

        if (row == null)
        {
            await ProcessEmptyResult(sql, executionMs, ct).ConfigureAwait(false);
        }
        else
        {
            var exchange = CreateExchange(body, row, sql, 1, executionMs);
            IncrementInflight();
            try
            {
                await Processor.Process(exchange, ct).ConfigureAwait(false);
                await ExecuteLifecycleSql(connection, tx, _options.OnSuccess, row, exchange, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exchange.Exception = ex;
                await ExecuteLifecycleSql(connection, tx, _options.OnFailure, row, exchange, ct).ConfigureAwait(false);
                if (tx != null) { await tx.RollbackAsync(ct).ConfigureAwait(false); return; }
            }
            finally
            {
                DecrementInflight();
                await exchange.DisposeAsync().ConfigureAwait(false);
            }
        }

        await ExecuteLifecycleSql(connection, tx, _options.OnBatchComplete, null, null, ct).ConfigureAwait(false);
        if (tx != null) await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Handles empty result for RouteEmptyResultSet / SendEmptyMessageWhenIdle.</summary>
    private async Task ProcessEmptyResult(string sql, long executionMs, CancellationToken ct)
    {
        if (_options.RouteEmptyResultSet)
        {
            var empty = new Dictionary<string, object?>();
            var emptyExchange = CreateExchange(empty, empty, sql, 0, executionMs);
            try { await Processor.Process(emptyExchange, ct).ConfigureAwait(false); }
            finally { await emptyExchange.DisposeAsync().ConfigureAwait(false); }
        }
        else if (_options.SendEmptyMessageWhenIdle)
        {
            var idleExchange = Exchange.Create(new Message(null), _endpoint.ScopeFactory);
            idleExchange.In.Headers[SqlHeaders.Query] = sql;
            idleExchange.In.Headers[SqlHeaders.RowCount] = 0;
            try { await Processor.Process(idleExchange, ct).ConfigureAwait(false); }
            finally { await idleExchange.DisposeAsync().ConfigureAwait(false); }
        }
    }

    // ── Parameter binding for poll query ────────────────────────────

    /// <summary>
    /// Binds explicit parameters from <c>.Param()</c> to the poll command.
    /// Only constant values and SQL parameter references are supported in consumer context
    /// (no exchange available for expression resolution).
    /// </summary>
    private void BindPollParameters(DbCommand cmd)
    {
        var explicitParams = _options.ExplicitParameters;
        if (explicitParams.Count == 0) return;

        var paramNames = SqlParameterParser.ExtractParameterNames(cmd.CommandText);

        foreach (var name in paramNames)
        {
            if (!explicitParams.TryGetValue(name, out var explicitVal))
                continue;

            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = SqlProducer.ResolveParamValue(explicitVal, null);
            cmd.Parameters.Add(param);
        }
    }

    // ── Exchange creation ───────────────────────────────────────────

    /// <summary>
    /// Creates the exchange for one polled row.
    /// </summary>
    /// <param name="body">
    /// What the route sees as the message body — the raw row dictionary, or the mapped POCO
    /// when <c>outputClass</c> is set, or a scalar in Scalar mode.
    /// </param>
    /// <param name="row">
    /// The raw columns of the row, independent of <paramref name="body"/>. Used for the header
    /// copy that OnSuccess/OnFailure parameter binding relies on. Null when there is no row
    /// (Scalar mode).
    /// </param>
    /// <param name="sql">The resolved poll query, echoed into the <c>redbSql.query</c> header.</param>
    /// <param name="rowCount">Rows in this poll cycle; -1 when streaming (not known upfront).</param>
    /// <param name="executionTimeMs">Query execution time in milliseconds.</param>
    private Exchange CreateExchange(object? body, Dictionary<string, object?>? row, string sql, int rowCount, long executionTimeMs)
    {
        var exchange = Exchange.Create(new Message(body), _endpoint.ScopeFactory);
        exchange.In.Headers[SqlHeaders.Query] = sql;
        exchange.In.Headers[SqlHeaders.RowCount] = rowCount;
        exchange.In.Headers[SqlHeaders.ExecutionTime] = executionTimeMs;
        exchange.In.Headers[SqlHeaders.OutputType] = _options.OutputType.ToString();

        if (_options.DataSource != null)
            exchange.In.Headers[SqlHeaders.DataSource] = _options.DataSource;

        // Copy row fields to headers for auto-bind in OnSuccess/OnFailure
        if (row != null)
        {
            foreach (var (key, value) in row)
                exchange.In.Headers[key] = value;
        }

        return exchange;
    }

    private async Task ExecuteLifecycleSql(
        DbConnection connection,
        DbTransaction? tx,
        string? sql,
        Dictionary<string, object?>? row,
        IExchange? exchange,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sql)) return;

        var resolved = ResolveNamedQuery(sql);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = resolved;
        cmd.CommandTimeout = _options.CommandTimeout;
        if (tx != null) cmd.Transaction = tx;

        BindParameters(cmd, row, exchange);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void BindParameters(DbCommand cmd, Dictionary<string, object?>? row, IExchange? exchange)
    {
        var sql = cmd.CommandText;
        var paramNames = SqlParameterParser.ExtractParameterNames(sql);
        var explicitParams = _options.ExplicitParameters;

        foreach (var name in paramNames)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;

            // Special: @redbError → exception message
            if (name.Equals("redbError", StringComparison.OrdinalIgnoreCase) && exchange?.Exception != null)
            {
                param.Value = exchange.Exception.Message;
            }
            // Priority 0: explicit param from .Param()
            else if (explicitParams.TryGetValue(name, out var explicitVal))
            {
                param.Value = SqlProducer.ResolveParamValue(explicitVal, exchange);
            }
            // Priority 1: row data
            else if (row != null && row.TryGetValue(name, out var rowVal))
            {
                param.Value = rowVal ?? DBNull.Value;
            }
            // Priority 2: exchange headers
            else if (exchange?.In.Headers.TryGetValue(name, out var headerVal) == true)
            {
                param.Value = headerVal ?? DBNull.Value;
            }
            else
            {
                param.Value = DBNull.Value;
            }

            cmd.Parameters.Add(param);
        }
    }

    private string ResolveQuery(IExchange? exchange)
    {
        // First try URI path (the SQL after "sql:" scheme)
        var rawSql = _endpoint.QueryOrProcedure;

        // If DynamicValue Query is set in options, use that instead
        if (_options.Query is { } dv)
        {
            var resolved = exchange != null ? dv.Resolve(exchange) : dv.Resolve(new Exchange());
            if (resolved != null)
                rawSql = resolved;
        }

        return ResolveNamedQuery(rawSql);
    }

    private string ResolveNamedQuery(string sql)
    {
        if (!sql.StartsWith(SqlNamedQueryRegistry.RefPrefix, StringComparison.OrdinalIgnoreCase))
            return sql;

        var registry = GetNamedQueryRegistry();

        if (registry != null)
            return registry.ResolveRef(sql);

        throw new InvalidOperationException(
            $"Named query reference '{sql}' cannot be resolved — no ISqlNamedQueryRegistry is registered.");
    }

    private ISqlNamedQueryRegistry? GetNamedQueryRegistry()
    {
        return _endpoint.SqlComponent.Context?.GetService<ISqlNamedQueryRegistry>();
    }

    private ISqlConnectionFactory ResolveConnectionFactory() => _endpoint.ResolveConnectionFactory();
}
