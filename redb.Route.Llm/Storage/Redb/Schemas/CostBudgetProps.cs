using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Running cost / token totals scoped to a conversation. One row per
/// conversation per billing period — <see cref="PeriodStartUtc"/> is the
/// boundary that lets a daily / monthly reset roll the counters. The
/// conversation id lives on the indexed <c>value_string</c> column of
/// <c>_objects</c>, not in props.
/// </summary>
[RedbScheme("LLM Cost Budget")]
public class CostBudgetProps
{
    /// <summary>Optional billing-period anchor (e.g. day or month start, UTC).</summary>
    public DateTimeOffset? PeriodStartUtc { get; set; }

    /// <summary>Aggregated input tokens.</summary>
    public long InputTokens { get; set; }

    /// <summary>Aggregated output tokens.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Aggregated cost in USD.</summary>
    public decimal CostUsd { get; set; }

    /// <summary>When the row was last updated.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
