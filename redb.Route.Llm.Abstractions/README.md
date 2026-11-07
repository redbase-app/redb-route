# redb.Route.Llm.Abstractions

Contracts package for the `redb.Route.Llm` tool surface. Apache 2.0.

Holds the **declarative** types every redb.Route LLM tool author needs, without
pulling the provider engine, HttpClient, or Anthropic/OpenAI SDK code.

## What's in this package

| Type | Role |
|------|------|
| `LlmToolCapability` | Tool metadata exposed to the model (name, description, input schema). |
| `LlmToolSafety` | Governance metadata — side-effect, cost, caching, approval, claims. |
| `ToolSideEffect`, `ToolCostClass`, `ToolCachingPolicy` | Enums used by `LlmToolSafety`. |
| `ILlmToolDescriptor` | The contract a tool implements: capability + endpoint URI builder. |
| `IToolDescriptorRegistry` | Registry of `ILlmToolDescriptor` resolved by tool name. |
| `[ExposeAsLlmTool]` | Attribute that adorns handler classes / methods to declare their tool surface. |

## Why a separate package

- The LLM **engine** (`redb.Route.Llm`) is heavy: HTTP transport, SSE parsers,
  Anthropic / OpenAI providers, governance, conversation persistence.
- The **contracts** are tiny: a handful of POCOs, an interface, and an attribute.

A connector or third-party tool author who wants to declare an
`ILlmToolDescriptor` can take a dependency on this package without inheriting
the entire engine surface.

## Dispatch model

`ILlmToolDescriptor` is **declarative + URI-building**, not self-executing.
The engine in `redb.Route.Llm` resolves the descriptor, calls
`BuildEndpointUri(inputJson, parentExchange)`, then dispatches via
`IProducerTemplate.RequestBody(endpointUri, message)`. Every tool runs as a
redb.Route exchange — gets the parent's transaction scope, headers, principal,
and DI scope for free.
