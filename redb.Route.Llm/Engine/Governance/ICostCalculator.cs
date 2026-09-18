using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Engine.Governance;

/// <summary>
/// Prices one provider response so a cost budget can be enforced. The engine asks this once per
/// iteration and once before the first call (to prove the model can be priced at all).
/// <para>
/// Return <c>null</c> only when this deployment cannot price the call at all — a model whose rates it
/// does not know. A calculator that knows the rates returns <c>0m</c> for zero usage; returning
/// <c>null</c> there would make the engine refuse a cost-budgeted run it could actually enforce.
/// </para>
/// </summary>
public interface ICostCalculator
{
    /// <summary>Estimated cost in USD for one response, or <c>null</c> when the model cannot be priced.</summary>
    /// <param name="usage">Token usage reported by the provider for the call.</param>
    /// <param name="factory">The connection factory the call was made through (model id, provider id).</param>
    decimal? Estimate(LlmUsage usage, LlmConnectionFactory factory);
}

/// <summary>
/// Default calculator: prices nothing. Registered by <c>AddRedbRouteLlm()</c> so the engine always has
/// a calculator to ask; a run that requests a cost budget while this one is registered fails fast
/// instead of silently enforcing a zero ceiling.
/// </summary>
public sealed class NullCostCalculator : ICostCalculator
{
    /// <inheritdoc />
    public decimal? Estimate(LlmUsage usage, LlmConnectionFactory factory) => null;
}