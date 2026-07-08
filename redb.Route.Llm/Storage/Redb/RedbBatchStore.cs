using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IBatchStore"/>. One <see cref="LlmBatchProps"/> row
/// per submitted batch; the batch id lives on the indexed
/// <c>_objects.value_string</c> column so callback lookups hit a single-row
/// server-side query.
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
            .WhereRedb(o => o.ValueString == record.BatchId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        var props = ToProps(record);

        if (existing is null)
        {
            var row = new RedbObject<LlmBatchProps>
            {
                value_string = record.BatchId,
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
    public async Task RegisterManyAsync(IEnumerable<BatchJobRecord> records, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var input = records
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.BatchId))
            .ToList();
        if (input.Count == 0) return;

        var redb = Resolve(exchange);

        // ONE indexed IN-clause lookup for all batch ids, then ONE bulk SaveAsync —
        // SaveAsync handles insert/update streams internally.
        var keys = input.Select(r => r.BatchId).ToArray();
        var existingByKey = (await redb.Query<LlmBatchProps>()
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

            if (existingByKey.TryGetValue(record.BatchId, out var existing))
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
                rowsToSave.Add(new RedbObject<LlmBatchProps>
                {
                    value_string = record.BatchId,
                    Props = props
                });
            }
        }

        if (rowsToSave.Count == 0) return;
        await redb.SaveAsync(rowsToSave).ConfigureAwait(false);
    }

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
            .WhereRedb(o => o.ValueString == batchId)
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
            .WhereRedb(o => o.ValueString == batchId)
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
