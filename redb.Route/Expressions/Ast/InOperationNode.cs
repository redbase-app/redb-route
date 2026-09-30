using redb.Route.Abstractions;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// A list literal after <c>in</c>: <c>('gold','silver')</c>, <c>(1, 2, 3)</c>, or <c>()</c>.
/// Evaluates every item against the exchange, so an item can be any expression.
/// </summary>
public sealed class ListLiteralNode : AstNode
{
    /// <summary>The items in source order.</summary>
    public IReadOnlyList<AstNode> Items { get; }

    /// <summary>Creates the node.</summary>
    public ListLiteralNode(IReadOnlyList<AstNode> items)
        => Items = items ?? throw new ArgumentNullException(nameof(items));

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
    {
        var values = new List<object?>(Items.Count);
        foreach (var item in Items)
            values.Add(item.Evaluate(exchange));
        return values;
    }

    /// <inheritdoc />
    public override string ToString() => "(" + string.Join(", ", Items) + ")";
}

/// <summary>
/// <c>value in list</c> and <c>value not in list</c> — Camel Simple's membership operators.
/// <para>
/// Elements are compared with the equality <c>==</c> uses (<see cref="ExpressionResolver.Ast_AreEqual"/>),
/// so <c>in</c> cannot disagree with <c>==</c> about whether <c>2</c> equals <c>2L</c>. A missing
/// collection (null) is an empty one: nothing is a member of it, which is the SQL reading of an empty
/// <c>IN</c> and the one a missing header should have. A string is refused rather than split — Camel
/// reads <c>'a,b'</c> as a list, but here a comma inside a string already means something else, and
/// iterating a string would quietly test membership among its characters.
/// </para>
/// </summary>
public sealed class InOperationNode : AstNode
{
    /// <summary>The value tested for membership.</summary>
    public AstNode Value { get; }

    /// <summary>The list: a <see cref="ListLiteralNode"/> or an expression yielding a collection.</summary>
    public AstNode List { get; }

    /// <summary>True for <c>not in</c>.</summary>
    public bool Negated { get; }

    /// <summary>Creates the node.</summary>
    public InOperationNode(AstNode value, AstNode list, bool negated)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        List = list ?? throw new ArgumentNullException(nameof(list));
        Negated = negated;
    }

    /// <inheritdoc />
    public override object? Evaluate(IExchange exchange)
        => ExpressionResolver.Ast_In(Value.Evaluate(exchange), List.Evaluate(exchange), Negated, ToString());

    /// <inheritdoc />
    public override string ToString() => $"{Value} {(Negated ? "not in" : "in")} {List}";
}
