using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// Ф0 skeleton tests: DSL URI building (no path truncation), scheme/TLS resolution, option binding and
/// validation, secret redaction, and component registration through a real RouteContext. No AS2 traffic —
/// send/receive arrive in later phases.
/// </summary>
public class As2SkeletonTests
{
    // ── DSL ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Receive_KeepsFullPath_NoFirstSegmentTruncation()
    {
        string uri = As2Dsl.Receive("/inbound/orders").Host("0.0.0.0").Port(4080).ConnectionFactory("walmart");

        uri.Should().Be("as2:/inbound/orders?host=0.0.0.0&port=4080&connectionFactory=walmart");
        // The path must survive parsing intact — the Http first-segment-drop bug must not recur.
        EndpointUriParser.Parse(uri).Path.Should().Be("/inbound/orders");
    }

    [Fact]
    public void Receive_AddsLeadingSlashWhenMissing()
    {
        string uri = As2Dsl.Receive("inbound").Port(4080);
        uri.Should().StartWith("as2:/inbound?");
    }

    [Fact]
    public void Send_MapsHttpsToAs2sScheme()
    {
        ((string)As2Dsl.Send("https://partner.example.com/as2")).Should().Be("as2s://partner.example.com/as2");
        ((string)As2Dsl.Send("http://partner.example.com/as2")).Should().Be("as2://partner.example.com/as2");
    }

    [Fact]
    public void Send_AppendsConnectionFactory()
    {
        ((string)As2Dsl.Send("https://p/as2").ConnectionFactory("walmart"))
            .Should().Be("as2s://p/as2?connectionFactory=walmart");
    }

    // ── Options: binding & validation ────────────────────────────────────────

    [Fact]
    public void Options_BindFromUri_MapsTypedValues()
    {
        var o = new As2EndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["port"] = "5443", ["sign"] = "false", ["mdnMode"] = "Async", ["asyncMdnUrl"] = "http://us/mdn",
        });

        o.Port.Should().Be(5443);
        o.Sign.Should().BeFalse();
        o.MdnMode.Should().Be(As2MdnMode.Async);
    }

    [Fact]
    public void Options_Validate_AcceptsDefaults()
    {
        var act = () => new As2EndpointOptions().Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Options_Validate_AsyncWithoutTarget_Throws()
    {
        var act = () => new As2EndpointOptions { MdnMode = As2MdnMode.Async }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_Validate_BadPort_Throws()
    {
        var act = () => new As2EndpointOptions { Port = 70000 }.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_UnsupportedSignAlg_Throws()
    {
        var act = () => new As2EndpointOptions { SignAlg = "md5" }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_Validate_UnsupportedEncryptionAlg_Throws()
    {
        var act = () => new As2EndpointOptions { EncryptAlg = "rc4" }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_Validate_SupportedMatrix_Ok()
    {
        var act = () => new As2EndpointOptions { SignAlg = "sha-512", EncryptAlg = "3des" }.Validate();
        act.Should().NotThrow();
    }

    // ── Secret redaction ─────────────────────────────────────────────────────

    [Fact]
    public void CertPassword_IsSensitive_RedactedInUri()
    {
        // Binding harvests [Sensitive] props → registers the key for URI sanitization.
        var o = new As2EndpointOptions();
        o.BindFromUri(new Dictionary<string, string> { ["certPassword"] = "s3cr3t" });
        o.CertPassword.Should().Be("s3cr3t");

        var masked = EndpointUri.Sanitize("as2s://p/as2?certPassword=s3cr3t");
        masked.Should().NotContain("s3cr3t");
        masked.Should().Contain(EndpointUri.Redacted);
    }

    // ── Component registration through RouteContext ──────────────────────────

    [Fact]
    public void Component_ResolvesAs2Scheme_ToAs2Endpoint()
    {
        using var ctx = new RouteContext();
        ctx.AddComponent(new As2Component());

        var endpoint = ctx.GetEndpoint(As2Dsl.Receive("/in").Host("127.0.0.1").Port(4080));

        endpoint.Should().BeOfType<As2Endpoint>();
    }

    [Fact]
    public void Component_ResolvesAs2sScheme_AndSetsTls()
    {
        using var ctx = new RouteContext();
        ctx.AddComponent(new As2Component());

        var endpoint = (As2Endpoint)ctx.GetEndpoint(As2Dsl.Send("https://partner/as2"));

        endpoint.EndpointOptions.UseTls.Should().BeTrue();
    }
}
