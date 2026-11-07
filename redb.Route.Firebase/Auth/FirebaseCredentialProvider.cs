using System.Collections.Concurrent;
using FirebaseAdmin;
using Google.Api.Gax;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;

namespace redb.Route.Firebase;

/// <summary>
/// Resolves Firebase credentials from:
/// 1. Explicit JSON path (<c>credentialPath</c> argument, else <see cref="DefaultCredentialPath"/>)
/// 2. <c>GOOGLE_APPLICATION_CREDENTIALS</c> env var (Application Default Credentials)
/// 3. Emulator env vars — <c>FIRESTORE_EMULATOR_HOST</c> (Firestore),
///    <c>STORAGE_EMULATOR_HOST</c> (Storage) — via <see cref="EmulatorDetection.EmulatorOrProduction"/>.
/// <para>
/// Firebase apps are created as NAMED instances (never the process-global <c>[DEFAULT]</c>),
/// so the connector coexists with a host that initialized Firebase Admin SDK itself, and one
/// process can serve several service accounts. Apps are cached per
/// <c>(credentialPath, projectId)</c>, Firestore clients per <c>(projectId, credentialPath)</c>,
/// storage clients per <c>credentialPath</c>. Thread-safe.
/// </para>
/// <para>
/// Public so a named instance can be put into the context registry and referenced from
/// endpoint URIs via <c>connectionFactory=…</c>:
/// <code>
/// context.AddToRegistry("myFirebase", new FirebaseCredentialProvider
/// {
///     DefaultProjectId = "my-project",
///     DefaultCredentialPath = "/secrets/firebase-sa.json",
/// });
/// // fstore://orders?connectionFactory=myFirebase
/// </code>
/// </para>
/// </summary>
public sealed class FirebaseCredentialProvider : IFirebaseCredentialProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<FirebaseApp>> _apps = new();
    private readonly ConcurrentDictionary<string, Lazy<FirestoreDb>> _firestoreDbs = new();
    private readonly ConcurrentDictionary<string, Lazy<StorageClient>> _storageClients = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];
    private int _appCounter;

    /// <summary>Project ID used when the endpoint does not specify one.</summary>
    public string? DefaultProjectId { get; init; }

    /// <summary>Service-account JSON path used when the endpoint does not specify one.</summary>
    public string? DefaultCredentialPath { get; init; }

    /// <inheritdoc />
    public FirebaseApp GetOrCreateApp(string? credentialPath = null, string? projectId = null)
    {
        var credPath = credentialPath ?? DefaultCredentialPath;
        var pid = projectId ?? DefaultProjectId;

        // Lazy inside GetOrAdd so a racing second factory never creates a throwaway app.
        return _apps.GetOrAdd($"{credPath}\n{pid}", _ => new Lazy<FirebaseApp>(() =>
        {
            var options = new AppOptions
            {
                Credential = credPath is not null
                    ? LoadCredential(credPath)
                    : GoogleCredential.GetApplicationDefault(),
            };
            if (!string.IsNullOrWhiteSpace(pid))
                options.ProjectId = pid;

            // Named app: the host's [DEFAULT] instance is never touched.
            var name = $"redb.route:{_instanceId}:{Interlocked.Increment(ref _appCounter)}";
            return FirebaseApp.Create(options, name);
        })).Value;
    }

    /// <inheritdoc />
    public FirestoreDb GetFirestoreDb(string? projectId = null, string? credentialPath = null, string? databaseId = null)
    {
        var credPath = credentialPath ?? DefaultCredentialPath;
        var pid = projectId
                  ?? DefaultProjectId
                  ?? Environment.GetEnvironmentVariable("FIREBASE_PROJECT")
                  ?? throw new InvalidOperationException(
                      "Firebase project id is not configured. Set it on the endpoint (?projectId=...), " +
                      "in AddRedbRouteFirebase(o => o.ProjectId = ...), on the credential provider " +
                      "(DefaultProjectId), or via the FIREBASE_PROJECT environment variable.");

        // Lazy inside GetOrAdd so a racing second factory never builds a throwaway client.
        return _firestoreDbs.GetOrAdd(
            $"{pid}\n{credPath}\n{databaseId}",
            _ => new Lazy<FirestoreDb>(() =>
            {
                var builder = new FirestoreDbBuilder
                {
                    ProjectId = pid,
                    DatabaseId = databaseId,
                    EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
                };
                if (credPath is not null)
                    builder.GoogleCredential = LoadCredential(credPath);
                return builder.Build();
            })).Value;
    }

    /// <inheritdoc />
    public StorageClient GetStorageClient(string? credentialPath = null)
    {
        var credPath = credentialPath ?? DefaultCredentialPath;

        return _storageClients.GetOrAdd(
            credPath ?? "",
            _ => new Lazy<StorageClient>(() =>
            {
                var builder = new StorageClientBuilder
                {
                    EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
                };
                if (credPath is not null)
                    builder.GoogleCredential = LoadCredential(credPath);
                return builder.Build();
            })).Value;
    }

    /// <inheritdoc />
    public UrlSigner GetUrlSigner(string? credentialPath = null)
    {
        var credPath = credentialPath
                       ?? DefaultCredentialPath
                       ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")
                       ?? throw new InvalidOperationException(
                           "Signed URLs require a service-account JSON with a private key to sign with — " +
                           "set credentialPath on the endpoint, DefaultCredentialPath on the provider, " +
                           "or GOOGLE_APPLICATION_CREDENTIALS. Plain ADC cannot sign.");

        return _urlSigners.GetOrAdd(
            credPath,
            _ => new Lazy<UrlSigner>(() => UrlSigner.FromCredential(LoadCredential(credPath)))).Value;
    }

    private readonly ConcurrentDictionary<string, Lazy<UrlSigner>> _urlSigners = new();

    private static GoogleCredential LoadCredential(string credentialPath)
    {
        // GoogleCredential.FromStream is deprecated in favor of CredentialFactory,
        // but CredentialFactory is not available in all target framework versions.
#pragma warning disable CS0618
        using var stream = File.OpenRead(credentialPath);
        return GoogleCredential.FromStream(stream);
#pragma warning restore CS0618
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var client in _storageClients.Values)
        {
            if (client.IsValueCreated)
                client.Value.Dispose();
        }
        // Delete only OUR named apps — a host-owned [DEFAULT] is never ours.
        foreach (var app in _apps.Values)
        {
            if (app.IsValueCreated)
                app.Value.Delete();
        }
    }
}
