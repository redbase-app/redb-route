# redb.Route.Demo

Full-featured showcase of the [redb.Route](../redb.Route/README.md) ESB framework — **18 transports, 50+ EIP patterns, observability, transactions, and lifecycle events** wired into a single runnable module.

This project is designed to be **deployed into [redb.Tsak](../redb.Tsak/README.md)** (the redb.Route runtime container), but can also be studied as the definitive reference implementation for every feature of the framework.

---

## What This Demonstrates

| Section | Routes | What you learn |
|---------|--------|----------------|
| **1. Main Pipeline** | 8 | HTTP entry → throttle → dedup → 4 broker RPC calls → SQL → WireTap fan-out → response |
| **2. Error Handling** | 4 | `DoTry/DoCatch/DoFinally`, `CircuitBreaker`, `Retry` with backoff, `DeadLetterChannel` |
| **3. EIP Patterns** | 9 | Aggregator, Multicast, RecipientList, DynamicRouter, Loop, Resequencer, Enrich, IdempotentConsumer, Throttle |
| **4. Transport Showcase** | 8 | Timer, Cron, SEDA, Redis Pub/Sub, TCP echo, WebSocket, MQTT, IBM MQ topic |
| **5. Data & Observability** | 4 | JSON Schema validation, Marshal/Unmarshal, `Traced`+`Metered`, expression showcase |
| **6. Transactions** | 2 | `Transacted()` scope, `BeginTransaction`/`CommitTransaction`/`RollbackTransaction` |
| **7. Policies & Lifecycle** | 2 | Route policies, cluster-ready checks |
| **8. Named Redb Instances** | 1 | CRUD via named `IRedbService` from inside a route |
| **9. Scope Diagnostics** | 1 | Exchange introspection, route ID, property/header dump |

**Total: 39 routes across 9 sections.**

---

## Transports Used

| Transport | Role in demo |
|-----------|-------------|
| HTTP (`http:`) | Entry point — `POST /api/demo` triggers the main pipeline |
| RabbitMQ (`rabbitmq:`) | RPC round-trip — worker stamps body and replies |
| AMQP 1.0 (`amqp:`) | RPC round-trip — Artemis broker |
| gRPC (`grpc:`) | RPC round-trip — client/server in the same process |
| IBM MQ (`wmq:`) | RPC round-trip + topic pub/sub |
| Kafka (`kafka:`) | WireTap async audit — fire-and-forget |
| SQL / PostgreSQL (`sql:`) | INSERT + SELECT inside a transaction |
| File (`file:`) | WireTap snapshot — writes `{traceId}.json` to `output/` |
| Redis (`redis:`) | Pub/Sub channel fan-out |
| TCP (`tcp:`) | Echo server — receives message, stamps, returns |
| WebSocket (`websocket:`) | Push server — broadcasts to connected clients |
| MQTT (`mqtt:`) | Publish to `demo/telemetry`, subscribe consumer |
| SEDA (`seda:`) | In-process async queue for background processing |
| DirectVM (`direct-vm:`) | Cross-context synchronous enrichment call |
| Timer (`timer:`) | Heartbeat — fires every 10 seconds |
| Cron (`cron:`) | Scheduled job — runs at configured expression |
| SMTP / SFTP | Referenced in `InitRoute` component registration |
| redb.Core (`IRedbService`) | Named-instance CRUD — `DemoItemProps` saved via EAV |

---

## Project Structure

```
redb.Route.Demo/
├── InitRoute.cs                    ← Tsak module entry point (discovered by convention)
├── DemoRouteBuilder.cs             ← All 39 routes in 9 sections
├── DemoLifecycle.cs                ← IRouteLifecycleListener implementation
├── DemoItemProps.cs                ← redb.Core EAV model ([RedbScheme])
├── manifest.json                   ← Tsak module manifest
├── redb.Route.Demo.config.json     ← Module config (loaded by Tsak 5-layer pipeline)
└── output/                         ← File WireTap writes JSON snapshots here
```

### Key files explained

**`InitRoute.cs`** — the Tsak module entry point. Discovered automatically because the class is named `InitRoute` and contains a static method `main(IRouteContext)`. Registers all 18 transport components, the PostgreSQL data source, the lifecycle listener, and the route builder.

**`DemoRouteBuilder.cs`** — inherits `RouteBuilder` and overrides `Configure()`. Each private `ConfigureXxx()` method defines one or more routes using the fluent DSL. Read top to bottom — the message literally flows like that.

**`redb.Route.Demo.config.json`** — loaded via the Tsak 5-layer config pipeline. Contains connection strings for Postgres, RabbitMQ, Redis; feature flags; named redb provider configs for both PostgreSQL and MSSQL.

---

## Main Pipeline — How a Message Flows

```
POST /api/demo
  │
  ├─ Throttle (10 req/s)
  ├─ SetHeader traceId + startedAt
  ├─ ValidateJsonSchema
  ├─ IdempotentConsumer (dedup by traceId)
  │
  └─► direct://pipeline
        │
        ├─ Filter (non-empty body)
        ├─ SetProperty pipelineStartMs
        ├─ Choice (mode header) → nested Choice (priority)
        │
        ├─ Traced("broker-roundtrips")
        │    ├─► rabbitmq RPC → stamp.rabbit
        │    ├─► amqp RPC    → stamp.amqp
        │    ├─► grpc RPC    → stamp.grpc
        │    └─► wmq RPC     → stamp.wmq
        │
        ├─► direct-vm://enricher → stamp.vm
        │
        ├─ Metered("sql-operations")
        │    ├─ BeginTransaction
        │    ├─► SQL INSERT demo_log
        │    ├─► SQL SELECT last 5 rows → Split → Log each row
        │    └─ CommitTransaction
        │
        ├─ WireTap → kafka://demo-audit
        ├─ WireTap → file:///output/{traceId}.json
        ├─ WireTap → vm://audit-log
        ├─ WireTap → direct://demo-redis-pub
        ├─ WireTap → direct://demo-mqtt-pub
        ├─ WireTap → direct://demo-wmq-pub
        └─ WireTap → direct://demo-seda-send
        │
        └─ SetBody (JSON response with all stamps + elapsed)
```

---

## Running the Demo

### Prerequisites

The demo expects these services on `localhost`. Start them with Docker:

```bash
# PostgreSQL
docker run -d --name pg -e POSTGRES_PASSWORD=1 -p 5432:5432 postgres:16

# RabbitMQ
docker run -d --name rabbit -e RABBITMQ_DEFAULT_USER=admin -e RABBITMQ_DEFAULT_PASS=admin \
  -p 5672:5672 -p 15672:15672 rabbitmq:3-management

# Apache Artemis (AMQP 1.0)
docker run -d --name artemis -e AMQ_USER=admin -e AMQ_PASSWORD=admin \
  -p 5673:5672 -p 8161:8161 apache/activemq-artemis

# Kafka
docker run -d --name kafka -e KAFKA_PROCESS_ROLES=broker,controller \
  -e KAFKA_NODE_ID=1 -e KAFKA_LISTENERS=PLAINTEXT://0.0.0.0:29092,CONTROLLER://0.0.0.0:29093 \
  -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://localhost:29092 \
  -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:29093 \
  -p 29092:29092 apache/kafka:3.7.0

# Redis
docker run -d --name redis -p 6379:6379 redis:7

# MQTT (Eclipse Mosquitto)
docker run -d --name mosquitto -p 11883:1883 eclipse-mosquitto \
  mosquitto -c /mosquitto-no-auth.conf

# IBM MQ (optional — comment out wmq routes if not available)
docker run -d --name ibmmq -e LICENSE=accept -e MQ_QMGR_NAME=QM1 \
  -e MQ_APP_PASSWORD=admin -p 1414:1414 -p 9443:9443 ibmcom/mq
```

### Option A — run standalone (without Tsak)

```bash
# From the project root
dotnet run --project redb.Route.Demo
```

The HTTP entry point starts on `http://localhost:5088`. Send a test message:

```bash
# Windows (cmd / PowerShell)
curl -X POST http://localhost:5088/api/demo `
  -H "Content-Type: application/json" `
  -H "mode: full" `
  -H "priority: high" `
  -d "{\"message\":\"hello\"}"

# Linux / macOS
curl -X POST http://localhost:5088/api/demo \
  -H "Content-Type: application/json" \
  -H "mode: full" \
  -H "priority: high" \
  -d '{"message":"hello"}'
```

### Option B — deploy into redb.Tsak

1. Build the module:
   ```bash
   dotnet publish redb.Route.Demo -c Release -o publish/
   ```
2. Copy to Tsak libs directory:
   ```bash
   cp -r publish/ $TSAK_HOME/libs/redb.Route.Demo/
   ```
3. Use the Tsak CLI or REST API to start the context:
   ```bash
   tsak context start route.demo
   # or
   curl -X POST http://localhost:5000/api/contexts/route.demo/start \
     -H "X-Api-Key: $TSAK_API_KEY"
   ```

---

## Expected Response

A successful `POST /api/demo` returns a JSON object with timestamps from every transport in the pipeline:

```json
{
  "traceId": "a1b2c3d4e5f6",
  "mode": "full",
  "priority": "high",
  "fastTrack": "true",
  "stamps": {
    "rabbit": "ok:14:32:01.123",
    "amqp":   "ok:14:32:01.234",
    "grpc":   "ok:14:32:01.345",
    "wmq":    "ok:14:32:01.456",
    "vm":     "enriched:14:32:01.567"
  },
  "elapsedMs": 312
}
```

---

## Configuration

Edit `redb.Route.Demo.config.json` to adjust connection strings and feature flags:

```json
{
  "DemoSettings": {
    "PostgresConnection": "Host=localhost;Port=5432;Username=postgres;Password=1;Database=redb"
  },
  "RabbitMQ": { "Host": "localhost", "Port": 5672, "Username": "admin", "Password": "admin" },
  "Redis":    { "Host": "localhost", "Port": 6379 },
  "FeatureFlags": {
    "EnableSqlRoutes":  true,
    "EnableMqttRoutes": true
  }
}
```

When deployed via Tsak, secrets should be injected via environment variables — see [DEPLOYMENT_SECRETS.md](../redb.Route/DEPLOYMENT_SECRETS.md).

---

## Part of

[redb.Route](../redb.Route/README.md) — ESB & EIP Framework for .NET  
[redb.Tsak](../redb.Tsak/STATUS.md) — Runtime container for redb.Route modules
