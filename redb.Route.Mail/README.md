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
        .PostProcess(nameof(PostProcessAction.MarkRead)))
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
| **IMAP** | `Imap.Read(server)`, `.Folder()`, `.AdditionalFolders()`, `.Idle()`, `.IdleTimeout()`, `.SearchQuery()` (not applied, see below) |
| **POP3** | `Pop3.Read(server)` |
| **Consumer** | `.Delay()`, `.InitialDelay()`, `.Unseen()`, `.MaxMessages()`, `.FetchFilter()`, `.FetchBody()`, `.FetchAttachments()`, `.MaxAttachmentSize()`, `.Peek(bool)`, `.Idempotent()`, `.IdempotentRepository()`, `.IdempotentRepositoryRemoveOnCommit()`, `.SortBy()`, `.MinAge()`, `.MaxAge()`, `.SubjectFilter()`, `.FromFilter()` |
| **Post-process** | `.PostProcess(action)`, `.MoveTo()`, `.Delete()`, `.CopyTo()` |

`searchQuery` is not applied: the consumer selects the mails by `fetchFilter` and filters them with `subjectFilter`,
`fromFilter`, `minAge` and `maxAge`; a raw IMAP search string is not read.

The three schemes share one set of options, but each option belongs to one side: the SMTP group to `smtp:`, the
IMAP, consumer and post-process groups to `imap:` and `pop3:`. An option written on the other side is refused when the endpoint
is created, naming it ("smtp producer '...' sets postProcess: only an imap or pop3 consumer reads it."); connection,
authentication and TLS options are read by both.

## Three Schemes

| Scheme | Direction | Description |
|--------|-----------|-------------|
| `smtp` | Producer | Send emails |
| `imap` | Consumer | Receive via IMAP (IDLE push or polling) |
| `pop3` | Consumer | Receive via POP3 (polling) |

The `smtp` producer keeps its connection between sends. When the server has closed it while it was idle (a socket
timeout answered with `421`, a reset), the message is sent once more on a new connection; a refusal on a fresh connection
is the server's answer and fails the exchange. `disconnect=true` opens a connection per message instead.

## What happens to a consumed mail

`imap:` and `pop3:` treat a mail the way Apache Camel's mail consumer does once its exchange ends.

- **The exchange succeeded** (also when an `OnException` handled the failure): the mail is committed. `copyTo`
  copies it first; then `postProcess` applies (`MarkRead`, the default; `Delete`; `Move` or `MarkReadAndMove` with
  `moveTo`; `Flag`; `None`), or Camel's `delete=true`, which deletes it, moved to `moveTo` first when that is set.
  A `moveTo` or `copyTo` folder the mailbox does not have is created. POP3 knows only deleting and keeping.
- **The exchange failed** (an unhandled exception, or `.RollbackAll()`): the mail is left as it is. On IMAP it stays
  unseen and is fetched again; `peek=false` marks each mail read when it is fetched, so a failed one stays read and
  `fetchFilter=Unseen` does not fetch it again. On POP3 it stays on the server and is fetched again. The mails after
  it are routed as usual.

The route decides per mail with three headers on the message, read only when the exchange succeeded, as Camel's
`CamelMailDelete`, `CamelMailMoveTo` and `CamelMailCopyTo`:

| Header | Effect |
|--------|--------|
| `redbMail.Delete` | `true` deletes the mail; `false` marks it read (IMAP) or keeps it on the server (POP3) |
| `redbMail.MoveTo` | moves it to this IMAP folder, marked read unless `redbMail.Delete` is `true` |
| `redbMail.CopyTo` | copies it to this IMAP folder before anything else is done to it |

**Delete only what succeeded.** Delete the mails the route accepted; keep the rejected and the failed ones in the
mailbox, read, so they are not fetched again and can be looked at by hand:

```csharp
From(Imap.Read("imap.example.com").ConnectionFactory("inbox")
        .Unseen()
        .PostProcess(nameof(PostProcessAction.Delete))
        .Peek(false))                                          // a failed mail was marked read at fetch
    .Process(exchange =>
    {
        if (!Accepts(exchange))
        {
            exchange.In.Headers[MailHeaders.Delete] = false;   // rejected: kept, read
            exchange.Stop();
        }
    })
    .To("direct://process");                                   // accepted: deleted once the route ends
```

```xml
<from uri="imap://imap.example.com?connectionFactory=inbox&amp;fetchFilter=Unseen&amp;postProcess=Delete&amp;peek=false"/>
<choice>
  <when expr="header.verdict == 'rejected'">
    <setHeader name="redbMail.Delete" expr="false"/>
    <stop/>
  </when>
</choice>
<to uri="direct://process"/>
```

Without `peek=false`, handle the failure instead and set `redbMail.Delete=false` in the handler: the exchange then
commits and the mail is marked read rather than deleted.

**Idempotency.** `idempotent=true` skips, until the consumer restarts, the mails it routed successfully.
`idempotentRepository=name` (Camel's option) uses a registered `IIdempotentRepository` instead, which a persistent
implementation keeps across restarts: a mail's key is added before its route and the mail is skipped while the
repository holds it; a failed exchange releases the key; a committed one removes it
(`idempotentRepositoryRemoveOnCommit=true`, the default, since the mail was deleted or marked read) or confirms it.
The key is the Message-ID, else the folder, UIDVALIDITY and UID (IMAP) or the UIDL (POP3).

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
