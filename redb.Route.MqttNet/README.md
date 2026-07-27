# redb.Route.MqttNet

MQTT 5.0 transport for redb.Route via MQTTnet. Subscribe consumer, publish producer, shared subscriptions, QoS levels, retained messages, and TLS.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.MqttNet?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.MqttNet)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.MqttNet
```

## Usage

### Fluent DSL

```csharp
using redb.Route.MqttNet.Fluent;

// Subscribe to topic
From(Mqtt.Subscribe("sensors/temperature/#")
        .Server("mqtt.example.com").Port(1883)
        .ClientId("route-consumer")
        .Qos(MqttQualityOfServiceLevel.AtLeastOnce))
    .Log("Temperature: ${body}")
    .To("direct://process");

// Publish to topic
From("direct://alerts")
    .To(Mqtt.Publish("alerts/critical")
        .Server("mqtt.example.com")
        .Username("svc").Password("secret")
        .Qos(MqttQualityOfServiceLevel.ExactlyOnce)
        .Retain());

// Shared subscription (load balancing across consumers)
From(Mqtt.Subscribe("orders/new")
        .Server("broker.local")
        .SharedSubscription("order-processors")
        .UseTls())
    .To("direct://handle-order");

// Broker configuration (shared across routes)
From(Mqtt.Subscribe("events/#")
        .Broker("main")
        .Qos(MqttQualityOfServiceLevel.AtLeastOnce))
    .To("seda://events");
```

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Broker(name)`, `.Server()`, `.Port()`, `.Username()`, `.Password()`, `.ClientId()`, `.UseTls()`, `.KeepAlive()`, `.CleanSession()` |
| **QoS** | `.Qos(level)` — AtMostOnce, AtLeastOnce, ExactlyOnce |
| **Subscribe** | `Mqtt.Subscribe(topic)`, `.SharedSubscription(group)`, `.ConcurrentConsumers(n)` |
| **Publish** | `Mqtt.Publish(topic)`, `.Retain()`, `.MessageExpiryInterval()`, `.ContentType()`, `.ResponseTopic()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

### Concurrent processing

`.ConcurrentConsumers(n)` (default `1` = serial) processes up to `n` received messages in parallel on a
worker pool. Each message is acknowledged **only after** its worker finishes (manual ack), so at-least-once
is preserved for QoS 1/2 — a failed message is not acked and the broker redelivers. Ordering is not
preserved when `n > 1`.

```csharp
From(Mqtt.Subscribe("telemetry/#").Broker("main").Qos(1).ConcurrentConsumers(5))
    .Process(IngestReading);
```

See the framework-wide **Concurrency & Parallelism** guide (`CONCURRENCY.md` in the
[redb.Route repository](https://github.com/redbase-app/redb)) for how this compares to the `.Threads(N)`
processing EIP.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET
