using redb.Route.Expressions;
using System.Text.RegularExpressions;

namespace redb.Route.GenericFile;

/// <summary>
/// Shared utilities for file-based transports: glob matching, done-file resolution.
/// </summary>
public static class GenericFileUtils
{
    /// <summary>
    /// Ant-style path matching, as Apache Camel's <c>antInclude</c>/<c>antExclude</c> use it:
    /// <c>?</c> is one character, <c>*</c> matches within a single path segment and stops at a
    /// separator, and <c>**</c> spans any number of segments. Several patterns are separated by
    /// commas, and the path is matched with <c>/</c> as the separator whatever the transport wrote.
    /// <para>
    /// This is deliberately not <see cref="GlobMatch"/> with a longer input: there <c>*</c> becomes
    /// <c>.*</c> and crosses separators, so <c>*/x.csv</c> and <c>**/x.csv</c> would mean the same
    /// thing and a pattern could never name one level.
    /// </para>
    /// </summary>
    /// <param name="relativePath">Path relative to the polled directory (e.g. "TYPE_A/outbox/a.csv").</param>
    /// <param name="pattern">One or more comma-separated Ant patterns.</param>
    /// <param name="caseSensitive">Whether matching respects case (Camel's <c>antFilterCaseSensitive</c>, default true).</param>
    /// <returns>True when any pattern matches the path.</returns>
    public static bool AntMatch(string relativePath, string pattern, bool caseSensitive = true)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(pattern);

        var path = relativePath.Replace('\\', '/').TrimStart('/');

        foreach (var single in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var regex = AntPatternCache.GetOrAdd((single, caseSensitive), static key => BuildAntRegex(key.Pattern, key.CaseSensitive));
            if (regex.IsMatch(path))
                return true;
        }

        return false;
    }

    // One compiled Regex per (pattern, case) pair: a poll runs the same handful of patterns over
    // every file it listed, and a directory filter runs them again per directory.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Pattern, bool CaseSensitive), Regex> AntPatternCache = new();

    private static Regex BuildAntRegex(string pattern, bool caseSensitive)
    {
        var normalized = pattern.Replace('\\', '/').TrimStart('/');
        var sb = new System.Text.StringBuilder("^");

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '*')
            {
                var isDouble = i + 1 < normalized.Length && normalized[i + 1] == '*';
                if (isDouble)
                {
                    i++;
                    // "**/" also matches zero segments, so "**/x.csv" matches "x.csv" at the root.
                    if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        var options = RegexOptions.Compiled | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(sb.ToString(), options);
    }

    /// <summary>
    /// Simple glob matching supporting * and ? wildcards.
    /// Supports comma-separated patterns: "*.csv,*.json".
    /// </summary>
    /// <param name="input">File name to test.</param>
    /// <param name="pattern">Glob pattern (e.g. "*.csv" or "*.csv,*.json").</param>
    /// <returns>True if the input matches the pattern.</returns>
    public static bool GlobMatch(string input, string pattern)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pattern);

        // Support comma-separated patterns: "*.csv,*.json"
        if (pattern.Contains(','))
        {
            return pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Any(p => GlobMatch(input, p));
        }

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Tests whether a already-normalized path is the base directory itself or lives underneath it.
    /// Used by producers to keep a caller-supplied file name from escaping the endpoint directory.
    /// </summary>
    /// <param name="basePath">Base directory. A trailing separator is ignored.</param>
    /// <param name="candidatePath">Candidate path. Must already be normalized (no "..", no "." segments).</param>
    /// <param name="separator">Path separator of the transport ('/' for remote, OS separator for local).</param>
    /// <param name="comparison">Comparison to use. Remote paths are case-sensitive; local ones follow the OS.</param>
    /// <returns>True when the candidate stays inside the base directory.</returns>
    /// <remarks>
    /// The separator is part of the comparison on purpose: a plain prefix test would accept
    /// "/data/out2/x" as living inside "/data/out".
    /// </remarks>
    public static bool IsWithinDirectory(
        string basePath, string candidatePath, char separator, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(candidatePath);

        var root = basePath.TrimEnd(separator);
        if (root.Length == 0)
            return true; // the whole file system is the base

        return candidatePath.Equals(root, comparison)
               || candidatePath.StartsWith(root + separator, comparison);
    }

    /// <summary>
    /// Substitutes the file keys of Apache Camel's file language (<c>${file:name}</c>, <c>${file:name.noext}</c>,
    /// <c>${file:ext}</c>, <c>${file:parent}</c> and the rest of <see cref="FileLanguage.Keys"/>) in a pattern of a
    /// consumer option. Keys match ignoring case, as these options always have (<c>${FILE:NAME}</c>).
    /// </summary>
    /// <param name="facts">The file the option is resolved for (<see cref="GenericFileFacts"/>).</param>
    /// <param name="pattern">Pattern to substitute into. Returned unchanged when it holds no keys.</param>
    /// <returns>The pattern with the file keys replaced.</returns>
    /// <remarks>
    /// Only file keys are substituted. Exchange-level expressions (<c>${header.*}</c>, <c>${body}</c>) cannot be used
    /// here: these patterns are resolved before the exchange exists, or while it is being disposed.
    /// </remarks>
    /// <exception cref="ArgumentException">A key is not a file key; <see cref="ValidateFileKeys"/> reports it earlier.</exception>
    public static string SubstituteFileTokens(IFileFacts facts, string pattern)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(pattern);
        return FileLanguage.Substitute(pattern, facts, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The compatibility form of <see cref="SubstituteFileTokens(IFileFacts, string)"/> for a caller that has only the
    /// file name: the keys that need the path, the size or the time write nothing.
    /// </summary>
    /// <param name="fileName">File name with extension (e.g. "order.csv").</param>
    /// <param name="pattern">Pattern to substitute into. Returned unchanged when it holds no keys.</param>
    /// <param name="ops">File operations for path helpers.</param>
    /// <returns>The pattern with the file keys replaced.</returns>
    public static string SubstituteFileTokens(string fileName, string pattern, IFileOperations ops)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(ops);
        return SubstituteFileTokens(new GenericFileFacts(fileName, "", null, ops), pattern);
    }

    /// <summary>
    /// Checks the file keys in a consumer option when the endpoint is created: a key that is not a file key would
    /// otherwise surface only when the first file arrives (before 4.7.0 it was left in the path as written).
    /// </summary>
    /// <param name="option">The option name, for the message (<c>moveTo</c>).</param>
    /// <param name="pattern">The option value; empty or <c>null</c> passes.</param>
    /// <exception cref="InvalidOperationException">A key is not a file key; the message lists the keys.</exception>
    public static void ValidateFileKeys(string option, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return;
        if (FileLanguage.FindUnknownKey(pattern, StringComparison.OrdinalIgnoreCase) is { } key)
            throw new InvalidOperationException($"{option}='{pattern}': {FileLanguage.UnknownKeyMessage(key)}");
    }

    /// <summary>
    /// Resolves a done-file name pattern by substituting the file keys (<c>${file:name}</c>, <c>${file:name.noext}</c>
    /// and the rest of <see cref="FileLanguage.Keys"/>).
    /// </summary>
    /// <param name="file">The file to resolve the done-file name for.</param>
    /// <param name="doneFilePattern">The pattern (e.g. "${file:name}.done").</param>
    /// <param name="ops">File operations for path helpers.</param>
    /// <returns>Resolved done-file path.</returns>
    public static string ResolveDoneFileName(GenericFileInfo file, string doneFilePattern, IFileOperations ops)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(doneFilePattern);
        ArgumentNullException.ThrowIfNull(ops);

        var pattern = SubstituteFileTokens(new GenericFileFacts(file, file.FullPath, ops), doneFilePattern);

        if (ops.IsAbsolutePath(pattern))
            return pattern;

        var parentDir = ops.GetParentPath(file.FullPath);
        return ops.CombinePath(parentDir, pattern);
    }
}
