# redb.Route.Llm — Connector User Guide

> **Audience:** C# developers who already think in routes — Apache Camel, redb.Route, MassTransit, NServiceBus — and want a Large Language Model to be just one more endpoint in the pipeline.
>
> **What this is:** the canonical reference for how the `llm://` scheme works, how tools-as-routes work, how dynamic prompts work, what each provider needs, and how the test suite proves all of it against real APIs (including Claude).

---

## Table of contents

1. [The five-second pitch](#the-five-second-pitch)
2. [Mental model — connector, not SDK](#mental-model--connector-not-sdk)
3. [Architecture in one diagram](#architecture-in-one-diagram)
4. [Quick start](#quick-start)
5. [The four DSL shapes](#the-four-dsl-shapes)
   - [`.To("llm://…")` — model as a pipeline step](#1-tollm--model-as-a-pipeline-step)
   - [`From("llm://…")` — scheduled agent (consumer)](#2-fromllm--scheduled-agent-consumer)
   - [`.Llm("name", b => …)` — inline C# sugar](#3-llmname-b---inline-c-sugar)
   - [`.AsLlmTool("name")` — turn any route into a tool](#4-asllmtoolname--turn-any-route-into-a-tool)
6. [Headers vs URI options — per-message overrides](#headers-vs-uri-options--per-message-overrides)
7. [Dynamic prompts — the `#`-registry pattern](#dynamic-prompts--the--registry-pattern)
8. [Tool dispatch deep dive](#tool-dispatch-deep-dive)
9. [Pre-built homeless tools](#pre-built-homeless-tools)
10. [Provider matrix — 14 OpenAI-compat backends](#provider-matrix--14-openai-compat-backends)
11. [Spotlight: Anthropic Claude](#spotlight-anthropic-claude)
12. [URI parameter reference](#uri-parameter-reference)
13. [Headers reference](#headers-reference)
14. [Observability — headers, OTel, tsak.web](#observability--headers-otel-tsakweb)
15. [Storage & Persistence](#storage--persistence)
16. [Testing strategy](#testing-strategy)
17. [Apache Camel comparison](#apache-camel-comparison)
18. [Roadmap](#roadmap)
19. [FAQ](#faq)

---

## The five-second pitch

```csharp
.From("kafka://orders")
 .To(Llm.Factory("claude").Temperature(0.2).MaxTokens(1024).AsUri())
 .To("kafka://orders.translated");
```

That single fluent-DSL line is a fully wired LLM call:

- the inbound message body becomes the user prompt;
- the agent loop runs against the provider configured in factory `claude`;
- the assistant text replaces `exchange.Out.Body`;
- token usage, model id, stop reason and tool iterations land in headers;
- OpenTelemetry traces and metrics light up automatically;
- the endpoint is visible in the tsak.web dashboard with msg/sec, average duration, error rate and last-error info — like every other connector in redb.Route.

No new framework to learn, no new conversation layer, no new retry/idempotency story. The LLM is *already* a redb.Route endpoint.

---

## Mental model — connector, not SDK

Most C# LLM libraries — `Anthropic.SDK`, `OpenAI`, `LLamaSharp`, `LangChain.NET` — are direct API clients. They own retries, conversation state, tool dispatch, observability. Sometimes well, often partially, almost always incompatible with each other.

The redb.Route engine *already has* every primitive an LLM caller needs:

| Concern | DSL primitive |
| --- | --- |
| Retry / backoff | `RedeliveryPolicy`, `OnException` |
| Rate limiting | `Throttle` |
| Resilience | `CircuitBreaker` |
| Idempotency | `IdempotentConsumer` |
| Compensation | `Saga` |
| Audit / shadow | `WireTap`, `Multicast` |
| Tracing & metrics | `RouteActivitySource`, `RouteMetrics` |
| Persistence | `redb` schemes (typed object engine) |

**Wrapping an LLM as just another connector means every one of those primitives applies for free.** No retries reinvented, no breakers reinvented, no dashboards reinvented. One connector, the entire engine carries it.

That is the whole architectural argument. The rest of this guide is consequences.

---

## Architecture in one diagram

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

| Piece | Role |
| --- | --- |
| `LlmComponent` | Registers itself for scheme `llm`. One scheme, by design. |
| `LlmEndpoint` | Created from `llm://<connectionFactoryName>?...`. Resolves the named factory, owns options, exposes `IEndpointStatistics` for tsak.web. |
| `LlmConnectionFactory` | POCO with `Provider`, `ModelId`, API key (or secret-ref), tuning defaults. `Build()` produces an `ILlmProvider`. |
| `ILlmProvider` | The only thing that talks HTTP. Production: `OpenAiProvider` (covers 14 vendors). Tests: `StubProvider` and `FakeProvider`. |
| `IAgentEngine` | The tool-use loop. One inbound exchange = one agent run = N provider calls. Handles iterations, transcript, tool dispatch, governance hooks. |
| `IToolDescriptorRegistry` + `RouteToolBridge` | Tools-as-routes machinery. |
| `IPromptTemplateRegistry` | Versioned prompt store backing the `#`-ref resolver. |
| `Fluent/Llm.cs` | `Llm.Factory("...")` builder used to compose the URI. |
| `Extensions/...` | Sugar: `route.Llm("factory", b => ...)` for inline LLM steps; `.AsLlmTool(...)` for tool registration. |

---

## Quick start

### 1. Register

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteLlm();

    route.Services.AddLlmConnectionFactory("claude", f =>
    {
        f.Provider        = "anthropic";   // resolves to https://api.anthropic.com/v1/
        f.ModelId         = "claude-haiku-4-5";
        f.ApiKeySecretRef = "anthropic.api-key";
        f.Temperature     = 0.2;
        f.MaxTokens       = 1024;
    });

    route.AddRouteBuilder<MyRoutes>();
});
```

`AddRedbRouteLlm()` registers `IAgentEngine`, `IToolDescriptorRegistry`, `InMemoryPromptTemplateRegistry`, `LlmComponent`, and the inline-`.Llm()` extension wiring. One line — everything you need.

### 2. Use it

```csharp
public sealed class MyRoutes : RouteBuilder
{
    public override void Configure()
    {
        From("kafka://orders")
            .To("llm://claude?temperature=0.1&maxTokens=512")
            .To("kafka://orders.translated");
    }
}
```

That's the full minimum-viable shape. From here, everything else is variations.

---

## The four DSL shapes

### 1. `.To("llm://…")` — model as a pipeline step

This is the most common shape: the producer treats the inbound message as **one user turn**, runs the agent loop to completion, and writes the assistant text into `Out.Body`.

```csharp
From("kafka://orders")
    .To("llm://claude?temperature=0.1&maxTokens=512&conversation=header")
    .To("kafka://orders.translated");
```

What happens, step by step:

1. **Input.** `exchange.In.Body` is wrapped as a single `LlmTextBlock`. Any other type is `.ToString()`'d.
2. **Tools.** The `?tools=` parameter is parsed: missing/empty = no tools, `*` = every tool in the registry, CSV = named tools only.
3. **Agent loop.** `AgentEngine.RunAsync` cycles `provider call → optional tool_use → RouteToolBridge dispatch → next call → …` until `EndTurn`, `MaxIterations`, or cancellation.
4. **Output.** Assistant text in `Out.Body`. Headers populated with usage, model id, stop reason, tool iterations.

This is the shape to use when "an LLM call is one stage of a longer pipeline" — Kafka in, Kafka out, with an LLM in the middle.

### 2. `From("llm://…")` — scheduled agent (consumer)

`From("llm://factory?schedule=...")` is **not** "listen for replies" (LLMs are pull, not push). It is a scheduler that fires a fresh agent run every interval and pushes the assistant reply down the rest of the route as a normal exchange.

```csharp
From("llm://groq?schedule=5m" +
     "&systemPromptRef=#watchdog-system" +
     "&initialBodyRef=#daily-brief" +
     "&tools=*")
    .To("rabbitmq://alerts");
```

Use cases:

- **Watchdog agents** — every five minutes, the agent checks health endpoints (via tools), produces a verdict, drops it on a queue.
- **Scheduled report generation** — every morning at 8:00, the agent summarizes yesterday's traffic and emails it.
- **Self-improving agents with conversation memory** — the agent's own previous replies are part of its context (with `conversation=property`).

`?schedule=` accepts simple intervals: `500ms`, `30s`, `5m`, `1h`. For cron expressions, prefer `From("quartz://...").To("llm://...")` — Quartz is already a scheduler, no need to duplicate it inside the LLM consumer.

The agent's user prompt comes from `?initialBodyRef=`; the system prompt comes from `?systemPromptRef=`. Both honour the `#`-ref pattern (see [Dynamic prompts](#dynamic-prompts--the--registry-pattern)).

> **This is the shape no other ESB has.** Apache Camel's `langchain4j-*` family is producer-only — its "scheduled agent" pattern is `from("timer:...").to("langchain4j-chat:...")`, which couples *what fires the agent* and *what the agent says* into the same route. We treat the LLM endpoint itself as a scheduler.

### 3. `.Llm("name", b => …)` — inline C# sugar

When the LLM step is just an inline transformation, skip the URI:

```csharp
From("seda://drafts")
    .Llm("claude", x => x
        .WithSystemPrompt("Rewrite the user's text in formal English. Reply with text only.")
        .WithTemperature(0.0)
        .WithMaxTokens(800))
    .To("seda://drafts.formal");
```

`Llm(...)` is a thin extension over `Process(Func<IExchange, ...>)`. It calls `IAgentEngine.RunAsync` directly — same result as `.To("llm://...")`, but compiler-checked parameters, IntelliSense for tuning options, no URI escaping.

**Trade-off:** inline `.Llm(...)` requires `exchange.ServiceProvider` to resolve `IRouteContext` at runtime. Inside a hosted route this is automatic; in unit tests, build the exchange via `Exchange.Create(msg, scopeFactory)` so the service provider flows through.

### 4. `.AsLlmTool("name")` — turn any route into a tool

> **Tools are routes.** A tool is an ordinary `RouteBuilder` route mounted with one extra DSL aspect: `.AsLlmTool("name")`.

```csharp
r.From("rabbitmq://orders.lookup")
    .AsLlmTool("lookup_order")
        .Description("Look up an order by id from the orders queue.")
        .Input("{ \"orderId\": \"string\" }")
    .Process(e => e.Out!.Body = ResolveOrder(e.In.Body));
```

That's the tool. The agent loop discovers it through `IToolDescriptorRegistry`, and when the model emits a `tool_use` block with `name: "lookup_order"`, `RouteToolBridge` invokes the route as request/reply. Whatever lands in the final `Out.Body` is handed back to the model as the tool result.

This is **the architectural decision** that makes the connector cross-cutting: every existing redb.Route component (Kafka, RabbitMQ, HTTP, SQL, file, redb, …) becomes a potential LLM tool with one method call. **No per-connector LLM package, ever.** `0 bumps` across 22 connectors.

### Which shape to pick

| Use case | Pick |
| --- | --- |
| Mixed-transport pipeline (Kafka → llm → SQL) | `To("llm://…")` |
| Multiple LLM hops with different prompts | `To("llm://…")` + `Process` for headers |
| One-off inline call from C# code | `.Llm(name, b => …)` |
| Periodic agent that *generates* messages | `From("llm://…?schedule=…")` |
| Make any existing route callable by the model | `.AsLlmTool("…")` after `.From(...)` |

---

## Headers vs URI options — per-message overrides

Per-message headers always win over URI options. This lets a single endpoint URI handle many variations without rewrites:

```csharp
.Process(e => e.In.Headers[LlmHeaders.SystemPrompt] = "Reply in French.")
.To("llm://claude?systemPromptRef=defaultPrompt")  // header wins for THIS message
```

Two LLM hops in a row, different system prompts, same connection factory, no URI proliferation:

```csharp
From("direct:translate-then-summarize")
    .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
        "Translate to English. Reply with translation only.")
    .To("llm://claude")
    .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
        "Summarize in one sentence.")
    .To("llm://claude")
    .To("seda://summaries");
```

The URI is unchanged; only the header changes between hops. This is *the* pattern when the same factory is used for multiple steps with different prompts.

---

## Dynamic prompts — the `#`-registry pattern

`?initialBodyRef=` and `?systemPromptRef=` carry the framework-wide **registry-ref convention**: a leading `#` turns the value into a lookup key instead of a literal. This matches how Kafka/S3/Firebase already resolve `#name` connection factories from `IRouteContext.GetFromRegistry`.

### Resolution order

For a value `"#daily-brief"`:

1. **`IPromptTemplateRegistry`** — if a template named `daily-brief` exists, the latest version's body wins. Templates are versionable, so eval replays bind to a specific revision.
2. **`IRouteContext` registry** — fallback to a plain string registered via `ctx.AddToRegistry("daily-brief", "...")`.
3. If neither resolves, the result is `null` (treated as empty user prompt / no system prompt).

Plain values without a `#` prefix are passed through verbatim — backwards compatible with the literal-string usage.

### Why this matters: prompt-as-mutable-data

It decouples **who owns the prompt** from **who calls the model**. One route refreshes the value on its own schedule, another route consumes it. They share nothing but a name in the registry.

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

Route B never changes. Route A can be replaced with a Kafka consumer, an HTTP webhook, an S3 polling job — the agent route doesn't care.

### Versioned templates

Same syntax, but resolved through `IPromptTemplateRegistry`:

```csharp
await templates.SetAsync(new PromptTemplate
{
    Name = "watchdog-system",
    Version = "v3",
    Body = "You are an SRE watchdog. Reply with PASS or FAIL only."
});
// Then ?systemPromptRef=#watchdog-system picks v3 (the latest) at every tick.
```

Eval replays can bind to a specific version (`?systemPromptRef=#watchdog-system@v2`) — coming in Phase 2 with explicit version pinning.

---

## Tool dispatch deep dive

When `.AsLlmTool("name")` is added after `.From(...)`, three things happen:

1. The route's `From()` URI is captured.
2. A `RouteToolBridge` is built that wraps the URI and a description into an `ILlmToolDescriptor`.
3. The descriptor is registered against `name` in `IToolDescriptorRegistry`.

When the model emits a `tool_use` block at runtime:

1. `AgentEngine` looks up the descriptor by name.
2. The bridge builds an `IMessage` with the input JSON body.
3. `IProducerTemplate.RequestBody(uri, msg)` invokes the route as request/reply.
4. The route runs end-to-end. Whatever lands in `exchange.Out.Body` (or `In.Body` when `Out` is null) at the end of the pipeline is serialized and handed back to the model as the tool result.

That last point is critical: **the tool's result is the final body of the route**, not the body of any specific `.To(...)`. Three patterns work:

### Pattern A — explicit response in a final `.Process` (most predictable)

```csharp
r.From("direct:tool-lookup")
    .AsLlmTool("lookup")
        .Description("Look up the magic word for a key.")
        .Input("""{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}""")
    .Process(e =>
    {
        var input  = JsonSerializer.Deserialize<LookupInput>((string)e.In.Body!);
        var result = DoLookup(input);
        e.Out ??= e.In.Clone();
        e.Out.Body = JsonSerializer.Serialize(result);   // ← returned to the model
    });
```

This is what live tests use because it makes assertions trivial.

### Pattern B — request/reply `.To` populates `Out.Body` itself

```csharp
r.From("direct:tool-customer")
    .AsLlmTool("get_customer")
    .To("redb://customers/getById");                     // Out.Body = customer JSON
```

When the underlying connector is request/reply (HTTP, redb, SQL), it already populates `Out.Body`. The route is one line.

### Pattern C — fire-and-forget `.To` with a manual response

```csharp
r.From("direct:tool-publish")
    .AsLlmTool("publish_event")
    .Process(e => e.In.Body = BuildKafkaPayload(e.In.Body))
    .To("kafka://events")
    .Process(e =>
    {
        e.Out ??= e.In.Clone();
        e.Out.Body = "{\"ok\":true}";                    // ← what the model sees
    });
```

When the underlying transport is fire-and-forget (Kafka, RabbitMQ publish), append a `.Process` that fabricates a response body. The model sees `{"ok":true}` and moves on.

### Tool filter: the `?tools=` parameter

| Value | Meaning |
| --- | --- |
| (omitted) | no tools, plain chat |
| `*` | every tool in `IToolDescriptorRegistry` |
| `lookup_order,publish_event` | explicit allow-list |

Per-call filtering matters: an agent that does customer lookup should not also have access to `publish_payment`, even if both routes are mounted. The filter lives on the URI, so different LLM endpoints can expose different tool subsets from the same registry.

---

## Pre-built homeless tools

These ship in `redb.Route.Llm.Tools` (a separate package — homeless tools that don't have a natural connector home).

### `HttpFetchTool`

A safe, allow-listed HTTP `GET`. The model passes `{"url":"…"}`, the tool fetches, returns body + status.

```csharp
context.AddService(typeof(ILlmToolDescriptor),
    new HttpFetchTool(new HttpFetchOptions
    {
        HostAllowlist = new[] { "api.example.com", "docs.example.com" },
        Timeout       = TimeSpan.FromSeconds(5),
        MaxBytes      = 256 * 1024
    }));

r.From("direct:agent")
    .To(Llm.Factory("claude").Tools("http_fetch").AsUri())
    .To("mock:done");
```

`HostAllowlist` is enforced before the request is made — out-of-list URLs come back as a tool error without ever leaving the process. This is the recommended way to give an agent the open web *with a leash*.

### Coming soon

> The next session will add three deps-free utility tools and one HTTP-backed search tool:
>
> - **`JsonPathTool`** — extract a value by JSONPath from inbound JSON.
> - **`RegexExtractTool`** — capture-by-name from inbound text.
> - **`MathEvalTool`** — shunting-yard arithmetic, no `NCalc`, no eval injection.
> - **`WebSearchTool` (Tavily)** — `https://api.tavily.com/search` with API key.

Plus a brand-new `redb.Route.Exec` connector for shell commands, with `.AsLlmTool("shell")` as the natural LLM-side mounting point. See the comparison with Camel: their `camel-exec` is producer-only; we add a scheduled-consumer mode using the same `PeriodicTimer` pattern as `LlmConsumer`.

---

## Provider matrix — 14 OpenAI-compat backends

One `OpenAiProvider` class, fourteen vendors. The only thing that changes is the base URL and (sometimes) auth-header quirks.

| `Provider` value | Base URL | Notes |
| --- | --- | --- |
| `openai` | `https://api.openai.com/v1/` | Default. The reference implementation. |
| `anthropic` / `claude` | `https://api.anthropic.com/v1/` | **Claude via the official OpenAI-compat endpoint.** Live-tested with Haiku 4.5 and Sonnet 4.6. See [the spotlight](#spotlight-anthropic-claude). |
| `groq` | `https://api.groq.com/openai/v1/` | Fast Llama 3 / Llama 3.3, free tier. Strict-assertion test target. |
| `cerebras` | `https://api.cerebras.ai/v1/` | Llama 3.1 70B, very fast. |
| `openrouter` | `https://openrouter.ai/api/v1/` | Aggregator. Adds `HTTP-Referer` + `X-Title` headers automatically. |
| `gemini` / `google` | `https://generativelanguage.googleapis.com/v1beta/openai/` | Gemini's own OpenAI-compat surface. |
| `github-models` / `github` | `https://models.inference.ai.azure.com/` | GitHub Models. |
| `mistral` | `https://api.mistral.ai/v1/` | Mistral Small / Large. |
| `together` | `https://api.together.xyz/v1/` | Together AI. |
| `huggingface` / `hf` | `https://router.huggingface.co/v1/` | HF inference router. |
| `deepseek` | `https://api.deepseek.com/v1/` | DeepSeek. |
| `ollama` | `http://localhost:11434/v1/` | Local. No API key needed. |
| `lmstudio` | `http://localhost:1234/v1/` | Local. |
| any other / `custom` | falls through to `https://api.openai.com/v1/`, **override `BaseUrl`** | For self-hosted gateways (vLLM, llama.cpp server, in-house). |

Adding a fifteenth vendor is a one-line change to `OpenAiProvider.ResolveDefaultBaseUrl`.

### Per-vendor configuration cheat-sheet

```csharp
// OpenAI
services.AddLlmConnectionFactory("openai", f =>
{
    f.Provider = "openai";
    f.ModelId  = "gpt-4o-mini";
    f.ApiKey   = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
});

// Anthropic Claude (via OpenAI-compat endpoint)
services.AddLlmConnectionFactory("claude", f =>
{
    f.Provider = "anthropic";
    f.ModelId  = "claude-haiku-4-5";
    f.ApiKey   = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
});

// Groq
services.AddLlmConnectionFactory("groq", f =>
{
    f.Provider = "groq";
    f.ModelId  = "llama-3.3-70b-versatile";
    f.ApiKey   = Environment.GetEnvironmentVariable("GROQ_API_KEY");
});

// Local Ollama — no key
services.AddLlmConnectionFactory("local", f =>
{
    f.Provider = "ollama";
    f.ModelId  = "qwen2.5:7b";
});

// In-house gateway
services.AddLlmConnectionFactory("inhouse", f =>
{
    f.Provider = "custom";
    f.BaseUrl  = new Uri("https://llm-gateway.internal/v1/");
    f.ModelId  = "internal-model-v3";
    f.ApiKey   = "<vault-resolved-key>";
});
```

---

## Spotlight: Anthropic Claude

Claude is the most reliable closed-source model the connector has access to, and the live-test suite uses it as the **strict-assertion** reference. Everything below is verified end-to-end against the real Anthropic endpoint at `https://api.anthropic.com/v1/`.

### Why Claude through OpenAI-compat (not native Messages API)

Anthropic publishes an OpenAI-compatible endpoint that speaks the same `chat/completions` contract as OpenAI itself. That means **one line in `OpenAiProvider`** wires Claude in:

```csharp
"anthropic" or "claude" => new("https://api.anthropic.com/v1/"),
```

— and our universal `OpenAiProvider` handles requests, responses, tool calls, streaming and finish reasons identically. No new provider class, no Anthropic SDK, no separate retry logic.

A native `AnthropicProvider` (Messages API) is still on the roadmap, but it's reserved for features the OpenAI-compat surface doesn't expose: prompt caching, computer-use, fine-grained image content blocks. **For 95% of production chat / tool-use use cases, the OpenAI-compat path is fine.**

### Models

The Claude family rolls forward continuously. Model IDs the connector has been tested against:

| Model | Use case in tests | Why |
| --- | --- | --- |
| `claude-haiku-4-5` | Strict literal-token chat, tool-use loop | Smaller, fastest, cheapest. Reliably follows "reply with the literal token X". |
| `claude-sonnet-4-6` | Two-hop translation/summarization chain | Bigger, more degrees of freedom. Used for fuzzy assertions ("French-shaped output"). |
| `claude-opus-4-7` | (Available; not used in CI for cost reasons) | Top-of-stack for complex agentic work. |

### What lands in tests

The `ClaudeChatTests` suite runs three scenarios. All three pass deterministically.

**1. Literal-token chat (`Claude_Haiku_45_From_To_Mock_DeliversAnswer`)** — system prompt asks for the literal token `pong`, user sends `ping`, the assertion is `reply.Should().Contain("pong")`. Haiku 4.5 returns exactly `pong` in ~800ms.

```csharp
[EnvFact("REDB_LLM_ANT_API03_KEY")]
public async Task Claude_Haiku_45_From_To_Mock_DeliversAnswer()
{
    await using var host = LiveLlmHost.Build()
        .AddFactory("claude", new LlmConnectionFactory
        {
            Provider = "anthropic",
            ModelId = "claude-haiku-4-5",
            ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY"),
            Temperature = 0.0,
            MaxTokens = 32
        });

    await host.StartAsync(r =>
    {
        r.From("direct:chat")
            .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                "Reply with the literal token 'pong' and nothing else. No punctuation.")
            .To(LlmDsl.Factory("claude").Temperature(0.0).MaxTokens(8).AsUri())
            .To("mock:result");
    });

    await host.SendAsync("direct:chat", "ping");

    var sink = host.Mock("mock:result");
    sink.ReceivedCount.Should().Be(1);
    var reply = ((string)sink.ReceivedExchanges[0].In.Body!).Trim().ToLowerInvariant();
    reply.Should().Contain("pong");
}
```

**2. Tool-use loop (`Claude_Haiku_45_Tools_LookupAgent_UsesTool`)** — a `lookup` tool is mounted via `EchoToolRoute` (a `RouteBuilder` that registers `direct:tool-lookup` with `.AsLlmTool("lookup")`). The system prompt instructs Claude to call the tool first, then echo the answer. Claude calls the tool, captures the canned reply (`{"answer":"the magic word is rosebud"}`), and the final assistant message contains `rosebud`.

```csharp
var lookup = new EchoToolRoute(
    toolName: "lookup",
    description: "Look up the magic word for a key.",
    inputSchema: """{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}""",
    replyJson: """{"answer":"the magic word is rosebud"}""");

await host.StartAsync(lookup, r =>
{
    r.From("direct:agent")
        .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
            "Use the lookup tool to find the magic word, then reply with that exact phrase. " +
            "Do not invent a word — call the tool first.")
        .To(LlmDsl.Factory("claude").Tools("lookup").MaxIterations(4).AsUri())
        .To("mock:done");
});

await host.SendAsync("direct:agent", "What is the magic word?");

lookup.CapturedInputs.Should().NotBeEmpty();         // the tool was called
final.Should().Contain("rosebud");                   // the model echoed the result
```

**3. Two-hop chain (`Claude_Sonnet_46_TwoHopChain_SummarizeThenTranslate`)** — body flows through two `To("llm://…")` hops in a row. First hop summarizes English to one sentence. Second hop translates that sentence to French. No glue code between hops; the assistant text from hop 1 is the user message for hop 2.

```csharp
r.From("direct:chain")
    .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
        "Summarize the user's text in one short English sentence.")
    .To(LlmDsl.Factory("claude").Temperature(0.0).MaxTokens(64).AsUri())
    .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
        "Translate the user's text to French. Reply with the translation only, no preamble.")
    .To(LlmDsl.Factory("claude").Temperature(0.0).MaxTokens(64).AsUri())
    .To("mock:done");
```

The assertion is loose — French-shaped output, not a specific translation — because Sonnet has more freedom on a fuzzy task than Haiku does on `"return pong"`.

### Production-readiness checklist for a Claude-backed route

| Concern | What to do |
| --- | --- |
| API key in code | Don't. Use `LlmConnectionFactory.ApiKeySecretRef = "anthropic.api-key"` and resolve via your secret store. |
| Rate limits | Wrap the `To("llm://...")` in `.Throttle(...)`. Anthropic has per-minute and per-day limits. |
| Retries on 5xx | `.OnException<HttpRequestException>().MaximumRedeliveries(3).Backoff(...)` — same as any other connector. |
| Conversation memory | Set `LlmHeaders.ConversationId` on inbound messages (`conversation=header` on the URI). Store-backed memory is Phase 2. |
| Token budget | Phase 2 governance hook. Today, hard-cap with `MaxTokens` on the factory and on the URI per-call. |
| Cost observability | Read `llm.tokens.in` / `llm.tokens.out` headers, push to your metrics pipeline via `WireTap`. |

---

## URI parameter reference

```text
llm://<connectionFactoryName>
    ?temperature=0.2
    &maxTokens=1024
    &topP=0.9
    &systemPromptRef=<literal | #registry-key>
    &initialBodyRef=<literal | #registry-key>     # consumer only
    &conversation=none|header|property
    &stream=true                                   # producer only (skeleton in Phase 1)
    &schedule=500ms|30s|5m|1h                      # consumer only
    &maxIterations=8
    &tools=*|name1,name2
    &connectionFactory=<name>                      # alternative to the URI host
```

| Param | Producer (`To`) | Consumer (`From`) | Notes |
| --- | --- | --- | --- |
| `temperature`, `maxTokens`, `topP` | yes | yes | Floats / ints. Override factory defaults. |
| `systemPromptRef` | yes (header beats it) | yes | Honours `#`-ref. |
| `initialBodyRef` | n/a | yes | Honours `#`-ref. The consumer's user-prompt source. |
| `conversation` | yes | yes (`header` resolves to none — consumer-born exchanges have no inbound header) | Future: persistent memory backends. |
| `stream` | yes (`Out.Body = IAsyncEnumerable<string>`) | n/a | Phase 1 skeleton. |
| `schedule` | n/a | required | `500ms` / `30s` / `5m` / `1h`. |
| `maxIterations` | yes | yes | Hard ceiling on tool-loop turns. Default 8. |
| `tools` | yes | yes | `*`, CSV, or omitted. |

---

## Headers reference

### Headers the connector reads (per-message inputs)

| Header | Equivalent URI param | Purpose |
| --- | --- | --- |
| `LlmHeaders.SystemPrompt` (`llm.system_prompt`) | `systemPromptRef` | Per-message system prompt. Wins over URI. |
| `LlmHeaders.ConversationId` (`llm.conversation_id`) | n/a | Conversation correlation key. |
| `LlmHeaders.Temperature` (`llm.temperature`) | `temperature` | Per-message override. |
| `LlmHeaders.MaxTokens` (`llm.max_tokens`) | `maxTokens` | Per-message override. |

### Headers the connector writes (per-message outputs)

| Header | Meaning |
| --- | --- |
| `llm.provider.id` | `"anthropic"`, `"openai"`, `"groq"`, `"stub"`, … |
| `llm.model.id` | The actual model used (may differ from factory default if overridden). |
| `llm.tokens.in` / `llm.tokens.out` | Token usage from `usage` field in the response. |
| `llm.tool.iterations` | How many loop steps the agent took. |
| `llm.stop_reason` | `EndTurn`, `ToolUse`, `MaxTokens`, `Other`. |
| `llm.raw_stop_reason` | Vendor-native value (`"stop"`, `"tool_calls"`, `"length"`, `"content_filter"`). |

These flow downstream like any other headers — `WireTap` them to your metrics pipeline, branch on them, log them, persist them.

---

## Observability — headers, OTel, tsak.web

The connector hooks into redb.Route's existing observability stack. Nothing extra to wire.

**OpenTelemetry traces.** Every `LlmEndpoint` call is a span on `RouteActivitySource`. Tags include provider id, model id, token counts, iterations, stop reason. If your collector ingests redb.Route spans, LLM calls are already in there.

**Metrics.** `LlmMetrics` registers a Counter (`llm.requests`) and Histogram (`llm.duration_ms`) on the shared Meter. Tagged by provider and model.

**Endpoint statistics.** `LlmEndpoint` exposes `IEndpointStatistics`: `MessagesIn`, `MessagesOut`, `BytesIn`, throughput, `LastErrorMessage`, `HealthStatus`. `tsak.web` reads these the same way it reads from Kafka or RabbitMQ — no per-connector adapter.

**Cost dashboards.** Persist `llm.tokens.in` / `llm.tokens.out` to your OLAP store via a `WireTap` route, multiply by the per-vendor rate, you have a cost dashboard. The connector doesn't ship a cost calculator (vendor pricing changes too often), but every input it needs is already on the message.

---

## Storage & Persistence

By default the agent loop is stateless — fast for tests, useless for production. **One DI line** swaps every state surface (transcripts, approvals, budgets, idempotency, audit) onto redb without touching a single route:

```csharp
route.Services.AddRedbRouteLlm();
route.Services.AddRedbIdempotentRepository();
route.Services.AddRedbLlmStorage();   // ← persists everything to redb
```

After this call, your existing `.From("kafka://chat.in").To(Llm.Factory("claude").AsUri())` route starts writing every turn into a tree-structured `ConversationProps` + `MessageProps` schema, every tool call into `ToolAuditProps`, every approval decision into `ApprovalProps`, every accepted retry into `ToolCacheProps`. The join key is one header — `llm.conversation.id`.

**For the why and the how** — concrete DSL recipes (multi-turn chat with `ConversationFromHeader()`, Slack-backed approval gates, hard cost caps via `IBudgetEnforcer`, idempotent tool retries, branching transcripts, scheduled `From("llm://…")` agents with persistent run history) and the full schema tour, see [`STORAGE.md`](STORAGE.md).

---

## Testing strategy

The test suite lives in `redb.Route.Tests.Llm`. It splits into three layers:

### Layer 1 — wiring tests (no network)

`LlmComponentTests`, `LlmEndpointTests`, `LlmProducerTests`, `LlmIntegrationTests`, `LlmServiceCollectionExtensionsTests`, `LlmBuilderTests`, `LlmConnectionFactoryTests`, `LlmEndpointOptionsTests`, `StubProviderTests`. Service registration, options binding, scheme registration, end-to-end with `FakeProvider`. Run on every `dotnet test`.

### Layer 2 — DSL showcase (live, gated by env)

`DslShowcase/*Tests.cs`. Each file is the **smallest live shape that proves a single DSL claim**. They are written to be **read like documentation**.

| File | DSL shape | Provider |
| --- | --- | --- |
| `BasicChatTests.cs` | `From("direct:chat") → To("llm://…") → To("mock:result")` | Groq, Gemini, Mistral |
| `InlineLlmTests.cs` | `From("direct:chat") → .Llm("name", b => …) → To("mock:result")` | Groq, Mistral |
| `ToolRouteTests.cs` | A `RouteBuilder` mounted with `.AsLlmTool(...)` is auto-discovered by the agent loop | Groq |
| `HttpFetchToolTests.cs` | The pre-built `HttpFetchTool` plugs into a route with one line | Scripted |
| `ChainedLlmTests.cs` | Two `To("llm://…")` hops carry the body forward | Groq |
| `RegistryDrivenPromptTests.cs` | `?systemPromptRef=#name` resolves through the registry chain | Scripted |
| `ClaudeChatTests.cs` | All shapes against Anthropic Claude — chat, tool-use, two-hop chain | **Anthropic Claude** |

These tests are gated with `[EnvFact("REDB_LLM_<provider>_KEY")]` — they're **skipped, not failed**, when the matching key is unset. A clean checkout still passes `dotnet test`.

### Layer 3 — provider-specific live tests

`LiveProviderTests` for the older free-tier providers (Groq, Mistral, Cerebras, Gemini, OpenRouter), and `ClaudeChatTests` (the strict-assertion reference).

### How assertions are tuned per provider

Different providers have different reliability. The test suite reflects this:

| Provider | Assertion strictness | Why |
| --- | --- | --- |
| **Anthropic Claude (Haiku 4.5 + Sonnet 4.6)** | **Strictest** — literal tokens, tool-call captures | Reliably follows "reply with the literal token X" instructions. Used as the reference suite. |
| Groq + Llama 3.3 70B | Strict — literal `pong` | Fast, free, reliable. |
| Mistral / Cerebras | "Some non-empty reply" | Small free-tier models phrase things differently each call. |
| Gemini / OpenRouter | "Some non-empty reply" + tolerance for 429 | Quotas; failures are usually quota artefacts, not connector regressions. |

### Test helpers worth knowing

| Helper | Role |
| --- | --- |
| `LiveLlmHost` | Hand-rolled wiring of `RouteContext` + `LlmComponent` + `AgentEngine` + `IProducerTemplate` + `IToolDescriptorRegistry`. Lets a test stay close to a Camel-style example. |
| `EchoToolRoute` | A `RouteBuilder` that mounts `direct:tool-{name}` with `.AsLlmTool(name)` and captures every input the agent passes. |
| `LocalHttpServer` | Tiny in-process `HttpListener` server for testing HTTP-backed tools deterministically. |
| `FakeProvider` | Scriptable `ILlmProvider`. `EnqueueText`, `EnqueueToolUse`, exposes `CallCount` and `CapturedRequests`. Network-free. |
| `EnvLoader` | `[ModuleInitializer]` that loads `.env.local` once per process, never overwriting set variables. |
| `EnvFactAttribute` | xUnit `FactAttribute` that sets `Skip` when the named env var is missing. |
| `LiveLlmCollection` | xUnit `CollectionDefinition` with `DisableParallelization = true` — live tests don't race against shared free-tier quotas. |

### Adding a new live test — six-step recipe

1. Pick the smallest DSL shape that proves the new behavior.
2. Use `LiveLlmHost.Build()` and `.AddFactory(name, factory)`.
3. Tag: `[Trait("Category", "LiveLlm")] [Collection("LiveLlmSerial")]`.
4. Gate with `[EnvFact("REDB_LLM_<provider>_KEY")]`.
5. Pick assertion strictness matching the provider's reliability.
6. Read the test out loud. If it sounds like prose, ship it. If it reads like wiring, simplify.

---

## Apache Camel comparison

Camel's LLM story lives in a family — `camel-langchain4j-chat`, `-embeddings`, `-tokenizer`, `-tools`, `-agent`, `-web-search`, plus `camel-anthropic`, `camel-djl`, `camel-huggingface`. It's the closest analogue.

### What both ship

| Capability | `camel-langchain4j-*` | `redb.Route.Llm` |
| --- | --- | --- |
| Chat completion endpoint | `langchain4j-chat://chatId?chatModel=#model` | `llm://factory` |
| Tool dispatch into a route | `langchain4j-tools://name` (separate component) | `.AsLlmTool("name")` (DSL aspect on any `From(...)`) |
| Agent loop | `langchain4j-agent://...` | built into `IAgentEngine` |
| Scheduled invocation | only via `from("timer:...").to("langchain4j-chat:...")` — the LLM is producer-only | **`From("llm://factory?schedule=...")` is a first-class consumer** |
| Streaming responses | LangChain4j streaming chat model | `?stream=true` (skeleton) |
| Registry refs (`#name`) | yes — Camel-wide | yes — framework-wide; works for connection factories *and* prompts |
| Provider matrix | 25+ via LangChain4j (Anthropic, Bedrock, Vertex, Azure OpenAI, OpenAI, Mistral, Ollama, …) | 14 OpenAI-compatible behind one `OpenAiProvider` (incl. Anthropic Claude live-tested) |
| Embeddings / vector store | yes — `langchain4j-embeddings` + LangChain4j vector stores | Phase 2 (`embed://`, `vector://` schemes planned) |

### Where redb.Route.Llm wins

- **`From("llm://...")` is a first-class consumer.** No equivalent in the LangChain4j family — every component there is producer-only. Self-driven agents in one declarative line.

- **One scheme.** `llm://` and only `llm://`. Streaming, tools, scheduling and conversation are query options on the same endpoint — not five sibling components.

- **Tools are routes, not a separate component.** `.AsLlmTool("name")` is one DSL aspect. `0 bumps for 22 connectors`.

- **One `OpenAiProvider` for 14 vendors.** Adding a vendor is a base-URL change, not a new package.

- **`#`-registry refs apply to prompts.** Versioned prompts and "another route refreshes the prompt" both work without modifying the LLM route.

- **Endpoint statistics for free.** Every `LlmEndpoint` exposes `IEndpointStatistics` the same as Kafka or RabbitMQ; tsak.web renders it with no extra wiring.

- **Small dependency graph.** `redb.Route` + `Microsoft.Extensions.*` + `System.Text.Json`. No LangChain wrapper, no vendor SDKs.

### Where Camel `langchain4j-*` wins today

- Wider provider matrix (Bedrock, Vertex, Azure OpenAI with native auth).
- Embeddings, RAG, document loaders, web search shipping today (we're Phase 2).
- Chat memory stores in the box.
- Production-ready streaming.

### Architectural differences worth knowing

- **Wrapper vs. transport.** Camel's `langchain4j-*` is an adapter over LangChain4j; we talk HTTP directly through `ILlmProvider` and own our agent loop.
- **One scheme vs. one component per primitive.** Camel mirrors LangChain4j's type system at the URI layer. We collapse those into options on a single endpoint.
- **Tool registration philosophy.** Camel exposes tools via a dedicated URI; we annotate routes with `.AsLlmTool`. Practical consequence: zero coordination across connector packages to support LLM tools.
- **Prompt-as-data.** We route prompts through a versioned registry (`#name`) by default. Camel does this with custom processors.

---

## Roadmap

### Phase 1 (current — shipped)

- ✅ `OpenAiProvider` covering 14 OpenAI-compatible providers (incl. Anthropic Claude live-tested with Haiku 4.5 + Sonnet 4.6).
- ✅ `LlmProducer` (`.To("llm://...")`).
- ✅ `LlmConsumer` (`From("llm://...?schedule=...")`).
- ✅ Inline `.Llm("factory", ...)` extension.
- ✅ Tools-as-routes — `.AsLlmTool` aspect, `RouteToolBridge`.
- ✅ `HttpFetchTool` in `redb.Route.Llm.Tools`.
- ✅ `#`-ref resolver for `systemPromptRef` / `initialBodyRef` (producer + consumer).
- ✅ `IPromptTemplateRegistry` + `InMemoryPromptTemplateRegistry`.
- ✅ OTel + tsak.web statistics.
- ✅ DSL showcase test suite (BasicChat, InlineLlm, ToolRoute, ChainedLlm, HttpFetchTool, RegistryDrivenPrompt, ClaudeChat).

### Phase 1.5 (next session — confirmed scope)

- Utility tools in `redb.Route.Llm.Tools`: `JsonPathTool`, `RegexExtractTool`, `MathEvalTool`, `WebSearchTool` (Tavily).
- New connector `redb.Route.Exec` — shell command runner, **producer + scheduled consumer**, allowlist + cwd + timeout + env-overrides + output limits.
- One live integration test: Claude calls a `shell` tool wired through `redb.Route.Exec` via `.AsLlmTool("shell")`.

### Phase 2

- Native `AnthropicProvider` (Messages API) — for prompt caching, computer-use, fine-grained vision.
- Streaming producer surface (real `IAsyncEnumerable<string>` in `Out.Body`).
- Governance hooks: budget, shadow, approval, redaction (cut-points already in `IAgentEngine`).
- Persistent conversation memory backed by `[RedbScheme]` POCOs.
- Embeddings / vector schemes (`embed://`, `vector://`).
- More homeless tools and connectors: SSH, Git, Browser/Playwright.

---

## FAQ

**Q: Why one `llm://` scheme instead of `llm-chat://`, `llm-stream://`, `llm-agent://`?**
A: Modes are options, not separate transports. The semantics — request goes to a model, response comes back — are the same; only knobs differ. One scheme keeps the URI surface small and IDE-completable. If a future capability *changes the transport semantics* (embeddings return vectors, not text), it gets its own scheme (`embed://`).

**Q: Why is the user prompt always `In.Body` and not configurable?**
A: It is configurable — for the consumer via `?initialBodyRef=` (with `#`-ref support). For the producer, the inbound body is, by definition, the message you want to process. If you need a fixed prompt with the body as a placeholder, do it in a `.Process` step before `.To("llm://...")` — explicit beats implicit.

**Q: Can a tool be implemented in pure C# without a route?**
A: Yes — implement `ILlmToolDescriptor` directly and register it in `IToolDescriptorRegistry`. `HttpFetchTool` is exactly that pattern. But if your tool would naturally be a route (HTTP endpoint, Kafka publish, SQL query), prefer the route + `.AsLlmTool` shape — you get retries, telemetry, and the same governance for free.

**Q: How do I A/B-test two prompts?**
A: Two registry keys (`prompt.A`, `prompt.B`), one route per arm, route A goes through `?systemPromptRef=#prompt.A`, route B through `#prompt.B`. WireTap both arms to your evaluator. With `IPromptTemplateRegistry` you can pin specific versions.

**Q: Does `conversation=header` persist across process restarts?**
A: No — Phase 1 stores conversation state only in-memory inside the `IAgentEngine` for the duration of a single agent run. Persistent multi-turn memory is Phase 2 and will be backed by `[RedbScheme]`. For now, a "conversation" is a single inbound message that may include several tool-loop turns inside one agent run.

**Q: What happens when the model returns more tokens than `MaxTokens`?**
A: The provider returns `finish_reason: "length"` and the connector surfaces it as `LlmStopReason.MaxTokens` in the `llm.stop_reason` header. The truncated assistant text is still in `Out.Body`. It is the route author's responsibility to decide whether truncated output is acceptable.

**Q: How do I add a fifteenth provider?**
A: One line in `OpenAiProvider.ResolveDefaultBaseUrl`. Or, if the provider deviates from the OpenAI contract in some non-trivial way, implement `ILlmProvider` directly — it's a small interface with one method (`CompleteAsync`) plus an optional `StreamAsync`. The factory wires the new provider in via `LlmConnectionFactory.PrebuiltProvider`.

**Q: How is this different from semantic-kernel / Microsoft.Extensions.AI?**
A: Those are SDK-shaped libraries you call from your code. They live inside one application and own the call stack. `redb.Route.Llm` is an ESB connector — the call stack is the route, and the LLM is *one stage* in a longer pipeline that may also touch Kafka, redb, S3, SQL. If your application is a redb.Route process already, this is the natural shape; if it isn't, semantic-kernel is probably less friction.
