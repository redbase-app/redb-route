namespace redb.Route.Llm.Providers;

/// <summary>
/// Deterministic provider for tests, demos, and Phase-0 smoke. Echoes the last
/// user message back as an assistant text block, ending the turn without tool calls.
/// </summary>
public sealed class StubProvider : ILlmProvider
{
    private readonly LlmConnectionFactory _factory;

    /// <summary>Optional fixed reply — when set, overrides the echo behaviour.</summary>
    public string? FixedReply { get; init; }

    /// <summary>Creates a stub provider bound to a connection factory.</summary>
    public StubProvider(LlmConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public string ProviderId => "stub";

    /// <inheritdoc />
    public string ModelId => _factory.ModelId;

    /// <inheritdoc />
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var reply = FixedReply ?? ExtractEcho(request);

        var response = new LlmResponse
        {
            Content = [new LlmTextBlock(reply)],
            StopReason = LlmStopReason.EndTurn,
            Usage = new LlmUsage(EstimateTokens(request), EstimateTokens(reply)),
            RawStopReason = "stub:end_turn"
        };

        return Task.FromResult(response);
    }

    private static string ExtractEcho(LlmRequest request)
    {
        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            var m = request.Messages[i];
            if (m.Role != "user") continue;

            foreach (var block in m.Content)
                if (block is LlmTextBlock text)
                    return $"[stub] {text.Text}";
        }
        return "[stub] (no user message)";
    }

    private static int EstimateTokens(LlmRequest request)
    {
        var total = 0;
        foreach (var m in request.Messages)
            foreach (var c in m.Content)
                if (c is LlmTextBlock t) total += EstimateTokens(t.Text);
        return total;
    }

    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);
}
