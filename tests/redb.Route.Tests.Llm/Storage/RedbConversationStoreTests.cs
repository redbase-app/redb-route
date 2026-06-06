using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Storage.Redb;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Integration tests for <see cref="RedbConversationStore"/> backed by a live
/// Postgres Pro database. Validates Append + LoadPath + LoadTree round-trip,
/// branching (parent/child), and TreeQuery-based latest-leaf resolution.
/// </summary>
[Collection("PostgresPro")]
public sealed class RedbConversationStoreTests
{
    private readonly PostgresProFixture _fx;

    public RedbConversationStoreTests(PostgresProFixture fx) => _fx = fx;

    private static ConversationMessageMeta Meta(int iter, DateTime? at = null) => new()
    {
        CreatedAtUtc = at ?? DateTime.UtcNow,
        Iteration = iter,
        Usage = new LlmUsage(10, 5)
    };

    [Fact]
    public async Task Append_LoadPath_LoadTree_LinearConversation()
    {
        var store = new RedbConversationStore(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";

        var t0 = DateTime.UtcNow;
        var m1 = await store.AppendAsync(convId, null, LlmMessage.User("hi"), Meta(0, t0));
        var m2 = await store.AppendAsync(convId, m1, LlmMessage.Assistant("hello!"), Meta(1, t0.AddSeconds(1)));
        var m3 = await store.AppendAsync(convId, m2, LlmMessage.User("how are you?"), Meta(2, t0.AddSeconds(2)));

        var tree = await store.LoadTreeAsync(convId);
        tree.Should().HaveCount(3);
        tree.Select(x => x.Message.Role).Should().BeEquivalentTo(["user", "assistant", "user"]);

        var path = await store.LoadPathAsync(convId);
        path.Should().HaveCount(3);
        path.Last().Id.Should().Be(m3);
        path.First().Id.Should().Be(m1);
    }

    [Fact]
    public async Task LoadPath_Branching_ResolvesCorrectLeaf()
    {
        var store = new RedbConversationStore(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";

        var t0 = DateTime.UtcNow;
        var root = await store.AppendAsync(convId, null, LlmMessage.User("Q"), Meta(0, t0));
        var a = await store.AppendAsync(convId, root, LlmMessage.Assistant("A1"), Meta(1, t0.AddSeconds(1)));
        var b = await store.AppendAsync(convId, root, LlmMessage.Assistant("A2"), Meta(1, t0.AddSeconds(2)));

        var pathA = await store.LoadPathAsync(convId, a);
        pathA.Select(x => x.Id).Should().BeEquivalentTo([root, a], opts => opts.WithStrictOrdering());

        var pathB = await store.LoadPathAsync(convId, b);
        pathB.Select(x => x.Id).Should().BeEquivalentTo([root, b], opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task LoadPath_NullLeaf_PicksLatestLeafByCreatedAt()
    {
        var store = new RedbConversationStore(_fx.ScopeFactory);
        var convId = $"c-{Guid.NewGuid():N}";

        var t0 = DateTime.UtcNow;
        var root = await store.AppendAsync(convId, null, LlmMessage.User("Q"), Meta(0, t0));
        var older = await store.AppendAsync(convId, root, LlmMessage.Assistant("old"), Meta(1, t0.AddSeconds(1)));
        var newer = await store.AppendAsync(convId, root, LlmMessage.Assistant("new"), Meta(1, t0.AddSeconds(5)));

        var path = await store.LoadPathAsync(convId);
        path.Last().Id.Should().Be(newer);
    }
}
