using System.Diagnostics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.S3;

/// <summary>Bucket, tagging, ACL, versioning and restore operations of <see cref="S3Producer"/> (S-12 распил).</summary>
internal sealed partial class S3Producer
{
    // ═══════════════════════════════════════════════════════════════════
    //  BUCKET OPERATIONS
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessCreateBucketAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        await EnsureBucketExistsAsync(bucket, ct).ConfigureAwait(false);
        Logger?.LogDebug("S3 CreateBucket: {Bucket}", bucket);
    }

    private async Task ProcessDeleteBucketAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        await _client!.DeleteBucketAsync(bucket, ct).ConfigureAwait(false);
        Logger?.LogDebug("S3 DeleteBucket: {Bucket}", bucket);
    }

    private async Task ProcessHeadBucketAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        bool exists;
        try
        {
            await _client!.EnsureBucketExistsAsync(bucket).ConfigureAwait(false);
            exists = true;
        }
        catch
        {
            exists = false;
        }

        exchange.In.Headers[S3Headers.BucketExists] = exists;
        exchange.In.Body = exists;
    }

    private async Task ProcessListBucketsAsync(IExchange exchange, CancellationToken ct)
    {
        var response = await _client!.ListBucketsAsync(ct).ConfigureAwait(false);
        exchange.In.Body = response.Buckets.Select(b => new { b.BucketName, b.CreationDate }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TAGGING
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessGetObjectTaggingAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var response = await _client!.GetObjectTaggingAsync(new GetObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
        }, ct).ConfigureAwait(false);

        exchange.In.Body = response.Tagging.ToDictionary(t => t.Key, t => t.Value);
    }

    private async Task ProcessPutObjectTaggingAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var tags = exchange.In.GetHeader<Dictionary<string, string>>(S3Headers.ObjectTags)
                   ?? (exchange.In.Body as Dictionary<string, string>)
                   ?? throw new InvalidOperationException($"Tags must be provided via header '{S3Headers.ObjectTags}' or body (Dictionary<string, string>).");

        await _client!.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = tags.Select(kv => new Tag { Key = kv.Key, Value = kv.Value }).ToList() },
        }, ct).ConfigureAwait(false);
    }

    private async Task ProcessDeleteObjectTaggingAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        await _client!.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
        }, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ACL
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessGetObjectAclAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var response = await _client!.GetObjectAclAsync(new GetObjectAclRequest
        {
            BucketName = bucket,
            Key = key,
        }, ct).ConfigureAwait(false);

        exchange.In.Body = response.Grants;
    }

    private async Task ProcessPutObjectAclAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var cannedAclStr = exchange.In.GetHeader<string>(S3Headers.CannedAcl) ?? _options.CannedAcl.ToString();

        await _client!.PutObjectAclAsync(new PutObjectAclRequest
        {
            BucketName = bucket,
            Key = key,
            ACL = MapCannedAcl(cannedAclStr),
        }, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  VERSIONING
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessGetBucketVersioningAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);

        var response = await _client!.GetBucketVersioningAsync(new GetBucketVersioningRequest
        {
            BucketName = bucket,
        }, ct).ConfigureAwait(false);

        exchange.In.Body = response.VersioningConfig.Status?.Value ?? "Off";
        exchange.In.Headers[S3Headers.VersioningStatus] = response.VersioningConfig.Status?.Value ?? "Off";
    }

    private async Task ProcessPutBucketVersioningAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var status = exchange.In.GetHeader<string>(S3Headers.VersioningStatus) ?? "Enabled";

        await _client!.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig
            {
                Status = status.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                    ? VersionStatus.Enabled
                    : VersionStatus.Suspended,
            },
        }, ct).ConfigureAwait(false);

        Logger?.LogDebug("S3 PutBucketVersioning: {Bucket} → {Status}", bucket, status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  RESTORE
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessRestoreObjectAsync(IExchange exchange, CancellationToken ct)
    {
        var bucket = ResolveBucket(exchange);
        var key = ResolveKey(exchange);

        var days = exchange.In.GetHeader<int?>(S3Headers.RestoreDays) ?? 1;
        var tier = exchange.In.GetHeader<string>(S3Headers.RestoreTier) ?? "Standard";

        await _client!.RestoreObjectAsync(new RestoreObjectRequest
        {
            BucketName = bucket,
            Key = key,
            Days = days,
            Tier = tier switch
            {
                "Expedited" => GlacierJobTier.Expedited,
                "Bulk" => GlacierJobTier.Bulk,
                _ => GlacierJobTier.Standard,
            },
        }, ct).ConfigureAwait(false);

        Logger?.LogDebug("S3 RestoreObject: {Bucket}/{Key}, days={Days}, tier={Tier}", bucket, key, days, tier);
    }

}
