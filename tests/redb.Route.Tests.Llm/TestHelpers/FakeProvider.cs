namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Scriptable provider for engine/producer tests. Returns a queued <see cref="LlmResponse"/>
/// per <see cref="CompleteAsync"/> call. When the queue empties, defaults to a final
/// <c>EndTurn</c> echoing "(no script)".
/// </summary>
public sealed class FakeProvider : ILlmProvider
{
    private readonly Queue<LlmResponse> _scripted = new();

    public string ProviderId => "fake";
    public string ModelId { get; init; } = "fake-model";

    public List<LlmRequest> CapturedRequests { get; } = new();
    public int CallCount { get; private set; }
    public Func<LlmRequest, CancellationToken, Task>? OnCall { get; set; }
    public Exception? ThrowOnCall { get; set; }

    public FakeProvider Enqueue(LlmResponse response)
    {
        _scripted.Enqueue(response);
        return this;
    }

    public FakeProvider EnqueueText(string text, LlmStopReason stop = LlmStopReason.EndTurn,
        int tokensIn = 1, int tokensOut = 1)
    {
        return Enqueue(new LlmResponse
        {
            Content = [new LlmTextBlock(text)],
            StopReason = stop,
            Usage = new LlmUsage(tokensIn, tokensOut)
        });
    }

    public FakeProvider EnqueueToolUse(string toolName, string inputJson, string toolUseId = "tu_1",
        int tokensIn = 1, int tokensOut = 1)
    {
        return Enqueue(new LlmResponse
        {
            Content = [new LlmToolUseBlock(toolUseId, toolName, inputJson)],
            StopReason = LlmStopReason.ToolUse,
            Usage = new LlmUsage(tokensIn, tokensOut)
        });
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        CallCount++;
        CapturedRequests.Add(request);
        if (OnCall is not null) await OnCall(request, ct).ConfigureAwait(false);
        if (ThrowOnCall is not null) throw ThrowOnCall;

        if (_scripted.TryDequeue(out var scripted)) return scripted;

        return new LlmResponse
        {
            Content = [new LlmTextBlock("(no script)")],
            StopReason = LlmStopReason.EndTurn,
            Usage = LlmUsage.Empty
        };
    }
}
