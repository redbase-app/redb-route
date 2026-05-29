using System.Data.Common;
using System.Diagnostics;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Sql.Connection;
using redb.Route.Sql.Mapping;
using redb.Route.Telemetry;

namespace redb.Route.Sql;

/// <summary>
/// Producer for SQL Execute mode. Executes INSERT/UPDATE/DELETE/SELECT statements
/// with auto-bind parameters from Exchange headers and body.
/// Supports batch mode and all <see cref="SqlOutputType"/> mappings.
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

        // Batch mode: body is a list of items → execute SQL for each item
        if (_options.BatchSize > 0 && exchange.In.Body is System.Collections.IList items && items.Count > 0)
        {
            await ProcessBatch(exchange, items, ct).ConfigureAwait(false);
            return;
        }

        var factory = ResolveConnectionFactory();
        var sw = Stopwatch.StartNew();

        // Resolve Auto output type from the SQL text
        var outputType = _options.OutputType;
        if (outputType == SqlOutputType.Auto)
            outputType = ResolveAutoOutputType(_endpoint.QueryOrProcedure);

        var connection = await factory.CreateConnectionAsync(
            readOnly: outputType != SqlOutputType.None, ct).ConfigureAwait(false);

        // If ambient TransactionScope exists (route-level .Transacted()), the connection
        // auto-enlists — no local DbTransaction needed. Otherwise always wrap in a
        // local transaction for atomicity (like EF SaveChanges).
        var hasAmbientTx = Transaction.Current != null;
        DbTransaction? tx = null;
        if (!hasAmbientTx)
        {
            tx = _options.IsolationLevel.HasValue
                ? await connection.BeginTransactionAsync(_options.IsolationLevel.Value, ct).ConfigureAwait(false)
                : await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var sql = ResolveQuery(exchange);
            var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.CommandTimeout;
            if (tx != null) cmd.Transaction = tx;

            BindParameters(cmd, exchange);

            sw.Stop();
            var executionMs = sw.ElapsedMilliseconds;

            switch (outputType)
            {
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
                    // StreamList takes ownership of connection, cmd, and tx —
                    // they will be disposed when the stream is fully consumed.
                    await ExecuteStreamList(cmd, exchange, connection, tx, ct).ConfigureAwait(false);
                    SetCommonHeaders(exchange, sql, executionMs);
                    return;

                case SqlOutputType.None:
                default:
                    await ExecuteNonQuery(cmd, exchange, ct).ConfigureAwait(false);
                    break;
            }

            SetCommonHeaders(exchange, sql, executionMs);

            if (tx != null)
            {
                exchange.In.Headers[SqlHeaders.TransactionId] = tx.GetHashCode().ToString();
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            await cmd.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SQL execution failed: dataSource={DataSource}, outputType={OutputType}",
                _options.DataSource, _options.OutputType);
            if (tx != null)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
                catch (Exception rbEx) { _logger?.LogError(rbEx, "SQL transaction rollback failed"); }
            }
            throw;
        }
        finally
        {
            if (_options.OutputType != SqlOutputType.StreamList)
            {
                if (tx != null)
                    await tx.DisposeAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessBatch(IExchange exchange, System.Collections.IList items, CancellationToken ct)
    {
        var factory = ResolveConnectionFactory();
        var sw = Stopwatch.StartNew();
        var totalAffected = 0;
        var processedCount = 0;
        var errors = new List<Exception>();

        await using var connection = await factory.CreateConnectionAsync(
            readOnly: false, ct).ConfigureAwait(false);

        var hasAmbientTx = Transaction.Current != null;
        DbTransaction? tx = null;
        if (!hasAmbientTx)
        {
            tx = _options.IsolationLevel.HasValue
                ? await connection.BeginTransactionAsync(_options.IsolationLevel.Value, ct).ConfigureAwait(false)
                : await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var sql = ResolveQuery(exchange);

            foreach (var item in items)
            {
                try
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = _options.CommandTimeout;
                    if (tx != null) cmd.Transaction = tx;

                    // Create a virtual exchange from the batch item for parameter binding
                    var itemExchange = CreateBatchItemExchange(item, exchange);
                    BindParameters(cmd, itemExchange);

                    var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    totalAffected += affected;
                    processedCount++;
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    if (_options.BreakBatchOnError)
                        break;
                }
            }

            sw.Stop();
            exchange.In.Headers[SqlHeaders.UpdateCount] = totalAffected;
            exchange.In.Headers[SqlHeaders.RowCount] = processedCount;
            SetCommonHeaders(exchange, sql, sw.ElapsedMilliseconds);

            if (errors.Count > 0)
                exchange.In.Headers[SqlHeaders.Error] = $"{errors.Count} batch error(s): {errors[0].Message}";

            if (tx != null)
            {
                if (errors.Count > 0 && _options.BreakBatchOnError)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    exchange.In.Headers[SqlHeaders.TransactionId] = tx.GetHashCode().ToString();
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
            }

            if (errors.Count > 0 && _options.BreakBatchOnError)
                throw new AggregateException("Batch processing failed.", errors);
        }
        catch (AggregateException) { throw; } // re-throw batch error
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SQL batch execution failed: dataSource={DataSource}",
                _options.DataSource);
            if (tx != null)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
                catch (Exception rbEx) { _logger?.LogError(rbEx, "SQL batch transaction rollback failed"); }
            }
            throw;
        }
        finally
        {
            if (tx != null)
                await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IExchange CreateBatchItemExchange(object item, IExchange parent)
    {
        var exchange = new Exchange(new Message(item));

        // Copy parent headers as defaults
        foreach (var (key, value) in parent.In.Headers)
            exchange.In.Headers[key] = value;

        // If item is a dictionary, add its entries as headers for auto-bind
        if (item is Dictionary<string, object?> dict)
        {
            foreach (var (key, value) in dict)
                exchange.In.Headers[key] = value;
        }
        else if (item is IDictionary<string, object> dict2)
        {
            foreach (var (key, value) in dict2)
                exchange.In.Headers[key] = value;
        }

        return exchange;
    }

    private static async Task ExecuteSelectList(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var mapper = new DictionaryRowMapper();
        var rows = new List<Dictionary<string, object?>>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(mapper.Map(reader));

        exchange.In.Headers[SqlHeaders.RowCount] = rows.Count;
        SetResult(exchange, rows);
    }

    private static async Task ExecuteSelectOne(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var mapper = new DictionaryRowMapper();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = mapper.Map(reader);
            exchange.In.Headers[SqlHeaders.RowCount] = 1;
            SetResult(exchange, row);
        }
        else
        {
            exchange.In.Headers[SqlHeaders.RowCount] = 0;
            SetResult(exchange, null);
        }
    }

    private static async Task ExecuteScalar(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result == DBNull.Value) result = null;

        exchange.In.Headers[SqlHeaders.RowCount] = result != null ? 1 : 0;
        SetResult(exchange, result);
    }

    private static async Task ExecuteStreamList(DbCommand cmd, IExchange exchange, DbConnection connection, DbTransaction? tx, CancellationToken ct)
    {
        // StreamList differs from SelectList: rows are streamed lazily as IAsyncEnumerable.
        // The connection, command, and reader must stay alive until the stream is consumed.
        // We transfer ownership of these resources to the async enumerable.
        var mapper = new DictionaryRowMapper();
        var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var enumerable = StreamRows(reader, mapper, cmd, connection, tx, ct);
        exchange.In.Body = enumerable;
        exchange.In.Headers[SqlHeaders.RowCount] = -1; // unknown until fully iterated
    }

    private static async IAsyncEnumerable<Dictionary<string, object?>> StreamRows(
        DbDataReader reader,
        DictionaryRowMapper mapper,
        DbCommand cmd,
        DbConnection connection,
        DbTransaction? tx,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using (reader)
        await using (cmd)
        await using (connection)
        {
            try
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    yield return mapper.Map(reader);

                if (tx != null)
                    await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (tx != null)
                    await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteNonQuery(DbCommand cmd, IExchange exchange, CancellationToken ct)
    {
        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        exchange.In.Headers[SqlHeaders.UpdateCount] = affected;
    }

    private static void SetResult(IExchange exchange, object? value)
    {
        // Check if result should go to header or body
        exchange.In.Body = value;
    }

    private void SetCommonHeaders(IExchange exchange, string sql, long executionMs)
    {
        exchange.In.Headers[SqlHeaders.Query] = sql;
        exchange.In.Headers[SqlHeaders.ExecutionTime] = executionMs;

        if (_options.DataSource != null)
            exchange.In.Headers[SqlHeaders.DataSource] = _options.DataSource;

        exchange.In.Headers[SqlHeaders.OutputType] = _options.OutputType.ToString();
    }

    private void BindParameters(DbCommand cmd, IExchange exchange)
    {
        var paramNames = SqlParameterParser.ExtractParameterNames(cmd.CommandText);
        var explicitParams = _options.ExplicitParameters;

        foreach (var name in paramNames)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;

            // Priority 0: explicit param from .Param()
            if (explicitParams.TryGetValue(name, out var explicitVal))
            {
                param.Value = ResolveParamValue(explicitVal, exchange);
            }
            // Priority 1: exchange headers
            else if (exchange.In.Headers.TryGetValue(name, out var headerVal))
            {
                param.Value = headerVal ?? DBNull.Value;
            }
            // Priority 2: body as Dictionary
            else if (exchange.In.Body is Dictionary<string, object?> dict &&
                     dict.TryGetValue(name, out var bodyVal))
            {
                param.Value = bodyVal ?? DBNull.Value;
            }
            // Priority 3: body as IDictionary<string, object>
            else if (exchange.In.Body is IDictionary<string, object> dict2 &&
                     dict2.TryGetValue(name, out var bodyVal2))
            {
                param.Value = bodyVal2 ?? DBNull.Value;
            }
            else
            {
                param.Value = DBNull.Value;
            }

            cmd.Parameters.Add(param);
        }
    }

    internal static object ResolveParamValue(string value, IExchange? exchange)
    {
        if (value.Contains("${") && exchange != null)
        {
            return ExpressionResolver.ResolveTypedOrTemplate(value, exchange) ?? DBNull.Value;
        }
        return string.IsNullOrEmpty(value) ? DBNull.Value : (object)value;
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

    /// <summary>
    /// Resolves <see cref="SqlOutputType.Auto"/> by inspecting the SQL text.
    /// SELECT / WITH → SelectList, everything else → None.
    /// </summary>
    internal static SqlOutputType ResolveAutoOutputType(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return SqlOutputType.None;

        var trimmed = sql.AsSpan().TrimStart();
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            ? SqlOutputType.SelectList
            : SqlOutputType.None;
    }
}
