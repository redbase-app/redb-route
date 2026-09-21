# Transactions: committing the database, the messages and the dedup key together

A route usually does three things at once: it writes to a database, it sends messages onward, and it
remembers that it already handled this delivery. This guide is about making those agree — and about
what happens when they cannot.

## TL;DR

- There is **one** primitive: `.Transacted()` (alias `.Transaction()`).
- Inside it, every piece of `redb` work shares **one connection per (transaction, database)** and commits or rolls
  back with the block.
- A unit of work ends in one order: **database, then the deferred sends, then the acknowledgement**.
- A broker send inside the block **joins it by default** and leaves after the commit. `transacted=false` on the
  endpoint sends at once, outside the transaction.
- `.Retry(n)` wraps the transaction. One attempt is one unit of work.
- Put `.IdempotentConsumer(...)` **inside** `.Transacted()` so the dedup key commits with the work.
- Put the outgoing `.To(broker)` **after** `.EndIdempotentConsumer()`: the key guards the work, not the announcement.
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
        .EndIdempotentConsumer()
        .To("kafka://orders.created")               // after the dedup block; deferred until the commit
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
   that and propagates, and the delivery stays unacknowledged. On the redelivery the dedup key, now committed, skips the
   block with the work, while the send stands after the block and goes out. The work is done once and the message is
   not lost.

### Announce after the dedup block

In the example the `.To("kafka://…")` stands **after** `.EndIdempotentConsumer()`, not inside it, and that is the
difference between losing a message and not.

The idempotent consumer skips a duplicate whole: its block does not run, and the route goes on with the step after the
block. The one window the database-first order leaves open is "the work is stored, the message did not go out". The
broker delivers the message again, and the dedup key recognises it.

| Where the send stands | The redelivery after a failed send |
|---|---|
| Inside the dedup block | The block is skipped whole, the send with it. The work is in the database and the message never goes out. |
| After the block, inside `.Transacted()` | The block is skipped and the work is not repeated. The send runs and goes out after the commit. |

The rule: **the dedup key guards the work, not the announcement of it.** The work under the key happens exactly once;
the announcement goes out at least once, so the receiver handles a repeated message like any at-least-once consumer
does.

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
        .To("kafka://orders.created")           // joins the transaction: leaves after the commit
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

### Several sends in one block

The deferred sends leave in the order the route made them. Where the broker has a transaction of its own, the sends of
one producer in a block are committed in it, so they arrive together or not at all:

| Broker | The sends of one producer in a block |
|---|---|
| IBM MQ | one unit of work: every put under syncpoint, one `MQCMIT`; a failed put backs them all out |
| RabbitMQ | one channel transaction on a channel of the producer's own: the publishes, then one `tx.commit` |
| Kafka with `transactionalIdPrefix` | one Kafka transaction: the produces, the consumed offset when the route started from Kafka on the same cluster, then one commit; any failure aborts it |
| AMQP 1.0 with `localTransactions` | one AMQP local transaction: declare, the sends, one discharge; a send that cannot complete discharges it as failed. The broker must support it (Artemis, Qpid, Azure Service Bus) |
| Azure Service Bus with `batchCommit` | one Service Bus batch, taken whole or not at all; a block whose messages do not fit in one batch sends nothing |
| Kafka, AMQP 1.0, Azure Service Bus without these options; SQS, SNS, Redis | one after another, in order; a failure part-way leaves the earlier ones sent |

- Atomic means **per producer**, that is per connection. Sends through two producers, or to two brokers, are committed
  one after the other: making them one would take a distributed transaction (XA), which the route does not use.
- A producer's batches take turns on its connection, because a broker transaction belongs to the connection. On
  RabbitMQ a channel transaction is also markedly slower than publisher confirms; that is the price of the guarantee.
- Where the earlier sends of a failed commit stay sent, the message is not acknowledged and comes back, so they go out
  again: the receiving side deduplicates them, as it does for any at-least-once delivery.
- A Kafka transaction that carries the consumed offset makes a Kafka-to-Kafka route exactly-once within Kafka: the
  output and the offset move together, and the consumer does not commit the offset itself. The database still commits
  before it, not with it. See the Kafka connector's README.

## Which steps join the transaction

The rule is Camel's: inside a transacted route, a send through a transactional endpoint is part of the unit of work.
The endpoint's `transacted` parameter only states an exception to that rule.

| Step inside `.Transacted()` | `transacted` unset | `transacted=false` | `transacted=true` |
|---|---|---|---|
| RabbitMQ, AMQP 1.0, Kafka, IBM MQ, Azure Service Bus, SQS, SNS producer | leaves after the commit | leaves at once | leaves after the commit |
| Redis `PUBLISH`, `XADD` | leaves after the commit | leaves at once | leaves after the commit |
| Other Redis writes (`SET`, `INCR`, lists, hashes, sets) | runs at once | runs at once | runs after the commit |
| Request-reply (`replyTo=true`) | leaves at once | leaves at once | refused when the route starts |
| `sql:`, `redb` | part of the database transaction | | |

- **Outside a block** everything runs at once, and `transacted=true` fails the step: nothing would ever commit the
  send, and failing is better than losing it.
- **Leaving at once** is for a message that must go out even if the work rolls back: an alert, a trace. It goes out
  before the database commits and is not taken back, so the next service must not act on it as on a done piece of work.
  For a copy that should not even share the exchange, use `.WireTap(...)`.
- **Request-reply** always leaves at once: a request held back until the commit would wait for a reply that cannot
  come before it.
- **Redis** is a store first. A `SET`, an `INCR` or a `SETNX` lock exists for its effect and its result right now, so
  only an explicit `transacted=true` holds it back, and then the route does not get its result. `PUBLISH` and `XADD`
  announce work like a broker does and follow the block; the entry id and the recipient count reach the exchange
  headers after the commit. Reads and pops (`GET`, `LPOP` and the like) refuse `transacted=true` when the route starts.
- **HTTP, mail, files, MQTT, WebSocket** and the other non-transactional endpoints run at once wherever they stand. To
  announce through them only after the commit, put the step after `.End()`.

The AMQP 1.0 and Azure Service Bus clients enlist a send in the ambient `System.Transactions` transaction by
themselves. redb.Route never lets them: a send that leaves at once runs with the ambient transaction suppressed, and a
deferred one runs after the transaction closed. A broker therefore never becomes a second resource next to the database,
which would escalate the transaction to a distributed one.

## Blocks inside blocks

Nesting follows the transaction policy, as propagation does in Camel and Spring:

- **`Required`** (the default) or **`Mandatory`** inside a running block **joins** it. The inner block commits nothing
  of its own: its database work joins the outer transaction, its sends wait for the outer commit, and a failure in it
  marks the whole unit of work for rollback.
- **`RequiresNew`** or **`Suppress`** is a unit of work **of its own**. When it ends, its own database work and its own
  sends are settled; the outer block's sends stay with the outer block. A failure inside it rolls back only its own
  work, and the outer block decides what to do with the exception.

`.BeginTransaction()` ... `.CommitTransaction()` / `.RollbackTransaction()` follows the same rules and commits in the
same order, the database first and the sends after it. Inside an enclosing `.Transacted()` a `Required` imperative block
joins it and leaves the commit to it.

An **asynchronous hand-off** ends the block's reach, as in Camel, where a transaction belongs to the thread that
opened it. The exchange that `seda:`, `vm:` or an InOnly `.Threads()` passes on is a unit of work of its own: sends on
the other side are not part of the sending block. The same holds for a copy that outlives its block, such as an
exchange an aggregator or a resequencer releases later: it sees no block, so an unset `transacted` sends at once and
`transacted=true` fails instead of waiting for a commit that already happened.

## What counts as a failure

The transaction ends the way the **exchange** ends, not the way the last step returned:

| Exchange after the body | Transaction | Broker consumer |
|---|---|---|
| Completed, or `OnException` with `Handled(true)` | commit | ack, offset commit |
| A step threw | rollback | nack, release, no commit |
| `OnException` **without** `Handled(true)`: the exception stays on the exchange and the route returns normally, also from a sub-route called through `direct://` | rollback | nack, release, no commit |

`Handled(true)` means "this exchange is done, the failure is consumed". Without it the failure travels through the
transaction and out of the consumer exactly as a thrown one would.

`.RollbackAll()` is Camel's `markRollbackOnly()`: a rollback **without** an exception. The route stops at that step,
the transaction rolls back the database and the deferred sends, and the transaction logs no error. The consumer
handles the message as a failed delivery and does not acknowledge it: the broker delivers it again (Kafka follows its
`breakOnFirstError` setting, a file consumer takes its failure path, `moveFailed`). `Retry` does not repeat the
attempt, because nothing failed. Use it when the route decides the work must not stand, for
example after a validation step that finds the message not yet processable.

A **streamed reply** is read after the transaction: an HTTP, WebSocket or gRPC consumer writes a streamed body (a
`sql:` `StreamList`, say) to the caller once the route, and so `.Transacted()`, has finished and committed. A failure
while the stream is written goes past `OnException` and cannot roll the committed work back; the caller sees a broken
response. It is the same in Camel. When the reply must be all-or-nothing with the work, materialize it inside the
block instead of streaming it.

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

Register the action with `TransactedActions.Register(exchange, key, action, endpoint)`, under a key unique to the
message, or hand a prepared send to `TransactedActions.RegisterSend(exchange, key, send, endpoint)`. The set of actions
belongs to the enclosing block (`.Transacted()`, or `.BeginTransaction()` ... `.CommitTransaction()`) and exists only
while the block runs. Outside a block, or after it has ended, nothing would ever commit the action, so registration
throws instead.

A producer of your own decides with `TransactedActions.Defers(exchange, transacted)`, where `transacted` is its
`bool?` parameter: unset follows the block, `false` sends at once, `true` defers and fails outside a block. The
built-in producers do exactly that.

## One database per transacted route

A `TransactionScope` with **two independent durable connections** escalates to a distributed transaction (MSDTC),
which .NET does not support on Linux. Keep a transacted route to a single database. Brokers never take part as
`System.Transactions` resources: their sends are deferred and committed by the route, or leave at once with the
ambient transaction suppressed, so they never cause escalation.

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
From("rabbitmq://batches?queue=batches.in")
    .Split(ex => ((Batch)ex.In.Body!).Items)     // the parent is NOT transacted
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
| **Inside `.Transacted()`**, redb-backed repository | The key insert joins the transaction. A rollback removes the key, and a hard crash before the commit leaves nothing at all, no key and no work, so the redelivery is processed. Committed means both, and the key stays even if a later step fails (a send after the commit): the redelivery skips the committed work. The processor does **not** call `Remove`: another node may already hold the key again. |
| **Inside `.Transacted()`**, repository outside the transaction (in memory) | The key follows the transaction all the same: the processor removes it when the transaction rolls back and keeps it when it commits. |
| **Outside a transaction**, persistent repository | The key commits on its own and follows the exchange: it is confirmed when the exchange completes and removed when the exchange fails, anywhere in the route, after the block too, or is marked with `.RollbackAll()`. If the process dies between the key commit and the work, the key stays and the redelivery is skipped as a duplicate: an at-most-once loss window. |
| **No database at all**, for example an HTTP call as the work | Atomicity is impossible and the loss window is inherent. Rely on the downstream being idempotent. |

Rule of thumb: if the work is a database write, wrap the idempotent consumer in `.Transacted()`. If it is not, treat
delivery as at-most-once across a hard crash.

The key's fate is settled when the exchange's unit of work ends, before the route returns to its consumer: the key is
back before the message goes back to the broker, so a redelivery that another node picks up at once is not skipped.
This is Apache Camel's default (`completionEager=false`, `removeOnFailure=true`). Two details differ from Camel because
`OnException` redelivers the whole route rather than the failed step: the exchange that holds a key is not a duplicate
of itself, so a block that failed runs again on the redelivery and a block that succeeded is skipped, without the
`CamelDuplicateMessage` mark. A Split or Multicast branch is a unit of work of its own, as in Camel.

A repository tells the processor whether its writes join the transaction through
`IIdempotentRepository.JoinsAmbientTransaction` (true for `RedbIdempotentRepository`, false by default).

## Routes that do not start from a broker: the outbox recipe

Database first, sends after it, leaves one window: the work is stored, and the process dies before the message goes
out. A route that starts from a broker closes it by itself, because the unacknowledged message comes back. A route that
starts from an HTTP call, a timer or a file has nothing that comes back, and the announcement is lost for good.

The answer is the transactional outbox, built from two routes, as it is in Camel. The event is written as a row in the
same transaction as the work, so both stand or neither does; a second route polls the rows and sends them, marking each
one only after the broker has taken it.

```csharp
// 1. The work and the event commit together: the event is a row in the same database.
From(Http.Listen("/orders").Host("0.0.0.0").Port(8080).Methods("POST"))
    .Transacted()
        .Unmarshal<Order>()
        .ProcessWithRedb(async (redb, ex, ct) =>
        {
            var order = (Order)ex.In.Body!;
            await redb.SaveAsync(order, ct);
            await redb.Context.ExecuteAsync(
                "insert into outbox (id, payload, sent) values ($1, $2, false)",
                [Guid.NewGuid(), JsonSerializer.Serialize(order)], ct);
        })
    .End();

// 2. The relay: one exchange per unsent row; onSuccess marks the row once the send went through.
From(Sql.Poll("select id, payload from outbox where sent = false order by id")
        .DataSource("main")
        .Delay(1000)
        .OnSuccess("update outbox set sent = true where id = :#id"))
    .SetBody(ex => ex.In.Headers["payload"])
    .To("kafka://orders.created");
```

- A failed send leaves the row unmarked, and the next poll sends it again.
- A crash between the send and `onSuccess` sends the row twice: the relay is at-least-once, like every broker
  delivery, so the receiving side deduplicates, for example with an [idempotent consumer](#idempotent-consumer).
- Change data capture (Debezium, reading the outbox table from the database log) is the other common relay; it needs
  no polling and no `sent` column.

A built-in outbox with its own table and background dispatcher, as MassTransit or NServiceBus ship, is deliberately not
part of redb.Route: the two routes above are ordinary steps you can shape to your schema.

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
