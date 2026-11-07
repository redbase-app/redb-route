using System.Data.Common;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Sql.Batch;

/// <summary>
/// Binds one batch item to the statement. Sources, first match wins: explicit <c>param.name</c>; the item's own value
/// (<see cref="BatchItemAccessor"/>); the carrying exchange's header. An item that is itself an exchange (grouped by an
/// aggregation) is looked up in its own headers and body instead, and evaluates <c>${...}</c> on itself. A placeholder with no
/// value is an error of that item, as in Apache Camel; a value that is present but null binds NULL.
/// </summary>
internal static class BatchItemBinder
{
    /// <summary>
    /// Fills <paramref name="values"/> — one slot per <see cref="SqlParameterPlan.Names"/> entry, in that order — with the
    /// normalised values of the item at <paramref name="index"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A placeholder has no value for this item.</exception>
    internal static void ResolveValues(SqlParameterPlan plan, IExchange carrier, object? item, int index, object[] values)
    {
        IExchange? expressionContext = null;

        for (var slot = 0; slot < plan.Names.Count; slot++)
        {
            var name = plan.Names[slot];
            object? value;
            if (plan.TryGetExplicit(name, out var explicitParameter))
            {
                value = explicitParameter.IsExpression
                    ? explicitParameter.Resolve(expressionContext ??= CreateExpressionContext(carrier, item))
                    : explicitParameter.Resolve(null);
            }
            else if (!TryGetValue(carrier, item, name, out value, out var lookup))
            {
                throw new InvalidOperationException(MissingValueMessage(plan, item, name, index, lookup));
            }

            values[slot] = SqlParameterBinder.NormalizeForDb(value);
        }
    }

    private static bool TryGetValue(IExchange carrier, object? item, string name, out object? value, out ItemLookup lookup)
    {
        if (item is IExchange itemExchange)
        {
            if (itemExchange.In.Headers.TryGetValue(name, out value))
            {
                lookup = ItemLookup.Found;
                return true;
            }

            lookup = BatchItemAccessor.Find(itemExchange.In.Body, name, out value);
            return lookup == ItemLookup.Found;
        }

        lookup = BatchItemAccessor.Find(item, name, out value);
        return lookup == ItemLookup.Found || carrier.In.Headers.TryGetValue(name, out value);
    }

    /// <summary>
    /// The exchange an item's <c>${...}</c> values are evaluated in: an item exchange itself; otherwise a linked child of the
    /// carrying exchange (its properties, route context and DI scope) with the item as body and, as headers, the carrier's
    /// headers overlaid by the entries of a dictionary item. The child is not disposed: it owns no scope, and disposing it
    /// would dispose its body, which is an element of the carrier's list.
    /// </summary>
    private static IExchange CreateExpressionContext(IExchange carrier, object? item)
    {
        if (item is IExchange itemExchange)
            return itemExchange;

        var child = carrier.CreateLinkedChild(new Message(item));
        foreach (var (key, value) in carrier.In.Headers)
            child.In.Headers[key] = value;
        foreach (var (key, value) in BatchItemAccessor.Entries(item))
            child.In.Headers[key] = value;
        return child;
    }

    private static string MissingValueMessage(SqlParameterPlan plan, object? item, string name, int index, ItemLookup lookup)
    {
        var lookedAt = item switch
        {
            IExchange => $"no param.{name} option, no '{name}' header on the item exchange and no '{name}' key in its body",
            _ when BatchItemAccessor.IsXml(item) =>
                $"the item is {item!.GetType().Name}, an XML node, and XML carries no named values (as in Apache Camel), and there " +
                $"is no param.{name} option and no '{name}' header; bind it with param.{name}=${{xpath('...')}} relative to the item",
            _ when lookup == ItemLookup.NoNamedValues =>
                $"the item is {(item is null ? "null" : item.GetType().Name)} and carries no named values, and there is no " +
                $"param.{name} option and no '{name}' header; bind it with param.{name}=${{body}} or a ${{body...}} expression",
            _ when lookup == ItemLookup.Ambiguous =>
                $"the item has several keys equal to '{name}' ignoring case and none equal to it exactly, and there is no " +
                $"param.{name} option and no '{name}' header",
            _ => $"no param.{name} option, no '{name}' key or property in the item and no '{name}' header",
        };

        return $"Batch item {index}: " + SqlParameterBinder.MissingValueMessage(name, plan.SourceSql, lookedAt);
    }
}
