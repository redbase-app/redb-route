using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace redb.Route.Tests.Llm.Mcp;

/// <summary>
/// Verifies the observable recovery contract for stdio MCP clients:
/// <list type="bullet">
///   <item>Status transitions from <see cref="McpClientStatus.Healthy"/> to
///         <see cref="McpClientStatus.Dead"/> after the underlying process is killed.</item>
///   <item>Pending / subsequent <c>tools/call</c> requests fail with <see cref="McpException"/>
///         instead of hanging.</item>
///   <item>A fresh client built with the same launch configuration brings the
///         server back up cleanly — proving the manual-restart path works.</item>
/// </list>
/// <para>
/// Each test spawns its own Serena process so they don't perturb the shared
/// <see cref="SerenaFixture"/>. They still join the <c>SerenaSerial</c> collection
/// to avoid running multiple Serena LSPs simultaneously.
/// </para>
/// </summary>
[Trait("Category", "LiveMcp")]
[Collection("SerenaSerial")]
public sealed class RestartTests
{
    [SerenaFact]
    public async Task ProcessKill_TransitionsClientToDead_AndFailsNextCall()
    {
        var launch = SerenaConfig.Launch!;
        var transport = McpTransport.Stdio(launch.Command, launch.Arguments);
        var client = new StdioMcpClient("serena-restart-1", transport, NullLogger.Instance);

        try
        {
            using var initCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await client.InitializeAsync(initCts.Token);
            client.Status.Should().Be(McpClientStatus.Healthy);

            var pid = ResolvePid(client);
            pid.Should().BeGreaterThan(0, "the stdio transport must have spawned a process");

            // Kill the process tree out from under the client and give the
            // OnExited / pumps a moment to react.
            KillProcessTree(pid);

            // Next call must fail rather than hang. The exact exception type can
            // vary (transport-failed McpException vs. broken-pipe IOException)
            // depending on whether the read pump or the write pump notices first.
            using var callCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var act = async () => await client.ListToolsAsync(callCts.Token);
            await act.Should().ThrowAsync<Exception>(
                "calling a dead stdio client must surface as an error, not hang");

            // Give OnProcessExited a moment to mark the client dead.
            await WaitUntil(() => client.Status is McpClientStatus.Dead, TimeSpan.FromSeconds(5));
            client.Status.Should().Be(McpClientStatus.Dead,
                "OnProcessExited must transition the client to Dead");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [SerenaFact]
    public async Task FreshClient_AfterKill_DiscoversToolsAgain()
    {
        var launch = SerenaConfig.Launch!;
        var transport = McpTransport.Stdio(launch.Command, launch.Arguments);

        // Bring up + tear down a first client so we know one is "dead".
        var firstClient = new StdioMcpClient("serena-restart-2a", transport, NullLogger.Instance);
        try
        {
            using var cts1 = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await firstClient.InitializeAsync(cts1.Token);
            firstClient.Status.Should().Be(McpClientStatus.Healthy);
        }
        finally
        {
            await firstClient.DisposeAsync();
        }
        firstClient.Status.Should().Be(McpClientStatus.Dead);

        // Now build a fresh client and prove the recovery path is clean.
        var secondClient = new StdioMcpClient("serena-restart-2b", transport, NullLogger.Instance);
        try
        {
            using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await secondClient.InitializeAsync(cts2.Token);
            secondClient.Status.Should().Be(McpClientStatus.Healthy);

            var tools = await secondClient.ListToolsAsync(cts2.Token);
            tools.Should().NotBeEmpty("fresh Serena instance must re-publish its tool catalogue");
            tools.Should().Contain(t => t.Name.Contains("get_symbols_overview", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await secondClient.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads the private <c>_process</c> field of <see cref="StdioMcpClient"/>
    /// via reflection. This is the only reliable way to identify *this client's*
    /// process — naming-based heuristics would risk killing the
    /// <see cref="SerenaFixture"/>'s long-running instance instead.
    /// </summary>
    private static int ResolvePid(StdioMcpClient client)
    {
        var field = typeof(StdioMcpClient).GetField(
            "_process",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var proc = field?.GetValue(client) as Process;
        return proc?.Id ?? 0;
    }

    private static void KillProcessTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5_000);
        }
        catch
        {
            // Already gone or insufficient privilege — both acceptable for a kill.
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
    }
}
