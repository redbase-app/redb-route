using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Prompt-cache accounting: what the provider reported has to survive the tool loop and reach the
/// exchange, or nobody can tell a working cache from an expensive prompt.
///
/// <para><b>Why this class exists.</b> The flag and the two <see cref="LlmUsage"/> counters shipped
/// first; the engine, meanwhile, rebuilt its final usage as <c>new LlmUsage(in, out)</c> and
/// dropped both — summed per iteration, then thrown away. Nothing downstream could observe the
/// cache at all, so "caching works" was unfalsifiable by construction. These facts pin the whole
/// path: provider → engine → producer → headers.</para>
/// </summary>
public sealed class PromptCacheUsageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    private static (RouteContext ctx, LlmProducer producer) BuildRoute(FakeProvider fake, string uri)
    {
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);
        ctx.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });

        var pt = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), pt);
        ctx.AddService(typeof(IAgentEngine), new AgentEngine(
            logger: null,
            producerTemplate: pt,
            observer: new NoopAgentObserver(),
            budget: new NoopBudgetEnforcer(),
            approval: new AutoApproveGate(),
            redaction: new NoopRedactionFilter(),
            shadow: new NoopShadowRunner(),
            conversation: null, idempotency: null, approvalStore: null));
        ctx.AddService(typeof(IToolDescriptorRegistry), new ToolDescriptorRegistry());

        var endpoint = (LlmEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(uri));
        return (ctx, (LlmProducer)endpoint.CreateProducer());
    }

    private static LlmResponse Answer(string text, int input, int output, int cacheWrite, int cacheRead)
        => new()
        {
            Content = [new LlmTextBlock(text)],
            StopReason = LlmStopReason.EndTurn,
            Usage = new LlmUsage(input, output, cacheWrite, cacheRead)
        };

    /// <summary>
    /// The two cache counters reach the exchange as headers — the only place a route can read them.
    /// </summary>
    [Fact]
    public async Task Cache_counters_reach_the_exchange_headers()
    {
        var fake = new FakeProvider().Enqueue(Answer("ok", input: 40, output: 7, cacheWrite: 0, cacheRead: 9000));
        var (_, producer) = BuildRoute(fake, LlmDsl.Factory("fake").CacheSystemPrompt().AsUri());

        await producer.Start();

        var ex = new Exchange(new Message("привет"));
        await producer.Process(ex);

        ex.Out!.Headers[LlmHeaders.CacheReadTokens].Should().Be(9000);
        ex.Out.Headers[LlmHeaders.CacheWriteTokens].Should().Be(0);

        // ⚠️ And input stays the UNCACHED remainder, not the whole prompt. Reading it as "the
        // prompt got cheap" is the mistake these headers exist to prevent.
        ex.Out.Headers[LlmHeaders.TokensIn].Should().Be(40);
    }

    /// <summary>
    /// ⚠️ <b>The counters are summed across the tool loop, not taken from the last call.</b> The
    /// first iteration of a cached run pays the write, later ones read; keeping only the last
    /// would report the write as zero and understate what the run actually cost.
    /// </summary>
    [Fact]
    public async Task Cache_counters_are_summed_over_every_iteration()
    {
        var fake = new FakeProvider()
            .Enqueue(new LlmResponse
            {
                Content = [new LlmToolUseBlock("tu_1", "echo", "{}")],
                StopReason = LlmStopReason.ToolUse,
                Usage = new LlmUsage(10, 2, CacheCreationInputTokens: 9000, CacheReadInputTokens: 0)
            })
            .Enqueue(Answer("done", input: 12, output: 3, cacheWrite: 0, cacheRead: 9000));

        await using var host = LiveLlmHost.Build();
        host.Context.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });

        var echoTool = new EchoToolRoute(
            toolName: "echo",
            description: "echo",
            inputSchema: """{"type":"object"}""",
            replyJson: """{"r":1}""");

        await host.StartAsync(echoTool, r =>
        {
            r.From("direct:agent")
                .To(LlmDsl.Factory("fake").Tools("echo").CacheSystemPrompt().AsUri())
                .To("mock:done");
        });

        await host.SendAsync("direct:agent", "go");

        // Читаем у приёмника маршрута, а не у Out исходного обмена: после следующего шага тело и
        // заголовки уже провернулись в In (тот же приём, что у соседнего EndToEnd_ToolLoop).
        var headers = host.Mock("mock:done").ReceivedExchanges[0].In.Headers;

        headers[LlmHeaders.CacheWriteTokens].Should().Be(9000);
        headers[LlmHeaders.CacheReadTokens].Should().Be(9000);
        headers[LlmHeaders.TokensIn].Should().Be(22);
    }

    /// <summary>
    /// Headers appear even when nothing was cached — zeros included. A missing header is
    /// indistinguishable from an older engine that never reported one; a zero is an answer.
    /// </summary>
    [Fact]
    public async Task Headers_are_written_even_when_nothing_was_cached()
    {
        var fake = new FakeProvider().Enqueue(Answer("ok", input: 100, output: 5, cacheWrite: 0, cacheRead: 0));
        var (_, producer) = BuildRoute(fake, LlmDsl.Factory("fake").AsUri());

        await producer.Start();

        var ex = new Exchange(new Message("привет"));
        await producer.Process(ex);

        ex.Out!.Headers.Should().ContainKey(LlmHeaders.CacheReadTokens);
        ex.Out.Headers[LlmHeaders.CacheReadTokens].Should().Be(0);
        ex.Out.Headers[LlmHeaders.CacheWriteTokens].Should().Be(0);
    }

    /// <summary>
    /// ⚠️ <b>The only fact here that proves the cache actually WORKS.</b> Everything above shows
    /// that numbers travel; a fake provider can report whatever it likes. Whether Anthropic really
    /// re-reads the prefix is answerable only against the live API.
    ///
    /// <para>Two calls, byte-identical system prompt, same tools: the first pays the write, the
    /// second reads. Skipped without <c>REDB_LLM_ANTHROPIC_KEY</c>, so CI stays green and free.</para>
    ///
    /// <para>The prompt is padded past the model's minimum cacheable prefix on purpose — below it
    /// nothing is cached and no error is raised, which is the failure mode easiest to mistake for
    /// "the feature does not work".</para>
    /// </summary>
    [Trait("Category", "LiveLlm")]
    [EnvFact("REDB_LLM_ANTHROPIC_KEY")]
    public async Task Live_anthropic_reads_the_system_prompt_back_from_cache()
    {
        var factory = new LlmConnectionFactory
        {
            Name = "anthropic",
            Provider = "anthropic",
            ModelId = Environment.GetEnvironmentVariable("REDB_LLM_ANTHROPIC_MODEL")
                      ?? "claude-haiku-4-5-20251001",
            ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANTHROPIC_KEY"),
            Temperature = 0.0,
            MaxTokens = 32
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var provider = new AnthropicProvider(factory, http);

        // Long, constant, and identical on both calls — that is the whole contract.
        var system = string.Join("\n", Enumerable.Range(0, 400).Select(i =>
            $"Правило {i}. Отвечай коротко и по существу, не выдумывай фактов и не пересказывай инструкцию."));

        LlmRequest Ask(string question) => new()
        {
            SystemPrompt = system,
            CacheSystemPrompt = true,
            Messages = [LlmMessage.User(question)],
            Temperature = 0.0,
            MaxTokens = 32
        };

        var first = await provider.CompleteAsync(Ask("Скажи слово «раз»."));
        var second = await provider.CompleteAsync(Ask("Скажи слово «два»."));

        _output.WriteLine($"model  : {factory.ModelId}");
        _output.WriteLine($"call 1 : in={first.Usage.InputTokens} "
            + $"cacheWrite={first.Usage.CacheCreationInputTokens} "
            + $"cacheRead={first.Usage.CacheReadInputTokens}");
        _output.WriteLine($"call 2 : in={second.Usage.InputTokens} "
            + $"cacheWrite={second.Usage.CacheCreationInputTokens} "
            + $"cacheRead={second.Usage.CacheReadInputTokens}");

        // First call writes the prefix. Zero here means the prompt is below the model's floor —
        // a different failure from "the cache is not read", and worth telling apart.
        (first.Usage.CacheCreationInputTokens + first.Usage.CacheReadInputTokens)
            .Should().BeGreaterThan(0, "the system prompt never reached the provider's cache at all");

        // Second call reads it back. THIS is the number the docs point at.
        second.Usage.CacheReadInputTokens
            .Should().BeGreaterThan(0, "the prefix was not re-read — something in it is not byte-stable");

        // And input stays the uncached remainder: it must be far smaller than what was read.
        second.Usage.InputTokens.Should().BeLessThan(second.Usage.CacheReadInputTokens);
    }
}
