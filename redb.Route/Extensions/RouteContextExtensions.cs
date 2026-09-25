using System.Reflection;
using redb.Route.Abstractions;

namespace redb.Route.Extensions;

/// <summary>
/// Extension methods for <see cref="IRouteContext"/> — component auto-discovery and convenience helpers.
/// </summary>
public static class RouteContextExtensions
{
    /// <summary>
    /// Resolves a service the context was given, looking in both places it can live: the services
    /// set on the context itself (<c>AddService</c>, or a constructor argument), then the DI
    /// container behind <see cref="IRouteContext.GetServiceProvider"/>. Returns <c>null</c> when
    /// neither holds one — the caller decides whether that is a default, its own instance, or a
    /// refusal.
    /// <para>
    /// This is the shape a component needs when the host builds it by scanning: nothing can be
    /// injected through its constructor, so the one shared instance is looked up instead. Having it
    /// in one place keeps the two sources in one order everywhere — the metrics subscriber, the
    /// template options and the shared HTTP host all resolve through it.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Service type.</typeparam>
    /// <param name="context">Route context; a null context resolves to null.</param>
    public static T? Resolve<T>(this IRouteContext? context) where T : class
        => context?.GetService<T>()
           ?? context?.GetServiceProvider()?.GetService(typeof(T)) as T;

    /// <summary>
    /// Resolves a named object from the context registry and FAILS LOUD when nothing is
    /// registered under the name. A typo in <c>connectionFactory=…</c> must never silently
    /// fall back to URI parameters or defaults — a working configuration and a
    /// misconfiguration must be distinguishable (Ф11 волна Ж, Ж-1). A null context (no
    /// registry at all) fails the same way.
    /// </summary>
    /// <typeparam name="T">Expected registered type.</typeparam>
    /// <param name="context">Route context (may be null — still a loud failure).</param>
    /// <param name="name">Registry name the endpoint referenced.</param>
    public static T GetRequiredFromRegistry<T>(this IRouteContext? context, string name)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return context?.GetFromRegistry<T>(name)
               ?? throw new InvalidOperationException(
                   $"'{name}' is not registered in the context registry (expected {typeof(T).Name}). " +
                   "Register it with AddToRegistry(name, ...) before Start().");
    }

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
