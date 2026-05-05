using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Components;

/// <summary>
/// Cross-context synchronous component. Scheme: "direct-vm".
/// Like Direct but the processor registry is shared across all RouteContexts
/// via <see cref="SharedVmRegistry"/> (DI singleton).
/// A consumer in context A registers its processor; a producer in context B
/// can invoke it directly — synchronous, zero-copy, cross-context call.
/// </summary>
public class DirectVmComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, DirectVmEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string Scheme => "direct-vm";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return _endpoints.GetOrAdd(uri.BaseKey, _ => new DirectVmEndpoint(uri, this));
    }

    /// <summary>Resolves the shared registry from DI. Returns null if not registered.</summary>
    internal SharedVmRegistry? GetRegistry()
    {
        return Context?.GetServiceProvider()?.GetService<SharedVmRegistry>();
    }
}

/// <summary>
/// Options for direct-vm endpoints. Currently no parameters needed.
/// </summary>
public class DirectVmEndpointOptions : EndpointOptions
{
    /// <inheritdoc />
    public override void Validate() { }
}

/// <summary>
/// Direct-VM endpoint. Unlike DirectEndpoint, does not hold a local processor reference —
/// it delegates to <see cref="SharedVmRegistry"/> for cross-context lookup.
/// </summary>
public class DirectVmEndpoint : EndpointBase<DirectVmEndpointOptions>
{
    internal new DirectVmComponent Component => (DirectVmComponent)base.Component;

    /// <summary>The logical name used as key in the shared registry (the URI host).</summary>
    internal string Name => Uri.BaseKey;

    /// <summary>Creates a new direct-vm endpoint.</summary>
    public DirectVmEndpoint(EndpointUri uri, DirectVmComponent component)
        : base(uri, component, new DirectVmEndpointOptions())
    {
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new DirectVmProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new DirectVmConsumer(this, processor);
    }
}

/// <summary>
/// Direct-VM producer. Looks up the processor from the shared registry and invokes it synchronously.
/// </summary>
public class DirectVmProducer : IProducer
{
    private readonly DirectVmEndpoint _endpoint;
    private ILogger? _logger;

    /// <summary>Creates a direct-vm producer.</summary>
    public DirectVmProducer(DirectVmEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = (_endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var registry = _endpoint.Component.GetRegistry()
            ?? throw new InvalidOperationException(
                $"SharedVmRegistry is not available. Register it in DI to use direct-vm endpoints.");

        var processor = registry.GetProcessor(_endpoint.Name)
            ?? throw new InvalidOperationException(
                $"No consumer registered for direct-vm endpoint '{_endpoint.Name}'. " +
                "Ensure the consumer route is started before sending.");

        await processor.Process(exchange, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("DirectVm producer started: {Name}", _endpoint.Name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("DirectVm producer stopped: {Name}", _endpoint.Name);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Direct-VM consumer. Registers the processor in the shared registry on Start,
/// unregisters on Stop. Only one consumer per name allowed globally.
/// </summary>
public class DirectVmConsumer : IConsumer
{
    private readonly DirectVmEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private ILogger? _logger;

    /// <summary>Creates a direct-vm consumer.</summary>
    public DirectVmConsumer(DirectVmEndpoint endpoint, IProcessor processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        var registry = _endpoint.Component.GetRegistry()
            ?? throw new InvalidOperationException(
                "SharedVmRegistry is not available. Register it in DI to use direct-vm endpoints.");

        if (!registry.TryRegisterProcessor(_endpoint.Name, _processor))
            throw new InvalidOperationException(
                $"A consumer is already registered for direct-vm endpoint '{_endpoint.Name}'. " +
                "Only one consumer per name is allowed across all contexts.");

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("DirectVm consumer started: {Name}", _endpoint.Name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        var registry = _endpoint.Component.GetRegistry();
        registry?.TryUnregisterProcessor(_endpoint.Name, _processor);

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("DirectVm consumer stopped: {Name}", _endpoint.Name);
        return Task.CompletedTask;
    }
}
