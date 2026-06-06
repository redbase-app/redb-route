using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Engine.Governance;

/// <summary>
/// Gate that blocks a mutating or externally-irreversible tool call until an
/// approver responds. The agent loop calls <see cref="AwaitAsync"/> before
/// dispatching any tool whose <see cref="LlmToolCapability.Safety"/> demands
/// approval (either via <see cref="LlmToolSafety.RequiresApproval"/> or because
/// the run is marked <c>.RequiresApproval</c> on the DSL).
/// </summary>
public interface IApprovalGate
{
    /// <summary>
    /// Awaits an approval decision for the given tool call.
    /// </summary>
    /// <param name="request">Pending approval request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The approver's decision.</returns>
    Task<ApprovalDecision> AwaitAsync(ApprovalRequest request, CancellationToken ct = default);
}

/// <summary>Pending tool-call approval request handed to the gate.</summary>
public sealed class ApprovalRequest
{
    /// <summary>Conversation identifier (when persistence is on).</summary>
    public string? ConversationId { get; init; }

    /// <summary>Tool that will be executed.</summary>
    public required LlmToolCapability Tool { get; init; }

    /// <summary>Tool input JSON exactly as it would be passed to the bridged endpoint.</summary>
    public required string InputJson { get; init; }

    /// <summary>Parent exchange owning the call (carries principal, headers, scope).</summary>
    public required IExchange Exchange { get; init; }

    /// <summary>Tool-use id from the model response — propagated to the approver and audit.</summary>
    public required string ToolUseId { get; init; }
}

/// <summary>Outcome of an approval decision.</summary>
public sealed class ApprovalDecision
{
    /// <summary>True = proceed with the tool call; false = abort.</summary>
    public required bool Approved { get; init; }

    /// <summary>Identifier of the approval (for audit / receipt). May be null.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>Optional reason supplied by the approver (especially on rejection).</summary>
    public string? Reason { get; init; }

    /// <summary>Convenience factory for an auto-approval decision.</summary>
    public static ApprovalDecision Approve(string? approvalId = null) =>
        new() { Approved = true, ApprovalId = approvalId };

    /// <summary>Convenience factory for a denial decision.</summary>
    public static ApprovalDecision Deny(string reason) =>
        new() { Approved = false, Reason = reason };
}

/// <summary>
/// Default gate that auto-approves every request — used in development and in
/// fully-trusted scenarios. **Do not use in production** for tools with
/// <see cref="ToolSideEffect.External"/> or <see cref="LlmToolSafety.RequiresApproval"/>.
/// </summary>
public sealed class AutoApproveGate : IApprovalGate
{
    /// <inheritdoc />
    public Task<ApprovalDecision> AwaitAsync(ApprovalRequest request, CancellationToken ct = default)
        => Task.FromResult(ApprovalDecision.Approve("auto"));
}

/// <summary>
/// Gate that denies every approval request — used as a safe default when the
/// caller never wires an approver but mutating tools are present.
/// </summary>
public sealed class DenyAllGate : IApprovalGate
{
    /// <inheritdoc />
    public Task<ApprovalDecision> AwaitAsync(ApprovalRequest request, CancellationToken ct = default)
        => Task.FromResult(ApprovalDecision.Deny("no approval gate configured"));
}
