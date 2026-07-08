using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IEvalRunStore"/>. One <see cref="EvalRunProps"/>
/// row per run; the run id lives on the indexed <c>_objects.value_string</c>
/// column for direct lookup. Per-iteration metrics are kept as a typed
/// nested array on the row — REDB persists them natively, no JSON roundtrip.
/// <para>
/// The store does not own an <see cref="IRedbService"/> instance — each call
/// resolves one through <c>IRouteContext.GetRedbService(name, exchange)</c>,
/// which honours the per-exchange scope cache. The redb name is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c> (set by the LLM endpoint URI),
/// falling back to the constructor-supplied default name and then to the host's
/// default unnamed instance.
/// </para>
/// </summary>
public sealed class RedbEvalRunStore : IEvalRunStore
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the store. Scheme is synced by the host's redb.InitializeAsync().</summary>
    public RedbEvalRunStore(IRouteContext context, string? defaultRedbName = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _defaultRedbName = defaultRedbName;
    }

    private IRedbService Resolve(IExchange? exchange)
    {
        var name = _defaultRedbName;
        if (exchange is not null
            && exchange.Properties.TryGetValue(LlmKeys.RedbName, out var raw)
            && raw is string s && s.Length > 0)
            name = s;
        return _context.GetRedbService(name ?? string.Empty, exchange);
    }

    /// <inheritdoc />
    public async Task SaveAsync(EvalRunRecord record, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);

        var redb = Resolve(exchange);

        var existing = await redb.Query<EvalRunProps>()
            .WhereRedb(o => o.ValueString == record.RunId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        var props = ToProps(record);

        if (existing is null)
        {
            var row = new RedbObject<EvalRunProps>
            {
                value_string = record.RunId,
                Props = props
            };
            await redb.SaveAsync(row).ConfigureAwait(false);
        }
        else
        {
            existing.Props = props;
            existing.date_modify = DateTimeOffset.UtcNow;
            await redb.SaveAsync(existing).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SaveManyAsync(IEnumerable<EvalRunRecord> records, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var input = records
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.RunId))
            .ToList();
        if (input.Count == 0) return;

        var redb = Resolve(exchange);

        // ONE indexed IN-clause lookup for all run ids, then ONE bulk SaveAsync —
        // SaveAsync handles insert/update streams internally.
        var keys = input.Select(r => r.RunId).ToArray();
        var existingByKey = (await redb.Query<EvalRunProps>()
                .WhereRedb(o => keys.Contains(o.ValueString))
                .ToListAsync()
                .ConfigureAwait(false))
            .Where(o => o.value_string is not null)
            .ToDictionary(o => o.value_string!, StringComparer.Ordinal);

        var rowsToSave = new List<IRedbObject>(input.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var record in input)
        {
            ct.ThrowIfCancellationRequested();

            var props = ToProps(record);

            if (existingByKey.TryGetValue(record.RunId, out var existing))
            {
                // Hash-pre-check skips no-op upserts so SaveAsync’s change-tracking
                // doesn't re-read originals from DB for unchanged rows.
                var hashBefore = existing.ComputeHash();
                existing.Props = props;
                if (existing.ComputeHash() == hashBefore)
                    continue;
                existing.date_modify = now;
                rowsToSave.Add(existing);
            }
            else
            {
                rowsToSave.Add(new RedbObject<EvalRunProps>
                {
                    value_string = record.RunId,
                    Props = props
                });
            }
        }

        if (rowsToSave.Count == 0) return;
        await redb.SaveAsync(rowsToSave).ConfigureAwait(false);
    }

    private static EvalRunProps ToProps(EvalRunRecord record) => new()
    {
        RunId = record.RunId,
        Scenario = record.Scenario,
        AgentFingerprint = record.AgentFingerprint,
        Score = record.Score,
        InputTokens = record.TotalUsage.InputTokens,
        OutputTokens = record.TotalUsage.OutputTokens,
        CostUsd = record.TotalUsage.CostUsd,
        CreatedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(record.CreatedAtUtc, DateTimeKind.Utc)),
        Iterations = record.Iterations.Select(i => new EvalIterationMetric
        {
            Iteration = i.Iteration,
            InputTokens = i.InputTokens,
            OutputTokens = i.OutputTokens,
            DurationMs = i.DurationMs,
            Score = i.Score,
            Notes = i.Notes
        }).ToArray(),
        Dimensions = record.Dimensions is null ? null : new Dictionary<string, double>(record.Dimensions),
        Fixtures = record.Fixtures is null ? null : new Dictionary<string, string>(record.Fixtures)
    };

    /// <inheritdoc />
    public async Task<EvalRunRecord?> GetAsync(string runId, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var redb = Resolve(exchange);

        var row = await redb.Query<EvalRunProps>()
            .WhereRedb(o => o.ValueString == runId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        return row is null ? null : Materialize(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvalRunRecord>> ListAsync(string? scenario = null, int take = 50, IExchange? exchange = null, CancellationToken ct = default)
    {
        var redb = Resolve(exchange);

        var query = scenario is null
            ? redb.Query<EvalRunProps>()
            : redb.Query<EvalRunProps>().Where(p => p.Scenario == scenario);

        // Sort on the indexed base column (_objects.date_create) — avoids
        // pulling props just to order leaderboard rows.
        var rows = await query
            .OrderByDescendingRedb(o => o.DateCreate)
            .Take(Math.Max(1, take))
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.Select(Materialize).ToArray();
    }

    private static EvalRunRecord Materialize(RedbObject<EvalRunProps> row) => new()
    {
        RunId = row.Props.RunId,
        Scenario = row.Props.Scenario,
        AgentFingerprint = row.Props.AgentFingerprint,
        Score = row.Props.Score,
        TotalUsage = new AgentUsage(
            (int)row.Props.InputTokens,
            (int)row.Props.OutputTokens,
            row.Props.CostUsd),
        Iterations = row.Props.Iterations is null or { Length: 0 }
            ? []
            : row.Props.Iterations.Select(i => new EvalIterationRecord
            {
                Iteration = i.Iteration,
                InputTokens = i.InputTokens,
                OutputTokens = i.OutputTokens,
                DurationMs = i.DurationMs,
                Score = i.Score,
                Notes = i.Notes
            }).ToArray(),
        Dimensions = row.Props.Dimensions,
        Fixtures = row.Props.Fixtures,
        CreatedAtUtc = row.Props.CreatedAtUtc.UtcDateTime
    };
}
