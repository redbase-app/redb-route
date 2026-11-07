namespace redb.Route.Xslt;

/// <summary>
/// The form the XSLT result is materialised into (Apache Camel <c>xslt</c> <c>output</c> option).
/// </summary>
public enum XsltOutput
{
    /// <summary>A <see cref="string"/> (default).</summary>
    String,

    /// <summary>A <see cref="byte"/> array, encoded per the stylesheet's <c>xsl:output</c> encoding.</summary>
    Bytes,

    /// <summary>An <see cref="System.Xml.XmlDocument"/> (Camel's <c>DOM</c>).</summary>
    Dom,
}

/// <summary>
/// Pluggable XSLT transformation engine — the transformation counterpart of
/// <see cref="redb.Route.Validation.IMessageValidator"/>. An instance is compiled from a single
/// stylesheet and reused (the compiled template is the expensive part, so it is cached in the engine).
/// <para>
/// The built-in <see cref="XslCompiledTransformEngine"/> uses the BCL (<c>System.Xml.Xsl</c>,
/// <b>XSLT 1.0</b>) with zero external dependencies — matching Apache Camel's default JAXP engine.
/// </para>
/// <para>
/// To substitute a different processor — Saxon for XSLT 2.0/3.0, say — implement
/// <see cref="IXsltEngineFactory"/> and register it with <c>context.UseXsltEngine(...)</c> or
/// <c>services.AddXsltEngine(...)</c>. Implementing this interface alone is not enough and never
/// was: until 2026-09-03 every construction site named the built-in engine directly, so the
/// substitution this paragraph promised could not actually be made.
/// </para>
/// </summary>
public interface IXsltEngine
{
    /// <summary>
    /// Transforms <paramref name="body"/> through the compiled stylesheet and materialises the result
    /// as <paramref name="output"/>.
    /// </summary>
    /// <param name="body">
    /// The input document: a <see cref="string"/>, <see cref="byte"/> array, <see cref="System.IO.Stream"/>,
    /// <see cref="System.Xml.XmlReader"/>, or <see cref="System.Xml.XPath.IXPathNavigable"/>.
    /// </param>
    /// <param name="output">The result form (string / bytes / DOM).</param>
    /// <param name="parameters">Optional stylesheet parameters (<c>xsl:param</c>), by name.</param>
    /// <returns>The transformed result as a <see cref="string"/>, <see cref="byte"/> array, or DOM.</returns>
    object Transform(object? body, XsltOutput output, IReadOnlyDictionary<string, object?>? parameters);
}
