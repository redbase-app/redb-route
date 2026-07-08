namespace redb.Route.Llm.Engine;

/// <summary>
/// Drives the tool-loop: provider call → tool dispatch → provider call → ... until
/// <c>end_turn</c>, max iterations, budget cap or cancellation. Owns governance
/// (budget, shadow, idempotency, approval) — provider implementations stay pure.
/// </summary>
public interface IAgentEngine
{
    /// <summary>Runs the agent to completion.</summary>
    Task<AgentResponse> RunAsync(AgentRequest request, CancellationToken ct = default);
}
