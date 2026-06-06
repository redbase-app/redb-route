using System;
using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver — pluggable prefix resolvers for
/// extending the expression DSL with domain-specific roots
/// (e.g. <c>${conversation.*}</c>, <c>${tool.*}</c>) without coupling
/// the core resolver to those domains.
/// </summary>
public static partial class ExpressionResolver
{
    private static readonly ConcurrentDictionary<string, Func<IExchange, string, object?>> _prefixResolvers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a resolver for a custom property prefix. The <paramref name="prefix"/>
    /// must end with a dot (e.g. <c>"conversation."</c>); the resolver receives the
    /// current exchange and the remainder of the property path after the prefix
    /// (e.g. <c>"id"</c> for <c>${conversation.id}</c>).
    /// <para>
    /// If multiple registrations target the same prefix, the latest one wins.
    /// </para>
    /// </summary>
    public static void RegisterPrefixResolver(string prefix, Func<IExchange, string, object?> resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(resolver);
        if (!prefix.EndsWith('.'))
            throw new ArgumentException("Prefix must end with a dot, e.g. \"conversation.\"", nameof(prefix));

        _prefixResolvers[prefix] = resolver;
    }

    /// <summary>
    /// Removes a previously registered prefix resolver. Returns <c>true</c>
    /// if a resolver was removed.
    /// </summary>
    public static bool UnregisterPrefixResolver(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        return _prefixResolvers.TryRemove(prefix, out _);
    }

    /// <summary>
    /// Resolves the value of a property path against the registered prefix
    /// resolvers. Returns <c>(handled: true, value)</c> if a resolver matched
    /// the prefix; otherwise <c>(handled: false, null)</c>.
    /// </summary>
    internal static (bool Handled, object? Value) TryResolveByPrefix(IExchange exchange, string propertyName)
    {
        if (_prefixResolvers.IsEmpty || string.IsNullOrEmpty(propertyName))
            return (false, null);

        foreach (var (prefix, resolver) in _prefixResolvers)
        {
            if (!propertyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = propertyName.Substring(prefix.Length);
            DebugLog($"Prefix resolver '{prefix}' matched, remainder='{remainder}'");
            return (true, resolver(exchange, remainder));
        }

        return (false, null);
    }
}
