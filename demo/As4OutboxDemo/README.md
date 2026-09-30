# As4OutboxDemo — an AS4 outbox and inbox as a redb.Route module + a debug host

Files dropped into a folder are sent as AS4 messages (eDelivery AS4 1.16: signed, encrypted, compressed); the
receiver verifies them, drops duplicates, writes the document to another folder, and answers with a signed
receipt that the sender verifies. Two projects, one entry point: `InitRoute.main` is called by both the Tsak
worker and the debug host, so the route code is never duplicated (the layout of [EchoWorkerDemo](../EchoWorkerDemo)).

```
As4OutboxDemo/
├─ As4Module/           <- the module (class library -> As4Module.tpkg)
│  ├─ InitRoute.cs      <- main(IRouteContext): the node, the agreement, 2 routes
│  ├─ manifest.json     <- { Name, Version, EntryPoints: ["As4Module.dll"] }
│  └─ As4Module.csproj  <- + the PackTpkg target (zips manifest + DLL)
└─ As4Worker/           <- the debug host (exe)
   ├─ Program.cs        <- demo folder + self-signed certificate + InitRoute.main(ctx) + Start
   └─ As4Worker.csproj
```

## What it does

```
as4-demo/outbox/*  ──file──>  AS4 send to "demo-partner"  ──signed receipt──>  moved to as4-demo/sent/
                                           │ not delivered after 3 redeliveries / ebMS error  ──>  moved to as4-demo/failed/
AS4 receive :4090/as4/in  ──duplicates dropped──>  as4-demo/inbox/<original file name>
```

| Route | What it shows |
|---|---|
| `as4-outbox` | `file:` consumer → the eDelivery four-corner properties (`originalSender`, `finalRecipient`) → the file name as an `eb:Property` → `As4.Send(...)`; the receipt id is on the exchange after the send. |
| `as4-inbox` | `As4.Receive(...)` with an `IIdempotentRepository` (mandatory: a resend gets its receipt again and is not delivered twice) → `file:` producer, the name restored from the `eb:Property`. |
| `OnException` | `As4ReceiptException` (no receipt, bad receipt) and `HttpRequestException` (the access point unreachable): three redeliveries of **the same bytes** (same message id and signature, so the partner detects the duplicate); `As4ErrorSignalException` (the partner refused): no redelivery. The handlers only log; the exchange then fails and the file consumer moves the file to `failed/` (`MoveFailed`), a delivered one to `sent/` (`MoveTo`). |

The node sends to **its own** receiver: the partner is ourselves, with our certificate, so the demo runs with
nothing else installed. Every step is real (XML Signature over the attachments, AES-128-GCM, RSA-OAEP, gzip, the
receipt's non-repudiation digests); only the other party is missing.

To watch the failure path, send to a closed port:

```powershell
$env:AS4_DEMO_PARTNER_URL = "http://127.0.0.1:4099/as4/in"; dotnet run --project As4Worker
# Retries exhausted for HttpRequestException after 3 attempts
# AS4 not delivered, partner unreachable: invoice-....xml (...)      -> the file is in as4-demo/failed/
```

## Debug (no Tsak)

```bash
dotnet run --project As4Worker
```

On first start the host creates `as4-demo/` next to the exe (`certs/node.pfx` a self-signed demo certificate,
`outbox/`, `sent/`, `failed/`, `inbox/`) and drops a sample `invoice-HHmmss.xml` into `outbox/`. Within a second:

```
AS4 received from demo-partner: invoice-001808.xml (613355fa-...@as4.redb-demo.example)
AS4 sent invoice-001808.xml: message 613355fa-...@as4.redb-demo.example, receipt 5e825a25-...
```

Drop any file into `as4-demo/outbox`; it arrives in `as4-demo/inbox` under its own name. A folder of your choice:
`dotnet run --project As4Worker -- D:\as4-demo`.

## From the demo to a real partner

In `InitRoute.cs`:

1. **Our node**: `OurPartyId` (the id the network assigned, e.g. a PEPPOL participant id), `ExternalHostName`, and our
   certificate from our PKI instead of the self-signed one (`certs/node.pfx`, password `InitRoute.PfxPassword`).
2. **The partner**: its `PartyId`, the `Service` and `Action` you agreed on, and its certificates
   (`PartnerSigningCertificates`, `PartnerEncryptionCertificate`).
3. **The send URI**: the partner's access point, `AS4_DEMO_PARTNER_URL` (default: our own receiver).
4. **The duplicate store**: a redb or SQL `IIdempotentRepository` instead of the in-memory one, so ids survive a
   restart and are shared across nodes.

The options and what the connector refuses are in the [connector README](../../redb.Route.As4/README.md).

## Build the .tpkg and deploy to a worker

```bash
dotnet build As4Module -c Debug
# -> As4Module/output/As4Module.tpkg  (inside: As4Module.dll + manifest.json + As4Module.config.json)
```

Copy the `.tpkg` into the Tsak worker's `modules/` folder, set `AS4_DEMO_DIR` for the worker (the folder with
`certs/node.pfx`, `outbox/`, `inbox/`), and the worker picks the module up by hot-reload. The `.tpkg` carries only
the module DLL: `redb.Route.As4` and `redb.Route.File` come from the worker's shared libraries. `redb.Route.As4` is
not on NuGet yet, so the module builds against the sources; switch the project references to packages once it is
published.
