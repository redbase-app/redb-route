using redb.Route.Llm.Engine.Governance;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Deterministic pricing double: every call costs the same, regardless of tokens. Enough to prove the
/// engine prices iterations and enforces a cost ceiling; a per-token table would only test the table.
/// </summary>
public sealed class FlatCostCalculator : ICostCalculator
{
    private readonly decimal _costPerCall;

    /// <summary>Creates a calculator charging <paramref name="costPerCall"/> USD for every response.</summary>
    public FlatCostCalculator(decimal costPerCall)
    {
        _costPerCall = costPerCall;
    }

    /// <summary>Number of times the engine asked for a price.</summary>
    public int Estimates { get; private set; }

    /// <inheritdoc />
    public decimal? Estimate(LlmUsage usage, LlmConnectionFactory factory)
    {
        Estimates++;
        return _costPerCall;
    }
}
