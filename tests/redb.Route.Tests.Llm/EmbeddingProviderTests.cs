using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using redb.Route.Llm;
using redb.Route.Llm.Providers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// <see cref="OpenAiEmbeddingProvider"/> request/response mapping, driven through a
/// stub <see cref="HttpMessageHandler"/> — no live key, deterministic.
/// </summary>
public sealed class EmbeddingProviderTests
{
    private sealed class StubHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private static LlmConnectionFactory Factory(string provider = "openai", string model = "text-embedding-3-small") =>
        new() { Provider = provider, ModelId = model, ApiKey = "sk-test" };

    [Fact]
    public async Task EmbedAsync_ParsesVectors_HonoursIndexOrder_AndShapesRequest()
    {
        // data arrives out of order (index 1 before index 0) — must be reordered to match inputs.
        var handler = new StubHandler(
            """{"data":[{"index":1,"embedding":[0.3,0.4]},{"index":0,"embedding":[0.1,0.2]}]}""");
        var provider = new OpenAiEmbeddingProvider(Factory(), new HttpClient(handler));

        var vectors = await provider.EmbedAsync(new[] { "alpha", "beta" });

        vectors.Should().HaveCount(2);
        vectors[0].Should().Equal(0.1f, 0.2f);   // index 0 → first input
        vectors[1].Should().Equal(0.3f, 0.4f);   // index 1 → second input

        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/embeddings");
        handler.LastRequest.Headers.Authorization!.ToString().Should().Be("Bearer sk-test");

        var body = JsonDocument.Parse(handler.LastBody!).RootElement;
        body.GetProperty("model").GetString().Should().Be("text-embedding-3-small");
        body.GetProperty("input").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task EmbedOneAsync_ReturnsFirstVector()
    {
        var handler = new StubHandler("""{"data":[{"index":0,"embedding":[1,2,3]}]}""");
        IEmbeddingProvider provider = new OpenAiEmbeddingProvider(Factory(), new HttpClient(handler));

        (await provider.EmbedOneAsync("hi")).Should().Equal(1f, 2f, 3f);
    }

    [Fact]
    public async Task EmbedAsync_EmptyInputs_ReturnsEmpty_WithoutHttpCall()
    {
        var handler = new StubHandler("{}");
        var provider = new OpenAiEmbeddingProvider(Factory(), new HttpClient(handler));

        (await provider.EmbedAsync(Array.Empty<string>())).Should().BeEmpty();
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public void Endpoint_ResolvesPerProvider()
    {
        new OpenAiEmbeddingProvider(Factory("mistral"), new HttpClient(new StubHandler("{}")))
            .ProviderId.Should().Be("mistral");
        // base-url resolution is shared with OpenAiProvider; a smoke check that the
        // provider id is lower-cased and carried through.
    }

    [Fact]
    public async Task EmbedAsync_HttpError_Throws()
    {
        var handler = new StubHandler("""{"error":"bad model"}""", HttpStatusCode.BadRequest);
        var provider = new OpenAiEmbeddingProvider(Factory(), new HttpClient(handler));

        var act = async () => await provider.EmbedAsync(new[] { "x" });
        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
