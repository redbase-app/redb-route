using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm;
using redb.Route.Llm.Embeddings;
using redb.Route.Llm.Providers;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// <c>embed://&lt;factory&gt;</c> scheme — embeds the exchange body via the named
/// connection factory. Driven with a stub <see cref="EmbedComponent.ProviderFactory"/>
/// (no live endpoint): a single text → <c>float[]</c>, a collection → <c>float[][]</c>.
/// </summary>
public sealed class EmbedSchemeTests
{
    // Deterministic stub: vector = [text length, input index].
    private sealed class StubEmb : IEmbeddingProvider
    {
        public string ProviderId => "stub";
        public string ModelId => "stub-model";
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<float[]>>(
                inputs.Select((s, i) => new float[] { s.Length, i }).ToArray());
    }

    private static (RouteContext Ctx, ProducerTemplate Producer) Host(EmbedComponent component)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var ctx = new RouteContext(sp, contextId: "embed-scheme-test");
        ctx.AddToRegistry("emb", new LlmConnectionFactory
        {
            Provider = "openai", ModelId = "text-embedding-3-small", ApiKey = "sk-test"
        });
        ctx.AddComponent(component);
        var producer = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), producer);
        return (ctx, producer);
    }

    [Fact]
    public async Task SingleText_ProducesFloatVector()
    {
        var (ctx, producer) = Host(new EmbedComponent { ProviderFactory = _ => new StubEmb() });
        ctx.AddRoutes(r => r.From("direct:e").To("embed://emb"));
        await ctx.Start();
        producer.Start();

        var result = await producer.RequestBody("direct:e", "hello");   // 5 chars

        result.Should().BeOfType<float[]>();
        ((float[])result!).Should().Equal(5f, 0f);

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task ManyTexts_ProduceVectorArray_OrderPreserved()
    {
        var (ctx, producer) = Host(new EmbedComponent { ProviderFactory = _ => new StubEmb() });
        ctx.AddRoutes(r => r.From("direct:e2").To("embed://emb"));
        await ctx.Start();
        producer.Start();

        var result = await producer.RequestBody("direct:e2", new List<string> { "ab", "cde" });

        result.Should().BeOfType<float[][]>();
        var vecs = (float[][])result!;
        vecs.Should().HaveCount(2);
        vecs[0].Should().Equal(2f, 0f);   // "ab"  → len 2, index 0
        vecs[1].Should().Equal(3f, 1f);   // "cde" → len 3, index 1

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task PerRoute_ResolvesNamedFactory()
    {
        LlmConnectionFactory? seen = null;
        var (ctx, producer) = Host(new EmbedComponent { ProviderFactory = f => { seen = f; return new StubEmb(); } });
        ctx.AddRoutes(r => r.From("direct:e3").To("embed://emb"));
        await ctx.Start();
        producer.Start();

        await producer.RequestBody("direct:e3", "x");

        seen.Should().NotBeNull();
        seen!.ModelId.Should().Be("text-embedding-3-small");   // the factory named by embed://emb

        await ctx.DisposeAsync();
    }
}
