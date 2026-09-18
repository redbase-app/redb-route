using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// Streaming × tools (wave 8). <c>stream=true</c> bypasses the agent engine — no tool loop, no
/// governance, no conversation persistence — so a tool-using route configured to stream would silently
/// not run its tools. The endpoint refuses that combination at route build while leaving plain
/// streaming untouched.
/// </summary>
[Trait("Category", "Governance")]
public sealed class StreamingToolPolicyTests
{
    /// <summary>The combination is refused when the endpoint is created — on the way to the first call.</summary>
    [Fact]
    public async Task StreamingWithTools_IsRejectedByOptions()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = new FakeProvider() });

        await host.StartAsync(r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Stream().Tools("*").AsUri())
            .To("mock:done"));

        var thrown = await Record.ExceptionAsync(() => host.SendAsync("direct:agent", "hello"));

        thrown.Should().BeOfType<ArgumentException>()
            .Which.Message.Should().Contain("stream=true",
                "the message must name the option that causes the loss, so the fix is obvious");
    }

    [Fact]
    public async Task StreamingWithoutTools_IsStillAllowed()
    {
        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = new FakeProvider() });

        var started = await Record.ExceptionAsync(() => host.StartAsync(r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Stream().AsUri())
            .To("mock:done")));

        started.Should().BeNull("streaming plain completions is untouched by the tool-loop restriction");
    }
}