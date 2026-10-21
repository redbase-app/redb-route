# redb.Route changelog: 4.1.x

Releases of the 4.1 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.1.1] — 2026-09-25

> **This release carries a security fix.** A document type declaration in somebody else's XML could
> expand a tiny message into a huge one in memory, a denial of service against every route in the
> process. Every reader of foreign XML now refuses a DTD. The entry marked *Security* below says what
> was possible and what changed.

### Security — XML from somebody else is read with a DTD refused, not expanded

- A document type declaration can nest internal entities so that a tiny message expands into a huge one
  in memory — the "billion laughs" shape of an XXE attack, and a denial of service against every route
  in the process. Measured on .NET 10: 452 bytes became 300 000 characters, and the real thing does not
  stop at five levels. Reported while preparing the AS4 connector, 2026-09-25.
- The direct loaders all expand it: `XmlDocument.Load`, `XDocument.Load` and `XDocument.Parse` alike —
  LINQ to XML is no safer than the old DOM here — and `XmlResolver = null` does not help, because the
  expansion runs on internal entities rather than external ones. Only a reader built by
  `XmlReader.Create` refuses a DTD, which it does by default.
- New `SafeXml` in the core reads every document through such a reader, with an optional size bound, and
  keeps the two properties call sites depend on: line info for positioned errors, and preserved
  whitespace for signature canonicalization. Everything that reads somebody else's XML now goes through
  it: the SOAP envelope parser, the SOAP consumer and producer (decryption, signature checks, body
  fragments, WSDL rewriting), `xpath()` over a message body — which is any transport, not just SOAP —
  the XML model of a payload template, and the markup loaders, whose files arrive inside a package.
- A DTD is refused rather than budgeted: SOAP forbids one in an envelope outright, so for SOAP this is
  conformance rather than strictness, and a payload that needs one should say so explicitly. On the SOAP
  consumer the refusal comes back as a Sender fault, as any other unreadable request does.

### Added — a SOAP fault can be an answer instead of a failure

- Until now the only way to answer with `soap:Fault` was to throw, which left the exchange failed: the
  route's metrics counted an error, a dead-letter channel took a copy, and a supervising host saw a broken
  route — for a "no such order" the service is designed to return. Requested by the Tsak agent 2026-09-25,
  whose DLQ filled with business faults.
- **Consumer**: put `redbSoap.faultString` (and optionally `redbSoap.faultCode`) on the reply and return
  normally — the consumer sends the fault and the exchange stays successful. Without a code the fault is
  `soap:Sender` / `soap:Client`: a refusal about the content of the request will refuse the same bytes
  again, while `Receiver` is a retry hint. Throwing `SoapFaultException` is unchanged and still a failure.
- **Producer**: `throwOnFault=false` (default `true`) returns the fault as the reply, with `redbSoap.isFault`
  beside the code and reason to branch on. Mirrors `throwOnError` on the HTTP producer.
- Explicit on both sides rather than inferred from the fault code, which was the other option in the
  request: a Sender fault can be a bug in the route just as easily as a business answer, and only the route
  author knows which. Guessing would have made every mis-sent request look like a designed outcome.

### Added — `SetProperties` / `<setProperties>`: several exchange properties in one step

- The property twin of `SetHeaders`: `.SetProperties(("a", 1), ("b", Expr("property.a + 1")))` in C#,
  `<setProperties><property name="a" value="1"/><property name="b" expr="property.a + 1"/></setProperties>`
  in the markup. The rows run in order, so a later row reads an earlier one; one Message History entry
  for the whole step. A route that prepared an error reply with three `setProperty` in a row now does it
  in one step.
- The VSCode graph draws it (and `setHeaders`) as one tile `set ×N`; the tooltip lists the rows
  `name = value` in order, and the properties panel edits each row's name, its kind (`=` constant,
  `ƒ` expression) and its value.

### Changed — VSCode graph: a run of setters is one tile, conditional paths are drawn

- Consecutive `setProperty` / `setHeader` steps, mixed, are joined on the graph into one tile
  `set ×N` — the file keeps its elements, only the view joins them. The panel lists the rows in the
  file's order (the order matters: a later row may read an earlier one); each row switches between
  `property` and `header` (the element is renamed), edits its name, `=` constant / `ƒ` expression and
  value, and is removed on its own; Delete removes the whole run.
- Where a message goes when a condition does not hold is drawn, dashed and colored: orange — past the
  step (`filter` not passed, a `choice` without `otherwise` with no match, `ofType` other type, a
  duplicate at `idempotentConsumer`); grey with a cross — dropped (`sample`, `debounce`); red — the
  step raises (`validate` unless `throwOnFailure="false"`, `throwException`, a `throttle` rejecting on
  overflow), captioned where the error goes: `→ catch` of the enclosing try-catch, `→ onException`,
  or `→ error`.
- A folded container is drawn as a stack of cards with a shadow, a count chip of what it hides and a
  highlighted `▸`; the fold button is a boxed control in the tile's corner. The toolbar folds or
  unfolds every container at once (⊟ / ⊞), and every route folds to its title (▾ before its name:
  the entry tile and the count of its steps stay). The properties panel is a property grid — a sticky
  title with the step's glyph, label | value rows on hairlines, a sticky action bar — and its CONTENT
  lists the element's own configuration rows only: the steps nested in a branch or a container are no
  longer shown there as blank text fields. The properties panel is widened by dragging its left edge
  (remembered for all documents); top down, the columns stand in the middle of the window and the
  gaps between steps are halved. Lines follow the tiles when the layout shifts after drawing (a
  scrollbar appearing, the window or the panel resized).

### Fixed — HTTP, SignalR, WebSocket and SOAP find the shared Kestrel host in the context's container

- A module host adds components by scanning, so `ServerManager` is never assigned, while the one
  `SharedHttpServerManager` sits in the DI container the route context was given. gRPC and AS2 already
  looked there; these four did not. `http:` refused to start a consumer at all ("SharedHttpServerManager
  is not configured" with the manager right there in the container), and SignalR, WebSocket and SOAP
  quietly started a private listener on their own port instead of multiplexing onto the shared one.
  Requested by the Tsak agent 2026-09-24 after a module route stopped answering on the shared port.
- All six now resolve the same way: explicitly assigned → the service the context was given → what the
  component did last anyway. For SignalR, WebSocket, SOAP, gRPC and AS2 that last step is their own
  server, so a hand-built component stays usable; for `http:` it stays a refusal, because an `http:`
  consumer binds a port and which port it binds is the host's decision — the message now also names
  the container as a way to supply it.
- The middle step is one method, `IRouteContext.Resolve<T>()`: the services set on the context
  (`AddService`) first, then the DI container. That order was already written by hand in three places
  (the metrics subscriber, the template options, the JSONata options) and is now in one; a host with no
  container — `AddService` and nothing else — reaches the connectors too, which it did not before.
- Nothing is required of a host that already works: `AddRedbRouteHttp()`, `AddRedbRouteSignalR()` and
  the rest keep assigning the manager, which still wins over everything else.
- Deliberately not done: a component-customizer hook in the core, where the host would push the manager
  in at `AddComponent` instead of the component looking it up. It would only work when the service
  provider is set before components are added, which the engine does not require of a host, so it would
  need the lazy lookup kept beside it as a fallback — two mechanisms for one result. The Tsak agent, the
  only host that builds contexts by scanning, confirmed its own order guarantees this but asked not to
  carry both paths.

### Changed — VSCode extension 0.2.0: the route graph draws tiles and orthogonal edges

- Every step is a tile of one size: a colored glyph in the Enterprise Integration Patterns notation
  (Hohpe/Woolf, CC BY 4.0, redrawn as 24×24 line icons) and a short caption — `from`, `bean`, `sql`,
  `http`, `set`, `choice`, `when`, `loop`, `log`, `try-catch`. An endpoint tile shows its transport
  (`bean:#x` → `bean`, the structural `<to><sql>` → `sql`, with the SQL in the tooltip) instead of the
  word `to`. The details of a step live in the tooltip and the properties panel. The toolbar switches
  the glyph over the caption or beside it; the choice is remembered for all documents.
- No container draws a box any more. A wrapper (`metered`, `loop`, `split`, a transaction…) puts its
  steps into the route's own line between its tile and an `end …` tile, so they wrap together with
  the route instead of forming a stair of nested blocks; a loop adds a `repeat` arrow from its end
  back to its start and is kept on one row when it fits one. `choice` and `tryCatch` fan out into
  branches as before, and a `filter` is drawn the same way: one branch plus a bypass for the message
  it did not let in.
- Edges run in straight segments only. The branches of a choice or a tryCatch merge on a shared bus
  right before the next step. Before, the exit of a tall container and the merge of a choice were
  drawn as diagonals across the whole drawing.
- Left to right, the steps of a row hang from one top line, so the line through the tiles stays
  straight next to a tall branching.

### Fixed — a property whose name holds a dot reads the same bare as it does in `${...}`

- `property.omni.PollInterval` written bare — in `delay`, in `setProperty expr`, anywhere a value is
  expected without `${}` — read as empty, while the same name inside `${...}`, in a comparison, and any
  dotted **header** resolved. A module that namespaces its settings (`omni.PollInterval`) is the ordinary
  case, so the value silently became nothing. Reported 2026-09-24.
- The bare value path went straight to nested access: it looked for a property called `omni` and a member
  `PollInterval` on it. It now asks `ResolvePropertySmart`, as the header branch beside it and the template
  pipeline already did — the literal name first, the nested path second. One rule for the whole language.
- A nested member of a property (`property.omni.PollInterval` over an object in `omni`) keeps resolving; a
  property carrying the literal dotted name wins over it, which is the rule dotted headers have always had.

### Documentation — container-level handlers of a markup file apply to the whole context

- The `redb.Route.Xml` README said that `onException`, `intercept*` and `onCompletion` declared under `<routes>`
  apply to the routes of their own file. The engine registers the handlers of every builder for the whole
  context, so they cover the routes of all the other files and the C# routes of the same context too. The README
  now says so and advises declaring each handler once; the engine is unchanged.

### Added — the pack gate checks the template and the JSONata spec a route names

- `<payload template="…">` and `<transformJson spec="…">` are checked the way `file=` always was: a file
  missing from `resources/` is a build error, named with the position of the attribute. The check only makes
  sense now that the worker reads these files from the package (see the entry below); before, the gate would
  have approved a file the runtime could not find anyway.
- Driven by the specs, not by a list of elements: `AttributeSpec.Resource` marks an attribute that names a
  package file, and the two package elements opt in (one flag each in `redb.Route.Templates` and
  `redb.Route.JsonTransform`). The flag is an init property, so a released contribution compiled against the
  record keeps working and simply goes unchecked until it opts in.
- Two kinds of value cannot be checked at build time and are left alone: an `assembly:` locator (the text
  ships inside an assembly) and a value that still holds a placeholder (known once configuration resolves it).
  The same now holds for `file=`, whose error also gains the attribute's position.

### Fixed — a template and a JSONata specification are found inside a package, like every other file

- `<payload template="templates/order.json.sbn">` and `<transformJson spec="…">` in a `.tpkg` failed at
  context start: both packages resolved a relative path against their own `BaseDirectory` only, which stays
  the worker's directory, while the package keeps its files in `resources/` — known to the context's
  `IRouteResourceResolver` and to nothing else. `validateXsd` and `xslt` in the very same package found
  their files, because they ask that resolver. Reported by the Route-XML agent, 2026-09-24.
- Both steps now use one lookup: the context resolver first, then `BaseDirectory`. An absolute path and
  `assembly:` are untouched, and a file in neither place fails with both places named, the way the
  validator reports its search. The failure stays a `TemplateCompilationException` /
  `JsonTransformCompilationException` — the lookup changed, the contract did not.
- The compile cache is keyed by the file that was found, not by the relative name, so two modules that
  ship the same name do not share a compiled template, and a hot-reloaded module renders its own version.
  The name in messages stays the locator the route author wrote, not the unpacked package's temp path.
- `ResourceResolution` is public (`Locate` is new), and `TextSource.FilePath` tells a package that a
  source names a file. A connector that reads a file by a relative path has no other way to follow the
  framework's search order — the same reason `PredicateFactory` went public for the file filters.

## [4.1.0] — 2026-09-21

> **Why 4.1.0 and not a patch.** This release carries breaking changes, and every one of them is in an
> entry marked *breaking* below: a route that declares no id is named differently, `streamAutoAck` is
> gone, `stream=` on an `llm:` endpoint names a mode, and the Redis, Kafka and `llm:` endpoints now
> refuse a parameter they cannot read instead of ignoring it. Each entry says what to change. The
> ecosystem ships from one number, so redb core, redb.Route, redb.Tsak and redb.Identity are all 4.1.0.

### Added — the pack gate warns about a condition that compares outside a placeholder

- `expr="${header.kind} == 'order'"` is rendered to text before it is read, and non-empty text is true whatever it
  says: the branch wins on every message, silently. The gate now names the line and the attribute and shows the two
  spellings that work. A **warning**, not an error — the route loads and runs, and what the author meant is theirs
  to decide.
- Only the mixture is reported: a placeholder outside the braces together with an operator outside them. A lone
  `${header.enabled}` renders to `true`/`false` and is read as a boolean word; a comparison written entirely inside
  the braces is evaluated, not rendered; and a template with an operator in a VALUE position is ordinary text.
- Driven by the specs, not by a list of elements: `AttributeSpec.Condition` marks a predicate position (`filter`,
  every `when`, `validate`, `loop while`, `retryWhile`, `normalize/when`), so a package contribution that declares
  its own condition is covered the same way. The flag is an init property — a released contribution compiled
  against this record keeps working.

### Added — the file consumers filter by path, not only by file name (Apache Camel parity)

- `include`/`exclude` only ever saw the file name, so "poll these three directories out of two hundred" had no answer:
  the route had to poll the parent recursively and sort it out afterwards — after downloading every file and after
  `delete`/`move` had already touched the ones it did not want. Four options now decide by path, in File, FTP and SFTP
  alike, because they live in the shared consumer:
  - `antInclude` / `antExclude` — Ant patterns over the path relative to the polled directory
    (`TYPE_A/outbox/*.csv,TYPE_B/outbox/*.csv`), where `*` stops at a separator and `**` spans levels;
    `antFilterCaseSensitive` (default `true`, as in Camel) decides whether case matters.
  - `filterDirectory` — a condition asked per subdirectory **before it is listed**, so a directory it turns down costs
    no listing and no round trip. This is what makes one consumer over a partner tree of two hundred directories
    affordable, and the only way to keep to one connection when the server allows exactly one.
  - `filterFile` — a condition asked per file before it is read, moved or deleted.
  - `filter=#myFilter` — an `IGenericFileFilter` from the registry, deciding for files and, through
    `AcceptDirectory`, for directories too. A name the registry does not hold fails when the endpoint is created.
- Anything a filter rejects is left exactly as it was found: no exchange is created for it, so post-processing never
  reaches it. That is the difference from sorting it out in the route.
- The local listing now walks the tree itself instead of asking for all directories at once — that is what lets a
  directory be skipped, and `maxDepth` now stops the descent rather than discarding what was already enumerated.
  `IFileOperations.ListFilesAsync` takes the directory predicate as an optional parameter; a custom implementation of
  that interface keeps compiling and simply ignores it.
- `PredicateFactory` is public: a connector option that takes a condition string must compile it exactly as the DSL
  does, and the markup layer learned the same lesson with `OnWhen(string)` (core, 2026-09-21).

### Changed (breaking) — a route that declares no id is named after its endpoint, without a `://`

- The default id of a route was its sanitized `From()` URI, so every `{RouteId}` log line, metric tag, health-check
  entry and dashboard label carried `direct://orders?...` — and a `/` or a `:` in a label is what breaks dashboards.
  It is now the endpoint's scheme, its path and a deterministic UUID of the whole URI, joined with `-`:
  `direct-orders-1b4e28ba-2fa1-11d2-883f-0016d3cca427`. The name holds only `A-Z a-z 0-9 _ -`, and its readable part
  is capped at 48 characters.
- The UUID is RFC 4122 version 5 in the URL namespace over the normalized endpoint URI (`RouteIdFactory`), so the same
  endpoint keeps the same id on every start, in every process and on every node — metric series, stored checkpoints
  and `controlbus:route?routeId=...` commands still point at the same route after a restart — and any UUID library
  recomputes it from the URI without this code.
- Two endpoints differing only in a parameter now get different ids. Masking used to collapse `?password=one` and
  `?password=two` into one id and the second route was refused as a duplicate, although the URIs differ.
- The query string is no longer part of the id, and `user:pw@host` credentials are dropped from the readable part: a
  secret cannot reach a log line through the id at all, rather than reaching it masked.
- Ids declared with `RouteId("orders-in")` or `id="orders-in"` in markup are untouched. Anything pinned to the old
  URI-shaped id — a saved dashboard query, a control-bus call, a stored checkpoint — needs the new id or an explicit
  `RouteId()`.

### Fixed — `OnException ... Continued()` picks the route up after the failed step, as Camel does

- `Continued()` did exactly what `Handled()` does: it suppressed the failure and the route ended there, while its own
  documentation promised routing would continue. Camel's `continued(true)` resumes at the step after the one that
  failed — it can, because its error handler wraps every step and the pipeline simply moves on.
- Here the handler wraps the whole route, so every pipeline the failure unwinds through now records the step it stopped
  at, and a continued handler replays the rest: the scope that failed first, then the steps after it. Work a
  transaction rolled back is not replayed (routing picks up after the block), nor is a `DoTry` body whose failure a
  `DoCatch` took. A failure among the resumed steps is matched against the handlers anew; it is not redelivered,
  because a redelivery re-runs the route from its first step.

### Added — the three `OnException` callbacks take a processor, not only a delegate

- `OnRedelivery`, `OnExceptionOccurred` and `OnPrepareFailure` have `IProcessor` overloads beside the `Action<IExchange>`
  ones, and both run. A markup route can only name a bean from the registry, and a bean is an `IProcessor`: bridging it
  to a delegate would have meant blocking a thread inside the error handler. Camel takes a Processor for all three.
- Both places a handler can be declared — in the route and on the `RouteBuilder` — pass them on.

### Added — `<onException>` calls beans at the three moments of a failure

- `onExceptionOccurred`, `onRedelivery` and `onPrepareFailure` take a registry reference
  (`onRedelivery="#stampAttempt"`) and run the named `IProcessor`: on every occurrence, before each retry, and once
  before the handler takes over for good. A name the registry does not hold is refused at load, by position.
- They ride the engine's processor overloads (core, 2026-09-21); the markup could not use the older `Action`
  form, because a bean arrives asynchronous and bridging it would mean blocking a thread mid-failure.
- The README of `redb.Route.Xml` documents the handler's whole policy, what `continued` resumes and what it does
  not replay (the failing step, a rolled-back `.Transacted()` block, a body whose failure a `<catch>` took), and
  that a condition is `header.kind == 'order'` — `${header.kind} == 'order'` is a template and always true.

### Added — the markup spells the whole retry policy of `<onException>`

- `continued`, `useOriginalBody`, `logStackTrace`, `logExhausted`, `retryAttemptedLogLevel` and
  `retriesExhaustedLogLevel` are attributes now; an unknown level is a positioned error, not a silent default.
- `<when expr="…"/>` decides whether this handler takes the failure at all, `<retryWhile expr="…"/>` whether
  another attempt follows — conditions of the scope, spelled the way an `<intercept>` spells its own.
- `OnExceptionDefinition` gains the string overloads `OnWhen(string)` and `RetryWhile(string)`, which compile the
  condition exactly as `InterceptDefinition.When(string)` does: a declarative route has no other way to write a
  predicate, and the markup layer cannot reach the internal predicate factory.
- `continued` resumes the route at the step after the one that failed, as Camel does — the engine gained that on
  2026-09-21 after this markup work reported the difference; `handled` still ends the route after the handler.

### Added — `<transaction>`, `<threads>` and `<metered>` take the knobs their DSL already had

- `<transaction deadLetterChannel="…" retryAttempts="4" retryDelay="00:00:05">`. Attempts and delay are one knob:
  half of it is refused by name instead of retrying after a pause nobody wrote.
- `<threads poolSize="4" maxQueueSize="100" enqueueTimeout="00:00:03">` — the queue in front of the pool is
  bounded from the markup.
- `<metered name="orders"><tag name="partner" fromHeader="partnerId"/><tag name="kind" expr="header.kind"/>` — the
  metric splits by tags; a `<tag>` takes a header or an expression, and neither (or both) is refused.

### Fixed — the component catalog was missing the engine's own schemes

- `timer`, `direct`, `direct-vm`, `seda`, `vm`, `mock`, `bean`, `controlbus`, `validator` and `xslt` never reached
  the catalog: the tool walked `redb.Route.*.dll`, and the engine is `redb.Route.dll`, which that mask does not
  match. The schemes most routes start from had no completion and no structured form; the catalog goes from 38 to
  48 components.
- The tool now reads the engine and the markup library from its own (Default) load context instead of loading a
  second copy from the scanned directory: a second copy is a DIFFERENT `IComponent`, so every connector's cast
  failed silently and the catalog came out with four components.
- The catalog also catches up with `localTransactions` (AMQP), `batchCommit` (Azure Service Bus) and the path
  filters the file consumers gained — `antInclude`, `antExclude`, `antFilterCaseSensitive`, `filterDirectory`,
  `filterFile` and `filter` on `file`, `ftp` and `sftp` alike.

### Fixed — the generated catalog schema did not compile

- A component whose structured path synonym is also one of its options — `seda`'s `queue`, `amqp`'s `path` —
  declared that attribute twice, and the schema failed to compile with «the attribute 'queue' already exists»
  (twelve times over the real catalog, the 4.0.1 one included). The option wins: it carries the type and the enum
  values, the synonym only names the path.

### Fixed — the XML twin of the SerialNumbers demo always took the first branch

- Its conditions were written `expr="${header.serials.decision} == 'Accepted'"`. With `${…}` the line is a
  TEMPLATE, and a template in a condition position renders to a non-empty string, which is true whatever the
  header holds. Spelled `header.serials.decision == 'Accepted'` it compares. A test pins that a header whose name
  carries dots is read by a condition, since that is what the demo relies on.

### Added — AMQP local transactions and Azure Service Bus batch commit for the sends of a block

- AMQP 1.0: `localTransactions=true` (`.LocalTransactions()`) commits the sends a producer deferred in a
  `.Transacted()` block in one AMQP local transaction: a coordinator link, `Declare`, every send carrying the
  transaction, one `Discharge` — all of them arrive or none. A send that cannot complete (refused, or not settled within
  `timeout` seconds) discharges the transaction as failed. Driven on the protocol's own types: the client's
  `System.Transactions` path never got an answer to its discharge, the same broker answers these at once. The broker must
  support local transactions (ActiveMQ Artemis, Qpid, Azure Service Bus; not RabbitMQ's AMQP 1.0 plugin).
- Azure Service Bus: `batchCommit=true` (`.BatchCommit()`) sends them as one batch, which the entity takes whole or not
  at all. A block whose messages do not fit in one batch fails its commit and sends nothing.
- Both are options: without them the deferred sends go out one after the other, as before.

### Fixed — Azure Service Bus batch mode dropped messages without a word

- `enableBatch=true` stopped at `batchMaxMessages` items and at the first item that did not fit in `batchMaxSizeBytes`,
  and sent the rest silently truncated. Either now fails the send, and nothing is sent.

### Fixed — Redis consumers: PSUBSCRIBE, XREAD without a group, acknowledgement, pending entries, reliable lists

- `PSUBSCRIBE` subscribed to its pattern as a literal channel name unless `usePattern=true` was added by hand, and so
  received nothing. It is a pattern subscription by itself now.
- `XREAD` without a consumer group read from the same position on every poll: with `streamStartPosition=0` it processed
  the same entries forever, with the default `>` (a group-only position) every read failed. The consumer now keeps its
  position and moves it past every entry read. The start position is unset by default: without a group that means the
  entries added from now on (the stream's last id is pinned once when the consumer starts), with a group the group is
  created at the stream's end; `>` without a group is refused.
- **Breaking:** `streamAutoAck` is gone. `streamAutoAck=false` read with NOACK while its name promised a manual
  acknowledgement. A group consumer acknowledges an entry after its route succeeded and leaves a failed one pending;
  `streamNoAck=true` (`.StreamNoAck()`) reads with NOACK (at-most-once). The old parameter is refused with this
  explanation; the dead `RedisStreamAckAction` is removed.
- `streamClaimMinIdleMs` (`.StreamClaimMinIdle(ms)`): a group consumer claims (`XAUTOCLAIM`) entries of its group pending
  for at least that long — a failed entry of its own, or one a dead consumer left — and processes them again.
- `processingList` (`.ProcessingList(key)`): a `BLPOP`/`BRPOP` consumer moves each item there (`LMOVE`) and removes it
  after success; a failed item goes back to the head of the queue, and what a previous run left is returned at start.
  Without it the consumer pops, and a failed item is gone, as before. Both still poll: a blocking pop would hold the
  connection the whole process shares.
- A Redis endpoint refuses a parameter it cannot read (a misspelt option, a value of the wrong type), by name, as the
  Kafka and `llm:` endpoints do. The Redis README is rewritten where it did not match the code (the URI format, the
  namespace, the connection factory example, the method table) and gains a table of what each consumer guarantees.

### Added — Kafka transactions and exactly-once consume-process-produce (`transactionalIdPrefix`)

- `transactionalIdPrefix` (`Kafka.Topic(...).TransactionalIdPrefix(...)`) switches a producer to Kafka transactions.
  The sends it defers in a `.Transacted()` block commit as one Kafka transaction when the block commits, after the
  database: all of them or none, and `read_committed` readers (librdkafka's default) never see an aborted one. A send
  outside a block is a transaction of its own.
- When the route started from a Kafka consumer of the same cluster, the consumed offset commits in that transaction
  (`SendOffsetsToTransaction`) and the consumer does not commit it itself: the output and the offset move together,
  exactly-once within Kafka. With `enableAutoCommit=false` the offset stays the application's.
- The prefix names the producer; the connector appends the machine name, the process id and the producer's number,
  so nodes deploying one configuration, and two producers of one process, never fence each other. A fatal producer
  error rebuilds the producer with the same id before the next transaction.
- Unlike camel-kafka, the Kafka transaction spans the block's commit, not the whole exchange: a producer holds one open
  transaction at a time, and one held for a whole exchange would make every exchange wait for the one before.

### Changed — a Kafka endpoint refuses a parameter it cannot read

- A misspelt option, or a value of the wrong type, used to be dropped without a word: a misspelt
  `transactionalIdPrefix` would have left the producer without transactions. It is refused by name when the endpoint
  is created (the value is not echoed: it may be a secret), as the `llm:` endpoint does. A `transactional.id` in
  `additionalProperties` is refused too: it would put the client into transactional mode with nobody opening
  transactions.

### Fixed — `OnCompletion` and the exchange events reach the consumer's verdict

- A route whose `OnException` has no `Handled` returns normally with the failure left on the exchange, and
  `.RollbackAll()` returns normally with the exchange marked rollback-only; the consumer acknowledges neither. The
  `OnCompletion` blocks and the exchange events (`OnExchangeCompleted` / `OnExchangeFailed`, and so `NotifyBuilder`)
  judged by the escaping exception alone and took both for completions: `OnFailureOnly()` did not run,
  `OnCompleteOnly()` did, listeners heard "completed". Both are failures now; a handled failure stays a completion.

### Fixed — the idempotent consumer settles its key when the exchange ends, as Camel does

- The key was confirmed at the end of the `IdempotentConsumer` block. A step after the block that failed, or
  `.RollbackAll()` inside it, left the key confirmed while the consumer did not acknowledge the message: the redelivery
  was skipped as a duplicate and the message was lost. The key is now settled when the exchange's unit of work ends —
  confirmed on success, removed on failure anywhere in the route (Camel's defaults `completionEager=false`,
  `removeOnFailure=true`) — before the route returns to its consumer, so the key is back before the message goes back
  to the broker and a redelivery another node picks up at once is processed.
- `OnException` redelivers the whole route, where Camel redelivers the failed step: the exchange that holds a key is no
  longer a duplicate of itself. A block that failed runs again; a block that succeeded is skipped on the redelivery,
  without the `CamelDuplicateMessage` mark it used to get.
- A key claimed inside `.Transacted()` follows the transaction: it commits with the work and goes with a rollback.
  `IIdempotentRepository.JoinsAmbientTransaction` (default false, true for `RedbIdempotentRepository`) says whether the
  repository's writes are part of the transaction; for one that is not — the in-memory repository — the processor
  removes the key when the transaction rolls back. It used to keep such a key, and the redelivery of work that had
  rolled back was skipped.
- `SqlIdempotentRepository` (`redb.Route.Sql`) reports `JoinsAmbientTransaction` from its connection: true on PostgreSQL
  (Npgsql) and SQL Server (SqlClient), whose connections enlist in the route transaction, false on other providers
  (Microsoft.Data.Sqlite does not enlist) and with `Enlist=false`. It reported false everywhere, so after a rollback on
  PostgreSQL and SQL Server the consumer deleted a key the rollback had already taken back, and in a cluster that delete
  could remove the key another node had claimed meanwhile.
- `SqlIdempotentRepository` and `SqlClaimCheckRepository` create their table outside the route transaction, as Camel
  creates it outside any exchange. The first call used to run the `CREATE TABLE` inside the transaction of whatever message
  came first: when that transaction rolled back, PostgreSQL and SQL Server took the table back with it, the repository
  still took it for created, and every later call failed ("relation does not exist", "Invalid object name") until the
  process restarted. On MySQL and Oracle the same DDL would have committed that transaction implicitly.
- A Split or Multicast branch is a unit of work of its own. Split now records a failed branch's exception on the
  branch, as Multicast and RecipientList already did.

### Added — `ExchangeResources.OnCompletion`: work on the outcome of an exchange

- `ExchangeResources.OnCompletion(exchange, IExchangeCompletion)` is Camel's `addOnCompletion` with a
  `Synchronization`: `OnComplete` or `OnFailure` runs once when the exchange's unit of work ends. The outermost route
  the exchange entered owns the unit of work and ends it before returning to its consumer; a route called with the same
  exchange (`direct:`) shares it; an exchange no route owns (a Split or Multicast branch, the copy `.Threads()` goes on
  with) ends it when it is released. Failure is the consumer's verdict: an escaping exception, a failure left unhandled
  on the exchange, or a rollback-only mark. `ReleaseWithExchange` stays for resources that are released either way.

### Changed — the sends of a block leave in order, and one producer's sends commit in one broker transaction

- The deferred actions of a `.Transacted()` block were kept in a dictionary and committed in whatever order it gave:
  two sends to one queue in a block could arrive swapped. They now commit in the order the route registered them.
- IBM MQ and RabbitMQ commit the deferred sends of one producer in a block in one transaction of their own, so they
  arrive together or not at all: IBM MQ puts them all under syncpoint and issues one `MQCMIT` (a failed put backs them
  all out); RabbitMQ publishes them on a transacted channel of the producer's own and issues one `tx.commit`. The
  producer's batches take turns on its connection. Atomic means per producer: sends through two producers, or to two
  brokers, still commit one after the other.
- `TransactedActions.JoinBatch(exchange, key, create, endpoint)` lets a producer of your own keep one batch per block.
- AMQP 1.0 keeps sending one message after the other: a batch in an AMQP transaction through the client's
  `System.Transactions` support never got an answer to its discharge from the broker and hung until the connection's
  idle timeout. Kafka transactions and Azure Service Bus are separate work.

### Documentation — the outbox recipe for routes that do not start from a broker

- `TRANSACTIONS.md` shows the transactional outbox as two ordinary routes, as in Camel: the event is a row written in
  the same transaction as the work, and a `sql:` poll relays unsent rows and marks each one after the send. It closes
  the one window the database-first order leaves open (the process dies after the commit) for routes started by HTTP,
  a timer or a file, where nothing delivers the request again.

### Changed — Kafka producers default to `acks=all` and an idempotent producer

- The Kafka 3 client and Camel 4 default to `acks=all` with idempotence on; the connector defaulted to
  `acks=leader`, weaker than both, and turned idempotence on only with `transacted=true`. `acks` now defaults to
  `all` (endpoint and `KafkaConnectionFactory`), and the new `enableIdempotence` (`.EnableIdempotence()` in the
  builder), left unset, follows the effective `acks`: on with `all`, off otherwise. `enableIdempotence=true` with
  another `acks`, and `transacted=true` with an explicit `acks` other than `all` (it used to be overridden silently),
  are refused when the endpoint is created. A route that wants `acks=leader` sets it explicitly.

### Fixed — `RequestBody` refuses a reply it cannot hand over alive, and a reply that does not convert throws

- `ProducerTemplate.RequestBody` ends the exchange before it returns, so a reply that reads from resources of its
  exchange (a streamed `sql:` result with `outputType=StreamList`) came back already released and failed at its first
  read with "was released when its exchange ended". Such a body now carries the new marker `IExchangeBoundBody`, and
  `RequestBody` refuses it at the call with `InvalidOperationException` that points to `RequestAsync`: read the body,
  then dispose the exchange. The TestKit helpers `RequestBody<T>` and `RequestBodyAndHeaders<T>` do the same. Found by
  the Llm connector review; the `sql:` stream takes the marker in its own change.
- `ProducerTemplate.RequestBody<T>` swallowed a failed conversion and returned `default`: a reply of the wrong type
  came back as `0` or `null`. It now throws `InvalidCastException` naming both types. A nullable `T` converts to its
  underlying type, so `RequestBody<int?>` on the reply `"42"` returns `42` instead of `null` (the TestKit helpers threw
  there).

### Added — the pack gate names steps that never run

- `<stop/>`, `<throwException>` and, since the transactions work, `<rollbackAll/>` end the route for the
  exchange. A step written after one of them in the same list never runs; the gate warns with the position of
  the first such step. The property is part of the element spec (`ElementSpec.Terminal`, an init-only
  property so contributions compiled against 4.0.1 keep binding), so a package element that ends the route is
  checked the same way.
- The component catalog follows the tri-state `transacted` of the producers: the options that became
  `bool?` lose their false default (unset now means «wait for the enclosing transaction»), and `wmq`, `sqs`
  and `sns` gain the attribute as `xs:boolean`. The structured endpoint form keeps an unwritten attribute
  out of the URI and carries an explicit `false` into it; a test pins both.

### Changed — inside `.Transacted()` a broker send joins the transaction unless `transacted=false` says otherwise

- The rule is Camel's: inside a transacted route, a send through a transactional endpoint is part of the unit of work.
  Until now a producer deferred its send only with `transacted=true`; without it a `.To(broker)` inside `.Transacted()`
  went out at once, before the database committed and even when the block rolled back, although the guide and the
  examples showed it deferred. `transacted` on a producer is now `bool?`: unset follows the block (deferred inside
  one, at once outside), `false` sends at once even inside a block, `true` requires a block. The fluent builders take
  `.Transacted(false)`.
- Covered: RabbitMQ, AMQP 1.0, Kafka, IBM MQ, and now Azure Service Bus, SQS and SNS, which had no deferred send at
  all. Kafka's `transacted=true` still also makes the producer idempotent.
- Redis follows the same rule for what announces work: `PUBLISH` and `XADD` wait for the commit inside a block, and
  the entry id and the recipient count reach the headers after it. Other writes run at once unless `transacted=true`,
  and reads and pops (`GET`, `LPOP` and the like) refuse `transacted=true` when the route starts.
- A request-reply producer (`replyTo=true`) always sends at once and refuses `transacted=true` at start: a request
  held back until the commit would wait for a reply that cannot come.
- The AMQP 1.0 and Azure Service Bus clients enlist a send in the ambient `System.Transactions` transaction on their
  own, so an "immediate" send inside a block was in fact enlisted, and next to a database it would escalate the
  transaction to a distributed one. A send that leaves at once now runs with the ambient transaction suppressed, and
  deferred actions run outside any transaction.
- **Upgrading:** a route with `.Transacted()` whose broker send must leave even when the work rolls back needs
  `transacted=false` on that endpoint. `TransactedActions.Defers(exchange, transacted)` and
  `TransactedActions.RegisterSend(...)` give a producer of your own the same behaviour.

### Fixed — a block inside a block follows its transaction policy

- A `.Transacted()` inside another one committed or rolled back every deferred send of the exchange when it ended,
  the outer block's too, and before the outer database commit. Now a `Required` (or `Mandatory`) inner block joins the
  outer one and leaves its sends to the outer commit, and a `RequiresNew` or `Suppress` block keeps a set of its own
  and settles only its own sends.
- `.CommitTransaction()` sent the deferred messages before it committed the database, the reverse of the order
  `.Transacted()` uses. It now commits the database first, and after a send fails past the commit it rolls back what
  is left and propagates. `.BeginTransaction()` follows the same nesting rules.
- `seda:`, `vm:` and an InOnly `.Threads()` hand a copy of the exchange to another flow, and the copy carried the
  sending block's set of deferred actions: a send on the other side went into a set that the sending block may already
  have committed, and was lost. The hand-off now starts a unit of work of its own, as in Camel. A block also closes its
  set when it ends, so any copy that outlives it (an aggregator's, a resequencer's) sees no block at all.

### Fixed — `.RollbackAll()` rolls the transaction back, as Camel's `markRollbackOnly()` does

- `.RollbackAll()` rolled back the deferred sends and set a `RollbackOnly` property that nothing read: the route went
  on, the enclosing `.Transacted()` committed the database, and the consumer acknowledged the message. It now marks
  the unit of work for rollback and stops the route; the block rolls back the database and the sends without an
  exception, a joined inner block takes the whole unit of work with it, and an open `.BeginTransaction()` is rolled
  back on the spot. The consumers handle a rollback-only exchange as a failed delivery and do not acknowledge it:
  RabbitMQ, AMQP, IBM MQ, MQTT, Redis, Azure Service Bus and SQS deliver the message again, Kafka follows its failure
  setting (`breakOnFirstError`), and the file consumers take their failure path.
  `ExchangeFailureExtensions.MarkRollbackOnly()`, `IsRollbackOnly()` and `EndedInFailure()` expose the mark.

### Fixed — Redis: a deferred write is the same write, and binary bodies stay binary

- A deferred Redis write (`transacted=true`) went through a separate, reduced code path: it dropped the TTL, used a
  fixed hour for `EXPIRE`, turned the body into text and did nothing at all for `HMSET`, `GEOADD`, `PFADD`,
  `PFMERGE` and `SETBIT`. It now runs the same operation after the commit, on a snapshot of the exchange taken at the
  step. Found in the Redis connector review (item 4).
- `SET`, `SETNX` and `PUBLISH` wrote a `byte[]` body as the text `System.Byte[]`; they now write its bytes, as the
  other operations did (item 6).

### Fixed — a transacted send outside `.Transacted()` fails instead of disappearing

- A producer with `transacted=true` defers its send or write until the enclosing `.Transacted()` block has
  committed the database. On a route without the block, or on a step after the block has ended, each producer
  (RabbitMQ, AMQP 1.0, Kafka, IBM MQ, Redis) created the set of deferred actions itself; nothing ever committed
  it, and the write was silently lost. The block now owns the set: it opens it on entry and removes it on exit,
  a nested block reuses the outer one, and the producers register through `TransactedActions.Register`, which
  throws `InvalidOperationException` naming the endpoint when no block is active. The imperative block does the
  same: `.CommitTransaction()` and `.RollbackTransaction()` remove the set that `.BeginTransaction()` opened, so
  a transacted send after them is refused too. Found in the Redis connector review (item 5).

### Changed — the graph editor has two layouts, both in the mermaid look

- `⇢ mermaid`, the default: the left-to-right snake with the shapes and curves of the mermaid look. A
  step is left on its right and entered on its left, a branching fans out from a decision diamond, a
  bracket shows its title beside the body, and a wrapped row returns along the lane between the rows
  with rounded corners. `⇣ mermaid` is the same drawing top down. One overlay draws the curves of both
  directions.
- The layouts drawn with CSS borders are retired, the plain top-down `columns` and the plain snake with
  its elbows: the two mermaid layouts are those arrangements drawn with curves. A document that
  remembered a retired layout opens in the default one.
- The tooltip of a branch carries its whole condition. The label is capped so that a stack of branches
  stays readable; the tooltip was capped with it and showed the same cut text.

### Fixed — the graph editor shows the flow leaving a bracket

- In the Mermaid-look layout the branches that end a bracket (`transaction`, `filter`, `try`) stopped
  inside the box: no line came out of their last steps although the flow goes on after the scope. Every
  tail of the last inner step (every branch, when it is a branching) now curves to the middle of the
  bracket's bottom edge, where the edge to the next step starts.

### Fixed — `redb-route-xml csharp` and `mermaid` work on a route project

- A route under `{project}/routes/` references its schemas by file name, and the package keeps them in
  `{project}/resources/`, where the pack gate and the Tsak loader look. The generator and the diagram
  command looked next to the route file instead, so both failed on `<validateXsd file=…>` of any real
  project. `RoutePackage.ResourceRootFor(routeFile)` states the rule once, and both use it; a loose route
  file still reads its own directory.
- `mermaid --bin <build output>` loads the project's types and the package elements of its build
  output, the same way `check` and `pack` do: a route that names `<unmarshal target=>` of its own or
  uses `<redbSave>` draws. Without `--bin` such a route stops with a hint to pass it.

### Fixed — the pack gate resolves every type a route names

- With the built assemblies at hand (`--bin`, or the build-time `PackRouteOnBuild` target) the gate
  checked bean types and methods but not the other type attributes, so a typo in `<unmarshal
  target=>` or an unknown exception in `exceptions=` reached the worker and failed there at load.
  The gate now resolves every attribute the element specs declare as a type, walking the specs
  generically, so package contributions are covered the same way; each miss is an error with its
  position. Found on the SerialNumbers XML demo: `exceptions="…ValidationException, redb.Route"` is
  a LIST, the assembly suffix split into a second entry that does not exist.
- `AttributeType.TypeNameList` (appended last) marks list-valued type attributes, so the gate
  splits them on commas while a single `TypeName` keeps its assembly-qualified form. The enum's
  numeric values are pinned by a test: package contributions compiled against an earlier version
  store them.

### Changed — breaking — an `llm:` endpoint refuses a parameter it cannot read

- A parameter no `llm:` option has (`tempreature=0.2`), or a value that does not convert to the option's type
  (`maxIterations=abc`), used to be dropped without a word: the endpoint ran with the option's default. Such a
  parameter now fails the endpoint's creation, each one named, with the nearest option for a misspelt name
  (`did you mean 'temperature'?`) and the expected type or values for a bad value.
- **Migration:** a route whose `llm:` URI carries a parameter the endpoint does not know stops at start instead of
  running without it; remove the parameter or correct its name.

### Changed — breaking — `stream=` names a mode, and both modes run the agent engine with its tools

- `stream=calls` (DSL: `.Stream(LlmStreamMode.Calls)`) — the agent engine makes every model call as a stream,
  inside the route. Tools, the conversation, the budget, approvals, the audit and the route's `.Transacted()` work
  exactly as without streaming: the loop goes on with the whole answer the stream assembled. The pieces go to the
  new `IAgentObserver.OnDeltaAsync` as the model writes them, visible text and thinking marked apart
  (`AgentDeltaKind`); `Out.Body` is the final text. The connection carries data while the model writes, so a long
  answer from a tool-using agent survives tunnels and proxies that cut a silent connection, over HTTP/1.1 too. A
  provider whose stream ends without the assembled answer (`LlmStreamChunk.Response`) fails the call.
- `stream=body` (DSL: `.Stream(LlmStreamMode.Body)`) streams the text into `Out.Body` as an `IAsyncEnumerable<string>`
  for HTTP (SSE), WebSocket and gRPC consumers, as `stream=true` did, but no longer bypasses the engine: the agent run
  — tools, conversation, budget, approvals, audit — happens when the body is read, after the route, the way a
  streamed SQL result is read, and the visible text of every model call streams as the model writes it (thinking
  stays out). `tools=` with it is allowed. The body belongs to its exchange (`IExchangeBoundBody`): it is read once,
  released with the exchange (a read after the exchange ended fails, a run in progress is stopped), and refused by
  `ProducerTemplate.RequestBody`, which ends the exchange before it returns. It is refused inside `.Transacted()`:
  the block would commit before the answer exists, and the tools would not join it; use `stream=calls` there. The
  reply's `Content-Type` is `text/event-stream` whatever the request carried: a `text/plain` request used to make the
  HTTP consumer drop the SSE framing and the summary event.
- **Migration:** `stream=true` is refused when the endpoint is created, with both modes named; use `stream=body` for
  what it did. `.Stream()` without a mode is gone. Any other value is refused too: it used to be ignored silently, so
  a misspelt mode did not stream at all.

### Fixed — a streamed model call assembles the same answer as a plain one

- A streamed call now ends with the whole answer, `LlmStreamChunk.Response`: exactly what `CompleteAsync` returns
  for it — blocks in the order they arrived, thinking with its signature, redacted thinking, tool calls, usage, the
  stop reason and its raw value, the response id. The stream is assembled into the JSON a plain call returns and read
  by the same parser, so the two forms cannot drift apart. Streaming through the agent engine is built on it.
- The model's thinking is no longer dropped from a stream: DeepSeek's `reasoning_content` and Anthropic's
  `thinking_delta` arrive as `LlmThinkingBlock` pieces, apart from the visible text, and Anthropic's signature and
  redacted thinking are kept for the hand-back.
- A frame that cannot be read, and an error frame inside an OpenAI-compatible stream, used to be skipped: the answer
  went on shorter than the model wrote it. Both now fail, and the message shows the frame.
- A stream that ends before the model said why it stopped (no `finish_reason` / `stop_reason`) used to count as a
  finished answer. It is a cut one, and now fails.
- The OpenAI-compatible stream no longer reads synchronously (`StreamReader.EndOfStream`) in its asynchronous loop,
  which held a thread-pool thread for every wait between frames.

### Changed — `RequestTimeoutMs` limits the whole model call; the default is ten minutes

- `LlmConnectionFactory.RequestTimeoutMs` was the client's `HttpClient.Timeout`, and every provider reads the
  answer with `ResponseHeadersRead`, under which that timeout stops at the response headers. So one option meant
  two things. A server that sends the headers at once and the body when the model has finished (DeepSeek does)
  was never limited: the generation could run for any time, and a server that stalled after the headers was waited
  for forever. A server that answers only when done was limited for the whole generation. A client passed in by
  the host was never limited by the factory at all.
- Now the limit covers the whole call, waiting for the answer and reading it, for the chat providers
  (OpenAI-compatible and Anthropic), transcription and embeddings, whatever client sends the request. The
  package's own clients no longer carry a `Timeout`; a client passed in by the host keeps its own as well.
- Running out throws `LlmTimeoutException` (a `TimeoutException`) naming the provider, the factory and the limit.
  It used to be a `TaskCanceledException`, which the agent engine reported to observers as a cancelled run. A
  cancellation by the caller is still an `OperationCanceledException`.
- The default goes from 120 000 to 600 000 ms, the ten minutes of the official Anthropic and OpenAI SDKs: a
  non-streaming answer from a thinking model takes minutes. A factory that relied on the old default now waits up
  to ten minutes; set `RequestTimeoutMs` to keep a shorter limit.
- A streamed call is limited the same way, from the send to the last piece. `StreamIdleTimeoutMs` (new, off by
  default) limits a stream's silence: how long the provider may send nothing at all, not even a keep-alive comment or
  `ping`. It counts only the waits on the provider, not the time the reader spends between pieces; running out throws
  `LlmTimeoutException` with `Kind = StreamIdle`. It is off by default because some reasoning models stream nothing
  for minutes before their first token.
- Code that caught `TaskCanceledException` to detect a model timeout should catch `LlmTimeoutException` (or
  `TimeoutException`).

### Fixed — every model client keeps a long silent call alive, not only Anthropic's

- A non-streaming model call is one request whose answer arrives only when the model has finished, with nothing
  on the wire meanwhile, and VPN tunnels, NAT and proxies drop a TLS connection that is silent for about 50
  seconds. The Anthropic client already asked for HTTP/2 and sent a keep-alive PING every 15 seconds while a
  request was in flight; the OpenAI-compatible client (DeepSeek, OpenAI, Groq, Gemini and the rest), the
  transcription client and the MCP HTTP+SSE transport did not, and a long thinking call through such a network
  was cut. Their default clients now do the same, and every request they build carries the client's HTTP version.
  A client the caller passes in is used as it is. Over HTTP/1.1 and on plain `http://` endpoints nothing changes.
  Embeddings are left as they were: their calls are short.
