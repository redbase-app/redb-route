using System;

namespace redb.Route.Expressions.Ast;

/// <summary>
/// Enumerates the types of tokens recognized by the expression tokenizer.
/// </summary>
public enum TokenType
{
    /// <summary>Numeric literal (integer or floating-point).</summary>
    Number,

    /// <summary>String literal (single- or double-quoted).</summary>
    String,

    /// <summary>Identifier such as a variable name or keyword.</summary>
    Identifier,

    /// <summary>Operator symbol (arithmetic, comparison, or logical).</summary>
    Operator,

    /// <summary>Left parenthesis <c>(</c>.</summary>
    LeftParen,

    /// <summary>Right parenthesis <c>)</c>.</summary>
    RightParen,

    /// <summary>Comma separator <c>,</c>.</summary>
    Comma,

    /// <summary>Dot (property access) <c>.</c>.</summary>
    Dot,

    /// <summary>Left bracket <c>[</c>.</summary>
    LeftBracket,

    /// <summary>Right bracket <c>]</c>.</summary>
    RightBracket,

    /// <summary>Function name (identifier followed by an opening parenthesis).</summary>
    Function,

    /// <summary>Colon <c>:</c> used in ternary expressions.</summary>
    Colon,

    /// <summary>End-of-input marker.</summary>
    Eof
}

/// <summary>
/// Represents a single token produced by lexical analysis.
/// </summary>
public class Token
{
    /// <summary>
    /// Gets the type of this token.
    /// </summary>
    public TokenType Type { get; }

    /// <summary>
    /// Gets the textual value of this token.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the position (zero-based index) of this token in the input string.
    /// </summary>
    public int Position { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Token"/> class.
    /// </summary>
    /// <param name="type">The token type.</param>
    /// <param name="value">The textual value of the token.</param>
    /// <param name="position">The position of the token in the input string.</param>
    public Token(TokenType type, string value, int position)
    {
        Type = type;
        Value = value;
        Position = position;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Type}({Value})";
}
