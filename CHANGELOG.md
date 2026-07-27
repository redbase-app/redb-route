# Changelog

All notable changes to redb.Route will be documented in this file.
This changelog covers the **NuGet-published packages**:

| Package | Description |
|---------|-------------|
| `redb.Route` | Core engine: DSL, processors, expressions, telemetry |
| `redb.Route.Amqp` | AMQP 1.0 transport |
| `redb.Route.AzureServiceBus` | Azure Service Bus transport |
| `redb.Route.Controllers` | Transport-agnostic controller dispatch |
| `redb.Route.Core` | Bridge to redb.Core props storage |
| `redb.Route.Elasticsearch` | Elasticsearch 8.x transport |
| `redb.Route.Exec` | Local process execution transport (`exec:` scheme) |
| `redb.Route.File` | File system transport |
| `redb.Route.Firebase` | Firebase (Firestore, Cloud Storage, FCM) transport |
| `redb.Route.Ftp` | FTP/FTPS transport |
| `redb.Route.GenericFile` | Base library for file-based transports |
| `redb.Route.Grpc` | gRPC transport |
| `redb.Route.Http` | HTTP/HTTPS transport |
| `redb.Route.IbmMq` | IBM MQ transport |
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
| `redb.Route.Sqs` | Amazon SQS + SNS transport |
| `redb.Route.Tcp` | Raw TCP transport |
| `redb.Route.Telegram` | Telegram Bot transport |
| `redb.Route.Validation.Adapters` | FluentValidation + DataAnnotations adapters |
| `redb.Route.WebSocket` | WebSocket transport |

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Note on version history:** redb.Route has been running in production since version 1.0.0.
> Versions 1.0.0 – 1.0.3 were not published to NuGet (internal deployments only).
> The first public NuGet release is **1.0.4**.

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
  constant hash). Fixed the parser (see `RedBase.Core` changelog) and removed the pre-check so bulk
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

Demo: see `demos/Llm.AuditShell/` — single-file
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
  in `IbmMqConsumer.cs` for
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
