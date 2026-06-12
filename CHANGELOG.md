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
| `redb.Route.Tcp` | Raw TCP transport |
| `redb.Route.Validation.Adapters` | FluentValidation + DataAnnotations adapters |
| `redb.Route.WebSocket` | WebSocket transport |

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Note on version history:** redb.Route has been running in production since version 1.0.0.
> Versions 1.0.0 – 1.0.3 were not published to NuGet (internal deployments only).
> The first public NuGet release is **1.0.4**.

## [3.1.1] — Unreleased

> ⚠️ **Not yet published to NuGet.** This bump applies to **`redb.Route.Llm`,
> `redb.Route.Llm.Tools`, `redb.Route.Llm.Mcp`, `redb.Route.Http`,
> `redb.Route.WebSocket` and `redb.Route.Exec`** — every other package stays
> at 3.1.0. The release bundles four areas of work: (1) end-to-end
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

Demo: see [`demos/Llm.AuditShell/`](demos/Llm.AuditShell/) — single-file
HTTP shell that exposes both option-side defaults and header-side overrides,
plus the swap comment for `RedbConversationStore` and the LINQ-by-AuditTags
query above.

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
