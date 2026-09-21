# redb.Route.RabbitMQ

RabbitMQ transport for redb.Route. Consumer and producer with exchanges, queues, dead-letter, priority, TTL, and the official RabbitMQ.Client 7.x.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.RabbitMQ?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.RabbitMQ)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.RabbitMQ
```

## Usage

### URI Format

```
rabbitmq://queue-name?host=localhost&exchange=my-exchange&routingKey=order.*
```

### Fluent DSL

```csharp
using redb.Route.RabbitMQ.Fluent;

// Consumer with exchange binding
From(Rabbit.Queue("orders")
        .Host("rabbitmq.local")
        .Username("guest").Password("guest")
        .Exchange("order-exchange", "topic")
        .RoutingKey("order.new")
        .PrefetchCount(50)
        .ConcurrentConsumers(4))
    .Log("Order received")
    .To("direct://process");

// Producer with dead-letter
From("direct://outbound")
    .To(Rabbit.Queue("events")
        .Host("rabbitmq.local")
        .Durable()
        .MessageTtl(86400000)
        .DeadLetterExchange("dlx")
        .DeadLetterRoutingKey("failed"));
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Host()`, `.Port()`, `.Username()`, `.Password()`, `.VirtualHost()`, `.ConnectionFactory()`, `.ClientName()`, `.Ssl()`, `.SslServerName()`, `.SslCertPath()`, `.SslCertPassphrase()` |
| **Recovery** | `.AutomaticRecovery()`, `.TopologyRecoveryEnabled()`, `.RecoveryInterval()`, `.Heartbeat()`, `.ConnectionTimeout()` |
| **Exchange** | `.Exchange(name, type?)`, `.ExchangeDurable()`, `.ExchangeAutoDelete()`, `.Declare()` |
| **Queue** | `.Durable()`, `.AutoDelete()`, `.Exclusive()`, `.RoutingKey()`, `.MaxLength()`, `.MaxLengthBytes()`, `.Overflow()`, `.QueueType()`, `.MaxPriority()` |
| **Consumer** | `.ConcurrentConsumers()`, `.PrefetchCount()`, `.AutoAck()`, `.Transacted()`, `.Mandatory()`, `.ReplyTo()`, `.Timeout()` |
| **Message** | `.ContentType()`, `.MessageTtl()`, `.Expires()` |
| **DLX** | `.DeadLetterExchange()`, `.DeadLetterRoutingKey()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Consumer concurrency & acknowledgement

- **`.ConcurrentConsumers(N)`** is the single knob for consumer-side parallelism: it sets both the
  channel's AMQP consumer-dispatch concurrency and the app-level concurrency semaphore, so up to
  **N** messages from the queue are processed concurrently. Default `1` (strictly serial — message
  order preserved). With `N > 1`, ordering is not preserved and your processor must be
  thread-safe. Keep `.PrefetchCount()` ≥ `N` so the broker keeps the parallel slots fed.
- **`.AutoAck()`** switches the consumer to broker-side auto-acknowledge (**at-most-once**): the
  broker settles each delivery on hand-off, so a failed turn does **not** requeue. Default off
  (**at-least-once**: ack after a successful turn, nack-requeue on failure). Cannot be combined
  with `.Transacted()`.

## Transactions

- **Consumer.** `.Transacted()` takes deliveries on a transacted channel. Inside a route's
  `.Transacted()` block the ack comes last, after the database and the deferred sends have committed.
- **Producer.** Inside a route's `.Transacted()` block a publish **joins the transaction**: it goes
  out once the database has committed and is dropped if the block rolls back. `.Transacted(false)`
  publishes at once, outside the transaction; `.Transacted()` requires an enclosing block and fails
  the step outside one. A request-reply producer (`.ReplyTo()`) always publishes at once and refuses
  `.Transacted()`: a request held back until the commit would wait for a reply that cannot come.
- **Several publishes in one block.** The deferred publishes of one producer in a block are committed
  in one channel transaction, on a channel of the producer's own (a channel with publisher confirms
  cannot run transactions): they arrive together, in order, or not at all. The producer's batches
  take turns on that channel, and a channel transaction is markedly slower than publisher confirms.

See the framework-wide **Transactions** guide (`TRANSACTIONS.md` in the
[redb.Route repository](https://github.com/redbase-app/redb)).

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new RabbitMQConnectionFactory
{
    Host = "rabbit.internal",
    Username = "svc",
    Password = secrets.RabbitPassword,
});
// rabbitmq://orders?connectionFactory=prod
```

## Concurrency

The default is **1** concurrent consumer — the industry norm (Camel, Spring, the Azure SDK all
ship 1): a single consumer preserves ordering and your handlers need no thread safety.
Parallelism is an explicit opt-in:

```
concurrentConsumers=4       # a fixed worker count
concurrentConsumers=auto    # max(CPU count, 2) — the NServiceBus formula
```

Anything else — `0`, a negative, a typo — fails at endpoint creation naming the option (the old
int-typed option silently fell back to 1). Raising the value trades ordering for throughput:
messages from the same queue are processed out of order, and your processors must be safe to
run in parallel.
