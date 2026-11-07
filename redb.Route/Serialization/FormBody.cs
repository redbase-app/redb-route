using System.Collections;

namespace redb.Route.Serialization;

/// <summary>
/// The fields of an <c>application/x-www-form-urlencoded</c> body, in order. A name may repeat (checkboxes, a list
/// field), so the form is a list of pairs; <see cref="Get"/> answers the first value of a name and
/// <see cref="GetAll"/> every one. Names compare ordinally, as HTML forms send them.
/// </summary>
public sealed class FormBody : IReadOnlyList<KeyValuePair<string, string>>, ICloneable
{
    private readonly List<KeyValuePair<string, string>> _fields;

    /// <summary>An empty form.</summary>
    public FormBody() => _fields = [];

    /// <summary>A form with the given fields, in order.</summary>
    public FormBody(IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = [];
        foreach (var (name, value) in fields)
            Add(name, value);
    }

    /// <inheritdoc />
    public int Count => _fields.Count;

    /// <inheritdoc />
    public KeyValuePair<string, string> this[int index] => _fields[index];

    /// <summary>Appends a field; a field with the same name may already be there.</summary>
    public FormBody Add(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _fields.Add(new(name, value));
        return this;
    }

    /// <summary>The first value of <paramref name="name"/>, or <c>null</c>.</summary>
    public string? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var (key, value) in _fields)
            if (string.Equals(key, name, StringComparison.Ordinal))
                return value;
        return null;
    }

    /// <summary>Every value of <paramref name="name"/>, in order; empty when the field is absent.</summary>
    public IReadOnlyList<string> GetAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _fields.Where(f => string.Equals(f.Key, name, StringComparison.Ordinal)).Select(f => f.Value).ToArray();
    }

    /// <summary>Whether the form has a field named <paramref name="name"/>.</summary>
    public bool Contains(string name) => Get(name) is not null;

    /// <summary>The fields as a dictionary of the first value of each name (later repeats are left out).</summary>
    public Dictionary<string, string> ToDictionary()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in _fields)
            map.TryAdd(key, value);
        return map;
    }

    /// <summary>A copy with its own field list (names and values are immutable strings).</summary>
    public object Clone() => new FormBody(_fields);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The form in its wire form, UTF-8 (for logs and tests).</summary>
    public override string ToString() => FormUrlEncoding.Encode(this, System.Text.Encoding.UTF8);
}
