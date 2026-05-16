using System;
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
    /// Parses the token list and builds an AST.
    /// </summary>
    /// <returns>The root <see cref="AstNode"/> of the parsed expression.</returns>
    public AstNode Parse()
    {
        return Expression();
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

        while (_currentToken.Type == TokenType.Operator && 
              (_currentToken.Value == "==" || _currentToken.Value == "!=" || 
               _currentToken.Value == ">" || _currentToken.Value == "<" || 
               _currentToken.Value == ">=" || _currentToken.Value == "<="))
        {
            var op = _currentToken.Value;
            Advance();
            node = new BinaryOperationNode(node, op, AdditiveExpression());
        }

        return node;
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
              (_currentToken.Value == "*" || _currentToken.Value == "/"))
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
                    return new LiteralNode(double.Parse(token.Value));
                }
                return new LiteralNode(int.Parse(token.Value));

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
    /// Parses a function call expression, including its arguments.
    /// </summary>
    /// <returns>The parsed <see cref="FunctionCallNode"/>.</returns>
    private AstNode FunctionCall()
    {
        var functionName = Eat(TokenType.Function).Value;
        Eat(TokenType.LeftParen);
        
        var arguments = new List<AstNode>();
        
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
}
