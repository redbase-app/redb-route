using Microsoft.Extensions.Logging;
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
/// contract: a failed write never breaks a run, and it is logged as a warning with
/// the tool, the <c>tool_use</c> id and the exchange id, so the missing row can be found.
/// <para>
/// The observer is invoked from inside the agent loop, not from a route
/// pipeline, and therefore has no <c>IExchange</c> in scope. Many runs call it at
/// once, so every call opens a redb scope of its own with
/// <c>IRouteContext.CreateRedbScope(name)</c> and releases it: a shared instance would
/// put every run on one connection. The scope uses the constructor-supplied default
/// name, or the host's default service when null. A custom observer can be
/// substituted to pipe audit rows elsewhere.
/// </para>
/// <para>
/// The row belongs to the route's transaction when there is one: under
/// <c>.Transacted()</c> it is written and rolled back together with the tool writes it
/// describes.
/// </para>
/// </summary>
public sealed class RedbAuditObserver : IAgentObserver
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;
    private ILogger? _logger;

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
                // The tool name leads the row name so an operator can slice by tool on a base field
                // (_objects.name) and read the scheme without joining anything. Filtering by the ToolName
                // property is also server-side (props-typed Where), just costlier.
                name = $"audit:{context.Tool.Name}:{context.Run.ExchangeId}:{context.ToolUseId}",
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

            await using var scope = _context.CreateRedbScope(_defaultRedbName);
            await scope.Service.SaveAsync(row).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A lost audit row never breaks the run, but it must not vanish either: the operator has to be able to
            // find the gap. That includes a refusal to open the scope, which is a configuration error.
            Logger()?.LogWarning(ex,
                "Audit row for tool '{Tool}' (tool_use {ToolUseId}, exchange {ExchangeId}) was not written; the run goes on.",
                context.Tool.Name, context.ToolUseId, context.Run.ExchangeId);
        }
    }

    /// <inheritdoc />
    public Task OnRunCompletedAsync(AgentRunCompletedContext context, CancellationToken ct = default) => Task.CompletedTask;

    private ILogger? Logger() => _logger ??=
        (_context.GetService<ILoggerFactory>()
         ?? _context.GetServiceProvider()?.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
        ?.CreateLogger<RedbAuditObserver>();
}
