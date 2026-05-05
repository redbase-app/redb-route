using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Serialization;

/// <summary>
/// Processor that converts the exchange body to the specified target type.
/// Uses ContentType to determine encoding for byte[]/string conversions.
/// Supports Stream ↔ byte[] ↔ string conversions.
/// </summary>
public sealed class ConvertBodyProcessor : IProcessor
{
    private readonly Type _targetType;
    private readonly IDataFormatRegistry? _registry;

    /// <summary>Creates a body converter processor.</summary>
    /// <param name="targetType">Target type to convert body to.</param>
    /// <param name="registry">Optional data format registry for ContentType-based deserialization.</param>
    public ConvertBodyProcessor(Type targetType, IDataFormatRegistry? registry = null)
    {
        _targetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
        _registry = registry;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var body = exchange.In.Body;
        if (body is null)
            return;

        if (_targetType.IsInstanceOfType(body))
            return;

        exchange.In.Body = body is Stream
            ? await ConvertStreamAsync(body, exchange.In.ContentType, ct).ConfigureAwait(false)
            : Convert(body, exchange.In.ContentType);
    }

    private object Convert(object body, string? contentType)
    {
        // byte[] → string
        if (_targetType == typeof(string) && body is byte[] bytes)
        {
            var encoding = GetEncoding(contentType);
            return encoding.GetString(bytes);
        }

        // string → byte[]
        if (_targetType == typeof(byte[]) && body is string str)
        {
            var encoding = GetEncoding(contentType);
            return encoding.GetBytes(str);
        }

        // byte[] → Stream
        if (_targetType == typeof(Stream) && body is byte[] b)
            return new MemoryStream(b, writable: false);

        // string → Stream
        if (_targetType == typeof(Stream) && body is string s)
            return new MemoryStream(GetEncoding(contentType).GetBytes(s));

        // string/byte[] → T via registered serializer when ContentType is known
        if (contentType is not null)
        {
            var serializer = _registry?.GetSerializer(contentType);
            if (serializer is not null)
            {
                var data = body switch
                {
                    byte[] raw => raw,
                    string txt => Encoding.UTF8.GetBytes(txt),
                    _ => null
                };
                if (data is not null)
                    return serializer.Deserialize(data, _targetType)
                        ?? throw new InvalidOperationException(
                            $"Serializer for '{contentType}' returned null for type '{_targetType.Name}'.");
            }
        }

        // Fallback: try System.Convert
        try
        {
            return System.Convert.ChangeType(body, _targetType);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot convert body of type '{body.GetType().Name}' to '{_targetType.Name}'." +
                (contentType != null ? $" ContentType: '{contentType}'." : ""),
                ex);
        }
    }

    private async Task<object> ConvertStreamAsync(object body, string? contentType, CancellationToken ct)
    {
        var stream = (Stream)body;

        // Stream → byte[]
        if (_targetType == typeof(byte[]))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }

        // Stream → string
        if (_targetType == typeof(string))
        {
            using var reader = new StreamReader(stream, GetEncoding(contentType), leaveOpen: true);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Cannot convert body of type 'Stream' to '{_targetType.Name}'.");
    }

    private static Encoding GetEncoding(string? contentType)
    {
        if (contentType is null)
            return Encoding.UTF8;

        // Parse charset from ContentType, e.g. "text/plain; charset=iso-8859-1"
        var charsetIndex = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (charsetIndex >= 0)
        {
            var charset = contentType[(charsetIndex + 8)..].Trim().TrimEnd(';').Trim();
            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch
            {
                // Unknown charset — fall back to UTF-8
            }
        }

        return Encoding.UTF8;
    }
}
