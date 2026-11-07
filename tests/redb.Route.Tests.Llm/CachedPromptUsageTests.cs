using System.Net;
using System.Text;

namespace redb.Route.Tests.Llm;

/// <summary>
/// A prompt served from the provider's cache reaches <see cref="LlmUsage"/> on the OpenAI-compatible path too
/// (issue #11). <c>LlmUsage.InputTokens</c> is the uncached remainder, not the whole prompt, so the cached part is
/// taken out of it: a cost calculator or an audit built on these numbers would otherwise bill a cached prefix at full
/// price. DeepSeek reports the split in its own fields, OpenAI in <c>prompt_tokens_details</c>; both mean the same.
/// </summary>
public sealed class CachedPromptUsageTests
{
    private sealed class FixedHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }

    private static OpenAiProvider Provider(string body, string mediaType = "application/json") =>
        new(new LlmConnectionFactory { Provider = "deepseek", ModelId = "deepseek-v4-pro", ApiKey = "sk-test" },
            new HttpClient(new FixedHandler(body, mediaType)));

    private static LlmRequest Request() => new() { Messages = [LlmMessage.User("hi")] };

    private static string Answer(string usage) => $$"""
        {"id":"chatcmpl-1","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{{usage}}}
        """;

    [Fact]
    public async Task DeepSeek_CachedPrefix_ReachesUsage()
    {
        // The numbers of the report: a ~170k system field served from cache, 63 tokens of it new. DeepSeek fills its
        // own pair and leaves the OpenAI-shaped cached_tokens at zero, so the zero must not win.
        var provider = Provider(Answer("""
            {"prompt_tokens":172095,"completion_tokens":11,"total_tokens":172106,
             "prompt_tokens_details":{"cached_tokens":0},
             "prompt_cache_hit_tokens":172032,"prompt_cache_miss_tokens":63}
            """));

        var usage = (await provider.CompleteAsync(Request())).Usage;

        usage.CacheReadInputTokens.Should().Be(172032, "this is the number that says caching works");
        usage.InputTokens.Should().Be(63, "InputTokens is what was billed at full price, not the whole prompt");
        usage.CacheCreationInputTokens.Should().Be(0, "DeepSeek does not bill cache creation separately");
        usage.OutputTokens.Should().Be(11);
    }

    [Fact]
    public async Task OpenAi_CachedPrefix_ReachesUsage()
    {
        var provider = Provider(Answer("""
            {"prompt_tokens":1000,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":896}}
            """));

        var usage = (await provider.CompleteAsync(Request())).Usage;

        usage.CacheReadInputTokens.Should().Be(896);
        usage.InputTokens.Should().Be(104, "the prompt minus what came from cache");
    }

    [Fact]
    public async Task WithoutCacheFields_TheWholePromptIsInput()
    {
        var provider = Provider(Answer("""{"prompt_tokens":38,"completion_tokens":5,"total_tokens":43}"""));

        var usage = (await provider.CompleteAsync(Request())).Usage;

        usage.InputTokens.Should().Be(38);
        usage.CacheReadInputTokens.Should().Be(0);
    }

    [Fact]
    public async Task StreamedCall_ReportsTheCachedPrefixToo()
    {
        var sse = string.Join("\n", [
            """data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"done"}}]}""",
            "",
            """data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "",
            """data: {"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":172095,"completion_tokens":11,"prompt_cache_hit_tokens":172032,"prompt_cache_miss_tokens":63}}""",
            "",
            "data: [DONE]"]) + "\n";
        var provider = Provider(sse, "text/event-stream");

        LlmResponse? answer = null;
        await foreach (var chunk in provider.StreamAsync(Request()))
            if (chunk.Response is not null) answer = chunk.Response;

        answer!.Usage.CacheReadInputTokens.Should().Be(172032, "the streamed answer carries the same usage");
        answer.Usage.InputTokens.Should().Be(63);
    }
}
