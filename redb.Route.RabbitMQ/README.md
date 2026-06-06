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
| **Consumer** | `.ConcurrentConsumers()`, `.PrefetchCount()`, `.Transacted()`, `.Mandatory()`, `.ReplyTo()`, `.Timeout()` |
| **Message** | `.ContentType()`, `.MessageTtl()`, `.Expires()` |
| **DLX** | `.DeadLetterExchange()`, `.DeadLetterRoutingKey()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
