# redb.Route.Soap

**SOAP / WSDL web-service transport for the [redb.Route](../redb.Route) ESB framework**, oriented to Apache
Camel's `camel-cxf`. Call SOAP services and host SOAP endpoints as ordinary route steps — with the two SOAP
header planes, SOAP 1.1 / 1.2, faults, WS-Security (UsernameToken + XML-Signature + XML-Encryption), MTOM
attachments, and `?wsdl` publishing.

Schemes: `soap` (HTTP), `soaps` (HTTPS).

- **Call** (`.To(...)`) — wrap a payload in an envelope, POST it, parse the response, surface a `soap:Fault`.
- **Host** (`.From(...)`) — run a SOAP endpoint on the shared Kestrel server; the route handles the request
  and its reply becomes the response envelope.
- **Both directions**, **SOAP 1.1 and 1.2**, three camel-cxf **dataFormat** modes, **WS-Security**, **MTOM**.

Everything is **in-box** — `HttpClient`, the shared `redb.Route.Http.Hosting` Kestrel host, and
`System.Security.Cryptography.Xml` for WS-Security. No CoreWCF, no `System.ServiceModel` runtime, no
vulnerable dependency. Interop is validated against an independent SOAP stack (Node.js `soap`) in both
directions, plain and MTOM.

---

## Install & register

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteSoap();
    route.AddRouteBuilder<MyRoutes>();
});
```

`AddRedbRouteSoap()` registers the `soap` / `soaps` schemes and shares one Kestrel receive server with every
other HTTP-based connector in the process (via `redb.Route.Http.Hosting`).

---

## Quick start

Every endpoint works two equivalent ways — a **fluent builder** or a plain **string URI**. Use whichever
reads better; the fluent builder simply emits the string form under the hood.

```csharp
// Call a service — fluent
From("direct://get-fares")
    .To(Soap.Call("https://gds/air.svc").ConnectionFactory("amadeus").Operation("GetFares"));

// Call a service — string URI (identical)
From("direct://get-fares")
    .To("soaps://gds/air.svc?connectionFactory=amadeus&operation=GetFares");

// Host a SOAP endpoint — fluent
From(Soap.Listen("/svc/orders").Host("0.0.0.0").Port(4090).ConnectionFactory("orders"))
    .Process(handle);

// Host a SOAP endpoint — string URI (identical)
From("soap:/svc/orders?host=0.0.0.0&port=4090&connectionFactory=orders")
    .Process(handle);
```

In the default **Payload** mode the message body is the XML of `<soap:Body>` — send a fragment, receive a
fragment. No code generation, works against any service.

## Endpoint URIs

If you prefer raw strings (config files, `From`/`To` with a literal), here is the full grammar.

**Producer** (`.To(...)`) — `soap://` for HTTP, `soaps://` for HTTPS; the host+path is the service address:

```
soap://{host}{/path}?connectionFactory={name}&operation={op}&action={soapAction}
soaps://gds/air.svc?connectionFactory=amadeus&operation=GetFares
```

**Consumer** (`.From(...)`) — `soap:` plus the receive path; the host/port are query parameters:

```
soap:/{path}?host={bind}&port={port}&connectionFactory={name}
soap:/svc/orders?host=0.0.0.0&port=4090&connectionFactory=orders
```

| Query parameter | Side | Meaning |
|---|---|---|
| `connectionFactory` | both | Name of the registered `SoapConnectionFactory`. |
| `operation` | producer | Operation name / SOAPAction. |
| `action` | producer | Explicit SOAPAction (overrides the factory default). |
| `host` | consumer | Bind address (`0.0.0.0` for all interfaces). |
| `port` | consumer | Listen port. |

The `soaps` scheme (or an `https://` URL passed to `Soap.Call`) turns on TLS. Certificates, credentials, the
SOAP version and the data format live on the `SoapConnectionFactory` (below), never in the URI.

---

## Service bindings — `SoapConnectionFactory`

A binding is the WSDL/endpoint, version, data format, and WS-Security material. Register it **once** by name;
routes reference it with `.ConnectionFactory("name")`, so certificates and credentials never live in a URI
(the same pattern as AS2 / RabbitMQ).

```csharp
context.AddToRegistry("amadeus", new SoapConnectionFactory
{
    EndpointUrl = "https://gds/air.svc",
    SoapVersion = SoapVersion.Soap11,
    DataFormat  = SoapDataFormat.Payload,
    DefaultAction = "urn:GetFares",

    // WS-Security (optional)
    SigningCert = ourPfx,      // our cert + PRIVATE key — signs outgoing, decrypts incoming
    EncryptCert = partnerCer,  // partner's PUBLIC cert — encrypts outgoing, verifies their signature
    Username = "svc", Password = "secret",   // UsernameToken

    Mtom = true,               // enable MTOM/XOP attachments
    Wsdl = "contracts/air.wsdl",              // published on GET ?wsdl (consumer)
});
```

| Property | Role |
|---|---|
| `EndpointUrl` | Service URL to POST to (producer) / public address (consumer). |
| `SoapVersion` | `Soap11` (default) or `Soap12`. Drives Content-Type and SOAPAction placement. |
| `DataFormat` | `Payload` (default), `Message`, or `Pojo`. |
| `DefaultAction` | SOAPAction used when the exchange carries no `redbSoap.action`. |
| `SigningCert` | Our cert **with private key**: signs outgoing, decrypts incoming. |
| `EncryptCert` | Partner's public cert: encrypts outgoing, **authenticates** their signature. |
| `Username` / `Password` | WS-Security UsernameToken. `Password` is `[Sensitive]` (redacted in logs). |
| `RequestType` / `ResponseType` | Pojo mode: CLR types for the request (consumer) / response (producer). |
| `Mtom` | Send/receive binary attachments as `multipart/related`. |
| `Wsdl` | File path or inline XML; a consumer serves it on `GET ?wsdl`. |

---

## dataFormat modes (camel-cxf parity)

Set `SoapConnectionFactory.DataFormat`:

### `Payload` (default)
The route body is the inner `<soap:Body>` XML. Works with any service, no codegen. The producer wraps a
fragment in an envelope; the consumer hands the route the Body payload and wraps the route's reply.

### `Message` — transparent proxy
The route body is the **whole envelope**, in and out. The producer sends `In.Body` verbatim (no header
injection, no auto-security — those are the route's own); the consumer hands the route the entire wire
envelope and returns whatever envelope the route produced. Use it to inspect, log, or forward SOAP traffic
untouched.

### `Pojo` — typed objects
Typed request/response via `XmlSerializer` — the .NET analogue of camel-cxf's POJO mode over JAXB
document/literal. DTOs are ordinary XML-serializable types and may be generated from a WSDL with
`dotnet-svcutil`.

```csharp
context.AddToRegistry("air", new SoapConnectionFactory {
    EndpointUrl = "https://gds/air.svc",
    DataFormat = SoapDataFormat.Pojo,
    ResponseType = typeof(GetFaresResponse),   // producer: reply type
});

From("direct://q")
    .Process(e => e.In.Body = new GetFaresRequest { Route = "JFK-LHR" })
    .To(Soap.Call("https://gds/air.svc").ConnectionFactory("air"));
// e.Out.Body is now a GetFaresResponse
```

On a consumer set `RequestType` (the inbound type the route receives); the route's reply object is serialized
back. Override the producer's response type per message with the `redbSoap.responseType` header.

---

## Two header planes

SOAP has two distinct header planes, and the connector keeps them apart:

1. **Transport HTTP headers** — `SOAPAction`, `Content-Type`. Handled internally.
2. **Envelope `<soap:Header>`** — the SOAP header block. Each child element maps to a
   `redbSoap.header.<LocalName>` exchange header (in and out). Set one on the producer and it becomes a
   `<soap:Header>` child; read them on the consumer.

Connector metadata carries the `redbSoap.` prefix and is **stripped before the envelope goes out**
(`SoapHeaders.IsRedbHeader`), so it never leaks onto the wire.

| Header | Direction | Meaning |
|---|---|---|
| `redbSoap.action` | in/out | SOAPAction of the request/operation. |
| `redbSoap.operation` | in | Local name of the Body payload's root element (route on it). |
| `redbSoap.faultCode` / `redbSoap.faultString` | in | Fault code / reason of a `soap:Fault` response. |
| `redbSoap.username` / `redbSoap.password` | in | WS-Security UsernameToken, surfaced on the consumer. |
| `redbSoap.signatureValid` | in | Whether an inbound Body signature verified (see WS-Security). |
| `redbSoap.responseType` | out | Pojo mode: per-message response CLR `Type`. |
| `redbSoap.attachments` | in/out | MTOM attachments — an `IReadOnlyList<SoapAttachment>`. |
| `redbSoap.header.*` | in/out | Envelope `<soap:Header>` block elements (the second plane). |

---

## SOAP 1.1 vs 1.2 and faults

`SoapVersion` drives the wire:

- **1.1** — `text/xml` + a separate `SOAPAction` HTTP header; faults carry `faultcode` / `faultstring`.
- **1.2** — `application/soap+xml` with the action folded into the Content-Type; faults carry
  `Code`/`Value` and `Reason`/`Text`.

A fault response surfaces on `redbSoap.faultCode` / `redbSoap.faultString` and throws `SoapFaultException`
from the producer. On the consumer, a route exception becomes a `soap:Fault` (HTTP 500 for 1.1, HTTP 200 for
1.2, per convention). Both fault shapes are parsed regardless of prefix (the parser is namespace-driven, so
WCF `s:` and CXF `soapenv:` envelopes read the same).

---

## WS-Security

All in-box via `System.Security.Cryptography.Xml`, configured with certificates/credentials on the
`SoapConnectionFactory`. Certificate roles mirror AS2:

- **`SigningCert`** — our cert with private key: **signs** outgoing bodies and **decrypts** incoming ones.
- **`EncryptCert`** — the partner's public cert: **encrypts** outgoing bodies and **authenticates** their
  incoming signature.

**UsernameToken** — set `Username` (+ `Password`) and the producer prepends a `<wsse:Security>` header; the
consumer surfaces them on `redbSoap.username` / `redbSoap.password`.

**XML-Signature** — with `SigningCert` set the producer signs the `<soap:Body>` (Exclusive C14N, SHA-256,
`wsu:Id` reference, embedded X.509). The consumer verifies and reports `redbSoap.signatureValid`:

- When **`EncryptCert` is set**, verification is **authenticated**: the signer must be that exact partner
  certificate (thumbprint match) and the signature must cover the `<soap:Body>` — a forged self-signed
  signature or a signature over a decoy element is rejected.
- When it is **not set**, only cryptographic **integrity** against the embedded key is checked (the Body was
  not tampered), which does **not** establish who the sender is.

**XML-Encryption** — with `EncryptCert` set the producer encrypts the `<soap:Body>` to the partner; the
consumer decrypts with its `SigningCert` private key. The wire is the **standard WSS layout** real stacks
produce and expect: an `<xenc:EncryptedKey>` (AES session key, RSA-OAEP-wrapped) in the `<wsse:Security>`
header, joined by a `ReferenceList` to the `<xenc:EncryptedData>` (AES-256-CBC) in the Body. The order is
**sign-then-encrypt** outbound and **decrypt-then-verify** inbound, and the producer applies the same to
responses (symmetric). Decryption also reads the legacy inline layout.

```csharp
context.AddToRegistry("secured", new SoapConnectionFactory {
    EndpointUrl = "https://partner/svc",
    SigningCert = ourPfx,       // sign + decrypt
    EncryptCert = partnerCer,   // encrypt + authenticate their signature
});
```

---

## MTOM / attachments

Set `Mtom = true`. Binary attachments travel as `multipart/related` + `xop:Include` and live on the
`redbSoap.attachments` header plane as `SoapAttachment` records — a side plane, like Camel's
`AttachmentMessage`, so the Body contract is untouched.

```csharp
var msg = new Message("<Upload xmlns=\"urn:svc\"><file>" +
    "<xop:Include xmlns:xop=\"http://www.w3.org/2004/08/xop/include\" href=\"cid:doc-1\"/></file></Upload>");
msg.Headers[SoapHeaders.Attachments] =
    new List<SoapAttachment> { new("doc-1", "application/pdf", pdfBytes) };
await producer.Process(new Exchange(msg));
```

The consumer exposes inbound attachments on `redbSoap.attachments`; the route reads them and, to reply with
attachments, sets a **new** list on its response (the connector will not echo the caller's own inbound
attachments). The MIME parser is line-anchored, so boundary-looking bytes inside a binary part never cause a
mis-split.

---

## WSDL publishing

Set `SoapConnectionFactory.Wsdl` to a file path or inline XML on a consumer and it is served on
`GET {path}?wsdl`, with the `<soap:address>` location rewritten to the address the client actually reached
you on (camel-cxf `?wsdl` parity). Without a WSDL, GET is not an allowed method on the endpoint.

.NET Core has no runtime WSDL *import* (by design), so the WSDL is the authored — or `dotnet-svcutil`-derived
— contract, not reflected from CLR types. Pair it with `Pojo` mode for typed request/response.

---

## Telemetry & statistics

Like every redb.Route connector: a `Client` span on the producer and a `Server` span on the consumer
(`rpc.system = soap`, with the operation/action), and `IEndpointStatistics` counters
(`MessagesIn/Out`, `Errors`, bytes). `[Sensitive]` fields (the UsernameToken password) are redacted in logs
and the TSAK dashboard, and endpoint URIs are sanitized.

---

## Fluent DSL

```csharp
// Producer — builds "soaps://host/svc?connectionFactory=name&operation=GetFares"
Soap.Call("https://host/svc")   // http ⇒ soap, https ⇒ soaps
    .ConnectionFactory("name")
    .Operation("GetFares")      // or .Action("urn:GetFares")

// Consumer — builds "soap:/svc/path?host=0.0.0.0&port=4090&connectionFactory=name"
Soap.Listen("/svc/path")
    .Host("0.0.0.0")
    .Port(4090)
    .ConnectionFactory("name")
```

Both builders implicitly convert to the endpoint URI (shown in the comments), so they drop straight into
`.To(...)` / `.From(...)` — and you can pass that exact string yourself instead (see **Endpoint URIs**).

---

## Interop & boundaries

**Validated** against independent stacks (zero shared code), gated `Category=Interop`; harness in
`C:\Work\yaml\soap`:

- **Node.js `soap`** in both directions — our producer → their server, their client → our consumer — plain
  SOAP and MTOM (`SoapInteropTests`, `SoapMtomInteropTests`).
- **Node.js `crypto` (OpenSSL)** decrypts our WSS-encrypted envelope end to end — RSA-OAEP key unwrap +
  AES-256-CBC — proving the encryption layout interoperates (`SoapEncryptionInteropTests`).

Honest boundaries (documented in `../../docs/SOAP_CONNECTOR_PLAN.md`):

- **Runtime WSDL import** is absent in .NET Core by design — use a static WSDL contract + Pojo types.
- **WSDL generation from CLR types** (reflection, as CXF/CoreWCF) is not done — contract-first is the model.
- **`CipherReference`** (external ciphertext) and non-OAEP key transport are not handled; UsernameToken
  `PasswordDigest` is treated as plaintext. Rare WS-* variants.

Part of the redb.Route connector family.
