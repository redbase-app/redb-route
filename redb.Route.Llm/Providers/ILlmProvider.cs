namespace redb.Route.Llm.Providers;

/// <summary>
/// Pure transport abstraction over an LLM API. Implementations are responsible
/// for protocol-level work only: serialise <see cref="LlmRequest"/>, call the
/// provider's HTTP/SDK, deserialise <see cref="LlmResponse"/>.
/// <para>
/// Tool dispatch, conversation persistence, budget enforcement and shadow-mode
/// belong to <see cref="Engine.IAgentEngine"/>, not here.
/// </para>
/// </summary>
public interface ILlmProvider
{
    /// <summary>Provider identifier ("anthropic", "openai", "stub", ...).</summary>
    string ProviderId { get; }

    /// <summary>Model identifier this provider was configured with.</summary>
    string ModelId { get; }

    /// <summary>Performs a single non-streaming completion call.</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>
    /// Performs a streaming completion. Yields content blocks (text deltas,
    /// tool-use begin/end, partial JSON) until <c>stop_reason</c> is received.
    /// Default implementation buffers <see cref="CompleteAsync"/> as a single chunk —
    /// override for true token-streaming.
    /// </summary>
    async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await CompleteAsync(request, ct).ConfigureAwait(false);
        yield return new LlmStreamChunk(response.Content, response.StopReason, response.Usage);
    }
}
