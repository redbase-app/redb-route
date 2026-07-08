using System.Text.Json;

using FluentAssertions;

using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Tools;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Drives the <c>knowledge_search</c> tool (<see cref="KnowledgeSearchTool"/>) straight
/// through the route pipeline — a <c>direct:</c> route mounted via <c>.AsLlmTool(...)</c>
/// over <see cref="IKnowledgeStore.SearchTextAsync"/>. No LLM provider involved.
/// </summary>
public sealed class KnowledgeSearchToolTests
{
    private static async Task<IKnowledgeStore> SeededStore(params (string Id, string Text, string? Collection)[] chunks)
    {
        IKnowledgeStore store = new InMemoryKnowledgeStore();
        await store.UpsertManyAsync(chunks.Select(c => new KnowledgeChunk
        {
            Id = c.Id,
            Text = c.Text,
            Collection = c.Collection,
            Embedding = new float[] { 0f }
        }));
        return store;
    }

    [Fact]
    public async Task RegistersDescriptor_AndReturnsRankedHits()
    {
        var store = await SeededStore(
            ("1", "Apples grow on trees.", "fruit"),
            ("2", "apple, apple, apple pie.", "fruit"),
            ("3", "The office is downtown.", "misc"));

        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new KnowledgeSearchTool(new KnowledgeSearchOptions { Store = store }));

        var descriptor = host.ToolRegistry.Get("knowledge_search");
        descriptor.Should().NotBeNull();
        descriptor!.Capability.Safety.SideEffect.Should().Be(ToolSideEffect.ReadOnly);

        var ex = await host.SendAsync("direct:llm.knowledge_search", """{"query":"apple"}""");

        var results = JsonDocument.Parse((string)ex.Out!.Body!).RootElement.GetProperty("results");
        results.GetArrayLength().Should().Be(2);                 // #1 and #2 contain "apple"
        results[0].GetProperty("id").GetString().Should().Be("2");   // 3 occurrences rank first
        ex.Out.Headers["llm.knowledge_search.hits"].Should().Be(2);
    }

    [Fact]
    public async Task PinnedCollection_IgnoresModelSuppliedCollection()
    {
        var store = await SeededStore(
            ("a", "a shared term", "alpha"),
            ("b", "a shared term", "beta"));

        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new KnowledgeSearchTool(
            new KnowledgeSearchOptions { Store = store, Collection = "beta" }));

        // The model asks for alpha, but the tool is pinned to beta — pin wins.
        var ex = await host.SendAsync("direct:llm.knowledge_search",
            """{"query":"shared","collection":"alpha"}""");

        var results = JsonDocument.Parse((string)ex.Out!.Body!).RootElement.GetProperty("results");
        results.GetArrayLength().Should().Be(1);
        results[0].GetProperty("id").GetString().Should().Be("b");
    }

    private sealed class StubEmbeddingProvider(Dictionary<string, float[]> map) : IEmbeddingProvider
    {
        public string ProviderId => "stub";
        public string ModelId => "stub";
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<float[]>>(
                inputs.Select(i => map.TryGetValue(i, out var v) ? v : new float[] { 0f, 0f }).ToArray());
    }

    [Fact]
    public async Task SemanticMode_RanksByCosine_NotKeyword()
    {
        IKnowledgeStore store = new InMemoryKnowledgeStore();
        await store.UpsertManyAsync(new[]
        {
            new KnowledgeChunk { Id = "cats", Text = "about cats", Collection = "kb", Embedding = new float[] { 1f, 0f } },
            new KnowledgeChunk { Id = "dogs", Text = "about dogs", Collection = "kb", Embedding = new float[] { 0f, 1f } },
        });

        // The query shares NO keyword with the chunk texts, but embeds near "cats".
        var emb = new StubEmbeddingProvider(new() { ["feline pets"] = new[] { 0.9f, 0.1f } });

        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new KnowledgeSearchTool(new KnowledgeSearchOptions
        {
            Store = store, Collection = "kb", EmbeddingProvider = emb
        }));

        var ex = await host.SendAsync("direct:llm.knowledge_search", """{"query":"feline pets","top_k":1}""");

        var results = JsonDocument.Parse((string)ex.Out!.Body!).RootElement.GetProperty("results");
        results.GetArrayLength().Should().Be(1);
        results[0].GetProperty("id").GetString().Should().Be("cats");   // semantic, not substring
    }

    [Fact]
    public async Task NoMatch_ReturnsEmptyResults()
    {
        var store = await SeededStore(("1", "apple and banana", null));

        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new KnowledgeSearchTool(new KnowledgeSearchOptions { Store = store }));

        var ex = await host.SendAsync("direct:llm.knowledge_search", """{"query":"zebra"}""");

        JsonDocument.Parse((string)ex.Out!.Body!).RootElement.GetProperty("results")
            .GetArrayLength().Should().Be(0);
        ex.Out.Headers["llm.knowledge_search.hits"].Should().Be(0);
    }
}
