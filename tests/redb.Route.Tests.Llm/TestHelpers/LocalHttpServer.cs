using System.Net;
using System.Text;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Tiny in-process HTTP server backed by <see cref="HttpListener"/>. Listens
/// on a free localhost port chosen at start time, replies to any request with
/// the body / headers configured by the test, and records every request URL
/// for assertion. Used as a deterministic target for
/// <see cref="redb.Route.Llm.Tools.HttpFetchTool"/> tests so we don't depend on
/// the public internet.
/// <para>
/// HttpListener requires <c>http.sys</c> permission on Windows; binding to
/// <c>127.0.0.1</c> on a high port works without elevation.
/// </para>
/// </summary>
public sealed class LocalHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<string> _requestPaths = new();

    /// <summary>Base URL the server listens on (e.g. <c>http://127.0.0.1:34567/</c>).</summary>
    public string BaseUrl { get; }

    /// <summary>Host portion of <see cref="BaseUrl"/> (e.g. <c>127.0.0.1</c>).</summary>
    public string Host => "127.0.0.1";

    /// <summary>Body returned for every request (defaults to a small JSON document).</summary>
    public string ResponseBody { get; set; } = """{"ok":true,"answer":"42"}""";

    /// <summary>Content-Type for replies.</summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>Status code for replies.</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Snapshot of request URL paths (with query) the server has received.</summary>
    public IReadOnlyList<string> RequestPaths
    {
        get { lock (_requestPaths) return _requestPaths.ToArray(); }
    }

    /// <summary>Starts a server on a free localhost port.</summary>
    public LocalHttpServer()
    {
        var port = PickFreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            lock (_requestPaths) _requestPaths.Add(ctx.Request.Url?.PathAndQuery ?? "");

            ctx.Response.StatusCode = StatusCode;
            ctx.Response.ContentType = ContentType;
            var bytes = Encoding.UTF8.GetBytes(ResponseBody);
            ctx.Response.ContentLength64 = bytes.LongLength;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }

    private static int PickFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _listener.Close();
        _cts.Dispose();
    }
}
