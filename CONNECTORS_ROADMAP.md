# redb.Route — Connectors Roadmap

`redb.Route` is a .NET integration framework inspired by Apache Camel: a lightweight DSL
for building message-routing pipelines (`.From(...)...Process(...)...To(...)`) on top of a
pluggable component model. Each **connector** is a NuGet package that registers one or
more URI schemes (`kafka:`, `http:`, `sftp:`, …) and exposes a typed options model, a
Producer, a Consumer, and integration tests across `net8.0` / `net9.0` / `net10.0`.

This document is the single source of truth for **which connectors exist, what they do,
and what is on the wishlist.** Updated whenever a connector ships or a new one is planned.

---

## Current Status

23 connectors shipped.

| Package | Scheme(s) | Status | Tests |
|---|---|---|---|
| `redb.Route` (core) | `direct:`, `seda:`, `timer:`, `log:`, `mock:`, `validator:` | ✅ Done | 844 × 3 TFM |
| `redb.Route.Kafka` | `kafka:` | ✅ Done | 54 × 3 TFM |
| `redb.Route.RabbitMQ` | `rabbitmq:` | ✅ Done | 48 × 3 TFM |
| `redb.Route.Redis` | `redis:` | ✅ Done | 134 × 3 TFM |
| `redb.Route.Amqp` | `amqp:` | ✅ Done | 83 × 3 TFM |
| `redb.Route.Http` | `http:`, `https:` | ✅ Done | 119 × 3 TFM |
| `redb.Route.Tcp` | `tcp:` | ✅ Done | 89 × 3 TFM |
| `redb.Route.WebSocket` | `ws:`, `wss:` | ✅ Done | 79 × 3 TFM |
| `redb.Route.Grpc` | `grpc:` | ✅ Done | 64 × 3 TFM |
| `redb.Route.File` | `file:` | ✅ Done | 104 × 3 TFM |
| `redb.Route.Ftp` | `ftp:`, `ftps:` | ✅ Done | 121 × 3 TFM |
| `redb.Route.Sftp` | `sftp:` | ✅ Done | 180 × 3 TFM |
| `redb.Route.Mail` | `smtp:`, `pop3:`, `imap:` | ✅ Done | 95 × 3 TFM |
| `redb.Route.Quartz` | `quartz:` | ✅ Done | 62 × 3 TFM |
| `redb.Route.IbmMq` | `wmq:` | ✅ Done | 99 × 3 TFM |
| `redb.Route.Ldap` | `ldap:` | ✅ Done | 118 × 3 TFM |
| `redb.Route.MqttNet` | `mqtt:`, `mqtts:` | ✅ Done | 169 × 3 TFM |
| `redb.Route.SignalR` | `signalr:` | ✅ Done | 84 × 3 TFM |
| `redb.Route.AzureServiceBus` | `asb:` | ✅ Done | 58 × 3 TFM |
| `redb.Route.S3` | `s3:` | ✅ Done | 69 × 3 TFM |
| `redb.Route.Elasticsearch` | `elasticsearch:` | ✅ Done | 48 × 3 TFM |
| `redb.Route.Firebase` | `fcm:` | ✅ Done | 127 × 3 TFM |
| `redb.Route.Sql` | `sql:` | ✅ Done | 295 × 3 TFM |

---

## Connector Specifications

### Messaging & Streaming

#### `redb.Route.Kafka` — Apache Kafka ✅
- **Scheme:** `kafka:`
- **Dependencies:** `Confluent.Kafka`
- **Producer/Consumer:** publish to topic / subscribe with consumer groups
- **Key features:** partitioning, key/value serializers (JSON / Avro via Schema Registry), at-least-once / exactly-once (transactions), manual & auto offset commits, headers ↔ exchange headers
- **DSL example:**
```csharp
.From("kafka:orders?brokers=kafka:9092&groupId=order-processor&autoOffsetReset=earliest")
.To("kafka:enriched?brokers=kafka:9092&keySerializer=String&valueSerializer=Json")
```

#### `redb.Route.RabbitMQ` — RabbitMQ ✅
- **Scheme:** `rabbitmq:`
- **Dependencies:** `RabbitMQ.Client`
- **Producer/Consumer:** exchange/queue/binding declaration, publisher confirms, manual ack/nack
- **Key features:** routing keys via Simple expressions, dead-letter exchange, QoS prefetch, mandatory/immediate, RPC (Direct Reply-To), connection recovery
- **DSL example:**
```csharp
.From("rabbitmq:orders?exchange=orders.ex&queue=orders.q&autoAck=false&prefetchCount=10")
.To("rabbitmq:notifications?exchange=notifications.ex&routingKey=${header.customerType}")
```

#### `redb.Route.Redis` — Redis ✅
- **Scheme:** `redis:`
- **Dependencies:** `StackExchange.Redis`
- **Producer:** SET/GET/HSET, list/stream push, pub/sub publish
- **Consumer:** pub/sub subscribe, Streams (XREAD/XREADGROUP with consumer groups + acks), key-space notifications, BLPOP/BRPOP queue
- **Key features:** TLS, sentinel, cluster, dynamic key naming, idempotency via SETNX

#### `redb.Route.Amqp` — AMQP 1.0 ✅
- **Scheme:** `amqp:`
- **Dependencies:** `AMQPNetLite.Core`
- Generic AMQP 1.0 (works against Azure Service Bus, ActiveMQ Artemis, Solace, etc.). Use `redb.Route.AzureServiceBus` for Azure-native features (sessions, dead-letter mgmt).

---

### HTTP, RPC & Realtime

#### `redb.Route.Http` — HTTP ✅
- **Scheme:** `http:`, `https:`
- **Dependencies:** 0 (BCL `HttpClient`; ASP.NET optional for consumer)
- **Producer:** HTTP client (GET/POST/PUT/DELETE)
- **Consumer:** built-in HTTP server (webhook receiver)
- **Key features:** method/timeout, `throwOnError`, headers ↔ exchange headers, Basic / Bearer auth, CORS, multipart, streaming
- **DSL example:**
```csharp
.To("https:api.example.com/orders?method=POST&timeout=30000")
.From("http:0.0.0.0:8080/webhook?methods=POST")
```

#### `redb.Route.Grpc` — gRPC ✅
- **Scheme:** `grpc:`
- **Dependencies:** `Grpc.Net.Client`
- **Producer:** unary + server-streaming + client-streaming + bi-directional calls
- **Consumer:** host a gRPC service backed by a route pipeline
- **Key features:** TLS, deadlines, metadata ↔ headers, dynamic proto via reflection (optional)

#### `redb.Route.WebSocket` — WebSocket ✅
- **Scheme:** `ws:`, `wss:`
- **Dependencies:** 0 (BCL `System.Net.WebSockets`)
- **Producer:** WebSocket client (send frames)
- **Consumer:** built-in WebSocket server (accept connections, broadcast)
- **Key features:** text & binary frames, sub-protocols, keep-alive ping/pong, multi-client broadcast

#### `redb.Route.SignalR` — ASP.NET SignalR ✅
- **Scheme:** `signalr:`
- **Dependencies:** `Microsoft.AspNetCore.SignalR.Client`
- **Producer:** invoke hub methods from a route
- **Consumer:** subscribe to hub events into a route
- **Key features:** groups, users, connection-level auth, automatic reconnect

#### `redb.Route.Tcp` — Raw TCP ✅
- **Scheme:** `tcp:`
- **Dependencies:** 0 (BCL `System.Net.Sockets`)
- **Producer:** TCP client. **Consumer:** TCP server.
- **Key features:** framing codecs (`LineDelimited`, `LengthPrefix`, `Raw`, `FixedLength`), `maxConnections`, `keepAlive`, `idleTimeout`, custom `ICodec`
- **DSL example:**
```csharp
.From("tcp:0.0.0.0:9000?codec=LengthPrefix&maxConnections=100")
.To("tcp:192.168.1.100:9000?codec=LineDelimited")
```

---

### Files & Object Storage

#### `redb.Route.File` — Local filesystem ✅
- **Scheme:** `file:`
- **Dependencies:** 0 (BCL `System.IO`)
- **Producer:** write files (Append / Overwrite / TempRename for atomicity)
- **Consumer:** directory polling with filters
- **Key features:** dynamic `fileName` via Simple expressions (`${header.orderId}.json`), `fileExist` strategy, `noop` / `moveTo` / `delete`, `idempotent` (skip already processed), glob `include` / `exclude`, `recursive`, `sortBy`, `charset`, `tempPrefix`
- **DSL example:**
```csharp
.From("file:C:/input?include=*.csv&noop=true&idempotent=true&delay=5000")
.To("file:C:/output?fileName=${header.orderId}.json&fileExist=Append")
```

#### `redb.Route.Ftp` — FTP / FTPS ✅
- **Scheme:** `ftp:`, `ftps:`
- **Dependencies:** `FluentFTP`
- Same option surface as File (`include`, `exclude`, `moveTo`, `delete`, `noop`, `idempotent`, `tempPrefix`).
- **Key features:** active/passive mode, binary/ascii, explicit/implicit TLS (FTPS), client certificates, recursive listing

#### `redb.Route.Sftp` — SFTP (SSH) ✅
- **Scheme:** `sftp:`
- **Dependencies:** `SSH.NET`
- **Producer:** upload to remote server. **Consumer:** poll remote directory.
- **Key features:** auth via `username/password` or `privateKey`, `knownHosts` host-key verification, atomic upload via `tempPrefix`, `stepwise` cd compatibility mode
- **DSL example:**
```csharp
.From("sftp:sftp.example.com/incoming?username=user&privateKey=~/.ssh/id_rsa&delay=30000")
.To("sftp:sftp.example.com/outgoing?username=user&password=xxx&tempPrefix=.tmp")
```

#### `redb.Route.S3` — Amazon S3 ✅
- **Scheme:** `s3:`
- **Dependencies:** `AWSSDK.S3`
- **Producer:** put objects, multi-part upload, presigned URLs
- **Consumer:** list/poll bucket prefix (event-driven via SQS notifications planned in Phase 4)
- **Key features:** dynamic `key` via Simple expressions, server-side encryption (SSE-S3 / SSE-KMS), storage class, `moveTo` / `delete` post-processing, idempotency, S3-compatible endpoints (MinIO, Ceph, Wasabi)

---

### Email, Directory & Mobile

#### `redb.Route.Mail` — SMTP + POP3 + IMAP ✅
- **Scheme:** `smtp:`, `pop3:`, `imap:`
- **Dependencies:** `MailKit`
- **Components:** `SmtpComponent`, `Pop3Component`, `ImapComponent`
- **Key features:** SMTP send (To/CC/BCC from headers, body → email body, attachments via `MimePart[]`), POP3 polling with `delete=true`, IMAP polling + **IDLE push**, folder select, unseen filter, full TLS/STARTTLS
- **DSL example:**
```csharp
.To("smtp:mail.example.com:587?username=bot@ex.com&password=xxx&tls=true")
.From("imap:mail.example.com:993?username=inbox@ex.com&password=xxx&folder=INBOX&unseen=true&tls=true")
```

#### `redb.Route.Ldap` — LDAP / Active Directory ✅
- **Scheme:** `ldap:`
- **Dependencies:** `Novell.Directory.Ldap.NETStandard`
- **Producer:** search / add / modify / delete entries, bind for authentication
- **Consumer:** poll for changes (changelog or full-diff)
- **Key features:** simple bind + SASL (GSSAPI / DIGEST-MD5), StartTLS, paged search, referrals, attribute mapping ↔ exchange headers

#### `redb.Route.Firebase` — Firebase Cloud Messaging ✅
- **Scheme:** `fcm:`
- **Dependencies:** `FirebaseAdmin`
- **Producer:** send push notifications to devices / topics / condition expressions
- **Key features:** Android / iOS / Web payloads, topic management (subscribe/unsubscribe), batch send (multicast), data + notification payloads, dry-run

---

### IoT & Enterprise Messaging

#### `redb.Route.MqttNet` — MQTT 3.1 / 3.1.1 / 5.0 ✅
- **Scheme:** `mqtt:`, `mqtts:`
- **Dependencies:** `MQTTnet`
- **Producer:** publish to topic. **Consumer:** subscribe with QoS 0/1/2.
- **Key features:** TLS + client certs, last-will-and-testament, retained messages, MQTT 5 user properties ↔ exchange headers, shared subscriptions, session persistence

#### `redb.Route.IbmMq` — IBM MQ ✅
- **Scheme:** `wmq:`
- **Dependencies:** `IBMMQDotnetClient` (managed .NET client)
- **Producer:** send to queues / topics (immediate / transacted / RPC)
- **Consumer:** MQGET polling with backout support
- **Key features:** transactions (MQCMIT / MQBACK), `backoutThreshold` + DLQ, RPC with dynamic reply queue, persistence levels, TLS via `sslCipherSpec` / `sslPeerName`, W3C distributed tracing in message properties, full MQMD headers ↔ exchange headers
- **DSL example:**
```csharp
.From(Wmq.Queue("ORDERS.IN")
    .Host("mq.example.com").Port(1414)
    .Channel("DEV.APP.SVRCONN").QueueManager("QM1")
    .Transacted(true)
    .BackoutThreshold(3).BackoutQueue("ORDERS.DLQ"))
```

#### `redb.Route.AzureServiceBus` — Azure Service Bus ✅
- **Scheme:** `asb:`
- **Dependencies:** `Azure.Messaging.ServiceBus`
- **Producer:** send to queue / topic, scheduled messages, batches
- **Consumer:** queue / subscription receivers, **sessions**, dead-letter management
- **Key features:** auto-renew lock, peek-lock vs receive-and-delete, dead-letter queue routing, MSI / connection-string auth, distributed tracing

---

### Data Stores

#### `redb.Route.Sql` — SQL via pure ADO.NET ✅
- **Scheme:** `sql:`
- **Dependencies:** `System.Data.Common` (BCL — 0 external; driver is your choice: `Npgsql`, `Microsoft.Data.SqlClient`, `MySqlConnector`, …)
- **Components:** `SqlComponent` (parameterised SQL), `SqlStoredComponent` (stored procedures)
- **Key features:**
  - Producer: execute SELECT / INSERT / UPDATE / DELETE with parameter binding from exchange headers / body
  - Consumer: poll a query, optionally update a `processed_at` column for at-least-once semantics
  - `SqlIdempotentRepository` — standalone `IIdempotentRepository` backed by a raw ADO.NET table (no `redb.Core` required)
- **DSL example:**
```csharp
.From("sql:select id, payload from inbox where processed_at is null?onConsumeBatchComplete=update inbox set processed_at = now() where id = :id&delay=5000")
.To("sql:insert into orders(id, total) values(:id, :total)")
```

#### `redb.Route.Elasticsearch` — Elasticsearch / OpenSearch ✅
- **Scheme:** `elasticsearch:`
- **Dependencies:** `Elastic.Clients.Elasticsearch`
- **Producer:** index / update / delete documents, bulk operations
- **Consumer:** scroll / search-after polling
- **Key features:** dynamic index names via Simple expressions, pipeline ingestion, refresh policies, version & sequence-number for optimistic concurrency, OpenSearch wire-compatible

---

### Scheduling & Bridges

#### `redb.Route.Quartz` — Quartz.NET scheduler ✅
- **Scheme:** `quartz:`
- **Dependencies:** `Quartz`
- **Consumer only:** trigger a route on a cron schedule / interval
- **Key features:** cron expressions, misfire instructions, named scheduler reuse, clustered scheduler (via Quartz JobStore)

#### `redb.Route.Core` — bridge to `redb.Core` ✅
- **Dependencies:** `redb.Route` + `redb.Core`
- **Extension methods** to access `IRedbService` from a route pipeline
- **RedbIdempotentRepository** — `IIdempotentRepository` backed by `redb.Core` (typed `IdempotentEntryProps` object, no raw SQL)

---

## Implementation Roadmap

### Phase 1 — Transport (✅ Complete)

| # | Package | Dependencies | Status |
|---|---|---|---|
| 1 | `redb.Route.Kafka` | Confluent.Kafka | ✅ Done |
| 2 | `redb.Route.RabbitMQ` | RabbitMQ.Client | ✅ Done |
| 3 | `redb.Route.Redis` | StackExchange.Redis | ✅ Done |
| 4 | `redb.Route.Amqp` | AMQPNetLite.Core | ✅ Done |
| 5 | `redb.Route.Http` | 0 (BCL + ASP.NET) | ✅ Done |
| 6 | `redb.Route.Tcp` | 0 (BCL) | ✅ Done |
| 7 | `redb.Route.WebSocket` | 0 (BCL) | ✅ Done |
| 8 | `redb.Route.Grpc` | Grpc.Net.Client | ✅ Done |
| 9 | `redb.Route.File` | 0 (BCL) | ✅ Done |
| 10 | `redb.Route.Ftp` | FluentFTP | ✅ Done |

### Phase 2 — Integrations (✅ Complete)

| # | Package | Dependencies | Status |
|---|---|---|---|
| 1 | `redb.Route.Mail` | MailKit | ✅ Done |
| 2 | `redb.Route.Quartz` | Quartz | ✅ Done |
| 3 | `redb.Route.Sftp` | SSH.NET | ✅ Done |
| 4 | `redb.Route.IbmMq` | IBMMQDotnetClient | ✅ Done |
| 5 | `redb.Route.Sql` | System.Data.Common (ADO.NET) | ✅ Done |
| 6 | `redb.Route.Ldap` | Novell.Directory.Ldap.NETStandard | ✅ Done |
| 7 | `redb.Route.MqttNet` | MQTTnet | ✅ Done |
| 8 | `redb.Route.SignalR` | Microsoft.AspNetCore.SignalR.Client | ✅ Done |

### Phase 3 — Cloud & Enterprise (✅ Complete)

| # | Package | Dependencies | Status |
|---|---|---|---|
| 1 | `redb.Route.AzureServiceBus` | Azure.Messaging.ServiceBus | ✅ Done |
| 2 | `redb.Route.S3` | AWSSDK.S3 | ✅ Done |
| 3 | `redb.Route.Elasticsearch` | Elastic.Clients.Elasticsearch | ✅ Done |
| 4 | `redb.Route.Firebase` | FirebaseAdmin | ✅ Done |

### Phase 4 — Planned

Grouped by ecosystem. Order within a group reflects current priority.

#### AWS

| Package | Dependencies | Notes |
|---|---|---|
| `redb.Route.Sqs` | AWSSDK.SQS | Queue messaging — pairs with `redb.Route.S3` for S3-event-driven pipelines |
| `redb.Route.Sns` | AWSSDK.SimpleNotificationService | Fan-out pub/sub: SMS / email / HTTP delivery |
| `redb.Route.Dynamodb` | AWSSDK.DynamoDBv2 | Key-value / document store + DynamoDB Streams consumer |

#### Azure

| Package | Dependencies | Notes |
|---|---|---|
| `redb.Route.AzureEventHub` | Azure.Messaging.EventHubs | High-throughput event streaming (Kafka-compatible) |
| `redb.Route.AzureBlob` | Azure.Storage.Blobs | Blob storage — Producer + Consumer (poll prefix / Event Grid) |
| `redb.Route.CosmosDb` | Microsoft.Azure.Cosmos | Cosmos DB document store + Change Feed consumer |

#### Google Cloud

| Package | Dependencies | Notes |
|---|---|---|
| `redb.Route.GooglePubSub` | Google.Cloud.PubSub.V1 | Pub/Sub messaging |
| `redb.Route.GoogleStorage` | Google.Cloud.Storage.V1 | GCS object storage |

#### Cloud-native / Cross-cloud

| Package | Dependencies | Notes |
|---|---|---|
| `redb.Route.Nats` | NATS.Net | NATS Core + JetStream (durable consumers, KV, object store) |
| `redb.Route.Pulsar` | DotPulsar | Apache Pulsar — multi-tenant streaming with tiered storage |
| `redb.Route.MongoDB` | MongoDB.Driver | Document store + Change Streams consumer |
| `redb.Route.Clickhouse` | ClickHouse.Client | Analytics / OLAP — bulk insert producer, streaming consumer |
| `redb.Route.Neo4j` | Neo4j.Driver | Graph database (Cypher producer) |

#### Specialised

| Package | Dependencies | Notes |
|---|---|---|
| `redb.Route.Saga` | (in-house) | Saga / Compensation orchestration on top of routes |
| `redb.Route.Telegram` | Telegram.Bot | Telegram Bot API — Producer (sendMessage) + Consumer (long-poll / webhook) |
| `redb.Route.Slack` | Slack.NetStandard | Slack incoming / outgoing webhooks + Events API consumer |
| `redb.Route.Twilio` | Twilio | SMS / WhatsApp / Voice |
| `redb.Route.S7` | S7NetPlus | Siemens S7 PLC — industrial automation read / write |

> Have a connector you need that is not on this list? Open an issue at
> [github.com/redbase-app/redb-route/issues](https://github.com/redbase-app/redb-route/issues).

---

## Common Patterns

All connectors follow the same architectural conventions, which makes writing a new one
mostly mechanical:

- **Class hierarchy:** `ComponentBase` → `EndpointBase<TOptions>` → `IProducer` / `IConsumer`
- **Options binding:** URI parameters parsed via `EndpointOptions.BindFromUri()` into a strongly-typed options class
- **Connection management:** factories registered in the route registry, retrieved with `context.GetFromRegistry<TFactory>()`
- **Transactions:** components opt in via `ITransactedAction` (Kafka, RabbitMQ, IbmMq, Sql, …)
- **Error handling:** `DefaultErrorHandler` / `DeadLetterChannel` / per-route `OnException()` clauses
- **Idempotency:** any consumer can plug in an `IIdempotentRepository` (in-memory, SQL, redb)
- **Headers ↔ protocol metadata:** consistent two-way mapping in every connector
- **Testing:** unit tests + Testcontainers-based integration tests per TFM (`net8.0` / `net9.0` / `net10.0`)
- **NuGet:** every connector ships as an independent package; you pull only what you need
