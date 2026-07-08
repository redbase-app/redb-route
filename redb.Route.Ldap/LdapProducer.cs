using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;
using Novell.Directory.Ldap.Controls;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using NovellEntry = Novell.Directory.Ldap.LdapEntry;

namespace redb.Route.Ldap;

/// <summary>
/// LDAP producer. Dispatches to the correct LDAP operation based on <see cref="LdapOperationType"/>.
/// Supports all operations: Search, Add, Modify, Delete, Compare, Rename, Bind.
/// </summary>
internal sealed class LdapProducer : ConnectableProducer
{
    private readonly LdapEndpoint _endpoint;
    private readonly LdapEndpointOptions _options;

    /// <summary>Creates an LDAP producer.</summary>
    public LdapProducer(LdapEndpoint endpoint, LdapEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"ldap:{_endpoint.OperationType}:{_endpoint.BaseDn}";

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        // Warm up the pool with one connection to verify connectivity
        var conn = await _endpoint.AcquireConnectionAsync(ct).ConfigureAwait(false);
        _endpoint.ReleaseConnection(conn);
        Logger?.LogInformation("LDAP producer connected: {Name}", ProducerName);
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        using var activity = RouteActivitySource.Source.StartActivity($"ldap.{_endpoint.OperationType}");
        activity?.SetTag("ldap.operation", _endpoint.OperationType.ToString());
        activity?.SetTag("ldap.base_dn", _endpoint.BaseDn);
        activity?.SetTag("ldap.server", _options.Server);
        activity?.SetTag("ldap.port", _options.EffectivePort);
        activity?.SetTag("ldap.ssl", _options.Ssl || _options.StartTls);

        // BIND uses a fresh isolated connection — NOT from pool
        if (_endpoint.OperationType == LdapOperationType.BIND)
        {
            await ProcessBindAsync(exchange, activity, ct).ConfigureAwait(false);
            return;
        }

        var conn = await _endpoint.AcquireConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await DispatchOperationAsync(conn, exchange, activity, ct).ConfigureAwait(false);
        }
        catch (LdapException ex)
        {
            Logger?.LogError(ex, "LDAP {Operation} failed: baseDn={BaseDn}, resultCode={Code}",
                _endpoint.OperationType, _endpoint.BaseDn, ex.ResultCode);
            _endpoint.RecordError(ex);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogError(ex, "LDAP {Operation} failed: baseDn={BaseDn}",
                _endpoint.OperationType, _endpoint.BaseDn);
            _endpoint.RecordError(ex);
            throw;
        }
        finally
        {
            _endpoint.ReleaseConnection(conn);
        }
    }

    private Task DispatchOperationAsync(
        LdapConnection conn, IExchange exchange, Activity? activity, CancellationToken ct)
    {
        return _endpoint.OperationType switch
        {
            LdapOperationType.SEARCH => ProcessSearchAsync(conn, exchange, activity, ct),
            LdapOperationType.ADD => ProcessAddAsync(conn, exchange, activity, ct),
            LdapOperationType.MODIFY => ProcessModifyAsync(conn, exchange, activity, ct),
            LdapOperationType.DELETE => ProcessDeleteAsync(conn, exchange, activity, ct),
            LdapOperationType.COMPARE => ProcessCompareAsync(conn, exchange, activity, ct),
            LdapOperationType.RENAME => ProcessRenameAsync(conn, exchange, activity, ct),
            _ => throw new NotSupportedException($"LDAP operation not supported: {_endpoint.OperationType}")
        };
    }

    // ────────────────────────────────────────────────────────
    //  SEARCH
    // ────────────────────────────────────────────────────────

    private async Task ProcessSearchAsync(
        LdapConnection conn, IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var filter = _options.Filter?.Resolve(exchange) ?? "(objectClass=*)";
        var baseDn = _endpoint.BaseDn;
        var scope = MapScope(_options.ParseScope());
        var attributes = _options.ParseAttributes();
        var pageSize = _options.PageSize;

        activity?.SetTag("ldap.filter", filter);
        activity?.SetTag("ldap.scope", _options.Scope);

        var results = new List<LdapEntry>();
        var sw = Stopwatch.StartNew();

        // Set up search constraints
        var constraints = new LdapSearchConstraints
        {
            MaxResults = _options.SizeLimit > 0 ? _options.SizeLimit : 0,
            ServerTimeLimit = _options.TimeLimit > 0 ? _options.TimeLimit : 0,
            ReferralFollowing = _options.FollowReferrals
        };

        // Add paged results control unless explicitly disabled (PageSize = 0).
        if (pageSize > 0)
        {
            var pageControl = new SimplePagedResultsControl(pageSize, Array.Empty<byte>());
            constraints.SetControls(pageControl);
        }

        byte[]? cookie = null;

        do
        {
            ct.ThrowIfCancellationRequested();

            if (pageSize > 0 && cookie != null)
            {
                var nextPageControl = new SimplePagedResultsControl(pageSize, cookie);
                constraints.SetControls(nextPageControl);
            }

            var searchResults = await conn.SearchAsync(baseDn, scope, filter, attributes, false, constraints, ct)
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
                    Logger?.LogWarning("LDAP size limit exceeded for search on {BaseDn}", baseDn);
                    break;
                }
                catch (LdapReferralException ex) when (!_options.FollowReferrals)
                {
                    Logger?.LogDebug(ex, "LDAP referral ignored for search on {BaseDn}", baseDn);
                    break;
                }

                results.Add(ConvertEntry(novellEntry));
            }

            // Extract cookie for next page
            cookie = GetPageCookie(searchResults);

        } while (cookie is { Length: > 0 });

        sw.Stop();

        activity?.SetTag("ldap.result_count", results.Count);

        // Set exchange output
        _endpoint.RecordMessageOut();

        exchange.In.Body = results;
        exchange.In.Headers[LdapHeaders.ResultCount] = results.Count;
        exchange.In.Headers[LdapHeaders.SearchTime] = sw.ElapsedMilliseconds;
        exchange.In.Headers[LdapHeaders.BaseDn] = baseDn;
        exchange.In.Headers[LdapHeaders.Filter] = filter;
        exchange.In.Headers[LdapHeaders.Scope] = _options.Scope;
        exchange.In.Headers[LdapHeaders.Server] = _options.Server;
        exchange.In.Headers[LdapHeaders.Port] = _options.EffectivePort;
        exchange.In.Headers[LdapHeaders.Ssl] = _options.Ssl || _options.StartTls;
    }

    // ────────────────────────────────────────────────────────
    //  ADD
    // ────────────────────────────────────────────────────────

    private async Task ProcessAddAsync(
        LdapConnection conn, IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var dn = exchange.In.GetHeader<string>(LdapHeaders.Dn) ?? _endpoint.BaseDn;

        if (exchange.In.Body is not IDictionary<string, object> attrs || attrs.Count == 0)
            throw new InvalidOperationException(
                "ADD operation requires exchange.In.Body to be a Dictionary<string, object> with at least one attribute.");

        var attrSet = new LdapAttributeSet();
        foreach (var (key, value) in attrs)
        {
            var ldapAttr = value switch
            {
                string s => new LdapAttribute(key, s),
                string[] arr => new LdapAttribute(key, arr),
                byte[] bytes => new LdapAttribute(key, bytes),
                IEnumerable<string> seq => new LdapAttribute(key, seq.ToArray()),
                _ => new LdapAttribute(key, value?.ToString() ?? "")
            };
            attrSet.Add(ldapAttr);
        }

        var entry = new NovellEntry(dn, attrSet);
        await conn.AddAsync(entry, ct).ConfigureAwait(false);

        activity?.SetTag("ldap.dn", dn);
        _endpoint.RecordMessageOut();

        exchange.In.Body = dn;
        exchange.In.Headers[LdapHeaders.Dn] = dn;

        Logger?.LogDebug("LDAP ADD completed: {Dn}", dn);
    }

    // ────────────────────────────────────────────────────────
    //  MODIFY
    // ────────────────────────────────────────────────────────

    private async Task ProcessModifyAsync(
        LdapConnection conn, IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var dn = exchange.In.GetHeader<string>(LdapHeaders.Dn) ?? _endpoint.BaseDn;

        LdapModification[] mods;

        if (exchange.In.Body is LdapModification[] explicitMods)
        {
            mods = explicitMods;
        }
        else if (exchange.In.Body is IDictionary<string, object> attrs && attrs.Count > 0)
        {
            // Dictionary = REPLACE semantics for each attribute
            mods = attrs.Select(kv =>
            {
                var attr = kv.Value switch
                {
                    string s => new LdapAttribute(kv.Key, s),
                    string[] arr => new LdapAttribute(kv.Key, arr),
                    byte[] bytes => new LdapAttribute(kv.Key, bytes),
                    IEnumerable<string> seq => new LdapAttribute(kv.Key, seq.ToArray()),
                    _ => new LdapAttribute(kv.Key, kv.Value?.ToString() ?? "")
                };
                return new LdapModification(LdapModification.Replace, attr);
            }).ToArray();
        }
        else
        {
            throw new InvalidOperationException(
                "MODIFY operation requires exchange.In.Body to be a Dictionary<string, object> or LdapModification[].");
        }

        await conn.ModifyAsync(dn, mods, ct).ConfigureAwait(false);

        activity?.SetTag("ldap.dn", dn);
        activity?.SetTag("ldap.mod_count", mods.Length);
        _endpoint.RecordMessageOut();

        exchange.In.Body = dn;
        exchange.In.Headers[LdapHeaders.Dn] = dn;
        exchange.In.Headers[LdapHeaders.ModCount] = mods.Length;

        Logger?.LogDebug("LDAP MODIFY completed: {Dn}, {Count} modifications", dn, mods.Length);
    }

    // ────────────────────────────────────────────────────────
    //  DELETE
    // ────────────────────────────────────────────────────────

    private async Task ProcessDeleteAsync(
        LdapConnection conn, IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var dn = exchange.In.GetHeader<string>(LdapHeaders.Dn) ?? _endpoint.BaseDn;

        await conn.DeleteAsync(dn, ct).ConfigureAwait(false);

        activity?.SetTag("ldap.dn", dn);
        _endpoint.RecordMessageOut();

        exchange.In.Body = dn;
        exchange.In.Headers[LdapHeaders.Dn] = dn;
        exchange.In.Headers[LdapHeaders.Deleted] = true;

        Logger?.LogDebug("LDAP DELETE completed: {Dn}", dn);
    }

    // ────────────────────────────────────────────────────────
    //  COMPARE
    // ────────────────────────────────────────────────────────

    private async Task ProcessCompareAsync(
        LdapConnection conn, IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var dn = exchange.In.GetHeader<string>(LdapHeaders.Dn) ?? _endpoint.BaseDn;
        var attrName = exchange.In.GetHeader<string>(LdapHeaders.CompareAttribute)
            ?? throw new InvalidOperationException(
                $"COMPARE operation requires header '{LdapHeaders.CompareAttribute}'.");
        var attrValue = exchange.In.GetHeader<string>(LdapHeaders.CompareValue)
            ?? throw new InvalidOperationException(
                $"COMPARE operation requires header '{LdapHeaders.CompareValue}'.");

        var attr = new LdapAttribute(attrName, attrValue);
        var result = await conn.CompareAsync(dn, attr, ct).ConfigureAwait(false);

        activity?.SetTag("ldap.dn", dn);
        activity?.SetTag("ldap.compare_result", result);
        _endpoint.RecordMessageOut();

        exchange.In.Body = result;
        exchange.In.Headers[LdapHeaders.Dn] = dn;
        exchange.In.Headers[LdapHeaders.CompareResult] = result;

        Logger?.LogDebug("LDAP COMPARE {Dn}.{Attr} = {Result}", dn, attrName, result);
    }

    // ────────────────────────────────────────────────────────
    //  RENAME
    // ────────────────────────────────────────────────────────

    private async Task ProcessRenameAsync(
        LdapConnection conn, IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var dn = exchange.In.GetHeader<string>(LdapHeaders.Dn) ?? _endpoint.BaseDn;
        var newRdn = exchange.In.GetHeader<string>(LdapHeaders.NewRdn)
            ?? throw new InvalidOperationException(
                $"RENAME operation requires header '{LdapHeaders.NewRdn}'.");
        var newParentDn = exchange.In.GetHeader<string>(LdapHeaders.NewParentDn);

        if (newParentDn != null)
            await conn.RenameAsync(dn, newRdn, newParentDn, true, ct).ConfigureAwait(false);
        else
            await conn.RenameAsync(dn, newRdn, true, ct).ConfigureAwait(false);

        // Compute new DN
        var parentDn = newParentDn ?? GetParentDn(dn);
        var resultDn = string.IsNullOrEmpty(parentDn) ? newRdn : $"{newRdn},{parentDn}";

        activity?.SetTag("ldap.old_dn", dn);
        activity?.SetTag("ldap.new_dn", resultDn);
        _endpoint.RecordMessageOut();

        exchange.In.Body = resultDn;
        exchange.In.Headers[LdapHeaders.OldDn] = dn;
        exchange.In.Headers[LdapHeaders.NewDn] = resultDn;
        exchange.In.Headers[LdapHeaders.NewRdn] = newRdn;

        Logger?.LogDebug("LDAP RENAME {OldDn} → {NewDn}", dn, resultDn);
    }

    // ────────────────────────────────────────────────────────
    //  BIND (user authentication)
    // ────────────────────────────────────────────────────────

    private async Task ProcessBindAsync(
        IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var authDn = exchange.In.GetHeader<string>(LdapHeaders.AuthDn)
            ?? throw new InvalidOperationException(
                $"BIND operation requires header '{LdapHeaders.AuthDn}'.");
        var authPassword = exchange.In.GetHeader<string>(LdapHeaders.AuthPassword)
            ?? throw new InvalidOperationException(
                $"BIND operation requires header '{LdapHeaders.AuthPassword}'.");

        try
        {
            // Use endpoint's connection options to get proper SSL/TLS configuration
            var connOptions = _endpoint.BuildConnectionOptions();
            using var conn = new LdapConnection(connOptions);
            await conn.ConnectAsync(_options.Server, _options.EffectivePort, ct).ConfigureAwait(false);

            if (_options.StartTls)
                await conn.StartTlsAsync(ct).ConfigureAwait(false);

            await conn.BindAsync(_options.ProtocolVersion, authDn, authPassword, ct).ConfigureAwait(false);

            activity?.SetTag("ldap.bind_result", 0);
            _endpoint.RecordMessageOut();

            exchange.In.Body = true;
            exchange.In.Headers[LdapHeaders.BindResult] = 0;
            exchange.In.Headers[LdapHeaders.AuthDn] = authDn;

            Logger?.LogDebug("LDAP BIND succeeded: {AuthDn}", authDn);
        }
        catch (LdapException ex)
        {
            activity?.SetTag("ldap.bind_result", ex.ResultCode);

            exchange.In.Body = false;
            exchange.In.Headers[LdapHeaders.BindResult] = ex.ResultCode;
            exchange.In.Headers[LdapHeaders.AuthDn] = authDn;

            Logger?.LogWarning("LDAP BIND failed for {AuthDn}: resultCode={Code}, {Message}",
                authDn, ex.ResultCode, ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            activity?.SetTag("ldap.bind_result", -1);

            exchange.In.Body = false;
            exchange.In.Headers[LdapHeaders.BindResult] = -1;
            exchange.In.Headers[LdapHeaders.AuthDn] = authDn;

            Logger?.LogWarning("LDAP BIND network error for {AuthDn}: {Error}",
                authDn, ex.Message);
        }
        finally
        {
            // Security: always clear password from headers
            exchange.In.Headers.Remove(LdapHeaders.AuthPassword);
        }
    }

    // ────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────

    private static int MapScope(LdapSearchScope scope) => scope switch
    {
        LdapSearchScope.Base => LdapConnection.ScopeBase,
        LdapSearchScope.OneLevel => LdapConnection.ScopeOne,
        LdapSearchScope.Subtree => LdapConnection.ScopeSub,
        _ => LdapConnection.ScopeSub
    };

    private static LdapEntry ConvertEntry(NovellEntry entry)
    {
        var result = new LdapEntry { Dn = entry.Dn };
        var attrSet = entry.GetAttributeSet();

        foreach (LdapAttribute attr in attrSet)
        {
            if (attr.ByteValueArray is { Length: > 0 } byteValues && IsBinaryAttribute(attr.Name))
            {
                // Binary attribute — store raw bytes
                result.Attributes[attr.Name] = byteValues.Length == 1
                    ? (object)byteValues[0]
                    : byteValues;
            }
            else
            {
                var values = attr.StringValueArray;
                result.Attributes[attr.Name] = values.Length == 1
                    ? (object)values[0]
                    : values;
            }
        }

        return result;
    }

    private static bool IsBinaryAttribute(string name)
    {
        // Well-known binary attributes in LDAP/AD
        return name.EndsWith(";binary", StringComparison.OrdinalIgnoreCase)
            || name.Equals("objectGUID", StringComparison.OrdinalIgnoreCase)
            || name.Equals("objectSid", StringComparison.OrdinalIgnoreCase)
            || name.Equals("thumbnailPhoto", StringComparison.OrdinalIgnoreCase)
            || name.Equals("jpegPhoto", StringComparison.OrdinalIgnoreCase)
            || name.Equals("userCertificate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("msExchMailboxGuid", StringComparison.OrdinalIgnoreCase)
            || name.Equals("msExchMailboxSecurityDescriptor", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? GetPageCookie(ILdapSearchResults results)
    {
        var controls = results.ResponseControls;
        if (controls == null) return null;

        foreach (var control in controls)
        {
            if (control is SimplePagedResultsControl pagedControl)
                return pagedControl.Cookie;
        }

        return null;
    }

    private static string GetParentDn(string dn)
    {
        var commaIndex = dn.IndexOf(',');
        return commaIndex >= 0 ? dn[(commaIndex + 1)..] : "";
    }
}
