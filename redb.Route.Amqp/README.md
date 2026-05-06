# redb.Route.Amqp

AMQP 1.0 transport for redb.Route via AMQPNetLite. Supports ActiveMQ Artemis, ActiveMQ Classic, Azure Service Bus, Amazon MQ, Qpid, and any AMQP 1.0 compliant broker.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Amqp?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Amqp)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

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

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
