using System.Diagnostics;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Smtp;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Mail;

/// <summary>
/// SMTP component. Scheme: "smtp".
/// Provides email sending via SMTP using MailKit.
/// URI: smtp://mail.example.com:587?username=bot@ex.com&amp;password=xxx&amp;security=StartTls
/// </summary>
public class SmtpComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "smtp";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new MailEndpointOptions();
        if (!string.IsNullOrEmpty(uri.Path) && uri.Path != "/")
            options.Host = uri.Path.TrimStart('/');
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new SmtpEndpoint(uri, this, options);
    }
}

/// <summary>
/// SMTP endpoint. Creates an SmtpProducer for sending email.
/// </summary>
public class SmtpEndpoint : EndpointBase<MailEndpointOptions>
{
    /// <summary>Creates an SMTP endpoint.</summary>
    public SmtpEndpoint(EndpointUri uri, SmtpComponent component, MailEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The mail server host.</summary>
    public string Host => Options.Host;

    /// <summary>The resolved port number.</summary>
    public int Port => Options.ResolvePort("smtp");

    /// <summary>Endpoint options for external access.</summary>
    internal MailEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new SmtpProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => throw new NotSupportedException("SMTP does not support consuming. Use imap: or pop3: scheme instead.");
}

/// <summary>
/// SMTP producer — sends email messages via MailKit SmtpClient.
/// Supports plain text and HTML bodies, attachments, CC/BCC, Reply-To,
/// OAuth2 authentication, and TLS/SSL.
/// </summary>
public class SmtpProducer : IProducer
{
    private readonly SmtpEndpoint _endpoint;
    private readonly MailEndpointOptions _options;
    private SmtpClient? _client;
    private readonly object _lock = new();

    /// <summary>Creates an SMTP producer.</summary>
    public SmtpProducer(SmtpEndpoint endpoint, MailEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_client is { IsConnected: true })
        {
            await _client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
            _client.Dispose();
            _client = null;
        }
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        using var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.Host} send", ActivityKind.Producer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "smtp");
            activity.SetTag("messaging.operation", "send");
            activity.SetTag("messaging.destination.name", _endpoint.Host);
        }

        var message = BuildMimeMessage(exchange);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.smtp.from", message.From.ToString());
            activity.SetTag("messaging.smtp.to", message.To.ToString());
            if (!string.IsNullOrEmpty(message.Subject))
                activity.SetTag("messaging.smtp.subject", message.Subject);
        }

        var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var response = await client.SendAsync(message, ct).ConfigureAwait(false);

        exchange.In.Headers[MailHeaders.MessageId] = message.MessageId;

        if (_options.Disconnect)
        {
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
            client.Dispose();
            lock (_lock) _client = null;
        }
    }

    private MimeMessage BuildMimeMessage(IExchange exchange)
    {
        var msg = new MimeMessage();

        // ── Addressing (header > expression > static) ──
        var from = GetHeader(exchange, MailHeaders.From) ?? _options.ResolveOption(_options.From, exchange);
        if (!string.IsNullOrWhiteSpace(from))
            msg.From.AddRange(InternetAddressList.Parse(from));

        var to = GetHeader(exchange, MailHeaders.To) ?? _options.ResolveOption(_options.To, exchange);
        if (!string.IsNullOrWhiteSpace(to))
            msg.To.AddRange(InternetAddressList.Parse(to));

        var cc = GetHeader(exchange, MailHeaders.Cc) ?? _options.ResolveOption(_options.Cc, exchange);
        if (!string.IsNullOrWhiteSpace(cc))
            msg.Cc.AddRange(InternetAddressList.Parse(cc));

        var bcc = GetHeader(exchange, MailHeaders.Bcc) ?? _options.ResolveOption(_options.Bcc, exchange);
        if (!string.IsNullOrWhiteSpace(bcc))
            msg.Bcc.AddRange(InternetAddressList.Parse(bcc));

        var replyTo = GetHeader(exchange, MailHeaders.ReplyTo) ?? _options.ResolveOption(_options.ReplyTo, exchange);
        if (!string.IsNullOrWhiteSpace(replyTo))
            msg.ReplyTo.AddRange(InternetAddressList.Parse(replyTo));

        // ── Subject (header > expression > static) ──
        msg.Subject = GetHeader(exchange, MailHeaders.Subject)
                      ?? _options.ResolveOption(_options.Subject, exchange)
                      ?? "";

        // ── In-Reply-To / References (threading) ──
        var inReplyTo = GetHeader(exchange, MailHeaders.InReplyTo);
        if (!string.IsNullOrWhiteSpace(inReplyTo))
            msg.InReplyTo = inReplyTo;

        var references = GetHeader(exchange, MailHeaders.References);
        if (!string.IsNullOrWhiteSpace(references))
        {
            foreach (var refId in references.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                msg.References.Add(refId);
        }

        // ── Priority ──
        var priority = GetHeader(exchange, MailHeaders.Priority);
        if (!string.IsNullOrWhiteSpace(priority) && Enum.TryParse<MessagePriority>(priority, true, out var p))
            msg.Priority = p;

        // ── Body ──
        var body = exchange.In.Body?.ToString() ?? "";
        var contentType = GetHeader(exchange, MailHeaders.ContentType)
                          ?? _options.ResolveOption(_options.ContentType, exchange)
                          ?? _options.ContentType;
        var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

        var bodyBuilder = new BodyBuilder();

        if (isHtml)
        {
            bodyBuilder.HtmlBody = body;
            // Also set plain text alternative from header if provided
            var textAlt = GetHeader(exchange, MailHeaders.TextBody);
            if (!string.IsNullOrWhiteSpace(textAlt))
                bodyBuilder.TextBody = textAlt;
            else if (_options.AlternativeBody)
                bodyBuilder.TextBody = StripHtml(body);
        }
        else
        {
            bodyBuilder.TextBody = body;
            // Also set HTML alternative from header if provided
            var htmlAlt = GetHeader(exchange, MailHeaders.HtmlBody);
            if (!string.IsNullOrWhiteSpace(htmlAlt))
                bodyBuilder.HtmlBody = htmlAlt;
        }

        // ── Attachments ──
        AddAttachments(bodyBuilder, exchange);

        msg.Body = bodyBuilder.ToMessageBody();

        // ── Custom headers from exchange ──
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (key.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
                && !MailHeaders.IsRedbHeader(key)
                && value is string sv)
            {
                msg.Headers.Add(key, sv);
            }
        }

        return msg;
    }

    private void AddAttachments(BodyBuilder bodyBuilder, IExchange exchange)
    {
        // From options: comma-separated file paths
        if (!string.IsNullOrWhiteSpace(_options.Attachments))
        {
            foreach (var path in _options.Attachments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (System.IO.File.Exists(path))
                    bodyBuilder.Attachments.Add(path);
            }
        }

        // From exchange properties: List<MailAttachment> or byte[][]
        if (exchange.Properties.TryGetValue("Attachments", out var attachObj))
        {
            switch (attachObj)
            {
                case IEnumerable<MailAttachment> attachments:
                    foreach (var att in attachments)
                        bodyBuilder.Attachments.Add(att.FileName, att.Content, ContentType.Parse(att.ContentType ?? "application/octet-stream"));
                    break;

                case IEnumerable<string> filePaths:
                    foreach (var path in filePaths)
                    {
                        if (System.IO.File.Exists(path))
                            bodyBuilder.Attachments.Add(path);
                    }
                    break;
            }
        }
    }

    private async Task<SmtpClient> EnsureConnectedAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_client is { IsConnected: true })
                return _client;
        }

        var client = new SmtpClient();
        client.Timeout = _options.Timeout;

        if (_options.SkipCertificateValidation)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        await client.ConnectAsync(
            _endpoint.Host,
            _endpoint.Port,
            _options.ResolveSecurityOptions(),
            ct).ConfigureAwait(false);

        await AuthenticateAsync(client, ct).ConfigureAwait(false);

        lock (_lock) _client = client;
        return client;
    }

    private async Task AuthenticateAsync(SmtpClient client, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_options.AccessToken))
        {
            var mechanism = _options.AuthMechanism switch
            {
                MailAuthMechanism.OAuthBearer => new MailKit.Security.SaslMechanismOAuthBearer(_options.Username, _options.AccessToken),
                _ => (MailKit.Security.SaslMechanism)new MailKit.Security.SaslMechanismOAuth2(_options.Username, _options.AccessToken)
            };
            await client.AuthenticateAsync(mechanism, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(_options.Username))
        {
            await client.AuthenticateAsync(_options.Username, _options.Password, ct).ConfigureAwait(false);
        }
    }

    private static string? GetHeader(IExchange exchange, string key)
    {
        return exchange.In.Headers.TryGetValue(key, out var val) && val is string s && !string.IsNullOrWhiteSpace(s)
            ? s : null;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<p>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}

/// <summary>
/// Attachment data for programmatic attachment via exchange properties.
/// </summary>
/// <param name="FileName">Attachment file name.</param>
/// <param name="Content">Attachment content bytes.</param>
/// <param name="ContentType">MIME content type (default: application/octet-stream).</param>
public sealed record MailAttachment(string FileName, byte[] Content, string? ContentType = null);
