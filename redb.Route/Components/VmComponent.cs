using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Components;

/// <summary>
/// Cross-context asynchronous component. Scheme: "vm".
/// Like SEDA but the channel is shared across all RouteContexts
/// via <see cref="SharedVmRegistry"/> (DI singleton).
/// Producer enqueues the exchange (with clone) and returns immediately;
/// a consumer in any context dequeues and processes asynchronously.
/// URI: vm://name?concurrentConsumers=1&amp;size=0
/// </summary>
public class VmComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, VmEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string Scheme => "vm";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new VmEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        return _endpoints.GetOrAdd(uri.BaseKey, _ => new VmEndpoint(uri, this, options));
    }

    /// <summary>Resolves the shared registry from DI. Returns null if not registered.</summary>
    internal SharedVmRegistry? GetRegistry()
    {
        return Context?.GetServiceProvider()?.GetService<SharedVmRegistry>();
    }
}

/// <summary>
/// Options for VM endpoints.
/// </summary>
public class VmEndpointOptions : EndpointOptions
{
    /// <summary>Number of concurrent consumer workers (default: 1).</summary>
    public int ConcurrentConsumers { get; set; } = 1;

    /// <summary>Maximum queue size. 0 = unbounded (default: 0).</summary>
    public int Size { get; set; }

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
/// VM endpoint. Uses a shared <see cref="Channel{T}"/> from <see cref="SharedVmRegistry"/>.
/// </summary>
public class VmEndpoint : EndpointBase<VmEndpointOptions>
{
    internal new VmComponent Component => (VmComponent)base.Component;

    /// <summary>The logical name used as key in the shared registry.</summary>
    internal string Name => Uri.BaseKey;

    private Channel<IExchange>? _channel;

    /// <summary>Gets the shared channel (lazy-resolved from registry).</summary>
    internal Channel<IExchange> Channel
    {
        get
        {
            if (_channel is not null) return _channel;
            var registry = Component.GetRegistry()
                ?? throw new InvalidOperationException(
                    "SharedVmRegistry is not available. Register it in DI to use vm endpoints.");
            _channel = registry.GetOrCreateChannel(Name, Options.Size);
            return _channel;
        }
    }

    /// <summary>Creates a VM endpoint.</summary>
    public VmEndpoint(EndpointUri uri, VmComponent component, VmEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new VmProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new VmConsumer(this, processor, Options);
    }
}

/// <summary>
/// VM producer. Clones the exchange and enqueues into the shared channel.
/// </summary>
public class VmProducer : IProducer
{
    private readonly VmEndpoint _endpoint;
    private ILogger? _logger;

    /// <summary>Creates a VM producer.</summary>
    public VmProducer(VmEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var copy = exchange.Clone();
        await _endpoint.Channel.Writer.WriteAsync(copy, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("Vm producer started: {Name}", _endpoint.Name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("Vm producer stopped: {Name}", _endpoint.Name);
        return Task.CompletedTask;
    }
}

/// <summary>
/// VM consumer. Runs N concurrent workers that dequeue from the shared channel and process.
/// </summary>
public class VmConsumer : DrainableConsumer
{
    private readonly VmEndpoint _endpoint;
    private readonly VmEndpointOptions _options;
    private Task[]? _workers;
    private long _processedCount;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => _endpoint.Name;

    /// <summary>Total number of exchanges processed across all workers.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>Creates a VM consumer.</summary>
    public VmConsumer(VmEndpoint endpoint, IProcessor processor, VmEndpointOptions options)
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
            await foreach (var exchange in _endpoint.Channel.Reader.ReadAllAsync(pollCt).ConfigureAwait(false))
            {
                await ProcessWithTracking(exchange, processingCt).ConfigureAwait(false);
                Interlocked.Increment(ref _processedCount);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }
}
