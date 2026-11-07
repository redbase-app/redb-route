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
    /// <summary>The namespaces of the language: <c>bean:</c> (a method call), <c>ref:</c> (a registry object), <c>env:</c> (an environment variable).</summary>
    internal static readonly HashSet<string> FunctionNamespaces = new(StringComparer.Ordinal) { "bean", "ref", "env" };

    /// <summary>Set after a namespace token: the next word is the call's target, read by <see cref="NamespaceTarget"/>.</summary>
    private bool _namespaceTargetNext;

    /// <summary>The namespace whose target is read next.</summary>
    private string _namespace = "";

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
        { "%", "%" },
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

        // The target of a namespaced call: a registry name may carry dashes (seen-orders.Check)
        if (_namespaceTargetNext)
        {
            _namespaceTargetNext = false;
            if (char.IsLetter(_currentChar) || _currentChar == '_')
                return NamespaceTarget();
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

        // A placeholder inside an expression (the arguments of a bean: call); the parser decides where it is allowed.
        if (_currentChar == '$' && _position + 1 < _length && _input[_position + 1] == '{')
        {
            var start = _position;
            Advance();
            Advance();
            return new Token(TokenType.PlaceholderOpen, "${", start);
        }

        // Handle single-character tokens
        switch (_currentChar)
        {
            case '}':
                return CreateToken(TokenType.RightBrace, Advance().ToString());
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
            case '%':
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
    /// <summary>
    /// If the next word after optional whitespace is exactly <paramref name="word"/> (case-insensitive,
    /// ending at a non-identifier character), consumes it and returns true; otherwise leaves the
    /// position untouched. Lets "NOT IN" become one token while "NOT inStock" stays NOT + identifier.
    /// </summary>
    private bool TryConsumeFollowingWord(string word)
    {
        var probe = _position;
        while (probe < _length && char.IsWhiteSpace(_input[probe]))
            probe++;
        if (probe == _position)
            return false;   // "NOTIN" is one identifier, not two words

        if (probe + word.Length > _length
            || string.Compare(_input, probe, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var end = probe + word.Length;
        if (end < _length && (char.IsLetterOrDigit(_input[end]) || _input[end] == '_' || _input[end] == '.'))
            return false;   // "NOT inStock", "NOT in.x": a longer identifier, not the IN operator

        while (_position < end)
            Advance();
        return true;
    }

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

        // A function namespace: a registered name written directly against its colon and its target (bean:svc.M).
        // With a space or without a target the colon stays the ternary's, so "c ? bean : other" reads as before.
        if (FunctionNamespaces.Contains(result) && _currentChar == ':' && _position + 1 < _length
            && (char.IsLetter(_input[_position + 1]) || _input[_position + 1] == '_'))
        {
            Advance(); // the colon
            _namespaceTargetNext = true;
            _namespace = result;
            return new Token(TokenType.Namespace, result, startPos);
        }

        var upper = result.ToUpperInvariant();

        // IN is an operator even with a parenthesis right after it: "x in('a','b')" is a list
        // literal, not a call to a function named "in". Checked before the function rule for that
        // reason; the other word operators keep their order, so nothing they parse changes.
        if (upper == "IN")
        {
            return new Token(TokenType.Operator, "IN", startPos);
        }

        // If the identifier is followed by an opening parenthesis, treat it as a function call
        if (_position < _length && _currentChar == '(')
        {
            return new Token(TokenType.Function, result, startPos);
        }

        // "NOT IN" is one binary operator, not a unary NOT in front of a stray IN.
        if (upper == "NOT" && TryConsumeFollowingWord("IN"))
        {
            return new Token(TokenType.Operator, "NOT IN", startPos);
        }

        // Check for word-operators (NOT, AND, OR, XOR)
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
    /// Reads the target of a namespaced call, <c>name.Method</c>: the name may hold dashes, as registry names do
    /// (<c>seen-orders</c>); the method after the last dot is an identifier, so a dash after it is the minus operator
    /// again (<c>bean:svc.Count-1</c> reads the method <c>Count</c>, then minus one). A function token when a
    /// parenthesis follows, an identifier token otherwise.
    /// <para>
    /// The target of <c>ref:</c> is a registry name only, so the whole run is the name, dots and dashes included
    /// (<c>ref:chat.settings</c>), as in Apache Camel; a trailing dot or dash is not part of it.
    /// </para>
    /// </summary>
    private Token NamespaceTarget()
    {
        var startPos = _position;
        var end = _position;
        while (end < _length && (char.IsLetterOrDigit(_input[end]) || _input[end] is '_' or '.' or '-'))
            end++;
        var run = _input[startPos..end];
        if (_namespace is "ref" or "env")
        {
            run = run.TrimEnd('.', '-');
            while (_position < startPos + run.Length)
                Advance();
            return new Token(TokenType.Identifier, run, startPos);
        }
        var lastDot = run.LastIndexOf('.');
        var methodStart = lastDot + 1;
        var methodDash = run.IndexOf('-', methodStart);
        if (methodDash >= 0)
            run = run[..methodDash];
        run = run.TrimEnd('-');
        while (_position < startPos + run.Length)
            Advance();
        return _position < _length && _currentChar == '('
            ? new Token(TokenType.Function, run, startPos)
            : new Token(TokenType.Identifier, run, startPos);
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
        else
        {
            // A literal that never ends: the parser would read it truncated by one character ('abc gave "ab") or, for
            // a lone quote, fail with an internal "length -1" (findings rows 14 and 17). Name it where the author can
            // find it.
            throw new InvalidOperationException(
                $"unterminated string literal starting at position {startPos + 1} within the expression \"{_input}\".");
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
                twoCharOp == "++" || twoCharOp == "--" || twoCharOp == "??" || twoCharOp == "=~")
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
