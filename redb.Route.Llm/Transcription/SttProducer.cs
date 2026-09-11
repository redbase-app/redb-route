using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Transcription;

/// <summary>
/// Speech-to-text producer: transcribes the exchange body and writes the recognised
/// text to <c>Out.Body</c>.
/// <para>
/// An empty result is a result, not a failure: silence and music transcribe to an
/// empty string, and the route decides what to say about that. The producer reports it
/// as <see cref="LlmHeaders.TranscriptionChars"/> = 0 rather than throwing, so
/// "nothing was said" and "recognition broke" stay distinguishable — the second one
/// throws.
/// </para>
/// </summary>
public sealed class SttProducer : ConnectableProducer
{
    private readonly SttEndpoint _endpoint;
    private readonly SttEndpointOptions _options;
    private ITranscriptionProvider? _provider;

    /// <summary>Creates the producer.</summary>
    public SttProducer(SttEndpoint endpoint, SttEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"stt:{_endpoint.FactoryName}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(exchange);

        var provider = ResolveProvider();
        var audio = await ReadAudioAsync(exchange, ct).ConfigureAwait(false);

        var result = await provider.TranscribeAsync(
            new TranscriptionRequest(
                audio,
                ResolveFileName(exchange),
                Language: ResolveOptional(exchange, LlmHeaders.TranscriptionLanguage, _options.Language),
                Prompt: ResolveOptional(exchange, LlmHeaders.TranscriptionPrompt, _options.Prompt)),
            ct).ConfigureAwait(false);

        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = result.Text;
        exchange.Out.ContentType = "text/plain";

        exchange.Out.Headers[LlmHeaders.TranscriptionModel] = provider.ModelId;
        exchange.Out.Headers[LlmHeaders.TranscriptionProvider] = provider.ProviderId;

        // Length and not the text: a header travels into logs, audits and dead letters, and
        // a transcription is the person's own words. How much was recognised answers "did it
        // work" without repeating what was said.
        exchange.Out.Headers[LlmHeaders.TranscriptionChars] = result.Text.Length;

        if (result.Language is { Length: > 0 } language)
            exchange.Out.Headers[LlmHeaders.TranscriptionLanguage] = language;

        if (result.DurationSeconds is { } duration)
            exchange.Out.Headers[LlmHeaders.TranscriptionDuration] = duration;

        Logger?.LogDebug(
            "stt: transcribed {Bytes} bytes into {Chars} characters via {Provider}/{Model}",
            audio.Length, result.Text.Length, provider.ProviderId, provider.ModelId);
    }

    /// <summary>
    /// Reads the recording off the exchange. A <see cref="Stream"/> is buffered because the
    /// upload needs a length — speech endpoints take a multipart part, not a chunked one.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>> ReadAudioAsync(IExchange exchange, CancellationToken ct)
    {
        switch (exchange.In.Body)
        {
            case byte[] bytes:
                return bytes;

            case ReadOnlyMemory<byte> memory:
                return memory;

            case Stream stream:
            {
                using var buffer = new MemoryStream();
                if (stream.CanSeek) stream.Position = 0;
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                return buffer.ToArray();
            }

            default:
                throw new InvalidOperationException(
                    $"stt://: unsupported body type '{exchange.In.Body?.GetType().Name ?? "null"}'. " +
                    "Expected recorded audio as byte[], ReadOnlyMemory<byte> or Stream — for a Telegram " +
                    "voice note that is what 'telegram://download' puts on the exchange.");
        }
    }

    /// <summary>
    /// Name to upload under: header, then option, then the exchange content type, then a
    /// plain default. The content-type step is what lets a transport that already knows the
    /// format ("audio/ogg") get it right with no configuration at all.
    /// </summary>
    private string ResolveFileName(IExchange exchange)
    {
        if (ResolveOptional(exchange, LlmHeaders.TranscriptionFileName, _options.FileName) is { Length: > 0 } name)
            return name;

        return exchange.In.ContentType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "audio/ogg" or "audio/opus" or "audio/x-opus+ogg" => "audio.oga",
            "audio/mpeg" or "audio/mp3" => "audio.mp3",
            "audio/mp4" or "audio/x-m4a" => "audio.m4a",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "audio.wav",
            "audio/flac" or "audio/x-flac" => "audio.flac",
            "audio/webm" or "video/webm" => "audio.webm",
            "video/mp4" => "audio.mp4",
            _ => "audio.ogg"
        };
    }

    private static string? ResolveOptional(IExchange exchange, string header, string? fallback)
    {
        if (exchange.In.Headers.TryGetValue(header, out var value)
            && value?.ToString() is { Length: > 0 } fromHeader)
            return fromHeader;

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private ITranscriptionProvider ResolveProvider()
    {
        if (_provider is not null) return _provider;

        var component = (SttComponent)_endpoint.Component;
        var factory = _endpoint.ResolvedFactory
            ?? component.Context?.GetFromRegistry<LlmConnectionFactory>(_endpoint.FactoryName)
            ?? throw new InvalidOperationException(
                $"stt://: connection factory '{_endpoint.FactoryName}' is not registered in the route context.");

        _provider = component.ProviderFactory(factory);
        return _provider;
    }
}
