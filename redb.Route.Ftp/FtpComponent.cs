using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Ftp;

/// <summary>
/// FTP transport component for redb.Route.
/// Scheme: <c>ftp</c>.
/// <para>
/// URI format: <c>ftp:///remote/path?host=server&amp;port=21&amp;username=admin&amp;password=secret</c>
/// </para>
/// <para>
/// Provides FTP integration powered by FluentFTP:
/// polling consumer with glob filtering, idempotency, pre/post-move;
/// atomic producer with temp-file upload and auto-create directories.
/// Supports passive/active mode and FTPS (FTP over TLS).
/// </para>
/// </summary>
public sealed class FtpComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "ftp";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new FtpEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // Named ConnectionFactory keeps the server password out of the route URI.
        if (!string.IsNullOrEmpty(options.ConnectionFactory) && Context is not null)
        {
            var factory = Context.GetFromRegistry<FtpConnectionFactory>(options.ConnectionFactory);
            if (factory is not null)
                factory.ApplyTo(options, uri);
            else
                Logger?.LogWarning(
                    "FTP: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                    options.ConnectionFactory);
        }

        options.Validate();

        return new FtpEndpoint(uri, this, options);
    }
}

/// <summary>
/// FTP endpoint. Creates either a consumer (for polling a remote directory)
/// or a producer (for uploading files). The path part of the URI is the remote base directory.
/// </summary>
public sealed class FtpEndpoint : EndpointBase<FtpEndpointOptions>
{
    /// <summary>Creates an FTP endpoint.</summary>
    public FtpEndpoint(EndpointUri uri, FtpComponent component, FtpEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The remote base directory path derived from the endpoint URI.</summary>
    public string RemotePath => NormalizeRemotePath(Uri.Path);

    /// <summary>Gets the endpoint options for external access.</summary>
    internal FtpEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new FtpProducer(this, Options, new FtpFileOperations(Options));
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new FtpConsumer(this, processor, Options, new FtpFileOperations(Options));
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";

        while (path.Contains("//"))
            path = path.Replace("//", "/");

        if (!path.StartsWith('/'))
            path = "/" + path;

        if (path.Length > 1 && path.EndsWith('/'))
            path = path[..^1];

        return path;
    }
}
