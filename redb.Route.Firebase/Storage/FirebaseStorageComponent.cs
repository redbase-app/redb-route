using Google.Cloud.Storage.V1;
using redb.Route.Abstractions;
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
        ObjectPrefix = slashIdx > 0 ? path[(slashIdx + 1)..] : null;
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
            _client = provider.GetStorageClient();
            return _client;
        }
        finally
        {
            _lock.Release();
        }
    }

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
        if (!string.IsNullOrEmpty(Options.ConnectionFactory))
        {
            var fromRegistry = StorageComponent.Context?
                .GetFromRegistry<IFirebaseCredentialProvider>(Options.ConnectionFactory);
            if (fromRegistry is not null) return fromRegistry;
        }

        return StorageComponent.CredentialProvider
               ?? throw new InvalidOperationException(
                   "No IFirebaseCredentialProvider available. Register via AddRedbRouteFirebase() or set ConnectionFactory.");
    }
}
