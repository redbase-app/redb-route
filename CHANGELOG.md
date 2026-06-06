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
