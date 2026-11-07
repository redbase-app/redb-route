using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using redb.Route.Abstractions;
using SysExpression = System.Linq.Expressions.Expression;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver — Expression Tree compilation methods
/// </summary>
public static partial class ExpressionResolver
{
    #region Resolver compilation

    /// <summary>
    /// Compiles a property resolver
    /// </summary>
    private static Func<object?, string, object?> CompilePropertyResolver(string expression)
    {
        DebugLog($"Compiling property resolver: '{expression}'");
        var objParam = SysExpression.Parameter(typeof(object), "obj");
        var pathParam = SysExpression.Parameter(typeof(string), "path");

        // For simplicity, use reflection in compiled code
        var resolveMethod = typeof(ExpressionResolver).GetMethod(nameof(ResolvePropertyPath), BindingFlags.NonPublic | BindingFlags.Static);
        var callExpression = SysExpression.Call(resolveMethod, objParam, pathParam);

        var lambda = SysExpression.Lambda<Func<object?, string, object?>>(callExpression, objParam, pathParam);
        DebugLog($"Property resolver compiled: '{expression}'");
        return lambda.Compile();
    }

    #endregion

    #region Property access compilation

    #endregion

    #region Value expression compilation

    /// <summary>
    /// Compiles an expression for value retrieval. Every expression is the AST's (findings register row 28); text that
    /// is not one is the text itself (row 29), and arithmetic that breaks off is an error.
    /// </summary>
    private static Func<IExchange, object?> CompileValueExpression(string expression)
    {
        DebugLog($"Compiling value expression: '{expression}'");

        {
            // Operators, calls and the other expression shapes: the AST, with nothing to decide about text.
            if (ContainsNamespacedCall(expression)
                || PostfixIncrementDecrementRegex.IsMatch(expression) || PrefixIncrementDecrementRegex.IsMatch(expression)
                || expression.Contains("??") || ContainsTernary(expression)
                || HasFunctionCalls(expression) || HasIndexAccess(expression)
                // Whitespace around an operator carries no meaning here either: an explicit
                // expression ("Expr(...)") is an expression whatever its spacing. Until
                // 2026-08-28 this was the last dialect that required single spaces.
                || ContainsComparisonOrWordLogicOperator(expression)
                // Route-XML Ф1.5: modulo is new, so no legacy path ever learned it — route it to
                // the AST. Deliberately narrow (only '%', literals masked) so no existing form
                // changes its compilation path.
                || ContainsModuloOperator(expression)
                || expression.StartsWith("NOT ", StringComparison.OrdinalIgnoreCase)
                || expression.StartsWith("!", StringComparison.Ordinal)
                || expression.StartsWith("-", StringComparison.Ordinal)
                || expression.StartsWith("+", StringComparison.Ordinal))
            {
                DebugLog($"Detected operators/functions, using AST parser");
                return CompileExpressionWithAst(expression);
            }
            
            // Arithmetic that starts to parse and breaks off is an error, not text (findings register row 29).
            if (BrokenArithmetic(expression) is { } broken)
                throw new BrokenExpressionException($"{broken} within the expression \"{expression}\".");

            // Text that is not an expression is the text itself (row 29), as the older compiler gave it.
            if (IsBareText(expression))
                return _ => expression;

            // Every other value expression is the AST's (findings register row 28, step 28b).
            return CompileExpressionWithAst(expression);
        }
    }

    #endregion

    #region Operation compilation

    /// <summary>
    /// Compiles postfix increment/decrement (x++ or x--)
    /// </summary>
    private static SysExpression CompilePostfixIncrementDecrement(string op, string innerExpression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling postfix {op} for '{innerExpression}'");
        
        // Do not strip property, header, or body prefixes since they are now handled
        // in the Apply* methods
        string actualPropertyName = innerExpression;
        DebugLog($"Using full property name: '{actualPropertyName}'");
        
        // Method for handling postfix increment/decrement
        var method = op == "++" 
            ? typeof(ExpressionResolver).GetMethod(nameof(ApplyPostfixIncrement), BindingFlags.NonPublic | BindingFlags.Static)
            : typeof(ExpressionResolver).GetMethod(nameof(ApplyPostfixDecrement), BindingFlags.NonPublic | BindingFlags.Static);
        
        return SysExpression.Call(method, exchangeParam, SysExpression.Constant(actualPropertyName));
    }


    #endregion

    #region Operator search helper methods



    /// <summary>
    /// Finds the index of the first comparison operator in the string
    /// </summary>
    private static int FindFirstOperator(string expression)
    {
        string[] operators = { "==", "!=", ">=", "<=", ">", "<", "&&", "||", " AND ", " OR ", " XOR " };

        int minIndex = int.MaxValue;
        foreach (var op in operators)
        {
            int index = expression.IndexOf(op);
            if (index >= 0 && index < minIndex)
            {
                minIndex = index;
            }
        }

        return minIndex == int.MaxValue ? -1 : minIndex;
    }


    /// <summary>
    /// Checks for increment/decrement operations
    /// </summary>
    private static bool HasIncrementDecrementOps(string expression)
        => PostfixIncrementDecrementRegex.IsMatch(expression) || 
           PrefixIncrementDecrementRegex.IsMatch(expression);

    /// <summary>
    /// Checks for standalone function calls (e.g. upper(...), concat(...)), 
    /// but NOT method calls on properties (e.g. property.text.toLower()).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex FunctionCallPattern = 
        new(@"(?<![.\w])[a-zA-Z_]\w*\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static bool HasFunctionCalls(string expression)
        => FunctionCallPattern.IsMatch(expression);

    /// <summary>
    /// Checks for index access
    /// </summary>
    private static bool HasIndexAccess(string expression)
        => expression.Contains("[") && expression.Contains("]");

    /// <summary>
    /// Comparison and word-logic operators, looked for outside quoted literals.
    /// Whitespace between tokens carries no meaning, so detection must not depend on it:
    /// <c>a&gt;1</c>, <c>a &gt; 1</c> and a tab- or newline-separated form are one expression.
    /// Word operators are matched the way the tokenizer matches them, case-insensitively.
    /// Arithmetic is deliberately absent: the value path already handles + - * / without
    /// requiring whitespace, and a bare literal must not be mistaken for a subtraction.
    /// </summary>
    // "in" / "not in" need whitespace before them and whitespace or "(" after: an operator stands
    // between operands, while "header.in" is a header called "in" and must keep its compilation path.
    // "range" / "not range" the same way, with whitespace on both sides: its right side is a quoted literal.
    private static readonly Regex ComparisonOrWordLogicRegex =
        new(@"==|!=|=~|>=|<=|>|<|\b(AND|OR|XOR)\b|(?<=\s)IN(?=[\s(])|(?<=\s)RANGE(?=\s)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reports whether the expression contains a comparison or word-logic operator that is not
    /// part of a quoted literal. Used at the condition boundary to decide whether a string is a
    /// boolean expression, which has to be parsed, or a value to be read for truthiness.
    /// </summary>
    /// <param name="expression">The expression to inspect.</param>
    /// <returns><c>true</c> when an operator is present outside every quoted literal.</returns>
    internal static bool ContainsComparisonOrWordLogicOperator(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return ComparisonOrWordLogicRegex.IsMatch(MaskQuotedLiterals(expression));
    }

    // A namespace written against its target, outside quotes: what the tokenizer reads as a Namespace token.
    // Built from the tokenizer's set, so the two cannot drift when a namespace is added.
    private static readonly Regex NamespacedCallRegex = new(
        @"(?<![\w.])(?:" + string.Join("|", Ast.Tokenizer.FunctionNamespaces.Order(StringComparer.Ordinal).Select(Regex.Escape)) + @"):[A-Za-z_]",
        RegexOptions.Compiled);

    /// <summary>
    /// Reports whether the expression holds a namespaced call (<c>bean:svc.Check(...)</c>) outside every quoted
    /// literal. Such an expression belongs to the AST, deterministically: no other path knows the namespace.
    /// </summary>
    internal static bool ContainsNamespacedCall(string expression)
        => expression.Contains(':', StringComparison.Ordinal) && NamespacedCallRegex.IsMatch(MaskQuotedLiterals(expression));

    /// <summary>
    /// What the package gate says about one expression, computed as the engine compiles it (findings register row 32):
    /// an error with the engine's words when it does not compile (text after a complete expression included), or a
    /// warning when a bare expression starts as a value (<c>header.b 3</c>) but the bare-text rule (row 29) makes it the
    /// text itself, which in a condition is always true. Both <c>null</c> when the expression is fine.
    /// </summary>
    /// <param name="expression">A bare expression, or the inner text of one <c>${...}</c>.</param>
    /// <param name="placeholder"><c>true</c> for the inner text of a <c>${...}</c>.</param>
    /// <param name="condition"><c>true</c> for a bare expression in a condition attribute.</param>
    internal static (string? Error, string? Warning) GateCheck(string expression, bool placeholder, bool condition)
    {
        // {{key}} is configuration, resolved before the engine sees the text where it is resolved at all.
        if (string.IsNullOrWhiteSpace(expression) || expression.Contains("{{", StringComparison.Ordinal))
            return (null, null);
        try
        {
            if (placeholder)
                CompilePlaceholder(expression);
            else if (condition)
                Predicates.PredicateFactory.FromString(expression);
            else
                GetCompiledValueExpression(expression);
        }
        catch (ExpressionCompilationException ex)
        {
            return (ex.Message, null);
        }
        return placeholder ? (null, null) : (null, BareTextAfterValue(expression));
    }

    /// <summary>The gate's warning for a bare expression that starts with a root of the exchange and becomes text.</summary>
    private static string? BareTextAfterValue(string expression)
    {
        if (!IsBareText(expression))
            return null;
        try
        {
            var tokens = new Ast.Tokenizer(expression).GetAllTokens();
            if (tokens is not [{ Type: Ast.TokenType.Identifier } first, ..] || !IsValueWord(first.Value)
                || first.Value is "true" or "false" or "null")
                return null;
            new Ast.Parser(tokens).ParseReportingRest(out var rest);
            return rest is null
                ? null
                : $"\"{expression}\" reads as the text itself, not as an expression: '{rest.Value}' at position " +
                  $"{rest.Position + 1} does not continue it; in a condition the text is always true.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    // A scheme at the start (direct:x, kafka://t) or "://" anywhere: the text names endpoints.
    private static readonly Regex EndpointLikeRegex = new(@"^\s*[A-Za-z][A-Za-z0-9+.\-]*:|://", RegexOptions.Compiled);

    /// <summary>A bare text that starts as arithmetic and breaks off: a positioned error, never rendered as text.</summary>
    private sealed class BrokenExpressionException(string message) : ExpressionCompilationException(message);

    /// <summary>
    /// The value path's rule for bare text (findings register row 29). A text with no arithmetic operator outside quotes
    /// is what it was: a lookup, a literal, or the text itself (<c>hello</c>, <c>plain text</c>, <c>key=value</c>). A text
    /// with one is arithmetic, and it must be whole: it parses to its end, and each bare word in it is a value (a root
    /// such as <c>header.a</c>, <c>property.x</c>, <c>body</c>, <c>exception.Message</c>, a number or a quoted text).
    /// Otherwise the old compiler computed nonsense from the pieces (<c>1 +</c> gave <c>1</c>, <c>header.a+b</c> gave
    /// <c>2b</c>); now the problem is named with its position. <c>null</c> when the text is fine.
    /// </summary>
    internal static string? BrokenArithmetic(string expression)
    {
        if (!HasBinaryOperatorsInExpression(expression) || HasIncrementDecrementOps(expression))
            return null;
        // An endpoint URI, or a list of them, is text where an expression names a destination
        // (<recipientList expr="direct://a,direct://b"/>): its slashes are not division.
        if (EndpointLikeRegex.IsMatch(expression))
            return null;
        List<Ast.Token> tokens;
        try
        {
            tokens = new Ast.Tokenizer(expression).GetAllTokens();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        if (!tokens.Any(t => t.Type == Ast.TokenType.Operator && t.Value is "+" or "-" or "*" or "/"))
            return null; // the detector saw an operator the language does not read as arithmetic
        try
        {
            new Ast.Parser(tokens).ParseReportingRest(out var rest);
            if (rest is not null)
                return $"unexpected '{rest.Value}' at position {rest.Position + 1}";
        }
        catch (InvalidOperationException ex)
        {
            return tokens.LastOrDefault(t => t.Type != Ast.TokenType.Eof) is { } last && ex.Message.Contains("Eof", StringComparison.Ordinal)
                ? $"the expression ends after '{last.Value}' at position {last.Position + 1}, where a value is expected"
                : ex.Message;
        }
        // A name with a dash read as a subtraction: header.Content-Type is header.Content minus Type.
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            var (name, minus, next) = (tokens[i], tokens[i + 1], tokens[i + 2]);
            if (name.Type == Ast.TokenType.Identifier && minus is { Type: Ast.TokenType.Operator, Value: "-" }
                && next.Type == Ast.TokenType.Identifier && IsValueWord(name.Value) && name.Value.Contains('.')
                && name.Position + name.Value.Length == minus.Position && minus.Position + 1 == next.Position)
            {
                var end = i + 2;
                while (end + 2 < tokens.Count && tokens[end + 1] is { Type: Ast.TokenType.Operator, Value: "-" }
                       && tokens[end + 2].Type == Ast.TokenType.Identifier
                       && tokens[end].Position + tokens[end].Value.Length == tokens[end + 1].Position)
                    end += 2;
                var whole = expression.Substring(name.Position, tokens[end].Position + tokens[end].Value.Length - name.Position);
                return $"'{whole}' at position {name.Position + 1} reads as a subtraction: a name with a dash is written ${{{whole}}}";
            }
        }

        foreach (var token in tokens.Where(t => t.Type == Ast.TokenType.Identifier))
            if (!IsValueWord(token.Value))
                return $"'{token.Value}' at position {token.Position + 1} is not a value: write header.{token.Value}, " +
                       $"property.{token.Value} or the text '{token.Value}' in quotes";
        return null;
    }

    /// <summary>
    /// Text in a value position that is not an expression, kept as the text itself (findings register row 29, as in
    /// Apache Camel): it does not tokenize or parse (<c>((((</c>, <c>'unterminated</c>, <c>${}</c>), it parses only
    /// in part (<c>plain text</c>, <c>key=value</c>), or it is one bare word that names no root (<c>hello</c>). Called
    /// once arithmetic and the operators that route to the AST are ruled out.
    /// </summary>
    internal static bool IsBareText(string expression)
    {
        List<Ast.Token> tokens;
        try
        {
            tokens = new Ast.Tokenizer(expression).GetAllTokens();
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        Ast.AstNode root;
        try
        {
            root = new Ast.Parser(tokens).ParseReportingRest(out var rest);
            if (rest is not null)
                return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        return root is Ast.IdentifierNode identifier && !IsValueWord(identifier.Name);
    }

    /// <summary>
    /// The rule of a key that may be a constant (the cache key, after findings register row 29): text without
    /// <c>${...}</c> that does not start with a root of the exchange (<c>header.x</c>, <c>property.x</c>, <c>body</c>,
    /// <c>exception.x</c>), a namespace (<c>bean:</c>, <c>ref:</c>, <c>env:</c>), a function call, a quoted literal, a
    /// parenthesis or a sign is the key as written: <c>orders-all</c>, <c>k-out</c>, <c>b:c</c>. Anything else is an
    /// expression and compiles as one, so <c>header.customerId</c> is evaluated and <c>header.a+b</c> stays the row 29
    /// error. Not a relaxation of row 29 for expressions in general: only a key that names itself a template calls it.
    /// </summary>
    internal static bool IsConstantKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || TemplateScanner.HasPlaceholder(text))
            return false;
        List<Ast.Token> tokens;
        try
        {
            tokens = new Ast.Tokenizer(text).GetAllTokens();
        }
        catch (InvalidOperationException)
        {
            return true; // not even tokens: no expression starts here
        }
        return tokens[0] switch
        {
            { Type: Ast.TokenType.Identifier } first => !IsRoot(first.Value),
            { Type: Ast.TokenType.Function or Ast.TokenType.Namespace or Ast.TokenType.String or Ast.TokenType.LeftParen } => false,
            { Type: Ast.TokenType.Operator, Value: "!" or "-" or "+" } => false,
            _ => true,
        };

        // A dotted root (header.x, exception.Message) or a root word the language reads alone.
        static bool IsRoot(string word)
            => IsValueWord(word) && (word.Contains('.') || word is "body" or "contentType" or "exchange" or "attachments"
                                     || word.Equals("true", StringComparison.OrdinalIgnoreCase)
                                     || word.Equals("false", StringComparison.OrdinalIgnoreCase)
                                     || word.Equals("null", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A bare word arithmetic can read: a root of the exchange, or a keyword literal.</summary>
    private static bool IsValueWord(string word)
    {
        var root = word.Contains('.') ? word[..word.IndexOf('.')] : word;
        return root is "header" or "headers" or "property" or "properties" or "body" or "exception" or "in" or "out"
                   or "contentType" or "attachments" or "exchange"
               || word.Equals("null", StringComparison.OrdinalIgnoreCase)
               || word.Equals("true", StringComparison.OrdinalIgnoreCase)
               || word.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reports whether the expression holds the namespace <paramref name="ns"/> (<c>bean</c>, <c>ref</c>) written
    /// against its target outside every quoted literal: one namespace of <see cref="ContainsNamespacedCall"/>.
    /// </summary>
    internal static bool ContainsNamespace(string expression, string ns)
        => expression.Contains(ns + ":", StringComparison.Ordinal)
           && Regex.IsMatch(MaskQuotedLiterals(expression), @"(?<![\w.])" + Regex.Escape(ns) + ":[A-Za-z_]");

    /// <summary>
    /// Reports whether the expression contains the modulo operator outside every quoted literal
    /// (Route-XML Ф1.5). Narrow on purpose: the value dialect routes '%' to the AST through this,
    /// and nothing else, so existing forms keep their compilation path.
    /// </summary>
    private static bool ContainsModuloOperator(string expression)
        => MaskQuotedLiterals(expression).Contains('%');

    /// <summary>
    /// Replaces every quoted literal, quotes included, with a run of filler characters of the same
    /// length, so an operator inside a literal cannot be seen by a scan over the result. Literals
    /// are delimited the same way the tokenizer delimits them: a matching quote character, with a
    /// backslash escaping the next character, and an unterminated literal running to the end.
    /// </summary>
    private static string MaskQuotedLiterals(string expression)
    {
        if (expression.IndexOf('\'') < 0 && expression.IndexOf('"') < 0)
            return expression;

        const char filler = '#';
        var masked = new StringBuilder(expression.Length);
        var position = 0;

        while (position < expression.Length)
        {
            var current = expression[position];
            if (current is not ('\'' or '"'))
            {
                masked.Append(current);
                position++;
                continue;
            }

            var quote = current;
            masked.Append(filler);
            position++;

            while (position < expression.Length && expression[position] != quote)
            {
                if (expression[position] == '\\' && position + 1 < expression.Length)
                {
                    masked.Append(filler);
                    position++;
                }

                masked.Append(filler);
                position++;
            }

            if (position < expression.Length)
            {
                masked.Append(filler);
                position++;
            }
        }

        return masked.ToString();
    }

    /// <summary>
    /// Checks for binary operators outside quoted strings
    /// </summary>
    private static bool HasBinaryOperatorsInExpression(string expression)
    {
        bool inQuotes = false;
        char quoteChar = '\0';
        int bracketDepth = 0;
        
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            
            // Handle quotes
            if ((c == '\'' || c == '"') && (i == 0 || expression[i-1] != '\\'))
            {
                if (!inQuotes)
                {
                    inQuotes = true;
                    quoteChar = c;
                    continue;
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                    continue;
                }
            }
            // If not inside quotes, check for operators and parentheses
            else if (!inQuotes)
            {
                // Account for parentheses for nesting level tracking
                if (c == '(')
                {
                    bracketDepth++;
                    return true; // Presence of parentheses already indicates expression complexity
                }
                else if (c == ')')
                {
                    bracketDepth--;
                    return true; // Presence of parentheses already indicates expression complexity
                }
                // Check for null-coalescing operator
                else if (c == '?' && i + 1 < expression.Length && expression[i + 1] == '?')
                {
                    return true;
                }
                // Check for ternary operator (single ? not followed by ?)
                else if (c == '?' && (i + 1 >= expression.Length || expression[i + 1] != '?'))
                {
                    return true;
                }
                // Check for binary operators
                else if (c == '+' || c == '-' || c == '*' || c == '/' || c == '%')
                {
                    // For unary operations (e.g. +1 or -2), verify this is not a unary operator
                    if (c == '+' || c == '-')
                    {
                        // If this is the first character or follows another operator, it is unary
                        if (i == 0 ||
                            expression[i-1] == '+' ||
                            expression[i-1] == '-' ||
                            expression[i-1] == '*' ||
                            expression[i-1] == '/' ||
                            expression[i-1] == '%' ||
                            expression[i-1] == '(' ||
                            expression[i-1] == '[' ||
                            expression[i-1] == ',')
                        {
                            // This is a unary operator, continue
                            continue;
                        }

                        // Check increment/decrement operations (++ and --)
                        if (i < expression.Length - 1 && (c == '+' || c == '-') && expression[i+1] == c)
                        {
                            DebugLog($"HasOperations: Detected increment/decrement operation in '{expression}'");
                            return true; // This is an increment or decrement operation
                        }
                    }
                    
                    return true; // This is a binary operator
                }
            }
        }
        
        return false;
    }

    /// <summary>
    /// Checks whether the expression contains a ternary operator (single <c>?</c> not part of <c>??</c>).
    /// </summary>
    private static bool ContainsTernary(string expression)
    {
        for (int i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '?')
            {
                // Skip ?? (null-coalescing)
                if (i + 1 < expression.Length && expression[i + 1] == '?')
                {
                    i++; // skip second ?
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    #endregion
}

