using System.Globalization;
using System.Reflection;
using redb.Route.Abstractions;
using redb.Route.Expressions.Ast;
using SysExpression = System.Linq.Expressions.Expression;

namespace redb.Route.RedbCore.Query;

/// <summary>
/// Translates a route-language condition string into a server-side redb LINQ predicate — the
/// visitor over the engine's ONE expression AST (no parser of its own, §9 of the language
/// catalog). The split rule: a subtree that references a props property of the target type is
/// TRANSLATED into the LINQ expression; a subtree that does not (headers, body, functions,
/// arithmetic — anything the engine can evaluate) is FOLDED per message through the AST's own
/// <see cref="AstNode.Evaluate"/> and enters the query as a constant. What is neither — an
/// unknown identifier, arithmetic over a props property, an untranslatable function — refuses
/// LOUDLY at route build, never by silently filtering on the client.
/// </summary>
internal static class RedbQueryTranslator
{
    /// <summary>The engine's value roots — never props properties (the tokenizer's reserved words).</summary>
    private static readonly HashSet<string> EngineRoots = new(StringComparer.OrdinalIgnoreCase)
        { "body", "header", "property", "jpath", "xpath", "logical" };

    /// <summary>The string functions translated onto props (everything else stays value-side).</summary>
    private static readonly Dictionary<string, string> StringFunctions = new(StringComparer.OrdinalIgnoreCase)
        { ["contains"] = nameof(string.Contains), ["startsWith"] = nameof(string.StartsWith), ["endsWith"] = nameof(string.EndsWith) };

    /// <summary>
    /// Builds the per-message predicate factory: parse and translate ONCE at definition time,
    /// produce an <c>Expression&lt;Func&lt;TProps, bool&gt;&gt;</c> per message (value-side
    /// subtrees are evaluated against the exchange right then).
    /// </summary>
    public static Func<IExchange, System.Linq.Expressions.LambdaExpression> TranslateWhere(Type propsType, string where)
    {
        var ast = Parse(where);
        var parameter = SysExpression.Parameter(propsType, "p");
        var build = BuildBool(ast, propsType, parameter, where);
        return exchange => SysExpression.Lambda(build(exchange), parameter);
    }

    /// <summary>
    /// An ordering key selector over a props path — static, nothing per-message in it. Typed
    /// exactly <c>Func&lt;TProps, TKey&gt;</c> with the member's own key type: the provider's
    /// ordering parser reads a property access, not a boxing Convert (такт E2E finding).
    /// </summary>
    public static System.Linq.Expressions.LambdaExpression TranslateOrderBy(Type propsType, string path)
    {
        var parameter = SysExpression.Parameter(propsType, "p");
        if (!TryPropsMember(Parse(path), propsType, parameter, out var member))
            throw new InvalidOperationException(
                $"orderBy '{path}' is not a props property path of {propsType.Name} — ordering happens server-side, on props only.");
        return SysExpression.Lambda(
            typeof(Func<,>).MakeGenericType(propsType, member!.Type), member, parameter);
    }

    // ── the visitor ──────────────────────────────────────────────────

    private static AstNode Parse(string expression)
    {
        var tokens = new Tokenizer(expression).GetAllTokens();
        return new Parser(tokens).Parse();
    }

    /// <summary>A subtree that must come out boolean.</summary>
    private static Func<IExchange, SysExpression> BuildBool(
        AstNode node, Type propsType, System.Linq.Expressions.ParameterExpression parameter, string source)
    {
        switch (node)
        {
            case BinaryOperationNode { Operator: "AND" or "OR" } logical:
            {
                var left = BuildBool(logical.Left, propsType, parameter, source);
                var right = BuildBool(logical.Right, propsType, parameter, source);
                return logical.Operator == "AND"
                    ? x => SysExpression.AndAlso(left(x), right(x))
                    : x => SysExpression.OrElse(left(x), right(x));
            }

            case UnaryOperationNode { Operator: "NOT" or "!" } not:
            {
                var operand = BuildBool(not.Operand, propsType, parameter, source);
                return x => SysExpression.Not(operand(x));
            }

            case BinaryOperationNode { Operator: "==" or "!=" or ">" or "<" or ">=" or "<=" } comparison:
                return BuildComparison(comparison, propsType, parameter, source);

            case FunctionCallNode function when StringFunctions.ContainsKey(function.Name):
                return BuildStringFunction(function, propsType, parameter, source);

            default:
            {
                // A bool props property standing alone — or a value-only subtree, folded to a
                // per-message boolean constant (a header gate inside AND is legitimate).
                if (TryPropsMember(node, propsType, parameter, out var member))
                {
                    if (member!.Type != typeof(bool))
                        throw Refuse(source, $"'{node}' is a {member.Type.Name} props property where a condition is expected");
                    return _ => member;
                }
                if (ContainsPropsRef(node, propsType))
                    throw Refuse(source, $"'{node}' mixes props properties into an operation the storage cannot run " +
                                         "server-side (arithmetic and unknown functions stay on the value side, or use filter=\"#spec\")");
                ValidateValueSide(node, propsType, source);
                return x => SysExpression.Constant(
                    System.Convert.ToBoolean(node.Evaluate(x) ?? false, CultureInfo.InvariantCulture));
            }
        }
    }

    private static Func<IExchange, SysExpression> BuildComparison(
        BinaryOperationNode comparison, Type propsType, System.Linq.Expressions.ParameterExpression parameter, string source)
    {
        var leftIsProps = TryPropsMember(comparison.Left, propsType, parameter, out var leftMember);
        var rightIsProps = TryPropsMember(comparison.Right, propsType, parameter, out var rightMember);

        if (leftIsProps && rightIsProps)
            return _ => MakeBinary(comparison.Operator, leftMember!, rightMember!);

        if (!leftIsProps && !rightIsProps)
        {
            // No props reference at all — a per-message gate; still refuse when a props property
            // is buried inside arithmetic (that is the silent-mistranslation trap).
            if (ContainsPropsRef(comparison, propsType))
                throw Refuse(source, $"'{comparison}' uses a props property inside an expression the storage " +
                                     "cannot run server-side (move it to a plain comparison, or use filter=\"#spec\")");
            ValidateValueSide(comparison, propsType, source);
            return x => SysExpression.Constant(
                System.Convert.ToBoolean(comparison.Evaluate(x) ?? false, CultureInfo.InvariantCulture));
        }

        var (member, valueNode, op) = leftIsProps
            ? (leftMember!, comparison.Right, comparison.Operator)
            : (rightMember!, comparison.Left, Mirror(comparison.Operator));
        if (member.Type == typeof(string) && op is ">" or "<" or ">=" or "<=")
            throw Refuse(source, $"ordering comparison '{op}' on a string props property is not supported " +
                                 "(compare equality, or use filter=\"#spec\")");
        if (ContainsPropsRef(valueNode, propsType))
            throw Refuse(source, $"'{valueNode}' mixes a props property into the value side of a comparison " +
                                 "(compare a props path against a value, or use filter=\"#spec\")");
        ValidateValueSide(valueNode, propsType, source);

        return x => MakeBinary(op, member, ConstantOfType(valueNode.Evaluate(x), member.Type, source));
    }

    private static Func<IExchange, SysExpression> BuildStringFunction(
        FunctionCallNode function, Type propsType, System.Linq.Expressions.ParameterExpression parameter, string source)
    {
        if (function.Arguments.Count != 2)
            throw Refuse(source, $"{function.Name}() takes (propsPath, value)");
        if (!TryPropsMember(function.Arguments[0], propsType, parameter, out var member))
            throw Refuse(source, $"{function.Name}(): the first argument must be a props property path");
        if (member!.Type != typeof(string))
            throw Refuse(source, $"{function.Name}(): '{function.Arguments[0]}' is {member.Type.Name}, not string");
        if (ContainsPropsRef(function.Arguments[1], propsType))
            throw Refuse(source, $"{function.Name}(): the value argument must not reference props");
        ValidateValueSide(function.Arguments[1], propsType, source);

        var method = typeof(string).GetMethod(StringFunctions[function.Name], [typeof(string)])!;
        var valueNode = function.Arguments[1];
        return x => SysExpression.Call(member,
            method, ConstantOfType(valueNode.Evaluate(x), typeof(string), source));
    }

    // ── props-path resolution and classification ─────────────────────

    /// <summary>
    /// Resolves the node as a props member chain (<c>Status</c>, <c>Customer.Name</c>) when its
    /// root identifier is a property of the props type (engine roots never are). Segment lookup
    /// is case-insensitive; a wrong segment refuses loudly with the candidates.
    /// </summary>
    private static bool TryPropsMember(
        AstNode node, Type propsType, System.Linq.Expressions.ParameterExpression parameter, out SysExpression? member)
    {
        member = null;
        var segments = new Stack<string>();
        var current = node;
        while (current is PropertyAccessNode access)
        {
            segments.Push(access.PropertyName);
            current = access.Object;
        }
        if (current is not IdentifierNode root)
            return false;

        // The tokenizer keeps dots inside one identifier (`Customer.Name` is a single token).
        var rootSegments = root.Name.Split('.');
        if (EngineRoots.Contains(rootSegments[0]))
            return false;
        foreach (var segment in rootSegments.Skip(1).Reverse())
            segments.Push(segment);

        var property = FindProperty(propsType, rootSegments[0]);
        if (property is null)
            return false;

        SysExpression expr = SysExpression.Property(parameter, property);
        foreach (var segment in segments)
        {
            var next = FindProperty(expr.Type, segment)
                ?? throw new InvalidOperationException(
                    $"'{segment}' is not a property of {expr.Type.Name} (path '{node}'); available: " +
                    string.Join(", ", expr.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)));
            expr = SysExpression.Property(expr, next);
        }
        member = expr;
        return true;
    }

    /// <summary>
    /// A value-side subtree may reference only the engine's roots — a bare identifier that is
    /// neither props nor engine is almost certainly a typo, and folding it to null would filter
    /// silently wrong. Refuse loudly instead.
    /// </summary>
    private static void ValidateValueSide(AstNode node, Type propsType, string source)
    {
        switch (node)
        {
            case IdentifierNode id when !EngineRoots.Contains(RootOf(id.Name)):
                throw Refuse(source, $"'{id.Name}' is neither a props property of {propsType.Name} (available: " +
                    string.Join(", ", propsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)) +
                    ") nor an engine value (header./body/property.)");
            case BinaryOperationNode binary:
                ValidateValueSide(binary.Left, propsType, source);
                ValidateValueSide(binary.Right, propsType, source);
                break;
            case UnaryOperationNode unary:
                ValidateValueSide(unary.Operand, propsType, source);
                break;
            case FunctionCallNode function:
                foreach (var argument in function.Arguments)
                    ValidateValueSide(argument, propsType, source);
                break;
            case PropertyAccessNode access:
                ValidateValueSide(Root(access), propsType, source);
                break;
        }
    }

    private static PropertyInfo? FindProperty(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    private static bool ContainsPropsRef(AstNode node, Type propsType) => node switch
    {
        IdentifierNode id => !EngineRoots.Contains(RootOf(id.Name)) && FindProperty(propsType, RootOf(id.Name)) is not null,
        PropertyAccessNode access => ContainsPropsRef(Root(access), propsType),
        BinaryOperationNode binary => ContainsPropsRef(binary.Left, propsType) || ContainsPropsRef(binary.Right, propsType),
        UnaryOperationNode unary => ContainsPropsRef(unary.Operand, propsType),
        FunctionCallNode function => function.Arguments.Any(a => ContainsPropsRef(a, propsType)),
        _ => false,
    };

    private static AstNode Root(PropertyAccessNode access)
    {
        AstNode current = access;
        while (current is PropertyAccessNode a)
            current = a.Object;
        return current;
    }

    /// <summary>The engine resolves dotted identifiers itself (<c>header.x</c> is ONE identifier).</summary>
    private static string RootOf(string identifier)
    {
        var dot = identifier.IndexOf('.');
        return dot < 0 ? identifier : identifier[..dot];
    }

    // ── expression assembly ──────────────────────────────────────────

    private static SysExpression MakeBinary(string op, SysExpression left, SysExpression right) => op switch
    {
        "==" => SysExpression.Equal(left, right),
        "!=" => SysExpression.NotEqual(left, right),
        ">" => SysExpression.GreaterThan(left, right),
        "<" => SysExpression.LessThan(left, right),
        ">=" => SysExpression.GreaterThanOrEqual(left, right),
        "<=" => SysExpression.LessThanOrEqual(left, right),
        _ => throw new InvalidOperationException($"operator '{op}' is not translatable."),
    };

    private static string Mirror(string op) => op switch
    {
        ">" => "<", "<" => ">", ">=" => "<=", "<=" => ">=", _ => op,
    };

    /// <summary>A folded value as a typed constant of the member's type (invariant conversion).</summary>
    private static SysExpression ConstantOfType(object? value, Type targetType, string source)
    {
        if (value is null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                throw Refuse(source, $"null compared against a non-nullable {targetType.Name}");
            return SysExpression.Constant(null, targetType);
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        object converted = value.GetType() == underlying
            ? value
            : underlying switch
            {
                _ when underlying == typeof(Guid) => Guid.Parse(value.ToString()!),
                _ when underlying.IsEnum => Enum.Parse(underlying, value.ToString()!, ignoreCase: true),
                _ when underlying == typeof(DateTime) => System.Convert.ToDateTime(value, CultureInfo.InvariantCulture),
                _ => System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture),
            };
        return SysExpression.Constant(converted, targetType);
    }

    private static InvalidOperationException Refuse(string source, string reason)
        => new($"where '{source}': {reason}.");
}
