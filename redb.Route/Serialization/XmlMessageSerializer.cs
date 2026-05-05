using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using redb.Route.Abstractions;

namespace redb.Route.Serialization;

/// <summary>
/// XML message serializer using <see cref="System.Xml.Serialization.XmlSerializer"/>.
/// Thread-safe, caches <see cref="XmlSerializer"/> instances per type.
/// </summary>
public sealed class XmlMessageSerializer : IMessageSerializer
{
    private static readonly ConcurrentDictionary<Type, XmlSerializer> SerializerCache = new();

    private readonly XmlWriterSettings _writerSettings;
    private readonly XmlReaderSettings _readerSettings;

    /// <summary>Default writer settings: UTF-8 without BOM, no XML declaration, no indentation.</summary>
    public static readonly XmlWriterSettings DefaultWriterSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        OmitXmlDeclaration = true,
        Indent = false,
        CloseOutput = false
    };

    /// <summary>Default reader settings.</summary>
    public static readonly XmlReaderSettings DefaultReaderSettings = new()
    {
        CloseInput = false,
        IgnoreWhitespace = true
    };

    /// <summary>Creates an XML serializer with default settings.</summary>
    public XmlMessageSerializer()
        : this(DefaultWriterSettings, DefaultReaderSettings)
    {
    }

    /// <summary>Creates an XML serializer with custom writer and reader settings.</summary>
    /// <param name="writerSettings">Settings for XML output.</param>
    /// <param name="readerSettings">Settings for XML input.</param>
    public XmlMessageSerializer(XmlWriterSettings writerSettings, XmlReaderSettings readerSettings)
    {
        _writerSettings = writerSettings ?? throw new ArgumentNullException(nameof(writerSettings));
        _readerSettings = readerSettings ?? throw new ArgumentNullException(nameof(readerSettings));
    }

    /// <inheritdoc />
    public string ContentType => "application/xml";

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        var serializer = GetOrCreateSerializer(typeof(T));
        using var ms = new MemoryStream();
        using (var xw = XmlWriter.Create(ms, _writerSettings))
        {
            serializer.Serialize(xw, value);
        }
        return ms.ToArray();
    }

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var serializer = GetOrCreateSerializer(typeof(T));
        using var ms = new MemoryStream(data);
        using var xr = XmlReader.Create(ms, _readerSettings);
        return (T?)serializer.Deserialize(xr);
    }

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(type);
        var serializer = GetOrCreateSerializer(type);
        using var ms = new MemoryStream(data);
        using var xr = XmlReader.Create(ms, _readerSettings);
        return serializer.Deserialize(xr);
    }

    private static XmlSerializer GetOrCreateSerializer(Type type)
        => SerializerCache.GetOrAdd(type, static t => new XmlSerializer(t));
}
