using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// Streaming × tools. Both modes run the agent engine with its tools: <c>stream=calls</c> inside the route
/// (StreamCallsTests), <c>stream=body</c> when the body is read (LazyStreamTests). So the combination is allowed when
/// the endpoint is created; the one placement rule left is <c>stream=body</c> inside <c>.Transacted()</c>, refused at
/// run time because the block would commit before the answer exists.
/// </summary>
[Trait("Category", "Governance")]
public sealed class StreamingToolPolicyTests
{
    [Theory]
    [InlineData(LlmStreamMode.Calls)]
    [InlineData(LlmStreamMode.Body)]
    public async Task StreamingWithTools_IsAllowed(LlmStreamMode mode)
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory
            {
                Provider = "stub",
                PrebuiltProvider = new FakeProvider().EnqueueText("done")
            });

        await host.StartAsync(r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Stream(mode).Tools("*").AsUri()));

        IExchange? exchange = null;
        var thrown = await Record.ExceptionAsync(async () => exchange = await host.SendAsync("direct:agent", "hello"));
        if (exchange is not null) await exchange.DisposeAsync();

        thrown.Should().BeNull("a tool-using agent streams in either mode");
        exchange!.Exception.Should().BeNull();
    }

    [Fact]
    public async Task StreamingWithoutTools_IsStillAllowed()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = new FakeProvider() });

        var started = await Record.ExceptionAsync(() => host.StartAsync(r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Stream(LlmStreamMode.Body).AsUri())
            .To("mock:done")));

        started.Should().BeNull("streaming plain completions is untouched");
    }
}
