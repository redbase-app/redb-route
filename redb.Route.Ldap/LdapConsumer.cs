using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;
using redb.Route.Abstractions;
using redb.Route.Core;
using NovellEntry = Novell.Directory.Ldap.LdapEntry;

namespace redb.Route.Ldap;

/// <summary>
/// LDAP consumer. Polls the directory for changes using configurable change tracking.
/// Supports ModifyTimestamp (universal) and USN (Active Directory) modes.
/// Optionally detects deletions by comparing known DNs with current results.
/// </summary>
internal sealed class LdapConsumer : DrainableConsumer
{
    private readonly LdapEndpoint _endpoint;
    private readonly LdapEndpointOptions _options;
    private readonly HashSet<string> _knownDns = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _baseFilter;
    private int _pollsSinceFullSync;

    /// <inheritdoc />
    protected override IEndpoint ConsumerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"ldap:WATCH:{_endpoint.BaseDn}";

    /// <summary>Number of change events delivered.</summary>
    public long ProcessedCount { get; private set; }

    /// <summary>Creates an LDAP consumer.</summary>
    public LdapConsumer(LdapEndpoint endpoint, IProcessor processor, LdapEndpointOptions options)
        : base(processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _baseFilter = _options.Filter?.Resolve(new Exchange()) ?? "(objectClass=*)";
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken pollCt, CancellationToken processingCt)
    {
        var mode = _options.ParseChangeTrackingMode();

        if (mode == LdapChangeTrackingMode.Persistent)
            throw new NotSupportedException(
                "Persistent Search is not yet implemented. Use ModifyTimestamp or Usn mode.");

        string? marker = _options.InitialLoad ? null : await GetInitialMarkerAsync(mode, processingCt).ConfigureAwait(false);

        Logger?.LogInformation("LDAP consumer started: baseDn={BaseDn}, mode={Mode}, initialLoad={InitialLoad}, detectDeletions={DetectDeletions}",
            _endpoint.BaseDn, mode, _options.InitialLoad, _options.DetectDeletions);

        while (!pollCt.IsCancellationRequested)
        {
            try
            {
                var (entries, newMarker) = await PollChangesAsync(mode, marker, processingCt)
                    .ConfigureAwait(false);

                foreach (var entry in entries)
                {
                    var exchange = CreateExchange(entry);
                    await ProcessWithTracking(exchange, processingCt).ConfigureAwait(false);
                    ProcessedCount++;
                }

                // Detect deletions if enabled
                if (_options.DetectDeletions)
                    await DetectDeletionsAsync(entries, processingCt).ConfigureAwait(false);

                if (newMarker != null)
                    marker = newMarker;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "LDAP poll failed for {BaseDn}", _endpoint.BaseDn);
                _endpoint.RecordError(ex);
            }

            try
            {
                await Task.Delay(_options.PollInterval, pollCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Logger?.LogInformation("LDAP consumer stopped: baseDn={BaseDn}, processed={Count}",
            _endpoint.BaseDn, ProcessedCount);
    }

    private IExchange CreateExchange(LdapEntry entry)
    {
        var exchange = Exchange.Create(new Message(entry), _endpoint.ScopeFactory);
        exchange.In.Headers[LdapHeaders.ChangeType] = entry.ChangeType ?? "modified";
        exchange.In.Headers[LdapHeaders.Dn] = entry.Dn;
        exchange.In.Headers[LdapHeaders.BaseDn] = _endpoint.BaseDn;
        exchange.In.Headers[LdapHeaders.Timestamp] = DateTime.UtcNow.ToString("O");
        return exchange;
    }

    private async Task<(List<LdapEntry> Entries, string? NewMarker)> PollChangesAsync(
        LdapChangeTrackingMode mode, string? marker, CancellationToken ct)
    {
        var conn = await _endpoint.AcquireConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            return mode switch
            {
                LdapChangeTrackingMode.Usn => await PollUsnChangesAsync(conn, marker, ct).ConfigureAwait(false),
                _ => await PollModifyTimestampChangesAsync(conn, marker, ct).ConfigureAwait(false)
            };
        }
        finally
        {
            _endpoint.ReleaseConnection(conn);
        }
    }

    private async Task<(List<LdapEntry> Entries, string? NewMarker)> PollModifyTimestampChangesAsync(
        LdapConnection conn, string? marker, CancellationToken ct)
    {
        var filter = marker != null
            ? $"(&{_baseFilter}(modifyTimestamp>={marker}))"
            : _baseFilter;

        var entries = await SearchEntriesAsync(conn, filter, ct).ConfigureAwait(false);

        // Derive next marker from the latest modifyTimestamp in results
        string? newMarker = marker;
        foreach (var entry in entries)
        {
            var ts = entry.GetString("modifyTimestamp");
            if (ts != null && (newMarker == null || string.CompareOrdinal(ts, newMarker) > 0))
                newMarker = ts;
        }

        // If no entries or no modifyTimestamp available, fall back to UTC
        if (newMarker == marker)
            newMarker = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "Z";

        foreach (var entry in entries)
            entry.ChangeType = marker == null ? "added" : "modified";

        return (entries, newMarker);
    }

    private async Task<(List<LdapEntry> Entries, string? NewMarker)> PollUsnChangesAsync(
        LdapConnection conn, string? marker, CancellationToken ct)
    {
        var filter = marker != null
            ? $"(&{_baseFilter}(uSNChanged>={marker}))"
            : _baseFilter;

        var entries = await SearchEntriesAsync(conn, filter, ct).ConfigureAwait(false);

        // Find highest USN for next poll
        string? newMarker = marker;
        foreach (var entry in entries)
        {
            var usn = entry.GetString("uSNChanged");
            if (usn != null && (newMarker == null || string.CompareOrdinal(usn, newMarker) > 0))
                newMarker = usn;
        }

        // Increment USN to avoid re-reading the same entry
        if (newMarker != null && long.TryParse(newMarker, out var usnVal))
            newMarker = (usnVal + 1).ToString();

        foreach (var entry in entries)
            entry.ChangeType = marker == null ? "added" : "modified";

        return (entries, newMarker);
    }

    private async Task DetectDeletionsAsync(List<LdapEntry> currentEntries, CancellationToken ct)
    {
        var currentDns = new HashSet<string>(
            currentEntries.Select(e => e.Dn),
            StringComparer.OrdinalIgnoreCase);

        _pollsSinceFullSync++;

        // On first poll or every FullSyncInterval cycles, do a full scan
        if (_knownDns.Count == 0 || _pollsSinceFullSync >= _options.FullSyncInterval)
        {
            _pollsSinceFullSync = 0;

            var conn = await _endpoint.AcquireConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var allEntries = await SearchEntriesAsync(conn, _baseFilter, ct)
                    .ConfigureAwait(false);

                var allDns = new HashSet<string>(
                    allEntries.Select(e => e.Dn),
                    StringComparer.OrdinalIgnoreCase);

                // Emit "deleted" for every known DN that's no longer in the directory
                foreach (var dn in _knownDns)
                {
                    if (!allDns.Contains(dn))
                    {
                        var deleted = new LdapEntry { Dn = dn, ChangeType = "deleted" };
                        var exchange = CreateExchange(deleted);
                        await ProcessWithTracking(exchange, ct).ConfigureAwait(false);
                        ProcessedCount++;

                        Logger?.LogDebug("LDAP deletion detected: {Dn}", dn);
                    }
                }

                _knownDns.Clear();
                foreach (var dn in allDns)
                    _knownDns.Add(dn);
            }
            finally
            {
                _endpoint.ReleaseConnection(conn);
            }
        }
        else
        {
            // Incremental: add new entries to known set
            foreach (var dn in currentDns)
                _knownDns.Add(dn);
        }
    }

    private async Task<List<LdapEntry>> SearchEntriesAsync(
        LdapConnection conn, string filter, CancellationToken ct)
    {
        var scope = MapScope(_options.ParseScope());
        var attributes = _options.ParseAttributes();

        var results = new List<LdapEntry>();

        var searchResults = await conn.SearchAsync(_endpoint.BaseDn, scope, filter, attributes, false, ct)
            .ConfigureAwait(false);

        while (await searchResults.HasMoreAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            NovellEntry novellEntry;
            try
            {
                novellEntry = await searchResults.NextAsync(ct).ConfigureAwait(false);
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.SizeLimitExceeded)
            {
                Logger?.LogWarning("LDAP size limit exceeded during poll for {BaseDn}", _endpoint.BaseDn);
                break;
            }

            results.Add(ConvertEntry(novellEntry));
        }

        return results;
    }

    private async Task<string?> GetInitialMarkerAsync(LdapChangeTrackingMode mode, CancellationToken ct)
    {
        if (mode == LdapChangeTrackingMode.ModifyTimestamp)
            return DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "Z";

        if (mode == LdapChangeTrackingMode.Usn)
        {
            // Read highestCommittedUSN from RootDSE
            var conn = await _endpoint.AcquireConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var searchResults = await conn.SearchAsync("", LdapConnection.ScopeBase, "(objectClass=*)",
                    new[] { "highestCommittedUSN" }, false, ct).ConfigureAwait(false);

                if (await searchResults.HasMoreAsync(ct).ConfigureAwait(false))
                {
                    var entry = await searchResults.NextAsync(ct).ConfigureAwait(false);
                    var attr = entry.Get("highestCommittedUSN");
                    return attr?.StringValue;
                }
            }
            finally
            {
                _endpoint.ReleaseConnection(conn);
            }
        }

        return null;
    }

    private static LdapEntry ConvertEntry(NovellEntry entry)
    {
        var result = new LdapEntry { Dn = entry.Dn };
        var attrSet = entry.GetAttributeSet();
        foreach (LdapAttribute attr in attrSet)
        {
            var values = attr.StringValueArray;
            result.Attributes[attr.Name] = values.Length == 1 ? (object)values[0] : values;
        }
        return result;
    }

    private static int MapScope(LdapSearchScope scope) => scope switch
    {
        LdapSearchScope.Base => LdapConnection.ScopeBase,
        LdapSearchScope.OneLevel => LdapConnection.ScopeOne,
        _ => LdapConnection.ScopeSub
    };
}
