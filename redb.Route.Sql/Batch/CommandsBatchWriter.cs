using redb.Route.Sql.Mapping;

namespace redb.Route.Sql.Batch;

/// <summary>
/// Break-on-first-error batch on a connection that cannot create a <c>DbBatch</c>: one command, its parameters reused, runs
/// the items one after another as they are read. The first failing item ends the batch — nothing is read or sent after it,
/// and the processor rolls the whole transaction back.
/// </summary>
internal sealed class CommandsBatchWriter(SqlEndpointOptions options) : IBatchWriter
{
    /// <inheritdoc />
    public string Strategy => "Commands";

    /// <inheritdoc />
    public async Task<BatchOutcome> WriteAsync(BatchWriteRequest request, CancellationToken ct)
    {
        var outcome = new BatchOutcome();
        await using var command = new BatchItemCommand(request, options);

        while (await request.Items.ReadAsync().ConfigureAwait(false))
        {
            var index = request.Items.Index;
            int affected;
            try
            {
                affected = await command.ExecuteAsync(request.Items.Current, index, ct).ConfigureAwait(false);
            }
            // A returned row that cannot be mapped is the endpoint's configuration, not this item's failure: it goes up as is.
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested && ex is not SqlRowMappingException)
            {
                throw new BatchItemFailedException(index, ex);
            }

            outcome.RowsAffected += affected;
            outcome.ItemsWritten++;
        }

        return outcome;
    }
}
