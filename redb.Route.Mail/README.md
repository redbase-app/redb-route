# redb.Route.Mail

Email transport for redb.Route via MailKit. SMTP producer for sending, IMAP/POP3 consumers for receiving with IDLE push, attachments, TLS/SSL, and OAuth.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Mail?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Mail)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Mail
```

## Usage

### Fluent DSL

```csharp
using redb.Route.Mail.Fluent;

// Send email via SMTP
From("direct://send-email")
    .To(Smtp.Send("smtp.gmail.com")
        .Port(587)
        .Security(SecureSocketOptions.StartTls)
        .Username("sender@gmail.com").Password("app-password")
        .From("sender@gmail.com")
        .To("recipient@example.com")
        .Subject("Order Confirmation"));

// Receive emails via IMAP (IDLE push)
From(Imap.Read("imap.gmail.com")
        .Port(993)
        .Security(SecureSocketOptions.SslOnConnect)
        .Username("inbox@gmail.com").Password("app-password")
        .Folder("INBOX")
        .Unseen()
        .Idle()
        .FetchBody()
        .FetchAttachments()
        .PostProcess(PostProcessAction.MarkRead))
    .Log("Email from: ${header.From} — ${header.Subject}")
    .To("direct://process");

// POP3 polling
From(Pop3.Read("pop3.example.com")
        .Port(995)
        .Security(SecureSocketOptions.SslOnConnect)
        .Username("user").Password("pass")
        .Delay(30000)
        .MaxMessages(10))
    .To("seda://email-queue");
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Port()`, `.Security(mode)`, `.ConnectionTimeout()`, `.Timeout()`, `.Username()`, `.Password()`, `.AccessToken()`, `.AuthMechanism()`, `.TrustAllCertificates()`, `.ClientCert()` |
| **SMTP** | `Smtp.Send(server)`, `.From()`, `.To()`, `.Cc()`, `.Bcc()`, `.ReplyTo()`, `.Subject()`, `.ContentType()`, `.AlternativeBody()`, `.Attachments()` |
| **IMAP** | `Imap.Read(server)`, `.Folder()`, `.AdditionalFolders()`, `.Idle()`, `.IdleTimeout()`, `.SearchQuery()` |
| **POP3** | `Pop3.Read(server)` |
| **Consumer** | `.Delay()`, `.InitialDelay()`, `.Unseen()`, `.MaxMessages()`, `.FetchFilter()`, `.FetchBody()`, `.FetchAttachments()`, `.MaxAttachmentSize()`, `.Peek()`, `.Idempotent()`, `.SortBy()`, `.MinAge()`, `.MaxAge()`, `.SubjectFilter()`, `.FromFilter()` |
| **Post-process** | `.PostProcess(action)`, `.MoveTo()` |

## Three Schemes

| Scheme | Direction | Description |
|--------|-----------|-------------|
| `smtp` | Producer | Send emails |
| `imap` | Consumer | Receive via IMAP (IDLE push or polling) |
| `pop3` | Consumer | Receive via POP3 (polling) |

The `smtp` producer keeps its connection between sends. When the server has closed it while it was idle (a socket
timeout answered with `421`, a reset), the message is sent once more on a new connection; a refusal on a fresh connection
is the server's answer and fails the exchange. `disconnect=true` opens a connection per message instead.

## Attachments

Mail works with the [message attachments](../README.md#attachments) (`exchange.In.Attachments`), the same list an
upload, a SOAP MTOM call or a route step fills, so the connector before `smtp:` does not matter.

```csharp
// From code
exchange.In.Attachments.Add(new Attachment
{
    Name = "invoice", FileName = "invoice-42.pdf", ContentType = "application/pdf",
    Content = AttachmentContent.FromBytes(pdf),
});

// From a route step: a file read by file:, sent as an attachment
From("file:///data/out?include=*.pdf")
    .AddAttachmentFromBody("report", fileName: Header("redbFile.Name"), contentType: "application/pdf")
    .SetBody("The report is attached.")
    .To("smtp://mail.example.com?to=team@example.com&subject=Report");

// Forwarding: the received attachments go out as they came
From("imap://imap.example.com?username=in&password=...")
    .To("smtp://mail.example.com?to=archive@example.com");
```

**`smtp:` sends** every message attachment as a part of the mail: the file name (the attachment name when there is
none), the content type, the `Content-ID`, an `inline` disposition when the part it came from was inline, and the other
part headers except `Content-*`. The `attachments=` option still adds files by path, before the message attachments.

**`imap:` and `pop3:` receive** every attachment of the mail onto the message attachments: named by its file name
without a directory, with its content type, `Content-ID` and part headers; an attached mail (`message/rfc822`) is one
attachment with its bytes. The body stays as before: `MailMessageBody` with the text and the decoded files, and the
`redbMail.HasAttachments`, `redbMail.AttachmentCount`, `redbMail.AttachmentNames` headers. `fetchAttachments=false`
puts none on the message; `maxAttachmentSize` leaves out the ones above it. `MailMessageBody` converts to its text, so
`smtp:` sends a received mail on with its text.

The exchange property `Attachments` is not read: it is an ordinary property now. A route that put a
`List<MailAttachment>` or file paths there sends the mail without those files until it puts them on the message
attachments (see `UPGRADING.md`, "Mail and SOAP attachments are message attachments").

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new MailConnectionFactory
{
    Host = "smtp.internal",
    Username = "noreply",
    Password = secrets.SmtpPassword,
});
// smtp://noreply@internal?connectionFactory=prod
```

## Tracing

On the `redb.Route` activity source (`AddSource("redb.Route")`), with `messaging.system` = `imap` / `pop3` / `smtp` and
`redb.route.endpoint`:

- **IMAP and POP3 consumers.** One `Consumer` span per routed message, `{host} receive`. The connector reads no trace
  context from a message, so the span is a root, never a child of the activity the poll loop inherited from whoever
  started the routes; an empty mailbox opens none. A failed route marks it an error; our own stop does not.
- **SMTP producer.** One `Producer` span per send, `{host} send`; an error when the send fails, unless our own token
  cancelled it.
- `RouteEngineOptions.EnableTelemetry = false` opens none of these spans.
