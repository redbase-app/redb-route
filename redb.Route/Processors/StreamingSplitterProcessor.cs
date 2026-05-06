using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Splits a single exchange into multiple exchanges using an async enumerable (streaming).
/// Each part is processed one at a time without buffering the entire collection.
/// No parallel mode or aggregation — streaming and buffering are opposing strategies.
/// </summary>
internal sealed class StreamingSplitterProcessor : IProcessor
{
    private readonly Func<IExchange, IAsyncEnumerable<object?>> _splitter;
    private readonly IProcessor _target;
    private readonly bool _stopOnException;

    /// <summary>Creates a streaming splitter processor.</summary>
    /// <param name="splitter">Function that produces an async enumerable of body parts.</param>
    /// <param name="target">Processor to handle each split exchange.</param>
    /// <param name="stopOnException">Whether to stop on the first exception.</param>
    internal StreamingSplitterProcessor(
        Func<IExchange, IAsyncEnumerable<object?>> splitter,
        IProcessor target,
        bool stopOnException = true)
    {
        ArgumentNullException.ThrowIfNull(splitter);
        ArgumentNullException.ThrowIfNull(target);
        _splitter = splitter;
        _target = target;
        _stopOnException = stopOnException;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var index = 0;
        Exception? firstException = null;

        await foreach (var part in _splitter(exchange).WithCancellation(ct).ConfigureAwait(false))
        {
            if (exchange.IsStopped) break;

            var splitMessage = new Message(part);

            // Copy headers from original
            foreach (var header in exchange.In.Headers)
                splitMessage.Headers[header.Key] = header.Value;

            // Set split metadata headers (Camel-compatible)
            splitMessage.Headers["CamelSplitIndex"] = index;

            var splitExchange = exchange.CreateLinkedChild(splitMessage);

            try
            {
                await _target.Process(splitExchange, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
                if (_stopOnException)
                {
                    exchange.Exception = ex;
                    break;
                }
            }
            finally
            {
                if (splitExchange is IAsyncDisposable d)
                    await d.DisposeAsync().ConfigureAwait(false);
            }

            index++;
        }

        exchange.Properties["CamelSplitSize"] = index;
        ProcessorMetrics.SplitterParts.Add(index);

        if (firstException != null && _stopOnException)
            throw firstException;
    }
}
