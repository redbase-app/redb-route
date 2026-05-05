using redb.Route.Abstractions;

namespace redb.Route.Components;

/// <summary>
/// Default <see cref="IIdempotentRepositoryProvider"/> implementation that resolves
/// repositories from the <see cref="IRouteContext"/> registry under keys of the form
/// <c>idempotent:{name}</c>. This keeps named lookup zero-config: register once,
/// route DSL refers by string name, no separate DI plumbing needed.
/// </summary>
public sealed class RegistryIdempotentRepositoryProvider : IIdempotentRepositoryProvider
{
    /// <summary>Registry key prefix used for idempotent repositories.</summary>
    public const string KeyPrefix = "idempotent:";

    private readonly IRouteContext _context;

    /// <summary>Creates a provider backed by the given route context's registry.</summary>
    public RegistryIdempotentRepositoryProvider(IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public IIdempotentRepository Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var repo = _context.GetFromRegistry<IIdempotentRepository>(KeyPrefix + name);
        if (repo is null)
            throw new InvalidOperationException(
                $"No IIdempotentRepository registered under name '{name}'. " +
                $"Register via context.AddToRegistry(\"{KeyPrefix}{name}\", repository).");
        return repo;
    }

    /// <inheritdoc />
    public bool TryGet(string name, out IIdempotentRepository repository)
    {
        repository = null!;
        if (string.IsNullOrEmpty(name)) return false;
        var found = _context.GetFromRegistry<IIdempotentRepository>(KeyPrefix + name);
        if (found is null) return false;
        repository = found;
        return true;
    }
}

/// <summary>
/// Convenience extensions for working with named idempotent repositories on
/// <see cref="IRouteContext"/>.
/// </summary>
public static class IdempotentRepositoryRegistryExtensions
{
    /// <summary>
    /// Registers an <see cref="IIdempotentRepository"/> under the given logical name.
    /// Routes can later refer to it via the named DSL overload of <c>IdempotentConsumer</c>.
    /// </summary>
    public static IRouteContext AddIdempotentRepository(this IRouteContext context,
        string name, IIdempotentRepository repository)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(repository);
        context.AddToRegistry(RegistryIdempotentRepositoryProvider.KeyPrefix + name, repository);
        return context;
    }

    /// <summary>
    /// Resolves the registered <see cref="IIdempotentRepositoryProvider"/> on the context,
    /// or returns a default one backed by the context registry if no override was added.
    /// </summary>
    public static IIdempotentRepositoryProvider GetIdempotentRepositoryProvider(this IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var custom = context.GetService<IIdempotentRepositoryProvider>();
        return custom ?? new RegistryIdempotentRepositoryProvider(context);
    }
}
