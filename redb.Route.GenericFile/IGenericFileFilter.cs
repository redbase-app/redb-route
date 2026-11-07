namespace redb.Route.GenericFile;

/// <summary>
/// A filter the route author writes in code and names from the URI (<c>filter=#myFilter</c>),
/// resolved from the route registry — Apache Camel's <c>GenericFileFilter</c>. Use it when the
/// decision needs code: a lookup, a partner table, a calendar. For a condition that fits in a
/// string, <c>filterFile</c> and <c>filterDirectory</c> are simpler, and for a path pattern
/// <c>antInclude</c>/<c>antExclude</c> are simpler still.
/// </summary>
public interface IGenericFileFilter
{
    /// <summary>
    /// Decides whether a polled file is taken. Called after the cheap filters (name patterns, path
    /// patterns) and before anything reads or moves the file.
    /// </summary>
    /// <param name="file">The file as the listing found it.</param>
    /// <returns><c>true</c> to poll the file, <c>false</c> to leave it untouched.</returns>
    bool Accept(GenericFileInfo file);

    /// <summary>
    /// Decides whether a subdirectory is walked into at all, while the listing is still running —
    /// so a directory turned down here costs nothing, not even a listing. The default takes every
    /// directory, which is what a filter that only cares about files wants.
    /// </summary>
    /// <param name="fullPath">Full path of the subdirectory.</param>
    /// <param name="relativePath">Its path relative to the directory the endpoint polls.</param>
    /// <returns><c>true</c> to walk into it, <c>false</c> to skip it and everything below.</returns>
    bool AcceptDirectory(string fullPath, string relativePath) => true;
}
