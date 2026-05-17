# redb.Route — Connectors Roadmap

> Core connectors (components) covering the main integration protocols.

## Current Status

| Package | Scheme | Status | Tests |
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
| `redb.Route.Ftp` | `ftp:` | ✅ Done | ✅ TFM |
| `redb.Route.Mail` | `smtp:`, `pop3:`, `imap:` | ✅ Done | 95 × 3 TFM |
| `redb.Route.Quartz` | `quartz:` | ✅ Done | 62 × 3 TFM |
| `redb.Route.Sftp` | `sftp:` | ✅ Done | 180 × 3 TFM |
| `redb.Route.IbmMq` | `wmq:` | ✅ Done | 99 × 3 TFM |
| `redb.Route.Ldap` | `ldap:` | ✅ Done | ✅ TFM |
| `redb.Route.MqttNet` | `mqtt:` | ✅ Done | ✅ TFM |
| `redb.Route.SignalR` | `signalr:` | ✅ Done | ✅ TFM |
| `redb.Route.AzureServiceBus` | `asb:` | ✅ Done | ✅ TFM |
| `redb.Route.S3` | `s3:` | ✅ Done | ✅ TFM |
| `redb.Route.Elasticsearch` | `elasticsearch:` | ✅ Done | ✅ TFM |
| `redb.Route.Firebase` | `fcm:` | ✅ Done | ✅ TFM |
| `redb.Route.Sql` | `sql:` | ✅ Done | ✅ TFM |

---

## Connector Specifications

### 1. `redb.Route.File` — File connector ✅
- **Scheme:** `file:`
- **Dependencies:** 0 (BCL `System.IO`)
- **Producer:** write files (Append / Overwrite / TempRename)
- **Consumer:** directory polling with filters
- **Key features:**
  - `fileName` — dynamic name via Simple expressions (`${header.orderId}.json`)
  - `fileExist` — strategy: `Append`, `Override`, `Fail`, `Ignore`
  - `noop=true` — do not move or delete the file after processing
  - `moveTo` / `delete=true` — post-processing action
  - `idempotent=true` — skip already-processed files
  - `include` / `exclude` — glob filters (`*.csv`, `*.json`)
  - `recursive=true` — recursive directory traversal
  - `delay` — polling interval in ms
  - `sortBy` — file sort order (name, date, size)
  - `charset` — encoding (default: UTF-8)
  - `tempPrefix` — write via temp file then rename (atomicity)
- **DSL examples:**
```csharp
// Poll directory, idempotent CSV processing
.From("file:C:/input?include=*.csv&noop=true&idempotent=true&delay=5000")

// Write result to file with dynamic name
.To("file:C:/output?fileName=${header.orderId}.json&fileExist=Append")

// Move processed files to archive
.From("file:C:/inbox?moveTo=C:/archive&delay=10000")
```

---

### 2. `redb.Route.Http` — HTTP connector ✅
- **Scheme:** `http:`, `https:`
- **Dependencies:** 0 (BCL `HttpClient`; ASP.NET optional for consumer)
- **Producer:** HTTP client (GET/POST/PUT/DELETE)
- **Consumer:** built-in HTTP server (webhook receiver)
- **Key features:**
  - `method` — HTTP method (default: GET for read, POST for write)
  - `timeout` — request timeout
  - `throwOnError=true` — throw exception on 4xx/5xx
  - Headers: `exchange.In.Headers["Content-Type"]` → HTTP headers
  - Body: exchange body → HTTP body, response body → exchange Out body
  - Query params from URI or headers
  - Basic Auth / Bearer Token via headers or URI params
  - Consumer: bind address, allowed methods, CORS
- **DSL examples:**
```csharp
// POST request to external API
.To("https:api.example.com/orders?method=POST&timeout=30000")

// GET with bearer token
.To("http:api.example.com/users?method=GET&authToken=${header.token}")

// Webhook receiver (consumer)
.From("http:0.0.0.0:8080/webhook?methods=POST")
```

---

### 3. `redb.Route.Tcp` — TCP connector ✅
- **Scheme:** `tcp:`
- **Dependencies:** 0 (BCL `System.Net.Sockets`)
- **Producer:** TCP client (send data)
- **Consumer:** TCP server (accept connections)
- **Key features:**
  - `codec` — framing: `LineDelimited` (`\n`), `LengthPrefix` (4-byte BE), `Raw`, `FixedLength`
  - `maxConnections` — max connections limit for server
  - `keepAlive=true` — persistent connections
  - `bufferSize` — read buffer size
  - `idleTimeout` — idle connection timeout
  - `ICodec` interface for custom framing
- **DSL examples:**
```csharp
// TCP client: send with line-delimited framing
.To("tcp:192.168.1.100:9000?codec=LineDelimited")

// TCP server: accept connections
.From("tcp:0.0.0.0:9000?codec=LengthPrefix&maxConnections=100")
```

---

### 4. `redb.Route.Mail` — Email connector (SMTP + POP3 + IMAP) ✅
- **Scheme:** `smtp:`, `pop3:`, `imap:`
- **Dependencies:** **MailKit** (single package for all email protocols)
- **Components:** `SmtpComponent`, `Pop3Component`, `ImapComponent`
- **Key features:**
  - SMTP Producer: send email (To/CC/BCC from headers, body → email body)
  - POP3 Consumer: poll inbox, `delete=true`
  - IMAP Consumer: poll with IDLE push, folder select, unseen filter
  - TLS/SSL: `tls=true`, `port=587` / `port=993`
  - Attachments: `exchange.In.Headers["Attachments"]` → `MimePart[]`
  - HTML/Plain: `contentType=text/html`
- **DSL examples:**
```csharp
// Send email
.To("smtp:mail.example.com:587?username=bot@ex.com&password=xxx&tls=true")

// Poll inbox via POP3
.From("pop3:mail.example.com?username=inbox@ex.com&password=xxx&delete=true&delay=60000")

// IMAP with IDLE push
.From("imap:mail.example.com:993?username=inbox@ex.com&password=xxx&folder=INBOX&unseen=true&tls=true")
```

---

### 5. `redb.Route.Sftp` — SFTP connector ✅
- **Scheme:** `sftp:`, `ftp:`
- **Dependencies:** **SSH.NET** (SFTP), **FluentFTP** (FTP, optional)
- **Producer:** upload files to remote server
- **Consumer:** poll remote directory
- **Key features:**
  - Same options as File connector, but remote: `include`, `exclude`, `moveTo`, `delete`, `noop`, `idempotent`
  - Auth: `username/password` or `privateKey` (path to SSH key)
  - `knownHosts` — server key verification
  - `tempPrefix` — atomic upload via temp file
  - `stepwise=true` — step-by-step cd (compatibility with strict servers)
  - FTP: active/passive mode, binary/ascii
- **DSL examples:**
```csharp
// SFTP: download from remote server
.From("sftp:sftp.example.com/incoming?username=user&privateKey=~/.ssh/id_rsa&delay=30000")

// SFTP: upload
.To("sftp:sftp.example.com/outgoing?username=user&password=xxx&tempPrefix=.tmp")

// FTP: passive mode
.From("ftp:ftp.example.com/data?username=ftp&password=xxx&passive=true")
```

---

### 6. `redb.Route.IbmMq` — IBM MQ connector ✅
- **Scheme:** `wmq:`
- **Dependencies:** **IBMMQDotnetClient** 9.4.1.1 (managed .NET client)
- **Producer:** send messages to queues and topics (immediate / transacted / RPC)
- **Consumer:** receive messages via MQGET polling with backout support
- **Key features:**
  - `destinationType` — Queue (default) / Topic
  - `concurrentConsumers` — parallel consumers
  - `waitInterval` — MQGET interval in ms
  - `transacted=true` — MQCMIT/MQBACK transactions
  - `persistence` — App / Persistent / NonPersistent
  - `backoutThreshold` / `backoutQueue` — automatic dead-letter
  - `rpcEnabled=true` — Request/Reply with dynamic reply-queue
  - `rpcTimeout` — RPC timeout in ms (default: 20000)
  - `correlationPattern` — MsgId / CorrelId for RPC
  - `sslCipherSpec` / `sslPeerName` — TLS/SSL
  - W3C Distributed Tracing via message properties
  - MQMD headers ↔ Exchange headers (MsgId, CorrelId, Format, CCSID, Priority, Expiry, ReplyToQueue, etc.)
- **DSL examples:**
```csharp
// Send to queue
.To(Wmq.Queue("DEV.QUEUE.1")
    .Host("mq.example.com").Port(1414)
    .Channel("DEV.APP.SVRCONN")
    .QueueManager("QM1")
    .User("app").Password("passw0rd"))

// Consume from queue with transactions
.From(Wmq.Queue("ORDERS.IN")
    .Host("mq.example.com").Port(1414)
    .Channel("DEV.APP.SVRCONN")
    .QueueManager("QM1")
    .Transacted(true)
    .BackoutThreshold(3)
    .BackoutQueue("ORDERS.DLQ"))

// RPC request
.To(Wmq.Queue("REQUEST.Q")
    .Host("mq.example.com").Port(1414)
    .Channel("DEV.APP.SVRCONN")
    .QueueManager("QM1")
    .RpcEnabled(true).RpcTimeout(30000))
```

---

### 7. `redb.Route.Sql` — SQL connector (pure ADO.NET) ✅
- **Scheme:** `sql:`
- **Dependencies:** `System.Data.Common` (BCL, 0 external)
- **Components:** `SqlComponent`, `SqlStoredComponent`
- **SqlIdempotentRepository** — `IIdempotentRepository` backed by a raw ADO.NET table (standalone, no redb.Core required)
- **No dependency on redb.Core** — pure ADO.NET via `DbProviderFactory`

### 8. `redb.Route.Core` — redb.Core bridge (optional)
- **Dependencies:** `redb.Route` + `redb.Core`
- **Extension methods** to access `IRedbService` from a route pipeline
- **RedbIdempotentRepository** — `IIdempotentRepository` backed by redb.Core (typed `IdempotentEntryProps` object, no raw SQL)

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
| 2 | `redb.Route.Quartz` | Quartz.NET | ✅ Done |
| 3 | `redb.Route.Sftp` | SSH.NET | ✅ Done |
| 4 | `redb.Route.IbmMq` | IBMMQDotnetClient | ✅ Done |
| 5 | `redb.Route.Sql` | System.Data.Common (ADO.NET) | ✅ Done |
| 6 | `redb.Route.Ldap` | Novell.Directory.Ldap | ✅ Done |
| 7 | `redb.Route.MqttNet` | MQTTnet | ✅ Done |
| 8 | `redb.Route.SignalR` | Microsoft.AspNetCore.SignalR | ✅ Done |

### Phase 3 — Cloud & Enterprise (✅ Complete)

| # | Package | Dependencies | Status |
|---|---|---|---|
| 1 | `redb.Route.AzureServiceBus` | Azure.Messaging.ServiceBus | ✅ Done |
| 2 | `redb.Route.S3` | AWSSDK.S3 | ✅ Done |
| 3 | `redb.Route.Elasticsearch` | Elastic.Clients.Elasticsearch | ✅ Done |
| 4 | `redb.Route.Firebase` | FirebaseAdmin | ✅ Done |

### Phase 4 — Planned

| # | Package | Dependencies | Notes |
|---|---|---|---|
| 1 | `redb.Route.Sqs` | AWSSDK.SQS | Amazon SQS — queue messaging (pairs with S3) |
| 2 | `redb.Route.Sns` | AWSSDK.SimpleNotificationService | Amazon SNS — fan-out pub/sub |
| 3 | `redb.Route.AzureEventHub` | Azure.Messaging.EventHubs | Azure Event Hubs — high-throughput streaming |
| 4 | `redb.Route.Nats` | NATS.Net | NATS — cloud-native messaging |
| 5 | `redb.Route.GooglePubSub` | Google.Cloud.PubSub.V1 | Google Cloud Pub/Sub |
| 6 | `redb.Route.Pulsar` | DotPulsar | Apache Pulsar — multi-tenant streaming |
| 7 | `redb.Route.MongoDB` | MongoDB.Driver | MongoDB document store |
| 8 | `redb.Route.CosmosDb` | Microsoft.Azure.Cosmos | Azure Cosmos DB |

## Common Patterns

- All inherit `ComponentBase` → `EndpointBase<TOptions>` → `IProducer` / `IConsumer`
- URI parameters are bound via `EndpointOptions.BindFromUri()`
- ConnectionFactory via registry (`context.GetFromRegistry<T>()`)
- Transactions via `ITransactedAction` (where applicable)
- Unit + Integration tests per TFM (net8.0 / net9.0 / net10.0)
- `InternalsVisibleTo` for test projects
