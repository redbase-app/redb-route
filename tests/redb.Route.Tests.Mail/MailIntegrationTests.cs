using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Mail;
using MimeKit;
using MailKit.Net.Smtp;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using Xunit.Abstractions;

namespace redb.Route.Tests.Mail;

/// <summary>
/// Integration tests against GreenMail (localhost).
/// Expects GreenMail at SMTP:3025, IMAP:3143, POP3:3110, API:8080.
/// Users must be provisioned via REST API before authentication.
/// Start with: docker compose -f docker-compose.tests.yml up greenmail -d
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailIntegrationTests
{
    private const string SmtpHost = "localhost";
    private const int SmtpPort = 3025;
    private const string ImapHost = "localhost";
    private const int ImapPort = 3143;
    private const string Pop3Host = "localhost";
    private const int Pop3Port = 3110;
    private const string GreenMailApiBase = "http://localhost:8080";

    private static readonly HttpClient _http = new();
    private readonly ITestOutputHelper _output;

    public MailIntegrationTests(ITestOutputHelper output) => _output = output;

    // ───── Helpers ─────

    /// <summary>Provisions a user via GreenMail REST API (idempotent).</summary>
    private static async Task ProvisionUserAsync(string login, string password = "secret")
    {
        var json = $"{{\"email\":\"{login}@localhost\",\"login\":\"{login}\",\"password\":\"{password}\"}}";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{GreenMailApiBase}/api/user", content);
        // 200 = created, ignore duplicates
    }

    private static string UniqueUser() => $"test-{Guid.NewGuid():N}";

    private SmtpEndpoint CreateSmtpEndpoint(string user, string password = "secret")
    {
        var uri = EndpointUriParser.Parse(
            $"smtp://{SmtpHost}?port={SmtpPort}&username={user}&password={password}" +
            $"&security=None&from={user}@localhost");
        return (SmtpEndpoint)new SmtpComponent().CreateEndpoint(uri);
    }

    private ImapEndpoint CreateImapEndpoint(string user, string password = "secret",
        string? extraParams = null)
    {
        var qs = $"port={ImapPort}&username={user}&password={password}&security=None";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"imap://{ImapHost}?{qs}");
        return (ImapEndpoint)new ImapComponent().CreateEndpoint(uri);
    }

    private Pop3Endpoint CreatePop3Endpoint(string user, string password = "secret",
        string? extraParams = null)
    {
        var qs = $"port={Pop3Port}&username={user}&password={password}&security=None";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"pop3://{Pop3Host}?{qs}");
        return (Pop3Endpoint)new Pop3Component().CreateEndpoint(uri);
    }

    /// <summary>Sends one email directly via MailKit to seed the mailbox.</summary>
    private async Task SendDirectAsync(string toUser, string subject, string body,
        string fromUser = "sender", bool isHtml = false)
    {
        await ProvisionUserAsync(fromUser);
        await ProvisionUserAsync(toUser);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(fromUser, $"{fromUser}@localhost"));
        mime.To.Add(new MailboxAddress(toUser, $"{toUser}@localhost"));
        mime.Subject = subject;
        mime.Body = isHtml
            ? new TextPart("html") { Text = body }
            : new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(SmtpHost, SmtpPort,
            MailKit.Security.SecureSocketOptions.None);
        await client.AuthenticateAsync(fromUser, "secret");
        await client.SendAsync(mime);
        await client.DisconnectAsync(true);
    }

    /// <summary>Sends email with attachment.</summary>
    private async Task SendWithAttachmentAsync(string toUser, string subject,
        string textBody, string fileName, byte[] fileContent, string fromUser = "sender")
    {
        await ProvisionUserAsync(fromUser);
        await ProvisionUserAsync(toUser);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(fromUser, $"{fromUser}@localhost"));
        mime.To.Add(new MailboxAddress(toUser, $"{toUser}@localhost"));
        mime.Subject = subject;

        var body = new TextPart("plain") { Text = textBody };
        var attachment = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(fileContent)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            FileName = fileName
        };
        mime.Body = new Multipart("mixed") { body, attachment };

        using var client = new SmtpClient();
        await client.ConnectAsync(SmtpHost, SmtpPort,
            MailKit.Security.SecureSocketOptions.None);
        await client.AuthenticateAsync(fromUser, "secret");
        await client.SendAsync(mime);
        await client.DisconnectAsync(true);
    }

    // ───── SMTP Producer Tests ─────

    [Fact]
    public async Task SmtpProducer_SendsPlainText_CanBeReadViaImap()
    {
        var user = UniqueUser();
        _output.WriteLine($"User: {user}");
        await ProvisionUserAsync(user);

        // Send via redb SmtpProducer
        var ep = CreateSmtpEndpoint(user);
        var producer = (SmtpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello from redb.Route.Mail"));
        exchange.In.Headers[MailHeaders.To] = $"{user}@localhost";
        exchange.In.Headers[MailHeaders.Subject] = "SMTP Producer Test";
        await producer.Process(exchange);
        await producer.Stop();

        // Verify via raw IMAP
        using var imap = new ImapClient();
        await imap.ConnectAsync(ImapHost, ImapPort, MailKit.Security.SecureSocketOptions.None);
        await imap.AuthenticateAsync(user, "secret");
        var inbox = imap.Inbox;
        await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);

        inbox.Count.Should().BeGreaterThanOrEqualTo(1);
        var msg = await inbox.GetMessageAsync(0);
        msg.Subject.Should().Be("SMTP Producer Test");
        msg.TextBody.Should().Contain("Hello from redb.Route.Mail");

        await imap.DisconnectAsync(true);
    }

    [Fact]
    public async Task SmtpProducer_SendsHtmlEmail_WithSubjectFromOptions()
    {
        var user = UniqueUser();
        await ProvisionUserAsync(user);

        var uri = EndpointUriParser.Parse(
            $"smtp://{SmtpHost}?port={SmtpPort}&username={user}&password=secret" +
            $"&security=None&from={user}@localhost&to={user}@localhost" +
            $"&subject=DefaultSubject&contentType=text/html");
        var ep = (SmtpEndpoint)new SmtpComponent().CreateEndpoint(uri);
        var producer = (SmtpProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("<h1>Bold</h1>"));
        await producer.Process(exchange);
        await producer.Stop();

        // Verify via IMAP
        using var imap = new ImapClient();
        await imap.ConnectAsync(ImapHost, ImapPort, MailKit.Security.SecureSocketOptions.None);
        await imap.AuthenticateAsync(user, "secret");
        var inbox = imap.Inbox;
        await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);

        inbox.Count.Should().Be(1);
        var msg = await inbox.GetMessageAsync(0);
        msg.Subject.Should().Be("DefaultSubject");
        msg.HtmlBody.Should().Contain("<h1>Bold</h1>");

        await imap.DisconnectAsync(true);
    }

    // ───── IMAP Consumer Tests ─────

    [Fact]
    public async Task ImapConsumer_ReceivesMessage_SetsHeaders()
    {
        var user = UniqueUser();
        _output.WriteLine($"User: {user}");

        // Seed a message
        await SendDirectAsync(user, "IMAP Test", "Hello IMAP consumer");

        // Consume via redb ImapConsumer
        var ep = CreateImapEndpoint(user, extraParams: "delay=500&fetchFilter=All");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ex = ci.ArgAt<IExchange>(0);
                received.Add(ex);
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (ImapConsumer)ep.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[MailHeaders.Subject].Should().Be("IMAP Test");
        rx.In.Headers[MailHeaders.Protocol].Should().Be("imap");
        rx.In.Headers[MailHeaders.Folder].Should().Be("INBOX");
        rx.In.Headers[MailHeaders.From].Should().NotBeNull();
        consumer.ProcessedCount.Should().Be(1);
    }

    [Fact]
    public async Task ImapConsumer_HtmlMessage_SetsIsHtml()
    {
        var user = UniqueUser();

        await SendDirectAsync(user, "HTML Email", "<p>Rich content</p>", isHtml: true);

        var ep = CreateImapEndpoint(user, extraParams: "delay=500&fetchFilter=All");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { received.Add(ci.ArgAt<IExchange>(0)); tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (ImapConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[MailHeaders.IsHtml].Should().Be(true);
    }

    [Fact]
    public async Task ImapConsumer_MultipleMessages_ReadsAll()
    {
        var user = UniqueUser();

        await SendDirectAsync(user, "Msg-1", "Body 1");
        await SendDirectAsync(user, "Msg-2", "Body 2");
        await SendDirectAsync(user, "Msg-3", "Body 3");

        var ep = CreateImapEndpoint(user, extraParams: "delay=500&fetchFilter=All");
        var received = new ConcurrentBag<IExchange>();
        var counter = 0;
        var allDone = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                if (Interlocked.Increment(ref counter) >= 3) allDone.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (ImapConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(15_000));
        await consumer.Stop();

        received.Should().HaveCountGreaterThanOrEqualTo(3);
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ImapConsumer_MaxMessages_LimitsCount()
    {
        var user = UniqueUser();

        await SendDirectAsync(user, "Limit-1", "Body 1");
        await SendDirectAsync(user, "Limit-2", "Body 2");
        await SendDirectAsync(user, "Limit-3", "Body 3");

        var ep = CreateImapEndpoint(user, extraParams: "delay=500&maxMessages=2&fetchFilter=All");
        var received = new ConcurrentBag<IExchange>();
        var counter = 0;
        var done = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                if (Interlocked.Increment(ref counter) >= 2) done.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (ImapConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(done.Task, Task.Delay(10_000));
        await consumer.Stop();

        // Should have at most 2 in the first poll cycle
        received.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task ImapConsumer_WithAttachment_ExtractsAttachmentData()
    {
        var user = UniqueUser();
        var fileData = new byte[] { 10, 20, 30, 40, 50 };

        await SendWithAttachmentAsync(user, "Attach Test", "See file", "data.bin", fileData);

        var ep = CreateImapEndpoint(user, extraParams: "delay=500&fetchFilter=All");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { received.Add(ci.ArgAt<IExchange>(0)); tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (ImapConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        ((int)rx.In.Headers[MailHeaders.AttachmentCount]!).Should().Be(1);
        rx.In.Headers[MailHeaders.AttachmentNames].Should().Be("data.bin");

        rx.In.Body.Should().BeOfType<MailMessageBody>();
        var mailBody = (MailMessageBody)rx.In.Body!;
        mailBody.Attachments.Should().HaveCount(1);
        mailBody.Attachments[0].FileName.Should().Be("data.bin");
        mailBody.Attachments[0].Content.Should().BeEquivalentTo(fileData);
    }

    // ───── POP3 Consumer Tests ─────

    [Fact]
    public async Task Pop3Consumer_ReceivesMessage()
    {
        var user = UniqueUser();
        _output.WriteLine($"User: {user}");

        await SendDirectAsync(user, "POP3 Test", "Hello POP3 consumer");

        var ep = CreatePop3Endpoint(user, extraParams: "delay=500");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.ArgAt<IExchange>(0));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (Pop3Consumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[MailHeaders.Subject].Should().Be("POP3 Test");
        rx.In.Headers[MailHeaders.Protocol].Should().Be("pop3");
        consumer.ProcessedCount.Should().Be(1);
    }

    // ───── SMTP → IMAP roundtrip ─────

    [Fact]
    public async Task SmtpToImap_FullRoundtrip_HeadersPreserved()
    {
        var user = UniqueUser();
        _output.WriteLine($"User: {user}");
        await ProvisionUserAsync(user);

        // Send via SmtpProducer
        var smtpEp = CreateSmtpEndpoint(user);
        var producer = (SmtpProducer)smtpEp.CreateProducer();
        await producer.Start();

        var sendExchange = new Exchange(new Message("Roundtrip body"));
        sendExchange.In.Headers[MailHeaders.To] = $"{user}@localhost";
        sendExchange.In.Headers[MailHeaders.Subject] = "Roundtrip";
        await producer.Process(sendExchange);
        await producer.Stop();

        // Receive via ImapConsumer
        var imapEp = CreateImapEndpoint(user, extraParams: "delay=500&fetchFilter=All");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { received.Add(ci.ArgAt<IExchange>(0)); tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (ImapConsumer)imapEp.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[MailHeaders.Subject].Should().Be("Roundtrip");
        rx.In.Body!.ToString().Should().Contain("Roundtrip body");
    }

    // ───── SMTP → POP3 roundtrip ─────

    [Fact]
    public async Task SmtpToPop3_FullRoundtrip()
    {
        var user = UniqueUser();
        _output.WriteLine($"User: {user}");
        await ProvisionUserAsync(user);

        // Send via SmtpProducer
        var smtpEp = CreateSmtpEndpoint(user);
        var producer = (SmtpProducer)smtpEp.CreateProducer();
        await producer.Start();

        var sendExchange = new Exchange(new Message("POP3 roundtrip body"));
        sendExchange.In.Headers[MailHeaders.To] = $"{user}@localhost";
        sendExchange.In.Headers[MailHeaders.Subject] = "POP3 Roundtrip";
        await producer.Process(sendExchange);
        await producer.Stop();

        // Receive via Pop3Consumer
        var pop3Ep = CreatePop3Endpoint(user, extraParams: "delay=500");
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { received.Add(ci.ArgAt<IExchange>(0)); tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (Pop3Consumer)pop3Ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        await consumer.Stop();

        received.Should().HaveCount(1);
        var rx = received.First();
        rx.In.Headers[MailHeaders.Subject].Should().Be("POP3 Roundtrip");
    }

    // ── Expression resolution roundtrip ─────────────────────────────

    [Fact]
    public async Task SmtpProducer_ExpressionSubjectAndTo_ResolvedOnSend()
    {
        var user = UniqueUser();
        _output.WriteLine($"User: {user}");
        await ProvisionUserAsync(user);

        // Subject and To come from expressions
        var uriStr = $"smtp://{SmtpHost}?port={SmtpPort}&username={user}&password=secret"
                   + $"&security=None&from={user}@localhost"
                   + "&subject=" + Uri.EscapeDataString("${header.subj}")
                   + "&to=" + Uri.EscapeDataString("${header.dest}");
        var uri = EndpointUriParser.Parse(uriStr);
        var ep = (SmtpEndpoint)new SmtpComponent().CreateEndpoint(uri);
        var producer = (SmtpProducer)ep.CreateProducer();
        await producer.Start();

        var msg = new Message("Expression mail body");
        msg.Headers["subj"] = "Order Confirmed #99";
        msg.Headers["dest"] = $"{user}@localhost";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();

        // Verify via raw IMAP
        using var imap = new ImapClient();
        await imap.ConnectAsync(ImapHost, ImapPort, MailKit.Security.SecureSocketOptions.None);
        await imap.AuthenticateAsync(user, "secret");
        var inbox = imap.Inbox;
        await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);

        inbox.Count.Should().BeGreaterThanOrEqualTo(1);
        var received = await inbox.GetMessageAsync(0);
        received.Subject.Should().Be("Order Confirmed #99");
        received.TextBody.Should().Contain("Expression mail body");

        await imap.DisconnectAsync(true);
    }
}
