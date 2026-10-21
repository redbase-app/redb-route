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
        var options = new DirectVmEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        // Keyed with the parameters: a producer's headerFilterStrategy must not be lost to, or leak into, a consumer of
        // the same name in this context. The call itself finds its target by name (Uri.BaseKey) in the shared registry.
        return _endpoints.GetOrAdd(uri.NormalizedKey, _ => new DirectVmEndpoint(uri, this, options));
    }

    /// <summary>Resolves the shared registry from DI. Returns null if not registered.</summary>
    internal SharedVmRegistry? GetRegistry()
    {
        return Context?.GetServiceProvider()?.GetService<SharedVmRegistry>();
    }
}

/// <summary>
/// Options for direct-vm endpoints.
/// </summary>
public class DirectVmEndpointOptions : EndpointOptions
{
    /// <summary>
    /// Producer only, as in Apache Camel 3: a registry reference (<c>#name</c>) to an
    /// <see cref="IHeaderFilterStrategy"/> for the call, in both directions. With it the called context works on a
    /// copy of the exchange that carries the headers the strategy lets out; when the call returns, the caller keeps
    /// its own headers and takes the reply's body and the reply headers the strategy lets in. Without it (the
    /// default) the called context works on the caller's own exchange and every header crosses both ways.
    /// </summary>
    [EndpointRole(EndpointRole.Producer)]
    public string? HeaderFilterStrategy { get; set; }

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
    public DirectVmEndpoint(EndpointUri uri, DirectVmComponent component, DirectVmEndpointOptions options)
        : base(uri, component, options)
    {
        _headerFilter = new EndpointHeaderFilter(this, options.HeaderFilterStrategy);
    }

    private readonly EndpointHeaderFilter _headerFilter;

    /// <summary>
    /// The strategy <c>headerFilterStrategy=#name</c> names, or <c>null</c>: direct-vm has no default strategy. A name
    /// the registry does not hold fails when the producer is built.
    /// </summary>
    internal IHeaderFilterStrategy? HeaderFilterStrategy => _headerFilter.Configured;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new DirectVmProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        var written = EndpointOptions.WrittenForOtherRole(Options.GetType(), Uri, EndpointRole.Consumer);
        if (written.Count > 0)
            throw new InvalidOperationException(
                $"direct-vm consumer '{EndpointUri.Sanitize(Uri.NormalizedKey)}' sets {string.Join(", ", written)}, " +
                "which applies to the calling side (.To), as in Apache Camel; a consumer has nothing to filter. " +
                "Move it to the producer URI.");
        return new DirectVmConsumer(this, processor);
    }
}

/// <summary>
/// Direct-VM producer. Looks up the processor from the shared registry and invokes it synchronously.
/// </summary>
public class DirectVmProducer : IProducer
{
    private readonly DirectVmEndpoint _endpoint;
    private readonly IHeaderFilterStrategy? _headerFilter;
    private ILogger? _logger;

    /// <summary>Creates a direct-vm producer.</summary>
    public DirectVmProducer(DirectVmEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = (_endpoint.Component as ComponentBase)?.Logger;
        _headerFilter = endpoint.HeaderFilterStrategy;
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

        if (_headerFilter is not null)
        {
            await ProcessFiltered(processor, exchange, _headerFilter, ct).ConfigureAwait(false);
            return;
        }

        // direct-vm: runs the target route — in another context — on THIS exchange, and the target's
        // first step stamps exchange.RouteId; nothing restores it, so after the call the caller would
        // carry the callee's id and its own route span and step metrics would be attributed to the callee.
        // Save ours and put it back once the target returns — on failure too, before the caller's own
        // error handler runs.
        var callerRouteId = exchange.RouteId;
        try
        {
            await processor.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            exchange.RouteId = callerRouteId;
        }
    }

    /// <summary>
    /// The call under a header filter strategy, Apache Camel 3's <c>DirectVmProducer</c>: the called context works on a
    /// copy, so dropping a header there never takes it from the caller. The copy shares the caller's DI scope, unit of
    /// work and properties (<see cref="IExchange.CloneLinked"/>), as the zero-copy call does. When it returns, the
    /// caller keeps its own headers and takes the reply's body, content type and the reply headers the strategy lets
    /// in, plus the copy's properties, exception and stop, which the zero-copy call would have left on it.
    /// </summary>
    private static async Task ProcessFiltered(IProcessor processor, IExchange exchange, IHeaderFilterStrategy headerFilter,
        CancellationToken ct)
    {
        var submitted = exchange.CloneLinked();
        try
        {
            foreach (var (name, value) in submitted.In.Headers.ToList())
            {
                if (headerFilter.FilterOutgoing(name, value, submitted))
                    submitted.In.Headers.Remove(name);
            }

            try
            {
                await processor.Process(submitted, ct).ConfigureAwait(false);
            }
            finally
            {
                var reply = submitted.HasOut ? submitted.Out! : submitted.In;
                exchange.In.Body = reply.Body;
                exchange.In.ContentType = reply.ContentType;
                foreach (var (name, value) in reply.Headers)
                {
                    if (!headerFilter.FilterIncoming(name, value, submitted))
                        exchange.In.Headers[name] = value;
                }

                foreach (var (key, value) in submitted.Properties)
                {
                    if (!ExchangeResources.IsOwnedByExchange(key))
                        exchange.Properties[key] = value;
                }

                if (submitted.Exception is not null)
                {
                    exchange.Exception = submitted.Exception;
                    exchange.ExceptionHandled = submitted.ExceptionHandled;
                }

                if (submitted.IsStopped)
                    exchange.Stop();
            }
        }
        finally
        {
            // A streamed reply body now on the caller keeps its connection until the caller ends, as with Enrich.
            ExchangeResources.HandOver(submitted, exchange);
            await submitted.DisposeAsync().ConfigureAwait(false);
        }
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
