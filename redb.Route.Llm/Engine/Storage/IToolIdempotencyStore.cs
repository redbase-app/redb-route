using redb.Route.Abstractions;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Tracks tool invocations so that retried or duplicated tool calls produce a
/// single side effect. The contract is intentionally a thin wrapper over
/// <see cref="IIdempotentRepository"/> — the redb-backed implementation reuses
/// the existing <c>IdempotentEntryProps</c> table to avoid duplicating schema.
/// </summary>
public interface IToolIdempotencyStore
{
    /// <summary>
    /// Attempts to reserve <paramref name="toolUseId"/> for the given conversation.
    /// Returns the cached result on a duplicate, or <see cref="ToolIdempotencyReservation.NewReservation"/>
    /// when the caller should execute the tool and finalize via <see cref="CompleteAsync"/>.
    /// </summary>
    Task<ToolIdempotencyReservation> TryReserveAsync(
        string conversationId,
        string toolUseId,
        CancellationToken ct = default);

    /// <summary>Persists the tool output JSON for the previously-reserved key.</summary>
    Task CompleteAsync(
        string conversationId,
        string toolUseId,
        string outputJson,
        CancellationToken ct = default);

    /// <summary>Releases the reservation on failure so a retry can re-run the tool.</summary>
    Task ReleaseAsync(
        string conversationId,
        string toolUseId,
        CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="IToolIdempotencyStore.TryReserveAsync"/>.</summary>
public sealed class ToolIdempotencyReservation
{
    /// <summary>True when this caller owns the reservation and must execute the tool.</summary>
    public required bool IsNew { get; init; }

    /// <summary>Cached output JSON when <see cref="IsNew"/> is false and the prior call completed.</summary>
    public string? CachedOutputJson { get; init; }

    /// <summary>Sentinel reservation indicating the caller must execute the tool.</summary>
    public static readonly ToolIdempotencyReservation NewReservation = new() { IsNew = true };

    /// <summary>Builds a hit reservation that returns the previously-recorded output.</summary>
    public static ToolIdempotencyReservation Hit(string outputJson) => new()
    {
        IsNew = false,
        CachedOutputJson = outputJson
    };
}

/// <summary>
/// In-memory idempotency store backed by a thin <see cref="IIdempotentRepository"/>
/// composition — the registered repository handles the dedup key, an internal
/// dictionary remembers the cached output.
/// </summary>
public sealed class InMemoryToolIdempotencyStore : IToolIdempotencyStore
{
    private readonly IIdempotentRepository _repository;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _outputs = new(StringComparer.Ordinal);

    /// <summary>Creates a store over the given idempotency repository.</summary>
    public InMemoryToolIdempotencyStore(IIdempotentRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <inheritdoc />
    public async Task<ToolIdempotencyReservation> TryReserveAsync(string conversationId, string toolUseId, CancellationToken ct = default)
    {
        var key = BuildKey(conversationId, toolUseId);
        var added = await _repository.Add(key, ct).ConfigureAwait(false);
        if (added) return ToolIdempotencyReservation.NewReservation;

        return _outputs.TryGetValue(key, out var cached)
            ? ToolIdempotencyReservation.Hit(cached)
            : ToolIdempotencyReservation.Hit("{}");
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string conversationId, string toolUseId, string outputJson, CancellationToken ct = default)
    {
        var key = BuildKey(conversationId, toolUseId);
        _outputs[key] = outputJson;
        await _repository.Confirm(key, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string conversationId, string toolUseId, CancellationToken ct = default)
    {
        var key = BuildKey(conversationId, toolUseId);
        _outputs.TryRemove(key, out _);
        await _repository.Remove(key, ct).ConfigureAwait(false);
    }

    private static string BuildKey(string conversationId, string toolUseId) =>
        $"llm-tool:{conversationId}:{toolUseId}";
}
