using redb.Core;
using redb.Core.Exceptions;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IPromptTemplateRegistry"/>. One
/// <see cref="PromptTemplateProps"/> row per (name, version); the composite
/// business key <c>"{name}@{version}"</c> lives in <c>_objects.value_string</c>
/// (partial index on PostgreSQL/SQLite; MSSQL cannot index NVARCHAR(MAX)) and,
/// normalized, in <c>_objects._value_unique</c> — a version registers exactly
/// once, concurrent registrations of one version resolve first-wins.
/// <para>
/// "Latest" lookup is server-side: filter by the value_string prefix
/// <c>"{name}@"</c> and pick the most recently created row.
/// </para>
/// <para>
/// The registry does not own an <see cref="IRedbService"/> instance — each
/// call resolves one through <c>IRouteContext.GetRedbService(name, exchange)</c>,
/// which honours the per-exchange scope cache. The redb name is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c> (set by the LLM endpoint URI),
/// falling back to the constructor-supplied default name and then to the host's
/// default unnamed instance.
/// </para>
/// </summary>
public sealed class RedbPromptTemplateRegistry : IPromptTemplateRegistry
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the registry. Scheme is synced by the host's redb.InitializeAsync().</summary>
    public RedbPromptTemplateRegistry(IRouteContext context, string? defaultRedbName = null)
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
    public async Task<PromptTemplate?> GetAsync(string name, string? version = null, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var redb = Resolve(exchange);

        if (version is not null)
        {
            var key = BuildKey(name, version);
            var row = await redb.Query<PromptTemplateProps>()
                .WhereRedb(o => o.ValueUnique == RedbUniqueKey.Normalize(key))
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            return row is null ? null : Materialize(row);
        }

        // Latest: server-side ORDER BY _objects.date_create DESC LIMIT 1.
        // Sorting on the indexed base column avoids touching props for ordering.
        var latest = await redb.Query<PromptTemplateProps>()
            .Where(p => p.Name == name)
            .OrderByDescendingRedb(o => o.DateCreate)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        return latest is null ? null : Materialize(latest);
    }

    /// <inheritdoc />
    public async Task SetAsync(PromptTemplate template, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Body);

        var redb = Resolve(exchange);

        var key = BuildKey(template.Name, template.Version);
        var existing = await redb.Query<PromptTemplateProps>()
            .WhereRedb(o => o.ValueUnique == RedbUniqueKey.Normalize(key))
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (existing is null)
        {
            // The (name, version) key rides in _value_unique: two concurrent
            // registrations of one version race to a single row. First wins — the
            // loser's body is dropped, because a version is immutable provenance
            // (MessageProps pins "which exact prompt drove this answer" to it) and
            // silently swapping the winner's body would corrupt that audit trail.
            var row = new RedbObject<PromptTemplateProps>
            {
                value_string = key,
                ValueUnique = RedbUniqueKey.Normalize(key),
                Props = new PromptTemplateProps
                {
                    Name = template.Name,
                    Version = template.Version,
                    Body = template.Body,
                    Description = template.Description,
                    CreatedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(template.CreatedAtUtc, DateTimeKind.Utc))
                }
            };
            try
            {
                await redb.SaveAsync(row).ConfigureAwait(false);
            }
            catch (RedbUniqueViolationException)
            {
                // The version is already registered — first wins.
            }
        }
        else if (string.Equals(existing.value_string, key, StringComparison.Ordinal))
        {
            // The Р6 upsert contract: re-setting the version you own updates it in place.
            existing.Props.Body = template.Body;
            existing.Props.Description = template.Description;
            // The framework does not auto-stamp date_modify; bump it ourselves
            // so list/latest queries see the real update time.
            existing.date_modify = DateTimeOffset.UtcNow;
            await redb.SaveAsync(existing).ConfigureAwait(false);
        }
        else
        {
            // The unique key is owned by a row whose raw string diverged — a race winner the
            // string lookup used to miss (it then hit the collision path below and dropped the
            // body). Same doctrine on the new access path: first wins, the incoming body is
            // dropped, because a version is immutable provenance.
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptTemplate>> ListAsync(IExchange? exchange = null, CancellationToken ct = default)
    {
        var redb = Resolve(exchange);

        var rows = await redb.Query<PromptTemplateProps>()
            .OrderByDescendingRedb(o => o.DateCreate)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.Select(Materialize).ToArray();
    }

    private static PromptTemplate Materialize(RedbObject<PromptTemplateProps> row) => new()
    {
        Name = row.Props.Name,
        Version = row.Props.Version,
        Body = row.Props.Body,
        Description = row.Props.Description,
        CreatedAtUtc = row.Props.CreatedAtUtc.UtcDateTime
    };

    private static string BuildKey(string name, string version) => $"{name}@{version}";
}
