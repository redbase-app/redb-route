using System.Runtime.CompilerServices;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// One <c>ref:name</c> in a compiled expression: the object registered under that name. The expression is compiled
/// once and shared by every context, so the object is looked up per route context, on the first evaluation there, as
/// a <c>bean:</c> call resolves its bean (<see cref="BeanCallSite"/>). A weak table keeps no context alive. Refused
/// under <see cref="ExpressionSandbox"/>, as <c>bean:</c> is.
/// </summary>
internal sealed class RegistryRefSite(string reference)
{
    private readonly ConditionalWeakTable<IRouteContext, object> _resolved = new();

    /// <summary>The registered object.</summary>
    public object Get(IExchange exchange)
    {
        var context = exchange.Context ?? throw new InvalidOperationException(
            $"ref:{reference}: the exchange has no route context, so there is no registry to find the object in.");
        if (ExpressionSandbox.IsActive)
            throw new ExpressionSandboxViolationException("ref:" + reference);
        return _resolved.GetValue(context, Resolve);
    }

    private object Resolve(IRouteContext context)
        => context.GetFromRegistry<object>(reference) ?? throw new InvalidOperationException(
            $"ref:{reference}: '{reference}' is not in the context registry. " +
            "Register it with AddToRegistry or a <bean> declaration.");
}
