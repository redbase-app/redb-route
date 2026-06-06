using System.Collections.Concurrent;
using redb.Route.Llm.Engine.Observability;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Captures every <see cref="IAgentObserver.OnToolInvokedAsync"/> call so a live test
/// can assert which tools the model actually invoked, what input JSON it sent and what
/// the tool returned. Other observer events are accepted as no-ops.
/// </summary>
public sealed class ToolInvocationSpy : IAgentObserver
{
    private readonly ConcurrentBag<AgentToolInvocationContext> _invocations = new();

    /// <summary>Snapshot of every captured invocation in registration order.</summary>
    public IReadOnlyCollection<AgentToolInvocationContext> Invocations => _invocations.ToArray();

    /// <summary>Convenience filter — returns invocations whose tool name matches.</summary>
    public IReadOnlyCollection<AgentToolInvocationContext> ForTool(string name) =>
        _invocations.Where(i => i.Tool.Name == name).ToArray();

    /// <inheritdoc />
    public Task OnRunStartedAsync(AgentRunContext context, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnIterationCompletedAsync(AgentIterationContext context, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnToolInvokedAsync(AgentToolInvocationContext context, CancellationToken ct = default)
    {
        _invocations.Add(context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default) => Task.CompletedTask;
}
