# redb.Route.Redis

Redis transport for redb.Route. Pub/Sub, Streams, key-value, lists, sorted sets — every Redis data structure is reachable as an endpoint URI via StackExchange.Redis; the builder has factories for keys, Pub/Sub, streams and lists, and `Redis.Command(operation, key)` for the rest.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Redis?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Redis)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Redis
```

## Usage

### URI Format

```
redis:OPERATION:resource?connectionString=localhost:6379&...
```

The operation is the first path segment (`SET`, `GET`, `PUBLISH`, `SUBSCRIBE`, `XADD`, `XREAD`, `BLPOP`, …), the
resource — key, channel or stream — follows it: `redis:SET:session:42?ttl=300`, `redis:SUBSCRIBE:notifications`.
`redis:SET/session/42` works too. A parameter the endpoint cannot read (a misspelt option, a value of the wrong type) is
refused by name when the endpoint is created.

### Fluent DSL

```csharp
using redb.Route.Redis;

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
| **Streams** | `Redis.XAdd()`, `Redis.XRead()`, `Redis.XGroup()`, `.ConsumerGroup()`, `.ConsumerName()`, `.StreamMaxLength()`, `.StreamReadCount()`, `.StreamBlockTime()`, `.StreamStartPosition()`, `.StreamNoAck()`, `.StreamClaimMinIdle()` |
| **Lists** | `Redis.LPush()`, `Redis.RPush()`, `Redis.LPop()`, `Redis.RPop()`, `Redis.LLen()`, `Redis.LRange()`, `.ProcessingList()` |
| **Any operation** | `Redis.Command(operation, key)` — the factories above cover keys, Pub/Sub, streams and lists; every other operation (`HSET`, `ZADD`, `GEOADD`, `BLPOP`, …) is built with it |
| **Options** | `.Ttl()`, `.Transacted()`, `.PollDelay()`, `.UsePattern()`, `.CustomCommand()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

`Redis.Command(operation, key)` names an **operation** of this connector. The `COMMAND` operation is something else:
a raw Redis command, named with `.CustomCommand("PING")` (or the `redbRedis.Command` header) and run through
`ExecuteAsync`.

## Consumers and delivery

| Consumer | How it reads | A failed route |
|---|---|---|
| `SUBSCRIBE` / `PSUBSCRIBE` | Pub/Sub; `PSUBSCRIBE` subscribes to a pattern (`events.*`), `usePattern` makes `SUBSCRIBE` do so too | lost: Pub/Sub keeps nothing |
| `XREAD` / `XGROUP` with `consumerGroup` | `XREADGROUP`, acknowledged (`XACK`) after the route succeeded | stays pending; `streamClaimMinIdleMs` claims it again (`XAUTOCLAIM`) once it idled that long — a dead consumer's entries too |
| the same with `streamNoAck=true` | `XREADGROUP NOACK`: delivered when read | not read again (at-most-once) |
| `XREAD` without a group | a position of its own, moved past every entry read; starts at the entries added from now on, or at `streamStartPosition` (`0` = from the beginning) | moved past, like a Kafka consumer without `breakOnFirstError` |
| `BLPOP` / `BRPOP` | polls `LPOP` / `RPOP` every `pollDelayMs` (a blocking pop would hold the connection the whole process shares) | lost (at-most-once) |
| the same with `processingList` | `LMOVE` into that list, removed after the route succeeded | back to the head of the queue and processed again; what a previous run left there is returned at start. One consumer per processing list |

`streamClaimMinIdleMs` must exceed the longest time a route takes: a slower route would have its entry claimed by another
consumer while it still works on it.

## Transactions

Redis is a store first, so inside a route's `.Transacted()` block only the operations that announce work wait for the
commit:

- **`PUBLISH` and `XADD`** follow the block like a broker send: they run once the database has committed and are
  dropped if the block rolls back. The entry id (`redbRedis.Stream.MessageId`) and the recipient count reach the exchange
  headers after the commit. `.Transacted(false)` runs them at once.
- **Other writes** (`SET`, `INCR`, `SETNX`, lists, hashes, sets) run at once: they exist for their effect and their
  result right now. `.Transacted()` holds one back until the commit; the route then does not get its result.
- **Reads and pops** (`GET`, `LPOP` and the like) exist for what they return and refuse `.Transacted()` when the route
  starts.

A deferred operation is the same operation, run after the commit on a snapshot of the exchange taken at the step: the
same key, body, fields, headers and TTL. Outside a block, `.Transacted()` fails the step instead of losing the write.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new RedisConnectionFactory
{
    ConnectionString = "redis.internal:6379",
    Password = secrets.RedisPassword,
});
// redis:GET:cache?connectionFactory=prod
```
