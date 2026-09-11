using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.Xml.XPath;
using System.Xml.Xsl;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Supplies bound values to the <c>$name</c> variables of an XPath expression.
/// </summary>
/// <remarks>
/// <para>
/// The BCL resolves XPath variables only through an <see cref="XsltContext"/>, which it accepts in
/// the same slot as a plain namespace resolver — so one object carries both the prefixes and the
/// values. <see cref="XsltContext"/> derives from <see cref="XmlNamespaceManager"/>, which is what
/// makes carrying both natural rather than a trick.
/// </para>
/// <para>
/// Built per evaluation, because the values come from the message. The path is not rebuilt, which
/// is the whole point: the same query text runs whatever the data.
/// </para>
/// </remarks>
internal sealed class XPathVariableContext : XsltContext
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    private XPathVariableContext() : base(new NameTable())
    {
    }

    internal static XPathVariableContext Build(
        IXmlNamespaceResolver? namespaces,
        IReadOnlyList<(string Name, IExpression Value)> parameters,
        IExchange exchange)
    {
        var context = new XPathVariableContext();

        if (namespaces is not null)
        {
            foreach (var (prefix, uri) in namespaces.GetNamespacesInScope(XmlNamespaceScope.ExcludeXml))
                context.AddNamespace(prefix, uri);
        }

        foreach (var (name, value) in parameters)
            context._values[name] = ToXPathValue(value.Evaluate<object?>(exchange));

        return context;
    }

    /// <summary>
    /// Converts a CLR value to one of the four types XPath 1.0 knows.
    /// </summary>
    /// <remarks>
    /// XPath has no integers, no dates and no null: numbers are doubles, and everything it cannot
    /// place is a string. Converting here rather than at the call site keeps the coercion in one
    /// place, and keeps it invariant — a decimal separator that follows the server's locale would
    /// make the same route match different documents on different machines.
    /// </remarks>
    private static object ToXPathValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b,
        string s => s,
        double d => d,
        float f => (double)f,
        decimal m => (double)m,
        sbyte or byte or short or ushort or int or uint or long or ulong
            => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <inheritdoc />
    /// <remarks>
    /// Only unprefixed variables resolve. <c>WithParameters</c> binds local names alone — it
    /// rejects even a leading <c>$</c> — so <c>$x:id</c> can never have a binding here, and
    /// matching it to the binding named <c>id</c> would answer a different question than the one
    /// the path asked. Returning <c>null</c> lets the engine fail with its own "variable not
    /// defined", which is the honest outcome.
    /// </remarks>
    public override IXsltContextVariable? ResolveVariable(string prefix, string name)
        => string.IsNullOrEmpty(prefix) && _values.TryGetValue(name, out var value)
            ? new BoundValue(value)
            : null;

    /// <inheritdoc />
    public override IXsltContextFunction? ResolveFunction(string prefix, string name, XPathResultType[] argTypes)
        => null;

    /// <inheritdoc />
    public override bool Whitespace => true;

    /// <inheritdoc />
    public override bool PreserveWhitespace(XPathNavigator node) => true;

    /// <inheritdoc />
    public override int CompareDocument(string baseUri, string nextbaseUri) => 0;

    /// <summary>One bound value, presented to the engine with the XPath type it actually has.</summary>
    private sealed class BoundValue(object value) : IXsltContextVariable
    {
        public bool IsLocal => false;

        public bool IsParam => false;

        public XPathResultType VariableType => value switch
        {
            double => XPathResultType.Number,
            bool => XPathResultType.Boolean,
            _ => XPathResultType.String
        };

        public object Evaluate(XsltContext xsltContext) => value;
    }
}
