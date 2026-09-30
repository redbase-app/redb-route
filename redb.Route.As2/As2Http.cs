using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.As2;

/// <summary>Request-body handling shared by the message and the MDN receiver.</summary>
internal static class As2Http
{
    /// <summary>
    /// Spools the request body after <paramref name="prefix"/> (the core stream cache: memory, or a temporary file past
    /// the threshold of <paramref name="spool"/>), refusing more than <paramref name="limit"/> bytes. The limit is set on
    /// the request itself (the listener's own limit belongs to whichever route registered the port first) and enforced
    /// while reading, so a body that is not declared up front is cut at the limit too. Returns the spool rewound and
    /// readable, or null after answering 413 when the body is too large.
    /// </summary>
    public static async Task<StreamCache?> ReadBodyAsync(HttpContext http, long limit, byte[]? prefix, StreamCacheOptions spool, CancellationToken ct)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limit;

        if (http.Request.ContentLength is { } declared && declared > limit)
            return TooLarge(http);

        var buffer = new StreamCache(spool);
        var complete = false;
        try
        {
            if (prefix is not null) await buffer.WriteAsync(prefix, ct).ConfigureAwait(false);
            long read = 0;
            var chunk = new byte[81920];
            int n;
            while ((n = await http.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                read += n;
                if (read > limit)
                    return TooLarge(http);
                await buffer.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
            }
            buffer.CompleteWriting();
            buffer.Position = 0;
            complete = true;
            return buffer;
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(http);
        }
        finally
        {
            if (!complete) await buffer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static StreamCache? TooLarge(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return null;
    }
}
