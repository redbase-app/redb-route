using System.Security.Cryptography;
using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Builds the id of a route that declared none with <c>RouteId("...")</c>: the endpoint's scheme,
/// its path and a deterministic UUID of the whole endpoint URI, joined with <c>-</c> — for example
/// <c>kafka-orders-1b4e28ba-2fa1-11d2-883f-0016d3cca427</c>.
/// <para>
/// The id is stable. The same endpoint yields the same id on every start, in every process and on
/// every node of a cluster, so metric series, stored checkpoints and control-bus commands keep
/// pointing at the same route across a restart. It holds no URI separator (a <c>/</c> or a <c>:</c>
/// breaks dashboard labels and metric tags) and no query string, so a secret in the URI cannot
/// surface in a log line; endpoints differing only in a parameter still get different ids, because
/// the parameters are hashed rather than shown.
/// </para>
/// <para>
/// The UUID is RFC 4122 version 5 (name-based, SHA-1) in the standard URL namespace over
/// <see cref="EndpointUri.NormalizedKey"/>, so anyone can recompute a route id from its URI with any
/// UUID library, without this code.
/// </para>
/// </summary>
public static class RouteIdFactory
{
    /// <summary>The RFC 4122 URL namespace, in which a route's UUID is computed.</summary>
    public static readonly Guid UrlNamespace = new("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>Longest readable part (scheme and path) kept in front of the UUID.</summary>
    private const int MaxLabelLength = 48;

    /// <summary>Builds the default id of a route consuming from <paramref name="uri"/>.</summary>
    /// <param name="uri">The parsed <c>From()</c> endpoint URI of the route.</param>
    public static string ForEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var label = Label(uri);
        var uuid = Uuid5(UrlNamespace, uri.NormalizedKey);
        return label.Length == 0 ? uuid.ToString() : $"{label}-{uuid}";
    }

    // The readable head: scheme and path, every other character folded into a single '-'. Non-ASCII
    // letters go the same way — a route id is read by dashboards and metric backends, not by humans
    // alone.
    private static string Label(EndpointUri uri)
    {
        var text = $"{uri.Scheme}-{StripUserInfo(uri.Path)}";
        var sb = new StringBuilder(MaxLabelLength);
        foreach (var c in text)
        {
            if (sb.Length >= MaxLabelLength) break;
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }

    // "user:password@host:5672/orders" -> "host:5672/orders". Credentials are not part of a name,
    // and the '@' form hides them from Sanitize's query-parameter masking.
    private static string StripUserInfo(string path)
    {
        var slash = path.IndexOf('/');
        var authority = slash < 0 ? path : path[..slash];
        var at = authority.LastIndexOf('@');
        return at < 0 ? path : path[(at + 1)..];
    }

    /// <summary>
    /// RFC 4122 version 5 UUID: the SHA-1 of the namespace followed by the name, with the version
    /// and variant bits set. SHA-1 is what the standard prescribes here; this is a name, not a
    /// signature.
    /// </summary>
    internal static Guid Uuid5(Guid ns, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        ns.TryWriteBytes(input.AsSpan(0, 16), bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);   // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(hash[..16], bigEndian: true);
    }
}
