using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using System.Linq;

namespace redb.Route.Http;

/// <summary>
/// HTTP consumer. Uses SharedHttpServerManager to register routes on a shared
/// Kestrel server instance per (host, port). Multiple consumers can serve
/// different routes on the same port.
/// <para>Supports configurable bind address, CORS, allowed methods, HTTPS/TLS,
/// request body streaming, and InOut exchange patterns for synchronous responses.</para>
/// </summary>
public class HttpConsumer : IConsumer
{
    private readonly HttpEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly HttpEndpointOptions _options;
    private readonly SharedHttpServerManager _serverManager;
    private ILogger? _logger;
    private RouteRegistration? _registration;
    private long _processedCount;

    /// <summary>Creates an HTTP consumer.</summary>
    public HttpConsumer(HttpEndpoint endpoint, IProcessor processor, HttpEndpointOptions options, SharedHttpServerManager serverManager)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serverManager = serverManager ?? throw new ArgumentNullException(nameof(serverManager));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <summary>Number of requests successfully processed.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>The base URL the server is listening on. Available after Start().</summary>
    public string? BaseUrl { get; private set; }

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        var host = _options.Host;
        var port = _options.Port;
        var path = _endpoint.ConsumerPath;
        var ssl = _options.Ssl;

        // Translate the per-endpoint CORS knobs into a per-route CORS policy. The shared server
        // installs ONE CORS dispatch middleware per (host, port) which selects the policy by
        // the matched route, so different endpoints on the same port may declare different
        // policies (resolves the legacy "first-wins" gotcha; see SharedHttpServerManager).
        RouteCorsOptions? corsOptions = null;
        if (_options.Cors)
        {
            // Validation in HttpEndpointOptions.Validate guarantees that at least one of
            // CorsOrigins / CorsOriginsResolver is supplied here -- no implicit "*" fallback.
            corsOptions = new RouteCorsOptions(
                AllowedOrigins: _options.CorsOrigins,
                AllowedMethods: _options.Methods,
                AllowCredentials: _options.CorsCredentials,
                OriginsResolver: _options.CorsOriginsResolver);
        }

        _registration = _serverManager.RegisterRoute(
            host, port, path, _options.Methods, HandleRequest,
            ssl, _options.SslCertPath, _options.SslCertPassword,
            corsOptions, _options.MaxRequestBodySize, _options.Protocol);

        await _serverManager.EnsureStarted(host, port, ct).ConfigureAwait(false);

        BaseUrl = _serverManager.GetBaseUrl(host, port);

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("HTTP consumer started: {Url}", BaseUrl + path);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_registration is not null)
        {
            var host = _options.Host;
            var port = _options.Port;

            _serverManager.UnregisterRoute(_registration);
            _registration = null;

            await _serverManager.StopIfEmpty(host, port, ct).ConfigureAwait(false);
        }
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("HTTP consumer stopped: {Host}:{Port}", _options.Host, _options.Port);
    }

    private async Task HandleRequest(HttpContext httpContext)
    {
        // Build exchange from HTTP request
        var exchange = await BuildExchange(httpContext).ConfigureAwait(false);

        // Set exchange pattern
        exchange.Pattern = _options.InOut ? ExchangePattern.InOut : ExchangePattern.InOnly;

        try
        {
            // Process through the route pipeline
            try
            {
                await _processor.Process(exchange, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "HTTP request processing failed: {Method} {Path}",
                    httpContext.Request.Method, httpContext.Request.Path);
                exchange.Exception ??= ex;
            }

            // Check for exceptions
            if (exchange.Exception is not null && !exchange.ExceptionHandled)
            {
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await httpContext.Response.WriteAsync(
                    exchange.Exception.Message, httpContext.RequestAborted).ConfigureAwait(false);
                return;
            }

            // Build HTTP response from exchange
            try
            {
                await WriteResponse(httpContext, exchange).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex,
                    "HTTP response writing failed: {Method} {Path}, route={RouteId}, statusStarted={Started}",
                    httpContext.Request.Method, httpContext.Request.Path, exchange.RouteId, httpContext.Response.HasStarted);
                throw;
            }

            Interlocked.Increment(ref _processedCount);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<Exchange> BuildExchange(HttpContext httpContext)
    {
        var request = httpContext.Request;

        // Read request body
        object? body = null;
        if (request.ContentLength > 0 || request.ContentType is not null)
        {
            if (_options.StreamRequest)
            {
                // Stream passthrough — body stays as HttpRequest.Body stream.
                // Exchange.DisposeAsync does NOT own this stream (Kestrel does),
                // but the stream remains valid until WriteResponse completes.
                body = request.Body;
            }
            else
            {
                using var ms = new MemoryStream();
                await request.Body.CopyToAsync(ms, httpContext.RequestAborted).ConfigureAwait(false);
                body = ms.ToArray();
            }
        }

        var message = new Message(body);

        // Set ContentType from HTTP request
        if (!string.IsNullOrEmpty(request.ContentType))
            message.ContentType = request.ContentType;

        // Map HTTP request headers to exchange headers
        message.Headers[HttpHeaders.Method] = request.Method;
        message.Headers[HttpHeaders.Path] = request.Path.Value ?? "/";
        message.Headers[HttpHeaders.Url] = $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}";
        message.Headers[HttpHeaders.Port] = _options.Port;

        if (request.QueryString.HasValue)
        {
            message.Headers[HttpHeaders.Query] = request.QueryString.Value?.TrimStart('?') ?? string.Empty;

            // Extract individual query parameters as redbHttp.QueryParam.*
            foreach (var qp in request.Query)
            {
                message.Headers[$"{HttpHeaders.QueryParamPrefix}{qp.Key}"] = qp.Value.Count switch
                {
                    0 => string.Empty,
                    1 => (object)qp.Value[0]!,
                    _ => string.Join(",", qp.Value!)
                };
            }
        }

        // Extract route template parameters as redbHttp.RouteParam.*
        if (httpContext.Items.TryGetValue("__redbRouteValues", out var rvObj) && rvObj is RouteValueDictionary routeValues)
        {
            foreach (var (key, value) in routeValues)
            {
                if (value is not null)
                    message.Headers[$"{HttpHeaders.RouteParamPrefix}{key}"] = value;
            }
        }

        if (httpContext.Connection.RemoteIpAddress is not null)
        {
            message.Headers[HttpHeaders.RemoteAddress] = httpContext.Connection.RemoteIpAddress.ToString();
        }

        // Copy all HTTP headers to exchange, and remember which ones came from
        // the request so they are not accidentally echoed in the response.
        var requestHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            // Skip pseudo-headers
            if (header.Key.StartsWith(':')) continue;

            // Transport-owned headers are set above from the connection itself, and processors trust
            // them: redbHttp.RemoteAddress drives rate limiting, brute-force lockout and audit. This copy
            // runs AFTER those assignments, so without the guard a caller sending a header literally
            // named "redbHttp.RemoteAddress" (a valid HTTP token) would overwrite the socket address and
            // pick its own throttle bucket.
            if (HttpHeaders.IsRedbHeader(header.Key)) continue;

            requestHeaderNames.Add(header.Key);
            message.Headers[header.Key] = header.Value.Count switch
            {
                0 => string.Empty,
                1 => (object)header.Value[0]!,
                _ => string.Join(", ", header.Value!)
            };
        }

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Properties["redbHttp.RequestHeaderNames"] = requestHeaderNames;
        return exchange;
    }

    private async Task WriteResponse(Microsoft.AspNetCore.Http.HttpContext httpContext, IExchange exchange)
    {
        // Pick the response message: Out if set, otherwise In (pipeline merges Out→In)
        var responseMsg = exchange.HasOut ? exchange.Out! : exchange.In;

        // Determine status code: header override > fallback > options default
        var statusCode = _options.ResponseCode;
        if (responseMsg.Headers.TryGetValue(HttpHeaders.ResponseCode, out var rc))
        {
            if (rc is int code) statusCode = code;
            else if (rc is string codeStr && int.TryParse(codeStr, out var parsed)) statusCode = parsed;
        }
        else if (responseMsg.Headers.TryGetValue("status.code", out var sc))
        {
            // Fallback: controller dispatchers set "status.code" for transport-agnostic status
            if (sc is int code2) statusCode = code2;
            else if (sc is string scStr && int.TryParse(scStr, out var parsed2)) statusCode = parsed2;
        }

        httpContext.Response.StatusCode = statusCode;

        // Set Content-Type
        var responseContentType = responseMsg.ContentType ?? _options.ContentType;
        if (responseMsg.Headers.TryGetValue(HttpHeaders.ResponseContentType, out var rct) && rct is string rctStr)
        {
            responseContentType = rctStr;
        }

        // InOut pattern — propagate headers and (optionally) body.
        // NOTE: headers MUST be copied even when Body is null. Otherwise body-less
        // responses (HTTP 302 redirects, 204 No Content, Set-Cookie-only replies)
        // silently drop Location / Set-Cookie / etc. See repo memory
        // 'http-redirect-headers-bug' for the symptom history.
        if (_options.InOut)
        {
            httpContext.Response.ContentType = responseContentType;

            // Copy response headers (always, regardless of body presence).
            // When the response message is exchange.In, it still contains the
            // original request headers. Never echo those back.
            var requestHeaderNames =
                exchange.Properties.TryGetValue("redbHttp.RequestHeaderNames", out var namesObj)
                && namesObj is HashSet<string> names
                    ? names
                    : null;

            foreach (var (key, value) in responseMsg.Headers)
            {
                if (value is null) continue;
                if (requestHeaderNames?.Contains(key) == true) continue;
                if (HttpHeaders.NonBridgedHeaders.Contains(key)) continue;
                if (HttpHeaders.IsRedbHeader(key)) continue;
                if (IsInternalHeader(key)) continue;

                // Headers like Set-Cookie can carry multiple values. Use StringValues so
                // ASP.NET emits one header line per entry instead of stringifying the array.
                Microsoft.Extensions.Primitives.StringValues sv = value switch
                {
                    string s => s,
                    string[] arr => arr,
                    System.Collections.IEnumerable seq when value is not string =>
                        seq.Cast<object?>().Select(o => o?.ToString() ?? string.Empty).ToArray(),
                    _ => value.ToString()
                };
                if (ContainsInvalidHeaderValueCharacters(sv)) continue;

                httpContext.Response.Headers.TryAdd(key, sv);
            }

            var body = responseMsg.Body;
            // RFC 7230 §3.3.3 / RFC 9112 §6.1: 1xx, 204 and 304 responses MUST NOT contain a
            // message body, and Kestrel hard-throws on Body.WriteAsync for those status codes
            // (Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpProtocol.FirstWriteAsyncInternal:
            //  "Writing to the response body is invalid for responses with status code <N>").
            // Skip the write entirely — the header copy above already propagated whatever
            // Location/ETag/Set-Cookie the producer wanted to surface, which is the only
            // legitimate payload for these status families. Silently dropping a non-empty
            // body is safer than letting Kestrel kill the response mid-flight: a producer
            // that sets a body for a 204 has a bug to fix, but their downstream client gets
            // a clean RFC-shaped response instead of a torn TCP connection.
            var statusForbidsBody =
                statusCode == 204 ||
                statusCode == 304 ||
                (statusCode >= 100 && statusCode < 200);
            if (statusForbidsBody)
            {
                // Intentional no-op — see note above. We don't even check body.Length: even a
                // zero-length WriteAsync trips FirstWriteAsyncInternal's status guard.
            }
            else if (body is byte[] bytes)
            {
                await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted).ConfigureAwait(false);
            }
            else if (body is Stream stream)
            {
                await stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted).ConfigureAwait(false);
            }
            else if (body is IAsyncEnumerable<string> asyncStrings)
            {
                // Streaming body: per-yield flush, no transport buffering.
                //   - text/event-stream → SSE framing (data: ...\n\n + final 'event: done').
                //   - everything else    → chunked plain text (each yield = one chunk).
                // The framing choice is driven by the response Content-Type, which is the
                // canonical signal carried end-to-end (HTTP Accept ↔ Content-Type), rather
                // than a private header. Producers that opt-in to streaming should set
                // ContentType="text/event-stream" on Out (LlmProducer does this for
                // ?stream=true).
                var useSse = responseContentType is not null
                    && responseContentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

                // Hint to nginx / cloud LB to not buffer the response. Cache-Control
                // 'no-transform' also blocks gzip middleware from buffering for compression.
                httpContext.Response.Headers["Cache-Control"] = "no-cache, no-transform";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";

                // Disable ASP.NET response buffering so per-chunk WriteAsync hits the
                // socket immediately. Safe to call after headers are set but before
                // the first body byte is written.
                httpContext.Features
                    .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()
                    ?.DisableBuffering();

                if (useSse)
                    await WriteSseStreamAsync(httpContext, responseMsg, asyncStrings, httpContext.RequestAborted).ConfigureAwait(false);
                else
                    await WriteChunkedTextStreamAsync(httpContext, asyncStrings, httpContext.RequestAborted).ConfigureAwait(false);
            }
            else if (body is not null)
            {
                await httpContext.Response.WriteAsync(
                    body.ToString() ?? string.Empty, httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Pumps an <see cref="IAsyncEnumerable{T}"/> of text chunks to the response
    /// as plain chunked-transfer (one yield = one chunk). Caller is responsible
    /// for setting Content-Type and disabling buffering.
    /// </summary>
    private static async Task WriteChunkedTextStreamAsync(
        Microsoft.AspNetCore.Http.HttpContext httpContext,
        IAsyncEnumerable<string> source,
        CancellationToken ct)
    {
        await foreach (var chunk in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(chunk)) continue;
            await httpContext.Response.WriteAsync(chunk, ct).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pumps an <see cref="IAsyncEnumerable{T}"/> of text chunks to the response
    /// as Server-Sent Events. Each yield becomes one <c>data:</c> frame; a final
    /// <c>event: done</c> carries late-bound message headers
    /// (<c>llm.tokens.in</c>, <c>llm.tokens.out</c>, <c>llm.stop_reason</c>, …)
    /// that the producer writes only after the stream completes.
    /// </summary>
    private static async Task WriteSseStreamAsync(
        Microsoft.AspNetCore.Http.HttpContext httpContext,
        IMessage responseMsg,
        IAsyncEnumerable<string> source,
        CancellationToken ct)
    {
        await foreach (var chunk in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(chunk)) continue;
            // SSE spec: each line of the payload needs its own 'data: ' prefix;
            // a blank line terminates the event.
            var startIdx = 0;
            for (var i = 0; i < chunk.Length; i++)
            {
                if (chunk[i] != '\n') continue;
                var line = chunk.Substring(startIdx, i - startIdx);
                await httpContext.Response.WriteAsync("data: " + line + "\n", ct).ConfigureAwait(false);
                startIdx = i + 1;
            }
            if (startIdx < chunk.Length)
            {
                await httpContext.Response.WriteAsync(
                    "data: " + chunk.Substring(startIdx) + "\n", ct).ConfigureAwait(false);
            }
            await httpContext.Response.WriteAsync("\n", ct).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }

        // Final 'done' event — serialises late-bound stream-summary headers
        // (LlmHeaders.TokensIn/Out, StopReason, ...) that the producer set after
        // the IAsyncEnumerable finished iterating.
        var trailerJson = BuildSseTrailerJson(responseMsg);
        if (trailerJson is not null)
        {
            await httpContext.Response.WriteAsync("event: done\ndata: " + trailerJson + "\n\n", ct).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the JSON payload for the SSE <c>event: done</c> trailer. Picks up
    /// streaming-summary headers (anything under the <c>llm.</c> namespace plus
    /// the standard <c>llm.tokens.*</c> / <c>llm.stop_reason</c>). Returns
    /// <c>null</c> when there's nothing useful to send.
    /// </summary>
    private static string? BuildSseTrailerJson(IMessage responseMsg)
    {
        var keys = new[]
        {
            "llm.tokens.in", "llm.tokens.out", "llm.cost.usd",
            "llm.stop_reason", "llm.tool.iterations",
            "llm.model.id", "llm.provider.id"
        };
        Dictionary<string, object?>? payload = null;
        foreach (var k in keys)
        {
            if (!responseMsg.Headers.TryGetValue(k, out var v) || v is null) continue;
            payload ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            payload[k] = v is string ? v : v.ToString();
        }
        return payload is null ? null : System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private static bool IsInternalHeader(string key)
    {
        return key.StartsWith("redb", StringComparison.OrdinalIgnoreCase)
               || key.StartsWith("Camel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsInvalidHeaderValueCharacters(Microsoft.Extensions.Primitives.StringValues values)
    {
        foreach (var value in values)
        {
            if (value is null) continue;
            foreach (var ch in value)
            {
                // Kestrel rejects control chars and non-ASCII in raw header values.
                if (ch < 0x20 || ch > 0x7E)
                    return true;
            }
        }

        return false;
    }

}
