using System.Text.RegularExpressions;

namespace redb.Route.GenericFile;

/// <summary>
/// Shared utilities for file-based transports: glob matching, done-file resolution.
/// </summary>
public static class GenericFileUtils
{
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
    /// Substitutes file-level variables in a pattern.
    /// Supported: <c>${file:name}</c> (full name with extension) and
    /// <c>${file:name.noext}</c> (name without extension).
    /// </summary>
    /// <param name="fileName">File name with extension (e.g. "order.csv").</param>
    /// <param name="pattern">Pattern to substitute into. Returned unchanged when it holds no variables.</param>
    /// <param name="ops">File operations for path helpers.</param>
    /// <returns>The pattern with file variables replaced.</returns>
    /// <remarks>
    /// Only file-level variables are supported. Exchange-level expressions (<c>${header.*}</c>,
    /// <c>${body}</c>) cannot be used here: these patterns are resolved before the exchange
    /// exists, or while it is being disposed.
    /// </remarks>
    public static string SubstituteFileTokens(string fileName, string pattern, IFileOperations ops)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(ops);

        if (pattern.Length == 0 || !pattern.Contains("${", StringComparison.Ordinal))
            return pattern;

        var result = pattern.Replace("${file:name}", fileName, StringComparison.OrdinalIgnoreCase);
        return result.Replace("${file:name.noext}",
            ops.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a done-file name pattern by substituting ${file:name} and ${file:name.noext} variables.
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

        var pattern = SubstituteFileTokens(file.Name, doneFilePattern, ops);

        if (ops.IsAbsolutePath(pattern))
            return pattern;

        var parentDir = ops.GetParentPath(file.FullPath);
        return ops.CombinePath(parentDir, pattern);
    }
}
