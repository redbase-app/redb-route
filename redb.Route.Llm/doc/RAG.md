# redb.Route.Llm — RAG (knowledge base)

> Retrieval-augmented generation as **routes and DSL steps**, not a separate vector
> service. Documents are ingested through a scheme, embedded through a provider,
> searched by keyword **or** vector, and fed to the model either as a **tool** the
> agent calls or as **context injected** into the system prompt. Everything is a
> redb.Route primitive.

Added incrementally in **3.3.0** — see [CHANGELOG](../../CHANGELOG.md).

---

## The loop

```text
  From("file://docs")──►To("knowledge://kb")          ← ingest: chunk (+embed) → upsert
                              │
                              ▼
                     IKnowledgeStore (RedbKnowledgeStore)
                     KnowledgeChunkProps: text=note, embedding=value_bytes
                              │
              ┌───────────────┴────────────────┐
              ▼                                 ▼
   SearchTextAsync (keyword LIKE)     SearchAsync (cosine, needs embeddings)
              │                                 │
              └───────────────┬─────────────────┘
                              ▼
        ┌─────────────────────┴──────────────────────┐
        ▼                                             ▼
 knowledge_search tool                    .Knowledge("kb", k) DSL
 (agent calls it in the loop)             (injects top-K into the system prompt)
              │                                             │
              └───────────────────┬─────────────────────────┘
                                  ▼
                          .To("llm://claude")
```

## Pieces (all in `redb.Route.Llm`)

| Piece | Type | What |
|---|---|---|
| `knowledge://<coll>` | scheme (producer) | `To(...)` — chunk the exchange body, optionally embed, upsert |
| `embed://<factory>` | scheme (producer) | `To(...)` — embed the body as a route step (text → `float[]`, texts → `float[][]`) |
| `IEmbeddingProvider` / `OpenAiEmbeddingProvider` | provider | text → vectors (OpenAI-compatible `/embeddings`) |
| `IKnowledgeStore.SearchTextAsync` | store method | keyword substring, server-side `ILIKE` on the indexed `note` |
| `IKnowledgeStore.SearchAsync` | store method | cosine over embeddings |
| `KnowledgeSearchTool` (`knowledge_search`) | `.AsLlmTool` | the agent searches the base itself |
| `.Knowledge("coll", k)` | DSL step | retrieve + inject top-K into the system prompt |

Storage is the property-less `KnowledgeChunkProps` (`value_string`=chunk id,
`name`=collection, `note`=`{text,meta}` JSON, `value_bytes`=embedding). Enabled by
`AddRedbLlmStorage()` → `RedbKnowledgeStore` (or `InMemoryKnowledgeStore` for tests).

---

## Keyword vs semantic

- **Keyword** (`SearchTextAsync`, `knowledge_search` without an embedder, `.Knowledge()`
  without one): server-side `LIKE` over the chunk text. No embedding infra, cheap,
  exact — best for structured / numbered / code / ID content, and for a query that
  *is* a term.
- **Semantic** (with an `IEmbeddingProvider`): embed the query, cosine-rank. Matches
  meaning / paraphrase — best for prose corpora and natural-language questions.
- Hybrid is a matter of running both and merging; each surface degrades gracefully.

Non-ASCII (Cyrillic / CJK) is stored as UTF-8 in `note` (relaxed JSON escaping), so
keyword `LIKE` matches a raw non-ASCII query — not `\uXXXX`.

---

## End to end

```csharp
// DI
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteLlm();
    route.Services.AddRedbLlmStorage();                 // IKnowledgeStore → redb
    route.Services.AddLlmConnectionFactory("claude", f => { f.Provider="anthropic"; f.ModelId="claude-haiku-4-5"; f.ApiKeySecretRef="anthropic.api-key"; });
});
// optional: register an embedding provider for semantic search
// services.AddSingleton<IEmbeddingProvider>(OpenAiEmbeddingProvider.Create(
//     new LlmConnectionFactory { Provider="openai", ModelId="text-embedding-3-small", ApiKey=key }));

// component for the ingest scheme
context.AddComponent(new KnowledgeComponent());

// 1) ingest
From("file://handbook?include=*.md")
    .To("knowledge://handbook?chunkChars=1000&overlap=100");

// 2a) let the agent search (tool)
context.AddRoutes(new KnowledgeSearchTool(new KnowledgeSearchOptions { Collection = "handbook" }));
From("kafka://questions")
    .To(Llm.Factory("claude").Tools("knowledge_search").AsUri())
    .To("kafka://answers");

// 2b) or inject context automatically (DSL)
From("kafka://questions")
    .Knowledge("handbook", k: 5)          // retrieve + inject into the system prompt
    .To("llm://claude")
    .To("kafka://answers");
```

**IP-scoping:** `KnowledgeSearchOptions.Collection` (and the `.Knowledge` collection)
**pin** the agent to one collection — the model's `collection` argument is ignored.
Chunk text goes into the model's reasoning context (tool result / system prompt), not
to the end user; pair with a "ground on it, don't quote verbatim" instruction to keep
source material private.

---

## Not shipped yet

- `vector://` sink scheme (route a vector to an external store as a step) — `embed://`
  produces vectors; there is no companion sink scheme yet.
- ANN index — `RedbKnowledgeStore.SearchAsync` is an O(N) in-process cosine scan
  (fine for thousands of chunks). For millions, wrap `IKnowledgeStore` with a
  pgvector / Qdrant implementation that overrides `SearchAsync`.
- Rich document loaders (PDF/DOCX parsing) — bring your own text; chunking is a
  deterministic character window.
