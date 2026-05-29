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

## [3.0.0] — 2026-05-28

### Added

#### DSL — full Camel parity, single canonical `RouteDefinition`
- **`redb.Route` (DSL)** — Package A "enterprise EIP closure": the parallel
  v2 type tree (`IRouteDefinition2`, `RouteBuilder2`, `BlockStack`,
  `ExceptionRouteDefinition`, the v1 `OldRouteCompiler`, the v1 typed
  `Abstractions/Typed/IRouteDefinition.cs`, etc.) has been collapsed into a
  single canonical surface — `IRouteDefinition` / `RouteDefinition` /
  `RouteBuilder`. The route AST is now exclusively built from
  `IProcessorDefinition` nodes, each of which compiles itself via
  `CreateProcessor(IRouteContext)`; there is no separate compiler class. The
  previous "v2 DSL → bridge → legacy compiler" indirection has been removed.
- **`redb.Route` (DSL)** — `IRouteContext` is now propagated down the
  definition tree via a `Parent` chain, so any nested `*Definition` can reach
  the owning context (logger factory, services, idempotent repositories,
  policy factories) without explicit threading.
- **`redb.Route` (DSL)** — `RouteStepProjection`: a read-only canonical
  projection of the `IProcessorDefinition` tree into `RouteStep` records,
  exposed as `RouteDefinition.Steps`. Intended for diagnostics, validation,
  and tooling (e.g. route visualisers); it is **not** used by the runtime
  compiler. `FromStep`, `ToStep`, `FilterStep` (with optional `SubSteps`
  body), `ChoiceStep`, `SagaRouteStep`, etc. all flow through this
  projection.
- **`redb.Route` (DSL)** — `RouteBuilder.Definitions` and
  `RouteBuilder.ExceptionDefinitions` are now `public` (previously
  `internal`). This unblocks downstream test fixtures and tooling that need
  to introspect the route AST after `Build()`.
- **`redb.Route` (DSL)** — `OnExceptionDefinition` gained the fluent setters
  `LogStackTrace(bool)` and `LogExhausted(bool)` to match the rest of the
  Camel `onException(...)` builder surface.

#### Dynamic endpoints (Camel `toD()` / dynamic `wireTap` / dynamic `enrich`)
- **`redb.Route` (DSL)** — `DynamicEndpointResolver`: per-instance producer
  cache keyed by the URI resolved at runtime. Three constructors accept a
  string template (`${header.xxx}` / `${property.yyy}` / `${body}`
  placeholders), an `IExpression` instance, or a raw
  `Func<IExchange, string>`. Producers are tracked via
  `RouteContext.TrackProducer(...)` for graceful shutdown.
- **`redb.Route` (DSL)** — `ToDynamicProcessor` + `ToDynamicDefinition`
  implement Camel's `toD(...)` — `IRouteDefinition.ToD(string|IExpression|Func)`.
- **`redb.Route` (DSL)** — `WireTapDynamicDefinition`,
  `EnrichDynamicDefinition`, `PollEnrichDynamicDefinition` and matching
  `IRouteDefinition.WireTap(...)` / `Enrich(...)` / `PollEnrich(...)`
  overloads that accept a dynamic URI. `EnrichProcessor` and
  `PollEnrichProcessor` gained an alternate constructor taking a
  `DynamicEndpointResolver`; their `Process` chooses between the resolver
  and the cached producer at run time.
- **`redb.Route` (DSL)** — string-template expression DSL:
  `SetBodyExpression(...)`, `SetHeaderExpression(...)`,
  `SetPropertyExpression(...)` on `IRouteDefinition`.
- **`redb.Route` (DSL)** — `LogDefinition.LogStaticDefinition` auto-upgrades
  to `TemplateLogProcessor` when the configured message contains a
  `${...}` placeholder, so users get template-interpolation without a
  separate API.
- **`redb.Route` (Core)** — `RouteContext` now registers the current
  `ILoggerFactory` into its service collection so processors built from
  `.Log(...)` / template expressions can resolve their logger without
  extra plumbing.

#### Tests
- **`redb.Route` (Tests)** — new DSL **reference suites** that pin Camel
  semantics with extensive scenario coverage:
  `Reference/DslChoiceReferenceTests.cs` (~767 lines),
  `Reference/DslDoTryReferenceTests.cs` (~441 lines),
  `Reference/DslFilterReferenceTests.cs` (extended). These are the
  authoritative compatibility specs for Choice/When/Otherwise,
  TryCatchFinally and Filter scope semantics.
- **`redb.Route.Tests.Core`** — twelve tests (`RedbRouteExtensionsTests`,
  `RedbTransactedActionTests`) were rewritten on top of the real
  `RouteDefinition` + `Exchange` pipeline, removing the previous
  `IRouteDefinition` mock-based scaffolding.

#### IBM MQ diagnostics
- **`redb.Route.IbmMq`** — diagnostic timing around `MQGET`. The consumer
  emits a `Debug`-level `MQGET blocked for {N}ms` log entry for any blocking
  get longer than ~50 ms. This was originally raised at `Information` while
  diagnosing a ~500 ms producer→consumer latency in production; it has been
  lowered to `Debug` so it stays silent under default verbosity and only
  lights up when ops explicitly enable IBM MQ diagnostics.
  `IbmMqProducer` / `IbmMqMessageHelper` / `IbmMqEndpoint` /
  `IbmMqComponent` received the supporting plumbing.

### Known limitations
- **`redb.Route.IbmMq` — ~500 ms minimum end-to-end latency on the managed
  client.** The managed IBM MQ .NET client (`amqmdnetstd.dll`) used by this
  package is **not event-driven** on `MQGET` with `MQGMO_WAIT`. It carries
  an internal polling tick of ~500 ms that is **independent** of the
  `WaitInterval` supplied in `MQGMO`: `WaitInterval` only governs the upper
  timeout, not the lower delivery-granularity bound. As a result the
  typical producer→consumer latency on this transport is ~500 ms even after
  channel reconfiguration (we have validated `SHARECNV(1)` on
  `DEV.APP.SVRCONN` — it does not change the floor). The native
  (unmanaged) client is event-driven but requires the IBM MQ Client
  redistributable to be installed on the host, which is not viable for
  self-contained .NET deployments and is therefore not used here.

  **Planned fix:** rewrite `IbmMqConsumer.ReceiveLoopAsync` to use the
  managed async-consume API (`MQQueue.Cb(...)` +
  `MQQueueManager.Ctl(MQOP_START, ...)`). With the callback path the broker
  pushes messages and per-message latency drops to ~0. Tracked for a future
  release; the change is non-trivial because the loop becomes
  callback-driven (different cancellation, back-pressure and lifecycle
  model than the current poll loop). See the in-source `KNOWN ISSUE` block
  in [`IbmMqConsumer.cs`](src/redb.Route.IbmMq/IbmMqConsumer.cs) for
  details.

  **Field diagnosis recipe.** Enable `Debug` on
  `redb.Route.IbmMq.IbmMqConsumer` and inspect the
  `MQGET blocked for {N}ms` log line:
    - `N ≈ 500 ms` consistently → managed-client polling tick; the
      MQCB rewrite above is required.
    - `N < 50 ms` while end-to-end latency is still ~500 ms → the
      bottleneck is on the producer side (PUT missing a flush or an
      extra round-trip), not the consumer.

### Added — Telemetry (carried over)
- **`redb.Route` (Telemetry)** — shared telemetry identity. Both `Meter` and
  `ActivitySource` now use a single canonical name `redb.Route`, exposed via
  the `RouteActivitySource.TelemetryName` constant (also surfaced as
  `RouteActivitySource.SourceName` and `RouteMetrics.MeterName`). OTel
  collectors can subscribe once and get both signals.
- **`redb.Route` (Telemetry)** — `RouteTelemetryExtensions.StartTransportSpan(...)`
  helper that opens a transport span with the conventional OpenTelemetry
  semantic attributes (`messaging.system` / `db.system` / `http.method` /
  `rpc.system` / `network.transport`, plus `redb.route.endpoint`,
  `messaging.destination.name`, `messaging.operation`). Returns `null` when
  no listener is registered (zero overhead).
- **`redb.Route` (Telemetry)** — `ProcessorMetrics` gained 16 new instruments
  covering the previously-unmeasured EIP processors:
  - WireTap: `redb.route.wiretap.dispatched`, `redb.route.wiretap.failed`
  - Multicast: `redb.route.multicast.branches`, `redb.route.multicast.failed_branches`
  - Recipient List: `redb.route.recipientlist.recipients`
  - Aggregator: `redb.route.aggregator.completed`,
    `redb.route.aggregator.timed_out`, `redb.route.aggregator.inflight_groups`
  - Idempotent Consumer: `redb.route.idempotent.duplicate`,
    `redb.route.idempotent.passed`
  - Retry: `redb.route.retry.attempts`, `redb.route.retry.success`,
    `redb.route.retry.exhausted`
  - Saga: `redb.route.saga.completed`, `redb.route.saga.compensated`,
    `redb.route.saga.failed`
  - Dead Letter: `redb.route.deadletter.sent`
- **`redb.Route` (Telemetry)** — `MeteredProcessor` now enriches every metric
  point with the new tags `redb.route.endpoint` (canonical endpoint URI) and
  `redb.route.scheme` (transport scheme such as `http`, `kafka`, `postgres`)
  in addition to the existing `redb.route.id`.
- **Transport spans** — 16 producers now open a transport span via the new
  helper, producing OpenTelemetry-compliant span trees from the route pipeline
  down to the wire: `Http`, `Sql`, `Sql` (procedure), `Grpc`, `MqttNet`,
  `AzureServiceBus`, `Redis`, `Elasticsearch`, `Tcp`, `S3`, `GenericFile`
  (covers File / Sftp / Ftp), `Firebase.Storage`, `Firebase.Firestore`,
  `Firebase.Fcm`, `WebSocket`, `SignalR`. The five previously-instrumented
  transports (`Kafka`, `RabbitMQ`, `IbmMq`, `Amqp`, `Mail`, `Ldap`) keep their
  existing spans unchanged.

### Changed
- **`redb.Route` (DSL)** — `IOldRouteDefinition` renamed to `IRouteDefinition`
  and all consumer projects (`redb.Route.Controllers`,
  `redb.Route.Core`, `redb.Route.Validation.Adapters`,
  `redb.Route.Tests.Core`) realigned. The Camel-style canonical name is now
  the single name across the public API.
- **`redb.Route`** — `MeteredProcessor` constructor signature gained two
  optional parameters `endpointUri` and `endpointScheme`. Existing call sites
  that only pass `(inner, routeId)` continue to work; `RouteContext` now wires
  the endpoint URI and scheme so dashboards can slice metrics per endpoint.
- **`redb.Route`** — `InstrumentedProcessor.ActivityExtensions.RecordException`
  uses `Activity.AddException(...)` on NET9+ and falls back to a manual
  `ActivityEvent("exception", ...)` with `exception.type` / `exception.message` /
  `exception.stacktrace` tags on NET8, matching the OpenTelemetry
  exception-recording convention on both target frameworks.

### Removed
- **`redb.Route` (Legacy)** — the entire v1 compiler stack has been removed:
  `OldRouteCompiler` (~907 lines), `OldRouteDefinition` (~1500 lines
  partial), `OldRouteDefinition<TIn>`, `OldRouteBuilder` /
  `OldInlineRouteBuilder`, `OldCompiledRoute`, `BlockStack`,
  `ExceptionRouteDefinition`, `IOldRouteDefinition`, the
  `Legacy/Abstractions/Typed/IRouteDefinition.cs`, `Legacy/Extensions/*`,
  the v2→v1 bridges (`RouteBuilder2BatchBridge`,
  `RouteDefinition2BridgeBuilder`, `ProcessorDefinitionWrapperStep`), and the
  `IRouteDefinition2` / `RouteBuilder2` parallel surface. The `Legacy/`
  folder no longer exists. `RouteContext._builders` /
  `RouteContext._routes` are now `List<RouteBuilder>` /
  `List<CompiledRoute>` directly, with no intermediate adapter.
- **`redb.Route`** — five stale code comments still referencing
  `OldRouteCompiler` / `OldRouteDefinition` (in `RouteStep`,
  `NormalizerDefinition`, `SagaDefinition`, `AggregatorProcessor`,
  `IdempotentConsumerProcessor`) were rewritten in terms of the current
  type names; explanatory intent preserved.

### Notes
- **Pipeline EIP semantics.** `PipelineProcessor` now strictly follows the
  Camel Pipeline contract: between steps, an `Out` produced by step `i` is
  merged into `In` and cleared before step `i+1` runs; on the **final** step
  `Out` is left as-is and is **not** synthesised from `In`. InOut callers
  should therefore consume the reply as `exchange.Out ?? exchange.In`. This
  was previously documented inline in `PipelineProcessor.cs`; recording it
  here as the authoritative engine contract. Downstream conventions (e.g.
  the Identity layer's "business processors write to `In.Body`, do not
  pre-create `Out`") sit on top of this contract without changing it.

### Tests
- **`redb.Route.Tests`** — new `Telemetry/InMemoryTelemetryTests.cs` using the
  OpenTelemetry SDK in-memory exporters (`OpenTelemetry.Exporter.InMemory`)
  to verify: shared meter/activity-source name, WireTap dispatched/failed,
  Multicast branches/failed-branches, Idempotent passed/duplicate, Retry
  attempts/success/exhausted, transport-span semantic tags, `MeteredProcessor`
  endpoint/scheme tag enrichment, and `Activity.AddException` event emission.
- **Per-transport telemetry smoke tests** — added `*TelemetrySmokeTests.cs`
  files (and one Firebase pair appended to `FirebaseIntegrationTests`)
  covering all P1 transport spans: Http, Tcp, WebSocket, Grpc, Sql,
  SqlProcedure, GenericFile, MqttNet, Redis, S3, Elasticsearch, SignalR,
  AzureServiceBus, Firestore, Firebase Storage. Each test builds a real
  endpoint, runs the producer through `OpenTelemetry.Sdk.CreateTracerProviderBuilder()
  .AddSource(RouteActivitySource.SourceName).AddInMemoryExporter(...)`,
  and asserts the conventional semantic attributes
  (`http.method` / `network.transport` / `db.system` / `messaging.system` /
  `rpc.system` / `redb.system`, plus `redb.route.endpoint` and
  `messaging.destination.name`). Docker-dependent tests are tagged
  `[Trait("Category","Integration")]`.

### Pending (integration smoke)
- _(none — completed below; see `### Tests` for the per-transport smoke sweep.)_

### Fixed
- **`redb.Route.Ldap` (tests)** — `LdapEndpointOptionsTests.Validate_ZeroPageSize_*`
  and `LdapComponentTests.CreateEndpoint_InvalidPageSize_Throws` were updated to
  match the (already-shipped) behaviour where `PageSize=0` legitimately disables
  the paged-results control. The tests now assert that `PageSize=0` is accepted
  and that only `PageSize < 0` throws.
- **`redb.Route.Firebase` (tests)** — `FirestoreEndpointOptionsTests.Validate_NoCredential_NoEnvVar_Throws`
  now captures and restores the `GOOGLE_APPLICATION_CREDENTIALS` and
  `FIRESTORE_EMULATOR_HOST` environment variables in a `try`/`finally` to
  avoid racing with `FirebaseIntegrationTests.InitializeAsync`, which sets
  `FIRESTORE_EMULATOR_HOST` for the whole test host.
- **`redb.Route.Firebase` (tests)** — xUnit collection-level race fixed.
  `try`/`finally` alone was not enough: by default xUnit runs test classes in
  different collections concurrently within an assembly, so option-validation
  classes that mutate `FIRESTORE_EMULATOR_HOST` / `GOOGLE_APPLICATION_CREDENTIALS`
  could still overlap with the live-emulator integration suite that reads them.
  Introduced `FirebaseEnvSensitiveCollection` (`[CollectionDefinition("FirebaseEnvSensitive", DisableParallelization = true)]`)
  and applied `[Collection("FirebaseEnvSensitive")]` to all four env-sensitive
  classes (`FirestoreEndpointOptionsTests`, `FirebaseStorageEndpointOptionsTests`,
  `FcmEndpointOptionsTests`, `FirebaseIntegrationTests`). Result: 149/149 PASS,
  no intermittent
  `Emulator environment variable 'FIRESTORE_EMULATOR_HOST' is not set` failures.
- **`redb.Route` (dev/test infra)** — `docker-compose.tests.yml`: the Azure
  Service Bus emulator (`servicebus`) had `SQL_SERVER: azurite` configured, but
  Azurite is blob/queue/table storage and does not speak TDS. The emulator host
  therefore crash-looped on startup (initial run created MDFs in the container's
  writable layer, subsequent restarts failed with
  *Cannot create file '/var/opt/mssql/data/SbGatewayDatabase.mdf' because it already exists*),
  killing the AMQP listener mid-suite and producing
  *AMQP transport failed to open because the inner transport tcpNN is closed*
  on the consumer side. Added a dedicated `sqledge` service
  (`mcr.microsoft.com/azure-sql-edge:latest`) with `ACCEPT_EULA=Y` /
  `MSSQL_SA_PASSWORD`, changed `servicebus.environment.SQL_SERVER` to `sqledge`,
  declared the dependency, and bumped `start_period` to `60s` to cover SQL Edge
  warm-up. This is a test-infra change only; published packages are not
  affected.

### Fixed
- **`redb.Route`** — `WireTapProcessor` no longer propagates the caller's
  `CancellationToken` into the fire-and-forget tap branch. Previously, when the
  main pipeline was cancelled (e.g. an HTTP request was aborted by the client),
  an in-flight audit/notification tap could be killed mid-write — typically
  surfacing as a failed `ExecuteNonQuery`/`Commit` on the audit store. The tap
  branch now runs with `CancellationToken.None` and is only torn down on host
  shutdown, which matches the EIP "InOnly, detached" semantics of WireTap.

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
