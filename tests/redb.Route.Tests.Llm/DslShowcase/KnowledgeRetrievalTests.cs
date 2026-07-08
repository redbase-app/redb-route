using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Knowledge;
using redb.Route.Llm.Providers;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// <c>.Knowledge(collection, k)</c> — retrieves top-K chunks for the current message
/// and injects them into the system prompt before a downstream <c>.To("llm://")</c>.
/// </summary>
public sealed class KnowledgeRetrievalTests
{
    private sealed class StubEmb(Dictionary<string, float[]> map) : IEmbeddingProvider
    {
        public string ProviderId => "stub";
        public string ModelId => "stub";
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<float[]>>(
                inputs.Select(i => map.TryGetValue(i, out var v) ? v : new float[] { 0f, 0f }).ToArray());
    }

    private static (RouteContext Ctx, ProducerTemplate Producer) Host()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var ctx = new RouteContext(sp, contextId: "knowledge-retrieval-test");
        var producer = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), producer);
        return (ctx, producer);
    }

    private static async Task<IKnowledgeStore> Seed(params (string Id, string Text, float[]? Emb)[] chunks)
    {
        IKnowledgeStore store = new InMemoryKnowledgeStore();
        await store.UpsertManyAsync(chunks.Select(c => new KnowledgeChunk
        {
            Id = c.Id, Text = c.Text, Collection = "kb", Embedding = c.Emb ?? Array.Empty<float>()
        }));
        return store;
    }

    [Fact]
    public async Task Keyword_InjectsRetrievedChunks_IntoSystemPrompt()
    {
        var store = await Seed(("1", "apples grow on trees", null), ("2", "bananas are yellow", null));
        var (ctx, producer) = Host();
        ctx.AddRoutes(r => r.From("direct:q")
            .Knowledge(new KnowledgeRetrievalOptions { Collection = "kb", TopK = 3, Store = store })
            .Process(e => e.In.Body = e.In.Headers.TryGetValue(LlmHeaders.SystemPrompt, out var sp) ? sp : "(none)"));
        await ctx.Start();
        producer.Start();

        var result = (string)(await producer.RequestBody("direct:q", "apples"))!;
        result.Should().Contain("apples grow on trees");
        result.Should().NotContain("bananas");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Semantic_UsesEmbeddings_NotSubstring()
    {
        var store = await Seed(("cats", "about cats", new[] { 1f, 0f }), ("dogs", "about dogs", new[] { 0f, 1f }));
        var emb = new StubEmb(new() { ["feline pets"] = new[] { 0.9f, 0.1f } });
        var (ctx, producer) = Host();
        ctx.AddRoutes(r => r.From("direct:q2")
            .Knowledge(new KnowledgeRetrievalOptions { Collection = "kb", TopK = 1, Store = store, EmbeddingProvider = emb })
            .Process(e => e.In.Body = e.In.Headers.TryGetValue(LlmHeaders.SystemPrompt, out var sp) ? sp : "(none)"));
        await ctx.Start();
        producer.Start();

        var result = (string)(await producer.RequestBody("direct:q2", "feline pets"))!;
        result.Should().Contain("about cats");   // semantic match, query shares no keyword

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task AugmentsExistingSystemPrompt()
    {
        var store = await Seed(("1", "apples grow on trees", null));
        var (ctx, producer) = Host();
        ctx.AddRoutes(r => r.From("direct:q3")
            .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] = "You are helpful.")
            .Knowledge(new KnowledgeRetrievalOptions { Collection = "kb", Store = store })
            .Process(e => e.In.Body = e.In.Headers[LlmHeaders.SystemPrompt]));
        await ctx.Start();
        producer.Start();

        var result = (string)(await producer.RequestBody("direct:q3", "apples"))!;
        result.Should().StartWith("You are helpful.");
        result.Should().Contain("apples grow on trees");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task NoStore_IsNoOp()
    {
        var (ctx, producer) = Host();
        ctx.AddRoutes(r => r.From("direct:q4")
            .Knowledge(new KnowledgeRetrievalOptions { Collection = "kb" })   // no store, no DI
            .Process(e => e.In.Body = e.In.Headers.ContainsKey(LlmHeaders.SystemPrompt) ? "injected" : "clean"));
        await ctx.Start();
        producer.Start();

        ((string)(await producer.RequestBody("direct:q4", "apples"))!).Should().Be("clean");

        await ctx.DisposeAsync();
    }
}
