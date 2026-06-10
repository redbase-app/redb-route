using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Governance;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Persists approval decisions for audit and replay. Every call answered by
/// <see cref="IApprovalGate"/> should land here so reviewers can correlate the
/// gate's verdict with the resulting tool call.
/// <para>
/// The optional <c>exchange</c> parameter on every method carries the route
/// pipeline's current exchange; REDB-backed implementations resolve a
/// per-exchange <see cref="redb.Core.IRedbService"/> through
/// <c>IRouteContext.GetRedbService(name, exchange)</c> using the named-redb
/// hint stored in <see cref="LlmKeys.RedbName"/>. In-memory implementations
/// ignore it.
/// </para>
/// </summary>
public interface IApprovalStore
{
    /// <summary>Records a decision against the pending request.</summary>
    Task RecordAsync(ApprovalRequest request, ApprovalDecision decision, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>Looks up the previously-recorded decision for <paramref name="approvalId"/>.</summary>
    Task<ApprovalRecord?> FindAsync(string approvalId, IExchange? exchange = null, CancellationToken ct = default);
}

/// <summary>An approval decision joined with the request that triggered it.</summary>
public sealed class ApprovalRecord
{
    /// <summary>Approval identifier (also stored on the audit row).</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Conversation the approval belonged to.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Tool name that was gated.</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool input JSON as it was about to be dispatched.</summary>
    public required string InputJson { get; init; }

    /// <summary>True when the gate allowed the call.</summary>
    public required bool Approved { get; init; }

    /// <summary>Reason text supplied by the approver (especially on denial).</summary>
    public string? Reason { get; init; }

    /// <summary>Wall-clock timestamp the decision was recorded.</summary>
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>In-memory approval store — sufficient for tests and demos.</summary>
public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly ConcurrentDictionary<string, ApprovalRecord> _records = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task RecordAsync(ApprovalRequest request, ApprovalDecision decision, IExchange? exchange = null, CancellationToken ct = default)
    {
        var id = decision.ApprovalId ?? Guid.NewGuid().ToString("N");
        _records[id] = new ApprovalRecord
        {
            ApprovalId = id,
            ConversationId = request.ConversationId,
            ToolName = request.Tool.Name,
            InputJson = request.InputJson,
            Approved = decision.Approved,
            Reason = decision.Reason
        };
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ApprovalRecord?> FindAsync(string approvalId, IExchange? exchange = null, CancellationToken ct = default) =>
        Task.FromResult(_records.TryGetValue(approvalId, out var rec) ? rec : null);
}
