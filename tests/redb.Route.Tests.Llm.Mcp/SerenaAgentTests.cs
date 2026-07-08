using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>
/// End-to-end agent tests against the live Serena MCP server. Each test:
/// <list type="number">
///   <item>Reuses the shared <see cref="SerenaFixture"/> (one Serena process per collection).</item>
///   <item>Builds an <see cref="McpAgentHost"/> that projects Serena's <c>tools/list</c>
///         catalogue into <see cref="IToolDescriptorRegistry"/> as <see cref="McpToolDescriptor"/>s.</item>
///   <item>Sends a question whose answer requires reading the source file via the
///         <c>serena__get_symbols_overview</c> tool.</item>
///   <item>Asserts the model actually invoked the MCP tool through the
///         <see cref="ToolInvocationSpy"/>.</item>
/// </list>
/// <para>
/// Groq <c>llama-3.3-70b-versatile</c> is used because its free-tier tool-use is
/// the most reliable mix we have. Claude <c>claude-haiku-4-5</c> provides a second
/// vendor cross-check.
/// </para>
/// </summary>
[Trait("Category", "LiveLlmMcp")]
[Collection("SerenaSerial")]
public sealed class SerenaAgentTests
{
    private readonly SerenaFixture _fixture;

    public SerenaAgentTests(SerenaFixture fixture) => _fixture = fixture;

    [SerenaEnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_AgentInvokesGetSymbolsOverview_OnRedbRouteFile()
    {
        var spy = new ToolInvocationSpy();

        await using var host = McpAgentHost.Build("serena", _fixture.Client!, _fixture.Tools, spy)
            .AddFactory("groq", new LlmConnectionFactory
            {
                Provider = "groq",
                ModelId = "llama-3.3-70b-versatile",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GROQ_KEY"),
                Temperature = 0.0,
                MaxTokens = 512,
            });

        await host.StartAsync(r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "You are a code-archaeology assistant. When the user asks about a C# source file, " +
                    "you MUST call the serena__get_symbols_overview tool with that file's relative path " +
                    "to inspect its top-level symbols. Reply with a single short sentence summarising " +
                    "what the file declares.")
                .To(LlmDsl.Factory("groq")
                    .Tools("serena__get_symbols_overview")
                    .MaxIterations(4)
                    .AsUri());
        });

        var question =
            "What top-level types are declared in 'redb.Route/src/redb.Route.Llm.Mcp/Protocol/McpProtocol.cs'? " +
            "Use the serena__get_symbols_overview tool with relative_path set to that path, then summarise.";

        var ex = await host.SendAsync("direct:agent", question);

        spy.ForTool("serena__get_symbols_overview")
            .Should().NotBeEmpty("the model must call serena's get_symbols_overview tool");

        // The spy already proves the agent dispatched through mcp://. The exact
        // response payload depends on which path Groq passed to Serena, so we
        // only check the tool was invoked and the run produced a final reply.
        ex.In.Body.Should().NotBeNull();
    }

    [SerenaEnvFact("REDB_LLM_ANT_API03_KEY")]
    public async Task Claude_AgentInvokesGetSymbolsOverview_OnRedbRouteFile()
    {
        var spy = new ToolInvocationSpy();

        await using var host = McpAgentHost.Build("serena", _fixture.Client!, _fixture.Tools, spy)
            .AddFactory("claude", new LlmConnectionFactory
            {
                Provider = "anthropic",
                ModelId = "claude-haiku-4-5",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY"),
                Temperature = 0.0,
                MaxTokens = 512,
            });

        await host.StartAsync(r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "You inspect C# code via Serena's MCP tools. To answer questions about a source file, " +
                    "call serena__get_symbols_overview with the file's relative_path, then summarise " +
                    "the result in one short sentence.")
                .To(LlmDsl.Factory("claude")
                    .Tools("serena__get_symbols_overview")
                    .MaxIterations(4)
                    .AsUri());
        });

        var question =
            "Look at 'redb.Route/src/redb.Route.Llm.Mcp/McpProducer.cs' using the " +
            "serena__get_symbols_overview tool and tell me what top-level type it declares.";

        var ex = await host.SendAsync("direct:agent", question);

        spy.ForTool("serena__get_symbols_overview")
            .Should().NotBeEmpty("Claude must call serena's get_symbols_overview tool");

        // The spy already proves Claude reached the MCP producer. The exact
        // response payload is LLM-dependent (the model can call the tool with
        // different relative_path values across runs), so assert only that the
        // round-trip completed and the run produced a final assistant reply.
        ex.In.Body.Should().NotBeNull();
    }
}
