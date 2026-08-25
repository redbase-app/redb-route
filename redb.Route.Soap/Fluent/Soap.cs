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
    private string? _connectionFactory;

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

    /// <summary>Builds the <c>soap:/path?...</c> URI.</summary>
    public string Build()
    {
        var sb = new StringBuilder("soap:").Append(_path);
        var sep = '?';
        void Append(string key, string value)
        {
            sb.Append(sep).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            sep = '&';
        }
        Append("host", _host);
        Append("port", _port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(_connectionFactory)) Append("connectionFactory", _connectionFactory);
        return sb.ToString();
    }

    /// <summary>Allows passing the builder directly to From().</summary>
    public static implicit operator string(SoapConsumerBuilder builder) => builder.Build();
}
