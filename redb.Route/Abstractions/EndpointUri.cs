using System.Text;
using System.Text.RegularExpressions;
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
    /// <summary>Constant emitted in place of a redacted secret value.</summary>
    public const string Redacted = "****";

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

    // --- Secret detection ------------------------------------------------

    // Substring keywords (case-insensitive). A parameter name is treated as a
    // secret if it CONTAINS any of these. Deliberately avoids bare "key"/"pass"
    // so benign params (routingKey, partitionKey, ...) are never over-masked;
    // "password" already covers bindPassword/sslKeyPassword/proxyPassword/etc.
    private static readonly string[] SensitiveKeywords =
    {
        "password", "passphrase", "pwd", "secret", "token", "apikey",
        "accesskey", "credential", "connectionstring", "privatekey",
        "authorization", "bearer", "sasljaasconfig"
    };

    // Connector-registered extra names (copy-on-write; read via a stable snapshot).
    private static volatile HashSet<string> _extraSensitiveKeys =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers additional query-parameter names that must be masked in logs and
    /// telemetry (analogous to Camel's <c>addSanitizeKeywords</c>). Case-insensitive.
    /// Intended to be called once at startup by connector registration.
    /// </summary>
    public static void AddSensitiveKeys(params string[] keys)
    {
        if (keys is null || keys.Length == 0) return;
        var copy = new HashSet<string>(_extraSensitiveKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
            if (!string.IsNullOrWhiteSpace(k))
                copy.Add(k.Trim());
        _extraSensitiveKeys = copy;
    }

    /// <summary>Returns <c>true</c> if a query-parameter name should be masked.</summary>
    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (_extraSensitiveKeys.Contains(key)) return true;
        foreach (var kw in SensitiveKeywords)
            if (key.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Masks the password segment of a "user:password@host..." authority/path.
    // Anchored at start so only the leading userinfo (authority position) matches;
    // a bare "host:port/seg@x" (no ':' before '@' within the authority) is left as-is.
    private static readonly Regex PathUserInfoPassword =
        new(@"^([^/@:]*:)([^/@]*)@", RegexOptions.Compiled);

    /// <summary>
    /// Masks the password in userinfo of a scheme-less path/authority
    /// (<c>user:password@host</c> → <c>user:****@host</c>). Returns the input
    /// unchanged when there is no userinfo password.
    /// </summary>
    public static string MaskUserInfoPassword(string pathOrAuthority)
    {
        if (string.IsNullOrEmpty(pathOrAuthority) || pathOrAuthority.IndexOf('@') < 0)
            return pathOrAuthority;
        return PathUserInfoPassword.Replace(pathOrAuthority, m => m.Groups[1].Value + Redacted + "@");
    }

    /// <summary>
    /// Format-preserving redaction of a raw endpoint URI string. Keeps scheme,
    /// separator (<c>://</c> vs <c>:</c>), path, parameter names, order, and every
    /// non-secret value byte-for-byte; replaces sensitive query-parameter values and
    /// the userinfo password with <see cref="Redacted"/>. Never throws.
    /// Use this at every logging / telemetry / DTO boundary that would otherwise emit
    /// a raw URI (Camel <c>URISupport.sanitizeUri</c> equivalent).
    /// </summary>
    public static string Sanitize(string? rawUri)
    {
        if (string.IsNullOrEmpty(rawUri)) return rawUri ?? string.Empty;

        var queryIndex = rawUri.IndexOf('?');
        var basePart = queryIndex >= 0 ? rawUri[..queryIndex] : rawUri;
        var queryPart = queryIndex >= 0 ? rawUri[(queryIndex + 1)..] : null;

        // Mask userinfo password in the authority (only for scheme://authority form).
        var doubleSlash = basePart.IndexOf("://", StringComparison.Ordinal);
        if (doubleSlash > 0)
        {
            var head = basePart[..(doubleSlash + 3)];
            var rest = basePart[(doubleSlash + 3)..];
            basePart = head + MaskUserInfoPassword(rest);
        }

        if (queryPart is null)
            return basePart;

        var pairs = queryPart.Split('&');
        for (var i = 0; i < pairs.Length; i++)
        {
            var pair = pairs[i];
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var rawKey = pair[..eq];
            // Key may be URL-encoded; decode only for the sensitivity test.
            var key = Uri.UnescapeDataString(rawKey);
            if (IsSensitiveKey(key))
                pairs[i] = rawKey + "=" + Redacted;
        }

        return basePart + "?" + string.Join("&", pairs);
    }

    /// <summary>
    /// Redacts <c>key=value</c> secret assignments inside arbitrary text — connection strings
    /// (<c>...;Password=p;...</c>), query fragments, and driver exception messages that may embed
    /// either. Only the value of a key considered sensitive by <see cref="IsSensitiveKey"/> is
    /// replaced; everything else is preserved verbatim. Never throws.
    /// <para>
    /// Use this as the backstop for text that is not a parseable endpoint URI. Note that logging an
    /// <see cref="Exception"/> object itself cannot be scrubbed in-process (the formatter renders
    /// <c>ToString()</c>); wire this into a logging-sink filter for that case.
    /// </para>
    /// </summary>
    public static string RedactSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('=') < 0)
            return text ?? string.Empty;

        var sb = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            // The key is the run of identifier-ish characters immediately before '='.
            var keyStart = eq;
            while (keyStart > i && IsKeyChar(text[keyStart - 1]))
                keyStart--;
            var key = text[keyStart..eq];

            // The value runs to the next separator (connection-string ';', query '&', quote, space).
            var valueStart = eq + 1;
            var valueEnd = valueStart;
            while (valueEnd < text.Length && !IsValueDelimiter(text[valueEnd]))
                valueEnd++;

            sb.Append(text, i, eq - i + 1);          // everything up to and including '='

            if (valueEnd > valueStart && key.Length > 0 && IsSensitiveKey(key))
                sb.Append(Redacted);
            else
                sb.Append(text, valueStart, valueEnd - valueStart);

            i = valueEnd;
        }

        return sb.ToString();
    }

    private static bool IsKeyChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-';

    private static bool IsValueDelimiter(char c) =>
        c is ';' or '&' or '"' or '\'' || char.IsWhiteSpace(c);

    /// <summary>
    /// Reconstructs the URI with sensitive parameters and userinfo passwords masked.
    /// Secret values are fully redacted to <see cref="Redacted"/> (no partial disclosure).
    /// Example: <c>rabbitmq:my-queue?host=localhost&amp;username=admin&amp;password=****</c>
    /// </summary>
    public string ToMaskedUriString()
    {
        var maskedPath = MaskUserInfoPassword(Path);

        if (RawParameters.Count == 0)
            return $"{Scheme}:{maskedPath}";

        var sb = new StringBuilder();
        sb.Append(Scheme);
        sb.Append(':');
        sb.Append(maskedPath);

        var sep = '?';
        foreach (var (key, value) in RawParameters)
        {
            sb.Append(sep);
            sb.Append(HttpUtility.UrlEncode(key));
            sb.Append('=');
            sb.Append(IsSensitiveKey(key) ? Redacted : HttpUtility.UrlEncode(value));
            sep = '&';
        }

        return sb.ToString();
    }
}
