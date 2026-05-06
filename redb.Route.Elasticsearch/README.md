# redb.Route.Elasticsearch

Elasticsearch connector for the [redb.Route](../../README.md) ESB framework.  
Provides a full-featured **producer** (9 operations) and a **polling consumer** with deep pagination, source filtering, and delete-after-read. Compatible with **Elasticsearch 8.x**.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Elasticsearch?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Elasticsearch)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

## Quick Start

```csharp
// Consumer: poll an index for new documents
.From(Es.Index("orders")
    .Nodes("http://localhost:9200")
    .Query("""{"term":{"status":"pending"}}""")
    .Sort("timestamp:asc")
    .Size(100)
    .DeleteAfterRead())

// Producer: index a document
.To(Es.Index("orders")
    .Nodes("http://localhost:9200")
    .Refresh("wait_for"))
```

## URI Format

```
elasticsearch://[OPERATION:]index-name?nodes=http://host:9200&param=value
es://[OPERATION:]index-name?nodes=http://host:9200                          # short alias
```

Operations: `Index` (default), `Bulk`, `Search`, `Get`, `Update`, `Delete`, `Count`, `Exists`, `MultiSearch`.

## Producer Operations

| Operation | Body (In) | Result (Out) | Pattern |
|-----------|-----------|--------------|---------|
| **Index** | JSON document | — | InOnly |
| **Bulk** | JSON array of documents | — | InOnly |
| **Search** | JSON query DSL | `List<JsonObject>` hits | InOut |
| **Get** | — (ID via header) | JSON document | InOut |
| **Update** | Partial JSON document | — | InOnly |
| **Delete** | — (ID via header) | — | InOnly |
| **Count** | JSON query DSL (optional) | `long` count | InOut |
| **Exists** | — (ID via header) | `bool` | InOut |
| **MultiSearch** | JSON array of queries | `List<List<JsonObject>>` | InOut |

### Index

```csharp
var exchange = new Exchange(new Message("""{"title":"Hello","author":"Alice"}"""));
exchange.In.Headers[ElasticsearchHeaders.DocumentId] = "doc-1"; // optional, auto-generated if omitted
await producer.Process(exchange);
// Headers set: DocumentId, Version, Result, SequenceNumber, PrimaryTerm
```

### Bulk

```csharp
var docs = new JsonArray { new JsonObject { ["title"] = "A" }, new JsonObject { ["title"] = "B" } };
var exchange = new Exchange(new Message(docs.ToJsonString()));
await producer.Process(exchange);
// Headers set: BulkItemCount, BulkErrors, BulkErrorItems
```

Documents are automatically chunked into batches of `bulkSize` (default: 100).

### Search

```csharp
var exchange = new Exchange(new Message("""{"match_all":{}}"""));
await producer.Process(exchange);
var docs = exchange.Out!.Body as List<JsonObject>;
var total = (long)exchange.Out.Headers[ElasticsearchHeaders.TotalHits]!;
```

### MultiSearch

Execute multiple search queries in a single request. Each query supports `index`, `query`, `size`, `from`, `sort`, and `_source`:

```csharp
var queries = """
[
  {"index": "logs-2024", "query": {"term": {"level": "error"}}, "size": 5, "sort": "timestamp:desc"},
  {"index": "logs-2024", "query": {"match_all": {}}, "_source": "message,timestamp"},
  {"query": {"range": {"score": {"gte": 90}}}}
]
""";
var exchange = new Exchange(new Message(queries));
await producer.Process(exchange);

var results = exchange.Out!.Body as List<List<JsonObject>>;  // one list per query
var totalHits = exchange.Out.Headers[ElasticsearchHeaders.MultiSearchTotalHits] as long[];
var hasErrors = (bool)exchange.Out.Headers[ElasticsearchHeaders.MultiSearchHasErrors]!;
```

**MultiSearch query fields:**

| Field | Type | Description |
|-------|------|-------------|
| `index` | string | Override index for this sub-query |
| `query` | object | Elasticsearch query DSL |
| `size` | int | Max hits to return |
| `from` | int | Offset for pagination |
| `sort` | string | Sort expression: `field:asc,field2:desc` |
| `_source` | string\|bool | Field filter: `"title,author"` or `false` to disable |

### Operation Override via Header

Any producer can switch operations at runtime:

```csharp
exchange.In.Headers[ElasticsearchHeaders.Operation] = "Count";
await producer.Process(exchange);
```

## Consumer

The consumer polls an Elasticsearch index and delivers each document as an exchange.

```csharp
.From(Es.Index("events")
    .Nodes("http://localhost:9200")
    .Query("""{"range":{"timestamp":{"gte":"now-1h"}}}""")
    .Sort("timestamp:asc,_id:asc")
    .Size(200)
    .SourceIncludes("title,body,timestamp")
    .DeleteAfterRead()
    .Delay(5000))
```

### Pagination Strategies

- **`search_after`** (default) — stateful cursor-based pagination using sort values from the last hit. Efficient for deep pagination.  
- **Scroll API** — enabled when `scrollTimeout` is set (e.g., `scrollTimeout=1m`). Legacy approach, deprecated in ES 8.x.

### Delete After Read

When `deleteAfterRead=true`, the consumer deletes each document after successful processing. If processing throws an exception, the document is preserved.

### Consumer Headers (per document)

| Header | Type | Description |
|--------|------|-------------|
| `redbEs.IndexName` | string | Source index name |
| `redbEs.DocumentId` | string | Document `_id` |
| `redbEs.Version` | long | Document `_version` |
| `redbEs.SequenceNumber` | long | `_seq_no` for optimistic concurrency |
| `redbEs.PrimaryTerm` | long | `_primary_term` for optimistic concurrency |
| `redbEs.Score` | double | Relevance `_score` |
| `redbEs.SortValues` | string | JSON-serialized sort values |

## Configuration Reference

### Connection

| Parameter | Default | Description |
|-----------|---------|-------------|
| `nodes` | `http://localhost:9200` | Comma-separated node URIs |
| `apiKey` | — | Base64-encoded API key |
| `username` | — | Basic auth username |
| `password` | — | Basic auth password |
| `certificateFingerprint` | — | SHA-256 TLS fingerprint |
| `connectionFactory` | — | Named factory from DI registry |
| `enableDebugMode` | `false` | Log raw request/response |

### Timeouts & Resilience

| Parameter | Default | Min | Description |
|-----------|---------|-----|-------------|
| `requestTimeout` | 30000 | 1000 | Request timeout (ms) |
| `pingTimeout` | 2000 | — | Node ping timeout (ms) |
| `deadTimeout` | 60000 | — | Dead node backoff (ms) |
| `maxDeadTimeout` | 600000 | — | Max dead node timeout (ms) |
| `maxRetries` | 3 | 0 | Retries per request |

### Producer

| Parameter | Default | Description |
|-----------|---------|-------------|
| `operation` | `Index` | Default operation |
| `pipeline` | — | Ingest pipeline name |
| `routing` | — | Custom routing value |
| `refresh` | — | Refresh policy: `true`, `false`, `wait_for` |
| `bulkSize` | 100 | Documents per bulk batch |

### Consumer

| Parameter | Default | Description |
|-----------|---------|-------------|
| `delay` | 5000 | Poll interval (ms), min 100 |
| `initialDelay` | 1000 | Delay before first poll (ms) |
| `query` | match_all | JSON query DSL |
| `size` | 100 | Hits per page (1–10000) |
| `sort` | `_doc:asc` | Sort fields: `field:order,...` |
| `scrollTimeout` | — | Scroll API timeout (e.g. `1m`) |
| `deleteAfterRead` | `false` | Delete docs after processing |
| `trackTotalHits` | `true` | Track exact total hit count |
| `sourceIncludes` | — | Fields to include (CSV) |
| `sourceExcludes` | — | Fields to exclude (CSV) |

## Connection Factory

For complex or shared client configurations, register a named `ElasticsearchConnectionFactory`:

```csharp
var factory = new ElasticsearchConnectionFactory
{
    Nodes = "https://node1:9200,https://node2:9200",
    ApiKey = "base64encodedkey",
    EnableSniffing = true,
    MaxRetries = 5,
};

// Reference by name in URI
Es.Index("my-index").ConnectionFactory("myFactory")
```

The factory supports `StaticNodePool` (default) and `SniffingNodePool` (cluster discovery) depending on node count and `EnableSniffing` flag.

## Headers Reference

All headers use the `redbEs.` prefix.

| Constant | Value | Direction | Used By |
|----------|-------|-----------|---------|
| `IndexName` | `redbEs.IndexName` | In | Consumer, Producer |
| `DocumentId` | `redbEs.DocumentId` | In/Out | All |
| `Version` | `redbEs.Version` | Out | Index, Get, Update |
| `SequenceNumber` | `redbEs.SequenceNumber` | Out | Index, Get, Consumer |
| `PrimaryTerm` | `redbEs.PrimaryTerm` | Out | Index, Get, Consumer |
| `Operation` | `redbEs.Operation` | In | Producer (override) |
| `Result` | `redbEs.Result` | Out | Index, Update, Delete |
| `Score` | `redbEs.Score` | Out | Consumer |
| `TotalHits` | `redbEs.TotalHits` | Out | Search |
| `TotalHitsRelation` | `redbEs.TotalHitsRelation` | Out | Search |
| `ScrollId` | `redbEs.ScrollId` | Out | Consumer (scroll) |
| `SortValues` | `redbEs.SortValues` | Out | Consumer |
| `BulkItemCount` | `redbEs.BulkItemCount` | Out | Bulk |
| `BulkErrors` | `redbEs.BulkErrors` | Out | Bulk |
| `BulkErrorItems` | `redbEs.BulkErrorItems` | Out | Bulk |
| `MultiSearchResponseCount` | `redbEs.MultiSearchResponseCount` | Out | MultiSearch |
| `MultiSearchHasErrors` | `redbEs.MultiSearchHasErrors` | Out | MultiSearch |
| `MultiSearchTotalHits` | `redbEs.MultiSearchTotalHits` | Out | MultiSearch |
| `Pipeline` | `redbEs.Pipeline` | In | Producer |
| `Routing` | `redbEs.Routing` | In | Producer |
| `Refresh` | `redbEs.Refresh` | In | Producer |

## Requirements

- **Elasticsearch 8.x** (tested with 8.17.0)
- .NET 8.0 / 9.0 / 10.0
- `Elastic.Clients.Elasticsearch` 8.19.18
