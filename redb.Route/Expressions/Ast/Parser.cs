using System;
using System.Globalization;
using System.Collections.Generic;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// Recursive descent parser that builds an AST (Abstract Syntax Tree) from a list of tokens.
/// </summary>
public class Parser
{
    /// <summary>
    /// The list of tokens to parse.
    /// </summary>
    private readonly List<Token> _tokens;

    /// <summary>
    /// The current position in the token list.
    /// </summary>
    private int _position;

    /// <summary>
    /// The token at the current position.
    /// </summary>
    private Token _currentToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="Parser"/> class.
    /// </summary>
    /// <param name="tokens">The list of tokens produced by the tokenizer.</param>
    public Parser(List<Token> tokens)
    {
        _tokens = tokens;
        _position = 0;
        _currentToken = tokens.Count > 0 ? tokens[0] : new Token(TokenType.Eof, string.Empty, 0);
    }

    /// <summary>
    /// Parses the token list and builds an AST. The expression must take the whole text: a token after a complete
    /// expression (<c>header.b == 3 garbage</c>) is an error naming it and its position (findings register row 32).
    /// </summary>
    /// <returns>The root <see cref="AstNode"/> of the parsed expression.</returns>
    /// <exception cref="InvalidOperationException">The text does not parse, or text follows the complete expression.</exception>
    public AstNode Parse()
    {
        var root = ParseReportingRest(out var rest);
        if (rest is not null)
            throw new InvalidOperationException(
                $"unexpected '{rest.Value}' at position {rest.Position + 1} within the expression: the expression ends before it.");
        return root;
    }

    /// <summary>
    /// Parses the complete expression at the start of the text and reports the first token after it, <c>null</c> when
    /// the expression ends where the text ends. The rule for bare text (row 29) and the package gate read the rest;
    /// <see cref="Parse"/> refuses it.
    /// </summary>
    internal AstNode ParseReportingRest(out Token? rest)
    {
        var root = Expression();
        rest = _currentToken.Type == TokenType.Eof ? null : _currentToken;
        return root;
    }

    /// <summary>
    /// Advances the parser to the next token.
    /// </summary>
    private void Advance()
    {
        _position++;
        if (_position < _tokens.Count)
        {
            _currentToken = _tokens[_position];
        }
        else
        {
            _currentToken = new Token(TokenType.Eof, string.Empty, -1);
        }
    }

    /// <summary>
    /// Consumes the current token if it matches the expected type; otherwise throws an exception.
    /// </summary>
    /// <param name="tokenType">The expected token type.</param>
    /// <returns>The consumed token.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the current token does not match the expected type.</exception>
    private Token Eat(TokenType tokenType)
    {
        if (_currentToken.Type == tokenType)
        {
            var token = _currentToken;
            Advance();
            return token;
        }
        
        throw new InvalidOperationException($"Expected token of type {tokenType}, but found {_currentToken.Type} ({_currentToken.Value})");
    }

    /// <summary>
    /// Parses a full expression (entry point for the recursive descent).
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    private AstNode Expression()
    {
        return TernaryExpression();
    }

    /// <summary>
    /// Parses a ternary conditional expression (<c>condition ? ifTrue : ifFalse</c>) with the lowest precedence.
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    private AstNode TernaryExpression()
    {
        var node = NullCoalescingExpression();

        if (_currentToken.Type == TokenType.Operator && _currentToken.Value == "?")
        {
            Advance(); // consume '?'
            var ifTrue = Expression();
            Eat(TokenType.Colon);
            var ifFalse = Expression();
            return new TernaryNode(node, ifTrue, ifFalse);
        }

        return node;
    }

    /// <summary>
    /// Parses a null-coalescing expression (??) with the lowest precedence.
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    private AstNode NullCoalescingExpression()
    {
        var node = LogicalExpression();

        while (_currentToken.Type == TokenType.Operator && _currentToken.Value == "??")
        {
            var op = _currentToken.Value;
            Advance();
            node = new BinaryOperationNode(node, op, LogicalExpression());
        }

        return node;
    }

    /// <summary>
    /// Parses a logical expression (AND, OR, XOR).
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    private AstNode LogicalExpression()
    {
        var node = ComparisonExpression();

        while (_currentToken.Type == TokenType.Operator && 
              (_currentToken.Value == "AND" || _currentToken.Value == "OR" || _currentToken.Value == "XOR"))
        {
            var op = _currentToken.Value;
            Advance();
            node = new BinaryOperationNode(node, op, ComparisonExpression());
        }

        return node;
    }

    /// <summary>
    /// Parses a comparison expression (==, !=, &gt;, &lt;, &gt;=, &lt;=).
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    private AstNode ComparisonExpression()
    {
        var node = AdditiveExpression();

        while (true)
        {
            if (TryRangeOperator(out var negated))
            {
                node = new RangeNode(node, RangeLiteral(out var from, out var to), from, to, negated);
                continue;
            }

            if (!(_currentToken.Type == TokenType.Operator &&
                  (_currentToken.Value == "==" || _currentToken.Value == "!=" || _currentToken.Value == "=~" ||
                   _currentToken.Value == ">" || _currentToken.Value == "<" ||
                   _currentToken.Value == ">=" || _currentToken.Value == "<=" ||
                   _currentToken.Value == "IN" || _currentToken.Value == "NOT IN")))
                break;

            var op = _currentToken.Value;
            Advance();
            node = op is "IN" or "NOT IN"
                ? new InOperationNode(node, MembershipTarget(), negated: op == "NOT IN")
                : new BinaryOperationNode(node, op, AdditiveExpression());
        }

        return node;
    }

    /// <summary>
    /// <c>range</c> and <c>not range</c> right after a complete operand, as in Apache Camel's Simple. The words are
    /// operators only here: anywhere else <c>range</c> is an ordinary name (<c>header.range</c>, a variable
    /// <c>range</c>), because after an operand neither a name nor a unary <c>not</c> could follow.
    /// </summary>
    private bool TryRangeOperator(out bool negated)
    {
        negated = false;
        if (IsWord(_currentToken, "range"))
        {
            Advance();
            return true;
        }
        if (_currentToken.Type == TokenType.Operator && _currentToken.Value == "NOT"
            && _position + 1 < _tokens.Count && IsWord(_tokens[_position + 1], "range"))
        {
            Advance();
            Advance();
            negated = true;
            return true;
        }
        return false;
    }

    private static bool IsWord(Token token, string word)
        => token.Type == TokenType.Identifier && token.Value.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The right side of <c>range</c>: a quoted <c>'from..to'</c> literal of two numbers, inclusive, read when the
    /// expression is compiled. Returns the literal as written, for the node's text.
    /// </summary>
    private string RangeLiteral(out decimal from, out decimal to)
    {
        var token = _currentToken;
        if (token.Type != TokenType.String)
            throw new InvalidOperationException(
                $"range takes a quoted 'from..to' literal, as in header.n range '1..10' " +
                $"(position {(token.Position < 0 ? "end" : (token.Position + 1).ToString(CultureInfo.InvariantCulture))} within the expression).");
        Advance();
        var text = token.Value[1..^1];
        var dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0
            || !decimal.TryParse(text[..dots].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out from)
            || !decimal.TryParse(text[(dots + 2)..].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out to))
            throw new InvalidOperationException(
                $"range '{text}' is not 'from..to' with two numbers, as in '1..10' " +
                $"(position {token.Position + 1} within the expression).");
        if (from > to)
            throw new InvalidOperationException(
                $"range '{text}' starts above its end: write the smaller number first " +
                $"(position {token.Position + 1} within the expression).");
        return text;
    }

    /// <summary>
    /// The right-hand side of <c>in</c>: a list literal when a parenthesis follows — <c>('a','b')</c>,
    /// <c>()</c> for none — and otherwise an ordinary operand that must evaluate to a collection
    /// (<c>header.codes</c>). Parentheses after <c>in</c> always mean a literal, so a collection is
    /// written without them.
    /// </summary>
    private AstNode MembershipTarget()
    {
        if (_currentToken.Type != TokenType.LeftParen)
            return AdditiveExpression();

        Advance();
        var items = new List<AstNode>();
        if (_currentToken.Type != TokenType.RightParen)
        {
            items.Add(Expression());
            while (_currentToken.Type == TokenType.Comma)
            {
                Advance();
                items.Add(Expression());
            }
        }
        Eat(TokenType.RightParen);
        return new ListLiteralNode(items);
    }

    /// <summary>
    /// Parses an additive expression (+ and -).
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    private AstNode AdditiveExpression()
    {
        var node = MultiplicativeExpression();

        while (_currentToken.Type == TokenType.Operator && 
              (_currentToken.Value == "+" || _currentToken.Value == "-"))
        {
            var op = _currentToken.Value;
            Advance();
            node = new BinaryOperationNode(node, op, MultiplicativeExpression());
        }

        return node;
    }

    /// <summary>
    /// Parses a multiplicative expression (* and /).
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    private AstNode MultiplicativeExpression()
    {
        var node = UnaryExpression();

        while (_currentToken.Type == TokenType.Operator &&
              (_currentToken.Value == "*" || _currentToken.Value == "/" || _currentToken.Value == "%"))
        {
            var op = _currentToken.Value;
            Advance();
            node = new BinaryOperationNode(node, op, UnaryExpression());
        }

        return node;
    }

    /// <summary>
    /// Parses a unary expression (prefix +, -, NOT, !, ++, --) and checks for postfix ++ / --.
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    private AstNode UnaryExpression()
    {
        if (_currentToken.Type == TokenType.Operator && 
           (_currentToken.Value == "+" || _currentToken.Value == "-" || 
            _currentToken.Value == "NOT" || _currentToken.Value == "!" ||
            _currentToken.Value == "++" || _currentToken.Value == "--"))
        {
            var op = _currentToken.Value;
            Advance();
            return new UnaryOperationNode(op, UnaryExpression());
        }

        var expr = PrimaryExpression();
        
        // Check for postfix ++ and -- operations
        if (_currentToken.Type == TokenType.Operator && 
           (_currentToken.Value == "++" || _currentToken.Value == "--"))
        {
            var op = _currentToken.Value;
            Advance();
            return new PostfixOperationNode(expr, op);
        }
        
        return expr;
    }

    /// <summary>
    /// Parses a primary expression: numbers, strings, identifiers, booleans, null, function calls, or parenthesized expressions.
    /// </summary>
    /// <returns>The parsed AST node.</returns>
    /// <exception cref="InvalidOperationException">Thrown when an unexpected token is encountered.</exception>
    private AstNode PrimaryExpression()
    {
        var token = _currentToken;

        switch (token.Type)
        {
            case TokenType.Number:
                Advance();
                if (token.Value.Contains('.'))
                {
                    // Source text is culture-invariant: "2.5" is two and a half on every machine.
                    // Until 2026-08-29 this parsed with the ambient culture, so a route with a
                    // decimal literal compiled on en-US and failed to compile on ru-RU.
                    return new LiteralNode(double.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture));
                }
                return new LiteralNode(int.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture));

            case TokenType.String:
                Advance();
                // Remove quotes and handle escape sequences
                var str = token.Value.Substring(1, token.Value.Length - 2)
                    .Replace("\\\"", "\"")
                    .Replace("\\'", "'")
                    .Replace("\\\\", "\\");
                return new LiteralNode(str);

            case TokenType.Identifier:
                Advance();
                if (token.Value.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    return new LiteralNode(null);
                }
                if (token.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    return new LiteralNode(true);
                }
                if (token.Value.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    return new LiteralNode(false);
                }
                
                // Handle property access via dot notation or index access
                AstNode result = new IdentifierNode(token.Value);
                result = PostfixAccess(result);
                return result;

            case TokenType.Function:
                var funcResult = FunctionCall();
                return PostfixAccess(funcResult);

            case TokenType.Namespace when token.Value == "ref":
                return RegistryReference();

            case TokenType.Namespace when token.Value == "env":
            {
                Advance();
                var name = Eat(TokenType.Identifier).Value;
                if (_currentToken.Type == TokenType.LeftParen)
                    throw new InvalidOperationException(
                        $"env:{name} names an environment variable and calls nothing (position {token.Position + 1} within the expression).");
                return new EnvNode(name, token.Position);
            }

            case TokenType.Namespace:
                return PostfixAccess(NamespacedCall());

            case TokenType.PlaceholderOpen:
                if (_namespacedArgumentDepth == 0)
                    throw new InvalidOperationException(
                        $"${{...}} inside an expression is supported only in the arguments of a bean: call " +
                        $"(position {token.Position + 1} within the expression).");
                Advance();
                var inner = Expression();
                Eat(TokenType.RightBrace);
                return inner;

            case TokenType.LeftParen:
                Advance();
                var expr = Expression();
                Eat(TokenType.RightParen);
                return expr;

            default:
                throw new InvalidOperationException($"Unexpected token: {token.Type} ({token.Value})");
        }
    }

    /// <summary>
    /// Parses chained postfix access operations: property access (<c>.prop</c>) and index access (<c>[n]</c>).
    /// </summary>
    /// <param name="node">The base AST node to apply postfix operations to.</param>
    /// <returns>The resulting AST node with all chained accesses applied.</returns>
    private AstNode PostfixAccess(AstNode node)
    {
        while (true)
        {
            if (_currentToken.Type == TokenType.Dot)
            {
                Eat(TokenType.Dot);
                if (_currentToken.Type == TokenType.Function)
                {
                    // A method of the value before the dot: upper(header.s).length()
                    var methodToken = Eat(TokenType.Function);
                    Eat(TokenType.LeftParen);
                    var args = Arguments();
                    node = new MethodCallNode(node, methodToken.Value, args,
                        $"{node}.{methodToken.Value}" + (args.Count == 0 ? "()" : "(...)"), methodToken.Position);
                    continue;
                }
                var propertyName = Eat(TokenType.Identifier).Value;
                node = new PropertyAccessNode(node, propertyName);
            }
            else if (_currentToken.Type == TokenType.LeftBracket)
            {
                Eat(TokenType.LeftBracket);
                var indexExpr = Expression();
                Eat(TokenType.RightBracket);
                node = new IndexAccessNode(node, indexExpr);
            }
            else
            {
                break;
            }
        }

        return node;
    }

    /// <summary>
    /// How many namespaced-call argument lists enclose the current position: a <c>${...}</c> is a primary there, the
    /// typed value of the expression inside it, and nowhere else.
    /// </summary>
    private int _namespacedArgumentDepth;

    /// <summary>
    /// Parses <c>ref:name</c>: the registry object itself. Nothing is called on it and nothing is read from it; a
    /// parenthesis after the name points to <c>bean:</c>.
    /// </summary>
    private AstNode RegistryReference()
    {
        var ns = Eat(TokenType.Namespace);
        var name = Eat(TokenType.Identifier).Value;
        if (_currentToken.Type == TokenType.LeftParen)
            throw new InvalidOperationException(
                $"ref:{name} names a registry object and calls nothing; call a method with bean:{name}.Method(...) " +
                $"(position {ns.Position + 1} within the expression).");
        return new RegistryRefNode(name, ns.Position);
    }

    /// <summary>
    /// Parses <c>bean:name.Method(arg, ...)</c> or <c>bean:name.Method</c> (no arguments). The method is required:
    /// in an expression there is no exchange-shaped call to pick one from.
    /// </summary>
    private AstNode NamespacedCall()
    {
        var ns = Eat(TokenType.Namespace);
        var target = _currentToken;
        var hasTarget = target.Type is TokenType.Function or TokenType.Identifier;
        var dot = hasTarget ? target.Value.LastIndexOf('.') : -1;
        if (dot <= 0 || dot == target.Value.Length - 1)
            throw new InvalidOperationException(
                $"a {ns.Value}: call names its method: {ns.Value}:{(hasTarget ? target.Value.TrimEnd('.') : "svc")}.Method(...) " +
                $"(position {ns.Position + 1} within the expression).");

        var reference = target.Value[..dot];
        var method = target.Value[(dot + 1)..];
        var arguments = new List<AstNode>();
        if (target.Type == TokenType.Identifier)
        {
            Advance();
            return new BeanCallNode(ns.Value, reference, method, arguments, ns.Position);
        }

        Eat(TokenType.Function);
        Eat(TokenType.LeftParen);
        _namespacedArgumentDepth++;
        try
        {
            if (_currentToken.Type != TokenType.RightParen)
            {
                arguments.Add(Expression());
                while (_currentToken.Type == TokenType.Comma)
                {
                    Advance();
                    arguments.Add(Expression());
                }
            }
        }
        finally
        {
            _namespacedArgumentDepth--;
        }
        Eat(TokenType.RightParen);
        return new BeanCallNode(ns.Value, reference, method, arguments, ns.Position);
    }

    /// <summary>
    /// Parses a function call expression, including its arguments.
    /// </summary>
    /// <returns>The parsed <see cref="FunctionCallNode"/>.</returns>
    private AstNode FunctionCall()
    {
        var functionToken = Eat(TokenType.Function);
        var functionName = functionToken.Value;
        Eat(TokenType.LeftParen);

        var arguments = new List<AstNode>();

        // A dotted name is a method of a value, header.o.Boom(...): the path before the last dot is the value.
        var lastDot = functionName.LastIndexOf('.');
        if (lastDot > 0 && lastDot < functionName.Length - 1)
        {
            var args = Arguments();
            var method = functionName[(lastDot + 1)..];
            return new MethodCallNode(new IdentifierNode(functionName[..lastDot]), method, args,
                functionName + (args.Count == 0 ? "()" : "(...)"), functionToken.Position);
        }

        // Parse arguments
        if (_currentToken.Type != TokenType.RightParen)
        {
            arguments.Add(Expression());
            
            while (_currentToken.Type == TokenType.Comma)
            {
                Advance(); // Skip the comma
                arguments.Add(Expression());
            }
        }
        
        Eat(TokenType.RightParen);

        return new FunctionCallNode(functionName, arguments);
    }

    /// <summary>The arguments after an opening parenthesis already eaten, and the closing one.</summary>
    private List<AstNode> Arguments()
    {
        var arguments = new List<AstNode>();
        if (_currentToken.Type != TokenType.RightParen)
        {
            arguments.Add(Expression());
            while (_currentToken.Type == TokenType.Comma)
            {
                Advance();
                arguments.Add(Expression());
            }
        }
        Eat(TokenType.RightParen);
        return arguments;
    }
}
