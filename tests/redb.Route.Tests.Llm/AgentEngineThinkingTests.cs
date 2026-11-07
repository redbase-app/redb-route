using redb.Route.Llm.Engine.Storage;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// A thinking block travels through the run and reaches the conversation record in the block's own
/// field, with its signature, never as text. It is not an answer: it never reaches
/// <c>AgentResponse.Text</c> or <c>Out.Body</c>.
/// <para>
/// Every turn is written, including a turn the model spent entirely on thinking: the record keeps what
/// happened (the thought, the usage, the stop reason). Whether such a turn is sent back to a provider
/// is a request-shape question, answered by the provider request tests.
/// </para>
/// </summary>
public sealed class AgentEngineThinkingTests
{
    private static AgentEngine Engine(IConversationStore? conversation = null) => new(
        logger: null,
        producerTemplate: null,
        observer: null,
        budget: null,
        approval: null,
        redaction: null,
        shadow: null,
        conversation: conversation,
        idempotency: null,
        approvalStore: null);

    private static LlmConnectionFactory Factory(FakeProvider provider) => new()
    {
        Provider = "fake",
        ModelId = provider.ModelId,
        PrebuiltProvider = provider
    };

    private static LlmResponse Reply(LlmStopReason stop, params LlmContentBlock[] blocks) => new()
    {
        Content = blocks,
        StopReason = stop,
        Usage = new LlmUsage(3, 4)
    };

    [Fact]
    public async Task ThinkingBlock_IsAppendedToConversation()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeProvider().Enqueue(Reply(LlmStopReason.EndTurn,
            new LlmThinkingBlock("hidden chain of thought", "sig-1"), new LlmTextBlock("shipped")));

        var response = await Engine(store).RunAsync(new AgentRequest
        {
            Factory = Factory(provider),
            Exchange = new Exchange(new Message("status of order 42?")),
            UserContent = [new LlmTextBlock("status of order 42?")],
            ConversationId = "conv-thinking"
        });

        // The run itself keeps the block — that is what makes the next iteration of a tool loop work.
        response.Content.OfType<LlmThinkingBlock>().Should().ContainSingle();

        var path = await store.LoadPathAsync("conv-thinking");
        path.Should().HaveCount(2, "the prompt and the answer; a thinking block is not a third message");

        // The record holds the block with its turn: the schema has a field for the thought and for the
        // signature.
        var stored = path[^1].Message.Content.OfType<LlmThinkingBlock>().Should().ContainSingle().Subject;
        stored.Text.Should().Be("hidden chain of thought");
        stored.Signature.Should().Be("sig-1");
        path[^1].Message.Content.OfType<LlmTextBlock>().Should().ContainSingle().Which.Text.Should().Be("shipped");
    }

    [Fact]
    public async Task TurnWithNothingButThinking_IsWritten()
    {
        var store = new InMemoryConversationStore();
        // The model spent the whole turn thinking and ran out of tokens before saying anything. The
        // turn is still part of what happened: its row carries the thought, the tokens it cost and the
        // stop reason that explains the empty answer.
        var provider = new FakeProvider().Enqueue(Reply(LlmStopReason.MaxTokens, new LlmThinkingBlock("still thinking")));

        await Engine(store).RunAsync(new AgentRequest
        {
            Factory = Factory(provider),
            Exchange = new Exchange(new Message("status?")),
            UserContent = [new LlmTextBlock("status?")],
            ConversationId = "conv-silent"
        });

        var path = await store.LoadPathAsync("conv-silent");
        path.Should().HaveCount(2, "the prompt and the turn that was nothing but thinking");

        var turn = path[1];
        turn.Message.Role.Should().Be("assistant");
        turn.Message.Content.Should().ContainSingle()
            .Which.Should().BeOfType<LlmThinkingBlock>().Which.Text.Should().Be("still thinking");
        turn.Meta.StopReason.Should().Be(LlmStopReason.MaxTokens);
        turn.Meta.Usage.OutputTokens.Should().Be(4, "the tokens the turn cost are recorded with it");
    }

    [Fact]
    public async Task VisibleAnswer_DoesNotContainThinkingText()
    {
        const string Secret = "SECRET chain of thought";
        var provider = new FakeProvider();
        for (var i = 0; i < 2; i++)
            provider.Enqueue(Reply(LlmStopReason.EndTurn, new LlmThinkingBlock(Secret), new LlmTextBlock("shipped")));

        var response = await Engine().RunAsync(new AgentRequest
        {
            Factory = Factory(provider),
            Exchange = new Exchange(new Message("status?")),
            UserContent = [new LlmTextBlock("status?")]
        });

        response.Text.Should().Be("shipped", "the answer is the text block, not the block that came first");
        response.Text.Should().NotContain(Secret);

        // The same invariant at the edge of a route: what a caller reads is the answer.
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);
        ctx.AddToRegistry("fake", Factory(provider));
        ctx.AddService(typeof(IAgentEngine), new AgentEngine());

        var endpoint = (LlmEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("llm://fake"));
        var producer = (LlmProducer)endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("status?"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("shipped");
        exchange.Out.Body!.ToString().Should().NotContain(Secret);
    }
}
