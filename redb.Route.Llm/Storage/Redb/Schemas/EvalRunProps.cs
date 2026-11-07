using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// A persisted eval-harness run. Scenario / fingerprint / score are top-level
/// so leaderboard queries do not touch <c>_values</c>. Per-iteration metrics
/// and scoring dimensions are stored as typed nested collections — REDB
/// persists them natively, no JSON marshalling.
/// </summary>
[RedbScheme("LLM Eval Run")]
public class EvalRunProps
{
    /// <summary>Identifier assigned by the eval runner — business key.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Scenario name (e.g. "summarize-pr", "sql-rag").</summary>
    public string Scenario { get; set; } = string.Empty;

    /// <summary>Agent / prompt / model fingerprint that produced the run.</summary>
    public string AgentFingerprint { get; set; } = string.Empty;

    /// <summary>Aggregate score in [0.0, 1.0]; null when the harness did not score the run.</summary>
    public double? Score { get; set; }

    /// <summary>Aggregated input tokens.</summary>
    public long InputTokens { get; set; }

    /// <summary>Aggregated output tokens.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Total cost in USD.</summary>
    public decimal CostUsd { get; set; }

    /// <summary>When the run finished.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// Per-iteration metrics — one entry per agent loop iteration. Typed nested
    /// array; queryable by iteration number when needed.
    /// </summary>
    public EvalIterationMetric[] Iterations { get; set; } = [];

    /// <summary>
    /// Custom scoring dimensions (e.g. <c>"accuracy" = 0.92</c>,
    /// <c>"latency_p95_ms" = 870</c>). Native dictionary — no JSON.
    /// </summary>
    public Dictionary<string, double>? Dimensions { get; set; }

    /// <summary>
    /// Fixture identifiers / version hashes consumed by the run — preserves
    /// reproducibility provenance without leaking fixture payloads into the row.
    /// </summary>
    public Dictionary<string, string>? Fixtures { get; set; }
}

/// <summary>Per-iteration metric inside an <see cref="EvalRunProps"/> run.</summary>
public class EvalIterationMetric
{
    /// <summary>1-based iteration index within the run.</summary>
    public int Iteration { get; set; }

    /// <summary>Input tokens consumed by this iteration.</summary>
    public int InputTokens { get; set; }

    /// <summary>Output tokens produced by this iteration.</summary>
    public int OutputTokens { get; set; }

    /// <summary>Wall-clock duration of the iteration in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>Per-iteration score in [0.0, 1.0]; null when the harness did not score this iteration.</summary>
    public double? Score { get; set; }

    /// <summary>Optional free-form note (judge rationale, failure tag, etc.).</summary>
    public string? Notes { get; set; }
}
