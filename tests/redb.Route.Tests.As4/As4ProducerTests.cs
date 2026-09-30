using System.Net;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.As4;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.As4.Security;
using redb.Route.As4.Signals;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф3: the AS4 producer against a loopback partner (<see cref="HttpListener"/>, as the AS2 producer tests do)
/// that decrypts and verifies what it gets and answers with a real signed non-repudiation receipt — or with an
/// ebMS error, nothing, a receipt that echoes the wrong digests, or one signed by a stranger.
/// </summary>
public class As4ProducerTests
{
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Us = As4TestMessages.KeyPair("CN=us.as4.test");
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Partner = As4TestMessages.KeyPair("CN=acme.as4.test");
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Stranger = As4TestMessages.KeyPair("CN=stranger.as4.test");
    private const string Payload = "<invoice xmlns=\"urn:example\"><total>42</total></invoice>";

    private enum Answer { Receipt, Error, Nothing, WrongDigest, SignedByStranger, Late, Huge }

    [Fact]
    public async Task Send_GetsAVerifiedReceipt_AndPutsTheOutcomeOnOut()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        var exchange = NewExchange();
        await producer.Process(exchange);

        var sentId = exchange.In.GetHeader<string>(As4Headers.MessageId);
        sentId.Should().EndWith("@us.example");
        exchange.Pattern.Should().Be(ExchangePattern.InOut);
        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        exchange.Out.GetHeader<string>(As4Headers.MessageId).Should().Be(sentId);
        exchange.Out.GetHeader<string>(As4Headers.Partner).Should().Be("acme");

        // What arrived at the partner: our header values and, after decrypt + verify + decompress, the payload.
        var user = fake.Received!.UserMessages.Single();
        user.MessageInfo.MessageId.Should().Be(sentId);
        user.From.PartyIds.Single().Value.Should().Be("urn:redb:us");
        user.To.PartyIds.Single().Value.Should().Be("urn:redb:acme");
        user.CollaborationInfo.Action.Should().Be("Submit");
        user.PropertyValue("originalSender").Should().Be("C1");
        Encoding.UTF8.GetString(fake.Payload!).Should().Be(Payload);
    }

    [Fact]
    public async Task Send_ByDefault_CarriesNoTimestamp_AsTheEDeliveryPolicyOfDomibus()
    {
        // Domibus' eDeliveryAS4Policy has no IncludeTimestamp and a Strict layout: a wsu:Timestamp fails it.
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        await producer.Process(NewExchange());

        fake.Envelope!.GetElementsByTagName("Timestamp", WsSecurityNames.Wsu).Count.Should().Be(0);
    }

    [Fact]
    public async Task Send_WithATimestampTtl_CarriesATimestamp()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port, p => p.TimestampTtl = TimeSpan.FromMinutes(5));
        await using var _ = context;

        await producer.Process(NewExchange());

        fake.Envelope!.GetElementsByTagName("Timestamp", WsSecurityNames.Wsu).Count.Should().Be(1);
    }

    [Fact]
    public async Task Send_ReusesTheMessageIdOnTheExchange_SoARedeliveryIsTheSameMessage()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        var exchange = NewExchange();
        exchange.In.Headers[As4Headers.MessageId] = "fixed-1@us.example";
        await producer.Process(exchange);

        fake.Received!.UserMessages.Single().MessageInfo.MessageId.Should().Be("fixed-1@us.example");
    }

    [Fact]
    public async Task Route_RedeliveryOnMissingReceipt_ResendsTheSameMessage()
    {
        await using var fake = new FakePartner(Answer.Nothing, Answer.Receipt);
        await using var context = NewContext();
        context.AddRoutes(r =>
        {
            r.OnException<As4ReceiptException>()
                .MaximumRedeliveries(1)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(10));

            r.From("direct://out")
                .To($"as4://127.0.0.1:{fake.Port}/as4?connectionFactory=node&partner=acme&timeout=15000");
        });
        await context.Start();
        var producer = context.GetEndpoint("direct://out").CreateProducer();
        await producer.Start();

        var exchange = NewExchange();
        await producer.Process(exchange);

        exchange.Exception.Should().BeNull();
        fake.ReceivedIds.Should().HaveCount(2);
        fake.ReceivedIds[1].Should().Be(fake.ReceivedIds[0]);
    }

    [Fact]
    public async Task Send_NoResponseWithinTheTimeout_IsMissingReceipt_AndRedeliverable()
    {
        await using var fake = new FakePartner(Answer.Late, Answer.Receipt);
        await using var context = NewContext();
        context.AddRoutes(r =>
        {
            r.OnException<As4ReceiptException>()
                .MaximumRedeliveries(1)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(10));

            r.From("direct://out")
                .To($"as4://127.0.0.1:{fake.Port}/as4?connectionFactory=node&partner=acme&timeout=500");
        });
        await context.Start();
        var producer = context.GetEndpoint("direct://out").CreateProducer();
        await producer.Start();

        var exchange = NewExchange();
        await producer.Process(exchange);

        exchange.Exception.Should().BeNull();
        exchange.Out!.GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
        fake.ReceivedIds.Should().HaveCount(2).And.OnlyContain(id => id == fake.ReceivedIds[0]);
    }

    [Fact]
    public async Task Send_AForwardOnlyStreamBody_IsSent_AndNotClosed()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;
        var stream = new ForwardOnlyStream(Encoding.UTF8.GetBytes(Payload));
        var exchange = NewExchange();
        exchange.In.Body = stream;

        await producer.Process(exchange);

        Encoding.UTF8.GetString(fake.Payload!).Should().Be(Payload);
        stream.Disposed.Should().BeFalse("the exchange owns its body stream, not the producer");
    }

    [Fact]
    public async Task Route_RedeliveryOfASeekableStreamBody_SendsTheSameBytes()
    {
        await using var fake = new FakePartner(Answer.Nothing, Answer.Receipt);
        await using var context = NewContext();
        context.AddRoutes(r =>
        {
            r.OnException<As4ReceiptException>()
                .MaximumRedeliveries(1)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(10));

            r.From("direct://out")
                .To($"as4://127.0.0.1:{fake.Port}/as4?connectionFactory=node&partner=acme&timeout=15000");
        });
        await context.Start();
        var producer = context.GetEndpoint("direct://out").CreateProducer();
        await producer.Start();
        var exchange = NewExchange();
        exchange.In.Body = new MemoryStream(Encoding.UTF8.GetBytes(Payload));

        await producer.Process(exchange);

        fake.ReceivedIds.Should().HaveCount(2);
        Encoding.UTF8.GetString(fake.Payload!).Should().Be(Payload, "the second attempt reads the stream from its start");
    }

    [Fact]
    public async Task Route_Redelivery_IsTheSameMessage_ByteForByte()
    {
        // A retransmission is the same message (AS4 reception awareness): Domibus answers a duplicate with the stored
        // receipt of the first transmission, whose non-repudiation digests only match the first signature.
        await using var fake = new FakePartner(Answer.Nothing, Answer.Receipt);
        await using var context = NewContext();
        context.AddRoutes(r =>
        {
            r.OnException<As4ReceiptException>()
                .MaximumRedeliveries(1)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(10));

            r.From("direct://out")
                .To($"as4://127.0.0.1:{fake.Port}/as4?connectionFactory=node&partner=acme&timeout=15000");
        });
        await context.Start();
        var producer = context.GetEndpoint("direct://out").CreateProducer();
        await producer.Start();

        var exchange = NewExchange();
        await producer.Process(exchange);

        exchange.Exception.Should().BeNull();
        fake.RequestHashes.Should().HaveCount(2);
        fake.RequestHashes[1].Should().Be(fake.RequestHashes[0], "the second attempt sends the bytes of the first");
    }

    [Fact]
    public async Task ANewExchange_WithTheSameMessageId_IsBuiltAfresh()
    {
        // The stored transmission belongs to one exchange; nothing is shared between exchanges.
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        foreach (var attempt in new[] { 1, 2 })
        {
            var exchange = NewExchange();
            exchange.In.Headers[As4Headers.MessageId] = "fixed-2@us.example";
            await producer.Process(exchange);
        }

        fake.RequestHashes.Should().HaveCount(2);
        fake.RequestHashes[1].Should().NotBe(fake.RequestHashes[0]);
    }

    [Fact]
    public async Task Route_RedeliveriesExhausted_GoToTheDeadLetterRoute()
    {
        await using var fake = new FakePartner(Answer.Nothing);
        await using var context = NewContext();
        string? deadMessageId = null;
        context.AddRoutes(r =>
        {
            r.OnException<As4ReceiptException>()
                .MaximumRedeliveries(2)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(10))
                .Handled()
                .To("direct://dead");

            r.From("direct://out")
                .To($"as4://127.0.0.1:{fake.Port}/as4?connectionFactory=node&partner=acme&timeout=15000");
            r.From("direct://dead")
                .Process(e => deadMessageId = e.In.GetHeader<string>(As4Headers.MessageId));
        });
        await context.Start();
        var producer = context.GetEndpoint("direct://out").CreateProducer();
        await producer.Start();

        await producer.Process(NewExchange());

        fake.ReceivedIds.Should().HaveCount(3).And.OnlyContain(id => id == fake.ReceivedIds[0]);
        deadMessageId.Should().Be(fake.ReceivedIds[0]);
    }

    [Fact]
    public async Task Send_PartnerAnswersWithAnError_ThrowsItsCode()
    {
        await using var fake = new FakePartner(Answer.Error);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        var act = () => producer.Process(NewExchange());

        (await act.Should().ThrowAsync<As4ErrorSignalException>()).Which.ErrorCode.Should().Be("EBMS:0010");
    }

    [Fact]
    public async Task Send_PartnerAnswersNothing_IsMissingReceipt()
    {
        await using var fake = new FakePartner(Answer.Nothing);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        var act = () => producer.Process(NewExchange());

        (await act.Should().ThrowAsync<As4ReceiptException>()).Which.Code.Should().Be(As4ErrorCode.MissingReceipt);
    }

    [Fact]
    public async Task Send_ResponseOverTheLimit_IsInvalidReceipt_WithoutReadingItAll()
    {
        await using var fake = new FakePartner(Answer.Huge);
        await using var context = NewContext();
        var producer = context.GetEndpoint($"as4://127.0.0.1:{fake.Port}/as4?connectionFactory=node&partner=acme&timeout=15000&maxResponseBodySize=1048576").CreateProducer();
        await producer.Start();

        var act = () => producer.Process(NewExchange());

        var error = (await act.Should().ThrowAsync<As4ReceiptException>()).Which;
        error.Code.Should().Be(As4ErrorCode.InvalidReceipt);
        error.Message.Should().Contain("1048576");
    }

    [Fact]
    public void Options_Validate_NonPositiveResponseLimit_Throws()
    {
        var act = () => new As4EndpointOptions { ConnectionFactory = "node", MaxResponseBodySize = 0 }.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Send_ReceiptEchoesOtherDigests_IsInvalidReceipt()
    {
        await using var fake = new FakePartner(Answer.WrongDigest);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        var act = () => producer.Process(NewExchange());

        var thrown = (await act.Should().ThrowAsync<As4ReceiptException>()).Which;
        thrown.Code.Should().Be(As4ErrorCode.InvalidReceipt);
        thrown.Detail.Should().Contain("non-repudiation");
    }

    [Fact]
    public async Task Send_ReceiptSignedByAStranger_IsInvalidReceipt()
    {
        await using var fake = new FakePartner(Answer.SignedByStranger);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        var act = () => producer.Process(NewExchange());

        var thrown = (await act.Should().ThrowAsync<As4ReceiptException>()).Which;
        thrown.Code.Should().Be(As4ErrorCode.InvalidReceipt);
        thrown.Detail.Should().Contain("signature");
    }

    [Fact]
    public async Task Send_WithoutFourCornerProperties_IsRefusedBeforeSending()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        var exchange = new Exchange(new Message(Payload) { ContentType = "text/xml" });
        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*originalSender*");
        fake.Received.Should().BeNull();
    }

    [Fact]
    public async Task Send_WithOurSigningCertificateExpired_IsRefusedBeforeSending()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var expired = As4TestMessages.KeyPair("CN=us.as4.test", DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));
        var (context, producer) = await Start(fake.Port, node: n => n.SigningCertificate = expired);
        await using var _ = context;

        var act = () => producer.Process(NewExchange());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*signing certificate*has expired*");
        fake.Received.Should().BeNull();
    }

    [Fact]
    public async Task Send_ToAPartnerWhoseEncryptionCertificateExpired_IsRefusedBeforeSending()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var expired = As4TestMessages.PublicOnly(
            As4TestMessages.KeyPair("CN=acme.as4.test", DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1)));
        var (context, producer) = await Start(fake.Port, p => p.PartnerEncryptionCertificate = expired);
        await using var _ = context;

        var act = () => producer.Process(NewExchange());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*encryption certificate*has expired*");
        fake.Received.Should().BeNull();
    }

    [Fact]
    public async Task Send_OverridingTheActionWithoutPermission_IsRefused()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port);
        await using var _ = context;

        var exchange = NewExchange();
        exchange.In.Headers[As4Headers.Action] = "Refund";
        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not allow overriding the action*");
    }

    [Fact]
    public async Task Send_WithRefToMessageId_UsesTheReplyLeg()
    {
        await using var fake = new FakePartner(Answer.Receipt);
        var (context, producer) = await Start(fake.Port, p => p.ReplyLeg = new As4Leg { Service = "urn:example:reply", Action = "Confirm" });
        await using var _ = context;

        var exchange = NewExchange();
        exchange.In.Headers[As4Headers.RefToMessageId] = "request-1@acme.example";
        await producer.Process(exchange);

        var user = fake.Received!.UserMessages.Single();
        user.MessageInfo.RefToMessageId.Should().Be("request-1@acme.example");
        user.CollaborationInfo.Service.Value.Should().Be("urn:example:reply");
        user.CollaborationInfo.Action.Should().Be("Confirm");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static Exchange NewExchange()
    {
        var exchange = new Exchange(new Message(Payload) { ContentType = "text/xml" });
        exchange.In.Headers[As4Headers.OriginalSender] = "C1";
        exchange.In.Headers[As4Headers.FinalRecipient] = "C4";
        return exchange;
    }

    private static async Task<(RouteContext Context, As4Producer Producer)> Start(int port, Action<As4Partner>? configure = null,
        Action<As4ConnectionFactory>? node = null)
    {
        var context = NewContext(configure, node);
        var producer = (As4Producer)context.GetEndpoint($"as4://127.0.0.1:{port}/as4?connectionFactory=node&partner=acme&timeout=15000").CreateProducer();
        await producer.Start();
        return (context, producer);
    }

    private static RouteContext NewContext(Action<As4Partner>? configure = null, Action<As4ConnectionFactory>? node = null)
    {
        var context = new RouteContext();
        context.AddComponent(new As4Component());

        var partner = new As4Partner
        {
            Name = "acme",
            PartyId = "urn:redb:acme",
            Service = "urn:example",
            Action = "Submit",
            PartnerSigningCertificates = { As4TestMessages.PublicOnly(Partner) },
            PartnerEncryptionCertificate = As4TestMessages.PublicOnly(Partner),
        };
        configure?.Invoke(partner);
        var factory = new As4ConnectionFactory
        {
            OurPartyId = "urn:redb:us",
            ExternalHostName = "us.example",
            SigningCertificate = Us,
            DecryptionCertificates = { Us },
            Partners = { partner },
        };
        node?.Invoke(factory);
        context.AddToRegistry("node", factory);
        return context;
    }

    /// <summary>
    /// A loopback AS4 partner answering one way or another — request by request, the last answer repeating;
    /// it records what it received.
    /// </summary>
    private sealed class FakePartner : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Answer[] _answers;
        private readonly Task _loop;

        public FakePartner(params Answer[] answers)
        {
            _answers = answers;
            Port = global::redb.Route.Tests.Shared.TestPorts.Next();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public EbmsMessaging? Received { get; private set; }

        /// <summary>The envelope of the last request, decrypted.</summary>
        public System.Xml.XmlDocument? Envelope { get; private set; }

        public byte[]? Payload { get; private set; }

        /// <summary>MessageId of every user message received, in order.</summary>
        public List<string> ReceivedIds { get; } = [];

        /// <summary>SHA-256 of every raw request body, in order: a retransmission is the same bytes.</summary>
        public List<string> RequestHashes { get; } = [];

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext http;
                try { http = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }      // listener stopped
                catch (ObjectDisposedException) { return; }     // listener disposed
                catch (ArgumentException) when (!_listener.IsListening) { return; }   // stopped before the first wait (net10: invalid handle)

                using var body = new MemoryStream();
                await http.Request.InputStream.CopyToAsync(body);
                RequestHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body.ToArray())));
                var request = SwaMessage.Read(http.Request.ContentType!, body.ToArray(), 1024 * 1024);

                As4SecurityEngine.Decrypt(request, [Partner]);
                var verification = As4SecurityEngine.Verify(request, [As4TestMessages.PublicOnly(Us)], TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
                Received = MessagingReader.Read(request.Envelope);
                Envelope = request.Envelope;
                Payload = As4TestMessages.Gunzip(request.Parts.Values.Single().ToArray());
                var messageId = Received.UserMessages.Single().MessageInfo.MessageId;
                var mode = _answers[Math.Min(ReceivedIds.Count, _answers.Length - 1)];
                ReceivedIds.Add(messageId);

                if (mode == Answer.Huge)
                {
                    // A proxy error page of no known length: chunked, far over any receipt.
                    http.Response.StatusCode = 200;
                    http.Response.ContentType = "text/html";
                    http.Response.SendChunked = true;
                    var junk = new byte[1 << 20];
                    try
                    {
                        for (var i = 0; i < 20; i++) await http.Response.OutputStream.WriteAsync(junk);
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }   // the sender stops reading at its limit; the listener may be stopping
                    // The test may dispose the listener between the check and the call: Abort then throws ObjectDisposedException.
                    try { if (_listener.IsListening) http.Response.Abort(); }
                    catch (ObjectDisposedException) { }   // the listener stopped: nothing left to abort

                    continue;
                }

                if (mode == Answer.Late)
                    continue;                                   // never answers; the listener aborts it on dispose

                if (mode == Answer.Nothing)
                {
                    http.Response.StatusCode = 202;
                    http.Response.Close();
                    continue;
                }

                var info = new MessageInfo(DateTimeOffset.UtcNow, MessageIdFactory.New("acme.example"), messageId);
                SignalMessage signal = mode == Answer.Error
                    ? new SignalMessage(info, null,
                        [new EbmsError("EBMS:0010", As4ErrorSeverity.Failure, "Processing", "ebms", messageId, "ProcessingModeMismatch", null, "no P-Mode")],
                        null)
                    : new SignalMessage(info, Receipts.NonRepudiation(mode == Answer.WrongDigest ? Tampered(verification.References) : verification.References), [], null);

                var answer = SwaMessage.Create(MessagingWriter.CreateEnvelope(signal));
                As4SecurityEngine.Sign(answer, mode == Answer.SignedByStranger ? Stranger : Partner,
                    As4KeyReference.BinarySecurityToken, timestampTtl: null, DateTimeOffset.UtcNow);
                var (contentType, bytes) = answer.Write();

                http.Response.StatusCode = 200;
                http.Response.ContentType = contentType;
                await http.Response.OutputStream.WriteAsync(bytes);
                http.Response.Close();
            }
        }

        private static IReadOnlyList<System.Xml.XmlElement> Tampered(IReadOnlyList<System.Xml.XmlElement> references)
        {
            var copies = references.Select(r => (System.Xml.XmlElement)r.CloneNode(deep: true)).ToList();
            copies[0].GetElementsByTagName("DigestValue", WsSecurityNames.Ds)[0]!.InnerText = Convert.ToBase64String(new byte[32]);
            return copies;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await _loop.ConfigureAwait(false);
        }
    }
}

/// <summary>A stream that reads forward only, as a network body does, and records whether it was closed.</summary>
internal sealed class ForwardOnlyStream(byte[] content) : Stream
{
    private readonly MemoryStream _inner = new(content, writable: false);

    public bool Disposed { get; private set; }

    public override bool CanRead => !Disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
