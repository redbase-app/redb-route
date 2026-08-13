using System.Text;

namespace redb.Route.As2.Fluent;

/// <summary>
/// Entry point for the AS2 fluent DSL.
/// <example><code>
/// // Receive server (consumer)
/// From(As2.Receive("/inbound/orders").Host("0.0.0.0").Port(4080).ConnectionFactory("walmart"))
///     .To("direct://process-edi");
///
/// // Send to partner (producer)
/// From("direct://outbound")
///     .To(As2.Send("https://partner.example.com/as2").ConnectionFactory("walmart"));
/// </code></example>
/// </summary>
public static class As2
{
    /// <summary>AS2 receive server on the given HTTP path.</summary>
    public static As2Builder Receive(string path) => new(As2Mode.Receive, path);

    /// <summary>AS2 async-MDN receiver on the given HTTP path (correlates receipts posted back by partners).</summary>
    public static As2Builder ReceiveMdn(string path) => new(As2Mode.Receive, path) { AsMdnReceiver = true };

    /// <summary>AS2 producer that POSTs to the given partner URL.</summary>
    public static As2Builder Send(string url) => new(As2Mode.Send, url);
}

/// <summary>Which side of the AS2 exchange a builder produces.</summary>
internal enum As2Mode
{
    /// <summary>Receive server (consumer).</summary>
    Receive,
    /// <summary>Send client (producer).</summary>
    Send,
}

/// <summary>
/// Fluent builder for AS2 endpoint URIs. Builds a string consumed by <see cref="As2Component.CreateEndpoint"/>.
/// <para>
/// Receive: <c>as2:/path?host=..&amp;port=..</c> — host/port as query, the path kept intact (no first-segment
/// truncation, mirroring the Http listener fix). Send: <c>as2[s]://host[:port]/path</c> — HTTPS maps to the
/// <c>as2s</c> scheme.
/// </para>
/// </summary>
public sealed class As2Builder
{
    private readonly As2Mode _mode;
    private readonly string _target;
    private string? _host;
    private int? _port;
    private string? _connectionFactory;

    /// <summary>When set, this receive endpoint accepts async MDN receipts (<c>mode=mdn</c>).</summary>
    internal bool AsMdnReceiver { get; init; }

    internal As2Builder(As2Mode mode, string target)
    {
        _mode = mode;
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    /// <summary>Bind host for the receive server (consumer only).</summary>
    public As2Builder Host(string host) { _host = host; return this; }

    /// <summary>Listen port for the receive server (consumer only).</summary>
    public As2Builder Port(int port) { _port = port; return this; }

    /// <summary>Reference a named <see cref="As2ConnectionFactory"/> from the registry instead of inline config.</summary>
    public As2Builder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Builds the AS2 URI string.</summary>
    public string Build() => _mode == As2Mode.Receive ? BuildReceive() : BuildSend();

    private string BuildReceive()
    {
        // Keep the path intact incl. its leading slash — the first segment must NOT be dropped
        // (see the Http ConsumerPath fix). host/port travel as query parameters.
        var path = _target.StartsWith('/') ? _target : "/" + _target;
        var sb = new StringBuilder("as2:").Append(path);
        var sep = '?';
        void Add(string k, string v) { sb.Append(sep).Append(k).Append('=').Append(Uri.EscapeDataString(v)); sep = '&'; }

        if (_host is not null) Add("host", _host);
        if (_port is not null) Add("port", _port.Value.ToString());
        if (_connectionFactory is not null) Add("connectionFactory", _connectionFactory);
        if (AsMdnReceiver) Add("mode", "mdn");
        return sb.ToString();
    }

    private string BuildSend()
    {
        // Map the partner URL scheme onto as2/as2s so the component is selected; TLS is carried by the scheme.
        var scheme = "as2";
        var rest = _target;
        if (rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { scheme = "as2s"; rest = rest["https://".Length..]; }
        else if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { rest = rest["http://".Length..]; }
        else if (rest.StartsWith("as2s://", StringComparison.OrdinalIgnoreCase)) { scheme = "as2s"; rest = rest["as2s://".Length..]; }
        else if (rest.StartsWith("as2://", StringComparison.OrdinalIgnoreCase)) { rest = rest["as2://".Length..]; }

        var sb = new StringBuilder(scheme).Append("://").Append(rest);
        var sep = rest.Contains('?') ? '&' : '?';
        void Add(string k, string v) { sb.Append(sep).Append(k).Append('=').Append(Uri.EscapeDataString(v)); sep = '&'; }

        if (_connectionFactory is not null) Add("connectionFactory", _connectionFactory);
        return sb.ToString();
    }

    /// <summary>Allows passing the builder directly to From()/To() without calling Build().</summary>
    public static implicit operator string(As2Builder builder) => builder.Build();

    /// <inheritdoc />
    public override string ToString() => Build();
}
