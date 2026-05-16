using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Ldap;

/// <summary>
/// LDAP endpoint. Parses the operation type and base DN from the URI path.
/// Manages a connection pool for LDAP operations.
/// <para>
/// URI path format: <c>OPERATION:baseDn</c> (e.g. <c>SEARCH:dc=example,dc=com</c>).
/// </para>
/// </summary>
public sealed class LdapEndpoint : EndpointBase<LdapEndpointOptions>, IDisposable
{
    private readonly ConcurrentBag<LdapConnection> _pool = new();
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    /// <summary>Parsed LDAP operation type.</summary>
    public LdapOperationType OperationType { get; }

    /// <summary>Base DN or target DN parsed from the URI path.</summary>
    public string BaseDn { get; }

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options (exposed for producer/consumer).</summary>
    internal LdapEndpointOptions EndpointOptions => Options;

    /// <summary>Creates an LDAP endpoint.</summary>
    public LdapEndpoint(EndpointUri uri, LdapComponent component, LdapEndpointOptions options)
        : base(uri, component, options)
    {
        Logger = component.Logger;
        _semaphore = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
        (OperationType, BaseDn) = ParsePath(uri.Path);
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new LdapProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        if (OperationType != LdapOperationType.WATCH)
            throw new InvalidOperationException(
                $"LDAP consumer is only supported for WATCH operation, got {OperationType}.");
        return new LdapConsumer(this, processor, Options);
    }

    /// <summary>
    /// Acquires a bound LDAP connection from the pool.
    /// If no idle connection is available, creates a new one.
    /// </summary>
    internal async Task<LdapConnection> AcquireConnectionAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Service-account authenticated endpoints must never reuse a connection
            // that is merely TCP-connected but no longer bound on the server side.
            // AD can report "successful bind must be completed" in that state.
            if (!string.IsNullOrEmpty(Options.BindDn))
                return await CreateConnectionAsync(ct).ConfigureAwait(false);

            // Try reuse a pooled connection
            while (_pool.TryTake(out var conn))
            {
                if (conn.Connected)
                    return conn;
                try { conn.Dispose(); } catch { /* discard broken */ }
            }

            // Create new connection
            return await CreateConnectionAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Returns a connection to the pool for reuse.
    /// Disposes the connection if it is no longer connected.
    /// </summary>
    internal void ReleaseConnection(LdapConnection conn)
    {
        if (!string.IsNullOrEmpty(Options.BindDn))
        {
            try { conn.Dispose(); } catch { /* authenticated connection is not pooled */ }
            _semaphore.Release();
            return;
        }

        if (conn.Connected && !_disposed)
            _pool.Add(conn);
        else
            try { conn.Dispose(); } catch { /* suppress */ }

        _semaphore.Release();
    }

    /// <inheritdoc />
    public override Task Stop(CancellationToken ct = default)
    {
        Dispose();
        Logger?.LogInformation("LDAP endpoint stopped: {Uri}", Uri);
        return Task.CompletedTask;
    }

    /// <summary>Disposes all pooled connections and the semaphore.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        while (_pool.TryTake(out var conn))
        {
            try { conn.Dispose(); } catch { /* suppress cleanup errors */ }
        }
        _semaphore.Dispose();
    }

    private async Task<LdapConnection> CreateConnectionAsync(CancellationToken ct)
    {
        var connOptions = BuildConnectionOptions();
        var conn = new LdapConnection(connOptions);

        // Configure protocol version and referral following
        conn.Constraints = new LdapSearchConstraints
        {
            ReferralFollowing = Options.FollowReferrals
        };

        // Connect
        var host = Options.Server;
        var port = Options.EffectivePort;

        await conn.ConnectAsync(host, port, ct).ConfigureAwait(false);

        // STARTTLS upgrade on plain connection (mutually exclusive with LDAPS)
        if (Options.StartTls)
            await conn.StartTlsAsync(ct).ConfigureAwait(false);

        // Bind with service account if credentials provided
        if (!string.IsNullOrEmpty(Options.BindDn))
        {
            ct.ThrowIfCancellationRequested();
            await conn.BindAsync(Options.BindDn, Options.BindPassword ?? "", ct).ConfigureAwait(false);
        }

        return conn;
    }

    /// <summary>
    /// Builds Novell LdapConnectionOptions from our endpoint options,
    /// wiring SSL, certificate validation, and client certificates.
    /// </summary>
    internal LdapConnectionOptions BuildConnectionOptions()
    {
        var opts = new LdapConnectionOptions();

        // LDAPS: enable SSL on the socket layer (port 636 by default)
        if (Options.Ssl)
            opts.UseSsl();

        // Skip server certificate validation (development only!)
        if (Options.SkipCertificateValidation)
            opts.ConfigureRemoteCertificateValidationCallback(
                (sender, certificate, chain, sslPolicyErrors) => true);

        // Client certificate for mutual TLS
        if (!string.IsNullOrEmpty(Options.ClientCertPath))
        {
#if NET9_0_OR_GREATER
            var cert = string.IsNullOrEmpty(Options.ClientCertPassword)
                ? X509CertificateLoader.LoadCertificateFromFile(Options.ClientCertPath)
                : X509CertificateLoader.LoadPkcs12FromFile(Options.ClientCertPath, Options.ClientCertPassword);
#else
            var cert = string.IsNullOrEmpty(Options.ClientCertPassword)
                ? new X509Certificate2(Options.ClientCertPath)
                : new X509Certificate2(Options.ClientCertPath, Options.ClientCertPassword);
#endif
            opts.ConfigureClientCertificates(new[] { cert });
        }

        return opts;
    }

    /// <summary>
    /// Parses the URI path into (OperationType, BaseDn).
    /// Expected format: <c>OPERATION:dn,segments</c>
    /// </summary>
    internal static (LdapOperationType Operation, string BaseDn) ParsePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("LDAP URI path cannot be empty. Expected format: OPERATION:baseDn");

        // Find first colon — separates operation from base DN
        var colonIndex = path.IndexOf(':');
        if (colonIndex <= 0)
            throw new ArgumentException(
                $"Invalid LDAP URI path: '{path}'. Expected format: OPERATION:baseDn (e.g. SEARCH:dc=example,dc=com)");

        var operationStr = path[..colonIndex];
        var baseDn = path[(colonIndex + 1)..];

        if (!Enum.TryParse<LdapOperationType>(operationStr, ignoreCase: true, out var operation))
            throw new ArgumentException(
                $"Unknown LDAP operation: '{operationStr}'. Supported: {string.Join(", ", Enum.GetNames<LdapOperationType>())}");

        return (operation, baseDn);
    }
}
