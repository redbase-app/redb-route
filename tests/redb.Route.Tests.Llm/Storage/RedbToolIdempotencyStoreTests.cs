using redb.Route.Llm.Storage.Redb;
using redb.Route.Processors;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Integration tests for <see cref="RedbToolIdempotencyStore"/>. Pairs the
/// store with the in-memory <see cref="InMemoryIdempotentRepository"/> for
/// dedup; the cached output JSON is the part actually persisted in redb.
/// </summary>
[Collection("PostgresPro")]
public sealed class RedbToolIdempotencyStoreTests
{
    private readonly PostgresProFixture _fx;

    public RedbToolIdempotencyStoreTests(PostgresProFixture fx) => _fx = fx;

    private RedbToolIdempotencyStore NewStore() =>
        new(new InMemoryIdempotentRepository(), _fx.ScopeFactory);

    [Fact]
    public async Task Reserve_FirstCall_IsNew()
    {
        var store = NewStore();
        var convId = $"c-{Guid.NewGuid():N}";

        var res = await store.TryReserveAsync(convId, "tu-1");
        res.IsNew.Should().BeTrue();
    }

    [Fact]
    public async Task Reserve_AfterComplete_ReturnsCachedOutput()
    {
        var store = NewStore();
        var convId = $"c-{Guid.NewGuid():N}";

        var first = await store.TryReserveAsync(convId, "tu-2");
        first.IsNew.Should().BeTrue();

        await store.CompleteAsync(convId, "tu-2", """{"ok":true}""");

        var second = await store.TryReserveAsync(convId, "tu-2");
        second.IsNew.Should().BeFalse();
        second.CachedOutputJson.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public async Task Release_AllowsRetry()
    {
        var store = NewStore();
        var convId = $"c-{Guid.NewGuid():N}";

        (await store.TryReserveAsync(convId, "tu-3")).IsNew.Should().BeTrue();
        await store.ReleaseAsync(convId, "tu-3");

        (await store.TryReserveAsync(convId, "tu-3")).IsNew.Should().BeTrue();
    }
}
