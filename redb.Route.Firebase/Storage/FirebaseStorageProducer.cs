using System.Text;
using System.Text.Json;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Firebase Storage producer — upload, download, delete, list, and get metadata
/// for objects in a Google Cloud Storage (Firebase Storage) bucket.
/// </summary>
internal sealed class FirebaseStorageProducer : ConnectableProducer
{
    private readonly FirebaseStorageEndpoint _endpoint;
    private readonly FirebaseStorageEndpointOptions _options;
    private StorageClient? _client;

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => _endpoint.Uri.NormalizedKey;

    internal FirebaseStorageProducer(FirebaseStorageEndpoint endpoint, FirebaseStorageEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _client = await _endpoint.GetOrCreateClientAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        switch (_options.Operation)
        {
            case FirebaseStorageOperationType.Upload:
                await ProcessUpload(exchange, ct).ConfigureAwait(false);
                break;
            case FirebaseStorageOperationType.Download:
                await ProcessDownload(exchange, ct).ConfigureAwait(false);
                break;
            case FirebaseStorageOperationType.Delete:
                await ProcessDelete(exchange, ct).ConfigureAwait(false);
                break;
            case FirebaseStorageOperationType.List:
                await ProcessList(exchange, ct).ConfigureAwait(false);
                break;
            case FirebaseStorageOperationType.GetMetadata:
                await ProcessGetMetadata(exchange, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown Storage operation: {_options.Operation}");
        }
    }

    private async Task ProcessUpload(IExchange exchange, CancellationToken ct)
    {
        var objectName = ResolveObjectName(exchange)
                         ?? Guid.NewGuid().ToString();

        // Prepend prefix if present in URI
        if (_endpoint.ObjectPrefix is not null)
            objectName = _endpoint.ObjectPrefix + objectName;

        var bodyIsStream = exchange.In.Body is Stream;
        var stream = GetBodyStream(exchange);
        try
        {
            var contentType = _options.ContentType ?? exchange.In.ContentType ?? "application/octet-stream";

            // Upload with metadata in a single API call (includes CacheControl if set)
            var obj = await _client!.UploadObjectAsync(
                new Google.Apis.Storage.v1.Data.Object
                {
                    Bucket = _endpoint.BucketName,
                    Name = objectName,
                    ContentType = contentType,
                    CacheControl = _options.CacheControl
                },
                stream, cancellationToken: ct).ConfigureAwait(false);

            exchange.In.Headers[FirebaseStorageHeaders.ObjectName] = obj.Name;
            exchange.In.Headers[FirebaseStorageHeaders.BucketName] = obj.Bucket;
            exchange.In.Headers[FirebaseStorageHeaders.Md5Hash] = obj.Md5Hash;
            exchange.In.Headers[FirebaseStorageHeaders.Generation] = obj.Generation;
            exchange.In.Headers[FirebaseStorageHeaders.MediaLink] = obj.MediaLink;
            _endpoint.RecordMessageOut();
        }
        finally
        {
            // Dispose only streams we created (not the caller's original stream)
            if (!bodyIsStream)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessDownload(IExchange exchange, CancellationToken ct)
    {
        var objectName = exchange.In.GetHeader<string>(FirebaseStorageHeaders.ObjectName)
                         ?? ResolveObjectName(exchange)
                         ?? throw new InvalidOperationException("ObjectName header or option is required for Download");

        var ms = new MemoryStream();
        try
        {
            // Run download and metadata retrieval in parallel
            var downloadTask = _client!.DownloadObjectAsync(
                _endpoint.BucketName, objectName, ms, cancellationToken: ct);
            var metaTask = _client.GetObjectAsync(
                _endpoint.BucketName, objectName, cancellationToken: ct);
            await Task.WhenAll(downloadTask, metaTask).ConfigureAwait(false);

            if (_options.StreamBody)
            {
                ms.Position = 0;
                exchange.Out = new Message(ms);
                ms = null!; // Transfer ownership to exchange
            }
            else
            {
                exchange.Out = new Message(ms.ToArray());
            }

            SetMetadataHeaders(exchange.Out, metaTask.Result);
            exchange.Pattern = ExchangePattern.InOut;
            _endpoint.RecordMessageIn();
        }
        finally
        {
            ms?.Dispose();
        }
    }

    private async Task ProcessDelete(IExchange exchange, CancellationToken ct)
    {
        var objectName = exchange.In.GetHeader<string>(FirebaseStorageHeaders.ObjectName)
                         ?? ResolveObjectName(exchange)
                         ?? throw new InvalidOperationException("ObjectName header or option is required for Delete");

        await _client!.DeleteObjectAsync(_endpoint.BucketName, objectName,
            cancellationToken: ct).ConfigureAwait(false);
        _endpoint.RecordMessageOut();
    }

    private async Task ProcessList(IExchange exchange, CancellationToken ct)
    {
        var prefix = _options.Prefix ?? _endpoint.ObjectPrefix;
        var objects = _client!.ListObjectsAsync(_endpoint.BucketName, prefix);
        var list = new List<object>();

        await foreach (var obj in objects.ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = obj.Name,
                ["Size"] = obj.Size,
                ["ContentType"] = obj.ContentType,
                ["TimeCreated"] = GcsDateTimeHelper.SafeParse(() => obj.TimeCreatedDateTimeOffset, obj.TimeCreatedRaw)
            });
        }

        exchange.Out = new Message(list);
        exchange.Out.Headers[FirebaseStorageHeaders.ObjectCount] = list.Count;
        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageIn();
    }

    private async Task ProcessGetMetadata(IExchange exchange, CancellationToken ct)
    {
        var objectName = exchange.In.GetHeader<string>(FirebaseStorageHeaders.ObjectName)
                         ?? ResolveObjectName(exchange)
                         ?? throw new InvalidOperationException("ObjectName header or option is required for GetMetadata");

        var meta = await _client!.GetObjectAsync(_endpoint.BucketName, objectName,
            cancellationToken: ct).ConfigureAwait(false);

        exchange.Out = new Message(meta.Metadata);
        SetMetadataHeaders(exchange.Out, meta);
        exchange.Pattern = ExchangePattern.InOut;
        _endpoint.RecordMessageIn();
    }

    // ── Helpers ──

    private string? ResolveObjectName(IExchange exchange)
    {
        return _options.ObjectName?.Resolve(exchange);
    }

    private static Stream GetBodyStream(IExchange exchange)
    {
        return exchange.In.Body switch
        {
            byte[] bytes => new MemoryStream(bytes),
            Stream s => s,
            string str => new MemoryStream(Encoding.UTF8.GetBytes(str)),
            null => throw new InvalidOperationException("Upload requires a non-null body"),
            _ => new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(exchange.In.Body)))
        };
    }

    private static void SetMetadataHeaders(IMessage message, Google.Apis.Storage.v1.Data.Object meta)
    {
        message.Headers[FirebaseStorageHeaders.ObjectName] = meta.Name;
        message.Headers[FirebaseStorageHeaders.BucketName] = meta.Bucket;
        message.Headers[FirebaseStorageHeaders.ContentType] = meta.ContentType;
        message.Headers[FirebaseStorageHeaders.ContentLength] = meta.Size;
        message.Headers[FirebaseStorageHeaders.Md5Hash] = meta.Md5Hash;
        message.Headers[FirebaseStorageHeaders.Crc32c] = meta.Crc32c;
        message.Headers[FirebaseStorageHeaders.Generation] = meta.Generation;
        message.Headers[FirebaseStorageHeaders.MetaGeneration] = meta.Metageneration;
        message.Headers[FirebaseStorageHeaders.TimeCreated] = GcsDateTimeHelper.SafeParse(() => meta.TimeCreatedDateTimeOffset, meta.TimeCreatedRaw);
        message.Headers[FirebaseStorageHeaders.Updated] = GcsDateTimeHelper.SafeParse(() => meta.UpdatedDateTimeOffset, meta.UpdatedRaw);
        message.Headers[FirebaseStorageHeaders.MediaLink] = meta.MediaLink;

        if (meta.Metadata is not null)
        {
            foreach (var (key, value) in meta.Metadata)
                message.Headers[FirebaseStorageHeaders.MetadataPrefix + key] = value;
        }
    }
}
