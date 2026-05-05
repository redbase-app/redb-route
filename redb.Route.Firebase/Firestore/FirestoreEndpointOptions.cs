using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Endpoint options for the Firestore component.
/// Supports both producer (CRUD/Query) and consumer (realtime listener / polling) modes.
/// </summary>
public sealed class FirestoreEndpointOptions : EndpointOptions
{
    // ── Auth ──

    /// <summary>Path to the Firebase service-account JSON file.</summary>
    public string? CredentialPath { get; set; }

    /// <summary>Firebase/GCP project ID.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Named <see cref="IFirebaseCredentialProvider"/> reference from the registry.</summary>
    public string? ConnectionFactory { get; set; }

    // ── Operation ──

    /// <summary>Producer operation type. Default: <see cref="FirestoreOperationType.Set"/>.</summary>
    public FirestoreOperationType Operation { get; set; } = FirestoreOperationType.Set;

    // ── Document targeting ──

    /// <summary>Document ID. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? DocumentId { get; set; }

    // ── Query (consumer &amp; producer Query) ──

    /// <summary>Where filter: <c>"status==pending"</c> or <c>"age&gt;18"</c>. Multiple separated by <c>;</c>.</summary>
    public string? Where { get; set; }

    /// <summary>Order by field: <c>"createdAt"</c> or <c>"createdAt desc"</c>.</summary>
    public string? OrderBy { get; set; }

    /// <summary>Maximum number of documents to return.</summary>
    public int? Limit { get; set; }

    /// <summary>Pagination offset.</summary>
    public int? Offset { get; set; }

    // ── Set/Update ──

    /// <summary>Merge fields on Set instead of overwriting the entire document.</summary>
    public bool Merge { get; set; }

    // ── Consumer: Realtime Listener ──

    /// <summary>Use snapshot listener for realtime updates (default for consumer).</summary>
    public bool Realtime { get; set; } = true;

    /// <summary>Emit on metadata-only changes (e.g. pending writes).</summary>
    public bool IncludeMetadataChanges { get; set; }

    // ── Consumer: Polling fallback ──

    /// <summary>Poll interval (ms) for non-realtime mode. Default: 5000.</summary>
    public int Delay { get; set; } = 5000;

    /// <summary>Initial delay (ms) before first poll. Default: 1000.</summary>
    public int InitialDelay { get; set; } = 1000;

    // ── Serialization ──

    /// <summary>Body as raw JSON string instead of <c>Dictionary&lt;string, object?&gt;</c>.</summary>
    public bool RawJson { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(CredentialPath)
            && string.IsNullOrWhiteSpace(ConnectionFactory)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST")))
            throw new ArgumentOutOfRangeException(nameof(CredentialPath),
                "CredentialPath, ConnectionFactory, GOOGLE_APPLICATION_CREDENTIALS, or FIRESTORE_EMULATOR_HOST is required");

        if (Delay < 100)
            throw new ArgumentOutOfRangeException(nameof(Delay), "Delay must be >= 100ms");

        if (Limit is not null && Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(Limit), "Limit must be >= 1");
    }
}
