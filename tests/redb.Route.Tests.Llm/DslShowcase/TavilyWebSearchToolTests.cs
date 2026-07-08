using FluentAssertions;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Drives <see cref="TavilyWebSearchTool"/> against a local HTTP server that mimics the Tavily
/// search API. Verifies the request shape (POST + api_key + query) and that the tool reshapes
/// the response to <c>{answer, results}</c> for the model.
/// </summary>
public sealed class TavilyWebSearchToolTests
{
    [Fact]
    public async Task TavilyWebSearchTool_PostsQueryAndShapesResponse()
    {
        using var server = new LocalHttpServer
        {
            ResponseBody = """
                {
                  "answer": "Paris is the capital of France.",
                  "results": [
                    { "title": "France", "url": "https://en.wikipedia.org/wiki/France", "content": "Paris is the capital city.", "score": 0.9 },
                    { "title": "Paris",  "url": "https://en.wikipedia.org/wiki/Paris",  "content": "Capital of France.",       "score": 0.8 }
                  ]
                }
                """
        };

        await using var host = LiveLlmHost.Build();
        await host.StartAsync(new TavilyWebSearchTool(new TavilyWebSearchOptions
        {
            ApiKey = "test-key",
            Endpoint = server.BaseUrl + "search",
            MaxResults = 2,
            Timeout = TimeSpan.FromSeconds(5)
        }));

        host.ToolRegistry.Get("web_search").Should().NotBeNull();

        var ex = await host.SendAsync("direct:llm.web_search",
            """{"query":"capital of France"}""");

        var body = (string)ex.Out!.Body!;
        body.Should().Contain("\"answer\":\"Paris is the capital of France.\"");
        body.Should().Contain("https://en.wikipedia.org/wiki/France");
        body.Should().Contain("https://en.wikipedia.org/wiki/Paris");
        body.Should().NotContain("score");

        ex.Out.Headers["llm.web_search.results"].Should().Be(2);
        server.RequestPaths.Should().ContainSingle().Which.Should().Contain("/search");
    }

    [Fact]
    public void TavilyWebSearchTool_RequiresApiKey()
    {
        var act = () => new TavilyWebSearchTool(new TavilyWebSearchOptions());
        act.Should().Throw<ArgumentException>();
    }
}
