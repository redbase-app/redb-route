using redb.Route.Llm.Engine.Storage;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Producer-level coverage for the <see cref="LlmHeaders.Preamble"/> header: fixed opening
/// messages open the transcript on every call, sit before the loaded history, and never reach
/// the conversation store. Backed by <see cref="InMemoryConversationStore"/> — no DB required.
/// </summary>
public sealed class LlmPreambleTests
{
    private const string Question = "Read it? Now, in your own words: do you accept?";
    private const string Reply = "I accept — as a working rule, with the reservations named.";

    private static (LlmProducer producer, FakeProvider fake, InMemoryConversationStore store) Build()
    {
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);

        var fake = new FakeProvider();
        ctx.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });

        var store = new InMemoryConversationStore();
        ctx.AddService(typeof(IAgentEngine), new AgentEngine(
            logger: null,
            producerTemplate: null,
            observer: null,
            budget: null,
            approval: null,
            redaction: null,
            shadow: null,
            conversation: store,
            idempotency: null,
            approvalStore: null));

        var endpoint = (LlmEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("llm://fake?conversation=header"));
        return ((LlmProducer)endpoint.CreateProducer(), fake, store);
    }

    private static IReadOnlyList<LlmMessage> Preamble() =>
    [
        LlmMessage.User(Question),
        new LlmMessage
        {
            Role = "assistant",
            Content = [new LlmTextBlock(Reply)],
            CacheBreakpoint = true
        }
    ];

    private static async Task TurnAsync(LlmProducer producer, string text)
    {
        var msg = new Message(text);
        msg.Headers[LlmHeaders.ConversationId] = "c-test";
        msg.Headers[LlmHeaders.Preamble] = Preamble();
        await producer.Process(new Exchange(msg));
    }

    private static string Text(LlmMessage m) => m.Content.OfType<LlmTextBlock>().First().Text;

    [Fact]
    public async Task Preamble_OpensTheTranscript_AndSitsBeforeTheLoadedHistory()
    {
        var (producer, fake, _) = Build();
        fake.EnqueueText("first").EnqueueText("second");
        await producer.Start();

        await TurnAsync(producer, "hi");
        await TurnAsync(producer, "again");

        fake.CapturedRequests.Should().HaveCount(2);

        // Second call: preamble first, then the stored history, then the new turn. (The captured
        // list is the engine's live transcript, so the tail also carries the reply appended after
        // the call — the prefix is what this fact is about.)
        var seen = fake.CapturedRequests[1].Messages;
        seen.Take(5).Select(m => m.Role).Should().Equal("user", "assistant", "user", "assistant", "user");
        Text(seen[0]).Should().Be(Question);
        Text(seen[1]).Should().Be(Reply);
        seen[1].CacheBreakpoint.Should().BeTrue("the breakpoint rides on the message into the provider");
        Text(seen[2]).Should().Be("hi");
        Text(seen[3]).Should().Be("first");
        Text(seen[4]).Should().Be("again");
    }

    [Fact]
    public async Task Preamble_IsNeverPersisted()
    {
        var (producer, fake, store) = Build();
        fake.EnqueueText("ok");
        await producer.Start();

        await TurnAsync(producer, "hi");

        // The store holds what was said in this dialog — one user turn, one reply — and nothing
        // of the fixed opening exchange.
        var rows = await store.LoadTreeAsync("c-test");
        rows.Should().HaveCount(2);
        rows.Select(r => r.Message.Role).Should().Equal("user", "assistant");
        rows.Select(r => Text(r.Message)).Should().NotContain(Question).And.NotContain(Reply);
    }

    [Fact]
    public async Task WithoutTheHeader_NothingChanges()
    {
        var (producer, fake, _) = Build();
        fake.EnqueueText("ok");
        await producer.Start();

        var msg = new Message("hi");
        msg.Headers[LlmHeaders.ConversationId] = "c-test";
        await producer.Process(new Exchange(msg));

        fake.CapturedRequests.Single().Messages[0].Role.Should().Be("user");
        Text(fake.CapturedRequests.Single().Messages[0]).Should().Be("hi");
    }
}
