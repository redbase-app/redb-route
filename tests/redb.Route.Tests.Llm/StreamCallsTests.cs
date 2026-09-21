using System.Runtime.CompilerServices;
using System.Transactions;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// <c>stream=calls</c>: the agent engine makes every model call as a stream inside the route. The pieces go to the
/// observer as the model writes them; the loop goes on with the whole answer the stream assembled, so tools, the
/// conversation, the budget and the route's transaction work exactly as without streaming, and the connection
/// carries data the whole time. <c>stream=true</c> no longer means anything and is refused with the two modes named.
/// </summary>
public sealed class StreamCallsTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Input = """{"q":"x"}""";
    private const string Reply = """{"answer":"42"}""";

    /// <summary>Records the pieces the engine hands on.</summary>
    private sealed class DeltaRecorder : IAgentObserver
    {
        public List<AgentDeltaContext> Deltas { get; } = [];
        public Task OnRunStartedAsync(AgentRunContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnIterationCompletedAsync(AgentIterationContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnToolInvokedAsync(AgentToolInvocationContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default) => Task.CompletedTask;

        public Task OnDeltaAsync(AgentDeltaContext context, CancellationToken ct = default)
        {
            Deltas.Add(context);
            return Task.CompletedTask;
        }
    }

    private static FakeProvider ToolThenAnswer() => new FakeProvider()
        .EnqueueToolUse("lookup", Input, "tu_1")
        .EnqueueText("The answer is 42.");

    private static LlmConnectionFactory Factory(ILlmProvider provider) =>
        new() { Provider = "stub", PrebuiltProvider = provider };

    /// <summary>Runs one turn through <c>direct:agent</c> → <c>llm://demo</c> → <c>mock:done</c>; returns the answer.</summary>
    private static async Task<(object? Answer, EchoToolRoute Tool)> RunAsync(
        ILlmProvider provider, string options, IAgentObserver? observer = null, bool transacted = false,
        Func<string, string>? reply = null)
    {
        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, reply ?? (_ => Reply));
        await using var host = LiveLlmHost.Build(observer).AddFactory("demo", Factory(provider));

        var uri = $"llm://demo?tools=lookup&maxIterations=4{options}";
        await host.StartAsync(tool, r =>
        {
            var from = r.From("direct:agent");
            if (transacted) from.Transacted().To(uri).To("mock:done");
            else from.To(uri).To("mock:done");
        });

        await host.SendAsync("direct:agent", "What is the answer?");
        return (host.Mock("mock:done").ReceivedExchanges.Single().In.Body, tool);
    }

    [Fact]
    public async Task StreamCalls_TheEngineStreamsEveryModelCall()
    {
        var provider = new StreamingFakeProvider(ToolThenAnswer());

        var (answer, tool) = await RunAsync(provider, "&stream=calls");

        provider.StreamCalls.Should().Be(2, "both model calls of the tool loop are streamed");
        provider.PlainCalls.Should().Be(0);
        tool.CapturedInputs.Should().ContainSingle("the tool runs in the loop as without streaming");
        answer.Should().Be("The answer is 42.", "Out.Body is the whole final answer, as without streaming");
    }

    [Fact]
    public async Task StreamCalls_SendsTheSameConversationAsAPlainRun()
    {
        var plainScript = ToolThenAnswer();
        var streamedScript = ToolThenAnswer();

        var (plainAnswer, _) = await RunAsync(plainScript, "");
        var (streamedAnswer, _) = await RunAsync(new StreamingFakeProvider(streamedScript), "&stream=calls");

        streamedScript.SentMessages.Should().BeEquivalentTo(plainScript.SentMessages, o => o.WithStrictOrdering(),
            "the model sees the same transcript on every call, tool results included");
        streamedAnswer.Should().Be(plainAnswer);
    }

    [Fact]
    public async Task StreamCalls_TheObserverGetsThePiecesAsTheyCome()
    {
        var script = new FakeProvider().Enqueue(new LlmResponse
        {
            Content = [new LlmThinkingBlock("Let me look it up."), new LlmTextBlock("The answer is 42.")],
            StopReason = LlmStopReason.EndTurn
        });
        var recorder = new DeltaRecorder();

        await RunAsync(new StreamingFakeProvider(script), "&stream=calls", recorder);

        recorder.Deltas.Should().OnlyContain(d => d.Iteration == 1 && d.Run.ExchangeId != null);
        string.Concat(recorder.Deltas.Where(d => d.Kind == AgentDeltaKind.Thinking).Select(d => d.Text))
            .Should().Be("Let me look it up.", "thinking is a piece of its own kind");
        string.Concat(recorder.Deltas.Where(d => d.Kind == AgentDeltaKind.Text).Select(d => d.Text))
            .Should().Be("The answer is 42.");
        recorder.Deltas.Count(d => d.Kind == AgentDeltaKind.Text).Should().BeGreaterThan(1, "the text came in pieces");
    }

    [Fact]
    public async Task StreamCalls_InsideTransacted_TheToolRunsInTheRouteTransaction()
    {
        var provider = new StreamingFakeProvider(ToolThenAnswer());
        bool? toolSawTransaction = null;

        await RunAsync(provider, "&stream=calls", transacted: true, reply: _ =>
        {
            toolSawTransaction = Transaction.Current is not null;
            return Reply;
        });

        provider.StreamCalls.Should().Be(2, "or this test proves nothing about streaming");
        toolSawTransaction.Should().BeTrue(
            "the streamed run happens inside the route, so its tools join the route's transaction as without streaming");
    }

    /// <summary>A provider whose stream never hands over the assembled answer.</summary>
    private sealed class NoAnswerProvider : ILlmProvider
    {
        public string ProviderId => "no-answer";
        public string ModelId => "no-answer-model";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new LlmStreamChunk([new LlmTextBlock("partial")], LlmStopReason.EndTurn, LlmUsage.Empty);
        }
    }

    [Fact]
    public async Task StreamCalls_AStreamWithoutTheAssembledAnswer_Fails()
    {
        var thrown = await Record.ExceptionAsync(() => RunAsync(new NoAnswerProvider(), "&stream=calls"));

        thrown.Should().NotBeNull("pieces alone are not an answer the loop, the store and the tools can go on with");
        thrown!.ToString().Should().Contain("no-answer", "the message names the provider that broke the contract");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("fast")]
    public async Task Stream_WithoutAMode_IsRefused(string value)
    {
        var provider = new StreamingFakeProvider(new FakeProvider().EnqueueText("never asked for"));

        var thrown = await Record.ExceptionAsync(() => RunAsync(provider, $"&stream={value}"));

        thrown.Should().NotBeNull($"'stream={value}' names no streaming mode, and ignoring it would silently not stream");
        thrown!.ToString().Should().Contain("stream=calls").And.Contain("stream=body", "the message names both modes");
        provider.StreamCalls.Should().Be(0);
        provider.PlainCalls.Should().Be(0);
    }
}
