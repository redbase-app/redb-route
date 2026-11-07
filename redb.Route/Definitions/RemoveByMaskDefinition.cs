using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>Which store <see cref="RemoveByMaskDefinition"/> cleans.</summary>
public enum RemoveTarget
{
    /// <summary>Message headers.</summary>
    Headers,
    /// <summary>Exchange properties.</summary>
    Properties,
}

/// <summary>
/// Removes every header (or property) whose name matches a mask — exact, trailing <c>*</c>
/// (<c>X-Internal-*</c>) or <c>regex:</c> — except the names listed. The usual last step before a
/// message leaves for an external system.
/// </summary>
public sealed class RemoveByMaskDefinition : ProcessorDefinition
{
    /// <summary>Creates the node.</summary>
    public RemoveByMaskDefinition(RemoveTarget target, string pattern, IReadOnlyList<string> except)
    {
        UriMask.Validate(pattern, nameof(pattern));
        ArgumentNullException.ThrowIfNull(except);
        Target = target;
        Pattern = pattern;
        Except = except;
    }

    /// <summary>Headers or properties.</summary>
    public RemoveTarget Target { get; }

    /// <summary>Name mask.</summary>
    public string Pattern { get; }

    /// <summary>Names kept even when they match.</summary>
    public IReadOnlyList<string> Except { get; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange =>
        {
            var store = Target == RemoveTarget.Headers ? exchange.In.Headers : exchange.Properties;
            // The exchange keeps its DI scopes (__redb_scope:*) and registered resources (__redb_resource:*) as
            // properties; they are bookkeeping, not user data, and dropping them would leave ReleaseScopes nothing to
            // dispose — a scope or connection leak.
            var doomed = store.Keys
                .Where(key => UriMask.IsMatch(Pattern, key)
                    && !Except.Contains(key, StringComparer.OrdinalIgnoreCase)
                    && !(Target == RemoveTarget.Properties && ExchangeResources.IsOwnedByExchange(key)))
                .ToList();
            foreach (var key in doomed)
                store.Remove(key);
        });
}
