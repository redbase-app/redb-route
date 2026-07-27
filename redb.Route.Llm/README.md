# redb.Route.Llm

> Internal package README. The NuGet description is separate.
> Long-form docs live under doc/.

## What this is

A **first-class transport** for the redb.Route engine that turns a Large Language
Model into an addressable endpoint, exactly the same way Kafka, RabbitMQ or HTTP
are addressable endpoints.

```csharp
.From("kafka://orders")
 .To(Llm.Factory("claude").Temperature(0.2).MaxTokens(1024).AsUri())
 .To("kafka://orders.translated");
```

That single line is a fully wired LLM call:

- input message body becomes the user prompt;
- the agent runs against the provider configured in factory `claude`;
- the assistant text replaces `exchange.Out.Body`;
- token usage, model id, stop reason, tool iterations are pushed into headers;
- OpenTelemetry traces and metrics light up automatically;
- the endpoint is visible in the tsak.web dashboard with messages-per-second,
  average duration, error rate and last-error info — like every other connector.

## Why a connector and not a "library"

Most C# LLM libraries work as direct API clients you call from your code. They
own retries, conversation state, tool dispatch and observability — sometimes
well, often partially.

The redb.Route engine *already has*:

| Concern | DSL primitive |
|---|---|
| Retry / backoff | `RedeliveryPolicy`, `OnException` |
| Rate limiting | `Throttle` |
| Resilience | `CircuitBreaker` |
| Idempotency | `IdempotentConsumer` |
| Compensation | `Saga` |
| Audit / shadow | `WireTap`, `Multicast` |
| Tracing & metrics | `RouteActivitySource`, `RouteMetrics` |
| Persistence | `redb` schemes (typed object engine) |

Wrapping an LLM as just another connector means *every one of those primitives
applies for free*. We do not reinvent retries, breakers, dashboards or audit —
we author one connector and the entire engine carries it.

## How it works

```text
   exchange ──► LlmEndpoint ──► LlmProducer ──► AgentEngine ──► ILlmProvider
                                                    │              (HTTP)
                                                    ▼
                                            tool loop (optional)
                                                    │
                                                    ▼
                                       IToolDescriptorRegistry ──► RouteToolBridge
                                                                       │
                                                                       ▼
                                                              IProducerTemplate
                                                                       │
                                                                       ▼
                                                  any redb.Route route mounted
                                                  with .AsLlmTool("name")
```

Pieces:

- **`LlmComponent`** — registers itself for scheme `llm` (one URI scheme,
  by design — see PLAN.md §3a).
- **`LlmEndpoint`** — created from `llm://<connectionFactoryName>?...`. Resolves
  the named `LlmConnectionFactory` from the route registry, owns options, exposes
  `IEndpointStatistics` (tsak.web reads these).
- **`LlmConnectionFactory`** — POCO with provider id, model id, API key (or secret
  ref), tuning defaults. `Build()` produces an `ILlmProvider`.
- **`ILlmProvider`** — the only thing that talks HTTP. Two production providers ship today:
  - **`OpenAiProvider`** — one universal transport for **14 OpenAI-compatible APIs**:
    `openai`, `anthropic`/`claude` (via the official OpenAI-compat endpoint at
    `https://api.anthropic.com/v1/`), `groq`, `cerebras`, `openrouter`,
    `gemini` (OpenAI-compat endpoint), `github-models`, `mistral`, `together`,
    `huggingface`, `deepseek`, `ollama`, `lmstudio`, plus a generic `custom`
    for any self-hosted gateway.
  - **`StubProvider`** — deterministic echo for unit tests / CI without keys.
  - **`AnthropicProvider`** — native Anthropic Messages API client. Selected
    via `Provider = "anthropic"` and ships in parallel to the OpenAI-compat
    path; reads true Messages-API SSE (`message_start` /
    `content_block_delta` / `message_stop`) for `?stream=true`, and exists
    so we can land features the OpenAI-compat surface flattens away
    (prompt caching, computer-use, fine-grained image blocks).
- **`IAgentEngine`** — the tool-use loop. One inbound exchange = one agent run =
  N provider calls. Handles iterations, transcript, tool dispatch, governance hooks.
- **`IToolDescriptorRegistry`** + **`RouteToolBridge`** — tools-as-routes machinery
  (see *Tools* below).
- **`IPromptTemplateRegistry`** — versioned prompt store backing the `#`-ref
  resolver (see *Dynamic prompts* below).
- **`Fluent/Llm.cs`** — `Llm.Factory("...")` builder used to compose the URI.
- **`Extensions/LlmRouteDefinitionExtensions.cs`** — sugar `route.Llm("factory", b => ...)`
  for inline LLM steps when you do not want a separate endpoint.

## Quick start

### 1. Register

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteLlm();

    route.Services.AddLlmConnectionFactory("claude", f =>
    {
        f.Provider        = "anthropic";   // "openai" | "anthropic"/"claude" | "groq" |
                                           // "cerebras" | "openrouter" | "gemini" |
                                           // "github-models" | "mistral" | "together" |
                                           // "huggingface" | "deepseek" | "ollama" |
                                           // "lmstudio" | "custom" | "stub"
        f.ModelId         = "claude-haiku-4-5";
        f.ApiKeySecretRef = "anthropic.api-key";
        f.Temperature     = 0.2;
        f.MaxTokens       = 1024;
    });

    route.AddRouteBuilder<MyRoutes>();
});
```

`AddRedbRouteLlm()` registers `IAgentEngine`, `IToolDescriptorRegistry`,
`InMemoryPromptTemplateRegistry`, `LlmComponent`, and the inline-`.Llm()`
extension wiring.

### 2. `To("llm://...")` — model as a pipeline step

This is the most common shape. The producer treats the inbound message as **one
user turn**, runs the agent loop to completion, and writes the assistant text
into `Out.Body`.

```csharp
public sealed class MyRoutes : RouteBuilder
{
    public override void Configure()
    {
        From("kafka://orders")
            .To("llm://claude?temperature=0.1&maxTokens=512&conversation=header")
            .To("kafka://orders.translated");
    }
}
```

What happens, step by step:

1. **Input** — `exchange.In.Body` is wrapped as a single `LlmTextBlock`. Any
   other type is `.ToString()`'d.
2. **Tools** — `?tools=` is parsed: missing/empty = no tools, `*` = every tool
   in the registry, CSV = named tools only.
3. **Agent loop** — `AgentEngine.RunAsync` cycles provider call → optional
   `tool_use` → `RouteToolBridge` dispatch → next call → … until `EndTurn`,
   `MaxIterations`, or cancellation.
4. **Output** — assistant text in `Out.Body`. Headers populated:

| Header | Meaning |
|---|---|
| `llm.provider.id` | `"anthropic"` / `"openai"` / `"stub"` / … |
| `llm.model.id` | the actual model used |
| `llm.tokens.in` / `llm.tokens.out` | usage |
| `llm.tool.iterations` | how many loop steps |
| `llm.stop_reason` | `EndTurn`, `ToolUse`, `MaxTokens`, … |

### 3. `From("llm://...")` — scheduled agent

`From("llm://factory?schedule=...")` is **not** "listen for replies" (LLMs are
pull, not push). It is a scheduler that fires a fresh agent run every interval
and pushes the assistant reply down the rest of the route as a normal exchange.

```csharp
From("llm://groq?schedule=5m" +
     "&systemPromptRef=#watchdog-system" +
     "&initialBodyRef=#daily-brief" +
     "&tools=*")
    .To("rabbitmq://alerts");
```

Use cases: watchdog agents, scheduled report generation, self-improving agents
with conversation memory.

`?schedule=` accepts simple intervals only — `500ms`, `30s`, `5m`, `1h`. For
cron expressions, prefer `From("quartz://...").To("llm://...")` — Quartz is
already a scheduler, no need to duplicate it inside the LLM consumer.

The agent's user prompt comes from `?initialBodyRef=`; the system prompt comes
from `?systemPromptRef=`. Both honour the `#`-ref pattern (see *Dynamic
prompts* below).

### 4. Inline `.Llm("factory", ...)` — C# sugar

When the LLM step is just an inline transformation, skip the URI:

```csharp
From("seda://drafts")
    .Llm("claude", x => x
        .WithSystemPrompt("Rewrite the user's text in formal English. Reply with text only.")
        .WithTemperature(0.0)
        .WithMaxTokens(800))
    .To("seda://drafts.formal");
```

`Llm(...)` is a thin extension over `Process(Func<IExchange, ...>)`. It calls
`IAgentEngine.RunAsync` directly — same result as `.To("llm://...")`, but
compiler-checked parameters, IntelliSense for tuning options, no URI escaping.

Trade-off: inline `.Llm(...)` requires `exchange.ServiceProvider` to resolve
`IRouteContext` at runtime. Inside a hosted route this is automatic; in unit
tests, build the exchange via `Exchange.Create(msg, scopeFactory)` so the
service provider flows through.

| Use | Pick |
|---|---|
| Mixed-transport pipeline (kafka → llm → sql) | `To("llm://...")` |
| Multiple LLM hops with different prompts | `To("llm://...")` + `Process` for headers |
| One-off inline call from C# code | `.Llm(name, b => ...)` |
| Periodic agent generating messages | `From("llm://...?schedule=...")` |

### 5. Headers vs URI parameters

Per-message headers always win over URI options. This lets a single endpoint
URI handle many variations without rewrites:

```csharp
.Process(e => e.In.Headers[LlmHeaders.SystemPrompt] = "Reply in French.")
.To("llm://claude?systemPromptRef=defaultPrompt")  // header wins
```

| URI param | Overriding header |
|---|---|
| `systemPromptRef` | `LlmHeaders.SystemPrompt` |
| `conversation=header` | `LlmHeaders.ConversationId` |

## Tools

> **Tools are routes.** A tool is an ordinary `RouteBuilder` route mounted
> with one extra DSL aspect: `.AsLlmTool("name")`.

This is the architectural decision that makes the connector cross-cutting:
**every existing redb.Route component (Kafka, RabbitMQ, HTTP, SQL, file, redb,
…) becomes a potential LLM tool with one method call.** No per-connector LLM
package — `0 bumps` across all sibling connectors.

### How tool dispatch works

1. `.AsLlmTool("name")` (after `.From(...)`) captures the route's `From()` URI
   and registers a `RouteToolBridge` against the name in `IToolDescriptorRegistry`.
2. When the model emits a `tool_use` block, `AgentEngine` looks up the bridge,
   builds an `IMessage` with the input JSON body, and calls
   `IProducerTemplate.RequestBody(uri, msg)` — i.e. invokes the route as
   request/reply.
3. The route runs end-to-end. Whatever lands in `exchange.Out.Body` (or
   `In.Body` when `Out` is null) at the end of the pipeline is serialized and
   handed back to the model as the tool result.

That last point is important: the tool's result is the **final body of the
route**, not the body of any specific `.To(...)`. Three patterns work:

```csharp
// Pattern A — explicit response in a final .Process (most predictable)
r.From("direct:tool-lookup")
    .AsLlmTool("lookup")
    .Process(e =>
    {
        var input  = JsonSerializer.Deserialize<LookupInput>((string)e.In.Body!);
        var result = DoLookup(input);
        e.Out ??= e.In.Clone();
        e.Out.Body = JsonSerializer.Serialize(result);   // ← returned to the model
    });

// Pattern B — request/reply .To (HTTP, redb, SQL) populates Out.Body itself
r.From("direct:tool-customer")
    .AsLlmTool("get_customer")
    .To("redb://customers/getById");                     // Out.Body = customer JSON

// Pattern C — fire-and-forget .To (Kafka/RabbitMQ) — body untouched
r.From("direct:tool-publish")
    .AsLlmTool("publish_event")
    .Process(e => e.In.Body = BuildKafkaPayload(e.In.Body))
    .To("kafka://events")
    .Process(e => { e.Out ??= e.In.Clone(); e.Out.Body = "{\"ok\":true}"; });
```

### Examples per connector

```csharp
// RabbitMQ-backed tool
r.From("rabbitmq://orders.lookup")
    .AsLlmTool("lookup_order")
        .Description("Look up an order by id from the orders queue.")
        .Input("{ \"orderId\": \"string\" }")
    .Process(e => e.Out!.Body = ResolveOrder(e.In.Body));

// Kafka-backed tool — publish an event
r.From("kafka://orders-topic")
    .AsLlmTool("publish_order_event")
    .Process(...)
    .To("kafka://events");

// SQL / redb tool — direct passthrough
r.From("direct:tool-customer-by-id")
    .AsLlmTool("get_customer")
    .To("redb://customers/getById");
```

### Pre-built tool: `HttpFetchTool`

Lives in `redb.Route.Llm.Tools` (separate package — homeless tools without a
natural connector home). Plugs into a route in one line:

```csharp
r.From("direct:agent")
    .To(Llm.Factory("claude").Tools("http_fetch").AsUri())
    .To("mock:done");

context.AddService(typeof(ILlmToolDescriptor),
    new HttpFetchTool(new HttpFetchOptions
    {
        HostAllowlist = new[] { "api.example.com" },
        Timeout       = TimeSpan.FromSeconds(5)
    }));
```

### Tool filter

`?tools=` on the LLM URI is the per-call filter:

| Value | Meaning |
|---|---|
| (omitted) | no tools, plain chat |
| `*` | every tool in `IToolDescriptorRegistry` |
| `lookup_order,publish_event` | explicit allow-list |

## Dynamic prompts — the `#`-registry pattern

`?initialBodyRef=` and `?systemPromptRef=` carry the framework-wide
**registry-ref convention**: a leading `#` turns the value into a lookup key
instead of a literal. This matches how Kafka/S3/Firebase already resolve
`#name` connection factories from `IRouteContext.GetFromRegistry`.

### Resolution order

For a value `"#daily-brief"`:

1. **`IPromptTemplateRegistry`** — if a template named `daily-brief` exists,
   the latest version's body wins. Templates are versionable so eval replays
   bind to a specific revision.
2. **`IRouteContext` registry** — fallback to a plain string registered via
   `ctx.AddToRegistry("daily-brief", "...")`.
3. If neither resolves, the result is `null` (treated as empty user prompt /
   no system prompt).

Plain values without a `#` prefix are passed through verbatim — backwards
compatible with the literal-string usage.

### Why this matters

It decouples *who owns the prompt* from *who calls the model*. One route
refreshes the value on its own schedule, another route consumes it. They share
nothing but a name in the registry.

```csharp
// Route A — refreshes the prompt every minute from the database.
From("timer://refresh-prompt?period=1m")
    .Process(async (e, ct) =>
    {
        var fresh = await _db.LoadDailyBriefAsync(ct);
        e.Context.AddToRegistry("daily-brief", fresh);
    });

// Route B — agent ticks every 5 minutes, picks up whatever the latest brief is.
From("llm://groq?schedule=5m" +
     "&initialBodyRef=#daily-brief" +
     "&systemPromptRef=#watchdog-system" +
     "&tools=*")
    .To("rabbitmq://alerts");
```

Versioned templates use the same syntax but resolve through
`IPromptTemplateRegistry`:

```csharp
await templates.SetAsync(new PromptTemplate
{
    Name = "watchdog-system",
    Version = "v3",
    Body = "You are an SRE watchdog. Reply with PASS or FAIL only."
});
// Then the URI ?systemPromptRef=#watchdog-system picks v3 (the latest) at every tick.
```

The same `#`-ref works on `LlmProducer` (the `.To("llm://...")` shape), so
non-scheduled pipelines also benefit.

## Persistence — `AddRedbLlmStorage()`

The agent loop ships with **in-memory** defaults for all of its state surfaces
(conversation transcripts, tool idempotency, approvals, cost ledgers, audit).
That keeps `AddRedbRouteLlm()` zero-dependency — fine for tests and stateless
agents, but loses everything on restart. The connector also ships **REDB-backed
implementations of every one of those surfaces** in `Storage/Redb/`. They are
opt-in via a single call:

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteLlm();          // engine + in-memory defaults
    route.Services.AddRedbIdempotentRepository();   // required for idempotency
    route.Services.AddRedbLlmStorage();         // ← swap defaults for REDB stores
});
```

`AddRedbLlmStorage()` replaces the default singletons with:

| Interface | Default | REDB store | Schema |
|---|---|---|---|
| `IConversationStore` | `InMemoryConversationStore` | `RedbConversationStore` | `ConversationProps` (root) + `MessageProps` (child) — tree, parent linkage via native `parent_id`, conversation/message ids on indexed `value_string`, conversation FK on `value_long` |
| `IToolIdempotencyStore` | `InMemoryToolIdempotencyStore` | `RedbToolIdempotencyStore` (wraps `IIdempotentRepository`) | reuses redb's idempotency repository for the dedupe key + cached-result row |
| `IApprovalStore` | `InMemoryApprovalStore` | `RedbApprovalStore` | `ApprovalProps` — one row per decision, approval id on `value_string` |
| `ICostBudgetStore` | `InMemoryCostBudgetStore` | `RedbCostBudgetStore` | `CostBudgetProps` — running totals per tenant/window |
| `IAgentObserver` (audit) | `NoopAgentObserver` | `RedbAuditObserver` | `ToolAuditProps` — one row per tool call |

Design choices worth knowing:

- **Indexed by design.** Per-row business identifiers live on `_objects.value_string`
  (an indexed column), not inside the `Props` JSON. Lookups are O(log n) on a
  single column, not full-scan-with-JSON-decode.
- **Tree integrity for transcripts.** `RedbConversationStore` uses
  `IRedbService.CreateChildAsync` — parent linkage is the tree's native
  `parent_id`, not a soft FK in props. The tree primitive enforces integrity.
- **Lazy scheme sync.** Each store calls `EnsureSchemes` on first use; nothing
  to register up-front, no migration step. Fine for tests, fine for prod.
- **Scope per call.** Stores resolve `IRedbService` from a fresh
  `IServiceScopeFactory` scope per operation — safe under concurrent agent runs.
- **Schemas are POCOs in `Storage/Redb/Schemas/`.** Standard `[RedbScheme]`
  attribute, evolvable like any other redb schema.

`AddRedbLlmStorage()` also wires `IPromptTemplateRegistry`, `IToolCacheStore`,
`IEvalRunStore`, `IBatchStore` and `IKnowledgeStore` to their redb-backed stores —
all ten storage surfaces move to redb in one call. (RAG on `IKnowledgeStore` is
covered in doc/RAG.md.)

> **For the long form** — concrete DSL recipes for each store (multi-turn chat,
> approvals, budgets, idempotent retries, audit replay, branching, scheduled
> agents) and a tour of all 9 schemas — see `doc/STORAGE.md`.
> That guide shows *why* each surface matters and how a `.From("kafka://…").To("llm://…")`
> route lights up persistence with a single header.

## URI reference

```
llm://<connectionFactoryName>
    ?temperature=0.2
    &maxTokens=1024
    &topP=0.9
    &systemPromptRef=<literal | #registry-key>
    &initialBodyRef=<literal | #registry-key>     # consumer only
    &conversation=none|header|property
    &stream=true                                   # producer only (HTTP→SSE, WS→per-frame)
    &schedule=500ms|30s|5m|1h                      # consumer only
    &maxIterations=8
    &tools=*|name1,name2
    &connectionFactory=<name>                      # alternative to the URI host
```

| Param | Producer (`To`) | Consumer (`From`) |
|---|---|---|
| `temperature`, `maxTokens`, `topP` | yes | yes |
| `systemPromptRef` | yes (header beats it) | yes |
| `initialBodyRef` | n/a | yes |
| `conversation` | yes | yes (`header` resolves to none — consumer-born exchanges have no inbound header) |
| `stream` | yes (`Out.Body = IAsyncEnumerable<string>`; `HttpConsumer` flushes as SSE, `WsConsumer` as one text frame per token) | n/a |
| `schedule` | n/a | required |
| `maxIterations` | yes | yes |
| `tools` | yes | yes |

## Comparison with Apache Camel `langchain4j-*`

Camel's LLM story lives in a family — `camel-langchain4j-chat`,
`-embeddings`, `-tokenizer`, `-tools`, `-agent`, `-web-search`, plus
`camel-anthropic`, `camel-djl`, `camel-huggingface`. It's the closest
analogue to what `redb.Route.Llm` does, so the comparison is worth pinning down.

### What we both ship

| Capability | `camel-langchain4j-*` | `redb.Route.Llm` |
|---|---|---|
| Chat completion endpoint | `langchain4j-chat://chatId?chatModel=#model` | `llm://factory` |
| Tool dispatch into a route | `langchain4j-tools://name` (separate component) | `.AsLlmTool("name")` (DSL aspect on any `From(...)`) |
| Agent loop (multi-turn tool use) | `langchain4j-agent://...` | built into `IAgentEngine` |
| Scheduled invocation | only via `from("timer:...").to("langchain4j-chat:...")` — the LLM is producer-only | **`From("llm://factory?schedule=...")` is a first-class consumer** — the LLM endpoint *is* the scheduler |
| Streaming responses | LangChain4j streaming chat model | `?stream=true` end-to-end: producer emits `IAsyncEnumerable<string>`; `redb.Route.Http` flushes per-chunk as SSE (`event: done` trailer carries final `llm.tokens.*` / `llm.cost.usd` / `llm.stop_reason`); `redb.Route.WebSocket` yields one `Text` frame per token |
| Registry refs (`#name`) | yes — Camel-wide | yes — framework-wide; works for connection factories **and** prompts |
| Conversation memory | LangChain4j `ChatMemoryStore` family | header / property conversation id; full persistence via `AddRedbLlmStorage()` |
| Provider matrix | 25+ via LangChain4j (Anthropic, Bedrock, Vertex, Azure OpenAI, OpenAI, Mistral, Ollama, …) | 14 OpenAI-compatible behind one `OpenAiProvider` (incl. Anthropic Claude via the official OpenAI-compat endpoint, live-tested with Haiku 4.5 + Sonnet 4.6) + stub; native Anthropic Messages API on deck |
| Embeddings / vector store | yes — `langchain4j-embeddings` + LangChain4j vector stores | **`IEmbeddingProvider` + `OpenAiEmbeddingProvider`** (OpenAI-compat) + **`embed://` scheme** (embed as a route step) + `RedbKnowledgeStore.SearchAsync` cosine; ANN index (pgvector/Qdrant) wraps `IKnowledgeStore` |
| RAG primitives (ingest, retrieval, injection) | yes — via LangChain4j family | **`knowledge://` ingest + `knowledge_search` tool + `.Knowledge()` DSL** (keyword + semantic) — see doc/RAG.md; document loaders BYO |
| Endpoint statistics | standard Camel JMX / Micrometer | `IEndpointStatistics` per LLM endpoint, surfaces in tsak.web with no setup |
| Versioned prompt registry | not built in | `IPromptTemplateRegistry` + `#name` resolver |
| Governance hooks (budget / approval / shadow / redaction) | not built in | hook interfaces inside the agent loop (Noop today, scaffolding ready) |

### Where redb.Route.Llm wins

- **`From("llm://...")` is a first-class consumer.** This one has no
  equivalent in `camel-langchain4j-*` — every LangChain4j component there is
  producer-only. The Camel pattern for "fire the model on a schedule" is
  `from("timer:...").to("langchain4j-chat:...")`, which couples *what* fires
  the agent and *what* the agent says into the same route. We treat the
  LLM endpoint itself as a scheduler:

  ```csharp
  From("llm://groq?schedule=5m" +
       "&systemPromptRef=#watchdog-system" +
       "&initialBodyRef=#daily-brief" +
       "&tools=*")
      .To("rabbitmq://alerts");
  ```

  That's a self-driven agent in one declarative line — no auxiliary route,
  no glue code, no `Process` step to populate the body. And because both
  prompts use `#name` refs, *another* route can refresh either of them on
  its own schedule without touching the agent route. We don't know of an
  ESB connector that combines a scheduled LLM consumer with a
  registry-resolved prompt as the default shape.

- **One scheme.** `llm://` and only `llm://`. Streaming, tools, scheduling and
  conversation are query options on the same endpoint — not five sibling
  components with five sets of headers and parameters to learn.

- **Tools are routes, not a separate component.** `.AsLlmTool("name")` is
  a single-line DSL aspect that turns *any existing route* into an LLM tool.
  No extra NuGet package per connector. RabbitMQ, Kafka, HTTP, SQL, redb —
  they all become tools the same way:

  ```csharp
  r.From("rabbitmq://orders.lookup")
      .AsLlmTool("lookup_order").Description("Look up an order by id.")
      .Process(...);
  ```

  The `0 bumps for all sibling connectors` outcome falls out of the architecture —
  there is nothing to coordinate between the LLM connector and the rest.

- **One `OpenAiProvider` for 14 vendors.** OpenAI, Anthropic (Claude via the
  official OpenAI-compat endpoint), Groq, Cerebras, OpenRouter, Mistral,
  Together, HuggingFace, Deepseek, Ollama, LM Studio, GitHub Models, Gemini's
  OpenAI-compat endpoint and a `custom` slot all flow through the same class.
  Adding a vendor is a base-URL change, not a new package.

- **`#`-registry refs apply to prompts — and pair with the LLM consumer.**
  Versioned prompts, A/B testing of prompts, and "another route refreshes
  the prompt every minute" all work without modifying the LLM route.
  `?systemPromptRef=#watchdog` resolves through `IPromptTemplateRegistry`
  (latest version) with a fallback to the generic route registry. Camel
  uses `#name` for components and beans; we extended the same convention
  to prompts. The combination with `From("llm://...?schedule=...")` is
  what makes a long-running self-driven agent practical: the agent route
  is declarative and unchanged; *who* refreshes the prompt and *when*
  is a separate concern that lives in another route.

- **Governance hooks live inside the agent loop, not around it.** Budget,
  approval, shadow runs and redaction are interfaces invoked by `AgentEngine`
  itself. Today they're Noop, but the cut-points are in the right place —
  no need to wrap routes in extra `Process` steps to enforce a per-tenant
  token budget.

- **Endpoint statistics for free.** Every `LlmEndpoint` exposes
  `IEndpointStatistics` (`MessagesIn/Out`, `BytesIn`, throughput,
  `LastErrorMessage`, `HealthStatus`) the same as Kafka or RabbitMQ — and
  tsak.web renders it with no extra wiring.

- **Small dependency graph.** `redb.Route` + `Microsoft.Extensions.*` +
  `System.Text.Json`. No LangChain wrapper, no vendor SDKs.

### Where Camel `langchain4j-*` wins today

- **Provider matrix is wider** — LangChain4j ships native bindings for
  vendors we don't cover yet (Bedrock, Vertex, Azure OpenAI with native auth,
  several local-only model toolkits).
- **Document loaders & ANN vector index** — LangChain4j ships PDF/DOCX loaders and
  pluggable ANN vector stores. We ship the RAG *loop* (`knowledge://` ingest,
  `IEmbeddingProvider`, keyword + cosine search, `knowledge_search` tool,
  `.Knowledge()` injection, `embed://` scheme — doc/RAG.md) but chunk
  plain text and cosine-scan in-process; an ANN index (pgvector/Qdrant) wraps
  `IKnowledgeStore.SearchAsync` for scale.
- **Memory shapes** — windowed / token-windowed sliding-window memory comes
  in the box with LangChain4j. We ship persistent conversation transcripts
  via `AddRedbLlmStorage()` (`RedbConversationStore`, tree-backed with
  `parent_id` integrity), but window-by-N-messages and window-by-K-tokens
  shapes are not yet first-class — a route can implement them today via
  `Process` + the conversation store, just not as a one-line option.
- **Streaming** is production-ready in both: LangChain4j ships its own
  `StreamingChatModel`, our `LlmProducer` emits `IAsyncEnumerable<string>`
  into `Out.Body` and the HTTP / WebSocket consumers flush it on the wire
  (`text/event-stream` with an `event: done` summary trailer / one
  `WebSocketMessageType.Text` frame per token). For Anthropic Claude the
  native `AnthropicProvider` reads true SSE deltas; for the 14 OpenAI-compat
  backends `OpenAiProvider.StreamAsync` is the producer.

### Architectural differences worth knowing

- **Wrapper vs. transport.** Camel's `langchain4j-*` is an adapter over
  LangChain4j — most architectural decisions (memory shape, agent shape, tool
  registration) are LangChain4j's, not Camel's. `redb.Route.Llm` talks HTTP
  directly through `ILlmProvider`, owns its own `LlmRequest/Response` types,
  and keeps the agent loop inside our codebase. Trade-off in plain terms:
  Camel inherits LangChain4j's whole feature set and its API churn; we own
  fewer features but every line is ours to evolve.

- **One scheme vs. one component per primitive.** Camel mirrors LangChain4j's
  type system (chat, embeddings, tokenizer, agent, web-search) at the URI
  layer. We collapse those into options on a single endpoint when the
  workflow is "one inbound message, one assistant reply", and reserve new
  schemes only when the *transport semantics* change (embeddings will be
  `embed://` because they return vectors, not text).

- **Tool registration philosophy.** In Camel, a tool is a route exposed
  through a dedicated `langchain4j-tools://` URI plus JSON-schema metadata
  configured on that component. In redb.Route.Llm, a tool is a route plus
  one DSL call (`.AsLlmTool`). Both end up registering a bridge that the
  agent loop dispatches through; the difference is whether tool-ness lives
  on the URI (Camel) or as an annotation on the route definition (us). The
  practical consequence is that we never had to coordinate package versions
  across `redb.Route.RabbitMQ`, `redb.Route.Kafka`, `redb.Route.Sql` etc.
  to add LLM-tool support — they each support it the day they ship a route
  builder, because `.AsLlmTool` operates on the abstraction every connector
  already implements.

- **Prompt-as-data.** Both engines pass prompts as URI params or headers.
  We additionally route prompts through a registry (`#name`) so a prompt
  can be a versioned artifact mutated by another route. Camel can do this
  with custom processors; we made it the default convention to keep the
  declarative path clean.

### The one-liner summary

A redb.Route.Llm route reads top-down like prose, and every step is a
familiar redb.Route primitive (`From → Process → To → AsLlmTool → Mock`).
That's the design target: one scheme, DSL aspects instead of sibling
components, registry refs that work the same way as the rest of the
framework. The cost is a younger ecosystem — fewer providers wired, no
embeddings/vector store yet, no sliding-window memory shapes. The benefit
is that the parts we *do* ship feel like the rest of redb.Route, not like
a guest component grafted on.

## Layout

```
Engine/                 IAgentEngine + AgentRequest/Response + AgentEngine + PromptRef
Engine/Storage/         IPromptTemplateRegistry + InMemoryPromptTemplateRegistry
Engine/Governance/      Budget / Approval / Redaction / Shadow stubs
Engine/Observability/   Agent observer hooks
Extensions/             AddRedbRouteLlm + AddLlmConnectionFactory + .Llm(...) sugar
                        + RouteBuilderToolExtensions (.AsLlmTool)
Fluent/                 Llm.Factory("...").Temperature(...)...AsUri()
Providers/              ILlmProvider, LlmRequest/Response, StubProvider, OpenAiProvider
Telemetry/              LlmMetrics (Counter / Histogram on the shared Meter)
Tools/                  RouteToolBridge, ToolDescriptorRegistry, ToolFilter, LlmToolDsl
Storage/Redb/           RedbConversationStore, RedbApprovalStore, RedbCostBudgetStore,
                        RedbToolIdempotencyStore, RedbAuditObserver
Storage/Redb/Schemas/   ConversationProps, MessageProps, ApprovalProps, CostBudgetProps,
                        ToolAuditProps, ToolCacheProps, EvalRunProps, KnowledgeChunkProps,
                        PromptTemplateProps
LlmComponent.cs         scheme "llm" + endpoint factory
LlmConnectionFactory.cs POCO config + Build() → ILlmProvider
LlmEndpoint.cs          parses URI, resolves factory, wires statistics
LlmEndpointOptions.cs   per-call options bound from query
LlmHeaders.cs           header constants
LlmProducer.cs          ConnectableProducer — builds AgentRequest, runs engine, writes response
LlmConsumer.cs          PeriodicTimer scheduler, fires AgentEngine on each tick
```

## Status

- [x] Skeleton compiles cleanly across `net8.0`/`net9.0`/`net10.0`.
- [x] StubProvider works end-to-end.
- [x] `OpenAiProvider` — production transport covering 14 OpenAI-compatible
      providers (incl. **Anthropic Claude** via the official OpenAI-compat
      endpoint, live-tested against Haiku 4.5 and Sonnet 4.6).
- [x] OTel + tsak.web statistics in place.
- [x] `LlmProducer` (`.To("llm://...")`) — body in / assistant text out.
- [x] `LlmConsumer` (`From("llm://...?schedule=...")`) — `PeriodicTimer` driver.
- [x] Inline `.Llm("factory", ...)` extension shipped.
- [x] Tools-as-routes — `.AsLlmTool` aspect, `RouteToolBridge`, no per-connector packages.
- [x] `HttpFetchTool` (homeless tool) shipped in `redb.Route.Llm.Tools`.
- [x] **Utility tools** — `JsonPathTool` (Newtonsoft full dialect, aligned with
      `JsonPathExpression`), `XPathTool` (wraps framework `XPathExpression`),
      `MathEvalTool` (delegates to `ExpressionResolver`), `RegexExtractTool`,
      `TavilyWebSearchTool`. All thin wrappers over framework primitives.
- [x] **REDB persistence** — `AddRedbLlmStorage()` swaps in-memory defaults for
      `RedbConversationStore`/`RedbApprovalStore`/`RedbCostBudgetStore`/
      `RedbToolIdempotencyStore` + `RedbAuditObserver`. Indexed business ids on
      `_objects.value_string`, tree integrity for transcripts.
- [x] `#`-ref resolver for `systemPromptRef` / `initialBodyRef` (producer + consumer)
      with `IPromptTemplateRegistry` and `IRouteContext` fallback.
- [x] Live integration tests in `tests/redb.Route.Tests.Llm` covering Groq /
      Mistral / Cerebras / Gemini / OpenRouter / **Anthropic Claude** and a
      deterministic DSL showcase (`BasicChat`, `InlineLlm`, `ToolRoute`,
      `ChainedLlm`, `HttpFetchTool`, `RegistryDrivenPrompt`, `ClaudeChat`).
- [x] Native `AnthropicProvider` (Messages API) — shipped, runs alongside
      the OpenAI-compat path. Selected via `Provider = "anthropic"`. Reads
      true Messages-API SSE (`message_start` / `content_block_delta` /
      `message_stop`) for streaming; Messages-API-only features (prompt
      caching, computer-use, fine-grained image blocks) land here as needed.
- [ ] Governance hooks: budget / shadow / approval / idempotency (Phase-1 §2).
- [x] **Streaming producer surface** end-to-end. `LlmProducer.ProcessStreamingAsync`
      writes `IAsyncEnumerable<string>` to `Out.Body`, sets
      `Content-Type: text/event-stream` and the `llm.streaming` header.
      `redb.Route.Http` (`HttpConsumer`) flushes each yield as one SSE
      `data:` frame with `Cache-Control: no-cache, no-transform` /
      `X-Accel-Buffering: no` / `IHttpResponseBodyFeature.DisableBuffering()`,
      followed by an `event: done` trailer carrying `llm.tokens.in/out`,
      `llm.stop_reason`, `llm.cost.usd`, `llm.tool.iterations`,
      `llm.model.id`, `llm.provider.id`. `redb.Route.WebSocket`
      (`WsConsumer`) yields one `WebSocketMessageType.Text` frame per
      non-empty chunk with `endOfMessage=true`. Test coverage:
      `HttpStreamingTests` (4), `WsStreamingTests` (2), env-gated
      `LiveStreamingTests` against Anthropic Claude (native SSE), Groq,
      Cerebras, Gemini, Mistral and OpenRouter.
- [x] **RAG loop** — `knowledge://` ingest (`KnowledgeComponent`), `embed://` scheme,
      `IEmbeddingProvider`/`OpenAiEmbeddingProvider`, keyword `SearchTextAsync`
      + cosine `SearchAsync`, `KnowledgeSearchTool` (`knowledge_search`),
      `.Knowledge(coll, k)` retrieval DSL. Full guide: doc/RAG.md.
- [ ] More first-party tools (SQL / file / redb scheme).

See doc/USER-GUIDE.md — the full long-form guide
covering every DSL shape, the `#`-registry pattern, all 14 providers, the
Claude live-test suite, the testing strategy, the Camel comparison and a FAQ.
For phase planning: doc/PLAN.md,
doc/PHASE-1-MVP.md,
doc/PHASE-2.md, doc/STATUS.md.

## Design principles

1. **One scheme.** `llm://` and only that. No `llm-agent://` / `llm-stream://`
   variants — modes are options.
2. **Three NuGet packages, never more.** `redb.Route.Llm.Abstractions` (provider
   contract + content blocks + tool-descriptor interface),
   `redb.Route.Llm` (engine + providers + DSL aspects), and
   `redb.Route.Llm.Tools` (homeless tools without their own connector). Tools
   that *do* belong to a connector live in that connector's package, e.g.
   `SqlTool` in `redb.Route.Sql`.
3. **Tools are routes.** A tool is whatever a route says it is — built from the
   same DSL primitives. Zero per-connector LLM coupling.
4. **DSL is sugar.** `WithSystemPrompt`/`WithBudget`/`WithShadow`/… are extension
   methods over `IRouteDefinition.Process(...)`. No new core types.
5. **No `dynamic`.** Capability metadata, requests, responses — strongly typed.
6. **Registry-refs are the framework-wide pattern.** `#name` resolves from the
   route registry. Already used by Kafka/S3/Firebase connection factories;
   prompts now follow the same convention.
7. **redb engine for state.** Conversation, idempotency, audit — `[RedbScheme]`
   POCOs, opt-in via `AddRedbLlmStorage()`.
8. **Stand-alone.** No dependency on Tsak or Identity.
