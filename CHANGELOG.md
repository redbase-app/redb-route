# Changelog

All notable changes to redb.Route will be documented in this file.
This changelog covers the **NuGet-published packages**:

| Package | Description |
|---------|-------------|
| `redb.Route` | Core engine: DSL, processors, expressions, telemetry |
| `redb.Route.Amqp` | AMQP 1.0 transport |
| `redb.Route.As2` | AS2 (RFC 4130) B2B/EDI transport — signed/encrypted S/MIME over HTTP with MDN receipts |
| `redb.Route.AzureServiceBus` | Azure Service Bus transport |
| `redb.Route.Cache` | Caching as an EIP — a `.Cache(key, ttl)` scope and the `cache:` component |
| `redb.Route.Controllers` | Transport-agnostic controller dispatch |
| `redb.Route.Core` | Bridge to redb.Core props storage |
| `redb.Route.DataFormats.Avro` | Apache Avro data format (Chr.Avro) — binary marshal/unmarshal |
| `redb.Route.DataFormats.Csv` | CSV data format (CsvHelper) — POCO collections, dictionaries, raw rows |
| `redb.Route.DataFormats.Protobuf` | Protocol Buffers data format (Google.Protobuf), optional Confluent Schema Registry |
| `redb.Route.DataFormats.Yaml` | YAML data format (YamlDotNet) — POCOs and dynamic documents |
| `redb.Route.Elasticsearch` | Elasticsearch 8.x transport |
| `redb.Route.Exec` | Local process execution transport (`exec:` scheme) |
| `redb.Route.File` | File system transport |
| `redb.Route.Firebase` | Firebase (Firestore, Cloud Storage, FCM) transport |
| `redb.Route.Ftp` | FTP/FTPS transport |
| `redb.Route.GenericFile` | Base library for file-based transports |
| `redb.Route.Grpc` | gRPC transport |
| `redb.Route.Http` | HTTP/HTTPS transport |
| `redb.Route.Http.Hosting` | Shared Kestrel HTTP hosting (`SharedHttpServerManager`) reused by HTTP-based transports |
| `redb.Route.IbmMq` | IBM MQ transport |
| `redb.Route.JsonTransform` | Declarative JSON-to-JSON transformation (JSONata) |
| `redb.Route.Kafka` | Apache Kafka transport |
| `redb.Route.Ldap` | LDAP / Active Directory transport |
| `redb.Route.Llm` | LLM transport — universal OpenAI-compatible provider + native AnthropicProvider |
| `redb.Route.Llm.Abstractions` | LLM tool-capability contracts (`ILlmToolDescriptor`, `LlmToolCapability`, `.AsLlmTool()` DSL) |
| `redb.Route.Llm.Mcp` | MCP-client connector (`mcp:` scheme) — spawns external Model Context Protocol servers, auto-discovers tools via `tools/list` |
| `redb.Route.Llm.Tools` | Utility LLM tools — HttpFetch / JsonPath / XPath / MathEval / RegexExtract / Tavily web search |
| `redb.Route.Mail` | Email transport (SMTP, IMAP, POP3) |
| `redb.Route.MqttNet` | MQTT 5.0 transport |
| `redb.Route.Quartz` | Quartz.NET scheduling transport |
| `redb.Route.RabbitMQ` | RabbitMQ transport |
| `redb.Route.Redis` | Redis transport |
| `redb.Route.S3` | AWS S3 / MinIO transport |
| `redb.Route.Sftp` | SFTP transport |
| `redb.Route.SignalR` | SignalR transport |
| `redb.Route.Sql` | SQL database transport |
| `redb.Route.Soap` | SOAP 1.1/1.2 transport — WS-Security, MTOM attachments, WSDL-first services |
| `redb.Route.Sqs` | Amazon SQS + SNS transport |
| `redb.Route.Tcp` | Raw TCP transport |
| `redb.Route.Telegram` | Telegram Bot transport |
| `redb.Route.Templates` | Payload templates (Scriban) — the PayloadFactory analog |
| `redb.Route.TestKit` | Test kit — AdviceWith, mock expectations, NotifyBuilder |
| `redb.Route.Validation.Adapters` | FluentValidation + DataAnnotations adapters |
| `redb.Route.WebSocket` | WebSocket transport |
| `redb.Route.XPath2` | XPath 2.0 expressions (regex, sequences, dates, `for`/`some`/`every`) on XPath2.Net |
| `redb.Route.Xml` | Declarative XML routes — `.route.xml` files load into the same fluent DSL |
| `redb.Route.Xml.CodeGen` | `redb-route-xml` .NET tool — fluent C#, Mermaid diagrams and the XSD from `.route.xml`; not part of the runtime |

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Note on version history:** redb.Route has been running in production since version 1.0.0.
> Versions 1.0.0 – 1.0.3 were not published to NuGet (internal deployments only).
> The first public NuGet release is **1.0.4**.

## [Unreleased]

Pending changes live in `changelog/unreleased/` until the release; `pwsh scripts/changelog.ps1 -Preview` shows them.

## [4.6.0] — 2026-11-03

### Fixed

- **A bean of an unloadable module that takes `ILogger<T>` of its own type no longer keeps the module in memory.** The
  host's DI container kept every service type it was asked for, so a hot-swapped module stayed loaded. For its own types
  such a bean gets `ILogger<T>` and the options services (`IOptions<T>` and the rest, default instance) built outside
  the host's container, an empty `IEnumerable<T>`, and no other service. Packages: `redb.Route`, `redb.Route.Xml`.

## Earlier versions

Earlier releases are archived by line in [changelog/](changelog/): [4.5.x](changelog/CHANGELOG-4.5.x.md), [4.4.x](changelog/CHANGELOG-4.4.x.md), [4.3.x](changelog/CHANGELOG-4.3.x.md), [4.2.x](changelog/CHANGELOG-4.2.x.md), [4.1.x](changelog/CHANGELOG-4.1.x.md), [4.0.x](changelog/CHANGELOG-4.0.x.md), [3.x](changelog/CHANGELOG-3.x.md), [2.x](changelog/CHANGELOG-2.x.md), [1.x](changelog/CHANGELOG-1.x.md).
