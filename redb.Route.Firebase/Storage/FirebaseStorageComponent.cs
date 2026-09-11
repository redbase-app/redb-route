using Google.Cloud.Storage.V1;
using redb.Route.Abstractions;
using redb.Route.Extensions;
using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Firebase Storage component registered with scheme <c>fbstorage</c>.
/// Firebase Storage = GCS bucket — uses <c>Google.Cloud.Storage.V1</c>.
/// </summary>
internal sealed class FirebaseStorageComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "fbstorage";

    /// <summary>Shared credential provider. Set by DI registration or manually.</summary>
    internal IFirebaseCredentialProvider? CredentialProvider { get; set; }

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new FirebaseStorageEndpoint(uri, this, options);
    }
}

/// <summary>
/// Firebase Storage endpoint. Creates producers for file operations
/// and consumers for polling bucket contents.
/// URI path format: <c>fbstorage://bucket-name</c> or <c>fbstorage://bucket-name/prefix</c>.
/// </summary>
internal sealed class FirebaseStorageEndpoint : EndpointBase<FirebaseStorageEndpointOptions>, IDisposable
{
    private StorageClient? _client;
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal FirebaseStorageEndpoint(EndpointUri uri, FirebaseStorageComponent component, FirebaseStorageEndpointOptions options)
        : base(uri, component, options)
    {
        // Parse bucket name from URI path (first segment)
        var path = uri.Path;
        var slashIdx = path.IndexOf('/');
        BucketName = options.BucketName ?? (slashIdx > 0 ? path[..slashIdx] : path);

        // The path after the bucket is folder-like: "bucket/uploads" and "bucket/uploads/"
        // both mean the "uploads/" prefix — Upload must never produce "uploadsfile.txt".
        // A raw (non-folder) string prefix is still available via the Prefix option.
        var rawPrefix = slashIdx > 0 ? path[(slashIdx + 1)..] : null;
        ObjectPrefix = string.IsNullOrEmpty(rawPrefix) ? null
            : rawPrefix.EndsWith('/') ? rawPrefix : rawPrefix + "/";
    }

    /// <summary>Bucket name parsed from the URI path or options.</summary>
    internal string BucketName { get; }

    /// <summary>Optional object prefix from the URI path.</summary>
    internal string? ObjectPrefix { get; }

    /// <summary>The owning Storage component.</summary>
    internal FirebaseStorageComponent StorageComponent => (FirebaseStorageComponent)Component;

    /// <summary>Typed options for external access.</summary>
    internal FirebaseStorageEndpointOptions EndpointOptions => Options;

    /// <summary>Gets or creates a shared <see cref="StorageClient"/>.</summary>
    internal async Task<StorageClient> GetOrCreateClientAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;

            var provider = ResolveCredentialProvider();
            _client = provider.GetStorageClient(Options.CredentialPath);
            return _client;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Signer for CreateDownloadLink — requires a service-account credential.</summary>
    internal Google.Cloud.Storage.V1.UrlSigner GetUrlSigner()
        => ResolveCredentialProvider().GetUrlSigner(Options.CredentialPath);

    /// <summary>
    /// Project id for bucket-level operations (CreateBucket/ListBuckets/AutoCreateBucket) —
    /// GCS needs it to own the bucket; a missing project is a loud error, never a guess.
    /// </summary>
    internal string RequireProjectId()
        => Options.ProjectId
           ?? Environment.GetEnvironmentVariable("FIREBASE_PROJECT")
           ?? throw new InvalidOperationException(
               "Bucket operations require a project id. Set it on the endpoint (?projectId=...) " +
               "or via the FIREBASE_PROJECT environment variable.");

    /// <inheritdoc />
    public override IProducer CreateProducer()
        => new FirebaseStorageProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new FirebaseStorageConsumer(this, processor, Options);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Do NOT dispose _client — it's owned by the shared IFirebaseCredentialProvider singleton.
        // Only dispose our own synchronization primitive.
        _lock.Dispose();
    }

    private IFirebaseCredentialProvider ResolveCredentialProvider()
    {
        // A set-but-unknown name fails loud -- never a silent fallback (Ф11 Ж-1).
        if (!string.IsNullOrEmpty(Options.ConnectionFactory))
            return StorageComponent.Context
                .GetRequiredFromRegistry<IFirebaseCredentialProvider>(Options.ConnectionFactory);

        return StorageComponent.CredentialProvider
               ?? throw new InvalidOperationException(
                   "No IFirebaseCredentialProvider available. Register via AddRedbRouteFirebase() or set ConnectionFactory.");
    }
}
