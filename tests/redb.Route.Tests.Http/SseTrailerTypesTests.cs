using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Processors;

namespace redb.Route.Tests.Http;

/// <summary>
/// The SSE <c>event: done</c> trailer carries the summary headers a streaming producer left on the
/// reply. They used to go out as strings through a culture-dependent <c>ToString()</c>: a token count
/// arrived as <c>"7"</c> instead of <c>7</c>, and on a Russian-locale worker a decimal cost arrived as
/// <c>"0,0123"</c> — a number rendered with a comma, inside quotes. The Llm documentation shows plain
/// numbers, so the contract and the code had already parted ways. Reported 2026-09-25.
/// <para>
/// Runs in the HttpServer collection, so setting the process default culture cannot reach a test
/// running beside it.
/// </para>
/// </summary>
[Collection("HttpServer")]
public class SseTrailerTypesTests
{
    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static async IAsyncEnumerable<string> OneChunk(IMessage reply)
    {
        yield return "alpha";
        await Task.Yield();

        // Late-bound summary headers, exactly as a streaming producer leaves them.
        reply.Headers["llm.tokens.in"] = 7;
        reply.Headers["llm.tool.iterations"] = 2;
        reply.Headers["llm.cost.usd"] = 0.0123m;
        reply.Headers["llm.stop_reason"] = "EndTurn";
    }

    private static async Task<JsonDocument> TrailerOf(int port, HttpClient client)
    {
        using var resp = await client.GetAsync($"http://127.0.0.1:{port}/sse", HttpCompletionOption.ResponseHeadersRead);
        var body = await resp.Content.ReadAsStringAsync();

        var trailer = body[body.IndexOf("event: done", StringComparison.Ordinal)..];
        var dataLine = trailer.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal));
        return JsonDocument.Parse(dataLine["data: ".Length..]);
    }

    [Fact]
    public async Task Numbers_stay_numbers_and_do_not_pick_up_the_workers_locale()
    {
        var previousCulture = CultureInfo.DefaultThreadCurrentCulture;
        var port = FreePort();
        try
        {
            // A worker in Russia: a decimal renders with a comma under ToString().
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("ru-RU");

            await using var context = new RouteContext();
            context.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });
            var consumer = context.GetEndpoint($"http://127.0.0.1:{port}/sse?inOut=true")
                .CreateConsumer(new DelegateProcessor(ex =>
                {
                    ex.Out = ex.In.Clone();
                    ex.Out.ContentType = "text/event-stream";
                    ex.Out.Body = OneChunk(ex.Out);
                }));
            await consumer.Start();

            using var client = new HttpClient();
            using var json = await TrailerOf(port, client);
            var root = json.RootElement;

            root.GetProperty("llm.tokens.in").ValueKind.Should().Be(JsonValueKind.Number);
            root.GetProperty("llm.tokens.in").GetInt32().Should().Be(7);
            root.GetProperty("llm.tool.iterations").GetInt32().Should().Be(2);

            // The one that used to carry the locale out of the process.
            root.GetProperty("llm.cost.usd").ValueKind.Should().Be(JsonValueKind.Number);
            root.GetProperty("llm.cost.usd").GetDecimal().Should().Be(0.0123m);

            // A string stays a string — the fix is about types, not about quoting everything.
            root.GetProperty("llm.stop_reason").ValueKind.Should().Be(JsonValueKind.String);
            root.GetProperty("llm.stop_reason").GetString().Should().Be("EndTurn");

            await consumer.Stop();
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = previousCulture;
        }
    }
}
