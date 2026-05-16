using MailKit;
using MimeKit;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

public sealed class MailMessageHelperTests
{
    [Fact]
    public void CreateExchange_PlainTextMessage_SetsBodyAndHeaders()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Subject = "Hello";
        mime.Body = new TextPart("plain") { Text = "Hello World" };

        var exchange = MailMessageHelper.CreateExchange(mime, "imap", "INBOX");

        exchange.In.Body.Should().Be("Hello World");
        exchange.In.Headers[MailHeaders.From].Should().Be("alice@example.com");
        exchange.In.Headers[MailHeaders.To].Should().Be("bob@example.com");
        exchange.In.Headers[MailHeaders.Subject].Should().Be("Hello");
        exchange.In.Headers[MailHeaders.Protocol].Should().Be("imap");
        exchange.In.Headers[MailHeaders.Folder].Should().Be("INBOX");
        exchange.In.Headers[MailHeaders.IsHtml].Should().Be(false);
        ((int)exchange.In.Headers[MailHeaders.AttachmentCount]!).Should().Be(0);
    }

    [Fact]
    public void CreateExchange_HtmlMessage_SetsHtmlBody()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Subject = "HTML Test";
        mime.Body = new TextPart("html") { Text = "<h1>Hello</h1>" };

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        exchange.In.Body.Should().Be("<h1>Hello</h1>");
        exchange.In.Headers[MailHeaders.IsHtml].Should().Be(true);
        exchange.In.Headers[MailHeaders.HtmlBody].Should().Be("<h1>Hello</h1>");
    }

    [Fact]
    public void CreateExchange_MultipartAlternative_SetsBothBodies()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Subject = "Multi";

        var multipart = new MultipartAlternative
        {
            new TextPart("plain") { Text = "Plain text" },
            new TextPart("html") { Text = "<p>HTML text</p>" }
        };
        mime.Body = multipart;

        var exchange = MailMessageHelper.CreateExchange(mime, "pop3");

        // HTML takes priority for body
        exchange.In.Body.Should().Be("<p>HTML text</p>");
        exchange.In.Headers[MailHeaders.TextBody].Should().Be("Plain text");
        exchange.In.Headers[MailHeaders.HtmlBody].Should().Be("<p>HTML text</p>");
        exchange.In.Headers[MailHeaders.IsHtml].Should().Be(true);
        exchange.In.Headers[MailHeaders.Protocol].Should().Be("pop3");
    }

    [Fact]
    public void CreateExchange_WithAttachments_ExtractsAttachmentData()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Subject = "With Attachment";

        var body = new TextPart("plain") { Text = "See attached" };
        var attachment = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3 })),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            FileName = "report.pdf"
        };

        var multipart = new Multipart("mixed") { body, attachment };
        mime.Body = multipart;

        var exchange = MailMessageHelper.CreateExchange(mime, "imap", "INBOX");

        ((int)exchange.In.Headers[MailHeaders.AttachmentCount]!).Should().Be(1);
        exchange.In.Headers[MailHeaders.HasAttachments].Should().Be(true);
        exchange.In.Headers[MailHeaders.AttachmentNames].Should().Be("report.pdf");

        exchange.In.Body.Should().BeOfType<MailMessageBody>();
        var mailBody = (MailMessageBody)exchange.In.Body!;
        mailBody.Text.Should().Be("See attached");
        mailBody.Attachments.Should().HaveCount(1);
        mailBody.Attachments[0].FileName.Should().Be("report.pdf");
        mailBody.Attachments[0].Content.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        mailBody.Attachments[0].ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public void CreateExchange_WithCcAndBcc_SetsHeaders()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Cc.Add(new MailboxAddress("Charlie", "charlie@example.com"));
        mime.Bcc.Add(new MailboxAddress("Dave", "dave@example.com"));
        mime.Subject = "CC test";
        mime.Body = new TextPart("plain") { Text = "body" };

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        exchange.In.Headers[MailHeaders.Cc].Should().Be("charlie@example.com");
        exchange.In.Headers[MailHeaders.Bcc].Should().Be("dave@example.com");
    }

    [Fact]
    public void CreateExchange_MultipleRecipients_JoinsWithComma()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.To.Add(new MailboxAddress("Carol", "carol@example.com"));
        mime.Subject = "Multi-to";
        mime.Body = new TextPart("plain") { Text = "body" };

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        ((string)exchange.In.Headers[MailHeaders.To]!).Should().Contain("bob@example.com");
        ((string)exchange.In.Headers[MailHeaders.To]!).Should().Contain("carol@example.com");
    }

    [Fact]
    public void CreateExchange_WithUid_SetsUidHeader()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Body = new TextPart("plain") { Text = "body" };

        var uid = new UniqueId(42);
        var exchange = MailMessageHelper.CreateExchange(mime, "imap", "INBOX", uid);

        exchange.In.Headers[MailHeaders.Uid].Should().Be("42");
    }

    [Fact]
    public void CreateExchange_WithIndex_SetsIndexHeader()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Body = new TextPart("plain") { Text = "body" };

        var exchange = MailMessageHelper.CreateExchange(mime, "pop3", index: 5);

        exchange.In.Headers[MailHeaders.Index].Should().Be(5);
    }

    [Fact]
    public void CreateExchange_WithPriority_SetsHeader()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Body = new TextPart("plain") { Text = "urgent" };
        mime.Importance = MessageImportance.High;

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        exchange.In.Headers[MailHeaders.Priority].Should().Be("high");
    }

    [Fact]
    public void CreateExchange_NormalPriority_IsDefault()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Body = new TextPart("plain") { Text = "normal" };

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        exchange.In.Headers[MailHeaders.Priority].Should().Be("normal");
    }

    [Fact]
    public void CreateExchange_WithInReplyTo_SetsThreadingHeaders()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Body = new TextPart("plain") { Text = "reply" };
        mime.InReplyTo = "original-id@example.com";
        mime.References.Add("original-id@example.com");
        mime.References.Add("thread-root@example.com");

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        exchange.In.Headers[MailHeaders.InReplyTo].Should().Be("original-id@example.com");
        ((string)exchange.In.Headers[MailHeaders.References]!).Should().Contain("original-id@example.com");
        ((string)exchange.In.Headers[MailHeaders.References]!).Should().Contain("thread-root@example.com");
    }

    [Fact]
    public void CreateExchange_WithDate_SetsDateHeader()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Body = new TextPart("plain") { Text = "dated" };
        mime.Date = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero);

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        exchange.In.Headers.Should().ContainKey(MailHeaders.Date);
    }

    [Fact]
    public void CreateExchange_NoFolder_OmitsFolderHeader()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Body = new TextPart("plain") { Text = "pop3 msg" };

        var exchange = MailMessageHelper.CreateExchange(mime, "pop3");

        exchange.In.Headers.Should().NotContainKey(MailHeaders.Folder);
    }

    [Fact]
    public void CreateExchange_NullMime_Throws()
    {
        var act = () => MailMessageHelper.CreateExchange(null!, "imap");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateExchange_WithSender_SetsSenderHeader()
    {
        var mime = new MimeMessage();
        mime.Sender = new MailboxAddress("Sender", "sender@example.com");
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.Body = new TextPart("plain") { Text = "body" };

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        exchange.In.Headers[MailHeaders.Sender].Should().Be("sender@example.com");
    }

    [Fact]
    public void CreateExchange_WithReplyTo_SetsHeader()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.ReplyTo.Add(new MailboxAddress("Reply", "reply@example.com"));
        mime.Body = new TextPart("plain") { Text = "body" };

        var exchange = MailMessageHelper.CreateExchange(mime, "imap");

        exchange.In.Headers[MailHeaders.ReplyTo].Should().Be("reply@example.com");
    }
}
