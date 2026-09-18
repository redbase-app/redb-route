namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>
/// An asynchronous batch source that counts what the connector read from it, can fail after a given number of items, and
/// records whether its enumerator was disposed.
/// </summary>
internal sealed class CountingAsyncSource<T> : IAsyncEnumerable<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly int? _failAfter;

    public CountingAsyncSource(IReadOnlyList<T> items, int? failAfter = null)
    {
        _items = items;
        _failAfter = failAfter;
    }

    /// <summary>Items handed out so far.</summary>
    public int ItemsRead { get; private set; }

    /// <summary>The enumerator was disposed.</summary>
    public bool Disposed { get; private set; }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(this, cancellationToken);

    private sealed class Enumerator(CountingAsyncSource<T> source, CancellationToken ct) : IAsyncEnumerator<T>
    {
        private int _index = -1;

        public T Current => source._items[_index];

        public ValueTask<bool> MoveNextAsync()
        {
            ct.ThrowIfCancellationRequested();
            if (source._failAfter is { } failAfter && _index + 1 == failAfter)
                throw new FakeSourceException($"fake: the source failed after {failAfter} items");
            if (_index + 1 >= source._items.Count)
                return ValueTask.FromResult(false);

            _index++;
            source.ItemsRead++;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            source.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A failure of the batch source itself, not of an item's statement.</summary>
internal sealed class FakeSourceException(string message) : Exception(message);
