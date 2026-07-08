using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Live integration suite that drives every <c>redb.Route.Llm.Tools</c> utility tool
/// through a real provider's tool-use loop. Each test:
/// <list type="number">
///   <item>Mounts the production tool route via <c>new XxxTool(...)</c>.</item>
///   <item>Hooks a <see cref="ToolInvocationSpy"/> so we can prove the model called the tool.</item>
///   <item>Configures an agent that exposes only that one tool.</item>
///   <item>Sends a question whose answer the model cannot reliably guess without the tool —
///         the prompt embeds a fact the model would otherwise have to fabricate or compute.</item>
///   <item>Asserts both the invocation happened and the final reply carries the tool's output.</item>
/// </list>
/// <para>
/// We use Groq <c>llama-3.3-70b-versatile</c> for the deterministic tools because its
/// free-tier tool-use is the most reliable mix we have. Tavily uses Claude — Anthropic
/// is the most consistent provider for chained tool-use that summarises external content.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class LiveToolInvocationTests
{
    [EnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_JsonPathTool_ExtractsValueFromEmbeddedJson()
    {
        var spy = new ToolInvocationSpy();

        await using var host = LiveLlmHost.Build(spy)
            .AddFactory("groq", new LlmConnectionFactory
            {
                Provider = "groq",
                ModelId = "llama-3.3-70b-versatile",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GROQ_KEY"),
                Temperature = 0.0,
                MaxTokens = 256
            });

        await host.StartAsync(new JsonPathTool(), r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Use the json_path tool when you need to read a specific value from a JSON document. " +
                    "Reply with only the extracted value, no quotes, no extra words.")
                .To(LlmDsl.Factory("groq").Tools("json_path").MaxIterations(4).AsUri())
                .To("mock:done");
        });

        var question =
            "Given this JSON: {\"order\":{\"items\":[{\"sku\":\"X-1\",\"qty\":3},{\"sku\":\"Z-9\",\"qty\":11}]}}. " +
            "What is the SKU of the second item? Use the json_path tool with path $.order.items[1].sku.";

        await host.SendAsync("direct:agent", question);

        spy.ForTool("json_path").Should().NotBeEmpty("the model must call json_path");
        var lastOutput = spy.ForTool("json_path").Last().OutputJson ?? "";
        lastOutput.Should().Contain("Z-9");

        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().Contain("Z-9");
    }

    [EnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_XPathTool_ExtractsValueFromEmbeddedXml()
    {
        var spy = new ToolInvocationSpy();

        await using var host = LiveLlmHost.Build(spy)
            .AddFactory("groq", new LlmConnectionFactory
            {
                Provider = "groq",
                ModelId = "llama-3.3-70b-versatile",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GROQ_KEY"),
                Temperature = 0.0,
                MaxTokens = 512
            });

        await host.StartAsync(new XPathTool(), r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "You must use the xpath tool to read values from XML. " +
                    "After the tool returns its result, you MUST send a final assistant text message " +
                    "containing only the extracted value. Never end the conversation without that final message.")
                .To(LlmDsl.Factory("groq").Tools("xpath").MaxIterations(6).AsUri())
                .To("mock:done");
        });

        var question =
            "Given this XML: <library><book id=\"1\"><title>Dune</title></book>" +
            "<book id=\"2\"><title>Foundation</title></book></library>. " +
            "Use the xpath tool with the expression string(//book[@id='2']/title) to read the title of book #2, " +
            "then reply with just the title.";

        await host.SendAsync("direct:agent", question);

        spy.ForTool("xpath").Should().NotBeEmpty("the model must call xpath");
        spy.ForTool("xpath").Last().OutputJson.Should().Contain("Foundation");

        var sink = host.Mock("mock:done");
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().Contain("Foundation");
    }

    [EnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_MathEvalTool_ComputesNonTrivialExpression()
    {
        var spy = new ToolInvocationSpy();

        await using var host = LiveLlmHost.Build(spy)
            .AddFactory("groq", new LlmConnectionFactory
            {
                Provider = "groq",
                ModelId = "llama-3.3-70b-versatile",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GROQ_KEY"),
                Temperature = 0.0,
                MaxTokens = 256
            });

        await host.StartAsync(new MathEvalTool(), r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Always use the math_eval tool for arithmetic — never compute it yourself. " +
                    "Reply with only the numeric result.")
                .To(LlmDsl.Factory("groq").Tools("math_eval").MaxIterations(4).AsUri())
                .To("mock:done");
        });

        // 31337 * 271 = 8 492 327 — a value the model is unlikely to memorize, but
        // ExpressionResolver computes deterministically.
        await host.SendAsync("direct:agent", "What is 31337 multiplied by 271? Use the math_eval tool.");

        spy.ForTool("math_eval").Should().NotBeEmpty("the model must call math_eval");
        spy.ForTool("math_eval").Last().OutputJson.Should().Contain("8492327");

        var sink = host.Mock("mock:done");
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().Contain("8492327");
    }

    [EnvFact("REDB_LLM_GROQ_KEY")]
    public async Task Groq_RegexExtractTool_ExtractsPatternFromText()
    {
        var spy = new ToolInvocationSpy();

        await using var host = LiveLlmHost.Build(spy)
            .AddFactory("groq", new LlmConnectionFactory
            {
                Provider = "groq",
                ModelId = "llama-3.3-70b-versatile",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_GROQ_KEY"),
                Temperature = 0.0,
                MaxTokens = 256
            });

        await host.StartAsync(new RegexExtractTool(), r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Use the regex_extract tool to pull a value out of a string. " +
                    "Reply with only the extracted value.")
                .To(LlmDsl.Factory("groq").Tools("regex_extract").MaxIterations(4).AsUri())
                .To("mock:done");
        });

        var question =
            "Given the text 'order id ORD-7421 was placed at 14:55', extract the order id " +
            "(matches the pattern ORD-\\d+) using the regex_extract tool.";

        await host.SendAsync("direct:agent", question);

        spy.ForTool("regex_extract").Should().NotBeEmpty("the model must call regex_extract");
        spy.ForTool("regex_extract").Last().OutputJson.Should().Contain("ORD-7421");

        var sink = host.Mock("mock:done");
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().Contain("ORD-7421");
    }

    [EnvFact("REDB_LLM_ANT_API03_KEY", "REDB_LLM_TAVILY_KEY")]
    public async Task Claude_TavilyWebSearchTool_AnswersFromLiveSearch()
    {
        var spy = new ToolInvocationSpy();

        await using var host = LiveLlmHost.Build(spy)
            .AddFactory("claude", new LlmConnectionFactory
            {
                Provider = "anthropic",
                ModelId = "claude-haiku-4-5",
                ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY"),
                Temperature = 0.0,
                MaxTokens = 384
            });

        await host.StartAsync(new TavilyWebSearchTool(new TavilyWebSearchOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_TAVILY_KEY")!,
            MaxResults = 3,
            Timeout = TimeSpan.FromSeconds(15)
        }), r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "When the user asks for current real-world facts, call the web_search tool with a focused " +
                    "query, then summarise the findings in one short sentence.")
                .To(LlmDsl.Factory("claude").Tools("web_search").MaxIterations(4).AsUri())
                .To("mock:done");
        });

        // A factual query whose answer is stable enough to assert on — Tavily's
        // 'answer' field for capital-of-France queries reliably mentions Paris.
        await host.SendAsync("direct:agent", "What is the capital city of France?");

        spy.ForTool("web_search").Should().NotBeEmpty("the model must call web_search");

        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        var reply = ((string)sink.ReceivedExchanges[0].In.Body!).ToLowerInvariant();
        reply.Should().Contain("paris");
    }
}
