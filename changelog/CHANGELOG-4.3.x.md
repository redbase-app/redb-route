# redb.Route changelog: 4.3.x

Releases of the 4.3 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.3.0] — 2026-10-14

### Added — AS4: several payloads in one message, messages without payloads, point-to-point agreements

Preparation for the eDelivery AS4 conformance tests (common profile 1.16). Nothing changes for a route that sends a
single body and keeps the default agreement.

- **Several payloads on sending.** A body that is a list of `As4Payload` is sent as that many payloads, the
  `eb:PartInfo` elements in the order of the list, the first being the leading business document (profile §3.2.3:
  "a Producer ... MUST be able to control the order"). Each entry may give its Content-ID (null generates one) and
  its part properties, written next to `MimeType` and `CompressionType`, which stay the connector's: `CharacterSet`
  for an XML payload, for one. A sender had exactly one payload with a generated Content-ID before; a list as the
  body was sent as the text of its `ToString()`.
- **A message without payloads.** An empty list sends a simple SOAP message, not a multipart one. A received
  message without payloads has a null body, as before.
- **`payloadList` on receipt** (`.PayloadList()`): the body is always the list of `As4Payload`, one entry for a
  single payload, none for a message without. A single payload handed as the body itself carried neither its
  Content-ID nor its part properties. The list is what a send endpoint takes, so a route passes a message on as it
  came.
- **`As4Partner.RequireFourCornerProperties`** (default true, as before). `originalSender` and `finalRecipient`
  belong to the Four Corner Topology enhancement (profile §4.1.2), not to the common profile; the connector asked
  for them on every message, on sending and on receipt. A point-to-point agreement sets the property to false.
- **The `type` of a message property.** The header `redbAs4.propertyType.<name>` sets the `type` attribute of
  `eb:Property[@name]` on sending and carries it on receipt (profile §3.2.2: "It MUST be possible to set the type
  attribute for message properties"). The attribute was read and then dropped.
- **A reply leg with the values of its request leg.** A Two-Way exchange that answers under the same service and
  action, as the conformance tests run it, could not be configured: node validation refused the agreement as
  ambiguous ("partners 'x' and 'x' share agreement, sender, service and action"). Two legs of one partner are that
  partner's either way; only two different partners are told apart.
- **`demos/As4ConformanceDemo`**: the System Under Test for the conformance testing service, as a module. The
  access point, and the SUT Controller the service asks its owner to write: Submit, Deliver and Notify as routes.
  `ConformanceWorker selftest` plays the test driver on loopback through the flow of the service's sample messages,
  a ping, an ebMS error, a lost receipt (resent byte for byte, delivered once; `EBMS:0301` reported when none ever
  arrives) and the PKI path legs: 47 checks.
- Live against Domibus 5.1 (Harmony AP 2.6.2), the MSH the Commission's test driver runs on: three payloads under
  our Content-IDs, compressed and not, a message without payloads and an empty payload, each received and
  acknowledged with a receipt that names every part (`As4HarmonyLiveTests`). The module against Domibus as the test
  driver, both directions: a two-payload message in and the Deliver command back; a Submit command in, the user
  message under the commanded id and the Notify command back.
- Covered by `As4ProducerTests` and `As4ConsumerTests`: order, Content-IDs and part properties of three payloads
  through our own receiver and through a partner that decrypts and verifies; a message without payloads both ways;
  a single payload as a list; an agreement without the four-corner properties; a property type round trip; two
  payloads with one Content-ID refused before sending.

### Added — AS4: certification paths (`X509PKIPathv1`), trust in an authority instead of a pinned certificate

The connector knew one trust model: the partner's signing certificate is pinned in the agreement. A partner whose
certificate the receiver does not hold in advance presents it with its certification path, and the receiver trusts
an authority (WS-Security X.509 Token Profile §3.1.2; eDelivery AS4 1.16 §4.3.3; conformance assertions AS4_CP_TA47
to TA51, named among the topics of the Common Profile tests of testing service 2026.06).

- **`As4KeyReference.PkiPath`**: our signature carries our certificate with its authorities in a
  `wsse:BinarySecurityToken` of type `X509PKIPathv1`, the outermost authority first and our certificate last. The
  authorities come from the new `As4ConnectionFactory.SigningCertificateChain`, in any order; the path is ordered once,
  when the node starts, and a certificate of the chain that is not on the path of our certificate stops it from
  starting (it would be left out of every token, and the partner would refuse every message).
- **`As4Partner.PartnerTrustAnchors`**, in place of `PartnerSigningCertificates`: the partner must present its
  certificate in a path token, and the path is validated up to one of the anchors — the whole token a path (each
  certificate issued by the one before it, name and signature; every issuer a certification authority whose key may
  sign certificates), and from the anchor down (RFC 5280 §6.1) every path length constraint kept and everything within
  its validity period. An anchor may be a root or an intermediate authority, in the token or left out of
  it. A signer that references its certificate another way is refused with `EBMS:0101`, even when it would chain.
- **`As4Partner.PartnerCertificatePolicies`**: certificate policy OIDs the signer and every authority below the
  anchor must assert; `anyPolicy` satisfies each.
- **Revocation along the path**: with `RevocationMode` on the node, the signer and the authorities below the anchor
  are checked as a pinned certificate is (hard-fail unless `RevocationSoftFail`; a certificate that names no CRL and
  no AIA passes). .NET checks revocation only on a chain that ends at a self-signed root — under an intermediate
  anchor it reports a partial chain and checks nothing, on Windows and on Linux alike (probed on both) — so the chain
  is completed above the anchor from the token, the other anchors, the local stores and the anchor's AIA, for the
  revocation check only; no root found is an undetermined status.
- An agreement sets `PartnerSigningCertificates` or `PartnerTrustAnchors`, not both and not neither; validated when
  the node starts. A pinned agreement reads a path token for the certificate it is for, as any other reference form.
- **The reference names the token type** (`wsse11:TokenType`, WS-I Basic Security Profile 1.1 R5215). Found live:
  Domibus refused our first path token with `EBMS:0103`, "Any SECURITY_TOKEN_REFERENCE to a PKIPATH_TOKEN MUST contain
  a wsse11:TokenType attribute".
- Covered by `As4PkiPathTests`: the token and its order; a path to a root, to an intermediate authority and to an
  anchor left out of the token; a self-signed signer, another authority, certificates out of order, a certificate
  forged under the name of a trusted authority, an issuer that is not an authority, an issuer whose key may not sign
  certificates, a path beyond a path length constraint (also the anchor's own), an expired authority; policies; our
  chain ordered when the node starts and a stray certificate in it refused;
  and on loopback the five assertions TA47 to TA51. Live against Domibus 5.1 under its
  `eDeliveryAS4Policy_BST_PKIP` policy, both ways: it receives our path token, and its receipt's path token is
  validated against the anchor; and a path of three certificates (root, issuing authority, signer), which Domibus
  validates up to the root in its truststore (`As4HarmonyLiveTests`). Revocation along a path in
  `As4RevocationTests`, against CRLs a test CA publishes over HTTP: a revoked signer and a revoked intermediate
  authority, under a root anchor and under an intermediate one (with and without the root in the token), an
  unreachable CRL and a path with no root to be found (refused, unless soft-fail), and a message whose path leads to a
  revoked signer.

### Fixed — AS4: on Linux every message with a payload was refused by the partner

- The multipart body was written with MimeKit's default format options, whose line ending follows the platform:
  CRLF on Windows, a bare LF on Linux. MIME requires CRLF (RFC 2046 §4.1.1, §5.1.1). A receiver that splits the parts
  on CRLF took the LF before each boundary for the last byte of the part in front of it: one byte too many in an
  encrypted attachment, so its AES-GCM tag no longer matched. Domibus answered `EBMS:0004` ("Tag mismatch!"); a
  message without payloads, which is not multipart, went through. The body is written with CRLF on every platform
  now.
- **Since 4.2.0**, when the connector appeared: every interoperability run until now started from a Windows machine,
  where the default is CRLF. Found when the conformance module first ran in the Tsak container (Linux) against
  Domibus; after the fix the same container exchanges with Domibus in both directions.
- Covered by `As4MessagingTests.Multipart_IsWrittenWithCrlfLineEndings_OnEveryPlatform`. On Windows the test passes
  with or without the fix; it tells the two apart only when the suite runs on Linux.

### Fixed — AS4: a truncated compressed payload was delivered and acknowledged

- `GZipStream` ends without an exception when its input stops in the middle of the stream: a payload cut in half
  inflated to a part of the document, the route got it and the sender got a receipt for it. So did a payload missing
  its gzip trailer, and bytes after the header that happen to decode until they run out. The receiver now checks the
  gzip trailer (RFC 1952: CRC-32 and length of the content) against what it inflated and answers `EBMS:0303`
  DecompressionFailure otherwise, which is what the profile asks for a damaged payload (conformance assertion
  AS4_CP_TA30). A stream of one member, which is what AS4 senders write, is settled by its last eight bytes; a stream
  of several members (RFC 1952 §2.2) is walked member by member, each header, deflate stream and trailer checked, so
  it is accepted when whole and refused when it stops inside its last member or carries bytes after it.
- **An empty payload was sent as zero bytes on .NET 9 and later.** `GZipStream` writes nothing at all for empty
  content there (on .NET 8 it writes the empty member), and zero bytes are not a gzip stream: Domibus and Holodeck
  refuse them. The sender writes the empty gzip member now. **Behaviour to know:** the receiver refuses a zero-byte
  "compressed" part with `EBMS:0303`, so an empty payload from a 4.2.x sender running on .NET 9 or 10 is no longer
  accepted by a 4.3.0 receiver.
- **A signature that leaves out a required part is a policy failure.** A message whose signature verifies but does
  not cover `eb:Messaging`, the SOAP body or an attachment was answered `EBMS:0101` FailedAuthentication; it is
  `EBMS:0103` PolicyNoncompliance now, which is what Domibus answers from its WS-SecurityPolicy (`sp:SignedParts`):
  nothing that was signed has been altered. Checked live: a message with the empty body left out of the signature is
  refused by Domibus 5.1 with `EBMS:0103`, and the same message with the body covered is received (the conformance
  test `emptyBodyNotReferencedInSignature` of testing service 2026.06). A signature that does not verify stays
  `EBMS:0101`.
- **A `CharacterSet` part property that is not an encoding name is refused.** The profile requires the value to be
  an `EncName` of XML 1.0 §4.3.3 (§3.2.3; conformance assertion AS4_CP_TA29). A received message with another value is
  answered `EBMS:0003` ValueInconsistent and does not reach the route; a sender refuses to build one.
- Covered by `As4MessagingTests` (six kinds of truncated and damaged gzip, the trailer of whole streams up to several
  read buffers, the empty member, seven shapes of a stream of several members) and, as the conformance test driver's
  simulated sender sends them, by the `TA04`, `TA08`, `TA20_TA44`, `TA29`, `TA30`, `TA35` and `TA37` tests of
  `As4ConsumerTests` and `TA18` of `As4ProducerTests`.

### Changed — VS Code extension 0.2.21: the editor knows `payloadList` on `as4:`

- The editor catalog (`redb-route-1.0.catalog.xsd`, `redb-route-catalog.json`) is regenerated from the 4.3.0 build of
  the connectors. `<as4>` inside `<from>` takes `payloadList` (a boolean, `false` by default); inside `<to>` the
  attribute is refused, the option being receive-only. Nothing else in the catalog changed.

### Breaking — what a route builder declares applies to its own routes only

- `OnException`, `Intercept`, `InterceptFrom`, `InterceptSendToEndpoint` and `OnCompletion` declared on a
  `RouteBuilder` applied to every route of the context, the routes of other builders included, although the XML
  doc said "every route it defines". They apply to the routes of the declaring builder now — Apache Camel's
  RouteBuilder scope. Two builders may declare an `OnException` for the same type: that threw "Global exception
  handler for X is already registered" at `Start()`; each builder's handler is its own now.
- **`OnException` for the whole context is still there**, in its own form: a route configuration (below). The
  three levels are:

  | Declared | Applies to |
  |---|---|
  | in a route: `From(...).OnException<X>()...End()` | that route |
  | in a `RouteBuilder`: `OnException<X>()` | the routes of that builder |
  | in a route configuration: `RouteConfiguration().OnException<X>()` | every route of the context (or the routes that name its id) |

  Where several match, the nearest exception type wins (see "the nearest `OnException` wins" below).
- A builder's `OnException` is no longer registered as a context-wide handler (`AddGlobalExceptionHandler`); it is a
  layer of the error scope of each of its routes (see below). The handler body is compiled once per route instead
  of twice.
- **Route-XML too.** Each file loads into a builder of its own, so a container-level `<onException>`, intercept or
  `<onCompletion>` now applies to the routes of its own file only. The package README documented the opposite since
  4.1.1 ("the handlers apply to every route of the context, not only of this file", one file of
  handlers for all the others); a deployment that relies on such a file loses those handlers on the other files'
  routes **without an error**. Check every markup project for a handlers-only file before upgrading. The README now
  says what the engine does.
- **Migration.** A builder (or a markup file) that holds only `OnException` blocks for the routes of the others —
  the "exception handlers class" layout — becomes a route configuration (below): derive it from
  `RouteConfigurationBuilder` and declare the handlers under `RouteConfiguration()`; in markup, wrap them in
  `<routeConfiguration>`. Nothing else changes, the routes are not touched. Intercepts and `OnCompletion` shared by
  several builders: a base class the builders derive from. For "whatever fails anywhere, nobody took it" with no
  redelivery, the context's dead letter channel (below).
- Covered by `BuilderScopeTests` (intercept, completion and exception of one builder against the routes of
  another; two builders with the same exception type; the detached path).

### Added — route configurations: `OnException` for the whole context, with the full DSL

Apache Camel's Route Configuration. A builder's `OnException` reaches its own routes only, so error handling kept
apart from the routes — one class or one file of handlers for the whole module — had no home with the full DSL: the
context's handlers (`AddGlobalExceptionHandler`, `ErrorHandler`) are bare processors, with no redelivery, no `OnWhen`
and no steps.

- `RouteConfigurationBuilder` — a route builder whose `Configuration()` declares configurations; added like any
  builder (`context.AddRoutes(...)`, assembly discovery). `RouteConfiguration()` starts the default configuration,
  `RouteConfiguration("id")` one with an id; `.OnException<T>()` on it is the `OnException` of a builder, with every
  setting and every step (`MaximumRedeliveries`, `OnWhen`, `Handled`, `.SpanTag(...)`, `.To(...)`). `RouteConfiguration`
  is available in any `RouteBuilder`. Inline: `context.AddRouteConfiguration(c => c.OnException<X>()...)` and
  `context.AddRouteConfiguration("id", c => ...)`.
- **Which routes.** The default configuration (no id) is for every route of the context that has no configurations
  assigned. A route assigns them with `.RouteConfigurationId("a,b")`: exact ids, a wildcard ending in `*`, or a
  regular expression, separated by commas; such a route does not get the default one. An id is unique within a
  context. An exact id no configuration has fails `Start()`, naming the route: a route must not start without the
  handlers it asked for.
- **Route-XML.** `<routeConfiguration id="…">` with `<onException>` blocks, in a routes file — where it reaches the
  routes of the other files — and in `context.xml`; `<route routeConfigurationId="…">`. In the schema, and in the
  generated C#.
- The handler's steps are compiled into each route they apply to, as a builder's are: a detached body
  (`.Threads()`, an aggregation completed by its timeout) gets them, and `.SpanTag(..., SpanTarget.Inbound)` in a
  configuration's handler tags the request that failed.
- **A warning at start** for a builder (or a markup file) that declares `OnException`, intercepts or `OnCompletion`
  and no route: its declarations apply to nothing. It is what a class of shared handlers is until it becomes a route
  configuration, and nothing else said so — the other builders' routes simply ran without those handlers.
- Not in this release: `Intercept*` and `OnCompletion` in a route configuration.
- Covered by `RouteConfigurationTests`, `RouteConfigurationXmlTests`, `SpanTagTests`.

### Breaking — the nearest `OnException` wins, wherever it is declared

Apache Camel's exception policy (`DefaultExceptionPolicyStrategy`), in place of "the first scope that matches".

- **Across scopes.** A route's `OnException`, its builder's and its route configurations' used to be layers: the
  route's handler took a failure whenever it matched, however wide. Now the handler whose exception type is nearest
  to the failure wins wherever it is declared: with `OnException<Exception>` on the route and
  `OnException<IOException>` on the builder, an `IOException` goes to the builder's. The route's own handler wins a
  tie; a route configuration's handler is used instead of a handler of the same type on the route's builder.
- **Within a scope.** Each `OnException` block was a wrapper of its own, the first declared innermost, so a wide type
  declared first took what a narrower one declared later was meant for. A scope is one handler set now: the
  declaration order no longer decides between different types.
- **Causes.** A failure is matched from its innermost cause up to the exception that was thrown, and an exact type
  ends the search: `OnException<IOException>` takes an `InvalidOperationException` caused by an `IOException`. The
  handler still gets the exception that was thrown. Before, only the thrown exception was matched.
- **The same type twice.** A later `OnException` of the same type without `OnWhen` replaces the earlier one of the
  same scope (it used to be shadowed by it). Handlers with an `OnWhen` are each their own entry; one whose `OnWhen`
  is false is passed over.
- **Redelivery.** One scope is one redelivery loop: the handler is chosen again for the failure of each attempt, and
  the attempts are counted together.
- **What to check.** A route or a builder that declares both a wide and a narrow type, or a handler for a type that
  appears only as a cause: the narrow or the cause's handler now runs where the wide one did.
- Covered by `ExceptionPolicyTests`.

### Breaking — one error scope per route; the context's error handling works on every route

- Error handling had two unrelated models. Inside a route: the `OnException` wrappers, with their whole configuration.
  `IRouteContext.HandleException` — local handlers (`AddLocalExceptionHandler`), global handlers
  (`AddGlobalExceptionHandler`), `context.ErrorHandler` — ran a bare handler body and was called by two places only:
  a detached `.Threads()` worker and the Quartz consumer after the pipeline. So those three APIs **never ran for
  HTTP, timer, direct, broker routes**, although `IRouteContext` documents them as "invoked when this exception type
  is caught"; and a host's dead letter queue installed as `ErrorHandler` (Tsak's checkpoint DLQ) only saw cron,
  qtimer and `.Threads()` failures.
- Now every route has one error scope, as in Apache Camel, from the inside out: the route's `OnException`, those of
  its builder and of its route configurations (the nearest type winning among all three, see above), then the
  context's handling — local handler of the route → global handler → `ErrorHandler`. The context
  layer runs only for a failure **no `OnException` took**, `Handled(false)` included — Camel's context error handler,
  which runs only when no `onException` matched. A context handler that marks the exchange handled
  (`ExceptionHandled = true`, a dead letter channel) ends the failure; otherwise it goes on to the caller as before,
  as with Camel's `DefaultErrorHandler`.
- **What changes for you.** A handler registered with `AddGlobalExceptionHandler`/`AddLocalExceptionHandler` or set as
  `ErrorHandler` now runs on every route. A dead letter queue behind `ErrorHandler` now captures failures of HTTP,
  timer and broker routes too. A route that declares an `OnException` for a type takes that type away from the
  context layer: to dead-letter from inside such a handler, send there explicitly
  (`OnException<X>().Log(...).To("direct://errors")`), as `onException(...).to(dlc)` in Camel.
- `IRouteContext.HandleException` keeps its API and lookup for an external caller; the engine no longer calls it.
- Covered by `RouteErrorScopeTests`.

### Fixed — a detached body gets the route's whole error handling

- A body that runs after the exchange that fed it has left the route — a `.Threads()` worker (in-only), a `Debounce`
  flush, an `Aggregate` group completed by its timeout or on stop, a `Resequence` batch — ran outside the route's
  error wrappers. `.Threads()` reached a bare handler body through `HandleException` (no `OnWhen`, no redeliveries,
  no `Handled`/`Continued`, no `UseOriginalBody`, and never the route's own `OnException`: "No exception handler
  found"); the other three only logged the failure. Such a body is wrapped by the route's error scope now, with every
  setting of every handler; a redelivery re-runs that body, the part of the route before it has already completed.
  An aggregation completed by the arriving exchange stays inside the wrappers and is handled once.
- Covered by `RouteErrorScopeTests`, `DetachedErrorScopeTests`, `BuilderScopeTests`.

### Fixed — a failed Quartz fire runs the route's error handling once

- The Quartz consumer handed a failure left on the exchange to `IRouteContext.HandleException` after the pipeline:
  an `OnException` without `Handled()` ran a second time. The context layer of the route's error scope now covers what
  that call was for — the context's `ErrorHandler`, a host's dead letter queue — exactly once, and the consumer only
  records the outcome on its span, as every other consumer. Applies to `qtimer:` and `cron:`.
- Covered by `QuartzErrorHandlingTests`.

### Breaking — Quartz: one job key, one consumer; a shared store's schedule is not a node's to delete

- **One job key, one consumer per scheduler.** The job key is `{groupName}.{jobName}` — `redb` and the endpoint path by
  default — so `cron://nightly?schedule=A` and `cron://nightly?schedule=B` (two endpoints the context accepts), or
  `cron://nightly` and `qtimer://nightly`, claimed one job: the second consumer silently replaced the first one's
  schedule (`AddJob(replace)` + `RescheduleJob`), and only one route ever fired. The second claimant now fails at its
  start, naming both endpoints; give it its own path or `jobName=`/`groupName=`. A fire finds its consumer by that job
  key on its scheduler, no longer by the endpoint URI and the context id in the job data (another node of a cluster
  writes its own context id there).
- **A persistent store is not deleted from by a node.** With `AdoJobStore`, stopping a route deleted the job and its
  trigger (`deleteJob=true`, the default) or paused the trigger (`pauseJob`) — for every node of the cluster and for
  this node's next start; and a fire taken by a node without the consumer (a rolling upgrade, a route stopped on one
  node, a module not deployed there) "cleaned up the orphan" — the same deletion. Now `deleteJob`/`pauseJob` apply to
  an in-memory store only (a warning when set on a persistent one), and such a fire is skipped and logged once.
- **The scheduler starts once the context has started** (camel-quartz `autoStartScheduler`), the context's own or one
  handed to it; a route started later starts it at once. It used to start with the first consumer, so a stored trigger
  could fire before the other consumers of the context were registered. Code that starts a Quartz consumer by hand must
  start its context.
- **The context's own scheduler is shut down with the context.** Nothing ever shut it down: after `Stop()`/`Dispose()`
  its threads ran on and it stayed in Quartz's scheduler repository.
- **`QuartzSchedulerOptions`**: the store, cluster, serializer and pool of the scheduler the context builds, named
  `{prefix}-{context id}` — a host shares the options, not a scheduler, so each context has its own job namespace (two
  packages that both say `cron://nightly` do not collide) and, clustered, a cluster of the nodes that run it, as Camel's
  scheduler per CamelContext. A persistent store needs a given context id; a generated one is an error.
- `.Cluster(true)` on a Quartz route over a clustered store is a warning when a leader-only policy was attached: Quartz
  already fires a job on one node. A route the policy factory declined (redb.Tsak declines Quartz routes) runs on
  every node and is not warned about.
- Covered by `QuartzSchedulingTests`, `QuartzPersistentStoreTests` (the `pgdb` container), `QuartzConsumerRegistryTests`.

### Fixed — a route a policy factory declined no longer reads "no IRoutePolicyFactory is registered"

`IRoutePolicyFactory` returns `null` for a route it does not apply to. With a factory registered that declined a
`.Cluster(true)` route, the route's `RoutePolicyDescriptor.Reason` said no factory was registered and the startup
check logged the same warning. The reason now says the registered factory declined the route, and the startup warning
is kept for a context with no factory at all; the factory says why it declined. Covered by
`RoutePolicyDescriptorTests`.

### Added — the context's dead letter channel

- `DeadLetterErrorHandler` — Apache Camel's context-level `errorHandler(deadLetterChannel("direct:errors"))`:
  `context.ErrorHandler = new DeadLetterErrorHandler(context, "direct://errors")`. Every route sends to it a failure
  no `OnException` took, with the `CamelDeadLetter*` headers, and the failure is handled; a lost dead letter is logged
  and the failure goes on. Redeliveries stay with `OnException`. In `context.xml`:
  `<deadLetterChannel uri="direct://errors"/>`; one error handler per context — over one already set it is an error.
- Covered by `DeadLetterErrorHandlerTests`, `DeadLetterChannelXmlTests`.

### Fixed — a restart keeps the exception handlers registered on the context

- `Stop()` cleared the handlers added with `AddGlobalExceptionHandler` and `AddLocalExceptionHandler` together
  with what the route compiler had derived, so a context started again had lost them. They are configuration,
  like the components, and survive a restart now; only the routes' error scopes are rebuilt.
- Covered by `BuilderScopeTests` (`Restart_KeepsTheGlobalHandlerTheUserRegistered`,
  `Restart_KeepsTheLocalHandlerTheUserRegistered`).

### Added — `SpanTag`: a business key on a span, to find the trace by it

- `.SpanTag(key, value, target)` puts one attribute on a span of the engine. A trace could not be found by a
  business key — the SAP acknowledgement of an order carried no order number in its trace. Jaeger's tag search and
  Tempo's TraceQL match an attribute on any span of a trace, so the route's own span is enough to find it.
- The value follows the rule of a value position, as in `SetHeader`: a string is a literal, never a template; an
  expression is evaluated per message — `Expr("${header.orderNo}")`, `XPath("/Ack/OrderNo")`, `JPath(...)`. Strings,
  booleans, integers and doubles keep their type, a date is ISO 8601, anything else invariant text.
- `SpanTarget.Enclosing` (default) is the nearest span of the engine — the enclosing `Traced` scope, else the route
  span; with `.Tracing(false)` nothing is set, there is no fallback to the transport span. `SpanTarget.Inbound` is
  the server or consumer span of the transport that received the message, kept on the exchange (internal, not part
  of `IExchange`, not in a checkpoint snapshot) by a wrapper next to the context stamp; a route called through
  `direct:` uses its caller's. No connector changed. The host's own span (ASP.NET Core) is never written.
- Nothing is set — and the value is not evaluated — without a listener or on an unsampled span; nothing on a span
  that has ended (a wire tap running after the request); nothing for a `null` or empty value. A value that fails to
  evaluate is skipped and logged at Debug: telemetry does not change the outcome of a message.
- The key is checked when the route is built: lowercase, dot-separated; not in `redb.`, `messaging.`, `http.`,
  `rpc.`, `db.`, `exception.`, `otel.` — the existing tags keep their names and values, `redb.route.id` included; not
  a secret's name (`EndpointUri.IsSensitiveKey`). A value's length is the SDK's limit
  (`OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT`), as for every attribute of the engine.
- **The value is exported unfiltered** — never personal data or credentials. A business key goes on a span, not in
  baggage, which travels to every next service (`METRICS.md`).
- Route-XML: `<spanTag key="..." value="..."/>` or `expr="..."`, exactly one, and `target="Inbound"`. The editor's
  resources (`media/`) are not regenerated in this change: the tool reads the worker's shared layer, which is still
  4.2.2 in the tree; `EditorResourcesTests` fails until they are.
- Not added: `Traced(name, tags)`. It is "open a `Traced` scope and put a `SpanTag` in it" said twice, and it would
  evaluate the value before the step that knows it.
- Covered by `SpanTagTests` (engine), `HttpTracePropagationTests.SpanTag_Inbound_…` (a real HTTP server span) and
  `SpanTagXmlTests`.

### Added — route builders discovered from assemblies

- `RouteContext.AddRoutes(params Assembly[])` adds every public, non-abstract, closed `RouteBuilder` of the
  assemblies, created through its public parameterless constructor. In a host,
  `RedbRouteBuilder.AddRouteBuilders(params Assembly[])` registers them the way `AddRouteBuilder<T>()` does, so
  constructor injection works. Both take a filter (`AddRoutes(assemblies, type => ...)`) for conditions and for
  leaving a builder out — Apache Camel's include/exclude patterns; there is no attribute.
- Nothing is skipped silently: a builder without a parameterless constructor (on the context), a constructor that
  throws, or an assembly whose types cannot all be loaded is an error naming the type, and nothing is added. A
  type already added — explicitly or by an earlier discovery — is refused at registration instead of failing at
  `Start()` with a duplicate route id. Two explicit instances of one type stay allowed.
- The order is by full name, for a reproducible log; it carries no meaning, since a builder's declarations apply
  to its own routes.
- Covered by `RouteBuilderDiscoveryTests`.
