using System.Data.Common;
using System.Runtime.CompilerServices;
using redb.Route.Abstractions;

namespace redb.Route.Sql;

/// <summary>Creates the body of <c>outputType=StreamList</c> for a row type known only at run time.</summary>
internal static class StreamedQueryResult
{
    /// <summary>
    /// A streamed result whose rows are <paramref name="elementType"/>, mapped by <paramref name="map"/>: the body to hand
    /// the route, and the handle that releases it, to register with the exchange (<see cref="ExchangeResources"/>).
    /// </summary>
    internal static (object Body, IAsyncDisposable Release) Create(
        Type elementType, DbDataReader reader, Func<DbDataReader, object> map,
        DbCommand command, DbConnection connection, DbTransaction? transaction)
    {
        var body = (IStreamedQueryResult)Activator.CreateInstance(
            typeof(StreamedQueryResult<>).MakeGenericType(elementType),
            reader, map, command, connection, transaction)!;
        return (body, new StreamedQueryResultRelease(body));
    }
}

/// <summary>What the exchange releases when it ends: the reader, command, transaction and connection behind a streamed body.</summary>
internal interface IStreamedQueryResult
{
    /// <summary>Releases everything the stream holds; the transaction is rolled back unless the rows were read to the end.</summary>
    ValueTask ReleaseAsync();
}

/// <summary>
/// The handle registered with the exchange. The body itself is not disposable on purpose: copies of the exchange (Enrich,
/// WireTap, RecipientList, Threads) share the body and are disposed when their own work is done, and a disposable body would
/// then be closed under the original. The release belongs to the exchange that registered it, or to the one Enrich hands it
/// over to.
/// </summary>
internal sealed class StreamedQueryResultRelease(IStreamedQueryResult result) : IAsyncDisposable
{
    /// <inheritdoc />
    public ValueTask DisposeAsync() => result.ReleaseAsync();
}

/// <summary>
/// The body of <c>outputType=StreamList</c>: rows read lazily from an open reader. It owns the reader, its command, the
/// endpoint's local transaction (if any) and the connection, and releases them when the rows have been read to the end
/// (committing that transaction), when the reading stops early or fails, or — through the handle registered with the
/// exchange — when the exchange ends without anyone reading. The rows can be read once.
/// </summary>
internal sealed class StreamedQueryResult<T> : IAsyncEnumerable<T>, IStreamedQueryResult
{
    private readonly DbDataReader _reader;
    private readonly Func<DbDataReader, object> _map;
    private readonly DbCommand _command;
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    private int _enumerated;
    private int _released;

    public StreamedQueryResult(
        DbDataReader reader, Func<DbDataReader, object> map, DbCommand command, DbConnection connection, DbTransaction? transaction)
    {
        _reader = reader;
        _map = map;
        _command = command;
        _connection = connection;
        _transaction = transaction;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The rows were already read, or the exchange has ended.</exception>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // Read-once first: rows read to the end release the result too, and that is not "released with the exchange".
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
            throw new InvalidOperationException(
                "A streamed SQL result (outputType=StreamList) can be read once: its rows come from one open reader.");
        if (Volatile.Read(ref _released) != 0)
            throw new InvalidOperationException(
                "This streamed SQL result (outputType=StreamList) was released when its exchange ended; it can no longer be read.");

        return ReadRows(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync() => ReleaseAsync(commit: false);

    private async IAsyncEnumerable<T> ReadRows([EnumeratorCancellation] CancellationToken ct)
    {
        var readToEnd = false;
        try
        {
            while (await _reader.ReadAsync(ct).ConfigureAwait(false))
                yield return (T)_map(_reader);
            readToEnd = true;
        }
        finally
        {
            await ReleaseAsync(commit: readToEnd).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseAsync(bool commit)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;

        // Each resource is released even when the one before it fails; the first failure propagates.
        try
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (commit && _transaction is not null)
                    await _transaction.CommitAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    // Disposing an uncommitted transaction rolls it back.
                    if (_transaction is not null)
                        await _transaction.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await _command.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await _connection.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
