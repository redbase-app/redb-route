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
        // Pipeline EIP semantics:
        // - Intermediate steps that produce Out are merged (Out → In, Out := null) so the
        //   next step sees the result as its input.
        // - The final step's Out (if any) is left intact so InOut callers (direct-vm,
        //   replyTo, RPC) get the reply via exchange.Out.
        // - If the final step does NOT produce Out, we do not synthesize one from earlier
        //   steps: any subsequent step that modified In is the authoritative final result.
        for (var i = 0; i < _processors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (exchange.IsStopped) break;

            await _processors[i].Process(exchange, ct).ConfigureAwait(false);

            // Pipeline EIP: propagate Out body/headers → In so the next processor sees the result.
            if (exchange.HasOut && i < _processors.Count - 1)
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
}
