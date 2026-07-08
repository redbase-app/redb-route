using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Data;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.RedbCore.Models;

namespace redb.Route.RedbCore.Repositories;

/// <summary>
/// Idempotent repository backed by redb.Core EAV storage.
/// Works on any redb-supported database (PostgreSQL, SQL Server) via <see cref="IRedbService"/>
/// without raw DDL — the scheme is managed automatically through <see cref="IdempotentEntryProps"/>.
/// <para>
/// Uses <see cref="IServiceScopeFactory"/> to create a fresh <see cref="IRedbService"/> scope
/// per operation — safe for concurrent use from multiple route threads.
/// </para>
/// <para>
/// <b>Identity strategy:</b> each entry stores its composite identity in the indexed
/// <see cref="IRedbObject.Name"/> column as <c>"{ProcessorName}:{MessageKey}"</c>. Lookups
/// use <c>WhereRedb(x =&gt; x.Name == compositeName)</c> which hits the
/// <c>IX__objects__name</c> index in both PostgreSQL and SQL Server — O(log n) instead of
/// the EAV property-table scan that the previous implementation incurred.
/// </para>
/// <para>
/// <b>Cluster-safety caveat:</b> EAV has no native UNIQUE constraint, so the read-then-write
/// pattern still has a TOCTOU window between concurrent <see cref="Add"/> calls for the same key.
/// <see cref="DeadlockRetryHelper"/> mitigates database-level deadlocks but does not give
/// strict at-most-once semantics across cluster nodes. For cluster-safe idempotency use
/// <see cref="redb.Route.Sql.Repositories.SqlIdempotentRepository"/> with a manually-created
/// UNIQUE INDEX on <c>(processor_name, message_key)</c>.
/// </para>
/// </summary>
public sealed class RedbIdempotentRepository : IIdempotentRepository
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RedbIdempotentOptions _options;
    private bool _schemeEnsured;

    // Higher retry budget: multi-process TFM runs cause heavy cross-table contention on MsSql
    private const int MaxRetries = 5;
    private const int BaseDelayMs = 200;

    /// <summary>Creates a new repository.</summary>
    public RedbIdempotentRepository(IServiceScopeFactory scopeFactory, RedbIdempotentOptions options)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory;
        _options = options;
    }

    private IRedbService CreateRedb(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<IRedbService>();

    /// <summary>
    /// Composite identity name for a (processor, key) pair. Stored in the indexed
    /// <c>_objects._name</c> column for fast lookup.
    /// </summary>
    private string ComposeName(string key) => $"{_options.ProcessorName}:{key}";

    /// <inheritdoc/>
    public async Task<bool> Add(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureSchemeAsync().ConfigureAwait(false);

        if (_options.Ttl.HasValue)
            await CleanupExpiredAsync(ct).ConfigureAwait(false);

        var entryName = ComposeName(key);

        return await DeadlockRetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var redb = CreateRedb(scope);

            // Indexed lookup by composite name (IX__objects__name).
            // Defensive Props filter guards against any name collision from foreign schemes
            // that happen to share the same name format.
            var existing = await redb.Query<IdempotentEntryProps>()
                .WhereRedb(x => x.Name == entryName)
                .ToListAsync()
                .ConfigureAwait(false);

            if (existing.Any(e => e.Props.ProcessorName == _options.ProcessorName
                               && e.Props.MessageKey == key))
                return false;

            var entry = new RedbObject<IdempotentEntryProps>
            {
                name = entryName,
                Props = new IdempotentEntryProps
                {
                    ProcessorName = _options.ProcessorName,
                    MessageKey = key,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Confirmed = false
                }
            };

            await redb.SaveAsync(entry).ConfigureAwait(false);
            return true;
        });
    }

    /// <inheritdoc/>
    public async Task Confirm(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureSchemeAsync().ConfigureAwait(false);

        var entryName = ComposeName(key);

        await DeadlockRetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var redb = CreateRedb(scope);

            var items = await redb.Query<IdempotentEntryProps>()
                .WhereRedb(x => x.Name == entryName)
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var item in items)
            {
                if (item.Props.ProcessorName != _options.ProcessorName
                    || item.Props.MessageKey != key) continue;
                item.Props.Confirmed = true;
                await redb.SaveAsync(item).ConfigureAwait(false);
            }
        });
    }

    /// <inheritdoc/>
    public async Task Remove(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureSchemeAsync().ConfigureAwait(false);

        var entryName = ComposeName(key);

        await DeadlockRetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var redb = CreateRedb(scope);

            var items = await redb.Query<IdempotentEntryProps>()
                .WhereRedb(x => x.Name == entryName)
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var item in items)
            {
                if (item.Props.ProcessorName != _options.ProcessorName
                    || item.Props.MessageKey != key) continue;
                await redb.DeleteAsync(item).ConfigureAwait(false);
            }
        });
    }

    /// <inheritdoc/>
    public async Task<bool> Contains(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureSchemeAsync().ConfigureAwait(false);

        var entryName = ComposeName(key);

        return await DeadlockRetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var redb = CreateRedb(scope);

            var items = await redb.Query<IdempotentEntryProps>()
                .WhereRedb(x => x.Name == entryName)
                .ToListAsync()
                .ConfigureAwait(false);

            return items.Any(e => e.Props.ProcessorName == _options.ProcessorName
                               && e.Props.MessageKey == key);
        });
    }

    /// <inheritdoc/>
    public async Task Clear(CancellationToken ct = default)
    {
        await EnsureSchemeAsync().ConfigureAwait(false);

        await DeadlockRetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var redb = CreateRedb(scope);

            // Bulk clear is scope-wide; name-prefix filter not used (would require LIKE),
            // so we fall back to the Props-level scan here.
            var items = await redb.Query<IdempotentEntryProps>()
                .Where(e => e.ProcessorName == _options.ProcessorName)
                .ToListAsync()
                .ConfigureAwait(false);

            if (items.Count > 0)
                await redb.DeleteAsync((IEnumerable<IRedbObject>)items).ConfigureAwait(false);
        });
    }

    private async Task EnsureSchemeAsync()
    {
        if (_schemeEnsured) return;

        using var scope = _scopeFactory.CreateScope();
        var redb = CreateRedb(scope);
        await redb.SyncSchemeAsync<IdempotentEntryProps>().ConfigureAwait(false);
        _schemeEnsured = true;
    }

    private async Task CleanupExpiredAsync(CancellationToken ct)
    {
        if (_options.Ttl is not { } ttl) return;

        var cutoff = DateTimeOffset.UtcNow.Subtract(ttl);

        await DeadlockRetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var redb = CreateRedb(scope);

            var expired = await redb.Query<IdempotentEntryProps>()
                .Where(e => e.ProcessorName == _options.ProcessorName && e.CreatedAt < cutoff)
                .ToListAsync()
                .ConfigureAwait(false);

            if (expired.Count > 0)
                await redb.DeleteAsync((IEnumerable<IRedbObject>)expired).ConfigureAwait(false);
        });
    }
}
