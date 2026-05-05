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

        var pattern = doneFilePattern;
        pattern = pattern.Replace("${file:name}", file.Name, StringComparison.OrdinalIgnoreCase);
        pattern = pattern.Replace("${file:name.noext}",
            ops.GetFileNameWithoutExtension(file.Name), StringComparison.OrdinalIgnoreCase);

        if (ops.IsAbsolutePath(pattern))
            return pattern;

        var parentDir = ops.GetParentPath(file.FullPath);
        return ops.CombinePath(parentDir, pattern);
    }
}
