using System.Data.Common;
using redb.Route.Sql.Mapping;

namespace redb.Route.Sql.Batch;

/// <summary>
/// Break-on-first-error batch over <see cref="DbBatch"/>: the items go in chunks of <see cref="SqlEndpointOptions.BatchSize"/>
/// statements, one round trip per chunk, all in the batch's transaction. A chunk is read, then sent, before the next one is
/// read. A failed chunk ends the batch; the failed item is the command the provider names in
/// <see cref="DbException.BatchCommand"/>, or — when the provider does not name it — the chunk's first item, with the chunk's
/// range in <see cref="Exception.Data"/> under <see cref="FailedChunkKey"/>. When the batch collects returned rows, a chunk
/// runs as a reader: one result set per command, in item order.
/// </summary>
internal sealed class DbBatchWriter(SqlEndpointOptions options) : IBatchWriter
{
    /// <summary>Strategy name, as reported in <see cref="SqlHeaders.BatchStrategy"/>.</summary>
    internal const string Name = "DbBatch";

    /// <summary><see cref="Exception.Data"/> key of the failed chunk's item range when the failed command is unknown.</summary>
    internal const string FailedChunkKey = "redbSql.batchFailedChunk";

    /// <inheritdoc />
    public string Strategy => Name;

    /// <inheritdoc />
    public async Task<BatchOutcome> WriteAsync(BatchWriteRequest request, CancellationToken ct)
    {
        var outcome = new BatchOutcome { ChunkCount = 0 };
        var plan = request.Plan;
        var values = new object[plan.Names.Count];
        DbCommand? parameterFactory = null;

        try
        {
            while (await request.Items.ReadAsync().ConfigureAwait(false))
            {
                var start = request.Items.Index;
                var count = 0;

                await using var batch = request.Connection.CreateBatch();
                batch.Transaction = request.Transaction;
                batch.Timeout = options.CommandTimeout;

                do
                {
                    var index = request.Items.Index;
                    try
                    {
                        BatchItemBinder.ResolveValues(plan, request.Exchange, request.Items.Current, index, values);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        throw new BatchItemFailedException(index, ex);
                    }

                    var command = batch.CreateBatchCommand();
                    command.CommandText = plan.Sql;
                    for (var slot = 0; slot < plan.Slots.Count; slot++)
                    {
                        // .NET 8 lets a batch command create its own parameters; older providers need a command for that.
                        var parameter = command.CanCreateParameter
                            ? command.CreateParameter()
                            : (parameterFactory ??= request.Connection.CreateCommand()).CreateParameter();
                        parameter.ParameterName = plan.ParameterName(slot);
                        parameter.Value = values[plan.Slots[slot]];
                        command.Parameters.Add(parameter);
                    }
                    batch.BatchCommands.Add(command);
                    count++;
                }
                // A full chunk is sent before anything more is read from the source.
                while (count < options.BatchSize && await request.Items.ReadAsync().ConfigureAwait(false));

                int affected;
                try
                {
                    affected = request.Keys is { } keys
                        ? await ExecuteForRowsAsync(batch, keys, ct).ConfigureAwait(false)
                        : await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                // A returned row that cannot be mapped is the endpoint's configuration, not a chunk's failure: it goes up as is.
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested && ex is not SqlRowMappingException)
                {
                    throw ChunkFailure(ex, batch, start, start + count);
                }

                outcome.RowsAffected += affected;
                outcome.ItemsWritten += count;
                outcome.ChunkCount++;
            }
        }
        finally
        {
            if (parameterFactory is not null)
                await parameterFactory.DisposeAsync().ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// Sends the chunk as a reader, collects every command's result set in order, and returns the rows affected — the sum of
    /// the commands' own counts, read once the reader is closed.
    /// </summary>
    private static async Task<int> ExecuteForRowsAsync(DbBatch batch, BatchRowCollector keys, CancellationToken ct)
    {
        await using (var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false))
            await keys.ReadAllAsync(reader, ct).ConfigureAwait(false);

        var affected = 0;
        foreach (var command in batch.BatchCommands)
            affected += Math.Max(command.RecordsAffected, 0);
        return affected;
    }

    private static BatchItemFailedException ChunkFailure(Exception ex, DbBatch batch, int start, int end)
    {
        var failedCommand = (ex as DbException)?.BatchCommand;
        var position = failedCommand is null ? -1 : batch.BatchCommands.IndexOf(failedCommand);
        if (position >= 0)
            return new BatchItemFailedException(start + position, ex);

        ex.Data[FailedChunkKey] = $"{start}..{end - 1}";
        return new BatchItemFailedException(start, ex);
    }
}
