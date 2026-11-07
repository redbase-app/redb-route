using System.Globalization;
using System.Reflection;
using redb.Core.Models.Contracts;
using redb.Core.Query.Mapping;
using redb.Route.Abstractions;
using redb.Route.Expressions.Ast;
using SysExpression = System.Linq.Expressions.Expression;

namespace redb.Route.RedbCore.Query;

/// <summary>
/// What a condition string is translated against: the props type (<c>where=</c>, redb
/// <c>Where</c>) or the base fields of the stored object (<c>whereRedb=</c>, redb
/// <c>WhereRedb</c> over <see cref="IRedbObject"/>). One translator serves both; the root
/// decides which identifiers are storage members and how the refusals name them.
/// </summary>
internal sealed class QueryRoot
{
    private readonly Func<string, PropertyInfo?> _find;

    private QueryRoot(Type type, string noun, string attribute, bool nested, Func<string, PropertyInfo?> find, IEnumerable<string> candidates)
    {
        Type = type;
        Noun = noun;
        Attribute = attribute;
        Nested = nested;
        _find = find;
        Candidates = string.Join(", ", candidates);
    }

    /// <summary>The lambda parameter type.</summary>
    public Type Type { get; }

    /// <summary>How a member is called in a refusal: "props property" or "base field".</summary>
    public string Noun { get; }

    /// <summary>The attribute the condition came from, for the refusal prefix.</summary>
    public string Attribute { get; }

    /// <summary>Whether a member path may walk further (<c>Customer.Name</c>); base fields are flat.</summary>
    public bool Nested { get; }

    /// <summary>The member names, for a refusal that helps fix a typo.</summary>
    public string Candidates { get; }

    /// <summary>The root member by name (case-insensitive), or null when the name is not one.</summary>
    public PropertyInfo? Find(string name) => _find(name);

    /// <summary>The props of <paramref name="propsType"/>: every public instance property, paths nest.</summary>
    public static QueryRoot Props(Type propsType) => new(propsType, "props property", "where", nested: true,
        name => FindProperty(propsType, name),
        propsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));

    private static readonly PropertyInfo[] BaseFields = typeof(IRedbObject)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => BaseFieldMapper.IsBaseField(p.Name) && p.Name != nameof(IRedbObject.ValueBytes))
        .ToArray();

    /// <summary>
    /// The base fields of a stored object — exactly the members the core maps to columns
    /// (<see cref="BaseFieldMapper"/>). The core maps an unknown name to <c>_id</c> without a
    /// word, so nothing outside that list may reach it. <c>ValueBytes</c> is left out: a byte
    /// array has no comparison a condition string could express.
    /// </summary>
    public static QueryRoot Base { get; } = new(typeof(IRedbObject), "base field", "whereRedb", nested: false,
        name => BaseFields.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)),
        BaseFields.Select(p => p.Name));

    private static PropertyInfo? FindProperty(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
}

/// <summary>
/// Translates a route-language condition string into a server-side redb LINQ predicate — the
/// visitor over the engine's ONE expression AST (no parser of its own, §9 of the language
/// catalog). The split rule: a subtree that references a storage member of the root (a props
/// property, or a base field for <c>whereRedb</c>) is TRANSLATED into the LINQ expression; a
/// subtree that does not (headers, body, functions, arithmetic — anything the engine can
/// evaluate) is FOLDED per message through the AST's own <see cref="AstNode.Evaluate"/> and
/// enters the query as a constant. What is neither — an unknown identifier, arithmetic over a
/// storage member, an untranslatable function — refuses LOUDLY at route build, never by
/// silently filtering on the client.
/// </summary>
internal static class RedbQueryTranslator
{
    /// <summary>The engine's value roots — never storage members (the tokenizer's reserved words).</summary>
    private static readonly HashSet<string> EngineRoots = new(StringComparer.OrdinalIgnoreCase)
        { "body", "header", "property", "jpath", "xpath", "logical" };

    /// <summary>The string functions translated onto storage members (everything else stays value-side).</summary>
    private static readonly Dictionary<string, string> StringFunctions = new(StringComparer.OrdinalIgnoreCase)
        { ["contains"] = nameof(string.Contains), ["startsWith"] = nameof(string.StartsWith), ["endsWith"] = nameof(string.EndsWith) };

    /// <summary>
    /// Builds the per-message predicate factory: parse and translate ONCE at definition time,
    /// produce an <c>Expression&lt;Func&lt;TRoot, bool&gt;&gt;</c> per message (value-side
    /// subtrees are evaluated against the exchange right then).
    /// </summary>
    public static Func<IExchange, System.Linq.Expressions.LambdaExpression> TranslateWhere(QueryRoot root, string where)
    {
        var ast = Parse(where);
        var parameter = SysExpression.Parameter(root.Type, "p");
        var build = BuildBool(ast, root, parameter, where);
        return exchange => SysExpression.Lambda(build(exchange), parameter);
    }

    /// <summary>
    /// An ordering key selector over a member path — static, nothing per-message in it. Typed
    /// exactly <c>Func&lt;TRoot, TKey&gt;</c> with the member's own key type: the provider's
    /// ordering parser reads a property access, not a boxing Convert (такт E2E finding).
    /// </summary>
    public static System.Linq.Expressions.LambdaExpression TranslateOrderBy(QueryRoot root, string path)
    {
        var parameter = SysExpression.Parameter(root.Type, "p");
        if (!TryMember(Parse(path), root, parameter, out var member))
            throw new InvalidOperationException(
                $"orderBy '{path}' is not a {root.Noun} path of {root.Type.Name} — ordering happens server-side " +
                $"(available: {root.Candidates}).");
        return SysExpression.Lambda(
            typeof(Func<,>).MakeGenericType(root.Type, member!.Type), member, parameter);
    }

    // ── the visitor ──────────────────────────────────────────────────

    private static AstNode Parse(string expression)
    {
        var tokens = new Tokenizer(expression).GetAllTokens();
        return new Parser(tokens).Parse();
    }

    /// <summary>A subtree that must come out boolean.</summary>
    private static Func<IExchange, SysExpression> BuildBool(
        AstNode node, QueryRoot root, System.Linq.Expressions.ParameterExpression parameter, string source)
    {
        switch (node)
        {
            case BinaryOperationNode { Operator: "AND" or "OR" } logical:
            {
                var left = BuildBool(logical.Left, root, parameter, source);
                var right = BuildBool(logical.Right, root, parameter, source);
                return logical.Operator == "AND"
                    ? x => SysExpression.AndAlso(left(x), right(x))
                    : x => SysExpression.OrElse(left(x), right(x));
            }

            case UnaryOperationNode { Operator: "NOT" or "!" } not:
            {
                var operand = BuildBool(not.Operand, root, parameter, source);
                return x => SysExpression.Not(operand(x));
            }

            case BinaryOperationNode { Operator: "==" or "!=" or ">" or "<" or ">=" or "<=" } comparison:
                return BuildComparison(comparison, root, parameter, source);

            case InOperationNode membership:
                return BuildMembership(membership, root, parameter, source);

            case FunctionCallNode function when StringFunctions.ContainsKey(function.Name):
                return BuildStringFunction(function, root, parameter, source);

            default:
            {
                // A bool member standing alone — or a value-only subtree, folded to a
                // per-message boolean constant (a header gate inside AND is legitimate).
                if (TryMember(node, root, parameter, out var member))
                {
                    if (member!.Type != typeof(bool))
                        throw Refuse(root, source, $"'{node}' is a {member.Type.Name} {root.Noun} where a condition is expected" +
                            (member.Type == typeof(bool?) ? $" (compare it: {node} == true)" : ""));
                    return _ => member;
                }
                if (ContainsMemberRef(node, root))
                    throw Refuse(root, source, $"'{node}' mixes {root.Noun} members into an operation the storage cannot run " +
                                               "server-side (arithmetic and unknown functions stay on the value side, or use filter=\"#spec\")");
                ValidateValueSide(node, root, source);
                return x => SysExpression.Constant(
                    System.Convert.ToBoolean(node.Evaluate(x) ?? false, CultureInfo.InvariantCulture));
            }
        }
    }

    private static Func<IExchange, SysExpression> BuildComparison(
        BinaryOperationNode comparison, QueryRoot root, System.Linq.Expressions.ParameterExpression parameter, string source)
    {
        var leftIsMember = TryMember(comparison.Left, root, parameter, out var leftMember);
        var rightIsMember = TryMember(comparison.Right, root, parameter, out var rightMember);

        if (leftIsMember && rightIsMember)
            return _ => MakeBinary(comparison.Operator, leftMember!, rightMember!);

        if (!leftIsMember && !rightIsMember)
        {
            // No member reference at all — a per-message gate; still refuse when a member
            // is buried inside arithmetic (that is the silent-mistranslation trap).
            if (ContainsMemberRef(comparison, root))
                throw Refuse(root, source, $"'{comparison}' uses a {root.Noun} inside an expression the storage " +
                                           "cannot run server-side (move it to a plain comparison, or use filter=\"#spec\")");
            ValidateValueSide(comparison, root, source);
            return x => SysExpression.Constant(
                System.Convert.ToBoolean(comparison.Evaluate(x) ?? false, CultureInfo.InvariantCulture));
        }

        var (member, valueNode, op) = leftIsMember
            ? (leftMember!, comparison.Right, comparison.Operator)
            : (rightMember!, comparison.Left, Mirror(comparison.Operator));
        if (member.Type == typeof(string) && op is ">" or "<" or ">=" or "<=")
            throw Refuse(root, source, $"ordering comparison '{op}' on a string {root.Noun} is not supported " +
                                       "(compare equality, or use filter=\"#spec\")");
        if (ContainsMemberRef(valueNode, root))
            throw Refuse(root, source, $"'{valueNode}' mixes a {root.Noun} into the value side of a comparison " +
                                       $"(compare a {root.Noun} against a value, or use filter=\"#spec\")");
        ValidateValueSide(valueNode, root, source);

        return x => MakeBinary(op, member, ConstantOfType(valueNode.Evaluate(x), member.Type, root, source));
    }

    /// <summary>
    /// <c>member in list</c> → <c>Enumerable.Contains(T[], member)</c>, the shape the storage turns
    /// into an SQL <c>IN</c> for props and base fields alike. The list is evaluated per message and
    /// each element converted to the member's type the way a single comparison converts its value.
    /// An empty (or missing) list is decided here as a constant — nothing found for <c>in</c>,
    /// everything for <c>not in</c> — so it never reaches the storage as <c>{"$in": []}</c>, whose
    /// meaning is decided in SQL we do not see from here.
    /// </summary>
    private static Func<IExchange, SysExpression> BuildMembership(
        InOperationNode membership, QueryRoot root, System.Linq.Expressions.ParameterExpression parameter, string source)
    {
        if (ContainsMemberRef(membership.List, root))
            throw Refuse(root, source, $"'{membership}' has a {root.Noun} on the right of in; the right side is a list " +
                                       "of values — a collection the message carries (header.codes) or a literal ('a','b')");

        if (!TryMember(membership.Value, root, parameter, out var member))
        {
            // No member on either side: a per-message gate, like any other header condition.
            if (ContainsMemberRef(membership.Value, root))
                throw Refuse(root, source, $"'{membership.Value}' uses a {root.Noun} inside an expression the storage " +
                                           "cannot run server-side (put the member alone on the left of in, or use filter=\"#spec\")");
            ValidateValueSide(membership, root, source);
            return x => SysExpression.Constant(
                System.Convert.ToBoolean(membership.Evaluate(x) ?? false, CultureInfo.InvariantCulture));
        }

        ValidateValueSide(membership.List, root, source);

        var contains = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(member!.Type);

        return x =>
        {
            var raw = membership.List.Evaluate(x);
            var items = raw switch
            {
                null => [],
                string => throw Refuse(root, source, $"'{membership}': the right side of in is a string, and a string " +
                                                     "is not split into a list — reference a collection or write ('a','b')"),
                System.Collections.IEnumerable e => e.Cast<object?>().ToList(),
                _ => throw Refuse(root, source, $"'{membership}': the right side of in is a {raw.GetType().Name}, not a collection"),
            };

            if (items.Count == 0)
                return SysExpression.Constant(membership.Negated);

            var typed = Array.CreateInstance(member.Type, items.Count);
            for (var i = 0; i < items.Count; i++)
                typed.SetValue(ConvertToType(items[i], member.Type, root, source), i);

            SysExpression test = SysExpression.Call(contains, SysExpression.Constant(typed), member);
            return membership.Negated ? SysExpression.Not(test) : test;
        };
    }

    private static Func<IExchange, SysExpression> BuildStringFunction(
        FunctionCallNode function, QueryRoot root, System.Linq.Expressions.ParameterExpression parameter, string source)
    {
        if (function.Arguments.Count != 2)
            throw Refuse(root, source, $"{function.Name}() takes ({root.Noun}, value)");
        if (!TryMember(function.Arguments[0], root, parameter, out var member))
            throw Refuse(root, source, $"{function.Name}(): the first argument must be a {root.Noun}");
        if (member!.Type != typeof(string))
            throw Refuse(root, source, $"{function.Name}(): '{function.Arguments[0]}' is {member.Type.Name}, not string");
        if (ContainsMemberRef(function.Arguments[1], root))
            throw Refuse(root, source, $"{function.Name}(): the value argument must not reference a {root.Noun}");
        ValidateValueSide(function.Arguments[1], root, source);

        var method = typeof(string).GetMethod(StringFunctions[function.Name], [typeof(string)])!;
        var valueNode = function.Arguments[1];
        return x => SysExpression.Call(member,
            method, ConstantOfType(valueNode.Evaluate(x), typeof(string), root, source));
    }

    // ── member-path resolution and classification ────────────────────

    /// <summary>
    /// Resolves the node as a member chain of the root (<c>Status</c>, <c>Customer.Name</c>;
    /// <c>ValueGuid</c> for base fields) when its root identifier is a member of the root
    /// (engine roots never are). Segment lookup is case-insensitive; a wrong segment refuses
    /// loudly with the candidates.
    /// </summary>
    private static bool TryMember(
        AstNode node, QueryRoot root, System.Linq.Expressions.ParameterExpression parameter, out SysExpression? member)
    {
        member = null;
        var segments = new Stack<string>();
        var current = node;
        while (current is PropertyAccessNode access)
        {
            segments.Push(access.PropertyName);
            current = access.Object;
        }
        if (current is not IdentifierNode identifier)
            return false;

        // The tokenizer keeps dots inside one identifier (`Customer.Name` is a single token).
        var rootSegments = identifier.Name.Split('.');
        if (EngineRoots.Contains(rootSegments[0]))
            return false;
        foreach (var segment in rootSegments.Skip(1).Reverse())
            segments.Push(segment);

        var property = root.Find(rootSegments[0]);
        if (property is null)
            return false;
        if (segments.Count > 0 && !root.Nested)
            throw new InvalidOperationException(
                $"'{node}': {root.Noun}s are flat — '{property.Name}' has no members to walk into.");

        SysExpression expr = SysExpression.Property(parameter, property);
        foreach (var segment in segments)
        {
            var next = expr.Type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
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
    /// neither a member nor engine is almost certainly a typo, and folding it to null would
    /// filter silently wrong. Refuse loudly instead.
    /// </summary>
    private static void ValidateValueSide(AstNode node, QueryRoot root, string source)
    {
        switch (node)
        {
            case IdentifierNode id when !EngineRoots.Contains(RootOf(id.Name)):
                throw Refuse(root, source, $"'{id.Name}' is neither a {root.Noun} of {root.Type.Name} (available: " +
                    root.Candidates + ") nor an engine value (header./body/property.)");
            case BinaryOperationNode binary:
                ValidateValueSide(binary.Left, root, source);
                ValidateValueSide(binary.Right, root, source);
                break;
            case UnaryOperationNode unary:
                ValidateValueSide(unary.Operand, root, source);
                break;
            case FunctionCallNode function:
                foreach (var argument in function.Arguments)
                    ValidateValueSide(argument, root, source);
                break;
            case PropertyAccessNode access:
                ValidateValueSide(Root(access), root, source);
                break;
            case InOperationNode membership:
                ValidateValueSide(membership.Value, root, source);
                ValidateValueSide(membership.List, root, source);
                break;
            case ListLiteralNode list:
                foreach (var item in list.Items)
                    ValidateValueSide(item, root, source);
                break;
        }
    }

    // Every node kind that can hold an operand must be walked here. A kind missing from this switch
    // makes a member inside it invisible, and the expression is then folded into a per-message
    // constant that silently ignores the member — exactly what happened to 'in' before it was added.
    private static bool ContainsMemberRef(AstNode node, QueryRoot root) => node switch
    {
        IdentifierNode id => !EngineRoots.Contains(RootOf(id.Name)) && root.Find(RootOf(id.Name)) is not null,
        PropertyAccessNode access => ContainsMemberRef(Root(access), root),
        BinaryOperationNode binary => ContainsMemberRef(binary.Left, root) || ContainsMemberRef(binary.Right, root),
        UnaryOperationNode unary => ContainsMemberRef(unary.Operand, root),
        FunctionCallNode function => function.Arguments.Any(a => ContainsMemberRef(a, root)),
        InOperationNode membership => ContainsMemberRef(membership.Value, root) || ContainsMemberRef(membership.List, root),
        ListLiteralNode list => list.Items.Any(i => ContainsMemberRef(i, root)),
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

    /// <summary>
    /// A folded value as a typed constant of the member's type (invariant conversion). A
    /// <see cref="DateTime"/> reaches a <see cref="DateTimeOffset"/> member the way C# converts
    /// it implicitly; text parses invariantly.
    /// </summary>
    private static SysExpression ConstantOfType(object? value, Type targetType, QueryRoot root, string source)
        => SysExpression.Constant(ConvertToType(value, targetType, root, source), targetType);

    /// <summary>
    /// The conversion behind <see cref="ConstantOfType"/>, shared with the elements of an <c>in</c>
    /// list so a list element converts exactly as a single compared value does.
    /// </summary>
    private static object? ConvertToType(object? value, Type targetType, QueryRoot root, string source)
    {
        if (value is null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                throw Refuse(root, source, $"null compared against a non-nullable {targetType.Name}");
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        object converted = value.GetType() == underlying
            ? value
            : underlying switch
            {
                _ when underlying == typeof(Guid) => Guid.Parse(value.ToString()!),
                _ when underlying.IsEnum => Enum.Parse(underlying, value.ToString()!, ignoreCase: true),
                _ when underlying == typeof(DateTime) => System.Convert.ToDateTime(value, CultureInfo.InvariantCulture),
                _ when underlying == typeof(DateTimeOffset) => value switch
                {
                    DateTime dateTime => new DateTimeOffset(dateTime),
                    _ => DateTimeOffset.Parse(value.ToString()!, CultureInfo.InvariantCulture),
                },
                _ => System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture),
            };
        return converted;
    }

    private static InvalidOperationException Refuse(QueryRoot root, string source, string reason)
        => new($"{root.Attribute} '{source}': {reason}.");
}
