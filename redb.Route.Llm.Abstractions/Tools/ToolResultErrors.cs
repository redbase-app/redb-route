namespace redb.Route.Llm.Abstractions.Tools;

/// <summary>
/// Model-facing <c>tool_result</c> error codes: what a tool call that could not execute reports back
/// to the LLM. Deliberately terse — the model does not need the required-claims list, and handing it
/// over would turn a tool_result into the one channel that leaks the tool's security policy.
/// </summary>
public static class ToolResultErrors
{
    /// <summary>The tool requires claims the run's principal does not hold (or cannot prove).</summary>
    public const string ClaimsMissing = "claims_missing";

    /// <summary>The approval gate refused this call; the reason text stays in the observer channel.</summary>
    public const string ApprovalDenied = "approval_denied";

    /// <summary>
    /// The tool has an irreversible external effect and the run is inside a transaction that can still roll
    /// back, so the tool was not run.
    /// </summary>
    public const string ExternalInTransaction = "external_in_transaction";
}
