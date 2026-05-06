using System.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Single URI parser for the entire framework. Handles all endpoint URI formats:
/// - Standard: scheme://path?key=value&amp;key=value
/// - Colon-path: redis:GET:user:123?ttl=60
/// - Topic: kafka://my-topic?brokers=localhost:9092
/// </summary>
public static class EndpointUriParser
{
    /// <summary>Parses a route URI into normalized parts.</summary>
    /// <param name="uri">Raw endpoint URI string.</param>
    /// <returns>Parsed EndpointUri with scheme, path, cache key, and parameters.</returns>
    /// <exception cref="ArgumentException">If the URI format is invalid.</exception>
    public static EndpointUri Parse(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("Endpoint URI cannot be null or empty.", nameof(uri));

        // Split query string
        var queryIndex = uri.IndexOf('?');
        var basePart = queryIndex >= 0 ? uri[..queryIndex] : uri;
        var queryPart = queryIndex >= 0 ? uri[(queryIndex + 1)..] : null;

        // Parse scheme and path
        string scheme;
        string path;

        var doubleSlashIndex = basePart.IndexOf("://", StringComparison.Ordinal);
        if (doubleSlashIndex > 0)
        {
            // Standard format: scheme://path
            scheme = basePart[..doubleSlashIndex].ToLowerInvariant();
            path = basePart[(doubleSlashIndex + 3)..];
        }
        else
        {
            // Colon format: scheme:path (e.g., redis:GET:user:123)
            var colonIndex = basePart.IndexOf(':');
            if (colonIndex <= 0)
                throw new ArgumentException($"Invalid endpoint URI format: '{uri}'. Expected scheme://path or scheme:path.", nameof(uri));

            scheme = basePart[..colonIndex].ToLowerInvariant();
            path = basePart[(colonIndex + 1)..];
        }

        if (string.IsNullOrEmpty(scheme))
            throw new ArgumentException($"Empty scheme in URI: '{uri}'.", nameof(uri));

        // Parse query parameters
        var parameters = ParseQueryString(queryPart);

        // Normalize key: scheme://path?sorted_params (includes params for unique endpoint identity)
        var normalizedKey = parameters.Count == 0
            ? $"{scheme}://{path}"
            : $"{scheme}://{path}?{string.Join("&", parameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}={p.Value}"))}";

        return new EndpointUri(scheme, path, normalizedKey, parameters);
    }

    private static IReadOnlyDictionary<string, string> ParseQueryString(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return new Dictionary<string, string>(0);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = Uri.UnescapeDataString(pair[..eqIndex]);
                var value = Uri.UnescapeDataString(pair[(eqIndex + 1)..]);
                result[key] = value;
            }
            else if (pair.Length > 0)
            {
                // Flag parameter without value
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
        }

        return result;
    }
}
