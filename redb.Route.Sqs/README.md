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
```

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
- **`transacted=true`** defers the delete into the route transaction (`.Transacted()`), committing /
  rolling back together with redb DB work.
- **Ordering** is preserved only per FIFO message group and only with `concurrentConsumers=1`.

## Key options

| Option | Applies to | Notes |
|---|---|---|
| `region`, `serviceUrl`, `accessKey`/`secretKey`, `sessionToken`, `profileName`, `useDefaultCredentialsProvider` | both | `serviceUrl` targets LocalStack/ElasticMQ |
| `waitTimeSeconds` (0–20), `maxNumberOfMessages` (1–10), `visibilityTimeout` | SQS consumer | long-poll + batch + hide time |
| `concurrentConsumers`, `extendMessageVisibility`, `deleteAfterRead`, `resetVisibilityOnFailure`, `transacted` | SQS consumer | concurrency + ack |
| `delaySeconds`, `messageGroupId`, `messageDeduplicationId`, `enableBatch` | SQS producer | FIFO + batch send |
| `autoCreateQueue` / `autoCreateTopic`, `topicArn`, `subject`, `messageStructure` | both | topology + SNS payload |
| `subscribeSnsToSqs` + `subscribeQueueArn` | SNS | subscribe a queue to the topic on start |

## Part of

Part of the [redb.Route](https://github.com/redbase-app/redb) enterprise integration framework.
