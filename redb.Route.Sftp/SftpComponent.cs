using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Sftp;

/// <summary>
/// SFTP transport component for redb.Route.
/// Scheme: <c>sftp</c>.
/// <para>
/// URI format: <c>sftp:///remote/path?host=server&amp;port=22&amp;username=admin&amp;password=secret</c>
/// </para>
/// <para>
/// Provides enterprise-grade SFTP integration powered by SSH.NET:
/// polling consumer with glob filtering, idempotency, pre/post-move;
/// atomic producer with temp-file upload, chmod, and auto-create directories.
/// Supports password, public-key, and keyboard-interactive authentication plus proxy tunneling.
/// </para>
/// </summary>
public sealed class SftpComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "sftp";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new SftpEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // Named ConnectionFactory keeps the password / key passphrase / proxy password
        // out of the route URI.
        if (!string.IsNullOrEmpty(options.ConnectionFactory) && Context is not null)
        {
            var factory = Context.GetFromRegistry<SftpConnectionFactory>(options.ConnectionFactory);
            if (factory is not null)
                factory.ApplyTo(options, uri);
            else
                Logger?.LogWarning(
                    "SFTP: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                    options.ConnectionFactory);
        }

        options.Validate();

        return new SftpEndpoint(uri, this, options);
    }
}

/// <summary>
/// SFTP endpoint. Creates either a consumer (for polling a remote directory)
/// or a producer (for uploading files). The path part of the URI is the remote base directory.
/// </summary>
public sealed class SftpEndpoint : EndpointBase<SftpEndpointOptions>
{
    /// <summary>Creates an SFTP endpoint.</summary>
    public SftpEndpoint(EndpointUri uri, SftpComponent component, SftpEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The remote base directory path derived from the endpoint URI.</summary>
    public string RemotePath => NormalizeRemotePath(Uri.Path);

    /// <summary>Gets the endpoint options for external access.</summary>
    internal SftpEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new SftpProducer(this, Options, new SftpFileOperations(Options));
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new SftpConsumer(this, processor, Options, new SftpFileOperations(Options));
    }

    private static string NormalizeRemotePath(string path)
    {
        // Ensure leading slash for absolute SFTP paths
        if (string.IsNullOrEmpty(path))
            return "/";

        // Normalize double slashes
        while (path.Contains("//"))
            path = path.Replace("//", "/");

        // Ensure leading slash
        if (!path.StartsWith('/'))
            path = "/" + path;

        // Remove trailing slash (unless root)
        if (path.Length > 1 && path.EndsWith('/'))
            path = path[..^1];

        return path;
    }
}
