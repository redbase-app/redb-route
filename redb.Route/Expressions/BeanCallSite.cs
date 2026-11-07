using System.Runtime.CompilerServices;
using redb.Route.Abstractions;
using redb.Route.Components.Bean;

namespace redb.Route.Expressions;

/// <summary>
/// One <c>bean:name.Method(args)</c> call in a compiled expression. The expression is compiled once and shared by every
/// context that evaluates it, so the bean is resolved per route context: from that context's registry, on the first
/// evaluation there, with the method bound once through <see cref="BeanInvoker.ForValues"/> (the selection and
/// conversion rules of the <c>bean:</c> URI). A weak table keeps no context alive.
/// <para>
/// Synchronous only, as in Apache Camel's Simple language: an expression has no await, so a method returning a task
/// is refused when it is first resolved, pointing to the bean step. Refused under <see cref="ExpressionSandbox"/>: a
/// template's data never runs code.
/// </para>
/// </summary>
internal sealed class BeanCallSite(string reference, string method, int argumentCount)
{
    private readonly ConditionalWeakTable<IRouteContext, Resolved> _resolved = new();

    private sealed record Resolved(object Instance, BeanInvoker.BoundMethod Bound, bool Awaitable,
        Func<Task, object?>? TaskResult, Func<object?, object?>? ToTask);

    /// <summary>
    /// Calls the bean with the evaluated <paramref name="values"/> and returns the method's result. Synchronous: a
    /// task-returning method is refused (<see cref="AwaitableRefused"/>).
    /// </summary>
    public object? Invoke(IExchange exchange, object?[] values)
    {
        var resolved = ResolveFor(exchange);
        if (resolved.Awaitable)
            throw new InvalidOperationException(AwaitableRefused(reference, method, resolved.Bound.Method.ReturnType));
        return resolved.Bound.Call(resolved.Instance, values, exchange, CancellationToken.None);
    }

    /// <summary>
    /// Calls the bean and awaits a task-returning method (<c>Method()</c> in a condition, where the engine awaits
    /// <see cref="IPredicate.MatchesAsync"/>); a plain method's result is returned as it is.
    /// </summary>
    public async Task<object?> InvokeAsync(IExchange exchange, object?[] values, CancellationToken cancellationToken)
    {
        var resolved = ResolveFor(exchange);
        var raw = resolved.Bound.Call(resolved.Instance, values, exchange, cancellationToken);
        if (!resolved.Awaitable)
            return raw;
        var task = resolved.ToTask is { } toTask ? (Task)toTask(raw)! : (Task)raw!;
        await task.ConfigureAwait(false);
        return resolved.TaskResult?.Invoke(task);
    }

    private Resolved ResolveFor(IExchange exchange)
    {
        var context = exchange.Context ?? throw new InvalidOperationException(
            $"bean:{reference}.{method}: the exchange has no route context, so there is no registry to find the bean in.");
        var resolved = _resolved.GetValue(context, Resolve);
        if (ExpressionSandbox.IsActive)
            throw new ExpressionSandboxViolationException(method, resolved.Instance.GetType());
        return resolved;
    }

    private Resolved Resolve(IRouteContext context)
    {
        var instance = context.GetFromRegistry<object>(reference) ?? throw new InvalidOperationException(
            $"bean:{reference}.{method}: '{reference}' is not in the context registry. " +
            "Register it with AddToRegistry or a <bean> declaration.");
        var bound = BeanInvoker.ForValues(instance.GetType(), method, argumentCount, "#" + reference);
        var (_, taskResult, toTask) = BeanInvoker.ClassifyReturn(bound.Method.ReturnType);
        return new Resolved(instance, bound, BeanInvoker.IsAwaitable(bound.Method.ReturnType), taskResult, toTask);
    }

    /// <summary>The refusal of a task-returning method, worded once for the call and for the package gate.</summary>
    internal static string AwaitableRefused(string reference, string method, Type returnType)
        => $"bean:{reference}.{method} returns {returnType.Name}: a bean: call in an expression is " +
           $"synchronous. Call an asynchronous method as a step: <bean ref=\"#{reference}\" method=\"{method}(...)\"/> " +
           $"or .Bean(\"#{reference}\", \"{method}(...)\").";

    /// <summary>
    /// The <c>bean:</c> calls of one expression, as the engine parses it: the tool's view of what an expression will
    /// call (the package gate). Throws <see cref="InvalidOperationException"/> with the parser's positioned message
    /// when the expression does not parse.
    /// </summary>
    internal static IReadOnlyList<Ast.BeanCallNode> CallsIn(string expression) => NodesIn<Ast.BeanCallNode>(expression);

    /// <summary>
    /// The <c>ref:</c> names of one expression, as the engine parses it (the package gate). Throws
    /// <see cref="InvalidOperationException"/> with the parser's positioned message when the expression does not parse.
    /// </summary>
    internal static IReadOnlyList<Ast.RegistryRefNode> RefsIn(string expression) => NodesIn<Ast.RegistryRefNode>(expression);

    private static List<T> NodesIn<T>(string expression) where T : Ast.AstNode
    {
        var root = new Ast.Parser(new Ast.Tokenizer(expression).GetAllTokens()).Parse();
        var nodes = new List<T>();
        Collect(root, nodes);
        return nodes;
    }

    private static void Collect<T>(Ast.AstNode node, List<T> calls) where T : Ast.AstNode
    {
        if (node is T call)
            calls.Add(call);
        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            switch (property.GetValue(node))
            {
                case Ast.AstNode child:
                    Collect(child, calls);
                    break;
                case IEnumerable<Ast.AstNode> children:
                    foreach (var child in children)
                        Collect(child, calls);
                    break;
            }
        }
    }
}
