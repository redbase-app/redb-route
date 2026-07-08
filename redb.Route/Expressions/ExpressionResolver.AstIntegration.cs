using System;
using System.Linq.Expressions;
using System.Reflection;
using redb.Route.Abstractions;
using redb.Route.Expressions.Ast;
using SysExpression = System.Linq.Expressions.Expression;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver — AST parsing, compilation, and integration.
/// </summary>
public static partial class ExpressionResolver
{
    #region AST public wrappers for use in CompileAstNode

    // Make private methods accessible for use in AST.
    // Use different names to avoid recursion.

    /// <summary>
    /// Applies addition via AST delegation.
    /// </summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>Result of the addition.</returns>
    public static object? Ast_ApplyAddition(object? left, object? right) => _applyAddition(left, right);

    /// <summary>
    /// Applies subtraction via AST delegation.
    /// </summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>Result of the subtraction.</returns>
    public static object? Ast_ApplySubtraction(object? left, object? right) => _applySubtraction(left, right);

    /// <summary>
    /// Applies multiplication via AST delegation.
    /// </summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>Result of the multiplication.</returns>
    public static object? Ast_ApplyMultiplication(object? left, object? right) => _applyMultiplication(left, right);

    /// <summary>
    /// Applies division via AST delegation.
    /// </summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>Result of the division.</returns>
    public static object? Ast_ApplyDivision(object? left, object? right) => _applyDivision(left, right);

    /// <summary>
    /// Applies unary plus via AST delegation.
    /// </summary>
    /// <param name="value">The operand value.</param>
    /// <returns>Result of unary plus.</returns>
    public static object? Ast_ApplyUnaryPlus(object? value) => _applyUnaryPlus(value);

    /// <summary>
    /// Applies unary minus via AST delegation.
    /// </summary>
    /// <param name="value">The operand value.</param>
    /// <returns>Result of unary minus.</returns>
    public static object? Ast_ApplyUnaryMinus(object? value) => _applyUnaryMinus(value);

    /// <summary>
    /// Applies unary NOT via AST delegation.
    /// </summary>
    /// <param name="value">The operand value.</param>
    /// <returns>Result of unary NOT.</returns>
    public static object? Ast_ApplyUnaryNot(object? value) => _applyUnaryNot(value);

    /// <summary>
    /// Checks equality of two values via AST delegation.
    /// </summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><c>true</c> if the values are equal; otherwise <c>false</c>.</returns>
    public static bool Ast_AreEqual(object? left, object? right) => _areEqual(left, right);

    /// <summary>
    /// Returns true if the values are NOT equal.
    /// </summary>
    public static bool Ast_AreNotEqual(object? left, object? right) => !_areEqual(left, right);

    /// <summary>
    /// Returns true if left is greater than right.
    /// </summary>
    public static bool Ast_GreaterThan(object? left, object? right) => _compareNumeric(left, right) > 0;

    /// <summary>
    /// Returns true if left is less than right.
    /// </summary>
    public static bool Ast_LessThan(object? left, object? right) => _compareNumeric(left, right) < 0;

    /// <summary>
    /// Returns true if left is greater than or equal to right.
    /// </summary>
    public static bool Ast_GreaterThanOrEqual(object? left, object? right) => _compareNumeric(left, right) >= 0;

    /// <summary>
    /// Returns true if left is less than or equal to right.
    /// </summary>
    public static bool Ast_LessThanOrEqual(object? left, object? right) => _compareNumeric(left, right) <= 0;

    /// <summary>
    /// Evaluates logical AND between two values.
    /// </summary>
    public static bool Ast_LogicalAnd(object? left, object? right)
    {
        if (_TryConvertToBool(left, out var l) && _TryConvertToBool(right, out var r))
            return l && r;
        return false;
    }

    /// <summary>
    /// Evaluates logical OR between two values.
    /// </summary>
    public static bool Ast_LogicalOr(object? left, object? right)
    {
        if (_TryConvertToBool(left, out var l) && _TryConvertToBool(right, out var r))
            return l || r;
        return false;
    }

    /// <summary>
    /// Evaluates logical XOR between two values.
    /// </summary>
    public static bool Ast_LogicalXor(object? left, object? right)
    {
        if (_TryConvertToBool(left, out var l) && _TryConvertToBool(right, out var r))
            return l ^ r;
        return false;
    }

    /// <summary>
    /// Returns the left operand if it is not null; otherwise returns the right operand (null-coalescing).
    /// </summary>
    public static object? Ast_NullCoalesce(object? left, object? right)
        => left ?? right;

    /// <summary>
    /// Concatenates the string representations of all arguments.
    /// </summary>
    /// <param name="args">The values to concatenate.</param>
    /// <returns>The concatenated string.</returns>
    public static object? Ast_Concat(params object?[] args)
        => string.Concat(args.Select(a => a?.ToString() ?? string.Empty));

    /// <summary>Converts a string to upper case.</summary>
    public static object? Ast_Upper(object? value) => value?.ToString()?.ToUpperInvariant();

    /// <summary>Converts a string to lower case.</summary>
    public static object? Ast_Lower(object? value) => value?.ToString()?.ToLowerInvariant();

    /// <summary>Trims whitespace from a string.</summary>
    public static object? Ast_Trim(object? value) => value?.ToString()?.Trim();

    /// <summary>Returns the length of a string or collection.</summary>
    public static object? Ast_Length(object? value)
    {
        if (value == null) return null;
        if (value is string s) return s.Length;
        if (value is System.Collections.ICollection col) return col.Count;
        if (value is System.Collections.IEnumerable en)
            return System.Linq.Enumerable.Count(System.Linq.Enumerable.Cast<object>(en));
        return value.ToString()?.Length;
    }

    /// <summary>Extracts a substring.</summary>
    public static object? Ast_Substring(object? value, object? start, object? length)
    {
        var str = value?.ToString();
        if (str == null) return null;
        if (!TryConvertToDouble(start, out var startIdx)) return str;
        var s = (int)startIdx;
        if (s < 0) s = 0;
        if (s >= str.Length) return string.Empty;
        if (length != null && TryConvertToDouble(length, out var lenVal))
        {
            var len = (int)lenVal;
            if (len <= 0) return string.Empty;
            return str.Substring(s, Math.Min(len, str.Length - s));
        }
        return str.Substring(s);
    }

    /// <summary>Returns the absolute value.</summary>
    public static object? Ast_Abs(object? value)
    {
        if (TryConvertToDouble(value, out var d))
            return d == (int)d ? (object)(int)Math.Abs(d) : Math.Abs(d);
        return null;
    }

    /// <summary>Rounds a number to the specified number of digits.</summary>
    public static object? Ast_Round(object? value, object? digits)
    {
        if (!TryConvertToDouble(value, out var v)) return null;
        int d = 0;
        if (digits != null && TryConvertToDouble(digits, out var dv)) d = (int)dv;
        return Math.Round(v, d);
    }

    /// <summary>Returns the minimum of two values.</summary>
    public static object? Ast_Min(object? a, object? b)
    {
        if (TryConvertToDouble(a, out var va) && TryConvertToDouble(b, out var vb))
            return Math.Min(va, vb);
        return null;
    }

    /// <summary>Returns the maximum of two values.</summary>
    public static object? Ast_Max(object? a, object? b)
    {
        if (TryConvertToDouble(a, out var va) && TryConvertToDouble(b, out var vb))
            return Math.Max(va, vb);
        return null;
    }

    /// <summary>Checks if a string contains a substring (case-insensitive).</summary>
    public static object? Ast_Contains(object? value, object? search)
    {
        var s = value?.ToString();
        var q = search?.ToString();
        if (s == null || q == null) return false;
        return s.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks if a string starts with a prefix (case-insensitive).</summary>
    public static object? Ast_StartsWith(object? value, object? prefix)
    {
        var s = value?.ToString();
        var p = prefix?.ToString();
        if (s == null || p == null) return false;
        return s.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks if a string ends with a suffix (case-insensitive).</summary>
    public static object? Ast_EndsWith(object? value, object? suffix)
    {
        var s = value?.ToString();
        var x = suffix?.ToString();
        if (s == null || x == null) return false;
        return s.EndsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replaces all occurrences of a substring.</summary>
    public static object? Ast_Replace(object? value, object? oldStr, object? newStr)
    {
        var s = value?.ToString();
        var o = oldStr?.ToString();
        if (s == null || o == null) return s;
        return s.Replace(o, newStr?.ToString() ?? string.Empty);
    }

    /// <summary>Returns the current UTC date/time.</summary>
    public static object? Ast_Now() => DateTime.UtcNow;

    /// <summary>Formats a date value using the specified format string.</summary>
    public static object? Ast_DateFormat(object? value, object? format)
    {
        var fmt = format?.ToString() ?? "yyyy-MM-dd";
        if (value is DateTime dt)
            return dt.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        if (value is DateTimeOffset dto)
            return dto.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        if (DateTime.TryParse(value?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        return value?.ToString();
    }

    /// <summary>Adds a time interval to a date value.</summary>
    public static object? Ast_DateAdd(object? value, object? amount, object? unit)
    {
        DateTime baseDate;
        if (value is DateTime d1) baseDate = d1;
        else if (value is DateTimeOffset d2) baseDate = d2.UtcDateTime;
        else if (DateTime.TryParse(value?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.None, out var p)) baseDate = p;
        else return null;

        if (!TryConvertToDouble(amount, out var amt)) return null;
        var u = unit?.ToString()?.ToLowerInvariant() ?? "days";
        return u switch
        {
            "days" or "day" => baseDate.AddDays(amt),
            "hours" or "hour" => baseDate.AddHours(amt),
            "minutes" or "minute" => baseDate.AddMinutes(amt),
            "seconds" or "second" => baseDate.AddSeconds(amt),
            "months" or "month" => baseDate.AddMonths((int)amt),
            "years" or "year" => baseDate.AddYears((int)amt),
            _ => null
        };
    }

    /// <summary>Sums all numeric values in a collection.</summary>
    public static object? Ast_Sum(object? value)
    {
        if (value is System.Collections.IEnumerable en && value is not string)
        {
            double total = 0;
            foreach (var item in en)
                if (TryConvertToDouble(item, out var v)) total += v;
            return total;
        }
        return null;
    }

    /// <summary>Averages all numeric values in a collection.</summary>
    public static object? Ast_Avg(object? value)
    {
        if (value is System.Collections.IEnumerable en && value is not string)
        {
            double total = 0; int count = 0;
            foreach (var item in en)
                if (TryConvertToDouble(item, out var v)) { total += v; count++; }
            return count > 0 ? total / count : (object?)null;
        }
        return null;
    }

    /// <summary>Counts elements in a collection.</summary>
    public static object? Ast_Count(object? value)
    {
        if (value == null) return 0;
        if (value is System.Collections.ICollection col) return col.Count;
        if (value is System.Collections.IEnumerable en && value is not string)
            return System.Linq.Enumerable.Count(System.Linq.Enumerable.Cast<object>(en));
        return 1;
    }

    /// <summary>Evaluates a logical() function — converts a value to bool.</summary>
    public static object? Ast_Logical(object? value, IExchange exchange)
    {
        if (TryConvertToBool(value, out var result))
            return result;
        return false;
    }

    /// <summary>Evaluates a jpath() function via AST delegation.</summary>
    public static object? Ast_JPath(object? path, IExchange exchange)
    {
        var pathStr = path?.ToString();
        if (pathStr == null) return null;
        return ApplyJPath(exchange, pathStr);
    }

    /// <summary>Evaluates an xpath() function via AST delegation.</summary>
    public static object? Ast_XPath(object? path, IExchange exchange)
    {
        var pathStr = path?.ToString();
        if (pathStr == null) return null;
        return ApplyXPath(exchange, pathStr);
    }

    /// <summary>
    /// Accesses an element by index in a collection, array, or dictionary-like object.
    /// </summary>
    /// <param name="obj">The collection or array object.</param>
    /// <param name="index">The index value (integer or string key).</param>
    /// <returns>The element at the given index, or null if access fails.</returns>
    public static object? Ast_IndexAccess(object? obj, object? index)
    {
        if (obj == null || index == null) return null;

        // Try numeric indexing
        if (index is int intIndex || (index is string s && int.TryParse(s, out intIndex)))
        {
            if (obj is System.Collections.IList list)
            {
                if (intIndex >= 0 && intIndex < list.Count)
                    return list[intIndex];
                return null;
            }

            if (obj is Array arr)
            {
                if (intIndex >= 0 && intIndex < arr.Length)
                    return arr.GetValue(intIndex);
                return null;
            }

            // Try IEnumerable via ElementAt
            if (obj is System.Collections.IEnumerable enumerable)
            {
                int i = 0;
                foreach (var item in enumerable)
                {
                    if (i == intIndex) return item;
                    i++;
                }
                return null;
            }
        }

        // Try string key (for dictionaries)
        var keyStr = index.ToString();
        if (keyStr != null)
        {
            if (obj is System.Collections.IDictionary dict)
            {
                if (dict.Contains(keyStr)) return dict[keyStr];
                return null;
            }

            // Try generic IDictionary<string, object>
            if (obj is IDictionary<string, object> genDict)
            {
                genDict.TryGetValue(keyStr, out var value);
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Compares two numeric values via AST delegation.
    /// </summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>A negative, zero, or positive integer indicating the comparison result.</returns>
    public static int Ast_CompareNumeric(object? left, object? right) => _compareNumeric(left, right);

    /// <summary>
    /// Attempts to convert a value to a boolean via AST delegation.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="result">The boolean result if conversion succeeds.</param>
    /// <returns><c>true</c> if conversion succeeded; otherwise <c>false</c>.</returns>
    public static bool Ast_TryConvertToBool(object? value, out bool result) 
    {
        return _TryConvertToBool(value, out result);
    }

    /// <summary>
    /// Converts a value to boolean. Returns <c>false</c> for null, empty strings, zero, and "false";
    /// returns <c>true</c> for non-null/non-empty values that can't be parsed as a known boolean format.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The boolean interpretation of the value.</returns>
    public static bool Ast_ConvertToBool(object? value)
    {
        if (value == null) return false;
        if (Ast_TryConvertToBool(value, out var result)) return result;
        // For values that can't be converted (e.g. arbitrary strings, objects), 
        // treat non-null as truthy (similar to JavaScript/Python truthiness)
        return true;
    }

    /// <summary>
    /// Resolves a property path on an object via AST delegation.
    /// </summary>
    /// <param name="obj">The source object.</param>
    /// <param name="path">The dot-separated property path.</param>
    /// <returns>The resolved property value.</returns>
    public static object? Ast_ResolvePropertyPath(object? obj, string path) => _resolvePropertyPath(obj, path);

    // Store references to the original methods.
    private static readonly Func<object?, object?, object?> _applyAddition = ApplyAddition;
    private static readonly Func<object?, object?, object?> _applySubtraction = ApplySubtraction;
    private static readonly Func<object?, object?, object?> _applyMultiplication = ApplyMultiplication;
    private static readonly Func<object?, object?, object?> _applyDivision = ApplyDivision;
    private static readonly Func<object?, object?> _applyUnaryPlus = ApplyUnaryPlus;
    private static readonly Func<object?, object?> _applyUnaryMinus = ApplyUnaryMinus;
    private static readonly Func<object?, object?> _applyUnaryNot = ApplyUnaryNot;
    private static readonly Func<object?, object?, bool> _areEqual = AreEqual;
    private static readonly Func<object?, object?, int> _compareNumeric = CompareNumeric;
    // Cannot use an out parameter in a Func delegate, so we do not store a reference to TryConvertToBool.
    private static readonly Func<object?, string, object?> _resolvePropertyPath = ResolvePropertyPath;

    #endregion

    #region Caching helper methods

    /// <summary>
    /// Caches a compiled template.
    /// </summary>
    /// <param name="template">The template string key.</param>
    /// <param name="compiledTemplate">The compiled template delegate.</param>
    private static void CacheCompiledTemplate(string template, Func<IExchange, string> compiledTemplate)
    {
        _templateCache[template] = compiledTemplate;
    }
    
    /// <summary>
    /// Gets a compiled template from the cache.
    /// </summary>
    /// <param name="cacheKey">The cache key (may include context prefix).</param>
    /// <returns>The cached compiled template delegate, or <c>null</c> if not found.</returns>
    private static Func<IExchange, string>? GetCachedCompiledTemplate(string cacheKey)
    {
        if (_templateCache.TryGetValue(cacheKey, out var compiled))
        {
            return compiled;
        }
        return null;
    }

    #endregion

    #region Compilation via AST

    /// <summary>
    /// Compiles an expression using the AST pipeline.
    /// </summary>
    /// <param name="expression">The expression string to compile.</param>
    /// <returns>A compiled delegate that evaluates the expression against an <see cref="IExchange"/>.</returns>
    private static Func<IExchange, object?> CompileExpressionWithAst(string expression)
    {
        try
        {
            DebugLog($"Compiling expression via AST: '{expression}'");
            
            // Use Tokenizer and Parser to build the AST
            var tokenizer = new Ast.Tokenizer(expression);
            var tokens = tokenizer.GetAllTokens();
            var parser = new Ast.Parser(tokens);
            var ast = parser.Parse();
            
            DebugLog($"Built AST: {ast}");
            
            // Exchange parameter for the lambda expression
            var exchangeParam = SysExpression.Parameter(typeof(IExchange), "exchange");
            
            // Compile the AST node into an Expression
            var body = CompileAstNode(ast, exchangeParam);
            
            // Ensure the body is boxed to object? (comparison operators return bool/int)
            if (body.Type != typeof(object) && body.Type.IsValueType)
            {
                body = SysExpression.Convert(body, typeof(object));
            }
            
            // Create the lambda expression
            var lambda = SysExpression.Lambda<Func<IExchange, object?>>(body, exchangeParam);
            
            DebugLog($"Compiled lambda expression: {lambda}");
            
            // Compile the lambda expression into a delegate
            return lambda.Compile();
        }
        catch (Exception ex)
        {
            throw new ExpressionCompilationException($"Error compiling expression '{expression}' using AST: {ex.Message}", ex);
        }
    }
    
    /// <summary>
    /// Compiles an AST node into a <see cref="SysExpression"/>.
    /// </summary>
    /// <param name="node">The AST node to compile.</param>
    /// <param name="exchangeParam">The exchange parameter expression.</param>
    /// <returns>The compiled <see cref="SysExpression"/>.</returns>
    private static SysExpression CompileAstNode(AstNode node, ParameterExpression exchangeParam)
    {
        if (node == null)
            throw new ArgumentNullException(nameof(node));

        DebugLog($"Compiling AST node: {node.GetType().Name}");

        switch (node)
        {
            case LiteralNode literalNode:
                return SysExpression.Constant(literalNode.Value);

            case IdentifierNode identifierNode:
                // Get property from the exchange
                var getPropertyMethod = typeof(ExpressionResolver).GetMethod(
                    nameof(GetExchangeProperty), 
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                return SysExpression.Call(
                    getPropertyMethod,
                    exchangeParam,
                    SysExpression.Constant(identifierNode.Name));

            case PropertyAccessNode propertyAccessNode:
                // Get the object and its property
                var objExpr = CompileAstNode(propertyAccessNode.Object, exchangeParam);
                
                var resolvePropertyMethod = typeof(ExpressionResolver).GetMethod(
                    nameof(ResolvePropertyPathWithExchange), 
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                return SysExpression.Call(
                    resolvePropertyMethod,
                    objExpr,
                    SysExpression.Constant(propertyAccessNode.PropertyName),
                    exchangeParam);

            case IndexAccessNode indexNode:
                var indexObjExpr = CompileAstNode(indexNode.Object, exchangeParam);
                var indexExpr = CompileAstNode(indexNode.Index, exchangeParam);

                // Ensure both are object?
                if (indexObjExpr.Type != typeof(object))
                    indexObjExpr = SysExpression.Convert(indexObjExpr, typeof(object));
                if (indexExpr.Type != typeof(object))
                    indexExpr = SysExpression.Convert(indexExpr, typeof(object));

                var indexAccessMethod = typeof(ExpressionResolver).GetMethod(
                    nameof(Ast_IndexAccess),
                    BindingFlags.Public | BindingFlags.Static);

                return SysExpression.Call(indexAccessMethod, indexObjExpr, indexExpr);

            case UnaryOperationNode unaryNode:
                if (unaryNode.Operator == "++" || unaryNode.Operator == "--")
                {
                    // Get the property name for increment/decrement
                    string propertyName = GetPropertyNameFromNode(unaryNode.Operand);
                    
                    if (string.IsNullOrEmpty(propertyName))
                    {
                        throw new ExpressionCompilationException(
                            $"Failed to get property name for operation {unaryNode.Operator}");
                    }
                    
                    // Select the appropriate method for prefix increment/decrement
                    MethodInfo method = unaryNode.Operator == "++" 
                        ? typeof(ExpressionResolver).GetMethod(
                            nameof(ApplyPrefixIncrement),
                            BindingFlags.NonPublic | BindingFlags.Static)
                        : typeof(ExpressionResolver).GetMethod(
                            nameof(ApplyPrefixDecrement),
                            BindingFlags.NonPublic | BindingFlags.Static);
                    
                    return SysExpression.Call(
                        method,
                        exchangeParam,
                        SysExpression.Constant(propertyName));
                }
                else
                {
                    // Compile the operand, ensuring it is boxed to object?
                    var operandExpr = CompileAstNode(unaryNode.Operand, exchangeParam);
                    if (operandExpr.Type != typeof(object))
                        operandExpr = SysExpression.Convert(operandExpr, typeof(object));
                    
                    // Select the appropriate method for the unary operation
                    MethodInfo method;
                    switch (unaryNode.Operator)
                    {
                        case "+":
                            method = typeof(ExpressionResolver).GetMethod(
                                nameof(Ast_ApplyUnaryPlus),
                                BindingFlags.Public | BindingFlags.Static);
                            break;
                        case "-":
                            method = typeof(ExpressionResolver).GetMethod(
                                nameof(Ast_ApplyUnaryMinus),
                                BindingFlags.Public | BindingFlags.Static);
                            break;
                        case "NOT":
                        case "!":
                            method = typeof(ExpressionResolver).GetMethod(
                                nameof(Ast_ApplyUnaryNot),
                                BindingFlags.Public | BindingFlags.Static);
                            break;
                        default:
                            throw new ExpressionCompilationException(
                                $"Unsupported unary operator: {unaryNode.Operator}");
                    }
                    
                    return SysExpression.Call(method, operandExpr);
                }

            case BinaryOperationNode binaryNode:
                // Compile the left and right operands, ensuring they are boxed to object?
                var leftExpr = CompileAstNode(binaryNode.Left, exchangeParam);
                var rightExpr = CompileAstNode(binaryNode.Right, exchangeParam);
                
                if (leftExpr.Type != typeof(object))
                    leftExpr = SysExpression.Convert(leftExpr, typeof(object));
                if (rightExpr.Type != typeof(object))
                    rightExpr = SysExpression.Convert(rightExpr, typeof(object));
                
                // Select the appropriate method for the binary operation
                MethodInfo binaryMethod;
                switch (binaryNode.Operator)
                {
                    case "+":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_ApplyAddition),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "-":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_ApplySubtraction),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "*":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_ApplyMultiplication),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "/":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_ApplyDivision),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "==":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_AreEqual),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "!=":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_AreNotEqual),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case ">":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_GreaterThan),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "<":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_LessThan),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case ">=":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_GreaterThanOrEqual),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "<=":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_LessThanOrEqual),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "AND":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_LogicalAnd),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "OR":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_LogicalOr),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "XOR":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_LogicalXor),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    case "??":
                        binaryMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_NullCoalesce),
                            BindingFlags.Public | BindingFlags.Static);
                        break;
                    default:
                        throw new ExpressionCompilationException(
                            $"Unsupported binary operator: {binaryNode.Operator}");
                }
                
                return SysExpression.Call(binaryMethod, leftExpr, rightExpr);

            case PostfixOperationNode postfixNode:
                // Get the property name for increment/decrement
                string postfixPropertyName = GetPropertyNameFromNode(postfixNode.Operand);
                
                if (string.IsNullOrEmpty(postfixPropertyName))
                {
                    throw new ExpressionCompilationException(
                        $"Failed to get property name for postfix operation {postfixNode.Operator}");
                }
                
                // Select the appropriate method for postfix increment/decrement
                MethodInfo postfixMethod = postfixNode.Operator == "++" 
                    ? typeof(ExpressionResolver).GetMethod(
                        nameof(ApplyPostfixIncrement),
                        BindingFlags.NonPublic | BindingFlags.Static)
                    : typeof(ExpressionResolver).GetMethod(
                        nameof(ApplyPostfixDecrement),
                        BindingFlags.NonPublic | BindingFlags.Static);
                
                return SysExpression.Call(
                    postfixMethod,
                    exchangeParam,
                    SysExpression.Constant(postfixPropertyName));
                
            case FunctionCallNode functionNode:
                switch (functionNode.Name.ToLowerInvariant())
                {
                    case "concat":
                        // Compile all arguments and call Ast_Concat
                        var concatArgs = functionNode.Arguments
                            .Select(arg => CompileAstNode(arg, exchangeParam))
                            .ToArray();
                        
                        // Create an array of object? from the compiled arguments
                        var arrayInit = SysExpression.NewArrayInit(typeof(object),
                            concatArgs.Select(a => a.Type == typeof(object)
                                ? a
                                : SysExpression.Convert(a, typeof(object))));

                        var concatMethod = typeof(ExpressionResolver).GetMethod(
                            nameof(Ast_Concat),
                            BindingFlags.Public | BindingFlags.Static);

                        return SysExpression.Call(concatMethod, arrayInit);

                    case "upper":
                        return CompileSingleArgFunction(functionNode, exchangeParam, nameof(Ast_Upper));

                    case "lower":
                        return CompileSingleArgFunction(functionNode, exchangeParam, nameof(Ast_Lower));

                    case "trim":
                        return CompileSingleArgFunction(functionNode, exchangeParam, nameof(Ast_Trim));

                    case "length":
                        return CompileSingleArgFunction(functionNode, exchangeParam, nameof(Ast_Length));

                    case "abs":
                        return CompileSingleArgFunction(functionNode, exchangeParam, nameof(Ast_Abs));

                    case "substring":
                    {
                        var args = functionNode.Arguments.Select(a => CompileAstNode(a, exchangeParam)).ToArray();
                        var val = args.Length > 0 ? BoxToObject(args[0]) : SysExpression.Constant(null, typeof(object));
                        var start = args.Length > 1 ? BoxToObject(args[1]) : SysExpression.Constant(null, typeof(object));
                        var len = args.Length > 2 ? BoxToObject(args[2]) : SysExpression.Constant(null, typeof(object));
                        var m = typeof(ExpressionResolver).GetMethod(nameof(Ast_Substring), BindingFlags.Public | BindingFlags.Static);
                        return SysExpression.Call(m, val, start, len);
                    }

                    case "round":
                    {
                        var args = functionNode.Arguments.Select(a => CompileAstNode(a, exchangeParam)).ToArray();
                        var val = args.Length > 0 ? BoxToObject(args[0]) : SysExpression.Constant(null, typeof(object));
                        var digits = args.Length > 1 ? BoxToObject(args[1]) : SysExpression.Constant(null, typeof(object));
                        var m = typeof(ExpressionResolver).GetMethod(nameof(Ast_Round), BindingFlags.Public | BindingFlags.Static);
                        return SysExpression.Call(m, val, digits);
                    }

                    case "min":
                        return CompileTwoArgFunction(functionNode, exchangeParam, nameof(Ast_Min));

                    case "max":
                        return CompileTwoArgFunction(functionNode, exchangeParam, nameof(Ast_Max));

                    case "contains":
                        return CompileTwoArgFunction(functionNode, exchangeParam, nameof(Ast_Contains));

                    case "startswith":
                        return CompileTwoArgFunction(functionNode, exchangeParam, nameof(Ast_StartsWith));

                    case "endswith":
                        return CompileTwoArgFunction(functionNode, exchangeParam, nameof(Ast_EndsWith));

                    case "replace":
                        return CompileThreeArgFunction(functionNode, exchangeParam, nameof(Ast_Replace));

                    case "now":
                    {
                        var nowMethod = typeof(ExpressionResolver).GetMethod(nameof(Ast_Now), BindingFlags.Public | BindingFlags.Static);
                        return SysExpression.Call(nowMethod);
                    }

                    case "dateformat":
                        return CompileTwoArgFunction(functionNode, exchangeParam, nameof(Ast_DateFormat));

                    case "dateadd":
                        return CompileThreeArgFunction(functionNode, exchangeParam, nameof(Ast_DateAdd));

                    case "sum":
                        return CompileSingleArgFunction(functionNode, exchangeParam, nameof(Ast_Sum));

                    case "avg":
                        return CompileSingleArgFunction(functionNode, exchangeParam, nameof(Ast_Avg));

                    case "count":
                        return CompileSingleArgFunction(functionNode, exchangeParam, nameof(Ast_Count));

                    case "logical":
                        return CompileSingleArgFunctionWithExchange(functionNode, exchangeParam, nameof(Ast_Logical));

                    case "jpath":
                        return CompileSingleArgFunctionWithExchange(functionNode, exchangeParam, nameof(Ast_JPath));

                    case "xpath":
                        return CompileSingleArgFunctionWithExchange(functionNode, exchangeParam, nameof(Ast_XPath));

                    default:
                        throw new NotImplementedException(
                            $"Compilation of function '{functionNode.Name}' is not yet implemented.");
                }

            case TernaryNode ternaryNode:
                var conditionExpr = CompileAstNode(ternaryNode.Condition, exchangeParam);
                var ifTrueExpr = CompileAstNode(ternaryNode.IfTrue, exchangeParam);
                var ifFalseExpr = CompileAstNode(ternaryNode.IfFalse, exchangeParam);

                // Ensure condition is boxed to object? for Ast_ConvertToBool
                if (conditionExpr.Type != typeof(object))
                    conditionExpr = SysExpression.Convert(conditionExpr, typeof(object));

                var toBoolMethod = typeof(ExpressionResolver).GetMethod(
                    nameof(Ast_ConvertToBool),
                    BindingFlags.Public | BindingFlags.Static);

                var conditionBool = SysExpression.Call(toBoolMethod, conditionExpr);

                // Ensure both branches return object?
                if (ifTrueExpr.Type != typeof(object))
                    ifTrueExpr = SysExpression.Convert(ifTrueExpr, typeof(object));
                if (ifFalseExpr.Type != typeof(object))
                    ifFalseExpr = SysExpression.Convert(ifFalseExpr, typeof(object));

                return SysExpression.Condition(conditionBool, ifTrueExpr, ifFalseExpr);

            default:
                throw new ExpressionCompilationException(
                    $"Unsupported AST node type: {node.GetType().Name}");
        }
    }
    
    /// <summary>
    /// Compiles a postfix operation (x++ or x--).
    /// </summary>
    /// <param name="op">The postfix operator string.</param>
    /// <param name="operand">The AST node representing the operand.</param>
    /// <param name="exchangeParam">The exchange parameter expression.</param>
    /// <returns>The compiled <see cref="SysExpression"/> for the postfix operation.</returns>
    private static SysExpression CompilePostfixOperation(string op, AstNode operand, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling postfix operation: {op} for {operand}");
        
        // Get the property name from the operand
        string propertyName = GetPropertyNameFromNode(operand);
        
        if (string.IsNullOrEmpty(propertyName))
        {
            throw new InvalidOperationException($"Failed to get property name from operand {operand}");
        }
        
        return CompilePostfixIncrementDecrement(op, propertyName, exchangeParam);
    }
    
    /// <summary>
    /// Extracts the property name from an AST node.
    /// </summary>
    /// <param name="node">The AST node to extract the property name from.</param>
    /// <returns>The extracted property name, or an empty string if extraction fails.</returns>
    private static string GetPropertyNameFromNode(AstNode node)
    {
        DebugLog($"Extracting property name from node {node?.GetType().Name ?? "null"}");
        
        switch (node)
        {
            case IdentifierNode identifierNode:
                return identifierNode.Name;
                
            case PropertyAccessNode propertyAccessNode:
                var objName = GetObjectNameFromNode(propertyAccessNode.Object);
                return string.IsNullOrEmpty(objName) 
                    ? propertyAccessNode.PropertyName 
                    : $"{objName}.{propertyAccessNode.PropertyName}";
                
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Gets the object name from an AST node.
    /// </summary>
    /// <param name="node">The AST node to extract the object name from.</param>
    /// <returns>The extracted object name, or an empty string if extraction fails.</returns>
    private static string GetObjectNameFromNode(AstNode node)
    {
        if (node is IdentifierNode idNode)
        {
            return idNode.Name;
        }
        else if (node is PropertyAccessNode propNode)
        {
            var objName = GetObjectNameFromNode(propNode.Object);
            return string.IsNullOrEmpty(objName)
                ? propNode.PropertyName
                : $"{objName}.{propNode.PropertyName}";
        }
        
        return string.Empty;
    }

    /// <summary>
    /// Boxes an expression to <c>typeof(object)</c> if needed.
    /// </summary>
    private static SysExpression BoxToObject(SysExpression expr)
        => expr.Type == typeof(object) ? expr : SysExpression.Convert(expr, typeof(object));

    /// <summary>
    /// Compiles a single-argument function call (e.g. upper, lower, trim, length, abs).
    /// </summary>
    private static SysExpression CompileSingleArgFunction(FunctionCallNode node, ParameterExpression exchangeParam, string methodName)
    {
        var arg = node.Arguments.Count > 0
            ? BoxToObject(CompileAstNode(node.Arguments[0], exchangeParam))
            : SysExpression.Constant(null, typeof(object));
        var method = typeof(ExpressionResolver).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        return SysExpression.Call(method, arg);
    }

    /// <summary>
    /// Compiles a two-argument function call (e.g. min, max, contains, startsWith, endsWith).
    /// </summary>
    private static SysExpression CompileTwoArgFunction(FunctionCallNode node, ParameterExpression exchangeParam, string methodName)
    {
        var args = node.Arguments.Select(a => CompileAstNode(a, exchangeParam)).ToArray();
        var a = args.Length > 0 ? BoxToObject(args[0]) : SysExpression.Constant(null, typeof(object));
        var b = args.Length > 1 ? BoxToObject(args[1]) : SysExpression.Constant(null, typeof(object));
        var method = typeof(ExpressionResolver).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        return SysExpression.Call(method, a, b);
    }

    /// <summary>
    /// Compiles a three-argument function call (e.g. replace, dateAdd).
    /// </summary>
    private static SysExpression CompileThreeArgFunction(FunctionCallNode node, ParameterExpression exchangeParam, string methodName)
    {
        var args = node.Arguments.Select(a => CompileAstNode(a, exchangeParam)).ToArray();
        var a = args.Length > 0 ? BoxToObject(args[0]) : SysExpression.Constant(null, typeof(object));
        var b = args.Length > 1 ? BoxToObject(args[1]) : SysExpression.Constant(null, typeof(object));
        var c = args.Length > 2 ? BoxToObject(args[2]) : SysExpression.Constant(null, typeof(object));
        var method = typeof(ExpressionResolver).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        return SysExpression.Call(method, a, b, c);
    }

    /// <summary>
    /// Compiles a single-argument function that also needs the exchange parameter.
    /// </summary>
    private static SysExpression CompileSingleArgFunctionWithExchange(FunctionCallNode node, ParameterExpression exchangeParam, string methodName)
    {
        var arg = node.Arguments.Count > 0
            ? BoxToObject(CompileAstNode(node.Arguments[0], exchangeParam))
            : SysExpression.Constant(null, typeof(object));
        var method = typeof(ExpressionResolver).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        return SysExpression.Call(method, arg, exchangeParam);
    }

    #endregion

    #region AST helper methods — increment/decrement operations

    /// <summary>
    /// Applies prefix increment (++x) via AST.
    /// </summary>
    /// <param name="value">The current value (unused; operation reads from exchange).</param>
    /// <param name="propertyName">The property name to increment.</param>
    /// <param name="exchange">The exchange containing the property.</param>
    /// <returns>The incremented value.</returns>
    internal static object? Ast_ApplyPrefixIncrement(object? value, string propertyName, IExchange exchange)
        => ApplyIncrementDecrementOperation(exchange, propertyName, OperationType.PrefixIncrement);
    
    /// <summary>
    /// Applies prefix decrement (--x) via AST.
    /// </summary>
    /// <param name="value">The current value (unused; operation reads from exchange).</param>
    /// <param name="propertyName">The property name to decrement.</param>
    /// <param name="exchange">The exchange containing the property.</param>
    /// <returns>The decremented value.</returns>
    internal static object? Ast_ApplyPrefixDecrement(object? value, string propertyName, IExchange exchange)
        => ApplyIncrementDecrementOperation(exchange, propertyName, OperationType.PrefixDecrement);
    
    /// <summary>
    /// Applies postfix increment (x++) via AST.
    /// </summary>
    /// <param name="value">The current value (unused; operation reads from exchange).</param>
    /// <param name="propertyName">The property name to increment.</param>
    /// <param name="exchange">The exchange containing the property.</param>
    /// <returns>The original value before incrementing.</returns>
    internal static object? Ast_ApplyPostfixIncrement(object? value, string propertyName, IExchange exchange)
        => ApplyIncrementDecrementOperation(exchange, propertyName, OperationType.PostfixIncrement);
    
    /// <summary>
    /// Applies postfix decrement (x--) via AST.
    /// </summary>
    /// <param name="value">The current value (unused; operation reads from exchange).</param>
    /// <param name="propertyName">The property name to decrement.</param>
    /// <param name="exchange">The exchange containing the property.</param>
    /// <returns>The original value before decrementing.</returns>
    internal static object? Ast_ApplyPostfixDecrement(object? value, string propertyName, IExchange exchange)
        => ApplyIncrementDecrementOperation(exchange, propertyName, OperationType.PostfixDecrement);

    #endregion
}

