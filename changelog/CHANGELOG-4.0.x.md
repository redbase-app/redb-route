# redb.Route changelog: 4.0.x

Releases of the 4.0 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

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

- Guide: [Access IRedbService from Routes](../redb.Route.Core/README.md#access-iredbservice-from-routes) —
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
  `BeginRedbTransaction()`, is obsolete ([TRANSACTIONS.md](../TRANSACTIONS.md): one primitive, `.Transacted()`)
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

> **4.0.0 breaking bundle.** The entries under *Removed* and the first two
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
  One rule across the DSL: the `Expression`
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
  there. Breaking, and outside the breaking bundle listed above.

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
  guarantee are all covered by tests. Every shipped Route-XML example
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
- **The dead-options sweep, part B: 25 options
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
- **`redb.Route.Kafka`: the hardening sweep (every fix red-before, e2e against a live 3-node KRaft cluster).**

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

  *`transacted` no longer promises exactly-once (A3, breaking).* The connector always was an
  idempotent producer with a deferred send, at-least-once, with no `transactional.id`; README and the IntelliSense docs said "exactly-once semantics". They agree
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
- **A hub is an occupant of the shared listener, not a guest of it.** Three defects that the migration to the
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
- **S3 consumer no longer loses objects (all red-before against MinIO).** Three data-loss paths closed:
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
  `ThrowAsync`).
- **`redb.Route.Llm` storage: every business key is now guarded by the object unique key.**
  All redb-backed LLM stores used the same pre-V4
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
  provider can store. Core-side observations were reported to redb core
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
- **`RedbIdempotentRepository` did not survive a race on one key.** The check-then-save on `_objects._name` had a
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
- **Code review of the 4.0 work, first batch.**
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
    (A secret leaving through a surface that is not a log line; the other surfaces are covered by the
    regression below.)
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
  A route whose consumer URI
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
  a test may assert a lower bound and never "unchanged".
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
  `2.5` or `2,5` depending on where it runs. The snapshot records each of them, so a change shows up
  as a diff.

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
