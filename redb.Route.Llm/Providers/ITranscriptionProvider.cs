namespace redb.Route.Llm.Providers;

/// <summary>
/// Pure transport over a speech-to-text API — turns recorded audio into text. The
/// third sibling of <see cref="ILlmProvider"/> and <see cref="IEmbeddingProvider"/>:
/// implementations do protocol work only (encode the upload, HTTP, decode the
/// answer). Deciding whether a recording is worth transcribing, what to do with an
/// empty result and who pays for it belongs to the route, not here.
/// </summary>
public interface ITranscriptionProvider
{
    /// <summary>Provider identifier ("openai", "groq", "local", ...).</summary>
    string ProviderId { get; }

    /// <summary>Speech model id this provider was configured with.</summary>
    string ModelId { get; }

    /// <summary>Transcribes one recording.</summary>
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default);
}

/// <summary>
/// One recording to transcribe.
/// </summary>
/// <param name="Audio">The encoded audio, exactly as it arrived from its transport.</param>
/// <param name="FileName">
/// Name to upload the audio under, e.g. <c>voice.oga</c>. Not cosmetic: speech
/// endpoints pick the demuxer by extension, and an <c>.ogg/opus</c> recording sent as
/// <c>file</c> is a decode error on most servers rather than a transcription.
/// </param>
/// <param name="Language">
/// ISO-639-1 hint (<c>"ru"</c>). Null lets the model detect it — more forgiving on
/// mixed speech, measurably worse on short recordings, where a couple of words in one
/// language are routinely detected as another.
/// </param>
/// <param name="Prompt">
/// Optional decoding hint, in the model's own words: names and terms it would not
/// otherwise spell right. Hints can also displace what was actually said, so this is
/// deliberately something a caller opts into.
/// </param>
public readonly record struct TranscriptionRequest(
    ReadOnlyMemory<byte> Audio,
    string FileName,
    string? Language = null,
    string? Prompt = null);

/// <summary>
/// What came back.
/// </summary>
/// <param name="Text">
/// The recognised text, trimmed. Empty when the recording holds no speech — silence
/// and music transcribe to nothing, and that is an answer, not a failure.
/// </param>
/// <param name="Language">Language the provider reports it heard, when it reports one.</param>
/// <param name="DurationSeconds">
/// Length of the recording, when the provider reports it. The unit speech APIs bill in,
/// so it is the number to record against a run — but it is optional in the response,
/// which is why a caller that must have it should take it from its own transport instead.
/// </param>
public readonly record struct TranscriptionResult(
    string Text,
    string? Language = null,
    double? DurationSeconds = null);
