# redb.Route.IbmMq

IBM MQ (WebSphere MQ) transport for **redb.Route** ESB framework.
Native MQI access via `IBMXMSDotnetClient` (IBM.WMQ + IBM.XMS) — queues, topics, transactions, RPC, message groups, SSL/TLS, and W3C telemetry. Consumers can poll (default) or receive **event-driven** via an XMS `MessageListener` (`receiveMode=listener`, sub-50&#160;ms latency).

[![NuGet](https://img.shields.io/nuget/v/redb.Route.IbmMq?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.IbmMq)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.IbmMq
```

## Quick Start

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteIbmMq();
    route.AddRouteBuilder<MyRoutes>();
});
```

```csharp
public class MyRoutes : RouteBuilder
{
    public override void Configure()
    {
        From("wmq:DEV.QUEUE.1?host=mq-host&queueManager=QM1&channel=DEV.APP.SVRCONN")
            .Log("Received: ${body}")
            .To("wmq:ORDERS.OUT?host=mq-host&queueManager=QM1&channel=DEV.APP.SVRCONN");
    }
}
```

## URI Format

```
wmq:{destination}?{options}
```

`{destination}` — queue or topic name (e.g. `DEV.QUEUE.1`, `EVENTS/ORDER`).

## Fluent DSL

```csharp
// Queue (point-to-point)
From(Wmq.Queue("DEV.QUEUE.1")
    .Host("mq-host")
    .QueueManager("QM1")
    .Channel("DEV.APP.SVRCONN")
    .User("app")
    .Password("passw0rd"))

// Topic (pub/sub)
From(Wmq.Topic("EVENTS/ORDER")
    .Host("mq-host")
    .QueueManager("QM1")
    .ConcurrentConsumers(4))

// Persistent send with JMS interop
To(Wmq.Queue("ORDERS.OUT")
    .Host("mq-host")
    .Persistent()
    .TargetClient(IbmMqTargetClient.Jms))

// Transacted
From(Wmq.Queue("TXN.QUEUE")
    .Host("mq-host")
    .Transacted())

// RPC (request/reply)
To(Wmq.Queue("RPC.SERVICE")
    .Host("mq-host")
    .ReplyTo()
    .Timeout(10))

// SSL/TLS
From(Wmq.Queue("SECURE.QUEUE")
    .Host("mq-host")
    .SslCipherSpec("TLS_RSA_WITH_AES_256_CBC_SHA256")
    .SslKeyRepository("/var/mqm/ssl/key"))
```

## Event-Driven Receive (XMS Listener)

By default the consumer **polls** with a blocking `MQGET`-`WAIT`. The IBM.WMQ managed .NET client carries
an internal ~500 ms tick, so on an idle/low-traffic queue messages arrive ~250–500 ms after they were put.
Setting `receiveMode=listener` switches to an **event-driven** receive built on **XMS .NET** (`IBM.XMS`),
where the broker pushes messages into a `MessageListener` the instant they arrive — dropping delivery
latency to **single-digit milliseconds**. (IBM.WMQ exposes no public async-consume API, so XMS is the only
supported event-driven path on the managed .NET client.)

```csharp
// URI
From("wmq:ORDERS?host=mq-host&queueManager=QM1&channel=DEV.APP.SVRCONN&receiveMode=listener")

// Fluent
From(Wmq.Queue("ORDERS").Host("mq-host").QueueManager("QM1").Listener())
```

It is **opt-in** — `Poll` remains the default, so existing routes are unchanged. The listener path honours
the same consumer semantics as the poll path:

- **Transacted** (`transacted=true`) — a `SESSION_TRANSACTED` session; commit on success, rollback (redeliver)
  on error, on the delivering session. A route-level `.Transaction()` block settles it as part of the route
  unit-of-work; without one the engine settles it directly.
- **`concurrentConsumers=N`** — N XMS sessions on the shared connection (competing consumers, one in-flight
  per session for back-pressure). A topic clamps to a single subscriber.
- **RPC reply**, **backout threshold**, and **W3C trace-context propagation** all work as on the poll path.
- **Headers** — the same `redbIbmMq.*` MQMD metadata and application (user) headers as the poll path.
  User headers travel in the JMS `usr` folder, so they interoperate across IBM.WMQ, IBM.XMS and any JMS client.
- **RPC client leg** — a producer doing request-reply (`replyTo=true`) with `receiveMode=listener` also
  receives the response event-driven (XMS listener on the reply queue), so the whole round-trip is fast
  (~16 ms warm vs the ~250–500 ms poll floor). The request is still sent over IBM.WMQ; only reply
  reception moves to XMS. A dynamic reply queue uses an XMS temporary queue.

> The listener runs each route synchronously on the XMS dispatch thread (one in-flight message per session),
> which is what provides natural back-pressure. Reverting to the poll path is a matter of flipping the option
> back to `Poll` (or omitting it).

## Options Reference

### Connection

| Option | Default | Description |
|---|---|---|
| `host` | `localhost` | Queue manager host |
| `port` | `1414` | MQ listener port |
| `channel` | `DEV.APP.SVRCONN` | Server-connection channel |
| `queueManager` | `QM1` | Queue manager name |
| `user` | — | Auth username |
| `password` | — | Auth password |
| `clientId` | — | Client identifier |
| `connectionFactory` | — | Named factory from DI |

### Destination

| Option | Default | Description |
|---|---|---|
| `destinationType` | `Queue` | `Queue` or `Topic` |

### Consumer

| Option | Default | Description |
|---|---|---|
| `receiveMode` | `Poll` | `Poll` (MQGET-WAIT) or `Listener` (event-driven XMS, <50 ms) |
| `concurrentConsumers` | `1` | Parallel consumers |
| `waitInterval` | `5000` | MQGET wait (ms), poll mode only |
| `batchSize` | `0` | Batch size (0 = single) |
| `backoutThreshold` | `0` | Poison message threshold |
| `backoutQueue` | — | Backout queue name |
| `selector` | — | Message selector |
| `convert` | `true` | Apply MQGMO_CONVERT |

### Producer

| Option | Default | Description |
|---|---|---|
| `persistence` | `AsQDef` | `Persistent`, `NonPersistent`, `AsQDef` |
| `priority` | `-1` | Priority (0–9, -1 = queue default) |
| `expiry` | `-1` | Expiry (tenths/sec, -1 = unlimited) |
| `targetClient` | `Jms` | `Jms` (with MQRFH2) or `Mq` (raw MQMD) |
| `messageType` | `Datagram` | `Datagram`, `Request`, `Reply`, `Report` |

### Transactions

| Option | Default | Description |
|---|---|---|
| `transacted` | `false` | Local MQ transactions (MQCMIT/MQBACK) |

### RPC

| Option | Default | Description |
|---|---|---|
| `replyTo` | `false` | Enable request/reply |
| `replyToQueue` | — | Reply queue (or dynamic temp) |
| `replyToQueueManager` | — | Reply QM |
| `timeout` | `30` | RPC timeout (seconds) |
| `correlationPattern` | `MsgId` | `MsgId` or `CorrelId` |

### Dead Letter

| Option | Default | Description |
|---|---|---|
| `deadLetterQueue` | — | DLQ name |
| `maxRedeliveries` | `0` | Max redeliveries |

### SSL/TLS

| Option | Default | Description |
|---|---|---|
| `sslCipherSpec` | — | CipherSpec name |
| `sslCertLabel` | — | Certificate label |
| `sslPeerName` | — | Peer DN pattern |
| `sslKeyRepository` | — | Key repo path (.kdb) |
| `sslKeyResetCount` | `0` | Key renegotiation (bytes) |

### Advanced

| Option | Default | Description |
|---|---|---|
| `cCSID` | `1208` | Coded Character Set ID |
| `mqmdWriteEnabled` | `false` | Write MQMD from headers |
| `mqmdReadEnabled` | `true` | Read MQMD into headers |

## Headers

All IBM MQ metadata headers are prefixed with `redbIbmMq.`:

- `redbIbmMq.MsgId` — message ID (hex)
- `redbIbmMq.CorrelId` — correlation ID (hex)
- `redbIbmMq.Format` — MQMD format
- `redbIbmMq.Persistence` — persistence flag
- `redbIbmMq.Priority` — priority
- `redbIbmMq.Expiry` — expiry
- `redbIbmMq.ReplyToQueue` — reply-to queue
- `redbIbmMq.MsgType` — message type
- `redbIbmMq.BackoutCount` — backout count
- ... and more (see `IbmMqHeaders` class)

## Telemetry

W3C distributed tracing is automatically propagated via MQRFH2 user properties (`traceparent`, `tracestate`). OpenTelemetry tags:

- `messaging.system` = `ibmmq`
- `messaging.operation` = `receive` / `publish`
- `messaging.destination.name` = queue/topic name
- `messaging.ibmmq.queue_manager` = QM name
- `messaging.message.id` = MQMD MsgId

## Docker (Dev/Test)

```bash
docker compose -f docker-compose.tests.yml up -d ibmmq
```

Default dev credentials:
- **Host**: `localhost:1414`
- **Queue Manager**: `QM1`
- **Channel**: `DEV.APP.SVRCONN`
- **User**: `app` / `passw0rd`
- **Web Console**: `https://localhost:9443/ibmmq/console` (`admin` / `passw0rd`)

Pre-created queues: `DEV.QUEUE.1` … `DEV.QUEUE.5`, `DEV.DEAD.LETTER.QUEUE`.
