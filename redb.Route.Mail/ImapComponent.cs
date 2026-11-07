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
[EndpointRole(EndpointRole.Consumer, Hint = "Use smtp: to send mail.")]
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
        MailEndpointOptions.RefuseOtherSide(uri, Scheme, EndpointRole.Consumer);
        MailConnectionFactory.TryApply(Context, options, uri, Logger, "IMAP");
        options.Validate(EndpointRole.Consumer);

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
    private MailIdempotency? _idempotency;
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
    protected override Task OnStarting(CancellationToken ct)
    {
        // The repository is looked up when the consumer starts, so a name nothing is registered under stops the route.
        _idempotency = MailIdempotency.Create(_options, _endpoint);
        return Task.CompletedTask;
    }

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
            if (!_options.FetchBody)
            {
                // fetchBody=false: envelope scanning - only the headers travel from the server.
                // MimeKit keeps address/subject/date properties in sync with the header list, so
                // filters, idempotency and the exchange headers all work; the body stays empty.
                // (A dead option before часть B of the options sweep.)
                var headerList = await folder.GetHeadersAsync(uid, ct).ConfigureAwait(false);
                mime = MailMessageHelper.FromHeadersOnly(headerList);
            }
            else
            {
                mime = await folder.GetMessageAsync(uid, ct, progress: null).ConfigureAwait(false);
            }

            // Client-side filters
            if (!PassesFilters(mime)) continue;

            // Age filters
            if (!PassesAgeFilter(mime)) continue;

            // Idempotency, after the filters: a message they hold back is not taken. The key is the Message-ID, else
            // the folder, UIDVALIDITY and UID, which name one message for as long as the folder keeps its UIDVALIDITY.
            string? key = null;
            if (_idempotency is not null)
            {
                key = !string.IsNullOrEmpty(mime.MessageId)
                    ? mime.MessageId
                    : $"{folder.FullName}/{folder.UidValidity}/{uid.Id}";
                if (!await _idempotency.TryAdd(key, ct).ConfigureAwait(false)) continue;
            }

            var exchange = MailMessageHelper.CreateExchange(mime, "imap", folder.FullName, uid, scopeFactory: _endpoint.ScopeFactory, options: _options);

            if (_options.KeepRawMessage)
                exchange.Properties["RawMimeMessage"] = mime;

            using var span = StartConsumerSpan(mime, folder.FullName);

            // peek=false (Apache Camel's): the message is marked read as it is fetched for the route, so one whose
            // exchange fails stays read; the fetch itself always peeks.
            if (!_options.Peek)
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct).ConfigureAwait(false);

            IncrementInflight();
            try
            {
                try
                {
                    await Processor.Process(exchange, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // our own stop: end the fetch, the message is not a failure
                }
                catch (Exception ex)
                {
                    // A failure that escaped the route is one failed message: it is left as it is and the fetch goes
                    // on, so a message that fails on every attempt does not hold up the messages after it.
                    span.Activity.RecordFailure(ex);
                    Logger?.LogError(ex, "IMAP: the route failed for message {Uid} in {Folder}; the message is left as it is",
                        uid, folder.FullName);
                    if (key is not null)
                        await _idempotency!.Rollback(key, ct).ConfigureAwait(false);
                    continue;
                }

                if (exchange.Exception is { } failure && !exchange.ExceptionHandled
                    && (failure is not OperationCanceledException || !ct.IsCancellationRequested))
                    span.Activity.RecordFailure(failure);

                // Apache Camel's rollback: a failed exchange (an unhandled exception, or rollback-only) leaves the
                // message as it is; only a successful one is committed.
                if (exchange.EndedInFailure())
                {
                    Logger?.LogWarning(exchange.ExceptionHandled ? null : exchange.Exception,
                        "IMAP: the exchange for message {Uid} in {Folder} failed; the message is left as it is",
                        uid, folder.FullName);
                    if (key is not null)
                        await _idempotency!.Rollback(key, ct).ConfigureAwait(false);
                    continue;
                }

                Interlocked.Increment(ref _processedCount);
                await PostProcessAsync(folder, uid, client, exchange.HasOut ? exchange.Out! : exchange.In, ct).ConfigureAwait(false);
                if (key is not null)
                    await _idempotency!.Commit(key, ct).ConfigureAwait(false);
            }
            finally
            {
                DecrementInflight();
                await exchange.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Commits a message whose exchange succeeded, the way Apache Camel's mail consumer does. The copy
    /// (<see cref="MailHeaders.CopyTo"/>, else <see cref="MailEndpointOptions.CopyTo"/>) is taken first. Then, when
    /// <see cref="MailHeaders.Delete"/>, <see cref="MailHeaders.MoveTo"/> or <see cref="MailEndpointOptions.Delete"/>
    /// is set, the message is deleted (moved first when a folder is given) or marked read (and moved), the headers
    /// on the message the route ended with winning over the options. Otherwise
    /// <see cref="MailEndpointOptions.PostProcess"/> applies.
    /// </summary>
    private async Task PostProcessAsync(IMailFolder folder, UniqueId uid, ImapClient client, IMessage result,
        CancellationToken ct)
    {
        var copyHeader = result.GetHeader<string>(MailHeaders.CopyTo);
        var copyTo = !string.IsNullOrWhiteSpace(copyHeader) ? copyHeader : _options.CopyTo;
        if (!string.IsNullOrWhiteSpace(copyTo))
        {
            var copyTarget = await GetOrCreateFolderAsync(client, copyTo, ct).ConfigureAwait(false);
            await folder.CopyToAsync(uid, copyTarget, ct).ConfigureAwait(false);
        }

        var deleteHeader = result.Headers.ContainsKey(MailHeaders.Delete)
            ? result.GetHeader<bool>(MailHeaders.Delete)
            : (bool?)null;
        var moveHeader = result.GetHeader<string>(MailHeaders.MoveTo);
        if (deleteHeader is not null || !string.IsNullOrWhiteSpace(moveHeader) || _options.Delete)
        {
            var delete = deleteHeader ?? (_options.Delete || _options.PostProcess == PostProcessAction.Delete);
            var moveTo = !string.IsNullOrWhiteSpace(moveHeader) ? moveHeader
                : _options.Delete || _options.PostProcess is PostProcessAction.Move or PostProcessAction.MarkReadAndMove
                    ? _options.MoveTo
                    : null;
            await CommitAsync(folder, uid, client, delete, moveTo, ct).ConfigureAwait(false);
            return;
        }

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
                var targetFolder = await GetOrCreateFolderAsync(client, _options.MoveTo, ct).ConfigureAwait(false);
                await folder.MoveToAsync(uid, targetFolder, ct).ConfigureAwait(false);
                break;

            case PostProcessAction.MarkReadAndMove:
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct).ConfigureAwait(false);
                var moveTarget = await GetOrCreateFolderAsync(client, _options.MoveTo, ct).ConfigureAwait(false);
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

    /// <summary>Apache Camel's commit of a mail: deleted (moved first when a folder is given), or marked read and moved.</summary>
    private static async Task CommitAsync(IMailFolder folder, UniqueId uid, ImapClient client, bool delete, string? moveTo,
        CancellationToken ct)
    {
        if (!delete)
            await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(moveTo))
        {
            var target = await GetOrCreateFolderAsync(client, moveTo, ct).ConfigureAwait(false);
            await folder.MoveToAsync(uid, target, ct).ConfigureAwait(false);
            return;
        }

        if (delete)
        {
            await folder.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true, ct).ConfigureAwait(false);
            await folder.ExpungeAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The folder a message is moved or copied to, created (with the folders above it) when the mailbox does not have
    /// it yet, as Apache Camel's mail consumer creates a missing <c>moveTo</c> / <c>copyTo</c> folder. The path is the
    /// folder's full name, its levels split by the server's separator, under the personal namespace.
    /// </summary>
    private static async Task<IMailFolder> GetOrCreateFolderAsync(ImapClient client, string path, CancellationToken ct)
    {
        try
        {
            return await client.GetFolderAsync(path, ct).ConfigureAwait(false);
        }
        catch (FolderNotFoundException)
        {
        }

        var parent = client.PersonalNamespaces.Count > 0
            ? client.GetFolder(client.PersonalNamespaces[0])
            : client.GetFolder(new FolderNamespace(client.Inbox.DirectorySeparator, ""));
        foreach (var level in path.Split(parent.DirectorySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                parent = await parent.GetSubfolderAsync(level, ct).ConfigureAwait(false);
            }
            catch (FolderNotFoundException)
            {
                parent = await parent.CreateAsync(level, isMessageFolder: true, ct).ConfigureAwait(false);
            }
        }
        return parent;
    }

    // ── Search query builder ──────────────────────────────────────────

    // searchQuery is not applied (its XML doc says so): MailKit has no parser for a raw IMAP SEARCH string, so the
    // fetch filter alone selects the messages.
    private SearchQuery BuildSearchQuery() => BuildFetchFilterQuery();

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

        if (_options.TrustAllCertificates)
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

    /// <summary>
    /// The span of one message. A mailbox message carries no trace context the connector reads, so the span is a root,
    /// never a child of the activity the poll loop holds.
    /// </summary>
    private TransportSpan StartConsumerSpan(MimeMessage mime, string folder)
    {
        var span = RouteTelemetryExtensions.StartConsumerSpan<object?>(
            (_endpoint.Component as ComponentBase)?.Context,
            $"{_endpoint.Host} receive", ActivityKind.Consumer, "messaging.system", "imap", _endpoint.Uri.NormalizedKey,
            null, static (_, _) => null, destination: _endpoint.Host, operation: "receive");

        if (span.Activity is { IsAllDataRequested: true } activity)
        {
            activity.SetTag("messaging.imap.folder", folder);
            if (!string.IsNullOrEmpty(mime.MessageId))
                activity.SetTag("messaging.message.id", mime.MessageId);
            if (!string.IsNullOrEmpty(mime.Subject))
                activity.SetTag("messaging.imap.subject", mime.Subject);
        }

        return span;
    }
}
