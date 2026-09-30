using System.Globalization;
using System.Text;

namespace redb.Route.As4.Fluent;

/// <summary>
/// Entry point of the AS4 fluent DSL.
/// <example><code>
/// // Receive server: accepts every partner of the node and matches each message to one agreement
/// From(As4.Receive("/as4/in").Port(4090).Tls().ConnectionFactory("node").IdempotentRepository("as4-in"))
///     .To("direct://inbound");
///
/// // Send to a partner: the address is the URI, the agreement is the registry
/// From("direct://outbound")
///     .To(As4.Send("https://ap.partner.example/as4").ConnectionFactory("node").Partner("acme"));
/// </code></example>
/// </summary>
public static class As4
{
    /// <summary>AS4 receive server on the given HTTP path.</summary>
    public static As4Builder Receive(string path) => new(As4BuilderMode.Receive, path);

    /// <summary>AS4 sender posting to the given partner URL.</summary>
    public static As4Builder Send(string url) => new(As4BuilderMode.Send, url);
}

/// <summary>Which side of the exchange a builder produces.</summary>
internal enum As4BuilderMode
{
    /// <summary>Receive server (consumer).</summary>
    Receive,
    /// <summary>Sender (producer).</summary>
    Send,
}

/// <summary>
/// Fluent builder of AS4 endpoint URIs. Receive: <c>as4:/path?host=..&amp;port=..</c>, the path kept intact.
/// Send: <c>as4[s]://host[:port]/path</c>, with HTTPS mapped onto the <c>as4s</c> scheme.
/// </summary>
public sealed class As4Builder
{
    private readonly As4BuilderMode _mode;
    private readonly string _target;
    private readonly List<(string Key, string Value)> _options = [];
    private bool _useTls;

    internal As4Builder(As4BuilderMode mode, string target)
    {
        _mode = mode;
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    /// <summary>Registry name of the node (<see cref="As4ConnectionFactory"/>). Required.</summary>
    public As4Builder ConnectionFactory(string name) => Set("connectionFactory", name);

    /// <summary>Registry name of the partner to send to (send only); may be an expression such as <c>${header.partner}</c>.</summary>
    public As4Builder Partner(string nameOrExpression) => Set("partner", nameOrExpression);

    /// <summary>Overrides the agreement's <c>eb:Service</c> (send only; the agreement must allow it).</summary>
    public As4Builder Service(string valueOrExpression) => Set("service", valueOrExpression);

    /// <summary>Overrides the agreement's <c>eb:Action</c> (send only; the agreement must allow it).</summary>
    public As4Builder Action(string valueOrExpression) => Set("action", valueOrExpression);

    /// <summary><c>eb:ConversationId</c> of sent messages (send only).</summary>
    public As4Builder ConversationId(string valueOrExpression) => Set("conversationId", valueOrExpression);

    /// <summary><c>eb:RefToMessageId</c> — the reply of a Two-Way / Push-and-Push exchange (send only).</summary>
    public As4Builder RefToMessageId(string valueOrExpression) => Set("refToMessageId", valueOrExpression);

    /// <summary>Milliseconds to wait for the partner's response carrying the receipt (send only).</summary>
    public As4Builder Timeout(int milliseconds) => Set("timeout", milliseconds.ToString(CultureInfo.InvariantCulture));

    /// <summary>Named IIdempotentRepository that remembers received message ids (receive only, required).</summary>
    public As4Builder IdempotentRepository(string name) => Set("idempotentRepository", name);

    /// <summary>Hand a single payload to the route as a <c>Stream</c> instead of <c>byte[]</c> (receive only).</summary>
    public As4Builder StreamBody() => Set("streamBody", "true");

    /// <summary>Bind host of the receive server (receive only).</summary>
    public As4Builder Host(string host) => Set("host", host);

    /// <summary>Listen port of the receive server (receive only).</summary>
    public As4Builder Port(int port) => Set("port", port.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Serve the receive endpoint over TLS (receive only). Omit the certificate to take it from the node's
    /// connection factory; a TLS receiver that finds none refuses to start.
    /// </summary>
    public As4Builder Tls(string? certPath = null, string? certPassword = null)
    {
        _useTls = true;
        if (certPath is not null) Set("sslCertPath", certPath);
        if (certPassword is not null) Set("sslCertPassword", certPassword);
        return this;
    }

    /// <summary>Ask partners for a TLS client certificate (receive only; needs <see cref="Tls"/>).</summary>
    public As4Builder ClientCertificate(As4ClientCertificateMode mode, string? allowedThumbprints = null)
    {
        Set("clientCertificateMode", mode.ToString());
        if (allowedThumbprints is not null) Set("allowedClientThumbprints", allowedThumbprints);
        return this;
    }

    /// <summary>
    /// Admission limit (receive only): at most <paramref name="max"/> concurrent pipeline executions;
    /// overflow beyond the optional FIFO <paramref name="queue"/> is shed before any MIME or crypto work.
    /// </summary>
    public As4Builder MaxConcurrentRequests(int max, int queue = 0)
    {
        Set("maxConcurrentRequests", max.ToString(CultureInfo.InvariantCulture));
        if (queue > 0) Set("requestQueueLimit", queue.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>Status code of a request shed by the admission limit (receive only). Default 503: an AS4 sender retries on it.</summary>
    public As4Builder RejectStatusCode(int statusCode) => Set("rejectStatusCode", statusCode.ToString(CultureInfo.InvariantCulture));

    /// <summary><c>Retry-After</c> of a shed request in seconds; 0 = not sent (receive only).</summary>
    public As4Builder RetryAfterSeconds(int seconds) => Set("retryAfterSeconds", seconds.ToString(CultureInfo.InvariantCulture));

    /// <summary>Upper bound on a request body in bytes (receive only).</summary>
    public As4Builder MaxRequestBodySize(long bytes) => Set("maxRequestBodySize", bytes.ToString(CultureInfo.InvariantCulture));

    /// <summary>Upper bound on the SOAP envelope in characters (receive only); payloads are not counted.</summary>
    public As4Builder MaxEnvelopeCharacters(long characters) => Set("maxEnvelopeCharacters", characters.ToString(CultureInfo.InvariantCulture));

    /// <summary>Builds the AS4 URI string.</summary>
    public string Build() => _mode == As4BuilderMode.Receive ? BuildReceive() : BuildSend();

    private As4Builder Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _options.RemoveAll(o => o.Key == key);
        _options.Add((key, value));
        return this;
    }

    private string BuildReceive()
    {
        var path = _target.StartsWith('/') ? _target : "/" + _target;
        var sb = new StringBuilder(_useTls ? "as4s:" : "as4:").Append(path);
        AppendQuery(sb, '?');
        return sb.ToString();
    }

    private string BuildSend()
    {
        var scheme = "as4";
        var rest = _target;
        if (rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { scheme = "as4s"; rest = rest["https://".Length..]; }
        else if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { rest = rest["http://".Length..]; }
        else if (rest.StartsWith("as4s://", StringComparison.OrdinalIgnoreCase)) { scheme = "as4s"; rest = rest["as4s://".Length..]; }
        else if (rest.StartsWith("as4://", StringComparison.OrdinalIgnoreCase)) { rest = rest["as4://".Length..]; }

        // The query of an as4: URI holds the endpoint's options, as http:'s does; an address with a query of its own
        // would fail later as an unknown option, which reads as the author's typo.
        if (rest.Contains('?'))
            throw new ArgumentException(
                $"AS4 partner address '{_target}' has a query: the query of an as4 URI holds the endpoint's options, so a " +
                "partner address with one cannot be written. Ask the partner for an address without a query.", nameof(_target));

        var sb = new StringBuilder(scheme).Append("://").Append(rest);
        AppendQuery(sb, '?');
        return sb.ToString();
    }

    private void AppendQuery(StringBuilder sb, char first)
    {
        var sep = first;
        foreach (var (key, value) in _options)
        {
            sb.Append(sep).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            sep = '&';
        }
    }

    /// <summary>Lets the builder be passed to From()/To() without calling Build().</summary>
    public static implicit operator string(As4Builder builder) => builder.Build();

    /// <inheritdoc />
    public override string ToString() => Build();
}
