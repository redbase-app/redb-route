using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Knowledge;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// <c>knowledge://</c> ingest producer — <c>To("knowledge://coll")</c> chunks the
/// document body and upserts into the resolved <see cref="IKnowledgeStore"/>.
/// Driven through a bare <see cref="RouteContext"/> + <see cref="ProducerTemplate"/>,
/// no LLM provider.
/// </summary>
public sealed class KnowledgeIngestTests
{
    private static (RouteContext Ctx, ProducerTemplate Producer, IKnowledgeStore Store) BuildHost()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var ctx = new RouteContext(sp, contextId: "knowledge-ingest-test");
        IKnowledgeStore store = new InMemoryKnowledgeStore();
        ctx.AddService(typeof(IKnowledgeStore), store);
        ctx.AddComponent(new KnowledgeComponent());
        var producer = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), producer);
        return (ctx, producer, store);
    }

    [Fact]
    public async Task Ingest_ChunksDocument_UpsertsAndIsSearchable()
    {
        var (ctx, producer, store) = BuildHost();
        ctx.AddRoutes(r => r.From("direct:ingest").To("knowledge://kb?chunkChars=40&overlap=0"));
        await ctx.Start();
        producer.Start();

        const string text =
            "The mask breaks the name and speech. The idol breeds the fear of death. The rift loses meaning.";
        var reply = (string)(await producer.RequestBody("direct:ingest", text))!;

        var summary = JsonDocument.Parse(reply).RootElement;
        summary.GetProperty("collection").GetString().Should().Be("kb");
        summary.GetProperty("chunks").GetInt32().Should().BeGreaterThan(1);   // ~95 chars / 40 → 3 chunks

        var hits = await store.SearchTextAsync("idol", 10, "kb");
        hits.Should().NotBeEmpty();
        hits[0].Chunk.Collection.Should().Be("kb");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Reingest_SameDocId_ReplacesChunksInPlace()
    {
        var (ctx, producer, store) = BuildHost();
        // one chunk per doc (large chunkChars), stable docId → id "d1#0"
        ctx.AddRoutes(r => r.From("direct:ingest2").To("knowledge://kb?chunkChars=1000&docId=d1"));
        await ctx.Start();
        producer.Start();

        await producer.RequestBody("direct:ingest2", "first version about apples");
        (await store.SearchTextAsync("apples", 10, "kb")).Should().ContainSingle();

        await producer.RequestBody("direct:ingest2", "second version about bananas");
        (await store.SearchTextAsync("apples", 10, "kb")).Should().BeEmpty();          // replaced
        (await store.SearchTextAsync("bananas", 10, "kb")).Should().ContainSingle();

        await ctx.DisposeAsync();
    }

    [Fact]
    public void Chunker_WindowsWithOverlap()
    {
        KnowledgeChunker.Split("abcdefghij", chunkChars: 4, overlap: 0)
            .Should().Equal("abcd", "efgh", "ij");

        KnowledgeChunker.Split("abcdefghij", chunkChars: 4, overlap: 2)
            .Should().Equal("abcd", "cdef", "efgh", "ghij");

        KnowledgeChunker.Split("short", chunkChars: 100, overlap: 0)
            .Should().Equal("short");

        KnowledgeChunker.Split("   ", chunkChars: 10, overlap: 0)
            .Should().BeEmpty();
    }
}
