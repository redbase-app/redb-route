using System.Diagnostics;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Mail;

/// <summary>
/// IMAP component. Scheme: "imap".
/// Provides email consumption via IMAP using MailKit.
/// Supports both polling and IDLE push modes.
/// URI: imap://mail.example.com:993?username=inbox@ex.com&amp;password=xxx&amp;folder=INBOX&amp;unseen=true
/// </summary>
public class ImapComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "imap";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new MailEndpointOptions();
        if (!string.IsNullOrEmpty(uri.Path) && uri.Path != "/")
            options.Host = uri.Path.TrimStart('/');
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new ImapEndpoint(uri, this, options);
    }
}

/// <summary>
/// IMAP endpoint. Creates an ImapConsumer for receiving email.
/// </summary>
public class ImapEndpoint : EndpointBase<MailEndpointOptions>
{
    /// <summary>Creates an IMAP endpoint.</summary>
    public ImapEndpoint(EndpointUri uri, ImapComponent component, MailEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The mail server host.</summary>
    public string Host => Options.Host;

    /// <summary>The resolved port number.</summary>
    public int Port => Options.ResolvePort("imap");

    /// <summary>Endpoint options for external access.</summary>
    internal MailEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer()
        => throw new NotSupportedException("IMAP does not support producing. Use smtp: scheme instead.");

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new ImapConsumer(this, processor, Options);
    }
}

/// <summary>
/// IMAP consumer — fetches email messages via MailKit ImapClient.
/// Supports polling mode and IMAP IDLE push mode.
/// Full support for filtering, sorting, post-processing (mark read, delete, move),
/// attachments, idempotency, and OAuth2.
/// </summary>
public class ImapConsumer : DrainableConsumer
{
    private readonly ImapEndpoint _endpoint;
    private readonly MailEndpointOptions _options;
    private readonly HashSet<string>? _seenIds;
    private readonly Regex? _subjectRegex;
    private readonly HashSet<string>? _fromFilter;
    private long _processedCount;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => "imap";

    /// <summary>Creates an IMAP consumer.</summary>
    public ImapConsumer(ImapEndpoint endpoint, IProcessor processor, MailEndpointOptions options)
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
    protected override Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        return _options.Idle
            ? IdleLoop(pollCt, processingCt)
            : PollLoop(pollCt, processingCt);
    }

    // ── Polling mode ──────────────────────────────────────────────────

    private async Task PollLoop(CancellationToken pollCt, CancellationToken processingCt)
    {
        if (_options.InitialDelay > 0)
            await Task.Delay(_options.InitialDelay, pollCt).ConfigureAwait(false);

        var delay = TimeSpan.FromMilliseconds(_options.Delay);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                using var client = await ConnectAsync(processingCt).ConfigureAwait(false);
                await FetchAndProcessAsync(client, processingCt).ConfigureAwait(false);

                if (!_options.Disconnect)
                    await client.DisconnectAsync(quit: true, processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested || processingCt.IsCancellationRequested) { break; }
            catch (Exception ex) { Logger?.LogWarning(ex, "IMAP poll failed, retrying on next cycle"); }

            try { await Task.Delay(delay, pollCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── IDLE mode ─────────────────────────────────────────────────────

    private async Task IdleLoop(CancellationToken pollCt, CancellationToken processingCt)
    {
        if (_options.InitialDelay > 0)
            await Task.Delay(_options.InitialDelay, pollCt).ConfigureAwait(false);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                using var client = await ConnectAsync(processingCt).ConfigureAwait(false);
                var folder = await OpenFolderAsync(client, processingCt).ConfigureAwait(false);

                // Initial fetch
                await FetchFromFolderAsync(client, folder, processingCt).ConfigureAwait(false);

                // IDLE loop — wait for notifications
                while (!pollCt.IsCancellationRequested)
                {
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(pollCt);
                    idleCts.CancelAfter(_options.IdleTimeout);

                    try
                    {
                        await client.IdleAsync(idleCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!pollCt.IsCancellationRequested)
                    {
                        // IDLE timeout expired — refetch and re-IDLE (RFC 2177 recommendation)
                    }

                    // Re-fetch after IDLE notification or timeout
                    if (!pollCt.IsCancellationRequested)
                        await FetchFromFolderAsync(client, folder, processingCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested || processingCt.IsCancellationRequested) { break; }
            catch
            {
                // Connection lost — wait and reconnect
                try { await Task.Delay(5000, pollCt).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // ── Fetch & process ───────────────────────────────────────────────

    private async Task FetchAndProcessAsync(ImapClient client, CancellationToken ct)
    {
        var folders = GetFolderNames();
        foreach (var folderName in folders)
        {
            var folder = await client.GetFolderAsync(folderName, ct).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            await FetchFromFolderAsync(client, folder, ct).ConfigureAwait(false);
            await folder.CloseAsync(expunge: false, ct).ConfigureAwait(false);
        }
    }

    private async Task FetchFromFolderAsync(ImapClient client, IMailFolder folder, CancellationToken ct)
    {
        var query = BuildSearchQuery();
        var uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);

        if (uids.Count == 0) return;

        // Apply MaxMessages
        var toFetch = _options.MaxMessages > 0 && uids.Count > _options.MaxMessages
            ? uids.Take(_options.MaxMessages).ToList()
            : uids.ToList();

        // Sort if requested
        if (_options.SortBy != MailSortBy.None && toFetch.Count > 1)
            toFetch = await SortUidsAsync(folder, toFetch, ct).ConfigureAwait(false);

        foreach (var uid in toFetch)
        {
            if (ct.IsCancellationRequested) break;

            MimeMessage mime;
            if (_options.Peek)
                mime = await folder.GetMessageAsync(uid, ct, progress: null).ConfigureAwait(false);
            else
                mime = await folder.GetMessageAsync(uid, ct, progress: null).ConfigureAwait(false);

            // Idempotency check
            if (_seenIds is not null)
            {
                var msgId = mime.MessageId ?? uid.ToString();
                if (!_seenIds.Add(msgId)) continue;
            }

            // Client-side filters
            if (!PassesFilters(mime)) continue;

            // Age filters
            if (!PassesAgeFilter(mime)) continue;

            var exchange = MailMessageHelper.CreateExchange(mime, "imap", folder.FullName, uid, scopeFactory: _endpoint.ScopeFactory);

            if (_options.KeepRawMessage)
                exchange.Properties["RawMimeMessage"] = mime;

            using var activity = StartConsumerActivity(mime, folder.FullName);

            IncrementInflight();
            try
            {
                await Processor.Process(exchange, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _processedCount);
                await PostProcessAsync(folder, uid, client, ct).ConfigureAwait(false);
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
    }

    private async Task PostProcessAsync(IMailFolder folder, UniqueId uid, ImapClient client, CancellationToken ct)
    {
        switch (_options.PostProcess)
        {
            case PostProcessAction.MarkRead:
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct).ConfigureAwait(false);
                break;

            case PostProcessAction.Delete:
                await folder.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true, ct).ConfigureAwait(false);
                await folder.ExpungeAsync(ct).ConfigureAwait(false);
                break;

            case PostProcessAction.Move:
                var targetFolder = await client.GetFolderAsync(_options.MoveTo, ct).ConfigureAwait(false);
                await folder.MoveToAsync(uid, targetFolder, ct).ConfigureAwait(false);
                break;

            case PostProcessAction.MarkReadAndMove:
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct).ConfigureAwait(false);
                var moveTarget = await client.GetFolderAsync(_options.MoveTo, ct).ConfigureAwait(false);
                await folder.MoveToAsync(uid, moveTarget, ct).ConfigureAwait(false);
                break;

            case PostProcessAction.Flag:
                await folder.AddFlagsAsync(uid, MessageFlags.Flagged, silent: true, ct).ConfigureAwait(false);
                break;

            case PostProcessAction.None:
            default:
                break;
        }
    }

    // ── Search query builder ──────────────────────────────────────────

    private SearchQuery BuildSearchQuery()
    {
        if (!string.IsNullOrWhiteSpace(_options.SearchQuery))
        {
            // Custom search takes priority — but MailKit doesn't parse raw IMAP search strings,
            // so we support basic structured filters
            return _options.FetchFilter switch
            {
                _ => BuildFetchFilterQuery()
            };
        }

        return BuildFetchFilterQuery();
    }

    private SearchQuery BuildFetchFilterQuery()
    {
        return _options.FetchFilter switch
        {
            MailFetchFilter.Unseen => SearchQuery.NotSeen,
            MailFetchFilter.All => SearchQuery.All,
            MailFetchFilter.Recent => SearchQuery.Recent,
            MailFetchFilter.Flagged => SearchQuery.Flagged,
            MailFetchFilter.Answered => SearchQuery.Answered,
            MailFetchFilter.Unanswered => SearchQuery.Not(SearchQuery.Answered),
            _ => SearchQuery.NotSeen
        };
    }

    // ── Sorting ───────────────────────────────────────────────────────

    private async Task<List<UniqueId>> SortUidsAsync(IMailFolder folder, List<UniqueId> uids, CancellationToken ct)
    {
        try
        {
            var orderBy = _options.SortBy switch
            {
                MailSortBy.DateAsc => new[] { OrderBy.Date },
                MailSortBy.DateDesc => new[] { OrderBy.ReverseDate },
                MailSortBy.SubjectAsc => new[] { OrderBy.Subject },
                MailSortBy.FromAsc => new[] { OrderBy.From },
                MailSortBy.SizeAsc => new[] { OrderBy.Size },
                MailSortBy.SizeDesc => new[] { OrderBy.ReverseSize },
                _ => Array.Empty<OrderBy>()
            };

            if (orderBy.Length > 0)
            {
                var sorted = await folder.SortAsync(SearchQuery.All, orderBy, ct).ConfigureAwait(false);
                return sorted.Where(uids.Contains).ToList();
            }
        }
        catch (NotSupportedException) { /* server doesn't support SORT */ }

        return uids;
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

    // ── Connection helpers ────────────────────────────────────────────

    private async Task<ImapClient> ConnectAsync(CancellationToken ct)
    {
        var client = new ImapClient();
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

    private async Task<IMailFolder> OpenFolderAsync(ImapClient client, CancellationToken ct)
    {
        var folder = await client.GetFolderAsync(_options.Folder, ct).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        return folder;
    }

    private async Task AuthenticateAsync(ImapClient client, CancellationToken ct)
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

    private string[] GetFolderNames()
    {
        var folders = new List<string> { _options.Folder };
        if (!string.IsNullOrWhiteSpace(_options.AdditionalFolders))
        {
            folders.AddRange(_options.AdditionalFolders.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return folders.ToArray();
    }

    private Activity? StartConsumerActivity(MimeMessage mime, string folder)
    {
        var activity = RouteActivitySource.Source.StartActivity(
            $"{_endpoint.Host} receive", ActivityKind.Consumer);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("messaging.system", "imap");
            activity.SetTag("messaging.operation", "receive");
            activity.SetTag("messaging.destination.name", _endpoint.Host);
            activity.SetTag("messaging.imap.folder", folder);
            if (!string.IsNullOrEmpty(mime.MessageId))
                activity.SetTag("messaging.message.id", mime.MessageId);
            if (!string.IsNullOrEmpty(mime.Subject))
                activity.SetTag("messaging.imap.subject", mime.Subject);
        }

        return activity;
    }
}
