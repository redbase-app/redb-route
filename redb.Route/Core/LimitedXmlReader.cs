using System.Xml;
using System.Xml.Schema;

namespace redb.Route.Core;

/// <summary>
/// An <see cref="XmlReader"/> over another one that checks <see cref="XmlLimits"/> on every element it reads and
/// throws <see cref="XmlLimitException"/> at the first one over a limit. Everything else is the inner reader's:
/// the wrapper adds no buffering, so a loader on top builds no more of a hostile document than up to the offending
/// element. Line information passes through for <c>LoadOptions.SetLineInfo</c>.
/// </summary>
internal sealed class LimitedXmlReader : XmlReader, IXmlLineInfo, IXmlNamespaceResolver
{
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    private readonly XmlReader _inner;
    private readonly XmlLimits _limits;
    private long _elements;
    private long _namespaceDeclarations;

    internal LimitedXmlReader(XmlReader inner, XmlLimits limits)
    {
        _inner = inner;
        _limits = limits;
    }

    /// <inheritdoc />
    public override bool Read()
    {
        if (!_inner.Read())
            return false;
        if (_inner.NodeType == XmlNodeType.Element)
            CheckElement();
        return true;
    }

    private void CheckElement()
    {
        var depth = _inner.Depth + 1;
        if (_limits.MaxDepth is { } maxDepth && depth > maxDepth)
            throw Over(nameof(XmlLimits.MaxDepth), maxDepth, $"Element '{_inner.Name}' is nested {depth} levels deep");

        if (_limits.MaxElements is { } maxElements && ++_elements > maxElements)
            throw Over(nameof(XmlLimits.MaxElements), maxElements, $"The document has more than {maxElements} elements");

        var attributes = _inner.AttributeCount;
        if (_limits.MaxAttributesPerElement is { } maxAttributes && attributes > maxAttributes)
            throw Over(nameof(XmlLimits.MaxAttributesPerElement), maxAttributes, $"Element '{_inner.Name}' has {attributes} attributes");

        if (attributes == 0 || (_limits.MaxNamespaceDeclarationsPerElement is null && _limits.MaxNamespaceDeclarations is null))
            return;

        var declarations = 0;
        for (var i = 0; i < attributes; i++)
        {
            _inner.MoveToAttribute(i);
            if (_inner.NamespaceURI == XmlnsNamespace)
                declarations++;
        }
        _inner.MoveToElement();

        if (_limits.MaxNamespaceDeclarationsPerElement is { } maxPerElement && declarations > maxPerElement)
            throw Over(nameof(XmlLimits.MaxNamespaceDeclarationsPerElement), maxPerElement,
                $"Element '{_inner.Name}' declares {declarations} namespaces");

        _namespaceDeclarations += declarations;
        if (_limits.MaxNamespaceDeclarations is { } maxTotal && _namespaceDeclarations > maxTotal)
            throw Over(nameof(XmlLimits.MaxNamespaceDeclarations), maxTotal, $"The document declares more than {maxTotal} namespaces");
    }

    private XmlLimitException Over(string limit, long maximum, string what)
    {
        var line = _inner as IXmlLineInfo;
        var (number, position) = line is { } info && info.HasLineInfo() ? (info.LineNumber, info.LinePosition) : (0, 0);
        return new XmlLimitException(limit, maximum, $"{what}: over the XML limit {limit} = {maximum}.", number, position);
    }

    // ── Everything else is the inner reader's ───────────────────────────────

    /// <inheritdoc />
    public override XmlNodeType NodeType => _inner.NodeType;
    /// <inheritdoc />
    public override string LocalName => _inner.LocalName;
    /// <inheritdoc />
    public override string NamespaceURI => _inner.NamespaceURI;
    /// <inheritdoc />
    public override string Prefix => _inner.Prefix;
    /// <inheritdoc />
    public override string Name => _inner.Name;
    /// <inheritdoc />
    public override bool HasValue => _inner.HasValue;
    /// <inheritdoc />
    public override string Value => _inner.Value;
    /// <inheritdoc />
    public override int Depth => _inner.Depth;
    /// <inheritdoc />
    public override string BaseURI => _inner.BaseURI;
    /// <inheritdoc />
    public override bool IsEmptyElement => _inner.IsEmptyElement;
    /// <inheritdoc />
    public override bool IsDefault => _inner.IsDefault;
    /// <inheritdoc />
    public override char QuoteChar => _inner.QuoteChar;
    /// <inheritdoc />
    public override XmlSpace XmlSpace => _inner.XmlSpace;
    /// <inheritdoc />
    public override string XmlLang => _inner.XmlLang;
    /// <inheritdoc />
    public override IXmlSchemaInfo? SchemaInfo => _inner.SchemaInfo;
    /// <inheritdoc />
    public override Type ValueType => _inner.ValueType;
    /// <inheritdoc />
    public override int AttributeCount => _inner.AttributeCount;
    /// <inheritdoc />
    public override bool EOF => _inner.EOF;
    /// <inheritdoc />
    public override ReadState ReadState => _inner.ReadState;
    /// <inheritdoc />
    public override XmlNameTable NameTable => _inner.NameTable;
    /// <inheritdoc />
    public override XmlReaderSettings? Settings => _inner.Settings;
    /// <inheritdoc />
    public override bool CanResolveEntity => _inner.CanResolveEntity;
    /// <inheritdoc />
    public override bool CanReadBinaryContent => _inner.CanReadBinaryContent;
    /// <inheritdoc />
    public override bool CanReadValueChunk => _inner.CanReadValueChunk;
    /// <inheritdoc />
    public override bool HasAttributes => _inner.HasAttributes;

    /// <inheritdoc />
    public override string? GetAttribute(string name) => _inner.GetAttribute(name);
    /// <inheritdoc />
    public override string? GetAttribute(string name, string? namespaceURI) => _inner.GetAttribute(name, namespaceURI);
    /// <inheritdoc />
    public override string GetAttribute(int i) => _inner.GetAttribute(i);
    /// <inheritdoc />
    public override bool MoveToAttribute(string name) => _inner.MoveToAttribute(name);
    /// <inheritdoc />
    public override bool MoveToAttribute(string name, string? ns) => _inner.MoveToAttribute(name, ns);
    /// <inheritdoc />
    public override void MoveToAttribute(int i) => _inner.MoveToAttribute(i);
    /// <inheritdoc />
    public override bool MoveToFirstAttribute() => _inner.MoveToFirstAttribute();
    /// <inheritdoc />
    public override bool MoveToNextAttribute() => _inner.MoveToNextAttribute();
    /// <inheritdoc />
    public override bool MoveToElement() => _inner.MoveToElement();
    /// <inheritdoc />
    public override bool ReadAttributeValue() => _inner.ReadAttributeValue();
    /// <inheritdoc />
    public override string? LookupNamespace(string prefix) => _inner.LookupNamespace(prefix);
    /// <inheritdoc />
    public override void ResolveEntity() => _inner.ResolveEntity();
    /// <inheritdoc />
    public override int ReadValueChunk(char[] buffer, int index, int count) => _inner.ReadValueChunk(buffer, index, count);
    /// <inheritdoc />
    public override void Close() => _inner.Close();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    // ── IXmlLineInfo, IXmlNamespaceResolver ─────────────────────────────────

    bool IXmlLineInfo.HasLineInfo() => _inner is IXmlLineInfo info && info.HasLineInfo();
    int IXmlLineInfo.LineNumber => (_inner as IXmlLineInfo)?.LineNumber ?? 0;
    int IXmlLineInfo.LinePosition => (_inner as IXmlLineInfo)?.LinePosition ?? 0;

    IDictionary<string, string> IXmlNamespaceResolver.GetNamespacesInScope(XmlNamespaceScope scope) =>
        (_inner as IXmlNamespaceResolver)?.GetNamespacesInScope(scope) ?? new Dictionary<string, string>();
    string? IXmlNamespaceResolver.LookupNamespace(string prefix) => _inner.LookupNamespace(prefix);
    string? IXmlNamespaceResolver.LookupPrefix(string namespaceName) => (_inner as IXmlNamespaceResolver)?.LookupPrefix(namespaceName);
}
