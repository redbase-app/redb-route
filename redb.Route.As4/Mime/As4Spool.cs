using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.As4.Mime;

/// <summary>
/// Where the connector spools request bodies and payloads: the engine's stream cache options (threshold, temporary
/// directory), as <c>.StreamCaching()</c> resolves them (<see cref="RouteContextExtensions.GetStreamCacheOptions"/>).
/// </summary>
internal static class As4Spool
{
    /// <summary>The stream cache options of <paramref name="context"/>.</summary>
    public static StreamCacheOptions Options(IRouteContext? context) => context.GetStreamCacheOptions();

    /// <summary>The asynchronous form of <see cref="Run"/>, for a step that reads a foreign stream (the exchange body).</summary>
    public static async Task<Stream> RunAsync(StreamCacheOptions options, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(write);
        var spool = new StreamCache(options);
        try
        {
            await write(spool, ct).ConfigureAwait(false);
            spool.CompleteWriting();
            return spool;
        }
        catch
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs one processing step, <paramref name="write"/>, into a new spool (memory, or a temporary file past the
    /// threshold). The spool is returned readable and rewound; if the step throws, it is disposed and nothing of its
    /// output survives. Decryption relies on that, since GCM authenticates only at the end.
    /// </summary>
    public static Stream Run(StreamCacheOptions options, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(write);
        var spool = new StreamCache(options);
        try
        {
            write(spool);
            spool.CompleteWriting();
            return spool;
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }
}
