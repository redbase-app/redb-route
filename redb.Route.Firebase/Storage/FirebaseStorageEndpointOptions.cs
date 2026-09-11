using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Endpoint options for the Firebase Storage component.
/// Supports producer (upload/download/delete/list/metadata) and consumer (polling) modes.
/// Firebase Storage = GCS bucket — compatible with <c>Google.Cloud.Storage.V1</c>.
/// </summary>
public sealed class FirebaseStorageEndpointOptions : EndpointOptions
{
    // ── Auth ──

    /// <summary>Path to the Firebase service-account JSON file.</summary>
    public string? CredentialPath { get; set; }

    /// <summary>Firebase/GCP project ID.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Named <see cref="IFirebaseCredentialProvider"/> reference from the registry.</summary>
    public string? ConnectionFactory { get; set; }

    // ── Target ──

    /// <summary>Producer operation type. Default: <see cref="FirebaseStorageOperationType.Upload"/>.</summary>
    public FirebaseStorageOperationType Operation { get; set; } = FirebaseStorageOperationType.Upload;

    /// <summary>Override bucket name (default from URI path).</summary>
    public string? BucketName { get; set; }

    // ── Upload ──

    /// <summary>Object name/key. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? ObjectName { get; set; }

    /// <summary>MIME content type override.</summary>
    public string? ContentType { get; set; }

    /// <summary>Cache-Control header (e.g. <c>"public, max-age=3600"</c>).</summary>
    public string? CacheControl { get; set; }

    // ── Copy ──

    /// <summary>Destination object name for the Copy operation. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? DestinationObjectName { get; set; }

    /// <summary>Destination bucket for the Copy operation (default: source bucket).</summary>
    public string? DestinationBucket { get; set; }

    // ── Signed URLs ──

    /// <summary>TTL for CreateDownloadLink signed URLs, in milliseconds. Default: 1 hour.</summary>
    public long SignedUrlExpiration { get; set; } = 3_600_000;

    // ── Download ──

    /// <summary>Download body as Stream (true) or byte[] (false).</summary>
    public bool StreamBody { get; set; }

    // ── Consumer: Polling ──

    /// <summary>Poll interval (ms). Default: 5000.</summary>
    public int Delay { get; set; } = 5000;

    /// <summary>Initial delay (ms) before first poll. Default: 1000.</summary>
    public int InitialDelay { get; set; } = 1000;

    /// <summary>Object name prefix filter for listing.</summary>
    public string? Prefix { get; set; }

    /// <summary>Maximum number of objects to process per poll cycle.</summary>
    public int MaxMessagesPerPoll { get; set; } = 10;

    /// <summary>Download object body into exchange (default: true).</summary>
    public bool IncludeBody { get; set; } = true;

    /// <summary>Delete object after successful consumer processing.</summary>
    public bool DeleteAfterRead { get; set; }

    /// <summary>Move objects to this prefix after processing (copy + delete).</summary>
    public string? MoveAfterRead { get; set; }

    /// <summary>
    /// Move objects whose processing FAILED to this prefix (copy + delete) — a quarantine
    /// pocket so the poll loop stops retrying them. When unset, a failed object stays in
    /// place and is offered again on the next poll.
    /// </summary>
    public string? MoveFailed { get; set; }

    /// <summary>Skip previously processed objects (in-memory idempotent repository).</summary>
    public bool Idempotent { get; set; }

    /// <summary>
    /// Name of an <c>IIdempotentRepository</c> registered via
    /// <c>context.AddIdempotentRepository(name, repo)</c> — the same contract the route-level
    /// IdempotentConsumer EIP uses. With a persistent repository (RedbIdempotentRepository)
    /// deduplication survives restarts and scale-out. Takes precedence over the in-memory
    /// <see cref="Idempotent"/> flag.
    /// </summary>
    public string? IdempotentRepository { get; set; }

    /// <summary>
    /// Create the bucket on consumer start when it does not exist (requires
    /// <see cref="ProjectId"/> or <c>FIREBASE_PROJECT</c>). Default: false.
    /// </summary>
    public bool AutoCreateBucket { get; set; }

    // ── Consumer: Filtering ──

    /// <summary>Include glob pattern for object names.</summary>
    public string? Include { get; set; }

    /// <summary>Exclude glob pattern for object names.</summary>
    public string? Exclude { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        // STORAGE_EMULATOR_HOST is the env var Google.Cloud.Storage.V1 actually honors
        // (FIREBASE_STORAGE_EMULATOR_HOST belongs to the Firebase client SDKs, not this one).
        if (string.IsNullOrWhiteSpace(CredentialPath)
            && string.IsNullOrWhiteSpace(ConnectionFactory)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STORAGE_EMULATOR_HOST")))
            throw new ArgumentOutOfRangeException(nameof(CredentialPath),
                "CredentialPath, ConnectionFactory, GOOGLE_APPLICATION_CREDENTIALS, or STORAGE_EMULATOR_HOST required");

        if (Delay < 100)
            throw new ArgumentOutOfRangeException(nameof(Delay), "Delay must be >= 100ms");

        if (MaxMessagesPerPoll < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxMessagesPerPoll), "MaxMessagesPerPoll must be >= 1");
    }
}
