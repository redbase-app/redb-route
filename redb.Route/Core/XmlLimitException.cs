using System.Xml;

namespace redb.Route.Core;

/// <summary>
/// A document went over one of its <see cref="XmlLimits"/>. It is an <see cref="XmlException"/>: whoever already
/// answers malformed XML as the sender's fault answers this one the same way, with the position in the document.
/// </summary>
public sealed class XmlLimitException : XmlException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="limit">The name of the <see cref="XmlLimits"/> property that was exceeded.</param>
    /// <param name="maximum">Its value.</param>
    /// <param name="message">What went over it.</param>
    /// <param name="lineNumber">Line of the offending node, or 0 when unknown.</param>
    /// <param name="linePosition">Position of the offending node in its line, or 0 when unknown.</param>
    public XmlLimitException(string limit, long maximum, string message, int lineNumber, int linePosition)
        : base(message, null, lineNumber, linePosition)
    {
        Limit = limit;
        Maximum = maximum;
    }

    /// <summary>The name of the <see cref="XmlLimits"/> property that was exceeded, for example <c>MaxDepth</c>.</summary>
    public string Limit { get; }

    /// <summary>The value of that limit.</summary>
    public long Maximum { get; }
}
