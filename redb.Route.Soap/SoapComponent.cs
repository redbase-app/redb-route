using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;
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

    /// <summary>
    /// The receive server. Resolution order: explicitly assigned → the context's DI singleton → a
    /// lazily-created own instance (standalone/test). The DI step matters for module hosts that add
    /// the component by scanning rather than through the extension method, so nothing ever assigns
    /// <see cref="ServerManager"/> while the one shared manager sits in the container.
    /// </summary>
    internal SharedHttpServerManager Server =>
        ServerManager
        ?? Context.Resolve<SharedHttpServerManager>()
        ?? _ownServer.Value;

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

/// <summary>Whether the consumer requires, allows, or ignores a client certificate (mTLS).</summary>
public enum SoapClientCertificateMode
{
    /// <summary>No client certificate is requested. Default.</summary>
    NoCertificate,

    /// <summary>A certificate is requested; the call proceeds when the client presents none.</summary>
    AllowCertificate,

    /// <summary>A certificate is required; the handshake fails without one.</summary>
    RequireCertificate,
}

/// <summary>Options for a SOAP endpoint.</summary>
public sealed class SoapEndpointOptions : EndpointOptions
{
    /// <summary>Path from the URI (producer host+path, or consumer receive path).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Name of the registered <see cref="SoapConnectionFactory"/>.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Operation name / SOAPAction override for a producer call.</summary>
    public string? Operation { get; set; }

    /// <summary>
    /// Whether a <c>soap:Fault</c> in the reply fails the exchange (default true). Set false when the
    /// fault is an answer the service is designed to give: the reply comes back on <c>Out</c>, the
    /// fault code and reason on <c>redbSoap.faultCode</c> / <c>redbSoap.faultString</c>, and
    /// <c>redbSoap.isFault</c> says which kind of reply arrived. Explicit rather than inferred from
    /// the fault code: a Sender fault can be a bug in the route just as easily as a business answer,
    /// and only the author knows which. Mirrors <c>throwOnError</c> on the HTTP producer.
    /// </summary>
    public bool ThrowOnFault { get; set; } = true;

    /// <summary>Explicit SOAPAction (overrides the factory default).</summary>
    public string? Action { get; set; }

    /// <summary>Consumer bind host.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Consumer bind port.</summary>
    public int Port { get; set; }

    /// <summary>HTTPS/TLS. Set automatically when the URI scheme is <c>soaps</c>.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// Path to the PFX certificate the consumer serves TLS with. Required alongside
    /// <see cref="UseTls"/> unless a named <see cref="SoapConnectionFactory"/> supplies it.
    /// </summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>Client-certificate policy (mTLS) for the consumer. Requires <see cref="UseTls"/>.</summary>
    public SoapClientCertificateMode ClientCertificateMode { get; set; } = SoapClientCertificateMode.NoCertificate;

    /// <summary>
    /// Comma-separated thumbprints of accepted client certificates. When set, a presented certificate
    /// whose thumbprint is not listed is rejected even if its chain validates.
    /// </summary>
    public string? AllowedClientThumbprints { get; set; }

    /// <summary>
    /// Also publish the caller's address, path and method under <c>redbHttp.*</c>, so processors written
    /// against the HTTP transport (rate limiting, lockout, device metadata) work unchanged behind a SOAP
    /// endpoint. Default: false — the connector's own <c>redbSoap.*</c> headers always carry the same
    /// facts, and writing into another transport's namespace should be asked for.
    /// </summary>
    public bool EmitHttpCompatHeaders { get; set; }

    // ── Admission limit (HTTP_CONCURRENCY_LIMITS_PLAN) ──

    /// <summary>
    /// Maximum concurrent executions of this consumer's pipeline. 0 (default) = unlimited.
    /// Overflow beyond the limit and <see cref="RequestQueueLimit"/> is shed with
    /// <see cref="RejectStatusCode"/> before any pipeline work.
    /// </summary>
    public int MaxConcurrentRequests { get; set; }

    /// <summary>Requests over the limit that WAIT for a permit (FIFO). 0 (default) = reject immediately.</summary>
    public int RequestQueueLimit { get; set; }

    /// <summary>Status code for a shed request. Default 429 Too Many Requests.</summary>
    public int RejectStatusCode { get; set; } = 429;

    /// <summary>Value of the <c>Retry-After</c> header on a shed request; 0 = do not send it. Default 1.</summary>
    public int RetryAfterSeconds { get; set; } = 1;

    /// <inheritdoc />
    public override void Validate()
    {
        // Ф0: minimal. Full fail-fast (version/operation/cert checks) lands with the producer/consumer.
        ConcurrencyLimitOptions.ValidateShape(MaxConcurrentRequests, RequestQueueLimit, RejectStatusCode, RetryAfterSeconds);
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
