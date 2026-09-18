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

## [4.0.1] — 2026-09-18

### Added

- `LlmThinkingBlock` (`redb.Route.Llm.Providers`) — the thinking a provider returns is carried as a
  content block of its own (Anthropic `thinking` / `redacted_thinking` with their signature, DeepSeek
  `reasoning_content`) and handed back unchanged, in the original order, on the next request of the
  run. Each request path sends what its wire can express: Anthropic a signed or redacted block, the
  OpenAI-compatible path the block's text as `reasoning_content` where `tools` are declared. Thinking
  is never shown — it stays out of `AgentResponse.Text` and `Out.Body`. Streaming responses do not
  carry thinking blocks yet.
- Thinking blocks in the conversation record (`redb.Route.Llm.Storage.Redb`) — `MessageContentBlock`
  gains kind `"thinking"` with the thought in a field of its own, the signature beside it and the
  redacted payload, so a saved conversation replays a signed block byte-for-byte instead of losing it;
  a conversation continued on another provider goes to it as it was written. A turn of nothing but
  reasoning is written like any other turn, with its usage and stop reason.
- `IToolClaimsSource` (`redb.Route.Llm.Abstractions`) — resolves the claims of a run's principal, plus
  the default implementation `ExchangePrincipalClaimsSource`. The agent loop verifies
  `LlmToolSafety.RequiredClaims` before approval and denies a call it cannot verify (fail closed). The
  default source reads the caller's principal from the exchange (`ExchangePrincipal` — a property, so a
  client cannot send it as a header) and takes its `scope` / `scp` values, split on whitespace only
  (RFC 6749 §3.3 — a comma is a legal character inside a scope token, not a separator); a deployment
  with another vocabulary registers its own source and wins. A principal whose identity is not
  authenticated counts as no principal, so a resolver must return an identity with an authentication
  type. If a host removes the default source, a claim-declaring tool stops building at route build, with
  the required registration in the message — a requirement that can never be satisfied does not ship
  silently.
- `ICostCalculator` (`redb.Route.Llm`) — prices provider responses so cost budgets mean something.
- Budget URI options `budgetInputTokens`, `budgetOutputTokens`, `budgetCostUsd`, plus `.Budget(...)`
  on the fluent LLM builder and `WithBudget(...)` on the inline `.Llm(...)` builder.
- `StoreBudgetEnforcer` — cross-run budget enforcement over `ICostBudgetStore`, registered by
  `AddRedbLlmStorage()`.
- `ToolSkipReasons` / `ToolResultErrors` — the observer-channel skip reasons and the model-facing
  `tool_result` error codes as constants instead of scattered literals.
- `AgentEngine.FromContext(IRouteContext)` — builds an engine from whatever the route context and its DI
  container provide, falling back to the shipped no-op collaborators. `AddRedbRouteLlm()` registers the
  engine through it, so one extension call yields every seam (producer template, claims source, cache,
  cost calculator) instead of depending on the container's constructor selection.

### Fixed — concurrent LLM runs no longer share one redb instance and connection

- **Scheduled `llm:` prompts.** `From("llm://...?schedule=...")` resolved `initialBodyRef` and
  `systemPromptRef` without an exchange, so a redb-backed `IPromptTemplateRegistry` answered every
  concurrent tick from one shared instance and connection, and a second tick's lookup could be refused.
  The tick now creates its exchange first, sets the named redb on it, resolves both prompts with it and
  only then writes the initial body. The lookup runs inside the block that owns the exchange, so a
  failing template releases the exchange and its DI scope instead of leaking them.
- **Budget and audit.** `StoreBudgetEnforcer` (through `RedbCostBudgetStore`) and `RedbAuditObserver` run
  without an exchange and used `GetRedbService(name, exchange: null)`, so every concurrent agent run shared
  one instance and one connection: a second simultaneous command was refused, and the audit row was lost.
  Each call now opens a scope of its own with `IRouteContext.CreateRedbScope(name)` and releases it. A
  refusal to open that scope is a configuration error, and the budget call fails with it. The budget still
  runs outside the route's transaction; the audit row stays in it and rolls back with the tool writes it
  describes.
- **Audit failures.** `RedbAuditObserver` swallowed a failed write without a trace. It still never breaks
  the run, but the failure is now logged as a warning with the tool name, the `tool_use` id and the
  exchange id; the logger comes from the route context like the engine's.

### Fixed — the XML pack gate no longer reads SQL parameters as bean references

- `redb-route-xml check|pack` (`redb.Route.Xml` packaging) looked for registry references with a
  `#name` search over the whole attribute, so every Camel-style SQL parameter of the `sql:` connector
  (`VALUES (:#login, :#at)`) was reported as an undeclared, possibly dangling bean. A reference is now
  recognised by position, as the format convention states: a value that starts with `#` (the whole
  attribute, the path right after the scheme as in `bean:#x`, or a URI option value as in
  `dataSource=#main-db`). A `#` inside the SQL text is text. XML routes use the same
  `:#name` syntax.

### Added — a public transactions guide at the root

- `TRANSACTIONS.md` joins `METRICS.md` and `CONCURRENCY.md` as a public guide: the transaction model, one route end
  to end with `Retry`, a dead-letter channel and an idempotent consumer, the order a unit of work commits in, who
  acknowledges the incoming message, why `.WireTap(...)` stays outside, writing an `ITransactedAction`, one database
  per transacted route, parallelism, and the migration off `BeginRedbTransaction()`.
  The README section on deferred acknowledgement is corrected to the current order.

### Changed — behavioural — the unit of work commits database first, brokers after

- `.Transacted()` used to flush the deferred transport actions **before** completing its `TransactionScope`, so a
  message could be sent, or an incoming one acknowledged, while the database work was still uncommitted, and a
  failing database commit then left a message pointing at rows nobody could read. The order is now: the database
  transaction completes and closes, then the deferred sends go out. The failure window is "written but not
  announced" — the broker redelivers and the idempotent consumer absorbs the duplicate — never "announced but not
  written". A send that fails after the database committed is logged as such and propagates, so the message is not
  acknowledged and the work is picked up again.
- **`Retry` inside `.Transacted()` now wraps the transaction instead of living inside it.** One attempt is one unit
  of work: a failed attempt rolls back its own database work and its own deferred sends, and the next attempt starts
  with an empty set, so the attempt that succeeds no longer publishes the messages of the attempts that failed. No
  transaction is held open across the retry delays either, which is what made a transacted broker route hold locks
  for the whole retry sequence.
- **The inbound acknowledgement belongs to the consumer, not to the transaction.** RabbitMQ, AMQP 1.0, Kafka
  (single and batch), IBM MQ (poll and XMS), Redis Streams, Azure Service Bus (queue and session) and SQS no longer
  register the ack as an `ITransactedAction`; each settles after the whole unit of work, the route transaction
  included, has succeeded, and leaves the message unsettled when it has not. Rolling an ack back is a nack — an
  action on the broker, not an undo of work — and with `Retry` outside the transaction it would have made the broker
  redeliver a message while the local retry was still running on it. Outgoing sends stay deferred and transactional.
- **What this changes for an implementer of `ITransactedAction`:** the interface is the same, but `Commit` is now
  called after the database transaction has closed, so `Transaction.Current` is null there; an action that counted on
  running inside the ambient transaction must be revisited.

### Added — the exchange trail and the OpenTelemetry layer as expression values

- `messageHistory(kind)` in the expression language: the steps an exchange went through, as a value. `table` (the
  default) is the dump the failure handler prints, `compact` one line, `json` an array; `count`, `totalMs` and
  `slowestMs` are numbers, `slowest` and `lastNode` name a step. The numbers make it a predicate, so a route logs the
  trail only when it cost something: `.When("messageHistory(\x27slowestMs\x27) > 500")`. Message history is opt-in, so an
  exchange that recorded none reads as an empty string and zero rather than failing a diagnostic log; an unknown kind
  fails the route build.
- `stats()` reads the OpenTelemetry layer when its target starts with `otel:`:
  `stats(\x27otel:redb.route.step.duration/enrich\x27, \x27max\x27)`, target `otel:instrument[@route][/step]`, field `count`,
  `sum`, `min`, `max` or `last`. The EIP counters (`throttle.delayed`, `circuitbreaker.tripped`, …) and `.Metered()`
  durations live only in that layer, and until now only C# could read them through the snapshot. Needs the in-process
  subscriber (`UseMetricsSnapshot()`) and says so when it is missing; an instrument nobody measured reads as zero; a
  literal unknown field fails the route build.
- Both functions work in markup as well as in C#: XML routes have no lambdas, so `<log message="${messageHistory()}"/>`
  and `<when expr="messageHistory(\x27slowestMs\x27) &gt; 500">` were impossible to express before.

### Changed — the AWS connectors resolve one AWSSDK.Core

- `redb.Route.S3` was on `AWSSDK.S3` 4.0.20.3 while `redb.Route.Sqs` was on `AWSSDK.SQS` and
  `AWSSDK.SimpleNotificationService` 4.0.100.2, so the two connectors resolved different `AWSSDK.Core` versions.
  Side by side in one deployment layer (the Tsak worker publishes every connector into a shared folder) that is a
  file in two versions, and the layer build stops on it. All three packages are now on the same SDK train:
  `AWSSDK.S3` 4.0.103.3, `AWSSDK.SQS` and `AWSSDK.SimpleNotificationService` 4.0.100.14, and both projects resolve
  `AWSSDK.Core` 4.0.102.6 (checked in `obj/project.assets.json`). No code change; the test projects follow the
  same versions.

### Changed — behavioural — a host outside Tsak gets a redb scope per exchange

- A host that runs routes itself (a console, ASP.NET, a worker) and registers a named database with
  `context.RegisterRedbService(name, instance)` publishes no scope factory. With an exchange,
  `GetRedbService(name, exchange)` handed every exchange that one instance: one connection for all parallel
  exchanges, and parallel explicit calls on it refused by redb's command gate. An instance built through a DI
  container now opens a scope of the same database per exchange through `IRedbScopeSource` (redb.Core), cached on
  the exchange and released with it, as the published factory does under Tsak. The same holds for a default
  service registered on the context when the context's service provider has no redb of its own.
- What changes for such a host: a connection per parallel exchange instead of one shared connection (size the
  pool for it); the steps of a route no longer see work done on the registered instance outside the route, for
  example a transaction opened on it by host code. `.Transacted()` is unaffected: one connection per transaction
  and database.
- Unchanged: Tsak, whose published factory is used first; code without an exchange, which keeps the registered
  instance; an instance built without a container, which stays shared.

### Added — a scope of its own for redb work without an exchange

- `context.CreateRedbScope(name)` (`redb.Route.Core`) opens a new scope with its own `IRedbService` on every call,
  for code that runs without an exchange and may run in parallel: an observer, a background job, a store call made
  outside a route step. Without an exchange `GetRedbService(name)` hands every caller the same registered instance,
  one connection, and parallel explicit calls on it are refused by redb's command gate. The caller owns the returned
  `RedbScope` and disposes it (`await using`).
  - **Named:** the scope factory the host published for the name (Tsak does, one per named instance), otherwise a
    scope opened from the instance registered with `RegisterRedbService` through `IRedbScopeSource` (redb.Core).
  - **Default:** a scope opened from the service registered on the context, otherwise a scope of the context's
    service provider.
  - A service built without a DI container has no scope to open: the call throws `InvalidOperationException` rather
    than hand out the shared instance. Single-threaded start-up code (scheme sync, seeding) keeps using
    `GetRedbService` without an exchange.
  - A scope opened inside an ambient transaction takes part in it; work that must survive the route's rollback opens
    its scope under a suppressing `TransactionScope`.

- Guide: [Access IRedbService from Routes](redb.Route.Core/README.md#access-iredbservice-from-routes) —
  which `IRedbService` route code gets, with and without an exchange, and inside a transaction.
  The `redb.Route.Core` README samples are corrected: they called a `RedbIdempotentRepository(redb)` constructor and
  an `exchange.GetService<IRedbService>()` method that do not exist and imported the wrong namespace; the repository
  is registered by name with `AddRedbIdempotentRepository`, route steps use `ProcessWithRedb`.

### Fixed — an unhandled failure is a failure everywhere: transaction and ack

- A route that fails through an `OnException` without `Handled(true)` returns **normally** with the
  exception left on the exchange. `TransactedProcessor` took that return for success: the deferred
  broker actions were committed and the `TransactionScope` completed with the work half done — for
  example a `.Transacted()` body that calls a sub-route (`direct://`) whose own `OnException` has no
  `Handled(true)`. The processor now reads the exchange's terminal state after the body: an unhandled
  exception rolls the deferred actions back and leaves the scope incomplete (rollback), exactly like a
  throw; a handled one is a completed exchange and commits.
- The same failure was **acknowledged** by the broker consumers that settle by "did Process throw":
  RabbitMQ, AMQP 1.0, Kafka (single and batch commit), IBM MQ (poll and XMS engines), Redis Streams
  and MQTT. Each consumer now rethrows an unhandled failure left on the exchange
  (`IExchange.ThrowIfUnhandledFailure()` in `redb.Route.Core`) so its existing failure path takes
  over: nack-requeue, release, no offset commit, backout, no XACK, no MQTT ack. Azure Service Bus and
  SQS already read the terminal state and are unchanged.
- AMQP 1.0: `AmqpAckAction` now tracks whether the delivery is settled. Inside `.Transacted()` the
  transaction accepted the message and the consumer's inline `AutoAccept` accepted it a second time
  (an error on an already settled delivery); the inline path now settles through the same action and
  skips it once settled, on the accept and the release side alike.

### Fixed — a branch's DI scope no longer reaches the original exchange through the merge-back

- Multicast, Splitter, Scatter-Gather and Loop (`copy`) merged the aggregated clone's **properties** into the
  original exchange wholesale, including the clone's own bookkeeping: a named redb scope cached under
  `__redb_scope:*` by a step of the branch, or a resource registered with `ExchangeResources`. The clone
  released those right after the merge, so the original kept a disposed scope under the same key and the
  next redb call on that name failed with `ObjectDisposedException` (a registered resource was released
  twice). The merge-back now skips what the clone owns, as `Clone` / `CreateChild` already do on the way
  in: the exchange that created a scope or registered a resource is the one that releases it.

### Fixed — the editor validated against the schema without the package elements

- The extension's OASIS catalog bound the namespace `urn:redb:route:1.0` to the registry-only
  `redb-route-1.0.xsd`, while the catalog-aware schema that knows the package contributions
  (`<redb>`, `<redbSave>`, `<cache>`, ...) and the typed endpoint options shipped beside it, unbound.
  A project with its own `.vscode` binding (the scaffold writes one) was fine; any other document got
  «Element name 'redb' is invalid» from the editor while the loader and the pack gate accepted it.
  The catalog now binds the catalog-aware schema, and a unit test asserts that the bound file
  declares the package elements.

### Added — `<messageHistory>` in the markup

- The route-level `MessageHistory()` flag of the C# DSL now has a markup form: the leaf step
  `<messageHistory [value="false"]/>`, the way `<streamCaching/>` spells its own flag. Found missing
  while writing the SerialNumbers demo as XML — a route that wants the per-step timing in its failure
  log had no way to say so in markup. Schema, element list, editor palette and the generator's C#
  output carry it.

### Removed — the `<beginRedbTransaction>` markup element

- The route markup no longer has `<beginRedbTransaction [storage=]/>`. The verb it printed,
  `BeginRedbTransaction()`, is obsolete ([TRANSACTIONS.md](TRANSACTIONS.md): one primitive, `.Transacted()`)
  and a no-op under an ambient scope — and markup has no warning channel, so an author writing it
  inside `<transaction>` got a silent nothing. The loader now rejects it as an unknown element with
  the position; wrap the steps in `<transaction policy="…">`, the markup form of `.Transacted()`. The
  schema, the element list and the editor palette are regenerated without it. Shipped in 4.0.0 for
  four days; no package in the wild uses it.

### Fixed — build-time route packaging actually sees the runtime

- `redb-route-xml new` scaffolds a library project, and a library does not copy its NuGet
  assemblies to `bin/` — so the opt-in `PackRouteOnBuild` target handed the gate an output with
  no connectors and no markup contributions, and a `<redbSave>` failed the XSD check for no visible
  reason. The template now sets `CopyLocalLockFileAssemblies`; the XmlDemo project gets the same,
  plus the `redb.Route.Core` reference its `<redb*>` elements need and the package name `xmldemo`
  the worker already knows it by.
- `redb-route-xml` refuses a `--bin` whose `redb.Route.Xml` / `redb.Route` assembly version differs
  from the one the tool was built with. Contributions and components of another runtime version
  cannot be cast to the tool's own interfaces; before, that read as «0 contributions» and a
  misleading unknown-element error (a 3.7.3 tool over 4.0.0 bins). Update the tool to the
  runtime's version.

### Fixed — error handling now matches Camel (behavioural)

- **`OnException` without `Handled(true)` no longer swallows the failure** (`redb.Route`). When an
  exception matched a handler that did not set `Handled`/`Continued`, the processor marked the exchange
  `ExceptionHandled = true` and returned — so every consumer (`GenericFile`, `Quartz`, `As2`, `Http`,
  `Soap`, `Sqs`, `AzureServiceBus`, `Grpc`, `SignalR`), which treats `Exception != null && !ExceptionHandled`
  as a failure, saw success and acknowledged/committed while the transaction had rolled back. The failure
  is now left on the exchange (Camel `handled(false)`): the `OnException` route still runs (logging, DLQ,
  redelivery), but the consumer sees the failure and does not ack. `Handled(true)`/`Continued(true)` clear
  the exception as before. Reproduced red-before at the consumer-contract level.
- **The most-specific `OnException` handler wins, regardless of declaration order** (`redb.Route`).
  Handler selection took the first registered match, so an `OnException<Exception>()` declared first
  captured everything; it now picks the most-derived matching type (Camel's nearest-supertype rule),
  ties keeping declaration order.

### Added — string/quarantine conveniences

- **`Sql.DataSource(string)`** (`redb.Route.Sql`) — the fluent builder accepted only `DataSource(IExpression)`,
  so the documented `.DataSource("main")` did not compile. A string overload now matches the sibling
  connectors' `ConnectionFactory(string)`.
- **`.MoveFailed(...)` on the local File connector** (`redb.Route.File`) — a file whose processing fails
  can be quarantined to a directory (as the remote FTP/SFTP consumers already do) instead of being left in
  place and re-picked on every poll. New `FileEndpointOptions.MoveFailed` + DSL `MoveFailed(...)`; unset
  keeps the previous leave-in-place behaviour.

### Fixed — SQL table bootstrap is dialect-aware

- **`SqlIdempotentRepository` and `SqlClaimCheckRepository`** (`redb.Route.Sql`) auto-created their tables
  with `CREATE TABLE IF NOT EXISTS`, which SQL Server rejects — so `CreateTable = true` (the default) threw
  on SQL Server. The DDL is now chosen from the live connection: SQL Server gets a guarded
  `IF OBJECT_ID(...) IS NULL CREATE TABLE` with `NVARCHAR`/`VARBINARY(MAX)`; SQLite/PostgreSQL/MySQL keep
  `CREATE TABLE IF NOT EXISTS`, with the claim-check binary column typed per dialect (`BYTEA`/`LONGBLOB`/`BLOB`)
  — the latter also fixes claim-check bootstrap on PostgreSQL, which has no `BLOB` type.

### Fixed — documentation corrected

- **`redb.Route` README Error-Handling** showed `.Retry(...)` / `.DeadLetterChannel(...)` as top-level route
  steps with a `maxRetries:`/`initialDelay:` signature; they exist only on the `.Transacted()` scope as
  `Retry(int attempts, TimeSpan delay)` / `DeadLetterChannel(string)`. The example and the EIP table now
  show the correct scoped form.
- **`Unmarshal<T>()`** (`redb.Route`) documented itself as decoding via `IDataFormatRegistry` by ContentType,
  but is an alias of `ConvertBody<T>()` (type-converter based). The doc now says so and points to
  `Unmarshal(IMessageSerializer, Type)` for format decoding.

### Fixed — a poison file no longer blocks the rest of the poll batch

- **File consumers (`redb.Route.GenericFile` → local `File`, `Ftp`, `Sftp`)** ran each polled file with no
  guard around the route: an exception that escaped the route (no matching `OnException`, or a handler that
  rethrew) propagated out of the per-file loop and aborted the whole batch. Every file after the poison one
  in the listing was left unprocessed, and `MoveFailed` never ran for the poison file itself — so on SFTP a
  later file could be reprocessed on a loop while earlier ones sat untouched. The per-file processing now
  catches the escape, records it on the exchange, runs the failure path (idempotent-key release +
  `MoveFailed`) and continues to the next file; `OperationCanceledException` still aborts the poll (shutdown).

### Fixed — a logger factory registered after construction reaches components

- **`RouteContext`** wired component loggers only from a factory passed to its constructor;
  `AddService(typeof(ILoggerFactory), …)` afterwards left components — and the route compiler's
  `OnException`/statistics loggers — without one, so consumer failures were logged nowhere. Registering a
  factory now adopts it as the context factory and back-fills a logger onto components already registered
  (and forward-fills onto those added later).

### Fixed — context/builder-level OnException keeps its full configuration

- **Builder- and context-level `OnException`** (`redb.Route`) forwarded only redeliveries and
  `handled`/`continued` to the compiled handler; `OnWhen`, `RetryWhile`, `OnRedelivery`, `OnPrepareFailure`,
  `RetryAttemptedLogLevel`/`RetriesExhaustedLogLevel`, `LogStackTrace`, `LogExhausted`, `OnExceptionOccurred`
  and `UseOriginalBody` were silently dropped — so even `Handled(true)` still logged
  `Retries exhausted … after 0 attempts`. The compiler now forwards the full configuration, matching the
  in-route `OnException` path.

### Added — poll backoff for file consumers (File / FTP / SFTP)

- **`GenericFileConsumer` gains Apache Camel `ScheduledPollConsumer`-style poll backoff.** New options
  `BackoffMultiplier` / `BackoffIdleThreshold` / `BackoffErrorThreshold` (with matching DSL methods on
  File/FTP/SFTP): after that many consecutive idle or error polls, the next `BackoffMultiplier` polls are
  skipped — no listing and, for remote transports, no reconnect — then the counters reset. A down SFTP
  server or an empty directory no longer forces a poll (and a reconnect attempt) every `Delay`. Entering
  backoff logs one warning, resuming logs one info line; `BackoffMultiplier` requires at least one
  threshold and vice-versa, or the endpoint refuses the configuration. `BackoffOnFailedExchanges` (off by
  default — a superset of Camel) additionally counts a poll whose every created exchange failed unhandled
  as an error, so a route backs off when the downstream is down and every file fails, instead of
  re-reading the same files every `Delay`; a poll with any success still counts as success.

### Fixed — idempotent consumer and redb transactions under `.Transacted()`

- **`IdempotentConsumer` no longer risks a silent-loss window inside a transaction, and no longer masks the
  original failure.** Under an ambient transaction it does not `Remove` the dedup key on failure — the
  rollback removes it, and a `Remove` issued inside a doomed transaction would throw and hide the real
  error. Outside a transaction, a `Remove` that itself fails now keeps the original exception instead of
  being replaced by the cleanup error.
- **`BeginRedbTransaction()` is `[Obsolete]` and a no-op under `.Transacted()`.** With core enlisting redb
  in the ambient `TransactionScope`, opening a separate redb transaction there is rejected; the method now
  attaches without opening one when an ambient scope is active, and only opens its own when none is. Use
  `.Transacted()`.

### Changed — behavioural

- **An external tool does not run inside a transaction.** A tool declared `ToolSideEffect.External` that
  the model calls while an ambient transaction is open (a route's `.Transacted()`) is not dispatched: the
  model receives `external_in_transaction` (`ToolResultErrors.ExternalInTransaction`) and observers see
  the same skip reason (`ToolSkipReasons.ExternalInTransaction`). Its action could not be rolled back with
  the transaction, and a retry after a rollback would have repeated it. Mutating and read-only tools run
  inside the transaction as before, so their writes and deferred sends roll back with the route. The
  budget of an agent run is now recorded outside the route's transaction, so a rollback no longer
  un-counts spent tokens; on SQLite that second writer waits for the transaction's write lock and fails,
  so `llm:` with the redb budget store does not belong inside `.Transacted()` there. A shadow run no longer
  inherits the transaction.
- **Thinking is kept and handed back.** Both connectors used to discard a model's thinking: Anthropic
  `thinking` / `redacted_thinking` blocks and DeepSeek `reasoning_content` were dropped on parse and
  never sent back. They now travel with the turn that produced them, within a run and, with a
  conversation store, across runs, so a stored turn holds more data than before. A message that says
  nothing (no text and no tool call, such as a turn the model spent entirely on thinking) is kept in the
  record but not sent to the provider, on the Anthropic and the OpenAI-compatible path alike; the
  OpenAI-compatible path used to send it as `content: null`, and a refused message in the history
  breaks every later call of a conversation (2026-09-07). `RedbConversationStore` throws on a content
  block type or a stored kind it has no form for, where it used to write the block's `ToString()` as
  text and read an unknown kind back as text.
- **Claims are enforced.** `.RequireClaim(...)` now denies the call when the claims cannot be
  verified; previously the declaration was never consulted. Claims are checked against the caller's
  principal taken from the exchange (a transport-set property — a client-supplied identity header is
  not evidence), so a run with no inbound request, or one carrying an unauthenticated principal, is
  denied. Hosts with their own identity vocabulary register their own `IToolClaimsSource`.
- **Idempotency is gated on the declared side effect.** A tool that is not `ToolSideEffect.ReadOnly`
  is reserved before it runs and a replayed `tool_use` id returns the stored result; read-only tools
  are no longer reserved at all. Consequence: replay protection is no longer the default for tools
  that declare nothing, because `SideEffect` defaults to `ReadOnly` — declare `Mutating` on anything
  that changes state.
- **Tool-result cache is wired.** `ToolCachingPolicy.Memoize` is answered by a run-scoped in-process
  memo, `Persist` by `IToolCacheStore` (24 h TTL); a hit is a skipped invocation reported to observers
  as `cache_hit`. The key includes the *resolved* endpoint address (hashed, never stored raw), so two
  tenants with the same input never share an entry, and the tool's policy fingerprint, so entries
  written while a tool was laxer are not served after it got stricter. Only read-only tools may declare
  a caching policy — the route build rejects `Caching != None` with a mutating side effect — and an
  output the redaction filter changed is never cached.
- **Budgets are reachable and cost is real.** The three budget options reach the engine, and cost
  comes from `ICostCalculator` instead of a placeholder zero. A cost ceiling nobody can price fails
  the run before the first provider call instead of being silently ignored.
- `AddRedbLlmStorage()` now replaces **only the defaults this package ships** (`IBudgetEnforcer`,
  `IAgentObserver`); a host-registered implementation survives untouched. It also swaps the shipped
  per-run budget enforcer for `StoreBudgetEnforcer`, so a conversation's accumulated usage can stop a
  run that used to pass.
- **The cache key carries the caller.** A persisted entry is now keyed by tool name, resolved endpoint,
  policy fingerprint, the caller's identity and the values of the headers the route opted into
  propagating — not by the input alone. A tool route sees the real principal and the real headers, so
  "same input" was not "same answer": before this, an entry fetched for one caller could be served to
  another (same rights, different person).
- **The read-only caching rule is enforced at descriptor registration**, where every path converges —
  `.AsLlmTool(...)`, `LlmTool.Define(...).Build()`, `[ExposeAsLlmTool]` and MCP discovery — and MCP
  server options validate it when they are built. A non-read-only tool that declares a caching policy
  now fails registration (loudly, before any run) instead of being silently cached by paths that only
  the route DSL guarded.
- **Cache access is best effort.** A store that cannot be read or written logs a warning and the tool
  still runs; previously a store outage failed the whole run, and a failed write turned a successful
  tool call into an error the model could retry.
- **Cache entries and audit rows name their tool.** A persisted cache key starts with the tool name
  (`persist:<tool>:<hash>`) instead of being a bare hash, and an audit row's name leads with it
  (`audit:<tool>:<exchange>:<tool_use>`). Both are labels: they let an operator read the scheme and slice it
  by tool on an object column (`_objects.name` / `value_string`). Filtering by the `ToolName` property is
  server-side too (the props-typed `Where`) but resolves through the values table, so the label is the cheap
  path, not the only one. The hash still covers every security-relevant input; entries written in the
  previous format are missed once.
- `stream=true` with `tools=` now throws `ArgumentException` like the other option checks in
  `LlmEndpointOptions.Validate()`, instead of `InvalidOperationException`.
- **`stream=true` together with `tools=` is rejected** when the endpoint is created instead of
  silently ignoring the tools (streaming bypasses the agent engine, so its tools would never run).
  Streaming without tools is unchanged.

### Fixed

- **`llm://` endpoints could not see the engine the package registers.** The endpoint resolved the agent
  engine through the route context's own locator only (`IRouteContext.GetService<T>()` deliberately has no
  fallback), while `AddRedbRouteLlm()` registers the engine in the container — so a host that did everything
  right got `InvalidOperationException: No IAgentEngine registered` on every call. Producers, consumers and
  the inline step now share `AgentEngine.FindRegistered(context)` (locator, then container), which is also
  what made the "engine in the container only" tests turn green.
- The `No IAgentEngine registered` message advised `context.AddService<IAgentEngine>(new AgentEngine())`:
  no such overload exists (`AddService` takes `(Type, object)`), and a bare engine has no producer template
  (a tool call fails) and no claims source (a requirement is never verified). Both messages now name
  `AddRedbRouteLlm()` and `AgentEngine.FromContext(context)`, and say what a bare constructor costs.
- **The engine resolved from DI was not the engine the package wires.** `AddRedbRouteLlm()` registered
  `AgentEngine` by type, so the container picked a constructor it could satisfy — one whose parameters all
  have defaults — and the resolved engine had no producer template (every tool dispatch failed) and none of
  the governance seams this release advertises: claims, cache and cost were wired only in tests, which build
  the engine by hand. The registration is now an explicit factory over `AgentEngine.FromContext(...)`. The
  inline `.Llm(...)` fallback had the same hole (`?? new AgentEngine()`), and the shipped demos plus the demo
  host assembled the engine by hand, silently losing every seam added since — they now register their
  deliberate choices and let the factory build the engine (`Llm.HttpShell` stays on the released surface
  until the package carries `FromContext`; a note in the file gives the exact replacement).
- `ToolAuditProps` documented that `WhereRedb` predicates on `ConversationId`, `ToolName`, `Outcome` and
  `InvokedAtUtc` avoid the value table. Properties are not object columns, so predicates on them resolve
  through `_values`: server-side, but costlier than an object-column filter. The doc now says exactly that,
  and points at the row name as the cheap path for tool-scoped queries.
- Budget option docs promised that a zero ceiling "stops the run at once"; the enforcer has always
  enforced only positive limits, so `0` means "no ceiling" — the docs now say that, and a test pins it.
- MCP discovered tools declare `RequiresApproval = true` by default (external side effects). With the
  shipped `AutoApproveGate` this buys an audit row, not a blocked call.
- `SkipReason` values: `idempotent_cache_hit` → `idempotency_hit`; the approval refusal is now
  `denied: <reason>` built from `ToolSkipReasons.ApprovalDeniedPrefix`, and the model-facing error
  code is `approval_denied`.
- **`ToolSetHash` semantics changed**: the hash of the tool surface now includes safety (side effect,
  caching policy, cost class, approval flag, and claims — length-prefixed so `["a,b"]` and `["a","b"]`
  cannot collide). Hashes computed before this release are not comparable with hashes computed after
  it; re-baseline audit queries that compare them.
- MCP safety-override patterns are compiled when the override is built, so an invalid regex fails at
  configuration time instead of surfacing as "the server is silently absent from the tool set"; an
  override that matches no discovered tool is reported as a warning, with an example of the
  model-facing name it most likely copied.

### Removed

- Metric `redb.route.llm.tool_cache.expired` — an expired entry is indistinguishable from a miss
  through `IToolCacheStore.GetAsync`, and the shipped `Memoize` layer does not touch the store at all.
  The counter was never incremented. Cache stores no longer report hit/miss metrics either: the agent
  loop does, because it is the only layer that sees both the store and the run-scoped memo.

### Added — the caller's identity on the exchange

Nothing could bring an identity to a route over HTTP, gRPC, SOAP or AS2. The consumers ignored who a
request came from, and a trusted middleware had no channel to say it: a `redbHttp.*` header it added is
dropped by the anti-spoofing guard, and any other header cannot be told apart from one the caller sent.
WebSocket and SignalR had their own `Authenticate` hooks, but those reduced the principal to a user-id
string.

- **`ExchangePrincipal`** (core, `redb.Route.Abstractions`) — the caller's `ClaimsPrincipal` as an
  exchange property under `CamelAuthentication` (Camel's `Exchange.AUTHENTICATION`). A property rather
  than a header, so a caller cannot send it and producers do not bridge it on. Every exchange copy
  (child, linked child, clone, snapshot) inherits it, so a split part, a sub-route or an LLM tool call
  sees the identity of the request that started it. A route that validates credentials itself records
  the outcome with `ExchangePrincipal.Set`.
- **`HttpHostingOptions.ResolvePrincipal`** — one resolver for every listener in the process, installed
  like the trusted proxies: after proxy resolution, so it sees the client rather than the proxy, and
  after CORS, so a preflight never reaches it. It identifies and does not authorize: `null` serves the
  request without an identity, because one port carries routes with different requirements. A resolver
  that throws fails the request with 500 and an error log instead of downgrading the caller to
  anonymous. The result is kept in `HttpContext.Items` under `SharedHttpServerManager.PrincipalItem`
  (`SharedHttpServerManager.GetResolvedPrincipal`).
- The HTTP, gRPC, SOAP, AS2 (receive and async MDN), WebSocket and SignalR consumers put the principal on
  every exchange they build. On WebSocket and SignalR the component's own `Authenticate` still decides on
  its paths and takes precedence; without it, the host's principal fills `redbWs.UserId` and SignalR's
  `UserIdentifier` the same way.
- `SharedHttpServerManager(options, logger)` — a new constructor overload. The listener's own logging is
  switched off, so this logger is where resolver failures go; `AddRedbRouteHttpHosting()` passes the
  host's logger factory.

Additive: with no resolver configured, the host pipeline and every exchange are unchanged.

### Fixed — AS2 no longer reports an unsigned message as signature-verified

`redbAs2.signatureValid` on a received message is documented as "the inbound signature verified against
the partner cert", but the consumer started from `true` and overwrote it only when the message was
signed. An unsigned message in a partnership that does not require a signature therefore reached the
route reporting a verified signature, and a route that gates on the header accepted it as authenticated.
The header is now `true` only when a signature was present and verified — the rule the MDN side already
followed, starting from `false`. Rejection is unaffected: the consumer's own enforcement never read the
header, and the MDN it returns does not depend on it. *Behavioural* for a route that reads the header on
an unsigned flow: it now sees `false`.

### Changed — behavioural — SQL batch writes

The batch mode of `redb.Route.Sql` (`batchSize` above zero with a list body) no longer reports writes that did not
happen. Measured on PostgreSQL, SQL Server and SQLite: continuing past a failed item committed nothing on PostgreSQL
while the exchange succeeded, and on SQL Server wrote the following items outside the transaction after an error that
had ended it.

- **`breakBatchOnError` defaults to `true`**, as in Apache Camel: the first failing item rolls the whole batch back.
  *Migration:* set `breakBatchOnError=false` (or `.BreakBatchOnError(false)`) to keep going past failed items.
- **The provider's exception is thrown as is** instead of `AggregateException`, so `OnException<DbException>` and
  `catch (SqlException)` match it; the failed item's index is in `exception.Data["redbSql.batchFailedIndex"]` and in the
  `redbSql.batchFailedIndex` header. *Migration:* catch the provider's exception instead of `AggregateException`.
- **`breakBatchOnError=false` runs each item under a savepoint:** a failed item is undone and listed in
  `redbSql.batchErrors`, and the other items commit. It is refused before any write inside a transacted route or on a
  provider without savepoints, and it stops the batch with the item's own error when the server has ended the
  transaction.
- **An empty list writes nothing** (`redbSql.updateCount` is `0`); it used to run the statement once with NULL
  parameters.
- **A `${...}` value for a numeric or enum SQL option fails endpoint creation** (for example `Batch(Header("n"))`);
  it used to be dropped silently, which turned batching off.
- **A `@name` placeholder with no value is an error**, as in Apache Camel ("Cannot find key ... to use when setting
  named parameter"): `InvalidOperationException` naming the parameter and the statement, in a batch with the item's
  index. It used to bind `NULL` silently. Applies to `mode=Execute` (single statement and batch) and to the poll
  consumer's `onSuccess` / `onFailure` / `onBatchComplete`. A value that is present still binds `NULL` when it is null: a
  header or key set to `null` / `DBNull.Value`, an empty string, a `param.*` expression that evaluates to null.
  `@redbError` always has a value (`NULL` where nothing failed). *Migration:* give the placeholder a source —
  `param.name=...`, a header, or a key — or set it to `null` explicitly.
- **JSON values bind as scalars:** a `JsonElement` / `JsonNode` in a header, a dictionary body or a batch item becomes
  a string, `long` / `decimal` / `double`, `bool` or `NULL`, and a JSON object or array becomes its JSON text. It used to
  reach the provider as is and fail (`No mapping exists from object type JsonElement`).
- **The body of a single statement binds by key when it is a map**, as in Apache Camel: any dictionary (a CSV row
  `Dictionary<string, string>`, read-only and non-generic dictionaries) or a JSON object (`JsonElement`, `JsonDocument`,
  `JsonObject`), with the key's case as a fallback. Only `IDictionary<string, object>` used to be read. A POCO, XML or
  JSON-text body is still not read by name.
- **`batchSize` is the number of statements in one round trip**, not an on/off flag: on PostgreSQL and SQL Server a
  batch of 10 000 items with `batchSize=500` goes as 20 `DbBatch` round trips instead of 10 000 commands. The whole batch
  still commits or rolls back as one transaction; `redbSql.batchStrategy` reads `DbBatch` there instead of `Commands`.
  *Migration:* none; a very large `batchSize` makes one very large request.
- **Sequences, streams and JSON arrays are batch sources**, not only lists: with `batchSize` above zero an `IEnumerable`
  (`HashSet`, LINQ, `yield`), an `IAsyncEnumerable` of a reference type (a `StreamList` result) and a JSON array
  (`JsonElement`, `JsonDocument` with an array root, `JsonArray`) are written item by item, read as they are written —
  a stream of millions of rows never sits in memory. They used to run one statement with the whole body. A string, a
  dictionary or JSON object and an XML document stay one value. *Migration:* none, unless a route relied on such a body
  reaching a single statement: set `batchSize=0` there.

### Added — SQL batch results

- `SqlHeaders.BatchStrategy`, `BatchItemCount`, `BatchFailedIndex`, `BatchErrors`; the `SqlBatchItemError` record
  (index, message, SQLSTATE); `SqlBuilder.BreakBatchOnError(bool)`.
- **`DbBatch` for SQL batches:** with `breakBatchOnError=true` on a connection that can create a `DbBatch` (PostgreSQL,
  SQL Server), items go in chunks of `batchSize` statements, one round trip each; `redbSql.batchStrategy` is `DbBatch`
  and `SqlHeaders.BatchChunkCount` counts the round trips. The failed item's index comes from the provider's
  `DbException.BatchCommand`; a provider that does not fill it gets the chunk's first item and
  `exception.Data["redbSql.batchFailedChunk"]` with the chunk's range. Elsewhere one command is created per batch and its
  parameters are reused for every item. No option picks the strategy, as in Apache Camel.
- **`pollDelivery=List`** (`SqlEndpointOptions.PollDelivery`, `SqlBuilder.PollDelivery`; Apache Camel `useIterator=false`):
  a poll consumer delivers one exchange with every polled row — a list, or with `outputType=StreamList` the open stream —
  and runs `onSuccess` / `onFailure` and `onBatchComplete` once for it, after the reader is closed. The default `PerRow`
  keeps an exchange per row.

### Fixed — SQL batch

- Cancelling a batch no longer records the cancellation as an item error and goes on executing the remaining items.
- A `byte[]` body with `batchSize` set is one value, not a list of bytes inserted one by one.
- Batch items bind their own values whatever their shape: `Dictionary<string, string>` (CSV rows), read-only and
  non-generic dictionaries, JSON objects (`JsonElement`, `JsonObject`, `JsonDocument`), POCOs of the application (by
  property, `[Column]` or `snake_case`), and exchanges grouped by `AggregationStrategies.GroupedExchange()` (their own
  headers). These used to bind `NULL` or the carrying exchange's headers. Keys differing only by case are no longer
  resolved by whichever came last. XML nodes and other .NET types (`XElement`, `Uri`, `Stream`) carry no named values, as
  in Apache Camel: bind them with expressions such as `param.id=${xpath('@id')}`.
- A `param.*` expression of a batch item sees the exchange properties and route context (`${property.x}`); it used to be
  evaluated on a bare exchange.

### Fixed — a streamed SQL result releases its connection when the exchange ends

- `outputType=StreamList` handed the route an `IAsyncEnumerable` that closed its reader, command, transaction and
  connection only when read to the end. A stream the route did not read — the route failed, filtered the message, or
  replaced the body — kept its connection until the garbage collector found it; on PostgreSQL its open transaction also
  held a lock on the table (a `DROP TABLE` waited until timeout). The result is now registered with the exchange, as Apache
  Camel closes the result set on exchange completion, and released when the exchange ends. It can be read once; a second
  read fails with an explicit message.

### Changed — behavioural — `StreamList` inside a route transaction

- **`outputType=StreamList` is refused inside a transaction** (a transacted route, an ambient `TransactionScope`), before
  any connection is opened. Measured on PostgreSQL and SQL Server: the transaction does not commit while the stream's reader
  is open (`TransactionInDoubtException`), and a later SQL step in the same transaction would need a second connection and
  a distributed transaction. *Migration:* use `outputType=SelectList` inside the transaction, or read the stream outside it.

### Added — exchange resources

- `ExchangeResources.ReleaseWithExchange(exchange, resource)` (Apache Camel: `addOnCompletion`): a component registers an
  `IAsyncDisposable` that the exchange releases in `ReleaseScopes` / `DisposeAsync`, most recent first and before its DI
  scopes. Copies of the exchange do not inherit registered resources, and `RemoveProperties` by mask keeps them.

### Fixed — streaming SQL poll marks its rows

- A poll consumer with `outputType=StreamList` ran `onSuccess` / `onFailure` on the connection whose reader was still
  open, with or without a transaction. PostgreSQL and SQL Server refuse a second command there; the failure was swallowed
  without a log, the rows stayed unmarked and were processed again on every poll (SQLite allowed it, so the tests passed).
  Outside a transaction the statements now run on a connection of their own to the primary database, as Apache Camel runs
  `onConsume` through its `JdbcTemplate`; inside one they stay on the reader's connection and transaction, as in Camel.
  A failed row of a transacted streaming poll rolls the transaction back after the reader is closed; PostgreSQL and SQL
  Server refuse even the rollback while it is open, and the poll used to throw from there.
- A failing `onSuccess` is logged in every poll mode; it used to pass unnoticed when no `onFailure` was set.

### Changed — behavioural — streaming poll on SQLite outside a transaction

- **SQLite, `mode=Poll` with `outputType=StreamList` and `onSuccess` / `onFailure`, no transaction**: the statements now
  run on another connection, and in SQLite's default journal mode the open reader blocks that write ("database is
  locked"). It used to work on the reader's connection. *Migration:* set `transacted=true` (the statements stay on the
  reader's connection), switch the database to WAL, or drop `outputType=StreamList`.
- The SQL connector README no longer calls `outputClass` / `outputHeader` unimplemented or `Sql.Procedure(...)` broken.

### Fixed — SQL `outputType=Auto` and named queries

- **`outputType=Auto` follows the statement that runs**, not the URI path. A `SELECT` given in the `query` option (a
  constant or `${...}`) or through a named query `ref:name` ran as a non-query: the body stayed as it was,
  `redbSql.updateCount` was `-1`, and nothing failed. A `SELECT` path with an `INSERT` in `query` ran as a reader.
  *Behavioural:* such a route now gets the rows in the body (or `redbSql.updateCount` for the `INSERT`).
- **`sql:ref:name` works with named queries registered through `AddRedbRouteSql(sql => sql.AddNamedQuery(...))`.** The
  registry was never put where the connector looks for it: the producer threw "no ISqlNamedQueryRegistry is registered"
  and a poll consumer failed that way on every cycle. The registry is now registered on the route context.

### Changed — behavioural — SQL `outputClass` maps strictly

- **A column that cannot become its property fails the exchange** with an `InvalidOperationException` naming the column,
  the property and both types, as Apache Camel's `BeanPropertyRowMapper` refuses a type mismatch. It used to be skipped
  silently, leaving the property's default. Measured on SQLite, PostgreSQL and SQL Server: `timestamptz` / `datetime2`
  into `DateTimeOffset`, `date` into `DateOnly`, an integer into an `enum`, `5000000000` or `'abc'` into `int` all came
  out as defaults, with no error. The accepted conversions are listed in the connector README; `ScalarMapper<T>`
  follows them too.
- **`NULL` into a value-type property is an error**; into a nullable or reference property it sets `null` (it used to
  leave whatever the property's initializer set).
- **A timestamp without a time zone does not become a `DateTimeOffset`**, and a `DateTimeOffset` does not become a
  `DateTime`: the offset would be guessed or lost.
- **Text is parsed with the invariant culture**: `'12.5'` into `decimal` failed under a culture with a decimal comma.
- **An error while mapping `outputClass` reaches the route as itself**; the mapper was invoked by reflection per row and
  every exception arrived wrapped in `TargetInvocationException`.
- *Migration:* make a property nullable where its column is, map timestamps without a time zone to `DateTime`, and give a
  property a type that holds its column's values.

### Changed — behavioural — SQL read replica only with `readOnly=true`

- **`readOnly=true`** (`SqlEndpointOptions.ReadOnly`, `SqlBuilder.ReadOnly()`) sends a statement to the data source's read
  replica (`SqlConnectionOptions.ReadConnectionString`), for `mode=Execute`, `mode=Procedure` and a poll consumer. Without
  it every statement goes to the primary database. The replica used to be picked automatically: an `Execute` endpoint
  whose `outputType` reads rows — `INSERT … RETURNING` with `outputType=Scalar` included — and every poll consumer, its
  `onSuccess` / `onFailure` included, ran on it. Only the endpoint's author knows that a statement does not write (a
  `SELECT` can call a function that writes, advance a sequence or take a lock), so nothing is guessed from the SQL.
- `readOnly=true` is refused with `batchSize` above zero, and for a poll with `onSuccess` / `onFailure` /
  `onBatchComplete` or `transacted=true`: rows read on a lagging replica and marked on the primary come back.
- *Migration:* add `readOnly=true` (or `.ReadOnly()`) to the endpoints that should read from the replica.

### Fixed — SQL execution time

- `redbSql.executionTime` measured only resolving and binding the statement: the stopwatch stopped before the command ran.
  It now includes executing the statement and reading its result — for `outputType=StreamList`, until the reader is open —
  in the producer and in the poll consumer.

### Added — SQL batch keys and telemetry

- **`redbSql.generatedKeys` for a batch**, as Apache Camel's `CamelSqlGeneratedKeyRows`: with an `outputType` that reads
  rows (`SelectList`, `SelectOne`, `Scalar`, `StreamList`), the rows the batch's statements return — `INSERT … RETURNING`
  on PostgreSQL and SQLite, `OUTPUT inserted.*` on SQL Server — are collected in item order, across `DbBatch` round trips,
  as `List<Dictionary<string, object?>>` (`List<T>` with `outputClass`); `SqlHeaders.GeneratedKeysRowCount` holds their
  number, and the body stays the list that was written. A failed item of a batch that goes on past errors returns none.
  `outputType=Auto` makes an `INSERT` a statement without rows, so nothing is collected. MySQL's `LAST_INSERT_ID()` and
  Oracle's `RETURNING … INTO` are not collected in a batch.
- **Batch tags on the `sql.execute` span**: `db.operation.batch.size` (from two items, as the OpenTelemetry database
  conventions count a batch), `redb.sql.batch.strategy`, `redb.sql.batch.chunks` (`DbBatch` round trips) and
  `redb.sql.batch.failed_index`. Parameter values are never tagged.

### Changed — breaking — SQL parameters are written `:#name`, as in Apache Camel

- **A parameter in a `sql:` statement is `:#name`**: in the URI path and `query`, in `onSuccess` / `onFailure` /
  `onBatchComplete` (`:#redbError` there), in named queries, in batches and in the poll query. `@` is no longer a
  placeholder and reaches the database as written, so T-SQL variables (`DECLARE @n int = :#value`), `EXEC` argument
  names (`EXEC proc @arg = :#value`) and MySQL user variables work. They clashed with `@name` placeholders: with strict
  binding `EXEC proc @arg = @value` failed with "has no value", and a declared variable never worked.
- **Placeholders are found only in the SQL itself**, not inside string literals, quoted identifiers, comments or
  PostgreSQL dollar quotes: `'user@example.com'` and `-- :#todo` stay text.
- **`placeholderStyle`** (`SqlEndpointOptions.PlaceholderStyle`, `SqlBuilder.PlaceholderStyle(...)`) sets what the
  provider receives: `At` — `@name`, the default (Npgsql, SqlClient, Microsoft.Data.Sqlite, MySqlConnector); `Colon` —
  `:name` (Oracle); `Question` — `?` with one parameter per occurrence (ODBC, OleDb).
- **`backslashEscapes`** (`SqlEndpointOptions.BackslashEscapes`, `SqlBuilder.BackslashEscapes()`): MySQL and MariaDB escape
  a quote with a backslash by default, and `'it\'s'` used to hide the placeholders after it — the provider got `:#id` and
  refused the statement. With `backslashEscapes=true` a backslash inside `'…'` and `"…"` escapes the next character. Off by
  default: in standard SQL `'C:\'` is a complete literal, and nothing is guessed from the provider. Doubling the quote
  (`''`) works without the option. Checked on MySQL 8.4, MariaDB 11 and Firebird 5 (tier 2).
- **A placeholder of the poll query without a `param.*` value is an error** naming it, as everywhere else: the poll query
  has no exchange to take values from.
- **`SqlBuilder.Param("@x", ...)` is refused**: name the parameter `x` or `:#x`.
- Apache Camel's inline expressions `:#${...}` and `:#in:name` lists are refused with a message; use `param.name=${...}`.
- *Migration:* replace every `@name` placeholder with `:#name` in `sql:` URIs, lifecycle SQL, named queries and Route-XML;
  keep `@` only where the database itself means it.

### Fixed — SQL connector review after 4.0.0

A code review of everything the SQL connector gained after 4.0.0, with Apache Camel as the reference. Every finding is
fixed; the ones that change behaviour are marked.

- **A streamed result survives copies of its exchange.** The body of `outputType=StreamList` was disposable, and a copy of
  the exchange (WireTap, RecipientList, Loop, Threads, a `seda:` hand-off) shares the body: disposing the copy closed the
  reader and the connection under the original — deterministically after `.Enrich("sql:…?outputType=StreamList")`. The
  body is no longer disposable; the release is a resource of the exchange that ran the endpoint, and `Enrich` hands the
  resource exchange's resources over to the enriched one (`ExchangeResources.HandOver`, as Camel's
  `handoverCompletions`), so the enriched route reads the stream. A copy that outlives the original still finds the
  stream released: read it in the segment that produced it, or use `SelectList` across a `seda:`/`Threads` boundary.
- **`SqlIdempotentRepository` and `SqlClaimCheckRepository` bootstrap on MySQL and MariaDB.** Their `TEXT` key columns
  were refused in a primary key (error 1170), so `CreateTable=true` failed on the first call. The keys are sized now —
  `VARCHAR(128)` for the processor name, `VARCHAR(255)` for the message and claim keys, `NVARCHAR` of the same sizes on
  SQL Server, where the idempotent key also drops under the 900-byte clustered index limit (it was 1020). Tables created
  by an earlier release keep their schema. `SqlIdempotentOptions.TableName` is validated like the claim-check one.
- *Behavioural:* **`outputType=Auto` is decided by what the statement returns**, as Camel's `execute()` asks the driver —
  not by the first word of the text. A comment before `SELECT` or a leading `(` used to run the statement as a non-query
  (body untouched, `updateCount=-1`, no error); `WITH … UPDATE` ran as a reader. `INSERT … RETURNING` under `Auto` now
  returns its rows; a batch under `Auto` collects no rows, as Camel's `executeBatch`. `redbSql.outputType` reports the
  resolved type.
- *Behavioural:* **lifecycle SQL of a poll (`onSuccess` / `onFailure` / `onBatchComplete`) binds values under the
  producer's rule:** `""` binds `NULL` and a `JsonElement` binds its scalar. They used to reach the provider as they
  were — a JSON header failed `onSuccess`, the row stayed unmarked and was polled again forever.
- *Behavioural:* **a stop is not a failure of the row.** A cancellation of the poll while a row was in the route ran
  `onFailure` with the cancelled token, marked the row with "The operation was canceled" where the driver let it, and
  logged an error. The cancellation now goes up, `onFailure` does not run, nothing is logged as an error, and the row is
  polled again after the restart. Rollbacks no longer take the cancellation token.
- *Behavioural:* **`param.x=${…}` and `query=${…}` on a poll are errors** naming the option: a poll has no exchange to
  evaluate them in. The parameter used to bind the template text as its value (an empty result, silently), and the query
  fell back to the URI path.
- *Behavioural:* **a batch reports only its own run.** `redbSql.batchFailedIndex`, `batchErrors` and `redbSql.error` left
  by an earlier attempt on the same exchange (a redelivery through `OnException`, an earlier `sql:` step) are removed
  before the batch writes its result. `redbSql.batchStrategy` is set on failure too.
- *Behavioural:* **a returned row that `outputClass` cannot hold ends the batch** with `SqlRowMappingException` (an
  `InvalidOperationException`) in either error mode; in continue mode it used to be recorded as an error of every item,
  in break mode as a chunk failure with the chunk's first index.
- **`:#Id` and `:#id` in one statement are one parameter in the text the provider gets** (SQLite refused the second name).
- **`bool` and `enum` properties accept a `decimal` or `double` that holds an integer** — Oracle `NUMBER(1)`, PostgreSQL
  `numeric` — as the integer properties already did.
- **Invalid ISO text (`2024-13-45`) into a date property** fails as a mapping error naming the column, not with a
  `FormatException` carrying the value.
- **`SqlBuilder.Param(name, object)` and the numeric builder options are written invariantly**: `12.5`, not `12,5` under a
  culture with a decimal comma; dates as ISO 8601 round-trip text.
- **`SqlBuilder.DataSource(IExpression)` and `ConnectionString(IExpression)` take a constant only**; a `${…}` expression is
  refused, since the endpoint is fixed when it is created (a dynamic target is `ToD`). They used to write the template
  text into the URI. `SqlBuilder.ConnectionString(string)` added. `outputHeader=${…}` names the header per exchange; it
  used to be a header literally named `${header.x}`.
- **`asFunction=true` with an `OUT`/`INOUT` parameter is refused** at endpoint creation: `SELECT fn(...)` returns the
  scalar, and under a positional placeholder style the parameters shifted. **`pollDelivery=List` with `Scalar`/`SelectOne`**
  is refused instead of ignored.
- **`AddRedbRouteSql` called twice keeps the named queries of both calls** and registers one `sql` component; the second
  call used to replace the first call's registry.
- The message for a placeholder without a value names it `:#name`; a sequence of bytes or characters other than
  `byte[]`/`string` (`ArraySegment<byte>`, `List<byte>`, `char[]`) is one value, not a batch; a source that answers a
  cancellation with its own exception is not reported as a source failure; a transaction whose disposal throws no longer
  strands the producer's connection; a failing release of an exchange resource is logged through the context when the
  exchange has no DI scope.

### Changed — the SQL connector is tested on Microsoft.Data.SqlClient 7.0.3

- The SQL Server e2e suite of `redb.Route.Sql` runs on `Microsoft.Data.SqlClient` 7.0.3 (was 5.2.2), the version
  `redb.MSSql` now ships; the connector itself references no driver. Characterization and the full suite give the same
  results as on 5.2.2 on net8.0, net9.0 and net10.0.

## [4.0.0] — 2026-09-12

> **4.0.0 breaking bundle (docs/V4/09-BREAKING.md).** The entries under *Removed* and the first two
> under *Changed* are the major's breaking changes; they ship together with a migration note.

### Fixed — a SQL connection is returned to the pool whatever happens next

Two holes in `SqlProducer`, both on the hot path for audit and usage writes, where the failure
modes that trigger them (dropped sockets, cancellations) come in bursts — so a leak drains the
pool rather than dripping. `BeginTransactionAsync` ran before the `try`, so a throw there stranded
the connection with no `finally` to catch it; it now runs under the same `finally` as everything
else. And the StreamList branch decided who cleans up by the *option value* rather than by whether
ownership actually reached the stream: an error before the handoff — the reader failing to open —
leaked connection and transaction both. A flag set only after the handoff decides now, which also
covers the day `Auto` resolution and the option disagree. The command is disposed on the failure
path too, not only on success.

What StreamList hands over is safe on abandonment as well: the streaming iterators hold reader,
command and connection in `await using`, so disposing the enumerator — a completed loop or an
early `break` — releases them. The one case nobody can close is a stream that is never iterated
and never disposed; that is inherent to handing a caller a lazy sequence.

### Fixed — releasing an exchange's scopes no longer spends a one-shot latch

`Exchange.ReleaseScopes()` is public API, and downstream error handlers call it manually; the
first call used to spend the latch for the life of the exchange, so any redb scope cached on the
exchange *afterwards* — an error handler's tail touching redb again does exactly that — was
invisible to `DisposeAsync` forever. The latch is now an in-progress guard that re-arms after
every sweep (in a `finally`, so a throw mid-sweep cannot jam it): concurrent calls still cannot
double-sweep, nothing is disposed twice — entries leave the property bag as they are released and
the owned scope nulls out — and a later call releases whatever appeared since. The internal
`PrepareForReplay()` workaround, which existed only to re-arm the latch for checkpoint replays, is
gone with the defect it worked around.

### Fixed — a request the dispatcher cannot bind is the caller's error, not the server's

All four controller dispatchers (HTTP, gRPC, SignalR, SOAP) funnelled parameter-binding failures —
a JSON body missing required members, a route parameter that is not a guid, junk XML — into the
generic exception handler: the caller got 500 `InternalError` with a text that hides the actual
problem, and the operator got an Error log with a full stack for every piece of junk POSTed from
outside. The boundary is now drawn around the parameter-*resolution* step, not around exception
types: the same `FormatException` thrown inside the action stays a 500 with an Error log. HTTP,
gRPC and SignalR answer 400 `BadRequest` carrying the binding error's own text and log a Warning
without a stack. SOAP has no status codes, so the dispatcher throws the new
`MalformedRequestException` (core `Abstractions` — a transport-neutral "your request is wrong"
signal) and the SOAP consumer maps it to a `Sender`/`Client` fault carrying the message, instead
of a `Receiver` fault whose retry semantics are a lie for bytes that will fail identically
forever. Returning the binding error's text is the BR-4 rule, not a violation of it: it describes
the caller's own bytes, nothing in it is ours to hide. The reclassification covers only the
caller's data (body, headers, route and query values): a SOAP operation whose signature no
request could ever bind — a required simple parameter with no source — is the controller
author's error and stays a `Receiver` fault.

### Added — measurements readable from inside a route (METRICS_IN_ROUTE_PLAN, П1–П3)

- **`IExchange.Context`** — Apache Camel's `exchange.getContext()`. Stamped by an outermost route
  wrapper on entry and restored on exit, so "current" always means the route actually executing:
  inside a `direct:`/`vm:` sub-route the inner route's context, the caller's own again after it
  returns, and `null` on an exchange never handed to a route. A default interface member, so
  external `IExchange` implementers do not break. All five exchange copy paths (`Clone`,
  `Snapshot`, `CreateChild`, `CreateLinkedChild`, `CloneLinked`) carry it. This is what lets a
  processor read `(IEndpointStatistics)e.Context.GetEndpoint(...)` without capturing the context
  into every lambda.
- **`stats(target, metric)` in the expression language** — endpoint statistics as values, so a
  route can branch on its own measurements: `Filter("stats('direct:orders', 'health') != 'Critical'")`,
  `When("stats('current', 'cancelled') > 100")`. Target is an endpoint URI or `'current'`; metrics
  are the `IEndpointStatistics` surface by name, case-insensitive, with `averageProcessingTimeMs`
  as a number because the language compares numbers. A typo in a literal metric name fails the
  route build with the list of known names. Implemented in both engine branches — the `format()`
  lesson — and every failure is loud: a route that asks for measurements it cannot get must not
  route messages on a silent null.
- **`context.UseMetricsSnapshot()`** — the OpenTelemetry layer readable in-process, opt-in. The
  `redb.Route` meter is push-only, so the EIP counters (`throttle.delayed`,
  `circuitbreaker.tripped`, …) and `.Metered()` step durations used to go to a backend or nowhere;
  the snapshot is an in-process `MeterListener` keeping **current values** per
  (instrument, `redb.route.id`, `redb.route.step`) — counters as running totals, histograms as
  count/sum/min/max/last. Deliberately opt-in (a listener sees every measurement — nobody pays
  unasked), no windows, no percentiles, no history: that is a backend's job. Disposed when the
  context stops; collected points stay readable. `services.AddMetricsSnapshot()` for DI,
  `context.GetMetricsSnapshot()` to resolve.
- The guide for all of it, layer by layer with recipes: `METRICS.md`.

### Changed — the DeepSeek default base URL follows the current docs

`https://api.deepseek.com/` instead of `.../v1/`, checked 2026-09-10 against the provider's docs
after the V4.1-Flash release: DeepSeek documents the root as its base — unlike every `/v1`
neighbour in the table — and `/v1` survives only as an undocumented legacy alias (it still
answers 401, not 404, so nothing breaks either way; the canonical form is simply the durable
one). Model names were always pass-through, so V4.1-Flash works with no further change; a
self-hosted or proxied setup still overrides via `LlmConnectionFactory.BaseUrl`. Pinned by
`OpenAiBaseUrlTests` so a future edit does not "harmonise" it back to `/v1`.

### Changed — LLM storage lookups read the unique key (Ф4, no-migration form)

All 17 key lookups across the redb-backed LLM stores now search `ValueUnique ==
RedbUniqueKey.Normalize(key)` instead of `value_string` — the column the level-A write barrier
converges on. The performance claim, checked live on the MSSQL stend: `_value_string` has
meanwhile been retyped and indexed by the FK-index work, so both paths seek today — the unique
path is still the tighter one (a unique composite `(_id_scheme, _value_unique)` covering `_id`,
guaranteed at most one row) but the honest headline is correctness, not scan-versus-seek. The one
`value_string` lookup left is the conversation-message find, deliberately: messages carry no
unique key (a message id is only unique within its conversation).

By owner decision there is **no migration in any form** — no backfill, no lazy adoption. The
consequence is pinned by tests rather than implied: rows written before level A, carrying the key
only in `value_string`, are invisible to the stores now.

Moving the branch point surfaced two write-path asymmetries the string/unique split used to hide.
The batch register's terminal-status guard lived only on the collision path, so a redelivered
"submitted" with a matching raw id could downgrade a completed batch to pending forever — a latent
bug from before this change; the guard now applies however the row was found. And the template
registry's two branches genuinely disagreed (upsert on find, first-wins drop on collision): the
old split by raw string is preserved explicitly — re-setting the version you own updates it (the
Р6 contract), while a row owning the unique key under a diverged string is a race winner and the
incoming body is dropped, because a version is immutable provenance.

### Fixed — control-bus stats carry the whole counter surface, escaped

`controlbus:route?action=stats` is the one DSL-reachable view of endpoint statistics, and its XML
had trailed `IEndpointStatistics`: `Warnings`, `Rejected`, `Cancelled`, `bytesIn`/`bytesOut` and
the last error were readable through a captured context but invisible from a route — exactly the
columns the recent accounting work made honest. All of them are attributes now, `lastError`
included (present only when there is one). Free-text values — the route id and the error message —
are XML-escaped on the way in; a quote inside an exception text used to corrupt the document for
whoever parsed it downstream.

### Fixed — a cooperative cancellation is not a route error (BR-10)

A dashboard closing mid-poll cancels the caller's token; the exchange is abandoned, not failed. The
statistics wrapper still counted it into `Errors`, so a healthy management route turned red on an
error panel and its endpoint health degraded for the five-minute error window — on nothing but
polling churn. The counter saw what the log did not: `HttpConsumer` already filters
`OperationCanceledException` past its logging, and the error-handling layer has always read OCE as
"not a failure" (retry never retries it, the dead-letter channel never parks it, `OnException`
never handles it). The statistics and lifecycle layers were the last two counting it.

The rule, applied at every counting and event site: an `OperationCanceledException` while the
caller's token is cancelled is a **cancellation**; an OCE while the caller's token is live (an
internal timeout) is the route failing to answer in time and stays an **error**. Four sites:
`StatisticsProcessor` (both its branches — the rethrown exception and the one parked on the
exchange by a parallel branch), `CountedSend` (a cancelled send no longer errors the target
endpoint), and `ExchangeEventsProcessor` (listeners hear the exchange arrive and then hear
nothing — neither completed nor failed — so NotifyBuilder does not count a closed tab as a
failure).

Cancellations are not silently dropped: `IEndpointStatistics` grows a `Cancelled` counter
(`RecordCancelled()`), following `Rejected` from the concurrency work — with one accounting
difference stated in its doc: `Rejected` is counted before an exchange exists, `Cancelled` after
`MessagesIn`, so a consumer endpoint's completed count is now `MessagesIn - Errors - Cancelled`.
A storm of cancellations stays visible, in the right column. `RecordCancelled` bumps activity but
not the last-error stamp, so health stays clean. The interface addition is breaking for external
`IEndpointStatistics` implementers, in the major where `Rejected` already was.

### Fixed — Mermaid export: the continuation after a branching merges from branch tails

`MermaidRenderer` drew the step after a `choice`/`tryCatch` with a solid edge out of the
diamond itself, so on mermaid.live the continuation read as one more branch (the owner's
side-by-side against the graph editor caught it). The renderer now walks the same way the
visualizer does: one edge per branch TAIL into the continuation, nested branchings
recursively; an empty branch contributes its condition node. The two example goldens with a
post-branching continuation regenerated deliberately (`REDB_XML_GOLDEN_REGEN=1`), the diff is
exactly the diamond edges replaced by tail edges.

### Added — VSCode extension, the route GRAPH editor (Route-XML F8, stages 1–4)

The `redb Route Graph` custom editor (Open With / the title-bar button): the text stays the
truth, the graph is a projection over a position-exact XML tree — the editor NEVER
reserializes the file. Rendering per the visual language: the main line left-to-right with
arrowed edges, scopes as brackets with the condition on the schema, choice/tryCatch/multicast
as branch stacks the line forks around and visibly rejoins, split with the repeat mark, rich
`log` as one node, `onException` as a folded strip, unknown elements as opaque nodes kept
byte-for-byte, secrets redacted in labels, tooltips and the panel alike. A click opens the
properties panel: id/description first, spec attributes with enum/bool dropdowns, endpoint
URIs decomposed into the F4 catalog's typed options (37 schemes; the set ones first,
Sensitive masked and locked), rich-log content editable row by row. All four editing
operations of the plan: «+» slots with the mechanically-assembled palette, Delete, drag to
reorder, drag into/out of a bracket — every edit a surgical one-line-diff text change, a
step's comments travel with its block. Composite nodes fold to a counter square (deep levels
fold by default; the state lives in workspaceState, never in the file). The element list and
the component catalog are GENERATED resources (`redb-route-xml elements|catalog`); a category
missing for a registry element fails the extension build. 76 unit tests over the pure core
(parser spans, graph projection, text surgery, uri surgery, palette).

### Added — VSCode extension, text mode (Route-XML F7)

`tools/vscode-redb-route/`: completion, validation and enum hints for the XML routes via
Red Hat XML reading the shipped XSD — bound by an OASIS catalog to the NAMESPACE
`urn:redb:route:1.0`, so a document gets the schema because of what its root says, never its
file name; someone else's `route.xml` is untouched (the F7 activation contract, with negative
tests). A route tree in the sidebar (ids, `from` endpoints, click opens the line; comments
and CDATA cannot fake a route), snippets for the everyday constructions, and a Mermaid
command over the `redb-route-xml` tool when it is installed — everything else works without
.NET. TypeScript, zero runtime dependencies, the sniff and the scan pinned by node:test
units. Live Extension-Development-Host verification is the owner's step.

### Removed
- **Declared-but-never-read factory options, full sweep (the F11 odds-and-ends pass, the Zh-3 audit of all 22
  factories).** `AmqpConnectionFactory.Reconnect`/`ReconnectInterval`/`MaxReconnectAttempts`
  (the component's connection pool already self-heals — a closed connection is evicted and
  recreated) and `MaxLinksPerSession`; `LlmConnectionFactory.ModelVersion` and `Retries`
  (the Llm engine's resilience policy is deliberately fallback-to-another-factory, not
  retry-the-same-provider); `SoapConnectionFactory.ProxyType`. None of them was ever read.
  Along the way: AWS credentials in S3/Sqs moved off the deprecated `FallbackCredentialsFactory`
  to `DefaultAWSCredentialsIdentityResolver`; `S3Dsl` was split into three partial files per the
  "~400 lines" rule; named-factory examples (`AddToRegistry` + `connectionFactory=`) were added
  to the READMEs of 19 connectors, and the resolution contract is documented in the core README.
- **`S3EndpointOptions.CustomerAlgorithm` and `DeleteAfterWrite` (F11 wave S-B).** The SSE-C
  algorithm is hardwired to AES256 by the SDK (the option was never read), and
  `DeleteAfterWrite` ("delete the source file after upload") never had defined semantics for
  a message-body producer and was never implemented. The DSL's `Idempotent(IExpression)` key
  parameter is gone with `IdempotentKey` (see below).
- **`S3EndpointOptions.IdempotentKey` (F11 wave S-A, finding S-3).** The implementation was
  `return _options.IdempotentKey; // TODO: expression evaluation` — a CONSTANT key for every
  object, so with the option set exactly one object was processed in the consumer's lifetime
  and everything else was skipped as a "duplicate". An expression is impossible at this seam
  by construction (the key is needed BEFORE downloading, the exchange exists only after), and
  a custom idempotency key already has a first-class home: the route-level
  `IdempotentConsumer(...)` EIP with a real expression. The built-in pre-download filter
  keeps its honest `key|ETag|size` identity.
- **`FirestoreEndpointOptions.IncludeMetadataChanges` (F11 wave G).** Declared, documented —
  and unimplementable: `Query.Listen` in the .NET SDK (Google.Cloud.Firestore 3.13) has no
  metadata-changes overload. The option never did anything; removed instead of pretending.
- **String synonyms with the misleading `Expression` suffix:** `LoopExpression(string)` (all three
  overloads), `DelayExpression(string)`, `ThrottleExpression(string, period)`. In the rest of the DSL the
  suffix means "takes an `IExpression`", and those overloads (`LoopExpression(IExpression, …)`,
  `DelayExpression(IExpression)`, `ThrottleExpression(IExpression, period)`) stay. The string forms live
  on the verbs themselves now, like `Filter(string)` always did — mechanical replace:

  ```
  LoopExpression("${header.count}")               → Loop("${header.count}")
  LoopExpression("${header.count}", sub => …)     → LoopExpression(new StringExpression("${header.count}"), sub => …)
  DelayExpression("${header.backoff}")            → Delay("${header.backoff}")
  ThrottleExpression("${header.rate}", period)    → Throttle("${header.rate}", period)
  ```
- **The string forms of the `Set*` / `Transform` verbs** — `SetBodyExpression(string)`,
  `SetHeaderExpression(name, string)`, `SetPropertyExpression(key, string)`, `TransformExpression(string)`
  (owner decision, `docs/V4/09-BREAKING.md` §4, option (b) there). One rule across the DSL: the `Expression`
  suffix means "takes an `IExpression`", and a `${...}` template *is* an expression, so there is one
  form for it. A plain `string` keeps meaning a literal value — the position decides, the content never does.

  ```
  SetBodyExpression("Hello ${header.name}")        → SetBody(Expr("Hello ${header.name}"))
  SetHeaderExpression("target", "q-${header.t}")   → SetHeader("target", Expr("q-${header.t}"))
  SetPropertyExpression("k", "${header.id}")       → SetProperty("k", Expr("${header.id}"))
  TransformExpression("upper(body)")               → Transform(Expr("upper(body)"))
  ```

  `Expr(...)` is `RouteBuilder.Expr`, now **public and static**: inside a `RouteBuilder` subclass write
  `Expr(t)`, from the lambda style add `using static redb.Route.Core.RouteBuilder;` or write
  `RouteBuilder.Expr(t)`; `new StringExpression(t)` is the same thing. Before the removal an equivalence
  grid (fifteen forms across the four positions) proved the string form and `Expr(...)` already meant
  exactly the same, type included; it stays as the contract of the surviving form
  (`SetVerbExpressionFormTests`). The definitions and processors behind the removed verbs
  (`Set*StringExpressionDefinition`, `TransformStringExpressionDefinition`, `StringExpression*Processor`)
  are gone with them: nothing constructed them any more, and they did what `Set*(IExpression)` does.
- **`Newtonsoft.Json` is no longer a dependency of `redb.Route`.** The only consumer was `jpath` /
  `JsonPathExpression`, which now runs on JsonPath.Net 3.0.2 (RFC 9535) over System.Text.Json — one
  JSON object model in the core for the serializer, schema validation, `jpath` and the data formats.
  Packages that used Newtonsoft transitively must add their own reference (`redb.Route.Llm.Tools` did:
  its `JsonPathDsl` keeps the full Newtonsoft dialect on purpose). A 60-form characterization snapshot
  (`JsonPathDialectSnapshotTests`) was recorded on the old engine first; 44 forms are byte-identical,
  the 16 that changed are listed under *Changed* below.

### Changed
- **`${...}` templates render numbers and dates culture-invariant.** `${header.price}` is `2.5` on every
  machine (before: `2,5` on a ru-RU server); dates render ISO 8601 (`2026-09-01T10:30:00.0000000Z`).
  A route must not depend on the server locale; locale formatting is an explicit
  `format(x, 'N2', 'ru-RU')`. Strings and booleans are unchanged. This closes the last open item of the
  expression-language unification (a template with a whole-string `${x}` still keeps the CLR type).
- **A `${...}` placeholder in a consumer URI fails `Start()`.** `From("kafka://orders-${header.region}")`
  has no message to resolve against; before, it was silently kept as text. Use `{{key}}` configuration
  placeholders or a constant.
- **`jpath` dialect after the engine change** (`JsonPathExpression`, `${jpath(...)}`, `jpath('...')`):
  a date string in JSON stays a string (`Evaluate<DateTime>` still parses it; before, Newtonsoft
  auto-typed it and even rendered it by the machine culture); numbers render invariant (`"19.95"`, not
  `"19,95"`); single-quoted JSON is rejected (comments and trailing commas still parse); an array or
  object asked as `string` gives compact JSON text instead of throwing; `object` results are
  `System.Text.Json.Nodes.JsonObject` / typed arrays / `object[]` instead of Newtonsoft `JObject` /
  `JArray`; an empty filter result asked as `bool` is `false` instead of an exception; negative slices
  (`[-1:]`) and `JsonNode` bodies work; an invalid path fails in the constructor (route build), not on
  the first message. Everything else — selectors, filters, typed conversions, `Int64` scalars,
  `Int32[]` arrays, null / missing semantics, POCO bodies — is unchanged and pinned by the snapshot.
- **XPath in a condition asks whether the path matched.** `Filter(XPath("/order/discount"))` and
  `When(XPath(...))` read the node-set the way XPath 1.0 defines it — non-empty is true — instead of
  flattening it to the first node's text. Before, `<discount>0</discount>`, an empty `<vip/>` and
  `<flag>false</flag>` all read as "no match", while *two* `<discount>0</discount>` elements read as a
  match: a multi-node result was never flattened, so the old reading disagreed with itself. The other
  two questions keep their own answers — `XPath<bool>("/order/flag")` converts the node's text and is
  therefore `false`, and an expression that already yields a scalar (`string(...)`, `count(...) > 0`,
  or an explicit `XPathResult`) is read by the one DSL truthiness rule. This is the object form only;
  the `xpath('...')` function of the expression language remains a value read, so
  `Filter("xpath('/order/discount')")` still answers by content — write `xpath('boolean(/order/discount)')`
  there. Breaking, and outside the `docs/V4/09-BREAKING.md` bundle listed above.

### Added
- **Per-endpoint admission limits for the Kestrel family (HTTP, SOAP, AS2, gRPC) — load
  shedding, not backpressure.** Kestrel executes as many handlers as requests arrive; a route
  used to have no ceiling at all. Four new options on those consumers —
  `maxConcurrentRequests` (0 = unlimited, the previous behaviour), `requestQueueLimit` (FIFO
  wait slots), `rejectStatusCode` (default 429) and `retryAfterSeconds` (default 1, 0 = no
  header) — cap concurrent pipeline executions per registration and answer the overflow with
  `429 Too Many Requests` + `Retry-After` BEFORE any pipeline work. Strictly per-route: a
  saturated endpoint cannot eat a neighbor's budget on a shared listener. Implemented once, in
  the shared host, on the runtime's own `ConcurrencyLimiter`. gRPC counts unary calls only —
  a long-lived stream would hold a permit forever, and shed health probes would flap
  orchestrators (both documented). SignalR gets its own pair: `maxConnections` (an over-limit
  connection is aborted at OnConnected, before the Connected lifecycle event) and
  `maxParallelInvocationsPerClient` (SignalR's native per-client parallelism).
- **`IEndpointStatistics.Rejected` / `RecordRejected()`** — requests shed by an admission limit
  before a pipeline ran. Counted in neither `MessagesIn` nor `Errors`, so dashboards can tell
  "shedding load" from "failing".
- **Host-wide Kestrel backstop**: `HttpHostingOptions.Limits.MaxConcurrentConnections` /
  `MaxConcurrentUpgradedConnections` — Kestrel's own ceilings, previously never set (unlimited).
  The fuse for the whole port; the per-route limit is the thermostat.
- **`concurrentConsumers=auto` on the broker family** (RabbitMQ, AMQP 1.0, IBM MQ, SQS, MQTT,
  seda/vm — and `maxConcurrentCalls=auto` on Azure Service Bus): `auto` = max(CPU count, 2),
  the NServiceBus formula. The default stays **1** — the industry norm (Camel JMS/SQS/Kafka,
  Spring, the Azure SDK all ship 1) because 1 preserves ordering and keeps handlers free of
  thread-safety obligations; parallelism is an explicit opt-in. BREAKING for code (not URIs):
  these options are now strings, because the URI binder silently turned any unconvertible value
  into the int default — `concurrentConsumers=auto` would have quietly meant 1, and a typo like
  `concurrentConsumers=trash` DID quietly mean 1. Both now fail at endpoint creation, naming
  the option and the valid values. IBM MQ topics still clamp to a single subscriber.
- **`IEndpointStatistics.BytesOut` / `RecordBytesOut` — the missing half of the byte counter.**
  The surface had `BytesIn` alone, documented as "total bytes processed" on the property and as
  "incoming bytes" on the method, and connectors resolved the contradiction each in their own way.
  `SoapProducer` recorded the outgoing envelope as `BytesIn`; `LlmProducer` recorded the outgoing
  prompt as `BytesIn` **and computed the incoming response only to throw it away** — the code said
  so out loud: `// The IEndpointStatistics surface only exposes RecordBytesIn … _ = bytesOut;`.
  Both directions have their own counter now, `BytesIn` means "bytes that entered this endpoint"
  and `BytesOut` "bytes that left it", and the docs on both say it.

  Corrected accordingly: the SOAP producer's request envelope moves to `BytesOut`; the LLM
  producer's prompt moves to `BytesOut` and its response is recorded as `BytesIn` instead of being
  discarded — and is counted in UTF-8 bytes rather than UTF-16 chars, which is what a counter
  called *bytes* should hold (`"привет"` is 12, not 6). The WebSocket and TCP producers, which had
  the sizes at hand, record both directions too.

  **Dashboards that read these numbers need a look.** `BytesIn` on an LLM endpoint used to be the
  prompt and is now the answer; the prompt is `BytesOut`. `redb.Tsak` reads this surface. Adding a
  member to `IEndpointStatistics` is also source-breaking for anything outside this repo that
  implements the interface — `EndpointBase` is its only implementer here.
- **`ws://` and `signalr://` serve on the shared Kestrel, like HTTP/gRPC/SOAP/AS2 (F14 wave 1).**
  Each of the two connectors used to raise its own `WebApplication` on its own port, so the hub
  and the REST API it pushes for could not sit on one port behind one proxy and one TLS
  termination — the typical production layout simply did not assemble. Now
  `http://:8080/api/**`, `signalr://:8080/hub` and `ws://:8080/stream` are one listener, and
  several hubs on it are routed to their own consumer by request path. Two new seams on
  `SharedHttpServerManager` make it possible: `EnableWebSockets(host, port, keepAlive, ssl…)` and
  `RegisterServerConfigurator(host, port, services, endpoints, ssl…)` — services applied before
  `Build()`, endpoints mapped before the catch-all. Both are opt-in per listener and fail loud if
  the listener is already running without them, so the four transports that already shared the
  host behave byte-for-byte as before (pinned by a new characterization suite,
  `redb.Route.Tests.Hosting`).
- **Host-supplied authentication for the WebSocket and SignalR handshake (F14 wave 2).**
  `AddRedbRouteSignalR(o => o.Authenticate = …)` and `AddRedbRouteWebSocket(o => o.Authenticate = …)`
  take a delegate returning a `ClaimsPrincipal` or null; null rejects the handshake with 401
  before the connection is upgraded, and the principal's `NameIdentifier` becomes SignalR's
  `UserIdentifier` (which is what `Clients.User(...)` and the `redbSignalR.UserId` /
  `redbWs.UserId` headers run on). A delegate rather than `AddJwtBearer` because the process
  hosting a route context is a generic host, not an ASP.NET application. Note for implementers:
  the token arrives in the `Authorization` header on negotiate and in the `access_token` query
  parameter on the WebSocket upgrade (a browser cannot set headers there), so a host delegate has
  to read both.
- **Backplane seam for scale-out (F14 wave 3).**
  `AddRedbRouteSignalR(o => o.ConfigureHubServices(s => s.AddSignalR().AddStackExchangeRedis(…)))`
  adds services to the hub's listener container without the connector taking a dependency on
  Redis or Azure. A hub keeps its connections and groups in the memory of one process, so **with
  more than one replica a backplane is required**: without it a broadcast reaches only the clients
  attached to the replica that sent it. Both halves of that sentence are now e2e tests against the
  live Redis container.
- **A WebSocket route can push to its own clients (`mode=Server`, `Ws.Broadcast`).** The consumer
  kept a dictionary of connections and published `ActiveConnections`, but had no send path into
  it: a frame could only be a reply to an incoming one. Broadcast, or one client by the
  `redbWs.TargetConnection` header — the id the incoming exchange already carried. This is half
  the point of a WebSocket server (quotes, notifications, progress), and SignalR had it while
  WebSocket did not.
- **`reconnectTimeout` for the WebSocket producer.** With `reconnect=true` and the default
  `maxReconnectAttempts=0`, a send against a server that stays down never returned: the exchange
  did not fail, dead-letter never fired, the route just hung. The new time budget caps it; 0 keeps
  the previous behaviour, now documented rather than implied.
- **redb storage as a first-class citizen of the XML markup (Route-XML step 1) — declarative
  verbs in `redb.Route.Core` and their R21 contributions.** New string/Type DSL beside the
  lambda verbs (a lambda cannot live in markup): `RedbGet(Type, idExpr, depth, storage,
  target)`, `RedbGetJson(idExpr, …)` — the whole object as raw JSON via the core's new
  `LoadJsonAsync`, so a pure-XML module without any assembly reads redb objects,
  `RedbSave(Type?, byUnique, storage)` — an `IRedbObject` body saved as is, a JSON body
  materialized through the type (`byUnique` → `SaveByUniqueAsync` over the declared unique
  keys), `RedbDelete(idExpr, storage)`; context-level `SyncRedbScheme(Type)` (fail-fast at
  OnContextStarting) and `AddRedbIdempotentRepository(name, ttl)` (registry key
  `idempotent:{name}`, the one `<idempotentConsumer repository=…>` already looks up). In
  markup: `<redbGet id="${header.orderId}" [type=] [depth=] [storage=] [target=]/>`,
  `<redbSave [type=] [byUnique=]/>`, `<redbDelete id=/>`, `<beginRedbTransaction/>` and the
  `<redb>` block of context.xml with `<syncScheme type=/>` + `<idempotentRepository name=/>`.
  Every verb resolves the service per exchange through the same scoped model as the lambda
  verbs and joins the ambient redb transaction.
- **Context-level contribution seam (R21 grows a fourth position).** `IXmlContextContribution`
  (`XmlElementKind.ContextLevel`): a package element living inside `<context>` beside
  `<components>`/`<bean>`/`<onInit>` — applied to the context after beans, before `<onInit>`
  parsing; in route position it is a loud error; the generated XSD lists contributed blocks
  among the context children, and an unknown context section names them in its error.
- **`RedbQuery` / `<redbQuery>` — server-side props queries from a condition string (Route-XML
  step 2).** The `where` string rides the engine's ONE expression AST (no new parser, §9)
  through a visitor into redb LINQ: props paths incl. nested (`Customer.Name`), comparisons,
  `AND`/`OR`/`NOT`, `contains`/`startsWith`/`endsWith` on string props; every subtree WITHOUT
  a props reference — headers, body, functions, date arithmetic — folds per message through
  the AST's own evaluation and enters the query as a constant (so
  `CreatedAt < dateadd(now(), -1, 'day')` and per-message header gates just work). What is
  neither translatable nor foldable refuses LOUDLY at route build — arithmetic over a props
  property, an unknown identifier (named with the property candidates) — never by silently
  filtering on the client; in markup the refusal is a positioned document error.
  `orderBy`/`descending`/`take`/`skip` ride along (`RedbQuery` without any of them refuses:
  an unbounded scan must be asked for explicitly — say `take=`);
  `filter="#spec"` is the full-LINQ escape hatch (`IRedbQuerySpec<TProps>` from the context
  registry, combinable with `where`). The result `List<RedbObject<TProps>>` goes to the
  target (body/header/property). The condition may also live as the ELEMENT'S TEXT instead of
  the `where` attribute — a CDATA block frees it from `&lt;` escaping
  (`<redbQuery …><![CDATA[ CreatedAt < dateadd(now(), -1, 'day') ]]></redbQuery>`); exactly
  one of the two forms. An ordering comparison on a string props property refuses with a
  hint (Expression trees have no `<` for strings) instead of a raw reflection error.
  The `orderBy` selector is typed with the member's own key
  type — the provider's ordering parser reads a plain property access, a boxing Convert it
  refuses (caught by the live-worker E2E, where the whole cycle ran green: a pure-XML module
  with zero assemblies syncing a scheme, saving a JSON body and querying it back server-side
  on SQLite).
- **`pack`/`check` see the package contributions.** `RoutePackage.Check/Build` take the R21
  extensions and the tool discovers them from `--bin` (the Default-ALC fallback keeps the
  interface identity), so a package using `<redbQuery>`/`<cache>`/`<rest>` passes the XSD
  gate exactly the way the worker's discovery will parse it — before this the pack gate knew
  only the core elements and refused any package element.
- **The generator learned package namespaces.** `IXmlElementContribution.GeneratedUsings` —
  a printer whose verbs live outside the core namespaces declares its `using` lines and the
  generated file shell carries them (`<redbGet>` prints `RedbGet(…)` under
  `using redb.Route.RedbCore.Extensions;`).
- **The system prompt can be cached across turns (`redb.Route.Llm`).** `?cacheSystemPrompt=true`
  on the model address — or `.CacheSystemPrompt()` on the fluent builder — asks the provider to
  mark the system prompt as cacheable. On Anthropic that means emitting `system` as a one-element
  block array carrying `cache_control: {type: "ephemeral"}`; without the flag it stays a bare
  string, so no caller sees a changed wire shape it did not ask for. The flag rides `AgentRequest`
  through every iteration of the tool loop, which is where the saving actually accrues: a read
  costs a fraction of normal input, a write a premium over it, so it pays off from the second call.

  **`LlmUsage` grew the two numbers that say whether it works** — `CacheCreationInputTokens` and
  `CacheReadInputTokens`, parsed on both the streaming (`message_start`) and non-streaming paths,
  defaulting to zero so existing construction sites compile untouched. The doc comment states the
  thing that is easy to get wrong: `InputTokens` is the **uncached remainder**, not the whole
  prompt, and a long agentic run reporting four thousand input tokens has not shrunk.

  Two ways it silently does nothing, both documented on the option: caching matches on the rendered
  prefix, so a system prompt carrying a timestamp or a per-user name is written and never read back;
  and there is a model-dependent length floor below which nothing is cached at all, with no error.
  `CacheReadInputTokens` staying at zero across repeated calls is the signal for the first,
  `CacheCreationInputTokens` staying at zero for the second.

- **Those two counters now survive the agent loop and reach the exchange
  (`llm.tokens.cache.write` / `llm.tokens.cache.read`).** They did not: `AgentEngine` summed them
  per iteration and then rebuilt its final usage as `new LlmUsage(in, out)`, dropping both, so
  nothing downstream could observe the cache at all — "caching works" was unfalsifiable by
  construction. The totals are carried beside `AgentUsage` rather than inside it: that struct is
  the **budget** currency, and cache reads are not billable input, so folding them in would make
  every budget quietly wrong. Producer, consumer and the route-definition extension all write the
  pair, zeros included — a missing header is indistinguishable from an older engine, while a zero
  is an answer.

  Verified against the live API (`claude-haiku-4-5`, two calls with a byte-identical system
  prompt): `cacheRead = 16800` on both, `InputTokens` 16 then 15 — full price is paid for the user
  text alone. The live test is `[EnvFact("REDB_LLM_ANTHROPIC_KEY")]`, so an ordinary run stays
  free and green.
- **Fixed opening messages before the loaded history (`redb.Route.Llm`, `AgentRequest.Preamble`,
  header `llm.preamble`).** A byte-stable preamble — a frozen opening exchange, a few-shot block —
  opens the transcript on every iteration, is never persisted to the conversation store and survives
  a branch rebuild. `LlmMessage.CacheBreakpoint` asks the provider for a cache breakpoint after a
  message (Anthropic: `cache_control` on its last content block), so the cached prefix reaches past
  the system prompt into the preamble instead of paying for it in full on every turn.
- **HTTP/2 keep-alive pings on the Anthropic and Telegram transports (`redb.Route.Llm`,
  `redb.Route.Telegram`).** A non-streaming completion and a getUpdates long poll are tens of
  seconds of silence on the wire; VPN tunnels, NAT and proxies drop a silent TLS connection at
  ~50 s, which surfaced as "The response ended prematurely" — every long answer lost, every idle
  poll logged as a transient polling error. Both default clients now ask for HTTP/2 (HTTP/1.1
  remains the fallback) and send a PING frame every 15 s while a request is in flight
  (`SocketsHttpHandler.KeepAlivePingPolicy = WithActiveRequests`). Measured through a
  sing-tun/xray tunnel: 45 s of silence survived, 55 s was cut; with pings four consecutive
  50-second polls completed, without them HTTP/1.1 lost every one.
- **Empty messages are no longer sent to Anthropic (`redb.Route.Llm`, `AnthropicProvider`).** A
  message with no content used to become `{"type":"text","text":""}`, which the API rejects with
  400 "text content blocks must be non-empty" — and because the engine persists every assistant
  reply, ONE empty reply (the model spent its whole `max_tokens` on thinking) broke every later
  call of that conversation until the history was edited by hand. Empty and whitespace-only
  messages are now skipped; the API merges consecutive same-role turns.
- **`LlmConnectionFactory.Effort` (`redb.Route.Llm`).** The effort ladder of models that think
  by default (Claude 4.6+ / Sonnet 5 / Opus 5), sent as `output_config.effort` only when set;
  thinking tokens count against `max_tokens`, so `medium`/`low` keeps the answer inside the
  budget. Per factory on purpose: models without the ladder (Haiku 4.5) reject the field.
- **Telegram file download and speech-to-text (`redb.Route.Telegram` `?mode=download`,
  `redb.Route.Llm` `stt://`).** The Telegram producer gained a download mode (`GetFile` → size cap →
  bytes in the body, `telegram.file.*` headers); a new `stt://` scheme with `ITranscriptionProvider`
  (`OpenAiTranscriptionProvider`, `/v1/audio/transcriptions`, local llama.cpp-style stands included)
  turns a voice note into text under `llm.transcription.*` headers.
- **`redb.Route.XPath2` — a new package: XPath 2.0 expressions.** `using static redb.Route.XPath2.XPath2Dsl;`
  then `XPath2("...")` wherever a route takes an expression. It brings what XPath 1.0 has no answer
  for at all: regular expressions (`matches`, `replace`, `tokenize`), sequences (`distinct-values`,
  `reverse`, `string-join`, `empty`), `avg`/`max`, `if…then…else`, `for $o in … return …`,
  `some`/`every … satisfies`, `xs:date` comparisons and `instance of`. Source, bound parameters and
  trimming work as they do for XPath 1.0. **It is XPath 2.0, not XQuery:** `let`, `where` and
  `order by` are XQuery clauses and the engine rejects them when the route is built, so there is no
  sorting and no grouping — filtering goes in a predicate instead. There is no `xpath2()` function in
  the `${…}` language, which lives in the core assembly and does not know this package exists.
  Dependency: `XPath2` (MS-PL), which has none of its own, so the graph is two assemblies;
  `XPath2.Extensions` is deliberately not referenced because it would pull Newtonsoft.Json back in.
  One expression object is safe to evaluate concurrently: the engine's compiled form mutates shared
  slots when the expression binds range variables (`for`/`some`/`every`) and its `Clone()` shares
  those slots, so evaluations run on a per-thread compiled form — found in review by a 400k-run
  parallel probe, pinned by a test.
- **`IPredicateExpression`, and what it replaced.** An expression that defines its own reading in a
  condition implements this interface rather than `IPredicate`. The first cut used `IPredicate`, and
  that was wrong for a reason worth recording: condition verbs are overloaded on `IExpression` and
  `IPredicate` both, so an expression implementing the latter made `Filter(XPath("/a/b"))` an
  ambiguous call — the language would have gained the semantics and lost the spelling they were
  added for. The tests missed it because they passed expressions through an `IExpression`-typed
  parameter; there is now a compile-time guard that writes the call out directly.
- **Six EIPs take an expression, not only a delegate.** `Aggregate`, `RecipientList`,
  `DynamicRouter`, `IdempotentConsumer`, `Enrich` and `PollEnrich` were typed on `Func<IExchange, …>`
  alone, so a correlation key out of an XML body meant hand-writing
  `e => XPath("/order/id").Evaluate<string>(e)` at every call site. They now also accept an
  `IExpression`: `Aggregate(XPath("/order/customerId"), strategy, completion)`,
  `IdempotentConsumer(repo, jpath("$.messageId"))`, `RecipientList(XPath("/order/to/uri"))`,
  `DynamicRouter(XPath("/order/next"))`, `Enrich(Header("service"), merge)`,
  `PollEnrich(Header("source"), timeout)`. The existing delegate overloads are untouched. Wrapping
  was never the problem; wrapping once per call site was, because each hand-rolled wrapper decided
  on its own what an expression that matched nothing means — that decision now lives in one place:
  a missing correlation key, idempotency key or endpoint URI fails the exchange naming the EIP,
  while Dynamic Router reads "no value" as "no more hops" because that is what it means there.
  `RecipientList` accepts either a sequence of URIs or one delimited string (`uriDelimiter`,
  default `,`), mirroring `RoutingSlip(IExpression, …)`.
- **XPath takes bound parameters instead of concatenated ones.**
  `XPath("/order[@id=$id]").WithParameters(("id", Header("orderId")))` binds the header to the
  `$id` variable, so the value is compared and never parsed: a header holding `B-2' or '1'='1`
  selects an order with that literal id and matches nothing, rather than changing what the query
  asks. Building the path by concatenation — which is what Camel's `allowSimple` does — is why
  XPath injection has an OWASP entry and a CodeQL query of its own; binding values is the settled
  answer everywhere else (JAXP's `XPathVariableResolver`, XQuery's `declare variable $x external`,
  .NET's `XsltArgumentList`, SQL prepared statements). It also keeps the path one constant string
  whatever the message says, which is what lets a compiled expression be reused. Numbers bind as
  XPath numbers, invariantly, so a route does not change meaning with the server's locale. An
  expression carrying bindings or a source refuses `ToTemplateString()` — the `${xpath(path)}` form
  cannot carry either, and serialising without them would mean something else; only unprefixed
  variables resolve, so `$x:id` fails as undefined rather than quietly borrowing the binding
  named `id`.
  **XPath only:** `jpath` has no equivalent, and not because of the library — RFC 9535 has no
  variables, so there is nothing for JsonPath.Net to expose.
- **The XSLT engine is now actually replaceable.** `IXsltEngine` has been an interface since 3.5.0
  and its documentation said a Saxon-backed engine could be plugged in — it could not: the `.Xslt(...)`
  and `.XsltContent(...)` verbs and the `xslt:` component each named the built-in
  `XslCompiledTransformEngine` directly, so there was nothing behind the interface to substitute.
  Substitution goes through the new `IXsltEngineFactory` (the factory, not the engine, because a
  stylesheet has to be compiled and compiling is what a different processor does differently),
  registered with `context.UseXsltEngine(...)` or `services.AddXsltEngine(...)`. Without a
  registration nothing changes: the default is the same BCL engine, XSLT 1.0, no dependencies. We
  still cannot ship XSLT 2.0/3.0 — every .NET processor that implements it is commercial — but a
  route that has one can now use it under its own licence.
- **Namespaces are reachable from the DSL.** `XPath("/s:Envelope/s:Body").WithNamespaces(("s", "http://schemas.xmlsoap.org/soap/envelope/"))`.
  The expression always accepted an `IXmlNamespaceResolver`, but only through its constructor, so a
  route written in the DSL could not query a namespace-qualified document at all — an unbound prefix
  does not match nothing, it refuses to run. The prefixes are the expression's own and need not
  match the document's. Camel keeps namespace sets in a registry (`namespacesRef`) because its XML
  DSL cannot write a map inline; in C# a shared set is an ordinary variable, so there is no registry
  here.
- **`XPath(...).Trimmed()`** trims whitespace off extracted text. Off by default, which is where
  this library has always been and differs from Camel's `trim=true`. For a single value XPath
  answers this itself with `normalize-space(...)`; a node-set of several padded values cannot be
  normalised from inside XPath 1.0, which is what the option is for.
- **`CompiledXPathExpression` is an `IExpression`.** It computes its XPath per message — a selector
  from a header, a per-tenant rule — and was public and tested but implemented nothing, so there was
  no position in the DSL that would take it. It now goes wherever an expression goes, including the
  six EIPs above.
- **`xpath` and `jpath` can read something other than the body.** Both languages take a source:
  `XPath("/invoice/id").From(Header("original-request"))`, `JPath("$.id").From(Property("doc"))`,
  and in the expression language a second argument — `${xpath('/order/id', header.payload)}`,
  `jpath('$.id', property.doc)`. One argument still means the body, and the body is left alone:
  before this, querying XML that arrived in a header meant moving it into the body first, which
  costs the route its body for every step after. The source is an ordinary expression rather than
  Camel's `header:payload` string prefix, so a typo in `header.payloadd` is a parse the route can
  check instead of a string nobody reads. Camel writes `xpath(input,exp)` with the source **first**,
  which makes the first argument mean different things at one and two arguments; we keep the path
  first at both arities, so a route ported from Camel needs its argument order swapped. Inside the
  language a source that produced nothing reads as null, the same answer the one-argument form gives
  a null body — the language is lenient about missing data throughout, and a condition over an
  absent optional header is "no match", not an error. The strict contract lives on the object form:
  `From(...)` naming a source that produced nothing fails loudly. `Aggregate`, `RecipientList`,
  `DynamicRouter`, `IdempotentConsumer`, `Enrich` and `PollEnrich` were typed on `Func<IExchange, …>`
  alone, so a correlation key out of an XML body meant hand-writing
  `e => XPath("/order/id").Evaluate<string>(e)` at every call site. They now also accept an
  `IExpression`: `Aggregate(XPath("/order/customerId"), strategy, completion)`,
  `IdempotentConsumer(repo, jpath("$.messageId"))`, `RecipientList(XPath("/order/to/uri"))`,
  `DynamicRouter(XPath("/order/next"))`, `Enrich(Header("service"), merge)`,
  `PollEnrich(Header("source"), timeout)`. The existing delegate overloads are untouched. Wrapping
  was never the problem; wrapping once per call site was, because each hand-rolled wrapper decided
  on its own what an expression that matched nothing means — that decision now lives in one place:
  a missing correlation key, idempotency key or endpoint URI fails the exchange naming the EIP,
  while Dynamic Router reads "no value" as "no more hops" because that is what it means there.
  `RecipientList` accepts either a sequence of URIs or one delimited string (`uriDelimiter`,
  default `,`), mirroring `RoutingSlip(IExpression, …)`.
- **`XPathResult` — the XPath-level result type**, which is a different question from the CLR type the
  caller converts to (Apache Camel draws the same line as `resultQName` against `resultType`):
  `XPath("/order/total", XPathResult.Number)`, plus `String`, `Boolean`, `Node` and the default
  `NodeSet`. `string()`, `number()` and `boolean()` are XPath's own coercions, so the engine performs
  them rather than the conversion layer guessing; `Node` narrows a node-set to its first match in
  document order. Available on `XPathExpression`, on `TypedXPathExpression<T>` — the two compose,
  `XPath<int>("/order/qty", XPathResult.Number)` — and through the `XPath(path, result)` /
  `xpath(path, result)` DSL helpers. An expression carrying a non-default result type refuses
  `ToTemplateString()` instead of serializing to an `${xpath(path)}` that would mean something else,
  because the language function takes a path and nothing else.
- **FCM multicast and topic management (F11 D5–D6, red-before against fcm-echo).**
  `operation=Multicast` sends one message to many device tokens (`IEnumerable<string>` body or
  a comma-separated `redbFcm.Tokens` header) via `SendEachForMulticastAsync`;
  `SubscribeToTopic`/`UnsubscribeFromTopic` manage topic membership for a token list. All
  three finally populate the `SuccessCount`/`FailureCount` headers that had been declared
  "(future)" since the first release. Neither exists in Apache Camel at all.
- **Firestore `databaseId` for multi-database projects (F11 D7, red-before).** The connector
  was hardwired to `(default)`; the option/DSL `.DatabaseId(id)` reaches the client and the
  cache key, so two endpoints on different databases of one project no longer share a client.
- **Firebase Storage `StreamBody` download is true streaming now (F11 D8, red-before).** The
  body used to be a fully buffered `MemoryStream` wearing a stream costume; the download is
  pumped through a `Pipe` in the background and the exchange gets the reader side — memory
  stays flat regardless of object size, and a background failure surfaces on the next read.
- **Firebase Storage: Copy, signed URLs and bucket operations (F11 D1–D3, all red-before
  against fake-gcs).** `Copy` — server-side copy with `destinationObjectName`/
  `destinationBucket` (option or header); `CreateDownloadLink` — signed download URL with a
  TTL (`signedUrlExpiration`, default 1h; requires a service-account JSON — plain ADC cannot
  sign, and the error says so); `CreateBucket`/`DeleteBucket`/`ListBuckets` plus
  `AutoCreateBucket` on the consumer — parity with the S3 sibling and camel-google-storage.
- **Polling consumers can share a NAMED idempotent repository (F11 D4, red-before).** Firebase
  Storage and S3 consumers take `idempotentRepository=<name>` — the same
  `IIdempotentRepository` contract the route-level IdempotentConsumer EIP uses, registered
  via `context.AddIdempotentRepository(name, repo)`. Two-phase claim (Add → process →
  Confirm/Remove): a failed object is released and retried, a concurrent consumer cannot
  take the same object twice, and with the persistent `RedbIdempotentRepository`
  deduplication survives process restarts and scale-out. The per-consumer in-memory
  dictionary stays the default.
- **Firestore `Where` syntax finished (F11 G6, owner decision, red-before).** Single-quoted
  values are strict string literals (`status=='007'` no longer coerces to the number 7);
  ` array-contains ` is recognized only as a spaced token, so a field whose NAME contains the
  substring no longer tears the condition apart; `${...}` templates in `Where` resolve per
  exchange on the producer Query operation (through the shared expression engine — no new
  string dialect), and a consumer with `${...}` in `Where` fails loud at startup instead of
  producing a nonsense subscription.
- **Firebase connector, wave G (F11):** `FirebaseCredentialProvider` is public with
  `DefaultProjectId`/`DefaultCredentialPath` — a named instance goes into the context registry
  and `connectionFactory=…` finally has a usable implementation; Firestore consumer gained
  `MaxConcurrency` (default 16 — an initial snapshot no longer floods the pipeline with the
  whole collection in parallel); BatchWrite gained `DocumentIdField` (document id from an item
  field, stripped from the data) next to the auto-ID default. DSL: `.MaxConcurrency(n)`,
  `.InitialDelay(ms)`, `.DocumentIdField(name)`.
- **Core registration primitives for connector packages (F11 wave B):**
  `services.AddRouteComponent<TComponent>()` — singleton + startup configurator in one call —
  and `services.AddRouteContextConfigurator((sp, context) => ...)` for connectors whose
  registration wires more than a bare component (shared Kestrel manager, named registry
  entries, several components). All 28 connector packages now build on these two.
- **Firebase Storage consumer: `MoveFailed` quarantine prefix (F11 wave A).** Objects whose
  processing failed are moved (copy + delete) into the given prefix instead of being retried on
  every poll — the Camel `moveFailed` pocket. DSL: `.MoveFailed("failed/")`.
- **`redb.Route.Xml` (new package): declarative XML routes — first vertical slice (Route-XML F2).**
  A `.route.xml` document loads into the existing fluent DSL through a contribution registry
  (decision R21): every element is one `IXmlElementContribution` facading one DSL verb, the core
  registers its own elements through the same contract packages will use
  (`XmlRouteLoaderOptions.Extensions`), and a duplicate element name is a hard error, never a
  silent override. The whole document parses eagerly at load: every problem — an unknown element
  (with a "did you mean" hint), a malformed expression, `value` and `expr` together, a `${...}`
  in a consumer URI, an endpoint scheme not registered in the context — is collected in ONE pass
  with file/line/column positions and thrown as one `XmlRouteException`; a document with errors
  registers nothing. This slice carries the container (`<bean>` with `<property>` binding through
  the shared option converter, `<route>` with its attributes incl. the new `enabled=` — false
  skips registration entirely), twenty leaf steps, `<filter>`/`<split>` scopes and
  `<choice>`/`<when>`/`<otherwise>` branching; the generic `id`/`description` attributes flow
  into step identity (`StepId`/`StepDescription` in message history). Entry points:
  `AddXmlRoutesFromContent(xml)` and `AddXmlRoutes(files)` on `RouteContext`. The remaining
  element catalog, container-level handlers, the structured endpoint form and `context.xml`
  follow in further increments of the phase.
- **`redb.Route.Xml`: the rest of the F0 §4 element catalog (Route-XML F2, second increment).**
  ~35 more elements through the same registry. Leaves: `<sort>`, `<sample>`, `<streamCaching>`,
  `<validateJsonSchema>`/`<validateXsd>` (file XOR inline content; files load through the F1.4
  resource resolver), `<xslt>` (file passes through to the runtime resolver), `<marshal>`/
  `<unmarshal>` (registered `format=` or `type=` with per-step option attributes bound to a fresh
  serializer instance; options with `format=` are refused — registry serializers are shared;
  `<unmarshal>` with neither dispatches by the message `ContentType`), `<controlBus>`, `<enrich>`/
  `<pollEnrich>` (named strategies via `AggregationStrategies.ByName` or `#registry`),
  `<recipientList>`, `<dynamicRouter>`, `<routingSlip>`, `<claimCheck>`, the transaction verbs,
  `<exceptionHandled>`, `<routePolicy ref=>`, `<setHeaders>` with `<header>` children. Scopes:
  `<multicast>`, `<aggregate>` (strategy attribute is REQUIRED — owner decision: no silent
  default), `<tryCatch>` + `<try>`/`<catch>`/`<finally>`, `<loop>` (count/expr/while — exactly
  one), `<throttle>` (plain and keyed, expression maxPerPeriod, `rejectOnOverflow`), `<debounce>`,
  `<circuitBreaker>` + `<fallback>`, `<idempotentConsumer>`, `<resequence>`, `<transaction>`
  (named policies default/requiresNew/suppress/mandatory), `<traced>`, `<metered>`,
  `<replayable>`, `<threads>`, `<ofType>` (a typed converting section — children belong to it,
  siblings continue on the route), route-level `<onException>`, `<intercept>`/`<interceptFrom>`/
  `<interceptSendToEndpoint>`/`<onCompletion>` (childless `<when expr=>` inside them is the
  condition), `<scatterGather>` + `<recipient>`, `<loadBalance>` + `<endpoint>` (five strategies,
  weighted validates a weight on every endpoint), `<normalize>` + `<when>`/`<whenContentType>`/
  `<otherwise>` with `transform=` expressions, rich `<log>` with `<message>`/`<header>`/
  `<property>` children (text and children together is a schema error), and the `<split>`
  tokenizers `<tokenizeLines>`/`<tokenizeXml>`/`<tokenizeJsonArray>`. `<saga>` stays deferred:
  its DSL is lambda-only and has no XML shape yet.
- **`redb.Route.Xml`: container-level handlers and glob loading (Route-XML F2, third
  increment).** `<onException>`, `<intercept>`/`<interceptFrom>`/`<interceptSendToEndpoint>` and
  `<onCompletion>` are now valid directly under `<routes>` — declared on the builder, they apply
  to every route of the file, and they are configured by the SAME helpers the route-level
  elements use (no second parsing copy). New `AddXmlRoutes(globPattern)` overload:
  `"routes/*.route.xml"` loads every match in deterministic ordinal order, a pattern matching
  nothing is an error naming the searched places, and a wildcard-free argument loads that one
  file; single-file loading now resolves relative paths through the route resource resolver
  (AppContext base before the working directory), so `dotnet run` from another directory stops
  changing which files load. The R21 extension channel is exercised by tests from outside the
  core: a registered contribution parses like a core element, an unregistered one is rejected,
  and a name clashing with a core element fails registration hard.
- **`redb.Route.Xml`: the structured endpoint form — F0 §7.2 (Route-XML F2, fourth
  increment).** An address-carrying step (`<from>`, `<to>`, `<toD>`, `<wireTap>`, `<enrich>`,
  `<pollEnrich>`) now takes either `uri=` or exactly one child endpoint element:
  `<to><kafka path="orders" key="${header.tripId}"/></to>`, or the SQL shape with the query as
  CDATA content, `<param name=… value=…/>` family entries (→ `param.login=…` options) and long
  text options as children (`<onSuccess><![CDATA[…]]></onSuccess>`). ONE generic procedure
  normalizes any scheme into the same URI string a hand-written address would be — before the
  endpoint exists, so the whole engine pipeline (endpoint cache, `NormalizedKey`, statistics,
  mock masks, secret redaction) sees a single canon and gains zero new code; there is no switch
  per connector and no per-connector XML code at all (the owner's anti-copypaste rule by
  construction; per-connector `path` synonyms like kafka→`topic` arrive with the F4 catalog).
  Option values travel raw like the fluent builders emit them, except `%`→`%25` and `&`→`%26` —
  the two characters `EndpointUriParser`'s query parse cannot survive — so ordinary values stay
  byte-identical to the URI form and SQL with `&`/`LIKE '%…%'` round-trips exactly. `uri=`
  together with a child, two children, `path=` plus text content, `?` in a path and a malformed
  option child are all positioned schema errors; scheme checking and the `${…}`-in-consumer
  refusal work on the assembled string with the child element's position.
- **`redb.Route.Xml`: `context.xml` — the context-level document (Route-XML F2, fifth
  increment).** `AddXmlContextFromContent` / `AddXmlContext(file)` load a
  `<context xmlns="urn:redb:route:1.0">` with three sections: `<components>` (an explicit
  `<component type=…/>` list registering `IComponent`s for the schemes route files use),
  context-wide `<bean>` declarations — now with **nested anonymous beans** in
  `<constructorArg>` (an options object built inline, properties bound through the shared
  converter, recursively), and `<onInit>` — a pipeline of ordinary format steps (DDL through a
  connector producer, seeding through `bean:`) parsed by the same element registry and run ONCE
  inside `OnContextStarting`, the engine's only fail-fast bootstrap hook: a failed step keeps
  the context from accepting traffic and the error names the source file; a failed `Start()`
  retries the init on the next `Start()`. Also: `enabled=` on `<route>` is now
  `{{…}}`-aware — resolved through the context's own placeholder chain (`IConfiguration`, then
  context properties, `{{key:default}}` supported), so a route can be switched per stand from
  configuration; an unresolved key without a default and a non-bool result are positioned
  schema errors. One core seam for this: `RouteContext.ResolvePlaceholders` went
  private→internal so the loader reuses the existing lookup chain instead of copying it.
- **`redb.Route.Xml`: the XSD is generated from the element registry (Route-XML F4, first
  batch).** Every contribution now carries a public `ElementSpec` (attributes with types and
  enum values, nested branch/config children, content flags) — the R21 contract grew a
  default-implemented `Spec` property, so existing contributions keep compiling and a package
  that declares its shape gets schema validation and editor autocompletion for free.
  `XmlRouteSchema.Generate(registry)` emits the XSD for the INSTALLATION's registry (one
  namespace, `urn:redb:route:1.0`, per R21): the step vocabulary, scopes with their children,
  enum hints (`xs:enumeration` — editors offer the values), required attributes, `id`/
  `description` everywhere, foreign-namespace attributes tolerated (`anyAttribute ##other
  lax`, R9), foreign elements rejected; the §7.2 endpoint children are lax open content until
  the component catalog supplies the scheme elements. Tests pin the one-list guarantee both
  ways (every core contribution has a spec, every spec belongs to a contribution), validate
  all five shipped examples against the generated schema, and show a package contribution
  appearing in and disappearing from the schema with its registration. The §3.2 version
  policy is enforced on load: a newer minor says "update the redb.Route.Xml package", a
  different major is named a different format.
- **`redb.Route.Xml`: route packaging and the project scaffolder (Route-XML F5, the
  Tsak-independent half).** `redb-route-xml new <Name>` scaffolds a route project the way
  sketch 11 drew it: an ordinary `.csproj` (VS Code, Visual Studio and Rider all open it),
  `context.xml`, `routes/`, `resources/`, the generated XSD wired into `.vscode/settings.json`,
  and OUR layered-configuration convention — the L4 in-package config holds module identity
  ONLY (`ContextName` + `AutoStart`), settings and secrets arrive through the merged context
  configuration at deploy time (the identity `context.json` conveyor as the model; the
  everything-in-the-package legacy style with literal passwords is exactly what the checks
  warn about). `redb-route-xml pack` builds the F5.1 package layout (`manifest.json` with
  `Artifacts`/`SchemaVersion`/`Resources`/`Context`, the L4 config, artifacts, resources) into
  a `.tpkg` zip, and `check` is the same gate for CI. The F5.4 checks run first and a hard
  error refuses the build: well-formedness and schema validation with positions, `]]>` inside
  CDATA, `file=` resources missing from `resources/`, undeclared `#name` references (named as
  either module-code-registered or dangling), a literal-secret heuristic (warning), and every
  `{{key}}` without a default recorded in the manifest's `RequiredConfigKeys` — the
  required-keys manifest the module will fail fast against at init. The Tsak side (manifest
  fields in `ModuleManifest`, `XmlRouteModule`, package resolvers) stays untouched by owner
  instruction — the layout is exactly what it will consume.
- **`redb.Route.Xml`: the code generator — XML printed as the fluent C# it parses into
  (Route-XML F6).** `Print` is the third face of the R21 contribution beside `Apply` and
  `Spec` (default: a package element without a printer is an explicit generator error, never a
  silent skip). `XmlCodeGenerator.Generate(document, className, ns, style)` emits a
  `RouteBuilder` class: readable style for migration (descriptions become comments), machine
  style adds `#line` directives so stack traces and breakpoints point at the XML (the Razor
  trick). Generated code is STATEMENTS against receiver variables, never one long chain — a
  chain dies at the first scope closer (`End*` returns the facade), which is exactly why the
  F3 equivalence twins were written with variables; the model also expresses the `ofType`
  typed section naturally (a variable that never closes). Beans print through the new runtime
  helper `XmlBeans.Create` — the same resolver-and-converter semantics the `<bean>` section
  has, nested anonymous beans recurse. The honesty check is mechanical and committed: all five
  examples generate C# that lives in the test project (a golden — regenerate deliberately),
  COMPILES there, and produces definition trees byte-identical to loading the XML.
  `MermaidRenderer` (core, `Diagnostics`) is the third receiver of the same walk: flowchart
  diagrams from the definition tree — both spellings of a route draw identically — with
  committed `.mmd` goldens for every example.
- **`redb.Route.Xml`: the component catalog + §7.2 metadata on the component (Route-XML F4,
  second batch).** `ComponentBase` gained the two one-liners F0 §7.2 sanctions:
  `StructuredPathSynonym` (kafka → `topic` — read by the loader, the catalog and the schema,
  never from a hand-kept list) and `PathIsText` (sql: the structured form takes the path from
  CDATA content and refuses a path attribute). `ComponentCatalog.Build(components)` describes
  every connector by reflection over what it already has — scheme, synonyms, its `Options`
  class (found by naming, or as the single candidate in a single-component assembly), options
  with types, defaults, enum values and `[Sensitive]` marks — and `ToJson` emits the file the
  editor's property panel reads. A catalog-aware `XmlRouteSchema.Generate(registry, catalog)`
  turns the §7.2 endpoint children from lax open content into a STRICT set of scheme elements
  with typed, enum-hinted options. The anti-copy-paste criterion is a test: a connector with
  Options, a Scheme and one synonym line appears in the structured form, the catalog and the
  strict schema with zero XML-specific code. The all-connectors catalog TOOL is deliberately
  deferred: other agents are working in connector packages right now, and building their
  projects from here is against the multi-agent discipline — the mechanism is proven, the
  tool run is a quiet-window follow-up.
- **`RouteDescriber` (new, `redb.Route.Diagnostics`) + the F0.3 examples become executable
  contract (Route-XML F3).** The describer renders a definition tree as stable, diffable,
  human-readable text — one generic reflection walk (node kind, significant parameters,
  conditions through the V4 `IConditionSource.SourceTemplate` seam; never delegates, instances
  or hash codes) — shared by the XML-vs-C# equivalence tests, the future F6 generator
  round-trip and Mermaid. TestKit needs ZERO adaptation over XML routes: the canonical flow
  (`AddXmlRoutesFromContent` → `AdviceAllRoutes(MockEndpoints)` → `Mock` → `SendBody` →
  `AssertIsSatisfiedAsync`), `WeaveById` by the XML `id=` attribute, and the no-password-leak
  guarantee are all covered by tests. Every shipped example under `docs/Route-XML/examples/`
  now LOADS through the real loader in tests (stub schemes, mapped bean types, the examples
  directory as the resource root) and has a committed golden description; a golden is never
  updated to go green. The four translated examples (scope-diag, eip, deep-dsl-showcase,
  main-pipeline) each have an equivalence test: the tree loaded from XML and the tree built by
  the post-V4 fluent C# twin are BYTE-IDENTICAL — the R1 promise (one engine, two spellings)
  made executable on real material. The package README gained a "Testing an XML route"
  section. Loading the examples flushed out three real defects, each fixed red-before:
  - the named-repository `IdempotentConsumer` overload existed on the base but not on the
    `IRouteDefinition` facade, and the string-form extension patched over it with a cast to
    the concrete `RouteDefinition` — inside any scope (`<throttle>`, …) it refused to build.
    The facade now declares the overload and the cast is gone;
  - `<bean>` property and `<constructorArg>` values did not resolve `{{key}}`/`{{key:default}}`
    through the context's placeholder chain before type conversion — `{{ldap.port:636}}` failed
    to bind to an `int`, defeating the whole point of the section (configuration-fed beans);
  - `id=`/`description=` on branch children (`<when>`, `<otherwise>`, `<catch>`, `<finally>`,
    `<fallback>`) were silently dropped — R12 promises them on every element; they now land on
    the branch definition itself.
- **`redb.Route.Xml`: namespace discipline — no local-name smuggling.** The parser matched
  child elements by local name only, so `<alien:setHeader>` from a foreign namespace silently
  parsed as the core element — a hole that would undermine the whole XSD story of the F4
  catalog. Fixed red-before: every element of a document must live in `urn:redb:route:1.0`; a
  foreign or empty namespace is a positioned schema error. The extension-namespace strategy
  for package contributions is written up as a decision fork in the F4 document (§3.5).
- **`redb.Route.Xml`: the DI surface (Route-XML F2, sixth increment).** The same loading
  overloads on `RedbRouteBuilder`: inside `AddRedbRoute(r => r.AddXmlRoutes("routes/*.route.xml"))`
  (plus the file-list, `AddXmlRoutesFromContent` and `AddXmlContext` forms), applied at
  context-start through the standard `IRouteContextConfigurator` hook — an ordinary ASP.NET
  host runs XML routes with one line, no Tsak required; a document with errors fails the host
  start with the aggregated positioned report.
- **Camel-style parameter binding for `bean:` (Route-XML F1.6, owner decision).**
  `bean:#svc?method=validate(${body}, ${header.id})` — each argument is a route-language
  expression and the method's parameters carry ordinary types (`bool Validate(Order order,
  string id)`), so a bean stops being coupled to `IExchange`. The overload is picked by argument
  count (several same-count overloads are refused with the list — binding does not resolve by
  type); an optional trailing `CancellationToken` parameter is filled automatically; an empty
  list (`method=run()`) calls a parameterless method. Argument expressions compile at endpoint
  creation (a malformed one fails the build) and values convert through the shared option
  converter — a null into a value-type parameter fails naming the parameter. The
  `(IExchange[, CancellationToken])` signatures stay the first-priority form, untouched.
  Boundary: an argument must not contain a bare `&` (the URI query splits on it) — write the
  word form `AND`. Deliberately not in this cut: `@Body`/`@Header`-style parameter attributes and
  Camel's implicit body-to-first-parameter binding — tracked in the injections research.
- **The `bean:` component — user code as an ordinary endpoint (Route-XML F1.2, decision R8).**
  Two URI forms: `bean:#name?method=X` takes the object from the context registry;
  `bean:Namespace.Type, AssemblyName?method=X&prop=value` creates the instance through
  `ActivatorUtilities` (constructor DI works), binds the remaining query parameters onto its
  public settable properties through the same converter the endpoint options use (extracted as
  the shared `OptionValueConverter` rather than duplicated), and registers `[Sensitive]`-marked
  property names with the URI redaction set. A suitable method takes `(IExchange)` or
  `(IExchange, CancellationToken)`; `method=` is optional with exactly one candidate and the
  ambiguity error lists them; a non-null result (`T` or awaited `Task<T>`) becomes the body,
  `void`/`Task` leave it. The method is compiled into a delegate at endpoint creation —
  reflection never runs per message. Loud, specific errors: a version-pinned type name is
  refused (a pinned version breaks the route on every assembly bump), "not found" and "found but
  not public" are distinct, an unknown property names the writable ones. The instance lives as
  long as the endpoint (cached by normalized URI: equal URIs share, different parameters get
  their own). `IBeanTypeResolver` is a context service so a host that loads user code into its
  own `AssemblyLoadContext` (a Tsak package) can search its assemblies first; the default
  resolver searches every loaded assembly, collectible contexts included. Camel-parity note:
  Camel's parameter binding (`method=handle(${body}, ${header.x})`) is deliberately not in this
  first cut — the injection/parameter-binding research is tracked separately.
- **String forms for the remaining lambda-only EIPs (Route-XML F1.3).** Following the 4.0 rule —
  the string form lives on the verb, never under an `*Expression` name, and compiles at
  declaration so a malformed expression fails the build: `RecipientList(string, delimiter, …)`
  (the expression may yield a collection of URIs or one delimited string),
  `DynamicRouter(string)` (null/empty stops routing), `Resequence(string, batchSize, timeout)`,
  `Debounce(string, quietPeriod)`, `IdempotentConsumer(string keyExpression, string
  repositoryName, …)`, `UseSticky(string)` on the load balancer, `Normalize`'s
  `When(string condition, transform)`, the non-generic `OfType(Type)` (parity of construction
  with `OfType<T>()`, for callers that learn the type at load time), and
  `RoutePolicy(string policyName)` — resolved from the context registry when the route compiles,
  a missing registration fails `Start()` naming the route and the policy.
- **`Aggregate` from a correlation string, completing by condition, size and inactivity timeout.**
  `Aggregate(string correlation, strategy, completionCondition?, completionSize?,
  completionTimeout?)`: any combination, whichever fires first completes the group, and at least
  one criterion is required — an aggregate that can never complete is a leak, not a default.
  `completionSize` rides a counter the strategy wrapper stamps on the accumulated exchange (an
  engine-internal `__`-prefixed property, invisible to templates); `completionCondition` compiles
  through the same predicate path as `Filter(string)`. `completionTimeout` is new engine capability
  with Apache Camel semantics (the inactivity clock restarts on every arrival for the group):
  `AggregatorProcessor` grew a periodic scan timer in the resequencer's established shape —
  fire-and-forget flush, an exception from the target lands on the exchange — and is disposed
  with the route. `AggregateDefinition.CompletionTimeout` is public for the XML form.
- **Three expression-language additions: the modulo operator `%`, `uuid([format])` and
  `datediff(a, b, unit)` (Route-XML F1.5, owner decision).** `%` sits beside `*` and `/` in
  precedence, is whitespace-insensitive like every operator, coerces its operands like division
  does (invariantly; a zero divisor yields null, a whole result collapses to int) — so
  `Filter("header.n % 2 == 0")` partitions by key. `uuid()` is a fresh GUID as text (`D` format;
  `uuid('N')` compact) and is impure by design: every evaluation yields a new value, which the
  expression caches never memoize (they store delegates, not results). `datediff` is first minus
  second in `dateadd`'s unit vocabulary plus `ms`; dates coerce exactly as `dateadd` coerces them
  (string dates parse invariantly), an unknown unit yields null like `dateadd`. All three are
  implemented once and dispatched from both engine branches (compiled and interpreted); the
  characterization grid grew eleven rows — value, condition and template agree on every one —
  with zero drift across the 225 previously recorded forms. Adding `%` also taught the value
  dialect's operator detector about it (narrowly: only `%`, quoted literals masked), so
  `Expr("header.a % 5")` computes instead of reading null.
- **Resource resolution for file-reading endpoints: `IRouteResourceResolver` (Route-XML F1.4).**
  `validator:` and `xslt:` used to probe their schema/stylesheet with a bare `File.Exists`, i.e.
  relative to the process working directory — inside an unpacked package that directory is the
  worker's, not the package's. Both components now resolve the path through the context's
  registered `IRouteResourceResolver` (a context service; hosts register an instance with their
  package root). The default chain probes the absolute path, `ResourceRoot` (when set),
  `AppContext.BaseDirectory`, and the working directory — deliberately last, so today's behaviour
  survives as the final fallback. A miss fails endpoint creation with every probed location named
  in the message instead of the bare original string.
- **Step labels and honoured step ids in message history: `Description("...")` DSL verb
  (Route-XML F1.1).** `Description` labels the most recently added step
  (`ProcessorDefinition.StepDescription`, Apache Camel `description()`; before any step it
  describes the route itself), and message history now honours both identity verbs: a node's
  history id is its `StepId` when one was assigned via `Id("...")` (before: always the generated
  label+counter), and its label is the `StepDescription` when set (before: always the
  type-derived label). The counter is still consumed from the type-derived label for every node,
  so naming or describing one step never shifts the generated ids of its neighbours; a route
  with neither verb records byte-identical history. A duplicate step id within one route now
  fails route validation at `Start()` (case-insensitive, matching `WeaveById`) instead of
  producing unattributable traces.
- **`format(value, pattern[, culture])` in the expression language.** The explicit locale escape hatch
  the invariant-`${...}` change points at: `${format(header.price, 'N2', 'ru-RU')}` is `19,95`,
  `${format(header.price, 'N2')}` is `19.95` whatever the server's culture. It was named in the migration
  note and the expressions guide before it existed — the compiler answered
  `NotImplementedException: Compilation of function 'format' is not yet implemented`, so the breaking
  change had removed locale formatting instead of relocating it. Works for anything `IFormattable`
  (numbers, dates, `TimeSpan`, `Guid`); a string that holds a number or a date is parsed invariantly
  first, other text comes back as itself, `null` stays `null`. An unknown culture name is an error, not
  a silent fall back to invariant. Both branches of the engine (compiled and interpreted) share one
  implementation.
- **String forms on the verbs:** `Loop(string countExpression, copy, shareScope)` (count from the message;
  `LoopWhile(string)` remains the condition form), `Delay(string durationExpression)` (milliseconds, a
  `TimeSpan`, or `hh:mm:ss` text), `Throttle(string maxPerPeriodExpression, TimeSpan? period)`. A malformed
  expression fails at route build.
- **`redb.Route.Cache` (new package): cache as an EIP** (WSO2 `Cache` mediator / Camel cache
  component analog) over the .NET caching abstractions — `IMemoryCache` in-process (created on demand)
  or any `IDistributedCache` (Redis, SQL Server, …). Two forms sharing one store:

  ```csharp
  .Cache("customer-${header.customerId}", TimeSpan.FromMinutes(5))     // scope: hit → skip inner steps
      .Enrich("http://crm/customers/${header.customerId}")
  .EndCache()

  .To("cache:customers?action=get&key=${header.customerId}")           // component: get | put | remove | clear
  .Filter("header.cache.hit == false").Enrich("http://crm/...").To("cache:customers?action=put&key=${header.customerId}&ttl=5m").EndFilter()
  ```

  Scope options: `Region`, `Ttl`, `SlidingExpiration`, `CacheHeaders`, `Distributed()` / `InMemory()`,
  `KeyFromBody()` (SHA-256 of the body). Header `cache.hit` on every pass. A POCO body goes through a
  distributed cache as JSON with its CLR type and comes back as that type. Durations in URIs:
  `500ms`, `30s`, `5m`, `2h`, `1d`, `hh:mm:ss`. Setup: nothing for in-process; `context.UseCache(o => …)` /
  `services.AddRedbRouteCache(o => …)` for options (`MaxEntries`, `DefaultTtl`, `DefaultProvider`) and the
  `cache:` component in DI. `NodePipeline` is now public so scope nodes can live in packages.
- **`redb.Route.JsonTransform` (new package): declarative JSON-to-JSON transformation with JSONata**
  (Camel `jslt` / `jolt` / `jsonata` analog) on the native .NET engine Jsonata.Net.Native 3 — no
  JavaScript, no JVM. `TransformJson("Transforms/order-to-shipment.jsonata")` (file locator, compiled
  once, cached) or `TransformJson(TextSource.Inline("{ 'id': orderId, 'to': { 'city': address.city } }"))`.
  Input: JSON text / bytes / `Stream`, a System.Text.Json tree, or a POCO; `$headers` and `$properties`
  are bound (the same names as in payload templates); output JSON text with `ContentType`
  (`Indent` option) or a `JsonNode` (`JsonTransformOutput.Node`); an undefined result is a `null` body.
  A bad specification or a missing file fails `Start()` with the specification name. Options via
  `services.AddJsonTransform(...)` / `context.UseJsonTransform(...)`. XML: `<transformJson spec="…"/>`.
- **`TextSource` in the core** — one abstraction for "text from a file / embedded resource / inline"
  shared by `redb.Route.Templates` and `redb.Route.JsonTransform`: `TextSource.File(path)`,
  `TextSource.Embedded(assembly, path)`, `TextSource.Inline(text)`, `TextSource.FromLocator("assembly:Name/path")`.
  (`TemplateSource` of the unreleased Templates package became this type.)
- **REST DSL in `redb.Route.Http` (Camel `rest()` parity).** A declarative layer over the existing HTTP
  consumer and the shared Kestrel host: every verb becomes an ordinary route
  `From("http://host:port/base/path?methods=GET&inOut=true")`, so path templates, method dispatch
  (405), CORS and TLS stay where they already are.

  ```csharp
  this.Rest("/api/orders", o => { o.Port = 8080; o.BindingMode = RestBindingMode.Json; })
      .Get("/{id}").Produces("application/json").OutType<Order>().To("direct:get-order")   // header.id, header.query.page
      .Post().Consumes("application/json").Type<Order>().To("direct:create-order")          // 415 on another Content-Type
      .Put("/{id}/status").To("direct:set-status")
      .Delete("/{id}").Route().Process(...);                                                // inline steps
  ```

  Path parameters arrive as plain headers (`header.id`), query parameters as `header.query.*`;
  `Consumes` / `Produces` set the media types (a mismatching request is answered with 415); JSON
  binding unmarshals a declared `Type<T>()` and marshals an object body back; no body and no status
  → 204; the status code stays the consumer's `redbHttp.ResponseCode` header. An OpenAPI 3.0.3
  document (paths, path parameters, request / response schemas with `$ref`s, `operationId` = route
  id, `summary` = `Description`) is served at `{basePath}/openapi.json` (configurable / off).
  Several declarations share a port. `RouteBuilder.From` is now public so DSL layers can add routes.
- **Interception: `Intercept()`, `InterceptFrom(uriMask)`, `InterceptSendToEndpoint(uriMask)`** (Camel
  parity). Declared on a route or on the `RouteBuilder` for every route it defines; each opens a scope
  of steps with an optional `.When("...")`. They are not route steps but compile-time decorators, so
  `Intercept()` reaches steps nested in `Choice`, `Split`, `Multicast`, `TryCatch` and every other
  scope; `InterceptFrom` fires once on entry; `InterceptSendToEndpoint` wraps `To` / `ToD` whose
  target matches (dynamic targets are matched per message), publishes the target in headers
  `redb.toEndpoint` and `CamelToEndpoint`, and `.SkipSendToOriginalEndpoint()` replaces the send —
  a dry run that never even resolves the original endpoint. `Stop()` inside an intercept stops the
  exchange; an intercept's own steps are never intercepted.

  ```csharp
  InterceptSendToEndpoint("kafka://*").When("property.dryRun")
      .Log("dry run: would send to ${header.redb.toEndpoint}")
      .SkipSendToOriginalEndpoint();
  ```
- **`OnCompletion()`** (Camel parity): steps that run after the route finished with an exchange, on a
  **copy**, outside the route's transaction and error handlers, never changing the route's result.
  `.OnCompleteOnly()` / `.OnFailureOnly()` (the exception is on the copy), `.When("...")`,
  `.ModeBeforeConsumer()` to run before the consumer gets the reply (default: asynchronously after).
  A handled exception is a completion. A failing block is logged, not thrown. Route-level or builder-level.
- **Header / property / body sugar:** `SetHeaders(("a", 1), ("b", expr), ...)` — several headers in one
  step (constants, `IExpression`, `Func<IExchange, object?>`); `RemoveHeaders("X-Internal-*", except: "X-Internal-Keep")`
  and `RemoveProperties(mask, except...)` with exact / trailing-`*` / `regex:` masks; `Sort("body.items", "body.priority")`
  (the key expression sees each element as `body`; numbers compare numerically across CLR types) and a
  typed `Sort<T>(source, key, descending, comparer)` that leaves a `List<T>` in the body.
- `DynamicEndpointResolver.ResolveUri(exchange)` resolves a `ToD` target without creating a producer;
  `ToDynamicDefinition.Template` is public.
- **`AggregationStrategies` — a library of ready-made aggregation strategies** (Camel
  `AggregationStrategies` parity). Each is the `Func<IExchange, IExchange, IExchange>` the DSL already
  takes, so one strategy serves `Aggregate`, `Multicast`, `ScatterGather`, `Split` and `Enrich`:
  `GroupedBody()` / `GroupedBody<T>()`, `GroupedExchange()`, `UseLatest()`, `UseOriginal()`,
  `Concat(separator)`, `MergeHeaders()`, `IntoHeader(name)`, `IntoProperty(key)`,
  `Sum / Max / Min("body.amount")` (route-language expression or `IExpression`), `Custom(func)`.
  All tolerate the Split-style `null` first argument and the Multicast / Aggregate-style seeded first
  exchange. `AggregationStrategies.ByName("intoHeader:customer")` resolves the XML-form names.

  ```csharp
  .Aggregate(e => e.In.Headers["orderId"]!.ToString()!, AggregationStrategies.GroupedBody(), agg => ((List<object?>)agg.In.Body!).Count >= 3)
  .Enrich("sql:SELECT * FROM customers WHERE id = @id", AggregationStrategies.IntoHeader("customer"))
  .Multicast().AggregationStrategy(AggregationStrategies.Concat(",")).To("direct://a").To("direct://b").EndMulticast()
  ```
- **`Enrich(uri)` / `PollEnrich(uri, timeout)` without a strategy.** The response (or polled message)
  becomes the current message — `UseLatest()`, the Camel default; on a poll timeout the original stays.
  Dynamic-URI overloads too.
- **Keyed throttle with a per-message limit.** `Throttle(keyExtractor, e => limit, period)` and the string
  form `Throttle("header.customerId", "header.tier == 'gold' ? 100 : 10", period)`: key **and** limit
  come from the message, each key under its own gate. `KeyedThrottleDefinition.MaxPerPeriodFactory`
  exposes it; the fixed `int` form is the constant case. Every key's gate is now the same
  arrival-order-fair, per-message-limit gate as the plain `Throttle` (`ThrottleProcessor`, one per key),
  so both throttles behave identically in wait and reject (429 + `Retry-After`) modes; idle gates are
  still evicted after two periods.
- **Data formats beyond JSON/XML: four new packages plus byte wrappers in the core** (Camel data-format
  parity). Every format is an `IMessageSerializer`, so the existing `Marshal` / `Unmarshal` nodes,
  `DataFormatRegistry` and content-type-driven `Unmarshal<T>()` / `ConvertBody<T>()` just work.

  | Package | Content type | Library |
  |---|---|---|
  | `redb.Route.DataFormats.Csv` — `MarshalCsv()` / `UnmarshalCsv<List<Row>>()`, POCO lists, `List<Dictionary<string,string>>`, raw `List<string[]>`; delimiter, header, quote, culture options | `text/csv` | CsvHelper |
  | `redb.Route.DataFormats.Protobuf` — `MarshalProtobuf()` / `UnmarshalProtobuf<T>()` for generated `IMessage` types; optional Confluent wire framing (magic byte, schema id, message index) | `application/x-protobuf` | Google.Protobuf |
  | `redb.Route.DataFormats.Avro` — `MarshalAvro()` / `UnmarshalAvro<T>()`, schema built from the CLR type or given as JSON; optional Confluent wire framing | `application/avro` | Chr.Avro |
  | `redb.Route.DataFormats.Yaml` — `MarshalYaml()` / `UnmarshalYaml<T>()`, `object` target gives a string-keyed tree | `application/yaml` (+ `x-yaml`, `text/yaml`) | YamlDotNet |
  | core: `Base64MessageSerializer`, `GZipMessageSerializer`, `ZipMessageSerializer` (registered by default) | `application/base64`, `application/gzip`, `application/zip` | BCL |

  ```csharp
  .UnmarshalCsv<List<OrderRow>>(o => o.Delimiter = ";")     // per-node options
  .Marshal("application/gzip")                              // by registered content type
  context.AddCsvDataFormat();  builder.AddAvroDataFormat(); // registration for content-type addressing
  ```

  Each package registers with `context.Add<Format>DataFormat()` (or `builder.Add<Format>DataFormat()` in DI);
  the generic `context.AddDataFormat(serializer)` / `builder.AddDataFormat(serializer)` registers any
  `IMessageSerializer`. Malformed input fails with an error naming the format. Schema Registry
  integration is not part of the formats (framing only).
- **`Marshal` / `Unmarshal` by content type or serializer instance.** New DSL: `Marshal(string contentType)`,
  `Marshal(IMessageSerializer)`, `Unmarshal<T>(string contentType)`, `Unmarshal(string, Type)`,
  `Unmarshal<T>(IMessageSerializer)`, `Unmarshal(IMessageSerializer, Type)`. A content type that is not
  registered fails `Start()` (not the first message). `MarshalDefinition` / `UnmarshalDefinition` expose
  `ContentType` / `TargetType` for the XML form (`<marshal format="text/csv"/>`).
  `Unmarshal` now also accepts a `string` (UTF-8) or `Stream` body — text formats almost always arrive
  as strings; before, only `byte[]` was unmarshalled and any other body passed through silently.
- **`redb.Route.Templates` (new package): build a payload from a template** — the WSO2 PayloadFactory /
  Camel templating analog, on Scriban 7. A `string` is always a locator (file relative to
  `RouteTemplateOptions.BaseDirectory`, or `assembly:Name/Path/file.sbn`); inline text is
  `TextSource.Inline(...)`; `MediaType` (`Json` / `Xml` / `Text`) is mandatory.

  ```csharp
  .SetBodyTemplate("Templates/order-confirm.json.sbn", MediaType.Json, a => a
      .Set("customer", "header.customerId")        // route expression language, evaluated before the render
      .Set("total", "header.amount * header.qty")
      .SetValue("channel", "email"))
  .SetHeaderTemplate("X-Summary", TextSource.Inline("{{ body.items | array.size }} items"), MediaType.Text)
  ```

  - Compiled once at route build and cached by source: a missing file or a syntax error fails
    `Start()` with `Template 'Templates/x.sbn' (line,col): ...`.
  - Escaping of **substituted values only**, by media type (`"` `\` control characters for JSON;
    `& < > " '` for XML); `{{ v | raw }}` opts out for a pre-built fragment. Numbers and dates render
    culture-invariant (dates ISO 8601). Body gets `ContentType` from the media type.
  - Data model: `body` (JSON / XML text parsed into a navigable tree — `body.order.item[1].sku`; a POCO
    with its C# member names), `headers.name` / `headers["Content-Type"]`, `properties`, `exception`,
    `args`, `expr("header.a * 2")` (the same engine as `${...}`), `raw(...)`.
  - Sandbox: no `include`, no context / exchange objects, .NET objects expose public members only — a
    method call in a template fails the render. `SetPropertyTemplate` for properties.
  - Options: `services.AddRouteTemplates(o => ...)` or `context.UseTemplates(o => ...)` —
    `BaseDirectory`, `Liquid` (Scriban's Liquid-compatible syntax and filters), `StrictVariables`, `LoopLimit`.
  - XML form `<payload template="…" mediaType="json"><arg name="…" expr="…"/></payload>` (inline text in
    the element, `target="header:X"` / `property:X`) is recorded in the Route-XML node catalog.
- **`redb.Route.TestKit` (new package): test a route without its brokers, without changing the route.**
  Apache Camel `camel-test` parity — `AdviceWith`, a rich `MockEndpoint`, `NotifyBuilder` and one-line
  sends — with no test-framework dependency (assertions throw `MockAssertionException`).

  ```csharp
  await using var ctx = new RouteContext().AddRoutes(new OrdersRoutes());
  ctx.AdviceRoute("orders", a => a
      .ReplaceFrom("direct://test-in")            // instead of kafka://orders
      .MockEndpoints("kafka://*", "sql:*"));      // every matching To(...) goes to mock://kafka:...
  await ctx.Start();

  var vip = ctx.Mock("kafka://orders-vip").ExpectMessageCount(1).ExpectHeader("priority", "high");
  await ctx.SendBodyAndHeader("direct://test-in", order, "priority", "high");
  await vip.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
  ```

  - `AdviceRoute(routeId, ...)` / `AdviceAllRoutes(...)`: `ReplaceFrom`, `MockEndpoints` /
    `MockEndpointsAndSkip` (the original is never called), `WeaveById` / `WeaveByToUri` /
    `WeaveByType<T>()` with `.Replace / .Before / .After / .Remove`, `WeaveAddFirst`, `WeaveAddLast`.
    Advice rewrites the definition tree between `AddRoutes` and `Start()`; the tree walk is the core's
    `Outputs` + `IBranchingDefinition.Branches`, so a `To` inside `Choice`, `Split`, `Multicast`,
    `TryCatch` or a `CircuitBreaker` fallback is reached without per-type code.
  - `NotifyBuilder`: `ctx.Notify().FromRoute("orders").From("kafka://*").Filter("header.kind == 'vip'")
    .WhenReceived / WhenCompleted / WhenFailed / WhenDone(n).Create()` → `await matcher.MatchesAsync(timeout)`.
  - `ctx.SendBody`, `SendBodyAndHeader(s)`, `RequestBody<T>`, `RequestBodyAndHeaders<T>`, `ctx.Mock(uri)`
    (accepts the original URI or the `mock://` name; `MockUri.For("kafka://x?acks=all")` = `mock://kafka:x`).
  - URI masks everywhere: exact, trailing `*`, `regex:...`; query strings ignored (`UriMask` in the core).
- **Rich `MockEndpoint` in the core (Camel `MockEndpoint` parity).** Fluent, cumulative expectations:
  `ExpectMessageCount`, `ExpectMinimumMessageCount`, `ExpectBodies`, `ExpectBodiesInAnyOrder`,
  `ExpectHeader` (every message) / `ExpectHeaderReceived` (any), `ExpectProperty`, `Expect(lambda)`,
  `Expect(IPredicate)`, `Expect("header.x == 'y'")` (route language). `AssertIsSatisfiedAsync(timeout)`
  waits and fails with `mock://x Received message count. Expected: 1 but was: 0` plus every body
  received; `AssertIsNotSatisfiedAsync`, `IsSatisfiedAsync`. Scripted replies for `Enrich` /
  request-reply through `mock://`: `Whenever(n)` / `WheneverAny()` with `.SetBody / .SetHeader / .Delay /
  .Throw / .Do`. Expectations compare against the message *as it arrived* (a later step mutating the
  exchange cannot change what the mock saw); `ReceivedExchanges` still holds the live exchanges.
  The `expectedMessageCount` URI option counts as an expectation.
- **Step ids: `Id("...")` DSL verb (Camel `id()`).** Names the most recently added step
  (`ProcessorDefinition.StepId`) for `WeaveById` and diagnostics; before any step it names the route.
- **Exchange-level lifecycle events.** `IRouteLifecycleListener.OnExchangeReceived / OnExchangeCompleted /
  OnExchangeFailed` (default no-ops), published by the outermost route wrapper so "completed" and
  "failed" are the final outcome after every error handler; a handled exception counts as completed.
  Zero cost when no listener is registered.
- `ToDefinition.Uri` is public; `CircuitBreakerDefinition` implements `IBranchingDefinition` (its
  fallback body is now reached by the route validator and by AdviceWith).
- **Trusted reverse proxies on the shared Kestrel host (`redb.Route.Http.Hosting`).** A proxy chain is
  a property of the process, not of a route: two routes on one port never sit behind different proxies.
  So the list now lives on the host, next to TLS and the protocol set, and every consumer on that host
  gets the client's address and scheme without doing anything itself: Http, Soap, As2, Grpc, and the
  Tsak management API all read `Connection.RemoteIpAddress` and `Request.Scheme`, which the host
  rewrites before any of them runs.

  ```csharp
  services.AddRedbRouteHttpHosting(o => o.TrustedProxies.Add("10.0.0.5").Add("10.1.0.0/16"));
  ```

  `X-Forwarded-For` is walked from the right, past every listed proxy, to the first address that is not
  one: that is the client, whatever the length of the chain, because each proxy appends the peer it
  accepted from and a client's own entries always sit to the left of what the first trusted proxy
  wrote. A header from a peer that is not listed is ignored outright. An entry that does not parse
  stops the walk and keeps the socket peer; skipping it would carry the walk into the client-controlled
  part of the header. `X-Forwarded-Proto` is read in step, so `redbHttp.Url` carries the scheme the
  client used: behind a TLS-terminating proxy that is what a DPoP `htu` check or a SCIM `Location`
  header must compare against, and until now it compared against `http://`. The rewrite is off unless
  a proxy is listed, so a host with no configuration is byte-for-byte what it was.

  The walk is `ForwardedHeaderResolver`, a pure function on strings with no `HttpContext` in it: the
  ten lines that know about Kestrel are the adapter in `StartServer`. This is the same shape as
  `GrpcWire`, protocol logic apart from whoever holds the socket, and it is deliberate: the resolver
  and its tests survive a change of HTTP engine unchanged. Not handled, by decision rather than
  omission: the RFC 7239 `Forwarded` header, and `X-Forwarded-Host`, whose rewrite changes what
  redirects and absolute URLs point at and needs an allow-list of its own.

  Prompted by a reader of the 3.7 release notes who pointed out that a right-most-hop rule, which
  `redb.Tsak` used for its API-key throttle, names the *next proxy* rather than the client behind a
  chain such as Anti-DDoS to nginx, so every caller lands in one throttle bucket. `redb.Identity`
  already carried the correct walk in a per-route processor; this moves it to the layer that owns the
  socket so no product has to carry its own. Covered by 19 unit tests on the resolver and 9 end-to-end
  through a real Kestrel and `HttpConsumer`, five of which fail on the host without the resolver.

### Fixed
- **AMQP 1.0: producer `BytesOut` was permanently zero.** The counter guarded on
  `msg.Body is Data`, which is never true — AMQPNetLite's `Body` getter unwraps a `Data`
  section to its `byte[]`, and the producer's own message builder emits an `AmqpValue` anyway.
  Payload size is now read from the body *section* (Data, byte[] and string values).
- **AMQP 1.0: a typo in `expiryPolicy` failed silently into `session-end`** — on a durable
  subscription that meant the broker dropped it between restarts. Unknown values now throw at
  endpoint creation, naming the option and the valid values.
- **RabbitMQ / AMQP: a settle failure after a successful pipeline is a recorded error now.**
  An ack/accept that failed once the pipeline had succeeded was invisible to both the core
  (Process already returned) and the connector (its catch only logged) — the message would be
  redelivered while `Errors` stayed at zero.
- **IBM MQ `sslKeyResetCount` reaches every connection path.** It was wired into a factory
  method nothing calls at runtime; now the MQ-classes property builder that producers and poll
  consumers actually use sets `MQC.SSL_RESET_COUNT_PROPERTY`, and the XMS reply receiver sets
  `WMQ_SSL_KEY_RESETCOUNT` like the consumer engine already did.
- **IBM MQ `targetClient=Mq` covers the reply leg.** The RPC reply used to attach RFH2/message
  properties even in Mq mode — the exact thing the option exists to prevent for legacy
  requesters. The classic consumer now skips the RFH2 copy and the XMS consumer stamps
  `WMQ_TARGET_CLIENT=MQ` on the reply destination.
- **POP3 `fetchBody=false` on a server without `TOP`** (the command is optional per RFC 1939)
  no longer degenerates into an endless generic retry warning: the consumer logs once that the
  server lacks TOP and falls back to full fetch.
- **Mail: the body type is predictable from `HasAttachments` again.** A mail whose attachments
  all failed to decode into the carried list (message/rfc822 parts, everything over
  `maxAttachmentSize`) used to hand the route a bare string while `HasAttachments` said true —
  an unannounced contract change for routes that cast to `MailMessageBody`. With
  `fetchAttachments` on, an attachment-bearing mail always yields a `MailMessageBody`; the size
  cap drops payloads, not the shape. `MailMessageBody`/`MailAttachment` are also deep-cloneable
  now, so a mail body can travel through a `.Replayable()` checkpoint snapshot.
- **S3/IBM MQ docs:** the S3 README's Aggregate replacement recipe now compiles (it named
  parameters that do not exist), and the leftover option tables for the removed
  Streaming Upload block and the removed `deadLetterQueue`/`maxRedeliveries` pair are gone.
- **SFTP: the `Windows` separator value is gone** (it shipped in this release cycle and never
  worked: the jail check, directory creation, and recursive listing all speak `/` — which is
  the SFTP protocol's separator; servers on Windows accept `/` too, so a backslash mode could
  never be honest, the same protocol-fact reasoning that removed `binary` and `stepWise`).
  `separator` keeps its two meaningful values: `Auto` (default; normalizes backslashes in
  configured paths and file names to `/`) and `Unix` (byte-for-byte). A URI that still says
  `separator=windows` falls back to `Auto`. The consumer's vanished-subdirectory guard is now a
  real TOCTOU guard: it swallows `SftpPathNotFoundException` only when the subdirectory is
  actually gone — a path built wrong escapes loudly instead of becoming a silent per-poll skip.
- **Kafka: commit-on-revoke could lose a consumed-but-unprocessed record.** The revoked-partitions
  handler committed the consumer's *position*, which advances on `Consume` — not on processing.
  Rebalance callbacks run inside `Consume`, so a rebalance during batch collection committed
  records the pipeline never saw; the new owner started past them. The handler no longer
  commits: every processed record is already settled inline (per message, or per partition at
  batch end), and anything consumed-but-unprocessed now correctly replays (at-least-once).
- **Kafka `seekTo` is now a per-partition promise.** The one-shot flag burned on the first
  assignment callback — which can be empty (more consumers than partitions), and under
  `CooperativeSticky` partitions arrive incrementally across several callbacks, so only the
  first increment was seeked. Each partition now seeks exactly once, on its first assignment to
  this consumer, and never again on a later rebalance (proved on a live cluster: an empty first
  assignment no longer swallows the seek, a three-partition topic seeks all three).
- **Kafka: one fatal client error counted as 2–3 endpoint errors.** The librdkafka error
  callback, the batch consume catch, and the poll loop each recorded the same event. The poll
  loop is the single owner now; the other two log only.
- **Kafka: factory credentials and `GroupId` actually work as defaults.** `Validate()` and the
  consumer's groupId check see only the endpoint options, but the component copied nothing from
  the named factory except brokers — so "credentials via a named connectionFactory" (the error
  text's own advice) always threw, and the factory's documented default `GroupId` was
  unreachable. `SaslUsername`/`SaslPassword`/`GroupId` now land in the options before
  validation when the URI supplied nothing; the URI still wins.
- **Statistics ownership, part two: the audit now covers every connector and every send path.**
  The arc review found the "recorded exactly once, by the core" contract still leaking on side
  paths; all of them are closed:
  - Six more connectors stripped of self-recording: AzureServiceBus (MessagesIn and the doubled
    pipeline error, in both consumers), SQS (consumer MessagesIn, pipeline error, producer
    MessagesOut), SNS, TCP, LDAP (seven per-operation MessagesOut), Telegram. The LLM scheduled
    consumer no longer records MessagesOut/ProcessingTime per tick and counts only ticks that
    die before the exchange reaches the pipeline.
  - The sending EIPs — RecipientList, Enrich/PollEnrich, DeadLetterChannel, RoutingSlip,
    DynamicRouter, and the dynamic `.To()` — now record MessagesOut/Errors/ProcessingTime on
    their target endpoint through the same core funnel as a routed `.To()` and a template send
    (`CountedSend`). They used to bypass statistics entirely, so after the connector strip their
    sends would have counted zero.
  - `StatisticsProcessor` (the consumer-side wrapper) no longer records MessagesOut on pipeline
    success. MessagesOut is a producer-side counter; on an in-memory bridge endpoint (direct:,
    seda:) the two roles are one object and the old double write made every bridged send count
    twice. A consumer endpoint's completed count is `MessagesIn - Errors`. On a failed bridged
    exchange, Errors still counts once per leg (send + pipeline) — exactly two.
  - gRPC consumer: a failure AFTER the pipeline succeeded (reply encoding/writing) is recorded
    again — the previous guard only kept pre-exchange failures, so a route that stably failed
    to write its replies looked healthy.
- **Secret redaction: three call sites bypassed the existing sanitizer.** The core already
  masks userinfo passwords and sensitive query values (`EndpointUri.Sanitize` + `[Sensitive]`),
  but `HttpProducer` logged the raw resolved URL on every failed request, `As2Producer` built
  its logged name and its HTTP-failure exception text from the raw partner URL, and the shared
  transport-span helper put the raw `destination` URL into `messaging.destination.name` for
  every connector's telemetry. All four now go through `Sanitize` (format-preserving: plain
  queue/topic names pass through byte-for-byte).
- **HTTP `preserveHostHeader` no longer depends on `bridgeHeaders`.** The explicit Host set
  was nested inside the header-bridge block, so `bridgeHeaders=false` silently disabled the
  option. They are independent, as in camel-http.
- **AS2 producer no longer bridges the `Host` header.** A `From(http)` → `To(as2)` route used
  to forward the inbound request's Host to the partner — the same accidental preserve-host
  proxy that was fixed for the HTTP producer. `Host` joined the non-bridged (hop-by-hop) set.
- **Statistics ownership: endpoint counters are recorded exactly once, by the core.** Routed
  pipelines were already counted by the core — `StatisticsProcessor` wraps every `From()`
  processor (MessagesIn, BytesIn, Errors; see the part-two entry above for the MessagesOut
  refinement), `ToProcessor` counts every routed
  `.To()` (MessagesOut, Errors, ProcessingTime) — while seven connectors (WebSocket, SignalR,
  SOAP, S3, Elasticsearch, Firebase, gRPC) and the LLM producer recorded the same numbers
  themselves. In a route that **double-counted every message**: the red-before e2e showed
  MessagesIn=2 for one WebSocket frame and MessagesOut=2 for one send. Meanwhile the
  `ProducerTemplate` bypassed statistics entirely, so a template send was invisible unless the
  connector self-recorded — the inconsistency that made self-recording look necessary.

  The rule now has one sentence: **the core counts the pipeline; a connector records only what
  the core cannot see.** The `ProducerTemplate` counts its sends exactly like a routed `.To()`
  (MessagesOut, Errors, ProcessingTime — red-before: a template send recorded nothing).
  Connectors keep transport-level recording that no wrapper can observe: poll-loop and
  listener failures, pre-pipeline wire errors (a gRPC request that failed before an exchange
  existed, malformed SOAP/MTOM framing, a failed storage download), post-processing failures,
  producer wire bytes in both directions, and producer-side inbound operations (an LLM turn
  arriving, a Firestore get/query result, a storage download) as `MessagesIn` — no core
  wrapper counts those for a producer.

  **Dashboards:** numbers on routed endpoints of the seven connectors drop to their true
  values (half, for the doubled ones); `MessagesOut` on LLM endpoints now comes from the
  route/template hop, not from inside the producer. Hand-built consumers and bare
  `producer.Process` calls outside any route or template record pipeline numbers nowhere —
  that is the ownership contract, pinned by `StatisticsOwnershipTests` in the core and the
  per-connector `*StatisticsOwnershipTests`.
- **The dead-options sweep, part B (docs/KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN.md): 25 options
  across 9 connectors either work now or are gone.** The F11 sweep covered connection factories;
  endpoint options had never been audited, and the test convention — `SeekTo_SetsParam`-style
  assertions that an option lands in the URI query — let declared-but-never-read options ship
  documented, with DSL verbs, doing nothing. Every fix here is red-before, e2e against the live
  container park where one exists.

  **Wired (now do what their docs always said):** Kafka `breakOnFirstError`, `topicIsPattern`
  (wave A2); Http `preserveHostHeader` — and the bridge no longer leaks the original `Host`
  into every proxied request unconditionally, which made every `From(http)→To(http)` route an
  accidental preserve-host proxy (**migration:** proxy routes relying on that leak now set
  `preserveHostHeader=true` explicitly); Ftp `transferType` (ASCII/Binary reach FluentFTP);
  Sftp `compression` (zlib@openssh.com preferred, none kept as fallback), `separator`
  (Auto/Unix/Windows shape remote paths, Auto normalizes backslashes), `directoryMustExist`
  (a subdirectory vanishing mid-listing is skipped by default, loud when true); Mail `fetchBody`
  (real envelope scanning — IMAP `GetHeaders`/POP3 `GetMessageHeaders`, the body never travels),
  `fetchAttachments` (metadata stays, payload does not), `maxAttachmentSize` (caps the decoded
  copy handed to the route; the MIME is already in memory, stated honestly in the doc),
  `mapMimeHeaders` (raw MIME headers under `redbMail.Mime.*`); IbmMq `targetClient`
  (`Mq` = raw MQMD+body, no message properties at all, so nothing materializes as MQRFH2 for a
  legacy app) and `sslKeyResetCount` (both the MQ-classes and the XMS path); Amqp
  `terminusTimeout` **and** `expiryPolicy` — the latter had a resolver nobody called — both ride
  the Source/Target terminus now, defaults matching AMQP 1.0 so existing configurations behave
  identically.

  **Removed (breaking, 4.0.0 bundle) — because they cannot mean anything or duplicate an
  implemented mechanism:** Sftp `binary` (SFTP has no text mode; the protocol moves raw bytes)
  and `stepWise` (SFTP has no change-directory operation — SSH_FXP requests carry paths, a
  client-side "cd" is bookkeeping, so the FTP notion does not map; directory creation already
  walks segment by segment); IbmMq `deadLetterQueue` + `maxRedeliveries` (a second, dead
  vocabulary for poison handling next to the implemented native `backoutThreshold`/
  `backoutQueue`); Kafka `transactionIdPrefix` (fed a setting deliberately never configured);
  and the **entire S3 Streaming Upload block** — `streamingUploadMode`, `batchMessageNumber`,
  `batchSize`, `bufferSize`, `streamingUploadTimeout`, `namingStrategy`, the
  `.StreamingUpload()` DSL verb and the `S3NamingStrategy` enum: six options and a README
  section promised a camel-style accumulate-and-flush mode of which not one line was
  implemented. Accumulate-then-write belongs to the route in this framework — the core
  `Aggregate(...)` EIP completes by count, size and timeout; the S3 README now shows that
  recipe. Old URIs carrying any removed parameter do not break: unknown parameters land in
  `UnmappedParameters` and are ignored.

  Two sweep lessons are recorded in the plan: a read *inside the options file itself*
  (a `Validate()` check, an uncalled resolver) masks a dead option from the scan — that is how
  three of S3's six and Amqp's `expiryPolicy` hid; and per-connector poison/batching vocabularies
  keep reappearing next to core EIPs that already do the job.
- **`redb.Route.Kafka`: the hardening sweep (docs/KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN.md,
  waves A1–A7; every fix red-before, e2e against a live 3-node KRaft cluster).**

  *A typo in an enum-valued option fails loud now (A1).* `Enum.TryParse` used to swallow the
  assignment, so `securityProtocol=SaslSSLx` — or the canonical Kafka spellings `SASL_SSL` and
  `SCRAM-SHA-256`, which TryParse does not know — silently yielded a **plaintext connection with
  no credentials**. Every enum option (`acks`, `isolationLevel`, `autoOffsetReset`,
  `compressionType`, `partitionAssignmentStrategy`, `seekTo`) now parses strictly with the option
  name and valid values in the error; `_` / `-` separators are accepted, so both the C# and Kafka
  spelling families work. A password-carrying SASL mechanism without credentials is refused
  (Gssapi/OAuthBearer exempt). Same fix in `redb.Route.Redis` for `sslProtocols`. **Migration:**
  configurations that only worked because a typo fell back to a default now fail at startup —
  that is the point.

  *A message whose processing throws is no longer lost (A2).* The escaping exception used to
  reach the poll loop and the next successful commit covered the failed record's offset — a Kafka
  commit is a position, not a per-record mark. Default: move on explicitly (logged, counted;
  Camel's default too). `breakOnFirstError=true` — the option existed in URI/DSL/README and was
  read by nothing — now implements Camel semantics: seek back to the failed record and retry.
  `topicIsPattern=true` (also dead) now supplies the `^` prefix librdkafka's regex subscription
  keys on; combined with `partitionNumber` it is refused.

  *`transacted` no longer promises exactly-once (A3, breaking).* The honest story was always in
  docs/KAFKA_TRANSACTIONS_TODO.md (idempotent producer + deferred send, at-least-once, no
  `transactional.id`); README and the IntelliSense docs said "exactly-once semantics". They agree
  now, and `TransactionIdPrefix` + `Transacted(idPrefix)` are gone — the value fed a setting that
  is deliberately never configured, so it was silently discarded. A URI `transactionIdPrefix=` now
  lands in UnmappedParameters and is ignored.

  *`seekTo` works for group consumers (A4).* It read `consumer.Assignment` right after
  `Subscribe()`, when the group has not joined and the assignment is empty — a silent no-op
  everywhere except with `partitionNumber`. The seek now rides the partitions-assigned handler,
  once, on the first assignment; a later rebalance resumes from committed offsets (pinned by an
  e2e test that adds a second group member).

  *Statistics without double-counting (A5).* Discovery (the plan’s principle 0): pipeline statistics for
  routed endpoints already come from the core — `StatisticsProcessor` wraps every `From()`
  processor, `ToProcessor` counts for every `.To()`. The connector now records only what the core
  cannot see: transport-level poll errors and the producer's wire `BytesOut`. Flagged for the
  owner: six connectors that self-record `MessagesIn`/`BytesIn` in consumers likely double-count
  in routed mode.

  *Factory vs URI resolves by the family rule (A6).* A parameter actually supplied in the URI
  wins; everything else keeps the factory value. Before: URI brokers honored, URI SASL/SSL
  silently swallowed, endpoint defaults (`autoOffsetReset=Latest`, `retries=3`) stomped explicit
  factory settings, URI `acks` ignored entirely. A multi-partition batch now commits the last
  offset of **every** partition it touched, not just the last record's (the revoke handler used
  to paper over it at clean shutdown; kill -9 replayed far more than needed). The revoke-commit
  catch ignores only `Local_NoOffset` and reports everything else. Topic metadata logging uses
  the factory's `BuildAdminConfig()` (it existed, with full security, and was called by nothing),
  prints one line instead of one per partition, and is skipped when Information is off.

  *Smaller (A7):* consumer receive spans root correctly when no `traceparent` arrives (the
  `StartActivity(parentContext: default)` gotcha — it inherits `Activity.Current`); batch
  exchanges restore `ContentType` like single-message ones; a fatal librdkafka error stops the
  poll loop with `LogCritical` instead of retrying forever; `ProcessedCount` is thread-safe;
  factory `SaslPassword`/`SslKeyPassword` are `[Sensitive]`; `sslEndpointIdentificationAlgorithm`
  is available on the endpoint URI, not only the factory.
- **`redb.Route.Tcp`: `localhost` in a consumer URI crashed the start, and the producer could not
  reach a self-signed server (red-before).** `IPAddress.Parse(_options.Host)` threw a bare
  `FormatException` on any name — and the producer side of the same connector resolves names
  happily, so one URI worked as a client and crashed as a server. The host is resolved now (an IP,
  `0.0.0.0`, `localhost`, or a DNS name), with an error naming the host when it resolves to
  nothing. A `TcpListener` binds one address, so `localhost` means the IPv4 loopback here — stated
  in the README, unlike the shared HTTP host, which binds both because Kestrel opens two listeners
  for it. Separately, `TcpProducer` had no way to accept a self-signed certificate at all
  (`AuthenticateAsClientAsync(host)` with no callback), which made a staging server unreachable by
  construction; the explicit `trustAllCertificates` now present on ws, SignalR and S3 is available
  here too, on the endpoint, on `TcpConnectionFactory` and as a DSL verb. The producer also gained
  `clientCertPath`/`clientCertPassword` (`.ClientCert(path, password)`), so a server that requires
  mTLS is reachable at all — the naming follows the gRPC connector, and the certificate is loaded
  once at start rather than per connection. The consumer still does not *request* client
  certificates (`AuthenticateAsServerAsync` with the server certificate alone), which is a separate
  feature rather than a defect.
- **`redb.Route.Tcp`: a TLS listener without a certificate now refuses to start (red-before).**
  It used to bind and accept, then kill every accepted connection deep in the accept loop with an
  `ArgumentNullException` on a null-forgiven `SslCertPath!` — a type its catch list
  (`OperationCanceledException`, `IOException`, `SocketException`) does not even cover — while the
  port sat there looking alive. No plaintext ever flowed (the stream becomes an `SslStream` only
  after a successful handshake, and the read loop is inside the same `try`), so this was a failure
  mode and diagnostics defect rather than the AS2-class hole; it is now aligned with the shared
  host all the same. The certificate is also **loaded once at start** instead of being re-read from
  disk on every accepted connection — a file read and a PKCS12 parse leave the hot accept path,
  connections stop disagreeing about which certificate is current when the file is replaced under a
  running server, and the `X509Certificate2` (previously created per connection and never disposed)
  is released on Stop. A bad path or a wrong password is a start-time error too. The check lives at
  consumer start rather than in `Validate()` on purpose: `Ssl` is shared with the producer, which
  legitimately needs no local certificate.
- **`HttpProducer` printed the URI password into the log.** `ProducerName` built the
  "producer started" line from a raw `BuildProducerUrl()`, so `http://user:pass@host` carried its
  password there. Now redacted through `EndpointUri.Sanitize`, as the WebSocket and SignalR
  producers already are. Worth noting which half of the machinery applies: `[Sensitive]` marks
  option properties and covers secrets travelling as query parameters, while a userinfo password
  sits in the authority where no attribute reaches it — `Sanitize` handles both.
- **TLS without a certificate no longer opens a plaintext port anywhere (F14 addendum,
  red-before).** `SharedHttpServerManager` decided TLS with `entry.Ssl && certPath != null`, and the
  `else` branch opened an unencrypted socket while `GetBaseUrl` and every log line reported
  `https://`. That is not "TLS off" — it is a silent downgrade an operator cannot see, and every
  comparable stack refuses to start instead: nginx (`no "ssl_certificate" is defined`), httpd,
  Jetty, Spring Boot, and Kestrel's own `UseHttps()`. The host now does the same.

  **`redb.Route.As2` was the live victim.** Its consumer passed `useTls` to the shared host and no
  certificate with it — the connector had no server-certificate option at all — so an `as2s://`
  receiver listened in plaintext while `As2Endpoint` advertised `https://` to the trading partner as
  its `PartnerUrl`. Fixed with `sslCertPath`/`sslCertPassword` on `As2EndpointOptions` and on
  `As2ConnectionFactory` (where the password belongs), a `Tls(...)` verb in the DSL, and the same
  wiring in the async-MDN receiver. Both the message receiver and the MDN receiver now hand the
  certificate to the listener; without one they refuse to bind.

  Where the certificate comes from is a separate question from whether it exists, so the host gained
  the missing link: `AddRedbRouteHttpHosting(o => o.Tls.DefaultCertificatePath = ...)` (or
  `o.Tls.DefaultCertificate`), resolved after the endpoint and its connection factory. This is the
  shape Camel gives global `SSLContextParameters` and Spring Boot gives SSL bundles — a certificate
  on the endpoint is an override, not a requirement. Nothing about it turns TLS on; `ssl` stays an
  explicit per-endpoint decision.

  Consequently the eager per-connector checks are gone: `HttpEndpointOptions.Validate()` and
  `GrpcEndpointOptions.Validate()` no longer reject `Ssl` without `SslCertPath` (they could not see
  the factory or the host default), and the equivalent checks added to the WebSocket and SignalR
  consumers earlier in F14 are removed. One invariant, enforced once, at the bind, by the only place
  that sees all three sources. A route that used to fail at build time now fails at start with a
  message naming the listener and every place a certificate could come from.
- **A hub is an occupant of the shared listener, not a guest of it (F14 wave 7, review of the
  phase itself — `docs/V4/REVIEW-WS-SIGNALR-DONE.md`).** Three defects that the migration to the
  shared host introduced, all red-before: (1) `StopIfEmpty` counted only routes, and a hub
  registers none, so stopping the last HTTP route — or the first of two hubs — closed the socket
  under a hub that was still serving; (2) the hub's registration was one-shot per endpoint, so a
  consumer that stopped could not start again (`No routes registered for host:port`), a regression
  against the old behaviour where every start raised its own `WebApplication`; (3) hub dispatch
  was keyed on the request path alone, so one component serving `/hub` on two ports answered both
  from whichever consumer registered last. `RegisterServerConfigurator` returns a handle now,
  `UnregisterServerConfigurator` gives it back, `StopIfEmpty` waits for routes and configurators
  both, and the dispatch key carries the listener's port. Also from the same review: `localhost`
  goes through Kestrel's `ListenLocalhost`, which binds both loopbacks, rather than being mapped
  to `127.0.0.1` — a client that resolved it to `::1` would not have connected.
- **`messagePack=true` registered the JSON protocol (F14 wave 4).** The implementation was
  `if (options.MessagePack) signalRBuilder.AddJsonProtocol()` — the option turned on JSON under a
  MessagePack name, and the protocol package was not even referenced, so a client that negotiated
  MessagePack could not connect while three places in the documentation promised it worked. Both
  protocols are registered now (JSON stays the base, so existing clients are unaffected), with an
  e2e test that connects a real `AddMessagePackProtocol()` client. Pulling the package also pulled
  vulnerable transitive `MessagePack 2.5.187` (NU1903), so `MessagePack 3.1.8` is pinned
  explicitly and the protocol package is `10.0.11` — `10.0.3` itself carries GHSA-f8h2-vmm9-qhj6.
- **`wss://` and `ssl=true` without a certificate opened a PLAINTEXT socket (F14 wave 2, both
  connectors).** The consumers branched on `ssl && certPath != null` and fell through to
  `kestrel.Listen(...)` without HTTPS, while `BuildBaseUrl()` and the startup log reported
  `wss://` / `https://` — a configuration that should have refused to start instead opened an
  unencrypted port and announced it as secure. Both consumers now fail loud, naming the option to
  set. (The `Ssl` flag is the same for producer and consumer, so the check lives at consumer
  start rather than in `Validate()`, where it would break `wss://` clients.)
- **SignalR turned certificate validation OFF whenever `ssl` was not set (F14 wave 2).** The
  producer's `if (!_options.Ssl)` installed `DangerousAcceptAnyServerCertificateValidator`, on the
  theory that "TLS was not asked for, so there is nothing to check". But
  `HttpMessageHandlerFactory` applies to the whole SignalR transport, negotiate and redirects
  included, so an `http://` hub that answered with a redirect to `https://` was accepted with any
  certificate at all. Replaced by an explicit `trustAllCertificates=true`, the same shape S3 uses;
  the WebSocket producer, which previously could not disable validation at all and so could not
  reach a self-signed staging server, gained the same option. Both are covered against a live
  self-signed listener. (Along the way: SignalR needs `WebSocketConfiguration` as well as
  `HttpMessageHandlerFactory` — the WebSocket upgrade runs on its own `ClientWebSocket`.)
- **A server-mode producer could never find its consumer (F14 wave 6, both connectors).** The
  consumer registry was keyed on `EndpointUri.NormalizedKey`, which includes the sorted query
  parameters — and a server-mode producer carries at least `mode=server`, so its key never matched
  the consumer's. The key is the listener address plus the path now.
- **A SignalR hub with `ssl=true` served plain HTTP after the shared-host migration.** A hub maps
  itself through a configurator and never calls `RegisterRoute`, so its TLS settings had no way to
  reach the listener; `RegisterServerConfigurator` takes them.
- **`localhost` in a consumer URI crashed the shared host.** `IPAddress.Parse(entry.Host)` threw a
  bare `FormatException` for a name people write in consumer URIs constantly. The bind address is
  resolved now — `localhost`, an IP, or a DNS name — with an error that names the host when it
  resolves to nothing. Additive: what used to be a crash now works.
- **Endpoint statistics were not recorded by either connector, and producer names leaked URI
  passwords into the log.** Consumers record `MessagesIn`/`BytesIn`/`Errors`, producers record
  `MessagesOut`/`Errors`, so health and dashboards stop showing a live endpoint as idle; producer
  names go through `EndpointUri.Sanitize`, so `ws://user:pass@host` no longer prints its password
  in the "producer started" line. The WebSocket producer span also carries
  `messaging.destination.name`, which SignalR had and it did not; SignalR's `Stop` drains
  in-flight exchanges like the WebSocket one instead of relying on a 5-second host shutdown; and
  an unknown `encoding=` fails in `Validate()` with a message naming the value, instead of a bare
  framework `ArgumentException` from a constructor.
- **A typo in `connectionFactory=…` fails loud in every connector (F11 wave Zh, red-before
  on the reference set).** ~20 connectors resolved the named factory with a silent (or warning-level)
  fallback to URI parameters/defaults when the name was not in the registry — a misspelled
  name meant quietly connecting to the wrong broker with inline credentials. The new core
  helper `context.GetRequiredFromRegistry<T>(name)` throws with guidance instead, and all
  factory-resolve sites use it (Llm keeps its own lazy chain — it has no URI fallback to hide
  behind). Bonus: Kafka resolved the factory only AFTER `Validate()`, so a factory as the
  only source of `brokers` never worked — resolution moved before validation (the Telegram
  pattern). The old "falls back to URI parameters" unit specs were rewritten to expect the
  loud failure.
- **S3 connection options finally reach the AWS SDK; `S3ConnectionFactory.Build()` with a
  custom URL works at all (F11 wave S-B, red-before).** `SocketTimeout` (→ request
  `Timeout`; `ConnectionTimeout` now maps to `ConnectTimeout`), `RetryMode`
  (standard/adaptive) and `TrustAllCertificates` (custom `HttpClientFactory` for self-signed
  MinIO/Ceph) were declared, bound — and never applied. Bonus: in SDK v4 assigning
  `RegionEndpoint` and `ServiceURL` clears the other, so the factory's "set region, then null
  it for ServiceUrl" dance left BOTH empty — `Build()` with a MinIO URL always threw "No
  RegionEndpoint or ServiceURL configured". `ConditionalWrite` is implemented as a
  conditional PUT (`If-None-Match: *`, create-only semantics).
- **S3 consumer resource hygiene (F11 wave S-V).** Unsorted polls stream the listing page by
  page with an early exit at `MaxMessagesPerPoll` — no more O(N) memory per poll on big
  buckets (sorting still honestly buffers). The idempotent repository got double-buffer
  eviction at 10K entries instead of unbounded growth. Buffered downloads dispose the
  `GetObjectResponse` (the HTTP connection was leaked until GC), a streamed body's ownership
  is documented (the exchange disposes it), and `IncludeBody=false` now truly skips the
  content download instead of leaking a raw response stream into the body. A failed
  delete-after-copy in MoveAfterRead logs the duplication instead of failing the poll.
  `S3Producer` (890 lines) was split into three partial files per the "one type - up to ~400 lines" rule.
- **S3 consumer no longer loses objects (F11 wave S-A, all red-before against MinIO —
  findings S-1/S-2/S-4 of docs/V4/REVIEW-S3.md).** Three data-loss paths closed:
  (1) a raw processor throw was swallowed by the drain-guard and the object was still
  deleted/moved — with `DeleteAfterRead=true` being the DEFAULT, a failed exchange silently
  destroyed its object; post-processing now requires an actually successful exchange (no raw
  throw AND no unhandled `exchange.Exception`). (2) The idempotent mark was written at FILTER
  time — objects beyond `MaxMessagesPerPoll` were marked "seen" without a single processing
  attempt and never offered again, and a failed object was never retried; the mark is written
  only after success now. (3) Consumer exchanges were built without the endpoint's
  `ScopeFactory`, so per-exchange DI scopes silently never worked for S3 routes.
- **Firebase telemetry and statistics gaps (F11 wave V, red-before).** Storage spans carried
  `db.system=gcs` — GCS is not a database; object storage now uses the house attribute
  `redb.system` (`gcs`), aligned with the S3 sibling (`redb.system=s3`). FCM spans finally
  carry `messaging.destination.name` (topic/condition; a device token is a secret and goes in
  only as the literal `token`). The declared-but-never-read `FcmHeaders.ImageUrl` header now
  reaches the notification. Producer-Download records `BytesIn` like S3 does — a
  download-heavy endpoint no longer looks idle on the dashboard.
- **Firestore `Realtime=false` is an honest poll loop now (F11 wave G, red-before).** The
  option existed, `Validate()` policed `Delay`, DSL exposed `.Realtime(false)`/`.Delay(ms)` —
  and the consumer always started the snapshot listener anyway. A dedicated polling consumer
  runs the query every `Delay` ms (`InitialDelay` honored) and diffs results by
  `DocumentId → UpdateTime` into `Added`/`Modified`/`Removed` (removed docs carry a `null`
  body; the diff is in-memory, a restart re-delivers as `Added` — same as the listener's
  initial snapshot).
- **Firebase never occupies the process-global `[DEFAULT]` app (F11 wave G, red-before).**
  `FirebaseApp.Create(options)` claimed `[DEFAULT]`, so a host that initialized Firebase Admin
  itself collided with the connector, a second credential provider threw "already exists", and
  `Dispose` deleted an app the connector did not own. Apps are named per
  `(credentialPath, projectId)` now — several service accounts coexist in one process and only
  our own apps are deleted.
- **Missing Firestore project id fails loud (F11 wave G, red-before).** The provider silently
  fell back to a made-up `"default-project"`; now it throws with the four ways to configure it
  (`?projectId=…`, `FirebaseOptions.ProjectId` — which finally reaches Firestore/Storage, not
  just FCM — `DefaultProjectId`, `FIREBASE_PROJECT`).
- **Firebase Storage URI prefix is folder-like (F11 wave G, red-before).**
  `fbstorage://bucket/uploads` + `file.txt` produced `uploadsfile.txt`; the path after the
  bucket now always means the `uploads/` folder (a raw string prefix remains available via the
  `prefix` option).
- **Every connector's `AddRedbRouteX()` finally registers its components in a hosted context
  (F11 wave B, all 28 packages).** Registration hung on a lazy `I<X>ComponentRegistrar`
  singleton that no production code ever resolved — `AddRedbRoute()` + `AddRedbRouteKafka()`
  compiled, started, and then failed the first route with "No component registered for scheme".
  Real hosts survived only by calling `context.AddComponent(...)` by hand, and unit tests
  resolved the markers explicitly ("// trigger registrar"). All connectors now register through
  `IRouteContextConfigurator` — the hook `RouteHostedService` actually applies at startup
  (the pattern Telegram already used); the marker interfaces are gone. Acceptance tests added
  per connector: the documented two-line registration now genuinely resolves the scheme.
- **Firebase Storage consumer no longer loses objects whose processing failed (F11 wave A, all
  red-before against the live emulators).** `DeleteAfterRead`/`MoveAfterRead` fired unconditionally
  after the exchange — but the drain-guard swallows processing exceptions, so a failed object was
  deleted or moved into the success pocket, and with `Idempotent` it was also marked "seen" before
  processing and never retried. Post-processing now runs only when the exchange actually succeeded
  (no raw throw AND no unhandled `exchange.Exception`), the idempotent mark is written after
  success, and the new `MoveFailed` prefix quarantines poison objects instead of retrying forever.
- **Firebase `credentialPath` is honored by Firestore and Storage (F11 wave A).** The option was
  validated, documented and bound — and then ignored: `FirestoreDb.Create()`/`StorageClient.Create()`
  always used Application Default Credentials. Both clients are now built through the SDK builders
  with `CredentialsPath` and cached per `(projectId, credentialPath)`; emulator detection
  (`FIRESTORE_EMULATOR_HOST`, `STORAGE_EMULATOR_HOST`) finally works through the shipped provider —
  the docs promised auto-detection the SDK never performed, and `Validate()` accepted a
  `FIREBASE_STORAGE_EMULATOR_HOST` variable this SDK does not read (renamed to the real
  `STORAGE_EMULATOR_HOST`).
- **A dead Firestore listener is observed instead of silently ending the consumer (F11 wave A,
  red proven by reverting the monitor).** Nobody awaited `FirestoreChangeListener.ListenerTask`, so a
  permanent stream failure (revoked credentials, deleted project) left a zombie consumer with clean
  statistics. A monitor task now records the error on the endpoint and re-creates the subscription
  with exponential backoff (1s → 60s cap, reset after a stable minute).
- **Processors owning background resources are finally disposed with the context (review of the
  Route-XML F1 work, red-before).** Nothing had ever disposed compiled processors: the context's
  `DisposeAsync` released components only, so `ResequencerProcessor.DisposeAsync` was dead code
  since the day it was written, and the new aggregator inactivity-timeout timer (F1.3) outlived
  the context — a pending group was flushed into a stopped pipeline by a background timer that
  kept firing forever. `CompileNode` — the seam every compiled node flows through — now registers
  `IAsyncDisposable` processors, and disposing the context releases them before the components
  (a processor may still hold component resources). Deliberately not on `Stop()`: a stopped
  context can start again and reuses its compiled processors; between Stop and Dispose a late
  flush hits an unregistered `direct:` and lands on the exchange, the resequencer's established
  shape. Proven red-before: a pending group no longer flushes after `DisposeAsync`. The same
  review also fixed a vacuously green assertion in the phase's own tests (an un-awaited
  `ThrowAsync`); full findings in `docs/Route-XML/REVIEW-F1-2026-09-02.md`.
- **`redb.Route.Llm` storage: every business key is now guarded by the object unique key**
  (`docs/LLM/STORAGE_UNIQUE_KEYS_PLAN.md`). All redb-backed LLM stores used the same pre-V4
  check-then-save on `_objects.value_string` with no unique index anywhere, so two concurrent
  writers of one key both inserted and readers picked an arbitrary row. The worst case split a
  conversation in two: concurrent appends to a new conversation each minted their own root, and
  transcripts silently lost the other half. A concurrent first budget write split the cost
  accumulator across rows, undercounting the spend limit; prompt-template versions, knowledge
  chunks, cache entries, batch jobs, eval runs and approvals duplicated the same way. Every
  creation now also writes the key (normalized by the shared `RedbUniqueKey` helper, extracted
  from the idempotent-repository fix) into `_objects._value_unique`, and the loser of a race
  catches the typed `RedbUniqueViolationException`: conversation roots, budget rows, template
  versions and approvals resolve first-wins (the winner's row is adopted, never overwritten);
  cache entries, knowledge chunks, batch jobs, eval runs and tool-idempotency outputs re-apply
  onto the winner's row (last-writer-wins upserts); the bulk paths (`UpsertManyAsync`,
  `RegisterManyAsync`, `SaveManyAsync`) rebuild from a fresh lookup and retry once. Lookups stay
  on `value_string`, so rows created by older versions are still found and no backfill is needed.
  Along the way the conversation root's token counters moved under
  `ExecuteAtomicAsync` + `LockForUpdateAsync` (parallel appends lost increments), and the store
  doc-comments stop claiming `value_string` is indexed on MSSQL — NVARCHAR(MAX) is unindexable
  there and those lookups scan the scheme; the full-text index covers `_values._String` for
  `CONTAINS`, not equality. All thirteen race tests proven red on the un-fixed code in a worktree.

  An adversarial review of this work then found and fixed five defects of its own (each proven red
  on the pre-review code where testable): duplicate ids inside ONE bulk call self-collided on the
  new unique key and crashed the retry with a bare `ArgumentException`, losing the whole batch —
  the input is now deduplicated, last entry wins; the budget's `Reset`×`Add` interleave lost the
  restructured self-healing and threw away the delta — restored with a bounded retry loop; a raced
  batch re-register could drag a webhook-written terminal status back to "submitted" forever — a
  terminal status is never regressed now (the comment also lied: the webhook never registers, the
  real racer is a redelivered submit); the under-lock re-reads used `redb.LoadAsync`, which the
  props cache can answer with zero DB reads (`SkipHashValidationOnCacheCheck`), silently losing
  another node's increments — they re-read by query now, which always hits the row; and
  `RedbUniqueKey.Normalize` could split a surrogate pair at the 375-char cut, producing a key no
  provider can store. Core-side observations went to `docs/BUG_REDB_CORE_CACHE_AND_AMBIENT_TX.md`
  (the zero-DB cache shortcut vs `LockForUpdateAsync`, unique-violation recovery vs an enclosing
  PG transaction — `SaveByUniqueAsync`'s own retry included, shared cached Props instances).

  The core closed that report the same day (savepoints around saves under a caller's transaction,
  no zero-DB cache shortcut inside a transaction, the cache's dirty-snapshot guard pinned by a
  test), which lifts the last limitation of the barrier: the catch-and-recover paths of these
  stores — and of `RedbIdempotentRepository` — are now legal inside `.Transacted()` routes and any
  enclosing `ExecuteAtomicAsync`. The store-level contract is pinned by
  `ToolCache_SetRace_InsideCallerTransaction_RecoversAndTransactionSurvives`: the loser's violation
  fires inside the caller's transaction, recovery lands on the winner, and later writes in the same
  transaction still commit (before the core fix this died with PostgreSQL 25P02). The under-lock
  re-reads keep their query form as a guarantee independent of cache mode and core version.
- **An unhandled exception in a controller action no longer travels to the caller
  (`redb.Route.Controllers`).** A dispatcher answered 500 with the exception's own `Message` — text
  written by whoever threw it, routinely carrying a file path, a connection string or the name of an
  inner service, handed straight to the caller. It was written nowhere else, so the operator could not
  even see what the caller saw. The caller now gets a generic sentence with the exchange id to quote,
  and the exception goes to the log under the same id.

  The report named one dispatcher; the package has five, and **four** ran user action code with their
  own copy of the same line: `ControllerDispatcherProcessor`, `HttpControllerDispatcher`,
  `GrpcControllerDispatcher`, `SignalRControllerDispatcher`. All four now go through one
  `ControllerErrorReporting.Report(...)` — a copy is exactly how one of them stays wrong after the
  others are fixed.

  The dispatchers were not the whole of it either: the **transports** leaked the same text either side
  of them, and an adversarial review of the first fix found all three. `HttpConsumer` wrote
  `exchange.Exception.Message` as the 500 body for any exception escaping a route — with or without a
  controller. `SoapConsumer` put it in `faultstring` whenever the failure was not a deliberate
  `SoapFaultException` (a route that raises one still has its `FaultString` sent verbatim: that text is
  chosen, and WS-Trust clients branch on it). `GrpcWire.FromException` returned it as the status detail
  for anything it did not map, so it travelled in the `grpc-message` trailer. All three now emit the
  same generic sentence with the exchange id; statuses the framework itself chose — an `RpcException`
  the route raised, a protocol error, a deadline, a cancellation — keep their text.

  Framework-generated errors keep their text (400 for a missing header, 404 for no matching action):
  that text is ours and says nothing private, and it is now pinned by a test — without one, genericising
  those too would have passed every leak assertion while making the API harder to use. Both exception
  paths are covered — a sync action, whose exception arrives wrapped in `TargetInvocationException`, and
  an async one, whose faulted task rethrows the original — and the tests that pinned *which* exception
  is selected now read it from the log, where it went, instead of from the response, where it no longer
  is. Two dispatchers were still selecting it with `ex.InnerException ?? ex`, the pattern the other two
  warn against in comments: on the async path that logs a deeper transient wrapper (a `SocketException`
  inside a `DbException`) instead of the failure the action reported — harmless while the message also
  went to the caller, silent corruption of the only remaining record once it does not.

  The exchange id is the compensating control for withholding the detail, so it is pinned too: the
  caller's message carries it and the log line carries the same one. And it is found even when the host
  configures logging only through DI — `IRouteContext.GetService<T>()` reads the context's own service
  table, not the container, so a dispatcher that stopped there would silently have no logger and lose
  the exception entirely, which is worse than the disclosure it replaced. A filter that throws is logged
  as `IControllerActionFilter` documents, instead of vanishing into a bare `catch`.
  Reported as **BR-4** in `redb.Tsak/docs/BOUNDARIES_AND_FOLLOWUPS.md` §1.
- **A SOAP request the consumer could not read blamed the wrong party, and a decryption failure said
  why (`redb.Route.Soap`).** Three rejections that happen before an exchange exists — a malformed MTOM
  wrapper, a failed WS-Security decryption, a malformed envelope — all answered with the default fault
  code, `soap:Receiver` (`soap:Server` in 1.1). SOAP 1.2 defines that value as a failure "attributable to
  the processing of the message rather than to the contents of the message itself", which callers and
  retry handlers read as *retry*: the framework was telling a client to re-send bytes that can never
  succeed. All three now answer `soap:Sender` / `soap:Client`.

  How much to say about *why* is a separate question, and the answer differs per site. The two parse
  failures keep their text: `XmlException` reads "'<' is an unexpected token. Line 1, position 5." and
  `SoapMultipart` throws our own wording about the caller's own framing. Both describe the bytes the
  caller sent, not anything on this side, and they are the one clue that finds a BOM, a wrong encoding or
  a truncated stream — withholding them would buy no secrecy and cost the integrator the diagnosis.

  The decryption failure is the opposite case and now says nothing. `CryptographicException` describes
  *our* key and *our* configuration, and text that varies with the cause is a decryption oracle.
  WS-Security is explicit that a fault here "could be used as part of a denial of service or
  cryptographic attack", and defines exactly one code for every cause: `wsse:FailedCheck`, "the signature
  or decryption was invalid". The caller now gets that code and one fixed sentence carrying the request
  id to quote; the exception goes to the log, where the operator can read it and the attacker cannot. A
  test posts two genuinely different failures — a key we do not hold, and ciphertext tampered with after
  encryption — and requires the two answers to be identical once the request id is masked.

  No opt-in switch was added, and that is deliberate. CXF (`exceptionMessageCauseEnabled`, off "due to
  security consideration") and WCF (`IncludeExceptionDetailInFaults`, off, "recommended only as a way to
  temporarily debug a service application") both gate the *service operation's* exception, which is the
  path already closed by the entry above. What is withheld here is the reason a decryption failed, and
  that is precisely the thing that must not be switchable back on: an option nobody may safely enable is
  a trap, not an option.

  Left open on purpose: SOAP 1.2 puts application-specific codes in `Subcode` beneath one of its five
  `Value`s, while `SoapEnvelope.BuildFault` writes any code straight into `Value`. That predates this
  change and applies equally to the codes a route picks through `SoapFaultException`
  (`wst:FailedAuthentication` and the like), so it is a decision of its own rather than a wire-format
  change smuggled in here.
- **A route span carried no route id (`redb.Route`).** `InstrumentedProcessor` reads `exchange.RouteId`
  when it opens the span, but the id is stamped by the first step of the route pipeline — which that
  span already wraps — so `redb.route.id` was empty on every route-level span, on both the success and
  the failure path. The span name carried the route, the tag did not, and the tag is what a backend
  filters and groups by. It is now set once the id is known, including in the failure branch, where an
  unattributable span is exactly the one an operator is looking for. Found by a positive control in the
  secret-leak regression: the assertion "spans do mention this route" failed, which the previous
  negative-only assertions could never have shown.
- **`RedbIdempotentRepository` did not survive a race on one key**
  (`docs/BUG_IDEMPOTENT_REPOSITORY_UNIQUE_RACE.md`). The check-then-save on `_objects._name` had a
  TOCTOU window: two concurrent `Add` calls for one message both saw "new" and both inserted — the
  guarantee broke exactly in the case the repository exists for (and with redb.Identity's guard index
  deployed, the loser crashed on a raw driver error instead). An entry now also writes its composite
  identity into `ValueUnique` (`_objects._value_unique`, guarded by the core's per-scheme unique index
  `UIX__objects__scheme_unique` on PostgreSQL, MSSQL and SQLite); the loser's typed
  `RedbUniqueViolationException` is treated as "already processed" — first wins, at-most-once holds
  across threads and cluster nodes. Lookups stay on `_name`, so entries created by older versions are
  still found and no backfill is needed. A composite longer than 440 characters (client-supplied keys)
  is normalized to a readable prefix plus a SHA-256 tail — this also fixes long keys overflowing
  MSSQL's `_name nvarchar(450)`, which failed outright before. The repository's higher deadlock-retry
  budget (5 retries / 200 ms base), declared and documented since the constants were introduced, is
  now actually passed to `DeadlockRetryHelper` — the calls silently used the 3 / 50 ms defaults.
- **Code review of the 4.0 work, first batch (`docs/V4/REVIEW-CODE-2026-09-01.md`).**
  - `OnCompletion` released its copy with `DisposeAsync`, and the copy shares its body with the live
    exchange (`Message.Clone` copies the reference): a `Stream` body was closed under the consumer,
    synchronously when a `When` condition was false. The copy now releases only its DI scopes
    (`ReleaseScopes`, as the Multicast / Splitter clones do).
  - The after-consumer `OnCompletion` block ran on a bare `Task.Run`: the caller's ambient transaction
    flowed into it and completed underneath it, and its telemetry hung off a stopped span. It now runs
    in a detached frame like WireTap (`DetachedDispatch`: transaction suppressed, span re-rooted and
    linked), and the copy no longer shares the route's deferred transport actions.
  - A `When` condition of `OnCompletion` that threw turned a successful exchange into a failed one and,
    on the failure path, replaced the route's own exception. The condition is evaluated inside the
    block's own guard: logged and skipped, the route's result untouched.
  - `InterceptSendToEndpoint` published the raw target URI in `redb.toEndpoint` / `CamelToEndpoint`,
    credentials included; the headers now carry `EndpointUri.Sanitize(uri)`. Matching still uses the raw URI.
    (One site of **BR-3** in `redb.Tsak/docs/BOUNDARIES_AND_FOLLOWUPS.md` §1 — a secret leaving through a
    surface that is not a log line. The rest of that report is covered by the regression below.)
  - `jpath` over a `Stream` body consumed the stream: a second expression on the same exchange parsed
    nothing. A seekable stream is read from its position and restored; a non-seekable one is consumed.
  - `RemoveProperties` with a mask that matched them removed the exchange's own `__redb_scope:*` DI-scope
    properties, so `ReleaseScopes` found nothing to dispose. Those keys are never removed.
  - `InterceptFrom("  ")` is refused at declaration with a message naming the verb; before, the blank
    mask failed inside route compilation and, with `ThrowOnCompilationError=false`, dropped the route silently.
    A misplaced `InterceptFrom` / `InterceptSendToEndpoint` is reported under its own name, not as `Intercept()`.
  - `redb.Route.Cache`: a host `IMemoryCache` registered with a `SizeLimit` threw on every miss because
    entries carried no `Size` (only the package-created cache was sized); every entry is sized now.
    A hit restores the result where the miss left it: when the inner steps replied in `Out` (an HTTP
    enrich), a hit sets `Out` too, so a consumer that answers only on `Out` answers on a hit. Duration
    strings reject a bare number (`ttl=5` used to mean five days) and non-positive values, at parse time.
  - `redb.Route.Templates`: the template model no longer exposes `__*` exchange properties (the live
    DI scopes under `__redb_scope:*`), the same rule `redb.Route.JsonTransform` already applied.
- **Code review of the 4.0 work, second batch.**
  - `Marshal("application/gzip" | "application/zip" | "application/base64")` on a `byte[]` body did
    nothing: `MarshalProcessor` passed every `byte[]` through as "already marshalled", which is right for
    an object-model format and wrong for a byte wrapper whose input *is* bytes. `IMessageSerializer`
    gained `WrapsBytes` (default `false`; the three wrappers return `true`), and `Marshal` only skips a
    `byte[]` body for a format that does not wrap bytes. `.Marshal("application/json").Marshal("application/gzip")`
    now compresses.
  - GZip and Zip decompressed without a bound: a small compressed message could expand to gigabytes.
    Both formats take `maxDecodedBytes` (default `BinaryWrapperSerializer.DefaultMaxDecodedBytes`, 128 MB)
    and refuse a body that grows past it with an `InvalidOperationException` naming the limit; register
    your own instance to raise it. Zip also skips directory entries and refuses a multi-entry archive
    instead of silently reading whichever entry came first.
  - `AggregationStrategies.GroupedBody()` appended to any `List<object?>` it found in the accumulator.
    Bodies are cloned by reference, so an incoming list body was shared by the caller and every branch
    clone: the strategy mutated the caller's list and, when a branch kept it as its body, added the list
    to itself. The strategy now appends only to a list it created (a private subtype of `List<object?>`).
  - The compiled-template cache of `redb.Route.Templates` and the specification cache of
    `redb.Route.JsonTransform` are process-wide and were keyed by the relative file name: two contexts
    with different `BaseDirectory` shared one template, and an edited file was not picked up until the
    process restarted. `TextSource.ResolveCacheKey(baseDirectory)` keys a file by its resolved path,
    size and last write time, so a different directory is a different template and an edited file
    compiles afresh on the next route build.
- **Code review of the 4.0 work, third batch.**
  - `redb.Route.Templates` escaped a value every time Scriban turned it into a string, not once when
    it was written: `{{ headers.name + '!' }}` with `O"Brien` produced `O\"Brien!` with a literal
    backslash inside the JSON string, and `A&B` through an operator became `A&amp;amp;B` in XML. The
    escaping now sits on `TemplateContext.Write`, the single point where a value reaches the result,
    so operators, filters, `array.join` and object rendering are escaped exactly once; in-template
    conversions stay culture-invariant. XML output also replaces characters XML 1.0 cannot carry
    (control characters other than tab / LF / CR, lone surrogates) with U+FFFD instead of producing
    an unparseable document.
  - The template sandbox could be left through `expr()` and through arguments: both evaluate a
    route-language expression, and the expression engine invokes any public method it finds by
    reflection (`expr('body.Delete(path)')` ran). `ExpressionSandbox` (public, in the core) marks a
    flow in which the engine refuses reflection calls with `ExpressionSandboxViolationException`
    while built-in helpers (`contains`, `substring`, `length`, ...) keep working; the template engine
    opens it around argument evaluation and rendering. Every swallowing `catch` on the resolver path
    lets that exception through, so the render fails loudly instead of yielding an empty value.
  - `Multicast` disposed every branch clone after aggregation, including the clone whose body the
    strategy had just handed to the original exchange: `UseLatest()` with a `Stream` body delivered
    a closed stream, `GroupedExchange()` a list of exchanges with released scopes. Clones now release
    their DI scopes only, as Splitter, ScatterGather and RecipientList always did.
  - `redb.Route.Cache` stored and returned the body by reference. A `Stream` body is now buffered on
    the miss (the live message continues with the same bytes) so a hit replays it; a `JsonNode` body is
    copied into and out of the cache, so neither a later step on the miss nor a hit's mutation reaches
    the cached copy. Text and byte arrays are treated as immutable; any other object is still shared
    by every hit by design — do not mutate what a cache scope handed you, or clone it first.
- **Code review of the 4.0 work, fourth batch (core).**
  - Several intercepts ran in reverse declaration order, and a route's intercepts before the builder's.
    Now the first declared (builder-level ones first) is the outermost and runs first.
  - `InterceptSendToEndpoint` matched a static `To` against its unresolved URI: `To("{{orders.endpoint}}")`
    never matched `kafka://*`, so a dry run sent for real. The mask sees the resolved URI, the one the
    endpoint is created from; the published `redb.toEndpoint` is the resolved one too.
  - A dynamic target (`ToD`) was evaluated twice per message, once by the interception and once by the
    send; with a non-pure expression the published URI and the actual target could differ. The wrapper and
    the node share the node's resolver and the URI is evaluated once (parked on the exchange for the send
    that follows, cleared afterwards).
  - `UriMask` stripped the query string from both sides, so `kafka://orders?acks=all` matched `?acks=none`
    and a prefix `kafka://orders?acks=*` matched any query. A mask without a query ignores the candidate's
    query, as before; a mask that carries one matches only that query. Regular expressions are compiled
    once per pattern, case-insensitive and culture-invariant. `UriMask.Validate` reports a blank mask or an
    invalid `regex:` where the mask is declared (`Intercept*`, `RemoveHeaders`, `RemoveProperties`)
    instead of failing inside route compilation and, with `ThrowOnCompilationError=false`, dropping the route.
  - The lifecycle-listener list was a plain `List` enumerated on every exchange: registering a listener
    while exchanges flow (`NotifyBuilder` on a running context, a listener adding another from a callback)
    threw "Collection was modified" out of the notification and failed an unrelated in-flight message.
    Registration now replaces an immutable snapshot.
  - After an `AdviceRoute` / `AdviceAllRoutes`, a builder added later was never configured and its routes
    vanished without a word (`DefinitionsPrebuilt` short-circuited every builder). Builders remember whether
    they have been built; an unbuilt one is built at `Start()` regardless.
  - Intercept and `OnCompletion` bodies were invisible to the tree walk: the route validator did not
    validate them and `MockEndpoints` / `Weave*` did not reach a `To` inside them (a `To("kafka://audit")`
    in an `OnCompletion` hit the real broker in a test). `RouteDefinition` exposes them as
    `IBranchingDefinition.Branches`.
  - `OnCompletion` bodies compiled outside the route's compile frame: a `Replayable` checkpoint inside one
    failed `Start()` with "RegisterCheckpoint called outside route compilation", and the bodies got no
    message history. They compile inside the frame now.
  - `DynamicEndpointResolver` captured the first caller's `CancellationToken` into the cached producer
    start; when that first message was cancelled every later message for the URI awaited a cancelled
    task forever. A cancelled start is dropped from the cache and the next message retries.
  - `ProcessorDefinition.StepId` is no longer settable from outside the framework (a renamed step no
    longer matched its compiled processor).
- **Code review of the 4.0 work, fourth batch (packages).**
  - `redb.Route.Cache`: a miss is computed once per key at a time; concurrent exchanges for the same
    key wait for that result instead of each running the inner steps (a stampede on a hot key the moment
    it expires). Region and key are joined with a length-prefixed separator, so region `a` with key `b:c`
    and region `a:b` with key `c` are two entries and clearing a region never reaches a neighbour. A body
    type name read from a distributed cache is resolved against loaded assemblies only (never loads one
    from disk), and an entry whose type cannot be resolved counts as a miss instead of arriving as raw
    bytes. The list of keys written to a distributed cache forgets expired keys. A non-positive TTL or
    sliding window given in the DSL fails at route build; a key evaluated from a number or a date is
    written culture-invariant. The `cache:` producer's lazy start is atomic (two first messages could
    store an entry before the TTL was read).
  - `redb.Route.JsonTransform`: a `Stream` body is read from its position and left there with the
    stream open; a body that is not JSON fails with the specification's name; the result carries
    `Content-Type: application/json` in every branch (`Node` output and an undefined result included).
  - `redb.Route.DataFormats.Csv`: with a header record configured, `List<string[]>` no longer receives
    the header line as its first row; the header written for dictionary rows is the union of every
    row's keys in first-seen order, not the first row's keys.
- **Code review of the 4.0 work, fourth batch (core and HTTP, continued).**
  - The keyed throttle scanned every key's gate after every message (taking each gate's lock), so
    throughput fell with the number of live keys; eviction now runs at most once per period. A gate could
    also be evicted and disposed between a message's lookup and its use (`ObjectDisposedException` on a key
    idle for two periods); eviction and entry now decide under the gate's own lock, and a retired gate is
    never entered. `ThrottleProcessor` no longer touches its disposed token source when disposed under a
    message in flight.
  - `AggregationStrategies.Sum / Max / Min` took any numeric accumulator body for the running total, so
    over exchanges whose bodies happened to be numbers `Sum("header.amount")` summed the bodies. The
    running total is marked on the accumulator; a seeded first exchange goes through the expression.
  - `Unmarshal` of a positioned `MemoryStream` handed the whole buffer to the format, bytes before the
    position included; the shortcut applies only to a stream at its start.
  - `jpath` / `${jpath(...)}` parsed the path text on every message; the parsed expression is cached by
    path (the body is still parsed per evaluation).
  - REST DSL: two types with one short name (`V1.Order`, `V2.Order`) in one declaration shared one OpenAPI
    schema; components are named by short name, then namespace-qualified, then numbered (`x-clr-type`
    records the origin). Two inline `Route()` verbs whose ids sanitize alike (`/{id}` and `/id`) shared one
    `direct://` endpoint and the last handler silently won; the endpoint name carries a fingerprint of the
    route id. A client header named `query.<x>` arrived as if it were a query parameter; plain `query.*`
    headers are cleared before the real parameters are written.
  - `redb.Route.Http.Hosting`: the shared host's compiled route table was rebuilt only when the number of
    routes changed, so an unregister + register pair (a route restart, a REST redeploy) kept dispatching
    to the removed handler; every change invalidates the table.
  - `DataFormatRegistry` is thread-safe for registration while routes resolve serializers (a hot-loaded
    module adding a format).
- **A SOAP route can finally choose its fault code (`redb.Route.Soap`).** `SoapFaultException` carries a
  `FaultCode` and `SoapEnvelope.BuildFault` accepts one, but the consumer passed neither: every failure
  went out as `soap:Server`, and the reason text was the composed `"SOAP fault: <code> - <reason>"`
  rendering rather than the reason the route gave. A route could say what went wrong and not what kind
  of wrong it was, which leaves a caller only prose to branch on.

  The server half of that wiring simply had not been written: across the whole tree the exception was
  thrown only by `SoapProducer`, on the client side, when parsing an incoming fault. Both new branches
  are guarded (`chosen?.FaultCode`, `chosen?.FaultString ?? Message`), so for every other exception the
  response is byte-for-byte what it was.

  This matters wherever a client branches on the code: WS-Trust, for one, treats
  `wst:FailedAuthentication` and `wst:InvalidRequest` as different outcomes.
- **A comparison against a member behind a header or property now resolves in the expression
  engine (`redb.Route`).** `header.user.Age > 18`, `header.list.Count > 3`,
  `property.cfg.limit > 3` and `header.s.Length > 3` evaluated to false in every condition and
  template: the identifier resolver of the AST engine did a flat dictionary lookup of the whole
  dotted tail (`getHeader("user.Age")`), while the bare-value path had long resolved the same
  names through the smart resolvers (literal name first, then the member path). The AST engine
  now uses the same resolvers, so a member access means the same thing wherever it is written.

- **`Filter(Expr("header.amount>1000"))` no longer drops every message (`redb.Route`).** The
  string overload had been fixed earlier; the expression overload still read a string-born
  expression as a value and coerced it, so the unspaced comparison became a lookup of a header
  named `amount>1000`. An expression born from a string is now compiled as a condition in
  `Filter(IExpression)` and `When(IExpression)`: whitespace-insensitive, and malformed text fails
  while the route is built. Typed expressions (`HeaderExpression`, ...) keep the plain
  truthiness reading.

- **`${...}` inside `SetBodyExpression` / `SetHeaderExpression` / `SetPropertyExpression`
  understands unspaced comparisons and dashed header names (`redb.Route`).** `"${header.a>5}"`
  resolved to null and `"${header.Content-Type}"` to null on that path, while the same
  placeholders worked through `Expr(...)`; and `"${header.a + 1}"` worked on that path but
  rendered empty through `Expr(...)`. The two engines had complementary holes. Both now go through
  the one placeholder pipeline (see **Changed**).

- **Connector builders no longer compile a plain string as an expression (`redb.Route.File`,
  `Ftp`, `Sftp`, `Kafka`, `RabbitMQ`, `MqttNet`, `Redis`, `Http`).** Every string overload of the
  fluent DSLs — `Password`, `Host`, `RoutingKey`, `FileName`, `MoveTo`, `Brokers`, ... 66 methods —
  did `new StringExpression(value)` and immediately unwrapped it back to the original text, so the
  only lasting effect of the round trip was that the string went through the expression compiler.
  A password such as `secret(123` — `secret(` reads as a function call — threw
  `ExpressionCompilationException` while the route was being built. The string overloads now
  store the string; the `IExpression` overloads are unchanged, and a `${...}` value keeps being
  resolved per message by the endpoint options exactly as before. A string is a string, whatever
  it contains.

- **An explicit expression reads its operator whatever the spacing (`redb.Route`).**
  `SetHeader("big", Expr("header.amount>1000"))` used to yield null — the value dialect only saw
  an operator surrounded by single spaces, the last place in the language where whitespace carried
  meaning. Every position now agrees: `header.a>10`, `header.a > 10` and a tab- or
  newline-separated form are one expression in a condition, in a placeholder and in an explicit
  expression alike. Two consequences follow from "an expression is an expression": `Expr("a>b")`
  is a comparison (false), no longer the literal text; and an explicit expression that carries an
  operator but does not parse (`Expr("<xml>")`) fails while the route is built instead of
  yielding null on every message. A plain string is untouched — `SetHeader("k", "a>b")` stores
  `a>b` — and so is every connector option.

- **A decimal literal inside a function argument parses on every machine (`redb.Route`).**
  `max(2.5, 1)`, `round(x, 2)` with a decimal, `abs(-2.5)` compiled on an en-US machine and threw
  `ExpressionCompilationException` ("The input string '2.5' was not in a correct format") on a
  ru-RU one: the parser read numeric literals with the ambient culture. Source text is
  culture-invariant now — the meaning of a route cannot depend on the locale of the box it runs on.
  Pinned under en-US, ru-RU and de-DE.

- **`ThrottleExpression` throttles per message (`redb.Route`).** The template was evaluated once
  at route build against an empty exchange, and when that failed (a `${header.rate}` has no header
  to read at build time) the limit silently became `int.MaxValue`: the throttle was a no-op that
  looked configured. The limit is now computed on every message, so it can come from a header, a
  property or an expression and change between messages, as the Apache Camel throttler does with a
  dynamic expression. `Throttle(Func<IExchange, int>)` is a member of `IRouteDefinition`;
  `ThrottleExpression` gained an `IExpression` overload. A malformed template fails at `Start()`;
  a limit that evaluates to a non-positive number fails that exchange with
  `InvalidOperationException` rather than letting it through. `ThrottleProcessor` replaces its
  fixed-capacity semaphore with a sliding-window gate that serves waiters in arrival order; the
  fixed-limit form and `RejectOnOverflow` behave exactly as before.

- **A public field reads like a property through every root (`redb.Route`).** `header.x.Field`
  and `body.Field` returned null while `property.x.Field` returned the value: only one of the
  three member-resolution paths fell back to fields. Both now do, so `header.`, `property.` and
  `body.` resolve an object identically — properties, fields, nested objects, collections,
  indexers, dictionaries — in the value, condition and placeholder positions alike.

- **`min` and `max` work over a collection (`redb.Route`).** `min(property.nums)` and
  `max(header.list)` returned null while `sum` and `avg` over the same collection worked: the two
  were two-scalar-only. A single collection argument now folds over its numeric items; the
  two-scalar form is unchanged. Both evaluation modes share one implementation.

- **`contentType` resolves in the expression engine (`redb.Route`).** The removed hand-written
  branch knew the accessor; the unified engine now does too, so
  `Filter("contentType == 'application/json'")` works.

- **A string condition without spaces around its operator no longer discards every message
  (`redb.Route`).** `.Filter("header.amount>1000")` compiled to a lookup for a header literally named
  `amount>1000`. That header never exists, so the filter evaluated to false for every message — no
  exception, nothing in the log, all traffic silently dropped. The spaced form
  `header.amount > 1000` worked, which made the failure look like a data problem rather than a parse
  problem. The same applied to `When`.

  A condition is now compiled as a boolean expression, so whitespace between tokens carries no
  meaning: `header.a>10`, `header.a > 10`, `header.a  >  10` and tab- or newline-separated forms are
  one and the same condition. An operator inside a quoted literal stays part of the literal, so
  `header.name == 'a > b'` compares against the text `a > b`.

  Values are unaffected. `SetBody`, `SetHeader`, `Transform` and every connector option string keep
  their existing routing, so a literal stays a literal — prose such as `black and white`, base64
  padding such as `dGVzdC1zZWNyZXQ==`, a password containing `>` or an XML fragment are all
  untouched. That is deliberate: a condition and a value are different positions in the language.

- **A comparison inside `${...}` is now evaluated instead of rendering as an empty string
  (`redb.Route`).** `"${header.amount > 1000}"` produced `""`, and so did every comparison and
  word-logic form, because the placeholder was matched against the accessor branches first: a
  placeholder starting with `header.` was taken for a header whose name is `amount > 1000`. Such a
  header never exists, so the placeholder silently rendered as empty. Curiously, the same
  expression in brackets — `"${(header.amount > 1000)}"` — always worked, which is how the
  evaluator's presence was hidden.

  Comparisons and word logic now render their result: `"${header.a > 10}"` and `"${header.a>10}"`
  both give `True`. The template position therefore agrees with the condition position on every
  form of the test corpus.

  Arithmetic inside a placeholder followed once the template engine was unified (see the
  "one placeholder pipeline" entry under **Changed**): `"${header.a + header.b}"` renders `45`,
  and `"${header.Content-Type}"` keeps resolving the header, because the exact name is tried
  before the text is read as an expression.

- **A condition that cannot be compiled now fails while the route is being built.** Previously a
  malformed condition compiled to something meaningless and turned into a constant answer on live
  traffic. `.Filter("header.amount >")` and the like now throw `ExpressionCompilationException` from
  `RouteContext.Start()`, before a single message flows. An empty or whitespace-only condition throws
  `ArgumentException`.

- **`When` reached through the route-level alias now records its source.** `When(this
  IRouteDefinition, string)` set neither `SourcePredicate` nor `SourceExpression`; the nested
  `Filter(string, configure)` overload lost `SourceTemplate` the same way. Both now record what they
  were given.

- **A SOAP consumer could not actually serve TLS (`redb.Route.Soap`).** The consumer registered its
  listener with the TLS flag but never passed a certificate, and the fluent `Soap.Listen(...)` builder
  always emitted the `soap:` scheme, so there was no way to ask for TLS through the DSL at all. What
  reached Kestrel was "serve HTTPS, no certificate": on a developer's machine that quietly picks up the
  ASP.NET development certificate and looks like it works, and on a server it fails with an error that
  names neither the route nor TLS. Client certificates were not reachable either, so an mTLS-pinned SOAP
  endpoint could not be configured.

  The consumer now carries the same TLS surface the gRPC consumer has: `sslCertPath`,
  `sslCertPassword`, `clientCertificateMode` and `allowedClientThumbprints`. `SoapConnectionFactory`
  carries them too, so the certificate password lives in the registry rather than in the endpoint URI —
  the URI is the route key and travels through logs, telemetry and the dashboard as an ordinary string.
  A URI value wins where it says something, the factory fills the rest. `Soap.Listen(...).Ssl(certPath)`
  now emits `soaps:`, and `.ClientCertificate(mode, thumbprints)` sets the mTLS policy.

  Two configurations that cannot work are now refused when the consumer starts, rather than at the
  first handshake: TLS without a certificate, and a client-certificate policy on a plaintext listener.
  The second is the more dangerous of the two — it would leave an operator believing the endpoint is
  pinned to their partners while it is open to anyone.

  Existing routes are unaffected: a `soap:` consumer with no TLS options behaves exactly as before.


- **A SOAP route could not learn who was calling (`redb.Route.Soap`).** The consumer surfaced nothing
  from the connection: no caller address, no client certificate, nothing about the request beyond the
  envelope. Every IP-keyed protection a route might apply — rate limiting, brute-force lockout, auditing
  where a request came from — had nothing to key on. They did not fail; they saw no address and did
  nothing, which reads in a log as «no abuse» rather than as «not wired».

  The caller's address and source port now arrive as `redbSoap.remoteAddress` / `redbSoap.remotePort`,
  taken from the connection rather than from anything the caller sent, and a TLS client certificate
  arrives as `redbSoap.clientCert*`. `emitHttpCompatHeaders=true` (`.HttpCompatHeaders()` in the DSL)
  additionally mirrors the address, path and method into `redbHttp.*`, so processors written against the
  HTTP transport work unchanged behind a SOAP endpoint. That bridge is off by default and its shape
  matches the gRPC consumer's: the native headers always carry the facts, and writing into another
  transport's namespace stays a deliberate act.

- **A prefixed fault code was written without declaring its prefix (`redb.Route.Soap`).** A fault code is
  a QName in both SOAP versions, so `wst:FailedAuthentication` means nothing unless `wst` is bound on the
  envelope — and it was not. The result reads correctly to the eye and does not resolve in a client that
  treats it as the QName the specification says it is, which is precisely the kind of client that branches
  on fault codes rather than on prose.

  The prefix is now declared. The standard ones (`wst`, `wsse`, `wsu`, `wsa`, `wsrm`) are known, and
  `SoapFaultException` takes an optional namespace for a code in a namespace of the caller's own. A prefix
  that is neither known nor supplied is still written as it was handed over: inventing a namespace for it
  would be worse than leaving it unresolved.

  Envelopes with the default code are unchanged — `soap` is already bound, and nothing is added for it.

### Added
- **`IMessageValidator.ValidateAsync` (`redb.Route`).** A default interface method that forwards to
  `Validate`; `ValidateProcessor` awaits it, so a validator that has to consult a store or a service
  can override it and no longer blocks. `PredicateValidator` holds an `IPredicate` (delegate
  constructors kept) and awaits `MatchesAsync` — `Validate(predicate)` now behaves like `Filter`,
  `When` and `Loop` for an asynchronous predicate.

- **`Filter(IPredicate)`, `Loop(IPredicate, ...)`, `Validate(IPredicate, ...)` are members of
  `IRouteDefinition`; `When(IPredicate)` is a member of `ChoiceDefinition` and `WhenDefinition`
  (`redb.Route`).** They used to be extension methods that unwrapped the predicate into its
  synchronous `Matches` at the boundary. The predicate is now stored as the condition of the
  scope — `FilterDefinition`, `WhenDefinition`, `LoopDefinition` hold an `IPredicate`, a plain
  delegate is wrapped in a `LambdaPredicate` — and the processors (`FilterProcessor`,
  `ChoiceProcessor`, `LoopProcessor`) **await `IPredicate.MatchesAsync`**. Until now nothing in
  the engine called the asynchronous half of the contract: a predicate that consults a store or a
  service ran synchronously whatever it declared. `SourcePredicate` is the very instance that
  executes, not a copy kept beside it. The processors keep their delegate constructors and
  `WhenClause.Predicate` keeps its delegate shape, so existing code compiles; `WhenClause.Condition`
  exposes the stored predicate.

- **`.LoopWhile(condition)` — a loop driven by a condition (`redb.Route`).** `LoopExpression` reads
  its string as an iteration *count*; there was no string form of `Loop(Func<IExchange,bool>)` at all,
  so a "repeat while this holds" loop could not be written from a string. Two forms, with and without
  a nested configurator. The name is deliberately not `LoopExpression`: the two mean different things
  and must not be confused.

- **`.Validate(condition)` — validation from a condition string (`redb.Route`).** `Validate`
  previously took a delegate or an `IMessageValidator`. The string overload uses the same condition
  compilation as `Filter` and `When`, and honours `errorMessage` and `throwOnFailure`.

- **`ExpressionResolver.IsTemplate(string)`.** Answers whether a string is a `${...}` template rather
  than a bare expression. The same regular expression was duplicated inside `StringExpression`.

- **`IConditionSource` — one contract for what a condition scope was built from (`redb.Route`).**
  `FilterDefinition` and `WhenDefinition` implement it, so an introspecting consumer matches on a
  single type instead of enumerating definition classes and hoping the property names line up. Three
  names, three fixed types, read-only: `SourcePredicate` (`IPredicate?`), `SourceExpression`
  (`IExpression?`), `SourceTemplate` (`string?`). A condition written as a delegate leaves all three
  null — a lambda has no source to capture.

- **`Choice().When(expression)` now records the expression it was given.** The `IExpression` was
  folded into a delegate and lost; `WhenDefinition.SourceExpression` now holds it, matching what
  `Filter(expression)` has always done.

- **`.Log()` — the rich-log scope now opens without a level argument.** The fluent scope
  (`.Log(level).Message().Header().Property().ShowRouteId().EndLog()`) previously required an explicit
  `LogLevel`; the parameterless overload opens it at `Information`, so the canonical form reads
  `.Log().Message("…").Header("correlationId").Property("traceId").EndLog()`. `.Log(level)` is unchanged.

- **Incoming attachments are reachable (`redb.Route.Telegram`).** `TelegramUpdateMapper` put only
  `msg.Text` into the body and nothing at all from attachments. A voice note therefore arrived with
  an **empty body** and `telegram.messageType = Voice`: a route could see *that* a file had come and
  had no way to fetch it, because the `file_id` never reached the exchange.
  New consumer headers, present only when the message carries a file:
  `telegram.attachment.kind` / `.fileId` / `.mimeType` / `.fileSize` / `.duration` / `.fileName` /
  `.caption`. Covers voice, audio, video note, video, animation, document, sticker and photo — for
  photos the **largest** size of the ladder is reported, because downscaling is the caller's choice
  and upscaling is not available.
  Deliberately **not** the existing `telegram.fileId`: that one is a *producer* instruction ("send
  this already-hosted file"), and headers travel with the exchange. A route that consumed a voice
  note and answered with a document would have echoed the user's own recording back at them,
  silently, since both sides read one key.
  The body is untouched, and a caption stays a header: promoting it would make a captioned photo
  indistinguishable from a typed command downstream. Long polling and webhooks share the mapper, so
  both paths gained the headers at once.

### Tests
- **Nothing that leaves the process carries a secret — proved end to end (`redb.Route.Tests`).**
  `docs/SECURITY_URI_REDACTION_PLAN.md` asked for this regression and **BR-3**
  (`redb.Tsak/docs/BOUNDARIES_AND_FOLLOWUPS.md` §1) is the report behind it. A route whose consumer URI
  carries secrets in every shape — a `password=` query parameter, the connector-specific `bindPassword`
  and `sessionToken` from the leak audit, and a userinfo password — is compiled, started, sent a message
  and stopped, while log lines, span tags, metric labels, the `CompiledRoute` DTO the dashboard renders
  and the health-check payload are captured. No surface may contain any of them, and the route must
  still be recognisable (a blanket "log nothing" would pass a leak test and be useless in an incident).
  Each secret is unique per run, so the assertions cannot be spoiled — or falsely satisfied — by what
  other tests do in parallel. Verified to fail: dropping the sanitizer from a single site (the DTO) turns
  it red with all four secrets in cleartext.
  The core sites themselves were already redacted before this session; the regression is what makes that
  a guarantee rather than an observation, and it is what closes the redb.Route side of BR-3.
- **The telemetry tests no longer collect other tests' spans (`redb.Route.Tests`, `redb.Route.Tests.Http`).**
  An `ActivityListener` is process state: filtering by source name alone — the obvious spelling — made a
  test see the spans of every route any other test was running at that moment, so `TracedDslTests` and
  `HttpTelemetrySmokeTests` flaked under a full parallel run and were always green in isolation (six runs
  of the untouched tree produced two failures). Serialising by collection cannot help; the listener is not
  per collection. `RouteTelemetryProbe` keeps the source filter and adds the real discriminator: a
  predicate over the finished span, normally its `redb.route.id` tag, or `redb.route.endpoint` for a
  transport span with no route behind it. Both tags exist for production observability, so no test-only
  hook was added to the framework. Collection is also thread-safe now; `ActivityStopped` arrives on
  whatever thread finished the span and the spans were being appended to a plain `List`.
  Processor metrics keep their limitation, now stated where it bites: the counters carry no route tag, so
  a test may assert a lower bound and never "unchanged" (`docs/Route-XML/BOUNDARIES.md` §2.11).
  The same rule caught `ClearAllCaches_EmptiesAll`, which asserted an empty expression cache while other
  collections compile templates continuously; it now proves the clear per entry, by delegate identity of a
  template text unique to the test, which no parallel run can put back.
- **Characterisation net over the expression language (`redb.Route.Tests`).** One corpus of ~220
  forms — literals, accessors, member access including public fields, comparisons in every spacing,
  word logic, parentheses, arithmetic, prefix and postfix increments, functions, `min`/`max`,
  decimal literals, ternary, index access, whole-string placeholders, Apache Camel idioms, malformed
  input, and the configuration-shaped strings that must stay literal — is evaluated in every
  position a string can occupy (value,
  condition, template; the hand-written logical branch was a fourth column until its removal)
  and compared against a recorded snapshot. A failure names
  every line whose outcome moved, so a change to expression handling can no longer rearrange one
  position while quietly breaking another. The snapshot is generated under the invariant culture so
  it is the same on every machine.

  Putting the net in place immediately pinned several behaviours nothing had recorded before, among
  them that template interpolation formats numbers with the ambient culture — the same route emits
  `2.5` or `2,5` depending on where it runs. All of them are documented in
  `docs/Route-XML/BOUNDARIES.md`.

### Changed
- **One truthiness rule instead of three (`redb.Route`).** A value became a boolean by three
  different rules depending on where it was read: the DSL boundary (`Filter("${header.x}")`),
  the word-logic operators and `logical()` inside the engine, and the hand-written logical branch
  — and they disagreed on `0`, `"0"` and `"no"`. `Filter("${header.zero}")` passed a message
  while `Filter("logical(header.zero)")` dropped it. `RouteTruthiness` is now the single rule
  and every conversion site delegates to it: a bool is itself; a number is non-zero; a string is
  a boolean word (`true/false`, `1/0`, `yes/no`, `y/n`, `on/off`) and otherwise non-empty; null
  is false; any other object is true.

  **Behaviour changes to note:** a zero header or property is now false in a condition
  (it used to be true, being a non-null object); the strings `"0"`, `"no"`, `"off"` are false
  (they used to be true, being non-empty). Equality is deliberately *not* truthiness:
  `header.a == 'x'` with `a = 42` stays false — only bools, numbers and explicit boolean words
  take part in equality coercion.

  The boolean word set is English-only by design; a routing language must not change meaning
  with the author's locale. The Russian words the old `logical()` parser accepted now read as
  ordinary non-empty strings.

- **One placeholder pipeline (`redb.Route`).** `${...}` used to be interpreted by two engines
  with complementary holes — the one under `Expr(...)` / `StringExpression` / connector option
  strings, and the one under `SetBodyExpression` / `SetHeaderExpression` /
  `SetPropertyExpression`. Both now route through the same compiler, and the rules are:

  - `jpath(...)` / `xpath(...)` keep their own syntax; `body` and `contentType` are direct
    accessors.
  - A `header.` or `property.` placeholder resolves **literal name first**: the exact name when
    it exists (`${header.Content-Type}`), then the member path (`${header.user.Age}`), then the
    whole text as an expression (`${header.a+1}` is `11`). The name check happens per message,
    which is what makes a dash usable in a name at all.
  - Anything else is a full expression: arithmetic, comparisons, functions, ternary, index
    access, whitespace-insensitive.
  - **A whole-string placeholder keeps the CLR type of its value.** `Expr("${header.a}")` with
    an int header now yields the int, not `"10"`; `Expr("${header.a > 5}")` yields a bool.
    `Evaluate<string>` still converts, so code asking for a string is unaffected. This was
    already the behaviour of `SetHeaderExpression`; the two paths now agree.
  - `Expr("${header.missing}").Evaluate<string>()` is `null` rather than `""` — the typed
    reading of a missing value. Mixed templates still render a missing value as empty text.
  - A placeholder that cannot be compiled (`${<xml>}`, `${dGVzdC1zZWNyZXQ==}`) fails while the
    route is built. It used to be echoed as its own text, which could only ever mislead.
  - A placeholder that is a bare word (`${myVar}`) is a property lookup, as before.

  `ResolveTypedOrTemplate` delegates to the same pipeline; the private `ResolveExpression`
  ladder is no longer on any DSL path and remains only for `redb.Route.Llm`.

- **`WhenDefinition.SourceExpression` is renamed to `SourceTemplate`, and `SourceExpression` now
  holds an `IExpression?`.** It used to be a `string?` — so `SourceExpression` meant an `IExpression`
  on `FilterDefinition` and `SplitDefinition` but a string on `WhenDefinition`, and reading code was
  bound to get it wrong. The three names now carry one fixed type each everywhere, enforced by
  `IConditionSource`. **Breaking only for code that reads the property**: the setters have always
  been `internal`, so no route ever wrote it, and the route-authoring DSL is untouched.

- **`FilterDefinition.SourceExpression` is no longer set by the string overload of `Filter`.** A
  condition written as a string is carried by `SourceTemplate`, and the predicate built from it by
  `SourcePredicate`; `SourceExpression` now means what its type says — the filter was built from an
  `IExpression` instance.

- **The OpenAI provider family now uses the typed failures (`redb.Route.Llm`).**
  `LlmRateLimitException` (429, carrying `Retry-After`) and `LlmTransientException` (5xx and the soft
  529 "overloaded") existed and `AnthropicProvider` threw them, but `OpenAiProvider` (two call sites)
  and `OpenAiEmbeddingProvider` threw a bare `HttpRequestException` with the status as *text*. A
  caller could not tell "wait and retry" from "your key is wrong", and retrieval degrades to keyword
  search on any embedder failure, so an expired credential looked exactly like a busy server.
  The mapping moved into `LlmHttpErrors.FromResponse` and is now shared by all four call sites,
  Anthropic included: two copies of "which status means retry" drift apart, and the drift only shows
  in production. Anthropic's behaviour is unchanged. `4xx` other than 429 stays a plain
  `HttpRequestException` on purpose: a wrong key is not worth retrying.
  ⚠️ Behavioural break for a caller that catches `HttpRequestException` around an OpenAI-compatible
  provider. 429 and 5xx now arrive as `LlmRateLimitException` / `LlmTransientException`, and those
  derive from `Exception`, not from `HttpRequestException`.

### Removed
- **The hand-written logical branch — one language, one parser (`redb.Route`).** The library
  carried a second expression compiler next to the AST engine: `LogicalPredicate`,
  `LogicalExpression`, `ExpressionResolver.EvaluateLogicalExpression`,
  `GetCompiledLogicalExpression`, `CompileLogicalPredicate`, `ClearLogicalExpressionCache`, the
  logical-expression cache and its `CacheStatistics` counters (`LogicalExpressionCount`, `-Hits`,
  `-Misses`, `-HitRate`). It parsed comparisons by hand and got parentheses wrong:
  `(header.a > 10) AND (header.b < 5)` answered false, silently. Nothing in the library called it
  — conditions had already moved to the AST engine — and `LogicalExpression` was never
  constructed anywhere. All of it is gone; roughly 800 lines.

  `${logical(...)}` is unaffected as a language function: the AST engine has its own
  implementation, which the removed branch had been shadowing inside templates. Three forms that
  the branch answered wrongly now answer correctly:
  `${logical((header.a > 10) AND (header.b < 5))}`, `${logical(header.a + header.b > 40)}` and
  `${logical(header.s == 'x AND y')}` all render `True`.

  Migration for the removed public API: a condition string is evaluated through the DSL
  (`Filter`, `When`, `LoopWhile`, `Validate`); a boolean-valued expression is written as a
  condition string or via the predicate factories on `IExpression`
  (`isGreaterThan`, `contains`, ...). The tests that exercised the branch migrated to the
  condition path without losing a single assertion of behaviour.

- **The old template compiler (`redb.Route`).** Superseded by the one placeholder pipeline
  above; about 470 lines of expression-tree builders that only the old engine used.

- **`LogTemplateDefinition` and `RichLogDefinition` (`redb.Route`).** Both were unreachable — nothing in
  src, tests or demos constructed them and no code matched on their type — and both duplicated a live path
  that produces the identical processor: `.Log("…${header.x}…")` already routes through the template
  processor via `LogStaticDefinition`, and the fluent `.Log(level).Message().Header().Property().EndLog()`
  scope (`RichLogScopeDefinition`) produces the same `RichLogProcessor` as the flat `RichLogDefinition`.
  No logging behaviour changes; use the DSL forms.

## [3.7.2] — 2026-08-27

No changes of its own. The version moves with the ecosystem so the exact-version pins between packages keep resolving: `redb.Route.Core` 3.7.2 depends on `redb.Core` 3.7.2, and a partial release would demand a package that does not exist.

## [3.7.1] — 2026-08-26

> **Why 3.7.1, and what happened to 3.7.0.** 3.7.0 is withdrawn: it was built on .NET 9 and carries
> known vulnerabilities in its dependencies (see **Security** below). Every 3.7.0 package is unlisted
> on nuget.org and the `v3.7.0` releases were deleted from the public mirrors. An unlisted version
> still installs by exact number — but there is no reason to: 3.7.1 replaces it completely.
>
> A patch, not a minor: the public API surface does not change. Adding `net10.0` to the target list
> breaks nothing for existing consumers, and `net8.0` / `net9.0` are kept.

### Changed — the build moved to .NET 10

The applications and artifacts were built on net9 while the core and `redb.Route` had long
multi-targeted `net8.0;net9.0;net10.0`. The gap surfaced on 3.7.0: images and archives shipped as net9.

The `redb.Tsak.*` and `redb.Identity.*` libraries now declare `net8.0;net9.0;net10.0` — exactly like
the core and Route, so the whole ecosystem is uniform. Host applications and tests are pinned to a
single `net10.0`. Images, archives and tags are `-net10`.

.NET 8 and .NET 9 both reach end of support on **10 November 2026** — the same day, Microsoft aligned
the STS 9 date with LTS 8. .NET 10 is supported until **14 November 2028**. `net8.0` and `net9.0`
remain in the libraries' target list for now.

### Security — six high-severity advisories

Found while moving to .NET 10: changing the TFM forced a from-scratch rebuild and the NuGet audit
spoke up. It stays silent on an incremental build, which is how all of this reached 3.7.0.
- **`redb.Route.Sftp` — `SSH.NET` 2025.1.0** ([GHSA-q939-rpr3-3284], high), declared directly.
  Bumped to 2026.0.0; the connector's own suite passes 207/207 against it.
- The Kafka, RabbitMQ and Redis test projects pulled the same vulnerable `SSH.NET` 2024.2.0
  transitively. The root was `Testcontainers.*` 4.3.0 against 4.14.0 available — the version was
  raised rather than the symptom patched in three places.

[GHSA-q939-rpr3-3284]: https://github.com/advisories/GHSA-q939-rpr3-3284

## [3.7.0] — 2026-08-25

> **Why a minor.** A new connector package (`redb.Route.Soap`), two new EIPs in the DSL
> (`.ClaimCheck(...)`, `.ControlBus(...)` + the `controlbus:` component) and a rebuilt `redb.Route.Grpc`
> are all new public surface, so this cannot be a patch. The ecosystem moves together — `redb` core,
> `redb.Tsak` and `redb.Identity` ship 3.7.0 alongside.
>
> **Two behavioural changes need reading before upgrading**, both in `redb.Route.Grpc`: a failed call
> now throws (`ThrowOnError`, default `true`) where it used to return an error document, and errors
> reach clients as real gRPC statuses instead of `OK` plus a body. See **Changed (behavioural)**.
> `InMemoryFileIdempotentRepository` / `InMemorySftpIdempotentRepository` are removed — see **Removed**.

### Added
- **`redb.Route.Grpc` — a gRPC method address is now a route, and many of them share one port.** The
  consumer registers its method address (`/package.Service/Method`) as a path route on the shared Kestrel
  host — the same `SharedHttpServerManager` that already serves Http, As2 and Soap — and speaks the gRPC
  wire protocol itself (`GrpcWire`: length-prefixed framing, `grpc-status` / `grpc-message` trailers,
  `grpc-timeout` deadlines). Previously every `From("grpc:host:port")` built its own Kestrel, so a second
  gRPC route on the same port failed to bind and a facade had to be one route with a `Choice()` inside.
  Now `From("grpc:0.0.0.0:5001/identity.v1.Identity/Token")` and `…/Introspect` are ordinary routes with
  their own ids, policies, metrics and lifecycle, and the address — not a private header — selects them.
  A URI with no method address keeps serving the built-in generic `RedbService/Process` **and**
  `ProcessStream`, unchanged.
- **`redb.Route.Grpc` — real gRPC statuses out of a route.** The consumer maps the transport-neutral
  `status.code` that every controller dispatcher writes onto a gRPC status (401 → `Unauthenticated`,
  403 → `PermissionDenied`, 404 → `NotFound`, 429 → `ResourceExhausted`, …), and honours an explicit
  `redbGrpc.StatusCode` / `redbGrpc.StatusDetail`. `redbGrpc.Trailer.*` headers become response trailers.
  A non-OK status is delivered trailers-only because clients discard the payload of a failed call.
- **`redb.Route.Grpc` — typed `.proto` services without generated server stubs.** `Envelope=Auto` keeps the
  `RedbMessage` wrapper for the built-in address and passes raw protobuf bytes for any other, so a client
  generated from a real `.proto` can call a redb route directly. `.Envelope(Message|Raw)` overrides.
- **`redb.Route.Grpc` — server streaming, mTLS, health.** An `IAsyncEnumerable` reply body is written one
  frame per yield (the framework's own streaming shape, as in the HTTP consumer);
  `.ClientCertificates(mode, thumbprints…)` requires and pins client certificates, surfacing
  `redbGrpc.ClientCert*`; `.Health()` serves `grpc.health.v1.Health/Check` for Kubernetes / Consul / Envoy
  probes. The client address is resolved to `redbGrpc.RemoteIp` / `RemotePort`, and
  `.EmitHttpCompatHeaders()` mirrors it into `redbHttp.RemoteAddress` so IP-keyed processors written for
  HTTP (rate limiting, lockout, device metadata) work behind a gRPC facade unchanged.
- **`redb.Route.Grpc` — gzip.** Compressed requests are accepted and inflated (the size limit is
  re-checked after inflation, so a small frame cannot expand into an arbitrarily large buffer);
  `identity,gzip` is advertised back on every response. `.Compression(GrpcCompression.Gzip)` compresses
  replies, but only when the caller advertised gzip, and gzips outgoing requests on the client side. An
  unknown codec is answered with `Unimplemented` instead of a parse failure.
- **`redb.Route.Grpc` — the producer streams too.** `.Streaming()` on a client endpoint makes the call
  server-streaming and puts an `IAsyncEnumerable` into `Out.Body`, so a gRPC stream flows straight into a
  streaming consumer (the HTTP one turns it into SSE or chunked output) without being buffered in between.
  Parity with camel-grpc's `producerStrategy=STREAMING`.
- **`redb.Route.Grpc` — a stream body on a unary address fails loudly.** A unary call carries exactly one
  message, so an `IAsyncEnumerable` reply there cannot be delivered; the route now gets an error naming the
  address instead of the enumerable's type name going out as the payload.
- **`redb.Route.Grpc` — live interop tests against an independent gRPC stack.** Since the wire protocol is
  ours now, correctness is verified against Node.js `@grpc/grpc-js` in a container (`C:\Work\yaml\grpc`),
  both directions and cross-process: a foreign server parses our frames, a foreign client accepts our
  replies, trailers, `PERMISSION_DENIED` status, server stream, gzipped request and a real mTLS handshake
  with a pinned client certificate. The contract is a typed `.proto`, so the same tests prove a generated
  client can call a redb route with no server stubs on our side. Gated on the container
  (`--filter Category=Interop`), mirroring the SOAP and AS2 fixtures.
- **`redb.Route.Grpc` — camel-grpc parity on the URI.** `grpc://host:port/my.Service?method=Call` works
  alongside the full-address spelling, plus `maxMessageSize` and `negotiationType=PLAINTEXT|TLS`.
- **`redb.Route.Controllers` — `[GrpcMethod("Name")]`.** Pins the name gRPC callers dispatch on so renaming
  a C# method is not a breaking change, mirroring `[SoapOperation]` for SOAP.
- **`redb.Route.Http.Hosting` — client certificates on the shared host.** `RegisterRoute` accepts
  `clientCertificateMode` and a `clientCertificateValidation` callback (thumbprint pinning on top of
  Kestrel's chain validation), available to every HTTP-based transport.

### Added
- **`.ClaimCheck(...)` — the Claim Check EIP is now reachable from the DSL.** The processor, the five
  operations, the headers and two repositories (in-memory and file-backed) were all implemented, but
  nothing constructed `ClaimCheckDefinition`, so the pattern could not be used from a route at all.
  A large body is now checked into a store and a short claim key travels the route in its place:
  ```csharp
  .ClaimCheck(ClaimCheckOperation.Set, "order-42")   // body → key, original type kept in headers
  .To("kafka://orders")                              // the broker carries the key, not the payload
  .ClaimCheck(ClaimCheckOperation.Get, "order-42")   // key → body, restored as its original type
  ```
  `Push` / `Pop` use an exchange-scoped stack instead of a key and nest, so a body can be parked around
  an enrich call and restored after it. The repository is resolved at compile time: an explicit instance,
  a name registered with `context.AddClaimCheckRepository(name, repository)`, a context default set with
  `SetDefaultClaimCheckRepository`, an `IClaimCheckRepository` service, or — failing all of those — one
  shared in-memory repository created per route context, so a `Set` in one step and a `Get` in another
  reach the same store. An unknown repository name fails at startup naming the missing registration,
  not at the first message.

### Removed
- **`InMemoryFileIdempotentRepository` (`redb.Route.File`) and `InMemorySftpIdempotentRepository`
  (`redb.Route.Sftp`).** Both were byte-for-byte copies of `InMemoryGenericFileIdempotentRepository`,
  differing only in the signature of their static `DefaultKey`, and neither was reachable in production:
  the file consumer constructs the generic one directly, no endpoint option names a repository, and
  nothing resolves them from the registry or by reflection. They were left behind when the GenericFile
  extraction unified the three implementations — FTP, added later, never had one. Their tests were
  consolidated into `redb.Route.Tests.GenericFile` against the class that actually runs. Code that
  constructed either type directly should use `InMemoryGenericFileIdempotentRepository`, or the core
  `InMemoryIdempotentRepository` for a non-file `.IdempotentConsumer(...)`.

### Fixed
- **`redb.Route.File` / `redb.Route.GenericFile`: four defects found by a critical review of the file
  transports, each reproduced before it was fixed.** Three of them lost data silently, and all three
  survived a fully green suite because the tests covered options one at a time while the defects lived in
  their combinations.
  - **`readLock=Rename` delivered empty files forever.** The strategy renames the file aside to claim it,
    but the consumer kept reading the original path. The read failed, the failure was swallowed into an
    empty body, post-processing failed on the same missing path, the lock renamed the file back, and the
    next poll started over. Measured before the fix: six deliveries of a one-file directory, every body
    empty, the file still there. A read-lock strategy can now report the path it moved the file to.
  - **`readLock=FileLock` did the same, for the opposite reason.** The strategy held the file open with
    `FileShare.None`, so the consumer's own second open was refused by its own lock, and again the failure
    became an empty body. The strategy now exposes the handle it holds and the consumer reads through it.
    It also opens with `FileShare.Delete` so post-processing can delete or move the file while the lock is
    still held — exclusivity against other processes is unchanged and pinned by a test.
  - **`idempotent` together with any `readLock` could lose a file permanently.** The idempotent key was
    claimed *before* the read lock was taken, and the lock-refused path returned without releasing it. A
    consumer that lost the lock race had already marked the file as processed; since the default key is
    path + last-modified + size, it stayed marked forever and no consumer ever picked the file up again,
    with nothing in the log. The read lock is now taken first.
  - **A custom `idempotentKey` was used as a literal.** The DSL accepts an expression and the README
    promised one, but the runtime took the string as-is, so every file got the same key: the first file
    was processed and every later one silently skipped, permanently. `idempotentKey`, `moveTo`, `preMove`
    and `moveFailed` now resolve the same file variables that `doneFileName` already did — `${file:name}`
    and `${file:name.noext}`. Exchange-level expressions remain unavailable there by design: these values
    are needed before the exchange exists or while it is being disposed, and the DSL and README now say so
    instead of implying otherwise. Affects `redb.Route.Ftp` and `redb.Route.Sftp` too — the code is shared.
- **`redb.Route.GenericFile`: the idempotent repository folded case, so `Order.csv` shadowed `order.csv`.**
  The key embeds the file path and the set compared with `OrdinalIgnoreCase`. On every SFTP/FTP server
  and every non-Windows file system those are two different files, and the second one was skipped as a
  duplicate it never was — silently, and permanently for as long as the process lived. Now `Ordinal`,
  matching the core `InMemoryIdempotentRepository`.
- **`redb.Route.GenericFile`: an unreadable file arrived as a successfully processed empty message.**
  `CreateExchangeAsync` caught every read error, logged a warning and substituted `Array.Empty<byte>()`,
  after which post-processing happily deleted or archived the file. This is what made the two read-lock
  defects above silent. A read failure is now a processing failure for that file: the idempotent key is
  released, `moveFailed` applies, the file stays where it is, and the rest of the poll batch continues.
- **`redb.Route.Grpc`: four defects found by a critical review of the connector, each reproduced before
  it was fixed.** All four share one shape — untrusted or upstream-supplied input reaching code that sits
  *outside* the handler's own try block, so the failure escaped the route's error contract entirely.
  - **A crafted `grpc-timeout` could kill the request at the host.** The microsecond arm multiplied a
    caller-supplied `long` by 10 unchecked; `1000000000000000000u` wrapped to a negative tick count and
    `CancelAfter` threw `ArgumentOutOfRangeException` before the consumer's try block, while
    `9223372036854775807u` produced a tiny negative deadline that cancelled the call instantly. Worse, the
    `catch` only handled `OverflowException`, but `TimeSpan.FromHours` and friends raise
    `ArgumentOutOfRangeException` — so `9223372036854775807H` escaped the method too. Both are caught now,
    and the multiply is `checked`, so an unreadable deadline means what the doc always claimed: no
    deadline enforced, call proceeds.
  - **A header name the wire cannot express killed the call.** The producer copied every exchange header
    into gRPC metadata, whose key alphabet is far narrower — `Metadata.Add` throws on spaces, non-ASCII,
    and on any `-bin` suffix, which is a perfectly legal HTTP header name (`trace-bin`). That loop runs
    before the producer's try, so one odd header from an upstream HTTP consumer took the whole call down
    with an `ArgumentException` carrying no status. Unrepresentable keys are now dropped and logged; the
    rest of the headers still travel.
  - **A malformed envelope was reported as our fault.** In envelope mode the consumer parses
    caller-supplied bytes as a `RedbMessage`; garbage fell through the catch-all as `INTERNAL` — telling
    the caller "server problem, retry" about input only they can fix — and put the protobuf parser's own
    wording into `grpc-message`. Now `INVALID_ARGUMENT`, naming what was expected.
  - **A server stream that broke mid-flight was invisible.** Streaming failures surface while the consumer
    enumerates, long after `Process` returned, so `.OnException`, retry and dead-letter cannot see them —
    inherent to lazy streaming. But nothing recorded them either: a stream that broke every time looked
    like a stream that ended early. The break is now logged and counted on the endpoint, then rethrown so
    the reader still learns the stream did not finish.
- **`redb.Route.Llm` — review hardening: scheduled consumer, tool-loop, streaming and tool-error JSON.** A
  provider/HTTP timeout no longer terminates the scheduled consumer forever (an `HttpClient` timeout throws
  `OperationCanceledException` on a different token — only real shutdown stops the loop now). Hitting
  `MaxIterations`/budget mid tool-round returns the last assistant content instead of an empty answer. The
  provider's `HttpClient` is reused (cached in the factory) rather than minted and leaked per call, and a
  mid-stream failure is recorded as an endpoint error and failure metric instead of vanishing. Tool-error
  results are JSON-serialized, so a control character in a message no longer produces invalid JSON.
- **`redb.Route.Llm` — the native `AnthropicProvider` no longer 400s on current-generation Claude models.**
  Anthropic's Messages API changed the sampling contract across generations, and the connector sent
  `temperature`/`top_p` unconditionally: any config that set them and targeted Opus 4.7/4.8/5, Sonnet 5 or
  Fable 5 was rejected with HTTP 400 (those models removed the knobs), and Claude 4.0–4.6 rejected the two
  *together*. The provider now resolves an `AnthropicModelProfile` from the model id and shapes the request
  to the model's sampling policy — Claude 3.x takes both, Claude 4.0–4.6 takes one (`temperature` wins,
  `top_p` dropped), Claude 4.7+/5 takes neither — with an unrecognised id defaulting to the modern
  (no-sampling) contract so a future model release never 400s on a removed field. A dropped knob logs a
  warning and leaves an `llm.sampling.dropped` event on the span (model id, tier, dropped params) rather
  than failing silently.
  `LlmConnectionFactory.ModelContractTier` (`legacy`/`transitional`/`modern`) overrides the inference for
  proxy or self-hosted model ids. Anthropic-only; the OpenAI-compatible providers are untouched.
- **`redb.Route.As2` — MIC now matches for the compress-without-sign profile.** The sender hashed the
  compressed part while the receiver hashes the decompressed payload, so `Compress=true, Sign=false` always
  reported a MIC mismatch; the unsigned MIC is now computed over the uncompressed payload.
- **`redb.Route.Soap` — review hardening across modes, WS-Security symmetry and the controller/MIME edges.**
  Message (transparent-proxy) mode no longer decrypts/verifies the producer's response envelope (it now stays
  verbatim, matching the consumer's inbound skip); the consumer now signs/encrypts its response when certs are
  configured, so the producer's decrypt/verify leg is actually symmetric; a non-XML body in Message mode no
  longer throws out of the header-plane read; SOAP 1.2 `action` parses correctly when it is not the last
  Content-Type parameter. The SOAP controller dispatcher now fails fast on an ambiguous operation name across
  controllers (instead of silently running the first), awaits `ValueTask`/`ValueTask<T>` returns, and matches
  operation names case-sensitively (XML names are). The MTOM multipart parser respects quoted Content-Type
  parameters, so a foreign `boundary`/`start-info` containing `;` no longer breaks parsing.
- **An unhandled exception in a SEDA/in-memory consumer no longer kills the worker ([issue #6](https://github.com/redbase-app/redb-route/issues/6)).**
  The worker loop caught only cancellation and channel-closed, so a single failing exchange (e.g. a DB unique
  violation with no `OnException`) terminated the loop permanently and silently: the producer kept enqueueing,
  the route stopped consuming, and the exception surfaced only at shutdown. `ProcessWithTracking` now logs an
  unhandled exchange failure and drops it, so the consumer keeps draining — Apache Camel's `DefaultErrorHandler`
  + `SedaConsumer` behaviour. `OnException` / `DeadLetterChannel` remain the handled paths (they run inside the
  pipeline and never reach this net); broker consumers are unaffected (they use the manual ack/nack path). The
  fix covers every `ProcessWithTracking` consumer: `seda`, `direct-vm`, `timer`, and the S3 / Elasticsearch /
  Firebase / LDAP pollers.
- **`AddRouteBuilder<T>()` / `AddComponent<T>()` no longer fail host startup ([issue #5](https://github.com/redbase-app/redb-route/issues/5)).**
  The builder/component was registered only under its base type (`RouteBuilder` / `IComponent`), while the
  configurator resolves the concrete type — so startup threw `InvalidOperationException: No service for type
  '…' has been registered`. Both are now registered under the concrete type and exposed as the base type
  (same singleton). The documented onboarding path (`AddRedbRoute(r => r.AddRouteBuilder<MyRoutes>())`) works.
- **README — `.Retry(...)` examples corrected to the real error-handling API.** `.Retry` is not a route step;
  the README showed it as one in several places (a compile error). Route-level retry is `OnException(...)`
  with `MaximumRedeliveries` / `RedeliveryDelay`; per-step retry is `Transacted().Retry(attempts, delay)`.
- **`redb.Route.Controllers` — a JSON-object request body binds to controller parameters by name (gRPC / SignalR).**
  `ResolvePositional` used to drop the whole object into the first parameter, so a method with a route/path
  parameter plus a `[FromBody]` (the object became the id, the body stayed null) or with simple parameters
  (the object could not become an `int`) failed. A JSON object now binds each parameter by its name — honouring
  the `[FromRoute]`/`[FromQuery]` key, case-insensitively — while `[FromBody]` (or a lone unbound complex
  parameter) still receives the whole object, and unmatched parameters keep their defaults. JSON arrays and
  single values stay positional, so existing callers are unchanged.
- **`redb.Route.Grpc` — a reply body that is not a payload is refused instead of stringified.** A route
  answering with, say, a dictionary used to put the literal text
  `System.Collections.Generic.Dictionary\`2[…]` on the wire with an OK status. That is reachable in
  practice: a builder-level `OnException(...).Handled()` anywhere in the context replaces the answer with
  its own error document, and the route ends before any encoder of ours runs. The consumer now fails with
  `Internal` naming the offending type — the same lesson as the SOAP `byte[]` fix, one connector over.

### Security
- **`redb.Route.File`: the producer would write anywhere the incoming message told it to.** The target
  file name normally arrives from a header (`redbFile.Name`), i.e. from whatever produced the message —
  an uploaded file name, a partner's file name, a field of a payload. The local producer never validated
  it: `ValidatePath` is a no-op in the shared base and only the remote transports overrode it. Two ways
  out of the endpoint directory, both confirmed against the real producer: a relative `../escaped.txt`,
  and an absolute path, which `Path.Combine` silently honours by discarding the base entirely. The local
  producer now jails the target the way SFTP and FTP already did, under the same option name —
  `jailStartingDirectory`, default `true` — and throws `UnauthorizedAccessException` naming both the
  requested and the resolved path.
- **`redb.Route.Sftp`, `redb.Route.Ftp`, `redb.Route.File`: the jail compared a bare string prefix.** With
  a base of `/upload/in`, the target `/upload/instructions/x` starts with the base and was allowed
  through, into a directory the endpoint has nothing to do with. The check now compares on the directory
  boundary via the shared `GenericFileUtils.IsWithinDirectory`. The same flaw was in
  `FileClaimCheckRepository.GetSafePath`, which is now on the same helper.
- **`redb.Route.Grpc`: asking a producer for TLS left it connecting in cleartext.** `.Ssl()` sets
  `ssl=true`, but the producer builds its target address from `Plaintext` — a separate option that
  defaults to `true` and that nothing linked to `ssl`. Only `negotiationType=TLS` happened to set both.
  So `GrpcDsl.Call("host:443").Ssl()` produced `http://host:443`: the obvious spelling of "use TLS" was
  ignored, silently, on the leg that carries credentials outward. `ssl=true` now implies
  `plaintext=false`, and an explicit `plaintext` still wins so local debugging keeps its knob.
- **`redb.Route.Soap` — WS-Security signature-wrapping bypass fixed.** The anti-wrapping check resolved the
  protected Body with `GetElementsByTagName` (document order, any depth) while the processing path reads the
  Envelope's direct-child Body. An attacker could nest a genuinely-signed Body inside `<Header>` and put an
  unsigned Body as the direct child: the signature validated over the original while the route consumed the
  attacker's content with `redbSoap.signatureValid=true`. Verification now resolves the same direct-child Body
  the route uses and rejects an envelope with more than one Body. Regression test included.
- **`redb.Route.Soap` — XML-Encryption no longer leaks extra Body children in cleartext.** `EncryptBody`
  encrypted only the first `<soap:Body>` child, so a document/literal body with several elements sent the rest
  unencrypted. All children are now encrypted under one session key (a single `EncryptedKey` / `ReferenceList`),
  and decrypt restores them all.
- **`redb.Route.As2` — the receiver now enforces the partnership's signature/encryption requirements.** The
  inbound handler computed `signatureValid` but never acted on it: an unsigned message (or one whose signature
  failed) was delivered to the route and answered with a positive MDN. A message that the partnership requires
  to be signed/encrypted, or whose signature does not verify, is now rejected with a negative MDN and never
  processed. The crypto (signer pinned to the partner cert) was already correct; only the result was ignored.
- **`redb.Route.As2` — an unsigned/forged MDN is no longer accepted as a valid receipt.** `MdnParser` defaulted
  `SignatureValid` to true, lowering it only for a signed MDN — so a stripped-signature or fabricated
  `multipart/report` read as a valid signed receipt. It now defaults false; only a present-and-verified
  signature sets it true, and `RequireValidMdn` hard-fails a send on an unacceptable MDN (negative, MIC
  mismatch, or missing required signature).
- **`redb.Route.As2` — SSRF via `Receipt-Delivery-Option` blocked.** The async-MDN receipt URL was POSTed to
  verbatim; a request could point it at link-local metadata (169.254.169.254). Link-local literal IP targets
  are now rejected before delivery.
- **`redb.Route.As2` — the async-MDN correlation store no longer grows unbounded.** `Sweep` was never invoked;
  a partner that never delivered a promised async MDN leaked one live waiter per message. The store now runs a
  periodic eviction timer (and is disposed with the component).
- **`redb.Route.Http` / `redb.Route.Grpc` — a caller can no longer forge transport headers.** `HttpConsumer`
  set `redbHttp.RemoteAddress` from the connection and then copied the request headers over it, so a client
  sending a header literally named `redbHttp.RemoteAddress` (a valid HTTP token) replaced the socket
  address — the input to per-IP rate limiting, brute-force lockout and audit records. Inbound headers
  carrying a transport-reserved prefix (`redbHttp.`, `redbGrpc.`, `redbSoap.`, `redbSignalR.`, `redbMail.`,
  `redbAs2.`) are now dropped; the gRPC consumer applies the same rule to metadata and envelope headers, with
  `allowClientReservedHeaders=true` as an explicit, logged opt-out.

### Testing
- **`redb.Route.Tests.GenericFile` — the shared file pipeline now has its own suite.** The poll loop,
  filtering, sorting, limits, idempotency, done-file, pre-move / move / delete, the failure contract and
  the producer write flow are exercised against an in-memory `IFileOperations`, so the code that File, FTP
  and SFTP all share is covered in milliseconds without a disk or a server. Previously it had no suite of
  its own and was reached only through `redb.Route.Tests.File` and through docker-gated FTP/SFTP
  integration tests. The File suite gained end-to-end read-lock tests (the existing ones drove the
  strategies in isolation, where all five are correct) and producer path-safety tests. Every test added
  here was confirmed to fail on the unfixed code first.
- **`ClaimCheckDslTests`** covers the new DSL end to end: `Set`/`Get`/`GetAndRemove`, nested `Push`/`Pop`,
  and all four repository-resolution paths including the startup failure on an unknown name.
- **The idempotent-repository suite moved to the class that runs.** The two per-connector copies tested
  removed types; one of them also pinned the case-folding defect as correct behaviour
  (`CaseInsensitive_Keys` asserted that `File.TXT` and `file.txt` were the same file). The consolidated
  suite asserts the opposite, plus concurrency: exactly one of fifty racing `Add` calls wins the claim.

### Changed (behavioural)
- **`redb.Route.Grpc` — the producer throws on a failed call (`ThrowOnError`, default `true`).** It used to
  record the `RpcException` on the exchange and return; nothing in the pipeline reads that field, so
  `.OnException(...)`, retry and dead-letter never saw the failure and the route carried on with an empty
  `Out`. It now behaves like the HTTP and SOAP producers. Set `throwOnError=false` for the old behaviour.
- **`redb.Route.Grpc` — errors reach clients as gRPC statuses instead of `OK` plus an error document.** A
  caller that ignored the status and parsed the body will now see an `RpcException`. Set
  `suppressStatusMapping=true` to keep answering `OK`.
- **`redb.Route.Http.Hosting` — conflicting listener settings on one port now throw.** `Protocol` and
  `clientCertificateMode` from a later `RegisterRoute` used to be discarded silently, which put a gRPC
  route (HTTP/2 only) on an HTTP/1.1 listener and failed every call with an unreadable framing error.
- **`redb.Route.Grpc` — the consumer now opens a span.** `grpc receive` (`ActivityKind.Server`,
  `rpc.system=grpc`), mirroring the AS2 and SOAP consumers; previously only the producer's `grpc.invoke`
  existed. Traces and any span-count assertions will see one more span per call.
- **`redb.Route.Grpc` — `GrpcEndpoint.BuildProducerAddress()` composes from host and port** instead of
  echoing the URI path, which now also carries the method address. A URI without an explicit port
  (`grpc:myhost`) resolves to `http://myhost:50051` rather than `http://myhost`.

### Dependencies
- **`redb.Route.Grpc` no longer references `Grpc.AspNetCore`.** The server stack is gone with the wire
  protocol moving into `GrpcWire`; what remains is the message layer (`Google.Protobuf`, plus `Grpc.Tools`
  as a build-only dependency) and the client channel (`Grpc.Net.Client`, which brings `Grpc.Core.Api` —
  still used on both sides for `StatusCode` and `RpcException`). The generated `redb_service.proto` now
  emits `GrpcServices="Client"`: the server side is ours.
- **`redb.Route.Grpc` now references `redb.Route.Http.Hosting`**, the shared Kestrel host it serves on,
  joining `redb.Route.Http`, `redb.Route.As2` and `redb.Route.Soap`. `AddRedbRouteGrpc()` calls
  `AddRedbRouteHttpHosting()` itself (idempotent), so a worker with several HTTP-based transports still
  ends up with one server manager.

### Added
- **`redb.Route.Controllers` — SOAP as a controller transport (`SoapControllerDispatcher` + `RedbSoapController`).**
  SOAP joins HTTP / SignalR / gRPC as a controller dispatch target: `redbSoap.operation` maps to a controller
  method (by name or `[SoapOperation("...")]`), the XML body binds to the `[FromBody]` / complex parameter via
  `XmlSerializer`, and the typed reply serializes back into the response envelope. Errors become a `soap:Fault`.
  `From(Soap.Listen("/svc/air")…).RedbSoapController<AirController>();` — no HTTP attributes, DTOs may be
  `dotnet-svcutil`-generated. Use it in the default `Payload` data format. A required simple parameter that
  cannot bind faults with a readable message (naming the parameter) rather than a reflection error, and a
  `byte[]` reply body is emitted as XML instead of `"System.Byte[]"`. Verified end to end through the real
  SOAP consumer.
- **Control Bus EIP — `controlbus:` component + `.ControlBus(...)` DSL (Apache Camel parity).** Manage routes
  at runtime by sending a message: **start / stop / suspend / resume / restart / status / stats / fail** a
  route addressed by `routeId` (`current` targets the sending route). Registered out of the box, producer-only
  (`.To("controlbus:route?routeId=orders&action=stop")` or `.ControlBus(ControlBusAction.Stop, "orders")`).
  Options mirror Camel: `routeId`, `action`, `async` (fire-and-forget), `restartDelay` (ms), `loggingLevel`;
  plus the `controlbus:language:<lang>` command. `status` puts the route's `RouteStatus` on the body; `stats`
  returns per-route XML built from the endpoint's real `IEndpointStatistics` (messages in/out, errors,
  throughput, health), or the whole context when `routeId` is omitted. A control action opens a `Client`
  telemetry span like any other connector. Stopping the **current** route is auto-deferred (async dispatch)
  so a route can safely stop itself without deadlocking on its own in-flight exchange.
  ```csharp
  From("kafka://ingest")
      .Choice().When(Overloaded).ControlBus(ControlBusAction.Suspend, "current", async: true).End()
      .To("direct://process");
  ```
  Per-context, matching Camel — for cross-context control, send over `direct-vm`/`vm` to the target context
  and run `controlbus:` there. Built entirely on the existing per-route lifecycle
  (`StartRoute`/`StopRoute`/`ResumeRoute`); no new machinery.
- **`controlbus:notify` — consume route/context lifecycle events as messages (redb extension, beyond Camel).**
  Camel's control bus is producer-only; lifecycle changes are observed through the EventNotifier callback SPI
  (redb's equivalent is `IRouteLifecycleListener`). This adds a **consumer** so events flow into a route and
  can be handled with the full EIP pipeline: `From("controlbus:notify").Filter(...).To("telegram://ops")`.
  Emits `RouteStarted` / `RouteStopped` / `RouteSuspending` / `RouteErrored` / `ContextStarting|Started|Stopping|Stopped`
  / `ExchangeTimedOut`, with `controlbus.event` / `controlbus.routeId` / `controlbus.timestamp` (+ `controlbus.error`,
  `controlbus.exchangeId`, `controlbus.elapsedMs`) headers. Optional `routeId` and `events=` filters. Events are
  dispatched off the lifecycle-notification thread, so a slow reaction never stalls route start/stop.
- **`redb.Route.Soap` — new SOAP / WSDL web-service connector (baseline), oriented to Apache Camel `camel-cxf`.**
  Call SOAP services and host SOAP endpoints as ordinary route steps. Schemes `soap` / `soaps`. Producer wraps
  the body in a 1.1/1.2 envelope, POSTs it and surfaces the response on `exchange.Out`; a `soap:Fault` (both
  versions) throws `SoapFaultException` with `redbSoap.fault*`. Consumer hosts on the shared Kestrel host,
  delivers the `<soap:Body>` payload to the route and returns a response envelope (route exception ⇒ SOAP
  fault; a `byte[]` reply body is emitted as XML, not `"System.Byte[]"`). Two header planes handled correctly — transport HTTP headers vs the envelope `<soap:Header>` block
  (mapped under `redbSoap.header.*`), plus `redbSoap.operation` and the ContentType canonical plane.
  **WS-Security**: UsernameToken, XML-Signature of the Body (sign + verify) and XML-Encryption of the Body
  (encrypt + decrypt) in the standard WSS layout (`EncryptedKey` in the `wsse:Security` header with a
  `ReferenceList` to the `EncryptedData`, AES-256-CBC + RSA-OAEP), validated end to end by an independent
  crypto stack (Node.js OpenSSL). On the patched `System.Security.Cryptography.Xml` 9.0.18 (CVE-2026-50648). Signature
  verification authenticates against the configured partner certificate (rejecting a signature from any other
  cert) and requires the signature to cover the `<soap:Body>` (defeating signature-wrapping); the producer
  decrypts and verifies responses symmetrically. Full connector cross-cutting — `Client`/`Server` telemetry
  spans, `IEndpointStatistics`, `[Sensitive]` redaction, partner config via `SoapConnectionFactory`.
  ```csharp
  services.AddRedbRouteSoap();
  From("direct://q").To(Soap.Call("https://gds/air.svc").ConnectionFactory("amadeus").Operation("GetFares"));
  From(Soap.Listen("/svc/orders").Host("0.0.0.0").Port(4090)).Process(handle);
  ```
  **camel-cxf dataFormat + MTOM parity**: `Payload` (default), `Message` (whole-envelope transparent proxy)
  and `Pojo` (typed request/response via `XmlSerializer`, DTOs may be `dotnet-svcutil`-generated) — set on
  `SoapConnectionFactory.DataFormat`. **MTOM/XOP** binary attachments as `multipart/related`, exposed on the
  `redbSoap.attachments` plane (`SoapAttachment`) like Camel's `AttachmentMessage`, on both producer and
  consumer. **WSDL publishing** (`?wsdl` parity): a consumer with `SoapConnectionFactory.Wsdl` set serves the
  contract on `GET ?wsdl` with the `soap:address` rewritten to the caller's URL. Baseline is in-box (HttpClient
  + shared Kestrel + `System.Security.Cryptography.Xml`, no CoreWCF). Verified against an independent SOAP
  stack (Node.js `soap`) in both directions — plain SOAP and MTOM (their `forceMTOM` client ↔ our consumer) —
  as gated `Category=Interop` tests (harness in `C:\Work\yaml\soap`). See `docs/SOAP_CONNECTOR_PLAN.md`.

## [3.6.0] — 2026-08-13

> **Why a minor.** `.PropagateToolHeaders(...)` / `?propagateToolHeaders=` is new public API, so this
> cannot be a patch. Everything else here is a fix, and existing routes are unchanged: the option is
> opt-in and empty by default. The ecosystem moves together — `redb` core, `redb.Tsak` and
> `redb.Identity` ship 3.6.0 alongside, which also puts the core back on the shared number after the
> 3.5.1 split.

### Fixed
- **Fluent DSL builders — endpoint URIs now round-trip through the parser; space-bearing values no longer
  corrupt (fixed a `Cron.Schedule(...)` crash that took down the whole module).** Every fluent builder wrote
  query values with `HttpUtility.UrlEncode` (`application/x-www-form-urlencoded`, where space → `+`), but the
  endpoint parser decodes with `Uri.UnescapeDataString` (RFC 3986, where `+` stays `+`). The two are not
  inverse, so any value containing a space came back corrupted. Most visibly **`Cron.Schedule("job",
  "0 */5 * * * ?")`** — every cron expression has spaces — produced `schedule=0+*/5+...`, and
  `CronEndpointOptions.Validate()` then threw an `ArgumentException` **inside `context.Start()`**, bringing
  down the entire module (all routes, not just the one job). The same latent mismatch affected SQL statements,
  LDAP filters, Exec arguments, LLM prompts and any other space-bearing DSL value. Fixed at the writer side
  across **all** builders (`HttpUtility.UrlEncode` → `Uri.EscapeDataString`, which writes `%20` and round-trips
  cleanly). The parser is deliberately **untouched**: hand-written endpoint URIs and existing routes are
  byte-for-byte unaffected, and `System.Web` is dropped as a dependency. Guarded by a `Build()` → `Parse()`
  round-trip test so the invariant "write it the way the parser reads it" cannot silently regress again.
- **`redb.Route.Llm` — conversation isolation no longer depends on tree-filter semantics
  (cross-conversation leak + tree corruption).** `RedbConversationStore.LoadPathAsync` detected the
  conversation head with `TreeQuery(root).WhereLeaves()`, trusting the rooted query to scope the
  subtree. It did not: the core evaluated it as "freshest leaf of the whole schema" (fixed for Pro in
  `d88ff9fe`, still open for the Free PVT routing), so **any** turn of **any** conversation could load
  whoever wrote last globally — and the next message was then attached under that foreign node,
  corrupting `_id_parent` irreversibly. Head detection now goes through the conversation FK the
  connector already stamps (`value_long` on the indexed `_objects` row): the newest message of a
  conversation is always a leaf, so `WhereRedb(o => o.ValueLong == rootId)` ordered by `date_create`
  is exactly "the freshest leaf" without depending on how a provider implements tree filters. The FK
  was always stamped correctly, so this also **recovers already-corrupted trees** on read.
  Three further defects in the same file, independent of the core:
  - `AppendAsync` / `LoadPathAsync(convId, leafId)` resolved a message id across the whole schema, so
    an application that lets clients branch by message id could read — and write into — another
    conversation. The lookup is now scoped by the conversation FK and fails loudly.
  - The foreign conversation root slipped into the transcript as a role-less `MessageProps` (the path
    was trimmed by `id != rootId`, which only recognises *its own* root), posting an empty role to the
    provider — a 400 that reads like a model problem.
  - The root-id cache was keyed by conversation id alone while the redb instance is chosen per
    exchange (`?redb=`), so with two named databases a root resolved in A was returned for B. Keyed by
    `(instance, conversation)` now.
- **`redb.Route.Llm` — tool routes now run on a child of the agent exchange, not on a naked one.**
  `AgentEngine.DispatchToolEndpointAsync` built the tool's exchange from scratch (`new Message(...)` +
  `IProducerTemplate.RequestBody`), so a route mounted with `.AsLlmTool(...)` / `[ExposeAsLlmTool]` /
  MCP received **no** `Properties` (including `LlmKeys.RedbName`), **no** `RouteId` and a **fresh DI
  scope from the root container** instead of the conversation's. Every scoped service the agent route
  had resolved — principal, tenant accessor, per-exchange `IRedbService` — came back empty inside the
  tool. Only the ambient transaction survived, because it flows through the async context.
  Dispatch now uses `parentExchange.CreateLinkedChild(msg)` + `IProducerTemplate.RequestAsync`, which is
  what `docs/LLM/PLAN.md §4.1` specified and what `ILlmToolDescriptor` / `RouteToolBridge` documented.
  The child shares the parent's scope without owning it, so it releases only the `__redb_scope:*` entries
  the tool itself opened and the conversation keeps its scope after the call. Tool dispatch is
  sequential within an iteration, so the shared scope carries no concurrency risk.
- **`redb.Route.Llm` — the run's principal and audit tags reach tools.** The engine copied a hard-coded
  three-header allowlist (`llm.conversation.id`, `X-Correlation-Id`, `CorrelationId`) and nothing else, so
  a "who is asking" tool had no way to learn the subject except asking the model for it — an argument the
  model can be talked into forging. `llm.user.id` and `llm.audit.*` are now propagated **as the values
  resolved for the run**, not as raw headers: `?user=${header.X-User-Id}` and `?audit=` reach tools too,
  which a header copy would have missed.

### Added
- **`redb.Route.Llm` — `.PropagateToolHeaders(...)` / `?propagateToolHeaders=`.** Opt-in list of extra
  header names forwarded from the agent exchange to every tool call; a trailing `*` makes an entry a
  prefix match. Also on the inline step as `LlmCallBuilder.WithPropagatedToolHeaders(...)`.
  ```csharp
  .To(Llm.Factory("claude")
        .Tools("profile_state,billing_check")
        .User("${header.X-User-Id}")
        .PropagateToolHeaders("x-tenant-id", "accept-language"))
  ```
  Propagation stays **default-deny** — the inbound transport's header set is never forwarded implicitly,
  so an HTTP consumer's `Authorization` / `Cookie` cannot ride into a tool by accident. The policy lives in
  the new public `ToolHeaderPolicy`, and `AgentRequest.PropagateToolHeaders` carries it to the engine.

  Note the trust model this assumes: `llm.user.id` and `llm.audit.*` are read off the inbound exchange by
  `LlmProducer`, so a route that forwards client headers verbatim lets the caller set them. That was
  already true for what the engine persists in `MessageProps.UserId`; a route whose tools make access
  decisions on the principal must strip or overwrite client-supplied `llm.*` headers before the `llm://` hop.

## [3.5.1] — 2026-08-07

> **Why a separate patch instead of shipping inside 3.5.0.** The AS2 connector and the shared Kestrel
> host landed on `develop` **after** 3.5.0 had already been published to nuget.org (2026-08-05, 61
> packages). A published version cannot be replaced, so these two packages — and the
> `redb.Route.Http` build that goes with them — ship as 3.5.1.
>
> **`redb.Route.Http` is republished here on purpose.** `SharedHttpServerManager` moved out of it into
> `redb.Route.Http.Hosting`, so the 3.5.0 build on nuget.org still carries its own private copy of the
> multiplexing server. Mixing that 3.5.0 with `redb.Route.As2` would put **two** Kestrel managers in
> one process — and `redb.Route.As2` does not reference `redb.Route.Http`, so NuGet would never
> upgrade it for you. Take `redb.Route.Http` **3.5.1** whenever you use AS2 on a shared port; the
> public API of the package is unchanged either way.
>
> The whole `redb.Route` line moves to 3.5.1 together, so no combination of 3.5.x Route packages can
> mix the two hosting models. `redb` core, `redb.Tsak` and `redb.Identity` stay on 3.5.0 — the
> shared-runtime compat gate compares the **minor**, and 3.5.0 ↔ 3.5.1 is patch drift, which it allows.

### Added
- **`redb.Route.As2` — new AS2 (RFC 4130) B2B/EDI connector.** Exchange business documents with trading
  partners over HTTP(S) using signed and encrypted S/MIME messages and MDN receipts. Schemes `as2` / `as2s`.
  Both directions, **synchronous and asynchronous MDN**, **signed MDN**, and the standard signature /
  encryption algorithm matrix (`sha-1/256/384/512` × `aes-128/192/256-cbc`, `3des`, optional RFC 3274
  compression). Crypto is MimeKit (Bouncy Castle) — the same foundation the AS2 industry interoperates on.
  ```csharp
  services.AddRedbRouteAs2();

  context.AddToRegistry("walmart", new As2ConnectionFactory {
      OurCertificate = ourPfx, PartnerCertificate = theirCer,
      As2From = "OUR-ID", As2To = "WALMART-ID", PartnerUrl = "https://partner/as2",
      Sign = true, Encrypt = true, SignedMdn = true, MdnMode = As2MdnMode.Sync });

  From(As2.Receive("/inbound").Host("0.0.0.0").Port(4080).ConnectionFactory("walmart"))
      .To("direct://process-edi");                                    // receive server
  From("direct://out")
      .To(As2.Send("https://partner/as2").ConnectionFactory("walmart")); // send + verify MDN
  ```
  - **Partner config via `As2ConnectionFactory`** in the registry — certificates, AS2 IDs and the agreed
    profile referenced by `.ConnectionFactory("name")`, never in the URI.
  - **MDN outcome on `exchange.Out`** for a producer: `redbAs2.mdnDisposition`, `redbAs2.signatureValid`,
    `redbAs2.mdnMicMatch` (the partner received exactly what we sent). Async MDN uses `As2.ReceiveMdn(path)`
    with correlation by `Original-Message-ID`.
  - **Received document** on the consumer: business content type on `Message.ContentType` (the S/MIME wrapper
    type is deliberately kept off the headers), AS2 headers verbatim, computed MIC under `redbAs2.mic`.
  - Full connector parity — `IEndpointStatistics` / health in Tsak, `Consumer`/`Client` telemetry spans,
    `[Sensitive]` secret redaction.
  - **Interop validated against a live OpenAS2 v4.9.0 in both directions** (`redb → OpenAS2` and
    `OpenAS2 → redb`, signed + encrypted, positive MDN, MIC verified). See `src/redb.Route.As2/TESTING.md`.
- **`redb.Route.Http.Hosting` — new shared Kestrel hosting package.** `SharedHttpServerManager` (the
  multiplexing HTTP server, one Kestrel per `host:port`) was extracted from `redb.Route.Http` into a
  standalone package so HTTP-based connectors (`redb.Route.Http`, `redb.Route.As2`, …) share **one** server
  without depending on each other. Register once with `services.AddRedbRouteHttpHosting()` (idempotent); every
  connector resolves the same singleton. **No breaking change** — the types keep the `redb.Route.Http`
  namespace, so `redb.Route.Http` source and public API are unchanged.

## [3.5.0] — 2026-08-05

> **Why a minor (3.4 → 3.5).** This release adds public API: the **Message History**, **XSLT** and
> **Routing Slip** EIPs, and `{{key}}` / `{{key:default}}` property placeholders in endpoint URIs.
> Backward-compatible — existing routes are unchanged, and every new feature is opt-in (Message
> History is off until `.MessageHistory()` or `EnableMessageHistory`). The ecosystem number moves
> together: `redb` core, `redb.Tsak` and `redb.Identity` all ship 3.5.0 alongside — see the root
> `CHANGELOG.md` for why the runtime cannot stay a minor behind the framework.
>
> The AS2 connector and the shared Kestrel host were written against this version but merged after it
> was published; they ship in **3.5.1** — see above.

### Added
- **Message History EIP — `.MessageHistory()` / `RouteEngineOptions.EnableMessageHistory`.** Records the
  trail an exchange takes through a route: each node is timed and appended to
  `exchange.Properties["CamelMessageHistory"]` as a `MessageHistoryEntry` (route id, node id, label,
  elapsed ms), and the trail is dumped on failure (when retries are exhausted) so the failing step is
  visible. Apache Camel parity: **disabled by default** (slight overhead), enabled globally
  (`EnableMessageHistory`) or per route with `.MessageHistory(bool)` — a route override wins over the
  global setting. Read the trail with `MessageHistory.GetEntries(exchange)` / render it with
  `MessageHistory.Format(exchange)`. The elapsed time is recorded even when a node throws.
  ```csharp
  From("direct://orders").MessageHistory()
      .Process(validate)
      .To("http://fulfil");
  // on failure the log shows: routeId · to1 · to · 12.400  (etc.)
  ```
- **XSLT transformation — `.Xslt(...)` / `.XsltContent(...)` and the `xslt:` component.** Transforms the
  exchange body through an XSLT stylesheet, Apache Camel `xslt:` parity. Modelled exactly on the built-in
  validator (leaf DSL **and** a component, engine behind an interface): the engine is `IXsltEngine` with a
  built-in `XslCompiledTransformEngine` (BCL `System.Xml.Xsl`, **XSLT 1.0**, zero external dependencies —
  same as Camel's default JAXP engine; a Saxon-backed engine for 2.0/3.0 can be added later as another
  `IXsltEngine`, exactly as third-party validators live in `redb.Route.Validation.Adapters`). The
  stylesheet is compiled once when the route is built and reused.
  ```csharp
  From("direct://orders").Xslt("styles/orders.xsl");                 // from a file (imports resolve)
  From("direct://orders").XsltContent(inlineStylesheet, XsltOutput.Bytes);
  From("direct://orders").To("xslt:styles/orders.xsl?output=bytes"); // component, registered out of the box
  ```
  - **Parameters** — all message headers and exchange properties are passed to the stylesheet as
    `xsl:param` (a stylesheet sees the ones it declares), matching Camel.
  - **Input body** — `string`, `byte[]`, `Stream`, `XmlReader`, `XDocument`/`XElement`, or `IXPathNavigable`.
  - **`output`** — `string` (default) / `bytes` / `dom`. **`failOnNullBody`** (default true).
  - **`allowTemplateFromHeader`** — when set, a per-message stylesheet from the `CamelXsltResourceUri` /
    `CamelXsltStylesheet` header overrides the compiled default (dynamic stylesheets are compiled once and cached).
  - The `xslt:` component is **registered out of the box** — `.To("xslt:...")` needs no setup, and it
    composes as an endpoint (a Routing Slip / Recipient List can target `xslt:`).
  - Security: script and `document()` are disabled (BCL `XsltSettings.Default`) for stylesheets from
    outside the app.
  - `Content-Type` is left unchanged after the transform (matching Camel). `output=file` is intentionally
    not provided — route the transformed body to the File connector (`.To("file:...")`) instead.
- **Routing Slip EIP — `.RoutingSlip(...)`.** Pipes the exchange through a list of endpoints that is
  computed **once** up front (contrast the Dynamic Router, which recomputes the next hop after every
  step). The slip may be a factory delegate, an `IExpression` yielding a delimited URI string, or a
  `${...}` template — Apache Camel parity, including the default `,` delimiter and `ignoreInvalidEndpoints`.
  The same exchange is threaded through the endpoints in sequence with Pipeline semantics (each hop's
  `Out` becomes the next hop's `In`; the final `Out` is left for an InOut caller), and the current
  endpoint is exposed on the exchange property `CamelSlipEndpoint`. Producers are cached per URI.
  ```csharp
  From("direct://orders")
      .RoutingSlip("${header.route}")            // e.g. "direct://validate,direct://enrich,amqp://out"
  // or a factory:
  From("direct://orders")
      .RoutingSlip(ex => new[] { "direct://validate", "direct://enrich", "amqp://out" });
  ```
- **Property placeholders in endpoint URIs — `{{key}}` / `{{key:default}}`.** Apache Camel
  `PropertiesComponent` parity: endpoint URIs (and any string routed through `GetEndpoint`) externalise
  their configuration instead of hard-coding hosts, queue names, ports or paths. Placeholders are
  resolved **once at route-compile time** — values come from `IConfiguration` (environment variables,
  appsettings, user-secrets — with the container's own precedence, so no Camel-style `env:`/`sys:` prefix
  functions are needed), then the context's own properties (`SetProperty`) as a container-free fallback.
  A placeholder with neither a value nor a default fails fast at compile time. Resolution is single-pass
  and a URI without `{{` is untouched, so existing routes are unaffected.
  ```csharp
  From("{{orders.source}}")                        // e.g. amqp://broker/orders, from appsettings
      .To("http://api/{{tenant}}/pay");
  From("amqp://broker:{{amqp.port:5672}}/orders"); // default when the key is not configured
  ```
  Deliberately minimal (matching the redb.Route "no gold-plating" line): no nested placeholders, no
  `{{?optional}}` parameter removal, no encryption — `IConfiguration` already covers the layered-source
  need. `PropertyPlaceholderResolver` is public for reuse.
- **`redb.Route.IbmMq` — event-driven receive via XMS `MessageListener`, cutting delivery latency
  from ~250–500 ms to single-digit ms.** The default consumer polls with a blocking MQGET-WAIT on the
  IBM.WMQ managed client, which carries an internal ~500 ms tick independent of `waitInterval` — so an
  idle/low-traffic queue delivered messages ~250–500 ms after they arrived. The managed IBM.WMQ client
  exposes **no** public async-consume API (there is no `MQQueue.Cb`/`MQQueueManager.Ctl` in any 9.4.x —
  verified by reflection), so the fix uses **XMS .NET** (`IBM.XMS`), the JMS-style client that *does*
  support event-driven `MessageListener` push. New opt-in option **`receiveMode=listener`** (default
  stays `poll`) activates it; measured publish→receive on a local broker dropped to **~2–6 ms**.
  ```csharp
  r.From("wmq:ORDERS?queueManager=QM1&channel=DEV.APP.SVRCONN&receiveMode=listener")
  ```
  The listener runs the route synchronously on the XMS dispatch thread — one session = one in-flight
  message = natural back-pressure. The package reference changed from `IBMMQDotnetClient` to its
  superset `IBMXMSDotnetClient` (same `amqmdnetstd.dll` for IBM.WMQ + `amqmxmsstd.dll` for IBM.XMS;
  same vendor and version), so the default poll path is byte-for-byte unchanged.
  - **Transacted listener** (`receiveMode=listener&transacted=true`) uses a JMS `SESSION_TRANSACTED`
    session and commits/rolls back on the *delivering* session — a processing error rolls back and the
    broker redelivers, exactly like the poll path's connection syncpoint. An `IbmMqXmsAckAction` is
    registered on the exchange so a route-level `.Transaction()` block settles it as part of the route
    unit-of-work (mirroring the poll path's `IbmMqAckAction`); if the route has no transaction block the
    engine settles the session itself (commit on success, rollback on error). The action is idempotent,
    so the two paths never double-settle.
  - **`concurrentConsumers=N`** creates N XMS sessions on the shared connection — N competing consumers
    processing in parallel (each session single-threaded, queue-manager load-balanced), one in-flight
    per session for back-pressure. A topic clamps to a single subscriber (parallel subscriptions would
    duplicate delivery), same rule as the poll path.
  - **Request-reply, backout threshold and W3C trace propagation** all work on the listener path, at
    full parity with the poll path. A request carrying `JMSReplyTo` gets an `InOut` exchange and the
    `Out` body is sent back correlated by message id (non-persistent, so temporary reply queues accept
    it); a message past `backoutThreshold` is copied to `backoutQueue`; and `traceparent`/`tracestate`
    on the message continue the distributed trace. RPC reply and the backout copy are issued on the
    delivering session, so under `transacted` they commit atomically with consuming the request.
  - **RPC client leg is event-driven too.** With `receiveMode=listener` the producer's request-reply
    now receives the response through an XMS `MessageListener` on the reply queue instead of the poll
    loop that carried the ~500 ms tick, so the *whole* round-trip is fast (measured ~16 ms warm vs the
    poll floor of ~250–500 ms). The request is still sent over IBM.WMQ; only reply reception moves to
    XMS. A dynamic reply queue uses an XMS temporary queue (owned and consumed by the producer's own
    connection); a configured `replyToQueue` is used as-is.
  - **Header parity on the listener path.** The XMS consumer and the event-driven RPC reply now carry
    the same headers as the poll path: the `redbIbmMq.*` MQMD metadata (Destination, QueueManager, MsgId,
    CorrelId, Priority, BackoutCount — mapped from the JMS/MQMD properties, gated by `mqmdReadEnabled`
    like poll) and the application (user) headers.
  - Fluent `.Listener()` / `.ReceiveMode(...)` on the builder mirror the `receiveMode` URI option.
- **`redb.Route.IbmMq` — user-header catalogue moved to the JMS `usr` folder (interoperability fix).**
  User headers are carried as a single JSON property (MQ property names forbid hyphens, so `X-Custom-Id`
  can't be an individual property). That property was written under a **dotted name** (`redbIbmMq.HeaderKeys`),
  which MQ places in a custom MQRFH2 folder that JMS clients — including IBM.XMS and any other JMS
  consumer — do not surface as a user property, so headers were invisible off the IBM.WMQ path. It now
  uses an **unqualified name** (`redbIbmMqHeaders`) that lands in the standard `usr` folder, readable by
  IBM.WMQ, IBM.XMS and any JMS client alike. *Wire-format note:* a producer on this version and a consumer
  on an older one (or vice-versa) will not exchange user headers across the change — deploy producer and
  consumer of the IBM MQ connector together. MQMD metadata and message bodies are unaffected.

### Changed
- **Internal: unified the scope-body pipeline builder (`NodePipeline`).** Every scope definition
  (Choice, Filter, CircuitBreaker, TryCatch, Loop, Metered, Replayable, Aggregate, IdempotentConsumer,
  Resequence, Threads, Traced, Transaction, Throttle, Debounce, Split) carried its own identical copy of
  the "0 → no-op / 1 → the child / N → a pipeline" logic; these now route through a single
  `NodePipeline.Body`/`Node`. Behaviour is unchanged (verified by the full suite, incl. 70 transacted
  tests), and it provides the single compile-time seam that Message History (above) decorates each node
  through. No public API or DSL change.

### Fixed
- **`redb.Route.Http` — the fluent `Http.Listen("/path").Host(..).Port(..)` dropped the first path
  segment.** The builder emits host/port as query parameters (`http:/api/honest/echo?host=..&port=..`),
  so the whole leading-slash path is the route — but `HttpEndpoint.ConsumerPath` assumed any leading-slash
  path was the nonstandard `/host:port/route` form and stripped its first segment, registering
  `/honest/echo` instead of `/api/honest/echo` (and `/webhook` collapsed to `/`). It now strips the first
  segment only when it is an actual embedded host (recognised by a `:` in that segment — route segments
  have none), so `Http.Listen("/api/honest/echo").Host("0.0.0.0").Port(5092)` registers the full path. The
  equivalent URI DSL (`http:0.0.0.0:5092/api/honest/echo`) was already correct and is unchanged.

## [3.4.0] — 2026-07-27

> **Why a minor bump (3.3 → 3.4).** This release adds public API surface, not just fixes: replay
> checkpoints (`.Replayable`, `IExchange.Snapshot`, `IRouteContext.ReplayAsync`/`GetReplayMarkers`),
> the reusable scope-nesting validation primitives (`ICompositeScope`/`IDurableScope`/
> `IScopeNestingRule`/`IBranchingDefinition`), named `ConnectionFactory` on every connector, and
> `ProducerTemplate` exchange-typed overloads — alongside the endpoint-URI secret-redaction security
> hardening. The whole ecosystem ships at **3.4.0**. Backward-compatible: existing routes are unchanged.

### Security
- **Endpoint-URI secrets no longer leak into logs, telemetry, health checks, or the Tsak
  dashboard.** Credentials carried in an endpoint URI — a query-parameter (`?password=`,
  `?bindPassword=`, `?sessionToken=`, `?saslPassword=`, `?connectionString=`, …) or a userinfo
  password (`amqp://user:pass@host`) — were being written in cleartext at route-build and
  endpoint-start (`Compiled route …`, `Endpoint … started`), in the OpenTelemetry
  `redb.route.endpoint` span tag and metric label, in the inflight-exchange and health-check
  metadata, and in the `CompiledRoute.FromUri` DTO that the Tsak CLI/dashboard render. The
  masking that did exist was bypassed by these raw-string paths and, where it ran, (a) disclosed
  the first two characters of every secret and (b) used an exact-match deny-list of eight names —
  so `bindPassword`, `sessionToken`, `sslKeyPassword`, `authToken`, `clientSecret`,
  `privateKeyPassphrase`, `sharedAccessKey`, and userinfo passwords all slipped through. Fixes:
  - **Full redaction to a constant `****`** — no more first-two-character disclosure.
  - **Substring-based secret detection** replacing the exact-match list (covers the names above and
    any `*password*` / `*token*` / `*secret*` / `*apikey*` / `*credential*` variant); benign params
    like `routingKey` / `partitionKey` / `clientId` / `username` are deliberately never masked.
  - **Userinfo passwords are masked** (`user:pass@host` → `user:****@host`) — previously outside the
    masker entirely.
  - **Every core log / telemetry / metric / health-check / DTO boundary is routed through a new
    format-preserving `EndpointUri.Sanitize(string)`** (keeps scheme, `://`, path, param order, and
    non-secret values byte-for-byte). Unnamed routes now derive a **sanitized** route id, so a secret
    can no longer surface through `{RouteId}` log lines.
  - **`redb.Route.Elasticsearch`** — the `Nodes=` startup log now sanitizes each node URL (userinfo).
  - **`redb.Route.Exec`** — the debug `exec →` line logs the executable and argument **count** only;
    argument values (which routinely carry secrets on the command line) are no longer emitted.
  - **Producer/consumer start-stop lines that printed the raw endpoint key are sanitized** —
    `GenericFileProducer` (the base for **FTP/SFTP**, whose credentials live in query parameters,
    so `?password=` was reaching the log verbatim) plus the in-process Direct / SEDA / Mock / Log
    components.
  - New public API on `EndpointUri`: `Sanitize(string)`, `IsSensitiveKey(string)`, and
    `AddSensitiveKeys(params string[])` for connectors to register non-standard secret parameter
    names (analogous to Camel's `addSanitizeKeywords`).
  - **`[Sensitive]` on an endpoint option is now the source of truth for what gets redacted.**
    Guessing a secret from its parameter name fails open — that is exactly how `bindPassword`,
    `sessionToken` and `sslKeyPassword` leaked: they were credentials nobody had put on the list.
    An option marked `[Sensitive]` is redacted because it was *declared* one:
    ```csharp
    public string Server { get; set; } = "localhost";   // printed in logs
    [Sensitive] public string? BindPassword { get; set; }  // always ****
    ```
    `EndpointOptions.BindFromUri` harvests those declarations by reflection (once per options type)
    and feeds them into `EndpointUri.AddSensitiveKeys`, so the keyword set is **derived from the
    code, never hand-maintained** and a newly added credential option cannot be forgotten. This
    mirrors Apache Camel, where `@UriParam(secret = true)` is the declaration and the runtime list
    (`SensitiveUtils.SENSITIVE_KEYS`) is generated from those annotations by a build plugin; the
    .NET version needs no build step, only reflection. All 37 credential options across 22
    connectors are annotated. The name-keyword heuristic remains as a backstop for a URI rendered
    before any endpoint of that scheme has been created.
  - Display-only change: routing identity, endpoint cache keys (`NormalizedKey` / `BaseKey`), and
    message flow are unaffected. `CompiledRoute.FromUri` is now a redacted display value and must not
    be re-parsed to recover credentials.
- **`redb.Route.Ldap` — `connectionFactory` is now actually resolved: new `LdapConnectionFactory`.**
  `LdapBuilder.ConnectionFactory()` and `LdapEndpointOptions.ConnectionFactory` existed since 3.3.x
  but nothing ever read them — `LdapEndpoint` took `BindDn` / `BindPassword` straight off the URI, so
  a service-account password had to be written into the route and from there reached logs and the
  dashboard. `LdapEndpoint` now resolves the named `LdapConnectionFactory` from the route registry
  and fills in every connection/credential option the URI did not set (an explicit URI value still
  wins, so existing routes are unchanged; a missing factory logs a warning and falls back to URI
  parameters). A route can now carry no credentials at all:
  ```csharp
  context.AddToRegistry("honest-ldap", new LdapConnectionFactory {
      Server = "ldap.corp.local", Port = 636, Ssl = true,
      BindDn = "cn=svc-reader,dc=corp,dc=local",
      BindPassword = Environment.GetEnvironmentVariable("LDAP_BIND_PASSWORD") });

  r.From("ldap://SEARCH:dc=corp,dc=local?connectionFactory=honest-ldap&filter=(objectClass=user)")
  ```
- **Secrets embedded in exception messages are redacted before logging.** `OnExceptionProcessor`
  logs `ex.Message` on redelivery and retries-exhausted; a driver exception can carry a connection
  string (`...;Password=…;…`). Those two sites now run the message through the new
  `EndpointUri.RedactSecrets(string)`, which masks `key=value` secret assignments inside arbitrary
  text while preserving everything else. Note: when `LogStackTrace` is enabled the exception object
  itself is handed to the logger and cannot be scrubbed in-process — use `RedactSecrets` in a
  logging-sink filter for that path.

### Added
- **Replay checkpoints — `.Replayable("name")` save-points.** A named point in a route that snapshots
  the exchange as it passes, so the *tail* of the route (everything after the marker) can be re-run
  later from that frozen state — e.g. the platform replaying a failed exchange from the last
  successful step instead of from the mangled current state. The captured `RouteCheckpoint` lands in
  `exchange.Properties["route.checkpoint"]` (last marker wins) and replay is a typed in-process call
  `IRouteContext.ReplayAsync(routeId, markerName, snapshot)`. Full developer guide (incl. the
  Tsak-integration contract): `docs/REPLAY_CHECKPOINTS_GUIDE.md`.
  ```csharp
  From("timer://poll?period=5000")
      .Process(chargeCard)
      .Replayable("after-charge")     // save-point: card already charged
          .Process(sendReceipt)
          .To("http://receipts")
      .EndReplayable();
  ```
  - **`IExchange.Snapshot()` / `IMessage.Snapshot()`** — a deep, isolated copy distinct from
    `Clone()`: the body is deep-copied so the captured state is frozen against later in-place
    mutation (whereas `Clone()` intentionally shares the body — relied upon by e.g. Splitter
    aggregation). v1 handles immutable / `byte[]` / `ICloneable` bodies and throws loudly otherwise
    (never a silent shallow share). A snapshot carries no DI scope (dormant data — no per-message
    leak). Also corrected the misleading "Deep copy" doc on `Clone()`.
  - **`exposed: true`** additionally publishes the marker as `direct:__replay:{routeId}:{name}` so
    other routes can `.To(...)` it; the default (`exposed: false`) is reachable only via `ReplayAsync`.
  - **`IRouteContext.ReplayAsync` / `GetReplayMarkers`** and `ProducerTemplate` exchange-typed
    overloads (`Send`/`SendAsync`/`RequestAsync(..., IExchange, ct)`, caller-owned) round out the API.
  - A non-snapshot-able body degrades gracefully (warn, no capture) — checkpoints never break the
    happy path. Routing identity, cache keys, and existing `Clone()` behaviour are unchanged.
- **Reusable scope-nesting validation.** A general mechanism (not an ad-hoc per-type check) for
  expressing where a definition may/may not nest: `ICompositeScope` / `IDurableScope` scope-category
  markers, `IScopeNestingRule` (a node declares `Allowed`/`Warn`/`Forbid` against an ancestor
  category), and `IBranchingDefinition` (definitions whose children live outside `Outputs` — Choice
  When/Otherwise, TryCatch catch/finally — expose them for a generic tree-walk). The validator
  applies the rules generically: `Forbid` → build error, `Warn` → log. First use: a replay checkpoint
  may not cross a branching composite (build error) and warns inside a durable transaction (replay
  runs outside it). New structural constraints ship on the definition, never in the validator.
- **Named `ConnectionFactory` for connectors that previously had no way to keep credentials out of
  the endpoint URI.** Registered in the route registry and referenced by name
  (`?connectionFactory=my-bot`), so the secret never enters the URI at all — nothing to mask in
  logs, telemetry, or the dashboard. Each factory fills only the options the URI did not set
  explicitly, so **an inline URI value always wins and existing routes are unchanged**; a name that
  is not in the registry logs a warning and falls back to URI parameters.
  Rolled out to all ten connectors that previously had no such mechanism: **Telegram** (`TelegramConnectionFactory` — bot token; token-less DSL
  overloads `Tg.Receive().ConnectionFactory("bot")`), **MqttNet** (`MqttConnectionFactory` — broker
  address + username/password/TLS), **Http** (`HttpConnectionFactory` — Basic/Bearer credentials,
  TLS certificate password, timeout; `AuthToken` supports `${...}` expressions exactly like the URI
  form; the request address deliberately stays in the URI path so a factory can never silently
  redirect a route), **Mail** (`MailConnectionFactory` — one factory shared by SMTP / IMAP / POP3:
  mailbox username/password, OAuth2 access token, auth mechanism, transport security and client
  certificate; a host taken from the URI path is never overridden by the factory),
  **Ftp** / **Sftp** (`FtpConnectionFactory` / `SftpConnectionFactory` over a shared
  `RemoteFileConnectionFactory` base in `redb.Route.GenericFile` — host/port/username/password and
  timeouts in the base, FTPS settings for FTP, private-key path + passphrase, host-key checking and
  proxy credentials for SFTP), **SignalR** (`SignalRConnectionFactory` — hub access token, transport
  and TLS material), and **Grpc** / **Tcp** / **WebSocket** (`GrpcConnectionFactory` /
  `TcpConnectionFactory` / `WsConnectionFactory` — TLS certificate password and connect timeouts).
  For the connectors whose address lives in the endpoint path (Http, Mail, SignalR, Grpc, Tcp,
  WebSocket) the factory deliberately carries **no** host/port, so it can never silently redirect a
  route; the `wss` scheme likewise still forces TLS on regardless of the factory.
  A fluent `.ConnectionFactory("name")` was added to every builder that has one (Telegram also gains
  token-less mode overloads); SignalR and WebSocket are URI-only and unchanged in that respect.
  ```csharp
  context.AddToRegistry("support-bot", new TelegramConnectionFactory {
      Token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN")! });

  r.From("telegram://receive?connectionFactory=support-bot")   // no token in the route
  ```
- **`redb.Route.Telegram` — reply target as a first-class producer option: `replyToMessageId`
  (expression-capable) with fluent `ReplyTo(long)` / `ReplyTo(IExpression)` / `.ReplyToIncoming()`.**
  Replying to the message that triggered the exchange previously required a manual `.Process` step
  copying `telegram.messageId` into `telegram.replyToMessageId`. The option accepts a constant id or
  a `${...}` expression resolved per message; `.ReplyToIncoming()` is sugar for
  `replyToMessageId=${header.telegram.messageId}`. An explicit `telegram.replyToMessageId` header
  still wins; an expression that resolves to nothing sends the message unthreaded. A constant that
  is not a message id fails validation at endpoint start. Applies to `send` / `document` / `photo`.
- **`redb.Route.Telegram` — edit/delete target as an option: `messageId` (expression-capable) with
  fluent `MessageId(long)` / `MessageId(IExpression)`.** Chaining send → edit previously required a
  manual copy of `telegram.sentMessageId` into `telegram.messageId`; now
  `Tg.Edit(token).MessageId(Header(TelegramHeaders.SentMessageId))` does it. The header still wins.
- **`redb.Route.Telegram` — `answer` mode supports `showAlert`** (URI option, `.ShowAlert()` fluent,
  per-message `telegram.showAlert` header): the callback answer is shown as a modal alert instead of
  a toast.
- **`redb.Route.Telegram` — per-message `telegram.caption` header** for `document`/`photo`, wins
  over the `caption` option (consistent with `parseMode`/`fileName`).
- **`redb.Route.Telegram` — Mini App payloads (`WebApp.sendData`) are now surfaced: headers
  `telegram.webAppData` / `telegram.webAppButtonText`.** A `web_app_data` message carries no text,
  so the consumer previously handed such an update to the route with an empty body and no way to
  reach the payload short of parsing the raw `Update`. The data now becomes the exchange body — same
  contract as text messages and callback queries — and is also exposed as a header. Applies to both
  the long-polling and webhook paths (shared `TelegramUpdateMapper`); `telegram.messageType` is
  `"WebAppData"` for filtering.

### Fixed
- **`redb.Route.Telegram` — `document` / `photo` modes silently ignored `telegram.replyToMessageId`
  and `telegram.replyMarkup`.** Both headers were documented in the producer-headers table but only
  wired into `send`, so a photo with inline buttons or a document sent as a reply lost its markup /
  reply target. Both modes now pass them to the Bot API.

## [3.3.3] — 2026-07-15

> **Why the bump.** **No functional changes to redb.Route** — this is an ecosystem sync release, and
> the whole family is published at 3.3.3 (all 34 packages).
>
> Two reasons:
> 1. **The family had drifted apart.** Base sat at 3.3.1 while `Sql` and `Sqs` were at 3.3.2 from the
>    partial release below — so a shared-layer install mixed `3.3.1` and `3.3.2` archives side by side,
>    and "which versions go together" needed a table. From 3.3.3 every package in the ecosystem —
>    redb core, redb.Route, redb.Tsak — ships **one number**.
> 2. **It picks up `redb.Core` 3.3.3.** Not every package depends on redb storage — `redb.Route` itself
>    and the transports (Kafka, RabbitMQ, …) do not. But **`redb.Route.Core` and `redb.Route.Llm` do**,
>    and at 3.3.1 both pinned **`redb.Core` 3.3.0**, whose embedded `redb_init.sql` failed schema
>    initialization under a non-superuser database owner (see the redb core changelog). Since
>    `redb.Route.Core` is the package a redb-backed route worker is built on, that broken init reached
>    any deployment on a least-privilege database. All packages are rebuilt against `redb.Core` 3.3.3.
>
> The `Sql` / `Sqs` features listed under 3.3.2 below are **not** re-announced here — they shipped in
> 3.3.2 and are unchanged; those packages only change number.
>
> Also from this release, the shared assembly layer is distributed as **one archive per OS**
> (`redb-route-shared-3.3.3-<rid>.zip`, all 32 connectors inside) instead of one archive per connector.
> See `publish/ARCHITECTURE.md`.

## [redb.Route.Sql 3.3.2, redb.Route.Sqs 3.3.2] — 2026-07-14

> **Partial release.** Only these two connector packages are published at 3.3.2; the rest of the
> family — including `redb.Route` itself — stays at **3.3.1** and is unchanged. Both packages depend
> on `redb.Route >= 3.3.1`, so they drop into an existing 3.3.1 install without touching anything else.

### Fixed
- **`redb.Route.Sql` — `mode=Procedure` never took the procedure name from the URI path, which made
  the fluent `Sql.Procedure(...)` builder unusable.** `SqlBuilder.Build()` only ever emits the name
  into the URI path, while `SqlEndpointOptions.Validate()` demanded a separate `procedureName=`
  parameter — so **every** `Sql.Procedure("sp_x")` route threw
  `ArgumentException: ProcedureName is required for Procedure mode.` at endpoint creation, and the
  string-URI form had to repeat the name twice (`sql:sp_x?mode=Procedure&procedureName=sp_x`).
  `SqlComponent.CreateEndpoint` now falls back to the URI path when `procedureName=` is absent,
  which is what the `SqlEndpoint` xml-doc already promised ("The path part of the URI is the SQL
  query or stored procedure name"). An explicit `procedureName=` still wins, so nothing that works
  today changes behaviour. The bug survived because `Sql.Procedure` appeared in the docs but in no
  test and no route; an end-to-end test through the real `EndpointUriParser` now covers it.

### Added
- **`redb.Route.Sql` — `outputClass` now actually maps rows to a POCO.** The option was bound from
  the URI and silently ignored: `PocoRowMapper<T>` existed but was never instantiated, and every
  result came back as `Dictionary<string, object?>`. The new `SqlRowMapperFactory` resolves the type
  name (assembly-qualified, full, or short against loaded assemblies), verifies a public parameterless
  constructor, and caches the mapper. Producer: `SelectList` → a typed `List<T>`, `SelectOne` → `T`,
  `StreamList` → `IAsyncEnumerable<T>`. Poll consumer: the message body becomes the mapped POCO.
  The consumer still maps the raw row dictionary alongside the POCO, because header population and
  the `@name` auto-bind in `onSuccess`/`onFailure` run off the raw columns — a POCO would silently
  drop any column it has no property for. An unresolvable type name now fails loudly instead of
  being ignored. Unset (the default) → dictionaries, exactly as before.
- **`redb.Route.Sql` — `outputHeader` now delivers the result to a header instead of the body.**
  Also previously bound and ignored (`SqlProducer.SetResult` carried a "Check if result should go to
  header or body" comment and unconditionally wrote to the body). With `outputHeader=name` set, the
  query result lands in that header and the **incoming body is left intact** — which is what makes a
  SQL lookup usable as an enrichment step rather than a payload-destroying one. Supported for
  `SelectList`, `SelectOne`, `Scalar`, `StreamList`, and for `mode=Procedure&asFunction=true`.
  Unset → result replaces the body, exactly as before.
- **`redb.Route.Sqs` — SNS raw message delivery on the SNS→SQS auto-subscription.** New
  `rawMessageDelivery` option (`Sns.Topic(...).SubscribeSnsToSqs(arn).RawMessageDelivery()`, or
  `rawMessageDelivery=true` in the URI). When the SNS publisher auto-subscribes an SQS queue
  (`subscribeSnsToSqs=true`), it now also sets the subscription's `RawMessageDelivery=true`, so the
  queue receives the **bare payload** — and SNS message attributes map to SQS message attributes,
  restoring W3C trace continuity across the hop — instead of the default SNS JSON notification
  envelope (`{"Type":"Notification","Message":...}`) which the subscriber would otherwise have to
  unwrap. Default remains `false` (AWS-compatible envelope).

### Changed
- **`redb.Route.Sql` — README rewritten URI-first, and corrected where it contradicted the parser.**
  It documented SQL placeholders as `:id` / `:body`, but `SqlParameterParser` only ever recognised
  `@name`, and there is no implicit `@body` — a scalar body never binds itself into a parameter
  (use `param.msg=${body}`), and an unmatched placeholder silently becomes `DBNull`. The single URI
  example also omitted the mandatory `mode=Poll` for a consumer. The README now leads with string
  URIs for all three modes, documents the parameter-binding priority, and flags what remains
  unimplemented. See `docs/SQL_PROCEDURE_MODE_REGRESSION.md` for the full audit, including the
  options left alone on purpose (`transacted` is a no-op on producers, which always open a local
  transaction; `batchSize` is an on/off flag, not a chunk size; `SqlHeaders.GeneratedKeys` is never
  set but is kept because removing a public constant would break consumers).

## [redb.Route 3.3.1] — 2026-07-10

### Fixed
- **RabbitMQ — full AMQP basic-property round-trip (regression fix).** The producer forwarded only
  `CorrelationId` from headers — and by a bare name that didn't match what the consumer stamped —
  silently dropping `ReplyTo`/`MessageId`/`Priority`/`Expiration`/`Type`/`AppId`/`UserId`/`Timestamp`/
  `ContentEncoding`/`DeliveryMode` on a consume→produce hop. The producer now maps **every** settable
  string/byte `BasicProperties` field from headers via cached reflection (plus explicit `Timestamp` —
  an `AmqpTimestamp`, not `IConvertible` — and `Persistent`/`DeliveryMode`); the consumer stamps them
  symmetrically. Standard properties now use their **bare well-known names** (`ReplyTo`, `Priority`, …)
  so a round-trip carries them through with no docs needed; `redbRmq.*` is still accepted on the
  producer for back-compat and remains the prefix for delivery metadata (`Exchange`/`RoutingKey`/
  `DeliveryTag`/`Redelivered`/`ConsumerTag`).
- **AMQP 1.0 — property forwarding + `CorrelationId` round-trip.** The producer now forwards
  `MessageId`/`CorrelationId`/`ReplyTo`/`Subject`/`GroupId`/`To`/`ContentEncoding`/`ReplyToGroupId`/
  `UserId`/`GroupSequence`/`CreationTime`/`AbsoluteExpiryTime`/`Durable` from headers (header wins over
  option). `CorrelationId` was read by a bare name that didn't match the consumer's
  `redbAmqp.CorrelationId`, breaking round-trip — now aligned. Standard AMQP 1.0 properties use bare
  names (transport metadata stays `redbAmqp.*`); the consumer additionally stamps `ContentEncoding`/
  `To`/`ReplyToGroupId`/`UserId`.
- **Azure Service Bus — batch send now sets message properties.** `SendBatchAsync` created bare
  `ServiceBusMessage`s with no native/application properties; it now applies the same property mapping
  as single send (shared `ApplyProperties`), with a unique `MessageId` per batched message.
- **Redis — stream-field header prefixed.** The XADD field map was read by a bare `"StreamFields"`
  key, inconsistent with every other Redis header; now the prefixed `RedisHeaders.StreamFields`
  constant (bare name still accepted for back-compat).

### Changed
- **IBM MQ — MQMD header forwarding split into two tiers.** The standard, JMS-equivalent MQMD fields
  (`CorrelId`/`Priority`/`Expiry`/`Persistence`/`ReplyToQueue`+`ReplyToQueueManager`) now forward from
  headers **by default** (header wins) — matching what a WMQ JMS client maps from `JMSCorrelationID`/
  `JMSPriority`/`JMSExpiration`/`JMSDeliveryMode`/`JMSReplyTo`, so a naive consume→produce preserves
  them. The advanced/raw fields (`MsgType`/`Format`/`GroupId`/`MsgSeqNumber`) remain gated behind
  `MqmdWriteEnabled` (mirrors IBM MQ JMS `WMQ_MQMD_WRITE_ENABLED`) as they can alter message semantics.
  `MqmdReadEnabled` stays `true`.

### Added
- **Fluent DSL — `string` overloads on expression-first connector builders.** Value methods that
  accepted only `IExpression` (so a bare string literal wouldn't compile) now have additive `string`
  overloads across RabbitMQ, Sftp, Ftp, File, MqttNet, Kafka, Redis and Http (Ldap already had them).
  Each wraps the value in `StringExpression`, so `.Host("localhost")` works as a constant **and**
  `.RoutingKey("order.${header.type}")` still interpolates — full parity with the URI form. Purely
  additive; existing `IExpression` methods are unchanged.

## [redb.Route 3.3.0] — 2026-07-09

### Added
- **`redb.Route.Llm` — keyword knowledge retrieval (`IKnowledgeStore.SearchTextAsync`).**
  A case-insensitive substring search over stored chunks, alongside the existing embedding path
  (`SearchAsync`) — for corpora without embeddings or callers with no query vector (e.g. a small
  structured rule-set where exact terms beat semantic similarity). Added as a default interface
  member (source-compatible for external implementers). `RedbKnowledgeStore` pushes a **server-side
  `LIKE`** onto the indexed `_objects.note` column (`Contains(.., OrdinalIgnoreCase)` →
  `ComparisonOperator.ContainsIgnoreCase`), returns only the matched rows, and ranks that small set
  in-process by occurrence count (dropping rows that matched only the metadata envelope);
  `InMemoryKnowledgeStore` does the same over its dictionary.
- **`redb.Route.Llm` — prebuilt `knowledge_search` tool (`KnowledgeSearchTool`).** A ready
  `.AsLlmTool` route over `IKnowledgeStore.SearchTextAsync` so an agent can query the knowledge
  base itself: input `{query, top_k?, collection?}` → `{results:[{id, collection, score, text}]}`.
  The store is taken from `KnowledgeSearchOptions.Store` or resolved from the exchange's
  `IServiceProvider` (picks up `AddRedbLlmStorage()`'s store). `Collection` can be **pinned** so the
  model's `collection` argument is ignored — scoping an agent to a single tenant / document set.
  Lives in the main package (not `redb.Route.Llm.Tools`) because it depends on the engine's
  `IKnowledgeStore`; the utility-tools package stays Abstractions-only. When
  `KnowledgeSearchOptions.EmbeddingProvider` is set the tool runs **semantic** search (embeds the
  query → cosine `SearchAsync`); otherwise **keyword** (`SearchTextAsync`).
- **`redb.Route.Llm` — embeddings transport (`IEmbeddingProvider` + `OpenAiEmbeddingProvider`).**
  The retrieval half of RAG: turns text into vectors so the already-shipped `KnowledgeChunkProps` +
  `IKnowledgeStore.SearchAsync` (cosine) become usable. One OpenAI-compatible client
  (POST `{baseUrl}/embeddings`) over the same `LlmConnectionFactory` as `OpenAiProvider` — set
  `ModelId` to the embedding model (e.g. `text-embedding-3-small`) and it talks to OpenAI, Mistral,
  Together, DeepSeek, Gemini-compat, vLLM, llama.cpp, Ollama, LM Studio, … Preserves input order via
  the provider-reported `index`, `EmbedOneAsync` convenience for a single text.
- **`redb.Route.Llm` — `knowledge://` ingest scheme (`KnowledgeComponent`).** Producer-only:
  `To("knowledge://<collection>?chunkChars=1000&overlap=100&embed=true")` takes the exchange body as a
  document, chunks it (deterministic character windows with overlap), optionally embeds each chunk
  when an `IEmbeddingProvider` is registered, and upserts into the `IKnowledgeStore`. Chunk ids are
  `{docId}#{index}` (docId from `?docId=` or the `knowledge.doc.id` header) so a re-ingest of the same
  document replaces its chunks in place. Turns document loading into a route:
  `From("file://docs?include=*.md").To("knowledge://handbook")`.
- **`redb.Route.Llm` — `.Knowledge(collection, k)` retrieval DSL.** A route step that retrieves the
  top-K chunks for the current message and **injects them into the system prompt**
  (`LlmHeaders.SystemPrompt`), so a following `.To("llm://…")` answers grounded on them —
  `From("kafka://questions").Knowledge("handbook", k: 5).To("llm://claude")`. Semantic when an
  `IEmbeddingProvider` is available (embed query → `SearchAsync`), else keyword (`SearchTextAsync`);
  augments any existing system prompt; a no-op when no store is wired or nothing is retrieved (never
  breaks the pipeline). This completes the connector's RAG loop: `knowledge://` ingest → embeddings →
  keyword/semantic search → `knowledge_search` tool / `.Knowledge()` injection.
- **`redb.Route.Llm` — `embed://` scheme (`EmbedComponent`).** Embedding as a first-class route step,
  symmetric with `llm://`: `To("embed://<factory>")` turns the exchange body (a text → `float[]`, or a
  collection of texts → `float[][]`, order preserved) into vectors on `Out.Body`. The URI host names an
  `LlmConnectionFactory` (set `ModelId` to the embedding model), so different routes pick different
  embedding models by name — `From("kafka://texts").To("embed://openai").To("vector://sink")`.
  `EmbedComponent.ProviderFactory` is overridable (defaults to `OpenAiEmbeddingProvider.Create`).
- **New connector: `redb.Route.Sqs` — Amazon SQS + SNS** (native AWS SDK for .NET v4). One package,
  two schemes: `sqs://` (queue consumer + producer) and `sns://` (topic publisher + SNS→SQS fan-out).
  The SQS consumer does long-polling, `ConcurrentConsumers(N)` competing loops, visibility timeout with
  an optional extend-while-processing heartbeat, at-least-once acknowledgement (delete on success),
  transacted ack via `.Transacted()`, and FIFO. The producer does single + batch send and FIFO
  group/dedup ids; the SNS publisher supports subject / message structure / FIFO and an SNS→SQS
  auto-subscription. LocalStack / ElasticMQ compatible via `serviceUrl=`, full AWS credential chain,
  and W3C trace-context propagation across the hop. See `redb.Route.Sqs/README.md`.
- **New connector: `redb.Route.Telegram` — Telegram Bot API** (built on `Telegram.Bot`). Scheme
  `telegram://`: a long-polling consumer (`receive`, single `getUpdates` stream per token, one bot client
  shared per token) and a producer with `send` / `document` / `photo` / `edit` / `delete` / `answer`
  modes. Handles the 429 `retry_after` rate-limit contract (waits and retries), validates `parseMode`
  (throws on a typo instead of silently sending raw markup), supports inline / reply keyboards
  (`WithInlineKeyboard` / `WithReplyKeyboard`), a webhook-unpack pipeline (`UnpackTelegramUpdate`), and a
  fluent DSL (`Tg.Receive/Send/Document/...`). Delivery is **at-most-once** (Telegram advances the update
  offset on dispatch — documented; no redelivery); parallel processing via the `.Threads(N)` EIP; consumer
  telemetry + producer spans. See `redb.Route.Telegram/README.md`.
- **`controller.Redb()`** — extension on `RedbController` that resolves the per-request scoped
  `IRedbService` (its own connection) for the controller's current exchange. Use instead of
  `Context.GetRedbService()`, which returns the shared captive singleton.
- **`.Threads(N)` concurrency EIP (core `redb.Route`).** A Camel-style processing-concurrency stage:
  `From(...).Threads(N)…EndThreads()` caps a route section's concurrency at N, so a strictly serial
  source (poll consumers, MQTT, a single request thread) can process up to N exchanges at once — the
  general-purpose alternative to `.To("seda://x").ConcurrentConsumers(N)` without a named endpoint.
  **Adaptive by exchange pattern:** InOnly is a fire-and-forget hand-off (clone + worker pool, a
  transaction boundary like `.To("seda://")`); **InOut runs the body inline on the same exchange under a
  `SemaphoreSlim` gate**, so the reply — on `Out` or `In` — is preserved losslessly and request/reply
  (RPC) works across it (InOut is not a transaction boundary — the ambient transaction flows into the
  inline body). Options: `.MaxQueueSize(n)` and `.EnqueueTimeout(TimeSpan)` (default = wait for a free
  slot; on timeout throws `TimeoutException`). Ordering not preserved at N > 1. See `redb.Route/CONCURRENCY.md`.

### Fixed
- **`redb.Route.Llm` — non-ASCII text mangled on the wire (`\uXXXX` escaping).** Several JSON
  serializers used the default encoder, which escapes every non-ASCII char. On tool results this
  ~6×'d the tokens the model saw for Cyrillic / CJK content (and could surface literal `М…`);
  in the knowledge store it buried chunk text as `\uXXXX` in the `note` column, making the new
  keyword `LIKE` unable to match a raw non-ASCII query. Switched to
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` in `AgentEngine` (tool-reply + error serializers),
  `OpenAiProvider` (request body — matches `AnthropicProvider`, which already did this),
  `McpProtocol` (MCP JSON-RPC), and the `RedbKnowledgeStore` chunk envelope.
- **`redb.Route.Llm` — `RedbKnowledgeStore.UpsertManyAsync` (bulk ingest) was non-functional.**
  Two latent bugs on the never-exercised bulk path: (1) the existing-key lookup
  `keys.Contains(o.ValueString)` over a `string[]` threw `NotSupportedException` from the redb query
  parser (C# 13 binds it to `MemoryExtensions.Contains`); (2) a Props-hash "skip if unchanged"
  pre-check silently dropped every re-upsert of the property-less `KnowledgeChunkProps` (empty Props →
  constant hash). Fixed the parser (see `redb.Core` changelog) and removed the pre-check so bulk
  re-ingest actually updates chunk text/embeddings.
- **DI-scope / connection leaks on the exchange lifecycle.** Several paths created a per-exchange DI
  scope (which owns a redb DB connection) that could escape without being disposed: `ThreadsProcessor`,
  `SedaProducer` and `VmProducer` leaked the clone when the hand-off enqueue failed (cancellation /
  queue completed); `WireTapProcessor` leaked the tap clone when a user `onPrepare` / `newBody` callback
  threw before dispatch; the scheduled `llm://` and `exec://` consumers never disposed their per-tick
  exchange (a scope leaked on every fire). All now dispose in `finally` on every path. In addition,
  `Exchange.ReleaseScopes()` is now resilient to a throwing `DisposeAsync`: each cached scope is released
  in isolation (a fault is logged and the loop continues), so one broken scope can no longer strand its
  siblings — which would otherwise re-introduce the very connection leak this guards against.
- **Lazy producer start-up is now race-safe (no more cold-start `NullReferenceException` under concurrency).**
  On a cold start, concurrent exchanges reaching a not-yet-started producer could observe it half-initialised:
  `ConnectableProducer.Start()` set its `IsStarted` flag *before* `ConnectAsync()` completed, and `ToProcessor`
  handed out the producer as soon as it was created — before `Start()` finished. A second thread then called
  `Process()` on it (e.g. a Redis producer whose `_db` was still null → NRE). The bug was masked while sources
  were serial; it surfaced once `redb.Route.RabbitMQ` 3.2.2 made `ConcurrentConsumers(N)` truly parallel. Both
  paths are now single-flight — concurrent callers await the *same* startup, and a producer is observable as
  started only after `ConnectAsync()` has fully completed (its resources are ready). The dynamic-endpoint path
  (`toD`) was already safe via `Lazy<Task<IProducer>>`.
- **The default (unnamed) `IRedbService` is now resolved per exchange, not as a shared singleton.**
  `ProcessWithRedb(...)`, `SetBodyFromRedb(...)`, `SetHeaderFromRedb(...)` and
  `BeginRedbTransaction()` previously fell back to one `IRedbService` captured from the root DI
  provider — a single, non-thread-safe DB connection (EF-DbContext model) shared across every
  exchange. Under real concurrency (Splitter with parallel processing, SEDA, `ConcurrentConsumers(N)`,
  concurrent HTTP requests) two exchanges drove that one connection at once and the driver threw
  *"A command is already in progress"* / *"connection is busy"*. Each exchange now gets its own DI
  scope → its own scoped `IRedbService` → its own pooled connection, cached on the exchange and
  disposed with it. The unscoped singleton is used only when there is no exchange (single-threaded seed).

## [redb.Route.Amqp 3.2.1 · redb.Route.IbmMq 3.2.1] — 2026-07-04

> Targeted hotfix in **`redb.Route.Amqp`** and **`redb.Route.IbmMq`** only — each bumps
> **3.2.0 → 3.2.1**; every other `redb.Route.*` package is unchanged
> (`redb.Route.RabbitMQ` stays at 3.2.2, `redb.Route.Kafka` at 3.2.1, the rest at 3.2.0).
> Both depend on `redb.Route` 3.2.0 (no core change). Same class of bug as the RabbitMQ 3.2.2
> fix: `ConcurrentConsumers(N)` was a no-op.
>
> **Behaviour change to be aware of.** Before this release, an AMQP or IBM MQ consumer processed
> messages strictly one at a time regardless of `ConcurrentConsumers` — the option only sized an
> internal `SemaphoreSlim` that a serial receive loop never let engage. Now `ConcurrentConsumers(N)`
> runs **N real competing consumers**, so a route with `N > 1` processes up to N messages
> **concurrently**: per-destination ordering is no longer preserved on that route and its pipeline
> must be thread-safe. Routes at the default **1** stay strictly serial, exactly as before.

### Fixed

#### `redb.Route.Amqp` — `ConcurrentConsumers(N)` did nothing (serial receive loop)

The AMQP consumer ran a single receive loop that `await`ed `Process` inline before receiving the next
message, so only one message was ever in flight; the `SemaphoreSlim(ConcurrentConsumers)` was
acquired and released by that same loop and never saw a second holder — a dead gate. `Credit` (link
prefetch) only buffered deliveries; it never produced concurrent processing.

The fix runs `ConcurrentConsumers(N)` as **N independent competing consumers**, each with its own
AMQP `Session` + `ReceiverLink` + serial loop (AMQPNetLite sessions/links are not thread-safe, so
concurrency comes from N independent workers, never from sharing one link). RPC replies are sent on
the worker's own session; the processed-count is updated atomically. A new
`AmqpEndpoint.CreateDedicatedReceiverAsync` creates a receiver on a fresh dedicated session.
**Tuning note:** for even load-balancing across competing consumers, keep `Credit` low (e.g. `1`) —
a high credit lets one worker's prefetch buffer hoard the queue and drain it serially.

#### `redb.Route.IbmMq` — `ConcurrentConsumers(N)` did nothing (serial MQGET loop)

The IBM MQ consumer ran a single MQGET loop that `await`ed `Process` inline (`Get → Process → Get`),
so processing was strictly serial; the `SemaphoreSlim(ConcurrentConsumers)` was a dead gate (the count
was even wired correctly from options — unlike RabbitMQ where it was pinned to 1 — but the inline
`await` made it moot).

The fix runs `ConcurrentConsumers(N)` as **N independent competing consumers**, each with its own
dedicated `MQQueueManager` connection + destination handle + serial loop. This is required, not
cosmetic: the MQ managed client serialises MQI calls per connection and its transacted **syncpoint is
connection-scoped** (`Backout()` rolls back the whole connection), so each worker must own its
connection — which also makes transacted mode correct at `N > 1` (per-worker commit/rollback). Queues
are opened `INPUT_SHARED`, i.e. true competing consumers. **Topics are clamped to a single subscriber
with a warning** — N managed non-durable subscriptions would each receive a *copy* of every message
(duplicate delivery), not share the load; use a queue destination for competing consumers. RPC reply,
backout-queue routing and the transacted ack all use the worker's own connection; the processed-count
is atomic. (The unrelated ~500 ms managed-client MQGET poll-tick latency — a future MQCB rewrite — is
untouched.)

### Tests

- `redb.Route.Tests.Amqp` — new `AmqpConcurrencyTests`: `ConcurrentConsumers_ProcessesInParallel`
  (5 workers, `credit=1`, observed max-concurrency 5), `ConcurrentConsumersOne_ProcessesSerially`
  (max-concurrency 1), and `ConcurrentConsumers_AllMessagesProcessedExactlyOnce` (competing consumers
  share the queue — no duplication/loss). Full package suite: **133 passing** against ActiveMQ Artemis.
- `redb.Route.Tests.IbmMq` — new `IbmMqConcurrencyTests`: the same three shapes on `INPUT_SHARED`
  queues (observed max-concurrency 5 at `ConcurrentConsumers(5)`; 1 at the default; 24/24 delivered
  exactly once). Full package suite: **163 passing** against IBM MQ Developer Edition.

## [3.2.2] — 2026-07-03

> Targeted hotfix in **`redb.Route.RabbitMQ`** only — this package bumps to **3.2.2**;
> every other `redb.Route.*` package is unchanged (`redb.Route.Kafka` stays at 3.2.1, the
> rest at 3.2.0). RabbitMQ 3.2.2 still depends on `redb.Route` 3.2.0 (unchanged, already on
> NuGet) — no core changes, no mass-republish.
> Three items: (1) a fix for **consumer dispatch concurrency**, which was silently pinned to
> **1** so `ConcurrentConsumers(N)` never actually parallelised a queue; (2) a fix for a
> **channel leak** on per-route Stop/Start; (3) a new framework-level **`AutoAck`** consumer
> option (broker-side auto-acknowledge / at-most-once), the RabbitMQ analogue of Kafka's
> `EnableAutoCommit`.
>
> **Behaviour change to be aware of.** Before 3.2.2 *every* RabbitMQ consumer processed one
> message at a time regardless of `ConcurrentConsumers` (the real gate — the AMQP consumer
> dispatch concurrency — was stuck at 1). With 3.2.2 a route that sets
> `ConcurrentConsumers(N > 1)` now genuinely processes up to **N** messages **concurrently**,
> so per-queue message **ordering is no longer preserved** on such routes and their processors
> must be thread/concurrency-safe. Routes that leave `ConcurrentConsumers` at its default of
> **1** are unaffected — they stay strictly serial, exactly as before.

### Added

#### `redb.Route.RabbitMQ` — framework-level `AutoAck` consumer option

The RabbitMQ consumer gains a typed **`AutoAck`** option (default `false`), bound from the URI
(`?autoAck=true|false`) like every other endpoint option, plus a matching fluent
`RabbitBuilder.AutoAck(bool)` method.

When enabled, the consumer subscribes with `autoAck: true`, so the **broker settles every
delivery on hand-off** (at-most-once): there is no manual `BasicAck`/`BasicNack`, and a failure
in the processor does **not** requeue the message. This is the mirror of the manual-ack default
(at-least-once: ack after a successful turn, nack-requeue on failure) and the RabbitMQ analogue
of the Kafka `EnableAutoCommit` option shipped in 3.2.1 — it removes the need for a SEDA
hand-off stage when a route wants fire-and-forget "ack on receive" semantics (e.g. WSO2-style
`autoAck=true`).

`AutoAck` cannot be combined with `Transacted` — an auto-acked delivery is settled by the
broker on hand-off and cannot be transactionally committed or rolled back — so
`RabbitMQEndpointOptions.Validate()` now rejects that combination.

### Fixed

#### `redb.Route.RabbitMQ` — consumer dispatch concurrency was pinned to 1 (`ConcurrentConsumers` had no effect)

`RabbitMQEndpoint.CreateChannelAsync` built its channel with the three-argument
`CreateChannelOptions(...)` constructor, leaving the fourth parameter
(`consumerDispatchConcurrency`) at its **compile-time default**. In `RabbitMQ.Client` 7.2.1 that
default is **`Constants.DefaultConsumerDispatchConcurrency` = 1**, *not* `null` — verified against
the shipped assembly. A non-null per-channel value **overrides** the connection-level setting, so
every channel the library created was clamped to **serial dispatch**, and the value set on the
`ConnectionFactory` / URI (`consumerDispatchConcurrency=N`) was silently discarded — in both
named-factory and inline-connection modes.

The upshot: a single `AsyncEventingBasicConsumer` on a single channel received deliveries strictly
one at a time (`inFlight = 1`), and `ConcurrentConsumers(N)` — which only ever sized an internal
`SemaphoreSlim` — could not deliver any parallelism because the dispatcher never handed the
consumer more than one message at once. On production this showed up as a consumer that never kept
up: `unacked` climbed to the prefetch limit while messages were still processed serially.

The fix makes **`ConcurrentConsumers` the single knob for consumer-side parallelism**:
`CreateChannelAsync` now always passes `consumerDispatchConcurrency` explicitly, and the consumer
opens its consume channel with the dispatch concurrency set to `ConcurrentConsumers` (which also
sizes the semaphore). `ConcurrentConsumers(N)` therefore now processes up to N messages in
parallel. `ConsumerDispatchConcurrency` remains available as the connection-level default for other
channels and is no longer clobbered. A live-broker regression test
(`Consumer_ConcurrentConsumers_ProcessesInParallel`) publishes 20 messages, runs with
`ConcurrentConsumers(5)`, and asserts the observed maximum concurrency exceeds 1; a companion test
asserts `ConcurrentConsumers(1)` stays strictly serial.

#### `redb.Route.RabbitMQ` — AMQP channel leak on per-route Stop/Start

`RabbitMQConsumer.Stop()` cancelled its subscription (`BasicCancelAsync`), drained in-flight work,
and closed its dedicated RPC reply channel — but left its **main consume channel open** and still
registered in the endpoint's channel list. Closing that list is done only by
`RabbitMQEndpoint.Stop()`, which the engine invokes **only on full context teardown**, never on a
per-route `StopRoute`. Since `StartRoute` reuses the same consumer instance and opens a fresh
channel, **each Stop/Start cycle of an individual route leaked one channel** — a cancelled, idle
channel (no deliveries, `unacked = 0`, but still open with the prefetch showing) that accumulated on
the pooled connection until the whole context was disposed (tpkg hot-reload / container restart).

The consumer now **owns and releases its channel**: `Stop()` closes, disposes, and unregisters the
consume channel via a new `RabbitMQEndpoint.ReleaseChannelAsync`, and the Start-failure cleanup path
uses the same method (so a partial start no longer leaves a stale handle in the list either). Double
release is safe — `endpoint.Stop()` still guards on `IChannel.IsOpen`, and the list removal is
idempotent. This is a RabbitMQ-package-local fix; the core `RouteContext.StopRoute` is unchanged.
A regression test (`Consumer_StopStartCycles_DoNotLeakChannels`) runs five Stop/Start cycles on a
live broker and asserts the endpoint's tracked-channel count returns to 0 after every Stop.

### Tests

- `redb.Route.Tests.RabbitMQ` — new live-broker suite `RabbitMQConcurrencyLeakAutoAckTests`:
  `Consumer_ConcurrentConsumers_ProcessesInParallel` and
  `Consumer_ConcurrentConsumersOne_ProcessesSerially` (dispatch concurrency),
  `Consumer_StopStartCycles_DoNotLeakChannels` (channel leak), and
  `AutoAck_DeliversMessage` / `AutoAck_ProcessorThrows_MessageNotRequeued` /
  `ManualAck_ProcessorThrows_MessageRequeued` (AutoAck vs manual-ack requeue). Plus unit tests
  for the `AutoAck` builder param, its default-off, and the `AutoAck`+`Transacted`
  validation guard. Full package suite: **112 passing** against RabbitMQ 4.x.

## [3.2.1] — 2026-06-30

> The code changes in this release land in
> **`redb.Route.Kafka`** and **`redb.Route.RabbitMQ`** — and **only these two packages
> are bumped to 3.2.1**. Every other `redb.Route.*` package stays at **3.2.0** (no changes);
> a targeted hotfix, not a mass-republish. Kafka/RabbitMQ 3.2.1 depend on `redb.Route` 3.2.0.
> Three items: (1) a
> new framework-level **`EnableAutoCommit`** option on the Kafka consumer
> (default `true`) that brings Kafka offset-settle into parity with the RabbitMQ
> consumer's post-process ack; (2) a fix for a **double `BasicAck`** in the
> RabbitMQ consumer when a route-level `.Transacted()` wraps a non-transacted
> consumer; (3) a fix for the Kafka **transacted producer**, which threw
> `Local: Erroneous state` on the deferred send.
>
> **Behaviour change to be aware of.** Before 3.2.1 a Kafka consumer route
> committed its offset **only** at a transactional boundary (`.Transacted()` /
> `.CommitTransaction()`); a plain `From("kafka:...")` processed messages but
> never advanced the committed offset (the offset only moved on a graceful stop
> via the partitions-revoked commit). With 3.2.1 the default
> `EnableAutoCommit=true` commits the offset **inline after a successful turn**,
> so a plain consumer settles at-least-once exactly like the RabbitMQ consumer
> already did. Set `?enableAutoCommit=false` to restore the old "commit only at a
> transaction boundary" behaviour. Inside a transactional route the option is
> effectively ignored — the transaction owns the commit (see below).

### Added

#### `redb.Route.Kafka` — framework-level `EnableAutoCommit` consumer option

The Kafka consumer gains a typed **`EnableAutoCommit`** option (default `true`),
bound from the URI (`?enableAutoCommit=true|false`) like every other endpoint
option, plus a matching fluent `KafkaBuilder.EnableAutoCommit(bool)` method.

This is a **framework-level** setting, deliberately **not** librdkafka's own
`enable.auto.commit`: the underlying client stays at manual commit
(`EnableAutoCommit = false` in the built `ConsumerConfig`) **always**, so the
library never commits un-processed offsets on a background timer. Instead the
*redb.Route consumer* commits after it knows the turn succeeded:

- After a successful `Processor.Process(...)`, the consumer commits the offset
  **inline** (`IConsumer.Commit`) — mirroring the RabbitMQ consumer's
  `BasicAck`-after-Process. Single-message mode commits that message's offset;
  batch mode commits the **last** offset of the batch.
- **A transactional route takes precedence.** `KafkaCommitAction` now carries a
  `Committed` flag (idempotent `Interlocked` guard). When a `.Transacted()` /
  `.CommitTransaction()` boundary commits the deferred `KafkaCommitAction`
  *during* the turn, `Committed` is already set by the time `Process` returns, so
  the consumer **skips** the inline commit. The transaction owns the offset and
  `EnableAutoCommit` is effectively ignored — it only matters on non-transactional
  routes.
- On a failed turn (`Process` throws) the inline commit is skipped and the
  message is re-delivered — at-least-once, unchanged.

Net effect: Kafka and RabbitMQ consumers now share the same default mental model
("settle after a successful turn"). The previous behaviour — where a plain Kafka
consumer silently never advanced its committed offset — is opt-out via
`?enableAutoCommit=false`.

### Fixed

#### `redb.Route.Kafka` — `transacted=true` producer threw `Local: Erroneous state` on the deferred send

A Kafka producer with `transacted=true` set `config.TransactionalId` and called
`_producer.InitTransactions(30s)` on connect. That puts librdkafka into
**transactional mode**, where every `Produce` must be wrapped in
`BeginTransaction` … `CommitTransaction`. The connector never calls
`BeginTransaction` / `CommitTransaction` / `AbortTransaction` /
`SendOffsetsToTransaction`, so the deferred `ProduceAsync` — run when a
`.Transacted()` / `.CommitTransaction()` boundary commits the `KafkaSendAction` —
threw:

```
Confluent.Kafka.ProduceException: Local: Erroneous state
```

Confirmed end-to-end against a live 3-node KRaft cluster. The earlier tests
missed it because they only asserted the deferred action was *registered*, never
*committed*.

The fix drops `transactional.id` + `InitTransactions` from the transacted
producer path, leaving `EnableIdempotence = true` + `Acks = All`. The deferred
send is now a plain idempotent `Produce` deferred to the route boundary — which
delivers, and matches the connector's documented intent ("idempotent producer +
deferred commit, **not** EOS"). Real Kafka exactly-once
(`BeginTransaction` / `SendOffsetsToTransaction` / `CommitTransaction`) is scoped
in `docs/KAFKA_TRANSACTIONS_TODO.md`. A regression test
(`TransactedProducer_DeferredCommit_Delivers`) commits the deferred action and
asserts the message is actually delivered.

#### `redb.Route.RabbitMQ` — double `BasicAck` when a route `.Transacted()` wraps a non-transacted consumer

When a route-level `.Transacted()` segment wrapped a RabbitMQ consumer whose
**endpoint** was *not* `?transacted=true`, the delivery was settled **twice**:

1. The `TransactedProcessor` committed the deferred `RabbitMQAckAction` at the
   `.Transacted()` boundary → `BasicAck` #1.
2. The consumer's own post-process branch (`if (!_options.Transacted)`) then
   issued `BasicAck` #2 on the **same delivery tag**.

RabbitMQ rejects the duplicate settle with
`PRECONDITION_FAILED — unknown delivery tag` and **tears down the whole
channel**, so every subsequent message on that channel is silently dropped. The
error path carried the symmetric double-`BasicNack` hazard.

`RabbitMQAckAction` now carries a `Settled` flag (idempotent `Interlocked` guard
on both `Commit` and `Rollback` — first settle wins). The consumer routes its
inline ack/nack **through the same `RabbitMQAckAction`** and skips it when
`Settled` is already set, so the broker sees exactly one settle per delivery. The
guard covers the success path and both the inner and outer catch blocks.

### Tests

- `redb.Route.Tests.Kafka` — `Consumer_AutoCommitDefault_CommitsOffsetInline_BeforeStop`
  and `Consumer_AutoCommitDisabled_NoTransaction_DoesNotCommitInline`. Both
  inspect the **committed offset at the group coordinator** (via a non-subscribing
  probe consumer, so it never joins the group / triggers a rebalance) **while the
  consumer is still running** — i.e. before any graceful stop, so the
  partitions-revoked commit cannot mask the result. The first asserts the offset
  is committed inline with the default option; the second asserts it stays
  uncommitted with `?enableAutoCommit=false` and no transaction.
- `redb.Route.Tests.RabbitMQ` — `Consumer_RouteTransactionAcksDuringProcess_NoDoubleAck`.
  A processor commits `TRANSACT_ACTION` mid-`Process` (simulating the
  `.Transacted()` boundary), and the test asserts **both** published messages are
  processed — proving the channel survived the first ack instead of being torn
  down by a double settle.

## [3.2.0] — 2026-06-29

> All `redb.Route.*` packages are versioned
> together at **3.2.0**; the code changes in this release land in **`redb.Route`,
> `redb.Route.Llm`, `redb.Route.Llm.Tools`, `redb.Route.Llm.Mcp`,
> `redb.Route.Http`, `redb.Route.WebSocket` and `redb.Route.Exec`** (the other
> connectors are version-aligned, no code changes). The release bundles four areas of work: (1) end-to-end
> token-by-token streaming on the wire (`IAsyncEnumerable<string>` response
> bodies → SSE / chunked text over HTTP, one text frame per token over
> WebSocket); (2) REDB-backed stores for the remaining state surfaces
> (`IBatchStore`, `IEvalRunStore`, `IKnowledgeStore`,
> `IPromptTemplateRegistry`, `IToolCacheStore`); (3) async-batch callback
> plumbing (`LlmCallbackProcessor` + new `llm.batch.*` headers); (4) a thin
> DSL/tool split across the homeless tools in `redb.Route.Llm.Tools`. Plus a
> named-redb-per-exchange hint (`?redb=<name>`), the new
> `redb.Route.Llm.Mcp` MCP-client connector that brings the community
> ecosystem of Model Context Protocol servers into the agent toolset, and
> two targeted bug fixes (LLM agent loop orphan tool_use recovery, Exec OEM
> codepage on Windows). **No public API was removed or renamed.** The
> store-interface additions are optional parameters with defaults — existing
> implementations and call sites compile unchanged.

### Added

#### `redb.Route` — `ThrottleProcessor` / `KeyedThrottleProcessor` RFC 6585 §4 rejection mode

Both throttle processors gain an opt-in `rejectOnOverflow` constructor flag
(default `false` for backward compatibility) plus a matching fluent
`.RejectOnOverflow()` method on `ThrottleDefinition` and
`KeyedThrottleDefinition`. When set, overflow exchanges are short-circuited
with **HTTP 429 Too Many Requests** + a **`Retry-After`** header carrying
the current rate-limit period in delta-seconds (RFC 7231 §7.1.3), and a
small structured JSON body:

```json
{
  "error": "rate_limit_exceeded",
  "error_description": "Rate limit exceeded. Retry after 1 second(s).",
  "retry_after": 1
}
```

The behaviour is now selectable per-route in the DSL:

```csharp
// Legacy: silent semaphore-wait until a slot frees (still the default).
.Throttle(maxPerPeriod: 10)

// RFC 6585 — fast-fail with 429 + Retry-After. Recommended for HTTP-facing
// routes; the silent-wait variant looks like a hung server to clients.
.Throttle(maxPerPeriod: 10).RejectOnOverflow()

// Same option on the per-key (per-IP / per-client_id) variant.
.Throttle(e => e.GetClientId(), maxPerPeriod: 10, period: TimeSpan.FromSeconds(1))
    .RejectOnOverflow()
```

The non-blocking `SemaphoreSlim.Wait(0)` probe replaces the unconditional
`await WaitAsync(ct)` so a slot is only acquired when one is actually
available — the legacy mode still falls back to `WaitAsync` to preserve
the exact old timing for callers that didn't opt in.

Implementation lives in two places: the per-processor flag, and a shared
`ThrottleRejection.Apply(exchange, period)` helper that writes the 429
response (`redbHttp.ResponseCode`, `Retry-After`, JSON body) and calls
`exchange.Stop()` so no downstream processor (WireTap, tx commit,
idempotency capture) runs against the rejected exchange. Apache Camel's
`Throttler` has the same axis (`rejectExecution=true/false`) — this
brings the redb.Route EIP into parity.

The default stays `false` so every existing route continues to behave
exactly as it did in 3.1.0 and earlier; opting in is a per-route choice.

#### `redb.Route.Llm` / `redb.Route.Http` / `redb.Route.WebSocket` — end-to-end streaming wire contract for LLM token deltas

`LlmProducer.ProcessStreamingAsync` already emits an `IAsyncEnumerable<string>`
of provider token deltas into `exchange.Out.Body` when `?stream=true` (or the
`llm.streaming` header) is set on the LLM endpoint. As of 3.1.0 only the
producer surface existed; downstream transports buffered the enumerable into
a single response. **3.1.1 wires the contract end-to-end** so a route like

```csharp
From("http://+:8080/chat")
    .To("llm://claude?stream=true")
    // Out.Body is IAsyncEnumerable<string> here
    // HttpConsumer flushes each yield as one SSE 'data:' frame
```

streams token-by-token to the browser, and the equivalent WebSocket route

```csharp
From("ws://+:9001/chat")
    .To("llm://claude?stream=true")
    // Each yield → one WebSocketMessageType.Text frame, endOfMessage=true
```

streams token-by-token to the WebSocket client. No new types, no new options
— transports inspect `Out.Body` and pick the right wire shape.

**Producer-side contract** — `LlmProducer` now sets two response markers
alongside the streaming body:
- `Out.ContentType ??= "text/event-stream"` when not already set, so the HTTP
  transport defaults to SSE framing.
- `Out.Headers[LlmHeaders.Streaming] = true` (`"llm.streaming"`) — a stable
  signal any downstream component can branch on. Visible in `WireTap` /
  `Multicast` / audit routes.

Late-bound summary headers (`llm.tokens.in`, `llm.tokens.out`,
`llm.stop_reason`, `llm.tool.iterations`) are written **after** the
`IAsyncEnumerable` completes; `llm.provider.id` and `llm.model.id` are
written up-front. Transports collect them post-enumeration and surface them
in a transport-appropriate way (see HTTP `event: done` trailer below).

> **Scope.** The streaming path calls `ILlmProvider.StreamAsync` directly
> and bypasses `AgentEngine` — so tools (`?tools=`) are not dispatched,
> `AddRedbLlmStorage()` stores are not invoked, and governance hooks do not
> fire on a streamed turn. Use streaming for user-facing rendering of a
> single assistant turn; keep the non-streaming path when you need tools,
> persistence, approvals or budgets.

**`HttpConsumer` (`redb.Route.Http`).** Detects `Out.Body is
IAsyncEnumerable<string>` and picks one of two writers based on
`Out.ContentType`:
- `text/event-stream` → SSE: per-line `data: ` prefix, blank-line terminator
  per yield, response flushed per chunk. The stream ends with
  `event: done\ndata: {…json…}\n\n` whose payload is built opportunistically
  from whichever `llm.*` summary headers are present on the message at
  end-of-stream (`llm.tokens.in`, `llm.tokens.out`, `llm.cost.usd`,
  `llm.stop_reason`, `llm.tool.iterations`, `llm.model.id`,
  `llm.provider.id`) — missing headers are omitted from the JSON, custom
  ones (e.g. a pricing-table-derived `llm.cost.usd`) ride along for free.
- anything else → chunked plain text: one yield = one chunk on the
  Transfer-Encoding stream, no SSE framing, no trailer.

Both writers set the standard "do-not-buffer-me" envelope:
`Cache-Control: no-cache, no-transform`, `X-Accel-Buffering: no`, and
`IHttpResponseBodyFeature.DisableBuffering()`. This neutralises nginx and
similar reverse-proxy buffers and is what makes SSE actually progressive on
the wire (without `X-Accel-Buffering: no` nginx by default holds the whole
response until the upstream closes). Empty / null chunks are skipped — LLM
providers periodically emit empty SSE keep-alives that must not turn into
empty wire chunks. Client cancellation (`HttpClient` aborts the request)
propagates into the server-side `await foreach` via `HttpContext.RequestAborted`,
so the upstream provider stream is torn down promptly — no pinned upstream
sockets.

**`WsConsumer` (`redb.Route.WebSocket`).** Detects the same body type in the
InOut branch of `HandleWebSocket` and yields **one
`WebSocketMessageType.Text` frame per yield with `endOfMessage=true`**. Order
is preserved (the per-connection receive loop awaits each `SendAsync` before
reading the next inbound frame, so writes are naturally serial per socket);
empty chunks are skipped. The cancellation token is the consumer's
drain-safe `_drain.ProcessingToken`, so an in-flight stream completes during
a graceful stop. The non-streaming `ResolveResponseBody` path is unchanged
for non-`IAsyncEnumerable` bodies.

**Tests.** Two transport-level test suites pin the wire contract without
needing any LLM provider:
- `redb.Route.Tests.Http/HttpStreamingTests` — `Sse_PerChunkFlush_AndDoneTrailer`,
  `ChunkedPlain_NoSseFraming_NoTrailer`,
  `ChunksArriveProgressively_NotBuffered`, and
  `ClientCancel_PropagatesToEnumerator`. Verifies SSE line framing, the
  `event: done` JSON payload, progressive arrival (first byte well before
  last yield), and that aborting the `HttpClient` request surfaces on the
  server-side enumerator within seconds.
- `redb.Route.Tests.WebSocket/WsStreamingTests` —
  `Streaming_OneFramePerYield_OrderPreserved` and
  `Streaming_EmptyChunksSkipped`. Verifies one-frame-per-yield, ordered
  delivery, and that null / empty yields do not produce wire frames.

A new env-gated suite — `redb.Route.Tests.Llm/LiveStreamingTests` — exercises
`ILlmProvider.StreamAsync` end-to-end against real free-tier providers
(Anthropic Claude Haiku 4.5 via `AnthropicProvider` native SSE, plus Groq /
Cerebras / Gemini / Mistral / OpenRouter via `OpenAiProvider`). Each test
asserts more-than-one chunk on the wire (proves real streaming), at least one
text delta, the expected substring in the accumulated answer, and a non-null
terminal `StopReason`. Auto-skips when the corresponding key env var is
missing, same as `LiveProviderTests`.

#### `redb.Route.Llm` — REDB-backed stores for the remaining state surfaces

The agent loop ships in-memory defaults for every governance surface; 3.1.0
shipped REDB-backed `Conversation`, `Approval`, `CostBudget`,
`ToolIdempotency` and `AuditObserver` stores. 3.1.1 lands the rest:

- `RedbBatchStore` (`IBatchStore`) — tracks async-batch jobs submitted to
  Anthropic Message Batches / OpenAI Batch / vLLM batch endpoints; the
  callback webhook correlates back to the originating conversation through
  this store. Backed by the new `LlmBatchProps` schema.
- `RedbEvalRunStore` (`IEvalRunStore`) — persists evaluation runs by
  scenario / fingerprint for leaderboard queries.
- `RedbKnowledgeStore` (`IKnowledgeStore`) — RAG retrieval over the
  `KnowledgeChunkProps` schema.
- `RedbPromptTemplateRegistry` (`IPromptTemplateRegistry`) — versioned
  prompt store (the previous default was in-memory only).
- `RedbToolResultCache` (`IToolCacheStore`) — deterministic-tool result
  cache with TTL.

All five are opt-in through the same `AddRedbLlmStorage()` extension
(`ServiceCollectionExtensions` grew the appropriate `TryAddSingleton`
wiring) and ride on the existing `IRedbService` resolution path. A new
`ToolIdempotencyProps` schema replaces the ad-hoc storage shape used in
3.1.0 — see *Changed* below.

#### `redb.Route.Llm` — async-batch callback plumbing

- `LlmCallbackProcessor` — a vanilla `IProcessor` that consumes inbound
  webhook callbacks from async-batch LLM APIs. Wired into any HTTP route
  (no new URI scheme): resolves the batch id from header / query / JSON body,
  deduplicates via `IToolIdempotencyStore` (keyed `"batch:<id>"`),
  populates conversation / provider / model headers from the original
  submission stored in `IBatchStore`, and marks the batch completed. A
  duplicate callback sets `LlmHeaders.BatchDuplicate=true` so a downstream
  `Choice().When(...).Stop()` can drop it cleanly.
- New `LlmHeaders` constants: `BatchId` (`llm.batch.id`), `BatchStatus`
  (`llm.batch.status`), `BatchDuplicate` (`llm.batch.duplicate`),
  `ConversationMessageId` (`llm.conversation.message.id`).

#### `redb.Route.Llm` — named-redb hint per exchange (`?redb=<name>`)

The LLM connector now lets a route pin which named `IRedbService` instance
its persistence stores write to. Useful when one Tsak host runs multiple
LLM products against different DBs.

- New URI option `?redb=<name>` parsed into `LlmEndpointOptions.Redb`.
- New property key `LlmKeys.RedbName` (`llm.redb.name`) stamped onto
  `IExchange.Properties` by `LlmProducer`; storage implementations resolve
  the redb instance via `IRouteContext.GetRedbService(name, exchange)`.
- Every `I*Store` method gained an optional `IExchange? exchange = null`
  parameter so REDB-backed implementations can read this hint without
  changing call sites — in-memory implementations ignore it. **Source-
  compatible**: every interface change is an optional parameter with a
  default; existing implementations and call sites compile unchanged.
- Default `unnamed` `IRedbService` from the route context is used when no
  hint is set, matching the 3.1.0 behaviour exactly.

#### `redb.Route.Llm.Tools` — DSL / tool split

Each homeless tool was reshaped into a thin `IProcessor` (`*Tool.cs`) plus
a fluent route-DSL extension (`*Dsl.cs`) that mounts the processor with
typed options. Affects `HttpFetchTool`, `JsonPathTool`, `MathEvalTool`,
`RegexExtractTool`, `TavilyWebSearchTool`, `XPathTool`. The user-visible
DSL shape is:

```csharp
From("direct:fetch-weather")
    .AsLlmTool("get_weather")
        .Description("Fetches weather for a URL.")
        .Input("""{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""")
    .Then()
    .HttpFetch(new HttpFetchOptions { HostAllowlist = ["api.weather.gov"] });
```

A new shared helper `LlmToolJson` centralises the small JSON-payload
parsing / writing that every tool was duplicating. The split keeps the
agent-engine surface unchanged — tool descriptors and registry stay the
same; only the way you wire a tool *into a route* moves to a one-line DSL
call.

#### `redb.Route.Llm` — small additions

- `LlmMetrics` exposes one more counter for stream chunks alongside the
  existing call / iteration / token meters.
- `LlmConsumer` honours the same `?redb=` hint when scheduling a
  `From("llm://...")` agent run, so scheduled agents persist into the same
  named DB as inbound producer calls.
- `Engine/PromptRef`, `Engine/Eval/LlmEvalRunner` updated for the new
  store signatures.

#### `redb.Route.Llm` — xAI Grok provider alias

`OpenAiProvider.ResolveDefaultBaseUrl` gains a `"grok"` / `"xai"` alias
that resolves to `https://api.x.ai/v1/`. No other changes: tool calls,
streaming, budget enforcement and conversation memory work identically to
every other OpenAI-compatible provider.

```csharp
new LlmConnectionFactory("grok")
{
    Provider = "grok",
    ModelId  = "grok-3-mini",
    ApiKey   = Environment.GetEnvironmentVariable("REDB_LLM_GROK_KEY")
}
```

`LiveProviderTests` extended with five Grok scenarios (Smoke / NonAscii /
ToolUse / Usage / StopReason), gated on `REDB_LLM_GROK_KEY`.

#### `redb.Route.Llm` — per-message audit fields on `MessageProps` (compliance / replay)

Every assistant turn now persists the full set of inputs the provider call
was made under. This closes the audit gap that previously forced auditors
to trust that "the system prompt and sampling settings were the same as the
ones currently in config" — now they're stamped on the row that produced
the answer.

`MessageProps` (and its mirror `ConversationMessageMeta`) gain seven
nullable columns:

| Field | Set on | Purpose |
|---|---|---|
| `Temperature`, `MaxTokens`, `TopP` | assistant rows | effective sampling values after merging request + factory defaults |
| `PromptTemplateName`, `PromptTemplateVersion` | every row in the run | FK pair into `PromptTemplateProps` — pins the exact prompt text |
| `ToolSetHash` | assistant rows | SHA-256 of the canonical (name + description + InputSchema) of the tool set exposed on this call; detects tool-surface drift across runs |
| `ProviderSystemFingerprint` | assistant rows | OpenAI's `system_fingerprint` (and any echoing OpenAI-compatible provider — xAI, Together); null on Anthropic / Gemini-compat / Ollama |

Wiring:

- `AgentRequest` gains `PromptTemplateName` + `PromptTemplateVersion`.
  Callers that resolve a managed prompt template via
  `IPromptTemplateRegistry` set the pair so the engine can stamp it on
  every persisted message of the run.
- `AgentEngine` computes `ToolSetHash` once per run (canonical sort by
  name, raw `InputSchema` folded in verbatim — schema string changes show
  up as hash drift, which is exactly the auditor signal) and pipes it
  alongside the effective `Temperature` / `MaxTokens` / `TopP` into
  `PersistMessageAsync`.
- `OpenAiProvider.CompleteAsync` reads `system_fingerprint` from the
  response root and surfaces it on `LlmResponse.ProviderSystemFingerprint`;
  the engine forwards it to the assistant message row.
- `RedbConversationStore` writes the seven fields into `MessageProps` on
  append and rehydrates them on load; nothing else in the persist /
  materialise path changes.

Because every new column is nullable on both `MessageProps` and
`ConversationMessageMeta`, existing rows and existing call sites compile
and load unchanged. **No migrations required** — REDB picks up the new
props automatically.

> **What this still cannot solve.** Closed-source provider drift where the
> backend does not surface a fingerprint (Anthropic, most Gemini-compat
> endpoints): when the provider silently re-releases a model under the
> same id, no per-message capture on our side can detect it. For
> compliance-bound deployments the only honest answer remains self-hosted
> (`ollama`, `lmstudio`, vLLM via `huggingface`) — the alias surface for
> those is unchanged.

#### `redb.Route.Llm` — `UserId` + free-form `AuditTags` on every persisted row

The 3.1.1 audit-fields work above pinned the *machine* side of the call (model
id, prompt hash, tool-set hash, sampling settings). This follow-up extends the
same `MessageProps` row with the *human / governance* side — **who** issued
the call and **under what business labels** — so a single row answers the
auditor's full question without joining to anything external.

`MessageProps` (and the mirror `ConversationMessageMeta`) gain two more
nullable columns:

| Field | Type | Purpose |
|---|---|---|
| `UserId` | `string?` | Principal id stamped on every row of the run — pulled from the producer's `?user=` URI option (literal or `${header.X}` expression) or, falling back, the `llm.user.id` header. |
| `AuditTags` | `Dictionary<string,string>?` | Free-form `key → value` audit labels stamped on every row. Sources merged at producer time: the `?audit=key=val,key=val` URI CSV (each side URL-encoded so commas/equals in literal values are safe) ⊕ inbound `llm.audit.<name>` headers; **headers win on collision** so per-call dimensions can override per-route defaults. |

`AuditTags` is a real REDB Pro `Dictionary<string,string>` — not JSON. That
means the column is queryable through native LINQ-to-SQL (see
`redb.Examples/E060_DictContainsKey`, `E061_DictIndexer`,
`E062_DictNestedClass`):

```csharp
// Pull every row that came from a specific tenant, server-side, no client scan:
var rows = await redb.Query<MessageProps>()
    .Where(m => m.AuditTags!["tenant"] == "acme-prod"
             && m.UserId == "alice@acme.com")
    .ToListAsync();
```

DSL surface — three new fluent methods on `LlmBuilder`:

```csharp
.To(LlmDsl.Factory("haiku")
    .User("${header.X-User-Id}")          // principal — literal or ${header.X}
    .Audit("tenant", "${header.X-Tenant}") // repeatable, dynamic
    .Audit("env",    "prod")              // repeatable, literal
    .PromptTemplate("triage", "v1")       // (name, version) pinned per row
    .AsUri())
```

Wiring (additive, no breaking changes — every new field is nullable on both
DTOs and every public method keeps its existing signature):

- `LlmHeaders` gains `UserId = "llm.user.id"` and `AuditTagPrefix = "llm.audit."`.
- `LlmEndpointOptions` gains `User`, `Audit` (CSV), `PromptTemplateName`,
  `PromptTemplateVersion`. Bound from URI by reflection like the rest of the
  options — no parser change.
- `LlmProducer` resolves `${header.X}` / `${property.X}` / literal expressions
  pre-call against the inbound exchange, merges the `?audit=` CSV with any
  `llm.audit.<name>` headers (header wins on collision), and pipes the
  resolved values through `AgentRequest`.
- `AgentEngine.PersistMessageAsync` reads `request.UserId` / `request.AuditTags`
  and stamps both onto every `ConversationMessageMeta` it creates — same row
  cardinality (system / user / tool-result / assistant), no extra writes.
- `RedbConversationStore` writes both fields on append and rehydrates them on
  load. `Dictionary<string,string>` materialises through the framework's
  native dict serialiser; no custom JSON path on either side.

Demo: see [`demos/Llm.AuditShell/`](demo/Llm.AuditShell/) — single-file
HTTP shell that exposes both option-side defaults and header-side overrides,
plus the swap comment for `RedbConversationStore` and the LINQ-by-AuditTags
query above.

#### `redb.Route.Llm` — operator-side audit fields: factory alias, base URL, provider response id, latency, key fingerprint, retry count

Building on the `UserId` + `AuditTags` row above, this slice closes the
"which connection actually answered, and how long did it take?" gap on the
*operator* side — the dimensions a host already knows pre-call but had to
reconstruct from logs after the fact. Six more nullable columns on
`MessageProps` / `ConversationMessageMeta`:

| Field | Type | Stamped on | Source |
|---|---|---|---|
| `FactoryName` | `string?` | every row | `LlmConnectionFactory.Name` (the operator-chosen profile alias, e.g. `"haiku"`, `"gpt-mini"`) |
| `BaseUrl` | `string?` | every row | `LlmConnectionFactory.BaseUrl` (null → provider default endpoint was used) |
| `ProviderResponseId` | `string?` | assistant rows | provider response top-level `id` (OpenAI / xAI / Together / Anthropic) |
| `LatencyMs` | `long?` | assistant rows | wall-clock around `ILlmProvider.CompleteAsync` |
| `ApiKeyFingerprint` | `string?` | every row | SHA-256 of `LlmConnectionFactory.ApiKey`, first 16 hex chars (non-secret) |
| `RetryCount` | `int?` | every row | route-framework retry counter on the inbound exchange (see fallback chain below) |

Why this matters in audit: when a host registers multiple connections to the
same provider — different keys per tenant, separate quotas per environment,
old/new key during rotation — the (provider, model) pair is no longer enough
to answer "which configured connection served this row?". `FactoryName` +
`BaseUrl` + `ApiKeyFingerprint` make the answer one column lookup.
`ProviderResponseId` cross-references the provider's own usage / billing
logs; `LatencyMs` lets a compliance pull profile p99 per (tenant, model)
without touching application telemetry.

LINQ-to-SQL example — slow-tail rows for a specific configured connection:

```csharp
var slowGoldRows = await redb.Query<MessageProps>()
    .Where(m => m.FactoryName == "haiku"
             && m.AuditTags!["tier"] == "gold"
             && m.LatencyMs > 5000)
    .OrderByDescending(m => m.LatencyMs)
    .ToListAsync();
```

Wiring (additive, every field nullable on both DTOs):

- `LlmResponse` gains `ProviderResponseId`. Both providers — `OpenAiProvider`
  and `AnthropicProvider` — parse the top-level `id` from the response JSON.
- `AgentEngine.PersistMessageAsync` accepts two new optional parameters
  (`providerResponseId`, `latencyMs`). The assistant-persist site passes
  `last.ProviderResponseId` and the stopwatch elapsed around the provider
  call; non-assistant sites pass `null, null`. `FactoryName` / `BaseUrl` /
  `ApiKeyFingerprint` are computed inside `PersistMessageAsync` from
  `request.Factory` so every row of the run carries them with no per-call-site
  plumbing change.
- `ApiKeyFingerprint` is computed via `SHA256.HashData` of the UTF-8 key
  bytes, then `Convert.ToHexString(hash[..8]).ToLowerInvariant()` → 16 hex
  chars. Empty / null key → null fingerprint. The key itself never reaches
  the conversation store.
- `RetryCount` is read once at the top of `RunAsync` from the inbound
  exchange and stamped identically on every row of the turn. Source order:
  `exchange.Properties["RetryAttempt"]` (set by `RetryProcessor` for the
  per-step `.Retry(...)` DSL), then `exchange.In.Headers["CamelRedeliveryCounter"]`
  (set by `OnExceptionProcessor`), then
  `exchange.In.Headers["CamelDeadLetterRedeliveryCount"]` (set by
  `DeadLetterProcessor`). Null when none are present (first / only delivery).
- `RedbConversationStore` writes and rehydrates all six fields. No schema
  migration needed — REDB stores added props on existing rows transparently.


#### `redb.Route.Llm.Mcp` — new package — MCP-client connector for the agent toolset

`redb.Route.Llm.Mcp` is a producer-only NuGet that lets the agent consume
the **community ecosystem** of Model Context Protocol servers (filesystem,
git, fetch, github, sqlite, Serena, …) without writing a C# adapter per
server. The package adds the `mcp://` URI scheme — `mcp://serverName/toolName`
invokes `tools/call` on the named MCP server with the exchange body as JSON
arguments — and wires a hosted service that, on host startup, spawns each
registered server, performs the `initialize` + `tools/list` handshake, and
projects every remote tool into the existing `IToolDescriptorRegistry` as
an `McpToolDescriptor : ILlmToolDescriptor`. The agent picks them up via
DI like any native tool.

Because every MCP tool becomes a regular `LlmToolCapability`, the existing
audit (`ToolSetHash`), governance (`Safety` overrides per `(server, tool)`
regex), observability (`OnToolInvokedAsync`) and approval pipeline apply
verbatim — no parallel code paths.

**Transports.** `McpTransport.Stdio(command, args, env, workDir)` spawns an
external process and exchanges newline-delimited UTF-8 JSON-RPC frames over
stdin/stdout (stdin writes serialised through a `SemaphoreSlim`, stderr
drained to the logger at trace, stdout pump skips non-JSON lines). The
encoding is BOM-less UTF-8 (`UTF8Encoding(false)`) — the static
`Encoding.UTF8` emits a BOM on first WriteLine and many MCP servers
(Serena, Anthropic reference) reject the BOM-prefixed first frame as
invalid JSON. `McpTransport.Http(baseUrl, apiKey)` POSTs JSON-RPC to the
base URL and opens an SSE channel for server-initiated frames
(`notifications/tools/list_changed` triggers a registry rebuild).

**Cancellation.** `IProducerTemplate.RequestBody(uri, body, ct)` (the
CT-aware overload) threads the cancellation token through `IProducer.Process`
and into `IMcpClient.CallToolAsync(ct)`. On cancel the client emits a
JSON-RPC `notifications/cancelled` for the pending request id and removes
the TCS so callers stop waiting.

**Tool name budget.** Provider tool-name caps (Anthropic / OpenAI) max at 64
chars. `McpToolDescriptor.BuildModelFacingName(server, tool)` sanitises both
parts to `[a-zA-Z0-9_]`, truncates the server prefix to 24 chars and the
tool to 36, and joins with `__` (e.g. `serena__get_symbols_overview`).
Duplicates after truncation are logged and skipped.

**Wiring.**

```csharp
services.AddRedbRoute()
        .AddRedbRouteLlm()
        .AddRedbRouteMcp()
        .AddMcpServer("serena", McpTransport.Stdio(
            "uvx",
            ["--from", "git+https://github.com/oraios/serena",
             "serena", "start-mcp-server",
             "--context", "ide",
             "--project", projectPath]));
```

The hosted service registers before `RouteHostedService`, so descriptors
are in the registry by the time routes compile.

**Status / liveness.** `IMcpClient.Status` exposes
`Idle / Connecting / Healthy / Restarting / Dead`; the producer
short-circuits with `McpException` when a registered client is `Dead` (no
silent hangs on a torn-down transport).

#### `redb.Route.Llm` — `IProducerTemplate.RequestBody(uri, body, ct)` CT-aware overload

`IProducerTemplate` gained a third overload that accepts a
`CancellationToken`. Existing call sites that use the two-argument form
compile unchanged (the no-CT overload remains as a `ct: CancellationToken.None`
shim). `AgentEngine.DispatchToolEndpointAsync` now threads its run-level CT
through to the producer, so an aborted agent iteration cancels the in-flight
tool RPC at the transport layer instead of waiting for it to finish before
unwinding.

### Changed

- **`redb.Route.Llm` I*Store contracts.** Every store interface in
  `redb.Route.Llm/Engine/Storage/*` (`IApprovalStore`, `IConversationStore`,
  `ICostBudgetStore`, `IEvalRunStore`, `IKnowledgeStore`,
  `IPromptTemplateRegistry`, `IToolCacheStore`, `IToolIdempotencyStore`)
  gained an optional `IExchange? exchange = null` parameter to thread the
  named-redb hint through. **Source-compatible**: optional with default,
  existing implementations / call sites compile unchanged.
- **`ToolIdempotencyProps` schema** — the per-tool-call idempotency rows
  moved from the generic `ToolCacheProps` shape to a dedicated
  `ToolIdempotencyProps` schema with explicit lifecycle fields. The two
  surfaces previously shared one table; splitting them lets the cache TTL
  and the idempotency receipt evolve independently. **No data migration
  shipped** — early-3.1.x adopters running `AddRedbLlmStorage()` against
  populated data should treat this as fresh state (the wider rollout
  happens with the `Phase 2` story, where stores get their migration
  helpers).

### Fixed

#### `redb.Route` — `ProducerTemplate.SendAsync` / `RequestBody` auto-start the resolved producer

`IProducerTemplate.SendAsync(endpointUri, …)` resolved an endpoint via
`Context.GetEndpoint(uri)` → cached an `IProducer` via `endpoint.CreateProducer()` →
called `producer.Process(exchange)` directly. For `DirectVm` / `Direct` /
`Seda` producers this was fine because they don't extend `ConnectableProducer`,
but for **every** other transport (`HttpProducer`, `KafkaProducer`, `AmqpProducer`,
`AzureServiceBusProducer`, `MqttNetProducer`, `RabbitMqProducer`, `RedisProducer`,
`SmtpProducer`, `LdapProducer`, `WmqProducer`, …) `EnsureStarted()` threw

```
InvalidOperationException: <name> has not been started. Call Start() first.
```

because `ConnectableProducer.Process` requires `Start()` to flip the started
flag and call `ConnectAsync` first. The cached producer was created but
never started, so `SendAsync` was effectively broken for every connection-
based transport — direct-vm-only scenarios masked the gap.

`ProducerTemplate.SendAsync(IEndpoint, IMessage)`,
`ProducerTemplate.SendAsync(IEndpoint, object)`,
`ProducerTemplate.RequestBody(IEndpoint, object, ct)`, and
`ProducerTemplate.RequestBody(IEndpoint, IMessage, ct)` now call
`await producer.Start(ct).ConfigureAwait(false)` between
`GetOrCreateProducer` and the first `Process` call. `ConnectableProducer.Start`
short-circuits via `Interlocked.CompareExchange` on the started flag, so the
extra call is a one-time setup per producer / process-lifetime and a no-op
on every subsequent send.

This is the seam that unblocked outbound HTTP webhook delivery in
`redb.Identity` (W1 / outbound webhook subscriptions) — the identity events
route hands the message to `ProducerTemplate.SendAsync(subscription.Url, …)`
and the URL scheme (`https://…`, `kafka://…`, `amqp://…`) resolves to the
right transport without the Identity codebase touching `IHttpClientFactory`
or any transport-specific surface.

#### `redb.Route.Controllers` — `HttpControllerDispatcher.WriteResult` clears `Out.Body` when the controller returns `null`

When an HTTP controller returns `null` (intended: no response body → 204
No Content), `HttpControllerDispatcher.WriteResult` initialised the
response via:

```csharp
exchange.Out ??= exchange.In.Clone();
var defaultCode = result is null ? 204 : 200;
if (result is not null) { exchange.Out.Body = result; }
```

The `Out ??= In.Clone()` carried `In.Body` across. For HTTP DELETE / HEAD
requests with `Content-Length: 0` `In.Body` is `Array.Empty<byte>()` —
non-null `byte[]`. With `result is null` the if-branch was skipped and
`Out.Body` stayed as that empty `byte[]`. Downstream the HTTP consumer
matched `body is byte[]` and called `Response.Body.WriteAsync(...)`,
which on Kestrel hard-throws for 204 per RFC 7230 §3.3.3 / RFC 9112 §6.1
(*"Writing to the response body is invalid for responses with status
code 204"* from `HttpProtocol.FirstWriteAsyncInternal` — fires even for
zero-length writes). Earlier pipeline side-effects (database mutations,
audit events) had already committed, so clients saw a torn TCP response
instead of a clean 204.

The dispatcher now explicitly nulls `exchange.Out.Body` in the `result is null`
branch. The request body is input; it must not echo into the response.

Symptom observed on SCIM `DELETE /Users/{id}` (RFC 7644 §3.6 mandates
204) but the bug is generic to any controller that signals 204 by
returning `null`.

#### `redb.Route.Http` — `HttpConsumer.WriteResponse` skips body write for 204 / 304 / 1xx

Defense-in-depth companion to the dispatcher fix above. RFC 7230 §3.3.3 /
RFC 9112 §6.1 require that 1xx, 204, and 304 responses MUST NOT contain a
message body, and Kestrel hard-throws on `Response.Body.WriteAsync` for
those status codes — even on zero-length writes. `HttpConsumer.WriteResponse`
called `WriteAsync` unconditionally when `body is byte[]` and tore the
TCP response if any upstream layer set a body for those statuses.

The consumer now resolves the response status code before reaching the
body-write branches and short-circuits with an intentional no-op when
the status is 204, 304, or any 1xx. The header copy above the body
block still propagates `Location` / `ETag` / `Set-Cookie`, which is the
only legitimate payload for these status families. Silently dropping a
non-empty body for these codes is safer than letting Kestrel kill the
response mid-flight — a producer with a bug to fix is a less acute
symptom than a torn TCP connection visible to clients.

#### `redb.Route` — parallel `Splitter` / `Multicast` branches isolate the ambient transaction per branch

When a parallel `Splitter` (`.Split(...).Parallel()`) or `Multicast`
(`MulticastProcessor`, **parallel by default**) runs inside a `.Transacted(...)`
segment, every branch was dispatched with `Task.Run` — which flows the caller's
`ExecutionContext`. Because the route's `TransactionScope` uses
`TransactionScopeAsyncFlowOption.Enabled`, all branches observed and could
concurrently enlist resources in the **same** `Transaction.Current`.
`System.Transactions` forbids concurrent use of a single transaction across
threads: a second concurrent enlistment of a resource that participates in the
ambient transaction (SQL / ADO.NET / redb DB work) either promotes to MSDTC or
throws *"transaction context in use by another thread"*.

Each parallel branch now runs under its own
`Transaction.Current.DependentClone(DependentCloneOption.BlockCommitUntilComplete)`
(new internal `DependentTransactionBranch` helper): the dependent clone is a
private ambient transaction for that branch's thread, and the parent's commit
blocks until every branch signals completion, so a branch's writes are never
committed half-finished. No-op when there is no ambient transaction (the common
non-transacted path runs with zero overhead).

Broker transports that defer via `Properties["TRANSACT_ACTION"]`
(Kafka / RabbitMQ / Redis / Azure Service Bus / AMQP) were **never** affected:
they do not enlist in `System.Transactions`, and each registers under a
per-message-unique key (`kafka-send-{guid}`, `rabbitmq-ack-{deliveryTag}`,
`asb-ack-{sequence}`, …), so concurrent fan-out branches to the same endpoint
accumulate distinct entries in the thread-safe dictionary and commit/roll back
atomically. The stale `TransactedProcessor` doc comment that claimed keys were
"typically the endpoint URI" has been corrected.

#### `redb.Route` — detached branches (WireTap, Debounce) no longer leak the caller's transaction/trace context

`WireTapProcessor` (fire-and-forget `Task.Run`) and `DebounceProcessor`'s
quiet-period flush (`Task.Delay(...).ContinueWith(...)`) both dispatch
downstream work on a thread-pool continuation that, by default, **inherits
the caller's `ExecutionContext`**. Two ambient values flowed across that
boundary and were unsafe once the originating route call had unwound:

- **`System.Transactions.Transaction.Current`** — routes wrap segments in a
  `TransactionScope` created with `TransactionScopeAsyncFlowOption.Enabled`
  (`TransactionPolicy.CreateScope`), so the ambient transaction flows. A
  detached branch starting **after** the scope completed/disposed still saw
  the leaked `Transaction.Current`; any producer/DB code that auto-enlists
  threw *"the current TransactionScope is already complete"*. A WireTap or
  Debounce nested inside a `.Transacted(...)` segment is the reproducer.
- **`System.Diagnostics.Activity.Current`** — the originating span, likewise
  already stopped, so telemetry the branch emitted was parented to an ended
  span (a child whose start time post-dates its parent — a "tail" hanging
  off a request that already returned).

Both branches now route through a new internal `DetachedDispatch` helper.
`Capture()` snapshots the trace context + transaction presence on the
**originating** thread; `Enter()` runs at the top of the branch body and
(a) opens a `TransactionScope(Suppress)` so the branch runs with **no**
ambient transaction (only when one actually leaked — zero cost otherwise),
and (b) re-roots telemetry as a **fresh root span linked** (`ActivityLink`)
to the originating trace — correlation is preserved without the broken
parent lifecycle. Non-transaction AsyncLocal state (user/auth context) is
deliberately left flowing, since a detached audit branch usually needs it.

Additionally, `WireTapProcessor` now strips the deferred-transport-action
dictionary (`Properties["TRANSACT_ACTION"]`) from its clone: `Exchange.Clone()`
copies `Properties` shallowly, so the tap clone previously shared the **same**
`ConcurrentDictionary` that the owning `TransactedProcessor` commits/rolls
back, and a tap mutating it could race the main commit. The tap runs with the
transaction suppressed, so it has no part in that set.

`ThrottleProcessor` / `KeyedThrottleProcessor` were audited and are **not**
affected — their detached `ContinueWith` only releases a rate-limit semaphore
slot; the downstream `Process` is awaited inline. The concurrent-enlistment
behaviour of **parallel** `Splitter` / `Multicast` branches (all sharing one
ambient transaction under `Task.WhenAll`) is a distinct concern tracked
separately and intentionally out of scope here.

#### `redb.Route.Http` — concrete route paths now out-rank catch-all on the same `(host, port)`

`SharedHttpServerManager` matched routes in pure **registration order** and
returned the first whose template matched. A catch-all (`/{**path}`)
therefore swallowed every route registered after it on the same listener —
a concrete path such as `/api/echo` could never win once a `{**path}`
dispatcher was already registered (acute when the catch-all auto-starts at
boot and the specific route registers later, e.g. an `AutoStart(false)`
route started by hand). `ServerEntry.GetCompiled()` now orders the match
table by **specificity** — literal-heavy templates first, route parameters
before more parameters, catch-all (`{**…}`) last — with registration order
kept as a stable tie-breaker so equal-specificity routes preserve their
previous first-registered-wins behaviour. Both `MatchRoute` and the
CORS-dispatch `MatchByPath` consume the ordered table, so a specific path
and a `{**path}` fallback can coexist on one port, matching ASP.NET-style
routing precedence.

#### `redb.Route.Llm` — orphan `tool_use` recovery on conversation load

`AgentEngine.RunAsync` now sanitises the loaded conversation path: if the
last persisted message is an assistant turn that has `tool_use` blocks
without a matching `tool_result` user turn after it (the previous run was
cancelled, timed out, or threw between persisting the assistant message
and dispatching the tool — see `AgentEngine.cs` lines 195/222), a
synthetic `tool_result(error: "orphaned_tool_use_recovered")` user message
is appended and persisted before the new user prompt is added.

Without this, any provider that strictly enforces tool_use/tool_result
pairing — notably Anthropic's Messages API
(`400 invalid_request_error: tool_use ids were found without tool_result
blocks immediately after`) — 400's forever on every subsequent request,
poisoning the conversation permanently. `RedeliveryPolicy` then multiplies
the failure across retries.

The recovery is logged at warning level (`Recovered {N} orphaned tool_use
block(s) in conversation {Conv} on load.`) so production occurrences are
visible. Applies uniformly to `InMemoryConversationStore` and
`RedbConversationStore` — recovery happens after `LoadPathAsync`,
provider-agnostic.

#### `redb.Route.Exec` — child stdout/stderr decoded with the host's OEM codepage on Windows

`ExecProducer` now sets `ProcessStartInfo.StandardOutputEncoding` /
`StandardErrorEncoding` to the host console's active codepage (cp437,
cp932, cp936, cp949, …) on Windows, falling back to UTF-8 on Linux/macOS.
Without this, .NET defaulted to UTF-8 when reading the redirected
streams while `cmd.exe` / `fsutil` / `wmic` / `net` emit OEM bytes — the
mismatch surfaced as U+FFFD replacement characters in `redbExec.Stdout`
and the downstream JSON tool body, breaking LLM agents on
Japanese / Chinese / Korean / Greek / Turkish-locale Windows hosts (any
non-Latin OEM codepage).

Adds a dependency on `System.Text.Encoding.CodePages` 9.0.0 — the BCL
only ships ASCII/UTF-8/UTF-16/UTF-32 encodings on .NET; cp932/cp936/cp949
require `CodePagesEncodingProvider`.

#### `redb.Route.Llm.Mcp` — stdio client transitions to `Dead` on transport failure

`McpClientBase.OnTransportFailed` now sets `Status = McpClientStatus.Dead`
in addition to failing pending requests. Previously, when an stdio child
process exited unexpectedly (or the read pump tripped), the client failed
in-flight requests but kept reporting `Healthy`, so subsequent
`tools/call` requests went through the producer and silently hung waiting
on a defunct stdin. The producer's `if (Status is Dead) throw` short
circuit was unreachable. The fix makes process death immediately
observable both at the registry level and at the producer level.

#### `redb.Route` — `OnException` declared inside a nested scope is now hoisted to route level (Camel parity)

`RouteDefinition.CreateProcessor` only scanned the **top-level** route
outputs for inline `OnException` blocks. An `OnException` declared inside a
nested scope — `Transacted()`, `Traced()`, `Metered()`, `Throttle()`,
`Filter()`, … — was never hoisted. Worse, the orphaned definition was then
compiled by the enclosing scope's pipeline builder via its silent
`CreateProcessor` fallback, which emitted the **handler chain as an inline
pipeline step**: the exception handler body executed on *every* exchange
with `Exception == null`, corrupting healthy requests (e.g. overwriting the
request body with an error response). `OnWhen` / `Handled` / redelivery
settings were silently ignored.

Two changes:

- **Recursive hoisting.** The route compiler now collects `OnException`
  definitions from the entire definition tree (depth-first, declaration
  order) and wraps the full route body with the handler envelopes — Apache
  Camel parity: `onException` is route-scoped regardless of where it appears
  textually. Wrapping order is unchanged: last declared = outermost.
- **Fail-fast instead of silent fallback.**
  `OnExceptionDefinition.CreateProcessor` no longer compiles the handler
  chain as a standalone pipeline. A hoisted definition compiles to a no-op
  at its declaration site; a definition the compiler cannot hoist (e.g.
  declared inside a `Catch`/`Finally` block or another exception-handler
  pipeline) now throws `InvalidOperationException` at `Start()` with
  placement guidance, instead of corrupting traffic at runtime.

Found in redb.Identity: the `/connect/token` route wraps its body in
`Transacted(...)`, so its `OnException<InvalidOperationException>` OAuth
error mapper ran inline on every token request and replaced the form
parameters with an `error` body before the OpenIddict extract step —
`unsupported_grant_type`/HTTP 400 on perfectly valid requests.

---

## [3.1.0]

> **Why a minor bump (3.0.x → 3.1.0).** This release ships **four new
> NuGet packages** (`redb.Route.Llm`, `redb.Route.Llm.Abstractions`,
> `redb.Route.Llm.Tools`, `redb.Route.Exec`), **one new URI scheme**
> (`exec:`), a **second LLM provider** (native `AnthropicProvider`), and
> a **new persistence extension** (`AddRedbLlmStorage`) that brings five
> stores and nine REDB schemas. All additions are backwards-compatible —
> no public API on existing packages was removed or renamed — but the
> surface area added is too large to bury under a patch bump.

### Added

#### `redb.Route.Llm` — first public release

The Camel-style LLM connector becomes a published package. `From("…")`/
`To("llm://…")`, fluent builder (`Llm.Factory("haiku") …`), Camel-style
agent loop with tool dispatch, headers/URI options for system prompt,
conversation id, max tokens, temperature, max iterations, etc. See
`redb.Route.Llm/README.md` and `doc/USER-GUIDE.md` for the full surface.

- **`OpenAiProvider`** — one provider class covering **14 OpenAI-compatible
  APIs** through `LlmConnectionFactory.Build()` aliases:
  `openai`, `anthropic` (OpenAI-compat endpoint), `groq`, `cerebras`,
  `openrouter`, `gemini` (OpenAI-compat endpoint), `github-models`,
  `mistral`, `together`, `huggingface`, `deepseek`, `ollama`, `lmstudio`,
  plus `custom` for any self-hosted gateway. The provider id only switches
  the default base URL and a couple of provider-specific headers (e.g.
  OpenRouter's `HTTP-Referer` + `X-Title`).
- **`AnthropicProvider`** — *native* Messages API transport
  (`POST /v1/messages`), separate from the OpenAI-compat path. Maps
  `LlmRequest` to Anthropic's `messages` / `tools` / `tool_use` /
  `tool_result` content-block model and reassembles `LlmResponse` from
  the standard envelope. **Streaming is true SSE** —
  `content_block_start` / `content_block_delta` / `content_block_stop`
  events are reassembled per block; tool-use blocks accumulate
  `input_json_delta` partial JSON and surface as a single complete
  `LlmToolUseBlock` at end-of-block. **Error mapping**: HTTP 429 →
  `LlmRateLimitException` (honours `retry-after`); HTTP 529
  ("overloaded") and 5xx → `LlmTransientException`. JSON serialisation
  uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so Cyrillic /
  emoji / `&`/`>`/`<` are emitted as UTF-8, not `\uXXXX` — fixes
  unicode-escaped tool input in `[*-TOOL] ▶ in=…` route logs.
- **Reasoning-model fallback** in `OpenAiProvider`: when the response
  `message.content` is empty, the provider falls back to
  `message.reasoning` so models like Cerebras `gpt-oss-120b` or
  `zai-glm-4.7` still surface a textual answer through `LlmResponse`.
- **`AddRedbLlmStorage()`** extension on `IServiceCollection` — wires
  the LLM connector to a named REDB instance through the Tsak registry
  key `"redb-factory:{name}"`. Ships **five stores** backed by
  `IServiceScopeFactory` per named instance:
  - `IConversationStore` — `RedbConversationStore`: persistent multi-turn
    memory across runs and processes; `AgentEngine.LoadPathAsync`
    resumes a transcript by id.
  - `IApprovalStore` — `RedbApprovalStore`: `IApprovalGate` decisions
    survive restarts, supports human-in-the-loop tools.
  - `ICostBudgetStore` — `RedbCostBudgetStore`: per-tenant spend
    tracking, drives `IBudgetEnforcer` hard cut-offs.
  - `IToolIdempotencyStore` — `RedbToolIdempotencyStore`: dedup of
    expensive tool calls across retries.
  - `IAgentObserver` — `RedbAgentObserver`: full audit of every
    iteration / tool invocation / approval into the store.
  - **Nine REDB schemas** (`[RedbScheme]` POCOs):
    `Conversation`, `Message`, `Approval`, `CostBudget`, `ToolCache`,
    `ToolAudit`, `KnowledgeChunk`, `PromptTemplate`, `EvalRun`. See
    `redb.Route.Llm/doc/STORAGE.md` for the recipe catalogue
    (multi-turn chat, approval gates, hard budget, idempotent retries,
    audit, branching, scheduled agents).
- **`#`-registry prompts** — `LlmConnectionFactory` resolves system
  prompts from a registry by `#name` so prompt text lives in the host's
  Tsak config layer, not inline in route code.
- **Live integration test infrastructure** under
  `tests/redb.Route.Tests.Llm`:
  - `LiveProviderTests` — 5 scenarios (Smoke / NonAscii / ToolUse /
    Usage / StopReason) × free-tier providers (GitHub Models, Groq,
    Cerebras, OpenRouter, Gemini, Mistral, native Anthropic). Live
    end-to-end coverage with auto-skip when API keys are absent.
  - `LiveEndToEndTests` — exercises the full
    `LlmComponent → LlmEndpoint → LlmProducer → AgentEngine`
    path against a real provider, including a tool-loop driving
    `IToolRegistry`.
  - `LiveDslRouteTests` — Apache Camel-style end-to-end routes:
    `From("direct://...") → Process → To(Llm.Factory(...)) → Process →
    To("mock://...")`, a two-LLM judge chain, and cross-context RPC
    over `direct-vm://llm-service` using a shared `SharedVmRegistry`.
  - `ExecShellToolTests` — agent + `exec:` shell tool against live
    Anthropic / OpenAI-compat providers.
  - `UtilityToolTests` — agent + `redb.Route.Llm.Tools` (HttpFetch /
    JsonPath / XPath / MathEval / RegexExtract / Tavily) against live
    providers.
- **`[EnvFact("VAR")]`** xUnit attribute — auto-skips a fact when the
  named environment variable is missing, so contributors without API
  keys keep a green build while CI with the right secrets runs the full
  live matrix.
- **`[Collection("LiveLlmSerial")]`** — shared collection across all
  live LLM tests so xUnit parallelism does not multiply free-tier rate
  limits.

#### `redb.Route.Llm.Abstractions` — first public release

A small, dependency-light contract package. Exists separately from
`redb.Route.Llm` so any of the **23 transports can expose itself as an
LLM tool** by implementing `.AsLlmTool(name)` on the `From(uri)` route —
**zero connector version bumps**, zero transitive dependency on the LLM
provider implementation.

- **`ILlmToolDescriptor`** — descriptor contract: capability metadata +
  endpoint URI the agent dispatches to.
- **`LlmToolCapability`** — `Name`, `Description`, `InputSchema`
  (JSON Schema string), `LlmToolSafety` (`SideEffect`,
  `Caching`, `Cost`, `RequiresApproval`, `RequiredClaims`).
- **`IToolDescriptorRegistry`** — global registry; populated by
  `.AsLlmTool(...)` at route-build time, queried by `AgentEngine` at
  dispatch time.
- **`RouteToolBridge`** — bridges any `From(uri)` endpoint into the
  LLM tool surface. Forwards the model's JSON input through the host's
  producer template, inheriting the parent agent route's transaction
  scope, headers, principal and DI scope.
- **`[ExposeAsLlmTool]`** attribute — alternative to the fluent DSL:
  decorate a handler class and the bootstrapper turns it into a
  registered descriptor.
- **`.AsLlmTool(name)` DSL aspect** in `LlmToolDsl` — Apache-Camel-style
  metadata aspect placed immediately after `.From(uri)`. Closes with
  `.End()` or `.Then()`. Example:
  ```csharp
  From("direct:order-lookup")
      .AsLlmTool("get_order")
          .Description("Returns order details by id.")
          .Input("""{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}""")
          .SideEffect(ToolSideEffect.ReadOnly)
          .Cost(ToolCostClass.Cheap)
      .Then()
      .Bean<IOrderService>((svc, ex) => svc.HandleAsync(ex));
  ```
  Works with **any** transport: `Direct`, `Http`, `Grpc`, `Sql`,
  `Sftp`, `File`, `Redis`, `Exec`, etc. for request-response tools;
  `Kafka`, `MQTT`, `Mail`, `SignalR` for fire-and-forget action tools.

#### `redb.Route.Llm.Tools` — first public release

Six ready-to-use utility tools that live as ordinary `RouteBuilder`
classes registered by `.AsLlmTool(...)`, so they participate in the
same transaction scope, telemetry, error handling and DI as any other
route. All optional — depend only on what the agent needs.

| Tool | Purpose |
|------|---------|
| `HttpFetchTool` | `GET <url>` with size cap and host allowlist; returns body + status + headers. Built on `redb.Route.Http`. |
| `JsonPathTool` | Evaluate a JSONPath expression against a JSON document. Built on the core compiled-`JPath` engine. |
| `XPathTool` | Evaluate an XPath expression against an XML document. Built on the core compiled-`XPath` engine. |
| `MathEvalTool` | Safe arithmetic evaluator (integers / decimals / `+ - * / % ^`, parentheses, common functions). |
| `RegexExtractTool` | Apply a regex to a string; return all matches and named groups. |
| `TavilyWebSearchTool` | Tavily Search API (`https://api.tavily.com/search`); returns top-N results with snippets. API key via `TAVILY_API_KEY`. |

#### `redb.Route.Exec` — first public release

Local-process execution transport. New URI scheme `exec:` with two
operations: `exec://run` (one-shot producer) and a scheduled consumer
that runs commands on a `cron:` / `qtimer:` trigger.

- **`AllowedCommands(params string[])`** — explicit allowlist. Every
  invocation whose command is not on the list is rejected before a
  process starts; this is the security envelope for LLM-driven shells.
- **`WorkingDirectory(string)`** — pinned CWD; relative paths in tool
  arguments resolve there, files written in one call survive to the
  next. Without this, processes inherit the worker's CWD (e.g. the
  source tree under `dotnet run`).
- **`TimeoutMs`** — hard kill on timeout.
- **`MaxStdoutBytes` / `MaxStderrBytes`** — cap captured output.
- **Output headers** — `redbExec.ExitCode`, `redbExec.StdoutBytes`,
  `redbExec.StderrBytes`, plus `redbExec.TimedOut`.
- **`exec:` request schema** — `{"command":"<name>","args":["..."]}`;
  `redbExec` headers, `stdout`, `stderr`, `exitCode` returned. Designed
  to drop straight into `.AsLlmTool("shell")` for an LLM-driven shell;
  the demo `redb.Route.Demo` HTTP showcase wires it into a Claude
  agent. See `redb.Route.Exec/README.md`.

#### Demo — `redb.Route.Demo` HTTP LLM showcase

Two endpoints in `LlmHttpRoutes` modelled as the simplest possible
Camel-readable round-trip:

- `POST /api/llm/ask` — body is the user prompt, six-step route asks
  Claude Haiku, logs token usage + stop reason, returns the model's
  reply as plain text. Conversation memory via `X-Chat-Id` header.
- `POST /api/llm/shell` — same shape but the agent has a `shell` tool
  wired through `ExecComponent` with a pinned scratch directory under
  `Path.GetTempPath()/redb-llm-shell/`, allowlist `{cmd, pwsh,
  powershell}` on Windows / `{sh, bash}` on Linux, 5 s timeout,
  8 KiB stdout/stderr caps.

### Fixed

- **`OpenAiProvider.BuildRequestBody`** — `LlmToolResultBlock` now always
  emits `role: "tool"` regardless of the original `LlmMessage.Role` of
  the block it lives on. Previously, when `AgentEngine` produced an
  Anthropic-style `role: "user"` message that carried a tool-result
  block, strict OpenAI-compatible gateways (Groq) rejected the request
  with `400 messages.X : for role:user content not nullable`. The
  provider now partitions blocks per message and emits a separate
  `role: tool` entry per tool-result block.
- **`AnthropicProvider.JsonOpts`** — switched `Encoder` to
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. Without it,
  `block["input"]?.ToJsonString(JsonOpts)` (response-parse path,
  surfaced as `LlmToolUseBlock.InputJson`) escaped Cyrillic / emoji
  / `&`/`>`/`<` to `\uXXXX` and made tool-input route logs
  unreadable. Matches `JsonMessageSerializer.DefaultOptions` — safe
  for HTTP API responses; only unsafe inside HTML / inline JS, which
  this code path never produces.
- **`AgentEngine`** — now invokes `IConversationStore.LoadPathAsync`
  when a `ConversationId` is present on the request. The persisted
  transcript was being written but not read back, so resumed runs
  saw an empty history. With the fix, multi-turn chat works end-to-end
  via `RedbConversationStore` (`AddRedbLlmStorage()`).

## [3.0.1] — 2026-06-03

### Added

#### DSL — flat fluent navigation across nested scopes
- **`redb.Route` (DSL)** — added a new universal `End()` extension method
  on `IRouteDefinition` and a full set of typed `End*()` extension methods
  (`EndFilter`, `EndChoice`, `EndWhen`, `EndOtherwise`, `EndSplit`,
  `EndMulticast`, `EndAggregate`, `EndCircuitBreaker`, `EndThrottle`,
  `EndDebounce`, `EndLoop`, `EndTryCatch`, `EndOnException`, `EndTransaction`,
  `EndLog`, `EndResequence`, `EndTraced`, `EndMetered`,
  `EndIdempotentConsumer`, `EndSaga`). Each typed `End*()` walks the
  `Parent` chain looking for a scope of the requested type and returns its
  parent route. This means a single `.EndChoice()` call from deep inside
  `Choice → When → Split → Log` lands directly at the route root —
  semantically identical to chaining `.EndLog().EndSplit().EndChoice()` but
  more concise when the intermediate scopes do not need extra steps. Each
  helper throws a precise `InvalidOperationException` when called outside
  a matching scope.
- **`redb.Route` (DSL)** — added `When(...)` and `Otherwise()` as extension
  methods on `IRouteDefinition`. They walk the `Parent` chain to find the
  enclosing `ChoiceDefinition` and dispatch to its instance method, so a
  sibling branch can be opened immediately after a sub-scope closes — for
  example `.Choice().When(p).Split(...).EndSplit().When(p2).Process(...).EndChoice()`
  now compiles and behaves the same as the equivalent nested-lambda form.
  Instance methods on `ChoiceDefinition` / `WhenDefinition` /
  `OtherwiseDefinition` keep precedence over the extensions, so existing
  call sites are unaffected.
- **`redb.Route` (DSL)** — added a focused test fixture (`DeepNestedDslTests`,
  five scenarios) covering `Choice`/`When`/`Otherwise`/`Split`/`RichLog`
  composition, `TryCatch` with rich logging inside `DoCatch<T>`, mixed
  typed and universal `End*()` closers, cascading `EndChoice()` from deep
  inside, and the diagnostic `InvalidOperationException` raised when
  `End*()` is called outside any matching scope.

### Removed

#### Legacy `RouteStep` AST
- **`redb.Route` (DSL)** — removed the legacy `RouteStep` /
  `RouteStepProjection` AST and the `RouteDefinition.Steps` projection. The
  `ProcessorDefinition` tree built by the fluent DSL is now the single
  source of truth for route construction; everything that used to read
  `Steps` (Normalizer, Saga, integration tests) now uses
  `CreateProcessor` directly. The legacy files have been moved out of the
  shipping assembly into `tmp/oldRoute/` for reference only.

### Changed

#### DSL — single source of truth via CRTP base (`RouteDefinitionBase<TSelf>`)
- **`redb.Route` (DSL)** — the leaf DSL (`To`, `Process`, `ProcessAsync`,
  `SetBody`, `SetHeader`, `SetProperty`, `RemoveHeader`, `RemoveProperty`,
  `Transform`, `Validate`, `Marshal` / `Unmarshal`, `ConvertBody`, `Stop`,
  `Delay`, `Sample`, `BeginTransaction` / `Commit` / `Rollback`,
  `SetPattern`, `Respond`, `Bean`, `StreamCaching`, `Throw*`, `Log*`, plus
  every scope-opener: `Filter`, `Choice`, `Split`, `Multicast`, `Loop`,
  `Aggregate`, `IdempotentConsumer`, `Throttle` / `Debounce` / `KeyedThrottle`,
  `Metered`, `Traced`, `Resequence`, `Transaction`, `Saga`, `OnException`,
  `OfType<T>`, `CircuitBreaker`, `TryCatch`, etc.) is now defined exactly
  once in a new generic CRTP base, `RouteDefinitionBase<TSelf>`, instead of
  being duplicated across 27 scope-definition classes. Each typed leaf method
  returns `TSelf`, so chaining always preserves the current scope's concrete
  type — e.g. `.Filter(p).To("a").SetHeader("k","v")` keeps you on
  `FilterDefinition`, `.Choice().When(p).To("a")` keeps you on
  `WhenDefinition`, and only the explicit `End*()` / `End()` step exits the
  scope. There is no behavioural change for end users; the public DSL
  surface and route AST shape are identical to 3.0.0.
- **`redb.Route` (DSL)** — `RouteDefinition` is now a thin
  `RouteDefinitionBase<RouteDefinition>` subclass that retains only
  route-level concerns: `RouteId`, `From`, `AutoStart`, `Cluster`,
  `ProcessingTimeout`, `RoutePolicy`, `OnException` hoisting, and
  `CreateProcessor`. All other behaviour is inherited.
- **`redb.Route` (DSL)** — every pipeline-scope class
  (`FilterDefinition`, `ChoiceDefinition` / `WhenDefinition` /
  `OtherwiseDefinition`, `CircuitBreakerDefinition` / `FallbackDefinition`,
  `LoopDefinition`, `SplitDefinition` / `MulticastDefinition`,
  `TryCatchDefinition` / `CatchDefinition` / `FinallyDefinition`,
  `IdempotentConsumerDefinition`, `OnExceptionDefinition`,
  `TransactionDefinition`, `SagaDefinition`, `MeteredDefinition`,
  `TracedDefinition`, `ResequenceDefinition`, `ThrottleDefinition` /
  `DebounceDefinition` / `KeyedThrottleDefinition`, `AggregateDefinition`,
  `OfTypeDefinition<T>`, `OfTypeFilterDefinition<T>`) now inherits from
  `RouteDefinitionBase<TSelf>` and contains only its own scope-specific
  configuration (options, branch openers, `End*()` navigation,
  `CreateProcessor` override). Per-class duplicates of the leaf DSL have been
  removed.
- **`redb.Route` (DSL)** — `IRouteDefinition` remains the canonical
  cross-version contract; `RouteDefinitionBase<TSelf>` provides explicit
  interface implementations for every leaf method (split into a partial file,
  `RouteDefinitionBase.IRouteDefinition.cs`), so existing extension methods,
  test mocks, and `Action<IRouteDefinition>` configurators continue to bind
  unchanged.
- **`redb.Route` (DSL)** — non-pipeline definitions (`LoadBalancerDefinition`,
  `ScatterGatherDefinition`, `NormalizerDefinition`,
  `RichLogScopeDefinition`) intentionally remain on `ProcessorDefinition`:
  they have no child `Outputs` pipeline and no leaf DSL — they are
  configuration builders, and inheriting the CRTP base would have inflated
  their public surface with methods (`To`, `Process`, …) that are
  semantically invalid in those scopes.

### Fixed
- **`redb.Route` (DSL)** — `IRouteDefinition.GetContext()` now correctly
  returns the owning `IRouteContext` when called on any nested scope
  (`WhenDefinition`, `LoopDefinition`, `TracedDefinition`, `CatchDefinition`,
  etc.). Previously it relied on `self as RouteDefinition`, which only
  matched the route root; after the CRTP refactor scope classes inherit from
  `RouteDefinitionBase<TSelf>` (not from `RouteDefinition`), and the cast
  silently returned `null` inside any scope. The accessor now walks the
  `Parent` chain up to the owning `RouteDefinition` and returns its
  `Context`. This restores `Context_IsAvailable_In{Choice,Loop,Traced,DoTry}Scope`
  semantics for extension methods that read context at DSL build time.
- **`redb.Route` (DSL)** — `SagaDefinition.SetParent` is no longer required:
  the parent link is now established uniformly through `AddOutput`, which
  matches every other scope and removes a small inconsistency in the AST
  build path. Existing user code is unaffected.

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
  in [`IbmMqConsumer.cs`](redb.Route.IbmMq/IbmMqConsumer.cs) for
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
- `redb.Route.Core` — `RedbIdempotentRepository` backed by redb.Core props storage; `IRedbService` access from routes
- `redb.Route.Controllers` — `RedbController`, attribute routing, parameter binding, 4 dispatchers (generic, HTTP, SignalR, gRPC)
- `redb.Route.GenericFile` — shared base for File, FTP, SFTP (abstract consumer/producer, options, file-ops interfaces)
- `redb.Route.Validation.Adapters` — `FluentValidationMessageValidator<T>`, `DataAnnotationsValidator`, DSL extensions
