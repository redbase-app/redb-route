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
    /// Gets a <see cref="FirestoreDb"/> for the specified project.
    /// Automatically detects <c>FIRESTORE_EMULATOR_HOST</c> for local testing.
    /// </summary>
    /// <param name="projectId">Optional project ID override.</param>
    /// <returns>Firestore database client.</returns>
    FirestoreDb GetFirestoreDb(string? projectId = null);

    /// <summary>
    /// Gets a <see cref="StorageClient"/> using Application Default Credentials.
    /// </summary>
    /// <returns>Google Cloud Storage client.</returns>
    StorageClient GetStorageClient();
}
