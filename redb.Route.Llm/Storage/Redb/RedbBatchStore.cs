using redb.Core;
using redb.Core.Exceptions;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IBatchStore"/>. One <see cref="LlmBatchProps"/> row
/// per submitted batch; the batch id lives in <c>_objects.value_string</c>
/// (partial index on PostgreSQL/SQLite; MSSQL cannot index NVARCHAR(MAX)) and,
/// normalized, in <c>_objects._value_unique</c> — the per-scheme unique index
/// keeps a batch raced between the submit path and a fast webhook callback on
/// a single row.
/// <para>
/// The store does not own an <see cref="IRedbService"/> instance — each call
/// resolves one through <c>IRouteContext.GetRedbService(name, exchange)</c>,
/// which honours the per-exchange scope cache. The redb name is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c> (set by the LLM endpoint URI),
/// falling back to the constructor-supplied default name and then to the host's
/// default unnamed instance.
/// </para>
/// </summary>
public sealed class RedbBatchStore : IBatchStore
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the store. Scheme is synced by the host's redb.InitializeAsync().</summary>
    public RedbBatchStore(IRouteContext context, string? defaultRedbName = null)
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
    public async Task RegisterAsync(BatchJobRecord record, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.BatchId);

        var redb = Resolve(exchange);

        var existing = await redb.Query<LlmBatchProps>()
            .WhereRedb(o => o.ValueUnique == RedbUniqueKey.Normalize(record.BatchId))
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        var props = ToProps(record);

        if (existing is null)
        {
            // The batch id rides in _value_unique: duplicate submit registrations (a
            // redelivered exchange, a RegisterManyAsync reconciliation) race to a
            // single row — the unique index keeps them on one, the loser re-registers
            // onto the winner. The webhook itself never registers; it only advances
            // status via UpdateStatusAsync.
            var row = new RedbObject<LlmBatchProps>
            {
                value_string = record.BatchId,
                ValueUnique = RedbUniqueKey.Normalize(record.BatchId),
                Props = props
            };
            try
            {
                await redb.SaveAsync(row).ConfigureAwait(false);
            }
            catch (RedbUniqueViolationException)
            {
                var winnerKey = row.ValueUnique;
                var winner = await redb.Query<LlmBatchProps>()
                    .WhereRedb(o => o.ValueUnique == winnerKey)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
                if (winner is null) throw; // the key vanished again (deleted mid-race)

                // Never regress a terminal status: between our miss and this catch the
                // webhook may have already marked the winner completed/failed, and the
                // framework never polls — a redelivered "submitted" overwriting it
                // would report the batch as pending forever.
                if (IsTerminal(winner.Props.Status) && !IsTerminal(props.Status))
                    return;

                winner.Props = props;
                winner.date_modify = DateTimeOffset.UtcNow;
                await redb.SaveAsync(winner).ConfigureAwait(false);
            }
        }
        else
        {
            // Same terminal-status guard as the collision path below used to carry alone. The
            // unique lookup now finds the winner up front, so a redelivered "submitted" arrives
            // here — and before Ф4 this branch could downgrade a completed batch to pending
            // forever whenever the raw ids matched. The guard belongs to the register, not to
            // the way the row was found.
            if (IsTerminal(existing.Props.Status) && !IsTerminal(props.Status))
                return;

            existing.Props = props;
            existing.date_modify = DateTimeOffset.UtcNow;
            await redb.SaveAsync(existing).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task RegisterManyAsync(IEnumerable<BatchJobRecord> records, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        // One record per id even within one call — the last entry wins. Two fresh
        // records sharing an id would otherwise self-collide on the unique index
        // inside a single bulk save (the pre-barrier code silently inserted both).
        var input = records
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.BatchId))
            .GroupBy(r => r.BatchId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
        if (input.Count == 0) return;

        var redb = Resolve(exchange);
        await RegisterManyCoreAsync(redb, input, retryOnRace: true, ct).ConfigureAwait(false);
    }

    private static async Task RegisterManyCoreAsync(
        IRedbService redb, List<BatchJobRecord> input, bool retryOnRace, CancellationToken ct)
    {
        // ONE IN-clause lookup for all batch ids (partial index on PG/SQLite; a scan
        // on MSSQL), then ONE bulk SaveAsync — insert/update streams are internal.
        var keys = input.Select(r => r.BatchId).ToArray();
        var existingByKey = (await redb.Query<LlmBatchProps>()
                .WhereRedb(o => keys.Contains(o.ValueString))
                .ToListAsync()
                .ConfigureAwait(false))
            .Where(o => o.value_string is not null)
            .ToDictionary(o => o.value_string!, StringComparer.Ordinal);

        if (!retryOnRace)
        {
            // Retry pass after a lost creation race: the winners hold the unique key —
            // resolve by it too, so every collided batch id becomes an update.
            var normByKey = input.ToDictionary(r => r.BatchId, r => RedbUniqueKey.Normalize(r.BatchId), StringComparer.Ordinal);
            var norms = normByKey.Values.ToArray();
            var keyByNorm = normByKey.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
            var byUnique = await redb.Query<LlmBatchProps>()
                .WhereRedb(o => norms.Contains(o.ValueUnique))
                .ToListAsync()
                .ConfigureAwait(false);
            foreach (var row in byUnique)
                if (row.ValueUnique is { } u && keyByNorm.TryGetValue(u, out var k))
                    existingByKey.TryAdd(k, row);
        }

        var rowsToSave = new List<IRedbObject>(input.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var record in input)
        {
            ct.ThrowIfCancellationRequested();

            var props = ToProps(record);

            if (existingByKey.TryGetValue(record.BatchId, out var existing))
            {
                // Hash-pre-check skips no-op upserts so SaveAsync’s change-tracking
                // doesn't re-read originals from DB for unchanged rows. On the retry
                // pass it is skipped: the first pass already mutated instances, and a
                // pre-check comparing a copy to itself would silently drop the update.
                var hashBefore = existing.ComputeHash();
                existing.Props = props;
                if (retryOnRace && existing.ComputeHash() == hashBefore)
                    continue;
                existing.date_modify = now;
                rowsToSave.Add(existing);
            }
            else
            {
                rowsToSave.Add(new RedbObject<LlmBatchProps>
                {
                    value_string = record.BatchId,
                    ValueUnique = RedbUniqueKey.Normalize(record.BatchId),
                    Props = props
                });
            }
        }

        if (rowsToSave.Count == 0) return;

        try
        {
            await redb.SaveAsync(rowsToSave).ConfigureAwait(false);
        }
        catch (RedbUniqueViolationException) when (retryOnRace)
        {
            // A concurrent writer claimed one of the fresh batch ids after our lookup
            // and the batch rolled back whole. The winners are visible now: rebuild
            // from a fresh lookup and retry once — a second violation is a real error.
            await RegisterManyCoreAsync(redb, input, retryOnRace: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Lifecycle states the webhook writes once and nothing may undo.</summary>
    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";

    private static LlmBatchProps ToProps(BatchJobRecord record) => new()
    {
        ProviderId = record.ProviderId,
        ModelId = record.ModelId,
        ConversationId = record.ConversationId,
        Status = record.Status,
        SubmittedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(record.SubmittedAtUtc, DateTimeKind.Utc)),
        CompletedAtUtc = record.CompletedAtUtc is { } c
            ? new DateTimeOffset(DateTime.SpecifyKind(c, DateTimeKind.Utc))
            : null,
        ResultUrl = record.ResultUrl,
        MetadataJson = record.MetadataJson,
        ErrorMessage = record.ErrorMessage,
        AppendToConversation = record.AppendToConversation
    };

    /// <inheritdoc />
    public async Task<BatchJobRecord?> GetAsync(string batchId, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        var redb = Resolve(exchange);

        var row = await redb.Query<LlmBatchProps>()
            .WhereRedb(o => o.ValueUnique == RedbUniqueKey.Normalize(batchId))
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        return row is null ? null : Materialize(row, batchId);
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(
        string batchId,
        string status,
        DateTimeOffset? completedAtUtc = null,
        string? errorMessage = null,
        IExchange? exchange = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var redb = Resolve(exchange);

        var row = await redb.Query<LlmBatchProps>()
            .WhereRedb(o => o.ValueUnique == RedbUniqueKey.Normalize(batchId))
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        if (row is null) return;

        row.Props.Status = status;
        if (completedAtUtc is { } c) row.Props.CompletedAtUtc = c.ToUniversalTime();
        if (errorMessage is not null) row.Props.ErrorMessage = errorMessage;
        row.date_modify = DateTimeOffset.UtcNow;
        await redb.SaveAsync(row).ConfigureAwait(false);
    }

    private static BatchJobRecord Materialize(RedbObject<LlmBatchProps> row, string batchId) => new()
    {
        BatchId = row.value_string ?? batchId,
        ProviderId = row.Props.ProviderId,
        ModelId = row.Props.ModelId,
        ConversationId = row.Props.ConversationId,
        Status = row.Props.Status,
        SubmittedAtUtc = row.Props.SubmittedAtUtc.UtcDateTime,
        CompletedAtUtc = row.Props.CompletedAtUtc?.UtcDateTime,
        ResultUrl = row.Props.ResultUrl,
        MetadataJson = row.Props.MetadataJson,
        ErrorMessage = row.Props.ErrorMessage,
        AppendToConversation = row.Props.AppendToConversation
    };
}
