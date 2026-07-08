using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.File;

/// <summary>
/// File component. Scheme: "file".
/// Provides file system polling (consumer) and atomic file writing (producer).
/// URI: file:///C:/input?include=*.csv&amp;delay=5000&amp;noop=true
/// URI: file:///C:/output?fileName=${header.orderId}.json&amp;fileExist=Override
/// </summary>
public class FileComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "file";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new FileEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new FileEndpoint(uri, this, options);
    }
}

/// <summary>
/// File endpoint. Creates either a consumer (for polling) or a producer (for writing).
/// The path part of the URI is the base directory.
/// </summary>
public class FileEndpoint : EndpointBase<FileEndpointOptions>
{
    /// <summary>Creates a file endpoint.</summary>
    public FileEndpoint(EndpointUri uri, FileComponent component, FileEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The base directory path derived from the endpoint URI.</summary>
    public string DirectoryPath => NormalizeDirectoryPath(Uri.Path);

    /// <summary>Gets the endpoint options for external access.</summary>
    internal FileEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new FileProducer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new FileConsumer(this, processor, Options);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        // Handle URI-style paths: remove leading slash for Windows absolute paths
        // e.g. "/C:/input" -> "C:/input", "/home/user/input" stays as-is on Linux
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path[1..]; // Remove leading '/'
        }

        return Path.GetFullPath(path);
    }
}
