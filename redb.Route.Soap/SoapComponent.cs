using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Soap;

// ── Component ────────────────────────────────────────────────────────────────

/// <summary>
/// SOAP / WSDL transport component (oriented to Apache Camel <c>camel-cxf</c>). Schemes: <c>soap</c> (HTTP)
/// and <c>soaps</c> (HTTPS). Producer calls a service; consumer hosts a SOAP endpoint on the shared Kestrel
/// host. Baseline is in-box (HttpClient + Http.Hosting + System.Security.Cryptography.Xml); typed WSDL (Pojo)
/// and CoreWCF <c>?wsdl</c> publishing are optional later modes. See <c>docs/SOAP_CONNECTOR_PLAN.md</c>.
/// </summary>
public sealed class SoapComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "soap";

    /// <summary>HTTPS variant of the SOAP scheme.</summary>
    public override IReadOnlyList<string> AlternateSchemes => ["soaps"];

    /// <summary>Shared Kestrel host for the SOAP receive endpoint (set from DI; used by the Ф2 consumer).</summary>
    public SharedHttpServerManager? ServerManager { get; set; }

    private readonly Lazy<SharedHttpServerManager> _ownServer = new(() => new SharedHttpServerManager());

    /// <summary>The receive server: the DI-shared one, or a lazily-created own instance (standalone/test).</summary>
    internal SharedHttpServerManager Server => ServerManager ?? _ownServer.Value;

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new SoapEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Path = uri.Path;

        // The scheme carries the TLS decision: soaps ⇒ HTTPS.
        if (string.Equals(uri.Scheme, "soaps", StringComparison.OrdinalIgnoreCase))
            options.UseTls = true;

        options.Validate();
        return new SoapEndpoint(uri, this, options);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_ownServer.IsValueCreated)
            await _ownServer.Value.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

// ── Options ──────────────────────────────────────────────────────────────────

/// <summary>Options for a SOAP endpoint.</summary>
public sealed class SoapEndpointOptions : EndpointOptions
{
    /// <summary>Path from the URI (producer host+path, or consumer receive path).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Name of the registered <see cref="SoapConnectionFactory"/>.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Operation name / SOAPAction override for a producer call.</summary>
    public string? Operation { get; set; }

    /// <summary>Explicit SOAPAction (overrides the factory default).</summary>
    public string? Action { get; set; }

    /// <summary>Consumer bind host.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Consumer bind port.</summary>
    public int Port { get; set; }

    /// <summary>HTTPS/TLS. Set automatically when the URI scheme is <c>soaps</c>.</summary>
    public bool UseTls { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        // Ф0: minimal. Full fail-fast (version/operation/cert checks) lands with the producer/consumer.
    }
}

// ── Endpoint ─────────────────────────────────────────────────────────────────

/// <summary>SOAP endpoint. Producer calls a service; consumer hosts a receive endpoint.</summary>
public sealed class SoapEndpoint : EndpointBase<SoapEndpointOptions>
{
    private readonly SoapEndpointOptions _options;

    internal SoapEndpoint(EndpointUri uri, SoapComponent component, SoapEndpointOptions options)
        : base(uri, component, options)
        => _options = options;

    internal SoapEndpointOptions SoapOptions => _options;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new SoapProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => new SoapConsumer(this, processor, ((SoapComponent)Component).Server);
}
