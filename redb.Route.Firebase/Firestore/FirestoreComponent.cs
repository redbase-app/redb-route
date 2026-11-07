using Google.Cloud.Firestore;
using redb.Route.Abstractions;
using redb.Route.Extensions;
using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Firestore component registered with scheme <c>fstore</c>.
/// Creates endpoints for CRUD operations and realtime listeners.
/// </summary>
internal sealed class FirestoreComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "fstore";

    /// <summary>Shared credential provider. Set by DI registration or manually.</summary>
    internal IFirebaseCredentialProvider? CredentialProvider { get; set; }

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new FirestoreEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new FirestoreEndpoint(uri, this, options);
    }
}

/// <summary>
/// Firestore endpoint. Creates producers for CRUD operations and consumers for realtime listeners.
/// The URI path specifies the collection (e.g. <c>fstore://users</c>, <c>fstore://users/uid/orders</c>).
/// </summary>
internal sealed class FirestoreEndpoint : EndpointBase<FirestoreEndpointOptions>, IDisposable
{
    private FirestoreDb? _db;
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal FirestoreEndpoint(EndpointUri uri, FirestoreComponent component, FirestoreEndpointOptions options)
        : base(uri, component, options) { }

    /// <summary>Collection path from the URI.</summary>
    internal string CollectionPath => Uri.Path;

    /// <summary>The owning Firestore component.</summary>
    internal FirestoreComponent FirestoreComponent => (FirestoreComponent)Component;

    /// <summary>Typed options for external access.</summary>
    internal FirestoreEndpointOptions EndpointOptions => Options;

    /// <summary>Gets or creates a shared <see cref="FirestoreDb"/> client.</summary>
    internal async Task<FirestoreDb> GetOrCreateDbAsync(CancellationToken ct = default)
    {
        if (_db is not null) return _db;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_db is not null) return _db;

            var provider = ResolveCredentialProvider();
            _db = provider.GetFirestoreDb(Options.ProjectId, Options.CredentialPath, Options.DatabaseId);
            return _db;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
        => new FirestoreProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);

        // ${...} in Where is producer-Query-only (Г6): the consumer query is built once per
        // subscription/poll loop — there is no exchange to resolve the template against.
        if (Options.Where?.Contains("${") == true)
            throw new InvalidOperationException(
                "Where with ${...} expressions is supported only on the producer Query operation: " +
                "the consumer query is built once per subscription, there is no exchange to resolve against.");

        return Options.Realtime
            ? new FirestoreConsumer(this, processor, Options)
            : new FirestorePollingConsumer(this, processor, Options);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }

    private IFirebaseCredentialProvider ResolveCredentialProvider()
    {
        // A set-but-unknown name fails loud — never a silent fallback (Ф11 Ж-1).
        if (!string.IsNullOrEmpty(Options.ConnectionFactory))
            return FirestoreComponent.Context
                .GetRequiredFromRegistry<IFirebaseCredentialProvider>(Options.ConnectionFactory);

        return FirestoreComponent.CredentialProvider
               ?? throw new InvalidOperationException(
                   "No IFirebaseCredentialProvider available. Register via AddRedbRouteFirebase() or set ConnectionFactory.");
    }
}
