using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
/// An AS4 consumer and producer keep working after <c>Stop()</c> and <c>Start()</c> of the same route context (issue
/// redb-route #12: a component keeps its endpoints for the life of the context, so a stop must not close for good what
/// the next start needs). The stop gives the listener path back to the shared server, the start registers it again on
/// the cached endpoint; the duplicate store lives in the registry and still knows the messages taken before the stop;
/// a spooled body is written and read on both runs. AS4 has no pending-receipt table and no pull queue to outlive a
/// stop: the receipt is the HTTP response of the same request, and a pull request is answered EBMS:0010.
/// </summary>
public class As4RestartTests
{
    private const string Payload = "<invoice xmlns=\"urn:example\"><total>10</total></invoice>";

    private static readonly X509Certificate2 NodeA = As4TestMessages.KeyPair("CN=a.restart.as4.test");
    private static readonly X509Certificate2 NodeB = As4TestMessages.KeyPair("CN=b.restart.as4.test");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumer_and_producer_work_after_the_context_is_stopped_and_started_again(bool streamBody)
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        await using var context = new RouteContext();
        context.AddComponent(new As4Component());
        context.AddIdempotentRepository("dedup", new InMemoryIdempotentRepository());
        context.AddToRegistry("b", Node("b", NodeB, Partner("a", NodeA)));
        context.AddToRegistry("a", Node("a", NodeA, Partner("b", NodeB)));

        var delivered = new List<(string MessageId, string Body)>();
        var receive = As4Dsl.Receive("/as4/in").Host("127.0.0.1").Port(port).ConnectionFactory("b").IdempotentRepository("dedup");
        if (streamBody) receive.StreamBody();
        var receiveUri = receive.Build();
        context.AddRoutes(r => r.From(receiveUri).RouteId("as4-in")
            .Process(async (e, ct) =>
            {
                // A streamed body comes from the spool: it is read here, inside the exchange, as a route reads it.
                var body = e.In.Body is Stream stream
                    ? await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync(ct)
                    : Encoding.UTF8.GetString((byte[])e.In.Body!);
                lock (delivered) delivered.Add((e.In.GetHeader<string>(As4Headers.MessageId)!, body));
            }));
        context.AddRoutes(r => r.From("direct://send").RouteId("as4-out")
            .To(As4Dsl.Send($"http://127.0.0.1:{port}/as4/in").ConnectionFactory("a").Partner("b").Timeout(15000)));

        async Task<string> SendAsync()
        {
            var producer = context.GetEndpoint("direct://send").CreateProducer();
            await producer.Start();
            var exchange = NewExchange();
            await producer.Process(exchange);
            exchange.Exception.Should().BeNull("the message goes out and its receipt comes back on every run");
            (exchange.Out ?? exchange.In).GetHeader<bool>(As4Headers.ReceiptValid).Should().BeTrue();
            return exchange.In.GetHeader<string>(As4Headers.MessageId)!;
        }

        // A message as a partner's access point sends it, kept as bytes so that it can be sent again unchanged.
        var (resend, resendId) = SecuredMessage();

        await context.Start();
        var first = await SendAsync();
        (await PostAsync(port, resend)).Should().Be((HttpStatusCode.OK, (string?)null, true));

        await context.Stop();
        await context.Start();

        context.GetRoute("as4-in")!.Status.Should().Be(RouteStatus.Started);
        context.GetRoute("as4-out")!.Status.Should().Be(RouteStatus.Started);
        var second = await SendAsync();
        // Sent again after the restart: the duplicate store still knows it, so it gets its receipt and is not delivered.
        (await PostAsync(port, resend)).Should().Be((HttpStatusCode.OK, (string?)null, true));

        lock (delivered)
        {
            delivered.Select(d => d.MessageId).Should().Equal([first, resendId, second]);
            delivered.Should().OnlyContain(d => d.Body == Payload);
        }
        ((IEndpointStatistics)context.GetEndpoint(receiveUri)).Errors.Should().Be(0);
    }

    private static Exchange NewExchange()
    {
        var exchange = new Exchange(new Message(Payload) { ContentType = "text/xml" });
        exchange.In.Headers[As4Headers.OriginalSender] = "C1";
        exchange.In.Headers[As4Headers.FinalRecipient] = "C4";
        return exchange;
    }

    /// <summary>A user message from A to B, compressed, signed and encrypted as A sends it; the bytes resend unchanged.</summary>
    private static ((string ContentType, byte[] Body) Message, string MessageId) SecuredMessage()
    {
        const string contentId = "p1@a.example";
        var message = SwaMessage.Create(As4TestMessages.UserMessage(Guid.NewGuid().ToString("N") + "@a.example", contentId,
            from: "urn:redb:a", to: "urn:redb:b", service: "urn:example", action: "Submit"));
        message.AddPart(contentId, "application/gzip", As4TestMessages.Gzip(Encoding.UTF8.GetBytes(Payload)));
        var messageId = MessagingReader.Read(message.Envelope).UserMessages.Single().MessageInfo.MessageId;
        As4SecurityEngine.Sign(message, NodeA, As4KeyReference.BinarySecurityToken, null, DateTimeOffset.UtcNow);
        As4SecurityEngine.Encrypt(message, As4TestMessages.PublicOnly(NodeB), As4KeyReference.BinarySecurityToken);
        return (message.Write(), messageId);
    }

    /// <summary>Posts a message as raw bytes and reads the signal that answers it.</summary>
    private static async Task<(HttpStatusCode Status, string? ErrorCode, bool Receipt)> PostAsync(int port,
        (string ContentType, byte[] Body) message)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var content = new ByteArrayContent(message.Body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(message.ContentType);
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/as4/in", content);
        var body = await response.Content.ReadAsByteArrayAsync();
        var answer = SwaMessage.Read(response.Content.Headers.ContentType!.ToString(), body, 1024 * 1024);
        var signals = MessagingReader.Read(answer.Envelope).SignalMessages;
        var error = signals.SelectMany(s => s.Errors).FirstOrDefault();
        return (response.StatusCode, error?.ErrorCode, signals.Any(s => s.IsReceipt));
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
}
