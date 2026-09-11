using FirebaseAdmin;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;

namespace redb.Route.Firebase;

/// <summary>
/// Abstraction for Firebase credential resolution.
/// Enables mocking in unit tests without real service accounts.
/// </summary>
public interface IFirebaseCredentialProvider
{
    /// <summary>
    /// Gets or creates a <see cref="FirebaseApp"/> using the specified credentials.
    /// Thread-safe with lazy initialization.
    /// </summary>
    /// <param name="credentialPath">Optional path to service-account JSON file.</param>
    /// <param name="projectId">Optional Firebase project ID override.</param>
    /// <returns>Initialized <see cref="FirebaseApp"/> instance.</returns>
    FirebaseApp GetOrCreateApp(string? credentialPath = null, string? projectId = null);

    /// <summary>
    /// Gets a <see cref="FirestoreDb"/> for the specified project. Clients are cached per
    /// <c>(projectId, credentialPath)</c> pair. Detects <c>FIRESTORE_EMULATOR_HOST</c>
    /// for local development; falls back to production with the given service-account file
    /// or Application Default Credentials.
    /// </summary>
    /// <param name="projectId">Optional project ID override.</param>
    /// <param name="credentialPath">Optional path to service-account JSON file.</param>
    /// <returns>Firestore database client.</returns>
    FirestoreDb GetFirestoreDb(string? projectId = null, string? credentialPath = null, string? databaseId = null);

    /// <summary>
    /// Gets a <see cref="StorageClient"/>. Clients are cached per <paramref name="credentialPath"/>.
    /// Detects <c>STORAGE_EMULATOR_HOST</c> for local development; falls back to production
    /// with the given service-account file or Application Default Credentials.
    /// </summary>
    /// <param name="credentialPath">Optional path to service-account JSON file.</param>
    /// <returns>Google Cloud Storage client.</returns>
    StorageClient GetStorageClient(string? credentialPath = null);

    /// <summary>
    /// Gets a <see cref="UrlSigner"/> for signed download URLs. Requires a service-account
    /// JSON with a private key — plain ADC cannot sign. Default implementation throws
    /// <see cref="NotSupportedException"/> so test doubles stay small.
    /// </summary>
    /// <param name="credentialPath">Optional path to service-account JSON file.</param>
    UrlSigner GetUrlSigner(string? credentialPath = null)
        => throw new NotSupportedException(
            $"{GetType().Name} does not provide a UrlSigner.");
}
