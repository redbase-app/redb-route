using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Data;
using redb.Core.Exceptions;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.RedbCore.Models;

namespace redb.Route.RedbCore.Repositories;

/// <summary>
/// Idempotent repository backed by redb.Core props storage.
/// Works on any redb-supported database (PostgreSQL, SQL Server, SQLite) via
/// <see cref="IRedbService"/> without raw DDL — the scheme is managed automatically
/// through <see cref="IdempotentEntryProps"/>.
/// <para>
/// Uses <see cref="IServiceScopeFactory"/> to create a fresh <see cref="IRedbService"/> scope
/// per operation — safe for concurrent use from multiple route threads.
/// </para>
/// <para>
/// <b>Identity strategy:</b> each entry stores its composite identity
/// <c>"{ProcessorName}:{MessageKey}"</c> twice: in the indexed <see cref="IRedbObject.Name"/>
/// column (readable, O(log n) lookups via <c>IX__objects__name</c>) and in
/// <see cref="IRedbObject.ValueUnique"/>, which the core guards with the per-scheme UNIQUE
/// index <c>UIX__objects__scheme_unique</c> on every provider. A composite longer than
/// 440 characters (client keys can be arbitrary) is normalized to a readable prefix plus a
/// SHA-256 tail so it fits both columns on every provider.
/// </para>
/// <para>
/// <b>Cluster safety:</b> concurrent <see cref="Add"/> calls for one key race to insert the
/// same <c>ValueUnique</c>; the database lets exactly one row through and the loser's typed
/// <see cref="RedbUniqueViolationException"/> is treated as «already processed» — first wins,
/// nobody crashes, at-most-once holds across threads and cluster nodes. Entries created by
/// older versions carry no <c>ValueUnique</c>; the <c>Name</c> lookups still find them, so no
/// backfill is required.
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
    /// <c>_objects._name</c> column for fast lookup and in <c>_objects._value_unique</c>
    /// for the database-enforced uniqueness guarantee.
    /// </summary>
    private string ComposeName(string key) => ComposeEntryName(_options.ProcessorName, key);

    /// <summary>
    /// <c>"{processorName}:{key}"</c>, normalized by the shared <see cref="RedbUniqueKey"/>
    /// helper (440-char cap, SHA-256 tail for over-long composites). Internal so tests can
    /// compute the stored identity without copying the format.
    /// </summary>
    internal static string ComposeEntryName(string processorName, string key)
        => RedbUniqueKey.Normalize($"{processorName}:{key}");

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
                ValueUnique = entryName,
                Props = new IdempotentEntryProps
                {
                    ProcessorName = _options.ProcessorName,
                    MessageKey = key,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Confirmed = false
                }
            };

            try
            {
                await redb.SaveAsync(entry).ConfigureAwait(false);
            }
            catch (RedbUniqueViolationException)
            {
                // Lost the creation race: a concurrent Add committed this key between our
                // lookup and insert. First wins — the message is already claimed.
                return false;
            }
            return true;
        }, MaxRetries, BaseDelayMs);
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
        }, MaxRetries, BaseDelayMs);
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
        }, MaxRetries, BaseDelayMs);
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
        }, MaxRetries, BaseDelayMs);
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
        }, MaxRetries, BaseDelayMs);
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
        }, MaxRetries, BaseDelayMs);
    }
}
