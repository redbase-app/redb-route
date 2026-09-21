# redb.Route.Kafka

Apache Kafka transport for redb.Route. Consumer (subscribe), producer (publish), consumer groups, deferred-commit routing (idempotent producer), Kafka transactions with exactly-once consume-process-produce (`transactionalIdPrefix`), and full Confluent.Kafka configuration.

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
using redb.Route.Kafka;

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

// Inside a transacted block the send joins the transaction: it goes out once the database has
// committed. The builder's .Transacted() also makes the producer idempotent and requires the block.
From("direct://critical")
    .Transacted()
        .To(Kafka.Topic("audit")
            .Brokers("localhost:9092")
            .Transacted())
        .To(Kafka.Topic("alerts")
            .Brokers("localhost:9092")
            .Transacted(false))          // goes out at once, even if the block rolls back
    .End();
```

### Transactions: `.Transacted()` and Kafka transactions

Inside a route's `.Transacted()` block a Kafka send **joins the transaction**: the message is
published after the database commits and discarded when the block rolls back. The `transacted`
parameter states the exceptions: `false` sends at once, outside the transaction; `true` also makes
the producer idempotent and fails the step outside a block. On its own this is **at-least-once**:
the deferred sends go out one after the other, and the consumer commits its offset after the route.

**Kafka transactions** are a separate switch, `transactionalIdPrefix`:

```csharp
From(Kafka.Topic("orders").Brokers("b1:9092,b2:9092").GroupId("billing"))
    .Transacted()
        .IdempotentConsumer(Header("orderId"), "billing-inbound")   // the database work
            .Process(/* ... */)
        .EndIdempotentConsumer()
        .To(Kafka.Topic("invoices").Brokers("b1:9092,b2:9092")
            .TransactionalIdPrefix("billing"))
    .End();
```

- The sends such a producer defers in a block commit as **one Kafka transaction** when the block
  commits (after the database): all of them or none. `read_committed` readers, librdkafka's default,
  never see an aborted one.
- When the route started from a Kafka consumer of the **same cluster** (the same brokers), the
  consumed offset commits **in that transaction** (`SendOffsetsToTransaction`) and the consumer does
  not commit it itself: the output and the offset move together — exactly-once within Kafka. With
  `enableAutoCommit=false` the offset stays the application's to commit.
- A send outside a block (`transacted=false`, or no block) is a transaction of its own, a little
  slower than a plain send.
- The prefix names the producer; the connector appends the machine name, the process id and the
  producer's number, so nodes deploying the same configuration never fence each other.
- Two transactional producers in one block commit two transactions, one after the other: atomic per
  producer, as for IBM MQ and RabbitMQ. The database and Kafka still do not commit as one: the
  database commits first, and a failure between the two is covered by the redelivery and the
  idempotent consumer.

A raw `transactional.id` in `additionalProperties` is refused: it would put the client into
transactional mode with nobody opening transactions. The decisions and their reasons are in
docs/KAFKA_EOS_2026_09_19.md.

A parameter the endpoint cannot read — a misspelt option, or a value of the wrong type — is refused
by name when the endpoint is created: a misspelt `transactionalIdPrefix` would otherwise leave the
producer without transactions.

### Delivery defaults

A producer defaults to `acks=all` and an **idempotent producer**, as the Kafka 3 client and Camel 4
do: a send is confirmed once every in-sync replica has it, and a retried send is not written twice.
`enableIdempotence` left unset follows the effective `acks` (on with `all`, off otherwise);
`enableIdempotence=true` with another `acks`, or `transacted=true` or `transactionalIdPrefix` with an
explicit `acks` other than `all`, is refused when the endpoint is created. A route that wants the lower latency of
`acks=leader` sets it explicitly and gets a non-idempotent producer.

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
| **Producer** | `.Acks()`, `.EnableIdempotence()`, `.Key()`, `.Partition()`, `.Transacted()`, `.TransactionalIdPrefix()`, `.Linger()`, `.BatchSize()`, `.Compression()`, `.MessageTimeout()`, `.Retries()`, `.RecordMetadata()` |

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
