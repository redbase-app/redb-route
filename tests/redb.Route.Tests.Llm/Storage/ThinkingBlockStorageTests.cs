using System.Net;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Wave 16.2: the thinking block gets a place in the redb schema, so a signed block survives
/// save -&gt; load — and a branch off the node it sits on — and comes back to the provider exactly as
/// it went in.
/// <para>
/// The thought lives in a field of its own, with the signature and the redacted payload beside it;
/// <see cref="MessageContentBlock.Text"/> belongs to the text kind.
/// </para>
/// </summary>
[Collection("StoragePro")]
public sealed class ThinkingBlockStorageTests
{
    private const string Thought = "the order table is the source of truth, check it first";
    private const string Signature = "sig-9f3c-4a17-b0e2-8d55";
    private const string Model = "claude-sonnet-4-6";

    private readonly StorageProFixture _fx;

    public ThinkingBlockStorageTests(StorageProFixture fx) => _fx = fx;

    private static LlmMessage Ask() => LlmMessage.User("status of order 42?");

    /// <summary>The assistant turn as the provider issued it: the signed reasoning and the answer.</summary>
    private static LlmMessage Answer() => new()
    {
        Role = "assistant",
        Content =
        [
            new LlmThinkingBlock(Thought, Signature),
            new LlmTextBlock("shipped")
        ]
    };

    private static ConversationMessageMeta Meta(string convId, string? providerId = "anthropic", int iteration = 1) => new()
    {
        CreatedAtUtc = DateTime.UtcNow,
        Iteration = iteration,
        ProviderId = providerId,
        ModelId = Model,
        Usage = new LlmUsage(10, 5),
        // A server-side handle on this conversation's rows, so a raw read below cannot pick up
        // another test class's data (the dictionary-indexer query the store tests already use).
        AuditTags = new Dictionary<string, string>(StringComparer.Ordinal) { ["conv"] = convId }
    };

    /// <summary>Appends the prompt and the answer; returns the conversation id and the answer node id.</summary>
    private static async Task<(string ConversationId, string AnswerId)> ConversationAsync(
        RedbConversationStore store, LlmMessage? answer = null)
    {
        var convId = $"c-{Guid.NewGuid():N}";
        var promptId = await store.AppendAsync(convId, null, Ask(), Meta(convId, providerId: null, iteration: 0));
        var answerId = await store.AppendAsync(convId, promptId, answer ?? Answer(), Meta(convId));
        return (convId, answerId);
    }

    /// <summary>Reads the stored rows of one conversation — the schema itself, not the store's mapping.</summary>
    private Task<List<redb.Core.Models.Entities.RedbObject<MessageProps>>> RowsAsync(string convId) =>
        _fx.Redb.Query<MessageProps>()
            .Where(m => m.AuditTags!["conv"] == convId)
            .ToListAsync();

    [Fact]
    public async Task Store_ThinkingBlock_TextIsNull()
    {
        var store = new RedbConversationStore(_fx.RouteContext);
        var (convId, _) = await ConversationAsync(store);

        var rows = await RowsAsync(convId);
        var block = rows
            .SelectMany(r => r.Props.Content)
            .Should().ContainSingle(b => b.Kind == "thinking").Subject;

        block.ThinkingText.Should().Be(Thought);
        block.Signature.Should().Be(Signature);
        block.Text.Should().BeNull("Text belongs to the text kind; the thought has a field of its own");
    }

    [Fact]
    public async Task RedactedThinking_SurvivesRoundTrip()
    {
        const string Payload = "ErUBCkYIBRgCIkA5Zm9wYXF1ZS1lbmNyeXB0ZWQtYmxvYg==";
        var redacted = new LlmMessage
        {
            Role = "assistant",
            Content =
            [
                new LlmThinkingBlock(string.Empty, RedactedData: Payload),
                new LlmTextBlock("shipped")
            ]
        };

        var store = new RedbConversationStore(_fx.RouteContext);
        var (convId, _) = await ConversationAsync(store, redacted);

        var block = (await store.LoadPathAsync(convId))[^1].Message.Content
            .OfType<LlmThinkingBlock>().Should().ContainSingle().Subject;

        block.IsRedacted.Should().BeTrue();
        block.RedactedData.Should().Be(Payload, "the payload is what travels back — it is not readable data");
        block.Text.Should().BeEmpty();
        block.Signature.Should().BeNull("a redacted block carries no text to authenticate");
    }

    [Fact]
    public async Task Branch_FromThinkingNode_KeepsBlock()
    {
        var store = new RedbConversationStore(_fx.RouteContext);
        var convId = $"c-{Guid.NewGuid():N}";

        var promptId = await store.AppendAsync(convId, null, Ask(), Meta(convId, providerId: null, iteration: 0));
        var thinkingNode = await store.AppendAsync(convId, promptId, Answer(), Meta(convId));
        var regenerate = await store.AppendAsync(convId, thinkingNode, LlmMessage.Assistant("still shipped"), Meta(convId));
        var followUp = await store.AppendAsync(convId, thinkingNode, LlmMessage.User("and order 43?"),
            Meta(convId, providerId: null, iteration: 2));

        // Both branches hang off the node that carries the reasoning, and the block belongs to that
        // node — so it is on every path that runs through it.
        foreach (var leaf in new[] { regenerate, followUp })
        {
            var path = await store.LoadPathAsync(convId, leaf);
            var node = path.Should().ContainSingle(n => n.Id == thinkingNode).Subject;
            node.Message.Content.OfType<LlmThinkingBlock>().Should().ContainSingle()
                .Which.Signature.Should().Be(Signature);
        }
    }

    [Fact]
    public async Task SchemaSync_ThinkingFields_AreAdded()
    {
        // Syncing again over an existing schema neither throws nor rewrites anything.
        await _fx.Redb.SyncSchemeAsync<MessageProps>();
        await _fx.Redb.SyncSchemeAsync<MessageProps>();

        var store = new RedbConversationStore(_fx.RouteContext);
        var (convId, _) = await ConversationAsync(store);
        var rows = await RowsAsync(convId);

        var assistant = rows.Should().ContainSingle(r => r.Props.Role == "assistant").Subject;
        assistant.Props.Content.Single(b => b.Kind == "thinking").ThinkingText.Should().Be(Thought);

        // A row that carries no thinking — the shape every row had before this wave — reads back
        // unchanged: the new fields are simply null there.
        var prompt = rows.Should().ContainSingle(r => r.Props.Role == "user").Subject;
        var promptBlock = prompt.Props.Content.Should().ContainSingle().Subject;
        promptBlock.Kind.Should().Be("text");
        promptBlock.Text.Should().Be("status of order 42?");
        promptBlock.ThinkingText.Should().BeNull();
        promptBlock.Signature.Should().BeNull();
        promptBlock.RedactedData.Should().BeNull();
    }

    /// <summary>Captures the raw request body the provider puts on the wire.</summary>
    private sealed class BodyCapturingHandler : HttpMessageHandler
    {
        public string? LastRawBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRawBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"id":"msg_1","content":[{"type":"text","text":"ok"}],
                     "stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
                    """)
            };
        }
    }

    private static async Task<string> WireBodyAsync(params LlmMessage[] messages)
    {
        var handler = new BodyCapturingHandler();
        var provider = new AnthropicProvider(
            new LlmConnectionFactory { Provider = "anthropic", ModelId = Model, ApiKey = "test-key" },
            new HttpClient(handler));

        await provider.CompleteAsync(new LlmRequest { Messages = messages });
        return handler.LastRawBody!;
    }

    [Fact]
    public async Task Conversation_WithThinking_ReplayedRequest_IsByteIdentical()
    {
        // What the second iteration of a tool loop sends: the prompt plus the assistant turn that
        // carries the reasoning.
        var inRun = await WireBodyAsync(Ask(), Answer());

        // The same turn now comes back from the store, and the request is rebuilt from it.
        var store = new RedbConversationStore(_fx.RouteContext);
        var (convId, _) = await ConversationAsync(store);
        var loaded = await store.LoadPathAsync(convId);
        var replayed = await WireBodyAsync(loaded.Select(n => n.Message).ToArray());

        inRun.Should().Contain("\"type\":\"thinking\"");
        inRun.Should().Contain(Signature);

        replayed.Should().Be(inRun,
            "a block that comes back short of a byte is a 400 waiting to happen — the signature covers the text");
    }
}
