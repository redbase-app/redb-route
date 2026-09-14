# SerialNumbersDemo: a partner integration hub on redb and redb.Route

A small but complete integration project, meant as a template for your own: trading partners send
serial number requests as XML files over **SFTP** or **AS2**, the hub stores everything, decides each
request, issues serial numbers in **SQL Server** and sends the answer back over the partner's
transport.

It shows the pieces a real integration needs, each in the place it belongs:

| Concern | How |
|---|---|
| Transports | `Sftp.Directory(...)`, `As2.Receive(...)` / `As2.Send(...)`, `FileDsl.Write(...)`, `Sql.Poll(...)` |
| Connection settings | one named connection factory per partner in the registry, no secrets in routes |
| Durable archive | a raw copy of every received file, before anything can fail on its content |
| Routing by content | `Choice()` with `XPath(...)` |
| Validation | `ValidateXsd(...)` + `Unmarshal<T>("application/xml")` inside `DoTry` / `DoCatch` |
| Exactly-once intake | `IdempotentConsumer` backed by redb, around the transaction |
| Atomic processing | one redb transaction for objects and flat tables: `Transacted(TransactionPolicy.Suppress)` + `BeginRedbTransaction()` |
| Business rules | a pure function in the domain project, a `Choice()` branch in the route |
| Context-wide error handling | `OnException<T>().Handled(true)` in its own route builder |
| Reliable delivery | an outbox table written in the same transaction, read by `Sql.Poll` with `OnSuccess` / `OnFailure` |
| Scheduled work | `Cron.Schedule(...)` (Quartz.NET): a quota usage report from a flat table and redb objects |

## Layout

```
SerialNumbersDemo/
├─ Directory.Build.props            <- target framework and the redb package version, in one place
├─ SerialNumbers.Domain/            <- redb entities + the business rules (depends on redb.Core only)
│  ├─ Entities/                     <- Partner, Product, InboundMessage, SerialNumberRequest, SerialNumberResponse
│  └─ Services/                     <- SerialRequestPolicy (pure), statuses, reasons, transports
├─ SerialNumbers.Core/              <- the Tsak module
│  ├─ InitRoute.cs                  <- entry point: components, connections, database, routes
│  ├─ SerialNumbers.Core.config.json <- the module's configuration layer
│  ├─ ModuleSettings.cs             <- settings from the context, passwords from the environment
│  ├─ Infrastructure/               <- context-wide exception handling
│  ├─ Integration/Xml/              <- wire formats of the partner messages + XSD
│  ├─ Routes/Inbound/               <- one SFTP consumer / AS2 endpoint per partner
│  ├─ Routes/Processing/            <- intake (archive, route by type) and the request transaction
│  ├─ Routes/Outbound/              <- outbox polling and one delivery route per partner
│  ├─ Routes/Reports/               <- a cron route: the quota usage report
│  ├─ Services/                     <- the steps the routes call (redb objects, SQL through redb.Context)
│  └─ Database/schema.sql           <- the flat tables: serial numbers, allocation ledger, outbox
├─ SerialNumbers.Worker/            <- debug host + two simulated partners (ACME on SFTP, GLOBEX on AS2)
├─ SerialNumbers.deploy/            <- pack-tpkg.ps1, docker-compose.dev.yml, .env.example
└─ samples/                         <- partner files for every path through the hub
```

## Where the data lives

- **redb objects** for the business model: partners, products, every inbound message, requests and
  responses. Partners and products carry their natural key (code, GTIN) as the unique key of the object.
- **Flat tables** in the same database for what is volume rather than model: one row per serial
  number (a request can ask for tens of thousands), the yearly allocation ledger the quota is checked
  against, and the outbox.
- **One transaction** covers both: the flat tables are written through `redb.Context`, on the same
  connection as the objects of the request.

## Run it

Requirements: .NET 10 SDK, Docker.

```bash
docker compose -f SerialNumbers.deploy/docker-compose.dev.yml up -d
dotnet run --project SerialNumbers.Worker
```

The worker creates the `serials_demo` database, the redb tables, the flat tables, self-signed AS2
certificates, two partners and two products, then starts the hub and the simulated partners.
Relative paths resolve against the current directory: everything the worker writes goes under `runtime/`.

Drop sample files into a partner's outbox:

```bash
cp samples/acme/*.xml   runtime/partners/acme/outbox/     # uploaded to the hub over SFTP
cp samples/globex/*.xml runtime/partners/globex/outbox/   # sent to the hub over AS2
```

Responses arrive in `runtime/partners/<partner>/inbox/`, the raw copies in `runtime/archive/`, and a
quota usage report lands in `runtime/reports/` every minute.

## Configuration

The module reads its settings the way a Tsak module does: from the properties of its route context.

| Where | What |
|---|---|
| `SerialNumbers.Core/SerialNumbers.Core.config.json` | Module defaults, shipped in the package: archive folder, SFTP host and user, AS2 id and port, report folder and schedule |
| `Tsak:Contexts:serial-numbers:Override` in the worker's `appsettings.json` | Values of one environment, the last word. The debug host sets local folders, the SFTP container and a report every minute here |
| `ConnectionStrings:MSSql` of the worker | The database redb lives in; the flat tables live there too |
| `Sftp:Password`, `Sftp:Passwords:<code>`, `As2:CertificatePassword` in the `Override` section | Secrets: environment variables on the worker (`Tsak__Contexts__serial-numbers__Override__Sftp__Password`), never in a file that ships. See `SerialNumbers.deploy/.env.example` |

The Tsak worker merges `Tsak:Contexts:default`, `Tsak:Contexts:serial-numbers`, the module's config file
and the `Override` section in that order and sets every root key as a context property (a section
becomes a dictionary); `ModuleSettings.FromContext` reads them back. Route builders take nothing through
their constructors: in `Configure()` they read the settings, and the partner list `InitRoute` loaded from
redb, through `Context`. `SerialNumbers.Worker` builds the
same layers from its own `appsettings.json` (`ContextConfiguration.cs`), so an environment variable such
as `Tsak__Contexts__serial-numbers__Override__Report__Cron` or `ConnectionStrings__MSSql` works the same
way in both.

## What each sample does

| File | Path through the hub | Result |
|---|---|---|
| `acme/SNREQ_ACME_0001.xml` | SFTP, valid | Accepted, 30 000 serial numbers, response over SFTP |
| `acme/SNREQ_ACME_0002_over_limit.xml` | SFTP, above the per-request limit | Rejected `QuantityOutOfRange` |
| `acme/SNREQ_ACME_0003_unknown_product.xml` | SFTP, GTIN not in the catalog | Rejected `UnknownProduct` |
| `acme/SNREQ_ACME_0004_duplicate.xml` | SFTP, request id already seen | Rejected `DuplicateRequest`, in a response file of its own |
| `acme/SNREQ_ACME_0005_schema_violation.xml` | SFTP, breaks the XSD | Recorded as Invalid, no response |
| `acme/SNREQ_ACME_0006_broken.xml` | SFTP, not well-formed XML | Recorded as Invalid, no response |
| `acme/PIF_ACME_0001.xml` | SFTP, a message type with no route | Parked by the context-wide `OnException` |
| `globex/SNREQ_GLOBEX_0001.xml` | AS2, valid | Accepted, response over AS2 |
| `globex/SNREQ_GLOBEX_0002_quota.xml` | AS2, over the yearly quota | Rejected `AnnualQuotaExhausted` |

## Failures, and who deals with them

| Failure | Handled by | Outcome |
|---|---|---|
| Not well-formed XML | `Choice()` branch in the intake | Archived, recorded as Invalid |
| Schema violation | `DoTry` / `DoCatch<ValidationException>` | Archived, recorded as Invalid |
| Message type without a route | `OnException<UnsupportedMessageTypeException>().Handled(true)` | Archived, recorded as Parked |
| Business rejection | `Choice()` in the transaction | A normal response with the reason |
| Same file delivered twice | `IdempotentConsumer` | Skipped |
| Database or network down | nothing, on purpose | Rolled back; the SFTP file stays for the next poll, an AS2 sender gets a negative MDN |
| Partner unreachable on delivery | `Sql.Poll(...).OnFailure(...)` | The outbox row counts the attempt and is retried |

A handled exception is a success for the consumer: the SFTP file moves to `.done`, an AS2 sender gets
a positive MDN. That is why the context-wide handler catches one specific exception, and technical
failures are left unhandled.

## Scheduled work

`QuotaReportRouteBuilder` starts from `Cron.Schedule("reports/quota-usage", ...)`: Quartz.NET fires the
route, no message has to arrive. It sums the allocation ledger with `Sql.Execute(...)`, looks up the
annual quota of each product in redb and writes a CSV file:

```
gtin,product,requests,issued,annual_quota,remaining,used_percent
04607001234567,"Paracetamol 500 mg, 20 tablets",1,30000,10000000,9970000,0.3
04607009990001,"Vitamin D3 drops, 10 ml",1,30000,40000,10000,75.0
```

- The schedule is `Report:Cron` (Quartz format, seconds first; the module ships `0 0 6 * * ?`, every
  day at 06:00), read in `Report:TimeZone` (`UTC`). The debug host overrides it to every minute.
- `Stateful()` keeps two runs from overlapping when one takes longer than the interval.
- Under Tsak the route runs on the worker's shared Quartz scheduler. A Tsak cluster keeps that scheduler
  on the database job store with `quartz.jobStore.clustered=true` (the worker logs a critical error
  otherwise), and a clustered Quartz fires each trigger on one node.

## Debug, then deploy

`SerialNumbers.Worker` calls `SerialNumbers.Core.InitRoute.main(ctx)`, the same entry point the Tsak
worker calls, so breakpoints in routes and services work as in any console app. To deploy:

```powershell
./SerialNumbers.deploy/pack-tpkg.ps1
# -> SerialNumbers.deploy/output/SerialNumbers.tpkg
```

Copy the package into the worker's `modules/` folder. Its `ConnectionStrings:MSSql` already points at
the database redb uses; the SFTP server, the passwords and anything else that differs per environment go
into `Tsak:Contexts:serial-numbers:Override`, usually as environment variables of the worker
(`SerialNumbers.deploy/.env.example`). Partners are read when the module starts: after adding a
partner, reload the module.

## Keeping EF Core for part of the model

Entities that must stay in an existing EF Core model can be reached from a route with a small DSL
extension of your own. Every exchange carries a DI scope that is disposed with it:

```csharp
public static IRouteDefinition ProcessWithDb<TDb>(this IRouteDefinition route,
    Func<TDb, IExchange, CancellationToken, Task> action) where TDb : DbContext
    => route.Process(async (exchange, ct) =>
    {
        var db = exchange.ServiceProvider?.GetRequiredService<TDb>()
            ?? throw new InvalidOperationException("The exchange has no DI scope.");
        await action(db, exchange, ct);
    });
```

Keep in mind that an EF Core `DbContext` and redb use separate connections: work that must be atomic
belongs to one of them.
