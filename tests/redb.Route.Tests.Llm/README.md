# redb.Route.Tests.Llm

Integration tests for the **redb.Route.Llm** connector. Two suites live here:

| Suite | What it proves | Network |
| --- | --- | --- |
| `LlmComponentTests`, `LlmEndpointTests`, `LlmProducerTests`, `LlmIntegrationTests`, `LlmServiceCollectionExtensionsTests`, `LlmBuilderTests`, `LlmConnectionFactoryTests`, `LlmEndpointOptionsTests`, `StubProviderTests` | Wiring, options, service registration, end-to-end with a scripted `FakeProvider` | None |
| `LiveProviderTests` | The connector talks to **real free-tier LLM endpoints** (Groq, Mistral, Cerebras, Gemini, OpenRouter) | Yes (env keys) |
| `DslShowcase/*Tests` | The DSL **as a human would read it** — `From → To → Tools` — against real providers | Yes (env keys) |

The first set runs anywhere. The other two are **gated by environment variables** and silently skipped when keys are missing — so a clean checkout still passes `dotnet test`.

---

## Why DslShowcase exists

`redb.Route.Llm` is a Camel-style ESB connector. Its whole pitch is:

> "An LLM call is one more `To(...)` in your route. A tool is one more `From(...)`."

`DslShowcase/` is meant to be **read like documentation**. Each test is the smallest live shape that proves a single DSL claim. If you want to know how the connector is used in practice, open these files first — they are deliberately short, with comments aimed at a human reader, not a CI log.

| File | DSL shape | Provider |
| --- | --- | --- |
| `BasicChatTests.cs` | `From("direct:chat") → To("llm://...") → To("mock:result")` | Groq, Gemini, Mistral |
| `InlineLlmTests.cs` | `From("direct:chat") → .Llm("name", b => b...) → To("mock:result")` | Groq, Mistral |
| `ToolRouteTests.cs` | A `RouteBuilder` mounted with `.AsLlmTool(...)` is auto-discovered by the agent loop | Groq |
| `HttpFetchToolTests.cs` | The pre-built `HttpFetchTool` from `redb.Route.Llm.Tools` plugs into a route with one line | Scripted |
| `ChainedLlmTests.cs` | Two `To("llm://...")` hops in a row carry the body forward, no glue code | Groq |
| `RegistryDrivenPromptTests.cs` | `?systemPromptRef=#name` resolves through `IPromptTemplateRegistry` / `IRouteContext` | Scripted |
| `ClaudeChatTests.cs` | Same DSL shapes against Anthropic Claude via the official OpenAI-compat endpoint — chat, tool-use, two-hop chain | **Anthropic Claude (Haiku 4.5 + Sonnet 4.6)** |

`HttpFetchToolTests` deliberately uses a scripted `FakeProvider`. The point of that test is the **DSL + tool wiring + real HTTP fetch**, not whether a 7B free-tier model copies a long URL byte-for-byte. Live tool-use behavior is covered by `ToolRouteTests`.

---

## Running the tests

```bash
# Local-only (no network, no env keys needed):
dotnet test --filter "Category!=LiveLlm"

# Live tests against any provider whose key is set:
dotnet test --filter "Category=LiveLlm"

# Just the DSL showcase:
dotnet test --filter "FullyQualifiedName~DslShowcase"

# A single live test:
dotnet test --filter "FullyQualifiedName=redb.Route.Tests.Llm.DslShowcase.BasicChatTests.Groq_From_To_Mock_DeliversAnswer"
```

All live tests are tagged `[Trait("Category", "LiveLlm")]` and run inside the `LiveLlmSerial` xUnit collection so we don't hammer free tiers from parallel workers.

---

## Environment variables

Live tests use `[EnvFact("REDB_LLM_<provider>_KEY")]` — they are **skipped** (not failed) when the matching variable is unset. The variables are:

| Variable | Used by | Free-tier reliability |
| --- | --- | --- |
| `REDB_LLM_ANT_API03_KEY` | **Anthropic Claude** (Haiku 4.5 + Sonnet 4.6 via the official OpenAI-compat endpoint) | **Highest** — used for strict assertions, including literal-token replies |
| `REDB_LLM_GROQ_KEY` | Groq (Llama 3.3 70B) | High — used for strict assertions |
| `REDB_LLM_MISTRAL_KEY` | Mistral (`mistral-small-latest`) | Decent — used for "any reply" assertions |
| `REDB_LLM_CEREBRAS_KEY` | Cerebras | Decent for chat, slow for tools |
| `REDB_LLM_GEMINI_KEY` | Google Gemini (`gemini-2.0-flash`) | Variable — daily / per-minute quotas |
| `REDB_LLM_OPENROUTER_KEY` | OpenRouter (free models) | Variable — depends on upstream |
| `REDB_LLM_GITHUB_TOKEN` | GitHub Models | Optional |

You can set these in the shell, or drop a `.env.local` file at `redb.Tsak/publish/keys/.env.local` — `TestHelpers/EnvLoader.cs` walks up from `AppContext.BaseDirectory` and loads any `KEY=VALUE` lines it finds without overwriting existing variables.

---

## Philosophy: what "live" means

We are *not* claiming every free tier is production-grade. We *are* claiming the connector works in principle against all of them. So:

- For **Anthropic Claude (Haiku 4.5 + Sonnet 4.6)** we use **strict assertions**.
  Claude is the most reliable closed-source model we have access to — it
  reliably follows "reply with the literal token X" instructions, calls the
  right tool with the right args, and produces French-shaped output on
  translate hops. The full `ClaudeChatTests` suite passes every run.
- For **Groq + Llama 3.3 70B** we use **strict assertions** ("the model must produce the literal token `pong`"). Groq is the most reliable free tier we have access to.
- For **Mistral / Cerebras** we assert that *some* non-empty reply comes back. Their small free models phrase things differently each call.
- For **Gemini / OpenRouter** we accept that these will sometimes 429. A failure here is a free-tier quota artefact, not a connector regression.

When a free-tier provider fails, look at the error body before assuming the connector is broken — `429`, daily quota, or transient rate-limits are common, and the test will pass on retry.

---

## What the helpers do

| Helper | Role |
| --- | --- |
| `LiveLlmHost` | Hand-rolled wiring of `RouteContext` + `LlmComponent` + `AgentEngine` + `IProducerTemplate` + `IToolDescriptorRegistry`, mirroring what `services.AddRedbRouteLlm()` would do. Lets a test stay close to a Camel-style example. |
| `EchoToolRoute` | A `RouteBuilder` that mounts `direct:tool-{name}` with `.AsLlmTool(name)` and captures every input the agent passes — used to assert "the model called the tool with these args". |
| `LocalHttpServer` | Tiny in-process `HttpListener` server bound to a free localhost port. Configurable response/status, captures request paths. Used by `HttpFetchToolTests`. |
| `FakeProvider` | Scriptable `ILlmProvider`. `EnqueueText`, `EnqueueToolUse`, exposes `CallCount` and `CapturedRequests`. Network-free. |
| `EnvLoader` | `[ModuleInitializer]` that loads `.env.local` once per process, never overwriting set variables. |
| `LiveLlmCollection` | xUnit `CollectionDefinition` with `DisableParallelization = true`, so live tests don't race against shared free-tier quotas. |
| `EnvFactAttribute` | `FactAttribute` that sets `Skip` when the named env var is missing. |

---

## Adding a new live test

1. Pick the smallest DSL shape that proves the new behavior.
2. Use `LiveLlmHost.Build()` and `.AddFactory(name, factory)` — do not invent your own DI bootstrap.
3. Tag the test:
   ```csharp
   [Trait("Category", "LiveLlm")]
   [Collection("LiveLlmSerial")]
   ```
4. Gate on the right env var with `[EnvFact("REDB_LLM_<provider>_KEY")]`.
5. Pick the assertion strictness that matches the provider's reliability — see the table above.
6. Read the test out loud. If it sounds like prose, ship it. If it reads like wiring, simplify.
