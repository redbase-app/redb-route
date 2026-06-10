using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Governance;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Persists the outcome of a recorded evaluation run. Used by the eval harness
/// to track regression scores between agent / prompt / model versions.
/// <para>
/// The optional <c>exchange</c> parameter on every method carries the route
/// pipeline's current exchange; REDB-backed implementations resolve a
/// per-exchange <see cref="redb.Core.IRedbService"/> through
/// <c>IRouteContext.GetRedbService(name, exchange)</c>. In-memory
/// implementations ignore it.
/// </para>
/// </summary>
public interface IEvalRunStore
{
    /// <summary>Persists a completed evaluation run.</summary>
    Task SaveAsync(EvalRunRecord record, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>
    /// Persists many evaluation runs at once. Bulk-friendly stores commit the
    /// whole set in a single transaction; the default falls back to a loop.
    /// </summary>
    async Task SaveManyAsync(IEnumerable<EvalRunRecord> records, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            await SaveAsync(record, exchange, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Loads a single run by its identifier.</summary>
    Task<EvalRunRecord?> GetAsync(string runId, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>
    /// Lists runs filtered by scenario name. Returns newest first.
    /// </summary>
    Task<IReadOnlyList<EvalRunRecord>> ListAsync(string? scenario = null, int take = 50, IExchange? exchange = null, CancellationToken ct = default);
}

/// <summary>An evaluation run snapshot.</summary>
public sealed class EvalRunRecord
{
    /// <summary>Identifier assigned by the eval harness.</summary>
    public required string RunId { get; init; }

    /// <summary>Scenario name (e.g. "summarize-pr", "sql-rag").</summary>
    public required string Scenario { get; init; }

    /// <summary>Agent / prompt / model fingerprint that produced the run.</summary>
    public required string AgentFingerprint { get; init; }

    /// <summary>Aggregate score in [0.0, 1.0]; null when not scored.</summary>
    public double? Score { get; init; }

    /// <summary>Total usage observed across the run.</summary>
    public AgentUsage TotalUsage { get; init; } = AgentUsage.Zero;

    /// <summary>Per-iteration metrics — empty when the harness did not record per-iteration data.</summary>
    public IReadOnlyList<EvalIterationRecord> Iterations { get; init; } = [];

    /// <summary>Custom scoring dimensions (e.g. <c>"accuracy" = 0.92</c>).</summary>
    public IReadOnlyDictionary<string, double>? Dimensions { get; init; }

    /// <summary>Fixture identifiers / version hashes consumed by the run.</summary>
    public IReadOnlyDictionary<string, string>? Fixtures { get; init; }

    /// <summary>Wall-clock timestamp the record was created.</summary>
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Per-iteration metric inside an <see cref="EvalRunRecord"/>.</summary>
public sealed class EvalIterationRecord
{
    /// <summary>1-based iteration index within the run.</summary>
    public int Iteration { get; init; }

    /// <summary>Input tokens consumed by this iteration.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens produced by this iteration.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Wall-clock duration of the iteration in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>Per-iteration score in [0.0, 1.0]; null when not scored.</summary>
    public double? Score { get; init; }

    /// <summary>Optional free-form note (judge rationale, failure tag, etc.).</summary>
    public string? Notes { get; init; }
}

/// <summary>In-memory eval run store — used by unit tests; production swaps in the redb implementation.</summary>
public sealed class InMemoryEvalRunStore : IEvalRunStore
{
    private readonly ConcurrentDictionary<string, EvalRunRecord> _records = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(EvalRunRecord record, IExchange? exchange = null, CancellationToken ct = default)
    {
        _records[record.RunId] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<EvalRunRecord?> GetAsync(string runId, IExchange? exchange = null, CancellationToken ct = default) =>
        Task.FromResult(_records.TryGetValue(runId, out var rec) ? rec : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<EvalRunRecord>> ListAsync(string? scenario = null, int take = 50, IExchange? exchange = null, CancellationToken ct = default)
    {
        IEnumerable<EvalRunRecord> q = _records.Values;
        if (scenario is not null)
            q = q.Where(r => string.Equals(r.Scenario, scenario, StringComparison.Ordinal));
        var result = q.OrderByDescending(r => r.CreatedAtUtc).Take(take).ToArray();
        return Task.FromResult<IReadOnlyList<EvalRunRecord>>(result);
    }
}
