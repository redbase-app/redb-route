# redb.Route changelog: 4.6.x

Releases of the 4.6 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.6.2] — 2026-11-05

### Changed

- **Released in lockstep with redb.Identity 4.6.2.** No change in redb.Route itself: the packages move to 4.6.2 so the
  whole ecosystem stays on one number. Nothing to do on your side.

## [4.6.1] — 2026-11-04

### Changed

- **AS4: a duplicate message is answered with the very receipt of the original.** The receiver keeps the receipts
  it sends (in memory by default, the newest 100 000 for one day); set `ReceiptStore` on the connection factory to a
  SQL, Redis or file `IClaimCheckRepository` when several workers receive for one node. See the package README. Packages: `redb.Route.As4`.
- **AS4: four refusals answer with the ebMS code the eDelivery conformance tests expect.** An unsigned message gets
  EBMS:0010 (was 0103), an attachment signed with the Attachment-Complete transform or a `ds:SignatureValue` that is not
  base64 EBMS:0102 (was 0101), a message signed with a certificate the node trusts for another party EBMS:0004 (was 0101). Packages: `redb.Route.As4`.
- **AS4: when a partner answers with a page instead of an ebMS signal, the error quotes its start.** A proxy's or
  firewall's refusal page usually names who refused and a support reference; the EBMS:0302 error text now carries
  up to 160 characters of a textual answer. Packages: `redb.Route.As4`.

### Fixed

- **AS4: a key reference by issuer and serial number now finds the certificate when the issuer name has no spaces.**
  Domibus and other WSS4J senders write the issuer as `CN=a,OU=b,C=BE`; such messages were refused with EBMS:0102
  ("not for any of our certificates") or failed signature verification. Nothing to change on your side. Packages: `redb.Route.As4`.
- **AS4: a security header with malformed base64 is refused with EBMS:0101 or EBMS:0102.** A signature value,
  digest, cipher value or security token that was not valid base64 made the receiver answer with an unexpected
  failure instead of the ebMS error the profile asks for. Nothing to change on your side. Packages: `redb.Route.As4`.
- **AS4: a partner's signal that carries no receipt is a missing receipt.** An answer with an `eb:SignalMessage` about
  the sent message but without `eb:Receipt` or `eb:Error` was reported as EBMS:0302 InvalidReceipt; it is now EBMS:0301
  MissingReceipt and is redelivered under the retry policy like any missing receipt. Packages: `redb.Route.As4`.
- **`redb-route-xml csharp` finds a resource written as `file="resources/order.xsd"`.** It looked only under `resources/`
  and failed on `resources/resources/order.xsd`, while `check` and `pack` accepted the route. `csharp` and `mermaid` now
  read `resources/{file}` first, then `{file}` from the project root, the same as `check`; `file="order.xsd"` works as before. Packages: `redb.Route.Xml.CodeGen`, `redb.Route.Xml`.
- **VS Code extension: `#` bean hints also list the beans of the module's `context.xml`.** In a project from the
  redb-route templates, where the beans are declared in `context.xml`, typing `#` in a route file now offers them,
  and `context.xml` itself gets the hints too.

## [4.6.0] — 2026-11-03

### Fixed

- **A bean of an unloadable module that takes `ILogger<T>` of its own type no longer keeps the module in memory.** The
  host's DI container kept every service type it was asked for, so a hot-swapped module stayed loaded. For its own types
  such a bean gets `ILogger<T>` and the options services (`IOptions<T>` and the rest, default instance) built outside
  the host's container, an empty `IEnumerable<T>`, and no other service. Packages: `redb.Route`, `redb.Route.Xml`.
