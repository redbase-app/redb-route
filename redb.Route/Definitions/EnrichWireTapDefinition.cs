using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that wires a copy of the exchange to a secondary endpoint (wire tap pattern).
/// </summary>
public sealed class WireTapDefinition : ProcessorDefinition
{
    private readonly string _uri;
    private readonly Action<IExchange>? _onPrepare;
    private readonly Func<IExchange, object?>? _newBodyFactory;

    /// <summary>Creates a wire tap definition.</summary>
    /// <param name="uri">URI of the tap endpoint.</param>
    /// <param name="onPrepare">Optional action to prepare the tapped exchange copy.</param>
    /// <param name="newBodyFactory">Optional factory to set a different body on the copy.</param>
    public WireTapDefinition(
        string uri,
        Action<IExchange>? onPrepare = null,
        Func<IExchange, object?>? newBodyFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        _uri = uri;
        _onPrepare = onPrepare;
        _newBodyFactory = newBodyFactory;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<WireTapProcessor>();
        return new WireTapProcessor(new ToProcessor(_uri, context), _onPrepare, _newBodyFactory, logger);
    }
}

/// <summary>
/// Leaf definition that calls an external endpoint and merges the result into the exchange (content enricher).
/// </summary>
public sealed class EnrichDefinition : ProcessorDefinition
{
    private readonly string _resourceUri;
    private readonly Func<IExchange, IExchange, IExchange> _mergeStrategy;

    /// <summary>Creates an enrich definition.</summary>
    /// <param name="resourceUri">URI of the enrichment endpoint.</param>
    /// <param name="mergeStrategy">Merge function: (original, enriched) → merged.</param>
    public EnrichDefinition(
        string resourceUri,
        Func<IExchange, IExchange, IExchange> mergeStrategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceUri);
        ArgumentNullException.ThrowIfNull(mergeStrategy);
        _resourceUri = resourceUri;
        _mergeStrategy = mergeStrategy;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new EnrichProcessor(context, _resourceUri, _mergeStrategy);
}

/// <summary>
/// Leaf definition that polls an endpoint and merges the response (poll enricher pattern).
/// </summary>
public sealed class PollEnrichDefinition : ProcessorDefinition
{
    private readonly string _resourceUri;
    private readonly Func<IExchange, IExchange?, IExchange> _mergeStrategy;
    private readonly TimeSpan? _timeout;

    /// <summary>Creates a poll enrich definition.</summary>
    /// <param name="resourceUri">URI of the polling endpoint.</param>
    /// <param name="mergeStrategy">Merge function: (original, polled?) → merged.</param>
    /// <param name="timeout">Optional poll timeout.</param>
    public PollEnrichDefinition(
        string resourceUri,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceUri);
        ArgumentNullException.ThrowIfNull(mergeStrategy);
        _resourceUri = resourceUri;
        _mergeStrategy = mergeStrategy;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new PollEnrichProcessor(context, _resourceUri, _mergeStrategy, _timeout);
}

/// <summary>
/// Wire-tap definition whose target endpoint URI is computed per message (Camel <c>toD</c> semantics for wire-tap).
/// </summary>
public sealed class WireTapDynamicDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, string> _uriFactory;
    private readonly Action<IExchange>? _onPrepare;
    private readonly Func<IExchange, object?>? _newBodyFactory;

    /// <summary>Creates a dynamic-URI wire-tap definition.</summary>
    public WireTapDynamicDefinition(
        Func<IExchange, string> uriFactory,
        Action<IExchange>? onPrepare = null,
        Func<IExchange, object?>? newBodyFactory = null)
    {
        _uriFactory = uriFactory ?? throw new ArgumentNullException(nameof(uriFactory));
        _onPrepare = onPrepare;
        _newBodyFactory = newBodyFactory;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<WireTapProcessor>();
        var resolver = new DynamicEndpointResolver(context, _uriFactory);
        return new WireTapProcessor(new ToDynamicProcessor(resolver), _onPrepare, _newBodyFactory, logger);
    }
}

/// <summary>
/// Enrich definition whose source endpoint URI is computed per message.
/// </summary>
public sealed class EnrichDynamicDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, string> _uriFactory;
    private readonly Func<IExchange, IExchange, IExchange> _mergeStrategy;

    /// <summary>Creates a dynamic-URI enrich definition.</summary>
    public EnrichDynamicDefinition(
        Func<IExchange, string> uriFactory,
        Func<IExchange, IExchange, IExchange> mergeStrategy)
    {
        _uriFactory = uriFactory ?? throw new ArgumentNullException(nameof(uriFactory));
        _mergeStrategy = mergeStrategy ?? throw new ArgumentNullException(nameof(mergeStrategy));
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new EnrichProcessor(context, new DynamicEndpointResolver(context, _uriFactory), _mergeStrategy);
}

/// <summary>
/// Poll-enrich definition whose source endpoint URI is computed per message.
/// </summary>
public sealed class PollEnrichDynamicDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, string> _uriFactory;
    private readonly Func<IExchange, IExchange?, IExchange> _mergeStrategy;
    private readonly TimeSpan? _timeout;

    /// <summary>Creates a dynamic-URI poll-enrich definition.</summary>
    public PollEnrichDynamicDefinition(
        Func<IExchange, string> uriFactory,
        Func<IExchange, IExchange?, IExchange> mergeStrategy,
        TimeSpan? timeout = null)
    {
        _uriFactory = uriFactory ?? throw new ArgumentNullException(nameof(uriFactory));
        _mergeStrategy = mergeStrategy ?? throw new ArgumentNullException(nameof(mergeStrategy));
        _timeout = timeout;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new PollEnrichProcessor(context, new DynamicEndpointResolver(context, _uriFactory), _mergeStrategy, _timeout);
}

/// <summary>
/// Leaf definition that routes to recipient URIs computed at runtime (recipient list pattern).
/// </summary>
public sealed class RecipientListDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, IEnumerable<string>> _recipientListFactory;
    private readonly bool _parallelProcessing;
    private readonly bool _stopOnException;
    private readonly Func<IExchange, IExchange, IExchange>? _aggregationStrategy;

    /// <summary>Creates a recipient list definition.</summary>
    public RecipientListDefinition(
        Func<IExchange, IEnumerable<string>> recipientListFactory,
        bool parallelProcessing = false,
        bool stopOnException = false,
        Func<IExchange, IExchange, IExchange>? aggregationStrategy = null)
    {
        ArgumentNullException.ThrowIfNull(recipientListFactory);
        _recipientListFactory = recipientListFactory;
        _parallelProcessing = parallelProcessing;
        _stopOnException = stopOnException;
        _aggregationStrategy = aggregationStrategy;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new RecipientListProcessor(context, _recipientListFactory, _parallelProcessing,
            _stopOnException, _aggregationStrategy);
}

/// <summary>
/// Leaf definition that routes iteratively to URIs provided by a routing function (dynamic router pattern).
/// </summary>
public sealed class DynamicRouterDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, string?> _routingFunction;

    /// <summary>Creates a dynamic router definition.</summary>
    /// <param name="routingFunction">Function returning next URI, or null to stop routing.</param>
    public DynamicRouterDefinition(Func<IExchange, string?> routingFunction)
    {
        ArgumentNullException.ThrowIfNull(routingFunction);
        _routingFunction = routingFunction;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<DynamicRouterProcessor>();
        return new DynamicRouterProcessor(context, _routingFunction, logger);
    }
}

/// <summary>
/// Leaf definition that stores or retrieves the exchange body via a claim check repository.
/// </summary>
public sealed class ClaimCheckDefinition : ProcessorDefinition
{
    private readonly IClaimCheckRepository _repository;
    private readonly ClaimCheckOperation _operation;
    private readonly string? _key;
    private readonly TimeSpan? _ttl;

    /// <summary>Creates a claim check definition.</summary>
    public ClaimCheckDefinition(
        IClaimCheckRepository repository,
        ClaimCheckOperation operation,
        string? key = null,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _operation = operation;
        _key = key;
        _ttl = ttl;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<ClaimCheckProcessor>();
        return new ClaimCheckProcessor(_repository, _operation, _key, _ttl, logger);
    }
}
