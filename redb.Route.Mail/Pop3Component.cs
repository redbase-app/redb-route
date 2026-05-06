using System.Diagnostics;
using System.Text.RegularExpressions;
using MailKit.Net.Pop3;
using Microsoft.Extensions.Logging;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Mail;

/// <summary>
/// POP3 component. Scheme: "pop3".
/// Provides email consumption via POP3 using MailKit.
/// URI: pop3://mail.example.com:995?username=inbox@ex.com&amp;password=xxx
/// </summary>
public class Pop3Component : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "pop3";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new MailEndpointOptions();
        if (!string.IsNullOrEmpty(uri.Path) && uri.Path != "/")
            options.Host = uri.Path.TrimStart('/');
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new Pop3Endpoint(uri, this, options);
    }
}

/// <summary>
/// POP3 endpoint. Creates a Pop3Consumer for receiving email.
/// </summary>
public class Pop3Endpoint : EndpointBase<MailEndpointOptions>
{
    /// <summary>Creates a POP3 endpoint.</summary>
    public Pop3Endpoint(EndpointUri uri, Pop3Component component, MailEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The mail server host.</summary>
    public string Host => Options.Host;

    /// <summary>The resolved port number.</summary>
    public int Port => Options.ResolvePort("pop3");

    /// <summary>Endpoint options.</summary>
    internal MailEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer()
        => throw new NotSupportedException("POP3 does not support producing. Use smtp: scheme instead.");

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new Pop3Consumer(this, processor, Options);
    }
}

/// <summary>
/// POP3 consumer — fetches email messages via MailKit Pop3Client.
/// POP3 is a simple polling protocol: connect, list messages, download, optionally delete.
/// Does not support IDLE mode or folders.
/// </summary>
public class Pop3Consumer : DrainableConsumer
{
    private readonly Pop3Endpoint _endpoint;
    private readonly MailEndpointOptions _options;
    private readonly HashSet<string>? _seenIds;
    private readonly Regex? _subjectRegex;
    private readonly HashSet<string>? _fromFilter;
    private long _processedCount;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => "pop3";

    /// <summary>Creates a POP3 consumer.</summary>
    public Pop3Consumer(Pop3Endpoint endpoint, IProcessor processor, MailEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _seenIds = options.Idempotent ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;

        if (!string.IsNullOrWhiteSpace(options.SubjectFilter))
            _subjectRegex = new Regex(options.SubjectFilter, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        if (!string.IsNullOrWhiteSpace(options.FromFilter))
            _fromFilter = new HashSet<string>(
                options.FromFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Number of messages successfully processed.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        if (_options.InitialDelay > 0)
            await Task.Delay(_options.InitialDelay, pollCt).ConfigureAwait(false);

        var delay = TimeSpan.FromMilliseconds(_options.Delay);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                using var client = await ConnectAsync(processingCt).ConfigureAwait(false);

                var count = client.Count;
                var toFetch = _options.MaxMessages > 0 ? Math.Min(count, _options.MaxMessages) : count;

                for (var i = 0; i < toFetch; i++)
                {
                    if (processingCt.IsCancellationRequested) break;

                    var mime = await client.GetMessageAsync(i, processingCt).ConfigureAwait(false);

                    // Idempotency check
                    if (_seenIds is not null)
                    {
                        var msgId = mime.MessageId ?? i.ToString();
                        if (!_seenIds.Add(msgId)) continue;
                    }

                    // Client-side filters
                    if (!PassesFilters(mime)) continue;
                    if (!PassesAgeFilter(mime)) continue;

                    var exchange = MailMessageHelper.CreateExchange(mime, "pop3", index: i, scopeFactory: _endpoint.ScopeFactory);

                    if (_options.KeepRawMessage)
                        exchange.Properties["RawMimeMessage"] = mime;

                    using var activity = StartConsumerActivity(mime);

                    IncrementInflight();
                    try
                    {
                        await Processor.Process(exchange, processingCt).ConfigureAwait(false);
                        Interlocked.Increment(ref _processedCount);

                        // POP3 post-processing: only delete or none
                        if (_options.PostProcess == PostProcessAction.Delete)
                            await client.DeleteMessageAsync(i, processingCt).ConfigureAwait(false);
                    }
                    catch
                    {
                        activity?.SetStatus(ActivityStatusCode.Error);
                        throw;
                    }
                    finally
                    {
                        DecrementInflight();
                        await exchange.DisposeAsync().ConfigureAwait(false);
                    }
                }

                await client.DisconnectAsync(quit: true, processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested || processingCt.IsCancellationRequested) { break; }
            catch (Exception ex) { Logger?.LogWarning(ex, "POP3 poll failed, retrying on next cycle"); }

            try { await Task.Delay(delay, pollCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Client-side filters ───────────────────────────────────────────

    private bool PassesFilters(MimeMessage mime)
    {
        if (_subjectRegex is not null && !_subjectRegex.IsMatch(mime.Subject ?? ""))
            return false;

        if (_fromFilter is not null && mime.From.Mailboxes.All(mb => !_fromFilter.Contains(mb.Address)))
            return false;

        return true;
    }

    private bool PassesAgeFilter(MimeMessage mime)
    {
        if (_options.MinAge <= 0 && _options.MaxAge <= 0) return true;

        var messageDate = mime.Date.UtcDateTime;
        if (messageDate == default) return true;

        var age = DateTimeOffset.UtcNow - messageDate;

        if (_options.MinAge > 0 && age.TotalMilliseconds < _options.MinAge)
            return false;

        if (_options.MaxAge > 0 && age.TotalMilliseconds > _options.MaxAge)
            return false;

        return true;
    }

    // ── Connection ────────────────────────────────────────────────────

    private async Task<Pop3Client> ConnectAsync(CancellationToken ct)
    {
        var client = new Pop3Client();
        client.Timeout = _options.Timeout;

        if (_options.SkipCertificateValidation)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        await client.ConnectAsync(
            _endpoint.Host,
            _endpoint.Port,
            _options.ResolveSecurityOptions(),
            ct).ConfigureAwait(false);

        await AuthenticateAsync(client, ct).ConfigureAwait(false);

        return client;
    }

    private async Task AuthenticateAsync(Pop3Client client, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_options.AccessToken))
        {
            var mechanism = _options.AuthMechanism switch
            {
                MailAuthMechanism.OAuthBearer =>
                    new MailKit.Security.SaslMechanismOAuthBearer(_options.Username, _options.AccessToken),
                _ => (MailKit.Security.SaslMechanism)
                    new MailKit.Security.SaslMechanismOAuth2(_options.Username, _options.AccessToken)
            };
            await client.AuthenticateAsync(mechanism, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(_options.Username))
        {
            await client.AuthenticateAsync(_options.Username, _options.Password, ct).ConfigureAwait(false);
        }
    }

    private Activity? StartConsumerActivity(MimeMessage mime)
    {
        var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.Host} receive", ActivityKind.Consumer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "pop3");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", _endpoint.Host);
            if (!string.IsNullOrEmpty(mime.MessageId))
                activity.SetTag("messaging.message.id", mime.MessageId);
            if (!string.IsNullOrEmpty(mime.Subject))
                activity.SetTag("messaging.pop3.subject", mime.Subject);
        }

        return activity;
    }
}
