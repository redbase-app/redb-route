using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using redb.Route.As4;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;
using redb.Route.Components;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф1, live: a message signed and encrypted by us is accepted by Holodeck B2B node B (P-Mode
/// <c>redb-interop-push</c>, through the recorder on port 14091), which answers with a receipt we verify —
/// its signature, and that its non-repudiation references are exactly the ones we signed.
/// </summary>
public class As4HolodeckLiveTests
{
    private const int RecorderPort = 14091;

    [As4LiveTheory(RecorderPort)]
    [InlineData(As4KeyReference.BinarySecurityToken)]
    [InlineData(As4KeyReference.IssuerSerial)]
    [InlineData(As4KeyReference.KeyIdentifier)]
    public async Task OurSignedEncryptedMessage_IsAcceptedByHolodeck_WithAVerifiedReceipt(As4KeyReference form)
    {
        var messageId = Guid.NewGuid().ToString("N") + "@redb.test";
        const string contentId = "payload-1@redb.test";
        var payload = Encoding.UTF8.GetBytes($"<invoice xmlns=\"urn:example\"><id>{messageId}</id></invoice>");

        var envelope = As4TestMessages.UserMessage(messageId, contentId,
            from: "urn:redb:as4:test:redb", to: "org:holodeckb2b:example:company:B",
            agreementRef: "urn:redb:as4:interop", service: "urn:redb:as4:interop",
            serviceType: "org:holodeckb2b:services", action: "StoreMessage");
        var message = SwaMessage.Create(envelope);
        message.AddPart(contentId, "application/gzip", As4TestMessages.Gzip(payload));

        var sent = As4SecurityEngine.Sign(message, As4Stand.KeyPair("redb"), form,
            TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4Stand.Certificate("holodeck-b"), form);
        var (contentType, body) = message.Write();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await http.PostAsync($"http://127.0.0.1:{RecorderPort}/holodeckb2b/as4", content);

        var responseBody = await response.Content.ReadAsByteArrayAsync();
        var responseType = response.Content.Headers.ContentType?.ToString()
            ?? throw new InvalidOperationException($"Holodeck answered HTTP {(int)response.StatusCode} without a Content-Type.");
        var answer = SwaMessage.Read(responseType, responseBody, 1024 * 1024);

        var error = answer.Envelope.GetElementsByTagName("Error", WsSecurityNames.Eb).OfType<XmlElement>().FirstOrDefault();
        error.Should().BeNull($"Holodeck answered with an ebMS error: {error?.OuterXml}");

        var verification = As4SecurityEngine.Verify(answer, [As4Stand.Certificate("holodeck-b")], TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
        verification.Signer.Thumbprint.Should().Be(As4Stand.Certificate("holodeck-b").Thumbprint);

        answer.Envelope.GetElementsByTagName("RefToMessageId", WsSecurityNames.Eb)[0]!.InnerText.Should().Be(messageId);
        var echoed = answer.Envelope.GetElementsByTagName("MessagePartNRInformation", "http://docs.oasis-open.org/ebxml-bp/ebbp-signals-2.0")
            .OfType<XmlElement>()
            .Select(e => (XmlElement)e.GetElementsByTagName("Reference", WsSecurityNames.Ds)[0]!)
            .Select(Digest)
            .ToList();
        echoed.Should().BeEquivalentTo(sent.Select(Digest));
    }

    /// <summary>
    /// Ф7: an uncompressed text payload is signed over its MIME canonical form (SwA 1.1 §5.4.2) — Exclusive C14N for
    /// XML, CRLF line endings for other text. The payloads are chosen so that canonical and raw bytes differ.
    /// </summary>
    [As4LiveTheory(RecorderPort)]
    [InlineData("text/xml", "<?xml version='1.0'?>\n<invoice   xmlns='urn:example'  id='1'>\n  <total>42</total>\n</invoice>\n")]
    [InlineData("application/xml", "<invoice xmlns='urn:example'><note/></invoice>")]
    [InlineData("text/plain", "line one\nline two\n")]
    public async Task UncompressedTextPayload_IsSignedOverItsCanonicalForm_AndAcceptedByHolodeck(string mimeType, string text)
    {
        var messageId = Guid.NewGuid().ToString("N") + "@redb.test";
        const string contentId = "payload-1@redb.test";

        var envelope = As4TestMessages.UserMessage(messageId, contentId,
            from: "urn:redb:as4:test:redb", to: "org:holodeckb2b:example:company:B",
            agreementRef: "urn:redb:as4:interop", service: "urn:redb:as4:interop",
            serviceType: "org:holodeckb2b:services", action: "StoreMessage", uncompressedMimeType: mimeType);
        var message = SwaMessage.Create(envelope);
        message.AddPart(contentId, mimeType, Encoding.UTF8.GetBytes(text));

        As4SecurityEngine.Sign(message, As4Stand.KeyPair("redb"), As4KeyReference.BinarySecurityToken, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4Stand.Certificate("holodeck-b"), As4KeyReference.BinarySecurityToken);
        var (contentType, body) = message.Write();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await http.PostAsync($"http://127.0.0.1:{RecorderPort}/holodeckb2b/as4", content);

        var answer = SwaMessage.Read(response.Content.Headers.ContentType!.ToString(), await response.Content.ReadAsByteArrayAsync(), 1024 * 1024);
        var error = answer.Envelope.GetElementsByTagName("Error", WsSecurityNames.Eb).OfType<XmlElement>().FirstOrDefault();
        error.Should().BeNull($"Holodeck answered with an ebMS error: {error?.OuterXml}");
        answer.Envelope.GetElementsByTagName("Receipt", WsSecurityNames.Eb).Count.Should().Be(1);
    }

    [As4LiveFact(RecorderPort)]
    public async Task Producer_SendsToHolodeck_AndVerifiesItsReceipt()
    {
        await using var context = new redb.Route.Core.RouteContext();
        context.AddComponent(new As4Component());
        context.AddIdempotentRepository("dedup", new redb.Route.Processors.InMemoryIdempotentRepository());
        context.AddToRegistry("node", new As4ConnectionFactory
        {
            OurPartyId = "urn:redb:as4:test:redb",
            ExternalHostName = "redb.test",
            SigningCertificate = As4Stand.KeyPair("redb"),
            DecryptionCertificates = { As4Stand.KeyPair("redb") },
            Partners =
            {
                new As4Partner
                {
                    Name = "holodeck-b",
                    PartyId = "org:holodeckb2b:example:company:B",
                    AgreementRef = "urn:redb:as4:interop",
                    Service = "urn:redb:as4:interop",
                    ServiceType = "org:holodeckb2b:services",
                    Action = "StoreMessage",
                    PartnerSigningCertificates = { As4Stand.Certificate("holodeck-b") },
                    PartnerEncryptionCertificate = As4Stand.Certificate("holodeck-b"),
                },
            },
        });

        var producer = context.GetEndpoint(
            redb.Route.As4.Fluent.As4.Send($"http://127.0.0.1:{RecorderPort}/holodeckb2b/as4").ConnectionFactory("node").Partner("holodeck-b"))
            .CreateProducer();
        await producer.Start();

        var payload = $"<invoice xmlns=\"urn:example\"><id>{Guid.NewGuid():N}</id></invoice>";
        var exchange = new redb.Route.Core.Exchange(new redb.Route.Core.Message(payload) { ContentType = "text/xml" });
        exchange.In.Headers[As4Headers.OriginalSender] = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:C1";
        exchange.In.Headers[As4Headers.FinalRecipient] = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:C4";

        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        Directory.GetFiles(Path.Combine(As4Stand.Root, "node-b", "data", "msg_in"), "pl-*")
            .Select(As4Stand.ReadShared)
            .Should().Contain(payload, "node B delivers the decrypted, decompressed payload");
    }

    private const int HolodeckAPort = 14090;
    private const int InboundPort = 15090;   // node A's P-Mode redb-inbound-push posts to host.docker.internal:15090/as4/in

    [As4LiveFact(HolodeckAPort)]
    public async Task HolodeckPushesToOurReceiver_ThePayloadArrives_AndHolodeckAcceptsOurReceipt()
    {
        // Node A calls back one fixed port: one test process at a time.
        await using var exclusive = await As4Stand.ExclusiveAsync("inbound-15090", TimeSpan.FromMinutes(5));
        await using var context = new redb.Route.Core.RouteContext();
        context.AddComponent(new As4Component());
        context.AddIdempotentRepository("dedup", new redb.Route.Processors.InMemoryIdempotentRepository());
        context.AddToRegistry("node", new As4ConnectionFactory
        {
            OurPartyId = "urn:redb:as4:test:redb",
            ExternalHostName = "redb.test",
            SigningCertificate = As4Stand.KeyPair("redb"),
            DecryptionCertificates = { As4Stand.KeyPair("redb") },
            Partners =
            {
                new As4Partner
                {
                    Name = "holodeck-a",
                    PartyId = "org:holodeckb2b:example:company:A",
                    AgreementRef = "urn:redb:as4:inbound",
                    Service = "urn:redb:as4:inbound",
                    ServiceType = "org:holodeckb2b:services",
                    Action = "StoreMessage",
                    PartnerSigningCertificates = { As4Stand.Certificate("holodeck-a") },
                    PartnerEncryptionCertificate = As4Stand.Certificate("holodeck-a"),
                },
            },
        });

        var received = new TaskCompletionSource<redb.Route.Abstractions.IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AddRoutes(r => r.From(redb.Route.As4.Fluent.As4.Receive("/as4/in").Host("0.0.0.0").Port(InboundPort).ConnectionFactory("node").IdempotentRepository("dedup"))
            .Process(e => received.TrySetResult(e)));
        await context.Start();

        // Submit on node A: a payload file and its message metadata in data/msg_out.
        var marker = Guid.NewGuid().ToString("N");
        var payload = $"<invoice xmlns=\"urn:example\"><id>{marker}</id></invoice>";
        var outbox = Path.Combine(As4Stand.Root, "data", "msg_out");
        await File.WriteAllTextAsync(Path.Combine(outbox, $"in-{marker}.xml"), payload);
        await File.WriteAllTextAsync(Path.Combine(outbox, $"in-{marker}.mmd"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <MessageMetaData xmlns="http://holodeck-b2b.org/schemas/2014/06/mmd">
                <CollaborationInfo>
                    <AgreementRef pmode="redb-inbound-push"/>
                    <ConversationId>urn:redb:as4:inbound:{marker}</ConversationId>
                </CollaborationInfo>
                <MessageProperties>
                    <Property name="originalSender">urn:oasis:names:tc:ebcore:partyid-type:unregistered:C1</Property>
                    <Property name="finalRecipient">urn:oasis:names:tc:ebcore:partyid-type:unregistered:C4</Property>
                </MessageProperties>
                <PayloadInfo deleteFilesAfterSubmit="true">
                    <PartInfo containment="attachment" mimeType="text/xml" location="in-{marker}.xml"/>
                </PayloadInfo>
            </MessageMetaData>
            """);

        var exchange = await received.Task.WaitAsync(TimeSpan.FromSeconds(90));
        Encoding.UTF8.GetString((byte[])exchange.In.Body!).Should().Be(payload);
        exchange.In.GetHeader<string>(As4Headers.Partner).Should().Be("holodeck-a");
        var messageId = exchange.In.GetHeader<string>(As4Headers.MessageId)!;

        // Node A notifies its back end of a receipt only after it verified it (signature and NRR).
        var inbox = Path.Combine(As4Stand.Root, "data", "msg_in");
        var accepted = false;
        for (var i = 0; i < 60 && !accepted; i++)
        {
            accepted = Directory.GetFiles(inbox, "mi-*.xml")
                .Any(f => As4Stand.ReadShared(f).Contains($"RefToMessageId>{messageId}<", StringComparison.Ordinal));
            if (!accepted) await Task.Delay(TimeSpan.FromSeconds(1));
        }
        accepted.Should().BeTrue($"Holodeck A should accept our receipt for {messageId}");
    }

    private static (string Uri, string Digest) Digest(XmlElement reference) =>
        (reference.GetAttribute("URI"), reference.GetElementsByTagName("DigestValue", WsSecurityNames.Ds)[0]!.InnerText);
}
