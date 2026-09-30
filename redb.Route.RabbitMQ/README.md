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
| **Connection** | `.Host()`, `.Port()`, `.Username()`, `.Password()`, `.VirtualHost()`, `.ConnectionFactory()`, `.ClientName()`, `.PublisherConnection()` |
| **TLS and login** | `.Ssl(serverName, certPath, certPassphrase)`, `.SslCaCertPath()`, `.SslProtocols()`, `.RevocationMode(mode, softFail)`, `.AuthMechanism()` |
| **Recovery** | `.AutomaticRecovery()`, `.TopologyRecoveryEnabled()`, `.RecoveryInterval()`, `.Heartbeat()`, `.ConnectionTimeout()` |
| **Exchange** | `.Exchange(name, type?)`, `.ExchangeDurable()`, `.ExchangeAutoDelete()`, `.Declare()` |
| **Queue** | `.Durable()`, `.AutoDelete()`, `.Exclusive()`, `.RoutingKey()`, `.MaxLength()`, `.MaxLengthBytes()`, `.Overflow()`, `.QueueType()`, `.MaxPriority()` |
| **Consumer** | `.ConcurrentConsumers()`, `.PrefetchCount()`, `.AckMode()`, `.Transacted()`, `.Mandatory()`, `.ReplyTo()`, `.Timeout()` |
| **Message** | `.ContentType()`, `.MessageTtl()`, `.Expires()` |
| **DLX** | `.DeadLetterExchange()`, `.DeadLetterRoutingKey()` |

> Most builder methods accept both constant values and `IExpression` for runtime resolution via the expression engine.

## Consumer concurrency & acknowledgement

- **`.ConcurrentConsumers(N)`** is the single knob for consumer-side parallelism: it sets both the
  channel's AMQP consumer-dispatch concurrency and the app-level concurrency semaphore, so up to
  **N** messages from the queue are processed concurrently. Default `1` (strictly serial — message
  order preserved). With `N > 1`, ordering is not preserved and your processor must be
  thread-safe. Keep `.PrefetchCount()` ≥ `N` so the broker keeps the parallel slots fed.
- **`.AckMode(AckMode.Auto)`** (`ackMode=auto`) switches the consumer to broker-side auto-acknowledge
  (**at-most-once**): the broker settles each delivery on hand-off, so a failed turn does **not** requeue.
  Default `ackMode=manual` (**at-least-once**: ack after a successful turn, nack-requeue on failure).
  `ackMode=auto` cannot be combined with `.Transacted()`. The same option on every broker consumer.

## Transactions

- **Consumer.** `.Transacted()` takes deliveries on a transacted channel: every ack, and every
  nack that requeues a failed delivery, is followed by `tx.commit`, so the broker has applied it
  before the next delivery is settled. The acknowledgement belongs to the consumer, not to the
  route's `.Transacted()` block: it comes last, after the database and the deferred sends have
  committed. This works with `concurrentConsumers` above 1 as well.
- **Producer.** Inside a route's `.Transacted()` block a publish **joins the transaction**: it goes
  out once the database has committed and is dropped if the block rolls back. `.Transacted(false)`
  publishes at once, outside the transaction; `.Transacted()` requires an enclosing block and fails
  the step outside one. A request-reply producer (`.ReplyTo()`) always publishes at once and refuses
  `.Transacted()`: a request held back until the commit would wait for a reply that cannot come.
- **Several publishes in one block.** The deferred publishes of one producer in a block are committed
  in one channel transaction, on a channel of the producer's own (a channel with publisher confirms
  cannot run transactions): they arrive together, in order, or not at all. The producer's batches
  take turns on that channel, and a channel transaction is markedly slower than publisher confirms.

See the framework-wide **Transactions** guide (`TRANSACTIONS.md` in the
[redb.Route repository](https://github.com/redbase-app/redb)).

## Part of

[redb.Route](../README.md) — ESB & EIP Framework for .NET

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

The factory is the whole connection, as in Apache Camel: host, port, credentials, virtual host,
TLS, timeouts, recovery and client name all come from it. A URI that names a factory and also
gives one of those parameters is refused when the endpoint is created, naming the parameters —
it would otherwise go to another broker than the one written. Endpoint parameters (exchange,
routing key, queue arguments, prefetch and the like) belong on the URI as usual.

Endpoints on the same factory share one connection; two factories are two connections, even
with the same settings. Endpoints without a factory share a connection when all their
connection parameters are the same, and get a connection of their own when any differs.

## Publishing and consuming connections

By default producers and consumers with the same settings share one connection, as in Spring
AMQP. The broker's flow control and memory or disk alarms block a *publishing* connection as a
whole, and a consumer on it can then no longer acknowledge — RabbitMQ recommends separate
connections. Two ways to get them:

- two named factories, one for the producers and one for the consumers;
- `publisherConnection=true` on an endpoint: its producer's sends, and its consumer's RPC replies,
  go over a second connection with the same settings, shown as `"<clientName> (publisher)"` in the
  management UI. A consumer's replies can be moved off its connection only this way.

At most two connections per factory (or per set of URI settings) either way, never one per
endpoint.

## When the broker closes a channel

A channel-level error — a publish to an exchange that does not exist (404), a failed
precondition — closes the channel and keeps the connection, so connection recovery does not
reopen it.

- **Producer:** the next send opens a new channel (and a new reply queue for `replyTo=true`); only
  the send that hit the error fails.
- **Consumer:** it logs an Error saying it stopped receiving; unacknowledged deliveries go back to
  the queue. Restart the route. The same Error is logged when the broker cancels the subscription
  because the queue was deleted.
- A lost *connection* is restored with its channels and subscriptions by automatic recovery (on by
  default). With `automaticRecovery=false` nothing restores it: restart the route.

```csharp
context.AddToRegistry("prod", new RabbitMQConnectionFactory
{
    Host = "rabbit.internal",
    Username = "svc",
    Password = secrets.RabbitPassword,
});
// rabbitmq://orders?connectionFactory=prod
```

## Unroutable messages

A publish with `mandatory` set that no queue takes comes back from the broker, and the send fails
with `RabbitMQUnroutableException` (exchange, routing key, reply code) instead of the message
vanishing. `mandatory` is on by default for `direct` and `headers` exchanges and for the default
exchange, off for `topic` and `fanout`, where a message nobody subscribed to is normal; set it with
`mandatory=true|false`. The same exception comes from every path:

- an immediate send, when the broker returns it;
- a request-reply call (`replyTo=true`), at once instead of after the timeout;
- the sends of a `.Transacted()` block, when the block commits. The channel transaction has then
  committed the messages that did route, so a retry of the block sends those again.

For production queues consider an alternate exchange on the target exchange as well: the broker
then keeps what does not route instead of returning it.

```csharp
OnException<RabbitMQUnroutableException>()
    .Handled()
    .Log("No queue for ${header.region}: ${exception.message}")
    .To("rabbitmq://parking-lot")
.EndOnException();
```

## TLS and login

`ssl=true` (or `Ssl = true` on a factory) encrypts the connection; the broker's TLS listener is
usually port **5671**, so set the port too — the default stays 5672. Every TLS setting below
needs `ssl=true`: given without it, it would be ignored, so the endpoint is refused instead.

| Setting (URI / factory) | What it does |
|---|---|
| `sslServerName` | The name the broker certificate must carry. Unset, each host of a cluster list is checked against its own name; set it when the certificate names a load balancer or an alias. The name is always checked. |
| `sslCaCertPath` / `SslCaCertPath`, `SslCaCertificates` | Trust these roots (PEM file, or the certificates themselves on a factory) for the broker certificate instead of the system store — a private or corporate CA without installing it in the OS. |
| `sslCertPath` + `sslCertPassword` / `ClientCertificate` | Client certificate for mutual TLS: a PFX file, or on a factory the `X509Certificate2` itself (from the Windows store, a key vault). It must have its private key and be inside its validity period; both are checked before the handshake, naming the certificate. |
| `sslProtocols` | `Tls12`, `Tls13` or `Tls12,Tls13`. Unset, the OS chooses. SSL 3.0, TLS 1.0 and 1.1 are refused. |
| `revocationMode` + `revocationSoftFail` | Revocation check of the broker certificate: `NoCheck` (default), `Online` (CRL/OCSP), `Offline` (OS cache). An undeterminable status is refused unless `revocationSoftFail=true`; a certificate that names no CRL or OCSP source passes, as in the AS4 connector. |
| `authMechanism=External` | Log in with the client certificate (SASL EXTERNAL) — no password is sent; the broker takes the user from the certificate. Needs the broker's `rabbitmq_auth_mechanism_ssl` plugin. |

```csharp
context.AddToRegistry("prod", new RabbitMQConnectionFactory
{
    Host = "rmq1.internal, rmq2.internal",
    Port = 5671,
    Ssl = true,
    SslCaCertPath = "/etc/pki/corp-root.pem",
    ClientCertificate = certificateFromStore,        // mutual TLS
    AuthMechanism = RabbitMQAuthMechanism.External,  // the certificate is the login
    SslProtocols = SslProtocols.Tls13,
});
```

**OAuth 2.0.** A factory with `OAuth2TokenEndpoint`, `OAuth2ClientId` and `OAuth2ClientSecret`
(and optionally `OAuth2Scope`) logs in with an access token from the client credentials grant
instead of a password, and renews the token on the open connection before it expires, so the
broker never closes the connection for an expired token. The token endpoint must be https
(plain http only on the loopback address): the client secret is sent to it. Needs the broker's
`rabbitmq_auth_backend_oauth2` plugin. Any other source of credentials plugs in as
`CredentialsProvider` (an `ICredentialsProvider`). OAuth2, `CredentialsProvider` and
`AuthMechanism=External` are three ways to log in; a factory that sets two of them is refused.

```csharp
context.AddToRegistry("prod", new RabbitMQConnectionFactory
{
    Host = "rmq.internal", Port = 5671, Ssl = true,
    OAuth2TokenEndpoint = "https://login.example.com/oauth2/token",
    OAuth2ClientId = "billing-service",
    OAuth2ClientSecret = secrets.BillingClientSecret,
    OAuth2Scope = "rabbitmq.read:*/* rabbitmq.write:*/*",
});
```

Endpoints that share a pooled connection share its TLS identity and login, so the inline pool
key includes every TLS and login setting.

The integration tests run against a stand with a private CA, mutual TLS, EXTERNAL and OAuth 2.0
(`C:\Work\yaml\rabbit-tls`, see `RabbitMQTlsBrokerTests`).

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
