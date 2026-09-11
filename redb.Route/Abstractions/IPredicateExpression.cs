namespace redb.Route.Abstractions;

/// <summary>
/// An expression that defines its own reading in a condition, rather than being evaluated to a
/// value and read for truthiness.
/// </summary>
/// <remarks>
/// <para>
/// A query language brings a notion of a match that evaluating-then-reading cannot reconstruct.
/// <c>XPath("/order/discount")</c> in a condition asks whether the path matched; flattening the
/// node-set to a value first answers with the node's content instead, so
/// <c>&lt;discount&gt;0&lt;/discount&gt;</c> would read as "no match". Implementing this interface
/// keeps that decision where the language's semantics live.
/// </para>
/// <para>
/// It deliberately does <b>not</b> extend <see cref="IPredicate"/>. Condition-taking verbs are
/// overloaded on <see cref="IExpression"/> and <see cref="IPredicate"/> both, so an expression that
/// implemented the latter would make <c>Filter(XPath("/a/b"))</c> an ambiguous call — the language
/// would gain a semantics and lose the spelling that needed it.
/// </para>
/// </remarks>
public interface IPredicateExpression : IExpression
{
    /// <summary>
    /// Evaluates the expression in a condition position.
    /// </summary>
    /// <param name="exchange">The exchange to test.</param>
    /// <returns><c>true</c> when the condition holds for the exchange.</returns>
    bool Matches(IExchange exchange);
}
