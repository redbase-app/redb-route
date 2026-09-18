using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Transactions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Sql.Batch;

/// <summary>
/// Runs one batch over a reader of its items. The first item is read before anything else: an empty source writes nothing
/// and opens no connection. Otherwise all items share one connection and — unless the route already runs in an ambient
/// transaction — one local transaction. Breaking on the first error (the default, as in Apache Camel) sends the items in
/// <c>DbBatch</c> chunks of <see cref="SqlEndpointOptions.BatchSize"/> when the connection can create one, and through one
/// reused command otherwise; it rolls the whole batch back and rethrows the provider's own exception, with the failed item's
/// index in <see cref="Exception.Data"/> and in a header. Continuing past errors undoes each failed item to a savepoint and
/// reports it; it is refused before any write where savepoints cannot exist, and it stops when the transaction is gone. A
/// failure of the source itself ends the batch in either mode, reported the same way with the number of items read. No
/// option picks the strategy: as in Camel, the driver decides. With an <c>outputType</c> that reads rows, the rows the
/// statements return are collected into <see cref="SqlHeaders.GeneratedKeys"/>; the batch tags the <c>sql.execute</c> span.
/// </summary>
internal sealed class SqlBatchProcessor(SqlEndpoint endpoint, SqlEndpointOptions options, ILogger? logger)
{
    /// <summary>
    /// Writes the items of <paramref name="items"/> with <paramref name="sql"/>, sets the batch headers and tags
    /// <paramref name="activity"/>. The caller owns the reader and disposes it.
    /// </summary>
    internal async Task ProcessAsync(IExchange exchange, BatchItemReader items, string sql, Activity? activity, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var keys = BatchRowCollector.Create(options, sql);
        ClearResultHeaders(exchange);

        bool hasItems;
        try
        {
            hasItems = await items.HasItemsAsync().ConfigureAwait(false);
        }
        catch (BatchItemFailedException failure)
        {
            ThrowItemFailure(exchange, activity, failure, strategy: "None", items.ItemsRead);
            throw;
        }

        if (!hasItems)
        {
            SetHeaders(exchange, activity, sql, "None", new BatchOutcome(), keys, stopwatch, transaction: null);
            return;
        }

        var ambient = Transaction.Current is not null;
        if (!options.BreakBatchOnError && ambient)
        {
            throw new InvalidOperationException(
                "breakBatchOnError=false undoes a failed item to a savepoint of a local transaction, but this batch runs inside an " +
                "ambient transaction (a transacted route), where the connector has no savepoints. Use breakBatchOnError=true: " +
                "the route transaction then rolls back on the first error.");
        }

        var plan = SqlParameterPlan.Create(sql, options.ExplicitParameters, options.PlaceholderStyle, options.BackslashEscapes);
        var factory = endpoint.ResolveConnectionFactory();
        await using var connection = await factory.CreateConnectionAsync(readOnly: false, ct).ConfigureAwait(false);
        var writer = ChooseWriter(connection);

        DbTransaction? transaction = null;
        var committed = false;
        try
        {
            if (!ambient)
            {
                transaction = options.IsolationLevel.HasValue
                    ? await connection.BeginTransactionAsync(options.IsolationLevel.Value, ct).ConfigureAwait(false)
                    : await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            }

            BatchOutcome outcome;
            try
            {
                outcome = await writer
                    .WriteAsync(new BatchWriteRequest(connection, transaction, plan, items, exchange, keys), ct)
                    .ConfigureAwait(false);
            }
            catch (BatchItemFailedException failure)
            {
                // The rollback runs in finally; the route sees the original exception with its original stack.
                ThrowItemFailure(exchange, activity, failure, writer.Strategy, items.ItemsRead);
                throw;
            }

            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            committed = true;

            SetHeaders(exchange, activity, sql, writer.Strategy, outcome, keys, stopwatch, transaction);
        }
        finally
        {
            if (transaction is not null)
            {
                if (!committed)
                    await BatchRollback.RunAsync(transaction, logger).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Continuing past errors needs savepoints, and a <c>DbBatch</c> cannot go on past a failed command; breaking on the first
    /// error uses a <c>DbBatch</c> wherever the connection can create one.
    /// </summary>
    private IBatchWriter ChooseWriter(DbConnection connection)
    {
        if (!options.BreakBatchOnError)
            return new SavepointBatchWriter(options, logger);

        return connection.CanCreateBatch ? new DbBatchWriter(options) : new CommandsBatchWriter(options);
    }

    /// <summary>Records the failed item's index on the exception, the exchange and the span, then rethrows the original exception.</summary>
    [DoesNotReturn]
    private void ThrowItemFailure(IExchange exchange, Activity? activity, BatchItemFailedException failure, string strategy, int itemsRead)
    {
        var original = failure.InnerException!;
        logger?.LogError(original, "SQL batch stopped at item {Index}: dataSource={DataSource}, strategy={Strategy}",
            failure.Index, options.DataSource, strategy);

        original.Data[SqlHeaders.BatchFailedIndex] = failure.Index;
        exchange.In.Headers[SqlHeaders.BatchFailedIndex] = failure.Index;
        // The strategy tells a handler how to read the index: for a DbBatch whose driver names no command it is the chunk's start.
        exchange.In.Headers[SqlHeaders.BatchStrategy] = strategy;
        TagSpan(activity, strategy, itemsRead, chunks: null, failedIndex: failure.Index);

        ExceptionDispatchInfo.Capture(original).Throw();
    }

    /// <summary>
    /// A batch describes only its own run: what an earlier attempt on the same exchange (a redelivery, an earlier <c>sql:</c>
    /// step) left behind is removed before this one writes its result.
    /// </summary>
    private static void ClearResultHeaders(IExchange exchange)
    {
        var headers = exchange.In.Headers;
        headers.Remove(SqlHeaders.BatchFailedIndex);
        headers.Remove(SqlHeaders.BatchErrors);
        headers.Remove(SqlHeaders.Error);
        headers.Remove(SqlHeaders.BatchStrategy);
        headers.Remove(SqlHeaders.BatchChunkCount);
        headers.Remove(SqlHeaders.GeneratedKeys);
        headers.Remove(SqlHeaders.GeneratedKeysRowCount);
    }

    private void SetHeaders(
        IExchange exchange, Activity? activity, string sql, string strategy, BatchOutcome outcome, BatchRowCollector? keys,
        Stopwatch stopwatch, DbTransaction? transaction)
    {
        var headers = exchange.In.Headers;
        headers[SqlHeaders.UpdateCount] = outcome.RowsAffected;
        headers[SqlHeaders.RowCount] = outcome.ItemsWritten;
        headers[SqlHeaders.BatchItemCount] = outcome.ItemsWritten;
        headers[SqlHeaders.BatchStrategy] = strategy;

        if (outcome.ChunkCount is { } chunks)
            headers[SqlHeaders.BatchChunkCount] = chunks;

        if (outcome.Errors.Count > 0)
        {
            headers[SqlHeaders.BatchErrors] = outcome.Errors.AsReadOnly();
            headers[SqlHeaders.Error] = $"{outcome.Errors.Count} batch error(s): {outcome.Errors[0].Message}";
        }

        if (keys is not null)
        {
            headers[SqlHeaders.GeneratedKeys] = keys.Rows;
            headers[SqlHeaders.GeneratedKeysRowCount] = keys.Count;
        }

        if (transaction is not null)
            headers[SqlHeaders.TransactionId] = transaction.GetHashCode().ToString();

        TagSpan(activity, strategy, outcome.ItemsWritten + outcome.Errors.Count, outcome.ChunkCount, failedIndex: null);
        SqlExchangeHeaders.SetCommon(exchange, options, sql, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Batch tags on the <c>sql.execute</c> span. <c>db.operation.batch.size</c> follows the OpenTelemetry database conventions,
    /// which count an operation as a batch only from two statements. No parameter value is ever tagged.
    /// </summary>
    private static void TagSpan(Activity? activity, string strategy, int itemCount, int? chunks, int? failedIndex)
    {
        if (activity is null)
            return;

        activity.SetTag("redb.sql.batch.strategy", strategy);
        if (itemCount >= 2)
            activity.SetTag("db.operation.batch.size", itemCount);
        if (chunks is { } roundTrips)
            activity.SetTag("redb.sql.batch.chunks", roundTrips);
        if (failedIndex is { } index)
            activity.SetTag("redb.sql.batch.failed_index", index);
    }
}
