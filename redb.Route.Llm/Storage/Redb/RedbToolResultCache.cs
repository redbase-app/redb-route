using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.Llm.Telemetry;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IToolCacheStore"/>. Cache entries are keyed on the
/// caller-supplied content hash stored in the indexed
/// <c>_objects.value_string</c> column. TTL is honoured lazily — entries past
/// expiry are dropped on read.
/// <para>
/// Distinct from <see cref="RedbToolIdempotencyStore"/>: idempotency answers
/// "have I already run this tool call (by tool_use_id)?", while this store
/// answers "do I already have an output for this exact input?". Engine
/// consults the cache before reservation.
/// </para>
/// <para>
/// The store does not own an <see cref="IRedbService"/> instance — each call
/// resolves one through <c>IRouteContext.GetRedbService(name, exchange)</c>,
/// which honours the per-exchange scope cache. The redb name is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c> (set by the LLM endpoint URI),
/// falling back to the constructor-supplied default name and then to the host's
/// default unnamed instance.
/// </para>
/// </summary>
public sealed class RedbToolResultCache : IToolCacheStore
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the store. Scheme is synced by the host's redb.InitializeAsync().</summary>
    public RedbToolResultCache(IRouteContext context, string? defaultRedbName = null)
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
    public async ValueTask<string?> GetAsync(string cacheKey, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);

        var redb = Resolve(exchange);

        var row = await redb.Query<ToolCacheProps>()
            .WhereRedb(o => o.ValueString == cacheKey)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (row is null)
        {
            LlmMetrics.ToolCacheMisses.Add(1);
            return null;
        }

        if (row.Props.ExpiresAtUtc is { } exp && exp <= DateTimeOffset.UtcNow)
        {
            // Lazy eviction — soft-delete and report a miss. Counted separately
            // so dashboards can distinguish cold keys from TTL drops.
            LlmMetrics.ToolCacheExpired.Add(1);
            await redb.SoftDeleteAsync([row]).ConfigureAwait(false);
            return null;
        }

        // Hit-count goes through OpenTelemetry (RouteMetrics meter), NOT a
        // per-read SaveAsync. Tag with the tool name when we have one so
        // dashboards can break down hit rate per tool.
        if (row.Props.ToolName is { Length: > 0 } toolName)
            LlmMetrics.ToolCacheHits.Add(1, new KeyValuePair<string, object?>("llm.tool.name", toolName));
        else
            LlmMetrics.ToolCacheHits.Add(1);

        return row.Props.OutputJson;
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string cacheKey, string outputJson, TimeSpan? ttl = null, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(outputJson);

        var redb = Resolve(exchange);

        var existing = await redb.Query<ToolCacheProps>()
            .WhereRedb(o => o.ValueString == cacheKey)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = ttl is { } span && span > TimeSpan.Zero ? now.Add(span) : (DateTimeOffset?)null;

        if (existing is null)
        {
            var row = new RedbObject<ToolCacheProps>
            {
                value_string = cacheKey,
                Props = new ToolCacheProps
                {
                    OutputJson = outputJson,
                    CreatedAtUtc = now,
                    ExpiresAtUtc = expiresAt
                }
            };
            await redb.SaveAsync(row).ConfigureAwait(false);
        }
        else
        {
            // Hash-pre-check: idempotent SetAsync (same json/ttl/key) becomes
            // a no-op instead of a round-trip through SaveAsync change-tracking.
            var hashBefore = existing.ComputeHash();
            existing.Props.OutputJson = outputJson;
            existing.Props.CreatedAtUtc = now;
            existing.Props.ExpiresAtUtc = expiresAt;
            if (existing.ComputeHash() == hashBefore) return;
            existing.date_modify = now;
            await redb.SaveAsync(existing).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync(IExchange? exchange = null, CancellationToken ct = default)
    {
        var redb = Resolve(exchange);

        // Project only ids — avoids materialising props blobs server-side just
        // to feed them into the bulk SoftDeleteAsync(IEnumerable<long>) overload.
        var ids = await redb.Query<ToolCacheProps>()
            .Select(r => r.id)
            .ToListAsync()
            .ConfigureAwait(false);
        if (ids.Count == 0) return;

        await redb.SoftDeleteAsync(ids).ConfigureAwait(false);
    }
}
