# redb.Route.As2

**AS2 (RFC 4130) B2B/EDI transport for the [redb.Route](../redb.Route) ESB framework.** Exchange business
documents with trading partners over HTTP(S) using signed and encrypted S/MIME messages and MDN receipts —
the protocol the retail, logistics and EDI world runs on (Walmart, Amazon, and their supplier networks).

Schemes: `as2` (HTTP), `as2s` (HTTPS).

- **Send** (`.To(...)`) — compress → sign → encrypt a payload and POST it to a partner, then verify the MDN.
- **Receive** (`.From(...)`) — host an AS2 server: decrypt, verify, hand the document to your route, return an MDN.
- **Both directions**, **synchronous and asynchronous MDN**, **signed MDN**, and the standard signature /
  encryption algorithm matrix.

Crypto is provided by **MimeKit** (Bouncy Castle underneath) — the same cryptographic foundation the AS2
industry interoperates on (Apache camel-as2, OpenAS2, Mendelson). Interop is validated against a live
**OpenAS2 v4.9.0** server (see [TESTING.md](TESTING.md)).

---

## Install & register

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteAs2();
    route.AddRouteBuilder<MyRoutes>();
});
```

`AddRedbRouteAs2()` registers the `as2` / `as2s` schemes and shares one Kestrel receive server with every
other HTTP-based connector in the process (via `redb.Route.Http.Hosting`).

---

## Trading partners — `As2ConnectionFactory`

A partner is a bundle of certificates, AS2 identifiers and an agreed profile. Register it **once** by name;
routes reference it with `.ConnectionFactory("name")`, so certificates never live in a URI.

```csharp
context.AddToRegistry("walmart", new As2ConnectionFactory
{
    OurCertificate     = ourPfx,     // our cert + PRIVATE key — signs outgoing, decrypts incoming
    PartnerCertificate = theirCer,   // partner's PUBLIC cert — encrypts outgoing, verifies their signature
    As2From = "OUR-AS2-ID",
    As2To   = "WALMART-AS2-ID",
    PartnerUrl = "https://partner.example.com/as2",

    // Profile (what both sides agreed on)
    Sign = true, Encrypt = true, Compress = false,
    SignAlg = "sha-256", EncryptAlg = "aes-128-cbc",
    SignedMdn = true, MdnMode = As2MdnMode.Sync,
});
```

`OurCertificate` must carry a private key (an `X509Certificate2` loaded from a PKCS#12/PFX). Mark the PFX
password `[Sensitive]` when it comes from a URI (`certPassword`) so it is redacted in logs.

---

## Sending (producer)

```csharp
From("direct://outbound")
    .To(As2.Send("https://partner.example.com/as2").ConnectionFactory("walmart"));
```

The producer compresses (if enabled), signs, encrypts, and POSTs the message. For a **synchronous MDN** the
receipt is parsed, its signature verified, and its `Received-Content-MIC` checked against what we sent — the
outcome lands on `exchange.Out`:

| Header on `exchange.Out` | Meaning |
|---|---|
| `redbAs2.mdnDisposition` | e.g. `automatic-action/MDN-sent-automatically; processed` |
| `redbAs2.signatureValid` | `bool` — the MDN's signature verified |
| `redbAs2.mdnMicMatch` | `bool` — the partner received exactly what we sent, intact |

```csharp
From("direct://outbound")
    .To(As2.Send("https://partner/as2").ConnectionFactory("walmart"))
    .Choice()
        .When(e => e.Out!.GetHeader<bool>(As2Headers.MdnMicMatch))
            .Log("delivered & verified")
        .Otherwise()
            .To("direct://delivery-alert");
```

The connector does **not** throw on a negative MDN or MIC mismatch — it surfaces the outcome and logs a
warning, leaving the policy decision to your route.

---

## Receiving (consumer / AS2 server)

```csharp
From(As2.Receive("/inbound/orders").Host("0.0.0.0").Port(4080).ConnectionFactory("walmart"))
    .Unmarshal(...)                 // your EDI parsing
    .To("direct://process-order");
```

The decrypted, verified business document is the exchange body; its real content type is on
`Message.ContentType` (e.g. `application/edi-x12`). A synchronous MDN is returned automatically. Metadata is
surfaced under `redbAs2.*`:

| Header | Meaning |
|---|---|
| `redbAs2.mic` / `redbAs2.micalg` | the computed Message Integrity Check + algorithm |
| `redbAs2.signatureValid` | the inbound signature verified against the partner cert |
| `redbAs2.remoteAddress` | the sender's IP |
| `redbAs2.partner` | the resolved connection-factory name |

AS2 wire headers (`AS2-From`, `AS2-To`, `Message-ID`, `Subject`, `Disposition-Notification-*`) are copied
onto the message verbatim. The S/MIME **wrapper** `Content-Type` is deliberately *not* copied into the
headers — only the inner business content type reaches `Message.ContentType`.

---

## MDN modes

```csharp
MdnMode = As2MdnMode.Sync    // receipt returned in the HTTP response (default)
MdnMode = As2MdnMode.Async   // receiver acks 200, then POSTs the MDN to a separate URL later
MdnMode = As2MdnMode.None    // no receipt requested
SignedMdn = true             // request/produce a signed MDN (common requirement)
```

### Asynchronous MDN

The sender registers the outgoing `Message-ID`; the partner posts the MDN back later to a receiver you host:

```csharp
// Partner config
MdnMode = As2MdnMode.Async,
AsyncMdnUrl = "https://our-host:4081/as2/mdn",

// Routes
From(As2.Receive("/inbound").Host("0.0.0.0").Port(4080).ConnectionFactory("walmart"))
    .To("direct://process");

From(As2.ReceiveMdn("/as2/mdn").Host("0.0.0.0").Port(4081).ConnectionFactory("walmart"))
    .Process(e =>
    {
        // correlated to the original message by Original-Message-ID
        var original = e.In.GetHeader<string>(As2Headers.MessageId);
        var ok = e.In.GetHeader<bool>(As2Headers.MdnMicMatch);
    });
```

---

## Algorithm matrix

| Knob | Supported values |
|---|---|
| `SignAlg` | `sha-1`, `sha-256` (default), `sha-384`, `sha-512` |
| `EncryptAlg` | `aes-128-cbc` (default), `aes-192-cbc`, `aes-256-cbc`, `3des` |
| `Compress` | `true` / `false` (RFC 3274) |

An unsupported algorithm fails fast when the route is built (`Validate()`), not at run time.

---

## URI form (advanced)

The DSL is sugar over URIs; you can write them directly:

```
as2:/inbound/orders?host=0.0.0.0&port=4080&connectionFactory=walmart      # receive server
as2:/as2/mdn?host=0.0.0.0&port=4081&mode=mdn&connectionFactory=walmart     # async-MDN receiver
as2s://partner.example.com/as2?connectionFactory=walmart                   # producer (HTTPS ⇒ as2s)
```

`https://` in `As2.Send(...)` maps to the `as2s` scheme; the receive-path's first segment is preserved
(no truncation). Certificates and algorithms come from the connection factory, not the URI.

---

## Cross-cutting

Like every redb.Route connector, AS2 endpoints get **statistics & health** (`IEndpointStatistics`, visible in
the Tsak dashboard) and **distributed tracing** for free — the producer opens a `Client` span and the
consumer a `Consumer` span linked to the inbound W3C `traceparent`. Secrets are `[Sensitive]`-redacted; the
receive server is the shared Kestrel host, so an HTTP route and an AS2 route in the same worker share one
server and never fight over a port.

---

## Status & maturity

Functionally complete: send + receive, sync + async + signed MDN, the algorithm matrix, cross-cutting
mechanics. Interop is **validated against a live OpenAS2 v4.9.0 in both directions** (`redb → OpenAS2` and
`OpenAS2 → redb`, signed + encrypted, positive MDN, MIC verified) — see [TESTING.md](TESTING.md). The design
and phase plan live in `../../docs/as2`.

Part of the redb.Route connector family.
