using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Tests.Llm.Mcp.Framing;

/// <summary>
/// Pure unit coverage of <see cref="McpClientBase"/>: response demux, monotonic
/// id generation, notification dispatch, transport-failure unwind, cancellation
/// emitting <c>notifications/cancelled</c>. No process spawning, no transport.
/// </summary>
public sealed class StdioJsonRpcFramingTests
{
    [Fact]
    public async Task CallTool_ResponseDelivered_OnIdMatch()
    {
        var client = new TestableMcpClient("test");

        // Drive a tools/call without going through Initialize; capture id from the request frame.
        var callTask = client.CallToolAsync("get_x", new JsonObject { ["a"] = 1 }, CancellationToken.None);

        await WaitForFrameAsync(client, 0);
        var requestFrame = client.SentFrames[0];
        var id = JsonNode.Parse(requestFrame)!["id"]!.GetValue<long>();

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hello" }),
                ["isError"] = false,
            },
        };
        client.InjectFrame(response.ToJsonString());

        var result = await callTask;
        result.IsError.Should().BeFalse();
        result.Content!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("hello");
    }

    [Fact]
    public async Task CallTool_OutOfOrderResponses_AreCorrectlyDemuxed()
    {
        var client = new TestableMcpClient("test");

        var task1 = client.CallToolAsync("first", null, CancellationToken.None);
        var task2 = client.CallToolAsync("second", null, CancellationToken.None);

        await WaitForFrameAsync(client, 1);
        var id1 = JsonNode.Parse(client.SentFrames[0])!["id"]!.GetValue<long>();
        var id2 = JsonNode.Parse(client.SentFrames[1])!["id"]!.GetValue<long>();
        id2.Should().BeGreaterThan(id1);

        // Reply to #2 before #1 — both must resolve correctly.
        client.InjectFrame(BuildResultFrame(id2, "second-result"));
        client.InjectFrame(BuildResultFrame(id1, "first-result"));

        (await task1).Content!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("first-result");
        (await task2).Content!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("second-result");
    }

    [Fact]
    public async Task CallTool_ServerError_RaisesMcpException()
    {
        var client = new TestableMcpClient("test");

        var task = client.CallToolAsync("broken", null, CancellationToken.None);
        await WaitForFrameAsync(client, 0);
        var id = JsonNode.Parse(client.SentFrames[0])!["id"]!.GetValue<long>();

        var errorFrame = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" },
        };
        client.InjectFrame(errorFrame.ToJsonString());

        var act = async () => await task;
        await act.Should().ThrowAsync<McpException>().WithMessage("*method not found*");
    }

    [Fact]
    public void NonJsonFrame_IsSkipped_WithoutCrashing()
    {
        var client = new TestableMcpClient("test");

        // Garbage / log line bleed. Must not throw.
        client.InjectFrame("INFO: server started");
        client.InjectFrame("");
        client.InjectFrame("  not-json  ");
        client.InjectFrame("{ this is not valid json");
    }

    [Fact]
    public async Task ListChangedNotification_RaisesToolsChangedEvent()
    {
        var client = new TestableMcpClient("test");

        var raised = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ToolsChanged += (_, _) => raised.TrySetResult(true);

        client.InjectFrame("""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""");

        var fired = await Task.WhenAny(raised.Task, Task.Delay(1_000)) == raised.Task;
        fired.Should().BeTrue("tools/list_changed must propagate to subscribers");
    }

    [Fact]
    public async Task TransportFailure_FailsAllPendingRequests()
    {
        var client = new TestableMcpClient("test");

        var t1 = client.CallToolAsync("a", null, CancellationToken.None);
        var t2 = client.CallToolAsync("b", null, CancellationToken.None);
        await WaitForFrameAsync(client, 1);

        client.InjectTransportFailure("process exited");

        var act1 = async () => await t1;
        var act2 = async () => await t2;
        await act1.Should().ThrowAsync<McpException>().WithMessage("*transport failed*process exited*");
        await act2.Should().ThrowAsync<McpException>().WithMessage("*transport failed*process exited*");
    }

    [Fact]
    public async Task Cancellation_EmitsCancelNotification_AndCancelsTask()
    {
        var client = new TestableMcpClient("test");
        using var cts = new CancellationTokenSource();

        var task = client.CallToolAsync("slow", null, cts.Token);
        await WaitForFrameAsync(client, 0);
        var id = JsonNode.Parse(client.SentFrames[0])!["id"]!.GetValue<long>();

        cts.Cancel();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The cancel notification is emitted asynchronously — give it a moment.
        for (var i = 0; i < 50 && client.SentFrames.Count < 2; i++)
            await Task.Delay(20);

        client.SentFrames.Should().HaveCountGreaterThanOrEqualTo(2,
            "the client must emit a notifications/cancelled frame after the request is cancelled");

        var cancelFrame = client.SentFrames.Skip(1).Select(f => JsonNode.Parse(f)!).First();
        cancelFrame["method"]!.GetValue<string>().Should().Be("notifications/cancelled");
        cancelFrame["params"]!["requestId"]!.GetValue<long>().Should().Be(id);
    }

    [Fact]
    public async Task Dispose_FailsPendingRequests()
    {
        var client = new TestableMcpClient("test");
        var task = client.CallToolAsync("never", null, CancellationToken.None);
        await WaitForFrameAsync(client, 0);

        await client.DisposeAsync();

        var act = async () => await task;
        await act.Should().ThrowAsync<McpException>().WithMessage("*disposed*");
    }

    [Fact]
    public void IdsAreStrictlyMonotonic_AcrossManyRequests()
    {
        var client = new TestableMcpClient("test");

        const int count = 50;
        for (var i = 0; i < count; i++)
            _ = client.CallToolAsync($"tool_{i}", null, CancellationToken.None);

        client.SentFrames.Should().HaveCount(count);
        var ids = client.SentFrames
            .Select(f => JsonNode.Parse(f)!["id"]!.GetValue<long>())
            .ToList();

        ids.Should().BeInAscendingOrder();
        ids.Distinct().Should().HaveCount(count, "every request must get a unique id");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task WaitForFrameAsync(TestableMcpClient client, int index, int timeoutMs = 1_000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            lock (client.SentFrames)
            {
                if (client.SentFrames.Count > index) return;
            }
            await Task.Delay(5);
        }
        throw new TimeoutException($"Frame[{index}] not produced within {timeoutMs}ms.");
    }

    private static string BuildResultFrame(long id, string text) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = false,
            },
        }.ToJsonString();
}
