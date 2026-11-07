using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.As4;
using redb.Route.As4.Security;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using As4Dsl = redb.Route.As4.Fluent.As4;

namespace redb.Route.Tests.As4;

/// <summary>
/// The X509PKIPathv1 security token (WS-Security X.509 Token Profile §3.1.2; eDelivery AS4 1.16 §4.3.3): a signer that
/// presents its certification path, and a receiver that trusts an authority instead of holding the signer's
/// certificate. The loopback tests carry the ids of the conformance assertions they follow (AS4_CP_TA47 to TA51).
/// </summary>
public class As4PkiPathTests
{
    private const string Policy = "1.3.6.1.4.1.99999.1.1";

    // root -> intermediate -> signer, each asserting the policy; and a signer of the same root without it.
    private static readonly X509Certificate2 Root = Authority("CN=Test Root CA");
    private static readonly X509Certificate2 Intermediate = Issue(Root, "CN=Test Issuing CA", authority: true, Policy);
    private static readonly X509Certificate2 Signer = Issue(Intermediate, "CN=a.pkipath.test", authority: false, Policy);
    private static readonly X509Certificate2 SignerWithoutPolicy = Issue(Intermediate, "CN=nopolicy.pkipath.test", authority: false);
    private static readonly X509Certificate2 SelfSigned = As4TestMessages.KeyPair("CN=selfsigned.pkipath.test");
    private static readonly X509Certificate2 Receiver = As4TestMessages.KeyPair("CN=b.pkipath.test");

    private static PkiPathTrust Trust(params X509Certificate2[] anchors) => new(anchors, []);

    // ── The token ────────────────────────────────────────────────────────────

    [Fact]
    public void Path_IsEncodedFromTheAuthorityDownToTheSigner_AndReadBackInThatOrder()
    {
        // The authorities are given in any order; the signer is the last certificate of the path (ITU-T X.509 PkiPath).
        var path = PkiPath.Order(Signer, [Intermediate, Root]);
        path.Select(c => c.Subject).Should().Equal(Root.Subject, Intermediate.Subject, Signer.Subject);

        var decoded = PkiPath.Decode(PkiPath.Encode(path));
        decoded.Select(c => c.Thumbprint).Should().Equal(Root.Thumbprint, Intermediate.Thumbprint, Signer.Thumbprint);
    }

    [Fact]
    public void Order_LeavesOutAnAuthorityThatIssuedNothingOfThePath()
    {
        var stranger = Authority("CN=Another Root CA");
        PkiPath.Order(Signer, [stranger, Intermediate]).Select(c => c.Subject).Should().Equal(Intermediate.Subject, Signer.Subject);
        PkiPath.Order(SelfSigned, [Root]).Should().ContainSingle();
    }

    [Theory]
    [InlineData("not asn.1")]
    [InlineData("empty sequence")]
    [InlineData("a certificate, not a sequence of them")]
    public void Decode_OfWhatIsNotAPath_IsRefused(string what)
    {
        byte[] der = what switch
        {
            "not asn.1" => [1, 2, 3, 4],
            "empty sequence" => [0x30, 0x00],
            _ => Signer.RawData,
        };
        var act = () => PkiPath.Decode(der);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Signature_WithAPath_CarriesThePathToken_AndNamesItsTypeOnTheReference()
    {
        var message = redb.Route.As4.Mime.SwaMessage.Create(As4TestMessages.UserMessage("m1@a.example", "p1@a.example"));
        message.AddPart("p1@a.example", "application/gzip", As4TestMessages.Gzip(Encoding.UTF8.GetBytes("<a/>")));
        As4SecurityEngine.Sign(message, Signer, As4KeyReference.PkiPath, null, DateTimeOffset.UtcNow, [Root, Intermediate, Signer]);

        var token = (System.Xml.XmlElement)message.Envelope.GetElementsByTagName("BinarySecurityToken", WsSecurityNames.Wsse)[0]!;
        token.GetAttribute("ValueType").Should().Be(PkiPath.ValueType);
        PkiPath.Decode(Convert.FromBase64String(token.InnerText)).Select(c => c.Subject)
            .Should().Equal(Root.Subject, Intermediate.Subject, Signer.Subject);

        // WS-I Basic Security Profile 1.1 R5215: WSS4J (Domibus, Holodeck) refuses the reference without wsse11:TokenType.
        var reference = (System.Xml.XmlElement)message.Envelope.GetElementsByTagName("SecurityTokenReference", WsSecurityNames.Wsse)[0]!;
        reference.GetAttribute("TokenType", WsSecurityNames.Wsse11).Should().Be(PkiPath.ValueType);
        ((System.Xml.XmlElement)reference.FirstChild!).GetAttribute("ValueType").Should().Be(PkiPath.ValueType);

        As4SecurityEngine.Verify(message, [], TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, pkiPath: Trust(Root))
            .Signer.Thumbprint.Should().Be(Signer.Thumbprint);
    }

    // ── Validation of a path ─────────────────────────────────────────────────

    [Fact]
    public void Path_ToATrustedRoot_IsTrusted() =>
        PkiPath.EnsureTrusted([Root, Intermediate, Signer], Trust(Root), DateTimeOffset.UtcNow);

    [Fact]
    public void Path_ToATrustedIntermediateAuthority_IsTrusted_AsACertificatePathPrefix() =>
        PkiPath.EnsureTrusted([Root, Intermediate, Signer], Trust(Intermediate), DateTimeOffset.UtcNow);

    [Fact]
    public void Path_ThatLeavesTheAnchorOut_IsTrusted_WhenTheAnchorIssuedItsFirstCertificate() =>
        PkiPath.EnsureTrusted([Intermediate, Signer], Trust(Root), DateTimeOffset.UtcNow);

    [Fact]
    public void Path_OfASelfSignedCertificate_IsNotTrusted_UnlessItIsItselfTheAnchor()
    {
        var act = () => PkiPath.EnsureTrusted([SelfSigned], Trust(Root), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*does not chain to a trust anchor*");

        PkiPath.EnsureTrusted([SelfSigned], Trust(SelfSigned), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Path_ToAnotherAuthority_IsNotTrusted()
    {
        var act = () => PkiPath.EnsureTrusted([Root, Intermediate, Signer], Trust(Authority("CN=Another Root CA")), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*does not chain to a trust anchor*");
    }

    [Fact]
    public void Certificates_OutOfOrder_AreNotAPath()
    {
        var act = () => PkiPath.EnsureTrusted([Signer, Intermediate, Root], Trust(Root), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*is not a path*");
    }

    [Fact]
    public void Path_WithACertificateForgedUnderTheNameOfATrustedAuthority_IsNotTrusted()
    {
        // Same subject as the trusted root, another key: the names line up, the signatures do not.
        var impostor = Authority(Root.Subject);
        var forged = Issue(impostor, "CN=forged.pkipath.test", authority: false);

        var act = () => PkiPath.EnsureTrusted([forged], Trust(Root), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*does not chain to a trust anchor*");
    }

    [Fact]
    public void Path_ThroughACertificateThatIsNotAnAuthority_IsNotTrusted()
    {
        // An end-entity certificate used to issue another one.
        var issuedByAnEndEntity = Issue(Signer, "CN=below.pkipath.test", authority: false);

        var act = () => PkiPath.EnsureTrusted([Root, Intermediate, Signer, issuedByAnEndEntity], Trust(Root), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*not a certification authority*");
    }

    [Fact]
    public void Path_WithAnExpiredAuthority_IsNotTrusted()
    {
        var expired = Issue(Root, "CN=Expired Issuing CA", authority: true, notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var signer = Issue(expired, "CN=late.pkipath.test", authority: false, notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        var act = () => PkiPath.EnsureTrusted([Root, expired, signer], Trust(Root), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*has expired*");
    }

    [Fact]
    public void Policy_MustBeAssertedByTheSignerAndTheAuthoritiesBelowTheAnchor()
    {
        PkiPath.EnsureTrusted([Root, Intermediate, Signer], new PkiPathTrust([Root], [Policy]), DateTimeOffset.UtcNow);

        var act = () => PkiPath.EnsureTrusted([Root, Intermediate, SignerWithoutPolicy], new PkiPathTrust([Root], [Policy]), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage($"*does not assert the certificate policy {Policy}*");

        var other = () => PkiPath.EnsureTrusted([Root, Intermediate, Signer], new PkiPathTrust([Root], ["1.3.6.1.4.1.99999.9"]), DateTimeOffset.UtcNow);
        other.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void AnyPolicy_InACertificate_SatisfiesARequiredPolicy()
    {
        var anyPolicy = Issue(Intermediate, "CN=anypolicy.pkipath.test", authority: false, "2.5.29.32.0");
        PkiPath.EnsureTrusted([Intermediate, anyPolicy], new PkiPathTrust([Intermediate], [Policy]), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Path_BeyondThePathLengthOfAnAuthority_IsNotTrusted()
    {
        // RFC 5280 §4.2.1.9: the issuing authority may have no authority below it; this path puts one there.
        var bounded = Issue(Root, "CN=Bounded Issuing CA", authority: true, pathLength: 0);
        var sub = Issue(bounded, "CN=Sub CA", authority: true);
        var signer = Issue(sub, "CN=deep.pkipath.test", authority: false);

        var act = () => PkiPath.EnsureTrusted([Root, bounded, sub, signer], Trust(Root), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*Bounded Issuing CA allows 0 authorities below it; the path has 1*");

        PkiPath.EnsureTrusted([Root, bounded, Issue(bounded, "CN=near.pkipath.test", authority: false)], Trust(Root), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void PathLength_OfTheAnchorItself_IsHonoured()
    {
        var bounded = Issue(Root, "CN=Bounded Anchor CA", authority: true, pathLength: 0);
        var sub = Issue(bounded, "CN=Sub Of Anchor CA", authority: true);
        var signer = Issue(sub, "CN=under.pkipath.test", authority: false);

        var act = () => PkiPath.EnsureTrusted([bounded, sub, signer], Trust(bounded), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*allows 0 authorities below it*");
    }

    [Fact]
    public void Path_ThroughAnAuthorityWhoseKeyMayNotSignCertificates_IsNotTrusted()
    {
        // A certification authority by its basic constraints, but its key usage leaves keyCertSign out.
        var crlOnly = Issue(Root, "CN=CRL Only CA", authority: true, usage: X509KeyUsageFlags.CrlSign);
        var signer = Issue(crlOnly, "CN=wrongkey.pkipath.test", authority: false);

        var act = () => PkiPath.EnsureTrusted([Root, crlOnly, signer], Trust(Root), DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*CRL Only CA is not allowed to sign certificates*");
    }

    // ── The agreement ────────────────────────────────────────────────────────

    [Fact]
    public void Agreement_PinsACertificateOrNamesAuthorities_NotBothAndNotNeither()
    {
        var both = Partner("x", Receiver);
        both.PartnerTrustAnchors.Add(Root);
        ((Action)both.Validate).Should().Throw<InvalidOperationException>().WithMessage("*either PartnerSigningCertificates*or PartnerTrustAnchors*");

        var neither = Partner("x", Receiver);
        neither.PartnerSigningCertificates.Clear();
        ((Action)neither.Validate).Should().Throw<InvalidOperationException>().WithMessage("*either PartnerSigningCertificates*or PartnerTrustAnchors*");

        var policiesWithoutAnchors = Partner("x", Receiver);
        policiesWithoutAnchors.PartnerCertificatePolicies.Add(Policy);
        ((Action)policiesWithoutAnchors.Validate).Should().Throw<InvalidOperationException>().WithMessage("*PartnerCertificatePolicies*PartnerTrustAnchors*");
    }

    // ── Loopback: a sender with a path, a receiver that trusts an authority ──

    [Fact]
    public async Task TA47_TA48_AMessageSignedWithAPath_IsReceived_ByANodeThatTrustsOneOfItsAuthorities()
    {
        string? signerThumbprint = null;
        await using var stand = await Stand.StartAsync(e => signerThumbprint = e.In.GetHeader<string>(As4Headers.SignerThumbprint));

        var exchange = NewExchange();
        await stand.Send(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        signerThumbprint.Should().Be(Signer.Thumbprint, "the signer is the last certificate of the path, not an authority");
    }

    [Fact]
    public async Task APathOnBothSides_TheReceiptIsSignedWithAPathToo_AndTheSenderTrustsItsAuthority()
    {
        // The receiver's certificate is its own authority here: a path of one, which the sender trusts as its anchor.
        await using var stand = await Stand.StartAsync(_ => { }, receiverAnswersWithAPath: true);

        var exchange = NewExchange();
        await stand.Send(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
    }

    [Fact]
    public async Task TA49_ASelfSignedSigner_IsNotAuthorized()
    {
        var reached = false;
        await using var stand = await Stand.StartAsync(_ => reached = true, signer: SelfSigned, authorities: []);

        var act = () => stand.Send(NewExchange());

        (await act.Should().ThrowAsync<As4ErrorSignalException>()).Which.ErrorCode.Should().Be("EBMS:0101");
        reached.Should().BeFalse();
    }

    [Theory]
    [InlineData(As4KeyReference.BinarySecurityToken)]   // TA50: a token that holds the signing certificate alone
    [InlineData(As4KeyReference.IssuerSerial)]          // TA51: a reference to the certificate
    [InlineData(As4KeyReference.KeyIdentifier)]
    public async Task TA50_TA51_ASignerThatDoesNotPresentAPath_IsNotAuthorized_EvenWhenItsCertificateWouldChain(As4KeyReference form)
    {
        var reached = false;
        await using var stand = await Stand.StartAsync(_ => reached = true, senderReference: form);

        var act = () => stand.Send(NewExchange());

        (await act.Should().ThrowAsync<As4ErrorSignalException>()).Which.ErrorCode.Should().Be("EBMS:0101");
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task ASignerWithoutTheRequiredCertificatePolicy_IsNotAuthorized()
    {
        var reached = false;
        await using var stand = await Stand.StartAsync(_ => reached = true, signer: SignerWithoutPolicy, policy: Policy);

        var act = () => stand.Send(NewExchange());

        (await act.Should().ThrowAsync<As4ErrorSignalException>()).Which.ErrorCode.Should().Be("EBMS:0101");
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task APinnedAgreement_AcceptsItsPartnersCertificateInAPathToken_AsInAnyOtherForm()
    {
        // The common profile leaves the token form to the sender: a receiver that pins the certificate reads the path
        // for the certificate it is for and compares it with the pin.
        var reached = false;
        await using var stand = await Stand.StartAsync(_ => reached = true, receiverPinsTheSigner: true);

        var exchange = NewExchange();
        await stand.Send(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        reached.Should().BeTrue();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static Exchange NewExchange()
    {
        var exchange = new Exchange(new Message("<invoice xmlns=\"urn:example\"/>") { ContentType = "text/xml" });
        exchange.In.Headers[As4Headers.OriginalSender] = "C1";
        exchange.In.Headers[As4Headers.FinalRecipient] = "C4";
        return exchange;
    }

    private static As4Partner Partner(string name, X509Certificate2 certificate) => new()
    {
        Name = name,
        PartyId = "urn:redb:" + name,
        Service = "urn:example",
        Action = "Submit",
        PartnerSigningCertificates = { As4TestMessages.PublicOnly(certificate) },
        PartnerEncryptionCertificate = As4TestMessages.PublicOnly(certificate),
    };

    /// <summary>Node B receiving on a loopback port from node A, whose signer B does not hold: B trusts the root.</summary>
    private sealed class Stand : IAsyncDisposable
    {
        private readonly RouteContext _context;
        private readonly int _port;

        private Stand(RouteContext context, int port)
        {
            _context = context;
            _port = port;
        }

        public static async Task<Stand> StartAsync(Action<IExchange> route, X509Certificate2? signer = null,
            X509Certificate2[]? authorities = null, As4KeyReference senderReference = As4KeyReference.PkiPath, string? policy = null,
            bool receiverAnswersWithAPath = false, bool receiverPinsTheSigner = false)
        {
            signer ??= Signer;
            var port = global::redb.Route.Tests.Shared.TestPorts.Next();
            var context = new RouteContext();
            context.AddComponent(new As4Component());
            context.AddIdempotentRepository("dedup", new InMemoryIdempotentRepository());

            // B's view of A: no pinned signing certificate, the root as the trust anchor.
            var aAtB = Partner("a", signer);
            if (!receiverPinsTheSigner)
            {
                aAtB.PartnerSigningCertificates.Clear();
                aAtB.PartnerTrustAnchors.Add(As4TestMessages.PublicOnly(Root));
            }
            if (policy is not null) aAtB.PartnerCertificatePolicies.Add(policy);
            if (receiverAnswersWithAPath) aAtB.KeyReference = As4KeyReference.PkiPath;
            context.AddToRegistry("b", new As4ConnectionFactory
            {
                OurPartyId = "urn:redb:b",
                ExternalHostName = "b.example",
                SigningCertificate = Receiver,
                DecryptionCertificates = { Receiver },
                Partners = { aAtB },
            });

            // A's view of B: signs with a path; B's own certificate is pinned, or trusted as its own authority.
            var bAtA = Partner("b", Receiver);
            bAtA.KeyReference = senderReference;
            if (receiverAnswersWithAPath)
            {
                bAtA.PartnerSigningCertificates.Clear();
                bAtA.PartnerTrustAnchors.Add(As4TestMessages.PublicOnly(Receiver));
            }
            var a = new As4ConnectionFactory
            {
                OurPartyId = "urn:redb:a",
                ExternalHostName = "a.example",
                SigningCertificate = signer,
                DecryptionCertificates = { signer },
                Partners = { bAtA },
            };
            foreach (var authority in authorities ?? [Intermediate, Root])
                a.SigningCertificateChain.Add(As4TestMessages.PublicOnly(authority));
            context.AddToRegistry("a", a);

            context.AddRoutes(r => r.From(As4Dsl.Receive("/as4/in").Host("127.0.0.1").Port(port).ConnectionFactory("b").IdempotentRepository("dedup"))
                .Process(e => route(e)));
            await context.Start();
            return new Stand(context, port);
        }

        public async Task Send(IExchange exchange)
        {
            var producer = _context.GetEndpoint(
                As4Dsl.Send($"http://127.0.0.1:{_port}/as4/in").ConnectionFactory("a").Partner("b").Timeout(15000)).CreateProducer();
            await producer.Start();
            await producer.Process(exchange);
        }

        public async ValueTask DisposeAsync() => await _context.DisposeAsync();
    }

    // ── Our path ─────────────────────────────────────────────────────────────

    [Fact]
    public void OurPath_IsOrderedOnce_WhenTheNodeIsResolved()
    {
        using var context = NodeContext(Intermediate, Root);

        var node = As4Node.Resolve(context, "node");

        node.SigningPath.Select(c => c.Subject).Should().Equal(Root.Subject, Intermediate.Subject, Signer.Subject);
    }

    [Fact]
    public void OurChain_WithACertificateOffThePath_StopsTheNodeFromStarting()
    {
        // An authority that issued neither our certificate nor another authority of the chain would be left out of every
        // token without a word, and the partner would refuse every message (EBMS:0101).
        using var context = NodeContext(Intermediate, Root, Authority("CN=Unrelated Root CA"));

        var act = () => As4Node.Resolve(context, "node");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SigningCertificateChain holds CN=Unrelated Root CA*not on the certification path*");
    }

    private static RouteContext NodeContext(params X509Certificate2[] chain)
    {
        var context = new RouteContext();
        var factory = new As4ConnectionFactory
        {
            OurPartyId = "urn:redb:a",
            SigningCertificate = Signer,
            DecryptionCertificates = { Signer },
            Partners = { Partner("b", Receiver) },
        };
        foreach (var authority in chain)
            factory.SigningCertificateChain.Add(As4TestMessages.PublicOnly(authority));
        context.AddToRegistry("node", factory);
        return context;
    }

    // ── A small certification hierarchy ──────────────────────────────────────

    private static X509Certificate2 Authority(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));
        return Reload(created);
    }

    private static X509Certificate2 Issue(X509Certificate2 issuer, string subject, bool authority, string? policy = null,
        DateTimeOffset? notAfter = null, int? pathLength = null, X509KeyUsageFlags? usage = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, pathLength.HasValue, pathLength ?? 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            usage ?? (authority ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment),
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        if (policy is not null)
        {
            // certificatePolicies ::= SEQUENCE OF PolicyInformation { policyIdentifier OBJECT IDENTIFIER }
            var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            using (writer.PushSequence())
            using (writer.PushSequence())
                writer.WriteObjectIdentifier(policy);
            request.CertificateExtensions.Add(new X509Extension("2.5.29.32", writer.Encode(), critical: false));
        }

        // A certificate lives within the validity period of its issuer.
        var issuerEnd = new DateTimeOffset(issuer.NotAfter.ToUniversalTime());
        var end = notAfter is { } asked && asked < issuerEnd ? asked : issuerEnd;
        var serial = RandomNumberGenerator.GetBytes(12);
        // Positive and without a redundant leading byte (DER INTEGER): a first byte of zero followed by one below 0x80
        // is refused by CertificateRevocationListBuilder.AddEntry, which a plain "& 0x7F" produced about once in 512.
        serial[0] = (byte)((serial[0] & 0x7F) | 0x40);
        // Signed with the issuer's key directly: the overload that takes the issuer's certificate refuses an issuer
        // that is not an authority, which one of the tests needs.
        using var issuerKey = issuer.GetRSAPrivateKey()!;
        using var issued = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1),
            new DateTimeOffset(issuer.NotBefore.ToUniversalTime()), end, serial);
        using var withKey = issued.CopyWithPrivateKey(rsa);
        return Reload(withKey);
    }

#pragma warning disable SYSLIB0057 // net8.0 is a target framework; X509CertificateLoader is net9+.
    private static X509Certificate2 Reload(X509Certificate2 certificate) =>
        new(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
}
