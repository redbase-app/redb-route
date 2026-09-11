using System.Net;

namespace redb.Route.Http;

/// <summary>
/// The set of reverse proxies the shared Kestrel host trusts to speak for the client.
/// </summary>
/// <remarks>
/// <para>
/// A proxy chain is a property of the process, not of a module or a route: two routes on one
/// port never sit behind different proxies. So the list lives on the host, next to TLS and the
/// protocol set, and every HTTP-based transport on that host (Http, Soap, As2, Grpc, the Tsak
/// management API) sees the resolved client address without doing anything itself.
/// </para>
/// <para>
/// Secure by default: with no entries the host ignores <c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c> entirely and the socket peer is the client. Add exactly the addresses
/// or networks of the proxies you operate. A list that is too wide (a whole cloud range where an
/// attacker can rent a machine) hands that attacker the right to choose its own client address.
/// </para>
/// </remarks>
public sealed class TrustedProxyOptions
{
    /// <summary>Individual addresses of trusted reverse proxies.</summary>
    public List<IPAddress> KnownProxies { get; } = new();

    /// <summary>CIDR networks of trusted reverse proxies.</summary>
    public List<IPNetwork> KnownNetworks { get; } = new();

    /// <summary>
    /// Rewrite the request scheme from <c>X-Forwarded-Proto</c> when the peer is trusted. On by
    /// default: behind a TLS-terminating proxy the URL a route sees (<c>redbHttp.Url</c>) must be
    /// the one the client used, or anything that compares against it (DPoP <c>htu</c>, SCIM
    /// <c>Location</c>, redirect URIs) compares against the wrong origin.
    /// </summary>
    public bool ForwardScheme { get; set; } = true;

    /// <summary>True when at least one proxy or network is listed.</summary>
    public bool IsEnabled => KnownProxies.Count > 0 || KnownNetworks.Count > 0;

    /// <summary>
    /// Adds a trusted proxy given as a bare address (<c>10.0.0.5</c>, <c>::1</c>) or a CIDR
    /// network (<c>10.0.0.0/8</c>). The form is decided by the presence of a slash.
    /// </summary>
    /// <exception cref="FormatException">The value is neither an address nor a CIDR network.</exception>
    public TrustedProxyOptions Add(string addressOrNetwork)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressOrNetwork);
        var value = addressOrNetwork.Trim();

        if (value.Contains('/'))
        {
            if (!IPNetwork.TryParse(value, out var network))
                throw new FormatException($"'{value}' is not a valid CIDR network.");
            KnownNetworks.Add(network);
            return this;
        }

        if (!IPAddress.TryParse(value, out var address))
            throw new FormatException($"'{value}' is not a valid IP address.");
        KnownProxies.Add(Normalize(address));
        return this;
    }

    /// <summary>
    /// Whether <paramref name="address"/> belongs to a trusted proxy. IPv4-mapped IPv6 addresses
    /// (<c>::ffff:10.0.0.5</c>, which is how a dual-stack socket reports an IPv4 peer) compare as
    /// their IPv4 form, so a list written in IPv4 matches either way.
    /// </summary>
    public bool IsTrusted(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var ip = Normalize(address);

        foreach (var known in KnownProxies)
        {
            if (known.Equals(ip)) return true;
        }

        foreach (var network in KnownNetworks)
        {
            if (network.Contains(ip)) return true;
        }

        return false;
    }

    /// <summary>
    /// Folds an IPv4-mapped IPv6 address to its IPv4 form; any other address is returned as is.
    /// Public so a caller building the list from <see cref="IPAddress"/> values, rather than
    /// through <see cref="Add"/>, can store them in the same form <see cref="IsTrusted"/> compares.
    /// </summary>
    public static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
