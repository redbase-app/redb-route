# redb.Route.As2 — Testing & Interop Validation

How the AS2 connector is tested, and the proof it interoperates with a real, independent AS2 implementation.

Test project: `redb.Route/tests/redb.Route.Tests.As2`: **141 test cases per target framework, green on net8.0 / net9.0 / net10.0** (2026-09-28, OpenAS2 stand up; without it the four interop cases are reported skipped).

---

## 1. Test layers

The connector is validated at three levels, each catching a different class of bug.

### Unit — crypto, MIC, options, DSL

Pure, fast, no network. Self-signed certificates are generated in-process (no PFX files in the repo).

- **S/MIME round-trips** for every profile — plain, signed, encrypted, signed+encrypted,
  compressed+signed+encrypted — asserting the decrypted payload is byte-identical.
- **MIC** (RFC 4130 §7.3.1) — deterministic; the MIC computed before signing equals the MIC recomputed on
  the received signed part; headers-included vs content-only differ; `Received-Content-MIC` wire format parses.
- **Negative** — a signature from the wrong certificate fails verification (not throws).
- **Algorithm matrix** — a `[Theory]` over `{sha-1, sha-256, sha-384, sha-512}` × `{aes-128/192/256-cbc, 3des}`
  × `{compress on/off}`, each a full round-trip with MIC verification.
- **Options / DSL** — URI binding, fail-fast validation (bad port, async without a target, unsupported
  algorithm), `[Sensitive]` redaction, and DSL URI building (the receive path is never truncated).

### Security and protocol (REVIEW-2026-09-28)

Each finding of the review has a test written to fail first: a receipt-URL **trap** (a local listener that must
receive nothing) for a stranger's signature, a sync agreement, and a host not allowed; foreign `AS2-From`/`AS2-To`;
MDNs without MIC, without `Original-Message-ID`, for unknown messages, unsigned where signed is agreed; the MDN only
when asked, signed only when asked, with the requested micalg; credentials not handed on; 413 over the size limits;
the stop drain and span of the MDN receiver; the agreement checked at start; legacy algorithms; expired and
not-yet-valid certificates; duplicates. Files: `As2ReceiveSecurityTests`, `As2MdnVerdictTests`, `As2LimitsTests`,
`As2AgreementTests`, `As2ProtocolTests`, `As2DuplicateTests`.

### End-to-end — loopback over a live Kestrel

The connector's **own producer sends a real signed+encrypted AS2 message over real HTTP** to the connector's
**own consumer** (hosted on the shared Kestrel server on a free localhost port), which decrypts, verifies,
delivers the payload to a route, and returns an MDN. These exercise both sides together:

- **Delivery** — the business payload arrives at the receiving route intact.
- **Synchronous MDN** — the response is a `multipart/report` with `Disposition: processed`,
  `Received-Content-MIC` and the echoed `Original-Message-ID`.
- **Signed MDN** — the producer parses the signed MDN, verifies its signature and confirms `mdnMicMatch`.
- **Asynchronous MDN** — producer (async) → consumer acks 200 and POSTs the MDN to a second endpoint
  (`As2.ReceiveMdn`), which correlates it by `Original-Message-ID` and delivers the outcome to a route.
- **Cross-cutting** — `IEndpointStatistics.MessagesIn` increments; an `ActivityListener` sees the producer
  `Client` span and the consumer `Consumer` span sharing one `TraceId` (distributed trace linked).

These loopback tests have already earned their keep — they caught a real `Content-Type` double-prefix bug
(MimeKit's `ContentType.ToString()` renders the whole header line) that unit tests would not have.

### Interop — against a real external AS2 server

Loopback proves *self-consistency*. Only exchanging with an **independent** implementation proves *interop
correctness* — that our wire format and MIC are accepted by software we didn't write. See §2.

---

## 2. Interop result — validated against OpenAS2 v4.9.0 ✅ (both directions)

Partner: `greicodex/openas2:latest` = OpenAS2 **v4.9.0** (a mature, Bouncy-Castle-based AS2 server) in Docker.
Both messages are **signed (SHA-256) + encrypted (AES-128-CBC)** with a **signed MDN**.

### redb → OpenAS2

Our producer POSTs to OpenAS2. Confirmed from OpenAS2's own logs:

```
AS2ReceiverHandler  - received 4344 bytes ... [<...@redb.route>]
MessageFileModule   - stored message to .../redb-openas2/inbox/...@redb.route
MDNSenderModule     - sent MDN [automatic-action/mdn-sent-automatically; processed] [<...@redb.route>]
```

OpenAS2 **decrypted** our message, **verified our signature**, **stored** the business document, and returned
a **positive signed MDN**. Our `MdnParser` verified that MDN's signature and confirmed the
`Received-Content-MIC` **matched** what we sent.

### OpenAS2 → redb

OpenAS2's directory poller builds a signed+encrypted message and POSTs it to our consumer. Confirmed:

```
AS2SenderModule     - Connecting to: http://host.docker.internal:15081/inbound [<...openas2_redb...>]
AS2MDNReceiverHandler - DISPOSITION MIC ALG: sha-256   IMPORTANCE: optional
AS2Util             - Pending MDN MSG FILE deleted ...   (our MDN correlated OpenAS2's pending message)
```

Our consumer **decrypted** OpenAS2's message with our private key, **verified OpenAS2's signature**, delivered
the EDI payload to the route, and returned a **signed MDN that OpenAS2 accepted** (pending MDN cleared).

**Correction (2026-09-28).** OpenAS2 4.9.0 does **not** compare the MIC of a synchronous MDN (no `MIC check` in its
trace log), so the paragraph above never proved our received-side MIC. The asynchronous path does compare it, and the
first asynchronous run failed with `MIC not matched`: the receiver prepared the received signed part for 7bit before
hashing it, turning OpenAS2's `binary` part into base64. Fixed (`ComputeReceivedMic` hashes the part as received);
the asynchronous interop below is the proof of the MIC in this direction.

### Asynchronous MDN, both directions

- **redb → OpenAS2** (`Redb_To_OpenAs2_AsyncMdn_IsPostedBack_Signed_AndConfirms`): we send with
  `Receipt-Delivery-Option` pointing at our `As2.ReceiveMdn` endpoint (`host.docker.internal:<port>`); OpenAS2 answers
  200 and logs `sent AsyncMDN ... OK`; our receiver verifies its signature and MIC: `mdnConfirmed = true`.
- **OpenAS2 → redb** (`OpenAs2_To_Redb_AsyncMdn_IsPostedToOpenAs2_AndItClosesThePendingMessage`): partnership
  `openas2-to-redb-async` (partner `redb-async`, `as2_receipt_option = http://127.0.0.1:14081`); our receiver answers
  200 and posts the signed MDN to OpenAS2's MDN receiver, which logs `MIC check` and deletes its pending message.

The asynchronous runs found three defects, each fixed with a test that failed first:

1. The machine-readable MDN part went out quoted-printable when a field line was long (OpenAS2 Message-IDs are about
   90 characters); RFC 3798 §3.1 requires 7bit, and OpenAS2 read the Original-Message-ID undecoded, cut at the soft
   line break (`As2MdnEncodingTests`).
2. The received MIC above (`As2ReceivedMicTests` and the interop).
3. The producer registered the wait for an asynchronous MDN after the partner's 200; OpenAS2 often posts the MDN
   first, which then read as `unknown` (`As2AsyncMdnOrderTests`).

This closes the "hard part" of AS2 in **both directions and both MDN modes**: MIC, S/MIME structure and MDN
handling are correct against a real, independent partner, not merely self-consistent.

### Running the interop test

The harness lives outside the repo at `C:\Work\yaml\as2` (Docker compose + OpenAS2 config + generated certs;
see its `README.md`). The interop test is `As2InteropTests` (`[Trait("Category", "Interop")]`) and is
**gated** by `[As2InteropFact]`: without OpenAS2 on `127.0.0.1:14080` the cases are reported **skipped**, with the
reason, never passed; `REDB_AS2_INTEROP=1` turns a missing stand into a failure. The reverse case listens on the
fixed port the stand posts to (15081), so the three target frameworks take turns on it through a lock file in the
temp directory, and each waits for its own uniquely named document.

```bash
cd C:\Work\yaml\as2
docker compose up -d
dotnet test redb.Route/tests/redb.Route.Tests.As2 --filter Category=Interop
```

Certificate paths come from the `AS2_INTEROP_CERTS` environment variable (default `C:\Work\yaml\as2\certs`).

### Configuration notes learned from OpenAS2 4.9.0

Captured so the harness reproduces cleanly:

- Partnerships live in a **separate `partnerships.xml`** (`XMLPartnershipFactory`) referenced by
  `config.xml`'s `partnership_file` property — **not** inline in `config.xml`.
- The whole `config_template/` must be present (`config.xml` references `commands.xml`, `messages.xml`, …).
  The harness is seeded from the image's own template, then `partnerships.xml` and `as2_certs.p12` are replaced.
- The AS2 receiver listens on **10080** inside the container (async-MDN on 10081); the default keystore
  password is `testas2`. The keystore holds our partner's key (`openas2`) plus our certificate (`redb`) as a
  trusted entry.

---

The reverse direction is driven by `As2InteropTests.OpenAs2_To_Redb_ReceivesSignedEncrypted`: it starts a redb
consumer on host port 15081, drops an EDI file into OpenAS2's outbox for partner `redb`, and waits for the
poller to deliver it. The harness's compose maps `host.docker.internal` via `extra_hosts: host-gateway` so the
Linux container can reach the host consumer, and the `openas2-to-redb` partnership carries a `subject`
attribute (required by the OpenAS2 poller).

## 3. Future work

- **Additional real implementations** — the loopback matrix covers the algorithm space; broadening interop
  coverage to Mendelson and partner gateways is future work.
- ~~Async MDN against OpenAS2~~ — done 2026-09-28, both directions (above).
