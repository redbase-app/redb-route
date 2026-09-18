using System.Collections;
using System.Runtime.CompilerServices;

namespace redb.Route.Sql.Batch;

/// <summary>
/// Reads the items of a batch source one at a time, whatever the source is — a list, a lazy sequence, an asynchronous stream.
/// Nothing is read ahead of what the writer asks for, except the first item, which the processor reads before it opens a
/// connection so that an empty source costs none. A failure of the source itself (not of an item's statement) ends the batch:
/// it is reported with the number of items read before it.
/// </summary>
internal sealed class BatchItemReader : IAsyncDisposable
{
    private readonly IAsyncEnumerator<object?> _enumerator;
    private readonly CancellationToken _ct;
    private bool _pending;

    private BatchItemReader(IAsyncEnumerator<object?> enumerator, CancellationToken ct)
    {
        _enumerator = enumerator;
        _ct = ct;
    }

    /// <summary>Items read from the source so far.</summary>
    internal int ItemsRead { get; private set; }

    /// <summary>Zero-based index of <see cref="Current"/>.</summary>
    internal int Index => ItemsRead - 1;

    /// <summary>The item last read.</summary>
    internal object? Current => _enumerator.Current;

    /// <summary>A reader over an asynchronous source.</summary>
    internal static BatchItemReader Over(IAsyncEnumerable<object?> source, CancellationToken ct) =>
        new(source.GetAsyncEnumerator(ct), ct);

    /// <summary>A reader over a synchronous source.</summary>
    internal static BatchItemReader Over(IEnumerable source, CancellationToken ct) =>
        new(Enumerate(source, ct).GetAsyncEnumerator(ct), ct);

    /// <summary>Reads the first item and keeps it for the first <see cref="ReadAsync"/>; false for an empty source.</summary>
    /// <exception cref="BatchItemFailedException">The source failed.</exception>
    internal async ValueTask<bool> HasItemsAsync()
    {
        _pending = await MoveAsync().ConfigureAwait(false);
        return _pending;
    }

    /// <summary>Advances to the next item; false when the source is exhausted.</summary>
    /// <exception cref="BatchItemFailedException">The source failed.</exception>
    internal async ValueTask<bool> ReadAsync()
    {
        if (_pending)
        {
            _pending = false;
            return true;
        }

        return await MoveAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _enumerator.DisposeAsync();

    private async ValueTask<bool> MoveAsync()
    {
        bool moved;
        try
        {
            moved = await _enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !_ct.IsCancellationRequested)
        {
            // The source's own failure: the batch ends at the item that could not be read. Under a cancellation the source's
            // exception (a driver answering the stop in its own way) goes up as is, as the writers' do.
            throw new BatchItemFailedException(ItemsRead, ex);
        }

        if (moved)
            ItemsRead++;
        return moved;
    }

    private static async IAsyncEnumerable<object?> Enumerate(IEnumerable source, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in source)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
