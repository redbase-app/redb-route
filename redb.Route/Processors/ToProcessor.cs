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
    private readonly object _producerLock = new();
    private Task<IProducer>? _producerTask;
    private IProducer? _producer;
    private IEndpoint? _endpoint;

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
        Task<IProducer>? task;
        lock (_producerLock)
        {
            task = _producerTask;
            _producerTask = null;
        }
        if (task is null) return;

        IProducer producer;
        try { producer = await task.ConfigureAwait(false); }
        catch { return; } // start failed — nothing to stop

        await producer.Stop(ct).ConfigureAwait(false);
    }

    private Task<IProducer> GetOrCreateProducerAsync(CancellationToken ct)
    {
        // Single-flight: the first exchange creates + starts the producer; concurrent exchanges await
        // the SAME task and only receive the producer AFTER Start() (and its ConnectAsync) has fully
        // completed. Previously this returned _producer as soon as it was assigned — before Start()
        // finished — so under concurrent processing (e.g. RabbitMQ ConcurrentConsumers(N)) a second
        // thread could Process() a half-initialised producer (Redis _db still null → NRE).
        lock (_producerLock)
        {
            return _producerTask ??= CreateAndStartProducerAsync();
        }
    }

    private async Task<IProducer> CreateAndStartProducerAsync()
    {
        var endpoint = _context.GetEndpoint(_endpointUri);
        var producer = endpoint.CreateProducer();
        try
        {
            // Producer lifecycle is independent of any single exchange — do NOT tie startup to one
            // message's cancellation token (it would cancel startup for every concurrent caller).
            await producer.Start(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Allow a later exchange to retry a failed cold start instead of caching the failure.
            lock (_producerLock) { _producerTask = null; }
            throw;
        }

        _endpoint = endpoint;
        _producer = producer;
        (_context as RouteContext)?.TrackProducer(producer);
        return producer;
    }
}
