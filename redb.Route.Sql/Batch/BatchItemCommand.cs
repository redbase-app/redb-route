using System.Data.Common;

namespace redb.Route.Sql.Batch;

/// <summary>
/// The statement of a batch as one command: created and given its parameters once, then run for every item with only the
/// parameter values replaced. <see cref="DbCommand.Prepare"/> is not called: SQL Server refuses it for parameters without an
/// explicit size.
/// </summary>
internal sealed class BatchItemCommand : IAsyncDisposable
{
    private readonly BatchWriteRequest _request;
    private readonly DbCommand _command;
    private readonly object[] _values;

    internal BatchItemCommand(BatchWriteRequest request, SqlEndpointOptions options)
    {
        _request = request;
        _values = new object[request.Plan.Names.Count];

        var command = request.Connection.CreateCommand();
        var ready = false;
        try
        {
            command.CommandText = request.Plan.Sql;
            command.CommandTimeout = options.CommandTimeout;
            if (request.Transaction is not null)
                command.Transaction = request.Transaction;

            for (var slot = 0; slot < request.Plan.Slots.Count; slot++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = request.Plan.ParameterName(slot);
                parameter.Value = DBNull.Value;
                command.Parameters.Add(parameter);
            }

            ready = true;
        }
        finally
        {
            if (!ready)
                command.Dispose();
        }

        _command = command;
    }

    /// <summary>
    /// Binds the values of <paramref name="item"/> (at <paramref name="index"/>) and executes the statement; returns the rows
    /// affected. When the batch collects returned rows, the statement runs as a reader and its rows go to the collector; the
    /// reader is closed before this returns or throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">A placeholder has no value for this item.</exception>
    internal async Task<int> ExecuteAsync(object? item, int index, CancellationToken ct)
    {
        BatchItemBinder.ResolveValues(_request.Plan, _request.Exchange, item, index, _values);
        for (var slot = 0; slot < _request.Plan.Slots.Count; slot++)
            _command.Parameters[slot].Value = _values[_request.Plan.Slots[slot]];

        if (_request.Keys is not { } keys)
            return await _command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var reader = await _command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await keys.ReadAllAsync(reader, ct).ConfigureAwait(false);
        await reader.CloseAsync().ConfigureAwait(false);
        return Math.Max(reader.RecordsAffected, 0);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _command.DisposeAsync();
}
