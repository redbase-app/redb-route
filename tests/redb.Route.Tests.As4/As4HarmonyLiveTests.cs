using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using redb.Route.As4;
using redb.Route.Components;
using As4Dsl = redb.Route.As4.Fluent.As4;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф9, live against a second, independent MSH: Harmony Access Point 2.6.2 (NIIS), whose MSH is Domibus 5.1 — the
/// eDelivery reference implementation. The stand runs on the shared server (C:\Work\yaml\as4\harmony\README.md) and is
/// reached through SSH tunnels: 127.0.0.1:18480 is Harmony (18080 on the server), and Harmony reaches our receiver on 15090 through the
/// reverse tunnel. Skipped when the tunnel is down, unless <c>REDB_AS4_INTEROP=1</c>.
/// </summary>
public class As4HarmonyLiveTests
{
    private const int HarmonyPort = 18480;   // local end of the tunnel; 18080 here is the Soap agent's soap-echo stand
    private const int InboundPort = 15090;
    private const string PartyIdType = "urn:oasis:names:tc:ebcore:partyid-type:unregistered";

    [As4LiveTheory(HarmonyPort)]
    [InlineData(As4KeyReference.BinarySecurityToken)]
    [InlineData(As4KeyReference.IssuerSerial)]
    [InlineData(As4KeyReference.KeyIdentifier)]
    public async Task WeSendToHarmony_ItVerifiesAndReceives_AndWeVerifyItsReceipt(As4KeyReference form)
    {
        await using var context = new redb.Route.Core.RouteContext();
        context.AddComponent(new As4Component());
        context.AddToRegistry("node", Node(partner =>
        {
            partner.Name = "harmony";
            partner.Service = "urn:redb:as4:interop";
            partner.ServiceType = "urn:redb:services";
            partner.Action = "StoreMessage";
            partner.KeyReference = form;
        }));
        var producer = context.GetEndpoint(As4Dsl.Send($"http://127.0.0.1:{HarmonyPort}/services/msh")
            .ConnectionFactory("node").Partner("harmony").Timeout(60000)).CreateProducer();
        await producer.Start();

        var exchange = new redb.Route.Core.Exchange(new redb.Route.Core.Message(
            $"<invoice xmlns=\"urn:example\"><id>{Guid.NewGuid():N}</id></invoice>") { ContentType = "text/xml" });
        exchange.In.Headers[As4Headers.OriginalSender] = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:C1";
        exchange.In.Headers[As4Headers.FinalRecipient] = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:C4";
        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue("Harmony's receipt is signed and names our digests");
        var messageId = exchange.In.GetHeader<string>(As4Headers.MessageId)!;
        using var harmony = await HarmonyClient.LoginAsync();
        (await harmony.StatusAsync(messageId)).Should().Be("RECEIVED");
    }

    [As4LiveFact(HarmonyPort)]
    public async Task UncompressedXml_IsSignedOverItsCanonicalForm_AndHarmonyVerifiesIt()
    {
        // The attachment is signed after Exclusive C14N (SwA 1.1 §5.4.2); raw and canonical bytes differ on purpose.
        var (context, producer) = await Sender(partner => partner.CompressPayloads = false);
        await using var _ = context;
        var exchange = NewExchange("<?xml version='1.0'?>\n<invoice   xmlns='urn:example'  id='1'>\n  <total>42</total>\n</invoice>\n");

        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        using var harmony = await HarmonyClient.LoginAsync();
        (await harmony.StatusAsync(exchange.In.GetHeader<string>(As4Headers.MessageId)!)).Should().Be("RECEIVED");
    }

    [As4LiveFact(HarmonyPort)]
    public async Task Redelivery_OfTheSameExchange_IsAcknowledgedAgain_WithTheStoredReceipt()
    {
        // A redelivery runs on the same exchange (OnException). Domibus answers the duplicate with the stored receipt of
        // the first transmission, whose NRR digests only match if the retransmission is the same message.
        var (context, producer) = await Sender(_ => { });
        await using var _ = context;
        var exchange = NewExchange("<invoice xmlns=\"urn:example\"/>");

        await producer.Process(exchange);
        var firstReceipt = exchange.Out!.GetHeader<string>(As4Headers.ReceiptMessageId);
        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue("the stored receipt names the digests of the same message");
        exchange.Out.GetHeader<string>(As4Headers.ReceiptMessageId).Should().Be(firstReceipt, "Domibus answers a duplicate with the receipt it stored");
    }

    [As4LiveFact(HarmonyPort)]
    public async Task HarmonySendsToUs_TheDocumentArrives_AndHarmonyAcceptsOurReceipt()
    {
        // Harmony calls back one fixed port, shared with the Holodeck inbound test: one test process at a time.
        await using var exclusive = await As4Stand.ExclusiveAsync("inbound-15090", TimeSpan.FromMinutes(5));
        await using var context = new redb.Route.Core.RouteContext();
        context.AddComponent(new As4Component());
        context.AddIdempotentRepository("dedup", new redb.Route.Processors.InMemoryIdempotentRepository());
        context.AddToRegistry("node", Node(partner =>
        {
            // Domibus' test message is the ebMS 3.0 test service (Ebms3Constants.TEST_SERVICE / TEST_ACTION).
            partner.Name = "harmony-test";
            partner.Service = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/service";
            partner.Action = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/test";
        }));
        var received = new TaskCompletionSource<redb.Route.Abstractions.IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AddRoutes(r => r.From(As4Dsl.Receive("/as4/in").Host("127.0.0.1").Port(InboundPort).ConnectionFactory("node").IdempotentRepository("dedup"))
            .Process(e => received.TrySetResult(e)));
        await context.Start();

        using var harmony = await HarmonyClient.LoginAsync();
        var messageId = await harmony.SendTestMessageAsync(receiver: "redb");

        var exchange = await received.Task.WaitAsync(TimeSpan.FromSeconds(90));
        exchange.In.GetHeader<string>(As4Headers.MessageId).Should().Be(messageId);
        exchange.In.GetHeader<string>(As4Headers.Partner).Should().Be("harmony-test");
        exchange.In.ContentType.Should().Be("text/xml");

        // Domibus marks a sent message ACKNOWLEDGED once it has verified the receipt (signature and NRR).
        string? status = null;
        for (var i = 0; i < 60 && status != "ACKNOWLEDGED"; i++)
        {
            status = await harmony.StatusAsync(messageId);
            if (status != "ACKNOWLEDGED") await Task.Delay(TimeSpan.FromSeconds(1));
        }
        status.Should().Be("ACKNOWLEDGED", $"Harmony should accept our receipt for {messageId}");
    }

    [As4LiveTheory(HarmonyPort)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeveralPayloads_UnderOurContentIds_AreReceivedByHarmony(bool compress)
    {
        // The payload profile of the eDelivery conformance tests: an XML leading document and a non-XML one, each under
        // the Content-ID the producer chose, with its CharacterSet part property.
        var (context, producer) = await Sender(partner => partner.CompressPayloads = compress);
        await using var _ = context;
        var run = Guid.NewGuid().ToString("N");
        var exchange = NewExchange("unused");
        exchange.In.Body = new List<As4Payload>
        {
            new($"xmlpayload-{run}@redb", "application/xml",
                new MemoryStream(Encoding.UTF8.GetBytes("<invoice xmlns=\"urn:example\">\n  <id>1</id>\n</invoice>")),
                new Dictionary<string, string> { ["CharacterSet"] = "UTF-8" }),
            new($"custompayload-{run}@redb", "application/octet-stream", new MemoryStream([0, 1, 2, 3, 250, 251, 252]),
                new Dictionary<string, string>()),
            new(null, "application/octet-stream", new MemoryStream(Encoding.UTF8.GetBytes("third")), new Dictionary<string, string>()),
        };
        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue("Harmony's receipt names the digests of every part");
        using var harmony = await HarmonyClient.LoginAsync();
        (await harmony.StatusAsync(exchange.In.GetHeader<string>(As4Headers.MessageId)!)).Should().Be("RECEIVED");
    }

    [As4LiveFact(HarmonyPort)]
    public async Task AMessageWithoutPayloads_IsReceivedByHarmony()
    {
        // The Notify command of the conformance tests has no payload: a simple SOAP message, signed, nothing to encrypt.
        var (context, producer) = await Sender(_ => { });
        await using var _ = context;
        var exchange = NewExchange("unused");
        exchange.In.Body = Array.Empty<As4Payload>();
        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        using var harmony = await HarmonyClient.LoginAsync();
        (await harmony.StatusAsync(exchange.In.GetHeader<string>(As4Headers.MessageId)!)).Should().Be("RECEIVED");
    }

    [As4LiveFact(HarmonyPort)]
    public async Task AnEmptyPayload_IsAWholeGzipMember_AndIsReceivedByHarmony()
    {
        // From .NET 9 on GZipStream writes nothing for empty content; zero bytes are not gzip to Domibus.
        var (context, producer) = await Sender(_ => { });
        await using var _ = context;
        var exchange = NewExchange("");
        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        using var harmony = await HarmonyClient.LoginAsync();
        (await harmony.StatusAsync(exchange.In.GetHeader<string>(As4Headers.MessageId)!)).Should().Be("RECEIVED");
    }

    [As4LiveTheory(HarmonyPort)]
    [InlineData(true, null)]
    [InlineData(false, "EBMS:0103")]
    public async Task AnEmptyBodyLeftOutOfTheSignature_IsRefusedByDomibus_AsItsPolicyRequiresTheBodySigned(bool coverBody, string? expectedError)
    {
        // What the reference implementation answers to the conformance test "emptyBodyNotReferencedInSignature": its
        // WS-SecurityPolicy (eDeliveryAS4Policy.xml) lists sp:Body among the signed parts. The same hand-built message
        // with the body covered is the control: it is received.
        const string contentId = "p1@redb.test";
        var envelope = As4TestMessages.UserMessage(Guid.NewGuid().ToString("N") + "@redb.test", contentId,
            from: "redb", to: "harmony", agreementRef: "urn:redb:as4:interop",
            service: "urn:redb:as4:interop", serviceType: "urn:redb:services", action: "StoreMessage");
        foreach (System.Xml.XmlElement partyId in envelope.GetElementsByTagName("PartyId", redb.Route.As4.Messaging.EbmsNamespaces.Eb))
            partyId.SetAttribute("type", PartyIdType);
        var message = redb.Route.As4.Mime.SwaMessage.Create(envelope);
        message.AddPart(contentId, "application/gzip", As4TestMessages.Gzip(Encoding.UTF8.GetBytes("<invoice xmlns=\"urn:example\"/>")));
        redb.Route.As4.Security.As4Signature.Sign(message, As4Stand.KeyPair("redb"), As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow, coverBody);
        redb.Route.As4.Security.As4SecurityEngine.Encrypt(message, As4Stand.Certificate("harmony_gw"), As4KeyReference.BinarySecurityToken);
        var (contentType, body) = message.Write();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        using var response = await http.PostAsync($"http://127.0.0.1:{HarmonyPort}/services/msh", content);
        var answerBytes = await response.Content.ReadAsByteArrayAsync();
        var answer = redb.Route.As4.Mime.SwaMessage.Read(response.Content.Headers.ContentType!.ToString(), answerBytes, 1024 * 1024);
        var signals = redb.Route.As4.Messaging.MessagingReader.Read(answer.Envelope).SignalMessages;

        signals.SelectMany(s => s.Errors).FirstOrDefault()?.ErrorCode.Should().Be(expectedError);
        signals.Any(s => s.IsReceipt).Should().Be(expectedError is null);
    }

    [As4LiveTheory(HarmonyPort)]
    [InlineData(false)]   // Harmony's certificate pinned: its receipt's path token is read for the certificate it is for
    [InlineData(true)]    // Harmony's certificate as the trust anchor: its receipt must come with a path, validated
    public async Task AMessageSignedWithACertificationPath_IsReceivedByHarmony_UnderItsPkiPathPolicy(bool trustAnAuthority)
    {
        // The leg redbPushPkiPath runs under eDeliveryAS4Policy_BST_PKIP.xml: the signing token is an X509PKIPathv1
        // BinarySecurityToken in both directions. The stand's certificates are their own authorities: paths of one.
        var (context, producer) = await Sender(partner =>
        {
            partner.Action = "StoreMessagePkiPath";
            partner.KeyReference = As4KeyReference.PkiPath;
            if (trustAnAuthority)
            {
                partner.PartnerSigningCertificates.Clear();
                partner.PartnerTrustAnchors.Add(As4Stand.Certificate("harmony_gw"));
            }
        });
        await using var _ = context;
        var exchange = NewExchange($"<invoice xmlns=\"urn:example\"><id>{Guid.NewGuid():N}</id></invoice>");
        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        using var harmony = await HarmonyClient.LoginAsync();
        (await harmony.StatusAsync(exchange.In.GetHeader<string>(As4Headers.MessageId)!)).Should().Be("RECEIVED");
    }

    [As4LiveFact(HarmonyPort)]
    public async Task APathOfThreeCertificates_RootIssuingAuthoritySigner_IsReadByDomibusInItsOrder()
    {
        // The stand's own certificates are their own authorities; this signer is issued under two (keys/pki-root.crt,
        // pki-issuing.crt), given here out of order. WSS4J takes the signer from the path by its position: a path
        // written in the wrong order would have Domibus verify the signature with an authority's key.
        await using var context = new redb.Route.Core.RouteContext();
        context.AddComponent(new As4Component());
        context.AddToRegistry("node", new As4ConnectionFactory
        {
            OurPartyId = "redb_pki",
            OurPartyIdType = PartyIdType,
            ExternalHostName = "redb.test",
            SigningCertificate = As4Stand.KeyPair("redb_pki"),
            SigningCertificateChain = { As4Stand.Certificate("pki-root"), As4Stand.Certificate("pki-issuing") },
            DecryptionCertificates = { As4Stand.KeyPair("redb_pki") },
            Partners =
            {
                new As4Partner
                {
                    Name = "harmony",
                    PartyId = "harmony",
                    PartyIdType = PartyIdType,
                    AgreementRef = "urn:redb:as4:interop",
                    Service = "urn:redb:as4:interop",
                    ServiceType = "urn:redb:services",
                    Action = "StoreMessagePkiPath",
                    KeyReference = As4KeyReference.PkiPath,
                    PartnerTrustAnchors = { As4Stand.Certificate("harmony_gw") },
                    PartnerEncryptionCertificate = As4Stand.Certificate("harmony_gw"),
                },
            },
        });
        var producer = context.GetEndpoint(As4Dsl.Send($"http://127.0.0.1:{HarmonyPort}/services/msh")
            .ConnectionFactory("node").Partner("harmony").Timeout(60000)).CreateProducer();
        await producer.Start();

        var exchange = NewExchange($"<invoice xmlns=\"urn:example\"><id>{Guid.NewGuid():N}</id></invoice>");
        await producer.Process(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        using var harmony = await HarmonyClient.LoginAsync();
        (await harmony.StatusAsync(exchange.In.GetHeader<string>(As4Headers.MessageId)!)).Should().Be("RECEIVED");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static async Task<(redb.Route.Core.RouteContext Context, redb.Route.Abstractions.IProducer Producer)> Sender(Action<As4Partner> configure)
    {
        var context = new redb.Route.Core.RouteContext();
        context.AddComponent(new As4Component());
        context.AddToRegistry("node", Node(partner =>
        {
            partner.Name = "harmony";
            partner.Service = "urn:redb:as4:interop";
            partner.ServiceType = "urn:redb:services";
            partner.Action = "StoreMessage";
            configure(partner);
        }));
        var producer = context.GetEndpoint(As4Dsl.Send($"http://127.0.0.1:{HarmonyPort}/services/msh")
            .ConnectionFactory("node").Partner("harmony").Timeout(60000)).CreateProducer();
        await producer.Start();
        return (context, producer);
    }

    private static redb.Route.Core.Exchange NewExchange(string xml)
    {
        var exchange = new redb.Route.Core.Exchange(new redb.Route.Core.Message(xml) { ContentType = "text/xml" });
        exchange.In.Headers[As4Headers.OriginalSender] = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:C1";
        exchange.In.Headers[As4Headers.FinalRecipient] = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:C4";
        return exchange;
    }

    private static As4ConnectionFactory Node(Action<As4Partner> configure)
    {
        var partner = new As4Partner
        {
            PartyId = "harmony",
            PartyIdType = PartyIdType,
            AgreementRef = "urn:redb:as4:interop",
            PartnerSigningCertificates = { As4Stand.Certificate("harmony_gw") },
            PartnerEncryptionCertificate = As4Stand.Certificate("harmony_gw"),
        };
        configure(partner);
        return new As4ConnectionFactory
        {
            OurPartyId = "redb",
            OurPartyIdType = PartyIdType,
            ExternalHostName = "redb.test",
            SigningCertificate = As4Stand.KeyPair("redb"),
            DecryptionCertificates = { As4Stand.KeyPair("redb") },
            Partners = { partner },
        };
    }

    /// <summary>The admin REST API of the Harmony stand (Domibus <c>/rest</c>): session cookie plus the XSRF header.</summary>
    private sealed class HarmonyClient : IDisposable
    {
        private readonly CookieContainer _cookies = new();
        private readonly HttpClient _http;

        private HarmonyClient()
        {
            _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{HarmonyPort}/"),
                Timeout = TimeSpan.FromSeconds(60),
            };
        }

        public static async Task<HarmonyClient> LoginAsync()
        {
            var client = new HarmonyClient();
            (await client._http.GetAsync("")).EnsureSuccessStatusCode();   // issues the XSRF cookie
            using var login = await client.PostJsonAsync("rest/security/authentication", new { username = "harmony", password = "teststand" });
            login.EnsureSuccessStatusCode();
            return client;
        }

        public async Task<string> SendTestMessageAsync(string receiver)
        {
            using var response = await PostJsonAsync("rest/testservice",
                new { sender = "harmony", receiver, receiverType = PartyIdType });
            var body = await response.Content.ReadAsStringAsync();
            response.IsSuccessStatusCode.Should().BeTrue($"Harmony refused the test message: {body}");
            return Json(body).GetString()!;
        }

        public async Task<string?> StatusAsync(string messageId)
        {
            var body = await _http.GetStringAsync($"rest/messagelog?messageId={Uri.EscapeDataString(messageId)}&page=0&pageSize=10&fields=messageId&fields=messageStatus");
            var messages = Json(body).GetProperty("messageLogEntries");
            return messages.GetArrayLength() == 0 ? null : messages[0].GetProperty("messageStatus").GetString();
        }

        private async Task<HttpResponseMessage> PostJsonAsync(string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            var xsrf = _cookies.GetCookies(_http.BaseAddress!)["XSRF-TOKEN"]?.Value;
            if (xsrf is not null) request.Headers.Add("X-XSRF-TOKEN", xsrf);
            return await _http.SendAsync(request);
        }

        /// <summary>Domibus prefixes JSON with the anti-JSON-hijacking line <c>)]}',</c>.</summary>
        private static JsonElement Json(string body) =>
            JsonDocument.Parse(body.StartsWith(")]}'", StringComparison.Ordinal) ? body[(body.IndexOf('\n') + 1)..] : body).RootElement;

        public void Dispose() => _http.Dispose();
    }
}
