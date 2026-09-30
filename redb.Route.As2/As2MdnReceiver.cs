using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.As2.Crypto;
using redb.Route.As2.Mdn;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Http;
using redb.Route.Telemetry;

namespace redb.Route.As2;

/// <summary>
/// Receives asynchronous MDN receipts a partner posts back to our <c>Receipt-Delivery-Option</c> URL.
/// Parses the MDN, compares its MIC with the one recorded for <c>Original-Message-ID</c>, applies the agreement's
/// signed-MDN policy, delivers the verdict to the route (<see cref="As2Headers.MdnConfirmed"/>), and answers 200. Bound to an
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

    // Unregistering the route stops new receipts; one already in the pipeline finishes before Stop returns.
    private readonly InflightDrainGuard _drain = new();
    private As2Profile? _profile;

    public async Task Start(CancellationToken ct = default)
    {
        // Resolved once, as the message receiver does: a misnamed agreement stops the route from starting.
        _profile = As2Profile.Resolve(_endpoint.Context, _options, _endpoint.Uri.Path);

        // Same as the message receiver: TLS without a certificate used to open a plaintext port.
        var certPath = _options.SslCertPath;
        var certPassword = _options.SslCertPassword;
        if (string.IsNullOrEmpty(certPath) && !string.IsNullOrEmpty(_options.ConnectionFactory))
        {
            var factory = _endpoint.Context.GetRequiredFromRegistry<As2ConnectionFactory>(_options.ConnectionFactory);
            certPath = factory.SslCertPath;
            certPassword = factory.SslCertPassword;
        }

        _drain.Start(ct);
        _registration = Component.Server.RegisterRoute(
            _options.Host, _options.Port, _endpoint.Uri.Path, "POST", HandleRequest,
            _options.UseTls, certPath, certPassword,
            maxRequestBodySize: _options.MaxRequestBodySize,
            concurrencyLimit: ConcurrencyLimitOptions.FromEndpoint(
                _options.MaxConcurrentRequests, _options.RequestQueueLimit,
                _options.RejectStatusCode, _options.RetryAfterSeconds,
                onRejected: _endpoint.RecordRejected));
        await Component.Server.EnsureStarted(_options.Host, _options.Port, ct).ConfigureAwait(false);
        _logger?.LogInformation("AS2 async-MDN receiver started: {Host}:{Port}{Path}", _options.Host, _options.Port, _endpoint.Uri.Path);
    }

    public async Task Stop(CancellationToken ct = default)
    {
        if (_registration is not null)
        {
            Component.Server.UnregisterRoute(_registration);
            _registration = null;
            await _drain.DrainAsync(ct, _logger, $"as2://{_options.Host}:{_options.Port}{_endpoint.Uri.Path}").ConfigureAwait(false);
            await Component.Server.StopIfEmpty(_options.Host, _options.Port, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleRequest(HttpContext http)
    {
        _drain.Increment();
        try
        {
            await HandleRequestCore(http).ConfigureAwait(false);
        }
        finally
        {
            _drain.Decrement();
        }
    }

    private async Task HandleRequestCore(HttpContext http)
    {
        using var span = As2Consumer.StartReceiveSpan(_endpoint, http.Request);
        var reference = http.TraceIdentifier;

        byte[] bodyBytes;
        await using (var body = await As2Http.ReadBodyAsync(http, _options.MaxRequestBodySize, null, _endpoint.Context.GetStreamCacheOptions(), http.RequestAborted)
            .ConfigureAwait(false))
        {
            if (body is null)
            {
                _logger?.LogWarning("AS2 asynchronous MDN refused: its body is over maxRequestBodySize {Limit} bytes (ref {Reference}).",
                    _options.MaxRequestBodySize, reference);
                _endpoint.RecordError();
                span.Activity?.SetStatus(ActivityStatusCode.Error, "request body over maxRequestBodySize");
                return;
            }
            // An MDN is a few kilobytes: its bytes are the exchange body.
            bodyBytes = new byte[body.Length];
            body.ReadExactly(bodyBytes);
        }

        var profile = _profile!;
        var cte = http.Request.Headers.TryGetValue("Content-Transfer-Encoding", out var cteValue) ? cteValue.ToString() : null;

        MdnParser.MdnResult result;
        try
        {
            result = MdnParser.Parse(http.Request.ContentType, cte, bodyBytes, _engine, profile.PartnerCertificate);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger?.LogWarning(e, "AS2 asynchronous MDN refused: it is not a readable MDN (ref {Reference}).", reference);
            _endpoint.RecordError(e);
            span.Activity.RecordFailure(e);
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // The MDN names the message it reports on; without one, or for one nobody waits for, its MIC cannot be compared.
        var originalId = string.IsNullOrEmpty(result.OriginalMessageId) ? null : result.OriginalMessageId;
        var expected = originalId is null ? null : Component.Correlation.ExpectedMic(originalId);
        var verdict = MdnVerdict.Of(result, expected, profile.SignedMdn);

        if (!verdict.SignatureAccepted && profile.RequireValidMdn)
        {
            // A signed MDN is agreed and a valid one required: an unsigned or foreign receipt may be anyone's, so it is
            // neither delivered nor allowed to end the wait; the partner's own receipt must still find the message.
            _logger?.LogWarning("AS2 asynchronous MDN for message {MessageId} refused: {Problem} (ref {Reference}).",
                originalId, verdict.Problem(result), reference);
            _endpoint.RecordError();
            span.Activity?.SetStatus(ActivityStatusCode.Error, "asynchronous MDN refused by the signature policy");
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Only a receipt the signature policy accepts ends the wait: an unsigned one where a signed one is agreed may
        // be forged, and must not make the partner's genuine receipt look like an orphan.
        if (originalId is not null && expected is not null && verdict.SignatureAccepted)
            Component.Correlation.Complete(originalId);

        if (verdict.Problem(result) is { } problem)
            _logger?.LogWarning("AS2 asynchronous MDN for message {MessageId} does not confirm the transfer: {Problem}.", originalId, problem);

        // Deliver the MDN outcome to the route; redbAs2.mdnConfirmed says whether it confirms the transfer. A route
        // failure is the route's: the partner delivered its receipt and is answered 200 either way.
        var message = new Message(bodyBytes) { ContentType = http.Request.ContentType };
        if (originalId is not null) message.Headers[As2Headers.MessageId] = originalId;
        message.Headers[As2Headers.MdnDisposition] = result.Disposition ?? string.Empty;
        message.Headers[As2Headers.SignatureValid] = result.SignatureValid;
        message.Headers[As2Headers.MdnMicMatch] = verdict.MicMatch;
        message.Headers[As2Headers.MdnMicStatus] = verdict.MicStatus;
        message.Headers[As2Headers.MdnConfirmed] = verdict.Confirmed;
        if (http.Connection.RemoteIpAddress is not null)
            message.Headers[As2Headers.RemoteAddress] = http.Connection.RemoteIpAddress.ToString();
        if (!string.IsNullOrEmpty(_options.ConnectionFactory))
            message.Headers[As2Headers.PartnerName] = _options.ConnectionFactory;

        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;
        ExchangePrincipal.Set(exchange, SharedHttpServerManager.GetResolvedPrincipal(http));
        try
        {
            using var processing = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, _drain.ProcessingToken);
            try { await _processor.Process(exchange, processing.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { exchange.Exception ??= e; }
            if (exchange.EndedInFailure())
            {
                _logger?.LogWarning(exchange.Exception, "AS2 asynchronous MDN for message {MessageId}: the route failed (ref {Reference}).",
                    originalId, reference);
                if (exchange.Exception is { } routeFailure) span.Activity.RecordFailure(routeFailure);
            }
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
    }
}
