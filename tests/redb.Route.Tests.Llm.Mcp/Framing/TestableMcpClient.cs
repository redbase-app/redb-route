using Microsoft.Extensions.Logging.Abstractions;
using redb.Route.Llm.Mcp.Protocol;

namespace redb.Route.Tests.Llm.Mcp.Framing;

/// <summary>
/// Test subclass that exposes the protected hooks of <see cref="McpClientBase"/>
/// and captures every outgoing frame so we can assert on what the client sent.
/// </summary>
internal sealed class TestableMcpClient : McpClientBase
{
    public List<string> SentFrames { get; } = new();
    public TaskCompletionSource StartTransportTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StopTransportTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestableMcpClient(string serverName)
        : base(serverName, NullLogger.Instance) { }

    public void InjectFrame(string frameJson) => OnFrameReceived(frameJson);
    public void InjectTransportFailure(string reason) => OnTransportFailed(reason);

    protected override Task StartTransportAsync(CancellationToken cancellationToken)
    {
        StartTransportTcs.TrySetResult();
        return Task.CompletedTask;
    }

    protected override Task StopTransportAsync()
    {
        StopTransportTcs.TrySetResult();
        return Task.CompletedTask;
    }

    protected override Task SendFrameAsync(string frameJson, CancellationToken cancellationToken)
    {
        lock (SentFrames) SentFrames.Add(frameJson);
        return Task.CompletedTask;
    }
}
