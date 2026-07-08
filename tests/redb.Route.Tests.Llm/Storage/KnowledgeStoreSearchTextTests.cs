using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// <see cref="IKnowledgeStore.SearchTextAsync"/> — keyword (substring) retrieval.
/// The in-memory suite runs everywhere (no DB); the redb suite proves the
/// server-side case-insensitive <c>LIKE</c> push-down against Postgres Pro.
/// </summary>
public sealed class InMemoryKnowledgeStoreSearchTextTests
{
    private static KnowledgeChunk Chunk(string id, string text, string? collection = null, string? meta = null)
        => new() { Id = id, Text = text, Collection = collection, MetadataJson = meta, Embedding = new float[] { 0f } };

    private static async Task<IKnowledgeStore> SeedAsync(params KnowledgeChunk[] chunks)
    {
        IKnowledgeStore store = new InMemoryKnowledgeStore();   // UpsertManyAsync is a default interface member
        await store.UpsertManyAsync(chunks);
        return store;
    }

    [Fact]
    public async Task FindsSubstring_CaseInsensitive()
    {
        var store = await SeedAsync(
            Chunk("1", "Apples grow on trees."),
            Chunk("2", "Bananas are yellow."),
            Chunk("3", "Cherries are red."));

        var hits = await store.SearchTextAsync("APPLE", topK: 5);

        hits.Should().ContainSingle();
        hits[0].Chunk.Id.Should().Be("1");
    }

    [Fact]
    public async Task RanksByOccurrenceCount()
    {
        var store = await SeedAsync(
            Chunk("once", "The fox ran away."),
            Chunk("many", "fox, fox and another fox."));

        var hits = await store.SearchTextAsync("fox", topK: 5);

        hits.Select(h => h.Chunk.Id).Should().Equal("many", "once");
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public async Task CollectionFilter_IsolatesResults()
    {
        var store = await SeedAsync(
            Chunk("a", "a shared keyword here", collection: "alpha"),
            Chunk("b", "a shared keyword here", collection: "beta"));

        var hits = await store.SearchTextAsync("keyword", topK: 5, collection: "beta");

        hits.Should().ContainSingle();
        hits[0].Chunk.Id.Should().Be("b");
    }

    [Fact]
    public async Task EmptyQuery_ReturnsEmpty()
    {
        var store = await SeedAsync(Chunk("1", "any text"));
        (await store.SearchTextAsync("   ", topK: 5)).Should().BeEmpty();
    }

    [Fact]
    public async Task NoMatch_ReturnsEmpty()
    {
        var store = await SeedAsync(Chunk("1", "apple and banana"));
        (await store.SearchTextAsync("zebra", topK: 5)).Should().BeEmpty();
    }
}

/// <summary>Integration: <see cref="RedbKnowledgeStore.SearchTextAsync"/> against Postgres Pro.</summary>
[Collection("PostgresPro")]
public sealed class RedbKnowledgeStoreSearchTextTests
{
    private readonly PostgresProFixture _fx;
    public RedbKnowledgeStoreSearchTextTests(PostgresProFixture fx) => _fx = fx;

    private static KnowledgeChunk Chunk(string id, string text, string? collection = null, string? meta = null)
        => new() { Id = id, Text = text, Collection = collection, MetadataJson = meta, Embedding = new float[] { 0.1f, 0.2f } };

    // Seed via single UpsertAsync, not UpsertManyAsync: the bulk path has a
    // pre-existing redb-parser limitation (string[].Contains binds to the
    // MemoryExtensions span overload, which WhereRedb does not translate).
    private static async Task SeedAsync(IKnowledgeStore store, params KnowledgeChunk[] chunks)
    {
        foreach (var c in chunks) await store.UpsertAsync(c);
    }

    [Fact]
    public async Task ServerSideLike_FindsAndRanks_CaseInsensitive()
    {
        var store = new RedbKnowledgeStore(_fx.RouteContext);
        var coll = $"k-{Guid.NewGuid():N}";

        await SeedAsync(store,
            Chunk($"{coll}:1", "Apple pie is tasty.", coll),
            Chunk($"{coll}:2", "apple, Apple, and APPLE again.", coll),
            Chunk($"{coll}:3", "Only bananas here.", coll));

        var hits = await store.SearchTextAsync("apple", topK: 5, collection: coll);

        hits.Should().HaveCount(2);                       // ILIKE matches Apple / APPLE too
        hits[0].Chunk.Id.Should().Be($"{coll}:2");        // 3 occurrences rank first
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public async Task NonAscii_StoredUtf8_IsSearchable()
    {
        // Proves the note envelope is written as UTF-8 (relaxed JSON escaping),
        // not \uXXXX — otherwise a non-ASCII query never matches the LIKE.
        var store = new RedbKnowledgeStore(_fx.RouteContext);
        var coll = $"k-{Guid.NewGuid():N}";

        await SeedAsync(store,
            Chunk($"{coll}:1", "A cosy café in Zürich.", coll),
            Chunk($"{coll}:2", "A plain office downtown.", coll));

        var hits = await store.SearchTextAsync("café", topK: 5, collection: coll);

        hits.Should().ContainSingle();
        hits[0].Chunk.Id.Should().Be($"{coll}:1");
    }

    [Fact]
    public async Task EnvelopeOnlyMatch_IsDropped()
    {
        // The query hits the metadata (part of the {text,meta} envelope the LIKE
        // sees) but NOT the decoded text → the in-process re-score must drop it.
        var store = new RedbKnowledgeStore(_fx.RouteContext);
        var coll = $"k-{Guid.NewGuid():N}";

        await SeedAsync(store,
            Chunk($"{coll}:text", "The keyword lives in the text.", coll),
            Chunk($"{coll}:meta", "Nothing relevant in this body.", coll, meta: "{\"tag\":\"keyword\"}"));

        var hits = await store.SearchTextAsync("keyword", topK: 5, collection: coll);

        hits.Should().ContainSingle();
        hits[0].Chunk.Id.Should().Be($"{coll}:text");
    }

    [Fact]
    public async Task NoMatch_ReturnsEmpty()
    {
        var store = new RedbKnowledgeStore(_fx.RouteContext);
        var coll = $"k-{Guid.NewGuid():N}";
        await SeedAsync(store, Chunk($"{coll}:1", "apple and banana", coll));

        (await store.SearchTextAsync("zebra", topK: 5, collection: coll)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertMany_BulkPath_InsertsAndUpdates()
    {
        // Exercises the whole bulk path that used to be broken end-to-end:
        //  1) existing-key lookup keys.Contains(o.ValueString) → IN clause
        //     (redb-parser fix for the MemoryExtensions.Contains span overload),
        //  2) ComputeHash() on a loaded property-less row (Props=null) → no NRE
        //     (RedbHash null guard),
        //  3) no false "unchanged" skip → the changed text is actually persisted.
        var store = new RedbKnowledgeStore(_fx.RouteContext);
        var coll = $"k-{Guid.NewGuid():N}";

        await store.UpsertManyAsync(new[]
        {
            Chunk($"{coll}:a", "alpha shared text", coll),
            Chunk($"{coll}:b", "beta shared text", coll),
        });

        // Re-upsert: :a changes text (existing key), :c is new.
        await store.UpsertManyAsync(new[]
        {
            Chunk($"{coll}:a", "alpha shared UPDATED text", coll),
            Chunk($"{coll}:c", "gamma shared text", coll),
        });

        (await store.SearchTextAsync("shared", topK: 10, collection: coll)).Should().HaveCount(3);

        var updated = await store.SearchTextAsync("UPDATED", topK: 5, collection: coll);
        updated.Should().ContainSingle();
        updated[0].Chunk.Id.Should().Be($"{coll}:a");   // the update was persisted, not skipped
    }
}
