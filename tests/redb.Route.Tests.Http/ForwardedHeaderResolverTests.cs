using System.Net;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// Pure tests for <see cref="ForwardedHeaderResolver"/>: the trust model on strings, with no
/// Kestrel involved. The host-level behaviour (the resolved address and scheme reaching the
/// exchange) is covered by <see cref="TrustedProxyHostingTests"/>.
/// </summary>
public class ForwardedHeaderResolverTests
{
    private static readonly IPAddress Peer = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");

    private static TrustedProxyOptions Trust(params string[] entries)
    {
        var t = new TrustedProxyOptions();
        foreach (var e in entries) t.Add(e);
        return t;
    }

    // ── Trust gate ──

    [Fact]
    public void NoTrustedProxies_HeaderIgnored_PeerIsClient()
    {
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7", "https", new TrustedProxyOptions());

        r.ClientAddress.Should().Be(Peer);
        r.AddressApplied.Should().BeFalse();
        r.Scheme.Should().BeNull();
    }

    [Fact]
    public void UntrustedPeer_HeaderIgnored_EvenWithTrustListPresent()
    {
        // The peer is not in the list: nothing in the header was written by a proxy we trust.
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7", "https", Trust("192.168.1.1"));

        r.ClientAddress.Should().Be(Peer);
        r.AddressApplied.Should().BeFalse();
        r.Scheme.Should().BeNull();
    }

    [Fact]
    public void NullPeer_NothingApplies()
    {
        var r = ForwardedHeaderResolver.Resolve(null, "203.0.113.7", "https", Trust("10.0.0.1"));

        r.ClientAddress.Should().BeNull();
        r.AddressApplied.Should().BeFalse();
    }

    // ── Walking the chain ──

    [Fact]
    public void TrustedPeer_SingleHop_RightmostIsClient()
    {
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7", null, Trust("10.0.0.1"));

        r.ClientAddress.Should().Be(Client);
        r.AddressApplied.Should().BeTrue();
        r.TrustedHops.Should().Be(0);
    }

    [Fact]
    public void TrustedPeer_TwoTrustedHops_SkipsProxyAndFindsClient()
    {
        // client -> edge (10.0.0.9) -> nginx (10.0.0.1, the socket peer) -> us.
        // nginx appended the edge's address, so the right-most entry is a proxy, not the client.
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7, 10.0.0.9", null, Trust("10.0.0.1", "10.0.0.9"));

        r.ClientAddress.Should().Be(Client);
        r.AddressApplied.Should().BeTrue();
        r.TrustedHops.Should().Be(1);
    }

    [Fact]
    public void TrustedPeer_ClientSpoofsTrustedAddressOnTheLeft_DoesNotWin()
    {
        // The client sent "1.2.3.4, 10.0.0.9" itself; the edge appended the client's real address.
        // Walking from the right meets the real client before any client-supplied entry.
        var r = ForwardedHeaderResolver.Resolve(Peer, "1.2.3.4, 10.0.0.9, 203.0.113.7", null, Trust("10.0.0.1", "10.0.0.9"));

        r.ClientAddress.Should().Be(Client);
    }

    [Fact]
    public void TrustedPeer_ChainEntirelyTrusted_KeepsSocketPeer()
    {
        var r = ForwardedHeaderResolver.Resolve(Peer, "10.0.0.9", "https", Trust("10.0.0.1", "10.0.0.9"));

        r.ClientAddress.Should().Be(Peer);
        r.AddressApplied.Should().BeFalse();
        r.Scheme.Should().Be("https", "the scheme still comes from a trusted proxy");
    }

    [Fact]
    public void TrustedPeer_UnparseableHop_StopsAndKeepsSocketPeer()
    {
        // "unknown" is a legal RFC 7239 identifier a proxy may emit. It must end the walk, not be
        // skipped: skipping would reach "203.0.113.7", which here is client-supplied.
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7, unknown", "https", Trust("10.0.0.1"));

        r.ClientAddress.Should().Be(Peer);
        r.AddressApplied.Should().BeFalse();
        r.Scheme.Should().BeNull("a malformed chain is not trusted for anything");
    }

    [Fact]
    public void TrustedPeer_EmptyHeader_KeepsSocketPeer()
    {
        var r = ForwardedHeaderResolver.Resolve(Peer, "   ", null, Trust("10.0.0.1"));

        r.ClientAddress.Should().Be(Peer);
        r.AddressApplied.Should().BeFalse();
    }

    // ── Address forms ──

    [Theory]
    [InlineData("203.0.113.7:51234", "203.0.113.7")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    public void Hop_Forms_ParseToBareAddress(string hop, string expected)
    {
        var r = ForwardedHeaderResolver.Resolve(Peer, hop, null, Trust("10.0.0.1"));

        r.ClientAddress.Should().Be(IPAddress.Parse(expected));
        r.AddressApplied.Should().BeTrue();
    }

    [Fact]
    public void Ipv4MappedPeer_MatchesIpv4TrustEntry()
    {
        // A dual-stack socket reports an IPv4 peer as ::ffff:a.b.c.d.
        var mapped = IPAddress.Parse("::ffff:10.0.0.1");
        var r = ForwardedHeaderResolver.Resolve(mapped, "203.0.113.7", null, Trust("10.0.0.1"));

        r.ClientAddress.Should().Be(Client);
        r.AddressApplied.Should().BeTrue();
    }

    [Fact]
    public void KnownNetworks_CidrMatchesPeerAndHops()
    {
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7, 10.0.5.5", null, Trust("10.0.0.0/8"));

        r.ClientAddress.Should().Be(Client);
        r.TrustedHops.Should().Be(1);
    }

    // ── Scheme ──

    [Fact]
    public void Scheme_SingleValue_UsedAsIs()
    {
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7", "HTTPS", Trust("10.0.0.1"));

        r.Scheme.Should().Be("https");
    }

    [Fact]
    public void Scheme_ReadInStepWithTrustedHops()
    {
        // Edge terminated TLS and wrote "https"; nginx wrote its own inbound "http". With one
        // trusted hop skipped, the scheme one position from the right is the edge's.
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7, 10.0.0.9", "https, http", Trust("10.0.0.1", "10.0.0.9"));

        r.Scheme.Should().Be("https");
    }

    [Fact]
    public void Scheme_UnknownValue_Ignored()
    {
        var r = ForwardedHeaderResolver.Resolve(Peer, "203.0.113.7", "gopher", Trust("10.0.0.1"));

        r.Scheme.Should().BeNull();
        r.ClientAddress.Should().Be(Client, "an odd scheme does not invalidate the address");
    }

    // ── Options ──

    [Fact]
    public void Add_RejectsGarbage()
    {
        var t = new TrustedProxyOptions();

        var act = () => t.Add("not-an-address");

        act.Should().Throw<FormatException>();
        t.IsEnabled.Should().BeFalse();
    }
}
