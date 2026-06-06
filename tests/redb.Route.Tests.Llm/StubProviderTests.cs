namespace redb.Route.Tests.Llm;

public sealed class StubProviderTests
{
    private static StubProvider Create(string modelId = "stub-model") =>
        new(new LlmConnectionFactory { ModelId = modelId });

    [Fact]
    public void ProviderId_IsStub()
    {
        Create().ProviderId.Should().Be("stub");
    }

    [Fact]
    public void ModelId_ReflectsFactory()
    {
        Create("xyz-7b").ModelId.Should().Be("xyz-7b");
    }

    [Fact]
    public async Task Complete_EchoesLastUserMessage()
    {
        var p = Create();
        var resp = await p.CompleteAsync(new LlmRequest
        {
            Messages = [LlmMessage.User("hello")]
        });

        resp.StopReason.Should().Be(LlmStopReason.EndTurn);
        resp.Content.Should().HaveCount(1);
        resp.Content[0].Should().BeOfType<LlmTextBlock>()
            .Which.Text.Should().Be("[stub] hello");
    }

    [Fact]
    public async Task Complete_FixedReply_OverridesEcho()
    {
        var p = new StubProvider(new LlmConnectionFactory()) { FixedReply = "constant" };
        var resp = await p.CompleteAsync(new LlmRequest
        {
            Messages = [LlmMessage.User("ignored")]
        });
        ((LlmTextBlock)resp.Content[0]).Text.Should().Be("constant");
    }

    [Fact]
    public async Task Complete_ReportsUsage()
    {
        var p = Create();
        var resp = await p.CompleteAsync(new LlmRequest
        {
            Messages = [LlmMessage.User("aaaaaaaa")] // 8 chars / 4 = 2 tokens
        });
        resp.Usage.InputTokens.Should().BeGreaterThan(0);
        resp.Usage.OutputTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Complete_EmptyTranscript_ReturnsPlaceholder()
    {
        var p = Create();
        var resp = await p.CompleteAsync(new LlmRequest());
        ((LlmTextBlock)resp.Content[0]).Text.Should().Contain("(no user message)");
    }

    [Fact]
    public async Task Complete_HonoursCancellation()
    {
        var p = Create();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => p.CompleteAsync(new LlmRequest(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
