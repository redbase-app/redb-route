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

    /// <summary>Skip previously processed objects (in-memory idempotent repository).</summary>
    public bool Idempotent { get; set; }

    // ── Consumer: Filtering ──

    /// <summary>Include glob pattern for object names.</summary>
    public string? Include { get; set; }

    /// <summary>Exclude glob pattern for object names.</summary>
    public string? Exclude { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(CredentialPath)
            && string.IsNullOrWhiteSpace(ConnectionFactory)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIREBASE_STORAGE_EMULATOR_HOST")))
            throw new ArgumentOutOfRangeException(nameof(CredentialPath),
                "CredentialPath, ConnectionFactory, GOOGLE_APPLICATION_CREDENTIALS, or FIREBASE_STORAGE_EMULATOR_HOST required");

        if (Delay < 100)
            throw new ArgumentOutOfRangeException(nameof(Delay), "Delay must be >= 100ms");

        if (MaxMessagesPerPoll < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxMessagesPerPoll), "MaxMessagesPerPoll must be >= 1");
    }
}
