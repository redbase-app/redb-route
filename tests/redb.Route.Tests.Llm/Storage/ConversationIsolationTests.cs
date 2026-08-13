using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Cross-conversation isolation for <see cref="RedbConversationStore"/>, against a
/// live Postgres Pro database.
/// <para>
/// Every test here carries a <b>negative control</b>: the conversation that must
/// NOT leak is written <i>last</i>, so it is the freshest row in the schema. That
/// is precisely the shape the original suite lacked — its transcripts were always
/// the newest rows in the database, so a "freshest leaf anywhere" query returned
/// the right answer by accident and the tests stayed green while conversations
/// leaked into each other.
/// </para>
/// </summary>
[Collection("PostgresPro")]
public sealed class ConversationIsolationTests
{
    private readonly PostgresProFixture _fx;

    public ConversationIsolationTests(PostgresProFixture fx) => _fx = fx;

    private static ConversationMessageMeta Meta(int iter, DateTime? at = null) => new()
    {
        CreatedAtUtc = at ?? DateTime.UtcNow,
        Iteration = iter,
        Usage = new LlmUsage(10, 5)
    };

    private static string NewConversationId() => $"c-{Guid.NewGuid():N}";

    private async Task<long> RootIdOfAsync(string conversationId)
    {
        var root = await _fx.Redb.Query<ConversationProps>()
            .WhereRedb(x => x.ValueString == conversationId)
            .FirstOrDefaultAsync();
        root.Should().NotBeNull($"conversation '{conversationId}' should have a root by now");
        return root!.id;
    }

    [Fact]
    public async Task LoadPath_EmptyConversation_DoesNotSeeAnotherTranscript()
    {
        var store = new RedbConversationStore(_fx.RouteContext);
        var quiet = NewConversationId();
        var busy = NewConversationId();

        // The quiet conversation exists but never gets a message.
        (await store.LoadPathAsync(quiet)).Should().BeEmpty();

        // Negative control: the other conversation is written afterwards, so its
        // rows are the newest in the whole schema.
        var t0 = DateTime.UtcNow;
        var m1 = await store.AppendAsync(busy, null, LlmMessage.User("private question"), Meta(0, t0));
        await store.AppendAsync(busy, m1, LlmMessage.Assistant("private answer"), Meta(1, t0.AddSeconds(1)));

        (await store.LoadPathAsync(quiet)).Should().BeEmpty(
            "a conversation with no messages has an empty transcript, no matter what else is in the database");
    }

    [Fact]
    public async Task LoadPath_ReturnsOnlyOwnMessages_WhenAnotherConversationIsNewer()
    {
        var store = new RedbConversationStore(_fx.RouteContext);
        var mine = NewConversationId();
        var theirs = NewConversationId();

        var t0 = DateTime.UtcNow;
        var m1 = await store.AppendAsync(mine, null, LlmMessage.User("mine 1"), Meta(0, t0));
        var m2 = await store.AppendAsync(mine, m1, LlmMessage.Assistant("mine 2"), Meta(1, t0.AddSeconds(1)));

        // Negative control again: written later, so globally freshest.
        var o1 = await store.AppendAsync(theirs, null, LlmMessage.User("theirs 1"), Meta(0, t0.AddSeconds(2)));
        await store.AppendAsync(theirs, o1, LlmMessage.Assistant("theirs 2"), Meta(1, t0.AddSeconds(3)));

        var path = await store.LoadPathAsync(mine);

        // Exact set, not Contain(...): a leak shows up as extra rows, and an
        // assertion that only checks for presence cannot see it.
        path.Select(x => x.Id).Should().BeEquivalentTo([m1, m2], opts => opts.WithStrictOrdering());
        path.Select(x => x.ConversationId).Should().AllBe(mine);
    }

    [Fact]
    public async Task LoadPath_LeafFromAnotherConversation_ReturnsEmpty()
    {
        var store = new RedbConversationStore(_fx.RouteContext);
        var mine = NewConversationId();
        var theirs = NewConversationId();

        await store.AppendAsync(mine, null, LlmMessage.User("mine"), Meta(0));
        var foreignLeaf = await store.AppendAsync(theirs, null, LlmMessage.User("theirs"), Meta(0));

        // Message ids are opaque GUIDs handed to callers; a caller that branches by
        // id must not be able to read another conversation through one.
        var path = await store.LoadPathAsync(mine, foreignLeaf);

        path.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_ParentFromAnotherConversation_Throws()
    {
        var store = new RedbConversationStore(_fx.RouteContext);
        var mine = NewConversationId();
        var theirs = NewConversationId();

        await store.AppendAsync(mine, null, LlmMessage.User("mine"), Meta(0));
        var foreignParent = await store.AppendAsync(theirs, null, LlmMessage.User("theirs"), Meta(0));

        // Must fail loudly: a silent write would attach this message into the other
        // conversation's tree, and no later read can undo that.
        var act = async () => await store.AppendAsync(
            mine, foreignParent, LlmMessage.Assistant("leaked"), Meta(1));

        await act.Should().ThrowAsync<InvalidOperationException>();

        // And the foreign conversation must be untouched.
        var theirPath = await store.LoadPathAsync(theirs);
        theirPath.Should().ContainSingle().Which.Id.Should().Be(foreignParent);
    }

    [Fact]
    public async Task LoadPath_OnAlreadyCorruptedTree_ReadsByFkAndSkipsForeignRoot()
    {
        // Reproduces data written *before* the fix: the conversation FK
        // (value_long) says "mine", while the physical parent is another
        // conversation's root. The FK was always stamped correctly, so a
        // FK-scoped read still recovers the right transcript — and the foreign
        // ConversationProps root must not slip in as a role-less message
        // (which the provider would reject with a 400 that looks like a model bug).
        var store = new RedbConversationStore(_fx.RouteContext);
        var mine = NewConversationId();
        var theirs = NewConversationId();

        await store.AppendAsync(mine, null, LlmMessage.User("anchor"), Meta(0));
        await store.AppendAsync(theirs, null, LlmMessage.User("theirs"), Meta(0));

        var myRootId = await RootIdOfAsync(mine);
        var theirRootId = await RootIdOfAsync(theirs);
        var theirRoot = await _fx.Redb.LoadAsync<ConversationProps>(theirRootId);

        var orphan = new TreeRedbObject<MessageProps>
        {
            value_string = Guid.NewGuid().ToString("N"),
            value_long = myRootId,                       // FK: mine
            Props = new MessageProps
            {
                Role = "assistant",
                Iteration = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Content = []
            }
        };
        await _fx.Redb.CreateChildAsync(orphan, theirRoot!);   // parent: theirs

        var path = await store.LoadPathAsync(mine);

        path.Should().NotBeEmpty();
        path.Select(x => x.Message.Role).Should().NotContain(string.Empty,
            "the foreign conversation root is not a message and must never reach the provider");
        path.Last().Id.Should().Be(orphan.value_string);
    }
}
