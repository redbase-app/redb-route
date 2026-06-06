using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IToolIdempotencyStore"/>. Reservations are tracked
/// via the supplied <see cref="IIdempotentRepository"/> (the
/// <c>RedbIdempotentRepository</c> in production); cached tool outputs are
/// persisted in a <see cref="ToolCacheProps"/> row whose composite cache key
/// (<c>"llm-tool:{conv}:{toolUseId}"</c>) lives in the indexed
/// <c>_objects.value_string</c> column. On a duplicate, the previously-saved
/// output is returned and the model never sees a second side effect.
/// </summary>
public sealed class RedbToolIdempotencyStore : IToolIdempotencyStore
{
    private readonly IIdempotentRepository _repository;
    private readonly IServiceScopeFactory _scopeFactory;
    private bool _schemeEnsured;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    /// <summary>Creates the store. <paramref name="repository"/> handles dedup; redb stores cached outputs.</summary>
    public RedbToolIdempotencyStore(IIdempotentRepository repository, IServiceScopeFactory scopeFactory)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async Task<ToolIdempotencyReservation> TryReserveAsync(string conversationId, string toolUseId, CancellationToken ct = default)
    {
        await EnsureSchemeAsync().ConfigureAwait(false);

        var key = BuildKey(conversationId, toolUseId);
        var fresh = await _repository.Add(key, ct).ConfigureAwait(false);
        if (fresh) return ToolIdempotencyReservation.NewReservation;

        var cached = await LoadCachedOutputAsync(key).ConfigureAwait(false);
        return ToolIdempotencyReservation.Hit(cached ?? "{}");
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string conversationId, string toolUseId, string outputJson, CancellationToken ct = default)
    {
        await EnsureSchemeAsync().ConfigureAwait(false);

        var key = BuildKey(conversationId, toolUseId);

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var row = await redb.Query<ToolCacheProps>()
            .WhereRedb(x => x.ValueString == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new RedbObject<ToolCacheProps>
            {
                value_string = key,
                Props = new ToolCacheProps
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
        }
        await redb.SaveAsync(row).ConfigureAwait(false);
        await _repository.Confirm(key, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string conversationId, string toolUseId, CancellationToken ct = default)
    {
        var key = BuildKey(conversationId, toolUseId);
        await _repository.Remove(key, ct).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
        var row = await redb.Query<ToolCacheProps>()
            .WhereRedb(x => x.ValueString == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        if (row is not null)
            await redb.DeleteAsync(row).ConfigureAwait(false);
    }

    private async Task<string?> LoadCachedOutputAsync(string key)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
        var row = await redb.Query<ToolCacheProps>()
            .WhereRedb(x => x.ValueString == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        return row?.Props.OutputJson;
    }

    private async Task EnsureSchemeAsync()
    {
        if (_schemeEnsured) return;
        await _ensureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_schemeEnsured) return;
            using var scope = _scopeFactory.CreateScope();
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.SyncSchemeAsync<ToolCacheProps>().ConfigureAwait(false);
            _schemeEnsured = true;
        }
        finally { _ensureLock.Release(); }
    }

    private static string BuildKey(string conversationId, string toolUseId) =>
        $"llm-tool:{conversationId}:{toolUseId}";
}
