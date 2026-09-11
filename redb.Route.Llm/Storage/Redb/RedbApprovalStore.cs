using redb.Core;
using redb.Core.Exceptions;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IApprovalStore"/>. One <see cref="ApprovalProps"/>
/// row per decision; the approval id lives in <c>_objects.value_string</c>
/// (partial index on PostgreSQL/SQLite; MSSQL cannot index NVARCHAR(MAX)) and,
/// normalized, in <c>_objects._value_unique</c> — one approval id resolves to
/// exactly one recorded decision, first wins.
/// <para>
/// The store does not own an <see cref="IRedbService"/> instance — each call
/// resolves one through <c>IRouteContext.GetRedbService(name, exchange)</c>,
/// which honours the per-exchange scope cache. The redb name is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c> (set by the LLM endpoint URI),
/// falling back to the constructor-supplied default name and then to the host's
/// default unnamed instance.
/// </para>
/// </summary>
public sealed class RedbApprovalStore : IApprovalStore
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the store. Scheme is synced by the host's redb.InitializeAsync().</summary>
    public RedbApprovalStore(IRouteContext context, string? defaultRedbName = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _defaultRedbName = defaultRedbName;
    }

    private IRedbService Resolve(IExchange? exchange)
    {
        var name = _defaultRedbName;
        if (exchange is not null
            && exchange.Properties.TryGetValue(LlmKeys.RedbName, out var raw)
            && raw is string s && s.Length > 0)
            name = s;
        return _context.GetRedbService(name ?? string.Empty, exchange);
    }

    /// <inheritdoc />
    public async Task RecordAsync(ApprovalRequest request, ApprovalDecision decision, IExchange? exchange = null, CancellationToken ct = default)
    {
        var id = decision.ApprovalId ?? Guid.NewGuid().ToString("N");
        var row = new RedbObject<ApprovalProps>
        {
            value_string = id,
            // A caller-supplied approval id must resolve to ONE decision: the key rides
            // in _value_unique, and a duplicate record of the same id is dropped —
            // first wins (a self-generated GUID never collides).
            ValueUnique = RedbUniqueKey.Normalize(id),
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

        var redb = Resolve(exchange);
        try
        {
            await redb.SaveAsync(row).ConfigureAwait(false);
        }
        catch (RedbUniqueViolationException)
        {
            // The decision for this approval id is already recorded — first wins.
        }
    }

    /// <inheritdoc />
    public async Task<ApprovalRecord?> FindAsync(string approvalId, IExchange? exchange = null, CancellationToken ct = default)
    {
        var redb = Resolve(exchange);
        var hit = await redb.Query<ApprovalProps>()
            .WhereRedb(x => x.ValueUnique == RedbUniqueKey.Normalize(approvalId))
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
}
