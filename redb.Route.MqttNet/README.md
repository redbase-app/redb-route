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
| **Subscribe** | `Mqtt.Subscribe(topic)`, `.SharedSubscription(group)` |
| **Publish** | `Mqtt.Publish(topic)`, `.Retain()`, `.MessageExpiryInterval()`, `.ContentType()`, `.ResponseTopic()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
