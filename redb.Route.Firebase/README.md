# redb.Route.Firebase

Firebase transport for the **redb.Route** ESB framework — **Firestore**, **Cloud Storage (GCS)**, and **FCM** in a single package.

Firestore producer with CRUD, queries, and 500-doc batch writes. Realtime snapshot listener consumer.
Cloud Storage producer with upload/download/delete/list/metadata. Polling consumer with idempotency, glob filtering, move/delete after read.
FCM producer with full platform support — Android priority/TTL/channel, APNS content-available/mutable-content, WebPush.

Shared `IFirebaseCredentialProvider` across all three services — one credential setup, three transports.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Firebase?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Firebase)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

| | |
|---|---|
| **Schemes** | `fstore`, `fbstorage`, `fcm` |
| **NuGet** | `redb.Route.Firebase` |
| **Dependencies** | Google.Cloud.Firestore 3.13.0 · Google.Cloud.Storage.V1 4.14.0 · FirebaseAdmin 3.5.0 |
| **Namespace** | `redb.Route.Firebase` |

---

## Quick Start

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteFirebase();

    // Realtime Firestore → log changes
    route.AddRoute("firestore-watch", r => r
        .From(Firestore.Collection("orders")
            .Where("status==pending")
            .CredentialPath("/secrets/firebase.json"))
        .To("log:order-change"));

    // Upload file to GCS bucket
    route.AddRoute("storage-upload", r => r
        .From("direct:upload")
        .To(FirebaseStorage.Bucket("media-uploads")
            .Operation(FirebaseStorageOperationType.Upload)
            .ObjectName(Expression.Header("fileName"))
            .ContentType("application/pdf")
            .CacheControl("public, max-age=86400")
            .CredentialPath("/secrets/firebase.json")));

    // Send push notification
    route.AddRoute("push-notify", r => r
        .From("direct:notify")
        .To(Fcm.Token(Expression.Header("deviceToken"))
            .Title("New Order")
            .Body(Expression.Simple("Order ${body['id']} is ready"))
            .CredentialPath("/secrets/firebase.json")));
});
```

---

## Credential Resolution

All three components share the same resolution order:

1. `ConnectionFactory` — named `IFirebaseCredentialProvider` from the service registry
2. `CredentialPath` — path to a service-account JSON file
3. `GOOGLE_APPLICATION_CREDENTIALS` environment variable (default credential)
4. Emulator environment variables — `FIRESTORE_EMULATOR_HOST`, `FIREBASE_STORAGE_EMULATOR_HOST` (for development)

```csharp
// Option 1: Explicit credential file
.CredentialPath("/secrets/firebase.json")

// Option 2: Named provider from DI
.ConnectionFactory("myFirebase")

// Option 3: Environment variable (no code needed)
// Set GOOGLE_APPLICATION_CREDENTIALS=/secrets/firebase.json
```

---

# Firestore (`fstore`)

## URI Format

```
fstore://collection-path?option=value&...
```

**Consumer** — realtime snapshot listener (default):
```
fstore://orders?where=status==pending&credentialPath=/secrets/fb.json
```

**Producer** — default operation `Set`:
```
fstore://users?documentId=${header.userId}&merge=true
fstore://events?operation=Query&where=type==purchase;amount>100&orderBy=createdAt desc&limit=50
fstore://bulk?operation=BatchWrite
```

Sub-collections use path segments:
```
fstore://users/uid-123/orders?operation=Query&limit=10
```

## Producer Operations

6 operations available, set via `operation` URI parameter.

| Operation | Description | Key Input | Key Output Headers |
|---|---|---|---|
| **Set** | Create or overwrite a document | Body: `Dictionary<string, object?>` | `DocumentId`, `DocumentPath`, `WriteTime` |
| **Get** | Read a single document by ID | `DocumentId` header or option | Body: document data, `DocumentId` |
| **Update** | Update specific fields | Body: fields to update, `DocumentId` | `DocumentId`, `WriteTime` |
| **Delete** | Delete a document by ID | `DocumentId` header or option | `DocumentId` |
| **Query** | Query with filters, ordering, pagination | `Where`, `OrderBy`, `Limit`, `Offset` | Body: list of documents, `DocumentCount` |
| **BatchWrite** | Write up to N documents (auto-chunked at 500) | Body: `IEnumerable<IDictionary<string, object?>>` | `DocumentCount` |

### Set with Merge

```csharp
// Overwrite entire document (default)
route.From("direct:save")
    .To(Firestore.Collection("users")
        .DocumentId(Expression.Header("userId")));

// Merge — only update provided fields
route.From("direct:update-partial")
    .To(Firestore.Collection("users")
        .DocumentId(Expression.Header("userId"))
        .Merge());
```

### Query

```csharp
route.From("timer:poll?delay=60000")
    .To(Firestore.Collection("events")
        .Operation(FirestoreOperationType.Query)
        .Where("type==purchase;amount>100")
        .OrderBy("createdAt desc")
        .Limit(50))
    .To("log:results");
```

Filter syntax: `field==value`, separated by `;`. Operators: `==`, `!=`, `>`, `>=`, `<`, `<=`, `array-contains`.

### BatchWrite

```csharp
route.From("direct:bulk-import")
    .To(Firestore.Collection("products")
        .Operation(FirestoreOperationType.BatchWrite));
```

Automatically chunks into batches of 500 (Firestore hard limit). Body must be `IEnumerable<IDictionary<string, object?>>`.

## Consumer

Realtime snapshot listener — receives document changes as they happen via gRPC stream.

```csharp
route.From(Firestore.Collection("orders")
        .Where("status==pending")
        .OrderBy("createdAt"))
    .Process(async (exchange, ct) =>
    {
        var changeType = exchange.In.GetHeader<string>(FirestoreHeaders.ChangeType);
        var docId = exchange.In.GetHeader<string>(FirestoreHeaders.DocumentId);
        // changeType: "Added", "Modified", or "Removed"
    });
```

Each document change creates a separate exchange. All changes within a snapshot are processed in parallel with graceful drain on stop.

### Consumer Headers

| Header | Type | Description |
|---|---|---|
| `redbFirestore.DocumentId` | `string` | Document ID |
| `redbFirestore.DocumentPath` | `string` | Full path (`collection/docId`) |
| `redbFirestore.CollectionPath` | `string` | Collection path |
| `redbFirestore.ChangeType` | `string` | `"Added"`, `"Modified"`, or `"Removed"` |
| `redbFirestore.CreateTime` | `Timestamp?` | Document creation time |
| `redbFirestore.UpdateTime` | `Timestamp?` | Last modification time |
| `redbFirestore.ReadTime` | `Timestamp?` | Snapshot read time |

### Body Format

Default: `Dictionary<string, object?>` (Firestore native types).
With `RawJson(true)`: serialized JSON string.

---

# Cloud Storage (`fbstorage`)

## URI Format

```
fbstorage://bucket-name?option=value&...
fbstorage://bucket-name/prefix?option=value&...
```

**Consumer** — polls bucket for objects:
```
fbstorage://incoming-data?prefix=uploads/&include=*.csv&deleteAfterRead=true&delay=30000
```

**Producer** — default operation `Upload`:
```
fbstorage://media-bucket?objectName=${header.fileName}&contentType=image/png
fbstorage://media-bucket?operation=Download
fbstorage://media-bucket?operation=List&prefix=archive/
```

## Producer Operations

5 operations available, set via `operation` URI parameter.

| Operation | Description | Key Input | Key Output Headers |
|---|---|---|---|
| **Upload** | Upload an object | Body: `byte[]`, `Stream`, or `string` | `ObjectName`, `BucketName`, `Md5Hash`, `Generation`, `MediaLink` |
| **Download** | Download an object | `ObjectName` header or option | Body: `byte[]` (or `Stream` with `StreamBody`), full metadata headers |
| **Delete** | Delete an object | `ObjectName` header or option | — |
| **List** | List objects in prefix | `Prefix` option | Body: `List<Dictionary>`, `ObjectCount` header |
| **GetMetadata** | Get object metadata without downloading | `ObjectName` header or option | Full metadata headers, Body: custom metadata dict |

### Upload

```csharp
route.From("direct:upload")
    .To(FirebaseStorage.Bucket("media")
        .ObjectName(Expression.Simple("uploads/${header.category}/${header.fileName}"))
        .ContentType("application/pdf")
        .CacheControl("public, max-age=3600"));
```

Body can be `byte[]`, `Stream`, `string`, or any object (auto-serialized to JSON). If `ObjectName` is not set, a GUID is generated.

### Download with Streaming

```csharp
// Default: byte[] in memory
route.From("direct:download")
    .To(FirebaseStorage.Bucket("media")
        .Operation(FirebaseStorageOperationType.Download));

// Large files: Stream (caller must dispose)
route.From("direct:download-large")
    .To(FirebaseStorage.Bucket("media")
        .Operation(FirebaseStorageOperationType.Download)
        .StreamBody());
```

Download runs object retrieval and metadata fetch in parallel for optimal performance.

## Consumer

Polls the bucket with `ListObjectsAsync`, downloads matching objects, and optionally deletes/moves them.

```csharp
route.From(FirebaseStorage.Bucket("incoming")
        .Prefix("data/")
        .Include("*.json")
        .Exclude("*.tmp")
        .DeleteAfterRead()
        .MaxMessagesPerPoll(50)
        .Delay(10_000)
        .CredentialPath("/secrets/firebase.json"))
    .To("direct:process");
```

### Consumer Pipeline

```
ListObjects → Filter (prefix/include/exclude) → Idempotency Check → Download Body → Process → Post-Process (delete/move)
```

### Consumer Options

| Option | Default | Description |
|---|---|---|
| `Delay` | `5000` | Poll interval in milliseconds |
| `InitialDelay` | `1000` | Delay before first poll |
| `Prefix` | — | Object name prefix filter |
| `MaxMessagesPerPoll` | `10` | Max objects per cycle |
| `IncludeBody` | `true` | Download object content into exchange body |
| `Include` | — | Glob pattern for object names to include (`*.csv`, `data/**/*.json`) |
| `Exclude` | — | Glob pattern for object names to exclude (`*.tmp`) |
| `Idempotent` | `false` | Skip previously processed objects (in-memory, double-buffer eviction at 10K entries) |
| `DeleteAfterRead` | `false` | Delete object after successful processing |
| `MoveAfterRead` | — | Move objects to this prefix after processing (copy + delete) |

### Consumer Headers

| Header | Type | Description |
|---|---|---|
| `redbStorage.ObjectName` | `string` | Object name (key) |
| `redbStorage.BucketName` | `string` | Bucket name |
| `redbStorage.ContentType` | `string` | MIME type |
| `redbStorage.ContentLength` | `ulong?` | Size in bytes |
| `redbStorage.Md5Hash` | `string` | MD5 hash |
| `redbStorage.Generation` | `long?` | Object generation |
| `redbStorage.TimeCreated` | `DateTimeOffset?` | Creation timestamp |
| `redbStorage.Updated` | `DateTimeOffset?` | Last update timestamp |
| `redbStorage.MediaLink` | `string` | Direct download URL |

---

# FCM (`fcm`)

## URI Format

```
fcm://send?messageType=Token&token=DEVICE_TOKEN&credentialPath=/secrets/fb.json
fcm://send?messageType=Topic&topic=news
fcm://send?messageType=Condition&condition='news' in topics && 'premium' in topics
```

## Producer (send-only)

FCM is producer-only — no consumer. Sends push notifications via Firebase Cloud Messaging.

### Token Targeting (single device)

```csharp
route.From("direct:push")
    .To(Fcm.Token(Expression.Header("deviceToken"))
        .Title("Order Shipped")
        .Body(Expression.Simple("Your order ${body['orderId']} is on the way"))
        .ImageUrl("https://example.com/shipped.png")
        .CredentialPath("/secrets/firebase.json"));
```

### Topic Targeting (broadcast)

```csharp
route.From("direct:broadcast")
    .To(Fcm.Topic("breaking-news")
        .Title("Breaking News")
        .Body(Expression.Simple("${body['headline']}"))
        .CredentialPath("/secrets/firebase.json"));
```

### Condition Targeting (topic expressions)

```csharp
route.From("direct:targeted")
    .To(Fcm.Condition("'premium' in topics && 'news' in topics")
        .Title("Premium News")
        .CredentialPath("/secrets/firebase.json"));
```

### Data-Only Messages

```csharp
// Silent push — no visible notification, just data payload
route.From("direct:sync")
    .To(Fcm.Token(Expression.Header("deviceToken"))
        .DataOnly()
        .CredentialPath("/secrets/firebase.json"));
// Body as Dictionary<string, string> → FCM data payload
// Or set headers with prefix: redbFcm.Data.key = value
```

### Platform-Specific Configuration

```csharp
route.From("direct:push")
    .To(Fcm.Token(Expression.Header("deviceToken"))
        .Title("Update Available")

        // Android
        .AndroidPriority("high")
        .AndroidTtlSeconds(3600)
        .AndroidChannelId("updates")

        // iOS
        .ApnsPriority("10")
        .ApnsCollapseId("update-group")

        // Web
        .WebPushLink("https://app.example.com/updates")

        .CredentialPath("/secrets/firebase.json"));
```

### Dynamic Targeting via Headers

Target can be overridden at runtime by setting exchange headers:

```csharp
exchange.In.Headers[FcmHeaders.Token] = "dynamic-device-token";
exchange.In.Headers[FcmHeaders.Title] = "Dynamic Title";
exchange.In.Headers[FcmHeaders.Body] = "Dynamic body text";

// Data payload via header prefix
exchange.In.Headers["redbFcm.Data.orderId"] = "12345";
exchange.In.Headers["redbFcm.Data.action"] = "refresh";
```

### Output Headers

| Header | Type | Description |
|---|---|---|
| `redbFcm.MessageId` | `string` | Server-assigned message ID (`projects/*/messages/*`) |

---

## Fluent DSL Reference

```csharp
using redb.Route.Firebase.Fluent;
```

### Firestore

| Category | Methods |
|---|---|
| **Entry** | `Firestore.Collection(path)` |
| **Operation** | `.Operation(op)` |
| **Document** | `.DocumentId(id)`, `.DocumentId(expr)` |
| **Query** | `.Where(filter)`, `.OrderBy(field)`, `.Limit(n)`, `.Offset(n)` |
| **Write** | `.Merge()` |
| **Consumer** | `.Realtime()`, `.Delay(ms)` |
| **Format** | `.RawJson()` |
| **Auth** | `.CredentialPath(p)`, `.ProjectId(id)`, `.ConnectionFactory(name)` |

### Cloud Storage

| Category | Methods |
|---|---|
| **Entry** | `FirebaseStorage.Bucket(name)`, `FirebaseStorage.Bucket(name, prefix)` |
| **Operation** | `.Operation(op)` |
| **Object** | `.ObjectName(name)`, `.ObjectName(expr)`, `.ContentType(ct)`, `.CacheControl(v)` |
| **Consumer** | `.Delay(ms)`, `.Prefix(p)`, `.MaxMessagesPerPoll(n)` |
| **Post** | `.DeleteAfterRead()`, `.MoveAfterRead(prefix)`, `.Idempotent()` |
| **Filter** | `.Include(glob)`, `.Exclude(glob)` |
| **Body** | `.IncludeBody()`, `.StreamBody()` |
| **Auth** | `.CredentialPath(p)`, `.ProjectId(id)`, `.ConnectionFactory(name)` |

### FCM

| Category | Methods |
|---|---|
| **Entry** | `Fcm.Token(v)`, `Fcm.Topic(v)`, `Fcm.Condition(v)` |
| **Notification** | `.Title(v)`, `.Body(v)`, `.ImageUrl(url)` |
| **Mode** | `.DataOnly()`, `.DryRun()` |
| **Android** | `.AndroidPriority(p)`, `.AndroidTtlSeconds(s)`, `.AndroidChannelId(id)` |
| **APNS** | `.ApnsPriority(p)`, `.ApnsCollapseId(id)` |
| **Web** | `.WebPushLink(url)` |
| **Auth** | `.CredentialPath(p)`, `.ProjectId(id)`, `.ConnectionFactory(name)` |

All builders support `implicit operator string` — pass directly to `.From()` / `.To()`.

---

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
