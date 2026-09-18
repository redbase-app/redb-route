# Transactions: committing the database, the messages and the dedup key together

A route usually does three things at once: it writes to a database, it sends messages onward, and it
remembers that it already handled this delivery. This guide is about making those agree — and about
what happens when they cannot.

## TL;DR

- There is **one** primitive: `.Transacted()` (alias `.Transaction()`).
- Inside it, every piece of `redb` work shares **one connection per (transaction, database)** and commits or rolls
  back with the block.
- A unit of work ends in one order: **database, then the deferred sends, then the acknowledgement**.
- `.Retry(n)` wraps the transaction. One attempt is one unit of work.
- Put `.IdempotentConsumer(...)` **inside** `.Transacted()` so the dedup key commits with the work.
- Keep a transacted route to **one database**, and do not run parallel branches inside a transaction.

## The model

`.Transacted()` opens a `System.Transactions.TransactionScope` around the enclosed steps. Under that scope the core
hands every piece of `redb` work — `SaveAsync`, `redb.Context`, stores that open their own DI scope, the key
generator — the **same** connection for a given database. The work commits together when the block completes, and any
failure before completion rolls all of it back.

```csharp
From("sftp://inbound/orders")
    .Transacted()
        .Unmarshal<Order>()
        .ProcessWithRedb(async (redb, ex, ct) => await redb.SaveAsync((Order)ex.In.Body!, ct))
        .To("kafka://orders.created")
    .End();
```

- **SQL Server and PostgreSQL** enter the transaction when the connection opens.
- **SQLite** issues `BEGIN IMMEDIATE` and holds the write lock for the whole block.
- Two commands at once on the transaction's connection are refused, and so is a command issued after the transaction
  ended. Both say why.

## One route, end to end

```csharp
From("rabbitmq://orders?queue=orders.in&transacted=true")
    .Transacted()                                   // one transaction per attempt
        .Retry(3, TimeSpan.FromSeconds(2))          // wraps the transaction, does not live inside it
        .DeadLetterChannel("rabbitmq://orders?exchange=orders.dlq")
        .IdempotentConsumer(Header("messageId"), "orders-inbound")
            .Unmarshal<Order>()
            .ProcessWithRedb(async (redb, ex, ct) =>
            {
                var order = (Order)ex.In.Body!;
                await redb.SaveAsync(order, ct);
                // Raw SQL that must commit with the redb work goes through redb.Context — the transaction's
                // own connection. A `sql:` step here would be a second connection; see "One database" below.
                await redb.Context.ExecuteAsync(
                    "update orders_flat set state = $1 where id = $2",
                    ["processed", order.Id], ct);
            })
            .To("kafka://orders.created")           // deferred: nothing is published yet
        .EndIdempotentConsumer()
    .End()
    .Log("done: ${messageHistory('compact')}");
```

What happens to one message:

1. **The consumer takes the delivery and leaves it unsettled.** Acknowledging is its job, and it comes last.
2. **The attempt opens its transaction.** The dedup key, the redb object and the raw SQL all run on one connection
   inside it, because they address the same database.
3. **`To("kafka://…")` publishes nothing yet.** It registers a deferred action on the exchange.
4. **The attempt succeeds:** the database transaction completes and closes, then the Kafka message goes out, then the
   consumer acknowledges the delivery. In that order.
5. **The attempt fails:** the database rolls back, the dedup key with it, so the redelivery is processed rather than
   skipped; the deferred Kafka message is discarded and the action set is emptied. `Retry` waits two seconds and opens
   a **new** transaction. Nothing is held open during the wait.
6. **All three attempts failed:** the exchange goes to the dead-letter channel, and the delivery is still not
   acknowledged, so the broker redelivers it by its own rules.
7. **Kafka fails after the database committed:** the database cannot be taken back. The failure is logged as exactly
   that and propagates, the delivery stays unacknowledged, and the redelivery is kept from doing the work twice by the
   dedup key, which is now committed.

### The same route without redb: `sql:` as the database work

A `sql:` step opens its own connection, and under `.Transacted()` that connection **joins the ambient transaction**
instead of starting one of its own. So a route whose database work is plain SQL is transacted the same way — just
keep it to one connection's worth of work and do not mix it with `redb` against the same database (see
[One database per transacted route](#one-database-per-transacted-route)).

```csharp
From("rabbitmq://orders?queue=orders.in&transacted=true")
    .Transacted()
        .Retry(3, TimeSpan.FromSeconds(2))
        .DeadLetterChannel("rabbitmq://orders?exchange=orders.dlq")
        .Unmarshal<Order>()
        .To("sql:insert into orders (id, state) values (:#id, 'new');"
            + " update orders_flat set state = 'processed' where id = :#id"
            + "?dataSource=#main"
            + "&param.id=${body.Id}")
        .To("kafka://orders.created")           // deferred, as always
    .End();
```

The statement runs inside the route's transaction, the Kafka message leaves only after that transaction committed, and
the delivery is acknowledged after the message went out. A failure anywhere rolls the statement back and publishes
nothing.

Keep the database work of a transacted route to **one** `sql:` step: every step opens its own connection, and a second
connection inside one transaction is the case the one-database rule forbids. Several statements go in one step, as
above, or through the connector's batch mode.

## The order things commit in

1. **The database transaction completes and closes.** Everything `redb` and raw SQL did on the block's connection is
   durable.
2. **The deferred sends go out.** Every `.To(broker)` inside the block published nothing until now; the messages leave
   only once the work they announce is stored.
3. **The consumer acknowledges the incoming message.** Last, and only if the two steps above went through.

The reason is the service on the other side: a message tells it something happened, and it then reads the work by id.
Announcing first would let it look up rows that do not exist yet, or never will. Committing first leaves the opposite
window, work stored but not yet announced, which the broker closes by redelivering and an
[idempotent consumer](#idempotent-consumer) keeps from being done twice.

A send that fails after the database committed is logged for what it is and propagates, so the message is never
acknowledged and the broker delivers it again.

## What counts as a failure

The transaction ends the way the **exchange** ends, not the way the last step returned:

| Exchange after the body | Transaction | Broker consumer |
|---|---|---|
| Completed, or `OnException` with `Handled(true)` | commit | ack, offset commit |
| A step threw | rollback | nack, release, no commit |
| `OnException` **without** `Handled(true)`: the exception stays on the exchange and the route returns normally, also from a sub-route called through `direct://` | rollback | nack, release, no commit |

`Handled(true)` means "this exchange is done, the failure is consumed". Without it the failure travels through the
transaction and out of the consumer exactly as a thrown one would.

## `Retry` wraps the transaction

```csharp
.Transacted()
    .Retry(3, TimeSpan.FromSeconds(1))
    .ProcessWithRedb(...)        // its own transaction, its own deferred sends
    .To("kafka://orders")
.End()
```

- A failed attempt rolls back its own database work **and** its own deferred sends, so the attempt that finally
  succeeds publishes only its own messages.
- No transaction is held open across the retry delays, so a retrying route does not hold database locks for the whole
  sequence.

## Who acknowledges the incoming message

The consumer does, never the transaction. RabbitMQ, AMQP 1.0, Kafka, IBM MQ, Redis Streams, Azure Service Bus and SQS
settle a delivery after the whole unit of work succeeded, and leave it unsettled when it did not.

Rolling an acknowledgement back would mean nacking, that is telling the broker to deliver the message again. That is
an action on the broker, not an undo of the work, and with `Retry` outside the transaction it would start a redelivery
while the local retry was still working on the same message. Outgoing sends are the opposite case and stay inside the
transaction: not sending is a real undo.

`.WireTap(...)` is deliberately outside all of this. The tapped branch runs detached, with the ambient transaction
suppressed, so it can publish even when the transaction later rolls back. Use it for notifications and copies, not for
a message the next service will act on.

## Writing your own `ITransactedAction`

`Commit` is called after the database transaction has closed, so `Transaction.Current` is null inside it. An action
that needs to take part in the database transaction is not a deferred action at all: do that work in a route step.

## One database per transacted route

A `TransactionScope` with **two independent durable connections** escalates to a distributed transaction (MSDTC),
which .NET does not support on Linux. Keep a transacted route to a single database. Brokers are **not**
`System.Transactions` resources: their sends are deferred and committed by the route, so they never cause escalation.

This is also why a `sql:` step and `redb` must not share one transaction against the same database: the `sql:`
connector opens its own connection. SQL Server refuses it, PostgreSQL breaks the commit, SQLite writes in autocommit
past the transaction. Raw SQL that must commit with the redb work goes through `redb.Context` inside
`ProcessWithRedb`, which is the transaction's own connection.

## Scope timeout

The scope timeout defaults to **1 minute**. Long work inside a transaction holds locks for that whole time, so keep
transacted blocks short.

## Parallelism

A parallel `.Split()` or `.Multicast()` **inside** a transaction is refused: concurrent commands on one transaction's
connection are not possible, and a failing branch would roll the whole transaction back at once. For parallel work,
keep the parent **without** `.Transacted()` and open a transaction **inside each sub-route**:

```csharp
From("jms://batch")
    .Split(body => ((Batch)body).Items)          // the parent is NOT transacted
        .To("direct://process-one");             // each sub-route is transacted on its own

From("direct://process-one")
    .Transacted()
        .ProcessWithRedb(/* ... */)
    .End();
```

## Idempotent consumer

Deduplicating a message and doing its work must commit together, or a crash between them loses the message. Place
`.IdempotentConsumer(...)` **inside** `.Transacted()`:

```csharp
.Transacted()
    .IdempotentConsumer(Header("messageKey"), "inbound-files")
        // ... work ...
    .EndIdempotentConsumer()
.End()
```

| Placement | Behaviour |
|---|---|
| **Inside `.Transacted()`**, redb-backed repository | The key insert joins the transaction. A rollback removes the key, and a hard crash before the commit leaves nothing at all, no key and no work, so the redelivery is processed. Committed means both. On a processing failure the processor does **not** call `Remove`: the rollback undoes the key. |
| **Outside a transaction**, persistent repository | The key commits on its own. If the process dies between the key commit and the work commit, the key stays and the redelivery is skipped as a duplicate: an at-most-once loss window. |
| **No database at all**, for example an HTTP call as the work | Atomicity is impossible and the loss window is inherent. Rely on the downstream being idempotent. |

Rule of thumb: if the work is a database write, wrap the idempotent consumer in `.Transacted()`. If it is not, treat
delivery as at-most-once across a hard crash.

## Migrating off `BeginRedbTransaction()`

`BeginRedbTransaction()` and the `Transacted(Suppress)` plus `BeginRedbTransaction()` pattern are obsolete. Under
`.Transacted()` redb joins the transaction by itself, and opening a redb transaction under an ambient scope is
rejected by the core.

```csharp
// before
.Transacted(TransactionPolicy.Suppress)
    .BeginRedbTransaction()
        // ... work ...
.End()

// after
.Transacted()
    // ... work ...
.End()
```

## Known limitations

- **`sql:` and `redb` in one transaction against the same database.** Not supported, see "One database per transacted
  route" above. Use `redb.Context` for raw SQL that must commit with redb work. Idempotency for a redb-backed route is
  the redb-backed repository; the SQL one is for routes without redb.
- **Freeing stuck keys.** Idempotent keys left unconfirmed by an older release are not released automatically yet. A
  lease policy, where an unconfirmed key older than N becomes available again, is planned.

## See also

- [METRICS.md](METRICS.md) — reading a route's own measurements, including `messageHistory()` used in the example.
- [CONCURRENCY.md](CONCURRENCY.md) — `.Threads(N)` and consumer-level parallelism.
