using System.Text;
using System.Web;

namespace redb.Route.Abstractions;

/// <summary>
/// Parsed endpoint URI. Immutable record with normalized cache key.
/// Created by EndpointUriParser — the single URI parser for the entire framework.
/// </summary>
/// <param name="Scheme">URI scheme (e.g., "kafka", "rabbitmq", "redis", "direct").</param>
/// <param name="Path">Path part (e.g., "orders", "GET:user:123", "my-topic").</param>
/// <param name="NormalizedKey">Cache key: "{scheme}://{path}" without query parameters.</param>
/// <param name="RawParameters">Query string parameters as raw strings before type conversion.</param>
public sealed record EndpointUri(
    string Scheme,
    string Path,
    string NormalizedKey,
    IReadOnlyDictionary<string, string> RawParameters)
{
    /// <summary>
    /// Cache key without query parameters: scheme://path.
    /// Used by in-process transports (Direct, SEDA) where consumer and producer
    /// must share the same endpoint regardless of per-side options.
    /// </summary>
    public string BaseKey => $"{Scheme}://{Path}";

    /// <inheritdoc />
    public override string ToString() => ToMaskedUriString();

    /// <summary>
    /// Reconstructs the full URI string including scheme, path, and all query parameters.
    /// Example: <c>seda:order-queue?concurrentConsumers=4&amp;size=1000</c>
    /// </summary>
    public string ToUriString()
    {
        if (RawParameters.Count == 0)
            return $"{Scheme}:{Path}";

        var sb = new StringBuilder();
        sb.Append(Scheme);
        sb.Append(':');
        sb.Append(Path);

        var sep = '?';
        foreach (var (key, value) in RawParameters)
        {
            sb.Append(sep);
            sb.Append(HttpUtility.UrlEncode(key));
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value));
            sep = '&';
        }

        return sb.ToString();
    }

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "saslPassword", "secret", "accessKey", "secretKey", "token", "apiKey",
        "connectionString"
    };

    /// <summary>
    /// Reconstructs the URI with sensitive parameters masked.
    /// Passwords and secrets are shown as first 2 chars + "****".
    /// Example: <c>rabbitmq:my-queue?host=localhost&amp;username=admin&amp;password=ad****</c>
    /// </summary>
    public string ToMaskedUriString()
    {
        if (RawParameters.Count == 0)
            return $"{Scheme}:{Path}";

        var sb = new StringBuilder();
        sb.Append(Scheme);
        sb.Append(':');
        sb.Append(Path);

        var sep = '?';
        foreach (var (key, value) in RawParameters)
        {
            sb.Append(sep);
            sb.Append(HttpUtility.UrlEncode(key));
            sb.Append('=');
            sb.Append(SensitiveKeys.Contains(key) ? MaskValue(value) : HttpUtility.UrlEncode(value));
            sep = '&';
        }

        return sb.ToString();
    }

    private static string MaskValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "****";
        return value.Length > 2
            ? value[..2] + "****"
            : "****";
    }
}
