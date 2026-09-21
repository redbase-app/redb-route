# redb.Route.AzureServiceBus

Azure Service Bus connector for the [redb.Route](../README.md) ESB framework.  
Provides a full-featured **producer** (single & batch), **consumer** (PeekLock / ReceiveAndDelete), **session consumer** (FIFO per session), and **transacted acknowledgement**. Uses the `asb` URI scheme.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.AzureServiceBus?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.AzureServiceBus)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Quick Start

```csharp
// Consumer: receive from a queue
.From(Asb.Queue("orders")
    .ConnectionString("Endpoint=sb://my-ns.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...")
    .MaxConcurrentCalls(5)
    .PrefetchCount(10))

// Producer: send to a queue
.To(Asb.Queue("orders")
    .ConnectionString("Endpoint=sb://..."))

// Topic/subscription roundtrip
.From(Asb.Topic("events", "my-subscription")
    .ConnectionString("Endpoint=sb://..."))

.To(Asb.Topic("events", "ignored-for-producer")
    .ConnectionString("Endpoint=sb://..."))
```

## URI Format

```
asb://entity-name?connectionString=...&param=value
```

Entity detection is automatic: if `subscriptionName` is set, the entity is treated as a **topic**; otherwise as a **queue**.

## Producer

The producer sends messages via `ServiceBusSender`. Two modes are supported:

### Single Message (default)

```csharp
var exchange = new Exchange(new Message("Hello ASB"));
exchange.In.Headers[AzureServiceBusHeaders.CorrelationId] = "corr-123";
exchange.In.Headers[AzureServiceBusHeaders.Subject] = "order.created";
await producer.Process(exchange);

// After send, MessageId header is set
var id = exchange.In.Headers[AzureServiceBusHeaders.MessageId];
```

### Batch Mode

Enable with `enableBatch=true`. Body must be `IEnumerable`. Each item is serialized to `BinaryData` and added to one batch, which Service Bus takes whole or not at all. A body with more items than `batchMaxMessages`, or items that do not fit in `batchMaxSizeBytes`, fails the send and nothing is sent.

```csharp
.To(Asb.Queue("orders")
    .ConnectionString("...")
    .EnableBatch(true)
    .BatchMaxMessages(50)
    .BatchMaxSizeBytes(262144))

var items = new[] { "msg-1", "msg-2", "msg-3" };
var exchange = new Exchange(new Message(items));
await producer.Process(exchange);

var count = (int)exchange.In.Headers[AzureServiceBusHeaders.BatchMessageCount]!; // 3
```

### Body Resolution

| Body Type | Serialization |
|-----------|---------------|
| `byte[]` | `BinaryData.FromBytes` |
| `BinaryData` | Passthrough |
| `string` | `BinaryData.FromString` |
| `Stream` | `BinaryData.FromStream` |
| Any object | `BinaryData.FromObjectAsJson` |

### Producer Headers

Headers with the `redbAsb.` prefix are mapped to native `ServiceBusMessage` properties.  
All other headers are copied to `ApplicationProperties`.

```csharp
// Native ASB properties
exchange.In.Headers[AzureServiceBusHeaders.MessageId] = "custom-id";
exchange.In.Headers[AzureServiceBusHeaders.SessionId] = "session-1";
exchange.In.Headers[AzureServiceBusHeaders.ScheduledEnqueueTime] = DateTimeOffset.UtcNow.AddMinutes(5);
exchange.In.Headers[AzureServiceBusHeaders.TimeToLive] = TimeSpan.FromMinutes(30);

// Application properties (forwarded as-is)
exchange.In.Headers["X-Trace-Id"] = "abc-123";
exchange.In.Headers["X-Priority"] = "high";
```

> **Note:** `PartitionKey` is only set when `SessionId` is empty (ASB constraint).

## Consumer

The consumer uses `ServiceBusProcessor` with callback-based message delivery.

```csharp
.From(Asb.Queue("orders")
    .ConnectionString("...")
    .ReceiveMode("PeekLock")          // default
    .MaxConcurrentCalls(10)
    .PrefetchCount(20)
    .MaxAutoLockRenewalDuration(300))  // 5 min
```

### Acknowledge Modes

| Scenario | `exchange.Exception` | `AutoDeadLetter` | Action |
|----------|---------------------|-------------------|--------|
| Success | `null` | — | Complete |
| Error | set | `false` | Abandon (re-delivery) |
| Error | set | `true` | Dead-letter with reason |

```csharp
// Auto dead-letter on processing error
.From(Asb.Queue("orders")
    .ConnectionString("...")
    .AutoDeadLetter(true)
    .DeadLetterReason("ProcessingFailed"))
```

### ReceiveAndDelete

Messages are removed from the queue immediately upon receipt. No acknowledgement needed:

```csharp
.From(Asb.Queue("orders")
    .ConnectionString("...")
    .ReceiveMode("ReceiveAndDelete"))
```

### Sub-Queues

Read from dead-letter or transfer dead-letter queues:

```csharp
.From(Asb.Queue("orders")
    .ConnectionString("...")
    .SubQueue("deadletter"))
```

### Consumer Headers

Each received message populates the following exchange headers:

| Header | Type | Description |
|--------|------|-------------|
| `redbAsb.MessageId` | `string` | Message ID |
| `redbAsb.CorrelationId` | `string` | Correlation ID |
| `redbAsb.SessionId` | `string` | Session ID |
| `redbAsb.PartitionKey` | `string` | Partition key |
| `redbAsb.ReplyToSessionId` | `string` | Reply-to session ID |
| `redbAsb.Subject` | `string` | Message subject / label |
| `redbAsb.ContentType` | `string` | Content type |
| `redbAsb.ReplyTo` | `string` | Reply-to address |
| `redbAsb.To` | `string` | Destination address |
| `redbAsb.TimeToLive` | `TimeSpan` | Time to live |
| `redbAsb.ScheduledEnqueueTime` | `DateTimeOffset` | Scheduled enqueue time |
| `redbAsb.SequenceNumber` | `long` | Sequence number |
| `redbAsb.DeliveryCount` | `int` | Number of deliveries |
| `redbAsb.EnqueuedTime` | `DateTimeOffset` | When message was enqueued |
| `redbAsb.ExpiresAt` | `DateTimeOffset` | When the message expires |
| `redbAsb.LockedUntil` | `DateTimeOffset` | Lock expiry (PeekLock only) |
| `redbAsb.DeadLetterSource` | `string` | Original entity (dead-letter messages) |
| `redbAsb.DeadLetterReason` | `string` | Dead-letter reason |
| `redbAsb.DeadLetterErrorDescription` | `string` | Dead-letter error description |

All `ApplicationProperties` from the message are also copied to exchange headers.

## Session Consumer

Enable session-aware FIFO processing with `enableSessions=true`:

```csharp
.From(Asb.Queue("session-queue")
    .ConnectionString("...")
    .EnableSessions(true)
    .MaxConcurrentSessions(5)
    .SessionIdleTimeout(30))

// Filter by specific session
.From(Asb.Queue("session-queue")
    .ConnectionString("...")
    .EnableSessions(true)
    .SessionId("my-session-id"))
```

The session consumer uses `ServiceBusSessionProcessor` and guarantees message ordering within a session. `MaxConcurrentSessions` controls parallelism across sessions; messages within the same session are always processed sequentially.

## Transactions

The consumer settles a delivery itself once the route has finished: it completes the message on success and abandons
it on failure. Inside `.Transacted()` that comes last, after the database and the deferred sends have committed.

A producer inside `.Transacted()` joins the transaction: the message is sent once the database has committed and
dropped if the block rolls back.

```csharp
From(Asb.Queue("orders.in").ConnectionString("..."))
    .Transacted()
        .ProcessWithRedb(async (redb, ex, ct) => await redb.SaveAsync((Order)ex.In.Body!, ct))
        .To(Asb.Queue("orders.created").ConnectionString("..."))                    // sent after the commit
        .To(Asb.Queue("alerts").ConnectionString("...").Transacted(false))          // sent at once
    .End();
```

- `transacted=false` sends at once, outside the transaction, even if the block later rolls back.
- `transacted=true` requires an enclosing block; outside one the step fails instead of losing the message.
- The Service Bus client would enlist a send in the ambient `System.Transactions` transaction by itself. The connector
  never lets it: a send that leaves at once runs with the ambient transaction suppressed and a deferred one after the
  transaction closed, so Service Bus never becomes a second resource next to the database.
- By default the sends a producer deferred in a block go out one after the other. With `batchCommit=true`
  (`.BatchCommit()`) they leave as **one batch** when the block commits: Service Bus takes a batch to one entity whole
  or not at all. The block's messages must fit in one batch (`batchMaxSizeBytes`, and the entity's limit — 256 KB on
  Standard); a block whose messages do not fit fails its commit and sends nothing. On a partitioned or session entity
  the messages of one batch must share the partition key or session id.

See the framework-wide **Transactions** guide (`TRANSACTIONS.md` in the
[redb.Route repository](https://github.com/redbase-app/redb)) for the commit order and nesting.

## Topic / Subscription

```csharp
// Producer sends to the topic
.To(Asb.Topic("events", "sub-name")
    .ConnectionString("..."))

// Consumer reads from a subscription
.From(Asb.Topic("events", "my-subscription")
    .ConnectionString("..."))
```

The second argument to `Asb.Topic()` is the subscription name. The producer ignores `subscriptionName` and sends directly to the topic; the consumer uses it to create a `ServiceBusProcessor` for that subscription.

## Connection Factory

For complex or shared client configurations, register a named `AzureServiceBusConnectionFactory`:

```csharp
var factory = new AzureServiceBusConnectionFactory
{
    ConnectionString = "Endpoint=sb://my-ns.servicebus.windows.net/;...",
    MaxRetries = 5,
    RetryMode = ServiceBusRetryMode.Fixed,
    TransportType = ServiceBusTransportType.AmqpWebSockets,
    ProxyAddress = "http://proxy:8080",
};

context.AddToRegistry("myAsb", factory);

// Reference by name in URI
Asb.Queue("orders").ConnectionFactory("myAsb")
```

If `ProxyAddress` is set, transport is automatically switched to `AmqpWebSockets` with the configured proxy.

### Factory Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ConnectionString` | `string` | `""` | ASB connection string |
| `MaxRetries` | `int` | `3` | Max retry count |
| `DelayMs` | `int` | `800` | Initial retry delay (ms) |
| `MaxDelayMs` | `int` | `60000` | Max retry delay (ms) |
| `RetryMode` | `ServiceBusRetryMode` | `Exponential` | `Exponential` or `Fixed` |
| `TryTimeoutMs` | `int` | `60000` | Per-try timeout (ms) |
| `TransportType` | `ServiceBusTransportType` | `AmqpTcp` | Transport: `AmqpTcp` or `AmqpWebSockets` |
| `ProxyAddress` | `string?` | — | HTTP proxy for `AmqpWebSockets` |

## DI Registration

```csharp
services.AddRedbRouteAzureServiceBus();
```

## Configuration Reference

### Connection

| Parameter | Default | Description |
|-----------|---------|-------------|
| `connectionString` | — | ASB connection string (required if no factory) |
| `connectionFactory` | — | Named factory from DI registry |

### Consumer

| Parameter | Default | Description |
|-----------|---------|-------------|
| `receiveMode` | `PeekLock` | `PeekLock` or `ReceiveAndDelete` |
| `maxConcurrentCalls` | `1` | Max concurrent handler invocations |
| `prefetchCount` | `0` | Messages to pre-fetch |
| `maxAutoLockRenewalDuration` | `300` | Auto lock renewal (seconds) |
| `subQueue` | — | `deadletter` or `transferdeadletter` |
| `autoDeadLetter` | `false` | Dead-letter on processing error |
| `deadLetterReason` | — | Dead-letter reason string |
| `transacted` | unset | Producer: unset follows an enclosing `.Transacted()` block, `false` sends at once, `true` requires a block |

### Sessions

| Parameter | Default | Description |
|-----------|---------|-------------|
| `enableSessions` | `false` | Session-aware consumer |
| `sessionId` | — | Fixed session ID filter |
| `maxConcurrentSessions` | `1` | Max concurrent session handlers |
| `sessionIdleTimeout` | `0` | Session idle timeout (seconds, 0 = SDK default) |

### Producer

| Parameter | Default | Description |
|-----------|---------|-------------|
| `messageId` | — | Dynamic message ID expression (`${...}`) |
| `producerSessionId` | — | Dynamic session ID expression |
| `partitionKey` | — | Partition key |
| `scheduleDelaySeconds` | `0` | Delayed delivery (seconds) |
| `timeToLive` | — | Message TTL (`HH:MM:SS` format) |

### Batch

| Parameter | Default | Description |
|-----------|---------|-------------|
| `enableBatch` | `false` | Enable batch send mode |
| `batchMaxMessages` | `100` | Max messages per batch |
| `batchMaxSizeBytes` | `262144` | Max batch size (bytes, default 256 KB) |

### Retry

| Parameter | Default | Description |
|-----------|---------|-------------|
| `retryMaxRetries` | `3` | Max retry count |
| `retryDelayMs` | `800` | Initial retry delay (ms) |
| `retryMaxDelayMs` | `60000` | Max retry delay (ms) |
| `retryMode` | `Exponential` | `Exponential` or `Fixed` |

## Headers Reference

All headers use the `redbAsb.` prefix.

| Constant | Value | Direction | Used By |
|----------|-------|-----------|---------|
| `MessageId` | `redbAsb.MessageId` | In/Out | Producer, Consumer |
| `CorrelationId` | `redbAsb.CorrelationId` | In/Out | Producer, Consumer |
| `SessionId` | `redbAsb.SessionId` | In/Out | Producer, Consumer |
| `PartitionKey` | `redbAsb.PartitionKey` | In/Out | Producer, Consumer |
| `ReplyToSessionId` | `redbAsb.ReplyToSessionId` | In/Out | Producer, Consumer |
| `Subject` | `redbAsb.Subject` | In/Out | Producer, Consumer |
| `ContentType` | `redbAsb.ContentType` | In/Out | Producer, Consumer |
| `ReplyTo` | `redbAsb.ReplyTo` | In/Out | Producer, Consumer |
| `To` | `redbAsb.To` | In/Out | Producer, Consumer |
| `TimeToLive` | `redbAsb.TimeToLive` | In/Out | Producer, Consumer |
| `ScheduledEnqueueTime` | `redbAsb.ScheduledEnqueueTime` | In/Out | Producer, Consumer |
| `SequenceNumber` | `redbAsb.SequenceNumber` | Out | Consumer |
| `DeliveryCount` | `redbAsb.DeliveryCount` | Out | Consumer |
| `EnqueuedTime` | `redbAsb.EnqueuedTime` | Out | Consumer |
| `ExpiresAt` | `redbAsb.ExpiresAt` | Out | Consumer |
| `LockedUntil` | `redbAsb.LockedUntil` | Out | Consumer (PeekLock) |
| `LockToken` | `redbAsb.LockToken` | — | Declared, not set |
| `DeadLetterSource` | `redbAsb.DeadLetterSource` | Out | Consumer (DLQ) |
| `DeadLetterReason` | `redbAsb.DeadLetterReason` | Out | Consumer (DLQ) |
| `DeadLetterErrorDescription` | `redbAsb.DeadLetterErrorDescription` | Out | Consumer (DLQ) |
| `SessionState` | `redbAsb.SessionState` | — | Declared, not set |
| `BatchMessageCount` | `redbAsb.BatchMessageCount` | Out | Producer (batch) |

## Requirements

- **Azure Service Bus** or Azure Service Bus Emulator
- .NET 8.0 / 9.0 / 10.0
- `Azure.Messaging.ServiceBus` 7.x

## Concurrency

The default is **1** concurrent consumer — the industry norm (Camel, Spring, the Azure SDK all
ship 1): a single consumer preserves ordering and your handlers need no thread safety.
Parallelism is an explicit opt-in:

```
maxConcurrentCalls=4       # a fixed worker count
maxConcurrentCalls=auto    # max(CPU count, 2) — the NServiceBus formula
```

Anything else — `0`, a negative, a typo — fails at endpoint creation naming the option (the old
int-typed option silently fell back to 1). Raising the value trades ordering for throughput:
messages from the same queue are processed out of order, and your processors must be safe to
run in parallel.
