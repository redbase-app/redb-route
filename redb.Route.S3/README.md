# redb.Route.S3

S3/MinIO transport for the **redb.Route** ESB framework.

Polling consumer with prefix filtering, glob patterns, idempotency, done-file markers, move/delete after read.
Multi-operation producer with multipart upload, server-side encryption (SSE-S3, SSE-KMS, SSE-C), presigned URLs,
tagging, versioning, ACL management, streaming upload, and Glacier restore.

Compatible with **AWS S3** and **MinIO** (via `ForcePathStyle`).

[![NuGet](https://img.shields.io/nuget/v/redb.Route.S3?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.S3)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

| | |
|---|---|
| **Scheme** | `s3` |
| **NuGet** | `redb.Route.S3` |
| **Dependency** | AWSSDK.S3 4.0.20.3 |
| **Namespace** | `redb.Route.S3` |

---

## Quick Start

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteS3();

    // Poll MinIO bucket for new CSV files
    route.AddRoute("s3-import", r => r
        .From(S3.Bucket("incoming")
            .ServiceUrl("http://localhost:9000").ForcePathStyle()
            .AccessKey("minioadmin").SecretKey("minioadmin")
            .Prefix("data/").Include("*.csv")
            .DeleteAfterRead().Delay(30_000))
        .To("log:received"));

    // Upload to AWS S3 with SSE-KMS
    route.AddRoute("s3-export", r => r
        .From("direct:upload")
        .To(S3.Bucket("reports")
            .Region("eu-west-1")
            .AccessKey("AKIA...").SecretKey("...")
            .KeyName(Expression.Header("fileName"))
            .UseKmsEncryption("arn:aws:kms:eu-west-1:123456:key/abc")
            .StorageClass("INTELLIGENT_TIERING")));
});
```

---

## URI Format

```
s3://bucket-name?option=value&...
s3:OPERATION:bucket-name?option=value&...
```

**Consumer** — polls the bucket with `ListObjectsV2`, downloads matching objects:
```
s3://my-bucket?accessKey=KEY&secretKey=SECRET&prefix=data/&include=*.json&delay=30000
```

**Producer** — default operation `PutObject` (upload):
```
s3://my-bucket?accessKey=KEY&secretKey=SECRET&keyName=output/${header.id}.json
```

**Producer with operation** — specify operation before bucket name:
```
s3:CopyObject:my-bucket?accessKey=KEY&secretKey=SECRET
s3:CreateDownloadLink:my-bucket?accessKey=KEY&secretKey=SECRET&presignedUrlExpiration=7200000
s3:ListObjects:my-bucket?accessKey=KEY&secretKey=SECRET&prefix=archive/
```

---

## Producer Operations

22 operations available, set via URI (`s3:OPERATION:bucket`) or `redbS3.Operation` header at runtime.

### Object Operations

| Operation | Description | Key Headers |
|---|---|---|
| `PutObject` | Upload an object (default) | Body → object content |
| `GetObject` | Download an object | `redbS3.Key` → body |
| `GetObjectRange` | Download byte range | `redbS3.RangeStart`, `redbS3.RangeEnd` |
| `DeleteObject` | Delete a single object | `redbS3.Key` |
| `DeleteObjects` | Batch delete | `redbS3.KeysToDelete` (`List<string>`) |
| `CopyObject` | Copy object | `redbS3.DestinationBucket`, `redbS3.DestinationKey` |
| `ListObjects` | List objects in bucket | Prefix/Delimiter from options |
| `HeadObject` | Get metadata (no body) | `redbS3.Key` |

### Presigned URLs

| Operation | Description | Output Header |
|---|---|---|
| `CreateDownloadLink` | Pre-signed GET URL | `redbS3.PresignedUrl` |
| `CreateUploadLink` | Pre-signed PUT URL | `redbS3.PresignedUrl` |

Expiration controlled by `presignedUrlExpiration` (default: 3600000 ms = 1 hour).

### Bucket Operations

| Operation | Description | Output Header |
|---|---|---|
| `CreateBucket` | Create a new bucket | — |
| `DeleteBucket` | Delete a bucket | — |
| `HeadBucket` | Check bucket existence | `redbS3.BucketExists` |
| `ListBuckets` | List all account buckets | Body → `List<S3Bucket>` |

### Tagging

| Operation | Description | Headers |
|---|---|---|
| `GetObjectTagging` | Get tags | Body → `List<Tag>` |
| `PutObjectTagging` | Set tags | `redbS3.ObjectTags` (`Dictionary<string, string>`) |
| `DeleteObjectTagging` | Remove all tags | `redbS3.Key` |

### ACL

| Operation | Description |
|---|---|
| `GetObjectAcl` | Body → `S3AccessControlList` |
| `PutObjectAcl` | `redbS3.CannedAcl` header or option |

### Versioning

| Operation | Description | Headers |
|---|---|---|
| `GetBucketVersioning` | Get versioning config | Body → status string |
| `PutBucketVersioning` | Enable/suspend versioning | `redbS3.VersioningStatus` (`"Enabled"` / `"Suspended"`) |

### Glacier

| Operation | Description | Headers |
|---|---|---|
| `RestoreObject` | Initiate Glacier restore | `redbS3.RestoreDays`, `redbS3.RestoreTier` (`Standard`/`Bulk`/`Expedited`) |

---

## Consumer

Polls S3 bucket using `ListObjectsV2` with automatic pagination.

### Pipeline

1. **List** — `ListObjectsV2` with `Prefix`, paginated via `ContinuationToken`
2. **Filter** — glob `Include`/`Exclude` patterns, `MinAge`/`MaxAge`, folder exclusion
3. **Sort** — `SortBy` (Key, LastModified, Size + Desc variants)
4. **Limit** — `MaxMessagesPerPoll` (default: 10, 0 = unlimited)
5. **Per object**:
   - Done-file check (`DoneFileName` pattern)
   - Idempotency check (in-memory, key = `Key + ETag + Size`)
   - Download body (or stream, or metadata-only)
   - Process exchange
   - Post-process: `DeleteAfterRead` (default) or `MoveAfterRead`

### Move After Read

```csharp
.From(S3.Bucket("incoming")
    .AccessKey("KEY").SecretKey("SECRET")
    .MoveAfterRead("archive", prefix: "processed/")
    .RemovePrefixOnMove())
```

Objects are copied to the destination bucket then deleted from source.

### Done File Markers

```csharp
.From(S3.Bucket("data")
    .AccessKey("KEY").SecretKey("SECRET")
    .DoneFileName("${file:name}.done"))
```

Consumer only processes `report.csv` when `report.csv.done` exists in the same prefix.

### Idempotency

```csharp
.From(S3.Bucket("data")
    .AccessKey("KEY").SecretKey("SECRET")
    .Idempotent())
```

Uses an in-memory `ConcurrentDictionary` keyed by `Key + ETag + Size`. Custom key expression supported via `Idempotent(Expression.Header("myKey"))`.

---

## Connection & Credentials

Credential resolution order:

1. `UseDefaultCredentialsProvider` → AWS default chain (env vars, instance profile, ECS task role)
2. `ProfileName` → named profile from `~/.aws/credentials`
3. `SessionToken` set → `SessionCredentials` (STS temporary credentials)
4. `AccessKey` + `SecretKey` → `BasicAWSCredentials`

### MinIO / S3-Compatible

```csharp
S3.Bucket("my-bucket")
    .ServiceUrl("http://localhost:9000")
    .ForcePathStyle()
    .AccessKey("minioadmin")
    .SecretKey("minioadmin")
```

`ForcePathStyle` is required for MinIO, Ceph, Dell ECS, and most S3-compatible stores.

### Connection Factory

Register a named `S3ConnectionFactory` in DI, then reference by name:

```csharp
S3.Bucket("data").ConnectionFactory("myS3Factory")
```

### Proxy

```csharp
S3.Bucket("data")
    .AccessKey("KEY").SecretKey("SECRET")
    .Proxy("proxy.corp.local", 8080)
```

---

## Multipart Upload

For large files, enable multipart upload:

```csharp
.To(S3.Bucket("large-files")
    .AccessKey("KEY").SecretKey("SECRET")
    .KeyName("uploads/${header.name}")
    .MultiPartUpload()
    .PartSize(10_485_760))  // 10 MB parts (min 5 MB)
```

Automatically splits files larger than `PartSize` into parts uploaded sequentially.

---

## Server-Side Encryption

Three SSE modes:

```csharp
// SSE-S3: AWS-managed AES-256
.UseAes256Encryption()

// SSE-KMS: AWS KMS managed key
.UseKmsEncryption("arn:aws:kms:region:account:key/id")

// SSE-C: Customer-provided key
.UseCustomerEncryption("AES256", "base64-key", "base64-key-md5")
```

---

## Streaming Upload

Accumulate multiple exchanges into a single S3 object:

```csharp
.To(S3.Bucket("logs")
    .AccessKey("KEY").SecretKey("SECRET")
    .KeyName("batch/events.jsonl")
    .StreamingUpload(batchMessages: 100, batchSize: 5_242_880)
    .StreamingUploadTimeout(30_000)
    .NamingStrategy(S3NamingStrategy.Progressive))
```

| Option | Default | Description |
|---|---|---|
| `BatchMessageNumber` | 10 | Flush after N messages |
| `BatchSize` | 1 MB | Flush after N bytes |
| `BufferSize` | 1 MB | Internal buffer size |
| `StreamingUploadTimeout` | 0 (none) | Flush timeout (ms) |
| `NamingStrategy` | `Progressive` | `Progressive` (key-1, key-2, ...) or `Random` (key-{guid}) |

---

## Conditional Write

```csharp
.To(S3.Bucket("data")
    .AccessKey("KEY").SecretKey("SECRET")
    .KeyName("unique-file.json")
    .ConditionalWrite())
```

Uses `x-amz-if-none-match` — upload fails if the object key already exists.

---

## Configuration Reference

### Connection / Credentials

| Parameter | Type | Default | Description |
|---|---|---|---|
| `serviceUrl` | string | `""` | S3-compatible endpoint URL |
| `region` | string | `"us-east-1"` | AWS region |
| `accessKey` | string | `""` | AWS access key ID |
| `secretKey` | string | `""` | AWS secret access key |
| `sessionToken` | string | `""` | STS session token |
| `profileName` | string | `""` | AWS named profile |
| `forcePathStyle` | bool | `false` | Path-style URLs (required for MinIO) |
| `useDefaultCredentialsProvider` | bool | `false` | Use AWS default credential chain |
| `connectionFactory` | string | `""` | Named connection factory from DI |
| `proxyHost` | string | `""` | HTTP proxy host |
| `proxyPort` | int | `0` | HTTP proxy port |
| `connectionTimeout` | int | `30000` | Connection timeout (ms) |
| `socketTimeout` | int | `60000` | Socket/read timeout (ms) |
| `maxConnections` | int | `50` | SDK connection pool size |
| `retryCount` | int | `3` | Retry attempts |
| `retryMode` | string | `"standard"` | `"standard"` or `"adaptive"` |
| `trustAllCertificates` | bool | `false` | Trust self-signed SSL certs |

### Common

| Parameter | Type | Default | Description |
|---|---|---|---|
| `autoCreateBucket` | bool | `false` | Auto-create bucket if missing |
| `prefix` | string | `""` | Key prefix filter |
| `delimiter` | string | `""` | ListObjectsV2 delimiter |
| `includeBody` | bool | `true` | Download object body |
| `streamBody` | bool | `false` | Body as `Stream` instead of `byte[]` |
| `ignoreBody` | bool | `false` | Skip body entirely (metadata only) |
| `includeFolders` | bool | `false` | Include folder markers |

### Consumer

| Parameter | Type | Default | Description |
|---|---|---|---|
| `delay` | int | `60000` | Polling interval (ms) |
| `initialDelay` | int | `1000` | Delay before first poll (ms) |
| `maxMessagesPerPoll` | int | `10` | Max objects per poll (0 = unlimited) |
| `deleteAfterRead` | bool | `true` | Delete after successful processing |
| `moveAfterRead` | bool | `false` | Move to destination bucket instead of delete |
| `destinationBucket` | string | `""` | Target bucket for move |
| `destinationBucketPrefix` | string | `""` | Prefix for moved key |
| `destinationBucketSuffix` | string | `""` | Suffix for moved key |
| `removePrefixOnMove` | bool | `false` | Strip source prefix on move |
| `fileName` | string | `""` | Consume single file by key |
| `include` | string | `""` | Glob include pattern |
| `exclude` | string | `""` | Glob exclude pattern |
| `sendEmptyMessageWhenIdle` | bool | `false` | Heartbeat on empty poll |
| `doneFileName` | string | `""` | Done-file marker pattern |
| `sortBy` | enum | `None` | `None`, `Key`, `KeyDesc`, `LastModified`, `LastModifiedDesc`, `Size`, `SizeDesc` |
| `minAge` | long | `0` | Min object age (ms) |
| `maxAge` | long | `0` | Max object age (ms, 0 = no limit) |
| `idempotent` | bool | `false` | Enable idempotent consumer |
| `idempotentKey` | string | `""` | Custom idempotent key expression |

### Producer

| Parameter | Type | Default | Description |
|---|---|---|---|
| `keyName` | DynamicValue | — | Object key (supports `${header.xxx}` expressions) |
| `storageClass` | string | `""` | S3 storage class |
| `contentType` | string | `""` | Content-Type (empty = auto-detect) |
| `contentDisposition` | string | `""` | Content-Disposition |
| `contentEncoding` | string | `""` | Content-Encoding |
| `cacheControl` | string | `""` | Cache-Control |
| `cannedAcl` | enum | `Private` | `Private`, `PublicRead`, `PublicReadWrite`, `AuthenticatedRead`, `BucketOwnerRead`, `BucketOwnerFullControl` |
| `multiPartUpload` | bool | `false` | Enable multipart upload |
| `partSize` | long | `26214400` (25 MB) | Multipart part size (min 5 MB) |
| `deleteAfterWrite` | bool | `false` | Delete source after upload |
| `conditionalWrite` | bool | `false` | Fail if object exists |

### Server-Side Encryption

| Parameter | Type | Default | Description |
|---|---|---|---|
| `serverSideEncryption` | enum | `None` | `None`, `Aes256`, `AwsKms`, `CustomerKey` |
| `kmsKeyId` | string | `""` | KMS key ARN (required for `AwsKms`) |
| `customerAlgorithm` | string | `""` | SSE-C algorithm |
| `customerKeyId` | string | `""` | SSE-C key (base64) |
| `customerKeyMD5` | string | `""` | SSE-C key MD5 (base64) |

### Presigned URLs

| Parameter | Type | Default | Description |
|---|---|---|---|
| `presignedUrlExpiration` | long | `3600000` (1h) | URL expiration (ms) |

### Streaming Upload

| Parameter | Type | Default | Description |
|---|---|---|---|
| `streamingUploadMode` | bool | `false` | Enable streaming upload |
| `batchMessageNumber` | int | `10` | Messages per batch |
| `batchSize` | long | `1048576` (1 MB) | Max batch size (bytes) |
| `bufferSize` | long | `1048576` (1 MB) | Internal buffer size |
| `streamingUploadTimeout` | long | `0` | Batch flush timeout (ms) |
| `namingStrategy` | enum | `Progressive` | `Progressive` or `Random` |

### Metadata

| Parameter | Type | Default | Description |
|---|---|---|---|
| `metadata` | string | `""` | Comma-separated `key=value` pairs |

---

## Headers Reference

### Common (set by consumer, used by producer)

| Header | Description |
|---|---|
| `redbS3.BucketName` | Bucket name |
| `redbS3.Key` | Object key |
| `redbS3.ContentType` | Content-Type |
| `redbS3.ContentLength` | Content-Length (bytes) |
| `redbS3.ContentMD5` | Content MD5 (base64) |
| `redbS3.ContentDisposition` | Content-Disposition |
| `redbS3.ContentEncoding` | Content-Encoding |
| `redbS3.CacheControl` | Cache-Control |
| `redbS3.ETag` | Object ETag |
| `redbS3.LastModified` | Last modified timestamp |
| `redbS3.StorageClass` | Storage class |
| `redbS3.VersionId` | Version ID |
| `redbS3.ServerSideEncryption` | SSE algorithm |
| `redbS3.Operation` | Operation type (runtime override) |

### Consumer Output

| Header | Description |
|---|---|
| `redbS3.ExpirationTime` | Object expiration (lifecycle) |
| `redbS3.ReplicationStatus` | Replication status |
| `redbS3.Timestamp` | Message timestamp (Unix ms) |

### Producer Input

| Header | Description |
|---|---|
| `redbS3.DestinationBucket` | Target bucket (CopyObject) |
| `redbS3.DestinationKey` | Target key (CopyObject) |
| `redbS3.CannedAcl` | ACL preset |
| `redbS3.RangeStart` | Byte range start (GetObjectRange) |
| `redbS3.RangeEnd` | Byte range end (GetObjectRange) |
| `redbS3.PresignedUrlExpiration` | URL expiration override (ms) |
| `redbS3.KeysToDelete` | `List<string>` of keys (DeleteObjects) |
| `redbS3.ObjectTags` | `Dictionary<string, string>` (PutObjectTagging) |
| `redbS3.VersioningStatus` | `"Enabled"` / `"Suspended"` (PutBucketVersioning) |
| `redbS3.RestoreDays` | Glacier restore days |
| `redbS3.RestoreTier` | `Standard` / `Bulk` / `Expedited` |
| `redbS3.OverrideBucketName` | Dynamic bucket override |

### Producer Output

| Header | Description |
|---|---|
| `redbS3.PresignedUrl` | Generated presigned URL |
| `redbS3.ProducedKey` | Resolved key after upload |
| `redbS3.ProducedBucketName` | Resolved bucket name |
| `redbS3.BucketExists` | Bucket existence result (HeadBucket) |

### Metadata Mapping

Headers prefixed with `redbS3.Meta.` are mapped to/from S3 user metadata:

```
redbS3.Meta.x-amz-meta-author → S3 metadata "x-amz-meta-author"
```

---

## DSL

```csharp
using redb.Route.S3;

// Basic consumer
S3.Bucket("my-bucket")
    .ServiceUrl("http://localhost:9000").ForcePathStyle()
    .AccessKey("minioadmin").SecretKey("minioadmin")

// Producer with operation
S3.Bucket("my-bucket", S3OperationType.CopyObject)
    .Region("eu-west-1")
    .AccessKey("KEY").SecretKey("SECRET")

// Presigned download link
S3.Bucket("my-bucket", S3OperationType.CreateDownloadLink)
    .AccessKey("KEY").SecretKey("SECRET")
    .PresignedUrlExpiration(7_200_000)

// Full producer example
S3.Bucket("archive")
    .Region("eu-west-1")
    .AccessKey("KEY").SecretKey("SECRET")
    .KeyName(Expression.Header("fileName"))
    .MultiPartUpload().PartSize(10_485_760)
    .UseKmsEncryption("my-key-id")
    .StorageClass("INTELLIGENT_TIERING")
    .Metadata("env=prod,team=data")
```

The builder implements `implicit operator string` — pass directly to `.From()` / `.To()`.

---

## DI Registration

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteS3();
    // ...
});
```

Registers the `S3Component` (scheme `s3`).
