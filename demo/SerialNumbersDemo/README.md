# SerialNumbersDemo: a partner integration hub on redb and redb.Route

A small but complete integration project, meant as a template for your own: trading partners send
serial number requests as XML files over **SFTP** or **AS2**, the hub stores everything, decides each
request, issues serial numbers in **SQL Server** and sends the answer back over the partner's
transport. A product API lets a web page, or code in the same process, activate a product, and the
requests that waited for it are answered without the partner sending anything again.

Needs redb and redb.Route **4.1.0** or later: one transaction path with `.Transacted()`, file consumer
backoff, path filters on the file consumers and an async `InitRoute.main` are used throughout.

It shows the pieces a real integration needs, each in the place it belongs:

| Concern | How |
|---|---|
| Transports | `Sftp.Directory(...)`, `As2.Receive(...)` / `As2.Send(...)`, `FileDsl.Write(...)`, `Sql.Poll(...)` |
| Connection settings | one named connection factory per partner in the registry, no secrets in routes |
| Durable archive | a raw copy of every received file, before anything can fail on its content |
| Routing by content | `Choice()` with `XPath(...)` |
| Validation | `ValidateXsd(...)` + `Unmarshal<T>("application/xml")` inside `DoTry` / `DoCatch` |
| Atomic processing | `.Transacted()`: redb objects, flat tables through `redb.Context` and an EF Core `DbContext`, one transaction |
| Duplicate requests | the request id of the partner is the unique key of the request object, checked in the same transaction |
| Business rules | a pure function in the domain project, a `Choice()` branch in the route |
| Requests that wait | a request for a draft product is put on hold, and released from the archive when the product is activated |
| Web and in-process calls | REST DSL `PUT /api/products/{gtin}/status`, and a `ProducerTemplate` to the same `direct:` endpoint |
| Serial numbers | one random number per block of 10 000, issued by one set-based SQL statement |
| Polling while things fail | SFTP consumer backoff: fewer polls while the server or the database is down |
| Several directories of one partner | one consumer on their parent with `Recursive()` and `AntInclude(...)`, so one connection serves them all |
| Seeing messages flow | `.Log(...)` at every stage, `.MessageHistory()` on the intake and request routes |
| Context-wide error handling | `OnException<T>().Handled(true)` in its own route builder |
| Reliable delivery | an outbox table written in the same transaction, read by `Sql.Poll` with `OnSuccess` / `OnFailure` |
| Scheduled work | `Cron.Schedule(...)` (Quartz.NET): a quota usage report from a flat table and redb objects |

## Layout

```
SerialNumbersDemo/
├─ Directory.Build.props            <- target framework, redb and EF Core package versions, in one place
├─ SerialNumbers.Domain/            <- redb entities + the business rules (depends on redb.Core only)
│  ├─ Entities/                     <- Partner, Product, InboundMessage, SerialNumberRequest, SerialNumberResponse
│  └─ Services/                     <- SerialRequestPolicy (pure), statuses, reasons, transports
├─ SerialNumbers.Core/              <- the Tsak module
│  ├─ InitRoute.cs                  <- entry point (async): components, connections, database, routes
│  ├─ SerialNumbers.Core.config.json <- the module's configuration layer
│  ├─ ModuleSettings.cs             <- settings from the context, passwords from the environment
│  ├─ Catalog/                      <- product status changes: the EF Core journal and the API contract
│  ├─ Infrastructure/               <- context-wide exception handling
│  ├─ Integration/Xml/              <- wire formats of the partner messages + XSD
│  ├─ Routes/Inbound/               <- one SFTP consumer / AS2 endpoint per partner
│  ├─ Routes/Processing/            <- intake (archive, route by type) and the request transaction
│  ├─ Routes/Catalog/               <- the product API and the release of requests on hold
│  ├─ Routes/Outbound/              <- outbox polling and one delivery route per partner
│  ├─ Routes/Reports/               <- a cron route: the quota usage report
│  ├─ Services/                     <- the steps the routes call (redb objects, SQL through redb.Context)
│  └─ Database/schema.sql           <- the flat tables and the EF Core journal table
├─ SerialNumbers.Worker/            <- debug host + two simulated partners (ACME on SFTP, GLOBEX on AS2)
├─ SerialNumbers.deploy/            <- pack-tpkg.ps1, docker-compose.dev.yml, docker-compose.tsak.yml, .env.example
└─ samples/                         <- partner files for every path through the hub
```

## Where the data lives

- **redb objects** for the business model: partners, products, every inbound message, requests and
  responses. Partners and products carry their natural key (code, GTIN) as the unique key of the object.
- **Flat tables** in the same database for what is volume rather than model: one row per serial
  number (a request can ask for tens of thousands), the yearly allocation ledger the quota is checked
  against, and the outbox.
- **An EF Core model** for the product status journal, standing in for the part of a data model an
  existing application keeps in EF Core.
- **One transaction** covers all three, as the next section shows.

## Run it

Requirements: .NET 10 SDK, Docker.

```bash
docker compose -f SerialNumbers.deploy/docker-compose.dev.yml up -d
dotnet run --project SerialNumbers.Worker
```

The worker creates the `serials_demo` database, the redb tables, the flat tables, self-signed AS2
certificates, two partners and three products (one of them a draft), then starts the hub and the
simulated partners. Relative paths resolve against the current directory: everything the worker writes
goes under `runtime/`.

Coming from the first version of the demo? Its flat tables have a different shape. Start from a clean
database: `docker compose -f SerialNumbers.deploy/docker-compose.dev.yml down`, then `up -d` again.

Drop sample files into a partner's outbox:

```bash
cp samples/acme/*.xml   runtime/partners/acme/outbox/     # uploaded to the hub over SFTP
cp samples/globex/*.xml runtime/partners/globex/outbox/   # sent to the hub over AS2
```

Responses arrive in `runtime/partners/<partner>/inbox/`, the raw copies in `runtime/archive/`, and a
quota usage report lands in `runtime/reports/` every minute.

## One transaction

This is the core of the request route (`Routes/Processing/SerialNumberRequestRouteBuilder.cs`):

```csharp
.Transacted()
    .ProcessWithRedb(SerialRequestService.RegisterAsync)
    .Choice()
        .When(e => DecisionOf(e) == MessageStatuses.Accepted)
            .ProcessWithRedb(SerialRequestService.AllocateAsync)
            .ProcessWithRedb(SerialRequestService.QueueResponseAsync)
        .When(e => DecisionOf(e) == MessageStatuses.Rejected)
            .ProcessWithRedb(SerialRequestService.RejectAsync)
            .ProcessWithRedb(SerialRequestService.QueueResponseAsync)
    .EndChoice()
.EndTransaction()
```

- `.Transacted()` opens a transaction around the block. redb takes part in it by itself: every step
  that works through `IRedbService`, raw SQL through `redb.Context` included, runs on the connection the
  transaction holds. Everything commits together or nothing does.
- **Duplicates are recognised by the data, not by a registry of processed files.** The request object
  carries the partner's request id as its unique key, written in the same transaction: a second request
  with that id is answered as a duplicate, a concurrent twin fails on the key, and a delivery that rolled
  back leaves no trace and is processed again. A separate repository of delivered files would key the
  same fact by file name and size, which a corrected file changes and a re-sent request does not.
- **SQL against the same database belongs in `redb.Context`**, not in a `sql:` endpoint inside the
  block: that endpoint opens a second connection while redb's is still open, and two open connections
  in one transaction make it a distributed transaction, which fails on SQL Server and aborts at commit on
  PostgreSQL. Outside a transaction, as in the outbox and report routes, `sql:` is the right tool.
- **No parallel branches in one transaction.** Work running at the same time on the transaction's
  connection, as a parallel `Split` or `Multicast` inside the block would, is refused; open the
  transaction inside each branch instead.
- The default policy is `Required`, `ReadCommitted`, 30 seconds. Pass a `TransactionPolicy` to
  `.Transacted(...)` when a block needs more.

## Requests on hold

A product starts as a **draft**. A request for it is not the partner's mistake, so it is neither
rejected nor failed: it is recorded with the status `OnHold`, the partner's file is accepted as usual
(the SFTP file moves to `.done`, an AS2 sender gets a positive MDN), and nothing is sent yet.

When the product is activated, the hub releases those requests on its own:

1. `set-product-status` changes the redb product and writes a row of the EF Core status journal, in one
   transaction.
2. After it commits, `release-held-requests` finds the requests on hold for the product and reads each
   one's file back from the archive.
3. Each file goes through the request route again, in a transaction of its own. The registration
   recognises the release and decides the **same** request object again, against the product and the
   quota as they are now.

A released request is no longer on hold, so a second activation call finds nothing to release, while a
release that rolled back left the request on hold and the next call retries it. A failure stops the batch
and reaches the caller; the requests not released stay on hold.

Activate the draft product from a web page, curl or anything else that speaks HTTP:

```bash
curl -X PUT http://localhost:8090/api/products/04607005550001/status \
     -H "Content-Type: application/json" \
     -d '{"status":"Active","changedBy":"alice"}'
# {"gtin":"04607005550001","status":"Active","outcome":"Changed","requestsReleased":1}
```

The REST DSL declares the operation, binds the JSON body and serves an OpenAPI document at
`/api/products/openapi.json`:

```csharp
this.Rest("/api/products", o => { o.Port = settings.ApiPort; o.BindingMode = RestBindingMode.Json; })
    .Put("/{gtin}/status").Consumes("application/json").Type<ProductStatusChange>()
    .To(RouteUris.SetProductStatus);
```

Code in the same process calls the same endpoint with a `ProducerTemplate`. The debug host does that
for the console command `status 04607005550001 Active` (`SerialNumbers.Worker/ProductConsole.cs`):

```csharp
using var producer = new ProducerTemplate(hub);
producer.Start();

var message = new Message(new ProductStatusChange(status, ChangedBy: "console"));
message.Headers[SerialHeaders.Gtin] = gtin;
Console.WriteLine(await producer.RequestBody(RouteUris.SetProductStatus, message, stopping));
```

## EF Core on the redb connection

The status journal is an EF Core `DbContext` (`Catalog/CatalogAuditDbContext.cs`), written in the same
transaction as the redb product (`Catalog/ProductCatalog.cs`):

```csharp
product.Props.Status = change.Status;
await redb.SaveAsync(product, ct);

await using var journal = CatalogAuditDbContext.On(await redb.Context.Db.GetUnderlyingConnectionAsync(ct));
journal.StatusChanges.Add(record);
await journal.SaveChangesAsync(ct);
```

`CatalogAuditDbContext.On` builds the context with `UseSqlServer(connection, contextOwnsConnection: false)`.
Inside `.Transacted()`, `GetUnderlyingConnectionAsync` returns the connection the transaction holds; the
connection is already enlisted, so EF Core writes into the same transaction and `SaveChanges` opens no
transaction of its own. Three rules keep it that way:

- EF Core uses exactly this connection, never one of its own.
- EF Core and redb use the connection one after the other, never at the same time.
- It runs inside `.Transacted()`. `ProductCatalog` refuses to run outside a transaction, where the two
  writes would commit separately.

This works on SQL Server and PostgreSQL. On SQLite the transaction is not an enlistment but a transaction
object redb begins itself, and EF Core does not join it.

The EF Core version is set in `Directory.Build.props`. Its SQL Server provider has to accept the
Microsoft.Data.SqlClient version redb.MSSql and the Tsak worker ship.

## Serial numbers that cannot be guessed

Serial numbers on medicines are checked at the pharmacy against the agency the authorization holder
reports them to, so they must not be guessable. The hub gives every number a block of its own: the i-th
number of a request is a random value between `(first + i) * 10000 + 1` and `(first + i + 1) * 10000`,
one chance in 10 000 to guess it. The randomness comes from `CRYPT_GEN_RANDOM`, the cryptographic
generator of SQL Server, and one `INSERT ... SELECT ... FROM GENERATE_SERIES(...)` writes all 30 000 rows
(`Services/SerialNumberAllocator.cs`). Blocks never overlap, so two numbers cannot collide.

The response lists every number:

```xml
<SerialNumberResponse>
  <RequestId>ACME-2026-0001</RequestId>
  <Gtin>04607001234567</Gtin>
  <Status>Accepted</Status>
  <Quantity>30000</Quantity>
  <SerialNumbers>
    <SerialNumber>000000007315</SerialNumber>
    <SerialNumber>000000012094</SerialNumber>
    ...
  </SerialNumbers>
</SerialNumberResponse>
```

## Seeing how messages flow

Every stage writes a log line: a file received over SFTP or AS2, archived, recognised, decided and
queued, released, delivered. The SFTP inbound route uses the richer form that adds headers to the entry:

```csharp
.Log()
    .Message("${header.serials.partner}: received ${header.serials.fileName} over SFTP")
    .Header(SftpHeaders.FileLength)
.EndLog()
```

`.MessageHistory()` on the intake and request routes times every step. The trace lives in the exchange,
so a step has to print it: the parked-message handler in `ExceptionRouteBuilder` logs it with
`.Log(MessageHistory.Format, LogLevel.Warning)`, and the same line at the end of the request route prints
the path of every request at `Debug`. History is off globally (`RouteEngineOptions.EnableMessageHistory`)
and switched on per route, because timing every step costs time.

In a processor of your own, take the logger from the exchange's context:
`exchange.Context.GetService<ILoggerFactory>()`. If you create a `RouteContext` yourself, pass it an
`ILoggerFactory`; without one the `.Log` steps write nothing and the routes keep working.

Under Tsak each route has a page with the messages in flight, counters, errors and per-endpoint
statistics, and the worker has a live log viewer with a level filter and search.

## Configuration

The module reads its settings the way a Tsak module does: from the properties of its route context.

| Where | What |
|---|---|
| `SerialNumbers.Core/SerialNumbers.Core.config.json` | Module defaults, shipped in the package: archive folder, SFTP host and user, AS2 id and port, product API port, report folder and schedule |
| `Tsak:Contexts:serial-numbers:Override` in the worker's `appsettings.json` | Values of one environment, the last word. The debug host sets local folders, the SFTP container and a report every minute here |
| `ConnectionStrings:MSSql` of the worker | The SQL Server database of the hub: redb objects, flat tables and the EF Core journal. Under Tsak it is redb's database only with `Tsak:Redb:Provider` set to `mssql` (see [Run it under Tsak](#run-it-under-tsak)) |
| `Sftp:Password`, `Sftp:Passwords:<code>`, `As2:CertificatePassword` in the `Override` section | Secrets: environment variables on the worker (`Tsak__Contexts__serial-numbers__Override__Sftp__Password`), never in a file that ships. See `SerialNumbers.deploy/.env.example` |

The Tsak worker merges `Tsak:Contexts:default`, `Tsak:Contexts:serial-numbers`, the module's config file
and the `Override` section in that order and sets every root key as a context property (a section
becomes a dictionary); `ModuleSettings.FromContext` reads them back. Route builders take nothing through
their constructors: in `Configure()` they read the settings, and the partner list `InitRoute` loaded from
redb, through `Context`. `SerialNumbers.Worker` builds the same layers from its own `appsettings.json`
(`ContextConfiguration.cs`), so an environment variable such as
`Tsak__Contexts__serial-numbers__Override__Report__Cron` or `ConnectionStrings__MSSql` works the same way
in both.

The AS2 endpoints and the product API share the process's HTTP servers, one per port. The host
registers them with `services.AddRedbRouteHttpHosting()`: the Tsak worker does, and so does the debug host.

## What each sample does

| File | Path through the hub | Result |
|---|---|---|
| `acme/SNREQ_ACME_0001.xml` | SFTP, valid | Accepted, 30 000 serial numbers, response over SFTP |
| `acme/SNREQ_ACME_0002_over_limit.xml` | SFTP, above the per-request limit | Rejected `QuantityOutOfRange` |
| `acme/SNREQ_ACME_0003_unknown_product.xml` | SFTP, GTIN not in the catalog | Rejected `UnknownProduct` |
| `acme/SNREQ_ACME_0004_duplicate.xml` | SFTP, request id already seen | Rejected `DuplicateRequest`, in a response file of its own |
| `acme/SNREQ_ACME_0005_schema_violation.xml` | SFTP, breaks the XSD | Recorded as Invalid, no response |
| `acme/SNREQ_ACME_0006_broken.xml` | SFTP, not well-formed XML | Recorded as Invalid, no response |
| `acme/SNREQ_ACME_0007_draft_product.xml` | SFTP, the product is a draft | On hold, no response; activate the product and it is accepted |
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
| Product is a draft | the policy decides `OnHold` | Recorded, nothing sent, released when the product is activated |
| Same request delivered twice | unique key of the request object, checked in the transaction | Answered as a duplicate, nothing issued |
| Database or network down | nothing, on purpose | Rolled back; the SFTP file stays for the next poll, an AS2 sender gets a negative MDN |
| The same failure on every poll | SFTP consumer backoff | After three failed polls in a row the next ten are skipped |
| A release fails | nothing, on purpose | That request rolls back and stays on hold, the batch stops, the API call fails; call it again |
| Partner unreachable on delivery | `Sql.Poll(...).OnFailure(...)` | The outbox row counts the attempt and is retried |

A handled exception is a success for the consumer: the SFTP file moves to `.done`, an AS2 sender gets
a positive MDN. That is why the context-wide handler catches one specific exception, and technical
failures are left unhandled.

The SFTP consumer's backoff is declared on the endpoint:

```csharp
Sftp.Directory(partner.SftpInboundFolder!)
    .Delay(2000)
    .MoveTo(".done")
    .BackoffErrorThreshold(3)
    .BackoffMultiplier(10)
    .BackoffOnFailedExchanges()
```

`BackoffErrorThreshold` and `BackoffMultiplier` work as in Apache Camel: a poll that could not run, for
example because the server is down, counts as failed. `BackoffOnFailedExchanges()` goes further than
Camel: a poll in which every file failed in the route, as they do while the database is down, counts as
failed too, and a poll with one successful file is a success.

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

`SerialNumbers.Worker` awaits `SerialNumbers.Core.InitRoute.main(ctx)`, the same entry point the Tsak
worker calls, so breakpoints in routes and services work as in any console app. `main` is async:

```csharp
public static async Task<IRouteContext> main(IRouteContext context)
```

Tsak accepts this form and the synchronous `IRouteContext main(IRouteContext context)`; a `main` of any
other shape is reported as an error when the module loads. To deploy:

```powershell
./SerialNumbers.deploy/pack-tpkg.ps1
# -> SerialNumbers.deploy/output/SerialNumbers.tpkg
```

The package carries the module's two DLLs and EF Core; redb, redb.Route and SqlClient come from the
worker's shared libraries. Copy it into the worker's `modules/` folder and configure the worker as the
next section describes. Partners are read when the module starts: after adding a partner, reload the
module.

## Run it under Tsak

Needs a Tsak worker 4.1.0, the version the module is built against; a 4.0.1 worker still recognises the
async `main`, while anything earlier skips the module without loading it.
`SerialNumbers.deploy/docker-compose.tsak.yml` runs the whole thing locally with the Tsak stack image, the
worker and its dashboard in one container. From the demo folder:

```bash
cp SerialNumbers.deploy/.env.example SerialNumbers.deploy/.env      # fill in the four Tsak values
docker compose -f SerialNumbers.deploy/docker-compose.tsak.yml up -d sqlserver sftp
dotnet run --project SerialNumbers.Worker -- seed
pwsh SerialNumbers.deploy/pack-tpkg.ps1
docker compose -f SerialNumbers.deploy/docker-compose.tsak.yml up -d tsak
```

`.env` holds the worker's API secret, the dashboard's service key, the key's hash (the command that computes
it is in `.env.example`) and the dashboard's admin password; it stays out of Git. Copy a sample into
`runtime/sftp/acme/to-hub/`; the response arrives in `runtime/sftp/acme/from-hub/`. The dashboard is at
http://localhost:8085, the product API at http://localhost:8090. The ports are published on 127.0.0.1 only.

What the worker needs, whichever way you run it:

- **The worker's default redb runs on SQL Server.** The module uses the worker's default redb, the
  unnamed one every route step gets through `ProcessWithRedb(...)`. The worker builds it from
  `Tsak:Redb:Provider` (`mssql`, `postgres` or `sqlite`; the worker ships `sqlite`) and the matching
  connection string, `ConnectionStrings:MSSql` for `mssql`. Set both `Tsak__Redb__Provider=mssql` and
  `ConnectionStrings__MSSql`: the connection string alone leaves the provider on SQLite, and the module's
  SQL Server script fails there with `near "IF": syntax error`. With `Tsak:Storage:Type=Redb`, as in the
  compose file, Tsak keeps its own state in the same database, which is fine for this demo. The compose
  file also sets `Tsak__Redb__PropsSaveStrategy=ChangeTracking`: a request is written several times over
  its life, and this way each save writes only what changed instead of rewriting every property. It needs
  the Pro tier, which is free and which `Tsak__Redb__UsePro=true` turns on.
- **The database exists before the first start.** redb creates its tables, not the database.
  `SerialNumbers.Worker seed` creates it, together with the demo partners and products, the AS2
  certificates and the ACME SFTP folders, which nothing else creates under Tsak. Without partners the
  module starts with no inbound routes and files wait in the folder.
- **Addresses as the worker sees them.** Inside a container network the SFTP server is `sftp:22`, not
  `localhost:2222`, and the certificate, archive and report folders are paths inside the container.
- **The AS2 partner is simulated only by the debug host.** Under Tsak, exchange files over SFTP.

Everything that differs per environment goes into `Tsak:Contexts:serial-numbers:Override`, usually as
environment variables of the worker (`SerialNumbers.deploy/.env.example`).

When a module does not start, the reason is the exception logged with `Failed to initialize module`; on a
4.0.0 worker it is the inner exception of a `TargetInvocationException`. A line saying the context started
with 0 endpoints right after it means the context is running without the module.
