# Changelog

All notable changes to redb.Route will be documented in this file.
This changelog covers the **NuGet-published packages**:

| Package | Description |
|---------|-------------|
| `redb.Route` | Core engine: DSL, processors, expressions, telemetry |
| `redb.Route.Amqp` | AMQP 1.0 transport |
| `redb.Route.AzureServiceBus` | Azure Service Bus transport |
| `redb.Route.Controllers` | Transport-agnostic controller dispatch |
| `redb.Route.Core` | Bridge to redb.Core EAV storage |
| `redb.Route.Elasticsearch` | Elasticsearch 8.x transport |
| `redb.Route.File` | File system transport |
| `redb.Route.Firebase` | Firebase (Firestore, Cloud Storage, FCM) transport |
| `redb.Route.Ftp` | FTP/FTPS transport |
| `redb.Route.GenericFile` | Base library for file-based transports |
| `redb.Route.Grpc` | gRPC transport |
| `redb.Route.Http` | HTTP/HTTPS transport |
| `redb.Route.IbmMq` | IBM MQ transport |
| `redb.Route.Kafka` | Apache Kafka transport |
| `redb.Route.Ldap` | LDAP / Active Directory transport |
| `redb.Route.Mail` | Email transport (SMTP, IMAP, POP3) |
| `redb.Route.MqttNet` | MQTT 5.0 transport |
| `redb.Route.Quartz` | Quartz.NET scheduling transport |
| `redb.Route.RabbitMQ` | RabbitMQ transport |
| `redb.Route.Redis` | Redis transport |
| `redb.Route.S3` | AWS S3 / MinIO transport |
| `redb.Route.Sftp` | SFTP transport |
| `redb.Route.SignalR` | SignalR transport |
| `redb.Route.Sql` | SQL database transport |
| `redb.Route.Tcp` | Raw TCP transport |
| `redb.Route.Validation.Adapters` | FluentValidation + DataAnnotations adapters |
| `redb.Route.WebSocket` | WebSocket transport |

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Note on version history:** redb.Route has been running in production since version 1.0.0.
> Versions 1.0.0 – 1.0.3 were not published to NuGet (internal deployments only).
> The first public NuGet release is **1.0.4**.

## [2.0.2] — 2026-05-16

### Changed
- **`redb.Route.Core`** — bumped `redb.Core` dependency to `2.0.2`.
  `redb.Core 2.0.2` renames `EavSaveStrategy` → `PropsSaveStrategy`; no API
  changes in `redb.Route.Core` itself.

## [2.0.1] — 2026-05-12

### Fixed
- **`redb.Route.Http`** — `HttpConsumer.WriteResponse` no longer echoes
  request headers back into the response. The original request header names
  are remembered on the exchange (`redbHttp.RequestHeaderNames` property)
  and skipped when copying headers from `exchange.In` (which acts as the
  fallback response message when `Out` is not set).
- **`redb.Route.Http`** — invalid header values (control characters and
  non-ASCII bytes that Kestrel would reject) are now filtered out instead
  of crashing the response pipeline.
- **`redb.Route.Http`** — internal framework headers (`redb*`, `Camel*`)
  are stripped from outgoing responses.
- **`redb.Route.Http`** — body-less InOut responses (HTTP 302 redirects,
  204 No Content, Set-Cookie-only replies) continue to propagate
  `Location` / `Set-Cookie` / etc. correctly; header copying remains
  unconditional and only the body write is gated on `Body is not null`.
- **`redb.Route.Ldap`** — service-account authenticated endpoints
  (`bindDn` is set) no longer reuse pooled connections. Active Directory
  could report *"successful bind must be completed"* on a pooled socket
  that was TCP-connected but no longer bound server-side. Such connections
  are now created per-operation and disposed on release.
- **`redb.Route.Ldap`** — `PageSize=0` is now a valid value that disables
  the RFC 2696 paged-results control entirely, for LDAP servers that do
  not support it. Validation accepts `PageSize >= 0`.
- **`redb.Route.Ldap`** — `LdapReferralException` raised during a search
  with `followReferrals=false` is now logged at Debug and the result
  iteration breaks cleanly instead of bubbling up.

### Changed
- **`redb.Route.Http`** — `HttpConsumer.HandleRequest` wraps `WriteResponse`
  in a try/catch that logs the failing method, path, route id and whether
  the response had already started, to aid diagnosing
  *"response already started"* errors.
- **`redb.Route.Ldap`** — service-account `Bind` switched from the
  4-argument overload (with explicit protocol version) to the 2-argument
  `BindAsync(dn, password, ct)`. The `protocolVersion` option is no longer
  forwarded to the bind call (LDAPv3 default of the underlying client
  applies).

## [2.0.0] — 2026-05-07

### Changed
- **License re-stated as Apache-2.0** as part of the RedBase 2.0 release
  alignment. Previous public release (`1.0.4`) carried the same license text
  in `LICENSE` but was tagged as MIT in some README badges; all metadata is
  now consistent (`Apache-2.0` in csproj, README badges, and CONTRIBUTING).
- Every nupkg now ships `LICENSE` + `NOTICE` files (Apache 2.0 § 4).
- Contributions are accepted under Apache-2.0; see `CONTRIBUTING.md`.
- Version bumped to `2.0.0` to align with the RedBase 2.0 release train
  (root packages also moved 1.3.0 → 2.0.0). No source-level API changes vs 1.0.4.

## [1.0.4] — 2026-05-06

First public NuGet release. The library has been production-tested since 1.0.0.

### Added

**Core engine (`redb.Route`)**
- Fluent DSL: `From → Process → To` pipeline definition via `IRouteDefinition`
- `RouteBuilder` base class for encapsulating route logic in dedicated classes
- Two-phase architecture: define (record `RouteStep` list) → compile (`RouteCompiler` builds processor chain)
- 24 EIP pattern processors: Filter, Choice, Split, Aggregate, WireTap, Multicast, RecipientList, DynamicRouter, Loop, Delay, Resequencer, Enrich, PollEnrich, IdempotentConsumer, Throttle, CircuitBreaker, Retry, DeadLetterChannel, DoTry/DoCatch/DoFinally, Transacted, Respond
- Expression engine: `Body`, `Header`, `Property`, `Constant`, `JPath`, `XPath`, `StringExpression` (`Expr`), `Exchange`
- 17 predicate methods: `isEqualTo`, `isNotEqualTo`, `isGreaterThan`, `isLessThan`, `isGreaterThanOrEqualTo`, `isLessThanOrEqualTo`, `isBetween`, `contains`, `startsWith`, `endsWith`, `regex`, `In`, `isNull`, `isNotNull`, `Handled`, `ExceptionHandled`
- String expression templates: `${header.name}`, `${body}`, `${property.key}`
- Built-in components: `Direct`, `SEDA`, `Timer`, `Log`, `Mock`
- Validation: JSON Schema (`JsonSchemaValidator`), XSD (`XsdValidator`), predicate (`PredicateValidator`)
- Serialization: JSON and XML marshal/unmarshal
- Error handling: `OnException<T>` with max redeliveries, exponential backoff, dead-letter routing
- OpenTelemetry: distributed tracing (`Traced`) and metrics (`Metered`) per route and per step
- Structured logging DSL: `.Log(LogLevel).Message().Header().ShowRouteId()`
- `InOut` exchange pattern support
- `RouteId` for route identification and introspection
- `RouteEngineOptions` for telemetry and metrics configuration
- Multi-target: `net8.0`, `net9.0`, `net10.0`

**Transports**
- `redb.Route.Kafka` — consumer/producer, consumer groups, SASL/SSL, transactions, Confluent.Kafka 7.x
- `redb.Route.RabbitMQ` — queues, exchanges, DLX, priority, TTL, quorum queues, RabbitMQ.Client 7.x
- `redb.Route.Redis` — Pub/Sub, Streams (consumer groups), KV, Lists, Sorted Sets, Geo, StackExchange.Redis
- `redb.Route.Sql` — ADO.NET polling consumer, query/batch producer, stored procedures, provider-agnostic
- `redb.Route.Http` — HttpClient producer, Kestrel consumer, CORS, auth, TLS, named URL parameters
- `redb.Route.Grpc` — GrpcChannel client, Kestrel server, binary message exchange
- `redb.Route.File` — polling consumer with glob, read locking, idempotency; atomic producer with temp-file
- `redb.Route.Sftp` — SSH.NET, key/password auth, proxy, glob, chmod, recursive traversal
- `redb.Route.MqttNet` — MQTT 5.0, QoS 0/1/2, shared subscriptions, retained, TLS, MQTTnet
- `redb.Route.Amqp` — AMQP 1.0 (Artemis, Azure SB, Amazon MQ, Qpid), AMQPNetLite
- `redb.Route.Mail` — SMTP producer, IMAP/POP3 consumers with IDLE push, attachments, OAuth, MailKit
- `redb.Route.Tcp` — text-line, length-prefixed, raw framing, TLS, InOut request-reply
- `redb.Route.WebSocket` — ClientWebSocket producer, Kestrel server consumer, ping/pong, subprotocol
- `redb.Route.Quartz` — Cron expressions, interval timers, Quartz.NET thread pool
- `redb.Route.AzureServiceBus` — queues, topics, sessions (FIFO), PeekLock/ReceiveAndDelete, batch send
- `redb.Route.Elasticsearch` — 9 producer operations (index, update, delete, bulk, etc.), polling consumer, Elasticsearch 8.x
- `redb.Route.Firebase` — Firestore (CRUD, queries, batch), Cloud Storage, FCM; shared credential provider
- `redb.Route.Ftp` — FluentFTP, passive/active, FTPS/TLS, jail-path protection, idempotency
- `redb.Route.IbmMq` — IBM MQI, queues, topics, transactions, RPC, message groups, W3C telemetry
- `redb.Route.Ldap` — LDAP/AD search, CRUD, authentication, change tracking, Novell.Directory.Ldap
- `redb.Route.S3` — AWS S3 + MinIO, multipart upload, SSE (S3/KMS/C), presigned URLs, versioning, Glacier restore
- `redb.Route.SignalR` — Hub consumer (server), client producer (`HubConnection`), broadcast producer (`IHubContext`)

**Integration & adapters**
- `redb.Route.Core` — `RedbIdempotentRepository` backed by redb.Core EAV; `IRedbService` access from routes
- `redb.Route.Controllers` — `RedbController`, attribute routing, parameter binding, 4 dispatchers (generic, HTTP, SignalR, gRPC)
- `redb.Route.GenericFile` — shared base for File, FTP, SFTP (abstract consumer/producer, options, file-ops interfaces)
- `redb.Route.Validation.Adapters` — `FluentValidationMessageValidator<T>`, `DataAnnotationsValidator`, DSL extensions
