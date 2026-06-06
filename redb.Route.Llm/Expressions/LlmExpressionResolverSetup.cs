using System;
using System.Reflection;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Llm.Expressions;

/// <summary>
/// Registers <c>${conversation.*}</c> and <c>${tool.*}</c> resolvers with
/// <see cref="ExpressionResolver"/>. The resolvers read the well-known
/// exchange properties published by <see cref="Engine.AgentEngine"/>
/// (<see cref="LlmExpressionKeys.Conversation"/>, <see cref="LlmExpressionKeys.Tool"/>)
/// and walk the requested path via reflection — so adding fields to
/// <see cref="LlmConversationContext"/> / <see cref="LlmToolContext"/>
/// automatically makes them available in templates.
/// </summary>
public static class LlmExpressionResolverSetup
{
    private static int _initialized;

    /// <summary>
    /// Idempotently registers the LLM prefix resolvers. Safe to call from
    /// every host's composition root — subsequent calls are no-ops.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        ExpressionResolver.RegisterPrefixResolver("conversation.", (exchange, path) =>
            ResolvePath(exchange.getProperty<object>(LlmExpressionKeys.Conversation), path));

        ExpressionResolver.RegisterPrefixResolver("tool.", (exchange, path) =>
            ResolvePath(exchange.getProperty<object>(LlmExpressionKeys.Tool), path));
    }

    private static object? ResolvePath(object? root, string path)
    {
        if (root is null || string.IsNullOrEmpty(path))
            return root;

        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null) return null;

            var type = current.GetType();
            var prop = type.GetProperty(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (prop is not null)
            {
                current = prop.GetValue(current);
                continue;
            }

            var field = type.GetField(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (field is not null)
            {
                current = field.GetValue(current);
                continue;
            }

            return null;
        }

        return current;
    }
}
