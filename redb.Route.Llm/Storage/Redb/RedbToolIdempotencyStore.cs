using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IToolIdempotencyStore"/>. Reservations are tracked
/// via the supplied <see cref="IIdempotentRepository"/> (the
/// <c>RedbIdempotentRepository</c> in production); idempotent tool outputs
/// are persisted in a <see cref="ToolIdempotencyProps"/> row whose composite
/// key <c>"llm-tool:{conv}:{toolUseId}"</c> lives in the indexed
/// <c>_objects.value_string</c> column. The dedicated scheme keeps
/// idempotency rows physically separate from <see cref="ToolCacheProps"/>
/// content-hash entries — lookups touch a single scheme partition.
/// On a duplicate, the previously-saved output is returned and the model
/// never sees a second side effect.
/// <para>
/// The store does not own an <see cref="IRedbService"/> instance — each call
/// resolves one through <c>IRouteContext.GetRedbService(name, exchange)</c>,
/// which honours the per-exchange scope cache. The redb name is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c> (set by the LLM endpoint URI),
/// falling back to the constructor-supplied default name and then to the host's
/// default unnamed instance.
/// </para>
/// </summary>
public sealed class RedbToolIdempotencyStore : IToolIdempotencyStore
{
    private readonly IIdempotentRepository _repository;
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the store. <paramref name="repository"/> handles dedup; redb stores cached outputs.</summary>
    public RedbToolIdempotencyStore(IIdempotentRepository repository, IRouteContext context, string? defaultRedbName = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
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
    public async Task<ToolIdempotencyReservation> TryReserveAsync(string conversationId, string toolUseId, IExchange? exchange = null, CancellationToken ct = default)
    {
        var key = BuildKey(conversationId, toolUseId);
        var fresh = await _repository.Add(key, ct).ConfigureAwait(false);
        if (fresh) return ToolIdempotencyReservation.NewReservation;

        var cached = await LoadCachedOutputAsync(Resolve(exchange), key).ConfigureAwait(false);
        return ToolIdempotencyReservation.Hit(cached ?? "{}");
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string conversationId, string toolUseId, string outputJson, IExchange? exchange = null, CancellationToken ct = default)
    {
        var key = BuildKey(conversationId, toolUseId);
        var redb = Resolve(exchange);

        var row = await redb.Query<ToolIdempotencyProps>()
            .WhereRedb(x => x.ValueString == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new RedbObject<ToolIdempotencyProps>
            {
                value_string = key,
                Props = new ToolIdempotencyProps
                {
                    ToolName = null,
                    OutputJson = outputJson,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                }
            };
        }
        else
        {
            row.Props.OutputJson = outputJson;
            row.date_modify = DateTimeOffset.UtcNow;
        }
        await redb.SaveAsync(row).ConfigureAwait(false);
        await _repository.Confirm(key, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string conversationId, string toolUseId, IExchange? exchange = null, CancellationToken ct = default)
    {
        var key = BuildKey(conversationId, toolUseId);
        await _repository.Remove(key, ct).ConfigureAwait(false);

        var redb = Resolve(exchange);
        var row = await redb.Query<ToolIdempotencyProps>()
            .WhereRedb(x => x.ValueString == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        if (row is not null)
            await redb.SoftDeleteAsync([row]).ConfigureAwait(false);
    }

    private static async Task<string?> LoadCachedOutputAsync(IRedbService redb, string key)
    {
        var row = await redb.Query<ToolIdempotencyProps>()
            .WhereRedb(x => x.ValueString == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        return row?.Props.OutputJson;
    }

    private static string BuildKey(string conversationId, string toolUseId) =>
        $"llm-tool:{conversationId}:{toolUseId}";
}
