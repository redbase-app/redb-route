using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Plays a <see cref="FakeProvider"/> script as a stream: thinking and text word by word, then the last chunk with the
/// whole answer (<see cref="LlmStreamChunk.Response"/>). Counts which kind of call the engine made.
/// </summary>
public sealed class StreamingFakeProvider(FakeProvider script) : ILlmProvider
{
    private int _streamCalls;
    private int _plainCalls;

    /// <summary>Streamed calls made so far.</summary>
    public int StreamCalls => Volatile.Read(ref _streamCalls);

    /// <summary>Plain calls made so far.</summary>
    public int PlainCalls => Volatile.Read(ref _plainCalls);

    /// <inheritdoc />
    public string ProviderId => "fake-stream";

    /// <inheritdoc />
    public string ModelId => script.ModelId;

    /// <inheritdoc />
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _plainCalls);
        return script.CompleteAsync(request, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Interlocked.Increment(ref _streamCalls);
        var response = await script.CompleteAsync(request, ct);
        foreach (var block in response.Content)
        {
            if (block is LlmThinkingBlock thinking)
                foreach (var piece in Words(thinking.Text))
                    yield return new LlmStreamChunk([new LlmThinkingBlock(piece)], null, null);
            else if (block is LlmTextBlock text)
                foreach (var piece in Words(text.Text))
                    yield return new LlmStreamChunk([new LlmTextBlock(piece)], null, null);
        }

        yield return new LlmStreamChunk(
            [.. response.Content.OfType<LlmToolUseBlock>()], response.StopReason, response.Usage)
        {
            Response = response
        };
    }

    private static IEnumerable<string> Words(string text) =>
        Regex.Split(text, "(?<= )").Where(w => w.Length > 0);
}
