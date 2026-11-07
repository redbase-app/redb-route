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
        if (string.IsNullOrEmpty(propertyName))
            return (false, null);

        // The exchange's own context first: a prefix a module registered there goes with the context.
        if (exchange.Context?.GetService<ExpressionPrefixResolvers>() is { } contextResolvers
            && contextResolvers.TryResolve(exchange, propertyName, out var contextValue))
            return (true, contextValue);

        if (_prefixResolvers.IsEmpty)
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

/// <summary>
/// The prefix resolvers of one route context: <c>${prefix.rest}</c> roots that the context's own code (a module
/// loaded for it) contributes. They live as a service of the context, so they go when the context is disposed and
/// keep no delegate of an unloaded module alive; an expression evaluated on an exchange of the context consults
/// them before the process-wide ones of <see cref="ExpressionResolver.RegisterPrefixResolver"/>. Register through
/// <c>context.RegisterPrefixResolver(prefix, resolver)</c>.
/// </summary>
public sealed class ExpressionPrefixResolvers
{
    private static readonly object CreateGate = new();
    private readonly ConcurrentDictionary<string, Func<IExchange, string, object?>> _resolvers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The resolvers of <paramref name="context"/>, created on first use.</summary>
    public static ExpressionPrefixResolvers For(IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetService<ExpressionPrefixResolvers>() is { } existing)
            return existing;

        lock (CreateGate)
        {
            if (context.GetService<ExpressionPrefixResolvers>() is { } raced)
                return raced;
            var created = new ExpressionPrefixResolvers();
            context.AddService(typeof(ExpressionPrefixResolvers), created);
            return created;
        }
    }

    /// <summary>
    /// Registers <paramref name="resolver"/> for <paramref name="prefix"/> (ending with a dot, e.g. <c>"order."</c>);
    /// a second registration of the prefix replaces the first.
    /// </summary>
    public void Register(string prefix, Func<IExchange, string, object?> resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(resolver);
        if (!prefix.EndsWith('.'))
            throw new ArgumentException("Prefix must end with a dot, e.g. \"order.\"", nameof(prefix));

        _resolvers[prefix] = resolver;
    }

    /// <summary>Removes the resolver of <paramref name="prefix"/>; true when there was one.</summary>
    public bool Unregister(string prefix) => !string.IsNullOrWhiteSpace(prefix) && _resolvers.TryRemove(prefix, out _);

    internal bool TryResolve(IExchange exchange, string propertyName, out object? value)
    {
        value = null;
        if (_resolvers.IsEmpty)
            return false;

        foreach (var (prefix, resolver) in _resolvers)
        {
            if (!propertyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            value = resolver(exchange, propertyName[prefix.Length..]);
            return true;
        }

        return false;
    }
}
