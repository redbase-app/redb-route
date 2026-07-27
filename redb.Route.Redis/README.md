# redb.Route.Redis

Redis transport for redb.Route. Pub/Sub, Streams, key-value, lists, sorted sets — all Redis data structures as endpoints via StackExchange.Redis.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Redis?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Redis)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Redis
```

## Usage

### URI Format

```
redis://key-or-channel?operation=subscribe&connection=localhost:6379
```

### Fluent DSL

```csharp
using redb.Route.Redis.Fluent;

// Pub/Sub — subscribe
From(Redis.Subscribe("notifications").Connection("localhost:6379"))
    .Log("Received: ${body}")
    .To("direct://process");

// Pub/Sub — publish
From("direct://outbound")
    .To(Redis.Publish("events").Connection("localhost:6379"));

// Streams — consumer group
From(Redis.XRead("order-stream")
        .Connection("localhost:6379")
        .ConsumerGroup("processors")
        .ConsumerName("node-1")
        .StreamReadCount(100))
    .To("direct://handle");

// Key-Value
From("direct://cache")
    .To(Redis.Set("session:user-123")
        .Connection("localhost:6379")
        .Ttl(3600));

// Lists
From("direct://enqueue")
    .To(Redis.LPush("task-queue").Connection("localhost:6379"));
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Connection()`, `.Database()`, `.Password()`, `.ConnectionFactory()` |
| **Key-Value** | `Redis.Set()`, `Redis.Get()`, `Redis.Del()`, `Redis.Exists()`, `Redis.Expire()`, `Redis.Incr()`, `Redis.Decr()`, `Redis.SetNx()` |
| **Pub/Sub** | `Redis.Publish()`, `Redis.Subscribe()`, `Redis.PSubscribe()` |
| **Streams** | `Redis.XAdd()`, `Redis.XRead()`, `Redis.XGroup()`, `.ConsumerGroup()`, `.ConsumerName()`, `.StreamMaxLength()`, `.StreamReadCount()`, `.StreamBlockTimeMs()`, `.StreamAutoAck()` |
| **Lists** | `Redis.LPush()`, `Redis.RPush()`, `Redis.LPop()`, `Redis.RPop()`, `Redis.LLen()`, `Redis.LRange()` |
| **Options** | `.Ttl()`, `.Transacted()`, `.PollDelay()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET
