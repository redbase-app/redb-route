# redb.Route.Kafka

Apache Kafka transport for redb.Route. Consumer (subscribe), producer (publish), consumer groups, deferred-commit routing (idempotent producer, at-least-once — see the transactions note below), and full Confluent.Kafka configuration.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Kafka?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Kafka)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Kafka
```

## Usage

### URI Format

```
kafka://topic-name?brokers=host:port&groupId=my-group&autoOffsetReset=earliest
```

### Fluent DSL

```csharp
using redb.Route.Kafka.Fluent;

// Consumer
From(Kafka.Topic("orders")
        .Brokers("broker1:9092,broker2:9092")
        .GroupId("order-service")
        .AutoOffsetReset(AutoOffsetReset.Earliest)
        .MaxPollRecords(500))
    .Log("Received: ${body}")
    .To("direct://process");

// Producer
From("direct://outbound")
    .To(Kafka.Topic("events")
        .Brokers("broker1:9092")
        .Acks(Acks.All)
        .Key("order-key")
        .Compression(CompressionType.Lz4));

// Deferred-commit producer: the send happens at the route's transaction boundary
From("direct://critical")
    .Transacted()
    .To(Kafka.Topic("audit")
        .Brokers("localhost:9092")
        .Transacted());
```

### Transactions: what `.Transacted()` is and is not

`.Transacted()` on a Kafka producer means an **idempotent producer whose send is deferred to the
route's transaction boundary** — the message is published when the route commits, discarded when
it rolls back. Together with the consumer's post-process offset commit this is **at-least-once**,
not Kafka exactly-once: no `transactional.id` is configured and no `InitTransactions` is called.
Why, and what real EOS would take, is written down in
docs/KAFKA_TRANSACTIONS_TODO.md. Deduplication belongs to
the route: `IdempotentConsumer(...)` with a real key.

### Failed messages

An exception that escapes the route on a consumed message is counted in endpoint statistics and,
by default, the consumer **moves on** — the failed record's offset is covered by the next
successful commit (Camel's default too). `.BreakOnFirstError()` flips that: the consumer seeks
back to the failed record and retries it instead of advancing, so a permanently poisoned record
blocks its partition — pair it with a route error handler that dead-letters.

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Brokers()`, `.SecurityProtocol()`, `.Sasl(mechanism, user, pass)`, `.SslCa()`, `.SslCert()`, `.ConnectionFactory()` |
| **Consumer** | `.GroupId()`, `.AutoOffsetReset()`, `.MaxPollRecords()`, `.PollTimeout()`, `.SeekTo()`, `.TopicIsPattern()`, `.BreakOnFirstError()`, `.SessionTimeout()`, `.HeartbeatInterval()`, `.MaxPollInterval()`, `.PartitionAssignmentStrategy()`, `.IsolationLevel()` |
| **Producer** | `.Acks()`, `.Key()`, `.Partition()`, `.Transacted()`, `.Linger()`, `.BatchSize()`, `.Compression()`, `.MessageTimeout()`, `.Retries()`, `.RecordMetadata()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Headers

Kafka headers are automatically mapped to/from redb.Route message headers.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new KafkaConnectionFactory
{
    Brokers = "b1:9092,b2:9092",
    SaslUsername = "svc",
    SaslPassword = secrets.KafkaPassword,
});
// kafka://orders?connectionFactory=prod
```
