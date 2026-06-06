using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IApprovalStore"/>. One <see cref="ApprovalProps"/>
/// row per decision; the approval id lives on the indexed
/// <c>_objects.value_string</c> column.
/// </summary>
public sealed class RedbApprovalStore : IApprovalStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private bool _schemeEnsured;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    /// <summary>Creates the store. Scheme is synced lazily on first use.</summary>
    public RedbApprovalStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async Task RecordAsync(ApprovalRequest request, ApprovalDecision decision, CancellationToken ct = default)
    {
        await EnsureSchemeAsync().ConfigureAwait(false);

        var id = decision.ApprovalId ?? Guid.NewGuid().ToString("N");
        var row = new RedbObject<ApprovalProps>
        {
            value_string = id,
            Props = new ApprovalProps
            {
                ConversationId = request.ConversationId,
                ToolName = request.Tool.Name,
                ToolUseId = request.ToolUseId,
                Approved = decision.Approved,
                Reason = decision.Reason,
                ApprovedBy = decision.ApprovalId,
                DecidedAtUtc = DateTimeOffset.UtcNow,
                InputJson = request.InputJson
            }
        };

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
        await redb.SaveAsync(row).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ApprovalRecord?> FindAsync(string approvalId, CancellationToken ct = default)
    {
        await EnsureSchemeAsync().ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
        var hit = await redb.Query<ApprovalProps>()
            .WhereRedb(x => x.ValueString == approvalId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        if (hit is null) return null;
        return new ApprovalRecord
        {
            ApprovalId = hit.value_string ?? approvalId,
            ConversationId = hit.Props.ConversationId,
            ToolName = hit.Props.ToolName,
            InputJson = hit.Props.InputJson,
            Approved = hit.Props.Approved,
            Reason = hit.Props.Reason,
            CreatedAtUtc = hit.Props.DecidedAtUtc.UtcDateTime
        };
    }

    private async Task EnsureSchemeAsync()
    {
        if (_schemeEnsured) return;
        await _ensureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_schemeEnsured) return;
            using var scope = _scopeFactory.CreateScope();
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.SyncSchemeAsync<ApprovalProps>().ConfigureAwait(false);
            _schemeEnsured = true;
        }
        finally { _ensureLock.Release(); }
    }
}
