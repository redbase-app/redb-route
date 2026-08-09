using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.As2;

/// <summary>
/// Receives asynchronous MDN receipts a partner posts back to our <c>Receipt-Delivery-Option</c> URL.
/// Parses the MDN, correlates it to the original outgoing message by <c>Original-Message-ID</c> (completing
/// the producer's pending wait), delivers the outcome to the route, and answers 200. Bound to an
/// <c>As2.ReceiveMdn(path)</c> endpoint. See <c>docs/as2/02-DESIGN.md §9</c>.
/// </summary>
internal sealed class As2MdnReceiver : IConsumer
{
    private readonly As2Endpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly As2EndpointOptions _options;
    private readonly IAs2CryptoEngine _engine = new As2CryptoEngine();
    private readonly ILogger? _logger;
    private RouteRegistration? _registration;

    public As2MdnReceiver(As2Endpoint endpoint, IProcessor processor, As2EndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = endpoint.Logger;
    }

    public IEndpoint Endpoint => _endpoint;

    private As2Component Component => (_endpoint.Component as As2Component)
        ?? throw new InvalidOperationException("AS2 endpoint has no As2Component.");

    public async Task Start(CancellationToken ct = default)
    {
        _registration = Component.Server.RegisterRoute(
            _options.Host, _options.Port, _endpoint.Uri.Path, "POST", HandleRequest, _options.UseTls);
        await Component.Server.EnsureStarted(_options.Host, _options.Port, ct).ConfigureAwait(false);
        _logger?.LogInformation("AS2 async-MDN receiver started: {Host}:{Port}{Path}", _options.Host, _options.Port, _endpoint.Uri.Path);
    }

    public async Task Stop(CancellationToken ct = default)
    {
        if (_registration is not null)
        {
            Component.Server.UnregisterRoute(_registration);
            _registration = null;
            await Component.Server.StopIfEmpty(_options.Host, _options.Port, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleRequest(HttpContext http)
    {
        byte[] bodyBytes;
        using (var ms = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
            bodyBytes = ms.ToArray();
        }

        var profile = As2Profile.Resolve(_endpoint.Context, _options);
        var cte = http.Request.Headers.TryGetValue("Content-Transfer-Encoding", out var cteValue) ? cteValue.ToString() : null;

        var result = MdnParser.Parse(http.Request.ContentType, cte, bodyBytes, _engine, profile.PartnerCertificate);

        var micMatch = true;
        if (!string.IsNullOrEmpty(result.OriginalMessageId))
        {
            var expected = Component.Correlation.ExpectedMic(result.OriginalMessageId);
            micMatch = expected is null || (result.ReceivedMic is not null && result.ReceivedMic.Value.Matches(expected.Value));
            Component.Correlation.Complete(result.OriginalMessageId, result);
        }

        if (!result.IsPositive || !micMatch || !result.SignatureValid)
            _logger?.LogWarning("Async MDN concern: original={Original}, disposition={Disposition}, micMatch={MicMatch}, sigValid={SigValid}",
                result.OriginalMessageId, result.Disposition, micMatch, result.SignatureValid);

        // Deliver the MDN outcome to the route (fire-and-forget notification).
        try
        {
            var message = new Message(bodyBytes) { ContentType = http.Request.ContentType };
            if (result.OriginalMessageId is not null) message.Headers[As2Headers.MessageId] = result.OriginalMessageId;
            message.Headers[As2Headers.MdnDisposition] = result.Disposition ?? string.Empty;
            message.Headers[As2Headers.SignatureValid] = result.SignatureValid;
            message.Headers[As2Headers.MdnMicMatch] = micMatch;

            var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
            exchange.Pattern = ExchangePattern.InOnly;
            try { await _processor.Process(exchange, http.RequestAborted).ConfigureAwait(false); }
            finally { await exchange.DisposeAsync().ConfigureAwait(false); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Async MDN route processing failed: original={Original}", result.OriginalMessageId);
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
    }
}
