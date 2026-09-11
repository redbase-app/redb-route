using System.Net;

namespace redb.Route.Http;

/// <summary>
/// Outcome of resolving the forwarded headers of one request.
/// </summary>
/// <param name="ClientAddress">The address to treat as the client. Equal to the socket peer when
/// nothing applied.</param>
/// <param name="Scheme">The scheme the client used (<c>http</c> / <c>https</c>), or null when the
/// header was absent, unreadable, or the peer is not trusted.</param>
/// <param name="AddressApplied">True when <see cref="ClientAddress"/> came out of
/// <c>X-Forwarded-For</c> rather than the socket.</param>
/// <param name="TrustedHops">How many trusted proxy entries were skipped while walking the chain.</param>
public readonly record struct ForwardedResolution(
    IPAddress? ClientAddress,
    string? Scheme,
    bool AddressApplied,
    int TrustedHops);

/// <summary>
/// Turns <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> into a client address and scheme,
/// given the socket peer and the set of proxies the host trusts. Pure: no <c>HttpContext</c>, no
/// I/O, so it is unit-testable on strings and survives a change of HTTP engine unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Every proxy in a chain appends the address it accepted the connection from, so a client's own
/// entries always sit to the left of the entry the first trusted proxy wrote. The chain is
/// therefore walked from the right: trusted entries are skipped, and the first entry that is not
/// a trusted proxy is the client. A header from a peer that is not itself trusted is ignored
/// outright, because nothing in it was written by anyone we trust.
/// </para>
/// <para>
/// An entry that does not parse as an address stops the walk and the socket peer is kept. It
/// must not be skipped: skipping would carry the walk past the last trusted hop into the part of
/// the header the client controls.
/// </para>
/// <para>
/// <c>X-Forwarded-Proto</c> is read in step with the address: with <c>k</c> trusted hops skipped,
/// the scheme is the entry <c>k</c> positions from the right, which is the scheme observed by the
/// proxy that talked to the client. A single value is used as is.
/// </para>
/// <para>
/// Not handled, deliberately: the RFC 7239 <c>Forwarded</c> header, and <c>X-Forwarded-Host</c>.
/// Rewriting the host changes what redirects and absolute URLs point at and needs an allow-list
/// of its own; it is a separate decision.
/// </para>
/// </remarks>
public static class ForwardedHeaderResolver
{
    /// <summary>Header name for the client address chain.</summary>
    public const string ForwardedFor = "X-Forwarded-For";

    /// <summary>Header name for the client scheme.</summary>
    public const string ForwardedProto = "X-Forwarded-Proto";

    /// <summary>
    /// Resolves the client address and scheme for one request.
    /// </summary>
    /// <param name="socketPeer">The address the connection was accepted from. Null yields an
    /// untouched result.</param>
    /// <param name="forwardedFor">Raw <c>X-Forwarded-For</c> value, comma separated, or null.</param>
    /// <param name="forwardedProto">Raw <c>X-Forwarded-Proto</c> value, or null.</param>
    /// <param name="trust">The proxies the host trusts.</param>
    public static ForwardedResolution Resolve(
        IPAddress? socketPeer,
        string? forwardedFor,
        string? forwardedProto,
        TrustedProxyOptions trust)
    {
        ArgumentNullException.ThrowIfNull(trust);

        if (socketPeer is null || !trust.IsEnabled || !trust.IsTrusted(socketPeer))
            return new ForwardedResolution(socketPeer, null, false, 0);

        var client = socketPeer;
        var applied = false;
        var trustedHops = 0;

        var chain = SplitChain(forwardedFor);
        if (chain.Count > 0)
        {
            var found = false;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (!TryParseHop(chain[i], out var hop))
                {
                    // Unreadable entry: stop here and keep the socket peer. Walking on would reach
                    // the client-controlled part of the header.
                    return new ForwardedResolution(socketPeer, null, false, trustedHops);
                }

                if (trust.IsTrusted(hop))
                {
                    trustedHops++;
                    continue;
                }

                client = hop;
                applied = true;
                found = true;
                break;
            }

            // Every entry was a trusted proxy (a proxy calling through itself, a health check):
            // the socket peer stays, and the scheme below still applies.
            if (!found)
                client = socketPeer;
        }

        var scheme = ResolveScheme(forwardedProto, trustedHops);
        return new ForwardedResolution(client, scheme, applied, trustedHops);
    }

    private static string? ResolveScheme(string? forwardedProto, int trustedHops)
    {
        var values = SplitChain(forwardedProto);
        if (values.Count == 0)
            return null;

        var index = Math.Max(0, values.Count - 1 - trustedHops);
        var value = values[index];

        if (value.Equals("https", StringComparison.OrdinalIgnoreCase)) return "https";
        if (value.Equals("http", StringComparison.OrdinalIgnoreCase)) return "http";
        return null;
    }

    private static List<string> SplitChain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Parses one chain entry, tolerating the forms proxies actually emit: a bare IPv4 or IPv6
    /// address, <c>IPv4:port</c>, and <c>[IPv6]:port</c>. IPv4-mapped IPv6 is folded to IPv4 so
    /// it compares with a list written in IPv4.
    /// </summary>
    internal static bool TryParseHop(string token, out IPAddress address)
    {
        address = IPAddress.None;
        var value = token;

        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0) return false;
            value = value.Substring(1, end - 1);
        }
        else if (value.Count(c => c == ':') == 1)
        {
            // IPv4:port. A bare IPv6 address carries several colons and is left alone.
            value = value[..value.IndexOf(':')];
        }

        if (!IPAddress.TryParse(value, out var parsed))
            return false;

        address = TrustedProxyOptions.Normalize(parsed);
        return true;
    }
}
