namespace redb.Route.Expressions;

/// <summary>
/// The type an XPath expression produces, at the XPath level.
/// </summary>
/// <remarks>
/// <para>
/// This is a different question from the CLR type the caller wants back. XPath 1.0 has four types
/// of its own — node-set, string, number, boolean — and an expression produces one of them before
/// .NET conversion is even considered. <c>XPath("/order/total", XPathResult.Number)</c> says "read
/// this the way XPath's <c>number()</c> reads it"; <c>XPath&lt;int&gt;("/order/total")</c> says
/// "give me the result as an <see cref="int"/>". The two compose, and either can be used alone.
/// </para>
/// <para>
/// The distinction matters most in a condition, where the default <see cref="NodeSet"/> asks
/// whether the path matched anything at all, while <see cref="String"/> asks what the match says.
/// Apache Camel draws the same line, as <c>resultQName</c> against <c>resultType</c>.
/// </para>
/// </remarks>
public enum XPathResult
{
    /// <summary>
    /// Every matching node (the default, and XPath's own default). In a condition a node-set is
    /// true when it is non-empty, whatever the matched nodes contain.
    /// </summary>
    NodeSet,

    /// <summary>
    /// The first matching node in document order, or nothing. In a condition it reads as
    /// "a node was found".
    /// </summary>
    Node,

    /// <summary>
    /// XPath's <c>string()</c> of the result: the text of the first matching node, or an empty
    /// string when nothing matched. The value is then read by the ordinary DSL rules, so in a
    /// condition <c>"0"</c> is false — the same as a header holding <c>"0"</c>.
    /// </summary>
    String,

    /// <summary>
    /// XPath's <c>number()</c> of the result, as a <see cref="double"/>. Text that is not a number
    /// yields <see cref="double.NaN"/>.
    /// </summary>
    Number,

    /// <summary>
    /// XPath's <c>boolean()</c> of the result: a non-empty node-set, a non-empty string and a
    /// non-zero number are true.
    /// </summary>
    Boolean
}
