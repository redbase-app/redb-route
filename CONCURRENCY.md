# Concurrency & Parallelism in redb.Route

How to process more than one message at a time — safely. Like [Apache Camel](https://camel.apache.org/manual/threading-model.html),
redb.Route separates concurrency into **two independent axes**, and you pick the one that fits:

| Axis | Question it answers | Primitive |
|------|---------------------|-----------|
| **Consumer-level** | How many messages does the *source* pull/receive at once? | Transport option (`ConcurrentConsumers`, `MaxConcurrentCalls`, partitions, …) |
| **Processing-level** | How many messages does the *pipeline* process at once, regardless of source? | The **`.Threads(N)`** EIP |

The rule of thumb:

- **Broker/queue source** (RabbitMQ, AMQP, IBM MQ, Azure Service Bus, SEDA, Kafka, MQTT) → use its **consumer-level** knob. The transport competes for messages natively and preserves its own delivery/ack semantics.
- **Polling source** (File, FTP/SFTP, SQL, S3, Elasticsearch, IMAP/POP3, LDAP, Timer, …) → these poll **serially by design** (one scan at a time is correct for a resource poll). To fan the *processing* out, add **`.Threads(N)`**.
- **Any route, any source** → `.Threads(N)` always works as a general processing-concurrency stage.

---

## Processing concurrency — the `.Threads(N)` EIP

`.Threads(N)` caps a route section's processing concurrency at **N**, so a strictly serial source keeps
dispatching while up to N exchanges are processed in parallel. It is the general-purpose alternative to
the `.To("seda://x?concurrentConsumers=N")` workaround — no named endpoint required.

**Adaptive by exchange pattern** — one API, transparent the way Camel's `threads()` is:

- **InOnly** — fire-and-forget hand-off: the exchange is *cloned* (own DI scope) and handed to the
  worker pool; the caller returns as soon as it's accepted, so a serial poll loop keeps pumping.
- **InOut** — request/reply: the body runs **inline on the same exchange** under a `SemaphoreSlim` gate
  (≤ N concurrent). Nothing is cloned or copied, so the reply is preserved **exactly** — whether the
  route writes it to `Out` **or to `In`** (redb.Route's HTTP consumer reads `In` when `Out` is unset).
  Concurrency across concurrent requests stays capped at N. **RPC works, losslessly.**

```csharp
From("file://in?move=.done")
    .Threads(10)                    // up to 10 files processed in parallel
        .Process(HeavyTransform)
        .To("http://scoring-api/score")
    .End();
```

Options:

```csharp
.Threads(10)                        // minimum: pool of 10, bounded hand-off queue == pool size
.Threads(10).MaxQueueSize(1000)     // larger buffer: let the source run further ahead before backpressure
.Threads(10).EnqueueTimeout(TimeSpan.FromSeconds(5))  // cap the wait for a free slot (default: wait forever)
```

### Semantics — what to know

| Property | Behaviour |
|----------|-----------|
| **Request/reply** | Adaptive by pattern: **InOnly** hands a clone to the pool (fire-and-forget); **InOut** runs the body **inline on the same exchange** under a gate, so the reply — on `Out` *or* `In` — is preserved losslessly. |
| **Backpressure** | Bounded. InOnly waits on the hand-off queue; InOut waits on the gate. When saturated the caller waits for a free slot — it never buffers unboundedly. Bound that wait with `.EnqueueTimeout(...)`, which fails fast with a `TimeoutException` (for InOut, surfaced to the awaiting caller) instead of waiting indefinitely. |
| **Per-exchange isolation** | **InOnly** clones the exchange and gives the worker its **own DI scope** (own scoped services / DB connection) — the worker never shares the caller's mutable exchange. **InOut** runs on the caller's own exchange/scope (inline). |
| **Transaction boundary** | **InOnly is a transaction boundary** — the worker runs detached (`ExecutionContext.SuppressFlow`), like `.To("seda://")`, so it does **not** extend the caller's transaction; open the tx **inside** the pooled body. **InOut is NOT a boundary** — the body runs inline, so the ambient `TransactionScope` flows into it (correct for request/reply — the caller awaits the result). |
| **Ordering** | **Not preserved** when `N > 1` (same as any competing-consumer model). Use `N = 1` — or don't use `.Threads()` — when strict ordering matters. |
| **Errors** | **InOnly** routes the fault to the route's error handling (`OnException` / dead-letter) on the pool thread — not swallowed. **InOut** lets the fault propagate up the pipeline to the same outer `OnException` wrapper as an un-threaded route; if unhandled it surfaces to the awaiting caller. |
| **Graceful drain** | On route/context stop the pool stops accepting, finishes every in-flight and queued exchange, and only force-cancels if a drain timeout is exceeded. In-flight work is not dropped. |

### When to reach for it

- A **polling** consumer is your bottleneck (File/SQL/S3/IMAP poll one batch at a time, then process serially).
- The per-message work is **I/O- or CPU-heavy** and independent, and **ordering doesn't matter**.
- You want processing concurrency **without** standing up a separate `seda://` route.

---

## Consumer concurrency — per transport

When the **source** is a broker or queue, prefer its native knob: it competes for messages at the protocol
level and keeps the transport's own delivery/ack guarantees.

| Transport | Option | What it does |
|-----------|--------|--------------|
| **RabbitMQ** | `.ConcurrentConsumers(N)` | One channel, one consumer, native `consumerDispatchConcurrency: N` — the RabbitMQ.Client dispatcher invokes the handler for up to N deliveries concurrently. |
| **AMQP / IBM MQ** | `.ConcurrentConsumers(N)` | N real competing consumers, each on its own session/QM — up to N messages processed concurrently. |
| **SEDA / VM** | `?concurrentConsumers=N` | N workers draining one in-memory queue. |
| **Azure Service Bus** | `.MaxConcurrentCalls(N)` | Passed through to `ServiceBusProcessor`. |
| **Kafka** | *partitions* | Scale by partition count + consumer-group members; a single consumer polls one partition serially by design. |
| **MQTT** | `.ConcurrentConsumers(N)` | N workers with **manual ack after processing** — see below. |
| **Quartz (Cron/Timer)** | `.Threads(N)` (on the endpoint) | Allows up to N overlapping job fires. |
| **Polling** (File, FTP/SFTP, SQL, S3, Elasticsearch, IMAP/POP3, LDAP, Timer, Exec, LLM) | — | Serial poll **by design**; add the `.Threads(N)` EIP for processing concurrency. |
| **Event-driven** (HTTP, gRPC, SignalR, TCP, WebSocket, Firestore) | host-managed | The host dispatches requests/connections concurrently already. |

### MQTT `ConcurrentConsumers` — why it's native

```csharp
From(Mqtt.Subscribe("telemetry/#").Broker("main").Qos(1).ConcurrentConsumers(5))
    .Process(IngestReading)
    .End();
```

A generic `.Threads(N)` on an MQTT route would acknowledge each message on **hand-off** — i.e. *before* it is
processed — because the receive handler returns as soon as the exchange is queued. That weakens QoS 1/2 to
at-most-once (a crash mid-processing loses the message).

The native `.ConcurrentConsumers(N)` dispatches to N workers but sets `AutoAcknowledge = false` and
**acknowledges only after** the worker finishes processing. Concurrency **and** at-least-once are preserved;
a failed message is not acked, so the broker redelivers on reconnect. Ordering is not preserved when `N > 1`.

> **Quartz note.** A stateful Quartz job (`[DisallowConcurrentExecution]`) will not overlap regardless of
> `Threads(N > 1)` — that's correct by design (stateful means "never run me concurrently"). The `Threads`
> setting is simply ignored in that case.

---

## Choosing between the two

```
Is the source a broker / queue (Rabbit, AMQP, IBM MQ, ASB, SEDA, Kafka, MQTT)?
   └─ yes → use the transport's consumer knob (ConcurrentConsumers / MaxConcurrentCalls / partitions).
            It keeps native delivery + ack semantics.
   └─ no  → the source polls serially (File, SQL, S3, IMAP, LDAP, Timer, …)
            └─ need parallel processing?  →  add  .Threads(N)  to the route.

Does strict ordering matter?      → keep N = 1 (or avoid concurrency on that route).
Does the work share a transaction with the source? → don't split it across .Threads(); open the tx inside the body.
```

Both axes compose: a broker consumer with `ConcurrentConsumers(4)` whose pipeline also contains a
`.Threads(8)` stage will have up to 4 concurrent receives, each of which can fan its own processing out to 8 —
though in practice you rarely need both on the same route.

---

## Mapping to Apache Camel

| Camel | redb.Route |
|-------|-----------|
| `threads(poolSize)` EIP (InOut-aware) | `.Threads(N)` / `.MaxQueueSize(q)` / `.EnqueueTimeout(t)` — adaptive: InOnly hands off, InOut preserves the reply |
| `seda?concurrentConsumers=N` | `.To("seda://x?concurrentConsumers=N")` |
| broker `concurrentConsumers` | `.ConcurrentConsumers(N)` (RabbitMQ / AMQP / IBM MQ / MQTT), `.MaxConcurrentCalls(N)` (ASB) |
| `split/multicast/recipientList().parallelProcessing()` | `.Split(...).Parallel()` / `.Multicast().Parallel()` with `.MaxParallelism(N)` |
| polling consumer (single-threaded scan) | polling connectors — serial by design; add `.Threads(N)` |

For fan-out **within** a single exchange (split a body into parts and process the parts in parallel), see the
`Split` / `Multicast` / `RecipientList` EIPs with `.Parallel().MaxParallelism(N)` — that is a *synchronous*
fan-out (the caller awaits all branches and can aggregate), distinct from `.Threads()`, which caps the
concurrency of *separate* exchanges (InOnly hands each clone to a worker pool; InOut runs inline under a gate).
