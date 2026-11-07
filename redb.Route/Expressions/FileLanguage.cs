using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace redb.Route.Expressions;

/// <summary>What a key of the file language means: one fact about a file.</summary>
public enum FileFact
{
    /// <summary>The file name with its extension.</summary>
    Name,

    /// <summary>The file name without its extension.</summary>
    NameOnly,

    /// <summary>The extension, without the leading dot.</summary>
    Extension,

    /// <summary>The directory that holds the file.</summary>
    Parent,

    /// <summary>The full path of the file.</summary>
    AbsolutePath,

    /// <summary>The path of the file relative to the directory the consumer polls.</summary>
    RelativePath,

    /// <summary>The size in bytes.</summary>
    Length,

    /// <summary>The last write time.</summary>
    LastModified,
}

/// <summary>
/// The facts about one file that the file language reads. Each source of files supplies its own: the expression
/// engine reads the <c>redbFile.*</c> headers of the message, a file consumer the file it is handling, the S3 consumer
/// the object key. A fact the source does not know is <c>null</c>.
/// </summary>
public interface IFileFacts
{
    /// <summary>The file name with its extension.</summary>
    string? Name { get; }

    /// <summary>The file name without its extension.</summary>
    string? NameOnly { get; }

    /// <summary>The extension, without the leading dot (<c>xml</c>).</summary>
    string? Extension { get; }

    /// <summary>The directory that holds the file.</summary>
    string? Parent { get; }

    /// <summary>The full path of the file.</summary>
    string? AbsolutePath { get; }

    /// <summary>The path relative to the polled directory.</summary>
    string? RelativePath { get; }

    /// <summary>The size in bytes.</summary>
    long? Length { get; }

    /// <summary>The last write time.</summary>
    DateTimeOffset? LastModified { get; }
}

/// <summary>
/// Apache Camel's file language: <c>${file:name}</c>, <c>${file:name.noext}</c> and the other keys. One table of keys
/// and one resolver serve the expression engine and the components that substitute file keys in their own options
/// (<c>moveTo</c>, <c>doneFileName</c> and the like), so a key means the same thing in both.
/// <para>
/// The expression engine matches keys with <see cref="StringComparison.Ordinal"/>, as Camel does; component options
/// match with <see cref="StringComparison.OrdinalIgnoreCase"/>, where <c>${FILE:NAME}</c> has always worked.
/// </para>
/// </summary>
public static class FileLanguage
{
    /// <summary>The keys, in the order an error lists them, and the fact each one reads.</summary>
    public static IReadOnlyList<KeyValuePair<string, FileFact>> Keys { get; } =
    [
        new("name", FileFact.Name),
        new("onlyname", FileFact.Name),
        new("name.noext", FileFact.NameOnly),
        new("onlyname.noext", FileFact.NameOnly),
        new("ext", FileFact.Extension),
        new("name.ext", FileFact.Extension),
        new("parent", FileFact.Parent),
        new("path", FileFact.AbsolutePath),
        new("absolute.path", FileFact.AbsolutePath),
        new("relative.path", FileFact.RelativePath),
        new("length", FileFact.Length),
        new("size", FileFact.Length),
        new("modified", FileFact.LastModified),
    ];

    private static readonly Regex OrdinalPlaceholder = new(@"\$\{file:(?<key>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex IgnoreCasePlaceholder = new(@"\$\{(?i:file):(?<key>[^}]*)\}", RegexOptions.Compiled);

    /// <summary>The fact a key reads; <c>false</c> when the key is not one of <see cref="Keys"/>.</summary>
    /// <param name="key">The key as written after <c>file:</c>; surrounding spaces are ignored.</param>
    /// <param name="comparison"><see cref="StringComparison.Ordinal"/> or <see cref="StringComparison.OrdinalIgnoreCase"/>.</param>
    /// <param name="fact">The fact, when the key is known.</param>
    public static bool TryGetFact(string key, StringComparison comparison, out FileFact fact)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckComparison(comparison);
        var trimmed = key.Trim();
        foreach (var (name, meaning) in Keys)
            if (string.Equals(name, trimmed, comparison))
            {
                fact = meaning;
                return true;
            }
        fact = default;
        return false;
    }

    /// <summary>The value of one fact: a string, a <see cref="long"/> length or a <see cref="DateTimeOffset"/>.</summary>
    public static object? Value(FileFact fact, IFileFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return fact switch
        {
            FileFact.Name => facts.Name,
            FileFact.NameOnly => facts.NameOnly,
            FileFact.Extension => facts.Extension,
            FileFact.Parent => facts.Parent,
            FileFact.AbsolutePath => facts.AbsolutePath,
            FileFact.RelativePath => facts.RelativePath,
            FileFact.Length => facts.Length,
            FileFact.LastModified => facts.LastModified,
            _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, null),
        };
    }

    /// <summary>The value a key reads from <paramref name="facts"/>.</summary>
    /// <exception cref="ArgumentException">The key is not one of <see cref="Keys"/>; the message lists them.</exception>
    public static object? Resolve(string key, IFileFacts facts, StringComparison comparison)
        => TryGetFact(key, comparison, out var fact)
            ? Value(fact, facts)
            : throw new ArgumentException(UnknownKeyMessage(key), nameof(key));

    /// <summary>
    /// Replaces every <c>${file:key}</c> in <paramref name="pattern"/> with the value it reads, written as a template
    /// writes it (numbers and dates culture-invariant); a fact the source does not know writes nothing.
    /// </summary>
    /// <exception cref="ArgumentException">A key is not one of <see cref="Keys"/>.</exception>
    public static string Substitute(string pattern, IFileFacts facts, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(facts);
        if (!pattern.Contains("${", StringComparison.Ordinal))
            return pattern;
        return Placeholder(comparison).Replace(pattern,
            m => ExpressionResolver.TemplateText(Resolve(m.Groups["key"].Value, facts, comparison)));
    }

    /// <summary>
    /// The first key in <paramref name="pattern"/> that is not one of <see cref="Keys"/>, or <c>null</c> when every
    /// <c>${file:key}</c> in it is known. A component calls it when its endpoint is created.
    /// </summary>
    public static string? FindUnknownKey(string pattern, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!pattern.Contains("${", StringComparison.Ordinal))
            return null;
        return Placeholder(comparison).Matches(pattern)
            .Select(m => m.Groups["key"].Value)
            .FirstOrDefault(key => !TryGetFact(key, comparison, out _));
    }

    /// <summary>An extension as the file language gives it: without the leading dot (<c>.xml</c> is <c>xml</c>).</summary>
    public static string? WithoutDot(string? extension)
        => extension is not null && extension.StartsWith('.') ? extension[1..] : extension;

    /// <summary>The words of the error for a key that is not one of <see cref="Keys"/>.</summary>
    public static string UnknownKeyMessage(string key)
        => $"'{key.Trim()}' is not a file key; the keys are {string.Join(", ", Keys.Select(k => k.Key))}.";

    private static Regex Placeholder(StringComparison comparison)
    {
        CheckComparison(comparison);
        return comparison == StringComparison.Ordinal ? OrdinalPlaceholder : IgnoreCasePlaceholder;
    }

    private static void CheckComparison(StringComparison comparison)
    {
        if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(comparison), comparison,
                "The file language compares keys with Ordinal (the expression engine) or OrdinalIgnoreCase (component options).");
    }
}
