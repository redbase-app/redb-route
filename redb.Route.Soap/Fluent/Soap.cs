using System.Text;

namespace redb.Route.Soap.Fluent;

/// <summary>
/// Entry point for the SOAP fluent DSL.
/// <example><code>
/// // Call a service
/// .To(Soap.Call("https://gds/air.svc").ConnectionFactory("amadeus").Operation("GetFares"))
///
/// // Host a SOAP endpoint
/// From(Soap.Listen("/svc/orders").Host("0.0.0.0").Port(4090).ConnectionFactory("self"))
/// </code></example>
/// </summary>
public static class Soap
{
    /// <summary>Producer: call a SOAP service at the given endpoint URL (http ⇒ soap, https ⇒ soaps).</summary>
    public static SoapProducerBuilder Call(string endpointUrl) => new(endpointUrl);

    /// <summary>Consumer: host a SOAP receive endpoint at the given path.</summary>
    public static SoapConsumerBuilder Listen(string path) => new(path);
}

/// <summary>Fluent builder for a SOAP producer (service call) URI.</summary>
public sealed class SoapProducerBuilder
{
    private readonly string _url;
    private string? _connectionFactory;
    private string? _operation;
    private string? _action;

    internal SoapProducerBuilder(string url) => _url = url ?? throw new ArgumentNullException(nameof(url));

    /// <summary>References a registered <see cref="SoapConnectionFactory"/>.</summary>
    public SoapProducerBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Operation name.</summary>
    public SoapProducerBuilder Operation(string operation) { _operation = operation; return this; }

    /// <summary>Explicit SOAPAction.</summary>
    public SoapProducerBuilder Action(string action) { _action = action; return this; }

    /// <summary>Builds the <c>soap://</c> / <c>soaps://</c> URI.</summary>
    public string Build()
    {
        string scheme = "soap", rest = _url;
        if (_url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { scheme = "soaps"; rest = _url["https://".Length..]; }
        else if (_url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { scheme = "soap"; rest = _url["http://".Length..]; }

        var sb = new StringBuilder(scheme).Append("://").Append(rest);
        var sep = rest.Contains('?') ? '&' : '?';
        void Append(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            sb.Append(sep).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            sep = '&';
        }
        Append("connectionFactory", _connectionFactory);
        Append("operation", _operation);
        Append("action", _action);
        return sb.ToString();
    }

    /// <summary>Allows passing the builder directly to To().</summary>
    public static implicit operator string(SoapProducerBuilder builder) => builder.Build();
}

/// <summary>Fluent builder for a SOAP consumer (receive endpoint) URI.</summary>
public sealed class SoapConsumerBuilder
{
    private readonly string _path;
    private string _host = "0.0.0.0";
    private int _port;
    private int _maxConcurrentRequests;
    private int _requestQueueLimit;
    private int? _rejectStatusCode;
    private int? _retryAfterSeconds;
    private string? _connectionFactory;
    private bool _ssl;
    private string? _sslCertPath;
    private SoapClientCertificateMode _clientCertificateMode = SoapClientCertificateMode.NoCertificate;
    private string? _allowedClientThumbprints;
    private bool _emitHttpCompatHeaders;

    internal SoapConsumerBuilder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>Bind host.</summary>
    public SoapConsumerBuilder Host(string host) { _host = host; return this; }

    /// <summary>Bind port.</summary>
    public SoapConsumerBuilder Port(int port) { _port = port; return this; }

    /// <summary>References a registered <see cref="SoapConnectionFactory"/>.</summary>
    public SoapConsumerBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>
    /// Serves the endpoint over TLS. The certificate path may be given here or left to the connection
    /// factory; the password belongs on the factory only, so it never becomes part of the route key.
    /// </summary>
    public SoapConsumerBuilder Ssl(string? certPath = null)
    {
        _ssl = true;
        _sslCertPath = certPath;
        return this;
    }

    /// <summary>Requires or allows a client certificate (mTLS). Only meaningful together with TLS.</summary>
    public SoapConsumerBuilder ClientCertificate(
        SoapClientCertificateMode mode, string? allowedThumbprints = null)
    {
        _clientCertificateMode = mode;
        _allowedClientThumbprints = allowedThumbprints;
        return this;
    }

    /// <summary>
    /// Also publishes the caller's address, path and method under <c>redbHttp.*</c>, so processors
    /// written against the HTTP transport work unchanged behind this endpoint.
    /// </summary>
    public SoapConsumerBuilder HttpCompatHeaders(bool enabled = true)
    {
        _emitHttpCompatHeaders = enabled;
        return this;
    }

    /// <summary>
    /// Admission limit: at most <paramref name="max"/> concurrent pipeline executions; overflow
    /// beyond the optional FIFO <paramref name="queue"/> is shed with 429 + Retry-After before
    /// any envelope work.
    /// </summary>
    public SoapConsumerBuilder MaxConcurrentRequests(int max, int queue = 0)
    {
        _maxConcurrentRequests = max;
        _requestQueueLimit = queue;
        return this;
    }

    /// <summary>Status code for a shed request (default 429).</summary>
    public SoapConsumerBuilder RejectStatusCode(int statusCode) { _rejectStatusCode = statusCode; return this; }

    /// <summary>Retry-After value for a shed request in seconds; 0 = do not send (default 1).</summary>
    public SoapConsumerBuilder RetryAfterSeconds(int seconds) { _retryAfterSeconds = seconds; return this; }

    /// <summary>Builds the <c>soap:/path?...</c> (or <c>soaps:</c>) URI.</summary>
    public string Build()
    {
        // The scheme is what the component reads the TLS decision from, so TLS changes the scheme rather
        // than adding a parameter — the same spelling a hand-written URI uses.
        var sb = new StringBuilder(_ssl ? "soaps:" : "soap:").Append(_path);
        var sep = '?';
        void Append(string key, string value)
        {
            sb.Append(sep).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            sep = '&';
        }
        Append("host", _host);
        Append("port", _port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(_connectionFactory)) Append("connectionFactory", _connectionFactory);
        if (!string.IsNullOrEmpty(_sslCertPath)) Append("sslCertPath", _sslCertPath!);
        if (_clientCertificateMode != SoapClientCertificateMode.NoCertificate)
            Append("clientCertificateMode", _clientCertificateMode.ToString());
        if (_emitHttpCompatHeaders) Append("emitHttpCompatHeaders", "true");
        if (_maxConcurrentRequests > 0) Append("maxConcurrentRequests", _maxConcurrentRequests.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_requestQueueLimit > 0) Append("requestQueueLimit", _requestQueueLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_rejectStatusCode is { } rsc) Append("rejectStatusCode", rsc.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_retryAfterSeconds is { } ras) Append("retryAfterSeconds", ras.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(_allowedClientThumbprints))
            Append("allowedClientThumbprints", _allowedClientThumbprints!);
        return sb.ToString();
    }

    /// <summary>Allows passing the builder directly to From().</summary>
    public static implicit operator string(SoapConsumerBuilder builder) => builder.Build();
}
