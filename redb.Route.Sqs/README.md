# redb.Route.Sqs

Amazon **SQS** + **SNS** transport for [redb.Route](https://github.com/redbase-app/redb), via the native
**AWS SDK for .NET v4** (`AWSSDK.SQS`, `AWSSDK.SimpleNotificationService`). One package, two schemes —
`sqs://` (queue: consume + produce) and `sns://` (topic: publish + SNS→SQS fan-out). LocalStack /
ElasticMQ compatible.

## Installation

```bash
dotnet add package redb.Route.Sqs
```

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteSqs();   // registers sqs:// and sns://
    route.AddRouteBuilder<MyRoutes>();
});
```

## Usage

```csharp
using redb.Route.Sqs.Fluent;

// Consume a queue — long-poll, 4 competing consumers, delete after processing
From(Sqs.Queue("orders").WaitTimeSeconds(20).ConcurrentConsumers(4))
    .Process(HandleOrder);

// Produce to a queue
To(Sqs.Queue("orders").Region("eu-west-1"));

// FIFO queue (name ends in .fifo) — group id required
To(Sqs.Queue("orders.fifo").MessageGroupId("${header.customerId}"));

// Publish to an SNS topic
To(Sns.Topic("events").Region("us-east-1"));

// SNS → SQS fan-out: subscribe a queue, then consume it with sqs://
To(Sns.Topic("events").SubscribeSnsToSqs("arn:aws:sqs:us-east-1:000000000000:events-q"));

// Same, but raw delivery — the queue gets the bare payload (no SNS JSON envelope),
// and SNS attributes arrive as SQS attributes (so trace context survives the hop):
To(Sns.Topic("events")
    .SubscribeSnsToSqs("arn:aws:sqs:us-east-1:000000000000:events-q")
    .RawMessageDelivery());
```

> **Envelope vs raw.** By default SNS wraps the payload in a JSON notification envelope
> (`{"Type":"Notification","Message":"<payload>",...}`), so the subscribing queue receives the
> *envelope*, not your payload — and SNS message attributes are buried inside it. Add
> `.RawMessageDelivery()` (sets `RawMessageDelivery=true` on the auto-created subscription) to get the
> **bare payload** and have SNS attributes map to SQS message attributes. It applies only to the
> `subscribeSnsToSqs` auto-subscription; the default stays `false` (AWS-compatible envelope).

Raw URIs work too: `sqs://orders?waitTimeSeconds=20&concurrentConsumers=4`,
`sns://events?region=us-east-1`.

## Concurrency & delivery

- **`concurrentConsumers=N`** runs N competing receive loops — up to N messages processed in parallel
  (the SQS-native model). See the framework-wide **Concurrency & Parallelism** guide
  (`CONCURRENCY.md`) for how this compares to the `.Threads(N)` processing EIP.
- **At-least-once**: a message is deleted only after it processes successfully. On failure it is left
  for redelivery after the **visibility timeout** (or reset to 0 immediately with
  `resetVisibilityOnFailure=true`).
- **`extendMessageVisibility=true`** keeps a message hidden while a long handler runs (heartbeat).
- **Inside `.Transacted()`** the delete comes last, after the database and the deferred sends have committed.
- **Producers join the transaction.** Inside `.Transacted()` an SQS send or an SNS publish leaves once the database has
  committed and is dropped if the block rolls back. `transacted=false` sends at once, outside the transaction;
  `transacted=true` requires an enclosing block and fails outside one. See the **Transactions** guide
  (`TRANSACTIONS.md` in the [redb.Route repository](https://github.com/redbase-app/redb)).
- **Ordering** is preserved only per FIFO message group and only with `concurrentConsumers=1`.

## Key options

| Option | Applies to | Notes |
|---|---|---|
| `region`, `serviceUrl`, `accessKey`/`secretKey`, `sessionToken`, `profileName`, `useDefaultCredentialsProvider` | both | `serviceUrl` targets LocalStack/ElasticMQ |
| `waitTimeSeconds` (0–20), `maxNumberOfMessages` (1–10), `visibilityTimeout` | SQS consumer | long-poll + batch + hide time |
| `concurrentConsumers`, `extendMessageVisibility`, `deleteAfterRead`, `resetVisibilityOnFailure` | SQS consumer | concurrency + ack |
| `delaySeconds`, `messageGroupId`, `messageDeduplicationId`, `enableBatch` | SQS producer | FIFO + batch send |
| `transacted` | SQS and SNS producer | unset follows an enclosing `.Transacted()` block, `false` sends at once, `true` requires a block |
| `autoCreateQueue` / `autoCreateTopic`, `topicArn`, `subject`, `messageStructure` | both | topology + SNS payload |
| `subscribeSnsToSqs` + `subscribeQueueArn` | SNS | subscribe a queue to the topic on start |
| `rawMessageDelivery` | SNS | on that auto-subscription, deliver the bare payload + map SNS attrs → SQS attrs (default `false` = JSON envelope) |

## Part of

Part of the [redb.Route](https://github.com/redbase-app/redb) enterprise integration framework.

## Named connection factory

Keep credentials out of the route URI: register a factory in the context registry and
reference it by name. A set-but-unknown name fails loud at startup — a typo can never
silently fall back to inline URI parameters.

```csharp
context.AddToRegistry("prod", new AwsConnectionFactory
{
    Region = "eu-west-1",
    AccessKey = "svc",
    SecretKey = secrets.AwsSecret,
});
// sqs://orders?connectionFactory=prod
```

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
