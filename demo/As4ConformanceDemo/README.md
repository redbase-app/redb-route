# As4ConformanceDemo — the System Under Test for the eDelivery AS4 conformance testing service

The European Commission's [eDelivery conformance testing service](https://ec.europa.eu/digital-building-blocks/sites/spaces/DIGITAL/pages/467118600/eDelivery+conformance+testing)
checks an AS4 access point against the eDelivery AS4 profile. Its test driver cannot press buttons in somebody else's
product, so it asks the owner of the access point to write a **SUT Controller**: the piece that obeys the driver's
commands and reports back. This demo is that controller, and the access point it drives, as one redb.Route module.

Two projects, one entry point: `InitRoute.main` is called by both the Tsak worker and the debug host (the layout of
[EchoWorkerDemo](../EchoWorkerDemo)).

```
As4ConformanceDemo/
├─ ConformanceModule/           <- the module (class library -> ConformanceModule.tpkg)
│  ├─ InitRoute.cs              <- main(IRouteContext): two nodes, their agreements, the routes
│  ├─ Commands.cs               <- Submit, Deliver, Notify: a command to a message and back
│  ├─ ConformanceSettings.cs    <- settings from the route context and the table of service/action legs
│  └─ manifest.json
├─ ConformanceWorker/           <- the debug host (exe)
│  ├─ Program.cs                <- InitRoute.main(ctx) + Start, or "selftest", or "lossyproxy"
│  ├─ ContextConfiguration.cs   <- the context settings as the Tsak worker builds them
│  └─ SelfTest.cs               <- a test driver of our own on loopback
└─ deploy/                      <- the module under Tsak: docker-compose.tsak.yml, docker-compose.mssql.yml, .env.example
```

## What the service asks for, and where it is

A command is an ordinary AS4 user message on a channel of its own (service
`http://www.edelivery.eu/as4/conformancetest`); what it says travels in `eb:MessageProperties` and its payloads
(user guide v2.90, §5.3).

```
test driver ──Submit──>  /as4/controller  ──>  the user message it asks for  ──>  test driver
                                                   │ receipt, ebMS error or no receipt  ──Notify──>  test driver
test driver ──user message──>  /as4/sut  ──>  receipt in the response
                                                   │ ──Deliver──>  test driver      (not for the ebMS test service)
```

| Route | What it does |
|---|---|
| `as4ct-submit` | Receives a **Submit**, turns its properties into the header values of the message to send (`messageId`, `conversationId`, `refToMessageId`, every other property as a message property) and queues it. The command gets its receipt first. |
| `as4ct-send`, `as4ct-send-retry` | Send that message to the test driver under the agreement of its service and action, then send a **Notify**: `signalType=receipt`, or `signalType=error` with the ebMS code. The retry route redelivers a missing receipt (the same message, byte for byte) before it reports `EBMS:0301`. |
| `as4ct-receive` | The access point under test. The receipt goes out with the response; the message is queued for a **Deliver**. A message for the ebMS test service stops here: acknowledged, reported to nobody. |
| `as4ct-deliver` | Sends the **Deliver**: the header values of the received message as properties, its payloads as they came. |

Two nodes, because the service uses two pairs of party ids: `sut` is the access point under test, `controller` the
command channel. The agreements of `sut` are built from `ConformanceLegs.Default`, one per service and action of the
tests (user guide, annex A); a received message is matched on sender, service and action. Add a pair with
the `ExtraLegs` setting when a test suite needs one that is not there.

Three things of the connector carry the controller, all in 4.3.0:

- a list of `As4Payload` as the body: several payloads in the order given, under the Content-IDs the driver chose,
  with their part properties, and an empty list for a Notify, which has no payload;
- `payloadList` on the receive endpoints: a single payload still arrives with its Content-ID and part properties;
- `RequireFourCornerProperties = false`: the common profile does not ask for `originalSender` and `finalRecipient`.

## Check it without the service

```
dotnet run --project ConformanceWorker --framework net10.0 -- selftest
```

A second route context plays the test driver on loopback and walks the message flow of the service's own sample
("AS4 Basic Connectivity Two Way SUT C3"), then a ping, an ebMS error and the retry profile: a proxy between the two
swallows the driver's responses, once (the message is resent byte for byte, delivered once, and the receipt of the
resend is reported) and for good (`EBMS:0301` is reported after the redeliveries). Every step prints `PASS` or `FAIL`;
the exit code is 0 when all 47 passed. The last scenario is the PKI path: a signer that presents its certification path
is acknowledged, one that does not is refused where a path is required, and on a Submit the SUT sends with its own. The driver is the same connector with the parties swapped, so this proves the
controller logic and nothing about interoperability.

## Check it against Domibus

The Commission's test driver runs on Domibus. The Harmony stand (Harmony AP 2.6.2, whose MSH is Domibus 5.1) plays
that driver: its P-Mode has the roles, legs and commands of the tests, and its WS plugin submits what the driver would
send. Both directions were run there: Domibus sends a two-payload message to the access point and gets the Deliver
with the properties and both payloads; Domibus sends a Submit and gets the user message under the commanded id, then
the Notify; a Two-Way exchange, the response referring to the request under the same service and action; and the
retry profile through `ConformanceWorker lossyproxy`, which stands in front of Domibus and loses its responses: one
lost (resent byte for byte, Domibus answers with its stored receipt) and all lost (`EBMS:0301` reported). The stand,
its P-Mode and the script that drives it are described in the stand's own README
(`conformance-driver.sh receive|submit|twoway|retry`).

## Settings

The module takes its settings from the route context, the way the Tsak worker provides them. Its own
`ConformanceModule.config.json`, which travels inside the package, holds the defaults; what differs per test account
arrives through the Override layer, environment variables on the worker of the form
`Tsak__Contexts__as4-conformance__Override__<Section>__<Key>`.

| Section and key | Default | Meaning |
|---|---|---|
| `TestDriver:Url` | (required) | MSH address of the test driver for the messages under test. |
| `TestDriver:ControllerUrl` | `TestDriver:Url` | MSH address of the test driver for Deliver and Notify. |
| `TestDriver:PartyId` | `test_driver` | Party id of the test driver. |
| `TestDriver:ControllerPartyId` | `test_driver_sut_controller_domain` | Its party id on the command channel. |
| `TestDriver:Certificate` | (required) | File of the test driver's certificate (DER or PEM). |
| `TestDriver:TrustAnchors` | the test driver's certificate | Certificate files (`;` between them) of the authorities its signer must chain to on the legs `bstPkiPath*`. |
| `Sut:PartyId` | `system_under_test` | Party id of the access point under test, as assigned. |
| `Sut:ControllerPartyId` | `sut_controller_domain` | Our party id on the command channel. |
| `Sut:Pfx`, `Sut:PfxPassword` | (required), none | The SUT key as a PKCS#12 file. |
| `Sut:Chain` | none | Certificate files of the authorities above the SUT certificate: sent with it as its certification path on the legs `bstPkiPath*`. |
| `Sut:HostName` | `redb.route` | Host part of the message ids we generate. |
| `Receive:Port` | `4090` | Port of both receive endpoints. |
| `Receive:SutPath`, `Receive:ControllerPath` | `/as4/sut`, `/as4/controller` | Their paths. |
| `Receive:DuplicateStore` | `memory` | Where the ids of received messages are remembered: `memory` (lost with the process) or `redb` (the worker's redb: a resend is still a duplicate after a restart, and on every node of a cluster). |
| `Receive:DuplicateWindowDays` | `7` | How long an id is remembered. |
| `Retry:Count`, `Retry:DelaySeconds` | `5`, `20` | Redeliveries of the retry profile (the guide asks for 5, less than 3 minutes apart). |
| `Retry:SendTimeoutMs` | `60000` | Milliseconds to wait for the test driver's response. |
| `PkiPath:Policy` | none | Certificate policy OID required on the leg `bstPkiPathSutC3PolicyFail`; without it that leg is left out. |
| `ExtraLegs` | none | More legs: `service\|action;service\|action`. |

The party ids above are the ones of the service's sample messages; use the ones you are given.

## Run it under Tsak

`deploy/` holds what a deployment needs: `docker-compose.tsak.yml` (the Tsak stack image, in-memory storage, the
package in `./modules`, the keys in `./keys`) and `.env.example`. Copy the example to `.env`, fill it in, put
`ConformanceModule/output/ConformanceModule.tpkg` into `modules/` and the two key files into `keys/`:

```
docker compose -f docker-compose.tsak.yml up -d
```

The package holds the module's DLL, its config file and the manifest; the AS4 connector comes from the worker's shared
layer, so **the worker image must carry the connector this module was built against**. The compose file joins the
Docker network of the access point that plays the test driver, and that access point posts to
`http://as4ct-tsak:4090/as4/sut` and `/as4/controller`. For the conformance testing service itself the two paths go
behind the reverse proxy that terminates TLS: the service needs two public HTTPS addresses from you.

### Persistent storage

`deploy/docker-compose.mssql.yml` goes on top of the first file: Tsak's own state and the module's memory of received
message ids live in a SQL Server database, and `Receive:DuplicateStore` becomes `redb`. Without it everything ends
with the process: a message the partner resends after a restart of the container would be delivered a second time.
Give the stand a database and a login of its own; redb creates its tables, not the database.

```
docker compose -f docker-compose.tsak.yml -f docker-compose.mssql.yml up -d
```

Checked against Domibus (2026-10-13): a message is delivered and reported, its receipt is lost on the way back, the
container is restarted, Domibus resends the message — the restarted module answers with the receipt again and does
not deliver or report it a second time.

### What was run

This was run on a Linux host against Domibus (2026-10-13): the scenarios of `conformance-driver.sh` pass from the
container — `receive`, `submit`, `twoway`, `retry` through the lossy proxy (one response lost, and all of them), and
`receive` and `submit` on the PKI path leg. That run is also what found that the connector wrote MIME line endings
by the platform's default: from Linux every message with a payload was refused until 4.3.0 (see the changelog).

## Run it in the debug host

The same entry point, outside Tsak: the host reads the module's config file and the same Override variables.

```
dotnet run --project ConformanceWorker --framework net10.0
```

## What is not here

- **Pull, Dynamic Discovery, SBDH**: optional enhancements of the profile, not implemented by the connector. The Four Corner enhancement needs nothing more than the properties the driver gives.
- **Negative tests** (a damaged payload, a hyperlink reference, compression after signing): the driver sends those
  itself and expects an ebMS error from the access point; no controller logic is involved.
- **Keys**: nothing of a test account belongs in a repository; `.gitignore` here refuses the usual file types.
