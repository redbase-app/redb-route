using System.Collections.Concurrent;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Idempotency-store spy for governance tests. Records every reservation, completion and
/// release so a test can assert <b>whether the engine consulted the store at all</b> —
/// which is the observable difference behind the <c>SideEffect</c> gate.
/// <para>
/// Unlike the shipped in-memory store this one does not use an
/// <c>IIdempotentRepository</c>: the tests care about the engine's calls, not about dedup
/// implementation, and a plain dictionary keeps the double free of infrastructure.
/// </para>
/// </summary>
public sealed class SpyIdempotencyStore : IToolIdempotencyStore
{
    private readonly ConcurrentDictionary<string, string> _outputs = new(StringComparer.Ordinal);
    private readonly List<string> _reservations = new();
    private readonly List<string> _completions = new();
    private readonly List<string> _releases = new();
    private readonly object _gate = new();

    /// <summary>Keys the engine tried to reserve, in order (<c>{conversationId}:{toolUseId}</c>).</summary>
    public IReadOnlyList<string> Reservations { get { lock (_gate) return _reservations.ToArray(); } }

    /// <summary>Keys the engine completed after a successful dispatch.</summary>
    public IReadOnlyList<string> Completions { get { lock (_gate) return _completions.ToArray(); } }

    /// <summary>Keys the engine released after a failed dispatch.</summary>
    public IReadOnlyList<string> Releases { get { lock (_gate) return _releases.ToArray(); } }

    /// <inheritdoc />
    public Task<ToolIdempotencyReservation> TryReserveAsync(
        string conversationId, string toolUseId, IExchange? exchange = null, CancellationToken ct = default)
    {
        var key = Key(conversationId, toolUseId);
        lock (_gate) _reservations.Add(key);

        return Task.FromResult(_outputs.TryGetValue(key, out var cached)
            ? ToolIdempotencyReservation.Hit(cached)
            : ToolIdempotencyReservation.NewReservation);
    }

    /// <inheritdoc />
    public Task CompleteAsync(
        string conversationId, string toolUseId, string outputJson, IExchange? exchange = null, CancellationToken ct = default)
    {
        var key = Key(conversationId, toolUseId);
        lock (_gate) _completions.Add(key);
        _outputs[key] = outputJson;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseAsync(
        string conversationId, string toolUseId, IExchange? exchange = null, CancellationToken ct = default)
    {
        var key = Key(conversationId, toolUseId);
        lock (_gate) _releases.Add(key);
        return Task.CompletedTask;
    }

    private static string Key(string conversationId, string toolUseId) => $"{conversationId}:{toolUseId}";
}

/// <summary>
/// Tool-cache spy. Records reads and writes so a test can assert the engine consulted the
/// cache (wave 3 wires it; before that, the counts stay at zero — which is itself the finding).
/// </summary>
public sealed class SpyToolCacheStore : IToolCacheStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _gets = new();
    private readonly List<string> _sets = new();
    private readonly object _gate = new();

    /// <summary>Keys read through <see cref="GetAsync"/>, in order.</summary>
    public IReadOnlyList<string> Gets { get { lock (_gate) return _gets.ToArray(); } }

    /// <summary>Keys written through <see cref="SetAsync"/>, in order.</summary>
    public IReadOnlyList<string> Sets { get { lock (_gate) return _sets.ToArray(); } }

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string cacheKey, IExchange? exchange = null, CancellationToken ct = default)
    {
        lock (_gate) _gets.Add(cacheKey);
        return ValueTask.FromResult(_entries.TryGetValue(cacheKey, out var value) ? value : null);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string cacheKey, string outputJson, TimeSpan? ttl = null, IExchange? exchange = null, CancellationToken ct = default)
    {
        lock (_gate) _sets.Add(cacheKey);
        _entries[cacheKey] = outputJson;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(IExchange? exchange = null, CancellationToken ct = default)
    {
        _entries.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Tool-cache store that always fails. Pins the best-effort rule: an unavailable cache may cost a hit,
/// it must never cost the run — a store outage cannot turn a working tool call into an error for the
/// model (which would then retry it).
/// </summary>
public sealed class ThrowingToolCacheStore : IToolCacheStore
{
    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string cacheKey, IExchange? exchange = null, CancellationToken ct = default)
        => throw new InvalidOperationException("cache store is unavailable");

    /// <inheritdoc />
    public ValueTask SetAsync(string cacheKey, string outputJson, TimeSpan? ttl = null, IExchange? exchange = null, CancellationToken ct = default)
        => throw new InvalidOperationException("cache store is unavailable");

    /// <inheritdoc />
    public ValueTask ClearAsync(IExchange? exchange = null, CancellationToken ct = default)
        => throw new InvalidOperationException("cache store is unavailable");
}

/// <summary>
/// Budget-enforcer spy: delegates to the shipped per-run enforcer and records the reason of every
/// stop, so a test can assert <b>why</b> a run stopped — the engine logs the reason and does not put
/// it on the response, and a test that only counted provider calls would not notice the difference
/// between "budget stopped it" and "the model asked to stop".
/// </summary>
public sealed class SpyBudgetEnforcer : IBudgetEnforcer
{
    private readonly InMemoryBudgetEnforcer _inner = new();
    private readonly List<string?> _stopReasons = new();
    private readonly List<bool> _sawAmbientTransaction = new();
    private readonly object _gate = new();

    /// <summary>For every budget call, in order: whether an ambient transaction was open when the engine made it.</summary>
    public IReadOnlyList<bool> SawAmbientTransaction { get { lock (_gate) return _sawAmbientTransaction.ToArray(); } }

    /// <summary>Reasons of every <see cref="BudgetDecision.Stop"/> the engine was handed, in order.</summary>
    public IReadOnlyList<string?> StopReasons { get { lock (_gate) return _stopReasons.ToArray(); } }

    /// <summary>Number of pre-iteration checks the engine performed.</summary>
    public int PreChecks { get; private set; }

    /// <summary>Number of post-iteration checks the engine performed.</summary>
    public int PostChecks { get; private set; }

    /// <inheritdoc />
    public async ValueTask<BudgetDecision> PreCheckAsync(
        string? conversationId, AgentBudget budget, AgentUsage usageSoFar, CancellationToken ct = default)
    {
        PreChecks++;
        ObserveTransaction();
        var decision = await _inner.PreCheckAsync(conversationId, budget, usageSoFar, ct).ConfigureAwait(false);
        Record(decision);
        return decision;
    }

    /// <inheritdoc />
    public async ValueTask<BudgetDecision> RecordAndCheckAsync(
        string? conversationId, AgentBudget budget, AgentUsage iterationUsage, AgentUsage totalUsage,
        CancellationToken ct = default)
    {
        PostChecks++;
        ObserveTransaction();
        var decision = await _inner.RecordAndCheckAsync(conversationId, budget, iterationUsage, totalUsage, ct)
            .ConfigureAwait(false);
        Record(decision);
        return decision;
    }

    private void ObserveTransaction()
    {
        lock (_gate) _sawAmbientTransaction.Add(System.Transactions.Transaction.Current is not null);
    }

    private void Record(BudgetDecision decision)
    {
        if (decision.Continue) return;
        lock (_gate) _stopReasons.Add(decision.Reason);
    }
}
