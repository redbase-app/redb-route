using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Components;

/// <summary>
/// Resolves <see cref="IClaimCheckRepository"/> instances for Claim Check steps.
/// Mirrors <see cref="RegistryIdempotentRepositoryProvider"/>: repositories live in the
/// <see cref="IRouteContext"/> registry under keys of the form <c>claimcheck:{name}</c>,
/// so a route can refer to one by string name with no extra DI plumbing.
/// </summary>
public static class ClaimCheckRepositoryRegistry
{
    /// <summary>Registry key prefix used for named claim check repositories.</summary>
    public const string KeyPrefix = "claimcheck:";

    /// <summary>Registry key holding the context-wide default repository.</summary>
    public const string DefaultKey = KeyPrefix + "__default";

    private static readonly object DefaultLock = new();

    /// <summary>
    /// Registers an <see cref="IClaimCheckRepository"/> under a logical name, so route steps
    /// can refer to it as <c>.ClaimCheck(operation, repositoryName: "large-payloads")</c>.
    /// </summary>
    public static IRouteContext AddClaimCheckRepository(
        this IRouteContext context, string name, IClaimCheckRepository repository)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(repository);

        context.AddToRegistry(KeyPrefix + name, repository);
        return context;
    }

    /// <summary>
    /// Registers the repository used by Claim Check steps that name none.
    /// Without it the context falls back to a shared <see cref="InMemoryClaimCheckRepository"/>.
    /// </summary>
    public static IRouteContext SetDefaultClaimCheckRepository(
        this IRouteContext context, IClaimCheckRepository repository)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);

        context.AddToRegistry(DefaultKey, repository);
        return context;
    }

    /// <summary>
    /// Resolves the repository for a Claim Check step.
    /// Order: the named registry entry, then a registered default, then an
    /// <see cref="IClaimCheckRepository"/> service, then a shared in-memory repository
    /// created once per context.
    /// </summary>
    /// <param name="context">Route context being compiled.</param>
    /// <param name="repositoryName">Logical name, or null for the default.</param>
    /// <exception cref="InvalidOperationException">A name was given but nothing is registered under it.</exception>
    public static IClaimCheckRepository ResolveClaimCheckRepository(
        this IRouteContext context, string? repositoryName = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.IsNullOrEmpty(repositoryName))
        {
            var named = context.GetFromRegistry<IClaimCheckRepository>(KeyPrefix + repositoryName);
            if (named is null)
            {
                throw new InvalidOperationException(
                    $"No IClaimCheckRepository registered under name '{repositoryName}'. " +
                    $"Register via context.AddClaimCheckRepository(\"{repositoryName}\", repository).");
            }

            return named;
        }

        var registeredDefault = context.GetFromRegistry<IClaimCheckRepository>(DefaultKey);
        if (registeredDefault is not null)
            return registeredDefault;

        var fromServices = context.GetService<IClaimCheckRepository>();
        if (fromServices is not null)
            return fromServices;

        // The fallback must be shared across steps: a Set in one step and a Get in another
        // have to reach the same store, otherwise the claim key resolves to nothing.
        lock (DefaultLock)
        {
            var existing = context.GetFromRegistry<IClaimCheckRepository>(DefaultKey);
            if (existing is not null)
                return existing;

            var created = new InMemoryClaimCheckRepository();
            context.AddToRegistry(DefaultKey, created);
            return created;
        }
    }
}
