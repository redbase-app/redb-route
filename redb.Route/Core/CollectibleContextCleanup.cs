using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace redb.Route.Core;

/// <summary>
/// Lets a collectible <see cref="AssemblyLoadContext"/> go when it unloads (a host's module or plugin): the caches the
/// .NET libraries keep over the types they handled drop that context's types. The caches of redb.Route keyed by type
/// hold their key weakly and need nothing here. The same cleanup as redb.Core's, for hosts of redb.Route without it;
/// with both in the process each runs once per unload, which costs one more emptying of those caches.
/// <para>
/// Every collectible context is watched from the moment one of its assemblies loads (and the ones that existed before
/// this class first ran are picked up then), so the hot path pays nothing: the work happens once per unload, in
/// <see cref="AssemblyLoadContext.Unloading"/>, which <c>Unload()</c> raises before the context can be collected.
/// </para>
/// </summary>
internal static class CollectibleContextCleanup
{
    private static readonly ConditionalWeakTable<AssemblyLoadContext, object> Watched = new();
    private static readonly object Marker = new();
    private static int _started;
    private static int _noHandlersReported;
    private static ILogger? _logger;

    /// <summary>
    /// Starts watching collectible contexts; called by every route context and by the serializers that build metadata
    /// over message types. The first logger handed over reports the cleanup.
    /// </summary>
    public static void EnsureWatching(ILogger? logger = null)
    {
        if (logger != null)
            Interlocked.CompareExchange(ref _logger, logger, null);

        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        AppDomain.CurrentDomain.AssemblyLoad += static (_, e) => Watch(e.LoadedAssembly);
        foreach (var context in AssemblyLoadContext.All)
            if (context.IsCollectible)
                Watch(context);
    }

    private static void Watch(Assembly assembly)
    {
        if (assembly.IsCollectible && AssemblyLoadContext.GetLoadContext(assembly) is { } context)
            Watch(context);
    }

    private static void Watch(AssemblyLoadContext context)
    {
        if (Watched.TryAdd(context, Marker))
            context.Unloading += OnUnloading;
    }

    private static void OnUnloading(AssemblyLoadContext context)
    {
        context.Unloading -= OnUnloading;
        ClearMetadataUpdateCaches(context);
    }

    /// <summary>
    /// The libraries of .NET keep process-wide caches built over the types they handled, and do not let a collectible
    /// context go while one of its types is in them: System.Text.Json above all (metadata in every options instance,
    /// the static options of the JSON serializer of redb.Route included, and emitted member accessors), TypeDescriptor
    /// and others. They declare a handler for hot reload (<see cref="MetadataUpdateHandlerAttribute"/>) whose
    /// <c>ClearCache</c> empties those caches; redb.Route calls <c>ClearCache(null)</c> of every handler declared by a
    /// loaded assembly, as <c>dotnet watch</c> does, once per unload. The assemblies of the unloading context are left
    /// out. Unloadable assemblies are not supported by System.Text.Json otherwise:
    /// https://github.com/dotnet/runtime/issues/65323. Costs cold caches in those libraries for what follows, once per
    /// unload.
    /// </summary>
    internal static int ClearMetadataUpdateCaches(AssemblyLoadContext unloading)
    {
        var found = 0;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || AssemblyLoadContext.GetLoadContext(assembly) == unloading)
                continue;

            MetadataUpdateHandlerAttribute[] handlers;
            try
            {
                handlers = (MetadataUpdateHandlerAttribute[])assembly.GetCustomAttributes(typeof(MetadataUpdateHandlerAttribute), false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Unloading context {Context}: cannot read the hot reload handlers of {Assembly}",
                    unloading.Name, assembly.FullName);
                continue;
            }

            foreach (var handler in handlers)
            {
                var clearCache = handler.HandlerType.GetMethod("ClearCache",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type[])]);
                if (clearCache == null)
                    continue;

                found++;
                try
                {
                    clearCache.Invoke(null, [null]);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unloading context {Context}: ClearCache of {Handler} failed, the other caches are cleared",
                        unloading.Name, handler.HandlerType.FullName);
                }
            }
        }

        if (found == 0 && Interlocked.Exchange(ref _noHandlersReported, 1) == 0)
            _logger?.LogWarning(
                "Unloading context {Context}: no loaded assembly declares a hot reload cache handler; caches of the .NET " +
                "libraries (System.Text.Json among them) may keep the context alive", unloading.Name);

        return found;
    }

    /// <summary>For tests: whether <paramref name="context"/> is watched.</summary>
    internal static bool IsWatched(AssemblyLoadContext context) => Watched.TryGetValue(context, out _);
}
