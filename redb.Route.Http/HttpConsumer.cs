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
            if (body is byte[] bytes)
            {
                await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted).ConfigureAwait(false);
            }
            else if (body is Stream stream)
            {
                await stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted).ConfigureAwait(false);
            }
            else if (body is not null)
            {
                await httpContext.Response.WriteAsync(
                    body.ToString() ?? string.Empty, httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
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
