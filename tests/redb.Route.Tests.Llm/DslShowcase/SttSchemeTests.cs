using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Transcription;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// <c>stt://&lt;factory&gt;</c> scheme — transcribes the exchange body via the named
/// connection factory. Driven with a stub <see cref="SttComponent.ProviderFactory"/>
/// (no live endpoint): recorded audio in, text out.
/// </summary>
public sealed class SttSchemeTests
{
    /// <summary>Deterministic stub: reports back what it was handed.</summary>
    private sealed class StubStt(string text = "распознанный текст") : ITranscriptionProvider
    {
        public TranscriptionRequest? Last { get; private set; }
        public string ProviderId => "stub";
        public string ModelId => "stub-speech";

        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(new TranscriptionResult(text, Language: "ru"));
        }
    }

    private static (RouteContext Ctx, ProducerTemplate Producer) Host(SttComponent component)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var ctx = new RouteContext(sp, contextId: "stt-scheme-test");
        ctx.AddToRegistry("speech", new LlmConnectionFactory
        {
            Provider = "local",
            ModelId = "large-v3",
            BaseUrl = new Uri("http://127.0.0.1:8083/v1/")
        });
        ctx.AddComponent(component);
        var producer = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), producer);
        return (ctx, producer);
    }

    /// <summary>Drives one exchange through <paramref name="uri"/> and hands back what came out.</summary>
    private static async Task<IExchange> RunAsync(
        ProducerTemplate producer, string uri, Action<IExchange> prepare)
    {
        var exchange = new Exchange(new Message(null));
        prepare(exchange);
        return await producer.RequestAsync(uri, exchange);
    }

    private static IMessage Result(IExchange exchange) => exchange.Out ?? exchange.In;

    [Fact]
    public async Task RecordedBytes_BecomeText()
    {
        var (ctx, producer) = Host(new SttComponent { ProviderFactory = _ => new StubStt() });
        ctx.AddRoutes(r => r.From("direct:v1").To("stt://speech"));
        await ctx.Start();
        producer.Start();

        var result = await producer.RequestBody("direct:v1", new byte[] { 1, 2, 3 });

        result.Should().Be("распознанный текст");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task AStreamBodyIsBufferedForTheUpload()
    {
        // Multipart needs a length; a chunked body is not something a speech endpoint takes.
        var stub = new StubStt();
        var (ctx, producer) = Host(new SttComponent { ProviderFactory = _ => stub });
        ctx.AddRoutes(r => r.From("direct:v2").To("stt://speech"));
        await ctx.Start();
        producer.Start();

        await producer.RequestBody("direct:v2", new MemoryStream([9, 8, 7]));

        stub.Last!.Value.Audio.ToArray().Should().Equal(9, 8, 7);

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task TheFileNameFollowsTheContentType()
    {
        // The transport that delivered the recording already knows the format. Deriving the
        // upload name from it is what lets a voice route work with no configuration at all —
        // and getting it wrong is a decode error, not a worse transcription.
        var stub = new StubStt();
        var (ctx, producer) = Host(new SttComponent { ProviderFactory = _ => stub });
        ctx.AddRoutes(r => r.From("direct:v3").To("stt://speech"));
        await ctx.Start();
        producer.Start();

        await RunAsync(producer, "direct:v3", exchange =>
        {
            exchange.In.Body = new byte[] { 1 };
            exchange.In.ContentType = "audio/ogg";
        });

        stub.Last!.Value.FileName.Should().Be("audio.oga");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task HeadersOverrideTheEndpointOptions()
    {
        var stub = new StubStt();
        var (ctx, producer) = Host(new SttComponent { ProviderFactory = _ => stub });
        ctx.AddRoutes(r => r.From("direct:v4").To("stt://speech?language=en&fileName=fixed.wav"));
        await ctx.Start();
        producer.Start();

        await RunAsync(producer, "direct:v4", exchange =>
        {
            exchange.In.Body = new byte[] { 1 };
            exchange.In.Headers[LlmHeaders.TranscriptionLanguage] = "ru";
            exchange.In.Headers[LlmHeaders.TranscriptionFileName] = "voice.oga";
        });

        stub.Last!.Value.Language.Should().Be("ru");
        stub.Last!.Value.FileName.Should().Be("voice.oga");

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task TheAnswerIsDescribedByLength_NeverByContent()
    {
        const string recognised = "шесть слов, а может и меньше";
        var stub = new StubStt(recognised);
        var (ctx, producer) = Host(new SttComponent { ProviderFactory = _ => stub });
        ctx.AddRoutes(r => r.From("direct:v5").To("stt://speech"));
        await ctx.Start();
        producer.Start();

        var result = Result(await RunAsync(producer, "direct:v5", e => e.In.Body = new byte[] { 1 }));

        result.Headers[LlmHeaders.TranscriptionChars].Should().Be(recognised.Length);
        result.Headers[LlmHeaders.TranscriptionModel].Should().Be("stub-speech");
        result.Headers[LlmHeaders.TranscriptionProvider].Should().Be("stub");
        result.Headers[LlmHeaders.TranscriptionLanguage].Should().Be("ru");

        // A header travels into logs, audits and dead letters; a transcription is the
        // speaker's own words. Nothing here repeats them.
        result.Headers.Values
            .Select(v => v?.ToString() ?? string.Empty)
            .Should().NotContain(s => s.Contains("слов"));

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task ANonAudioBodySaysWhatToPutThere()
    {
        var (ctx, producer) = Host(new SttComponent { ProviderFactory = _ => new StubStt() });
        ctx.AddRoutes(r => r.From("direct:v6").To("stt://speech"));
        await ctx.Start();
        producer.Start();

        var act = async () => await producer.RequestBody("direct:v6", "это текст, а не запись");

        (await act.Should().ThrowAsync<Exception>()).And.ToString().Should().Contain("telegram://download");

        await ctx.DisposeAsync();
    }

    [Fact]
    public void TheSchemeIsProducerOnly()
    {
        var component = new SttComponent();
        var endpoint = component.CreateEndpoint(EndpointUriParser.Parse("stt://speech"));

        var act = () => endpoint.CreateConsumer(Substitute.For<IProcessor>());

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void AnEndpointWithoutAFactoryRefusesToStart()
    {
        var component = new SttComponent();

        var act = () => component.CreateEndpoint(EndpointUriParser.Parse("stt://"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*connection factory*");
    }
}
