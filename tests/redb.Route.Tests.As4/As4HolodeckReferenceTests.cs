using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф1: a real message from Holodeck B2B 8.1.1 (capture <c>push-signed-encrypted-gzip</c>) decrypts and verifies
/// on .NET — the SwA attachment signature, BST references, RSA-OAEP with MGF1-SHA256 and AES-128-GCM, as the
/// eDelivery AS4 1.16 common profile has them. Every tampering is refused.
/// </summary>
public class As4HolodeckReferenceTests
{
    private const string Capture = "push-signed-encrypted-gzip";
    private const string Needs = @"captures\" + Capture;

    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static SwaMessage Request() => Load("request");

    private static SwaMessage Load(string side)
    {
        var (body, contentType) = As4Stand.Capture(Capture, side);
        return SwaMessage.Read(contentType, body, maxEnvelopeCharacters: 1024 * 1024);
    }

    [As4StandFact(Needs)]
    public void Receipt_VerifiesWithTheResponderKey_AndEchoesTheRequestReferences()
    {
        var request = Request();
        As4SecurityEngine.Decrypt(request, [As4Stand.KeyPair("holodeck-b")]);
        var sent = As4SecurityEngine.Verify(request, [As4Stand.Certificate("holodeck-a")], Tolerance, DateTimeOffset.UtcNow);

        var receipt = Load("response");
        var verification = As4SecurityEngine.Verify(receipt, [As4Stand.Certificate("holodeck-b")], Tolerance, DateTimeOffset.UtcNow);

        verification.Signer.Thumbprint.Should().Be(As4Stand.Certificate("holodeck-b").Thumbprint);
        var echoed = receipt.Envelope.GetElementsByTagName("MessagePartNRInformation", "http://docs.oasis-open.org/ebxml-bp/ebbp-signals-2.0")
            .OfType<XmlElement>()
            .Select(e => (XmlElement)e.GetElementsByTagName("Reference", WsSecurityNames.Ds)[0]!)
            .Select(r => (r.GetAttribute("URI"), r.GetElementsByTagName("DigestValue", WsSecurityNames.Ds)[0]!.InnerText))
            .ToList();
        var original = sent.References
            .Select(r => (r.GetAttribute("URI"), r.GetElementsByTagName("DigestValue", WsSecurityNames.Ds)[0]!.InnerText))
            .ToList();
        echoed.Should().BeEquivalentTo(original);
    }

    [As4StandFact(Needs)]
    public void Request_Parses_EnvelopeAndOneAttachment()
    {
        var message = Request();

        message.Parts.Should().HaveCount(1);
        message.Parts.Values.Single().ContentType.Should().Be("application/octet-stream");
        message.Envelope.GetElementsByTagName("Messaging", WsSecurityNames.Eb).Count.Should().Be(1);
    }

    [As4StandFact(Needs)]
    public void Request_Decrypts_VerifiesAndDecompresses_ToTheOriginalPayload()
    {
        var message = Request();

        var decrypted = As4SecurityEngine.Decrypt(message, [As4Stand.KeyPair("holodeck-b")]);
        var verification = As4SecurityEngine.Verify(message, [As4Stand.Certificate("holodeck-a")], Tolerance, DateTimeOffset.UtcNow);

        decrypted.Should().HaveCount(1);
        var part = message.Parts[decrypted[0]];
        part.ContentType.Should().Be("application/gzip");
        verification.Signer.Thumbprint.Should().Be(As4Stand.Certificate("holodeck-a").Thumbprint);
        verification.References.Should().HaveCount(3);

        Gunzip(part.ToArray()).Should().Equal(As4Stand.CaptureFile(Capture, "payload.xml"));
    }

    [As4StandFact(Needs)]
    public void Request_Decrypts_AndDecompresses_ToTheOriginalPayload()
    {
        var message = Request();

        var decrypted = As4SecurityEngine.Decrypt(message, [As4Stand.KeyPair("holodeck-b")]);

        decrypted.Should().HaveCount(1);
        var part = message.Parts[decrypted[0]];
        part.ContentType.Should().Be("application/gzip");
        Gunzip(part.ToArray()).Should().Equal(As4Stand.CaptureFile(Capture, "payload.xml"));
    }

    [As4StandFact(Needs)]
    public void Decrypt_WithSomeoneElsesKey_IsRefused()
    {
        var message = Request();

        var act = () => As4SecurityEngine.Decrypt(message, [As4Stand.KeyPair("redb")]);
        act.Should().Throw<CryptographicException>();
    }

    [As4StandFact(Needs)]
    public void Decrypt_TamperedCiphertext_IsRefused()
    {
        var message = Request();
        As4TestMessages.Flip(message.Parts.Values.Single(), 40);

        var act = () => As4SecurityEngine.Decrypt(message, [As4Stand.KeyPair("holodeck-b")]);
        act.Should().Throw<CryptographicException>();
    }

    [As4StandFact(Needs)]
    public void Verify_TamperedMessagingHeader_IsRefused()
    {
        var message = Request();
        As4SecurityEngine.Decrypt(message, [As4Stand.KeyPair("holodeck-b")]);
        var action = (XmlElement)message.Envelope.GetElementsByTagName("Action", WsSecurityNames.Eb)[0]!;
        action.InnerText = "SomethingElse";

        var act = () => As4SecurityEngine.Verify(message, [As4Stand.Certificate("holodeck-a")], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*digest of*does not match*");
    }

    [As4StandFact(Needs)]
    public void Verify_TamperedAttachment_IsRefused()
    {
        var message = Request();
        As4SecurityEngine.Decrypt(message, [As4Stand.KeyPair("holodeck-b")]);
        As4TestMessages.Flip(message.Parts.Values.Single(), 5);

        var act = () => As4SecurityEngine.Verify(message, [As4Stand.Certificate("holodeck-a")], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*digest of 'cid:*does not match*");
    }

    [As4StandFact(Needs)]
    public void Verify_SignerIsNotThePartner_IsRefused()
    {
        var message = Request();
        As4SecurityEngine.Decrypt(message, [As4Stand.KeyPair("holodeck-b")]);

        var act = () => As4SecurityEngine.Verify(message, [As4Stand.Certificate("redb")], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*not one of the partner's*");
    }

    [As4StandFact(Needs)]
    public void Verify_DuplicateIdOfTheSignedHeader_IsRefused()
    {
        var message = Request();
        As4SecurityEngine.Decrypt(message, [As4Stand.KeyPair("holodeck-b")]);

        // Signature wrapping: a second element with the id of the signed eb:Messaging.
        var messaging = (XmlElement)message.Envelope.GetElementsByTagName("Messaging", WsSecurityNames.Eb)[0]!;
        var body = (XmlElement)message.Envelope.GetElementsByTagName("Body", WsSecurityNames.Soap12)[0]!;
        body.AppendChild(messaging.CloneNode(deep: true));

        var act = () => As4SecurityEngine.Verify(message, [As4Stand.Certificate("holodeck-a")], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*Two elements carry the id*");
    }

    [As4StandFact(Needs)]
    public void Verify_WithoutDecrypting_IsRefused()
    {
        // The attachment signature is over the plaintext: ciphertext must not verify.
        var message = Request();

        var act = () => As4SecurityEngine.Verify(message, [As4Stand.Certificate("holodeck-a")], Tolerance, DateTimeOffset.UtcNow);
        act.Should().Throw<CryptographicException>().WithMessage("*digest of 'cid:*does not match*");
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
