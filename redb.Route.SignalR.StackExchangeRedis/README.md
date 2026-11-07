# redb.Route.SignalR.StackExchangeRedis

Redis backplane for the [redb.Route SignalR transport](../redb.Route.SignalR/README.md). It scales hubs out across
replicas, and it keeps the hubs of one listener apart on a shared Redis.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.SignalR.StackExchangeRedis?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.SignalR.StackExchangeRedis)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

| | |
|---|---|
| **NuGet** | `redb.Route.SignalR.StackExchangeRedis` |
| **Dependencies** | `redb.Route.SignalR`, `Microsoft.AspNetCore.SignalR.StackExchangeRedis` (the ASP.NET Core line of the target framework: 8.0, 9.0 or 10.0) |
| **Namespace** | `redb.Route.SignalR` |

## Why

A SignalR hub keeps its connections and groups in the memory of one process. With two replicas behind a load
balancer, a broadcast sent on one of them reaches only the clients attached to that one. A backplane carries every
broadcast (`Clients.All`, `Group`, `User`) through Redis to the hub on every replica.

SignalR names its Redis channels after the hub class. Every hub of the redb.Route transport is the same class, so a
plain `AddStackExchangeRedis` gives `/orders` and `/alerts` the same channels, and their clients hear each other's
broadcasts. This package keys the channels on the hub path, in code, for every hub.

## Installation

```bash
dotnet add package redb.Route.SignalR.StackExchangeRedis
```

## Usage

With the SignalR registration of a host:

```csharp
services.AddRedbRouteSignalR(o => o.UseRedisBackplane("redis:6379,password=..."));
```

With a component built by hand, as a Tsak module or a test does:

```csharp
context.AddComponent(new SignalRComponent { ServerManager = shared, Authenticate = Validate }
    .UseRedisBackplane("redis:6379"));
```

The routes do not change: a hub (`From(SignalR.Hub("0.0.0.0:5090/hub/ui"))`) and its broadcast
(`To(SignalR.Broadcast("0.0.0.0:5090/hub/ui").Method("uiChanged"))`) work as on a single replica, and a broadcast
from any replica reaches the clients of `/hub/ui` on all of them. XML routes are the same URIs; the backplane is a
setting of the component, not of a route.

The second overload takes the Redis options alone, for a connection factory or a `ConfigurationOptions` built in code:

```csharp
o.UseRedisBackplane(redis => redis.ConnectionFactory = async writer =>
    await ConnectionMultiplexer.ConnectAsync(myConfigurationOptions, writer));
```

## Channel prefix

| Configured `ChannelPrefix` | Hub path | Channels of the hub start with |
|---|---|---|
| none | `/hub/ui` | `/hub/ui` |
| `myapp` | `/hub/ui` | `myapp/hub/ui` |

The hub path is always part of the prefix. A prefix you set names the application or the stand, so several of them can
share one Redis:

```csharp
o.UseRedisBackplane("redis:6379", redis => redis.Configuration.ChannelPrefix = RedisChannel.Literal("honest-prod"));
```

The port is not part of the prefix: replicas behind a balancer may listen on different ports and still meet on the
channels of their hub.

## One hub, one set of channels per process

Within one process the same hub path on two ports is two different hubs. On one Redis without different prefixes they
would publish on the same channels, so the second of them refuses to start:

```text
SignalR hub /hub/ui on 0.0.0.0:5091 cannot use the Redis backplane channels '/hub/ui' on redis:6379: the hub /hub/ui
on 0.0.0.0:5090 of this process already publishes on them ... Give the hubs different paths, or different
ChannelPrefix values in UseRedisBackplane.
```

Give the hubs different paths, or give each its own `ChannelPrefix`. The check covers the hubs of the process that use
this package with one copy of it loaded; on a Tsak worker that means the package is in the worker's shared layer, next
to `redb.Route.SignalR`.

## Tsak cluster

Every worker of a Tsak cluster is a replica: it runs the same packages, and the hub of each worker publishes on the
same channels. The node id plays no part in the prefix, which is what lets the workers hear each other. Point the
workers at one Redis, keep one `ChannelPrefix` per cluster (or none), and a broadcast from the worker that handled a
message reaches the clients on every worker.

## A backplane added by hand

`ConfigureHubServices` still accepts any backplane. Its delegate runs once per hub and gets the hub's description,
so a hand-written Redis registration can key the channels the same way:

```csharp
o.ConfigureHubServices((services, hub) => services.AddSignalR()
    .AddStackExchangeRedis("redis:6379", redis => redis.Configuration.ChannelPrefix = RedisChannel.Literal(hub.Path)));
```

Keeping the hubs apart is then the host's responsibility, and the one-hub-per-channels check above does not run.

## Limitations

- Groups do not survive a reconnect, with or without a backplane: a reconnect is a new connection. See
  [Groups do not survive a reconnect](../redb.Route.SignalR/README.md#groups-do-not-survive-a-reconnect).
- The backplane carries broadcasts, not connections: a client stays attached to the replica it negotiated with, so
  the balancer needs sticky sessions, unless every client skips negotiation and connects with WebSockets directly.
- With a connection factory and no endpoints in the options, the one-hub-per-channels check treats every factory as
  the same Redis.
