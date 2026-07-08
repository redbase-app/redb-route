using redb.Route.Llm.Tools;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Demonstrates the canonical pre-built tool from <c>redb.Route.Llm.Tools</c>:
/// <see cref="HttpFetchTool"/>. The chat route is unchanged from the basic
/// shape; the only addition is registering <c>http_fetch</c> in the tool
/// filter.
/// <para>
/// We deliberately script the provider with <see cref="FakeProvider"/> here:
/// free-tier models call HTTP tools inconsistently (URLs get truncated, schemas
/// drift), and this suite is about <i>DSL + tool wiring + real HTTP fetch</i>,
/// not about whether a 7B free model copies a URL byte-for-byte.
/// <see cref="ToolRouteTests"/> covers the live tool-loop end-to-end.
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class HttpFetchToolTests
{
    [Fact]
    public async Task HttpFetchTool_PluggedIntoRoute_FetchesAndReturnsBody()
    {
        // Local HTTP server stands in for an external API.
        using var server = new LocalHttpServer
        {
            ResponseBody = """{"secret":"unicorn"}"""
        };
        var url = server.BaseUrl + "secrets";

        // Scripted provider: turn 1 calls the tool, turn 2 returns the secret.
        var fake = new FakeProvider()
            .EnqueueToolUse("http_fetch", $"{{\"url\":\"{url}\"}}")
            .EnqueueText("the secret is unicorn");

        await using var host = LiveLlmHost.Build()
            .AddFactory("scripted", new LlmConnectionFactory
            {
                Provider = "fake",
                ModelId = fake.ModelId,
                PrebuiltProvider = fake
            });

        var fetch = new HttpFetchTool(new HttpFetchOptions
        {
            HostAllowlist = new[] { server.Host },
            Timeout = TimeSpan.FromSeconds(5)
        });

        await host.StartAsync(fetch, r =>
        {
            r.From("direct:agent")
                .To(LlmDsl.Factory("scripted").Tools("http_fetch").MaxIterations(4).AsUri())
                .To("mock:done");
        });

        await host.SendAsync("direct:agent", "Fetch the secret.");

        // 1. Tool was registered.
        host.ToolRegistry.Get("http_fetch").Should().NotBeNull();

        // 2. The local HTTP server was hit by the tool.
        server.RequestPaths.Should().ContainSingle().Which.Should().Contain("/secrets");

        // 3. The tool result flowed back into the next provider call.
        fake.CallCount.Should().Be(2);
        fake.CapturedRequests[^1].Messages
            .SelectMany(m => m.Content)
            .OfType<LlmToolResultBlock>()
            .Should().ContainSingle()
            .Which.OutputJson.Should().Contain("unicorn");

        // 4. The final assistant text rotated through to the mock sink.
        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().Contain("unicorn");
    }
}
