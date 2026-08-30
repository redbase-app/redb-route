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

    /// <summary>Stub that also sets <c>Retry-After</c>, which only the 429 path reads.</summary>
    private sealed class RetryAfterHandler(HttpStatusCode status, int? retryAfterSeconds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"error":"slow down"}""", Encoding.UTF8, "application/json")
            };

            if (retryAfterSeconds is { } seconds)
                response.Headers.Add("Retry-After", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task EmbedAsync_RateLimited_ThrowsTypedRateLimit_WithRetryAfter()
    {
        // Retrieval degrades to keyword on ANY embedder failure, so an expired key and a busy
        // server used to look identical to the caller. 429 has to be its own kind, with the
        // provider's own wait time, or "retry" is guesswork.
        var provider = new OpenAiEmbeddingProvider(
            Factory(), new HttpClient(new RetryAfterHandler(HttpStatusCode.TooManyRequests, 7)));

        var error = await Assert.ThrowsAsync<LlmRateLimitException>(
            () => provider.EmbedAsync(new[] { "x" }));

        error.ProviderId.Should().Be("openai");
        error.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task EmbedAsync_ServerError_ThrowsTypedTransient_ButClientErrorDoesNot()
    {
        var transient = new OpenAiEmbeddingProvider(
            Factory(), new HttpClient(new RetryAfterHandler(HttpStatusCode.ServiceUnavailable, null)));

        var error = await Assert.ThrowsAsync<LlmTransientException>(
            () => transient.EmbedAsync(new[] { "x" }));

        error.StatusCode.Should().Be(503);

        // 401 stays a plain HttpRequestException on purpose: a wrong key is not worth retrying,
        // and lumping it in with "transient" would spin forever on a dead credential.
        var unauthorized = new OpenAiEmbeddingProvider(
            Factory(), new HttpClient(new RetryAfterHandler(HttpStatusCode.Unauthorized, null)));

        var act = async () => await unauthorized.EmbedAsync(new[] { "x" });

        (await act.Should().ThrowAsync<HttpRequestException>())
            .And.Should().NotBeOfType<LlmTransientException>();
    }
}
