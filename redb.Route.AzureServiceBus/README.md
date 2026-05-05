# redb.Route.AzureServiceBus

Azure Service Bus connector for the [redb.Route](../../README.md) ESB framework.  
Provides a full-featured **producer** (single & batch), **consumer** (PeekLock / ReceiveAndDelete), **session consumer** (FIFO per session), and **transacted acknowledgement**. Uses the `asb` URI scheme.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.AzureServiceBus?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.AzureServiceBus)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

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

Enable with `enableBatch=true`. Body must be `IEnumerable`. Each item is serialized to `BinaryData` and added to a batch respecting `batchMaxMessages` and `batchMaxSizeBytes`.

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

## Transacted Mode

When `transacted=true`, message acknowledgement is deferred. The consumer registers `ITransactedAction` instances in `exchange.Properties["TRANSACT_ACTION"]` — a `ConcurrentDictionary<string, ITransactedAction>`:

```csharp
.From(Asb.Queue("orders")
    .ConnectionString("...")
    .Transacted(true))

// In your processor:
var actions = exchange.Properties["TRANSACT_ACTION"]
    as ConcurrentDictionary<string, ITransactedAction>;

// On success:
foreach (var action in actions!.Values)
    await action.Commit();   // CompleteMessageAsync

// On failure:
foreach (var action in actions!.Values)
    await action.Rollback(); // AbandonMessageAsync
```

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
| `transacted` | `false` | Deferred ack via `ITransactedAction` |

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
