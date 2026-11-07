using System.Runtime.CompilerServices;
using System.Threading.Channels;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Observability;

namespace redb.Route.Llm;

/// <summary>
/// The body of <c>stream=body</c>: an agent run made when the body is read, after the route, the way a streamed SQL
/// result is read. The visible text of every model call is yielded as the model writes it; thinking stays out. The run
/// reads its exchange's resources (the conversation store, the redb scope), so the body is bound to the exchange
/// (<see cref="IExchangeBoundBody"/>): it is released with it through <see cref="ExchangeResources"/>, a read after the
/// exchange ended fails, and it can be read once — one read is one run, tools included.
/// </summary>
internal sealed class LazyAgentBody : IAsyncEnumerable<string>, IExchangeBoundBody
{
    private readonly Func<Func<AgentDeltaContext, CancellationToken, Task>, CancellationToken, Task> _run;
    private readonly CancellationTokenSource _released = new();
    private Task? _running;
    private int _read;

    /// <param name="run">
    /// The agent run: given the receiver of the pieces, it runs the engine and writes the summary. Its failure reaches
    /// the reader.
    /// </param>
    internal LazyAgentBody(Func<Func<AgentDeltaContext, CancellationToken, Task>, CancellationToken, Task> run) => _run = run;

    /// <summary>The handle the exchange releases when it ends: it stops a run in progress and closes the body.</summary>
    internal IAsyncDisposable Release => new ReleaseHandle(this);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The body was already read, or its exchange has ended.</exception>
    public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _read, 1) != 0)
            throw new InvalidOperationException(
                "A streamed agent answer (stream=body) can be read once: one read is one agent run, with its tools.");
        if (_released.IsCancellationRequested)
            throw new InvalidOperationException(
                "This streamed agent answer (stream=body) was released when its exchange ended; it can no longer be read. "
                + "Read the body before the exchange ends: ProducerTemplate.RequestAsync, then dispose the exchange.");

        return ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    private async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _released.Token);
        var pieces = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var running = RunAsync(pieces.Writer, stop.Token);
        _running = running;
        try
        {
            await foreach (var piece in pieces.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                yield return piece;
        }
        finally
        {
            // A reader that stops early ends the run, and the run is awaited: nothing of it outlives the read.
            if (!running.IsCompleted) await stop.CancelAsync().ConfigureAwait(false);
            await running.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ChannelWriter<string> pieces, CancellationToken ct)
    {
        try
        {
            await _run((delta, token) => delta.Kind == AgentDeltaKind.Text
                ? pieces.WriteAsync(delta.Text, token).AsTask()
                : Task.CompletedTask, ct).ConfigureAwait(false);
            pieces.TryComplete();
        }
        catch (Exception ex)
        {
            // Handed to the reader, who is the one waiting for the answer: reading the channel rethrows it.
            pieces.TryComplete(ex);
        }
    }

    private async ValueTask ReleaseAsync()
    {
        if (_released.IsCancellationRequested) return;
        await _released.CancelAsync().ConfigureAwait(false);

        // The exchange releases its DI scopes right after its resources: a run still reading them must be over first.
        if (_running is { } running) await running.ConfigureAwait(false);
    }

    private sealed class ReleaseHandle(LazyAgentBody body) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => body.ReleaseAsync();
    }
}
