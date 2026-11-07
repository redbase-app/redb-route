using redb.Route.Llm.Engine.Storage;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// A stored conversation can be continued on another provider: switching the factory is the caller's
/// decision, and the history goes to the new provider as it was written, thinking blocks included.
/// Each provider's request path sends what its wire can express (the provider request tests pin
/// that); the engine does not sort blocks by who produced them.
/// </summary>
public sealed class ThinkingProviderSwitchTests
{
    private const string Prompt = "status of order 42?";

    private static AgentEngine Engine(IConversationStore store) => new(
        logger: null,
        producerTemplate: null,
        observer: null,
        budget: null,
        approval: null,
        redaction: null,
        shadow: null,
        conversation: store,
        idempotency: null,
        approvalStore: null);

    private static ConversationMessageMeta Meta(string providerId, int iteration) => new()
    {
        CreatedAtUtc = DateTime.UtcNow,
        Iteration = iteration,
        ProviderId = providerId,
        Usage = new LlmUsage(1, 1)
    };

    [Fact]
    public async Task ProviderSwitch_StoredThinkingTravelsWithTheHistory()
    {
        var store = new InMemoryConversationStore();
        const string ConvId = "conv-switch";

        var promptId = await store.AppendAsync(ConvId, null, LlmMessage.User(Prompt), Meta("anthropic", 0));
        await store.AppendAsync(ConvId, promptId, new LlmMessage
        {
            Role = "assistant",
            Content = [new LlmThinkingBlock("anthropic reasoning", "sig-1"), new LlmTextBlock("shipped")]
        }, Meta("anthropic", 1));

        // The conversation was written by Anthropic; this run asks DeepSeek.
        var provider = new FakeProvider();
        await Engine(store).RunAsync(new AgentRequest
        {
            Factory = new LlmConnectionFactory
            {
                Provider = "deepseek",
                ModelId = provider.ModelId,
                PrebuiltProvider = provider
            },
            Exchange = new Exchange(new Message(Prompt)),
            UserContent = [new LlmTextBlock(Prompt)],
            ConversationId = ConvId
        });

        var sent = provider.SentMessages.Should().ContainSingle().Subject;
        sent.Should().HaveCount(3, "the stored prompt, the stored turn and this run's prompt");

        var turn = sent[1].Content;
        turn.Should().HaveCount(2, "the stored turn goes to the new provider as it was written");
        turn[0].Should().BeOfType<LlmThinkingBlock>().Which.Signature.Should().Be("sig-1");
        turn[1].Should().BeOfType<LlmTextBlock>().Which.Text.Should().Be("shipped");
    }
}
