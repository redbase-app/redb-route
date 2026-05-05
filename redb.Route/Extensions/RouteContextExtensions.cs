using System.Reflection;
using redb.Route.Abstractions;

namespace redb.Route.Extensions;

/// <summary>
/// Extension methods for <see cref="IRouteContext"/> — component auto-discovery and convenience helpers.
/// </summary>
public static class RouteContextExtensions
{
    /// <summary>
    /// Scans the specified assemblies for concrete <see cref="IComponent"/> implementations
    /// and registers any that are not already present in the context.
    /// Components must have a public parameterless constructor.
    /// </summary>
    /// <param name="context">The route context to register components in.</param>
    /// <param name="assemblies">One or more assemblies to scan.</param>
    /// <returns>The context (for fluent chaining).</returns>
    public static IRouteContext AddComponents(this IRouteContext context, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(context);

        var componentType = typeof(IComponent);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Some types may fail to load (missing deps); use whatever loaded successfully
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (!componentType.IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;

                IComponent component;
                try
                {
                    component = (IComponent)Activator.CreateInstance(type)!;
                }
                catch
                {
                    // Skip types that cannot be instantiated (e.g. require constructor args)
                    continue;
                }

                if (context.HasComponent(component.Scheme))
                    continue;

                context.AddComponent(component);
            }
        }

        return context;
    }
}
