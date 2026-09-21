using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Predicates;

/// <summary>
/// The single place that turns a DSL condition string into an <see cref="IPredicate"/>.
/// Every string-taking condition overload (Filter, When, LoopWhile, Validate) routes through it,
/// so a condition behaves identically no matter where it was written.
/// <para>
/// A condition is a boolean expression, which is a different position in the language from a
/// value: <c>SetBody("black and white")</c> must stay a literal, while
/// <c>Filter("header.a&gt;10")</c> must be a comparison. The two positions therefore compile
/// differently, and this type is the boundary between them.
/// </para>
/// </summary>
public static class PredicateFactory
{
    /// <summary>
    /// Builds a predicate from a condition string. Three shapes, one rule each:
    /// <list type="bullet">
    ///   <item>a <c>${...}</c> template is interpolated to a string and read for truthiness;</item>
    ///   <item>a condition carrying a comparison or word-logic operator is a boolean expression
    ///     and is parsed by the AST parser, which tokenises the input: whitespace between tokens
    ///     carries no meaning (<c>header.a&gt;10</c>, <c>header.a &gt; 10</c> and a tab- or
    ///     newline-separated form are one expression) and an operator inside a quoted literal is
    ///     not an operator;</item>
    ///   <item>anything else is a value read for truthiness, which keeps a bare accessor such as
    ///     <c>property.cfg.enabled</c> resolving exactly as it always has.</item>
    /// </list>
    /// Compilation happens here, while the route is being built, so a condition the parser cannot
    /// accept fails at build time instead of turning into a constant answer on live traffic.
    /// </summary>
    /// <param name="condition">The condition string as written by the route author.</param>
    /// <returns>A predicate that evaluates the condition against an exchange.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="condition"/> is null, empty or whitespace.</exception>
    /// <exception cref="ExpressionCompilationException">Thrown when the condition cannot be compiled.</exception>
    public static IPredicate FromString(string condition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);

        if (ExpressionResolver.IsTemplate(condition))
        {
            var template = ExpressionResolver.GetCompiledTemplate(condition);
            return new LambdaPredicate(exchange => RouteTruthiness.ToBoolean(template(exchange)));
        }

        var compiled = ExpressionResolver.ContainsComparisonOrWordLogicOperator(condition)
            ? ExpressionResolver.GetCompiledValueExpressionWithAst(condition)
            : ExpressionResolver.GetCompiledValueExpression(condition);

        return new LambdaPredicate(exchange => RouteTruthiness.ToBoolean(compiled(exchange)));
    }

    /// <summary>
    /// Builds a predicate from an expression instance used in a condition position.
    /// <para>
    /// An expression born from a string (<c>Expr("header.amount>1000")</c>) is a condition
    /// string that happened to be wrapped, so it takes the same path as
    /// <see cref="FromString"/>: compiled as a condition, whitespace-insensitive, fail-fast.
    /// Reading it as a value first would resurrect the original silent-drop defect: the value
    /// dialect turned the unspaced comparison into a lookup of a header named
    /// <c>amount&gt;1000</c>, which never exists.
    /// </para>
    /// <para>
    /// An expression that implements <see cref="IPredicateExpression"/> answers the condition itself.
    /// A query language brings its own notion of a match that evaluating-then-reading-truthiness
    /// cannot reconstruct: <c>XPath("/order/discount")</c> in a condition asks whether the path
    /// matched, and flattening the node-set to a value first would answer with the node's content
    /// instead — <c>&lt;discount&gt;0&lt;/discount&gt;</c> would read as "no match". Asking the
    /// expression keeps that decision where the language's semantics live.
    /// </para>
    /// <para>
    /// Any other expression is evaluated and read through <see cref="RouteTruthiness"/>.
    /// </para>
    /// </summary>
    /// <param name="expression">The expression written in a condition position.</param>
    /// <returns>A predicate that evaluates the expression against an exchange.</returns>
    public static IPredicate FromExpression(IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (expression is StringExpression text)
            return FromString(text.Template);

        if (expression is IPredicateExpression self)
            return new LambdaPredicate(self.Matches);

        return new LambdaPredicate(exchange => RouteTruthiness.ToBoolean(expression.Evaluate<object?>(exchange)));
    }
}
