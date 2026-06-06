using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Sends the exchange to a target endpoint via its producer.
/// Resolves the endpoint from IRouteContext by URI, creates a producer, and delegates Process.
/// The producer is started on first use and can be stopped via <see cref="StopProducerAsync"/>.
/// </summary>
public class ToProcessor : IProcessor
{
    private readonly string _endpointUri;
    private readonly IRouteContext _context;
    private IProducer? _producer;
    private IEndpoint? _endpoint;
    private bool _producerStarted;

    /// <summary>Gets the target endpoint URI.</summary>
    public string EndpointUri => _endpointUri;

    /// <summary>Creates a ToProcessor that sends exchanges to the given endpoint URI.</summary>
    /// <param name="endpointUri">Target endpoint URI (e.g., "kafka://orders").</param>
    /// <param name="context">Route context used to resolve endpoints.</param>
    public ToProcessor(string endpointUri, IRouteContext context)
    {
        _endpointUri = endpointUri ?? throw new ArgumentNullException(nameof(endpointUri));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var producer = await GetOrCreateProducerAsync(ct).ConfigureAwait(false);
        var stats = _endpoint as IEndpointStatistics;
        var sw = stats is not null ? Stopwatch.StartNew() : null;
        try
        {
            await producer.Process(exchange, ct).ConfigureAwait(false);
            stats?.RecordMessageOut();
        }
        catch (Exception ex)
        {
            stats?.RecordError(ex);
            throw;
        }
        finally
        {
            if (sw is not null)
            {
                sw.Stop();
                stats!.RecordProcessingTime(sw.Elapsed);
            }
        }
    }

    /// <summary>Stops the producer if one was created and started.</summary>
    public async Task StopProducerAsync(CancellationToken ct = default)
    {
        if (_producer != null && _producerStarted)
        {
            await _producer.Stop(ct).ConfigureAwait(false);
            _producerStarted = false;
        }
    }

    private async Task<IProducer> GetOrCreateProducerAsync(CancellationToken ct)
    {
        if (_producer != null) return _producer;

        _endpoint = _context.GetEndpoint(_endpointUri);
        _producer = _endpoint.CreateProducer();
        await _producer.Start(ct).ConfigureAwait(false);
        _producerStarted = true;
        (_context as RouteContext)?.TrackProducer(_producer);
        return _producer;
    }
}
