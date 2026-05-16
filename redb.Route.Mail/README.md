# redb.Route.Mail

Email transport for redb.Route via MailKit. SMTP producer for sending, IMAP/POP3 consumers for receiving with IDLE push, attachments, TLS/SSL, and OAuth.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Mail?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Mail)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

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
| **Connection** | `.Port()`, `.Security(mode)`, `.ConnectionTimeout()`, `.Timeout()`, `.Username()`, `.Password()`, `.AccessToken()`, `.AuthMechanism()`, `.SkipCertificateValidation()`, `.ClientCert()` |
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

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
