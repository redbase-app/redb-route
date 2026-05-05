using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Components;

/// <summary>
/// SEDA (Staged Event-Driven Architecture) component. Scheme: "seda".
/// Provides asynchronous in-memory queuing between route segments.
/// Producer enqueues the exchange and returns immediately; a background consumer
/// loop dequeues and processes exchanges asynchronously.
/// URI: seda://name?concurrentConsumers=1&amp;size=0
/// </summary>
public class SedaComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, SedaEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string Scheme => "seda";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new SedaEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        return _endpoints.GetOrAdd(uri.BaseKey, _ => new SedaEndpoint(uri, this, options));
    }
}

/// <summary>
/// Options for SEDA endpoints.
/// </summary>
public class SedaEndpointOptions : EndpointOptions
{
    /// <summary>Number of concurrent consumer workers (default: 1).</summary>
    public int ConcurrentConsumers { get; set; } = 1;

    /// <summary>Maximum queue size. 0 = unbounded (default: 0).</summary>
    public int Size { get; set; }

    /// <summary>Timeout in milliseconds for enqueue when queue is bounded. 0 = wait forever (default: 30000).</summary>
    public int Timeout { get; set; } = 30000;

    /// <inheritdoc />
    public override void Validate()
    {
        if (ConcurrentConsumers < 1)
            throw new ArgumentOutOfRangeException(nameof(ConcurrentConsumers), ConcurrentConsumers,
                "ConcurrentConsumers must be at least 1.");
        if (Size < 0)
            throw new ArgumentOutOfRangeException(nameof(Size), Size, "Size cannot be negative.");
    }
}

/// <summary>
/// SEDA endpoint. Uses <see cref="Channel{T}"/> as the in-memory async queue.
/// </summary>
public class SedaEndpoint : EndpointBase<SedaEndpointOptions>
{
    internal readonly Channel<IExchange> Queue;

    /// <summary>Creates a SEDA endpoint.</summary>
    public SedaEndpoint(EndpointUri uri, SedaComponent component, SedaEndpointOptions options)
        : base(uri, component, options)
    {
        Queue = options.Size > 0
            ? Channel.CreateBounded<IExchange>(new BoundedChannelOptions(options.Size)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = options.ConcurrentConsumers == 1,
                SingleWriter = false
            })
            : Channel.CreateUnbounded<IExchange>(new UnboundedChannelOptions
            {
                SingleReader = options.ConcurrentConsumers == 1,
                SingleWriter = false
            });
    }

    /// <summary>Gets the current approximate queue size.</summary>
    public int CurrentQueueSize => Queue.Reader.CanCount ? Queue.Reader.Count : -1;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new SedaProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new SedaConsumer(this, processor, Options);
    }
}

/// <summary>
/// SEDA producer. Enqueues the exchange into the channel and returns immediately.
/// The exchange is processed asynchronously by the consumer.
/// </summary>
public class SedaProducer : IProducer
{
    private readonly SedaEndpoint _endpoint;
    private ILogger? _logger;

    /// <summary>Creates a SEDA producer.</summary>
    public SedaProducer(SedaEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // Clone to ensure thread-safety: the pipeline thread and the SEDA worker
        // must not share a mutable exchange. Clone also creates a new DI scope.
        var copy = exchange.Clone();
        await _endpoint.Queue.Writer.WriteAsync(copy, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _logger?.LogInformation("SEDA producer started: {Name}", _endpoint.Uri.NormalizedKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _logger?.LogInformation("SEDA producer stopped: {Name}", _endpoint.Uri.NormalizedKey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// SEDA consumer. Runs N concurrent workers that dequeue and process exchanges.
/// </summary>
public class SedaConsumer : DrainableConsumer
{
    private readonly SedaEndpoint _endpoint;
    private readonly SedaEndpointOptions _options;
    private Task[]? _workers;
    private long _processedCount;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => _endpoint.Uri.NormalizedKey;

    /// <summary>Total number of exchanges processed across all workers.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>Creates a SEDA consumer.</summary>
    public SedaConsumer(SedaEndpoint endpoint, IProcessor processor, SedaEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        _workers = new Task[_options.ConcurrentConsumers];
        for (var i = 0; i < _options.ConcurrentConsumers; i++)
            _workers[i] = WorkerLoop(pollCt, processingCt);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task OnStopAccepting()
    {
        _endpoint.Queue.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task AwaitRunTask()
    {
        if (_workers != null)
        {
            try { await Task.WhenAll(_workers).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task WorkerLoop(CancellationToken pollCt, CancellationToken processingCt)
    {
        try
        {
            await foreach (var exchange in _endpoint.Queue.Reader.ReadAllAsync(pollCt).ConfigureAwait(false))
            {
                await ProcessWithTracking(exchange, processingCt).ConfigureAwait(false);
                Interlocked.Increment(ref _processedCount);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }
}
