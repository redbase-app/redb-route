using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IAgentObserver"/> that persists one
/// <see cref="ToolAuditProps"/> row per tool invocation. Non-blocking by
/// contract — failures are swallowed so audit problems never break a run.
/// <para>
/// The observer is invoked from inside the agent loop, not from a route
/// pipeline, and therefore has no <c>IExchange</c> in scope. It resolves the
/// <see cref="IRedbService"/> through <c>IRouteContext.GetRedbService(name,
/// exchange: null)</c> using the constructor-supplied default name (or the
/// host's default unnamed instance when null). A custom observer can be
/// substituted to pipe audit rows into a named redb instance.
/// </para>
/// </summary>
public sealed class RedbAuditObserver : IAgentObserver
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the observer. Scheme is synced by the host's redb.InitializeAsync().</summary>
    public RedbAuditObserver(IRouteContext context, string? defaultRedbName = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _defaultRedbName = defaultRedbName;
    }

    /// <inheritdoc />
    public Task OnRunStartedAsync(AgentRunContext context, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnIterationCompletedAsync(AgentIterationContext context, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task OnToolInvokedAsync(AgentToolInvocationContext context, CancellationToken ct = default)
    {
        try
        {
            var outcome = context.Exception is not null
                ? "error"
                : context.Skipped
                    ? (context.SkipReason?.Contains("denied", StringComparison.OrdinalIgnoreCase) == true ? "denied" : "skipped")
                    : "success";

            var row = new RedbObject<ToolAuditProps>
            {
                name = $"audit:{context.Run.ExchangeId}:{context.ToolUseId}",
                Props = new ToolAuditProps
                {
                    ConversationId = context.Run.ConversationId ?? string.Empty,
                    ToolName = context.Tool.Name,
                    ToolUseId = context.ToolUseId,
                    InvokedAtUtc = DateTimeOffset.UtcNow.Subtract(context.Duration),
                    DurationMs = (int)context.Duration.TotalMilliseconds,
                    Outcome = outcome,
                    SkipReason = context.SkipReason,
                    InputJson = context.InputJson,
                    OutputJson = context.OutputJson,
                    ErrorMessage = context.Exception?.Message
                }
            };

            var redb = _context.GetRedbService(_defaultRedbName ?? string.Empty, exchange: null);
            await redb.SaveAsync(row).ConfigureAwait(false);
        }
        catch
        {
            // Audit failures must not break the run.
        }
    }

    /// <inheritdoc />
    public Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default) => Task.CompletedTask;
}
