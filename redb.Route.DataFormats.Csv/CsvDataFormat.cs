using System.Collections;
using CsvHelper;
using CsvHelper.Configuration;
using redb.Route.Abstractions;

namespace redb.Route.DataFormats.Csv;

/// <summary>
/// CSV <see cref="IMessageSerializer"/> (CsvHelper). Marshals POCO collections (header from property
/// names), dictionaries (header from keys), raw rows (<c>string[]</c> / <c>object[]</c>, no header) or a
/// single POCO; unmarshals to <c>List&lt;T&gt;</c> / <c>T[]</c> / <c>IEnumerable&lt;T&gt;</c>, to
/// <c>List&lt;Dictionary&lt;string,string&gt;&gt;</c> (with header) or <c>List&lt;string[]&gt;</c> (without),
/// or to a single <c>T</c>. Errors name the format.
/// </summary>
public sealed class CsvDataFormat : IMessageSerializer
{
    /// <summary>Content type the format is registered under.</summary>
    public const string DefaultContentType = "text/csv";

    private readonly CsvDataFormatOptions _options;

    /// <summary>Creates the format with the given options (defaults when <c>null</c>).</summary>
    public CsvDataFormat(CsvDataFormatOptions? options = null) => _options = options ?? new CsvDataFormatOptions();

    /// <summary>The options in effect.</summary>
    public CsvDataFormatOptions Options => _options;

    /// <inheritdoc />
    public string ContentType => DefaultContentType;

    /// <inheritdoc />
    public IReadOnlyCollection<string> MediaTypes => [DefaultContentType, "application/csv"];

    private CsvConfiguration Configuration(bool forWriting)
    {
        var configuration = new CsvConfiguration(_options.Culture)
        {
            Delimiter = _options.Delimiter,
            HasHeaderRecord = _options.HasHeaderRecord,
            Quote = _options.Quote,
            TrimOptions = _options.TrimFields ? TrimOptions.Trim : TrimOptions.None,
            MissingFieldFound = null,
            HeaderValidated = null,
        };
        // NewLine is a writer setting: when set on a reader CsvHelper stops auto-detecting \r\n / \n / \r.
        if (forWriting)
            configuration.NewLine = _options.NewLine;
        return configuration;
    }

    // ── Marshal ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        switch (value)
        {
            case null: return [];
            case byte[] bytes: return bytes;
            case string text: return _options.Encoding.GetBytes(text);
        }

        try
        {
            using var writer = new StringWriter(_options.Culture);
            using (var csv = new CsvWriter(writer, Configuration(forWriting: true), leaveOpen: true))
            {
                switch (value)
                {
                    case IEnumerable<IDictionary> maps: WriteMaps(csv, maps); break;
                    case IEnumerable<string[]> rows: WriteRows(csv, rows); break;
                    case IEnumerable<object?[]> rows: WriteRows(csv, rows); break;
                    case IEnumerable records: csv.WriteRecords(records); break;
                    default: csv.WriteRecords(new[] { value }); break;
                }
            }
            return _options.Encoding.GetBytes(writer.ToString());
        }
        catch (Exception ex) when (ex is CsvHelperException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"CSV data format: cannot marshal {value.GetType().Name} ({ex.Message}).", ex);
        }
    }

    private void WriteMaps(CsvWriter csv, IEnumerable<IDictionary> maps)
    {
        // The header is the union of every row's keys in first-seen order: a column that only a later
        // row carries must not be dropped because the first row happened not to have it.
        var rows = maps.ToList();
        var header = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in rows)
            foreach (var key in map.Keys)
            {
                var name = key?.ToString() ?? string.Empty;
                if (seen.Add(name)) header.Add(name);
            }

        if (_options.HasHeaderRecord)
        {
            foreach (var name in header) csv.WriteField(name);
            csv.NextRecord();
        }
        foreach (var map in rows)
        {
            foreach (var name in header)
                csv.WriteField(map.Contains(name) ? map[name] : null);
            csv.NextRecord();
        }
    }

    private static void WriteRows(CsvWriter csv, IEnumerable<IEnumerable<object?>> rows)
    {
        foreach (var row in rows)
        {
            foreach (var cell in row) csv.WriteField(cell);
            csv.NextRecord();
        }
    }

    // ── Unmarshal ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data) => (T?)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(type);

        var text = _options.Encoding.GetString(data);
        if (type == typeof(string)) return text;

        try
        {
            using var reader = new StringReader(text);
            using var csv = new CsvReader(reader, Configuration(forWriting: false));

            var element = CollectionElement(type);
            if (element is null)
                return ReadSingle(csv, type);

            var items = ReadMany(csv, element);
            return ToCollection(type, element, items);
        }
        catch (Exception ex) when (ex is CsvHelperException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"CSV data format: cannot unmarshal to {type.Name} ({ex.Message}).", ex);
        }
    }

    private object? ReadSingle(CsvReader csv, Type type)
    {
        if (type == typeof(object) || IsMapType(type))
            return ReadMany(csv, type).FirstOrDefault();
        if (type == typeof(string[]))
            return ReadMany(csv, type).FirstOrDefault();
        return csv.GetRecords(type).Cast<object?>().FirstOrDefault();
    }

    private List<object?> ReadMany(CsvReader csv, Type element)
    {
        if (element == typeof(string[]))
            return ReadRows(csv, skipHeader: _options.HasHeaderRecord);
        if (element == typeof(object) || IsMapType(element))
            return _options.HasHeaderRecord ? ReadMaps(csv, element) : ReadRows(csv, skipHeader: false);
        return csv.GetRecords(element).Cast<object?>().ToList();
    }

    /// <summary>Raw rows; with a header record configured, the header line is data for nobody and is skipped.</summary>
    private static List<object?> ReadRows(CsvReader csv, bool skipHeader)
    {
        var rows = new List<object?>();
        if (skipHeader && !csv.Read()) return rows;
        while (csv.Read())
            rows.Add(csv.Parser.Record?.ToArray() ?? []);
        return rows;
    }

    private static List<object?> ReadMaps(CsvReader csv, Type element)
    {
        var rows = new List<object?>();
        if (!csv.Read()) return rows;
        csv.ReadHeader();
        var header = csv.HeaderRecord ?? [];
        var objectValues = element == typeof(object) || (element.IsGenericType && element.GetGenericArguments()[1] == typeof(object));

        while (csv.Read())
        {
            if (objectValues)
            {
                var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < header.Length; i++) map[header[i]] = csv.GetField(i);
                rows.Add(map);
            }
            else
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < header.Length; i++) map[header[i]] = csv.GetField(i) ?? string.Empty;
                rows.Add(map);
            }
        }
        return rows;
    }

    /// <summary>Element type when <paramref name="type"/> is a supported collection shape; otherwise <c>null</c> (single record).</summary>
    private static Type? CollectionElement(Type type)
    {
        if (type == typeof(string[])) return null;                     // one raw row
        if (type.IsArray) return type.GetElementType();
        if (!type.IsGenericType) return type == typeof(IEnumerable) ? typeof(object) : null;

        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IEnumerable<>)
            || definition == typeof(ICollection<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>))
            return type.GetGenericArguments()[0];
        return null;
    }

    private static bool IsMapType(Type type)
    {
        if (!type.IsGenericType) return false;
        var definition = type.GetGenericTypeDefinition();
        return (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            && type.GetGenericArguments()[0] == typeof(string);
    }

    private static object ToCollection(Type collectionType, Type element, List<object?> items)
    {
        if (collectionType.IsArray)
        {
            var array = Array.CreateInstance(element, items.Count);
            for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
            return array;
        }

        var listType = typeof(List<>).MakeGenericType(element == typeof(object) && items.Count > 0 && items[0] is IDictionary ? typeof(object) : element);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in items) list.Add(item);
        return list;
    }
}
