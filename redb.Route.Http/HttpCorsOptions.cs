namespace redb.Route.Http;

/// <summary>
/// Global CORS configuration for HTTP components.
/// Applied as defaults to all HTTP consumers unless overridden at the endpoint level.
/// <para>Usage: <c>services.AddRedbRouteHttp(cors => { cors.Enabled = true; cors.Origins = "https://example.com"; });</c></para>
/// </summary>
public class HttpCorsOptions
{
    /// <summary>Enable CORS for all HTTP consumers. Default: false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allowed origins (comma-separated). Default: * when CORS is enabled.
    /// Example: "https://example.com,https://app.example.com"
    /// </summary>
    public string? Origins { get; set; }

    /// <summary>
    /// Enable Access-Control-Allow-Credentials header. Default: false.
    /// When true, Origins must be set to a specific origin (not *).
    /// </summary>
    public bool Credentials { get; set; }

    /// <summary>
    /// Optional global per-request CORS-origin resolver applied as a default to all HTTP
    /// consumers that do not set their own <see cref="HttpEndpointOptions.CorsOriginsResolver"/>.
    /// See <see cref="HttpEndpointOptions.CorsOriginsResolver"/> for the contract.
    /// </summary>
    public Func<Microsoft.AspNetCore.Http.HttpRequest, string?>? OriginsResolver { get; set; }
}
