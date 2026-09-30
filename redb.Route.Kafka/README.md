# redb.Route.Kafka

Apache Kafka transport for redb.Route. Consumer (subscribe), producer (publish), consumer groups, deferred-commit routing (idempotent producer), Kafka transactions with exactly-once consume-process-produce (`transactionalIdPrefix`), and full Confluent.Kafka configuration.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Kafka?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Kafka)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Kafka
```

## Usage

### URI Format

```
kafka://topic-name?brokers=host:port&groupId=my-group&autoOffsetReset=earliest
```

### Fluent DSL

```csharp
using redb.Route.Kafka;

// Consumer
From(Kafka.Topic("orders")
        .Brokers("broker1:9092,broker2:9092")
        .GroupId("order-service")
        .AutoOffsetReset(AutoOffsetReset.Earliest)
        .MaxPollRecords(500))
    .Log("Received: ${body}")
    .To("direct://process");

// Producer
From("direct://outbound")
    .To(Kafka.Topic("events")
        .Brokers("broker1:9092")
        .Acks(Acks.All)
        .Key("order-key")
        .Compression(CompressionType.Lz4));

// Inside a transacted block the send joins the transaction: it goes out once the database has
// committed. The builder's .Transacted() also makes the producer idempotent and requires the block.
From("direct://critical")
    .Transacted()
        .To(Kafka.Topic("audit")
            .Brokers("localhost:9092")
            .Transacted())
        .To(Kafka.Topic("alerts")
            .Brokers("localhost:9092")
            .Transacted(false))          // goes out at once, even if the block rolls back
    .End();
```

### Transactions: `.Transacted()` and Kafka transactions

Inside a route's `.Transacted()` block a Kafka send **joins the transaction**: the message is
published after the database commits and discarded when the block rolls back. The `transacted`
parameter states the exceptions: `false` sends at once, outside the transaction; `true` also makes
the producer idempotent and fails the step outside a block. On its own this is **at-least-once**:
the deferred sends go out one after the other, and the consumer commits its offset after the route.

**Kafka transactions** are a separate switch, `transactionalIdPrefix`:

```csharp
From(Kafka.Topic("orders").Brokers("b1:9092,b2:9092").GroupId("billing"))
    .Transacted()
        .IdempotentConsumer(Header("orderId"), "billing-inbound")   // the database work
            .Process(/* ... */)
        .EndIdempotentConsumer()
        .To(Kafka.Topic("invoices").Brokers("b1:9092,b2:9092")
            .TransactionalIdPrefix("billing"))
    .End();
```

- The sends such a producer defers in a block commit as **one Kafka transaction** when the block
  commits (after the database): all of them or none. `read_committed` readers, librdkafka's default,
  never see an aborted one.
- When the route started from a Kafka consumer of the **same cluster** (the same brokers), the
  consumed offset commits **in that transaction** (`SendOffsetsToTransaction`) and the consumer does
  not commit it itself: the output and the offset move together — exactly-once within Kafka. With
  `ackMode=auto` the offset is committed on receipt, before the route runs (at-most-once), so it does not
  ride in the transaction.
- "The same cluster" is decided by the cluster id the brokers report, however the brokers are listed
  on the two endpoints. From another cluster, the consumer commits the offset itself and the route is
  at-least-once; the producer logs a warning naming both cluster ids.
- A consumer that loses its partition while its route runs (a rebalance, or `maxPollIntervalMs`
  exceeded) cannot commit through the transaction: the broker refuses its old group generation, the
  transaction aborts, and the consumer that took the partition over reads the record again.
- A commit that does not answer has an unknown outcome: the producer does not abort it, it is rebuilt
  before the next transaction, and the exchange fails. If that commit did go through, the record is
  written again only when the consumer reads it again (`breakOnFirstError`).
- Keep the block synchronous. An asynchronous step inside it (`.Threads()`, `seda:`) returns the route
  to the consumer before the transaction commits: the offset no longer rides in the transaction, the
  consumer commits it when the route returns, ahead of the work, and the producer logs a warning.
- A send outside a block (`transacted=false`, or no block) is a transaction of its own, a little
  slower than a plain send.
- The prefix names the producer; the connector appends the machine name, the process id and the
  producer's number, so nodes deploying the same configuration never fence each other.
- Two transactional producers in one block commit two transactions, one after the other: atomic per
  producer, as for IBM MQ and RabbitMQ. The database and Kafka still do not commit as one: the
  database commits first, and a failure between the two is covered by the redelivery and the
  idempotent consumer.

A raw `transactional.id` in `additionalProperties` is refused: it would put the client into
transactional mode with nobody opening transactions. So is `enable.auto.commit=true` (a timer would
commit records read but not yet processed, whatever `ackMode` says), and, for a `transacted=true` or
`transactionalIdPrefix` producer, an `enable.idempotence` or `acks` that contradicts it. The rule
holds for the endpoint's `additionalProperties` and for those of a named connection factory alike. The decisions and their reasons are in
docs/KAFKA_EOS_2026_09_19.md.

A parameter the endpoint cannot read — a misspelt option, or a value of the wrong type — is refused
by name when the endpoint is created: a misspelt `transactionalIdPrefix` would otherwise leave the
producer without transactions.

### Delivery defaults

A producer defaults to `acks=all` and an **idempotent producer**, as the Kafka 3 client and Camel 4
do: a send is confirmed once every in-sync replica has it, and a retried send is not written twice.
`enableIdempotence` left unset follows the effective `acks` (on with `all`, off otherwise);
`enableIdempotence=true` with another `acks`, or `transacted=true` or `transactionalIdPrefix` with an
explicit `acks` other than `all`, is refused when the endpoint is created. A route that wants the lower latency of
`acks=leader` sets it explicitly and gets a non-idempotent producer.

### Failed messages

An exception that escapes the route on a consumed message is counted in endpoint statistics and,
by default, the consumer **moves on** — the failed record's offset is covered by the next
successful commit (Camel's default too). `.BreakOnFirstError()` flips that: the consumer seeks
back to the failed record and retries it instead of advancing, so a permanently poisoned record
blocks its partition — pair it with a route error handler that dead-letters.

## Fluent Builder API

| Category | Methods |
|----------|---------|
| **Connection** | `.Brokers()`, `.SecurityProtocol()`, `.Sasl(mechanism, user, pass)`, `.SslCa()`, `.SslCert()`, `.SslVerifyHostname()`, `.ConnectionFactory()` |
| **Consumer** | `.GroupId()`, `.AutoOffsetReset()`, `.MaxPollRecords()`, `.PollTimeout()`, `.SeekTo()`, `.TopicIsPattern()`, `.BreakOnFirstError()`, `.SessionTimeout()`, `.HeartbeatInterval()`, `.MaxPollInterval()`, `.PartitionAssignmentStrategy()`, `.IsolationLevel()` |
| **Producer** | `.Acks()`, `.EnableIdempotence()`, `.Key()`, `.KeyFromHeader()`, `.Partition()`, `.Transacted()`, `.TransactionalIdPrefix()`, `.Linger()`, `.BatchSize()`, `.Compression()`, `.MessageTimeout()`, `.Retries()`, `.RecordMetadata()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Encryption and authentication

The connection is plaintext unless `securityProtocol` says otherwise: `Ssl` (TLS), `SaslSsl` (TLS plus
SASL), `SaslPlaintext` (SASL without TLS). The same options work on the URI and on a named connection
factory, the URI winning by name; the consumer's metadata client uses them too.

| Option | Meaning |
|---|---|
| `sslCaLocation` | PEM file of the CA that signed the brokers' certificates; unset, the system store |
| `sslCertificateLocation`, `sslKeyLocation`, `sslKeyPassword` | client certificate and key, for brokers that require one (mutual TLS) |
| `sslEndpointIdentificationAlgorithm` | `https` verifies the broker's hostname against its certificate, `none` does not; unset, librdkafka verifies it |
| `saslMechanism` | `Plain`, `ScramSha256`, `ScramSha512` (need `saslUsername`/`saslPassword`), `Gssapi` (Kerberos), `OAuthBearer` |

```csharp
.To(Kafka.Topic("orders").Brokers("b1:9093,b2:9093")
    .SecurityProtocol("SaslSsl")
    .Sasl("ScramSha512", userExpression, passwordExpression)
    .SslCa("/etc/kafka/ca.pem"))
```

- The hostname is checked against the name the client connects by: the bootstrap brokers first, then
  the names the brokers advertise. A certificate must carry both.
- `Plain` over `SaslPlaintext` sends the password unencrypted; the connector logs a warning. SCRAM
  does not send the password, but the traffic is still unencrypted.
- `OAuthBearer` has no token callback in the connector: use librdkafka's own OIDC client through
  `additionalProperties` (`sasl.oauthbearer.method=oidc`, `sasl.oauthbearer.client.id`, ...).
- Certificates held in memory (`ssl.ca.pem`, `ssl.certificate.pem`, `ssl.key.pem`) or a PKCS#12
  keystore (`ssl.keystore.location`) go through `additionalProperties` too, set from code: they cannot
  come from a URI, and the connector does not log them.
- `saslPassword` and `sslKeyPassword` are secrets: redacted wherever the endpoint URI is shown.

## Headers

The headers of a consumed record become message headers, and message headers become the headers of
a sent record. `content-type` also sets and is set from `Message.ContentType`.

`redbKafka.*` headers are exchange metadata and never go on the wire: the consumer sets
`redbKafka.Topic`, `.Partition`, `.Offset`, `.Timestamp` and `.Key` on every record, in single and
batch mode alike; with `recordMetadata=true` the producer sets `redbKafka.Sent.Topic`, `.Partition`,
`.Offset` and `.Timestamp` (the moment the send was confirmed, not the broker's record timestamp).
The key of a sent record comes from the `key` option; without one, from `redbKafka.Key`, so a route
passing consumed records on keeps their keys (their partition and compaction), as camel-kafka and
Spring Kafka do. `keyFromHeader=false` sends them without a key; `partitionNumber` sends without a
key in any case.

## Tracing

The connector traces on the shared `redb.Route` activity source, through the core's transport contract
(`RouteTelemetryExtensions`); `RouteEngineOptions.EnableTelemetry = false` opens no Kafka span.

- **Send**: a `Producer` span `{topic} publish`. Its W3C context (`traceparent`, `tracestate`) and the
  baggage go into the record's headers as UTF-8, replacing a value copied from a consumed record. A
  send that fails within the call (an immediate one, or a Kafka transaction of its own) marks the span
  an error; a send deferred to a `.Transacted()` block fails at the block's commit, on the route's span.
- **Receive, one record**: a `Consumer` span `{topic} receive` whose parent is the context in the
  record's headers (header names compared without case). Without one it is a root, never a child of
  the poll thread's ambient activity. The sender's baggage comes back on it, and the route runs under it.
- **Receive, a batch** (`maxPollRecords`): the records come from different senders and traces, so none
  of them is the parent. The batch span is a root of its own with a link to the context of every record
  that carries one, and `messaging.batch.message_count`. In a tracing backend the batch opens its own
  trace, and each link leads to the send of one record.

Both spans carry `messaging.system=kafka`, `messaging.destination.name`, `messaging.operation` and
`redb.route.endpoint`; a receive span also carries `messaging.kafka.consumer.group`, and a single
record's span its partition, offset and key.

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new KafkaConnectionFactory
{
    Brokers = "b1:9092,b2:9092",
    SaslUsername = "svc",
    SaslPassword = secrets.KafkaPassword,
});
// kafka://orders?connectionFactory=prod
```
