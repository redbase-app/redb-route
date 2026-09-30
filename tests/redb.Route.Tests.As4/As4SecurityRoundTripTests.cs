using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using redb.Route.As4;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф1: what we sign and encrypt, we decrypt and verify — through the wire format (serialized, parsed back),
/// for every key reference form, and every tampering is refused for its own reason. Keys are generated
/// in memory; no harness needed.
/// </summary>
public class As4SecurityRoundTripTests
{
    private static readonly X509Certificate2 Sender = KeyPair("CN=sender.as4.test");
    private static readonly X509Certificate2 Receiver = KeyPair("CN=receiver.as4.test");
    private static readonly X509Certificate2 Stranger = KeyPair("CN=stranger.as4.test");
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("<invoice xmlns=\"urn:example\"><total>42</total></invoice>");

    [Theory]
    [InlineData(As4KeyReference.BinarySecurityToken)]
    [InlineData(As4KeyReference.IssuerSerial)]
    [InlineData(As4KeyReference.KeyIdentifier)]
    public void SignEncrypt_ThenDecryptVerify_ReturnsThePayload(As4KeyReference form)
    {
        var received = Transfer(Build(form, timestamp: true));

        var decrypted = As4SecurityEngine.Decrypt(received, [Receiver]);
        var verification = As4SecurityEngine.Verify(received, [PublicOnly(Sender)], Tolerance, DateTimeOffset.UtcNow);

        decrypted.Should().ContainSingle();
        verification.Signer.Thumbprint.Should().Be(Sender.Thumbprint);
        verification.References.Should().HaveCount(4);   // eb:Messaging, Body, wsu:Timestamp, attachment
        var part = received.Parts[decrypted[0]];
        part.ContentType.Should().Be("application/gzip");
        Gunzip(part.ToArray()).Should().Equal(Payload);
    }

    [Fact]
    public void Sign_ReturnsTheReferencesTheReceiverSees()
    {
        var sent = Build(As4KeyReference.BinarySecurityToken, timestamp: false);
        var sentReferences = sent.References.Select(r => r.OuterXml).ToList();

        var received = Transfer(sent.Message);
        As4SecurityEngine.Decrypt(received, [Receiver]);
        var verification = As4SecurityEngine.Verify(received, [PublicOnly(Sender)], Tolerance, DateTimeOffset.UtcNow);

        verification.References.Select(r => r.OuterXml).Should().Equal(sentReferences);
    }

    [Fact]
    public void Verify_TamperedHeaderAfterSigning_IsRefused()
    {
        var received = Transfer(Build(As4KeyReference.BinarySecurityToken, timestamp: false));
        As4SecurityEngine.Decrypt(received, [Receiver]);
        ((XmlElement)received.Envelope.GetElementsByTagName("Action", WsSecurityNames.Eb)[0]!).InnerText = "Refund";

        var act = () => As4SecurityEngine.Verify(received, [PublicOnly(Sender)], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*digest of*does not match*");
    }

    [Fact]
    public void Verify_TamperedSignedInfo_IsRefused()
    {
        var received = Transfer(Build(As4KeyReference.BinarySecurityToken, timestamp: false));
        As4SecurityEngine.Decrypt(received, [Receiver]);
        // Rewriting a digest value together with the element it covers leaves the references consistent; only
        // the signature over SignedInfo can notice.
        var digest = (XmlElement)received.Envelope.GetElementsByTagName("DigestValue", WsSecurityNames.Ds)[0]!;
        digest.InnerText = Convert.ToBase64String(new byte[32]);

        var act = () => As4SecurityEngine.Verify(received, [PublicOnly(Sender)], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("The signature does not verify.");
    }

    [Fact]
    public void Verify_SignedByAStranger_IsRefused()
    {
        var received = Transfer(Build(As4KeyReference.BinarySecurityToken, timestamp: false));
        As4SecurityEngine.Decrypt(received, [Receiver]);

        var act = () => As4SecurityEngine.Verify(received, [PublicOnly(Stranger)], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*not one of the partner's*");
    }

    [Theory]
    [InlineData(As4KeyReference.BinarySecurityToken)]
    [InlineData(As4KeyReference.IssuerSerial)]
    public void Verify_AStrangerWithThePartnersSubject_IsRefused(As4KeyReference form)
    {
        // Same subject (and, self-signed, the same issuer name) as the partner, another key and serial number.
        var impostor = KeyPair(Sender.Subject);
        var received = Transfer(Build(form, timestamp: false, impostor));
        As4SecurityEngine.Decrypt(received, [Receiver]);

        var act = () => As4SecurityEngine.Verify(received, [PublicOnly(Sender)], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*not one of the partner's*");
    }

    [Theory]
    [InlineData(2 * 365, "*expired*")]      // past NotAfter (the certificate lives a year)
    [InlineData(-2, "*not yet valid*")]     // before NotBefore (a day before issue)
    public void Verify_OutsideTheSignersValidity_IsRefused(int daysFromNow, string reason)
    {
        // Domibus default (domibus.sender.certificate.validation.onreceiving=true): a pinned certificate is still
        // checked for its validity period. The time is the verifier's clock, the same one the timestamp is checked by.
        var received = Transfer(Build(As4KeyReference.BinarySecurityToken, timestamp: false));
        As4SecurityEngine.Decrypt(received, [Receiver]);

        var act = () => As4SecurityEngine.Verify(received, [PublicOnly(Sender)], Tolerance, DateTimeOffset.UtcNow.AddDays(daysFromNow));
        act.Should().Throw<CryptographicException>().WithMessage(reason);
    }

    [Fact]
    public void Verify_ExpiredTimestamp_IsRefused()
    {
        var received = Transfer(Build(As4KeyReference.BinarySecurityToken, timestamp: true));
        As4SecurityEngine.Decrypt(received, [Receiver]);

        // Age allowed for two hours, so it is Expires (5 minutes) that refuses it.
        var act = () => As4SecurityEngine.Verify(received, [PublicOnly(Sender)], Tolerance, DateTimeOffset.UtcNow.AddHours(1), timestampTimeToLive: TimeSpan.FromHours(2));
        act.Should().Throw<CryptographicException>().WithMessage("*Timestamp has expired*");
    }

    [Fact]
    public void Verify_UnsignedAttachmentAddedLater_IsRefused()
    {
        var received = Transfer(Build(As4KeyReference.BinarySecurityToken, timestamp: false));
        As4SecurityEngine.Decrypt(received, [Receiver]);
        received.AddPart("smuggled@example", "application/octet-stream", [1, 2, 3]);

        var act = () => As4SecurityEngine.Verify(received, [PublicOnly(Sender)], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*does not cover the attachment 'smuggled@example'*");
    }

    [Fact]
    public void Decrypt_ForSomeoneElse_IsRefused()
    {
        var received = Transfer(Build(As4KeyReference.BinarySecurityToken, timestamp: false));

        var act = () => As4SecurityEngine.Decrypt(received, [Stranger]);
        act.Should().Throw<CryptographicException>().WithMessage("*not for any of our certificates*");
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_IsRefused()
    {
        var received = Transfer(Build(As4KeyReference.BinarySecurityToken, timestamp: false));
        As4TestMessages.Flip(received.Parts.Values.Single(), 20);

        var act = () => As4SecurityEngine.Decrypt(received, [Receiver]);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Envelope_WithADoctype_IsRefusedBeforeAnyProcessing()
    {
        var (contentType, body) = Build(As4KeyReference.BinarySecurityToken, timestamp: false).Message.Write();
        var text = Encoding.UTF8.GetString(body).Replace("<soapenv:Envelope", "<!DOCTYPE x [<!ENTITY a \"a\">]><soapenv:Envelope");

        var act = () => SwaMessage.Read(contentType, Encoding.UTF8.GetBytes(text), 1024 * 1024);
        act.Should().Throw<XmlException>();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed record Sent(SwaMessage Message, IReadOnlyList<XmlElement> References);

    private static Sent Build(As4KeyReference form, bool timestamp) => Build(form, timestamp, Sender);

    private static Sent Build(As4KeyReference form, bool timestamp, X509Certificate2 signer)
    {
        const string contentId = "payload-1@redb.test";
        var envelope = As4TestMessages.UserMessage("m-1@redb.test", contentId);
        var message = SwaMessage.Create(envelope);
        message.AddPart(contentId, "application/gzip", Gzip(Payload));

        var references = As4SecurityEngine.Sign(message, signer, form, timestamp ? TimeSpan.FromMinutes(5) : null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, PublicOnly(Receiver), form);
        return new Sent(message, references);
    }

    private static SwaMessage Transfer(SwaMessage message)
    {
        var (contentType, body) = message.Write();
        return SwaMessage.Read(contentType, body, 1024 * 1024);
    }

    private static SwaMessage Transfer(Sent sent) => Transfer(sent.Message);

    private static X509Certificate2 KeyPair(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Round-trip through PFX so the key is usable for decryption on every platform.
#pragma warning disable SYSLIB0057 // net8.0 is a target framework; X509CertificateLoader is net9+.
        return new X509Certificate2(created.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 PublicOnly(X509Certificate2 certificate) => new(certificate.Export(X509ContentType.Cert));
#pragma warning restore SYSLIB0057

    private static byte[] Gzip(byte[] data) => As4TestMessages.Gzip(data);

    private static byte[] Gunzip(byte[] data) => As4TestMessages.Gunzip(data);
}
