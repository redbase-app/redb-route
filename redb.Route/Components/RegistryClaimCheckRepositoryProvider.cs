using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Components;

/// <summary>
/// Resolves <see cref="IClaimCheckRepository"/> instances for Claim Check steps. A named repository is found in the
/// <see cref="IRouteContext"/> registry by its bare name, checked for type (Camel's <c>lookupByNameAndType</c>), the
/// same key every other <c>#name</c> reference uses. The repository of steps that name none is the context's
/// <see cref="IClaimCheckRepository"/> service, looked up by type.
/// </summary>
public static class ClaimCheckRepositoryRegistry
{
    private static readonly object DefaultLock = new();

    /// <summary>
    /// Registers an <see cref="IClaimCheckRepository"/> in the context registry under <paramref name="name"/>, so
    /// route steps can refer to it as <c>.ClaimCheck(operation, repositoryName: "large-payloads")</c>.
    /// </summary>
    public static IRouteContext AddClaimCheckRepository(
        this IRouteContext context, string name, IClaimCheckRepository repository)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(repository);

        context.AddToRegistry(RegistryLookup.Key(name), repository);
        return context;
    }

    /// <summary>
    /// Registers the repository used by Claim Check steps that name none, as the context's
    /// <see cref="IClaimCheckRepository"/> service. Without it the context falls back to a shared
    /// <see cref="InMemoryClaimCheckRepository"/>.
    /// </summary>
    public static IRouteContext SetDefaultClaimCheckRepository(
        this IRouteContext context, IClaimCheckRepository repository)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);

        context.AddService(typeof(IClaimCheckRepository), repository);
        return context;
    }

    /// <summary>
    /// Resolves the repository for a Claim Check step. A name is looked up in the registry; without one, the context's
    /// <see cref="IClaimCheckRepository"/> service (<see cref="SetDefaultClaimCheckRepository"/>, or one the host
    /// registered), else a shared in-memory repository created once per context and kept as that service.
    /// </summary>
    /// <param name="context">Route context being compiled.</param>
    /// <param name="repositoryName">Registry name, or null for the default.</param>
    /// <exception cref="InvalidOperationException">A name was given and nothing, or an object of another type, is registered under it.</exception>
    public static IClaimCheckRepository ResolveClaimCheckRepository(
        this IRouteContext context, string? repositoryName = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.IsNullOrEmpty(repositoryName))
        {
            var key = RegistryLookup.Key(repositoryName);
            return RegistryLookup.Find<IClaimCheckRepository>(context, key)
                ?? throw new InvalidOperationException(
                    $"Nothing is registered under '{key}': register an IClaimCheckRepository with " +
                    $"context.AddClaimCheckRepository(\"{key}\", repository), or declare <bean name=\"{key}\" type=\"...\"/> in Route-XML.");
        }

        var fromServices = context.GetService<IClaimCheckRepository>();
        if (fromServices is not null)
            return fromServices;

        // The fallback must be shared across steps: a Set in one step and a Get in another
        // have to reach the same store, otherwise the claim key resolves to nothing.
        lock (DefaultLock)
        {
            var existing = context.GetService<IClaimCheckRepository>();
            if (existing is not null)
                return existing;

            var created = new InMemoryClaimCheckRepository();
            context.AddService(typeof(IClaimCheckRepository), created);
            return created;
        }
    }
}
