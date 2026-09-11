using redb.Route.Llm.Providers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Default base URLs are contracts with other people's servers, so the divergent one is pinned.
/// DeepSeek's documented base is the ROOT — unlike every /v1 neighbour in the table — verified
/// 2026-09-10 against api-docs.deepseek.com after the V4.1-Flash release: /v1 still answers as a
/// legacy alias (401, not 404) but vanished from the docs, so the canonical form is the durable
/// one. If a future edit "harmonises" it back to /v1, this test is the tripwire.
/// </summary>
public sealed class OpenAiBaseUrlTests
{
    [Fact]
    public void DeepSeek_base_is_the_documented_root_not_v1()
    {
        OpenAiProvider.ResolveDefaultBaseUrl("deepseek")
            .Should().Be(new Uri("https://api.deepseek.com/"));
    }

    [Fact]
    public void The_v1_neighbours_stay_on_v1()
    {
        OpenAiProvider.ResolveDefaultBaseUrl("openai").Should().Be(new Uri("https://api.openai.com/v1/"));
        OpenAiProvider.ResolveDefaultBaseUrl("mistral").Should().Be(new Uri("https://api.mistral.ai/v1/"));
    }
}
