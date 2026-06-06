using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IAgentObserver"/> that persists one
/// <see cref="ToolAuditProps"/> row per tool invocation. Non-blocking by
/// contract — failures are swallowed so audit problems never break a run.
/// </summary>
public sealed class RedbAuditObserver : IAgentObserver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private bool _schemeEnsured;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    /// <summary>Creates the observer. Scheme is synced lazily on first event.</summary>
    public RedbAuditObserver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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
            await EnsureSchemeAsync().ConfigureAwait(false);

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

            using var scope = _scopeFactory.CreateScope();
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.SaveAsync(row).ConfigureAwait(false);
        }
        catch
        {
            // Audit failures must not break the run.
        }
    }

    /// <inheritdoc />
    public Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default) => Task.CompletedTask;

    private async Task EnsureSchemeAsync()
    {
        if (_schemeEnsured) return;
        await _ensureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_schemeEnsured) return;
            using var scope = _scopeFactory.CreateScope();
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.SyncSchemeAsync<ToolAuditProps>().ConfigureAwait(false);
            _schemeEnsured = true;
        }
        finally { _ensureLock.Release(); }
    }
}
