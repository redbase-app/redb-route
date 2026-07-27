# SqsRpcDemo

A minimal, single-file example of **RPC over Amazon SQS** with **concurrent
(multi-threaded) workers**, using the `redb.Route.Sqs`
connector against **LocalStack**.

It demonstrates, end to end, the three things people ask about the SQS connector:

| # | Concept | In this demo |
|---|---------|--------------|
| 1 | **Producers** | The client sends request messages; the worker sends reply messages. |
| 2 | **Consumers** | The worker route consumes the request queue; the client consumes the reply queue. |
| 3 | **Multithreading** | The worker runs `.ConcurrentConsumers(4)` — the SQS-native competing-consumer model — so up to 4 requests are processed in parallel. The peak concurrency is measured and printed. |

## The RPC pattern

SQS has no built-in request/reply, so the demo uses the classic **correlation**
pattern (the same one Apache Camel uses for JMS/SQS RPC):

1. The client tags each request with a unique `correlationId` and a `replyTo`
   queue name — both travel as SQS **message attributes**.
2. The worker computes a result and replies to the `replyTo` queue (via `.ToD(...)`,
   a per-message dynamic destination), echoing the `correlationId` back.
3. The client's reply consumer matches the `correlationId` to the pending call and
   completes it.

Message attributes round-trip as exchange headers: a header set on the request
surfaces on the consumer as `redbSqs.attr.<name>`, and the SQS producer forwards
it back out as an attribute — so `correlationId` returns on the reply
**automatically**.

See [`../../CONCURRENCY.md`](../../CONCURRENCY.md) for how `.ConcurrentConsumers(N)`
(broker/queue source) compares to the `.Threads(N)` processing EIP (polling sources).

## Infrastructure

Uses **LocalStack** at `http://localhost:4566` with anonymous `test`/`test`
credentials in region `us-east-1` — exactly what the
[`redb.Route.Tests.Sqs`](../../tests/redb.Route.Tests.Sqs) integration tests use.
It is already in `docker ps` as `redb-localstack`; start it if needed:

```bash
docker start redb-localstack
# or:
docker compose -f C:\Work\yaml\Amazon\docker-compose.yml up -d
```

## Run

```bash
dotnet run --project redb.Route/demos/SqsRpcDemo --framework net9.0
```

Expected output: 12 numbers squared via RPC, each with its round-trip time, and a
line reporting the peak worker concurrency (up to 4). Because the four workers
overlap the (simulated) 300 ms of work per request, the whole batch finishes in
~1.2 s instead of the ~3.6 s a serial consumer would take.

```
   2² = 4     (round-trip  407 ms)
   8² = 64    (round-trip  441 ms)
   ...
All 12 RPC calls completed in 1180 ms.
Peak worker concurrency observed: 4 (pool size = 4).
```

## Going to real SQS

Drop `.ServiceUrl(...)` / `.Credentials(...)` and let the default AWS credential
provider chain plus `.Region("eu-west-1")` point at real Amazon SQS. Everything
else stays the same.
