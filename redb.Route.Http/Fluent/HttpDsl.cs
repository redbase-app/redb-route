using System.Text;
using System.Web;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Http;

/// <summary>
/// Fluent API for HTTP endpoints.
/// <example><code>
/// .To(Http.Post("api.example.com/users").ContentType("application/xml").Timeout(5000))
/// .To(Http.Get("api.example.com/data").BasicAuth("user", "pass"))
/// .From(Http.Listen("/webhook").Port(8080).Cors())
/// </code></example>
/// </summary>
public static class Http
{
    /// <summary>HTTP GET request to the given URL path.</summary>
    public static HttpBuilder Get(string path) => new("http", path, "GET");

    /// <summary>HTTP POST request to the given URL path.</summary>
    public static HttpBuilder Post(string path) => new("http", path, "POST");

    /// <summary>HTTP PUT request to the given URL path.</summary>
    public static HttpBuilder Put(string path) => new("http", path, "PUT");

    /// <summary>HTTP DELETE request to the given URL path.</summary>
    public static HttpBuilder Delete(string path) => new("http", path, "DELETE");

    /// <summary>HTTP PATCH request to the given URL path.</summary>
    public static HttpBuilder Patch(string path) => new("http", path, "PATCH");

    /// <summary>HTTP HEAD request to the given URL path.</summary>
    public static HttpBuilder Head(string path) => new("http", path, "HEAD");

    /// <summary>HTTP consumer — listens for incoming requests on the given route path.</summary>
    public static HttpBuilder Listen(string path) => new("http", path, null);
}

/// <summary>
/// Fluent API for HTTPS endpoints.
/// <example><code>
/// .To(Https.Post("secure-api.com/submit").BasicAuth("user", "pass"))
/// .From(Https.Listen("/secure").Port(8443).SslCert("cert.pfx", "pass"))
/// </code></example>
/// </summary>
public static class Https
{
    /// <summary>HTTPS GET request to the given URL path.</summary>
    public static HttpBuilder Get(string path) => new("https", path, "GET");

    /// <summary>HTTPS POST request to the given URL path.</summary>
    public static HttpBuilder Post(string path) => new("https", path, "POST");

    /// <summary>HTTPS PUT request to the given URL path.</summary>
    public static HttpBuilder Put(string path) => new("https", path, "PUT");

    /// <summary>HTTPS DELETE request to the given URL path.</summary>
    public static HttpBuilder Delete(string path) => new("https", path, "DELETE");

    /// <summary>HTTPS PATCH request to the given URL path.</summary>
    public static HttpBuilder Patch(string path) => new("https", path, "PATCH");

    /// <summary>HTTPS HEAD request to the given URL path.</summary>
    public static HttpBuilder Head(string path) => new("https", path, "HEAD");

    /// <summary>HTTPS consumer — listens for incoming requests on the given route path.</summary>
    public static HttpBuilder Listen(string path) => new("https", path, null);
}

/// <summary>
/// Fluent builder for HTTP/HTTPS endpoint URIs.
/// </summary>
public sealed class HttpBuilder
{
    private readonly string _scheme;
    private readonly string _path;
    private readonly string? _method;

    // Common
    private string? _timeout;
    private string? _contentType;

    // Producer
    private bool? _throwOnError;
    private bool? _bridgeHeaders;
    private string? _authScheme;
    private string? _authToken;
    private string? _username;
    private string? _password;
    private bool? _followRedirects;
    private string? _maxRedirects;
    private bool? _copyResponseHeaders;
    private bool _preserveHostHeader;

    // Query-string parameters
    private readonly List<(string Name, string Value)> _queryParams = [];

    // Host/Port (producer: embedded in URL path; consumer: query params)
    private string? _host;
    private string? _port;
    private string? _methods;
    private bool _cors;
    private string? _corsOrigins;
    private bool _corsCredentials;
    private string? _maxRequestBodySize;
    private string? _protocol;
    private bool _ssl;
    private string? _sslCertPath;
    private string? _sslCertPassword;
    private string? _responseCode;
    private bool _inOut;
    private bool _streamRequest;
    private bool _streamResponse;

    internal HttpBuilder(string scheme, string path, string? method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _scheme = scheme;
        _path = path;
        _method = method;
    }

    // ── Common ────────────────────────────────────────────────────────

    /// <summary>Request/response timeout in milliseconds. Default 30000.</summary>
    public HttpBuilder Timeout(int ms) { _timeout = ms.ToString(); return this; }

    /// <summary>Request/response timeout from an expression.</summary>
    public HttpBuilder Timeout(IExpression timeout) { _timeout = timeout.ToTemplateString(); return this; }

    /// <summary>Content type. Default "application/json".</summary>
    public HttpBuilder ContentType(string contentType) { _contentType = contentType; return this; }

    // ── Producer ──────────────────────────────────────────────────────

    /// <summary>Disable throwing on non-success HTTP status codes. Default is to throw.</summary>
    public HttpBuilder NoThrowOnError() { _throwOnError = false; return this; }

    /// <summary>Disable bridging headers from the exchange to the HTTP request. Default is on.</summary>
    public HttpBuilder NoBridgeHeaders() { _bridgeHeaders = false; return this; }

    /// <summary>Use HTTP Basic authentication.</summary>
    public HttpBuilder BasicAuth(IExpression username, IExpression password)
    {
        _authScheme = "Basic"; _username = username.ToTemplateString(); _password = password.ToTemplateString(); return this;
    }

    /// <summary>Use HTTP Basic authentication (template strings, support <c>${...}</c>).</summary>
    public HttpBuilder BasicAuth(string username, string password) => BasicAuth(new StringExpression(username), new StringExpression(password));

    /// <summary>Use Bearer token authentication. Token is resolved via AuthToken option at runtime.</summary>
    public HttpBuilder BearerAuth() { _authScheme = "Bearer"; return this; }

    /// <summary>Bearer/API-key token value or expression (e.g. <c>Property("jwt")</c>).</summary>
    public HttpBuilder AuthToken(IExpression token) { _authToken = token.ToTemplateString(); return this; }

    /// <summary>Bearer/API-key token value (template string, supports <c>${...}</c>).</summary>
    public HttpBuilder AuthToken(string token) => AuthToken(new StringExpression(token));

    /// <summary>Disable following HTTP redirects. Default is to follow.</summary>
    public HttpBuilder NoFollowRedirects() { _followRedirects = false; return this; }

    /// <summary>Maximum number of redirects to follow. Default 50.</summary>
    public HttpBuilder MaxRedirects(int max) { _maxRedirects = max.ToString(); return this; }

    /// <summary>Maximum number of redirects from an expression.</summary>
    public HttpBuilder MaxRedirects(IExpression max) { _maxRedirects = max.ToTemplateString(); return this; }

    /// <summary>Disable copying response headers to the exchange. Default is on.</summary>
    public HttpBuilder NoCopyResponseHeaders() { _copyResponseHeaders = false; return this; }

    /// <summary>Preserve the original Host header when proxying.</summary>
    public HttpBuilder PreserveHostHeader() { _preserveHostHeader = true; return this; }

    // ── Host / Port ────────────────────────────────────────────────────
    // Producer: embedded in URL (supports ${...} expressions).
    // Consumer: emitted as query params for Kestrel bind config.

    /// <summary>
    /// Target host. Producer: part of request URL. Consumer: Kestrel bind address.
    /// </summary>
    public HttpBuilder Host(IExpression host) { _host = host.ToTemplateString(); return this; }

    /// <summary>
    /// Target host (template string, supports <c>${...}</c>). Producer: part of request URL. Consumer: Kestrel bind address.
    /// </summary>
    public HttpBuilder Host(string host) => Host(new StringExpression(host));

    /// <summary>
    /// Target port. Producer: part of request URL. Consumer: Kestrel bind port.
    /// </summary>
    public HttpBuilder Port(int port) { _port = port.ToString(); return this; }

    /// <summary>
    /// Target port from an expression — resolved per message at runtime.
    /// </summary>
    public HttpBuilder Port(IExpression port) { _port = port.ToTemplateString(); return this; }

    // ── Consumer ──────────────────────────────────────────────────────

    /// <summary>Comma-separated HTTP methods to accept (e.g. "POST,PUT"). Default: all.</summary>
    public HttpBuilder Methods(string methods) { _methods = methods; return this; }

    /// <summary>Enable CORS with optional allowed origins.</summary>
    public HttpBuilder Cors(string? origins = null) { _cors = true; _corsOrigins = origins; return this; }

    /// <summary>Allow credentials in CORS requests.</summary>
    public HttpBuilder CorsCredentials() { _corsCredentials = true; return this; }

    /// <summary>Maximum request body size in bytes. Default 10 MB.</summary>
    public HttpBuilder MaxRequestBodySize(long bytes) { _maxRequestBodySize = bytes.ToString(); return this; }

    /// <summary>Maximum request body size from an expression.</summary>
    public HttpBuilder MaxRequestBodySize(IExpression bytes) { _maxRequestBodySize = bytes.ToTemplateString(); return this; }

    /// <summary>HTTP protocol version: Http1, Http2, Http1And2, Http3, Http1And2And3.</summary>
    public HttpBuilder Protocol(string protocol) { _protocol = protocol; return this; }

    /// <summary>Enable SSL/TLS with the given certificate.</summary>
    public HttpBuilder SslCert(IExpression certPath, IExpression? certPassword = null)
    {
        _ssl = true; _sslCertPath = certPath.ToTemplateString(); _sslCertPassword = certPassword?.ToTemplateString(); return this;
    }

    /// <summary>Enable SSL/TLS with the given certificate (template strings, support <c>${...}</c>).</summary>
    public HttpBuilder SslCert(string certPath, string? certPassword = null)
        => SslCert(new StringExpression(certPath), certPassword is null ? null : new StringExpression(certPassword));

    /// <summary>Default HTTP response status code. Default 200.</summary>
    public HttpBuilder ResponseCode(int code) { _responseCode = code.ToString(); return this; }

    /// <summary>Response status code from an expression.</summary>
    public HttpBuilder ResponseCode(IExpression code) { _responseCode = code.ToTemplateString(); return this; }

    /// <summary>Enable request-reply (InOut) exchange pattern.</summary>
    public HttpBuilder InOut() { _inOut = true; return this; }

    /// <summary>Enable request body streaming (consumer: body = Stream instead of byte[]).</summary>
    public HttpBuilder StreamRequest() { _streamRequest = true; return this; }

    /// <summary>Enable response body streaming (producer: Out body = Stream instead of byte[]).</summary>
    public HttpBuilder StreamResponse() { _streamResponse = true; return this; }

    // ── Query Parameters ──────────────────────────────────────────────

    /// <summary>
    /// Adds a query-string parameter to the HTTP request URL.
    /// Values containing <c>${...}</c> are resolved as expressions at runtime.
    /// </summary>
    /// <param name="name">Parameter name.</param>
    /// <param name="value">Constant value or expression string.</param>
    public HttpBuilder Param(string name, object? value)
    {
        _queryParams.Add((name, value?.ToString() ?? ""));
        return this;
    }

    /// <summary>
    /// Adds a query-string parameter bound to an expression, resolved at runtime.
    /// </summary>
    /// <param name="name">Parameter name.</param>
    /// <param name="expression">Expression that produces the parameter value.</param>
    public HttpBuilder Param(string name, IExpression expression)
    {
        _queryParams.Add((name, expression.ToTemplateString()));
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the HTTP/HTTPS endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append(_scheme);
        sb.Append(':');

        // Producer with explicit Host → embed host[:port] before path
        if (_method != null && _host != null)
        {
            sb.Append(_host);
            if (_port != null)
            {
                sb.Append(':');
                sb.Append(_port);
            }
            if (_path.Length > 0 && _path[0] != '/')
                sb.Append('/');
            sb.Append(_path);
        }
        else
        {
            sb.Append(_path);
        }

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolFalse(string key, bool? value) { if (value == false) Append(key, "false"); }

        // Method (producer)
        AppendIf("method", _method);

        // Common
        AppendIf("timeout", _timeout);
        AppendIf("contentType", _contentType);

        // Producer
        AppendBoolFalse("throwOnError", _throwOnError);
        AppendBoolFalse("bridgeHeaders", _bridgeHeaders);
        AppendIf("authScheme", _authScheme);
        AppendIf("authToken", _authToken);
        AppendIf("username", _username);
        AppendIf("password", _password);
        AppendBoolFalse("followRedirects", _followRedirects);
        AppendIf("maxRedirects", _maxRedirects);
        AppendBoolFalse("copyResponseHeaders", _copyResponseHeaders);
        AppendBool("preserveHostHeader", _preserveHostHeader);

        // Consumer host/port as query params (producer embeds them in URL path)
        if (_method == null)
        {
            AppendIf("host", _host);
            AppendIf("port", _port);
        }
        AppendIf("methods", _methods);
        AppendBool("cors", _cors);
        AppendIf("corsOrigins", _corsOrigins);
        AppendBool("corsCredentials", _corsCredentials);
        AppendIf("maxRequestBodySize", _maxRequestBodySize);
        AppendIf("protocol", _protocol);
        AppendBool("ssl", _ssl);
        AppendIf("sslCertPath", _sslCertPath);
        AppendIf("sslCertPassword", _sslCertPassword);
        AppendIf("responseCode", _responseCode);
        AppendBool("inOut", _inOut);
        AppendBool("streamRequest", _streamRequest);
        AppendBool("streamResponse", _streamResponse);

        // Query parameters
        foreach (var (name, value) in _queryParams)
            Append($"param.{name}", value);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(HttpBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
