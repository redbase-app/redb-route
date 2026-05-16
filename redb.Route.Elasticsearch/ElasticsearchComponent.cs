using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Elasticsearch;

/// <summary>
/// Elasticsearch transport component for redb.Route.
/// Schemes: <c>elasticsearch</c>, <c>es</c>.
/// <para>
/// URI format: <c>elasticsearch://index-name?nodes=http://localhost:9200</c>
/// </para>
/// <para>
/// Short alias: <c>es://index-name?nodes=http://localhost:9200</c>
/// </para>
/// <para>
/// With operation: <c>es:Search:index-name?nodes=http://localhost:9200&amp;query={"match_all":{}}</c>
/// </para>
/// </summary>
public sealed class ElasticsearchComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "elasticsearch";

    /// <inheritdoc />
    public override IReadOnlyList<string> AlternateSchemes => ["es"];

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new ElasticsearchEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new ElasticsearchEndpoint(uri, this, options);
    }
}

/// <summary>
/// Elasticsearch endpoint. Creates either a consumer (polling search)
/// or a producer (index/bulk/search/get/update/delete/count/exists).
/// <para>
/// The path part of the URI is parsed as <c>[OPERATION:]index-name</c>.
/// When an operation prefix is present (e.g. <c>es:Search:my-index</c>),
/// the producer dispatches that specific operation.
/// </para>
/// </summary>
public sealed class ElasticsearchEndpoint : EndpointBase<ElasticsearchEndpointOptions>
{
    private ElasticsearchClient? _client;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    /// <summary>Parsed operation type (default: Index for producer).</summary>
    public ElasticsearchOperationType OperationType { get; }

    /// <summary>Index name parsed from the URI path.</summary>
    public string IndexName { get; }

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options (exposed for consumer/producer).</summary>
    internal ElasticsearchEndpointOptions EndpointOptions => Options;

    /// <summary>Creates an Elasticsearch endpoint.</summary>
    public ElasticsearchEndpoint(EndpointUri uri, ElasticsearchComponent component, ElasticsearchEndpointOptions options)
        : base(uri, component, options)
    {
        Logger = component.Logger;
        (OperationType, IndexName) = ParsePath(uri.Path);
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new ElasticsearchProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new ElasticsearchConsumer(this, processor, Options);
    }

    /// <summary>Gets or creates a shared <see cref="ElasticsearchClient"/>.</summary>
    internal async Task<ElasticsearchClient> GetOrCreateClientAsync(CancellationToken ct = default)
    {
        if (_client is not null)
            return _client;

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null)
                return _client;

            _client = BuildClient();

            Logger?.LogInformation("Elasticsearch client created: Nodes={Nodes}, Index={Index}",
                Options.Nodes, IndexName);

            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <inheritdoc />
    public override Task Stop(CancellationToken ct = default)
    {
        // ElasticsearchClient uses HttpClient internally — no explicit dispose needed
        _client = null;
        _clientLock.Dispose();
        Logger?.LogInformation("Elasticsearch endpoint stopped: {Uri}", Uri);
        return base.Stop(ct);
    }

    // ── Helpers ──

    private ElasticsearchClient BuildClient()
    {
        // 1. Try named factory from registry
        if (!string.IsNullOrEmpty(Options.ConnectionFactory))
        {
            var component = Component as ElasticsearchComponent;
            var registryFactory = component?.Context?.GetFromRegistry<ElasticsearchConnectionFactory>(
                Options.ConnectionFactory);
            if (registryFactory is not null)
            {
                Logger?.LogDebug("Elasticsearch: using ConnectionFactory '{Name}' from registry",
                    Options.ConnectionFactory);
                return registryFactory.Build();
            }

            Logger?.LogWarning(
                "Elasticsearch: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                Options.ConnectionFactory);
        }

        // 2. Build from options
        var factory = new ElasticsearchConnectionFactory
        {
            Nodes = Options.Nodes,
            ApiKey = Options.ApiKey,
            Username = Options.Username,
            Password = Options.Password,
            CertificateFingerprint = Options.CertificateFingerprint,
            EnableDebugMode = Options.EnableDebugMode,
            RequestTimeout = Options.RequestTimeout,
            PingTimeout = Options.PingTimeout,
            DeadTimeout = Options.DeadTimeout,
            MaxDeadTimeout = Options.MaxDeadTimeout,
            MaxRetries = Options.MaxRetries,
        };
        return factory.Build();
    }

    private static (ElasticsearchOperationType Operation, string IndexName) ParsePath(string path)
    {
        // Path may be "OPERATION:index-name" or just "index-name"
        var colonIdx = path.IndexOf(':');
        if (colonIdx > 0)
        {
            var opPart = path[..colonIdx];
            var indexPart = path[(colonIdx + 1)..];
            if (Enum.TryParse<ElasticsearchOperationType>(opPart, ignoreCase: true, out var op))
                return (op, indexPart);
        }

        return (ElasticsearchOperationType.Index, path);
    }
}
