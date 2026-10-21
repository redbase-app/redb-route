# Upgrading redb.Route

What to check and change when moving to a new version. Each section lists the changes that need action;
[CHANGELOG.md](CHANGELOG.md) has the full list of what changed.

## 4.4

### HTTP header options

`bridgeHeaders` and `copyResponseHeaders` are Apache Camel's `skipRequestHeaders` and `skipResponseHeaders`, with the
meaning reversed:

| Before | Now |
|---|---|
| `bridgeHeaders=false` | `skipRequestHeaders=true` |
| `copyResponseHeaders=false` | `skipResponseHeaders=true` |
| `NoBridgeHeaders()` | `SkipRequestHeaders()` |
| `NoCopyResponseHeaders()` | `SkipResponseHeaders()` |

The defaults keep today's behaviour and need no option. **An old name fails the endpoint, it is not ignored**: the
HTTP options accept parameters they have no property for (`param.*`), so an unknown name would be dropped without a
word and `bridgeHeaders=false` would quietly send every header. Search URIs and markup for `bridgeHeaders` and
`copyResponseHeaders` before upgrading.

Which headers HTTP carries is now its default header filter strategy, `HttpHeaderFilterStrategy`; an endpoint can
name another with `headerFilterStrategy=#name`. The protocol fields (`HttpHeaders.ProtocolHeaders`: hop-by-hop,
`Host`, `Content-*`) are never carried, whatever the strategy.

### FluentValidation 12

`redb.Route.Validation.Adapters` brings FluentValidation 12 (was 11) into the project that uses it. A validator
written against the API that 12 removed no longer compiles there:

- `Transform` / `TransformForEach`;
- `CascadeMode.StopOnFirstFailure` and the `CascadeMode` properties: use `ClassLevelCascadeMode` /
  `RuleLevelCascadeMode`;
- `InjectValidator`: use `SetValidator` with constructor injection.

FluentValidation 12 needs .NET 8 or later.

### FTPS data channel

FluentFTP 55.1 (was 54.1) checks the certificate of every FTPS data connection. With `validateCertificate=true`, the
default, a server that presents on the data channel a certificate other than the control channel's, and untrusted,
fails the transfer with an `AuthenticationException`. Make the server present a trusted certificate on both
channels, or set `validateCertificate=false` to keep accepting it.

### Elasticsearch delete

`elasticsearch://Delete` of a document id the index does not hold used to fail the exchange; it now completes, and
`redbEs.Result` is `NotFound` (`Deleted` when the document was there). A route that relied on the error to catch a
wrong id checks that header instead.

### FCM topics

FirebaseAdmin 3.7 (was 3.5) manages topics through the FCM v1 API instead of the Instance ID batch API.
`SubscribeToTopic` and `UnsubscribeFromTopic` send one request per registration token, up to 100 at a time, where they
sent one request for up to 1000 tokens. What a route sees stays the same: `redbFcm.SuccessCount` and
`redbFcm.FailureCount` per call, and a token already subscribed counts as a success. Before upgrading:

- the service account needs the FCM v1 permission to manage registrations, not the Instance ID one;
- a large token list costs as many requests as it has tokens, which counts against the project's FCM quota;
- a firewall or proxy that let through `iid.googleapis.com` has to let through `fcm.googleapis.com` for these calls.

### SQL on PostgreSQL: Npgsql 10

- **The failed item of a batch.** Npgsql no longer names the failed command of a batch, so that queries and their
  parameters do not leak into logs. A failed batch write reports in `redbSql.batchFailedIndex` the first item of the
  failed chunk, not the item itself. For the exact item add `Include Failed Batched Command=true` to the connection
  string; the component does not set it on its own.
- **`date` and `time` columns** are read as `DateOnly` and `TimeOnly` where the target type is not given: a row read
  into a dictionary, a scalar read as `object`. Typed targets (`DateTime`, `TimeSpan` properties and parameters) are
  converted as before.
- **TLS with a root certificate.** With `Root Certificate` in the connection string only that certificate validates
  the server's chain; the system certificate store is no longer consulted as well.

### Quartz 3.22

Quartz moves from 3.16 to 3.22. Corrections that change when or whether a job fires:

- `[DisallowConcurrentExecution]` (stateful cron routes) holds a job to one run at a time across the whole cluster,
  where it used to be held per node only;
- day-of-month `L`, `LW` and `W` expressions that used to misfire now fire as written;
- fire times in the repeated hour of a DST fall-back, day-based calendars near DST changes and
  `DailyTimeIntervalTrigger` past `EndTimeUtc` are corrected;
- pausing or resuming groups by prefix covers every matching group; `ResumeAll` resumes empty paused groups.

A clustered job store (AdoJobStore) warns at start while three sets of columns are missing: `MISFIRE_ORIG_FIRE_TIME`
(3.17), `EXECUTION_GROUP` (3.18), `PREFERRED_NODE` and `PREFERRED_NODE_AUTO` (3.19). Without them Quartz runs as 3.16
did. An application that owns its Quartz tables adds them with the Quartz.NET 3.x migrations
(`database/migrations/3.17` to `3.19`); a redb.Tsak worker adds them itself at start.

### RabbitMQ empty queue name

A consumer with an empty queue name and `declare=false` now fails at start: only the topology declaration creates and
binds a temporary queue, so such a consumer read an unbound queue and got nothing. Name the queue to read, or set
`declare=true`. In the fluent API this endpoint is `Rabbit.Exchange(name, type).Declare()`: `Rabbit.Queue` takes no
empty name.

### SignalR on a shared listener

A hub is served from a service container of its own and joins or leaves a running listener, so the listener API
changes for a component that brings its own endpoints, and the hub configuration runs once per hub.

`RegisterServerConfigurator` and `UnregisterServerConfigurator` are removed. `RegisterEndpoints` maps the endpoints
with the component's own container, before or after the listener started, and returns a registration to dispose:

```csharp
// was: services went into the listener's container, endpoints were mapped when it was built, not after
var handle = manager.RegisterServerConfigurator(host, port,
    services: s => s.AddSingleton<StatusSource>(),
    endpoints: app => app.MapGet("/status", (StatusSource s) => s.Current()));
manager.UnregisterServerConfigurator(handle);

// now: the endpoints come with their own container and may join a running listener
var services = new ServiceCollection().AddRouting().AddSingleton<StatusSource>().BuildServiceProvider();
var registration = manager.RegisterEndpoints(host, port, services,
    endpoints => endpoints.MapGet("/status", (StatusSource s) => s.Current()));
registration.Dispose();
await services.DisposeAsync();
```

A middleware the configurator added with `app.Use(...)` has no place in a registration: put the check on the endpoints
as a convention or an endpoint filter.

Every listener accepts WebSocket upgrades now. `EnableWebSockets` is no longer needed to get them; it sets the
keep-alive of a listener that has not started, and on a running listener it changes nothing instead of throwing.

`ConfigureHubServices` and `SignalRComponent.HubServicesConfigurator` take the hub they configure:

```csharp
// was
o.ConfigureHubServices(s => s.AddSignalR().AddStackExchangeRedis("redis:6379"));

// now: the backplane package keys the channels on the hub path
o.UseRedisBackplane("redis:6379");                 // redb.Route.SignalR.StackExchangeRedis

// or, by hand, with the prefix the host is now responsible for
o.ConfigureHubServices((s, hub) => s.AddSignalR().AddStackExchangeRedis("redis:6379",
    redis => redis.Configuration.ChannelPrefix = RedisChannel.Literal(hub.Path)));
```

A backplane added without a prefix that includes the hub path lets the hubs of one listener see each other's
broadcasts through Redis, as every hub of the connector is the same hub class. Hosts with more than one hub path on
one Redis move to `UseRedisBackplane` or add the prefix.

The hub's container is its own: `Context.GetHttpContext().RequestServices` of a hub request resolves from it, and
services of the listener are not there. `messagePack=true` and `transport=...` apply to their own hub; a hub that got
MessagePack because another hub of the same port asked for it now needs `messagePack=true` itself.
