using System.Reflection;

namespace redb.Route.Core;

/// <summary>
/// Finds the <see cref="RouteBuilder"/> types of a set of assemblies — the one place both
/// <see cref="RouteContext.AddRoutes(System.Reflection.Assembly[])"/> and
/// <see cref="Extensions.RedbRouteBuilder.AddRouteBuilders(System.Reflection.Assembly[])"/> go through.
/// <para>
/// A candidate is a public, non-abstract, closed class deriving from <see cref="RouteBuilder"/> — a nested public type
/// included, <see cref="InlineRouteBuilder"/> excluded. Which of them to take is the caller's filter, as Apache
/// Camel's include/exclude patterns: there is no attribute, a type stays out by its filter or by being non-public
/// or abstract. The order is by full name, for a reproducible log; it carries no meaning, since a builder's
/// <c>OnException</c>, <c>Intercept*</c> and <c>OnCompletion</c> apply to its own routes only.
/// </para>
/// <para>
/// Nothing is skipped silently: an assembly whose types cannot all be loaded is an error, not a partial scan —
/// a route builder among the types that failed would otherwise vanish without a word.
/// </para>
/// </summary>
internal static class RouteBuilderDiscovery
{
    /// <summary>The route builder types of <paramref name="assemblies"/> that pass <paramref name="filter"/>, by full name.</summary>
    /// <param name="assemblies">Assemblies to scan; at least one.</param>
    /// <param name="filter">Which candidates to take; <c>null</c> takes them all.</param>
    internal static IReadOnlyList<Type> FindBuilderTypes(IEnumerable<Assembly> assemblies, Func<Type, bool>? filter)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var distinct = assemblies.Distinct().ToList();
        if (distinct.Count == 0)
            throw new ArgumentException("At least one assembly is required to discover route builders.", nameof(assemblies));
        if (distinct.Any(a => a is null))
            throw new ArgumentException("The assemblies to discover route builders in must not contain null.", nameof(assemblies));

        return distinct
            .SelectMany(LoadTypes)
            .Where(IsCandidate)
            .Where(t => filter?.Invoke(t) ?? true)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Creates a discovered builder through its public parameterless constructor — the context path, which has no
    /// container to inject from.
    /// </summary>
    internal static RouteBuilder Create(Type builderType)
    {
        if (builderType.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException(
                $"Route builder '{builderType.FullName}' has no public parameterless constructor, so it cannot be " +
                "created by assembly discovery on the context. Register it through dependency injection " +
                "(RedbRouteBuilder.AddRouteBuilders), add an instance with AddRoutes(builder), or leave it out with the filter.");

        try
        {
            return (RouteBuilder)Activator.CreateInstance(builderType)!;
        }
        catch (TargetInvocationException ex)
        {
            throw new InvalidOperationException(
                $"Route builder '{builderType.FullName}' threw from its constructor during assembly discovery.",
                ex.InnerException ?? ex);
        }
    }

    private static bool IsCandidate(Type type)
        => type.IsClass
           && !type.IsAbstract
           && !type.ContainsGenericParameters
           && type.IsVisible
           && type != typeof(InlineRouteBuilder)
           && typeof(RouteBuilder).IsAssignableFrom(type);

    private static Type[] LoadTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var reasons = ex.LoaderExceptions
                .Where(e => e is not null)
                .Select(e => e!.Message)
                .Distinct()
                .ToList();
            throw new InvalidOperationException(
                $"Not every type of assembly '{assembly.FullName}' could be loaded, so its route builders cannot be " +
                $"discovered reliably: {string.Join("; ", reasons)}",
                ex);
        }
    }
}
