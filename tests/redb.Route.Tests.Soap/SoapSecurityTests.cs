using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>Ф4a: WS-Security UsernameToken flows producer → wire → consumer.</summary>
public class SoapSecurityTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task UsernameToken_ReachesConsumer()
    {
        var port = FreePort();
        string? gotUser = null;
        string? gotPass = null;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("secured", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc",
            SoapVersion = SoapVersion.Soap11,
            Username = "alice",
            Password = "secret",
        });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
                .Process(e =>
                {
                    gotUser = e.In.GetHeader<string>(SoapHeaders.Username);
                    gotPass = e.In.GetHeader<string>(SoapHeaders.Password);
                    e.In.Body = "<Ack xmlns=\"urn:test\">ok</Ack>";
                });
            r.From("direct://call")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").ConnectionFactory("secured"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("<Ping xmlns=\"urn:test\"/>")));

        gotUser.Should().Be("alice");
        gotPass.Should().Be("secret");
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=soap-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public async Task SignedBody_IsVerifiedByConsumer()
    {
        var port = FreePort();
        bool? sigValid = null;
        using var cert = SelfSigned();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddToRegistry("signed", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc",
            SoapVersion = SoapVersion.Soap11,
            SigningCert = cert,
        });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
                .Process(e =>
                {
                    sigValid = e.In.GetHeader<bool>(SoapHeaders.SignatureValid);
                    e.In.Body = "<Ack xmlns=\"urn:test\">ok</Ack>";
                });
            r.From("direct://call")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").ConnectionFactory("signed"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("<Ping xmlns=\"urn:test\"><n>1</n></Ping>")));

        sigValid.Should().BeTrue("the consumer must verify the WS-Security Body signature");
    }

    [Fact]
    public void VerifyBody_AuthenticatesAgainstExpectedCert_And_RejectsOthers()
    {
        using var signer = SelfSigned();
        using var other = SelfSigned();   // a different, untrusted certificate

        var envelope = SoapEnvelope.Build("<Ping xmlns=\"urn:test\"/>", SoapVersion.Soap11);
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(envelope));
        SoapSignature.SignBody(doc, signer, SoapVersion.Soap11);

        // Expected partner == the actual signer ⇒ authenticated.
        SoapSignature.VerifyBody(doc, signer, SoapVersion.Soap11).Should().BeTrue();
        // Expected partner == a DIFFERENT cert ⇒ rejected (this is the H1 fix: no blind trust of embedded cert).
        SoapSignature.VerifyBody(doc, other, SoapVersion.Soap11).Should().BeFalse();
        // No expected cert ⇒ integrity-only still passes, but this does NOT authenticate the sender.
        SoapSignature.VerifyBody(doc, null, SoapVersion.Soap11).Should().BeTrue();
    }

    [Fact]
    public void VerifyBody_Fails_WhenBodyTamperedAfterSigning()
    {
        using var signer = SelfSigned();
        var envelope = SoapEnvelope.Build("<Ping xmlns=\"urn:test\"><n>1</n></Ping>", SoapVersion.Soap11);
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(envelope));
        SoapSignature.SignBody(doc, signer, SoapVersion.Soap11);

        // Tamper the signed Body payload.
        var n = doc.GetElementsByTagName("n")[0]!;
        n.InnerText = "999";

        SoapSignature.VerifyBody(doc, signer, SoapVersion.Soap11).Should().BeFalse();
    }

    [Fact]
    public void VerifyBody_Rejects_SignatureWrapping()
    {
        using var cert = SelfSigned();
        var envelope = SoapEnvelope.Build("<Ping xmlns=\"urn:test\"><n>1</n></Ping>", SoapVersion.Soap11);
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(envelope));
        SoapSignature.SignBody(doc, cert, SoapVersion.Soap11);

        // Signature-wrapping attack: move the genuinely-signed Body under <Header> (first in document order),
        // and put an attacker-controlled Body as the Envelope's direct child (what the processing path reads).
        const string soapNs = "http://schemas.xmlsoap.org/soap/envelope/";
        var env = doc.DocumentElement!;
        var header = (System.Xml.XmlElement)env.GetElementsByTagName("Header", soapNs)[0]!;
        var signedBody = (System.Xml.XmlElement)env.GetElementsByTagName("Body", soapNs)[0]!;
        env.RemoveChild(signedBody);
        header.AppendChild(signedBody);
        var evil = doc.CreateElement("soap", "Body", soapNs);
        evil.InnerXml = "<Ping xmlns=\"urn:test\"><n>999</n></Ping>";
        env.AppendChild(evil);

        // The signature still validates over the nested original, but it does NOT cover the direct-child Body
        // the route consumes — so verification must fail, in both authenticated and integrity-only modes.
        SoapSignature.VerifyBody(doc, cert, SoapVersion.Soap11).Should().BeFalse();
        SoapSignature.VerifyBody(doc, null, SoapVersion.Soap11).Should().BeFalse();
    }

    [Fact]
    public void EncryptBody_Encrypts_All_Body_Children()
    {
        using var cert = SelfSigned();
        // A document/literal Body with two children: both must be encrypted, not just the first.
        var envelope = SoapEnvelope.Build("<A xmlns=\"urn:t\">secret1</A><B xmlns=\"urn:t\">secret2</B>", SoapVersion.Soap11);
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(envelope));
        SoapEncryption.EncryptBody(doc, cert, SoapVersion.Soap11);

        doc.OuterXml.Should().NotContain("secret1").And.NotContain("secret2");
        doc.GetElementsByTagName("EncryptedData", SoapEncryption.XmlEncNs).Count.Should().Be(2);

        // And the one EncryptedKey / ReferenceList round-trips both back.
        SoapEncryption.DecryptBody(doc, cert);
        doc.OuterXml.Should().Contain("secret1").And.Contain("secret2");
    }

    [Fact]
    public void EncryptBody_ProducesStandardWssLayout()
    {
        using var cert = SelfSigned();
        var envelope = SoapEnvelope.Build("<Secret xmlns=\"urn:test\"><n>1</n></Secret>", SoapVersion.Soap11);
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(envelope));
        SoapEncryption.EncryptBody(doc, cert, SoapVersion.Soap11);

        var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("soap", "http://schemas.xmlsoap.org/soap/envelope/");
        ns.AddNamespace("wsse", SoapSecurity.WsseNs);
        ns.AddNamespace("xenc", SoapEncryption.XmlEncNs);

        // EncryptedKey lives in the <wsse:Security> header (standard WSS layout), not nested in EncryptedData.
        doc.SelectSingleNode("//wsse:Security/xenc:EncryptedKey", ns).Should().NotBeNull();
        // It references the EncryptedData by Id via a ReferenceList/DataReference.
        var dataRefUri = doc.SelectSingleNode(
            "//xenc:EncryptedKey/xenc:ReferenceList/xenc:DataReference/@URI", ns)?.Value;
        dataRefUri.Should().StartWith("#ED-");
        // The EncryptedData sits in the Body with the matching Id, and carries no nested EncryptedKey.
        var ed = doc.SelectSingleNode("//soap:Body/xenc:EncryptedData", ns) as System.Xml.XmlElement;
        ed.Should().NotBeNull();
        ("#" + ed!.GetAttribute("Id")).Should().Be(dataRefUri);
        ed.SelectSingleNode(".//xenc:EncryptedKey", ns).Should().BeNull();
    }

    [Fact]
    public async Task SignedAndEncrypted_RoundTrips_DecryptThenVerify()
    {
        var port = FreePort();
        string? gotBody = null;
        bool? sigValid = null;
        using var cert = SelfSigned();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        // Client signs with its cert AND encrypts to the partner; server holds the key (decrypt) and knows the
        // partner cert (authenticated verify). Exercises the sign→encrypt / decrypt→verify round-trip.
        ctx.AddToRegistry("client", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc",
            SoapVersion = SoapVersion.Soap11,
            SigningCert = cert,
            EncryptCert = cert,
        });
        ctx.AddToRegistry("server", new SoapConnectionFactory { SigningCert = cert, EncryptCert = cert });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port).ConnectionFactory("server"))
                .Process(e =>
                {
                    gotBody = e.In.Body!.ToString();
                    sigValid = e.In.GetHeader<bool>(SoapHeaders.SignatureValid);
                    e.In.Body = "<Ack xmlns=\"urn:test\">ok</Ack>";
                });
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").ConnectionFactory("client"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("<Secret xmlns=\"urn:test\"><n>7</n></Secret>")));

        gotBody.Should().NotBeNull().And.Contain("Secret").And.Contain("7"); // decrypted
        sigValid.Should().BeTrue("the signature must verify against the expected partner cert after decryption");
    }

    [Fact]
    public async Task EncryptedBody_IsDecryptedByConsumer()
    {
        var port = FreePort();
        string? gotBody = null;
        using var cert = SelfSigned();

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        // Producer encrypts to the partner's public cert (no signing); consumer decrypts with the private key.
        ctx.AddToRegistry("client", new SoapConnectionFactory
        {
            EndpointUrl = $"http://127.0.0.1:{port}/svc",
            SoapVersion = SoapVersion.Soap11,
            EncryptCert = cert,
        });
        ctx.AddToRegistry("server", new SoapConnectionFactory { SigningCert = cert });
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port).ConnectionFactory("server"))
                .Process(e => { gotBody = e.In.Body!.ToString(); e.In.Body = "<Ack xmlns=\"urn:test\">ok</Ack>"; });
            r.From("direct://call")
                .To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").ConnectionFactory("client"));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://call").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("<Secret xmlns=\"urn:test\"><n>42</n></Secret>")));

        // The route received plaintext — proving the Body was encrypted on the wire and decrypted on receipt.
        gotBody.Should().NotBeNull().And.Contain("Secret").And.Contain("42");
    }
}
