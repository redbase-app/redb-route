using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Firebase;

/// <summary>
/// Extension methods for registering Firebase components (FCM, Firestore, Storage) in DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Firebase components (<c>fcm://</c>, <c>fstore://</c>, <c>fbstorage://</c>) in the route context.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteFirebase();
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteFirebase(
        this IServiceCollection services,
        Action<FirebaseOptions>? configure = null)
    {
        var options = new FirebaseOptions();
        configure?.Invoke(options);

        services.AddSingleton<IFirebaseCredentialProvider>(sp =>
        {
            var provider = new FirebaseCredentialProvider();
            if (options.CredentialPath is not null)
                provider.GetOrCreateApp(options.CredentialPath, options.ProjectId);
            return provider;
        });

        services.AddSingleton<FcmComponent>();
        services.AddSingleton<FirestoreComponent>();
        services.AddSingleton<FirebaseStorageComponent>();

        services.AddSingleton<IFirebaseComponentRegistrar>(sp =>
        {
            var credProvider = sp.GetRequiredService<IFirebaseCredentialProvider>();
            var context = sp.GetRequiredService<IRouteContext>();

            var fcm = sp.GetRequiredService<FcmComponent>();
            fcm.CredentialProvider = credProvider;
            context.AddComponent(fcm);

            var firestore = sp.GetRequiredService<FirestoreComponent>();
            firestore.CredentialProvider = credProvider;
            context.AddComponent(firestore);

            var storage = sp.GetRequiredService<FirebaseStorageComponent>();
            storage.CredentialProvider = credProvider;
            context.AddComponent(storage);

            return new FirebaseComponentRegistrar();
        });

        return services;
    }
}

/// <summary>
/// Configuration options for the Firebase connector registration.
/// </summary>
public sealed class FirebaseOptions
{
    /// <summary>Path to the Firebase service-account JSON file.</summary>
    public string? CredentialPath { get; set; }

    /// <summary>Firebase/GCP project ID.</summary>
    public string? ProjectId { get; set; }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface IFirebaseComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class FirebaseComponentRegistrar : IFirebaseComponentRegistrar;
