# redb.Route.As4

**AS4 B2B transport for the [redb.Route](../redb.Route) ESB framework.** OASIS ebMS 3.0 with the AS4
profile, following the **eDelivery AS4 1.16 common profile** (28.01.2026) — the message exchange used by
EU eDelivery access points, e-invoicing networks, energy and customs gateways. Business documents travel
as SOAP 1.2 messages with attachments; every message is signed, its payloads compressed and encrypted,
and the receiver answers in the same HTTP response with a signed **non-repudiation receipt**.

Schemes: `as4` (HTTP), `as4s` (HTTPS).

- **Send** (`.To(...)`) — gzip → encrypt → sign a payload, POST it to the partner's access point, verify the
  receipt it answers with (signature and non-repudiation digests).
- **Receive** (`.From(...)`) — host an AS4 access point: decrypt, verify, decompress, match the message to an
  agreement, hand the document to your route, and answer with a receipt or an ebMS error **after** the
  route's unit of work.
- **One-Way/Push** and **Two-Way/Push-and-Push** (the reply leg with `eb:RefToMessageId`), both required by
  the profile.
- **Duplicate detection** on receive and **redelivery with the same message id** on send, built from the
  engine's `IIdempotentRepository` and `OnException` — no store or timer of the connector's own.

> **Status:** interop is proven live against Holodeck B2B and Domibus in both directions, and the review of
> 2026-09-28 is closed (see [Status](#status--maturity)). The design, the phase plan and the review live in
> `../../docs/as4`.

---

## Install & register

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteAs4();
    route.AddRouteBuilder<MyRoutes>();
});
```

`AddRedbRouteAs4()` registers the `as4` / `as4s` schemes. The receive side runs on the Kestrel host shared
with every other HTTP-based connector in the process (`redb.Route.Http.Hosting`), so an HTTP, SOAP, AS2 and
AS4 route in one worker never fight over a port.

---

## Concepts

| AS4 term | Here | Where it lives |
|---|---|---|
| Our access point (identity + keys) | **node** — `As4ConnectionFactory` | route context registry, by name |
| P-Mode / agreement with one partner | **partner** — `As4Partner` | the node's `Partners` list, by `Name` |
| Partner's access point URL | the **send URI** | `As4.Send("https://...")` |
| Our receive URL | the **receive URI** | `As4.Receive("/as4/in")` — one URL for all partners |
| Business document | exchange **body** | `byte[]` + `Message.ContentType` |
| `eb:Messaging` header values | exchange **headers** `redbAs4.*` | see [Headers](#headers) |

Certificates and passwords never appear in a URI: the URI is the route key and ends up in logs, spans and the
dashboard. A URI names the node (`connectionFactory=`) and, when sending, the partner (`partner=`).

---

## Node and partners

```csharp
context.AddToRegistry("node", new As4ConnectionFactory
{
    OurPartyId = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:us",
    ExternalHostName = "ap.us.example",        // host part of our message ids
    SigningCertificate = ourPfx,               // private key: signs messages and receipts
    DecryptionCertificates = { ourPfx },       // private keys: decrypt what partners encrypt for us
    Partners =
    {
        new As4Partner
        {
            Name = "acme",
            PartyId = "urn:oasis:names:tc:ebcore:partyid-type:unregistered:acme",
            Service = "urn:example:services:invoice",
            Action = "Submit",
            PartnerSigningCertificates = { acmeCertificate },   // verifies acme's signatures
            PartnerEncryptionCertificate = acmeCertificate,     // we encrypt payloads for acme
        },
    },
});
```

Certificate lists hold more than one entry while a key rotates. In Route-XML the node is a `<bean>` whose
`Partners` property is a `<list>` of nested `<bean>` or `<ref bean="…"/>` partners. A certificate is a nested bean too:

```xml
<!-- .NET 9 and later -->
<bean type="System.Security.Cryptography.X509Certificates.X509CertificateLoader, System.Security.Cryptography"
      factoryMethod="LoadPkcs12FromFile">
  <constructorArg value="{{as4.certificates}}/node.pfx"/>
  <constructorArg value="{{as4.password}}"/>
</bean>
<!-- .NET 8: the constructor, its overload named by type= -->
<bean type="System.Security.Cryptography.X509Certificates.X509Certificate2, System.Security.Cryptography">
  <constructorArg type="System.String" value="{{as4.certificates}}/node.pfx"/>
  <constructorArg type="System.String" value="{{as4.password}}"/>
</bean>
```

The duplicate store of a receive endpoint is a bean as well: `<bean name="as4-in" type="…"/>` of any
`IIdempotentRepository`, or `<redb><idempotentRepository name="as4-in"/>` for the redb-backed one.

The node and its partners are resolved and validated **when an endpoint starts**, not when the route is
defined — the registry may be filled after the routes. A missing, mistyped or invalid node or partner stops
the route from starting, with the offending registry name in the message.

### `As4ConnectionFactory` — our node

| Property | Default | Meaning |
|---|---|---|
| `OurPartyId` | — (required) | Our `eb:PartyId`. Exactly one, as eDelivery requires. |
| `OurPartyIdType` | null | Our `eb:PartyId/@type`; omitted when null. |
| `ExternalHostName` | `redb.route` | Host part of the message ids we generate (`uuid@host`). Set a public name; the default is a fixed placeholder, never the machine name, so internal host names do not leak. |
| `SigningCertificate` | — (required) | Our certificate **with private key**; signs our messages and receipts. |
| `DecryptionCertificates` | empty (required) | Our certificates with private keys that decrypt payloads. Several while we rotate; the one a message names is used. |
| `ClientCertificate` | null | Our TLS client certificate for partners that require mutual TLS when we send. |
| `SslCertPath` / `SslCertPassword` | null | PFX our receive server presents over TLS when the endpoint URI gives none. The password is `[Sensitive]`. |
| `ClientCertificateMode` / `AllowedClientThumbprints` | `NoCertificate` / null | Mutual TLS on our receive server when the endpoint URI does not say, as the SOAP factory offers it. |
| `SslProtocols` | TLS 1.2 + 1.3 | TLS versions our sender offers; the profile allows only these two, anything else is refused (Holodeck `AllowedProtocols`, phase4, Domibus default the same). |
| `RevocationMode` | `NoCheck` | Revocation check of pinned certificates (partner signing and encryption, our signing, allow-listed TLS clients): `NoCheck`, `Online` (CRL / OCSP the certificate points at) or `Offline` (OS cache). Off by default, as Camel/CXF, Holodeck and phase4. |
| `RevocationSoftFail` | false | Accept a certificate whose revocation status cannot be determined. Default: refused, as phase4 and Domibus refuse it. |
| `Partners` | empty | The agreements of this node, one per trading partner. |

### `As4Partner` — the agreement with one partner

The partner half of an ebMS P-Mode. Signing, encryption, compression support and a signed receipt in the
HTTP response are **not options**: the profile mandates them.

| Property | Default | Meaning |
|---|---|---|
| `Name` | — (required) | Name within the node: what `partner=` refers to and the `redbAs4.partner` header on receipt. |
| `PartyId` / `PartyIdType` | — / null | The partner's `eb:PartyId` and its type. |
| `OurRole` / `PartnerRole` | ebMS `initiator` / `responder` URIs | `eb:Role` of each side. |
| `AgreementRef` / `AgreementRefType` / `PModeId` | null | `eb:AgreementRef`, its `@type` and `@pmode`. When set, a received message must carry the same reference. |
| `Service` / `ServiceType` / `Action` | — (Service, Action required) | `eb:Service` and `eb:Action` of the request leg. |
| `AllowOverrideService` / `AllowOverrideAction` | false | Whether a route may send another service / action (endpoint option or header). |
| `ReplyLeg` | null | `As4Leg { Service, ServiceType, Action }` of the reply in a Two-Way / Push-and-Push exchange. |
| `PartnerSigningCertificates` | empty (required) | Certificates that verify the partner's signatures; several while the partner rotates. |
| `PartnerEncryptionCertificate` | — (required) | Certificate we encrypt payloads for. |
| `KeyReference` | `BinarySecurityToken` | How our signature references our certificate: `BinarySecurityToken`, `IssuerSerial` or `KeyIdentifier` (all three are accepted on receipt). |
| `SignatureAlgorithm` | RSA-SHA256 | `http://www.w3.org/2001/04/xmldsig-more#rsa-sha256` |
| `DigestAlgorithm` | SHA-256 | `http://www.w3.org/2001/04/xmlenc#sha256` |
| `DataEncryptionAlgorithm` | AES-128-GCM | `http://www.w3.org/2009/xmlenc11#aes128-gcm` |
| `KeyTransportAlgorithm` | RSA-OAEP | `http://www.w3.org/2009/xmlenc11#rsa-oaep` |
| `MaskGenerationAlgorithm` | MGF1-SHA256 | `http://www.w3.org/2009/xmlenc11#mgf1sha256` |
| `KeyTransportDigestAlgorithm` | SHA-256 | OAEP digest. |
| `TimestampTtl` | null | Lifetime of a `wsu:Timestamp` in what we sign; none by default, since Domibus' eDelivery policy (Strict layout, no IncludeTimestamp) rejects one. A received timestamp is always checked. |
| `TimestampTolerance` | 5 min | Clock skew accepted on the partner's `wsu:Timestamp`: how far ahead its `Created` may lie and how long past its `Expires` (WSS4J futureTimeToLive). |
| `TimestampTimeToLive` | 5 min | How old a received `Created` may be (WSS4J timeStampTTL). It bounds a replay whatever `Expires` says. |
| `CompressPayloads` | true | Gzip what we send. Received payloads are decompressed whenever their part properties say so. |

The algorithm properties accept exactly the eDelivery AS4 1.16 set listed above; any other URI stops the
endpoint from starting.

On receipt a message is matched to one partner the way Domibus and Holodeck match a P-Mode: it must be
addressed to our `OurPartyId`; its sender, service and action must be those of a partner's request leg or
reply leg; an `AgreementRef`, when the partner declares one, must match. Two partners that a message could
not tell apart are rejected when the node is validated.

---

## Sending (producer)

```csharp
using redb.Route.As4.Fluent;

From("direct://outbound")
    .SetHeader(As4Headers.OriginalSender, "urn:example:c1")
    .SetHeader(As4Headers.FinalRecipient, "urn:example:c4")
    .To(As4.Send("https://ap.acme.example/as4").ConnectionFactory("node").Partner("acme"));
```

The body is sent as one payload: `byte[]` as is, `string` as UTF-8, `Stream` copied (not buffered, and not
closed: the exchange owns it), anything else as its UTF-8 `ToString()`. The payload is compressed, encrypted and
written to the wire through the core stream cache, so a large one goes through a temporary file, not memory. Its media type is `Message.ContentType` (default `application/octet-stream`).
The eDelivery four-corner properties `originalSender` and `finalRecipient` are mandatory: without the two
headers the message is refused before anything is sent. Any other `redbAs4.property.<name>` header becomes
an `eb:Property`.

The producer waits for the partner's HTTP response and verifies the receipt: signed by one of the partner's
certificates, naming exactly the digests we signed. The outcome is on `exchange.Out` (InOut):
`redbAs4.receiptValid = true`, `redbAs4.receiptMessageId`, and the raw signal as the body.

| Outcome | Exception |
|---|---|
| The partner answers with an ebMS error | `As4ErrorSignalException` (`ErrorCode`, `Description`, `ErrorDetail`) |
| No receipt in the response, or no response within `timeout` | `As4ReceiptException` (`EBMS:0301` MissingReceipt) |
| Receipt with a bad signature or other digests | `As4ReceiptException` (`EBMS:0302` InvalidReceipt) |

### Send options

| URI option | DSL | Default | Meaning |
|---|---|---|---|
| `connectionFactory` | `.ConnectionFactory(name)` | — (required) | Registry name of the node. |
| `partner` | `.Partner(nameOrExpression)` | — (required) | Partner name; may be an expression such as `${header.partner}`. |
| `service` | `.Service(value)` | agreement | Overrides `eb:Service`; the agreement must allow it. |
| `action` | `.Action(value)` | agreement | Overrides `eb:Action`; the agreement must allow it. |
| `conversationId` | `.ConversationId(value)` | new id, or `redbAs4.conversationId` | `eb:ConversationId`. |
| `refToMessageId` | `.RefToMessageId(value)` | `redbAs4.refToMessageId`, else none | Set on the reply of a Two-Way exchange; the message then uses the partner's `ReplyLeg`. |
| `timeout` | `.Timeout(ms)` | 60000 | Milliseconds to wait for the response with the receipt; 0 = no limit. |
| `transacted` | — | — | `true` is refused when the URI is bound: eDelivery requires a synchronous receipt, so a send is never deferred to a transaction. |

`https://` in `As4.Send(...)` maps to the `as4s` scheme.

### Retries

Retries are the route's `OnException`. The producer puts `redbAs4.messageId` on the exchange before the
first attempt and keeps the signed request on the exchange: a redelivery sends **the same message, byte for byte** —
the same id and signature — so the partner detects the duplicate, and a receipt it stored for the first transmission
(Domibus does so) still names what we signed:

```csharp
OnException<As4ReceiptException>()
    .MaximumRedeliveries(5)
    .RedeliveryDelay(TimeSpan.FromSeconds(30))
    .UseExponentialBackOff()
    .Handled()
    .To("direct://as4-undelivered");          // dead letter after the last attempt
```

A `Stream` body is read from its start on every attempt when it can seek. A forward-only one (a network
stream) can be read once: to redeliver it, cache it in the route with `.StreamCaching()` before the send.

Redelivery waits in memory, and a restart loses the exchange being retried. For retry schedules of hours,
keep the message in your own outbox (a redb object with the `redbAs4.messageId` it was first sent with) and
let a `timer:` route resend what has no receipt yet — with the same id, so the partner still detects it. Such a resend
runs on a new exchange and is a new transmission (signed again): a partner that answers a duplicate with the receipt it
stored for the first transmission, as Domibus does, returns one that names the first signature, and the resend fails
with `EBMS:0302`. The partner did receive the message; the outbox can treat that answer to a resend as delivered.

---

## Receiving (consumer / access point)

```csharp
context.AddIdempotentRepository("as4-in", new InMemoryIdempotentRepository(TimeSpan.FromDays(7)));

From(As4.Receive("/as4/in").Port(4090).ConnectionFactory("node").IdempotentRepository("as4-in"))
    .Choice()
        .When(e => e.In.GetHeader<string>(As4Headers.Partner) == "acme").To("direct://acme-invoices")
    .End();
```

One URL serves every partner of the node. For each request the receiver, in order:

1. parses the envelope with the core `SafeXml` (no DTD, size-limited) — a malformed envelope or a DOCTYPE is a
   SOAP Fault `Sender` (400), an unknown header with `mustUnderstand` a `MustUnderstand` fault (500);
2. reads `eb:Messaging` strictly — a signal (receipt / error) without a waiting sender is answered 202 and
   logged, since the profile forbids asynchronous signals;
3. matches the message to one partner (`EBMS:0001` / `EBMS:0010` when none or more than one);
4. enforces the profile: unsigned or unencrypted → `EBMS:0103`; decrypts (`EBMS:0102`), verifies the
   signature and `wsu:Timestamp` (`EBMS:0101`), checks the attachments against `eb:PayloadInfo`
   (`EBMS:0007` / `EBMS:0011`), decompresses with a size limit (`EBMS:0303`);
5. claims the message id in the idempotent repository — a message received before gets its receipt again
   and never reaches the route;
6. runs the route, then answers: route failed or marked rollback-only → ebMS error (a
   `MalformedRequestException` → `EBMS:0003` with its own text, anything else → `EBMS:0004` with a reference
   to the exchange); otherwise a signed receipt with the non-repudiation digests.

The partner never reads the text of our exceptions: errors carry the code's fixed description and a
reference that finds the full story in our log.

**Body.** One payload is the body itself with its original `Message.ContentType`: `byte[]` by default, a `Stream`
with `streamBody=true` (closed by the exchange when it ends, as the File and S3 consumers hand theirs). A message
with several payloads has a list of `As4Payload` as the body, ready for `.Split()`; each `Content` is a stream the
exchange releases when it ends, so read it inside the route.

**Large messages.** The request and every payload are spooled by the core `StreamCache` (memory up to its
threshold, 128 KB by default, then a temporary file deleted on close): MimeKit reads the parts from the spooled
request, AES-GCM decrypts by stream (BouncyCastle), gzip inflates by stream with its bound. A payload reaches the
route only after its GCM tag and the signature are verified; a spool that fails a check is discarded.

### Receive options

| URI option | DSL | Default | Meaning |
|---|---|---|---|
| `connectionFactory` | `.ConnectionFactory(name)` | — (required) | Registry name of the node. |
| `idempotentRepository` | `.IdempotentRepository(name)` | — (required) | Name of an `IIdempotentRepository` registered with `context.AddIdempotentRepository`. See [Duplicate detection](#duplicate-detection). |
| `streamBody` | `.StreamBody()` | false | Hand a single payload to the route as a `Stream` instead of `byte[]`. |
| `host` | `.Host(host)` | `0.0.0.0` | Bind address. |
| `port` | `.Port(port)` | `4090` | Listen port. |
| scheme `as4s` | `.Tls(certPath?, password?)` | HTTP | Serve over HTTPS; the certificate from the URI, else the node. With neither the receiver refuses to start. |
| `clientCertificateMode` | `.ClientCertificate(mode, thumbprints?)` | the node's, else `NoCertificate` | `AllowCertificate` / `RequireCertificate` asks partners for a TLS client certificate (needs TLS; without it the receiver refuses to start). |
| `allowedClientThumbprints` | same | the node's, else any valid | Comma-separated thumbprints an accepted client certificate must match; any other is refused in the handshake. |
| `maxRequestBodySize` | `.MaxRequestBodySize(bytes)` | 100 MB | Upper bound on a request body. |
| `maxEnvelopeCharacters` | `.MaxEnvelopeCharacters(n)` | 1 M | Upper bound on the SOAP envelope; payloads are attachments and not counted. |
| `maxConcurrentRequests` / `requestQueueLimit` | `.MaxConcurrentRequests(max, queue)` | 0 (unlimited) / 0 | Admission limit per endpoint; overflow is shed before any MIME or crypto work. |
| `rejectStatusCode` / `retryAfterSeconds` | — | `503` / `1` | Answer to a shed request; an AS4 sender retries on 503. |

A receive endpoint takes no `partner`: it accepts every partner of its node.

### Duplicate detection

Required by the profile, so `idempotentRepository` is mandatory — it is the same contract the route-level
`IdempotentConsumer` and the S3 consumer use. The `eb:MessageId` is claimed only after the security checks, so
a forged message cannot take the id of a real one. The claim is confirmed when the route succeeds and
released when it fails, so the sender's resend is then delivered. A failure of the repository itself is
answered with `EBMS:0004` and the route is not run.

- **The store decides what survives.** `InMemoryIdempotentRepository` forgets on restart and does not see a
  resend that reaches another cluster node: behind a load balancer use the redb or SQL repository.
- **The duplicate window is the repository's `Ttl`.** Keep it longer than the sender's whole retry schedule.
- **One repository per receive endpoint.** Its keys are `eb:MessageId` as it is, as Domibus and Holodeck keep them:
  the id is globally unique by the ebMS contract. A repository shared with another route's business keys, or a
  partner reusing another partner's id, makes a new message look like a duplicate: it gets a receipt and is not
  delivered.
- **A resend that arrives while the first copy is still in the route also gets a receipt**, as in Holodeck
  B2B: `IIdempotentRepository` does not tell a claimed id from a confirmed one.
- **Business duplicates** (the same invoice sent as two messages) are a route concern: `.IdempotentConsumer(...)`
  on a business key.

---

## Headers

All connector headers start with `redbAs4.` (`As4Headers`).

| Header | Direction | Value |
|---|---|---|
| `messageId` | both | `eb:MessageId`. On send, set before the first attempt (or taken from the exchange). |
| `refToMessageId` | both | `eb:RefToMessageId`. |
| `timestamp` | receive | `eb:Timestamp`. |
| `conversationId` | both | `eb:ConversationId`. |
| `fromPartyId`, `fromPartyIdType`, `fromRole` | receive | Sender party. |
| `toPartyId`, `toPartyIdType`, `toRole` | receive | Recipient party. |
| `service`, `serviceType`, `action` | both | Business collaboration; on send they override the agreement when allowed. |
| `agreementRef`, `pmode` | receive | `eb:AgreementRef` and its `@pmode`. |
| `property.<name>` | both | `eb:MessageProperties`; `property.originalSender` and `property.finalRecipient` are mandatory. |
| `partner` | both | Name of the partner the message was matched to or sent to. |
| `signatureValid`, `signerThumbprint` | receive | Signature verified; thumbprint of the signing certificate. |
| `remoteAddress` | receive | Address of the peer. |
| `clientCertThumbprint`, `clientCertSubject`, `clientCertNotAfter` | receive | The TLS client certificate the partner presented (mutual TLS), as the SOAP receiver reports it. |
| `receiptValid`, `receiptMessageId` | send (on Out) | Receipt verified; its `eb:MessageId`. |

---

## Security notes

- **Own XML Signature processing.** .NET `SignedXml` cannot resolve `cid:` references to attachments, so the
  connector processes `ds:Reference` and `SignedInfo` itself (Attachment-Content-Signature-Transform,
  exclusive C14N through the public `XmlDsigExcC14NTransform`, RSA PKCS#1 v1.5).
- **Untrusted XML** goes through the core `SafeXml` only: DTDs are refused, the envelope is size-limited.
- **Signature wrapping**: duplicate `wsu:Id` values are refused, and every reference must cover what the profile
  requires (the `eb:Messaging` header, the body, every attachment).
- **Replay**: a received `wsu:Timestamp` is checked as WSS4J checks it (Domibus and Holodeck verify with it): `Created`
  required, not ahead beyond `TimestampTolerance`, not older than `TimestampTimeToLive`; `Expires` optional and not
  passed. Plus duplicate detection by message id.
- **No oracle**: decryption and signature failures carry the detail only in our log.
- **Unsigned errors to an unknown sender**: an error answered before the message is matched to a partner (broken MIME,
  no agreement) is not signed, since there is no agreement saying how; once the partner is known, errors are signed.
- **Certificate validity**: a pinned certificate is still checked for its validity period, as Domibus does by default:
  the partner's signing certificate on every received message and receipt (`EBMS:0101`), our signing certificate and
  the partner's encryption certificate before anything is sent, and an allow-listed TLS client certificate in the
  handshake (a pin narrows validation, it never replaces it). With `RevocationMode` set, revocation is checked for all
  of them; a certificate that names no CRL distribution point and no AIA passes, as in Domibus.
- **One `eb:PartyId` per side**: more is refused with `EBMS:0010`, as phase4 and Holodeck refuse it.
- **Principal**: the exchange principal is the envelope signer (`As4AuthenticationTypes.Signature`: the partner name,
  the certificate subject and thumbprint), then the TLS client certificate (`As4AuthenticationTypes.ClientCertificate`),
  then what the host's principal resolver adds, as camel-cxf with WSS4J takes the principal from the signature.
- **Attachment canonicalization**: an uncompressed attachment is signed over its MIME canonical form (SwA 1.1
  §5.4.2, the WSS4J rules): Exclusive C14N for XML types, CRLF line endings for other `text/*`.
- **Four-corner properties** are required on receipt too: a message without `originalSender` or `finalRecipient`
  is answered `EBMS:0010`, as Domibus answers it.
- Passwords are `[Sensitive]` and redacted in logs and the dashboard.

---

## What we refuse

The connector implements the eDelivery AS4 1.16 common profile and nothing wider. Each line below is a wall a partner
outside the profile hits; the answer is the one it gets.

| What | Accepted | Anything else |
|---|---|---|
| Signature | RSA-SHA256 | the partner's configuration is refused at start; a received message: `EBMS:0101` |
| Digest | SHA-256 | as above |
| Payload encryption | AES-128-GCM | refused at start; a received message: `EBMS:0102` (AES-256-GCM, AES-128-CBC) |
| Key transport | RSA-OAEP with MGF1-SHA256 and SHA-256 | refused at start; a received message: `EBMS:0102` (`rsa-oaep-mgf1p` of XML Encryption 1.0) |
| Canonicalization | Exclusive C14N | `EBMS:0101` |
| Reference transforms | exactly one `ds:Transform` per reference | `EBMS:0101` |
| Signature coverage | `eb:Messaging`, `soap:Body`, every attachment, the `wsu:Timestamp` when present | `EBMS:0101` |
| Unsigned or unencrypted message | never | `EBMS:0103` |
| Payload in the SOAP body | never (payloads are attachments) | `EBMS:0002` |
| Compression | gzip, with `MimeType` | `EBMS:0303` / `EBMS:0003` |
| `eb:PartyId` per side | exactly one | `EBMS:0010` |
| Four-corner properties | `originalSender` and `finalRecipient` present | `EBMS:0010` |
| MEP | one-way and two-way push, on the default MPC | a pull request: `EBMS:0010` (as Domibus without a pull process); another `mpc`: `EBMS:0001` (as Domibus); an unsolicited receipt or error: 202, logged |

---

## What it was tested with

**Automated suite** (`redb.Route/tests/redb.Route.Tests.As4`; last full run 2026-09-28, all green on **net8.0,
net9.0 and net10.0**; the live tests below skip when their stand is down unless `REDB_AS4_INTEROP=1`):

| Area | What is covered |
|---|---|
| Endpoint model | URIs and DSL, option validation, node and partner validation, ambiguous agreements, start-time failures. |
| Security | Sign / verify and encrypt / decrypt round trips for all three key references; tampering, a stranger's key, an expired timestamp, duplicate `wsu:Id`, an uncovered attachment. |
| Messaging | Strict `eb:Messaging` reader and writer, SwA MIME, payload consistency, gzip with a size limit. |
| Producer | Against a loopback partner that decrypts, verifies and answers: verified receipt, ebMS error, no receipt, no response within the timeout, receipt with other digests, receipt signed by a stranger, missing four-corner properties, reply leg; route-level redelivery with the same message id and dead letter after the last attempt; a forward-only `Stream` body sent and left open, a seekable one resent from its start. |
| Consumer | Loopback (our sender → our receiver) and raw requests: every refusal answered with its ebMS code or SOAP fault, exception text never sent, rollback-only never acknowledged, duplicates acknowledged without a second delivery, a resend after a route failure or an aborted request delivered, repository failure, a claim taken uncancelled; `streamBody`, a large payload spooled to disk, several payloads as released streams; an envelope without `Body` or `Header`, two `wsse:Security` headers, an unexpected failure answered `EBMS:0004`, `mustUnderstand` only for the roles we play. |
| Hardening | `wsu:Timestamp` by the WSS4J rules (no `Created`, too old, from the future, expired), a partner response over `maxResponseBodySize`, options of the other side refused (`[EndpointRole]`), a partner address with a query refused. |
| Engine | Admission limits (503, counted once as rejected), the shared host of a module host (container, context service, an HTTP and an AS4 route on one port), the drain on stop, statistics once per event (in, error, refusal, out), one trace across sender and receiver, `.Transacted()` commit and rollback, principal from the signer and the TLS client, a node and route from Route-XML. |
| Transport security | Real loopback TLS handshakes: a TLS receiver without a certificate refuses to start, one with a certificate leaves no plaintext port, the certificate from the node, mutual TLS accepting an allow-listed client certificate (and reporting it on the exchange), refusing a missing or foreign one in the handshake. |
| Profile | MIME canonical form of uncompressed attachments (XML, text, binary, a CRLF split across reads, a DTD refused), four-corner properties required on receipt, signer outside its validity period, a stranger with the partner's subject, expired own or partner certificates refused before sending. |

**Live interop with Holodeck B2B 8.1.1** (Docker stand `C:\Work\yaml\as4`, runs of 2026-09-25 to 2026-09-28):

| Direction | Scenario | Result |
|---|---|---|
| Holodeck → Holodeck | Reference capture of a signed, encrypted, gzipped push | our reader verifies Holodeck's signature and digests and decrypts AES-128-GCM |
| redb → Holodeck | Push signed + encrypted + compressed, each of `BinarySecurityToken`, `IssuerSerial`, `KeyIdentifier` | Holodeck delivers the payload and answers with a receipt whose non-repudiation digests match ours |
| Holodeck → redb | Holodeck pushes to our receiver | the route gets the document; Holodeck accepts our signed receipt |
| redb → Holodeck | Uncompressed `text/xml`, `application/xml`, `text/plain` whose raw and canonical bytes differ | Holodeck verifies our signature over the canonical form and answers with a receipt |

**Live interop with Harmony AP 2.6.2** (NIIS; its MSH is **Domibus 5.1**, the eDelivery reference implementation;
stand `C:\Work\yaml\as4\harmony` on a shared server, runs of 2026-09-26):

| Direction | Scenario | Result |
|---|---|---|
| redb → Domibus | Push signed + encrypted + compressed, each of the three key references | Domibus verifies and receives it (`RECEIVED`); we verify its receipt |
| redb → Domibus | Uncompressed XML whose raw and canonical bytes differ | Domibus verifies the signature over the canonical form |
| redb → Domibus | A redelivery on the same exchange | the same bytes: Domibus answers with the receipt it stored, which we verify |
| Domibus → redb | Domibus' ebMS test message | the route gets it; Domibus accepts our receipt (`ACKNOWLEDGED`) |

Not covered live: TLS on the wire with a partner MSH (both stands run plain HTTP; TLS and mutual TLS are tested on
loopback handshakes above), and Pull and asynchronous receipts, which are out of scope.

---

## Status & maturity

| Phase | Scope | State |
|---|---|---|
| 0 | Endpoint skeleton, node and partner model | done |
| 1 | WS-Security: signature over attachments, AES-128-GCM, three key references | done, proven against Holodeck |
| 2 | ebMS model, SwA MIME, compression | done |
| 3 | Send push, verify receipt and NRR | done, live |
| 4 | Receive push, receipt or error after the unit of work | done, live |
| 5 | Duplicate detection, redelivery with the same id | done |
| streams | Spooled request and payloads, streamed AES-GCM, `streamBody` | done, live |
| 7 | Rest of the profile, TLS / mutual TLS, MIME canonicalization of `text/*`, certificate validity | done |
| 8 | Transactions, limits, statistics, telemetry, shared host, Route-XML | done by tests; live Tsak and Jaeger check with the release |
| 9 | Interop: Holodeck both ways, Domibus both ways, redelivery | done, live |
| review | Findings of 2026-09-28 (`docs/as4/REVIEW-2026-09-28.md`) R1–R10 | done |
| 11 | Release: CHANGELOG, connector roadmap, VS Code catalog, packaging | in progress |

Asynchronous receipts (phase 6) and Pull (phase 10) are optional in the profile and out of scope for now.

---

## Cross-cutting

Like every redb.Route connector, AS4 endpoints get statistics and health (`IEndpointStatistics`, visible in
the Tsak dashboard) and distributed tracing on the core's transport contract: the receiver opens a `"{path} receive"`
Consumer span (parent: the host's request span when the application traces ASP.NET Core, else the sender's
`traceparent`, else a root; the sender's baggage is put back), the sender a `"{partner} send"` Client span whose
context it writes to the request. A refused message, a failed route, an unreachable partner or a missing or invalid
receipt marks the span an error; `EnableTelemetry=false` opens neither. The receiver drains in-flight requests on
shutdown in the order the HTTP family uses (unregister → drain → release the listener).

Part of the redb.Route connector family.
