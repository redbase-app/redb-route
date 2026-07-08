namespace redb.Route.Abstractions;

/// <summary>
/// Lookup service for named <see cref="IIdempotentRepository"/> instances. Lets routes refer
/// to repositories by string name (resolved lazily during pipeline execution) instead of
/// holding a hard reference at definition time.
/// <para>
/// Useful when the same route definition is reused across multiple contexts/tenants where
/// each picks its own backing store, or when DI registration occurs after routes are defined.
/// </para>
/// </summary>
public interface IIdempotentRepositoryProvider
{
    /// <summary>
    /// Returns the repository registered under <paramref name="name"/>.
    /// </summary>
    /// <param name="name">Logical repository name (case-insensitive).</param>
    /// <returns>The repository.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when no repository is registered under the given name.
    /// </exception>
    IIdempotentRepository Get(string name);

    /// <summary>
    /// Tries to look up a repository by name without throwing.
    /// </summary>
    bool TryGet(string name, out IIdempotentRepository repository);
}
