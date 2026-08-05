using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Wraps a single route node so that, as the exchange passes through, a <see cref="MessageHistoryEntry"/>
/// (route id, node id, node label, elapsed ms) is appended to the exchange's message history. Applied at
/// compile time by <see cref="RouteContext.CompileNode"/> when message history is enabled — the runtime
/// half of the Apache Camel Message History EIP.
/// <para>
/// The elapsed time is recorded in a <c>finally</c>, so a node that throws still leaves a history entry —
/// which is exactly what the failure dump needs to point at the step that failed.
/// </para>
/// </summary>
internal sealed class MessageHistoryProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly string _routeId;
    private readonly string _nodeId;
    private readonly string _label;

    public MessageHistoryProcessor(IProcessor inner, string routeId, string nodeId, string label)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _routeId = routeId;
        _nodeId = nodeId;
        _label = label;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            MessageHistory.Append(exchange, new MessageHistoryEntry(_routeId, _nodeId, _label, elapsedMs));
        }
    }
}
