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
| **Consumer** | `.Credit()`, `.AutoAccept()`, `.ConcurrentConsumers()`, `.ReceiveTimeout()` |
| **Producer** | `.MessageDurable()`, `.MessagePriority()`, `.MessageTtl()`, `.ContentType()`, `.Subject()`, `.GroupId()`, `.ReplyTo()`, `.Timeout()`, `.Transacted()`, `.Declare()`, `.RoutingType()` |

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
