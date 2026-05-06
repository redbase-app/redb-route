using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using SysHttpMethod = System.Net.Http.HttpMethod;

namespace redb.Route.Http;

/// <summary>
/// HTTP producer. Sends HTTP requests using <see cref="System.Net.Http.HttpClient"/>.
/// Supports all HTTP methods, header bridging, Basic/Bearer authentication,
/// configurable timeouts, and automatic error handling.
/// </summary>
public class HttpProducer : ConnectableProducer
{
    private readonly HttpEndpoint _endpoint;
    private readonly HttpEndpointOptions _options;
    private HttpClient? _httpClient;
    private HttpClientHandler? _handler;

    /// <summary>Creates an HTTP producer.</summary>
    public HttpProducer(HttpEndpoint endpoint, HttpEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"http:{_endpoint.BuildProducerUrl()}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct)
    {
        _handler = new HttpClientHandler
        {
            AllowAutoRedirect = _options.FollowRedirects,
            MaxAutomaticRedirections = _options.MaxRedirects
        };

        _httpClient = new HttpClient(_handler)
        {
            Timeout = _options.Timeout > 0
                ? TimeSpan.FromMilliseconds(_options.Timeout)
                : System.Threading.Timeout.InfiniteTimeSpan
        };

        // Set default auth header
        ConfigureAuthentication(_httpClient);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task DisconnectAsync(CancellationToken ct)
    {
        _httpClient?.Dispose();
        _httpClient = null;
        _handler?.Dispose();
        _handler = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        var url = ResolveUrl(exchange);
        var method = ResolveMethod(exchange);

        using var request = new HttpRequestMessage(method, url);

        // Set body for methods that support it
        if (HasBody(method))
        {
            SetRequestBody(request, exchange);
        }

        // Bridge exchange headers to HTTP headers
        if (_options.BridgeHeaders)
        {
            BridgeExchangeHeaders(request, exchange);
        }

        // Set per-request auth if using dynamic token
        ConfigurePerRequestAuth(request, exchange);

        HttpResponseMessage response;
        try
        {
            // Send request
            response = await _httpClient!.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogError(ex, "HTTP {Method} to {Url} failed. Timeout={Timeout}ms, auth={Auth}",
                method.Method, url, _options.Timeout, _options.AuthScheme);
            throw;
        }

        // When StreamResponse is enabled, the response stream is set as exchange body.
        // Disposing the HttpResponseMessage would close that stream, so we skip Dispose()
        // and let Exchange.DisposeAsync() close the body stream instead (equivalent effect).
        if (_options.StreamResponse)
        {
            await MapResponse(exchange, response, ct).ConfigureAwait(false);

            if (_options.ThrowOnError && !response.IsSuccessStatusCode)
            {
                response.Dispose();
                var body = exchange.HasOut ? exchange.Out!.Body?.ToString() : null;
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                    inner: null,
                    statusCode: response.StatusCode);
            }
        }
        else
        {
            using (response)
            {
                // Map response back to exchange
                await MapResponse(exchange, response, ct).ConfigureAwait(false);

                // Throw on error if configured
                if (_options.ThrowOnError && !response.IsSuccessStatusCode)
                {
                    var body = exchange.HasOut ? exchange.Out!.Body?.ToString() : null;
                    throw new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                        inner: null,
                        statusCode: response.StatusCode);
                }
            }
        }
    }

    private string ResolveUrl(IExchange exchange)
    {
        // Base URL from endpoint — ${...} expressions resolved per message
        var baseUrl = _options.ResolveOption(_endpoint.BuildProducerUrl(), exchange)
                      ?? _endpoint.BuildProducerUrl();

        // Resolve {name} named parameters from .Param() bindings
        baseUrl = ResolveNamedParams(baseUrl, exchange);

        // Check for query string parameters in exchange headers
        if (exchange.In.Headers.TryGetValue(HttpHeaders.Query, out var query) && query is string qs && qs.Length > 0)
        {
            var separator = baseUrl.Contains('?') ? "&" : "?";
            return $"{baseUrl}{separator}{qs}";
        }

        return baseUrl;
    }

    private string ResolveNamedParams(string url, IExchange exchange)
    {
        var parameters = _options.ExplicitParameters;
        if (parameters.Count == 0) return url;

        foreach (var (name, valueTemplate) in parameters)
        {
            var placeholder = $"{{{name}}}";
            if (!url.Contains(placeholder, StringComparison.Ordinal)) continue;

            // Resolve ${...} expressions inside the param value first
            var resolved = valueTemplate.Contains("${")
                ? (_options.ResolveOption(valueTemplate, exchange) ?? valueTemplate)
                : valueTemplate;

            url = url.Replace(placeholder, Uri.EscapeDataString(resolved), StringComparison.Ordinal);
        }

        return url;
    }

    private SysHttpMethod ResolveMethod(IExchange exchange)
    {
        // Allow override from exchange header
        if (exchange.In.Headers.TryGetValue(HttpHeaders.Method, out var hm) && hm is string methodStr)
        {
            return new SysHttpMethod(methodStr.ToUpperInvariant());
        }

        return _options.Method switch
        {
            HttpMethod.GET => SysHttpMethod.Get,
            HttpMethod.POST => SysHttpMethod.Post,
            HttpMethod.PUT => SysHttpMethod.Put,
            HttpMethod.DELETE => SysHttpMethod.Delete,
            HttpMethod.PATCH => SysHttpMethod.Patch,
            HttpMethod.HEAD => SysHttpMethod.Head,
            HttpMethod.OPTIONS => SysHttpMethod.Options,
            _ => SysHttpMethod.Get
        };
    }

    private static bool HasBody(SysHttpMethod method)
    {
        return method == SysHttpMethod.Post ||
               method == SysHttpMethod.Put ||
               method == SysHttpMethod.Patch;
    }

    private void SetRequestBody(HttpRequestMessage request, IExchange exchange)
    {
        var body = exchange.HasOut ? exchange.Out!.Body : exchange.In.Body;
        if (body is null) return;

        HttpContent content;

        if (body is byte[] bytes)
        {
            content = new ByteArrayContent(bytes);
        }
        else if (body is Stream stream)
        {
            content = new StreamContent(stream);
        }
        else
        {
            var text = body.ToString() ?? string.Empty;
            content = new StringContent(text, Encoding.UTF8);
        }

        // Set content type
        var contentType = exchange.In.ContentType ?? _options.ContentType;
        if (exchange.In.Headers.TryGetValue(HttpHeaders.ContentType, out var ct) && ct is string ctStr)
        {
            contentType = ctStr;
        }

        if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
        {
            content.Headers.ContentType = mediaType;
        }

        request.Content = content;
    }

    private static void BridgeExchangeHeaders(HttpRequestMessage request, IExchange exchange)
    {
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (value is null) continue;
            if (HttpHeaders.NonBridgedHeaders.Contains(key)) continue;

            var strValue = value.ToString();
            if (string.IsNullOrEmpty(strValue)) continue;

            // Try to add as request header (some headers may already be set)
            if (!request.Headers.TryAddWithoutValidation(key, strValue))
            {
                // Try content headers if request has content
                request.Content?.Headers.TryAddWithoutValidation(key, strValue);
            }
        }
    }

    private void ConfigureAuthentication(HttpClient client)
    {
        switch (_options.AuthScheme)
        {
            case HttpAuthScheme.Basic:
                var credentials = Convert.ToBase64String(
                    Encoding.ASCII.GetBytes($"{_options.Username}:{_options.Password}"));
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials);
                break;

            case HttpAuthScheme.Bearer when _options.AuthToken.HasValue && !_options.AuthToken.Value.IsDynamic:
                // Static token — set once on client
                var token = _options.AuthToken.Value.Resolve(null!);
                if (!string.IsNullOrEmpty(token))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                }
                break;
        }
    }

    private void ConfigurePerRequestAuth(HttpRequestMessage request, IExchange exchange)
    {
        // Dynamic bearer token — resolve per-request
        if (_options.AuthScheme == HttpAuthScheme.Bearer &&
            _options.AuthToken.HasValue && _options.AuthToken.Value.IsDynamic)
        {
            var token = _options.AuthToken.Value.Resolve(exchange);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }

    private async Task MapResponse(IExchange exchange, HttpResponseMessage response, CancellationToken ct)
    {
        // Read response body
        object? responseBody;
        if (_options.StreamResponse)
        {
            responseBody = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            responseBody = bytes.Length > 0 ? bytes : null;
        }

        // Set Out message with response body
        var outMessage = new Message(responseBody);
        exchange.Out = outMessage;

        // Set standard response headers
        outMessage.Headers[HttpHeaders.StatusCode] = (int)response.StatusCode;
        outMessage.Headers[HttpHeaders.StatusText] = response.ReasonPhrase ?? string.Empty;
        outMessage.Headers[HttpHeaders.Url] = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

        // Copy response headers to exchange
        if (_options.CopyResponseHeaders)
        {
            foreach (var header in response.Headers)
            {
                outMessage.Headers[header.Key] = string.Join(", ", header.Value);
            }

            if (response.Content.Headers.ContentType is not null)
            {
                outMessage.Headers[HttpHeaders.ContentType] = response.Content.Headers.ContentType.ToString();
                outMessage.ContentType = response.Content.Headers.ContentType.MediaType;
            }

            if (response.Content.Headers.ContentLength is not null)
            {
                outMessage.Headers[HttpHeaders.ContentLength] = response.Content.Headers.ContentLength.Value;
            }
        }
    }
}
