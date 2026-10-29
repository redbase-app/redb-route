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

## [4.5.0] — 2026-10-29

### Breaking

- **An `amqp:` consumer waits for the broker to accept its receiver link, and a refused link fails the start.** A link
  refused for its address, routing type or permissions used to be logged while the consumer counted as started and
  never received; now the route and the context fail to start with an error naming the consumer and the reason. Packages: `redb.Route.Amqp`. Upgrade: [UPGRADING.md](UPGRADING.md#an-amqp-consumer-starts-only-when-the-broker-accepted-its-link).
- **`smtp:` and `soap:` send and receive the message attachments; the old conventions are removed.** `smtp:` no longer
  reads `exchange.Properties["Attachments"]`, and nothing warns: a route that still sets it sends the mail without the
  files. `SoapAttachment`, `redbSoap.attachments` and `redbSoap.hasAttachments` are gone. Put the files on `exchange.In.Attachments`. Packages: `redb.Route.Mail`, `redb.Route.Soap`. Upgrade: [UPGRADING.md](UPGRADING.md#mail-and-soap-attachments-are-message-attachments).
- **An MQTT publish takes its endpoint QoS and retain flag, `redbMqtt.overrideQos` and `redbMqtt.overrideRetain` pick
  others.** `redbMqtt.qos` and `redbMqtt.retain`, written by the consumer, carried over to a republish: a retained message
  was retained again on the new topic. Set the override headers where a route set `redbMqtt.qos` or `redbMqtt.retain`. Packages: `redb.Route.MqttNet`. Upgrade: [UPGRADING.md](UPGRADING.md#an-mqtt-publish-takes-its-endpoint-qos-and-retain-flag).
- **An MQTT publish goes to its endpoint topic, `redbMqtt.overrideTopic` picks another.** `redbMqtt.topic`, written by
  the connector on every receive and publish, steered the next publish: a second publish went to the first topic, a
  republished message back to where it came from. Set `redbMqtt.overrideTopic` where a route set `redbMqtt.topic`. Packages: `redb.Route.MqttNet`. Upgrade: [UPGRADING.md](UPGRADING.md#an-mqtt-publish-goes-to-its-endpoint-topic).
- **A `redis:` consumer is ready to receive when `Start()` returns, and a Redis that does not answer fails the start.**
  The subscription, the consumer group and the XREAD position were set up in the background after the start, so a
  message published right after it was lost, and with Redis down the context ran with a consumer that never received.
  Now the route and the context fail to start with an error naming the consumer. Packages: `redb.Route.Redis`. Upgrade: [UPGRADING.md](UPGRADING.md#a-redis-consumer-starts-only-when-it-receives).

### Added

- **Endpoint statistics count refusals.** `IEndpointStatistics.Refused`, `LastRefusalTime` and `LastRefusalMessage`
  count requests a transport refused as the caller's fault, outside `Errors` and health; `RecordRefused(reason)` records
  one. They are default interface members, so existing implementations compile. Readable in a route as
  `stats(..., 'refused')` and `'lastRefusal'`, and in the control bus `routeStats`. Packages: `redb.Route`.
- **HTML form data is a built-in data format.** `ConvertBody<FormBody>()` after an HTTP consumer reads the fields of an
  `application/x-www-form-urlencoded` post, repeated names included, and `Marshal("application/x-www-form-urlencoded")`
  writes a `FormBody` or a dictionary; a format that needs the whole message (content type parameters, attachments)
  implements the new `IMessageDataFormat`. Packages: `redb.Route`.
- **Messages carry attachments, as Camel's `AttachmentMessage`.** `exchange.In.Attachments` is an ordered list of named,
  re-readable content (bytes, a file, a `StreamCache`) that survives copies, splits and checkpoints. Routes add and remove
  them with `AddAttachment`, `AddAttachmentFromBody`, `AddAttachmentFromFile` and `<addAttachment>` and read them with
  `${attachments.size}` and `attachmentContentAsText(name)`; a replay snapshot keeps up to `RedbRoute:Attachments:MaxReplayableBytes`. Packages: `redb.Route`, `redb.Route.Xml`.
- **An AS4 partner's address can live in its agreement**: `As4Partner.Address`, sent to by `As4.Send()` (`as4:?partner=...`),
  so a partner chosen per message gets its own address. An address may carry a query, such as a Domibus domain
  (`/domibus?domain=name`); in a send URI that query is the `addressQuery` option. Such an address used to be refused. Packages: `redb.Route.As4`.
- **MIME multipart data format, as Camel's `mimeMultipart`.** `redb.Route.DataFormats.Mime` reads a
  `multipart/form-data` upload into a `FormBody` of fields and message attachments of files (`ConvertBody<FormBody>()`
  after `AddMimeMultipartDataFormat()`), reads `multipart/mixed` the Camel way and writes both back, with limits on parts
  and sizes; a body that is not multipart passes unchanged. Packages: `redb.Route.DataFormats.Mime`.
- **`imap:` and `pop3:` put the attachments of a mail on the message attachments.** A received mail sent on to `smtp:`
  now keeps its files and its text, with no mapping step; `MailMessageBody` and the `redbMail.*` attachment headers stay
  as they were. Packages: `redb.Route.Mail`.
- **`<routeBuilder type="..."/>` in `context.xml` adds C# routes to the package's context.** A module keeps routes in
  markup and in the DSL side by side: name a public `RouteBuilder` of its own assembly with a parameterless constructor.
  `redb-route-xml check --bin` and `pack --bin` fail the build on a type that is missing or is not such a builder. Packages: `redb.Route.Xml`, `redb.Route.Xml.CodeGen`.
- **VS Code extension: `redb: New Project`.** A page inside VS Code creates a project from the redb templates
  through `dotnet new`: every project and solution template of `redb.Templates` and `redb.Tsak.Templates` as a
  card, a form with the template's own parameters, install and update buttons for the template packages, and the
  project opened when it is created. It needs a .NET SDK. The route tree moved from the Explorer to its own **redb**
  view in the Activity Bar, with New Project in its title bar.
- **The in-flight registry knows when the engine holds an exchange on purpose.** `IInflightRepository.BeginWait(exchangeId,
  delay)` marks a redelivery delay (`onException`, `Retry`, the dead letter channel) or a `delay` step;
  `InflightExchange.WaitingUntil`, `LastWaitEndedAt` and `BusySince` show it. A repository of your own keeps working: the
  default `BeginWait` records nothing.
- **`sql:` poll option `onFailureAfterRollback`.** With `transacted=true` a failed row used to roll back with its
  `onFailure` mark, be read again on every poll and hold up the rows behind it. With `onFailureAfterRollback=true` each row
  runs under a savepoint: a failed row is undone alone, keeps its mark, and the poll goes on. The default stays as it was.
- **`<dataFormat>` in `context.xml` registers a data format.** A module in markup could not register one, so
  `<marshal format="csv"/>` failed with "Data format 'csv' is not registered" on the Tsak worker. Now
  `<dataFormat type="…CsvDataFormat, redb.Route.DataFormats.Csv" name="csv" delimiter=";"/>` does what
  `context.AddDataFormat(...)` does in code; the other attributes set the format's options.
- **VS Code extension: `<routeBuilder>` in the schema.** Completion and validation offer `<routeBuilder type>` inside
  `<context>`, so a `context.xml` that adds C# routes is no longer marked as invalid.

### Changed

- **XML read through `SafeXml` is bounded in structure.** Every loader checks depth (256), attributes (1024) and namespace
  declarations (256) per element while it reads, and refuses a document over a limit with `XmlLimitException` before it
  is built: XPath over a body, SOAP, AS4, Route-XML. A document deeper than 256 levels used to load. `XmlLimits` sets
  other limits for code that reads XML itself. Packages: `redb.Route`, `redb.Route.Soap`, `redb.Route.As4`. Upgrade: [UPGRADING.md](UPGRADING.md#xml-structure-limits).
- **`attachments` in an expression means the message attachments.** A bare `${attachments}` or `attachments.size` used to
  read an exchange property named `attachments`; it now reads the attachments of the In message, as in Camel Simple.
  Write `property.attachments` where the property was meant. Packages: `redb.Route`. Upgrade: [UPGRADING.md](UPGRADING.md#attachments-in-an-expression).
- **AS4 envelopes are held to the shape of an eDelivery envelope.** Depth 32, 32 attributes and 16 namespace
  declarations on an element, 4096 in the envelope: a received envelope over them is a SOAP `Sender` fault, an answer
  over them an invalid receipt. A message carries exactly one `xenc:EncryptedKey` and each payload is decrypted once,
  otherwise `EBMS:0102`. Domibus and Holodeck B2B messages stay well within these limits. Packages: `redb.Route.As4`. Upgrade: [UPGRADING.md](UPGRADING.md#xml-structure-limits).
- **An AS4 receiver counts refused messages as refusals, not errors.** A malformed envelope, a failed signature or
  decryption, a message the agreement does not allow: `Refused` with the reason, no longer `Errors` or health.
  `EBMS:0004` and route failures stay errors. Packages: `redb.Route.As4`. Upgrade: [UPGRADING.md](UPGRADING.md#as4-refusals-in-their-own-counter).
- **`readLock=FileLock` is documented as Windows only.** Linux and macOS locks are advisory, so there the strategy
  keeps no other reader or consumer out and picks up files still being written. In Linux workers use `Rename`,
  `MarkerFile` or `Changed`. Packages: `redb.Route.File`.
- **Generated `Rest(...)` / `<rest>` route ids carry no '/' and no space, so the worker API and the dashboard can manage
  them.** `rest:openapi /api` and `rest:GET /api/orders/{id}` become `rest-openapi-api-…` and `rest-GET-api-orders-id-…`
  (a short fingerprint at the end, the same on every start), and an inline handler route ends in `-handler`. Code that
  looks a route up by the old id needs the new one; give a verb `.Id(...)` / `id=` to choose it yourself. Packages: `redb.Route.Http`.
- **The SOAP receiver bounds what it reads.** The request body is limited by `maxRequestBodySize`, 100 MB by default
  (it was unbounded; `0` restores that), and the envelope is checked against the XML limits before it is parsed and
  again after decryption. A request over a limit is a `Client` / `Sender` fault naming the limit. Packages: `redb.Route.Soap`. Upgrade: [UPGRADING.md](UPGRADING.md#xml-structure-limits).
- **VS Code extension 0.2.24: the icon draws the fan-out first.** The entry point and the step in the icon are
  smaller, so the branching route stands out in the Extensions view. The extension is now on the Visual Studio
  Marketplace as `redb.redb-route-xml`.

### Fixed

- **`format(value, pattern, culture)` refuses an unknown culture name on Linux and macOS too.** ICU accepted any
  well-formed tag there (`kl-INGON`) and formatted with whatever it guessed; now only known cultures are accepted on
  every system, as on Windows. Packages: `redb.Route`.
- **`amqp://...?connectionFactory=name` connects to the factory's broker.** The connection took its host, port and
  credentials from the URI defaults (`localhost:5672`, no user), so a named factory only worked when the broker happened
  to be there. The factory's `Host`, `Port`, `User`, `Password` and `Ssl` now decide where and how the connection opens. Packages: `redb.Route.Amqp`.
- **An XGROUP consumer without `streamStartPosition` receives.** Creating the group failed with "A null value is not
  valid in this context", the error went to the log and the consumer never read the stream. The group now starts at
  the end of the stream (`$`) as documented. Packages: `redb.Route.Redis`.
- **`tcp:` leaves the socket buffer sizes to the system unless `receiveBufferSize` / `sendBufferSize` is set.** The
  fixed 8 KiB default switched the Linux buffer tuning off, and a 64 KiB frame over loopback took about three seconds
  instead of milliseconds. A size you set is applied as before; Raw and TextLine reads still use 8192-byte chunks. Packages: `redb.Route.Tcp`.
- **`redb-route-xml pack` matches `config/<name>.config.json` with its letter case on every system.** On Windows
  `--name orders` used to pack `config/Orders.config.json`, on a Linux build agent the default config; now both warn
  and pack the default config. Pack with the exact name (the project name by default) or rename the file. Packages: `redb.Route.Xml`.
- **`<rest>` and `from http://` in an XML module on the Tsak worker failed with "No component registered for scheme
  'http'".** The worker builds a module's components by an assembly scan, and the scan skipped `HttpComponent` and
  `WsComponent`: `https` and `wss` were there, `http` and `ws` were not. Both now have a parameterless constructor; nothing
  to change on your side.
- **`redb-route-xml check` and `pack` warn when the package config has another name.** A package reads only
  `config/<name>.config.json`; when that file is missing but another `*.config.json` is in `config/`, the package silently
  got the default config. Now a warning names both files; rename the file or pack with the matching `--name`.
- **redb idempotent repository on a named redb instance.** `<idempotentRepository>` inside `<redb storage="...">` ignored
  `storage`: the keys went to the host's default redb, and a context without one did not start. The repository now keeps
  its keys in the named instance, as `<syncScheme>` does; in C# pass `storage:` to `AddRedbIdempotentRepository`.
- **VS Code extension: the route graph resolves `{{key}}` as the Tsak runtime does.** It reads
  `config/context.sample.json` and over it the module's `config/<package>.config.json`, each by its `Contexts` section
  (`default`, then the module's own context) or by its root keys when it has none. A key in the runtime's format (`"Contexts": { "<ctx>": { "hello.period": ... } }`)
  now shows resolved, and a nested `{"hello": {"period": ...}}`, which the runtime does not resolve, shows unresolved.
- **AS2 on Linux computes the MIC of a received message over its CRLF bytes.** The receiver hashed the signed part with
  the platform's line ends, LF on Linux, so the MIC in the MDN could differ from the sender's and the partner reported
  "MIC not matched". Windows hosts were not affected.
- **An exchange waiting for its redelivery or in a `delay` step no longer looks hung to a monitor.** The in-flight registry
  counted the wait as work, so a monitor that reads it (the Tsak watchdog) reported such an exchange as suspected or hung.
  The waits are now marked in the registry.
- **`redb-route-xml pack --entry <path>` names the DLL in the manifest as it lies in the package.** The DLL was packed at
  the package root, but the manifest kept the path given (`bin/Release/net10.0/X.dll`), so Tsak logged "Entry point ... not
  found" and loaded the DLL as a shared companion instead of the package's own entry point. Repack to fix existing packages.
- **`redb-route-xml check` and `pack --bin` find framework types the project's `bin/` does not carry.** Markup such as
  `<onException exceptions="System.Net.Http.HttpRequestException">` was refused with "type ... was not found in the built
  assemblies", though the worker resolves it. The checks now look in the build output, then in the shared .NET framework.
- **`redb-route-xml check` and `pack --bin` hold an untyped `<constructorArg>` against the bean's constructors.** A bean
  whose only constructor takes a `decimal` passed the checks with `<constructorArg value="0.20"/>` and then failed at load
  ("A suitable constructor ... could not be located"): without `type=` the value is passed as text. Such a bean is now an
  error that names the `type=` to write.
- **`s3:` with `moveAfterRead=true` starts without `deleteAfterRead=false`.** The default `deleteAfterRead=true` made such a
  consumer fail at start with "Cannot set both MoveAfterRead=true and DeleteAfterRead=true". The move now takes the place
  of the default delete, as Apache Camel's `move` does; only an explicit `deleteAfterRead=true` beside it is refused.
- **The `s3:` producer reports what it wrote after a multipart upload and a copy as well.** `redbS3.ProducedBucketName`,
  `redbS3.ProducedKey` and `redbS3.ETag` were set only by a single PUT: with `multiPartUpload=true` they were missing, and a
  copy set only the ETag. A copy now reports its destination.
- **`<tokenizeJsonArray/>` (`SplitJsonArray()`) splits the array a `jpath` left in the body.** After
  `<setBody expr="${jpath('$.lines')}"/>` the split failed with "SplitJsonArray: unsupported body type Object[]". It now
  takes any body `jpath` takes (JSON text, a System.Text.Json tree, a CLR array or a POCO); each part is the element's
  JSON text, as before.
- **A `sql:` poll runs `onFailure` for a row the route failed without throwing.** With an `onException handled="false"`
  (or a rollback-only exchange) the failure stayed on the exchange, and the row got `onSuccess`, as if it had been
  processed. It now gets `onFailure`, as when the failure escapes the route; a handled failure still completes the row.
- **`{{key}}` in `value=` of `<setHeader>`, `<setProperty>` and `<setBody>` takes the configured value.** The literal
  `{{key}}` went into the message, and a `{{key:default}}` default was not used, though the package listed the key as
  required. It now resolves through the context's configuration as in an endpoint URI. In C# the same is
  `Placeholder("{{key}}")`.
- **The stylesheet of an `<xslt file="…">` step (`.Xslt(file)`) is found through the context's resource resolver.** It was
  opened relative to the process's working directory, so a package's stylesheet was not found on the Tsak worker ("Could
  not find a part of the path '/app/resources/…'"). It now resolves as the `xslt:` endpoint and `<validateXsd>` already do.
- **An `smtp:` send after an idle pause longer than the server's timeout is no longer lost.** The producer kept its
  connection, and when the server had closed it ("421 Service shutting down", common at 30 s on GreenMail and many relays)
  the next message failed. It is now sent once more on a new connection; `disconnect=true` is no longer needed for that.
- **`redb-route-xml check --bin` on macOS and Linux reported connector elements as unknown.** Correct markup such as
  `<rest>`, `<redbQuery>` or `<redbSave>` failed with "The element 'routes' ... has invalid child element", though the
  worker ran the routes. The check now reads the connectors in whatever order the file system lists the build output.

## Earlier versions

Earlier releases are archived by line in [changelog/](changelog/): [4.4.x](changelog/CHANGELOG-4.4.x.md), [4.3.x](changelog/CHANGELOG-4.3.x.md), [4.2.x](changelog/CHANGELOG-4.2.x.md), [4.1.x](changelog/CHANGELOG-4.1.x.md), [4.0.x](changelog/CHANGELOG-4.0.x.md), [3.x](changelog/CHANGELOG-3.x.md), [2.x](changelog/CHANGELOG-2.x.md), [1.x](changelog/CHANGELOG-1.x.md).
