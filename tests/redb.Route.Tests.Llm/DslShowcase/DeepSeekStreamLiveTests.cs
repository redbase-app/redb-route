using System.Text;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Tests.Llm.TestHelpers;
using Xunit.Abstractions;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// The streamed parsers and the cache counters against a live provider. One DeepSeek key reaches both parsers:
/// the OpenAI-compatible API at <c>api.deepseek.com</c> and the Anthropic Messages API at
/// <c>api.deepseek.com/anthropic</c>. The offline tests prove the parsers on recorded shapes; these prove them on the
/// wire, through the package's own transport (HTTP/2, keep-alive pings, call limits).
/// <para>
/// Nothing here prints the key or the model's text: the output carries counts and lengths.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class DeepSeekStreamLiveTests
{
    private const string EnvVar = "REDB_LLM_DEEPSEEK";
    private const string Model = "deepseek-flash";
    private static readonly Uri AnthropicBaseUrl = new("https://api.deepseek.com/anthropic/");

    private readonly ITestOutputHelper _output;

    /// <summary>Creates the tests with xUnit's output sink.</summary>
    public DeepSeekStreamLiveTests(ITestOutputHelper output) => _output = output;

    private static string Key => Environment.GetEnvironmentVariable(EnvVar)!;

    private static LlmConnectionFactory OpenAiFactory() => new()
    {
        Name = "deepseek-live",
        Provider = "deepseek",
        ModelId = Model,
        ApiKey = Key,
        MaxTokens = 1024
    };

    private static LlmConnectionFactory AnthropicFactory() => new()
    {
        Name = "deepseek-anthropic-live",
        Provider = "anthropic",
        ModelId = Model,
        ApiKey = Key,
        BaseUrl = AnthropicBaseUrl,
        MaxTokens = 1024
    };

    private static LlmRequest Question(string? system = null) => new()
    {
        SystemPrompt = system,
        Messages = [LlmMessage.User("What is 2 + 2? Answer with the digit only.")],
        MaxTokens = 1024
    };

    /// <summary>Streams the question and checks the assembled answer against the pieces that made it.</summary>
    private async Task StreamedAnswerMatchesItsPiecesAsync(ILlmProvider provider)
    {
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var pieces = 0;
        LlmResponse? answer = null;

        await foreach (var chunk in provider.StreamAsync(Question()))
        {
            foreach (var piece in chunk.Content)
            {
                if (chunk.Response is not null) break;
                switch (piece)
                {
                    case LlmTextBlock t: text.Append(t.Text); pieces++; break;
                    case LlmThinkingBlock th: thinking.Append(th.Text); pieces++; break;
                }
            }
            if (chunk.Response is not null) answer = chunk.Response;
        }

        answer.Should().NotBeNull($"{provider.ProviderId}: the last chunk carries the assembled answer");
        var assembledText = string.Concat(answer!.Content.OfType<LlmTextBlock>().Select(b => b.Text));
        var assembledThinking = string.Concat(answer.Content.OfType<LlmThinkingBlock>().Select(b => b.Text));
        _output.WriteLine(
            $"{provider.ProviderId}: pieces={pieces}, text={assembledText.Length} chars, thinking={assembledThinking.Length} chars, "
            + $"blocks=[{string.Join(",", answer.Content.Select(b => b.GetType().Name))}], stop={answer.RawStopReason}, "
            + $"usage in/out/cacheRead={answer.Usage.InputTokens}/{answer.Usage.OutputTokens}/{answer.Usage.CacheReadInputTokens}");

        pieces.Should().BeGreaterThan(1, "the answer arrived in pieces");
        answer.StopReason.Should().Be(LlmStopReason.EndTurn);
        text.ToString().Should().Be(assembledText, "the assembled text is exactly the text that streamed");
        thinking.ToString().Should().Be(assembledThinking, "the assembled thinking is exactly the thinking that streamed");
        assembledText.Should().Contain("4");
        answer.Usage.OutputTokens.Should().BeGreaterThan(0, "the terminal usage reached the answer");
    }

    [EnvFact(EnvVar)]
    public Task OpenAiPath_StreamedAnswer_MatchesItsPieces() =>
        StreamedAnswerMatchesItsPiecesAsync(OpenAiProvider.Create(OpenAiFactory()));

    [EnvFact(EnvVar)]
    public Task AnthropicPath_StreamedAnswer_MatchesItsPieces() =>
        StreamedAnswerMatchesItsPiecesAsync(new AnthropicProvider(AnthropicFactory()));

    /// <summary>A prefix of a few thousand tokens that no earlier run has sent: it starts with this run's id.</summary>
    private static string LongPrefix()
    {
        var sb = new StringBuilder($"Run {Guid.NewGuid():N}. You answer arithmetic questions with the digit only.\n");
        for (var i = 1; i <= 300; i++)
            sb.Append("Rule ").Append(i).Append(": keep the answer short, exact and free of any explanation.\n");
        return sb.ToString();
    }

    [EnvFact(EnvVar)]
    public async Task OpenAiPath_RepeatedPrefix_IsReadFromCache()
    {
        var provider = OpenAiProvider.Create(OpenAiFactory());
        var request = Question(LongPrefix());

        var first = await provider.CompleteAsync(request);
        _output.WriteLine($"first: in={first.Usage.InputTokens}, cacheRead={first.Usage.CacheReadInputTokens}");
        first.Usage.CacheReadInputTokens.Should().Be(0, "the prefix starts with this run's id, so nothing of it was cached");

        // The provider stores the prefix after the first call; a later call reads it. Give it a few tries.
        LlmUsage? cached = null;
        for (var attempt = 1; attempt <= 4 && cached is null; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var again = await provider.CompleteAsync(request);
            _output.WriteLine($"plain #{attempt}: in={again.Usage.InputTokens}, cacheRead={again.Usage.CacheReadInputTokens}");
            if (again.Usage.CacheReadInputTokens > 0) cached = again.Usage;
        }

        cached.Should().NotBeNull("DeepSeek serves a repeated prefix from cache and says so in usage (issue #11)");
        cached!.InputTokens.Should().BeLessThan(cached.CacheReadInputTokens,
            "InputTokens is the remainder billed at full price, and most of this prompt came from cache");

        LlmResponse? streamed = null;
        await foreach (var chunk in provider.StreamAsync(request))
            if (chunk.Response is not null) streamed = chunk.Response;
        _output.WriteLine($"streamed: in={streamed!.Usage.InputTokens}, cacheRead={streamed.Usage.CacheReadInputTokens}");
        streamed.Usage.CacheReadInputTokens.Should().BeGreaterThan(0, "the streamed call reports the cache the same way");
    }

    /// <summary>Counts the pieces the engine hands on.</summary>
    private sealed class PieceCounter : IAgentObserver
    {
        private int _text;
        private int _thinking;
        public int Text => Volatile.Read(ref _text);
        public int Thinking => Volatile.Read(ref _thinking);
        public Task OnRunStartedAsync(AgentRunContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnIterationCompletedAsync(AgentIterationContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnToolInvokedAsync(AgentToolInvocationContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default) => Task.CompletedTask;

        public Task OnDeltaAsync(AgentDeltaContext context, CancellationToken ct = default)
        {
            if (context.Kind == AgentDeltaKind.Text) Interlocked.Increment(ref _text);
            else Interlocked.Increment(ref _thinking);
            return Task.CompletedTask;
        }
    }

    [EnvFact(EnvVar)]
    public async Task StreamCalls_ToolLoop_EndToEnd()
    {
        const string schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
        var tool = new EchoToolRoute("lookup", "Looks up the stored answer for a key. Always call it before answering.",
            schema, """{"answer":"42"}""");
        var counter = new PieceCounter();

        await using var host = LiveLlmHost.Build(counter).AddFactory("ds", OpenAiFactory());
        await host.StartAsync(tool, r => r.From("direct:agent")
            .To("llm://ds?stream=calls&tools=lookup&maxIterations=4")
            .To("mock:done"));

        await host.SendAsync("direct:agent",
            "Call the lookup tool with q = \"life\", then tell me the answer it returned, digits only.");

        var answer = host.Mock("mock:done").ReceivedExchanges.Single().In.Body as string;
        _output.WriteLine($"tool calls={tool.CapturedInputs.Count}, text pieces={counter.Text}, thinking pieces={counter.Thinking}, answer={answer?.Length} chars");

        tool.CapturedInputs.Should().NotBeEmpty("the streamed tool loop dispatched the tool");
        answer.Should().Contain("42", "the model answered with what the tool returned");
        // A two-character answer may arrive as one text piece; the run as a whole still streams, thinking included.
        counter.Text.Should().BeGreaterThan(0, "the answer reached the observer as it was written");
        (counter.Text + counter.Thinking).Should().BeGreaterThan(1, "the run reached the observer in pieces, not as one block");
    }
}
