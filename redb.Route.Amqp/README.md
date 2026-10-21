# redb.Route.Amqp

AMQP 1.0 transport for redb.Route via AMQPNetLite. Supports ActiveMQ Artemis, ActiveMQ Classic, Azure Service Bus, Amazon MQ, Qpid, and any AMQP 1.0 compliant broker.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Amqp?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Amqp)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Amqp
```

## Usage

### Fluent DSL

```csharp
using redb.Route.Amqp.Fluent;

// Consume from AMQP address
From(Amqp.Address("orders")
        .Host("artemis.local").Port(5672)
        .User("admin").Password("secret")
        .Credit(10)
        .Durable())
    .Log("Received order: ${body}")
    .To("direct://process");

// Produce to AMQP address
From("direct://send")
    .To(Amqp.Address("events")
        .Host("artemis.local")
        .User("admin").Password("secret")
        .MessageDurable()
        .ContentType("application/json"));

// Azure Service Bus
From(Amqp.Address("orders")
        .Host("mybus.servicebus.windows.net").Port(5671)
        .User("policy-name").Password("shared-access-key")
        .Ssl()
        .ContainerId("route-consumer"))
    .To("direct://handle");
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Host()`, `.Port()`, `.User()`, `.Password()`, `.ContainerId()`, `.VirtualHost()`, `.Ssl()`, `.ConnectionFactory()` |
| **Link** | `.Durable()`, `.ExpiryPolicy()`, `.TerminusTimeout()`, `.DistributionMode()`, `.Dynamic()`, `.FilterSelector()`, `.Capabilities()`, `.SenderSettleMode()`, `.ReceiverSettleMode()` |
| **Consumer** | `.Credit()`, `.AckMode()`, `.ConcurrentConsumers()`, `.ReceiveTimeout()` |
| **Producer** | `.MessageDurable()`, `.MessagePriority()`, `.MessageTtl()`, `.ContentType()`, `.Subject()`, `.GroupId()`, `.ReplyTo()`, `.Timeout()`, `.Transacted()`, `.LocalTransactions()`, `.Declare()`, `.RoutingType()` |

## Transactions

Inside a route's `.Transacted()` block a send **joins the transaction**: it goes out once the database has committed
and is dropped if the block rolls back. `.Transacted(false)` sends at once, outside the transaction; `.Transacted()`
requires an enclosing block and fails the step outside one. A request-reply producer (`.ReplyTo()`) always sends at
once and refuses `.Transacted()`.

The AMQP client would enlist a send in the ambient `System.Transactions` transaction by itself. The connector never
lets it: a send that leaves at once runs with the ambient transaction suppressed and a deferred one after the
transaction closed, so the broker never becomes a second resource next to the database. See the framework-wide
**Transactions** guide (`TRANSACTIONS.md` in the [redb.Route repository](https://github.com/redbase-app/redb)).

### Several sends in one block: `localTransactions`

By default the sends a producer deferred in a block go out one after the other: a failure part-way leaves the earlier
ones sent. With `localTransactions=true` (`.LocalTransactions()`) they commit in one **AMQP local transaction**: the
connector opens a link to the broker's transaction coordinator, declares a transaction, sends every message carrying
it and discharges it once — all of them arrive, or none. A send that cannot complete (the broker refuses it, or does
not settle it within `timeout` seconds — Artemis withholds credit on a full address) discharges the transaction as
failed, and the block's commit fails. The producer's transactions take turns on its link.

The broker must support AMQP local transactions: ActiveMQ Artemis, Apache Qpid, Azure Service Bus do; RabbitMQ's AMQP
1.0 plugin does not, and a broker that refuses the coordinator fails the commit with that said. The client's own
`System.Transactions` support is not used: its discharge never got an answer from Artemis, while the same transaction
driven through the protocol's own frames is answered at once.

## Headers

Exchange headers become application-properties of a sent message and of a reply, and the application-properties of
a delivery or a reply come back as exchange headers. The standard property names (`MessageId`, `CorrelationId`,
`Subject`...) go into the message properties instead; `redbAmqp.*` headers are exchange metadata and stay back.

`headerFilterStrategy=#name` names an `IHeaderFilterStrategy` in the registry that decides which headers the
endpoint carries, as in Apache Camel, in both directions of a request and a reply. It replaces the default,
`AmqpHeaderFilterStrategy`, entirely; derive from that class to add to it. The standard properties, the consumer's
`redbAmqp.*` metadata and the trace context are set whatever the strategy says.

```csharp
ctx.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy
{
    OutFilter = ["Correlation-ID"],
    FilterOnMatch = false,   // a whitelist: only the listed headers go out
});
// amqp://orders?host=localhost&headerFilterStrategy=#correlationOnly
```

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new AmqpConnectionFactory
{
    Host = "broker.internal",
    User = "svc-orders",
    Password = secrets.AmqpPassword,
});
// amqp://orders?connectionFactory=prod
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

A MULTICAST address (`routingType=MULTICAST` or the `topic` capability) and a dynamic node
(`dynamic=true`) run one receiver link whatever the value, with a warning: there every link is a
subscription or a node of its own, so N links would process each message N times instead of
sharing them, as IBM MQ topics do.

## Dynamic node

`dynamic=true` with no address asks the broker to create a node and name it (AMQP 1.0, 3.5.3). The
consumer reads that node, and `AmqpConsumer.Address` and the `redbAmqp.Address` header carry its
name once the consumer has started, for senders to reach it. A producer with `dynamic=true` sends to
a node the broker creates for it and logs its name. An endpoint with both an address and
`dynamic=true` is refused.

## Tracing

On the `redb.Route` activity source (`AddSource("redb.Route")`). The W3C context travels in the application
properties: `traceparent`, `tracestate`, `baggage`.

- **Consumer.** One `Consumer` span per message, `{address} receive`, over the whole unit of work, settlement included.
  Its parent is the sender's `traceparent`; without one it is a root, never a child of the activity the receive loop
  inherited from whoever started the routes. The sender's baggage is back on it, the route's spans are its children, it
  carries `redb.route.endpoint`, `messaging.message.id` and `messaging.amqp.subject`, and a failed route marks it an
  error.
- **Producer.** One `Producer` span per send, `{address} publish`; an error when the send fails. The message carries
  the context of this span: a `traceparent` the header bridge copied from a received message is replaced.
- `RouteEngineOptions.EnableTelemetry = false` opens neither span. A context that came in still goes out.
