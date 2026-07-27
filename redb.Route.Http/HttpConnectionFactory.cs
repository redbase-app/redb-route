using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Http;

/// <summary>
/// Named connection factory for HTTP. Register it in the route registry and reference it by name
/// from the endpoint URI (<c>connectionFactory=my-api</c>) so Basic/Bearer credentials and the TLS
/// certificate password never have to appear in the URI, and therefore can never reach logs,
/// telemetry, or the Tsak dashboard.
/// <para>
/// Note: the request/listen address (host and port) is deliberately NOT part of this factory — for
/// the HTTP connector it comes from the endpoint path (<c>http://0.0.0.0:5088/api</c>) and stays
/// there, so a factory can never silently redirect a route to a different host.
/// </para>
/// <para>
/// Example:
/// <code>
/// context.AddToRegistry("billing-api", new HttpConnectionFactory
/// {
///     AuthScheme = HttpAuthScheme.Bearer,
///     AuthToken = Environment.GetEnvironmentVariable("BILLING_TOKEN")
/// });
///
/// // route carries no credentials at all:
/// r.To("http://billing.corp.local/invoices?connectionFactory=billing-api")
/// </code>
/// </para>
/// </summary>
public sealed class HttpConnectionFactory
{
    /// <summary>Authentication scheme (None, Basic, Bearer).</summary>
    public HttpAuthScheme AuthScheme { get; set; } = HttpAuthScheme.None;

    /// <summary>Username for Basic auth.</summary>
    public string? Username { get; set; }

    /// <summary>Password for Basic auth.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Bearer token for Bearer auth. Supports <c>${...}</c> expressions exactly like the URI form
    /// (e.g. <c>${env:BILLING_TOKEN}</c>, <c>${header.jwt}</c>) — an expression is resolved per
    /// request, a plain value is used as-is.
    /// </summary>
    public string? AuthToken { get; set; }

    /// <summary>Request timeout in milliseconds (default 30000).</summary>
    public int Timeout { get; set; } = 30_000;

    /// <summary>Serve/connect over TLS.</summary>
    public bool Ssl { get; set; }

    /// <summary>Path to the TLS certificate.</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for the TLS certificate.</summary>
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Copies this factory's authentication and TLS settings onto the endpoint options, but only
    /// for parameters the endpoint URI did not set explicitly — an inline URI value always wins,
    /// so existing routes keep their behaviour.
    /// </summary>
    internal void ApplyTo(HttpEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.AuthScheme))) options.AuthScheme = AuthScheme;
        if (!supplied.ContainsKey(nameof(options.Username))) options.Username = Username;
        if (!supplied.ContainsKey(nameof(options.Password))) options.Password = Password;

        if (!supplied.ContainsKey(nameof(options.AuthToken)) && AuthToken is not null)
        {
            // Mirror EndpointOptions.BindDynamicValue: a ${...} value becomes a per-request
            // expression, anything else is wrapped as a static value.
            options.AuthToken = AuthToken.Contains("${", StringComparison.Ordinal)
                ? DynamicValue<string>.FromExpression(new StringExpression(AuthToken))
                : DynamicValue<string>.FromStatic(AuthToken);
        }

        if (!supplied.ContainsKey(nameof(options.Timeout))) options.Timeout = Timeout;
        if (!supplied.ContainsKey(nameof(options.Ssl))) options.Ssl = Ssl;
        if (!supplied.ContainsKey(nameof(options.SslCertPath))) options.SslCertPath = SslCertPath;
        if (!supplied.ContainsKey(nameof(options.SslCertPassword)))
            options.SslCertPassword = SslCertPassword;
    }
}
