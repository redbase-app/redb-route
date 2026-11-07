using System;
using System.Collections.Generic;
using System.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// Abstract base class for all AST (Abstract Syntax Tree) nodes.
/// </summary>
public abstract class AstNode
{
    private Func<IExchange, object?>? _compiled;

    /// <summary>
    /// Evaluates this AST node against the given exchange context. The tree under the node is compiled once, by the same
    /// compiler every expression of the engine goes through, and the compiled delegate is evaluated: there is one
    /// evaluator, and a node evaluated directly gives what the engine gives (findings register row 27).
    /// </summary>
    /// <param name="exchange">The exchange context used for expression resolution.</param>
    /// <returns>The result of evaluating this node.</returns>
    public virtual object? Evaluate(IExchange exchange)
        => (_compiled ??= ExpressionResolver.CompileAstTree(this))(exchange);
}

/// <summary>
/// AST node representing a literal (constant) value.
/// </summary>
public class LiteralNode : AstNode
{
    /// <summary>
    /// Gets the literal value held by this node.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteralNode"/> class.
    /// </summary>
    /// <param name="value">The literal value.</param>
    public LiteralNode(object? value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override string ToString() => Value?.ToString() ?? "null";
}

/// <summary>
/// AST node representing an identifier (variable reference).
/// </summary>
public class IdentifierNode : AstNode
{
    /// <summary>
    /// Gets the name of the identifier.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentifierNode"/> class.
    /// </summary>
    /// <param name="name">The identifier name.</param>
    public IdentifierNode(string name)
    {
        Name = name;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// AST node representing a binary operation (e.g. addition, comparison, logical operations).
/// </summary>
public class BinaryOperationNode : AstNode
{
    /// <summary>
    /// Gets the left operand of the binary operation.
    /// </summary>
    public AstNode Left { get; }

    /// <summary>
    /// Gets the operator symbol (e.g. "+", "==", "AND").
    /// </summary>
    public string Operator { get; }

    /// <summary>
    /// Gets the right operand of the binary operation.
    /// </summary>
    public AstNode Right { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BinaryOperationNode"/> class.
    /// </summary>
    /// <param name="left">The left operand node.</param>
    /// <param name="op">The operator string.</param>
    /// <param name="right">The right operand node.</param>
    public BinaryOperationNode(AstNode left, string op, AstNode right)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    /// <inheritdoc />
    public override string ToString() => $"({Left} {Operator} {Right})";
}

/// <summary>
/// AST node representing a unary (prefix) operation (e.g. negation, logical NOT, prefix increment/decrement).
/// </summary>
public class UnaryOperationNode : AstNode
{
    /// <summary>
    /// Gets the unary operator symbol (e.g. "+", "-", "!", "NOT", "++", "--").
    /// </summary>
    public string Operator { get; }

    /// <summary>
    /// Gets the operand of the unary operation.
    /// </summary>
    public AstNode Operand { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnaryOperationNode"/> class.
    /// </summary>
    /// <param name="op">The unary operator string.</param>
    /// <param name="operand">The operand node.</param>
    public UnaryOperationNode(string op, AstNode operand)
    {
        Operator = op;
        Operand = operand;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Operator}({Operand})";
}

/// <summary>
/// AST node representing a function call with arguments.
/// </summary>
public class FunctionCallNode : AstNode
{
    /// <summary>
    /// Gets the name of the function being called.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the list of argument nodes passed to the function.
    /// </summary>
    public List<AstNode> Arguments { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FunctionCallNode"/> class.
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <param name="arguments">The list of argument AST nodes.</param>
    public FunctionCallNode(string name, List<AstNode> arguments)
    {
        Name = name;
        Arguments = arguments;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}({string.Join(", ", Arguments)})";
}

/// <summary>
/// AST node representing property access on an object (e.g. <c>obj.PropertyName</c>).
/// </summary>
public class PropertyAccessNode : AstNode
{
    /// <summary>
    /// Gets the AST node representing the object being accessed.
    /// </summary>
    public AstNode Object { get; }

    /// <summary>
    /// Gets the name of the property being accessed.
    /// </summary>
    public string PropertyName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PropertyAccessNode"/> class.
    /// </summary>
    /// <param name="obj">The object node whose property is accessed.</param>
    /// <param name="propertyName">The name of the property to access.</param>
    public PropertyAccessNode(AstNode obj, string propertyName)
    {
        Object = obj;
        PropertyName = propertyName;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Object}.{PropertyName}";
}

/// <summary>
/// A namespaced call, <c>bean:name.Method(arg, ...)</c>: the bean <see cref="Reference"/> from the context registry
/// and its <see cref="Method"/> with the argument expressions. Compiled by the AST compiler, which resolves it.
/// </summary>
internal sealed class BeanCallNode(string ns, string reference, string method, List<AstNode> arguments, int position) : AstNode
{
    /// <summary>The namespace, <c>bean</c>.</summary>
    public string Namespace { get; } = ns;

    /// <summary>The registry name, without <c>#</c>.</summary>
    public string Reference { get; } = reference;

    /// <summary>The method name.</summary>
    public string Method { get; } = method;

    /// <summary>The argument expressions, in order.</summary>
    public List<AstNode> Arguments { get; } = arguments;

    /// <summary>Zero-based position of the namespace within the expression.</summary>
    public int Position { get; } = position;

    /// <inheritdoc />
    public override string ToString() => $"{Namespace}:{Reference}.{Method}({string.Join(", ", Arguments)})";
}

/// <summary>
/// <c>value range 'from..to'</c> and <c>value not range 'from..to'</c>: a numeric value within both ends, inclusive, as
/// in Apache Camel's Simple. The ends are read when the expression is compiled.
/// </summary>
internal sealed class RangeNode(AstNode value, string literal, decimal from, decimal to, bool negated) : AstNode
{
    /// <summary>The value tested.</summary>
    public AstNode Value { get; } = value;

    /// <summary>The literal as written, without its quotes.</summary>
    public string Literal { get; } = literal;

    /// <summary>The lower end, inclusive.</summary>
    public decimal From { get; } = from;

    /// <summary>The upper end, inclusive.</summary>
    public decimal To { get; } = to;

    /// <summary>True for <c>not range</c>.</summary>
    public bool Negated { get; } = negated;

    /// <inheritdoc />
    public override string ToString() => $"{Value} {(Negated ? "not range" : "range")} '{Literal}'";
}

/// <summary>
/// <c>value.method(args)</c>: a method called on a value, a built-in dot helper (<c>length</c>, <c>contains</c>, ...) or a
/// public method of the value's type.
/// </summary>
internal sealed class MethodCallNode(AstNode target, string method, List<AstNode> arguments, string written, int position) : AstNode
{
    /// <summary>The value the method is called on.</summary>
    public AstNode Target { get; } = target;

    /// <summary>The method's name as written.</summary>
    public string Method { get; } = method;

    /// <summary>The argument expressions, in order.</summary>
    public List<AstNode> Arguments { get; } = arguments;

    /// <summary>The call as written, for error messages (<c>header.o.Boom()</c>).</summary>
    public string Written { get; } = written;

    /// <summary>Zero-based position of the call within the expression.</summary>
    public int Position { get; } = position;

    /// <inheritdoc />
    public override string ToString() => $"{Target}.{Method}({string.Join(", ", Arguments)})";
}

/// <summary><c>env:NAME</c>: the OS environment variable of that name, read when the expression is evaluated.</summary>
internal sealed class EnvNode(string name, int position) : AstNode
{
    /// <summary>The variable's name.</summary>
    public string Name { get; } = name;

    /// <summary>Zero-based position of the namespace within the expression.</summary>
    public int Position { get; } = position;

    /// <inheritdoc />
    public override string ToString() => $"env:{Name}";
}

/// <summary><c>ref:name</c>: the object registered under that name in the route context's registry.</summary>
internal sealed class RegistryRefNode(string reference, int position) : AstNode
{
    /// <summary>The registry name, without <c>#</c>.</summary>
    public string Reference { get; } = reference;

    /// <summary>Zero-based position of the namespace within the expression.</summary>
    public int Position { get; } = position;

    /// <inheritdoc />
    public override string ToString() => $"ref:{Reference}";
}
