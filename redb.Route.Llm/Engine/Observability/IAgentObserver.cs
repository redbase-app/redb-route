using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Engine.Observability;

/// <summary>
/// Receives lifecycle notifications about an agent run. Implementations are
/// expected to be non-blocking — heavy work (DB writes, network IO) should be
/// queued. Multiple observers may be registered via DI; the engine fans out
/// calls in registration order.
/// </summary>
public interface IAgentObserver
{
    /// <summary>Called once when an agent run begins.</summary>
    Task OnRunStartedAsync(AgentRunContext context, CancellationToken ct = default);

    /// <summary>Called after each provider call returns, before tool dispatch.</summary>
    Task OnIterationCompletedAsync(AgentIterationContext context, CancellationToken ct = default);

    /// <summary>Called for each tool invocation, after it returns or throws.</summary>
    Task OnToolInvokedAsync(AgentToolInvocationContext context, CancellationToken ct = default);

    /// <summary>Called once when an agent run ends (whether normally or via cancellation/error).</summary>
    Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default);
}

/// <summary>Common identifiers passed to every observer call within one agent run.</summary>
public sealed class AgentRunContext
{
    /// <summary>Conversation identifier (when persistence is on).</summary>
    public string? ConversationId { get; init; }

    /// <summary>Connection factory name, model id, provider id.</summary>
    public required string FactoryName { get; init; }
    /// <summary>Provider identifier ("openai", "anthropic", ...).</summary>
    public required string ProviderId { get; init; }
    /// <summary>Model identifier as resolved from the factory.</summary>
    public required string ModelId { get; init; }

    /// <summary>Exchange id of the parent exchange that initiated the run.</summary>
    public required string ExchangeId { get; init; }
}

/// <summary>Per-iteration snapshot — usage, stop reason, content shape.</summary>
public sealed class AgentIterationContext
{
    /// <summary>Identifiers shared with the rest of the run.</summary>
    public required AgentRunContext Run { get; init; }

    /// <summary>Iteration index, starting from 1.</summary>
    public required int Iteration { get; init; }

    /// <summary>Stop reason of this iteration's provider response.</summary>
    public required LlmStopReason StopReason { get; init; }

    /// <summary>Usage produced by this iteration alone (not cumulative).</summary>
    public required LlmUsage IterationUsage { get; init; }

    /// <summary>Wall-clock duration of the provider call.</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>Tool invocation outcome.</summary>
public sealed class AgentToolInvocationContext
{
    /// <summary>Identifiers shared with the rest of the run.</summary>
    public required AgentRunContext Run { get; init; }

    /// <summary>Tool that was invoked (or attempted).</summary>
    public required LlmToolCapability Tool { get; init; }

    /// <summary>Tool input JSON.</summary>
    public required string InputJson { get; init; }

    /// <summary>Tool output JSON (null when the call raised an exception).</summary>
    public string? OutputJson { get; init; }

    /// <summary>Tool-use id from the model response.</summary>
    public required string ToolUseId { get; init; }

    /// <summary>Wall-clock duration of the tool execution.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Exception thrown by the tool, if any.</summary>
    public Exception? Exception { get; init; }

    /// <summary>True when the call was skipped due to idempotency / approval denial / cache hit.</summary>
    public bool Skipped { get; init; }

    /// <summary>Reason for skipping (when <see cref="Skipped"/> is true).</summary>
    public string? SkipReason { get; init; }
}

/// <summary>Final state of an agent run.</summary>
public sealed class AgentRunCompletedContext
{
    /// <summary>Identifiers shared with the rest of the run.</summary>
    public required AgentRunContext Run { get; init; }

    /// <summary>Number of iterations the loop completed.</summary>
    public required int Iterations { get; init; }

    /// <summary>Aggregate usage across all iterations.</summary>
    public required LlmUsage TotalUsage { get; init; }

    /// <summary>Final stop reason from the last iteration.</summary>
    public required LlmStopReason StopReason { get; init; }

    /// <summary>Exception that terminated the run, if any.</summary>
    public Exception? Exception { get; init; }

    /// <summary>True when the run was cancelled (via CancellationToken or budget stop).</summary>
    public bool Cancelled { get; init; }
}

/// <summary>Default observer — discards all events.</summary>
public sealed class NoopAgentObserver : IAgentObserver
{
    /// <inheritdoc />
    public Task OnRunStartedAsync(AgentRunContext context, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnIterationCompletedAsync(AgentIterationContext context, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnToolInvokedAsync(AgentToolInvocationContext context, CancellationToken ct = default) => Task.CompletedTask;
    /// <inheritdoc />
    public Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Composite observer — fans out events to every registered <see cref="IAgentObserver"/>.
/// Exceptions in one observer never break the fan-out.
/// </summary>
public sealed class CompositeAgentObserver : IAgentObserver
{
    private readonly IReadOnlyList<IAgentObserver> _observers;

    /// <summary>Creates a composite over the provided observers (order preserved).</summary>
    public CompositeAgentObserver(IEnumerable<IAgentObserver> observers)
    {
        _observers = observers?.ToArray() ?? throw new ArgumentNullException(nameof(observers));
    }

    /// <inheritdoc />
    public Task OnRunStartedAsync(AgentRunContext context, CancellationToken ct = default) =>
        Fan(o => o.OnRunStartedAsync(context, ct));

    /// <inheritdoc />
    public Task OnIterationCompletedAsync(AgentIterationContext context, CancellationToken ct = default) =>
        Fan(o => o.OnIterationCompletedAsync(context, ct));

    /// <inheritdoc />
    public Task OnToolInvokedAsync(AgentToolInvocationContext context, CancellationToken ct = default) =>
        Fan(o => o.OnToolInvokedAsync(context, ct));

    /// <inheritdoc />
    public Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default) =>
        Fan(o => o.OnRunCompletedAsync(context, ct));

    private async Task Fan(Func<IAgentObserver, Task> dispatch)
    {
        foreach (var observer in _observers)
        {
            try { await dispatch(observer).ConfigureAwait(false); }
            catch { /* observers must not break the fan-out */ }
        }
    }
}
