using System;
using System.Collections.Generic;
using System.Globalization;

namespace redb.Route.Expressions;

/// <summary>
/// The file facts of a message: the <c>redbFile.*</c> headers a file consumer sets. The names are redb.Route.File's
/// <c>FileHeaders</c>, written out because the core does not reference that package. The extension header holds a
/// leading dot (<c>.xml</c>); the fact is without it, as in Camel.
/// </summary>
internal sealed class FileHeaderFacts(IDictionary<string, object?> headers) : IFileFacts
{
    public string? Name => Text("redbFile.Name");

    public string? NameOnly => Text("redbFile.NameOnly");

    public string? Extension => FileLanguage.WithoutDot(Text("redbFile.Extension"));

    public string? Parent => Text("redbFile.Parent");

    public string? AbsolutePath => Text("redbFile.AbsolutePath");

    public string? RelativePath => Text("redbFile.RelativePath");

    public long? Length => Read("redbFile.Length") switch
    {
        long l => l,
        int i => i,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public DateTimeOffset? LastModified => Read("redbFile.LastModified") switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(d),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
        _ => null,
    };

    private object? Read(string header) => headers.TryGetValue(header, out var value) ? value : null;

    private string? Text(string header) => Read(header) is { } value ? ExpressionResolver.TemplateText(value) : null;
}
