# redb.Route.Kafka

Apache Kafka transport for redb.Route. Consumer (subscribe), producer (publish), consumer groups, transactions, and full Confluent.Kafka configuration.

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

// Transactional producer
From("direct://critical")
    .To(Kafka.Topic("audit")
        .Brokers("localhost:9092")
        .Transacted("tx-prefix"));
```

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

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
