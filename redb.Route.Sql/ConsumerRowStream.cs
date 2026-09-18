using System.Data.Common;
using System.Runtime.CompilerServices;

namespace redb.Route.Sql;

/// <summary>Creates the body of a streaming poll delivered as one list, for a row type known only at run time.</summary>
internal static class ConsumerRowStream
{
    /// <summary>A stream of <paramref name="elementType"/> rows over a reader positioned on its first row.</summary>
    internal static object Create(Type elementType, DbDataReader reader, Func<DbDataReader, object> map, int maxRows) =>
        Activator.CreateInstance(typeof(ConsumerRowStream<>).MakeGenericType(elementType), reader, map, maxRows)!;
}

/// <summary>
/// The body of a poll with <c>outputType=StreamList</c> and <c>pollDelivery=List</c>: the rows of the consumer's open reader,
/// which is positioned on the first row. The consumer owns the reader and closes it as soon as the route is done, so the rows
/// are read once, while the route runs; at most <c>maxMessagesPerPoll</c> of them.
/// </summary>
internal sealed class ConsumerRowStream<T>(DbDataReader reader, Func<DbDataReader, object> map, int maxRows) : IAsyncEnumerable<T>
{
    private int _enumerated;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The rows were already read, or the route has finished.</exception>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
            throw new InvalidOperationException(
                "The rows of a streaming poll (outputType=StreamList, pollDelivery=List) can be read once: they come from one open reader.");
        if (reader.IsClosed)
            throw new InvalidOperationException(
                "The rows of a streaming poll (outputType=StreamList, pollDelivery=List) are read while the route runs; the reader is closed.");

        return ReadRows(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    private async IAsyncEnumerable<T> ReadRows([EnumeratorCancellation] CancellationToken ct)
    {
        var count = 0;
        do
        {
            if (maxRows >= 0 && count >= maxRows)
                yield break;
            yield return (T)map(reader);
            count++;
        }
        while (await reader.ReadAsync(ct).ConfigureAwait(false));
    }
}
