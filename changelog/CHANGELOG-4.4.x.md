# redb.Route changelog: 4.4.x

Releases of the 4.4 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.4.2] — 2026-10-23

### Changed

- **VS Code extension 0.2.23: an icon and a fuller extension page.** The extension has its own icon, a description that
  names the route graph, and links to the repository and its issues. The README describes every feature, including
  editing in the graph and placeholder values. Nothing changes in how the extension works.

### Fixed

- **AS4 conformance demo: the worker registers its management API under the container name.** The PostgreSQL and SQL
  Server overlays of `demo/As4ConformanceDemo/deploy` set `Tsak:Cluster:ApiEndpoint` to `http://as4ct-tsak:9090`
  instead of `http://localhost:9090`, so the worker no longer warns about a loopback address at every start. Copies of
  the overlays change the same line.

## [4.4.1] — 2026-10-21

### Fixed

- **`seda:` works after `Stop()` and `Start()` of the same route context** (issue #12; broken in 4.2.2 to 4.4.0). The stop closed
  the queue for good: every send behind a seda hop failed with `ChannelClosedException` while the routes reported
  Started. On redb.Tsak this hit `/stop` then `/start`, `/restart` and the watchdog's automatic restart: the context
  looked healthy, but nothing behind a seda hop was processed. A consumer that starts again now opens a new queue. On an
  older version, recreate the context instead of restarting it (on Tsak, stop it and touch its `.tpkg`). Packages: `redb.Route`.

## [4.4.0] — 2026-10-21

> **Withdrawn on the day of release because of the seda restart bug in redb.Route; use 4.4.1.**

### Breaking

- **Elasticsearch: a delete of a missing document succeeds**, with `redbEs.Result` = `NotFound`; it used to fail the
  exchange. Packages: `redb.Route.Elasticsearch`. Upgrade: [UPGRADING.md](../UPGRADING.md#elasticsearch-delete).
- **FCM: topic subscriptions go through the FCM v1 API, one request per token** (FirebaseAdmin 3.7). What a route
  sees is unchanged; permissions, quota and firewall rules may need a change. Packages: `redb.Route.Firebase`. Upgrade: [UPGRADING.md](../UPGRADING.md#fcm-topics).
- **FTP: an FTPS data channel with a different, untrusted certificate is refused** (FluentFTP 55.1) while
  `validateCertificate=true`, the default. It used to be accepted without a check. Packages: `redb.Route.Ftp`. Upgrade: [UPGRADING.md](../UPGRADING.md#ftps-data-channel).
- **HTTP: `bridgeHeaders` and `copyResponseHeaders` are Apache Camel's `skipRequestHeaders` and
  `skipResponseHeaders`, with the meaning reversed.** An old name fails the endpoint instead of being ignored. Packages: `redb.Route.Http`. Upgrade: [UPGRADING.md](../UPGRADING.md#http-header-options).
- **`RegisterServerConfigurator` is replaced by `RegisterEndpoints`**, which maps endpoints with the component's own
  container on a listener before or after it started. `ConfigureHubServices` and `HubServicesConfigurator` take the hub
  they configure, and a hub's container no longer sees the services of the listener. Packages: `redb.Route.Http.Hosting`, `redb.Route.SignalR`. Upgrade: [UPGRADING.md](../UPGRADING.md#signalr-on-a-shared-listener).
- **RabbitMQ: a consumer with an empty queue name and `declare=false` fails at start.** It read an unbound queue and
  got nothing. Packages: `redb.Route.RabbitMQ`. Upgrade: [UPGRADING.md](../UPGRADING.md#rabbitmq-empty-queue-name).
- **SQL on PostgreSQL: Npgsql 10.** A failed batch reports the first item of the failed chunk, untyped `date` /
  `time` values arrive as `DateOnly` / `TimeOnly`, and `Root Certificate` alone validates the server's chain. Packages: `redb.Route.Sql`. Upgrade: [UPGRADING.md](../UPGRADING.md#sql-on-postgresql-npgsql-10).
- **`redb.Route.Validation.Adapters` brings FluentValidation 12** (was 11): a validator written against the API that
  12 removed no longer compiles. Packages: `redb.Route.Validation.Adapters`. Upgrade: [UPGRADING.md](../UPGRADING.md#fluentvalidation-12).

### Security

- **SignalR broadcasts stay on their own hub path.** `Clients.All`, `Group` and `User` of one hub reached the clients of
  every hub on the same listener, and through a Redis backplane every hub of the connector on that Redis. Each hub now
  has a container of its own, and `UseRedisBackplane` keys the Redis channels on the hub path. Packages: `redb.Route.SignalR`. Upgrade: [UPGRADING.md](../UPGRADING.md#signalr-on-a-shared-listener).

### Added

- **`${prefix.}` resolvers of a route context**: `context.RegisterPrefixResolver("order.", ...)` adds an expression root
  for that context only and goes with it. The process-wide `ExpressionResolver.RegisterPrefixResolver` is unchanged. Packages: `redb.Route`.
- **Header filter strategy, as Apache Camel's `HeaderFilterStrategy`**: `headerFilterStrategy=#name` decides which
  headers HTTP, Kafka, RabbitMQ, AMQP, Azure Service Bus, IBM MQ, gRPC, SQS/SNS and AS2 carry each way. Every
  transport keeps its behaviour by default; `DefaultHeaderFilterStrategy` takes names, patterns and prefixes; the README of redb.Route has the details ([Header filter strategy](../redb.Route/README.md#header-filter-strategy)). Packages: `redb.Route`, `redb.Route.Http`, `redb.Route.Kafka`, `redb.Route.RabbitMQ`, `redb.Route.Amqp`.
- **HTTP: `skipControlHeaders`**, Apache Camel's option: the endpoint's options decide the method and query instead of
  `redbHttp.Method` / `redbHttp.Query` that came with the exchange. Off by default. Packages: `redb.Route.Http`.
- **`Rabbit.Exchange(name, type)` builds a RabbitMQ endpoint without a queue name.** A producer publishes to the
  exchange; with `.Declare()` a consumer reads a temporary queue of its own bound to it, one copy per instance on fanout. Packages: `redb.Route.RabbitMQ`.
- **`redb.Route.SignalR.StackExchangeRedis`: a Redis backplane for SignalR hubs.** `UseRedisBackplane` scales hubs out
  through Redis with the channels of each hub keyed on its path after the application's `ChannelPrefix`; two hubs of the
  process that would share channels (one path on two ports) refuse to start. Packages: `redb.Route.SignalR.StackExchangeRedis`, `redb.Route.SignalR`.
- **VS Code extension 0.2.22: `#` offers the beans of the document.** Typing `#` where a value names a registry object
  (`?headerFilterStrategy=#`, `dataSource="#"`, `bean:#`) lists the `<bean name>` declarations of the current document.
  There is no type filter; a missing or wrong bean is still reported by `redb-route-xml pack` and `check`.

### Changed

- **`direct-vm:` binds its URI parameters**, so an unknown one fails the endpoint. Routes that pass
  `redb-route-xml check` are not affected. Packages: `redb.Route`.
- **`jpath` runs on JsonPath.Net 2.2.0 (MIT) instead of 3.0.2.** The 3.x releases of JsonPath.Net and Json.More.Net
  are published under a fee-based EULA for revenue-generating users; 4.0.0 to 4.3.0 depended on them, so commercial
  users are advised to upgrade. `jpath` behaves as before. Packages: `redb.Route`.
- **Quartz 3.22** (was 3.16) with corrections to when a job fires: `[DisallowConcurrentExecution]` holds across the
  whole cluster, day-of-month `L`/`LW`/`W` and DST edge cases fire as written. A clustered job store needs three new
  column sets. Packages: `redb.Route.Quartz`. Upgrade: [UPGRADING.md](../UPGRADING.md#quartz-322).
- **Dependency updates**: Google.Cloud.Firestore 4 and Storage.V1 5 (Firebase), Elastic.Clients.Elasticsearch
  8.19.27, IBMXMSDotnetClient 9.4.5.1, StackExchange.Redis 2.13.17, BouncyCastle 2.7.0 and MimeKit 4.18.1 (AS4), and
  everything else to its latest stable version.

### Fixed

- **A collectible load context unloads after its types went through System.Text.Json.** On unload redb.Route clears
  the caches of the .NET libraries, as `dotnet watch` does; the first serialization afterwards runs cold. Packages: `redb.Route`.
- **Caches keyed by type let the types of an unloadable module go**: XML serializers, SOAP and POJO bodies, the
  Protobuf data format, batch writes and endpoint options. They held a collectible context alive for good. Packages: `redb.Route`, `redb.Route.Soap`, `redb.Route.DataFormats.Protobuf`.
- **AMQP: `dynamic=true` creates a dynamic node**; it was accepted and ignored. `redbAmqp.Address` carries the node's name. Packages: `redb.Route.Amqp`.
- **AMQP: a MULTICAST consumer processes each message once** with `concurrentConsumers` above 1; it runs one link and
  logs a warning. Packages: `redb.Route.Amqp`.
- **IBM MQ: a reply that cannot be read fails the request at once** with `receiveMode=listener`, instead of waiting for
  the timeout. Packages: `redb.Route.IbmMq`.
- **RabbitMQ: a consumer with an empty queue name reads the queue bound to the exchange**, so every consumer of a
  fanout gets its copy. A producer with an empty queue name no longer declares a queue of its own. Packages: `redb.Route.RabbitMQ`.
- **SignalR hubs on a listener shared by several route contexts.** A hub joins and leaves a listener another context
  serves without touching its other hubs and routes (it failed to start and left the context DEGRADED), a broadcast
  endpoint no longer maps the hub a second time, and after a restart of the context the new hub answers. Packages: `redb.Route.SignalR`, `redb.Route.Http.Hosting`.
- **SQL: `outputClass` resolves through the route context, once per endpoint**, as `bean:` does. After a module was
  reloaded beside its previous version, every endpoint got the class of the version loaded first. Packages: `redb.Route.Sql`.
