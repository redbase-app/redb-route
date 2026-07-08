using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.S3;

/// <summary>
/// S3/MinIO transport component for redb.Route.
/// Scheme: <c>s3</c>.
/// <para>
/// URI format (consumer): <c>s3://bucket-name?region=us-east-1&amp;accessKey=KEY&amp;secretKey=SECRET&amp;prefix=data/</c>
/// </para>
/// <para>
/// URI format (producer): <c>s3://bucket-name?region=us-east-1&amp;accessKey=KEY&amp;secretKey=SECRET&amp;keyName=folder/file.txt</c>
/// </para>
/// <para>
/// URI format (operation): <c>s3:CopyObject:bucket-name?region=us-east-1&amp;accessKey=KEY&amp;secretKey=SECRET</c>
/// </para>
/// <para>
/// Provides enterprise-grade object storage integration compatible with AWS S3 and MinIO:
/// polling consumer with prefix filtering, idempotency, move/delete-after-read;
/// multi-operation producer with multipart upload, SSE, presigned URLs, versioning,
/// tagging, ACL management, and more.
/// </para>
/// </summary>
public sealed class S3Component : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "s3";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new S3EndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new S3Endpoint(uri, this, options);
    }
}

/// <summary>
/// S3 endpoint. Creates either a consumer (for polling a bucket)
/// or a producer (for uploading/managing objects).
/// <para>
/// The path part of the URI is parsed as <c>[OPERATION:]bucket-name</c>.
/// When an operation prefix is present (e.g. <c>s3:CopyObject:my-bucket</c>),
/// the producer dispatches that specific operation.
/// When no operation is specified, the default is <see cref="S3OperationType.PutObject"/> for producer.
/// </para>
/// </summary>
public sealed class S3Endpoint : EndpointBase<S3EndpointOptions>, IDisposable
{
    private IAmazonS3? _client;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    /// <summary>Parsed S3 operation type (default: PutObject for producer).</summary>
    public S3OperationType OperationType { get; }

    /// <summary>Bucket name parsed from the URI path.</summary>
    public string BucketName { get; }

    /// <summary>Logger from the parent component.</summary>
    internal ILogger? Logger { get; }

    /// <summary>Typed options (exposed for consumer/producer).</summary>
    internal S3EndpointOptions EndpointOptions => Options;

    /// <summary>Creates an S3 endpoint.</summary>
    public S3Endpoint(EndpointUri uri, S3Component component, S3EndpointOptions options)
        : base(uri, component, options)
    {
        Logger = component.Logger;
        (OperationType, BucketName) = ParsePath(uri.Path);
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new S3Producer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new S3Consumer(this, processor, Options);
    }

    /// <summary>Gets or creates a shared <see cref="IAmazonS3"/> client.</summary>
    internal async Task<IAmazonS3> GetOrCreateClientAsync(CancellationToken ct = default)
    {
        if (_client is not null)
            return _client;

        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null)
                return _client;

            _client = BuildClient();

            Logger?.LogInformation("S3 client created: Region={Region}, ServiceUrl={ServiceUrl}, Bucket={Bucket}",
                Options.Region,
                string.IsNullOrEmpty(Options.ServiceUrl) ? "(default)" : Options.ServiceUrl,
                BucketName);

            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    /// <inheritdoc />
    public override async Task Stop(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            try { _client.Dispose(); }
            catch (Exception ex) { Logger?.LogWarning(ex, "Error disposing S3 client"); }
            _client = null;
        }

        Logger?.LogInformation("S3 endpoint stopped: {Uri}", Uri);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _clientLock.Dispose();
    }

    // ── Helpers ──

    private IAmazonS3 BuildClient()
    {
        // 1. Try named factory from registry
        if (!string.IsNullOrEmpty(Options.ConnectionFactory))
        {
            var component = Component as S3Component;
            var registryFactory = component?.Context?.GetFromRegistry<S3ConnectionFactory>(Options.ConnectionFactory);
            if (registryFactory is not null)
            {
                Logger?.LogDebug("S3: using ConnectionFactory '{Name}' from registry", Options.ConnectionFactory);
                return registryFactory.Build();
            }

            Logger?.LogWarning("S3: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                Options.ConnectionFactory);
        }

        // 2. Build from URI parameters
        var config = new AmazonS3Config
        {
            ForcePathStyle = Options.ForcePathStyle,
            Timeout = TimeSpan.FromMilliseconds(Options.ConnectionTimeout),
            MaxConnectionsPerServer = Options.MaxConnections,
            MaxErrorRetry = Options.RetryCount,
        };

        if (!string.IsNullOrEmpty(Options.ServiceUrl))
        {
            config.ServiceURL = Options.ServiceUrl;
            config.AuthenticationRegion = Options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(Options.Region);
        }

        if (!string.IsNullOrEmpty(Options.ProxyHost) && Options.ProxyPort > 0)
        {
            config.ProxyHost = Options.ProxyHost;
            config.ProxyPort = Options.ProxyPort;
        }

        var credentials = ResolveCredentials();
        return new AmazonS3Client(credentials, config);
    }

    private AWSCredentials ResolveCredentials()
    {
        if (Options.UseDefaultCredentialsProvider)
#pragma warning disable CS0618 // FallbackCredentialsFactory is deprecated but still functional
            return FallbackCredentialsFactory.GetCredentials();
#pragma warning restore CS0618

        if (!string.IsNullOrEmpty(Options.ProfileName))
        {
            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(Options.ProfileName, out var profileCreds))
                return profileCreds;
            throw new InvalidOperationException($"AWS profile '{Options.ProfileName}' not found.");
        }

        if (!string.IsNullOrEmpty(Options.SessionToken))
            return new SessionAWSCredentials(Options.AccessKey, Options.SecretKey, Options.SessionToken);

        return new BasicAWSCredentials(Options.AccessKey, Options.SecretKey);
    }

    /// <summary>
    /// Parses the URI path. Supports:
    /// <list type="bullet">
    ///   <item><c>bucket-name</c> → (PutObject, "bucket-name")</item>
    ///   <item><c>CopyObject:bucket-name</c> → (CopyObject, "bucket-name")</item>
    /// </list>
    /// </summary>
    private static (S3OperationType Op, string Bucket) ParsePath(string path)
    {
        // Strip leading slashes
        path = path.TrimStart('/');

        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("S3 URI must include a bucket name in the path.");

        // Try to parse "OPERATION:bucket" or "OPERATION/bucket"
        var colonIndex = path.IndexOf(':');
        if (colonIndex > 0)
        {
            var opStr = path[..colonIndex];
            var bucket = path[(colonIndex + 1)..];
            if (Enum.TryParse<S3OperationType>(opStr, ignoreCase: true, out var op) && !string.IsNullOrEmpty(bucket))
                return (op, bucket);
        }

        // No operation prefix → just bucket name, default to PutObject
        return (S3OperationType.PutObject, path);
    }
}
