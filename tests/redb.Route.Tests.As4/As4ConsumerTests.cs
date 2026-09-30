using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using redb.Route.Abstractions;
using redb.Route.As4;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using As4Dsl = redb.Route.As4.Fluent.As4;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф4: the AS4 receiver on the shared Kestrel host. Loopback (our producer → our consumer) proves the whole
/// round trip and the receipt; raw requests prove every refusal answers with its ebMS code, the partner never
/// reads the text of one of our exceptions, and a failed unit of work — rollback-only included — is never
/// acknowledged with a receipt.
/// </summary>
public class As4ConsumerTests
{
    private static readonly X509Certificate2 NodeA = As4TestMessages.KeyPair("CN=a.as4.test");
    private static readonly X509Certificate2 NodeB = As4TestMessages.KeyPair("CN=b.as4.test");
    private static readonly X509Certificate2 Stranger = As4TestMessages.KeyPair("CN=stranger.as4.test");
    private const string Payload = "<invoice xmlns=\"urn:example\"><total>42</total></invoice>";

    // ── Loopback ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loopback_DeliversThePayloadAndHeaders_AndTheSenderGetsAVerifiedReceipt()
    {
        IExchange? received = null;
        await using var stand = await Stand.StartAsync(e => { received = e; return Task.CompletedTask; });

        var exchange = NewExchange();
        await stand.Send(exchange);

        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        received.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])received!.In.Body!).Should().Be(Payload);
        received.In.ContentType.Should().Be("text/xml");
        received.In.GetHeader<string>(As4Headers.Partner).Should().Be("a");
        received.In.GetHeader<string>(As4Headers.MessageId).Should().Be(exchange.In.GetHeader<string>(As4Headers.MessageId));
        received.In.GetHeader<string>(As4Headers.FromPartyId).Should().Be("urn:redb:a");
        received.In.GetHeader<string>(As4Headers.Action).Should().Be("Submit");
        received.In.GetHeader<string>(As4Headers.OriginalSender).Should().Be("C1");
        received.In.GetHeader<bool>(As4Headers.SignatureValid).Should().BeTrue();
        received.In.GetHeader<string>(As4Headers.SignerThumbprint).Should().Be(NodeA.Thumbprint);
    }

    [Fact]
    public async Task Principal_IsTheEnvelopeSigner_WithTheAgreementItAuthenticated()
    {
        // docs/as4/09 No. 10, as camel-cxf with WSS4J: the principal comes from the signature.
        System.Security.Claims.ClaimsPrincipal? principal = null;
        await using var stand = await Stand.StartAsync(e => { principal = ExchangePrincipal.Get(e); return Task.CompletedTask; });

        await stand.Send(NewExchange());

        var identity = principal!.Identities.First();
        identity.AuthenticationType.Should().Be(As4AuthenticationTypes.Signature);
        identity.IsAuthenticated.Should().BeTrue();
        identity.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value.Should().Be("a");
        identity.FindFirst(System.Security.Claims.ClaimTypes.Thumbprint)!.Value.Should().Be(NodeA.Thumbprint);
        identity.FindFirst(System.Security.Claims.ClaimTypes.Name)!.Value.Should().Be(NodeA.Subject);
        principal.Identities.Should().NotContain(i => i.AuthenticationType == As4AuthenticationTypes.ClientCertificate, "no TLS here");
    }

    [Fact]
    public async Task RouteFailure_IsAnsweredWithOther_WithoutTheExceptionText()
    {
        await using var stand = await Stand.StartAsync(_ => throw new InvalidOperationException(@"cannot open C:\secret\ledger.db"));

        var act = () => stand.Send(NewExchange());

        var error = (await act.Should().ThrowAsync<As4ErrorSignalException>()).Which;
        error.ErrorCode.Should().Be("EBMS:0004");
        error.Description.Should().NotContain("secret").And.NotContain("ledger");
        error.ErrorDetail.Should().StartWith("ref: ");
    }

    [Fact]
    public async Task RollbackOnly_IsNotAcknowledged()
    {
        await using var stand = await Stand.StartAsync(e => { e.MarkRollbackOnly(); return Task.CompletedTask; });

        var act = () => stand.Send(NewExchange());

        (await act.Should().ThrowAsync<As4ErrorSignalException>()).Which.ErrorCode.Should().Be("EBMS:0004");
    }

    [Fact]
    public async Task MalformedRequest_IsAnsweredWithValueInconsistent_AndItsOwnText()
    {
        await using var stand = await Stand.StartAsync(_ => throw new MalformedRequestException("element 'total' is not a number"));

        var act = () => stand.Send(NewExchange());

        var error = (await act.Should().ThrowAsync<As4ErrorSignalException>()).Which;
        error.ErrorCode.Should().Be("EBMS:0003");
        error.Description.Should().Be("element 'total' is not a number");
    }

    [Fact]
    public async Task UnknownSender_IsValueNotRecognized()
    {
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);

        var act = () => stand.Send(NewExchange(), from: "c");

        (await act.Should().ThrowAsync<As4ErrorSignalException>()).Which.ErrorCode.Should().Be("EBMS:0001");
    }

    // ── Duplicate detection (Ф5) ─────────────────────────────────────────────

    [Fact]
    public async Task Resend_OfADeliveredMessage_GetsAReceiptAgain_AndIsNotDeliveredTwice()
    {
        var deliveries = 0;
        var duplicates = new InMemoryIdempotentRepository();
        await using var stand = await Stand.StartAsync(_ => { Interlocked.Increment(ref deliveries); return Task.CompletedTask; }, duplicates);
        var (message, messageId) = SecuredRawMessage();

        var first = await stand.PostRaw(message);
        var second = await stand.PostRaw(message);

        first.Should().Be((HttpStatusCode.OK, (string?)null, true));
        second.Should().Be((HttpStatusCode.OK, (string?)null, true));
        deliveries.Should().Be(1);
        (await duplicates.Contains(messageId)).Should().BeTrue();
    }

    [Fact]
    public async Task Resend_AfterARouteFailure_IsDelivered()
    {
        var attempts = 0;
        await using var stand = await Stand.StartAsync(_ =>
            Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("first attempt fails") : Task.CompletedTask);
        var (message, _) = SecuredRawMessage();

        var first = await stand.PostRaw(message);
        var second = await stand.PostRaw(message);

        first.ErrorCode.Should().Be("EBMS:0004");
        second.Should().Be((HttpStatusCode.OK, (string?)null, true));
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Resend_AfterTheRequestWasAbortedMidRoute_IsDelivered()
    {
        var attempts = 0;
        await using var stand = await Stand.StartAsync(_ =>
            Interlocked.Increment(ref attempts) == 1 ? throw new OperationCanceledException("request aborted") : Task.CompletedTask);
        var (message, _) = SecuredRawMessage();

        using (await stand.PostBytes(message.ContentType, message.Body)) { }
        var second = await stand.PostRaw(message);

        second.Should().Be((HttpStatusCode.OK, (string?)null, true));
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Claim_IsTakenWithoutTheRequestsCancellation()
    {
        // A claim cut by the sender's disconnect could be written and then reported as failed, leaving the id marked for
        // a message the route never saw — its resend would get a receipt. The claim is a short state write, taken
        // uncancelled, as Confirm and Remove are.
        var duplicates = Substitute.For<IIdempotentRepository>();
        duplicates.Add(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask, duplicates);

        (await stand.PostRaw(SecuredRawMessage().Message)).Receipt.Should().BeTrue();

        await duplicates.Received(1).Add(Arg.Any<string>(), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task DuplicateStoreFailure_IsOther_AndNeverReachesTheRoute()
    {
        var reached = false;
        var duplicates = Substitute.For<IIdempotentRepository>();
        duplicates.Add(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException(@"cannot reach C:\secret\dedup.db")));
        await using var stand = await Stand.StartAsync(_ => { reached = true; return Task.CompletedTask; }, duplicates);
        var (message, _) = SecuredRawMessage();

        var (status, errorCode, receipt) = await stand.PostRaw(message);

        status.Should().Be(HttpStatusCode.OK);
        errorCode.Should().Be("EBMS:0004");
        receipt.Should().BeFalse();
        reached.Should().BeFalse();
    }

    // ── Streams ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamBody_HandsTheVerifiedPayloadAsAStream_ClosedWhenTheExchangeEnds()
    {
        Stream? body = null;
        string? read = null;
        await using var stand = await Stand.StartAsync(e =>
        {
            body = e.In.Body as Stream;
            read = new StreamReader(body!, leaveOpen: true).ReadToEnd();
            return Task.CompletedTask;
        }, streamBody: true);

        var (status, errorCode, receipt) = await stand.PostRaw(SecuredRawMessage().Message);

        (status, errorCode, receipt).Should().Be((HttpStatusCode.OK, (string?)null, true));
        read.Should().Be(Payload);
        body!.CanRead.Should().BeFalse("Exchange.DisposeAsync closes a Stream body");
    }

    [Fact]
    public async Task LargePayload_IsSpooledToDisk_NotHeldInMemory()
    {
        var large = new string('x', 300_000);
        bool? spooled = null;
        string? read = null;
        await using var stand = await Stand.StartAsync(e =>
        {
            var cache = (StreamCache)e.In.Body!;
            spooled = cache.IsSpooled;
            read = new StreamReader(cache, leaveOpen: true).ReadToEnd();
            return Task.CompletedTask;
        }, streamBody: true, spool: new redb.Route.Configuration.StreamCacheOptions { SpoolThreshold = 4096 });

        var (_, errorCode, receipt) = await stand.PostRaw(SecuredMessage(Encoding.UTF8.GetBytes(large)).Message);

        errorCode.Should().BeNull();
        receipt.Should().BeTrue();
        spooled.Should().BeTrue();
        read.Should().Be(large);
    }

    [Fact]
    public async Task SpoolThreshold_OfTheEngineOptions_ReachesTheReceiver()
    {
        bool? spooled = null;
        await using var stand = await Stand.StartAsync(e =>
        {
            spooled = ((StreamCache)e.In.Body!).IsSpooled;
            return Task.CompletedTask;
        }, streamBody: true, engine: new redb.Route.Configuration.RouteEngineOptions
        {
            StreamCaching = new redb.Route.Configuration.StreamCacheOptions { SpoolThreshold = 4096 },
        });

        var (_, errorCode, _) = await stand.PostRaw(SecuredMessage(Encoding.UTF8.GetBytes(new string('y', 50_000))).Message);

        errorCode.Should().BeNull();
        spooled.Should().BeTrue("50 KB is past the engine's 4 KB threshold, below the 128 KB default");
    }

    [Fact]
    public async Task SeveralPayloads_AreAs4PayloadStreams_ReleasedWithTheExchange()
    {
        IReadOnlyList<As4Payload>? payloads = null;
        List<string>? contents = null;
        await using var stand = await Stand.StartAsync(e =>
        {
            payloads = (IReadOnlyList<As4Payload>)e.In.Body!;
            contents = payloads.Select(p => new StreamReader(p.Content, leaveOpen: true).ReadToEnd()).ToList();
            return Task.CompletedTask;
        });

        var (_, errorCode, receipt) = await stand.PostRaw(SecuredMessage(Encoding.UTF8.GetBytes("<a/>"), Encoding.UTF8.GetBytes("<b/>")).Message);

        errorCode.Should().BeNull();
        receipt.Should().BeTrue();
        contents.Should().Equal("<a/>", "<b/>");
        payloads!.Should().OnlyContain(p => p.ContentType == "text/xml" && !p.Content.CanRead);
    }

    // ── Raw requests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnsignedMessage_IsPolicyNoncompliance_AndNeverReachesTheRoute()
    {
        var reached = false;
        await using var stand = await Stand.StartAsync(_ => { reached = true; return Task.CompletedTask; });

        var message = RawMessage();
        var (status, errorCode, _) = await stand.PostRaw(message.Write());

        status.Should().Be(HttpStatusCode.OK);
        errorCode.Should().Be("EBMS:0103");
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task WithoutFourCornerProperties_IsProcessingModeMismatch_AndNeverReachesTheRoute()
    {
        var reached = false;
        await using var stand = await Stand.StartAsync(_ => { reached = true; return Task.CompletedTask; });

        var (status, errorCode, receipt) = await stand.PostRaw(SecuredMessage(fourCorner: false, Encoding.UTF8.GetBytes(Payload)).Message);

        status.Should().Be(HttpStatusCode.OK);
        errorCode.Should().Be("EBMS:0010");
        receipt.Should().BeFalse();
        reached.Should().BeFalse();
    }

    [Theory]
    [InlineData("From")]
    [InlineData("To")]
    public async Task MoreThanOnePartyIdOnASide_IsProcessingModeMismatch(string side)
    {
        // eDelivery AS4 1.16 REQUIRES one eb:PartyId per side; phase4 and Holodeck refuse more with EBMS:0010.
        var reached = false;
        await using var stand = await Stand.StartAsync(_ => { reached = true; return Task.CompletedTask; });
        var message = RawMessage();
        var party = (XmlElement)message.Envelope.GetElementsByTagName(side, EbmsNamespaces.Eb)[0]!;
        var extra = message.Envelope.CreateElement("eb", "PartyId", EbmsNamespaces.Eb);
        extra.InnerText = "urn:redb:second";
        party.InsertBefore(extra, party.GetElementsByTagName("Role", EbmsNamespaces.Eb)[0]);
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);

        (await stand.PostRaw(message.Write())).ErrorCode.Should().Be("EBMS:0010");
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task PullRequest_IsProcessingModeMismatch_AsDomibusAnswersWithoutAPullProcess()
    {
        // Domibus (PullRequestLegConfigurationExtractor): no pull process for the mpc → EBMS:0010. Pull is out of scope.
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);
        var pull = new SignalMessage(new MessageInfo(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N") + "@a.example", null), null, [], EbmsNamespaces.DefaultMpc);
        var body = Encoding.UTF8.GetBytes(MessagingWriter.CreateEnvelope(pull).OuterXml);

        var response = await stand.PostBytes("application/soap+xml", body);
        var answer = SwaMessage.Read(response.Content.Headers.ContentType!.ToString(), await response.Content.ReadAsByteArrayAsync(), 1024 * 1024);

        MessagingReader.Read(answer.Envelope).SignalMessages.SelectMany(s => s.Errors).Single().ErrorCode.Should().Be("EBMS:0010");
    }

    [Theory]
    [InlineData("urn:example:other-mpc", "EBMS:0001")]
    [InlineData(EbmsNamespaces.DefaultMpc, null)]
    public async Task Mpc_OtherThanTheDefault_IsValueNotRecognized_AsDomibusMatchesItToTheLeg(string mpc, string? errorCode)
    {
        // Domibus (CachingPModeProvider.checkMpcMismatch): an mpc the leg does not name → EBMS:0001; the default passes.
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);
        var message = RawMessage();
        ((XmlElement)message.Envelope.GetElementsByTagName("UserMessage", EbmsNamespaces.Eb)[0]!).SetAttribute("mpc", mpc);
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);

        var (_, code, receipt) = await stand.PostRaw(message.Write());

        code.Should().Be(errorCode);
        receipt.Should().Be(errorCode is null);
    }

    [Fact]
    public async Task TamperedAfterSigning_IsFailedAuthentication()
    {
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);

        var message = RawMessage();
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        ((XmlElement)message.Envelope.GetElementsByTagName("ConversationId", EbmsNamespaces.Eb)[0]!).InnerText = "someone-else";

        (await stand.PostRaw(message.Write())).ErrorCode.Should().Be("EBMS:0101");
    }

    [Fact]
    public async Task EncryptedForSomeoneElse_IsFailedDecryption()
    {
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);

        var message = RawMessage();
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(Stranger), As4KeyReference.BinarySecurityToken);

        (await stand.PostRaw(message.Write())).ErrorCode.Should().Be("EBMS:0102");
    }

    [Fact]
    public async Task EnvelopeWithADoctype_IsASenderSoapFault()
    {
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);
        var body = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"aaaa\">]><soapenv:Envelope xmlns:soapenv=\"http://www.w3.org/2003/05/soap-envelope\"><soapenv:Body/></soapenv:Envelope>");

        var response = await stand.PostBytes("application/soap+xml", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("soapenv:Sender");
    }

    [Theory]
    [InlineData("Body")]
    [InlineData("Header")]
    public async Task EnvelopeWithoutBodyOrHeader_IsASenderSoapFault(string missing)
    {
        // Not a SOAP 1.2 envelope an AS4 message can be (SOAP 1.2 Part 1 §5): refused as the DTD is, before ebMS.
        var reached = false;
        await using var stand = await Stand.StartAsync(_ => { reached = true; return Task.CompletedTask; });
        var message = RawMessage();
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        var element = message.Envelope.GetElementsByTagName(missing, EbmsNamespaces.Soap12)[0]!;
        element.ParentNode!.RemoveChild(element);
        var (contentType, bytes) = message.Write();

        var response = await stand.PostBytes(contentType, bytes);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("soapenv:Sender");
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task TwoSecurityHeaders_IsPolicyNoncompliance_NotAServerError()
    {
        // WS-Security 1.1 §6.1: no two wsse:Security headers for one role. An ebMS error, not an unhandled 500.
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);
        var message = RawMessage();
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        var header = (XmlElement)message.Envelope.GetElementsByTagName("Header", EbmsNamespaces.Soap12)[0]!;
        header.AppendChild(message.Envelope.CreateElement("wsse", "Security", WsSecurityNames.Wsse));

        var (status, errorCode, receipt) = await stand.PostRaw(message.Write());

        status.Should().Be(HttpStatusCode.OK);
        errorCode.Should().Be("EBMS:0103");
        receipt.Should().BeFalse();
    }

    [Fact]
    public async Task UnexpectedFailure_IsAnsweredWithOther_AndCountedAsAnError()
    {
        // Anything the receiver did not foresee (here the spool directory is gone) is still an ebMS answer with a
        // reference, logged and counted — never a bare 500 the partner cannot tell from a network failure (BR-4).
        var spool = new redb.Route.Configuration.StreamCacheOptions { SpoolThreshold = 16, TempDirectory = Path.Combine(Path.GetTempPath(), "redb-as4-" + Guid.NewGuid().ToString("N"), "gone") };
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask, spool: spool);

        var (status, errorCode, receipt) = await stand.PostRaw(SecuredRawMessage().Message);

        status.Should().Be(HttpStatusCode.OK);
        errorCode.Should().Be("EBMS:0004");
        receipt.Should().BeFalse();
        stand.Errors.Should().Be(1);
    }

    [Theory]
    [InlineData("http://www.w3.org/2003/05/soap-envelope/role/none", false)]
    [InlineData("urn:example:gateway", false)]
    [InlineData("http://www.w3.org/2003/05/soap-envelope/role/next", true)]
    [InlineData("http://www.w3.org/2003/05/soap-envelope/role/ultimateReceiver", true)]
    public async Task MustUnderstandHeader_IsOursOnlyForTheRolesWePlay(string role, bool fault)
    {
        // SOAP 1.2 Part 1 §2.2: every node plays "next", the ultimate receiver also "ultimateReceiver"; a block for "none"
        // or another role is not addressed to us and must not fault.
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);
        var message = RawMessage();
        var header = (XmlElement)message.Envelope.GetElementsByTagName("Header", EbmsNamespaces.Soap12)[0]!;
        var block = message.Envelope.CreateElement("x", "Routing", "urn:example:routing");
        foreach (var (name, value) in new[] { ("mustUnderstand", "true"), ("role", role) })
        {
            var attribute = message.Envelope.CreateAttribute("soapenv", name, EbmsNamespaces.Soap12);
            attribute.Value = value;
            block.Attributes.Append(attribute);
        }
        header.AppendChild(block);
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        var (contentType, bytes) = message.Write();

        var response = await stand.PostBytes(contentType, bytes);

        if (fault)
            (await response.Content.ReadAsStringAsync()).Should().Contain("soapenv:MustUnderstand");
        else
            response.StatusCode.Should().Be(HttpStatusCode.OK, "a block for another role is left alone");
    }

    [Fact]
    public async Task UnknownMustUnderstandHeader_IsAMustUnderstandFault()
    {
        await using var stand = await Stand.StartAsync(_ => Task.CompletedTask);
        var message = RawMessage();
        var header = (XmlElement)message.Envelope.GetElementsByTagName("Header", EbmsNamespaces.Soap12)[0]!;
        var block = message.Envelope.CreateElement("x", "Routing", "urn:example:routing");
        var mustUnderstand = message.Envelope.CreateAttribute("soapenv", "mustUnderstand", EbmsNamespaces.Soap12);
        mustUnderstand.Value = "true";
        block.Attributes.Append(mustUnderstand);
        header.AppendChild(block);
        var (contentType, bytes) = message.Write();

        var response = await stand.PostBytes(contentType, bytes);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().Contain("soapenv:MustUnderstand");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static Exchange NewExchange()
    {
        var exchange = new Exchange(new Message(Payload) { ContentType = "text/xml" });
        exchange.In.Headers[As4Headers.OriginalSender] = "C1";
        exchange.In.Headers[As4Headers.FinalRecipient] = "C4";
        return exchange;
    }

    /// <summary>A user message from A to B, compressed, not yet signed or encrypted.</summary>
    private static SwaMessage RawMessage()
    {
        const string contentId = "p1@a.example";
        var message = SwaMessage.Create(As4TestMessages.UserMessage(Guid.NewGuid().ToString("N") + "@a.example", contentId,
            from: "urn:redb:a", to: "urn:redb:b", service: "urn:example", action: "Submit"));
        message.AddPart(contentId, "application/gzip", As4TestMessages.Gzip(Encoding.UTF8.GetBytes(Payload)));
        return message;
    }

    /// <summary>A user message from A to B with the given payloads (each gzipped), signed and encrypted as A sends it.</summary>
    private static ((string ContentType, byte[] Body) Message, string MessageId) SecuredMessage(params byte[][] payloads) =>
        SecuredMessage(fourCorner: true, payloads);

    private static ((string ContentType, byte[] Body) Message, string MessageId) SecuredMessage(bool fourCorner, params byte[][] payloads)
    {
        var ids = payloads.Select((_, i) => $"p{i}@a.example").ToArray();
        var message = SwaMessage.Create(As4TestMessages.UserMessage(Guid.NewGuid().ToString("N") + "@a.example", ids[0],
            from: "urn:redb:a", to: "urn:redb:b", service: "urn:example", action: "Submit", fourCorner: fourCorner, moreContentIds: ids[1..]));
        for (var i = 0; i < payloads.Length; i++)
            message.AddPart(ids[i], "application/gzip", As4TestMessages.Gzip(payloads[i]));
        var messageId = MessagingReader.Read(message.Envelope).UserMessages.Single().MessageInfo.MessageId;
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        return (message.Write(), messageId);
    }

    /// <summary>A user message from A to B, signed and encrypted as A sends it; the bytes resend unchanged.</summary>
    private static ((string ContentType, byte[] Body) Message, string MessageId) SecuredRawMessage()
    {
        var message = RawMessage();
        var messageId = MessagingReader.Read(message.Envelope).UserMessages.Single().MessageInfo.MessageId;
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        return (message.Write(), messageId);
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

    private static As4ConnectionFactory Node(string name, X509Certificate2 key, params As4Partner[] partners)
    {
        var node = new As4ConnectionFactory
        {
            OurPartyId = "urn:redb:" + name,
            ExternalHostName = name + ".example",
            SigningCertificate = key,
            DecryptionCertificates = { key },
        };
        foreach (var partner in partners) node.Partners.Add(partner);
        return node;
    }

    /// <summary>Node B receiving on a loopback port; nodes A and C (C unknown to B) can send to it.</summary>
    private sealed class Stand : IAsyncDisposable
    {
        private readonly RouteContext _context;
        private readonly int _port;
        private string _receiveUri = "";

        private Stand(RouteContext context, int port)
        {
            _context = context;
            _port = port;
        }

        /// <summary>Errors counted on the receive endpoint.</summary>
        public long Errors => ((IEndpointStatistics)_context.GetEndpoint(_receiveUri)).Errors;

        public static async Task<Stand> StartAsync(Func<IExchange, Task> route, IIdempotentRepository? duplicates = null,
            bool streamBody = false, redb.Route.Configuration.StreamCacheOptions? spool = null,
            redb.Route.Configuration.RouteEngineOptions? engine = null)
        {
            var port = global::redb.Route.Tests.Shared.TestPorts.Next();
            var context = new RouteContext(options: engine);
            context.AddComponent(new As4Component());
            context.AddIdempotentRepository("dedup", duplicates ?? new InMemoryIdempotentRepository());
            if (spool is not null) context.AddService(typeof(redb.Route.Configuration.StreamCacheOptions), spool);
            context.AddToRegistry("b", Node("b", NodeB, Partner("a", NodeA)));
            context.AddToRegistry("a", Node("a", NodeA, Partner("b", NodeB)));
            context.AddToRegistry("c", Node("c", Stranger, Partner("b", NodeB)));

            var receive = As4Dsl.Receive("/as4/in").Host("127.0.0.1").Port(port).ConnectionFactory("b").IdempotentRepository("dedup");
            if (streamBody) receive.StreamBody();
            context.AddRoutes(r => r.From(receive)
                .Process((e, _) => route(e)));
            await context.Start();
            return new Stand(context, port) { _receiveUri = receive.Build() };
        }

        public async Task Send(IExchange exchange, string from = "a")
        {
            var producer = _context.GetEndpoint(
                As4Dsl.Send($"http://127.0.0.1:{_port}/as4/in").ConnectionFactory(from).Partner("b").Timeout(15000)).CreateProducer();
            await producer.Start();
            await producer.Process(exchange);
        }

        public async Task<HttpResponseMessage> PostBytes(string contentType, byte[] body)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return await http.PostAsync($"http://127.0.0.1:{_port}/as4/in", content);
        }

        public async Task<(HttpStatusCode Status, string? ErrorCode, bool Receipt)> PostRaw((string ContentType, byte[] Body) message)
        {
            using var response = await PostBytes(message.ContentType, message.Body);
            var body = await response.Content.ReadAsByteArrayAsync();
            var answer = SwaMessage.Read(response.Content.Headers.ContentType!.ToString(), body, 1024 * 1024);
            var signals = MessagingReader.Read(answer.Envelope).SignalMessages;
            var error = signals.SelectMany(s => s.Errors).FirstOrDefault();
            return (response.StatusCode, error?.ErrorCode, signals.Any(s => s.IsReceipt));
        }

        public async ValueTask DisposeAsync() => await _context.DisposeAsync();
    }
}
