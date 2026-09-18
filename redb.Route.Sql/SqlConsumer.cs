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

        // Measured until the statement has run and its result is read (for a stream, until the reader is open): each poll
        // strategy reads the stopwatch at that point.
        var sw = Stopwatch.StartNew();

        // The replica only with readOnly=true, which Validate refuses for a poll that marks or locks its rows.
        await using var connection = await factory.CreateConnectionAsync(readOnly: _options.ReadOnly, ct).ConfigureAwait(false);

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

            switch (_options.OutputType)
            {
                case SqlOutputType.Scalar:
                    await PollScalar(cmd, connection, tx, sql, sw, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.SelectOne:
                    await PollSelectOne(cmd, connection, tx, sql, sw, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.StreamList when _options.PollDelivery == SqlPollDelivery.List:
                    await PollStreamAsList(cmd, connection, tx, sql, sw, ct).ConfigureAwait(false);
                    break;

                case SqlOutputType.StreamList:
                    await PollStreamPerRow(cmd, connection, tx, sql, sw, ct).ConfigureAwait(false);
                    break;

                default:
                    if (_options.PollDelivery == SqlPollDelivery.List)
                        await PollAsList(cmd, connection, tx, sql, sw, ct).ConfigureAwait(false);
                    else
                        await PollPerRow(cmd, connection, tx, sql, sw, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            // A stop (cancellation of this poll's token) is not a failure; anything else is logged here, whatever the caller does.
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Logger?.LogError(ex, "SQL consumer execution failed: dataSource={DataSource}",
                    _options.DataSource);
            }

            if (tx != null)
            {
                // The rollback is not interruptible: a cancelled token would only turn it into a "rollback failed" log.
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
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
        string sql, Stopwatch stopwatch, CancellationToken ct)
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

        var executionMs = stopwatch.ElapsedMilliseconds;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var exchange = CreateExchange(bodies[i] ?? row, row, sql, rows.Count, executionMs);
            IncrementInflight();
            try
            {
                await Processor.Process(exchange, ct).ConfigureAwait(false);
                await ExecuteOnSuccessAsync(connection, tx, row, exchange, ct).ConfigureAwait(false);
            }
            // A stop is not a failure of the row: a cancellation of this poll's token goes up, onFailure does not run.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                exchange.Exception = ex;
                await ExecuteLifecycleSql(connection, tx, _options.OnFailure, row, exchange, ct).ConfigureAwait(false);
                if (tx != null) { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); return; }
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
    /// without buffering all rows in memory. Each row is processed while the reader stays open.
    /// <para>
    /// Where <c>onSuccess</c> / <c>onFailure</c> run follows Apache Camel, which runs <c>onConsume</c> through its
    /// <c>JdbcTemplate</c>: inside a transaction (<c>transacted=true</c> or an ambient one) on the reader's connection and
    /// transaction; outside one on a connection of their own to the primary database, opened on first use and closed with the
    /// cycle. The consequences are the drivers': PostgreSQL and SQL Server refuse a second command on the reader's connection
    /// (a failing <c>onSuccess</c> is logged and the transaction rolls back), and SQLite in its default journal mode blocks a
    /// write on another connection while the reader is open. <c>onBatchComplete</c> runs after the reader is closed, on the
    /// reader's connection and transaction.
    /// </para>
    /// </summary>
    private async Task PollStreamPerRow(
        DbCommand cmd, DbConnection connection, DbTransaction? tx,
        string sql, Stopwatch stopwatch, CancellationToken ct)
    {
        var mapper = new DictionaryRowMapper();
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);
        var rowCount = 0;
        var inTransaction = tx != null || Transaction.Current != null;
        long executionMs = 0;

        DbConnection? rowStatementConnection = null;

        // Inside a transaction the reader's connection and transaction; outside one a connection of their own, on first use.
        async Task<DbConnection> RowStatementConnectionAsync() =>
            inTransaction ? connection : (rowStatementConnection ??= await OpenRowStatementConnectionAsync(ct).ConfigureAwait(false));

        var rowStatementTransaction = inTransaction ? tx : null;
        var rollbackAfterReader = false;

        try
        {
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                // A stream is measured until its reader is open; the rows are read while the route runs.
                executionMs = stopwatch.ElapsedMilliseconds;

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
                        if (!string.IsNullOrEmpty(_options.OnSuccess))
                        {
                            var statementConnection = await RowStatementConnectionAsync().ConfigureAwait(false);
                            await ExecuteOnSuccessAsync(statementConnection, rowStatementTransaction, row, exchange, ct).ConfigureAwait(false);
                        }
                    }
                    // A stop is not a failure of the row: a cancellation of this poll's token goes up, onFailure does not run.
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        exchange.Exception = ex;
                        if (!string.IsNullOrEmpty(_options.OnFailure))
                        {
                            var statementConnection = await RowStatementConnectionAsync().ConfigureAwait(false);
                            await ExecuteLifecycleSql(statementConnection, rowStatementTransaction, _options.OnFailure, row, exchange, ct).ConfigureAwait(false);
                        }
                        if (tx != null) { rollbackAfterReader = true; break; }
                    }
                    finally
                    {
                        DecrementInflight();
                        await exchange.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            if (rollbackAfterReader)
            {
                // After the reader is closed: PostgreSQL and SQL Server refuse even a rollback while it is open.
                await tx!.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            if (rowStatementConnection is not null)
                await rowStatementConnection.DisposeAsync().ConfigureAwait(false);
        }

        if (rowCount == 0)
        {
            await ProcessEmptyResult(sql, executionMs, ct).ConfigureAwait(false);
        }

        await ExecuteLifecycleSql(connection, tx, _options.OnBatchComplete, null, null, ct).ConfigureAwait(false);
        if (tx != null) await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// List delivery (<c>pollDelivery=List</c>, Apache Camel <c>useIterator=false</c>): reads the rows first, closes the reader,
    /// then hands the route one exchange with all of them (<c>List&lt;T&gt;</c> with <c>outputClass</c>). An empty result is
    /// delivered as an empty list when <c>routeEmptyResultSet</c> is set.
    /// </summary>
    private async Task PollAsList(
        DbCommand cmd, DbConnection connection, DbTransaction? tx,
        string sql, Stopwatch stopwatch, CancellationToken ct)
    {
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);
        var mapper = new DictionaryRowMapper();
        var rows = poco?.CreateList() ?? new List<Dictionary<string, object?>>();

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (_options.MaxMessagesPerPoll >= 0 && rows.Count >= _options.MaxMessagesPerPoll)
                    break;
                rows.Add(poco != null ? poco.Map(reader) : mapper.Map(reader));
            }
        }

        var executionMs = stopwatch.ElapsedMilliseconds;

        if (rows.Count > 0 || _options.RouteEmptyResultSet)
        {
            var exchange = CreateExchange(rows, null, sql, rows.Count, executionMs);
            if (await RunListExchangeAsync(exchange, connection, tx, static () => ValueTask.CompletedTask, ct).ConfigureAwait(false))
                return;
        }
        else
        {
            await ProcessEmptyResult(sql, executionMs, ct).ConfigureAwait(false);
        }

        await ExecuteLifecycleSql(connection, tx, _options.OnBatchComplete, null, null, ct).ConfigureAwait(false);
        if (tx != null) await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streaming list delivery (<c>outputType=StreamList</c>, <c>pollDelivery=List</c>; Apache Camel <c>useIterator=false</c>
    /// with a result set iterator): the route gets the open stream as the body and reads the rows while it runs. The reader
    /// is closed as soon as the route is done, so <c>onSuccess</c> / <c>onFailure</c> and <c>onBatchComplete</c> then share
    /// the consumer's connection and transaction on every driver.
    /// </summary>
    private async Task PollStreamAsList(
        DbCommand cmd, DbConnection connection, DbTransaction? tx,
        string sql, Stopwatch stopwatch, CancellationToken ct)
    {
        var poco = SqlRowMapperFactory.Resolve(_options.OutputClass);
        var mapper = new DictionaryRowMapper();
        var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var executionMs = stopwatch.ElapsedMilliseconds;
        var readerClosed = false;

        async ValueTask CloseReaderAsync()
        {
            if (readerClosed)
                return;
            readerClosed = true;
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Func<DbDataReader, object> map = poco != null ? poco.Map : r => mapper.Map(r);
                var stream = ConsumerRowStream.Create(
                    poco?.ElementType ?? typeof(Dictionary<string, object?>), reader, map, _options.MaxMessagesPerPoll);
                var exchange = CreateExchange(stream, null, sql, -1, executionMs);
                if (await RunListExchangeAsync(exchange, connection, tx, CloseReaderAsync, ct).ConfigureAwait(false))
                    return;
            }
            else
            {
                await CloseReaderAsync().ConfigureAwait(false);
                if (_options.RouteEmptyResultSet)
                {
                    var empty = poco?.CreateList() ?? new List<Dictionary<string, object?>>();
                    var exchange = CreateExchange(empty, null, sql, 0, executionMs);
                    if (await RunListExchangeAsync(exchange, connection, tx, static () => ValueTask.CompletedTask, ct).ConfigureAwait(false))
                        return;
                }
                else
                {
                    await ProcessEmptyResult(sql, executionMs, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await CloseReaderAsync().ConfigureAwait(false);
        }

        await ExecuteLifecycleSql(connection, tx, _options.OnBatchComplete, null, null, ct).ConfigureAwait(false);
        if (tx != null) await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes the one exchange of a list delivery: the route, then <paramref name="afterRoute"/> (closing a streamed list's
    /// reader), then <c>onSuccess</c> — or, on any failure, <c>onFailure</c> and a rollback. Values of the statements come from
    /// headers and <c>param.*</c>.
    /// </summary>
    /// <returns>True when the consumer transaction was rolled back and the poll cycle ends.</returns>
    private async Task<bool> RunListExchangeAsync(
        Exchange exchange, DbConnection connection, DbTransaction? tx, Func<ValueTask> afterRoute, CancellationToken ct)
    {
        IncrementInflight();
        try
        {
            try
            {
                try
                {
                    await Processor.Process(exchange, ct).ConfigureAwait(false);
                }
                finally
                {
                    await afterRoute().ConfigureAwait(false);
                }

                await ExecuteOnSuccessAsync(connection, tx, null, exchange, ct).ConfigureAwait(false);
            }
            // A stop is not a failure of the row: a cancellation of this poll's token goes up, onFailure does not run.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                exchange.Exception = ex;
                await ExecuteLifecycleSql(connection, tx, _options.OnFailure, null, exchange, ct).ConfigureAwait(false);
                if (tx != null)
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return true;
                }
            }
        }
        finally
        {
            DecrementInflight();
            await exchange.DisposeAsync().ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>A connection to the primary database for the row statements of a streaming poll.</summary>
    private Task<DbConnection> OpenRowStatementConnectionAsync(CancellationToken ct) =>
        ResolveConnectionFactory().CreateConnectionAsync(readOnly: false, ct);

    /// <summary>Scalar: ExecuteScalarAsync → single exchange. Ideal for FOR XML / FOR JSON.</summary>
    private async Task PollScalar(
        DbCommand cmd, DbConnection connection, DbTransaction? tx,
        string sql, Stopwatch stopwatch, CancellationToken ct)
    {
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var executionMs = stopwatch.ElapsedMilliseconds;
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
                await ExecuteOnSuccessAsync(connection, tx, null, exchange, ct).ConfigureAwait(false);
            }
            // A stop is not a failure of the row: a cancellation of this poll's token goes up, onFailure does not run.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                exchange.Exception = ex;
                await ExecuteLifecycleSql(connection, tx, _options.OnFailure, null, exchange, ct).ConfigureAwait(false);
                if (tx != null) { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); return; }
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
        string sql, Stopwatch stopwatch, CancellationToken ct)
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

        var executionMs = stopwatch.ElapsedMilliseconds;

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
                await ExecuteOnSuccessAsync(connection, tx, row, exchange, ct).ConfigureAwait(false);
            }
            // A stop is not a failure of the row: a cancellation of this poll's token goes up, onFailure does not run.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                exchange.Exception = ex;
                await ExecuteLifecycleSql(connection, tx, _options.OnFailure, row, exchange, ct).ConfigureAwait(false);
                if (tx != null) { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); return; }
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
    /// Binds the <c>:#name</c> placeholders of the poll query. The query has no exchange, so its values come from
    /// <c>param.*</c> only (constants and context placeholders); a placeholder without one is an error, as everywhere else.
    /// </summary>
    private void BindPollParameters(DbCommand cmd)
    {
        var plan = SqlParameterPlan.Create(cmd.CommandText, _options.ExplicitParameters, _options.PlaceholderStyle, _options.BackslashEscapes);
        var values = new object?[plan.Names.Count];

        for (var index = 0; index < plan.Names.Count; index++)
        {
            var name = plan.Names[index];
            if (!plan.TryGetExplicit(name, out var explicitParameter))
            {
                throw new InvalidOperationException(SqlParameterBinder.MissingValueMessage(name, plan.SourceSql,
                    $"a poll query has no exchange to take values from, and there is no param.{name} option"));
            }

            // Without an exchange an expression cannot be evaluated, and its text must not be bound as the value.
            if (explicitParameter.IsExpression)
            {
                throw new InvalidOperationException(
                    $"SQL parameter ':#{name}' of the poll query is set to '{explicitParameter.Value}', a ${{...}} expression, and a " +
                    $"poll query has no exchange to evaluate it in. Give param.{name} a constant or a {{{{property}}}} placeholder. " +
                    $"Query: {plan.SourceSql}");
            }

            values[index] = explicitParameter.Resolve(null);
        }

        AddPlanParameters(cmd, plan, values);
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

    /// <summary>
    /// Runs <c>onSuccess</c> for a processed row. A failure is logged and rethrown: the row's failure handling
    /// (<c>onFailure</c>, rollback) still sees it, and without <c>onFailure</c> the unmarked row — processed again on the next
    /// poll — would otherwise pass unnoticed.
    /// </summary>
    private async Task ExecuteOnSuccessAsync(
        DbConnection connection, DbTransaction? tx, Dictionary<string, object?>? row, IExchange exchange, CancellationToken ct)
    {
        try
        {
            await ExecuteLifecycleSql(connection, tx, _options.OnSuccess, row, exchange, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogError(ex,
                "SQL consumer onSuccess failed; the row stays unmarked and will be polled again: dataSource={DataSource}",
                _options.DataSource);
            throw;
        }
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
        var plan = SqlParameterPlan.Create(cmd.CommandText, _options.ExplicitParameters, _options.PlaceholderStyle, _options.BackslashEscapes);
        var values = new object?[plan.Names.Count];

        for (var index = 0; index < plan.Names.Count; index++)
        {
            var name = plan.Names[index];
            var isRedbError = name.Equals("redbError", StringComparison.OrdinalIgnoreCase);

            // Special: :#redbError → exception message
            if (isRedbError && exchange?.Exception != null)
            {
                values[index] = exchange.Exception.Message;
            }
            // Priority 0: explicit param from .Param()
            else if (plan.TryGetExplicit(name, out var explicitParameter))
            {
                values[index] = explicitParameter.Resolve(exchange);
            }
            // Priority 1: row data
            else if (row != null && row.TryGetValue(name, out var rowVal))
            {
                values[index] = rowVal ?? DBNull.Value;
            }
            // Priority 2: exchange headers
            else if (exchange?.In.Headers.TryGetValue(name, out var headerVal) == true)
            {
                values[index] = headerVal ?? DBNull.Value;
            }
            // :#redbError always has a value: NULL when nothing failed
            else if (isRedbError)
            {
                values[index] = DBNull.Value;
            }
            // As in Apache Camel: a placeholder with no value is an error, not a silent NULL
            else
            {
                throw new InvalidOperationException(SqlParameterBinder.MissingValueMessage(name, plan.SourceSql,
                    $"no param.{name} option, no '{name}' column in the polled row and no '{name}' header"));
            }
        }

        AddPlanParameters(cmd, plan, values);
    }

    /// <summary>
    /// Writes the command text in the provider's placeholder style and adds one parameter per slot of the plan, each value
    /// normalised as the producer normalises its own (<see cref="SqlParameterBinder.Add"/>).
    /// </summary>
    private static void AddPlanParameters(DbCommand cmd, SqlParameterPlan plan, object?[] values)
    {
        cmd.CommandText = plan.Sql;
        for (var slot = 0; slot < plan.Slots.Count; slot++)
            SqlParameterBinder.Add(cmd, plan.ParameterName(slot), values[plan.Slots[slot]]);
    }

    private string ResolveQuery(IExchange? exchange)
    {
        // First try URI path (the SQL after "sql:" scheme)
        var rawSql = _endpoint.QueryOrProcedure;

        // If DynamicValue Query is set in options, use that instead
        if (_options.Query is { } dv)
        {
            // A poll has no exchange: an expression cannot be resolved, and falling back to the path would hide it.
            if (exchange == null && dv.IsDynamic)
            {
                throw new InvalidOperationException(
                    "The query option is a ${...} expression, and a poll has no exchange to resolve it against. Give the poll " +
                    "query as the URI path, a constant query option or a ref:name.");
            }

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
