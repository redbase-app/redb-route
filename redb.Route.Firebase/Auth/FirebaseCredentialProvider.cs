using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;

namespace redb.Route.Firebase;

/// <summary>
/// Resolves Firebase credentials from:
/// 1. Explicit JSON path (<c>credentialPath</c> option)
/// 2. <c>GOOGLE_APPLICATION_CREDENTIALS</c> env var
/// 3. Firebase Emulator env vars (<c>FIRESTORE_EMULATOR_HOST</c>, etc.)
/// Thread-safe, lazy initialization with double-check lock.
/// </summary>
internal sealed class FirebaseCredentialProvider : IFirebaseCredentialProvider, IDisposable
{
    private FirebaseApp? _app;
    private FirestoreDb? _firestoreDb;
    private StorageClient? _storageClient;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _credentialPath;

    /// <inheritdoc />
    public FirebaseApp GetOrCreateApp(string? credentialPath = null, string? projectId = null)
    {
        if (_app is not null)
        {
            if (credentialPath is not null && _credentialPath != credentialPath)
                throw new InvalidOperationException(
                    $"FirebaseApp already initialized with credential '{_credentialPath}'. " +
                    $"Cannot re-initialize with '{credentialPath}'. Use a separate IFirebaseCredentialProvider instance.");
            return _app;
        }

        _lock.Wait();
        try
        {
            if (_app is not null) return _app;

            var options = new AppOptions();

            if (!string.IsNullOrWhiteSpace(credentialPath))
            {
                // GoogleCredential.FromStream is deprecated in favor of CredentialFactory,
                // but CredentialFactory is not available in all target framework versions.
#pragma warning disable CS0618
                using var stream = File.OpenRead(credentialPath);
                options.Credential = GoogleCredential.FromStream(stream);
#pragma warning restore CS0618
            }
            else
            {
                options.Credential = GoogleCredential.GetApplicationDefault();
            }

            if (!string.IsNullOrWhiteSpace(projectId))
                options.ProjectId = projectId;

            _credentialPath = credentialPath;
            _app = FirebaseApp.Create(options);
            return _app;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public FirestoreDb GetFirestoreDb(string? projectId = null)
    {
        if (_firestoreDb is not null) return _firestoreDb;

        _lock.Wait();
        try
        {
            if (_firestoreDb is not null) return _firestoreDb;

            var pid = projectId
                      ?? Environment.GetEnvironmentVariable("FIREBASE_PROJECT")
                      ?? "default-project";

            // FirestoreDb.Create() auto-detects FIRESTORE_EMULATOR_HOST
            _firestoreDb = FirestoreDb.Create(pid);
            return _firestoreDb;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public StorageClient GetStorageClient()
    {
        if (_storageClient is not null) return _storageClient;

        _lock.Wait();
        try
        {
            if (_storageClient is not null) return _storageClient;

            _storageClient = StorageClient.Create();
            return _storageClient;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _storageClient?.Dispose();
        _app?.Delete();
        _lock.Dispose();
    }
}
