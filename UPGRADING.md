# Upgrading redb.Route

What to check and change when moving to a new version. Each section lists the changes that need action;
[CHANGELOG.md](CHANGELOG.md) has the full list of what changed.

## 4.5.0

### A Redis consumer starts only when it receives

A `redis:` consumer (SUBSCRIBE, PSUBSCRIBE, XREAD, XGROUP, BLPOP, BRPOP) now subscribes, creates its consumer group
and pins its XREAD position before `Start()` returns. Before, it did that in the background after the route had
started: a message published or an entry added in between was lost, and a Redis that was down at start left a
consumer that looked started and never received anything, with an error in the log only.

What the operator sees now: when Redis does not answer at start, the route, and with it the context, fails to start
with `InvalidOperationException: Redis consumer redis:<OPERATION>:<resource> cannot start, Redis did not answer: ...`.
In a Tsak worker the context shows as failed instead of running. Check that Redis is reachable from the worker before
the context starts. Once a consumer has started, a later outage is handled as before: StackExchange.Redis reconnects
and subscribes again. Start waits up to the connect and command timeouts of the connection (5 s each by default)
before it gives up.

### An AMQP consumer starts only when the broker accepted its link

An `amqp:` consumer now waits in `Start()` until the broker has answered every receiver link it attaches, as it did
for `dynamic=true` only. Before, a link the broker refused (an address it does not serve, a routing type the address
does not have, no permission to read it) was logged by the receive loop, and the consumer looked started and never
received anything. On a MULTICAST address the subscription is the link itself, so it is now in place when `Start()`
returns.

What the operator sees now: the route, and with it the context, fails to start with
`InvalidOperationException: AMQP consumer amqp:<address> cannot start: the broker refused the receiver link
(<condition>: <description>).`, or `... the broker did not answer the receiver link within <timeout> s.` after the
endpoint's `timeout` (30 s by default). In a Tsak worker the context shows as failed instead of running. Fix the
address, the routing type or the permissions the message names.

### An MQTT publish goes to its endpoint topic

An `mqtt:` publish no longer reads `redbMqtt.topic` as its target. The connector writes that header itself, with the
topic a message was received on and the topic of every publish, so a second publish in a route went to the first
topic, and a received message republished to `mqtt://alerts/...` went back to the topic it came from. A route that set
`redbMqtt.topic` to choose where a publish goes sets `redbMqtt.overrideTopic` (`MqttHeaders.OverrideTopic`) instead;
`redbMqtt.topic` stays readable as the topic of the last receive or publish.

### An MQTT publish takes its endpoint QoS and retain flag

An `mqtt:` publish no longer reads `redbMqtt.qos` and `redbMqtt.retain`. The consumer writes them with the QoS and
retain flag of the received message, so a republished message kept the QoS it arrived with, and a retained message was
retained again on the new topic. A publish now uses the endpoint's `qos`, `qosExpression` and `retain`. A route that
set `redbMqtt.qos` or `redbMqtt.retain` to choose them sets `redbMqtt.overrideQos` (`MqttHeaders.OverrideQos`: 0, 1 or
2) or `redbMqtt.overrideRetain` (`MqttHeaders.OverrideRetain`: true or false) instead, as a number or bool from C# or
as text from markup (`<setHeader name="redbMqtt.overrideQos" value="1"/>`). Another value fails the publish. A route
that wants a republish to keep the incoming flags copies them over:
`exchange.In.Headers[MqttHeaders.OverrideRetain] = exchange.In.Headers[MqttHeaders.Retain];`. The MQTT 5 properties
(`redbMqtt.contentType`, `redbMqtt.responseTopic`, `redbMqtt.correlationData`, `redbMqtt.messageExpiryInterval`,
`redbMqtt.userProperties`) still pass through to a publish.

### XML structure limits

XML that redb.Route reads from outside goes through `SafeXml`, and `SafeXml` now checks the structure of a document
while it reads it. A document over a limit is refused with `XmlLimitException`, an `XmlException`, before it is built.
The limits every caller gets:

| Limit | Default |
|---|---|
| Nesting depth, the root counting as 1 | 256 |
| Attributes on one element, namespace declarations included | 1024 |
| Namespace declarations on one element | 256 |

Size, element count and namespace declarations in the whole document are not bounded by default. These limits apply
to XPath over a message body, the SOAP receiver and sender, Route-XML markup and templates. A document deeper than
256 levels used to be read and is refused now; search for such documents (generated XML, recursive structures)
before upgrading. Code that reads XML through `SafeXml` itself can pass its own `XmlLimits`:
`SafeXml.Parse(xml, XmlLimits.Default with { MaxDepth = 1024 })`.

**SOAP receiver.** A request is checked once against these limits before anything parses it, and again after the
body is decrypted; a request over a limit is a `Client` / `Sender` fault naming the limit. The request body is now
bounded by `maxRequestBodySize`, 100 MB by default; it was not bounded before. `maxRequestBodySize=0` restores no
bound.

**AS4 receiver and sender.** The envelope has tighter limits, the shape of an eDelivery envelope: depth 32,
32 attributes and 16 namespace declarations on an element, 4096 namespace declarations in the envelope, and
`maxEnvelopeCharacters` as before. A received envelope over them is a SOAP `Sender` fault; a partner's answer over
them is an invalid receipt. A message must carry exactly one `xenc:EncryptedKey`, and each payload is decrypted once;
anything else is `EBMS:0102`. Domibus, Holodeck B2B and this connector send messages well within all of this.

### AS4 refusals in their own counter

An AS4 receiver used to count every message it refused (a malformed envelope, a failed signature or decryption, a
message the agreement does not allow) in `Errors`, so a partner with a broken configuration turned the route red. These
are now counted in `IEndpointStatistics.Refused`, with the reason of the last one in `LastRefusalMessage`, and no longer
in `Errors`, the last error or health. `EBMS:0004` (a failure on our side) and route failures stay errors. An alert
that watches `Errors` of an AS4 receive endpoint for refused messages should watch `Refused` instead; in a route,
`stats('current', 'refused')`.

### `attachments` in an expression

A bare `attachments` in an expression (`${attachments}`, `attachments.size`) now reads the attachments of the In
message, as `${attachments}` does in Camel Simple. It used to fall back to an exchange property of that name, for
example the list an `smtp:` route put in `exchange.Properties["Attachments"]`. An expression that meant the property
writes `property.attachments`.

### Mail and SOAP attachments are message attachments

`smtp:`, `imap:`, `pop3:` and `soap:` now work with the message attachments, `exchange.In.Attachments` (see the
README, "Attachments"). The old conventions are removed with no transition period.

**Nothing warns you.** `smtp:` no longer reads the exchange property `Attachments`. That property is an ordinary
property now, so a route still setting it gets no error, and its mail goes out **without the files**. Search the code
for `Properties["Attachments"]`, `MailAttachment` set on an exchange property, `SoapHeaders.Attachments`,
`redbSoap.attachments`, `SoapHeaders.HasAttachments`, `redbSoap.hasAttachments` and `SoapAttachment`, and migrate
each one as shown below.

**Mail: files from code, before `smtp:`.**

```csharp
// before
exchange.Properties["Attachments"] = new List<MailAttachment>
{
    new("orders.zip", zip, "application/zip")
};

// after
exchange.In.Attachments.Add(new Attachment
{
    Name = "orders.zip", FileName = "orders.zip", ContentType = "application/zip",
    Content = AttachmentContent.FromBytes(zip)
});
```

**Mail: file paths, before `smtp:`.**

```csharp
// before
exchange.Properties["Attachments"] = new List<string> { @"C:\out\report.pdf" };

// after: the file is read when the mail is sent
exchange.In.Attachments.Add(new Attachment
{
    Name = "report.pdf", FileName = "report.pdf", ContentType = "application/pdf",
    Content = AttachmentContent.FromFile(@"C:\out\report.pdf")
});
// or a route step: .AddAttachmentFromFile(@"C:\out\report.pdf", contentType: "application/pdf")
```

The `attachments=` endpoint option (comma separated paths) stays as it was.

**Mail: received mail.** `imap:` and `pop3:` also put the attachments of a mail on the message attachments.
`MailMessageBody`, its `Attachments` and the `redbMail.*` attachment headers stay as they were, so routes reading
them need no change. Two behaviors are new: a received mail sent on to `smtp:` carries its attachments (before, they
were dropped), and its text (`MailMessageBody` converts to its text; before, the type name was sent as the text).

**SOAP: sending attachments (producer, or a consumer reply).**

```csharp
// before
msg.Headers[SoapHeaders.Attachments] =
    new List<SoapAttachment> { new("doc-1", "application/pdf", pdfBytes) };

// after
msg.Attachments.Add(new Attachment
{
    Name = "doc-1", ContentId = "doc-1", ContentType = "application/pdf",
    Content = AttachmentContent.FromBytes(pdfBytes)
});
```

A consumer route that replies with attachments replaces the request ones: `e.In.Attachments.Clear()`, then `Add`. As
before, a reply that still holds exactly the request attachments is sent without them.

**SOAP: reading attachments.**

```csharp
// before
var parts = e.In.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments);
byte[] bytes = parts![0].Content;
bool any = e.In.GetHeader<bool>(SoapHeaders.HasAttachments);

// after
var part = e.In.Attachments.Find("doc-1");          // named by the Content-ID
byte[] bytes = part!.Content.ReadAllBytes();
bool any = e.In.Attachments.Count > 0;              // in an expression: attachments.size > 0
```

`SoapAttachment.ContentId`, `ContentType` and `Content` map to `Attachment.ContentId` (also its `Name`),
`ContentType` and `Content.ReadAllBytes()`. A producer reply's attachments are on `exchange.Out.Attachments`, and on
`In` after the pipeline moves on.

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
