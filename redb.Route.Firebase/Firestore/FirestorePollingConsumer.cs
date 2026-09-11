using System.Text.Json;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Firestore polling consumer (<c>Realtime=false</c>) — runs the query every <c>Delay</c> ms
/// and diffs the result against the previous poll (<c>DocumentId → UpdateTime</c>):
/// a new id is <c>Added</c>, a changed <c>UpdateTime</c> is <c>Modified</c>, an id that left
/// the result set is <c>Removed</c> (body is <c>null</c> — the old data is not retained).
/// The first poll delivers everything as <c>Added</c>; the diff lives in memory, so a restart
/// starts from a clean snapshot — same as the realtime listener's initial snapshot.
/// Note: <c>Removed</c> is only visible within the query result window (Where/Limit shift it).
/// </summary>
internal sealed class FirestorePollingConsumer : DrainableConsumer
{
    private readonly FirestoreEndpoint _endpoint;
    private readonly FirestoreEndpointOptions _options;
    private Google.Cloud.Firestore.Query? _query;
    private Dictionary<string, Timestamp?> _lastSeen = new();

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"fstore://{_endpoint.CollectionPath} (polling)";

    internal FirestorePollingConsumer(FirestoreEndpoint endpoint, IProcessor processor,
        FirestoreEndpointOptions options) : base(processor)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task OnStarting(CancellationToken ct)
    {
        var db = await _endpoint.GetOrCreateDbAsync(ct).ConfigureAwait(false);

        Google.Cloud.Firestore.Query query = db.Collection(_endpoint.CollectionPath);
        if (_options.Where is not null)
            query = FirestoreQueryHelper.ApplyWhereFilters(query, _options.Where);
        if (_options.OrderBy is not null)
            query = FirestoreQueryHelper.ApplyOrderBy(query, _options.OrderBy);
        if (_options.Limit is not null)
            query = query.Limit(_options.Limit.Value);
        _query = query;
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        if (_options.InitialDelay > 0)
            await Task.Delay(_options.InitialDelay, pollCt).ConfigureAwait(false);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                await PollAsync(processingCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Firestore polling failed: {Collection}", _endpoint.CollectionPath);
                _endpoint.RecordError(ex);
            }

            try
            {
                await Task.Delay(_options.Delay, pollCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pollCt.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var snapshot = await _query!.GetSnapshotAsync(ct).ConfigureAwait(false);
        var current = new Dictionary<string, Timestamp?>(snapshot.Count);

        foreach (var doc in snapshot.Documents)
        {
            if (ct.IsCancellationRequested) return;
            current[doc.Id] = doc.UpdateTime;

            string changeType;
            if (!_lastSeen.TryGetValue(doc.Id, out var prevUpdate))
                changeType = "Added";
            else if (!Equals(prevUpdate, doc.UpdateTime))
                changeType = "Modified";
            else
                continue; // unchanged

            await EmitAsync(doc, changeType, snapshot.ReadTime, ct).ConfigureAwait(false);
        }

        // Ids that left the query result window
        foreach (var (docId, _) in _lastSeen)
        {
            if (ct.IsCancellationRequested) return;
            if (!current.ContainsKey(docId))
                await EmitRemovedAsync(docId, snapshot.ReadTime, ct).ConfigureAwait(false);
        }

        _lastSeen = current;
    }

    private async Task EmitAsync(DocumentSnapshot doc, string changeType, Timestamp readTime, CancellationToken ct)
    {
        var data = doc.ToDictionary();
        object body = _options.RawJson ? JsonSerializer.Serialize(data) : data;

        var exchange = Exchange.Create(new Message(body), _endpoint.ScopeFactory);
        exchange.In.Headers[FirestoreHeaders.DocumentId] = doc.Id;
        exchange.In.Headers[FirestoreHeaders.DocumentPath] = doc.Reference.Path;
        exchange.In.Headers[FirestoreHeaders.CollectionPath] = _endpoint.CollectionPath;
        exchange.In.Headers[FirestoreHeaders.ChangeType] = changeType;
        exchange.In.Headers[FirestoreHeaders.CreateTime] = doc.CreateTime;
        exchange.In.Headers[FirestoreHeaders.UpdateTime] = doc.UpdateTime;
        exchange.In.Headers[FirestoreHeaders.ReadTime] = readTime;

        // MessagesIn is counted by the core StatisticsProcessor - ownership audit.
        await ProcessWithTracking(exchange, ct).ConfigureAwait(false);
    }

    private async Task EmitRemovedAsync(string docId, Timestamp readTime, CancellationToken ct)
    {
        var exchange = Exchange.Create(new Message(null), _endpoint.ScopeFactory);
        exchange.In.Headers[FirestoreHeaders.DocumentId] = docId;
        exchange.In.Headers[FirestoreHeaders.CollectionPath] = _endpoint.CollectionPath;
        exchange.In.Headers[FirestoreHeaders.ChangeType] = "Removed";
        exchange.In.Headers[FirestoreHeaders.ReadTime] = readTime;

        // MessagesIn is counted by the core StatisticsProcessor - ownership audit.
        await ProcessWithTracking(exchange, ct).ConfigureAwait(false);
    }
}
