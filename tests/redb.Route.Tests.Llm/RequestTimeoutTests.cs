using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace redb.Route.Tests.Llm;

/// <summary>
/// <see cref="LlmConnectionFactory.RequestTimeoutMs"/> limits the whole call: waiting for the response AND reading it,
/// on every provider, whatever client sends it. It used to be the client's <c>HttpClient.Timeout</c>, which with
/// <c>ResponseHeadersRead</c> stops at the headers: a server that answered with headers at once and then held the
/// body (DeepSeek does) was never limited, and a client passed in by the caller was never limited by the factory at
/// all. A streamed call is limited the same way, the stream included; <see cref="LlmConnectionFactory.StreamIdleTimeoutMs"/>
/// limits a stream's silence, counting only the waits on the provider.
/// </summary>
public sealed class RequestTimeoutTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    /// <summary>Longer than any test waits: a server that never finishes.</summary>
    private static readonly TimeSpan Never = TimeSpan.FromSeconds(30);

    /// <summary>A test that would hang without the limit fails here instead.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static LlmConnectionFactory Factory(string provider, string model, int timeoutMs = 1000, int? idleMs = null) => new()
    {
        Name = "slow",
        Provider = provider,
        ModelId = model,
        ApiKey = "sk-test",
        RequestTimeoutMs = timeoutMs,
        StreamIdleTimeoutMs = idleMs,
    };

    /// <summary>The caller's client, without a timeout of its own: only the factory's limit can end the call.</summary>
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    private static LlmRequest Request() => new() { Messages = [LlmMessage.User("hi")] };

    /// <summary>Answers with headers at once and holds the body back.</summary>
    private sealed class SlowBodyHandler(string body, TimeSpan bodyDelay, string mediaType = "application/json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(new DelayedStream(Encoding.UTF8.GetBytes(body), bodyDelay));
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Does not answer at all until the delay passes.</summary>
    private sealed class SlowHeadersHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    /// <summary>A body whose first read waits for the delay; the reader's token can end the wait.</summary>
    private sealed class DelayedStream(byte[] data, TimeSpan delay) : Stream
    {
        private int _position;
        private bool _waited;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_waited)
            {
                await Task.Delay(delay, cancellationToken);
                _waited = true;
            }

            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // The OpenAI stream loop asks StreamReader.EndOfStream, which reads synchronously.
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void ShouldBeTheFactoryLimit(
        Exception? thrown, string providerId, Stopwatch elapsed, string because,
        LlmTimeoutKind kind = LlmTimeoutKind.Call, TimeSpan? limit = null)
    {
        var timeout = thrown.Should().BeOfType<LlmTimeoutException>(because).Subject;
        timeout.ProviderId.Should().Be(providerId);
        timeout.FactoryName.Should().Be("slow", "the message must say whose limit ran out");
        timeout.Kind.Should().Be(kind);
        timeout.Limit.Should().Be(limit ?? Limit);
        timeout.Message.Should().Contain(kind == LlmTimeoutKind.Call ? "RequestTimeoutMs" : "StreamIdleTimeoutMs",
            "the message names the option to raise");
        elapsed.Elapsed.Should().BeLessThan(Guard);
    }

    /// <summary>Answers at once with headers, then writes the lines one by one with a pause before each but the first.</summary>
    private sealed class TrickleHandler(IReadOnlyList<string> lines, TimeSpan interval, TimeSpan silenceAfter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(new TrickleStream(lines, interval, silenceAfter));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>A body that releases one line per <c>interval</c>, then keeps silent for <c>silenceAfter</c> before it ends.</summary>
    private sealed class TrickleStream(IReadOnlyList<string> lines, TimeSpan interval, TimeSpan silenceAfter) : Stream
    {
        private int _next;
        private byte[] _current = [];
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _current.Length)
            {
                if (_next == lines.Count)
                {
                    if (silenceAfter > TimeSpan.Zero) await Task.Delay(silenceAfter, cancellationToken);
                    return 0;
                }

                if (_next > 0) await Task.Delay(interval, cancellationToken);
                _current = Encoding.UTF8.GetBytes(lines[_next++] + "\n");
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private const string OpenAiPiece = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x\"}}]}";
    private const string OpenAiFinish = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}";

    /// <summary>An OpenAI-compatible stream of <paramref name="pieces"/> text pieces that ends as a model finishes.</summary>
    private static List<string> OpenAiLines(int pieces, string piece = OpenAiPiece)
    {
        var lines = new List<string>();
        for (var i = 0; i < pieces; i++) lines.AddRange([piece, ""]);
        lines.AddRange([OpenAiFinish, "", "data: [DONE]"]);
        return lines;
    }

    private static async Task<int> DrainAsync(ILlmProvider provider, TimeSpan pauseBetweenPieces = default)
    {
        var chunks = 0;
        await foreach (var _ in provider.StreamAsync(Request()))
        {
            chunks++;
            if (pauseBetweenPieces > TimeSpan.Zero) await Task.Delay(pauseBetweenPieces);
        }
        return chunks;
    }

    [Fact]
    public async Task OpenAi_BodyThatDoesNotArrive_IsCutAtTheFactoryLimit()
    {
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash"),
            Client(new SlowBodyHandler("""{"choices":[{"message":{"role":"assistant","content":"done"}}]}""", Never)));

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => provider.CompleteAsync(Request()).WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "deepseek", sw,
            "the headers came at once and the body never did: the limit covers reading the answer, not only waiting for it");
    }

    [Fact]
    public async Task OpenAi_HeadersThatDoNotArrive_AreCutAtTheFactoryLimit()
    {
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Client(new SlowHeadersHandler(Never)));

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => provider.CompleteAsync(Request()).WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "deepseek", sw,
            "the factory's limit holds for a client passed in by the caller, not only for the package's own");
    }

    [Fact]
    public async Task Anthropic_BodyThatDoesNotArrive_IsCutAtTheFactoryLimit()
    {
        var provider = new AnthropicProvider(Factory("anthropic", "claude-opus-4-8"),
            Client(new SlowBodyHandler("""{"content":[{"type":"text","text":"done"}],"stop_reason":"end_turn"}""", Never)));

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => provider.CompleteAsync(Request()).WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "anthropic", sw, "every provider keeps the same contract");
    }

    [Fact]
    public async Task Transcription_BodyThatDoesNotArrive_IsCutAtTheFactoryLimit()
    {
        var provider = new OpenAiTranscriptionProvider(Factory("openai", "whisper-1"),
            Client(new SlowBodyHandler("""{"text":"hi"}""", Never)));

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => provider
            .TranscribeAsync(new TranscriptionRequest(new byte[] { 0x4F, 0x67, 0x67, 0x53 }, "voice.oga"))
            .WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "openai", sw, "a long recording is limited the same way as a chat call");
    }

    [Fact]
    public async Task Embeddings_BodyThatDoesNotArrive_AreCutAtTheFactoryLimit()
    {
        var provider = new OpenAiEmbeddingProvider(Factory("openai", "text-embedding-3-small"),
            Client(new SlowBodyHandler("""{"data":[{"embedding":[0.1]}]}""", Never)));

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => provider.EmbedAsync(["hi"]).WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "openai", sw, "every provider keeps the same contract");
    }

    [Fact]
    public async Task Stream_ResponseThatDoesNotStart_IsCutAtTheFactoryLimit()
    {
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Client(new SlowHeadersHandler(Never)));

        async Task Drain()
        {
            await foreach (var _ in provider.StreamAsync(Request()))
            {
            }
        }

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => Drain().WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "deepseek", sw, "a stream that never starts is limited like any call");
    }

    [Fact]
    public async Task Stream_LongerThanTheLimit_IsCut()
    {
        // Pieces keep arriving, a quarter-second apart, for three seconds: the stream is alive, and still the call
        // outlasts RequestTimeoutMs, which covers the whole call, streamed or not.
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash"),
            Client(new TrickleHandler(OpenAiLines(12), TimeSpan.FromMilliseconds(125), TimeSpan.Zero)));

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => DrainAsync(provider).WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "deepseek", sw, "RequestTimeoutMs limits the whole call, the stream included");
    }

    [Fact]
    public async Task Anthropic_Stream_LongerThanTheLimit_IsCut()
    {
        var lines = new List<string>
        {
            "event: message_start",
            """data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"usage":{"input_tokens":1,"output_tokens":1}}}""",
            ""
        };
        for (var i = 0; i < 12; i++) lines.AddRange(["event: ping", """data: {"type":"ping"}""", ""]);
        lines.AddRange([
            "event: message_delta",
            """data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}""",
            "",
            "event: message_stop",
            """data: {"type":"message_stop"}"""]);
        var provider = new AnthropicProvider(Factory("anthropic", "claude-opus-4-8"),
            Client(new TrickleHandler(lines, TimeSpan.FromMilliseconds(80), TimeSpan.Zero)));

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => DrainAsync(provider).WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "anthropic", sw, "every provider limits its stream the same way");
    }

    [Fact]
    public async Task Stream_SilentLongerThanTheIdleLimit_IsCut()
    {
        var idle = TimeSpan.FromMilliseconds(400);
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash", timeoutMs: 60_000, idleMs: 400),
            Client(new TrickleHandler([OpenAiPiece, ""], TimeSpan.Zero, Never)));

        var sw = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => DrainAsync(provider).WaitAsync(Guard));

        ShouldBeTheFactoryLimit(thrown, "deepseek", sw,
            "one piece came and then nothing: a stream silent past StreamIdleTimeoutMs is stuck",
            LlmTimeoutKind.StreamIdle, idle);
    }

    [Fact]
    public async Task Stream_KeepAliveComments_HoldOffTheIdleLimit()
    {
        // Nothing but keep-alive comments for 1.6 s, with a 600 ms silence limit: the provider is there, only slow.
        var lines = new List<string>();
        for (var i = 0; i < 8; i++) lines.Add(": keep-alive");
        lines.AddRange(OpenAiLines(1));
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash", timeoutMs: 60_000, idleMs: 600),
            Client(new TrickleHandler(lines, TimeSpan.FromMilliseconds(200), TimeSpan.Zero)));

        var chunks = await DrainAsync(provider).WaitAsync(Guard);

        chunks.Should().Be(2, "one text piece and the last chunk: the comments count as the provider being there");
    }

    [Fact]
    public async Task Stream_SlowReader_IsNotSilence()
    {
        // The provider sends everything at once; the reader takes 300 ms over each piece. The silence limit counts
        // the waits on the provider, not the reader's own time.
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash", timeoutMs: 60_000, idleMs: 200),
            Client(new TrickleHandler(OpenAiLines(3), TimeSpan.Zero, TimeSpan.Zero)));

        var chunks = await DrainAsync(provider, pauseBetweenPieces: TimeSpan.FromMilliseconds(300)).WaitAsync(Guard);

        chunks.Should().Be(4);
    }

    [Fact]
    public void StreamIdleLimit_IsOffByDefault_AndPositiveWhenSet()
    {
        new LlmConnectionFactory().StreamIdleTimeoutMs.Should().BeNull(
            "some reasoning models stream nothing for minutes before their first token");

        var act = () => new LlmConnectionFactory { StreamIdleTimeoutMs = 0 };
        act.Should().Throw<ArgumentOutOfRangeException>("zero is not \"off\": off is unset");
    }

    [Fact]
    public async Task CallerCancellation_StaysACancellation()
    {
        var provider = new OpenAiProvider(Factory("deepseek", "deepseek-flash", timeoutMs: 60_000),
            Client(new SlowHeadersHandler(Never)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var thrown = await Record.ExceptionAsync(() => provider.CompleteAsync(Request(), cts.Token).WaitAsync(Guard));

        thrown.Should().BeAssignableTo<OperationCanceledException>(
            "a caller that stopped the call must not be told the model was too slow");
    }
}
