using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Components;

/// <summary>
/// In-memory synchronous component. Scheme: "direct".
/// When a producer sends an exchange to a direct endpoint, the consumer's processor
/// is invoked synchronously (in-process). No threading, no queuing — direct call.
/// Ideal for testing and for connecting route segments within the same process.
/// </summary>
public class DirectComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, DirectEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string Scheme => "direct";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return _endpoints.GetOrAdd(uri.BaseKey, _ => new DirectEndpoint(uri, this));
    }
}

/// <summary>
/// Options for direct endpoints. Currently no parameters needed.
/// </summary>
public class DirectEndpointOptions : EndpointOptions
{
    /// <inheritdoc />
    public override void Validate() { }
}

/// <summary>
/// Direct endpoint. Holds a reference to the consumer processor so producers can invoke it.
/// </summary>
public class DirectEndpoint : EndpointBase<DirectEndpointOptions>
{
    private volatile IProcessor? _consumerProcessor;

    /// <summary>Creates a new direct endpoint.</summary>
    public DirectEndpoint(EndpointUri uri, DirectComponent component)
        : base(uri, component, new DirectEndpointOptions())
    {
    }

    /// <summary>Gets the consumer processor (null if no consumer registered).</summary>
    internal IProcessor? ConsumerProcessor => _consumerProcessor;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new DirectProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new DirectConsumer(this, processor);
    }

    /// <summary>Registers the consumer processor (called by DirectConsumer.Start).</summary>
    internal void RegisterConsumerProcessor(IProcessor processor)
    {
        _consumerProcessor = processor;
    }

    /// <summary>Unregisters the consumer processor (called by DirectConsumer.Stop).</summary>
    internal void UnregisterConsumerProcessor()
    {
        _consumerProcessor = null;
    }
}

/// <summary>
/// Direct producer. Sends exchanges by invoking the endpoint's consumer processor synchronously.
/// </summary>
public class DirectProducer : IProducer
{
    private readonly DirectEndpoint _endpoint;
    private ILogger? _logger;

    /// <summary>Creates a direct producer.</summary>
    public DirectProducer(DirectEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var processor = _endpoint.ConsumerProcessor
            ?? throw new InvalidOperationException(
                $"No consumer registered for direct endpoint '{_endpoint.Uri.NormalizedKey}'. " +
                "Ensure the consumer route is started before sending.");
        await processor.Process(exchange, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _logger?.LogInformation("Direct producer started: {Name}", EndpointUri.Sanitize(_endpoint.Uri.NormalizedKey));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _logger?.LogInformation("Direct producer stopped: {Name}", EndpointUri.Sanitize(_endpoint.Uri.NormalizedKey));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Direct consumer. Registers the processor on the endpoint so producers can call it.
/// Does not poll or subscribe — just holds a reference.
/// </summary>
public class DirectConsumer : IConsumer
{
    private readonly DirectEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private ILogger? _logger;

    /// <summary>Creates a direct consumer.</summary>
    public DirectConsumer(DirectEndpoint endpoint, IProcessor processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _endpoint.RegisterConsumerProcessor(_processor);
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("Direct consumer started: {Name}", EndpointUri.Sanitize(_endpoint.Uri.NormalizedKey));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _endpoint.UnregisterConsumerProcessor();
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("Direct consumer stopped: {Name}", EndpointUri.Sanitize(_endpoint.Uri.NormalizedKey));
        return Task.CompletedTask;
    }
}
