using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using redb.Route.Abstractions;
using redb.Route.As4;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using As4Dsl = redb.Route.As4.Fluent.As4;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф0 skeleton tests: DSL URIs, option binding and validation, secret redaction, scheme registration, and
/// resolution of the node and its partners from the registry when an endpoint starts. No AS4 traffic yet.
/// </summary>
public class As4SkeletonTests
{
    // ── DSL ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Receive_KeepsFullPath()
    {
        string uri = As4Dsl.Receive("/as4/in").Host("0.0.0.0").Port(4090).ConnectionFactory("node");

        uri.Should().Be("as4:/as4/in?host=0.0.0.0&port=4090&connectionFactory=node");
        EndpointUriParser.Parse(uri).Path.Should().Be("/as4/in");
    }

    [Fact]
    public void Receive_WithTls_UsesAs4sScheme()
    {
        ((string)As4Dsl.Receive("as4/in").Tls()).Should().StartWith("as4s:/as4/in");
    }

    [Fact]
    public void Send_ToAnAddressWithAQuery_IsRefused_WithTheReason()
    {
        // The query of an as4: URI holds the endpoint's options (as http:'s does); a partner address with its own query
        // would otherwise fail later as "'tenant' is not an option", which reads as the author's typo.
        var act = () => As4Dsl.Send("https://ap.partner/as4?tenant=7").Build();

        act.Should().Throw<ArgumentException>().WithMessage("*query*options*");
    }

    [Fact]
    public void Send_MapsHttpsToAs4s_AndKeepsExpressions()
    {
        string uri = As4Dsl.Send("https://ap.acme.example/as4").ConnectionFactory("node").Partner("${header.partner}");

        uri.Should().Be("as4s://ap.acme.example/as4?connectionFactory=node&partner=" + Uri.EscapeDataString("${header.partner}"));
        ((string)As4Dsl.Send("http://ap/as4")).Should().Be("as4://ap/as4");
    }

    [Fact]
    public void Builder_LastValueOfAnOptionWins()
    {
        ((string)As4Dsl.Send("https://ap/as4").Partner("a").Partner("b")).Should().Be("as4s://ap/as4?partner=b");
    }

    // ── Options ──────────────────────────────────────────────────────────────

    [Fact]
    public void Options_BindFromUri_MapsTypedAndDynamicValues()
    {
        var o = new As4EndpointOptions();
        o.BindFromUri(new Dictionary<string, string>
        {
            ["connectionFactory"] = "node", ["partner"] = "acme", ["action"] = "${header.docType}",
            ["port"] = "5443", ["maxEnvelopeCharacters"] = "2048", ["clientCertificateMode"] = "RequireCertificate",
        });

        o.ConnectionFactory.Should().Be("node");
        o.Partner!.Value.IsDynamic.Should().BeFalse();
        o.Action!.Value.IsDynamic.Should().BeTrue();
        o.Port.Should().Be(5443);
        o.MaxEnvelopeCharacters.Should().Be(2048);
        o.ClientCertificateMode.Should().Be(As4ClientCertificateMode.RequireCertificate);
    }

    [Fact]
    public void Options_Validate_RequiresConnectionFactory()
    {
        var act = () => new As4EndpointOptions().Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*connectionFactory*");
    }

    [Fact]
    public void Options_Validate_AcceptsMinimal()
    {
        var act = () => new As4EndpointOptions { ConnectionFactory = "node" }.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Options_Validate_BadPort_Throws(int port)
    {
        var act = () => new As4EndpointOptions { ConnectionFactory = "node", Port = port }.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_Validate_ClientCertificateWithoutTls_Throws()
    {
        var act = () => new As4EndpointOptions
        {
            ConnectionFactory = "node", ClientCertificateMode = As4ClientCertificateMode.RequireCertificate,
        }.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*TLS*");
    }

    [Fact]
    public void Options_Validate_NonPositiveEnvelopeBound_Throws()
    {
        var act = () => new As4EndpointOptions { ConnectionFactory = "node", MaxEnvelopeCharacters = 0 }.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SslCertPassword_IsRedactedInUri()
    {
        new As4EndpointOptions().BindFromUri(new Dictionary<string, string> { ["sslCertPassword"] = "s3cr3t" });

        var masked = EndpointUri.Sanitize("as4s:/as4/in?connectionFactory=node&sslCertPassword=s3cr3t");
        masked.Should().NotContain("s3cr3t").And.Contain(EndpointUri.Redacted);
    }

    // ── Component ────────────────────────────────────────────────────────────

    [Fact]
    public void Component_ResolvesAs4Scheme()
    {
        using var ctx = new RouteContext();
        ctx.AddComponent(new As4Component());

        ctx.GetEndpoint(As4Dsl.Receive("/as4/in").ConnectionFactory("node")).Should().BeOfType<As4Endpoint>();
    }

    [Fact]
    public void Component_As4s_SetsTls_AndRebuildsPartnerUrl()
    {
        using var ctx = new RouteContext();
        ctx.AddComponent(new As4Component());

        var endpoint = (As4Endpoint)ctx.GetEndpoint(As4Dsl.Send("https://ap.acme.example:8443/as4").ConnectionFactory("node"));

        endpoint.EndpointOptions.UseTls.Should().BeTrue();
        endpoint.PartnerUrl.Should().Be("https://ap.acme.example:8443/as4");
    }

    [Fact]
    public void Component_ReceiveUri_HasNoPartnerUrl()
    {
        using var ctx = new RouteContext();
        ctx.AddComponent(new As4Component());

        ((As4Endpoint)ctx.GetEndpoint(As4Dsl.Receive("/as4/in").ConnectionFactory("node"))).PartnerUrl.Should().BeNull();
    }

    // ── Node resolution ──────────────────────────────────────────────────────

    [Fact]
    public void Node_Resolves_FactoryAndPartners()
    {
        using var ctx = NewContext();

        var node = As4Node.Resolve(ctx, "node");

        node.Partners.Keys.Should().BeEquivalentTo("acme");
    }

    [Fact]
    public void Node_UnknownFactory_NamesIt()
    {
        using var ctx = new RouteContext();

        var act = () => As4Node.Resolve(ctx, "missing");
        act.Should().Throw<Exception>().WithMessage("*missing*");
    }

    [Fact]
    public void Node_SigningCertificateWithoutPrivateKey_Throws()
    {
        using var ctx = NewContext(factory: f => f.SigningCertificate = PublicOnly(OurCertificate));

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*SigningCertificate*private key*");
    }

    [Fact]
    public void Node_WithoutPartners_Throws()
    {
        using var ctx = NewContext(factory: f => f.Partners.Clear());

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Partners is empty*");
    }

    [Fact]
    public void Node_PartnerWithoutName_Throws()
    {
        using var ctx = NewContext(partner: p => p.Name = null);

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*no Name*acme*");
    }

    [Fact]
    public void Node_RepeatedPartnerName_Throws()
    {
        using var ctx = NewContext(factory: f => f.Partners.Add(NewPartner(p => p.PartyId = "other")));

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*names repeat*acme*");
    }

    [Fact]
    public void Node_DecryptionCertificateWithoutPrivateKey_Throws()
    {
        using var ctx = NewContext(factory: f => f.DecryptionCertificates.Add(PartnerCertificate));

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*DecryptionCertificates*private key*");
    }

    [Fact]
    public void Node_PartnerWithoutSigningCertificate_Throws()
    {
        using var ctx = NewContext(partner: p => p.PartnerSigningCertificates.Clear());

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*'acme'*PartnerSigningCertificates*");
    }

    [Fact]
    public void Node_PartnerWithoutEncryptionCertificate_Throws()
    {
        using var ctx = NewContext(partner: p => p.PartnerEncryptionCertificate = null);

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*'acme'*PartnerEncryptionCertificate*");
    }

    [Fact]
    public void Node_UnsupportedAlgorithm_Throws()
    {
        using var ctx = NewContext(partner: p => p.DataEncryptionAlgorithm = "http://www.w3.org/2001/04/xmlenc#aes128-cbc");

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*data encryption*aes128-cbc*");
    }

    [Fact]
    public void Node_ReplyLegWithoutAction_Throws()
    {
        using var ctx = NewContext(partner: p => p.ReplyLeg = new As4Leg { Service = "svc" });

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*reply leg*Action*");
    }

    [Fact]
    public void Node_TwoPartnersIndistinguishableOnReceipt_Throws()
    {
        using var ctx = NewContext(factory: f => f.Partners.Add(NewPartner(p => p.Name = "acme2")));

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().Throw<InvalidOperationException>().WithMessage("*'acme'*'acme2'*could not be matched*");
    }

    [Fact]
    public void Node_OneAgreementForManyPartners_IsFine()
    {
        using var ctx = NewContext(
            factory: f => f.Partners.Add(NewPartner(p => { p.Name = "globex"; p.PartyId = "globex"; p.AgreementRef = "urn:agreement"; })),
            partner: p => p.AgreementRef = "urn:agreement");

        var act = () => As4Node.Resolve(ctx, "node");
        act.Should().NotThrow();
    }

    // ── Start-time checks ────────────────────────────────────────────────────

    [Fact]
    public async Task Producer_Start_ResolvesNode()
    {
        using var ctx = NewContext();
        var producer = SendProducer(ctx, "partner=acme");

        await producer.Start();

        producer.IsStarted.Should().BeTrue();
        producer.Node!.Partners.Should().ContainKey("acme");
    }

    [Fact]
    public async Task Producer_Start_UnknownStaticPartner_Throws()
    {
        using var ctx = NewContext();
        var producer = SendProducer(ctx, "partner=globex");

        var act = () => producer.Start();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*globex*not a partner*");
    }

    [Fact]
    public async Task Producer_Start_DynamicPartner_IsCheckedPerMessage()
    {
        using var ctx = NewContext();
        var producer = SendProducer(ctx, "partner=" + Uri.EscapeDataString("${header.partner}"));

        await producer.Start();

        producer.IsStarted.Should().BeTrue();
    }

    [Fact]
    public async Task Producer_Start_WithoutPartner_Throws()
    {
        using var ctx = NewContext();
        var producer = SendProducer(ctx, null);

        var act = () => producer.Start();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*partner*");
    }

    [Fact]
    public void Options_Validate_Transacted_Throws()
    {
        var act = () => new As4EndpointOptions { ConnectionFactory = "node", Transacted = true }.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*transacted*receipt*");
    }

    [Fact]
    public void Endpoint_WithAMisspeltOption_IsRefused_NamingTheNearestOne()
    {
        using var ctx = NewContext();
        var act = () => ctx.GetEndpoint("as4:/as4/in?connectionFactory=node&idempotentRepositry=dedup");
        act.Should().Throw<ArgumentException>().WithMessage("*'idempotentRepositry' is not an option*Did you mean 'idempotentRepository'?*");
    }

    [Fact]
    public void Endpoint_WithAnUnconvertibleValue_IsRefused()
    {
        using var ctx = NewContext();
        var act = () => ctx.GetEndpoint("as4:/as4/in?connectionFactory=node&port=http");
        act.Should().Throw<ArgumentException>().WithMessage("*port*");
    }

    [Fact]
    public void Endpoint_WithTransacted_IsRefusedWhenTheUriIsBound()
    {
        using var ctx = NewContext();
        var act = () => ctx.GetEndpoint("as4://partner.example/as4?connectionFactory=node&partner=acme&transacted=true");
        act.Should().Throw<ArgumentException>().WithMessage("*transacted*");
    }

    [Fact]
    public void Consumer_WithPartner_IsRefused_WhenCreated()
    {
        using var ctx = NewContext();
        var act = () => ctx.GetEndpoint("as4:/as4/in?connectionFactory=node&partner=acme&idempotentRepository=dedup").CreateConsumer(Substitute.For<IProcessor>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*partner*");
    }

    [Theory]
    [InlineData("timeout=5000")]
    [InlineData("service=urn%3Ax")]
    [InlineData("maxResponseBodySize=1024")]
    public void Consumer_WithASendOnlyOption_IsRefused_NamingIt(string option)
    {
        // [EndpointRole], as http: declares it: an option the other side reads would bind and do nothing.
        using var ctx = NewContext();
        var act = () => ctx.GetEndpoint($"as4:/as4/in?connectionFactory=node&idempotentRepository=dedup&{option}").CreateConsumer(Substitute.For<IProcessor>());

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{option[..option.IndexOf('=')]}*");
    }

    [Theory]
    [InlineData("idempotentRepository=dedup")]
    [InlineData("streamBody=true")]
    [InlineData("port=4091")]
    [InlineData("maxConcurrentRequests=2")]
    public void Producer_WithAReceiveOnlyOption_IsRefused_NamingIt(string option)
    {
        using var ctx = NewContext();
        var act = () => ctx.GetEndpoint($"as4://partner.example/as4?connectionFactory=node&partner=acme&{option}").CreateProducer();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{option[..option.IndexOf('=')]}*");
    }

    [Fact]
    public async Task Consumer_Start_UnknownIdempotentRepository_Throws()
    {
        using var ctx = NewContext();
        var consumer = ctx.GetEndpoint("as4:/as4/in?connectionFactory=node&idempotentRepository=missing-store").CreateConsumer(Substitute.For<IProcessor>());

        var act = () => consumer.Start();
        await act.Should().ThrowAsync<Exception>().WithMessage("*missing-store*");
    }

    [Fact]
    public async Task Consumer_Start_WithoutIdempotentRepository_Throws()
    {
        using var ctx = NewContext();
        var consumer = ctx.GetEndpoint("as4:/as4/in?connectionFactory=node").CreateConsumer(Substitute.For<IProcessor>());

        var act = () => consumer.Start();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*idempotentRepository*");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static readonly X509Certificate2 OurCertificate = SelfSigned("CN=redb-as4-test");
    private static readonly X509Certificate2 PartnerCertificate = PublicOnly(SelfSigned("CN=acme-as4-test"));

    private static RouteContext NewContext(Action<As4ConnectionFactory>? factory = null, Action<As4Partner>? partner = null)
    {
        var ctx = new RouteContext();
        ctx.AddComponent(new As4Component());

        ctx.AddIdempotentRepository("dedup", new InMemoryIdempotentRepository());

        var f = new As4ConnectionFactory
        {
            OurPartyId = "redb",
            SigningCertificate = OurCertificate,
            DecryptionCertificates = { OurCertificate },
            Partners = { NewPartner(partner) },
        };
        factory?.Invoke(f);
        ctx.AddToRegistry("node", f);
        return ctx;
    }

    private static As4Partner NewPartner(Action<As4Partner>? configure = null)
    {
        var p = new As4Partner
        {
            Name = "acme",
            PartyId = "acme",
            Service = "urn:example:invoice",
            Action = "Submit",
            PartnerSigningCertificates = { PartnerCertificate },
            PartnerEncryptionCertificate = PartnerCertificate,
        };
        configure?.Invoke(p);
        return p;
    }

    private static As4Producer SendProducer(RouteContext ctx, string? query)
    {
        var uri = "as4s://ap.acme.example/as4?connectionFactory=node" + (query is null ? "" : "&" + query);
        return (As4Producer)ctx.GetEndpoint(uri).CreateProducer();
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

#pragma warning disable SYSLIB0057 // net8.0 is a target framework; X509CertificateLoader is net9+.
    private static X509Certificate2 PublicOnly(X509Certificate2 certificate) =>
        new(certificate.Export(X509ContentType.Cert));
#pragma warning restore SYSLIB0057
}
