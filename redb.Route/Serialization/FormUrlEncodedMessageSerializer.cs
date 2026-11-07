namespace redb.Route.Serialization;

/// <summary>
/// HTML form data as a data format, registered by default:
/// <c>Unmarshal&lt;FormBody&gt;("application/x-www-form-urlencoded")</c> or <c>ConvertBody&lt;FormBody&gt;()</c> after an
/// HTTP consumer reads the posted fields, <c>Marshal("application/x-www-form-urlencoded")</c> writes a
/// <see cref="FormBody"/> or a dictionary of strings. Parsing and encoding follow the WHATWG URL standard.
/// </summary>
/// <remarks>
/// Unmarshal targets: <see cref="FormBody"/> (every field, in order) and <c>Dictionary&lt;string, string&gt;</c>
/// (the first value of each name). Marshal sources: <see cref="FormBody"/> and any sequence of string pairs, such as
/// <c>IDictionary&lt;string, string&gt;</c>.
/// </remarks>
public sealed class FormUrlEncodedMessageSerializer : Abstractions.IMessageSerializer
{
    /// <summary>Content type the format is registered under by default.</summary>
    public const string DefaultContentType = "application/x-www-form-urlencoded";

    private readonly FormUrlEncodedOptions _options;

    /// <summary>A format with the default options (UTF-8, ASP.NET Core limits).</summary>
    public FormUrlEncodedMessageSerializer() : this(new FormUrlEncodedOptions())
    {
    }

    /// <summary>A format with the given options.</summary>
    public FormUrlEncodedMessageSerializer(FormUrlEncodedOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.Encoding, nameof(options.Encoding));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxFields, nameof(options.MaxFields));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxNameLength, nameof(options.MaxNameLength));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxValueLength, nameof(options.MaxValueLength));
    }

    /// <summary>A format configured in place.</summary>
    public FormUrlEncodedMessageSerializer(Action<FormUrlEncodedOptions> configure) : this(Configure(configure))
    {
    }

    /// <inheritdoc />
    public string ContentType => DefaultContentType;

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => value switch
    {
        null => [],
        IEnumerable<KeyValuePair<string, string>> fields => _options.Encoding.GetBytes(FormUrlEncoding.Encode(fields, _options.Encoding)),
        _ => throw new NotSupportedException(
            $"Form data cannot be written from '{value.GetType().FullName}': use FormBody or a sequence of string pairs (IDictionary<string, string>).")
    };

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data) => (T?)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(type);
        var form = FormUrlEncoding.Decode(data, _options.Encoding, _options);
        if (type.IsAssignableFrom(typeof(FormBody)))
            return form;
        if (type.IsAssignableFrom(typeof(Dictionary<string, string>)))
            return form.ToDictionary();
        throw new NotSupportedException(
            $"Form data cannot be read as '{type.FullName}': use FormBody (every field) or Dictionary<string, string> (the first value of each name).");
    }

    private static FormUrlEncodedOptions Configure(Action<FormUrlEncodedOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new FormUrlEncodedOptions();
        configure(options);
        return options;
    }
}
