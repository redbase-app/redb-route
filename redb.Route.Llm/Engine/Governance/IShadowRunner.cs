using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Engine.Governance;

/// <summary>
/// Optional shadow runner — when enabled, every primary provider call is also
/// executed against an alternate factory, results compared and the divergence
/// recorded via the <see cref="Observability.IAgentObserver"/> for offline review.
/// The user-visible response always comes from the primary call; the shadow run
/// never affects routing.
/// </summary>
public interface IShadowRunner
{
    /// <summary>True when a shadow run should be executed alongside the primary.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Executes the shadow call. Implementations should be fire-and-forget from
    /// the caller's perspective — exceptions must not propagate.
    /// </summary>
    /// <param name="primary">Provider used for the visible response.</param>
    /// <param name="request">Request that was sent to the primary provider.</param>
    /// <param name="primaryResponse">Primary provider response — for divergence comparison.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RunAsync(
        ILlmProvider primary,
        LlmRequest request,
        LlmResponse primaryResponse,
        CancellationToken ct = default);
}

/// <summary>Default no-op shadow runner — disabled, never executes anything.</summary>
public sealed class NoopShadowRunner : IShadowRunner
{
    /// <inheritdoc />
    public bool Enabled => false;

    /// <inheritdoc />
    public Task RunAsync(ILlmProvider primary, LlmRequest request, LlmResponse primaryResponse, CancellationToken ct = default)
        => Task.CompletedTask;
}
