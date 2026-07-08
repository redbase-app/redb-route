# redb.Route.Llm — Storage Guide

> **Audience:** developers already wiring DSL routes with `redb.Route` who want
> the **LLM to be a fully-fledged pipeline citizen** — with memory, budget,
> approvals and audit trail — not a stateless HTTP call that leaves nothing
> behind.
>
> **TL;DR:** a single line — `route.Services.AddRedbLlmStorage()` — flips the
> agent loop from *"forgets everything on restart"* into a **persistent-by-default**
> system. All five surfaces (transcripts, approvals, budgets, idempotency,
> audit) move into redb. **Not a single line of route code changes** — your
> existing `.To("llm://claude")` starts persisting on its own.

---

## Contents

1. [Why storage in an LLM connector at all](#why-storage-in-an-llm-connector-at-all)
2. [One call flips everything — `AddRedbLlmStorage()`](#one-call-flips-everything--addredbllmstorage)
3. [Map — what flows where](#map--what-flows-where)
4. [The join key — `llm.conversation.id`](#the-join-key--llmconversationid)
5. [Concrete recipes — *the why and the how*](#concrete-recipes--the-why-and-the-how)
   - [A chatbot that remembers](#1-a-chatbot-that-remembers--multi-turn-via-conversationid)
   - [Inline `.Llm()` — conversation without a URI](#2-inline-llm--conversation-without-a-uri)
   - [Approval gates for dangerous tools](#3-approval-gates-for-dangerous-tools--verdict-into-redb)
   - [Hard budget per conversation](#4-hard-budget-per-conversation--050-and-the-agent-shuts-up)
   - [Idempotent tool retries](#5-idempotent-tool-retries--dont-transfer-money-twice)
   - [Audit every tool call](#6-audit-every-tool-call--what-the-agent-tried-and-what-blew-up)
   - [Branching — rewrite a turn and compare](#7-branching--rewrite-a-turn-and-compare)
   - [`From("llm://...")` — nightly agent](#8-fromllm--nightly-agent-with-permanent-memory)
6. [Schemas — how redb actually stores it](#schemas--how-redb-actually-stores-it)
7. [Stores and interfaces — reference](#stores-and-interfaces--reference)
8. [Operational gotchas](#operational-gotchas)
9. [What's next — Phase 2 / 3](#whats-next--phase-2--3)

---

## Why storage in an LLM connector at all

A bare LLM call from C# is just an HTTP client. You hit `client.SendAsync()`,
get a response, hand it to the user — **done**. A second later:

- you don't know **what** you just talked about with the model;
- you don't know **how much** it cost;
- you don't know **which tools** the agent invoked or **what they returned**;
- if the user types *"continue from where we left off"* — you scrape context by
  hand from your own data layer, because the LLM client has no memory.

These are **cross-cutting** concerns of every production agent, and ~80% of
their solutions are **identical**: write to the DB, read from the DB, by key.
So why isn't this in the box?

In `redb.Route.Llm` it **is** in the box — because redb already gives you
tree-structured objects, indexed `value_string` columns, soft-delete and
`TreeQuery`. We aren't writing "yet another little ORM for LLM logs". We
declare 9 schemas, ship 5 stores — and the agent loop starts writing into a
**typed, indexed, query-able** redb layer.

All of it — for **one line** of DI registration.

---

## One call flips everything — `AddRedbLlmStorage()`

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteLlm();              // engine + in-memory defaults
    route.Services.AddRedbIdempotentRepository();  // required by the idempotency store
    route.Services.AddRedbLlmStorage();            // ← one line: everything moves to redb

    route.Services.AddLlmConnectionFactory("claude", f =>
    {
        f.Provider        = "anthropic";
        f.ModelId         = "claude-haiku-4-5";
        f.ApiKeySecretRef = "anthropic.api-key";
    });

    route.AddRouteBuilder<MyRoutes>();
});
```

What `AddRedbLlmStorage()` does
(`Extensions/ServiceCollectionExtensions.cs:155`):

| Interface              | Default (in-memory)              | Replaced with               | Persists                           |
| ---------------------- | -------------------------------- | --------------------------- | ---------------------------------- |
| `IConversationStore`   | `InMemoryConversationStore`      | `RedbConversationStore`     | conversation tree                  |
| `IApprovalStore`       | `InMemoryApprovalStore`          | `RedbApprovalStore`         | approver decisions                 |
| `ICostBudgetStore`     | `InMemoryCostBudgetStore`        | `RedbCostBudgetStore`       | running token / $ counters         |
| `IToolIdempotencyStore`| `InMemoryToolIdempotencyStore`   | `RedbToolIdempotencyStore`  | cached tool outputs                |
| `IAgentObserver`       | `NoopAgentObserver`              | `RedbAuditObserver`         | one row per tool invocation        |

**Not a single line** in your routes changes. The old `.To("llm://claude")`
starts writing to the DB on its own — because the engine (`AgentEngine`)
already calls these five interfaces. Before this line, it called in-memory
fakes; after, it calls redb.

That's what *opt-in persistence* should look like: **not a new API**, but a
default replacement. No code migrations — your `.To("llm://claude")` works
exactly as it did, only now it has memory.

---

## Map — what flows where

```text
              .From("kafka://chat.in")
                       │
                       ▼  ──── headers["llm.conversation.id"] = userId
              ┌──────────────────────┐
              │  LlmProducer         │  (reads the header → conversationId)
              └────────┬─────────────┘
                       ▼
              ┌────────────────────────────────────────────────────┐
              │  AgentEngine (tool loop)                           │
              │                                                    │
              │  ┌── IConversationStore ──► RedbConversationStore
              │  │     • AppendAsync() per turn                    ──► ConversationProps
              │  │     • LoadPathAsync() to rebuild context        ──► MessageProps[] (tree)
              │  │                                                       │
              │  ├── IBudgetEnforcer ──► your impl                       │
              │  │     • PreCheckAsync() / RecordAndCheckAsync()         │
              │  │     • accumulates via ICostBudgetStore                ▼
              │  │                                              ──► CostBudgetProps (per-conv)
              │  ├── IApprovalGate ──► your impl
              │  │     • AwaitAsync() before risky tools                 │
              │  │     • IApprovalStore.RecordAsync() ─────────────► ApprovalProps
              │  │                                                       │
              │  ├── IToolIdempotencyStore ──► RedbToolIdempotencyStore
              │  │     • TryReserveAsync() / CompleteAsync()    ──► ToolCacheProps
              │  │                                                       │
              │  └── IAgentObserver ──► RedbAuditObserver
              │       • OnToolInvokedAsync() per tool            ──► ToolAuditProps
              └────────┬───────────────────────────────────────────────┘
                       ▼
              .To("kafka://chat.out")
```

Everything inside the engine box is **not part of your route**. It's the
engine, and *it* talks to redb through the interfaces. Your responsibility is
the DSL and the headers. Persistence is a side effect of running the agent.

---

## The join key — `llm.conversation.id`

Persistence rests on **one header**. If the inbound exchange carries
`llm.conversation.id`, all five stores write rows attached to that key. No
header — persistence is silently skipped (no error), the agent runs stateless.

You set the header three ways:

```csharp
// (1) URI param: producer reads the header from the exchange
.To(Llm.Factory("claude").ConversationFromHeader().AsUri())  // → ?conversation=header

// (2) URI param: use RouteId as conversationId
.To(Llm.Factory("claude").ConversationFromRoute().AsUri())   // → ?conversation=property

// (3) Inline DSL — set the header yourself
.SetHeader(LlmHeaders.ConversationId, ex => ex.In.Headers["X-User-Id"]?.ToString())
.To(Llm.Factory("claude").ConversationFromHeader().AsUri())
```

Full set of headers that affect storage (`LlmHeaders.cs`):

| Constant                  | Key                   | Who writes                   | Where it lands                       |
| ------------------------- | --------------------- | ---------------------------- | ------------------------------------ |
| `ConversationId`          | `llm.conversation.id` | inbound to producer          | `IConversationStore`, `ICostBudgetStore`, `IApprovalStore`, `IToolIdempotencyStore`, `IAgentObserver` |
| `ApprovalId`              | `llm.approval.id`     | `IApprovalGate`              | `IApprovalStore` (`value_string`)    |
| `ToolUseId`               | `llm.tool.use_id`     | model in a tool_use block    | dedup key in `IToolIdempotencyStore`, FK in audit |
| `TokensIn` / `TokensOut`  | `llm.tokens.in/out`   | producer after a call        | feeds `ICostBudgetStore.AddAsync`    |
| `CostUsd`                 | `llm.cost.usd`        | producer (optional)          | feeds `ICostBudgetStore.AddAsync`    |

---

## Concrete recipes — *the why and the how*

### 1. A chatbot that remembers — multi-turn via `ConversationId`

**Problem.** The user types *"What about the second option?"* — without memory
all you have is that question. Context is gone.

**Solution.** One route on a Kafka topic, header `X-User-Id` →
`llm.conversation.id`, the engine does `LoadPathAsync(userId)` before the call
and `AppendAsync(userId, ..., assistantTurn)` after.

```csharp
public sealed class ChatRoutes : IRouteDefinition
{
    public void Configure(IRouteBuilder routes)
    {
        routes.From("kafka://chat.in")
              .SetHeader(LlmHeaders.ConversationId, ex => ex.In.Headers["X-User-Id"]?.ToString())
              .To(Llm.Factory("claude")
                     .ConversationFromHeader()        // → ?conversation=header
                     .MaxTokens(2048)
                     .AsUri())
              .To("kafka://chat.out");
    }
}
```

What happens automatically *after* `AddRedbLlmStorage()`:

- on entry the engine resolves `conversationId = ex.In.Headers["llm.conversation.id"]`;
- `LoadPathAsync(userId)` — a server-side ancestor walk via
  `redb.GetPathToRootAsync<MessageProps>` — pulls the entire previous
  root → leaf path. **Not a top-N slice** — it's a true path through
  `_objects.parent_id`;
- every turn (user → assistant → tool → ...) `AppendAsync` writes a new
  `MessageProps` via `CreateChildAsync`. Tree integrity comes from redb,
  not us;
- the assistant reply becomes `path.Last()` of the next call.

**Bonus.** `LoadTreeAsync(userId)` returns the whole conversation tree in one
shot — for an admin panel, analytics, or replay.

---

### 2. Inline `.Llm()` — conversation without a URI

Same thing without `Llm.Factory(...).AsUri()` — for cases where you don't want
a separate endpoint:

```csharp
routes.From("kafka://support.in")
      .Llm("claude", b => b
          .WithConversationFromHeader("X-Ticket-Id")    // ticket = conversationId
          .WithSystemPrompt("You are first-line support. Ask for clarification, never invent facts.")
          .WithMaxIterations(4))
      .To("kafka://support.out");
```

`WithConversationFromHeader("X-Ticket-Id")`
(`LlmRouteDefinitionExtensions.cs:163`) tells the builder: "read header
`X-Ticket-Id` and pass it to the engine as the conversation key". From there
it's the same as before — the ticket has its own tree, every message is a
`MessageProps` under that root.

---

### 3. Approval gates for dangerous tools — verdict into redb

**Problem.** The agent can call `delete_user`. You want a human in the loop
on Slack first — and you want a **permanent** record of who, when, why.

**Solution.** Implement `IApprovalGate` (sends to Slack), the engine itself
persists the `ApprovalDecision` through `IApprovalStore`
(`Engine/Governance/IApprovalGate.cs:13`).

```csharp
public sealed class SlackApprovalGate : IApprovalGate
{
    private readonly ISlackClient _slack;
    private readonly IApprovalStore _store;

    public async Task<ApprovalDecision> AwaitAsync(ApprovalRequest req, CancellationToken ct)
    {
        var msg = await _slack.AskAsync(
            $"Tool `{req.Tool.Name}` on conversation `{req.ConversationId}`:\n```{req.InputJson}```",
            ct);

        var decision = msg.Approved
            ? ApprovalDecision.Approve($"slack:{msg.MessageId}")
            : ApprovalDecision.Deny(msg.RejectReason ?? "denied in slack");

        await _store.RecordAsync(req, decision, ct);   // ← persisted in ApprovalProps
        return decision;
    }
}

services.Replace(ServiceDescriptor.Singleton<IApprovalGate, SlackApprovalGate>());
```

The route doesn't change. When the model calls a tool whose
`LlmToolSafety.RequiresApproval == true`, the engine awaits
`gate.AwaitAsync(...)`, persists the decision into `ApprovalProps`. Six months
later an auditor asks *"who approved deleting user 4242"*:

```csharp
var rec = await approvalStore.FindAsync("slack:C12345.1709");   // approvalId == _objects.value_string
//  → ApprovalRecord { ConversationId, ToolName="delete_user", Approved=true, ApprovedBy="@vasya", DecidedAtUtc=... }
```

One query. Indexed. No JSON parsing.

---

### 4. Hard budget per conversation — $0.50 and the agent shuts up

**Problem.** A loopy agent can run away — 50 tool-use iterations, $14 per
conversation.

**Solution.** `IBudgetEnforcer` (`Engine/Governance/IBudgetEnforcer.cs:8`)
checks the limit **before** every iteration and **after** every response.
`RedbCostBudgetStore` carries the running total across runs.

```csharp
public sealed class HardBudgetEnforcer : IBudgetEnforcer
{
    private readonly ICostBudgetStore _store;
    private static readonly AgentBudget Limit = new(0, 0, MaxCostUsd: 0.50m);

    public async ValueTask<BudgetDecision> PreCheckAsync(string? convId, AgentBudget _,
                                                          AgentUsage __, CancellationToken ct)
    {
        if (convId is null) return BudgetDecision.Allow;
        var u = await _store.GetUsageAsync(convId, ct);
        return u.CostUsd >= Limit.MaxCostUsd
            ? BudgetDecision.Stop($"conversation budget exhausted: ${u.CostUsd:F4}")
            : BudgetDecision.Allow;
    }

    public async ValueTask<BudgetDecision> RecordAndCheckAsync(string? convId, AgentBudget _,
                                                                AgentUsage delta, AgentUsage __,
                                                                CancellationToken ct)
    {
        if (convId is null) return BudgetDecision.Allow;
        var total = await _store.AddAsync(convId, delta, ct);   // ← upsert into CostBudgetProps
        return total.CostUsd >= Limit.MaxCostUsd
            ? BudgetDecision.Stop($"cost cap hit: ${total.CostUsd:F4}")
            : BudgetDecision.Allow;
    }
}

services.Replace(ServiceDescriptor.Singleton<IBudgetEnforcer, HardBudgetEnforcer>());
```

`AddAsync` returns the **post-update** total — one redb round-trip both bumps
the counter and tells you if you crossed the limit. On the next user request
`PreCheckAsync` sees the budget is gone and the engine refuses **before** any
provider HTTP call. **Cheap, because no provider charge is incurred.**

---

### 5. Idempotent tool retries — don't transfer money twice

**Problem.** The agent invoked `transfer_money`, the network blipped, the
retry policy retried the whole exchange. The tool runs twice → money
transferred twice.

**Solution.** `IToolIdempotencyStore` deduplicates by
`(conversationId, toolUseId)`. `toolUseId` is the stable id the **model**
itself returns in the tool_use block. Two retries — one tool_use_id — one
real call:

```csharp
// inside the tool bridge
var res = await idempotencyStore.TryReserveAsync(convId, toolUseId, ct);
if (!res.IsNew)
    return res.CachedOutputJson!;        // ← short-circuit on retry

var output = await ExecuteRealTransfer(input);
await idempotencyStore.CompleteAsync(convId, toolUseId, output, ct);
return output;
```

`RedbToolIdempotencyStore` keeps the reservation in `IIdempotentRepository`
(the same redb infra `redb.Route.Processors` uses) and the response body in
`ToolCacheProps`. Hit-rate, miss-rate and TTL evictions are emitted as
OpenTelemetry counters on the shared `redb.Route` meter
(`redb.route.llm.tool_cache.hits` / `.misses` / `.expired`, tagged with
`llm.tool.name`) so the engine never writes to the row on a read — add
`.AddMeter("redb.Route")` to your OTel pipeline and the dashboard is there.

---

### 6. Audit every tool call — what the agent tried and what blew up

**Problem.** A month from now compliance asks: *"For client A on Nov 12, what
tools did the agent call? How long did each take? Which failed?"* You need an
answer **in a minute**, not a week.

**Solution.** `RedbAuditObserver` writes one row into `ToolAuditProps` per
`OnToolInvokedAsync` callback. Outcome is auto-classified:

| `ctx.Skipped`  | `ctx.Exception` | `Outcome`  |
| -------------- | --------------- | ---------- |
| `true`, reason contains `"deni"` | — | `denied`   |
| `true`, otherwise | —            | `skipped`  |
| `false`        | non-null        | `error`    |
| `false`        | null            | `success`  |

The compliance query is one LINQ line on indexed columns:

```csharp
var failedToday = await redb.Query<ToolAuditProps>()
    .Where(p => p.ConversationId == clientConvId &&
                p.Outcome == "error" &&
                p.InvokedAtUtc >= DateTimeOffset.UtcNow.AddDays(-1))
    .ToListAsync();
```

One important detail — `RedbAuditObserver` **swallows** its own failures. If
the DB is briefly unavailable, the agent **does not crash**. Audit is a
best-effort layer; if it's load-bearing for compliance, stack a second
observer that logs to file/Kafka and ships externally.

---

### 7. Branching — rewrite a turn and compare

`RedbConversationStore` keeps messages as a **tree**, not a flat list. So:

```csharp
var trunk    = await store.AppendAsync(conv, parentId: null,   userTurn1, meta);
var v1       = await store.AppendAsync(conv, parentId: trunk,  assistantA, meta);
var v2       = await store.AppendAsync(conv, parentId: trunk,  assistantB, meta);   // sibling under same parent

var pathV1 = await store.LoadPathAsync(conv, leafId: v1);   // [user, assistantA]
var pathV2 = await store.LoadPathAsync(conv, leafId: v2);   // [user, assistantB]
```

This is **free** — not "yet another versions table", but native
`_objects.parent_id`. Any other redb node in your system gets the same. For a
prompt A/B-testing UI, the cost of this feature is exactly zero.

---

### 8. `From("llm://...")` — nightly agent with permanent memory

```csharp
routes.From(Llm.Factory("claude")
                .Schedule("0 3 * * *")              // every night at 3:00
                .ConversationFromRoute()             // RouteId becomes conversationId
                .InitialBody("ref:#daily-prompt")
                .AsUri())
      .To("kafka://summaries.out");
```

`ConversationFromRoute()` writes `?conversation=property` → producer uses
`exchange.RouteId` as the conversation key. Every run is a new message **under
the same conversation root**. A year later — a structured history of nightly
runs with trends across `TotalCostUsd`, `RunCount`, `LastActivityAtUtc`
(fields on `ConversationProps`):

```csharp
var history = await redb.Query<ConversationProps>()
    .WhereRedb(p => p.ValueString == "morning-summary-route")
    .FirstOrDefaultAsync();
//  history.Props.RunCount, .TotalCostUsd, .LastActivityAtUtc — straight to a dashboard
```

---

## Schemas — how redb actually stores it

Two rules pervade everything:

1. **Indexed scalars on `_objects`.** The fields you filter on
   (`ConversationId`, `ToolName`, `Outcome`, `Approved`) live as top-level
   `*Props` properties. redb materialises them into indexed columns on
   `_values` — `WhereRedb` hits the index, no JSON scan.
2. **Business key on `value_string` / `value_long`.** The visible identifier
   (conversation id, message id, approval id) lives in the indexed
   `_objects.value_string` column. The FK back to a tree root lives in
   `_objects.value_long`. No JSON marshalling for any of *our* fields.

| Schema                | `[RedbScheme]`                | Top-level (indexed)                                                                                               | Notes |
| ---                   | ---                           | ---                                                                                                                | --- |
| `ConversationProps`   | `LLM Conversation`            | TenantId, Title?, Status, StartedAtUtc, LastActivityAtUtc, TotalInputTokens, TotalOutputTokens, TotalCostUsd, RunCount | tree-root; `value_string` = conversation id |
| `MessageProps`        | `LLM Conversation Message`    | Role, Iteration, CreatedAtUtc, ProviderId?, ModelId?, StopReason?, ToolUseId?, InputTokens, OutputTokens, Content[] | tree-child; `value_long` = root id; `Content` is a typed array of `MessageContentBlock` |
| `ApprovalProps`       | `LLM Approval`                | ConversationId?, ToolName, ToolUseId, Approved, Reason?, ApprovedBy?, DecidedAtUtc, InputJson                      | one row per decision |
| `CostBudgetProps`     | `LLM Cost Budget`             | PeriodStartUtc?, InputTokens, OutputTokens, CostUsd, UpdatedAtUtc                                                  | one row per conversation; `value_string` = conv id |
| `ToolCacheProps`      | `LLM Tool Cache`              | ToolName?, OutputJson, CreatedAtUtc, ExpiresAtUtc?                                                                 | `value_string` = `{conv}:{toolUseId}`; hit/miss/expire counts live on the `redb.Route` OTel meter, not in the row |
| `ToolAuditProps`      | `LLM Tool Audit`              | ConversationId, MessageId?, ToolName, ToolUseId, InvokedAtUtc, DurationMs, Outcome, SkipReason?, InputJson, OutputJson?, ErrorMessage? | one row per invocation |
| `KnowledgeChunkProps` | `LLM Knowledge Chunk`         | ChunkId, Collection?, Text, Embedding[], Dimension, MetadataJson?, UpdatedAtUtc                                    | Phase 2 RAG; `Embedding` is a native `float[]` |
| `PromptTemplateProps` | `LLM Prompt Template`         | Name, Version, Body, Description?, CreatedAtUtc                                                                    | Phase 2; `(Name, Version)` is the business key |
| `EvalRunProps`        | `LLM Eval Run`                | RunId, Scenario, AgentFingerprint, Score?, InputTokens, OutputTokens, CostUsd, CreatedAtUtc, Iterations[]          | Phase 3; `Iterations` is a typed metric array |

`MessageProps.Content` is a typed array of `MessageContentBlock`
(`Kind` ∈ `text` / `tool_use` / `tool_result`) — no JSON serialisation for
**our own** fields. Foreign JSON (tool input, tool output) is stored as
`string` on `InputJson` / `OutputJson` only when the structure isn't ours.

---

## Stores and interfaces — reference

All interfaces live in `redb.Route.Llm.Engine.Storage`. Implementations live
in `redb.Route.Llm.Storage.Redb`.

```csharp
public interface IConversationStore
{
    Task<string> AppendAsync(string convId, string? parentMessageId,
                             LlmMessage message, ConversationMessageMeta meta, CancellationToken ct);
    Task<IReadOnlyList<ConversationMessage>> LoadPathAsync(string convId, string? leafId = null, CancellationToken ct);
    Task<IReadOnlyList<ConversationMessage>> LoadTreeAsync(string convId, CancellationToken ct);
}

public interface IApprovalStore
{
    Task RecordAsync(ApprovalRequest req, ApprovalDecision decision, CancellationToken ct);
    Task<ApprovalRecord?> FindAsync(string approvalId, CancellationToken ct);
}

public interface ICostBudgetStore
{
    ValueTask<AgentUsage> GetUsageAsync(string convId, CancellationToken ct);
    ValueTask<AgentUsage> AddAsync(string convId, AgentUsage delta, CancellationToken ct);
    ValueTask ResetAsync(string convId, CancellationToken ct);
}

public interface IToolIdempotencyStore
{
    Task<ToolIdempotencyReservation> TryReserveAsync(string convId, string toolUseId, CancellationToken ct);
    Task CompleteAsync(string convId, string toolUseId, string outputJson, CancellationToken ct);
    Task ReleaseAsync(string convId, string toolUseId, CancellationToken ct);
}

public interface IAgentObserver  // engine fires these on lifecycle events
{
    Task OnRunStartedAsync(AgentRunContext, CancellationToken);
    Task OnIterationCompletedAsync(AgentIterationContext, CancellationToken);
    Task OnToolInvokedAsync(AgentToolInvocationContext, CancellationToken);   // ← audit row written here
    Task OnRunCompletedAsync(AgentRunCompletedContext, CancellationToken);
}
```

`RedbConversationStore` uses **server-side** tree primitives end-to-end:

- `redb.CreateChildAsync(child, parent)` — tree integrity at the DB level.
- `redb.TreeQuery<MessageProps>(rootObj).ToFlatListAsync()` — every descendant
  in one query, no parent/children pointer-building.
- `redb.GetPathToRootAsync<MessageProps>(leaf)` — ancestor walk in
  **root → leaf** order.

Latest-leaf detection scopes a `TreeQuery` to the conversation root, applies
`.WhereLeaves()`, orders by `CreatedAtUtc DESC` and takes one. Cheap, because
a conversation tree is usually small (tens of messages).

---

## Operational gotchas

- **Cost-budget isn't atomic across processes.** Two app instances racing on
  `AddAsync` may lose an increment. If a cluster-wide hard cap is critical,
  back `ICostBudgetStore` with a SQL row using `SELECT ... FOR UPDATE`. The
  default store assumes single-instance / eventual consistency.
- **Audit is best-effort.** `RedbAuditObserver` swallows failures. If audit
  **must** land, log to a file/Kafka via a second observer and ship out.
- **`AddRedbIdempotentRepository()` is required.** `RedbToolIdempotencyStore`
  wraps `IIdempotentRepository`. Calling `AddRedbLlmStorage()` without it
  throws on the first tool call. Keep them next to each other in DI.
- **Cleanup — only roots and flat schemas.** `DeleteWithPurgeAsync` cascades
  soft-delete down the tree. Pass only `ConversationProps.id` —
  `MessageProps` descendants ride along automatically. **Never**
  `DELETE FROM _objects` — you'd wipe data from neighbouring tests or other
  services sharing the schema.
- **Cross-TFM parallel tests.** Storage integration tests run net8/9/10
  concurrently against the same DB — so each test uses a unique
  `convId = $"c-{Guid.NewGuid():N}"` and queries are scoped via
  `Where(p => p.ConversationId == ...)`. Don't write a bare `ToListAsync()`
  without a predicate — you'll see other TFMs' rows.
- **Postgres Pro free tier.** All 16 storage tests fit inside the 1024-query
  free-tier budget on a single run.
- **Streaming (`?stream=true`) currently bypasses the agent loop.** The
  streaming producer path (`LlmProducer.ProcessStreamingAsync`) calls
  `ILlmProvider.StreamAsync` directly and **does not** invoke `AgentEngine`
  — which means `RedbConversationStore`, `RedbApprovalStore`,
  `RedbCostBudgetStore`, `RedbToolIdempotencyStore` and `RedbAuditObserver`
  see nothing from a streamed turn. Tools (`?tools=`) are also intentionally
  not dispatched in stream mode. If you need persistence + streaming on the
  same route today, drive persistence with a parallel non-streaming `WireTap`
  to a deterministic `llm://` step, or split the user-visible response from
  the audit-visible one. Restoring full agent-loop semantics on top of the
  streaming wire is on the Phase-2 list.

---

## What's next — Phase 2 / 3

The schemas are already declared — the stores follow:

| Phase | Schema                  | Adds                                                    |
| ----- | ----------------------- | ------------------------------------------------------- |
| 2     | `KnowledgeChunkProps`   | `IKnowledgeStore` — RAG retrieval over the embedding column |
| 2     | `PromptTemplateProps`   | versioned `IPromptTemplateRegistry` (currently in-memory) |
| 2     | `ToolCacheProps` (+TTL) | a dedicated `IToolCacheStore` for deterministic tools   |
| 3     | `EvalRunProps`          | `IEvalRunStore` for leaderboard queries (scenario × fingerprint) |

All of them plug in via the **same** one-line `AddRedbLlmStorage()` — no
migrations, no DSL breakage.

---

## See also

- [README.md § Persistence — `AddRedbLlmStorage()`](../README.md#persistence--addredbllmstorage) — the short registration form
- [USER-GUIDE.md § Storage & Persistence](USER-GUIDE.md#storage--persistence) — where storage lands in the long guide
- `redb.Route/tests/redb.Route.Tests.Llm/Storage/` — 16 integration tests, one per store
- `Storage/Redb/Schemas/` — POCOs with `[RedbScheme]` attributes
- `Engine/Storage/` — interfaces and in-memory defaults
- `Engine/Governance/` — `IApprovalGate`, `IBudgetEnforcer`, the extension points
