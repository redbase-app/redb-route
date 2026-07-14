using System.Collections;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Resolves the <c>outputClass</c> endpoint option to a <see cref="PocoRowMapper{T}"/>.
/// The option carries a type name (assembly-qualified or plain, resolved against loaded
/// assemblies), so the mapper can only be built reflectively — this factory hides that
/// and hands back a non-generic mapping delegate plus the element type, which callers
/// need to build a correctly typed <see cref="List{T}"/> or <see cref="IAsyncEnumerable{T}"/>.
/// Results are cached per type name; type resolution and constructor lookup happen once.
/// </summary>
internal static class SqlRowMapperFactory
{
    private static readonly ConcurrentDictionary<string, PocoMapping> _cache = new(StringComparer.Ordinal);

    /// <summary>A resolved POCO mapping: the element type and a row → object mapper.</summary>
    internal sealed record PocoMapping(Type ElementType, Func<DbDataReader, object> Map)
    {
        /// <summary>Creates an empty <c>List&lt;ElementType&gt;</c> to accumulate mapped rows.</summary>
        public IList CreateList() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ElementType))!;
    }

    /// <summary>
    /// Resolves <paramref name="outputClass"/> to a mapping, or <c>null</c> when the option
    /// is not set — in which case the caller keeps the default <see cref="DictionaryRowMapper"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The type cannot be found, or has no public parameterless constructor.
    /// </exception>
    public static PocoMapping? Resolve(string? outputClass)
        => string.IsNullOrWhiteSpace(outputClass) ? null : _cache.GetOrAdd(outputClass, Build);

    private static PocoMapping Build(string outputClass)
    {
        var type = ResolveType(outputClass)
            ?? throw new InvalidOperationException(
                $"outputClass '{outputClass}' could not be resolved. Use an assembly-qualified name " +
                "(e.g. 'MyApp.Models.Order, MyApp') or make sure the declaring assembly is loaded.");

        if (type.GetConstructor(Type.EmptyTypes) == null)
            throw new InvalidOperationException(
                $"outputClass '{type.FullName}' must have a public parameterless constructor.");

        var mapperType = typeof(PocoRowMapper<>).MakeGenericType(type);
        var mapper = Activator.CreateInstance(mapperType)!;
        var mapMethod = mapperType.GetMethod(nameof(PocoRowMapper<object>.Map))!;

        return new PocoMapping(type, reader => mapMethod.Invoke(mapper, [reader])!);
    }

    private static Type? ResolveType(string name)
    {
        var direct = Type.GetType(name, throwOnError: false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var found = assembly.GetType(name, throwOnError: false);
            if (found != null) return found;
        }

        // Last resort: match by short name across loaded assemblies.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            var found = Array.Find(types, t => t.Name.Equals(name, StringComparison.Ordinal));
            if (found != null) return found;
        }

        return null;
    }
}
