using redb.Route.Abstractions;

namespace redb.Route.Components;

/// <summary>
/// Default <see cref="IIdempotentRepositoryProvider"/>: resolves a repository from the <see cref="IRouteContext"/>
/// registry by its bare name, checked for type — Camel's <c>lookupByNameAndType</c>. The name is the registry key as
/// is, the same key every other <c>#name</c> reference uses, so a bean declared in markup under <c>dedup</c> is the
/// repository <c>repository="#dedup"</c> names.
/// </summary>
public sealed class RegistryIdempotentRepositoryProvider : IIdempotentRepositoryProvider
{
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
        if (TryGet(name, out var repository))
            return repository;

        var key = RegistryLookup.Key(name);
        throw new InvalidOperationException(
            $"Nothing is registered under '{key}': register an IIdempotentRepository with " +
            $"context.AddIdempotentRepository(\"{key}\", repository), or declare <bean name=\"{key}\" type=\"...\"/> in Route-XML.");
    }

    /// <summary>
    /// Finds the repository registered under <paramref name="name"/>. <c>false</c> when nothing is registered there;
    /// an object of another type under the name is a configuration error and throws.
    /// </summary>
    public bool TryGet(string name, out IIdempotentRepository repository)
    {
        repository = null!;
        if (string.IsNullOrEmpty(name)) return false;
        var found = RegistryLookup.Find<IIdempotentRepository>(_context, name);
        if (found is null) return false;
        repository = found;
        return true;
    }
}

/// <summary>A registry lookup by bare name, checked for type: nothing there, or the wrong type, are told apart.</summary>
internal static class RegistryLookup
{
    /// <summary>The registry key of a reference: <c>#name</c> and <c>name</c> are the same key.</summary>
    public static string Key(string name) => name.StartsWith('#') ? name[1..] : name;

    /// <summary>The object under <paramref name="name"/> as <typeparamref name="T"/>; <c>null</c> when nothing is there.</summary>
    /// <exception cref="InvalidOperationException">An object of another type is registered under the name.</exception>
    public static T? Find<T>(IRouteContext context, string name) where T : class
    {
        var key = Key(name);
        var found = context.GetFromRegistry<object>(key);
        return found switch
        {
            null => null,
            T typed => typed,
            _ => throw new InvalidOperationException(
                $"'{key}' is registered as {found.GetType().FullName}, which is not an {typeof(T).Name}."),
        };
    }
}

/// <summary>
/// Convenience extensions for working with named idempotent repositories on
/// <see cref="IRouteContext"/>.
/// </summary>
public static class IdempotentRepositoryRegistryExtensions
{
    /// <summary>
    /// Registers an <see cref="IIdempotentRepository"/> in the context registry under <paramref name="name"/>, the
    /// name routes refer to it by (<c>IdempotentConsumer(..., "name")</c>, <c>repository="#name"</c>,
    /// <c>idempotentRepository=name</c> on a consumer).
    /// </summary>
    public static IRouteContext AddIdempotentRepository(this IRouteContext context,
        string name, IIdempotentRepository repository)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(repository);
        context.AddToRegistry(RegistryLookup.Key(name), repository);
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
