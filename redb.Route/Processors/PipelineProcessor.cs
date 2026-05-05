using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Executes a list of processors sequentially. The backbone of route processing.
/// Stops early if exchange.IsStopped or cancellation is requested.
/// </summary>
public class PipelineProcessor : IProcessor
{
    private readonly List<IProcessor> _processors = [];

    /// <summary>Gets the list of processors in this pipeline.</summary>
    public IReadOnlyList<IProcessor> Processors => _processors;

    /// <summary>Adds a processor to the end of the pipeline.</summary>
    /// <param name="processor">Processor to add.</param>
    public void Add(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processors.Add(processor);
    }

    /// <summary>Adds multiple processors to the pipeline.</summary>
    /// <param name="processors">Processors to add.</param>
    public void AddRange(IEnumerable<IProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        _processors.AddRange(processors);
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // Track the last Out message produced by any step so we can restore it
        // after the pipeline completes. This handles nested pipelines correctly:
        // intermediate steps merge Out→In and null Out to prevent stale carry-over,
        // but the final Out is preserved for InOut request/reply callers.
        IMessage? lastOut = null;

        for (var i = 0; i < _processors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (exchange.IsStopped) break;

            await _processors[i].Process(exchange, ct).ConfigureAwait(false);

            // Pipeline EIP: propagate Out body/headers → In so the next processor sees the result.
            if (exchange.HasOut)
            {
                lastOut = exchange.Out;

                if (i < _processors.Count - 1)
                {
                    var outMsg = exchange.Out!;
                    exchange.In.Body = outMsg.Body;
                    exchange.In.ContentType = outMsg.ContentType;
                    foreach (var (key, value) in outMsg.Headers)
                        exchange.In.Headers[key] = value;
                    exchange.Out = null; // prevent stale Out from re-merging on next step
                }
            }
        }

        // Restore the last Out so InOut callers (direct-vm, replyTo, etc.) get the reply.
        // If the last step itself set Out, it's already there; this handles the case
        // where the last step did NOT set Out but an earlier step did.
        if (lastOut != null && !exchange.HasOut)
            exchange.Out = lastOut;
    }
}
