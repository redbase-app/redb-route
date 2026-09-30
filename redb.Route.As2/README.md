# redb.Route.As2

**AS2 (RFC 4130) B2B/EDI transport for the [redb.Route](../redb.Route) integration framework.** Exchange business
documents with trading partners over HTTP(S) as signed and encrypted S/MIME messages, confirmed by MDN receipts:
the protocol retail, logistics and EDI networks run on.

Schemes: `as2` (HTTP), `as2s` (HTTPS).

- **Send** (`.To(...)`): compress, sign and encrypt a payload, POST it to the partner, verify the MDN.
- **Receive** (`.From(...)`): host an AS2 server that checks who sent the message, decrypts and verifies it, hands the
  document to your route and answers with the MDN the sender asked for.
- **Asynchronous MDN** in both roles, **signed MDN**, **duplicate detection**, bounded request and response sizes.

Crypto is **MimeKit** (Bouncy Castle underneath), the foundation Apache camel-as2, OpenAS2 and Mendelson interoperate
on. Interop is tested in both directions against a live **OpenAS2 4.9.0** (see [TESTING.md](TESTING.md)).

---

## Install and register

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteAs2();
    route.AddRouteBuilder<MyRoutes>();
});
```

`AddRedbRouteAs2()` registers the `as2` and `as2s` schemes and shares one Kestrel receive server with every other
HTTP-based connector in the process (`redb.Route.Http.Hosting`): an HTTP route and an AS2 route on one port do not
fight over it.

---

## The agreement: `As2ConnectionFactory`

A trading partnership is certificates, AS2 identifiers and an agreed profile. Register it once by name; endpoints
name it with `.ConnectionFactory("name")`, so no certificate or password lives in a URI.

```csharp
context.AddToRegistry("walmart", new As2ConnectionFactory
{
    OurCertificate     = ourPfx,       // our certificate WITH its private key: signs what we send, decrypts what we receive
    PartnerCertificate = theirCer,     // the partner's public certificate: encrypts for it, verifies its signatures
    As2From = "OUR-AS2-ID",            // us: AS2-From on send, the AS2-To we accept on receive
    As2To   = "WALMART-AS2-ID",        // them: AS2-To on send, the AS2-From we accept on receive
    PartnerUrl = "https://partner.example.com/as2",

    Sign = true, Encrypt = true, Compress = false,
    SignAlg = "sha-256", EncryptAlg = "aes-128-cbc",
    MdnMode = As2MdnMode.Sync, SignedMdn = true, RequireValidMdn = true,
});
```

| Property | Default | Meaning |
|---|---|---|
| `OurCertificate` | none | Our S/MIME certificate with its private key. Required when anything is signed or encrypted, or a signed MDN is agreed. |
| `PartnerCertificate` | none | The partner's certificate. Required in the same cases. It is **pinned**: a signature is accepted only when made by this certificate, not merely by a valid one. |
| `As2From` / `As2To` | required | Our and the partner's AS2 identifiers (RFC 4130 §6.2). |
| `PartnerUrl` | from the send URI | Where the producer POSTs. If both the factory and the send URI name one, they must be the same. |
| `Sign` / `Encrypt` / `Compress` | `true` / `true` / `false` | What we do on send, and what we require on receive (an unsigned or unencrypted message where the agreement requires it is refused). |
| `SignAlg` | `sha-256` | Signature digest, and the MIC algorithm. |
| `EncryptAlg` | `aes-128-cbc` | Content encryption. |
| `AllowLegacyAlgorithms` | `false` | Permits `sha-1` and `3des`. Without it they are refused when the endpoint starts. |
| `MdnMode` | `Sync` | Send: which MDN we ask for. Receive: `Async` permits posting receipts to the address the sender names, `None` never sends an MDN. |
| `SignedMdn` | `true` | Send: ask for a signed MDN (as `required`), and do not count an unsigned one as a confirmation. |
| `RequireValidMdn` | `false` | Send: a transfer the MDN does not confirm fails the exchange (sync) or the receipt is refused (async, see below). |
| `AsyncMdnUrl` | none | Send, async: the URL the partner posts our receipt to (your `As2.ReceiveMdn` endpoint). |
| `AsyncMdnAllowedHosts` | empty | Receive, async: the hosts a receipt may be posted to. Required when a receive endpoint's `MdnMode` is `Async`. |
| `SslCertPath` / `SslCertPassword` | none | The TLS certificate of the receive server (not the S/MIME key). The password is `[Sensitive]`. |

**Checked at start.** Every endpoint that names a factory checks it when it starts (`As2ConnectionFactory.Validate`):
identifiers present, algorithms supported and not legacy unless allowed, our certificate present with its private key
and the partner's certificate present wherever something is signed or encrypted, URLs absolute `http(s)`. A send
endpoint also needs a partner URL, and `AsyncMdnUrl` for async; an async receive endpoint needs `AsyncMdnAllowedHosts`.
A broken agreement stops the route from starting; it never answers a partner's first message with an error.

**One source.** When an endpoint names a factory, the factory is the whole agreement: agreement options on the URI
(`sign`, `signAlg`, `mdnMode`, `as2From`, `asyncMdnAllowedHosts`, ...) are refused, naming the parameters. Without a
factory the inline URI options are the agreement; they carry no certificates, so that is only for an agreement that
neither signs nor encrypts.

---

## Sending (producer)

```csharp
From("direct://outbound")
    .To(As2.Send("https://partner.example.com/as2").ConnectionFactory("walmart"));
```

The producer compresses (if agreed), signs, encrypts and POSTs the document, with `AS2-From`, `AS2-To`, a fresh
`Message-ID` and, unless `MdnMode` is `None`, `Disposition-Notification-To`. With `SignedMdn` it asks for
`signed-receipt-protocol=required, pkcs7-signature; signed-receipt-micalg=required, <SignAlg>`. Our and the partner's
certificates must be within their validity period, or the send fails before anything is posted.

The sent `Message-ID` and MIC are on `exchange.In` (`Message-ID`, `redbAs2.mic`, `redbAs2.micalg`). A synchronous MDN
is read (at most `maxResponseBodySize` bytes), its signature verified against the pinned partner certificate, and its
`Received-Content-MIC` compared with ours. The verdict lands on `exchange.Out`:

| Header on `exchange.Out` | Meaning |
|---|---|
| `redbAs2.mdnConfirmed` | `true` only when the disposition is positive, the MIC matched, and the signature is valid where `SignedMdn` is agreed. **The one to branch on.** |
| `redbAs2.mdnMicStatus` | `matched`, `mismatch`, `absent` (the MDN carries no MIC) or `unknown` (nothing to compare with) |
| `redbAs2.mdnMicMatch` | `true` only for `matched` |
| `redbAs2.signatureValid` | the MDN was signed and the signature verified |
| `redbAs2.mdnDisposition` | the raw `Disposition`, e.g. `automatic-action/MDN-sent-automatically; processed` |

```csharp
From("direct://outbound")
    .To(As2.Send("https://partner/as2").ConnectionFactory("walmart"))
    .Choice()
        .When(e => e.Out!.GetHeader<bool>(As2Headers.MdnConfirmed))
            .Log("delivered and verified")
        .Otherwise()
            .To("direct://delivery-alert");
```

Without `RequireValidMdn` an unconfirmed MDN is logged and left to the route; with it, the exchange fails and the
route's error handling takes over. A missing MIC is never a match (RFC 4130 requires one in every MDN).

Exchange headers are bridged onto the request, except redb metadata (`redbAs2.*`), the AS2 and MIME headers the
connector sets itself, hop-by-hop headers, `Host`, and credentials (`Authorization`, `Proxy-Authorization`,
`Cookie`, `Set-Cookie`): a token from an inbound caller or an internal service never reaches the partner.

---

## Receiving (consumer / AS2 server)

```csharp
From(As2.Receive("/inbound/orders").Host("0.0.0.0").Port(4080).ConnectionFactory("walmart"))
    .Unmarshal(...)                 // your EDI parsing
    .To("direct://process-order");
```

What happens to a request, in order:

1. **Size.** A body over `maxRequestBodySize` (100 MB by default) is answered `413` before it is read.
2. **Who.** `AS2-From` must be the partner's identifier and `AS2-To` ours (case-sensitive, a quoted-string is
   unquoted, RFC 4130 §6.2).
3. **Decrypt** with `OurCertificate`, **verify** the signature against the pinned `PartnerCertificate` (which must be
   within its validity period), **decompress**.
4. **Policy.** A message not signed or not encrypted where the agreement requires it is refused.
5. **Duplicates** (with `idempotentRepository`): a `Message-ID` processed before is answered and not delivered.
6. **Route.** The decrypted document is the body; its content type is `Message.ContentType` (e.g. `application/edi-x12`).
7. **MDN**, as the request asked (below).

A refused message never reaches the route. Its MDN carries the RFC 4130 §7.4.3 code (`authentication-failed`,
`decryption-failed`, `decompression-failed`, `insufficient-message-security`, `unexpected-processing-error`) and a
fixed text with a reference (the request's trace identifier); the detail is in our log under that reference, never in
the MDN.

| Header on the received exchange | Meaning |
|---|---|
| `redbAs2.signatureValid` | `true` only when the message was signed and the signature verified; `false` for an unsigned one |
| `redbAs2.mic` / `redbAs2.micalg` | the MIC returned in the MDN, and its algorithm |
| `redbAs2.remoteAddress` | the sender's IP |
| `redbAs2.partner` | the connection-factory name |

The request's own headers (`AS2-From`, `AS2-To`, `Message-ID`, `Subject`, `Disposition-Notification-*`, your partner's
business headers) are copied verbatim, except the S/MIME wrapper's MIME headers, hop-by-hop headers and the hop's
credentials (`Authorization`, `Proxy-Authorization`, `Cookie`). A principal the host resolved is on the exchange
(`ExchangePrincipal`).

### The MDN the receiver returns

- **Only when asked**: no `Disposition-Notification-To`, no MDN (the answer is an empty `200`). `MdnMode = None` never
  sends one.
- **Signed when asked**: when `Disposition-Notification-Options` asks for `pkcs7-signature`, signed with
  `OurCertificate`.
- **With the requested MIC algorithm**: the first `signed-receipt-micalg` usable here (legacy only when allowed) is the
  algorithm of the MIC and of the MDN signature; if none is, the agreement's `SignAlg`, with a warning in the log.
- **Synchronous** in the response, unless the request names a `Receipt-Delivery-Option` and all of these hold, in
  which case it is posted there: the message **authenticated** (identifiers matched and, where signing is agreed, the
  signature verified), the agreement's `MdnMode` is `Async`, and the URL is absolute `http(s)`, without user
  information, to a host in `AsyncMdnAllowedHosts`. Anything else is logged and the MDN goes in the response: an
  unauthenticated caller cannot make us POST, least of all a receipt signed by our key, to an address of its choosing.
  The asynchronous MDN is posted after the `200`, outside the request, bounded by `timeout`, counted by the stop drain.

### Duplicates

A partner that did not see our answer sends the document again with the same `Message-ID`. Name an
`IIdempotentRepository` and the resend is answered with a positive MDN,
`processed/warning: duplicate-document`, and the same MIC, and is not delivered again:

```csharp
context.AddIdempotentRepository("as2-walmart", new InMemoryIdempotentRepository());   // or a durable one
From(As2.Receive("/inbound/orders").Port(4080).ConnectionFactory("walmart").IdempotentRepository("as2-walmart"))
```

The id is claimed only after the message authenticated (a forged copy cannot burn the id of the real one) and released
when the route fails, so the partner's resend is then delivered. The repository decides whether ids survive a restart
and are shared across nodes; give the endpoint one of its own. Without a repository every copy is delivered: the route
can still use `.IdempotentConsumer(e => e.In.GetHeader<string>(As2Headers.MessageId))`.

### Receiving over TLS

Two certificates, not interchangeable: `OurCertificate` is the S/MIME key of the **message**; this one secures the
**connection**.

```csharp
From(As2.Receive("/inbound/orders").Host("0.0.0.0").Port(4443)
        .Tls("/certs/as2-server.pfx", "password")
        .ConnectionFactory("walmart"))

// or keep the password in the registry
context.AddToRegistry("walmart", new As2ConnectionFactory { /* ... */ SslCertPath = "/certs/as2-server.pfx", SslCertPassword = secret });
From(As2.Receive("/inbound/orders").Port(4443).Tls().ConnectionFactory("walmart"))
```

The certificate may also come from the host (`AddRedbRouteHttpHosting(o => o.Tls.DefaultCertificatePath = ...)`). A
TLS receiver that finds none **refuses to bind**; it never opens a plaintext port behind an `https://` URL.

---

## Asynchronous MDN

```csharp
// Send side of the agreement
MdnMode = As2MdnMode.Async,
AsyncMdnUrl = "https://our-host:4081/as2/mdn",

// Routes
From("direct://outbound")
    .To(As2.Send("https://partner/as2").ConnectionFactory("walmart"));

From(As2.ReceiveMdn("/as2/mdn").Host("0.0.0.0").Port(4081).ConnectionFactory("walmart"))
    .Process(e =>
    {
        var original  = e.In.GetHeader<string>(As2Headers.MessageId);      // Original-Message-ID
        var confirmed = e.In.GetHeader<bool>(As2Headers.MdnConfirmed);
    });
```

The producer records the sent `Message-ID` and MIC and returns; the verdict arrives later as its own exchange on the
`ReceiveMdn` route, with the same headers as a synchronous MDN (`mdnConfirmed`, `mdnMicStatus`, `mdnMicMatch`,
`signatureValid`, `mdnDisposition`) plus `remoteAddress` and `partner`.

- An MDN for a message nobody waits for (no `Original-Message-ID`, sent before a restart, older than the 30-minute
  correlation window, or sent from another node: the record is in memory) has `mdnMicStatus = unknown` and is not a
  confirmation.
- Where `SignedMdn` is agreed, an unsigned or foreign-signed MDN is not a confirmation and does not end the wait, so
  the partner's genuine receipt still finds the message. With `RequireValidMdn` as well, it is refused (`400`) and not
  delivered.
- A negative MDN from the partner is delivered, unconfirmed: the route must learn that the transfer failed.
- The receiver has the message receiver's lifecycle: stop drain, `maxConcurrentRequests`, `maxRequestBodySize`, a
  `Consumer` span.

---

## Algorithms

| Knob | Values |
|---|---|
| `SignAlg` | `sha-256` (default), `sha-384`, `sha-512`; `sha-1` **legacy**, only with `AllowLegacyAlgorithms` |
| `EncryptAlg` | `aes-128-cbc` (default), `aes-192-cbc`, `aes-256-cbc`; `3des` **legacy** (SWEET32), only with `AllowLegacyAlgorithms` |
| `Compress` | `true` / `false` (RFC 3274) |

AES-GCM is not supported. An unsupported or unallowed algorithm stops the endpoint from starting.

---

## Endpoint options

| Option | Side | Default | Meaning |
|---|---|---|---|
| `connectionFactory` | both | none | The agreement (above). |
| `host` / `port` | receive | `0.0.0.0` / `4080` | Where the receive server listens. |
| `mode` | receive | `message` | `mdn` for an asynchronous-MDN receiver (`As2.ReceiveMdn`). |
| `useTls`, `sslCertPath`, `sslCertPassword` | receive | off | TLS of the receive server; the `as2s` scheme sets `useTls`. |
| `maxRequestBodySize` | receive | 100 MB | Larger requests are answered 413 before they are read. |
| `maxConcurrentRequests`, `requestQueueLimit`, `rejectStatusCode`, `retryAfterSeconds` | receive | `0`, `0`, `429`, `1` | Admission limit, below. |
| `idempotentRepository` | receive | none | Duplicate detection, above. |
| `streamBody` | receive | `false` | The body is the spooled payload as a `Stream` (closed with the exchange) instead of `byte[]`. |
| `timeout` | both | 30000 ms | The outgoing POST: the message on send, the asynchronous MDN on receive. |
| `maxResponseBodySize` | send | 4 MB | The synchronous MDN is read up to this; larger fails the send without reading it. |

The fluent builder has the same knobs: `.Host()`, `.Port()`, `.Tls()`, `.ConnectionFactory()`,
`.MaxRequestBodySize()`, `.MaxResponseBodySize()`, `.IdempotentRepository()`, `.StreamBody()`, `.MaxConcurrentRequests()`,
`.RejectStatusCode()`, `.RetryAfterSeconds()`. The URI form:

```
as2:/inbound/orders?host=0.0.0.0&port=4080&connectionFactory=walmart      # receive server
as2:/as2/mdn?host=0.0.0.0&port=4081&mode=mdn&connectionFactory=walmart     # async-MDN receiver
as2s://partner.example.com/as2?connectionFactory=walmart                   # producer (https => as2s)
```

The receive path is kept whole (its first segment is not taken for a host). Passwords are `[Sensitive]` and redacted
from logs; an unknown parameter is refused with the nearest option name.

### Admission limit

Kestrel runs as many handlers as requests arrive. `maxConcurrentRequests` caps the concurrent pipeline executions of an
endpoint; the overflow beyond `requestQueueLimit` waiting requests is shed with `rejectStatusCode` and `Retry-After`
before any MIME or crypto work (AS2 partners retry on their own). A shed request is counted in `Rejected`, not in
`MessagesIn` or `Errors`. The limit is per endpoint; for "slow down, do not drop" use `.Threads(n)` in the route.

---

## Cross-cutting

Statistics and health (`IEndpointStatistics`, visible in the Tsak dashboard), and distributed tracing on the core's
transport contract: the producer opens a `"{partner AS2 id} send"` Client span and writes its context to the request
(replacing a `traceparent` bridged from the exchange); the message and MDN receivers open `"{path} receive"` Consumer
spans whose parent is the host's request span when the application traces ASP.NET Core, else the sender's
`traceparent`, else a root, with the sender's baggage put back. A refused message, a failed route or a failed send
marks the span an error; `EnableTelemetry=false` opens none. Stopping a receive route waits for the messages
and asynchronous MDNs already in flight, and cancels them when the drain times out.

---

## Limits and known gaps

- **Revocation** of certificates (CRL/OCSP) is not checked, nor is the chain: the pin is the trust. Validity periods
  are checked.
- **Asynchronous MDN delivery** is one attempt; a failure is logged, not retried.
- **Correlation** of asynchronous MDNs is in memory, per process, for 30 minutes.
- **Spooling.** The request, each decrypted and decompressed stage and the payload go through the core stream cache
  (`StreamCaching` options: memory up to the threshold, 128 KB by default, then a temporary file), as in the AS4
  receiver. What MimeKit and Bouncy Castle buffer inside one decryption step is theirs; `byte[]` bodies (without
  `streamBody`) are the payload in memory by definition.
- `AS2-Version` is not negotiated (we speak 1.2). AS2 Restart (resuming a partial transfer) and multiple attachments
  (RFC 6362) are not supported.

---

## Tested on

- **141 tests** on net8.0, net9.0 and net10.0: crypto round-trips over the algorithm matrix, loopback over a live
  Kestrel (sync, async and signed MDN, spans, statistics, principal), the security checks above (forged signatures,
  foreign identifiers, receipt-URL traps, unsigned receipts, expired certificates), limits and drain, duplicates.
- **Interop against OpenAS2 4.9.0** (Docker), both directions, synchronous and asynchronous MDN: our signed and encrypted message accepted by OpenAS2
  with a positive signed MDN whose MIC we verify; OpenAS2's message received, decrypted, verified and routed, and our
  MDN accepted by OpenAS2, which checks our MIC on the asynchronous path. See [TESTING.md](TESTING.md).

Design notes and the review this version answers are in `../../docs/as2`.
