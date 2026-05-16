using System;
using System.Collections.Generic;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// Tokenizer (lexer) that splits an input expression string into a list of <see cref="Token"/> instances.
/// </summary>
public class Tokenizer
{
    /// <summary>
    /// The input expression string.
    /// </summary>
    private readonly string _input;

    /// <summary>
    /// The current character position in the input.
    /// </summary>
    private int _position;

    /// <summary>
    /// The total length of the input string.
    /// </summary>
    private int _length;

    /// <summary>
    /// The character at the current position.
    /// </summary>
    private char _currentChar;

    /// <summary>
    /// Set of recognized keywords (case-insensitive).
    /// </summary>
    private static readonly HashSet<string> _keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "body", "header", "property", "jpath", "xpath", "logical", "null", "true", "false"
    };

    /// <summary>
    /// Mapping of operator symbols and textual representations to their canonical forms.
    /// </summary>
    private static readonly Dictionary<string, string> _operators = new Dictionary<string, string>
    {
        { "+", "+" },
        { "-", "-" },
        { "*", "*" },
        { "/", "/" },
        { "==", "==" },
        { "!=", "!=" },
        { ">", ">" },
        { "<", "<" },
        { ">=", ">=" },
        { "<=", "<=" },
        { "&&", "AND" },
        { "||", "OR" },
        { "!", "NOT" },
        { "AND", "AND" },
        { "OR", "OR" },
        { "NOT", "NOT" },
        { "XOR", "XOR" },
        { "++", "++" },
        { "--", "--" },
        { "??", "??" },
        { "?", "?" },
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="Tokenizer"/> class.
    /// </summary>
    /// <param name="input">The expression string to tokenize.</param>
    public Tokenizer(string input)
    {
        _input = input ?? string.Empty;
        _length = _input.Length;
        _position = 0;

        if (_length > 0)
            _currentChar = _input[0];
    }

    /// <summary>
    /// Tokenizes the entire input string and returns all tokens including the trailing EOF token.
    /// </summary>
    /// <returns>A list of all tokens parsed from the input.</returns>
    public List<Token> GetAllTokens()
    {
        var tokens = new List<Token>();
        Token token;

        do
        {
            token = GetNextToken();
            tokens.Add(token);
        }
        while (token.Type != TokenType.Eof);

        return tokens;
    }

    /// <summary>
    /// Reads and returns the next token from the input.
    /// </summary>
    /// <returns>The next <see cref="Token"/> from the input stream.</returns>
    public Token GetNextToken()
    {
        // Skip whitespace
        while (_position < _length && char.IsWhiteSpace(_currentChar))
        {
            Advance();
        }

        // End of input reached
        if (_position >= _length)
        {
            return new Token(TokenType.Eof, string.Empty, _position);
        }

        // Handle numbers
        if (char.IsDigit(_currentChar))
        {
            return Number();
        }

        // Handle identifiers and keywords
        if (char.IsLetter(_currentChar) || _currentChar == '_')
        {
            return Identifier();
        }

        // Handle string literals
        if (_currentChar == '\'' || _currentChar == '\"')
        {
            return String();
        }

        // Handle single-character tokens
        switch (_currentChar)
        {
            case '(':
                return CreateToken(TokenType.LeftParen, Advance().ToString());
            case ')':
                return CreateToken(TokenType.RightParen, Advance().ToString());
            case '[':
                return CreateToken(TokenType.LeftBracket, Advance().ToString());
            case ']':
                return CreateToken(TokenType.RightBracket, Advance().ToString());
            case ',':
                return CreateToken(TokenType.Comma, Advance().ToString());
            case '.':
                return CreateToken(TokenType.Dot, Advance().ToString());
            case ':':
                return CreateToken(TokenType.Colon, Advance().ToString());
            case '+':
            case '-':
            case '*':
            case '/':
            case '!':
            case '=':
            case '<':
            case '>':
            case '&':
            case '|':
            case '?':
                return Operator();
        }

        // Unknown character — return as an operator token
        var ch = _currentChar;
        Advance();
        return new Token(TokenType.Operator, ch.ToString(), _position - 1);
    }

    /// <summary>
    /// Advances the position by one character and returns the new current character.
    /// </summary>
    /// <returns>The character at the new position, or <c>'\0'</c> if the end of input is reached.</returns>
    private char Advance()
    {
        _position++;
        if (_position < _length)
        {
            _currentChar = _input[_position];
            return _currentChar;
        }

        // End of input reached — set null character
        _currentChar = '\0';
        return _currentChar;
    }

    /// <summary>
    /// Reads a numeric token (integer or floating-point).
    /// </summary>
    /// <returns>A <see cref="Token"/> of type <see cref="TokenType.Number"/>.</returns>
    private Token Number()
    {
        var startPos = _position;
        var result = string.Empty;
        var hasDot = false;

        while (_position < _length && (char.IsDigit(_currentChar) || (_currentChar == '.' && !hasDot)))
        {
            if (_currentChar == '.')
                hasDot = true;

            result += _currentChar;
            Advance();
        }

        return new Token(TokenType.Number, result, startPos);
    }

    /// <summary>
    /// Reads an identifier or keyword token. If the identifier is immediately followed by
    /// an opening parenthesis, it is classified as a <see cref="TokenType.Function"/>.
    /// </summary>
    /// <returns>A <see cref="Token"/> of type <see cref="TokenType.Identifier"/> or <see cref="TokenType.Function"/>.</returns>
    private Token Identifier()
    {
        var startPos = _position;
        var result = string.Empty;

        while (_position < _length && (char.IsLetterOrDigit(_currentChar) || _currentChar == '_' || _currentChar == '.'))
        {
            result += _currentChar;
            Advance();
        }

        // If the identifier is followed by an opening parenthesis, treat it as a function call
        if (_position < _length && _currentChar == '(')
        {
            return new Token(TokenType.Function, result, startPos);
        }

        // Check for word-operators (NOT, AND, OR, XOR)
        var upper = result.ToUpperInvariant();
        if (upper == "NOT" || upper == "AND" || upper == "OR" || upper == "XOR")
        {
            return new Token(TokenType.Operator, upper, startPos);
        }

        // Check for keywords
        if (_keywords.Contains(result))
        {
            return new Token(TokenType.Identifier, result, startPos);
        }

        return new Token(TokenType.Identifier, result, startPos);
    }

    /// <summary>
    /// Reads a string literal token (single- or double-quoted), handling escape sequences.
    /// </summary>
    /// <returns>A <see cref="Token"/> of type <see cref="TokenType.String"/>.</returns>
    private Token String()
    {
        var startPos = _position;
        var quoteChar = _currentChar;
        var result = quoteChar.ToString();

        Advance(); // Skip the opening quote

        // Read until the closing quote
        while (_position < _length && _currentChar != quoteChar)
        {
            // Handle escape sequences
            if (_currentChar == '\\' && _position + 1 < _length)
            {
                result += _currentChar;
                Advance();
            }

            result += _currentChar;
            Advance();
        }

        if (_position < _length) // Append the closing quote
        {
            result += _currentChar;
            Advance();
        }

        return new Token(TokenType.String, result, startPos);
    }

    /// <summary>
    /// Reads an operator token, handling both single-character and two-character operators.
    /// Normalizes the operator to its canonical form (e.g. <c>&amp;&amp;</c> becomes <c>AND</c>).
    /// </summary>
    /// <returns>A <see cref="Token"/> of type <see cref="TokenType.Operator"/>.</returns>
    private Token Operator()
    {
        var startPos = _position;
        var op = _currentChar.ToString();
        Advance();

        // Handle two-character operators
        if (_position < _length)
        {
            var twoCharOp = op + _currentChar;
            if (twoCharOp == "==" || twoCharOp == "!=" || twoCharOp == ">=" ||
                twoCharOp == "<=" || twoCharOp == "&&" || twoCharOp == "||" ||
                twoCharOp == "++" || twoCharOp == "--" || twoCharOp == "??")
            {
                op = twoCharOp;
                Advance();
            }
        }

        // Normalize to canonical form
        if (_operators.TryGetValue(op, out var standardOp))
        {
            op = standardOp;
        }

        return new Token(TokenType.Operator, op, startPos);
    }

    /// <summary>
    /// Creates a token at the current position minus one (used after a single-character advance).
    /// </summary>
    /// <param name="type">The token type.</param>
    /// <param name="value">The token value.</param>
    /// <returns>A new <see cref="Token"/> instance.</returns>
    private Token CreateToken(TokenType type, string value)
    {
        return new Token(type, value, _position - 1);
    }
}
