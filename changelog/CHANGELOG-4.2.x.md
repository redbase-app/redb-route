# redb.Route changelog: 4.2.x

Releases of the 4.2 line, moved here unchanged from CHANGELOG.md, which keeps the latest released line only.
Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [4.2.3] — 2026-10-08

### Fixed — the XSD validator rejects a root element the schema does not declare

- A document whose root element is not a global declaration of the schema is refused now. The .NET
  schema processor reports an element it has no declaration for as a *warning* ("Could not find schema
  information for the element 'x'") and skips its subtree, and warnings are not delivered unless
  `ReportValidationWarnings` is set. So an unknown-namespace root, a foreign-namespace root and a
  no-namespace root all passed validation and only failed later, when the body was parsed. `XsdValidator`
  now checks the document's root against the compiled schema set (`XmlSchemaSet.GlobalElements`) before
  validating and fails it the way Apache Camel does (`cvc-elt.1.a`), because Camel's JAXP/Xerces parser
  treats an undeclared root as an error. Enabling `ReportValidationWarnings` instead was rejected: it
  would also turn legitimate warnings (an `xs:any processContents="lax"` element with no schema for one)
  into failures, which is stricter than Camel. This applies to every entry point: `.ValidateXsd(...)`, the
  `validator:` component and `<validateXsd>` in Route-XML.
- **Breaking.** A document that used to validate because its root was not declared (a foreign or absent
  namespace) is a failure now. A consumer that relied on it should send a document whose root the schema
  declares. Covered by `ValidatorTests` (`XsdValidator_RootWithoutNamespace_ReturnsFailure`,
  `XsdValidator_RootInForeignNamespace_ReturnsFailure`).

### Fixed — the idempotent consumer gives back the key a handled failure has to return

- An `OnException` handler with `Handled(true)` answers the exchange: the consumer acknowledges and the route
  reports success. The idempotent consumer read that as "the work was done" and confirmed the key it had
  claimed, so a redelivery of that message (the broker's own, or a requeue after a restart) was skipped as a
  duplicate and the work was lost for good, with nothing in the log but the handler's own line. A handled
  failure now gives the key back, exactly as an unhandled one does. Camel marks the exchange inside the error
  handler (`ExchangeHelper.setFailureHandled`) and `IdempotentOnCompletion` asks that mark, `removeOnFailure`
  being `true` by default. The mark is a flag of its own, separate from the ack decision: a handled failure is
  still acknowledged, which is the point of `Handled(true)`.
- Only `Handled(true)` sets the mark. `Continued(true)` clears it (the route carries on with the exchange, so
  the key stays confirmed), a failure caught by `DoCatch` never sets it (a catch says nothing about the work,
  so the key stays), and a redelivery clears it before the next attempt. A key claimed inside `.Transacted()`
  still follows the transaction, not the handler.
- The flag is public for anyone who needs the same rule: `MarkFailureHandled()`, `ClearFailureHandled()` and
  `IsFailureHandled()` on `IExchange`, in `ExchangeFailureExtensions`.
- Covered by `IdempotentUnitOfWorkTests`: `A_handled_failure_in_the_block_takes_the_key_back`,
  `A_handled_failure_after_the_block_takes_the_key_back`,
  `A_handled_failure_after_the_redeliveries_takes_the_key_back`, `A_continued_failure_keeps_the_key`,
  `A_failure_caught_by_do_catch_keeps_the_key`,
  `A_handled_failure_inside_a_transaction_still_rolls_the_transaction_back`.

### Changed — a handled failure is no longer logged as an exhausted failure; VS Code extension 0.2.20

- `Retries exhausted for <type> after N attempts`, with the exception and its stack trace, went to the log at
  `Error` level for a failure the handler had already answered. For a route that handles a known condition by
  design (a partner's 404, a validation rejection) every handled message produced an entry that reads like an
  incident. The engine asks before it writes now: a failure the handler handled is logged only when
  `LogHandled(true)`, one the handler continued only when `LogContinued(true)`, both `false` by default, as
  Camel's `logHandled` / `logContinued` are. An unhandled failure is logged as before, and so is a failure no
  handler matched.
- A rollback-only exchange (`.RollbackAll()`, `.MarkRollbackOnly()`) bypasses both checks: its work was rolled
  back, so the failure is real even when a handler answered a client. Camel's
  `RedeliveryErrorHandler.logFailedDelivery` skips the checks under the same condition,
  `!exchange.isRollbackOnly()`.
- Both knobs are on the definition and in Route-XML: `.OnException<X>().Handled(true).LogHandled(true)` and
  `<onException handled="true" logHandled="true">`, `logContinued` likewise. `LogExhausted(false)` still
  silences the entry altogether, handled or not.
- **Breaking.** A route that counted on the entry for a handled failure does not get it any more: put
  `LogHandled(true)` next to `Handled(true)` to keep it.
- Covered by `OnExceptionProcessorTests` (`HandledFailure_NotLogged_ByDefault`,
  `HandledFailure_Logged_WhenLogHandled`, `ContinuedFailure_NotLogged_ByDefault`,
  `ContinuedFailure_Logged_WhenLogContinued`, `HandledFailure_OnRollbackOnlyExchange_IsLogged`,
  `UnhandledFailure_StillLogged`) and by `RetryAndScopeKnobsTests`
  (`OnException_LoggingAndRetrySettings_ReachTheGeneratedCode`) for the XML round trip.
- The route describer lists both knobs (`logContinued=false logHandled=false` next to `logExhausted`), which is
  why the example golden of the Route-XML examples changed: a deliberate regeneration
  (`REDB_XML_GOLDEN_REGEN=1`), with no parser change behind it.
- The editor's resources are regenerated with both knobs and the extension is 0.2.17: the schema the editor
  validates against (`redb-route-1.0.catalog.xsd`, the one `catalog.xml` binds the namespace to) and
  `redb-route-1.0.xsd` know `logHandled` and `logContinued`, and `redb-route-elements.json` offers them in the
  property panel. Without it the editor flags `logHandled` as an attribute the element does not take.
- The same regeneration carried a fix that was overdue: `<route tracing="false">` was missing from the editor's
  schemas. The attribute reached the engine in 4.2.2, after the resources were last regenerated,
  and nothing noticed, since no test reads them. `EditorResourcesTests` now pins the core element list against
  the committed schemas and `redb-route-elements.json`, so a forgotten regeneration fails the build instead of
  shipping an editor that refuses a released attribute.
- The case-insensitive enum union is written for a short member list only (up to three members) and for a
  `[Flags]` option, whose comma-joined form is a pattern and nothing else. The editor's language server
  (LemMinX on Apache Xerces) died with an out-of-memory error on the first completion in a workspace without
  a project binding: the union spells every member out as character classes, and the catalog's 43 long
  pick-one lists (`fileExist`, `sortBy`, `postProcess`, `operation`) sit at the same sites the short ones do,
  so the schema had grown to 21k lines and 1.1 MB, half again what the extension carried before. Those long
  lists keep the plain member list now. The trade-off is the spelling: the schema takes the names the catalog
  lists
  (`fileExist="Append"`), and another case is marked although the engine parses it.
  `CatalogEnumOptionsTests` pins both halves of that.
- A connector's options are declared once now, not once per endpoint position. The schema inlined the whole
  option set of every scheme at all six positions that take an endpoint (`from`, `to`, `enrich`, `pollEnrich`,
  `wireTap`, `interceptSendToEndpoint`) and on both sides, so each connector stood in the file twelve times
  over: 12 declarations per option, 312 endpoint positions, one named complex type in the entire file.
  A language server builds a content model per complex type, so those copies multiplied exactly what it pays
  for. One named type per (component, side), referenced by every position, is the whole difference: the
  catalog schema is 7,612 lines and 353 KB now, against 18,912 and 1.0 MB, and the case-insensitive unions
  fall to 83 with it. A document sees none of this, the same file validates the same way.
- The skeleton completion knows where it is now (extension 0.2.20). It offered every composite skeleton
  at every element position, so `<choice>` and `<tryCatch>` were suggested directly under `<routes>`,
  where the loader refuses them ("`<choice>` is not valid at the container level") and where the schema
  never offered them; the `<routes>` skeleton was offered only inside a document that already had that
  root, so it could never be right and is gone. The place is read from the generated element list
  (`allowsSteps`, the same list the palette reads): beside the routes a `<route>`, inside any element
  that holds steps — a route, a choice branch, a tryCatch handler, an `onException` scope — the step
  skeletons, and nothing anywhere else. `skeletonscope.test.ts` pins it, including the half-typed
  document a completion request arrives on (the tree parser refuses that by design, so the context is
  read textually).
- The mirror gate (`publish-route-public.ps1`, step 8b) verifies `media/redb-route-1.0.catalog.xsd` against the
  tree now as well. It was the one media file the editor reads and the only one missing from the byte-for-byte
  list, so a stale schema could have shipped next to documents written for the new one.

## [4.2.2] — 2026-10-05

### Fixed — a downstream timeout reaches error handling, instead of passing through as a cancellation

- A timeout of a component's own deadline — an HTTP producer's `HttpClient.Timeout`, an RPC reply deadline —
  is raised as an `OperationCanceledException` (a `TaskCanceledException`), and every EIP handler rethrew any
  `OperationCanceledException` by type: `TryCatch`/`Catch<T>`, `OnException`, `Retry` and `DeadLetterChannel`
  all skipped it, and the exchange failed whole with a `500`. A cancellation is what the caller's token asked
  for; an `OperationCanceledException` raised while that token is still live is a failure, and error handling
  now sees it.
- Error handling names such a timeout a `System.TimeoutException`, keeping the original as `InnerException`, so
  a route can catch it by name: `Catch<TimeoutException>`, `OnException<TimeoutException>()` and
  `<catch exceptions="System.TimeoutException">` in Route-XML. A genuine cancellation (the caller's token
  fired) is still never handled, retried, dead-lettered or swallowed. The exception's text carries only the
  sanitized target URL, never a body or credentials.
- The rule lives once now, in `redb.Route.Core.RouteCancellation`, and is public for callers who need the same
  one: `IsCancellation(ex, ct)` (a cancellation is what the caller's token asked for), `IsOwnedTimeout(ex, ct)`,
  `Normalize(ex, ct[, message])` (name an owned timeout a `TimeoutException`) and `IsCancellationType(type)`.
  `HttpProducer` uses it; the inline predicate in the other transports is left as it is, a follow-up rather
  than part of this fix.

### Added — a dead `Catch<T>` / `OnException<T>` on an `OperationCanceledException` is warned about at load

- A `Catch<T>` or `OnException<T>` whose `T` is an `OperationCanceledException` (`TaskCanceledException` above
  all) can never fire: a genuine cancellation is filtered out and a timeout arrives as a `TimeoutException`.
  The route load now warns, naming the type and pointing at `System.TimeoutException`, instead of leaving the
  dead branch to be found by a red test. The route still builds.

### Fixed — a route reached through `direct:` / `direct-vm:` keeps its own `redb.route.id`

- A `direct:` / `direct-vm:` call runs the target route's pipeline on the CALLER's exchange, and the target's
  first step stamps `exchange.RouteId`, which nothing restored. After `to("direct:callee")` the caller carried
  the callee's id, so `route:caller`'s span tag and the caller's later step metrics — both read the live
  `exchange.RouteId` — were attributed to the callee. The caller's id is saved and put back when the target
  returns (on failure too), so "current route" means the route actually executing, the rule
  `IExchange.Context` already follows. `vm:`/`seda:` clone the exchange and are unaffected.

### Fixed — `<split stopOnException="false">` in Route-XML is no longer dropped

- The `<split>` loader passed the attribute only when it was `true`
  (`if (ctx.Convert<bool>(e, "stopOnException") == true) scope.StopOnException();`), and the default is `true`,
  so `stopOnException="false"` was ignored: a failure in one element stopped the whole batch — the batching
  pattern the attribute exists for could not be written at all. The loader now passes the value through as it
  is, as `<scatterGather>` already did, and the C# generator prints `StopOnException(false)` (a bare
  `StopOnException()` for `true`) instead of losing the `false` case. `<multicast>` carried the same
  loader/printer pattern and is fixed too — harmless there so far, its default being `false`.

### Added — a bean method's `IExchange` and `IMessage` parameters come from the binding

- `bean:#x?method=Require('incident.view')` now binds a method `Require(IExchange exchange, string permission)`:
  an `IExchange` parameter is the exchange itself and an `IMessage` parameter its In message, both supplied by the
  binding like a Camel bean's `Exchange` / `Message`, and neither counts among the arguments. A `CancellationToken`
  is filled in wherever it stands, not only as a trailing parameter. Was: the parameters had to be the arguments
  themselves plus an optional trailing token, so a bean that needed both an argument and the exchange had to read the
  argument from a property first, two steps instead of one. The method's remaining parameters are the arguments, in
  order; the XML form takes the same URI, so the route moves to Route-XML unchanged.

### Added — a route may switch tracing off on its own: `.Tracing(false)` / `<route tracing="false">`

- `RouteEngineOptions.EnableTelemetry` is context-wide, so a service route (a health check, a metric summary on a
  timer) had no way to stay out of the trace and its one-span traces crowded Jaeger. `.Tracing(false)` on a route,
  and the matching `<route tracing="false">` attribute in Route-XML, override the global option for that route only,
  the way `.MessageHistory()` already did. A route that says nothing inherits the global setting.

### Fixed — the Out → In merge no longer clears `ContentType` when the Out message has none

- `PipelineProcessor` copied `ContentType` from the Out message unconditionally, so a step that wrote an Out body
  without setting a content type wiped the In message's format to `null`. It is merged like a header now: the Out
  content type wins when it is set, and the In one stands when it is not. A step that does set it behaves as before.

## [4.2.0] — 2026-09-30

### Fixed — Controllers: a reply the action wrote itself reaches the caller

- An action may answer by itself: write `Exchange.Out` (body, `status.code`, `Content-Type`), stop the exchange and
  return `null`. The generic, SignalR and gRPC dispatchers then cleared that body (a regression of the "void action
  no longer echoes the request" fix above), and the SignalR and gRPC dispatchers also replaced its status with
  `204`: a `404` with an error document reached the caller as a `404` with an empty body. The HTTP dispatcher
  had cleared such a body since June and, reading only its own default, sent `redbHttp.ResponseCode: 204`
  next to the action's `status.code: 404`.
- The request body is now cleared only when the dispatcher made the reply itself (a clone of the request): an
  `Out` that already exists is the action's reply and keeps its body and status. The HTTP dispatcher mirrors
  the winning `status.code` into a missing `redbHttp.ResponseCode`. A void action with nothing written still
  answers `204` with no body.

### Breaking — a route with a bad static target no longer starts

- The endpoint of a static target — `To`, `WireTap`, `Enrich`, a load balancer's targets, a Scatter-Gather's fixed
  recipients — is created when its route starts, as Camel resolves a static `to(...)` at startup: an unknown scheme,
  an unknown URI parameter or a missing `{{key}}` stops the start (or skips the route with
  `ThrowOnCompilationError = false`). It used to surface on the first message sent there, hours after a deploy.
- A dynamic target (`ToD`, a recipient list, a routing slip) is still resolved per message, and producers are still
  made when a message first needs one — unlike Camel, which starts them with the route: starting a context opens no
  connection to a broker a route has not used yet. A static target matched by an intercept with
  `SkipSendToOriginalEndpoint()` is not created up front (a dry run whose real component is absent).


### Added — Route-XML `<aggregate forceCompletionOnStop>`; VS Code extension 0.2.16

- The attribute of Apache Camel `forceCompletionOnStop`: a group still open when the context stops
  completes with what it has and goes on down the route. It is parsed onto `.ForceCompletionOnStop(bool)` (an
  explicit `false` is kept, as the fluent call keeps it), printed by the C# generator, and known to the schema,
  the editor (`redb-route-1.0.catalog.xsd`, `redb-route-elements.json`) and `check`.
- The `eip.route.xml` example sets it on its aggregator; the generated `EipRoutes`, the golden tree and the
  hand-written C# equivalent prove the XML and the C# build the same definition.
- Extension version 0.2.16 for the schema change.

### Breaking — a typed read of a header, a property or a received body fails on a value it cannot parse

- `GetHeader<T>`, `GetProperty<T>` and the typed receive of `ConsumerTemplate` convert as Camel does: a missing value,
  or one with no conversion to the type at all, reads as default; a value the conversion exists for but cannot parse
  ("abc" as an int, an overflow) throws `FormatException` naming the key and the type, never the value. It used to
  read as default, so a typo in the data sent the route down its "no value" branch without a word.
- Numbers and dates are read in the invariant culture: `"1.5"` read as `decimal` was 0 under a culture with a decimal
  comma. A nullable target converts like its underlying type: `"5"` read as `int?` was null.

### Fixed — an exchange re-entering a route stays in flight until its last entry finishes

- A copy keeps the exchange id, so an exchange entering a route again (a route calling itself through `direct:`,
  parallel branches through one sub-route) registered the same id twice, and the first entry to finish took it off the
  inflight repository while the others still ran: `Browse()` and `CountByRoute` showed less than was in flight.
  `DefaultInflightRepository` now counts the entries.

### Changed — the endpoint byte count no longer serializes an object body

- `BytesIn` measures text, bytes and a seekable stream; any other object counts as 0, as Camel counts no bytes at all.
  It used to be serialized to JSON on every incoming exchange only to count the bytes of the result.

### Fixed — `bean:` awaits a method returning `ValueTask` or `ValueTask<T>`

- A bean method returning `ValueTask<T>` put the struct itself into the body instead of its result, and one returning
  `ValueTask` was not awaited: its side effect was lost and the body overwritten with the struct. Both are awaited now,
  as `Task` and `Task<T>`: the result of a `ValueTask<T>` becomes the body, a `ValueTask` leaves the body as it was.

### Breaking — an aggregation group holds exchanges of its own and completes in a DI scope of its own

- The aggregator kept the caller's exchange itself, and the caller disposes it as soon as the aggregator returns: a group
  completing later — on a timeout, on stop, or by a later arrival when the strategy keeps the first exchange — went on
  down the route with the bodies closed and the DI scope released. Now, as Camel copies an exchange into its
  aggregation repository, every arrival is taken over (its bodies included), the completed group runs on in a DI scope
  of its own, and everything the group held is released once, after it has gone on or been dropped.
- The steps after the aggregator run on the group's exchange, never on the caller's: what they write is not seen on the
  exchange that fed the group. A group completed by an arrival still fails that arrival when it fails.

### Added — `forceCompletionOnStop` on the aggregator; open groups are no longer dropped without a word

- `.Aggregate(...).ForceCompletionOnStop()` (Apache Camel `forceCompletionOnStop`): an aggregation group still open when
  the context stops completes with what it has and goes on down the route. It runs once the consumers have stopped —
  nothing more arrives — and before the producers stop, so what it sends still goes out.
- Without it an open group is dropped on stop, as Camel drops an in-memory repository, now with a warning naming how
  many and its exchanges released; the same when the context is disposed without a stop. It used to vanish silently.

### Fixed — route metrics and the route span count exchanges, not redelivery attempts

- The route span and `redb.route.exchanges.*` sat inside the `OnException` handlers declared on the route builder, so
  each redelivery attempt went through them again: one exchange redelivered twice was three spans and three failures.
  They now sit outside every error handler, like the endpoint statistics: one exchange, one span, one measurement with
  its final outcome. An exchange an error handler gave up on (the failure left on it, not thrown) now counts as
  failed, not processed.

### Fixed — a retry waits the delay its policy computes

- `RetryProcessor` added ±15% jitter of its own on top of `RetryPolicy.GetDelay`: jitter twice when the policy had a
  `CollisionAvoidanceFactor`, jitter nobody configured when it had none, and a wait past `MaxDelay`. It now waits the
  policy's delay as it is; jitter comes only from the policy (off by default, as Camel's collision avoidance).

### Breaking — `seda:` and `vm:` queues are bounded by default: 1000, as Camel

- `size` defaults to 1000 (was 0, unbounded). A full queue makes the sender wait up to `timeout` (30 s by default), as a
  bounded queue always did. A consumer slower than its senders no longer grows the queue until the process runs out of
  memory; a route that relied on it being unbounded writes `size=0`.

### Breaking — Controllers: a value that does not convert is a binding error, not the type's default

- A header, query, route, property or positional value that was present but did not convert to the parameter
  type bound silently as the type's default: `?page=abc` reached an `int page` as `0`. It is now an error that
  names the parameter and the value (`Parameter 'page': 'abc' is not a valid Int32.`), as Camel fails bean
  parameter binding with `ParameterBindingException`. A missing value still binds as the parameter's default.
- The HTTP, SignalR and gRPC dispatchers answer it with `400 BadRequest` and that message; SOAP with the Sender
  fault it already used for a header that does not bind; the direct-invoke DSL overloads fail the exchange.
- The generic dispatcher (`RedbController(registry)`, `RedbController<T>()`) now draws the same line as the
  transport dispatchers: a failure while binding is the caller's `400` (logged as a warning), the same exception
  inside the action stays a `500`. It answered `500` for both before.

### Fixed — Controllers: a void action no longer echoes the request as its reply

- The generic, SignalR and gRPC dispatchers build the reply as a clone of the request, and on a `void` (or
  `Task`) action they set `204` without clearing the body, so the caller got its own request back: the
  request payload on the generic dispatcher, the hub arguments on SignalR, the request bytes on gRPC. The
  body is now empty, as the README's response table says and as the HTTP dispatcher already did.

### Fixed — Controllers: header, query, route and property values bind the same on every server culture

- `[FromQuery]`, `[FromHeader]`, `[FromRoute]` and `[FromProperty]` converted text with the thread's culture:
  `amount=12.5` bound `125` for a `decimal` on a de-DE server and silently `0` on ru-RU. The value is wire text
  and is now parsed with the invariant culture.
- A `DateTimeOffset` parameter never bound from text: the type is not `IConvertible`, the conversion failed and
  the parameter silently became `default`. It is now parsed (invariant culture), and an unparsable value fails
  the binding like an unparsable `Guid` does (`400` on the HTTP, SignalR and gRPC dispatchers).

### Breaking — Cache: a hit never replays another caller's headers; `put` stores only the headers it names

- `CacheHeaders()` stored every header of the message, and a hit wrote them all over the current exchange.
  With `From("http:…")` the second caller's HTTP response carried the first caller's `Authorization` and
  `Cookie`, and a backend called after `EndCache()` received them as well (the consumer strips `Authorization`
  only when it checks it itself). The entry now keeps what the inner steps did to the headers, compared by
  instance with what the exchange came in with: the headers added or changed, and the names removed. A hit
  does the same to the current exchange and leaves its other headers alone. With the reply in `Out`, what the
  steps did to `In` comes back on a hit as well; it did not before.
- `Authorization`, `Proxy-Authorization`, `Cookie` and `Set-Cookie` are never stored, whichever form or step
  produced them.
- The `cache:` component: `put` stores only the headers named with `headers=X-Rate,Content-Language`.
  `cacheHeaders=true` is refused with the reason (a `put` cannot tell the headers of the value from the
  headers of the request), naming a credential is refused, and `headers=` on any other action is refused.

### Fixed — a copy of an exchange no longer disposes the body its origin goes on with

- A body that needs disposing (a stream, a `StreamCache`) is disposed by the exchange that owns it, once, when that
  exchange is done, as a Camel unit of work cleans up and a copy does not. A copy shares its origin's body by reference
  and used to dispose it: RecipientList, Splitter and Scatter-Gather with an aggregation, and a copying Loop, closed the
  body the route went on with (the aggregated or merged-back result included), and `.Threads()` lost its body to the
  caller disposing the original while the worker still read it. Now a copy disposes only a body no ancestor carries or
  lent it; the origin disposes its own and the ones it lent, so a body replaced by an aggregation is not leaked either.
  `.Threads()` hands the body over to the worker's copy. An exchange disposed twice, or with one body in `In` and
  `Out`, disposes it once; one body failing to dispose is logged and does not strand the others or the DI scopes.

### Fixed — Controllers: `async Task`, `ValueTask` and `ValueTask<T>` actions reply as documented

- The dispatchers and the direct-invoke DSL overloads took an action's result from the run-time type of the task
  it returned. An `async Task` method really returns a `Task<VoidTaskResult>`, so its runtime-internal result
  became the reply: `200` with `{}` on HTTP and gRPC, the struct itself on the generic and SignalR dispatchers,
  a Receiver fault on SOAP (the XML serializer refuses the internal type). Such an action now replies `204`
  with no body, as the response table in the README says, and an empty body on SOAP.
- A `ValueTask<T>` action replied with the `ValueTask<T>` struct instead of its result, and an `async ValueTask`
  action was not awaited, on every dispatcher but SOAP. Both are awaited now and reply like `Task<T>` and `Task`.
- `RedbController<T>(methodName)`, `RedbController(registry, controllerName, methodName)` and
  `RedbController(registry, controllerExpr, methodExpr)` let an action's synchronous exception reach the route
  wrapped in `TargetInvocationException`, so `OnException(typeof(...))` for the action's own exception type did
  not match. The action's exception now reaches the route as it was thrown, with its stack trace.
- All of them go through one invocation step that reads the shape from the method's declared return type, as
  ASP.NET Core's `ObjectMethodExecutor` does. An action declared to return `object` is not awaited even if the
  object it returns is a task.

### Added — Quartz consumer spans

- The `cron` and `qtimer` consumers open a root `Consumer` span per fire, `{scheme} receive`, with the route under it: a
  fire carries no trace context, so it is never a child of the activity the scheduler thread holds. A failed route
  marks it an error, our own stop does not; `EnableTelemetry = false` opens none. Was: no inbound span.

### Fixed — `ProducerTemplate` stops its producers: they flush and close

- `ProducerTemplate.Stop()` and `Dispose()`, and the new `DisposeAsync()`, stop the producers the template created, as a
  Camel template stops its producer cache; the context stopping stops them too, with the template left open. They were
  only dropped from the cache: a Kafka producer behind a template (the Llm tool bridge, TestKit sends) lost what it had
  not sent yet when the host stopped. A producer is stopped once, by whichever stops it first; a restarted template
  creates new ones. The context's list of producers to stop is now safe to add to from several threads.

### Fixed — SignalR: the hub works on .NET 9

- Since 4.0.0 the hub the consumer hosts closed every client connection with "Connection closed with an error"
  when the application ran on `net9.0`. The package referenced `Microsoft.AspNetCore.SignalR.Client` 9.0.3 and
  `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` 10.0.11 on every target framework, so a newer
  `Microsoft.AspNetCore.SignalR.Common` landed next to the older hub of the shared framework. Both packages now
  follow the target framework: 8.0.31 on `net8.0`, 9.0.20 on `net9.0`, 10.0.12 on `net10.0`, and the
  framework's own SignalR assemblies are used on each. The README no longer carries the .NET 9 warning.

### Fixed — Cache: `KeyFromBody()` hashes a stream, and names itself when a body cannot be hashed

- A `Stream` body went to `System.Text.Json` as an object and failed on the stream's own properties ("Timeouts
  are not supported on this stream"). It is now read to the end and hashed, and the message continues with its
  bytes, as it does when a miss is stored.
- An object body is hashed through the context's data format for the message's content type, the rule the
  distributed store serializes it by. It was the default `JsonSerializer`, whatever `ConfigureJsonCodec` said, so
  a key computed for an object body by an earlier version may differ, and that entry is computed once more.
  A body that cannot be serialized is an `InvalidOperationException` naming `KeyFromBody` and the type.
- Only the body is hashed: the content type and the headers are not part of the key.
- The distributed store resolves a body type by its name once per type, and closes `Serialize<T>` over a body
  type once, instead of walking the loaded assemblies and building the call on every read and write.

### Added — Firebase consumer spans; the producer spans honour `EnableTelemetry` and mark failures

- The Firestore consumer, realtime and polling, opens a root `Consumer` span per routed change,
  `firestore {collection} receive`; the Storage consumer one per routed object, `gcs {bucket} receive`. A change or an
  object carries no trace context, so it is never a child of the activity the consumer inherited; nothing arriving
  opens none. A failed route marks it an error, our own stop does not. Was: no inbound span.
- The FCM, Firestore and Storage producer spans are errors when the call fails, unless our own token cancelled it, and
  `EnableTelemetry = false` opens them no more. Was: always opened, never marked.

### Fixed — Cache: one computation per key across the cache nodes of a context

- The single flight on a miss belonged to the node: two scopes on the same region and key shared the store and
  still computed the entry once each. The flights now belong to the context and the provider, as the store
  does, so every node on one store waits for the first one's result. Other processes, and other contexts,
  compute on their own; a failed computation is not cached, so the waiters retry one at a time.
- `CacheScopeProcessor` no longer keeps a region it never read: the region is part of the store key.

### Fixed — Cache: `cache:…?action=get` hands a cached reply back as a reply

- An entry the caching scope stored out of `exchange.Out` (a request-reply producer replies there) keeps that
  fact, and the scope restores it to `Out` on a hit. The component ignored it and wrote the entry into `In`, so
  a route reading the same entry with `action=get` answered a request-reply consumer with the request body.
  `get` now restores the entry where the miss left it. `put` stores the message as it reaches the step: the
  pipeline has folded the previous step's reply into `In` by then.

### Breaking — Cache: a value under our key that is not a redb.Route cache entry is an error, not a hit

- The JSON envelope of the distributed store carries a format marker and a version now. A value under a cache
  key that is not JSON, has no marker, or has a version this build does not read is an `InvalidOperationException`
  naming the key: another writer shares the key space (give this cache its own `InstanceName`, or another
  region), or the entry was written by `redb.Route.Cache` before this version (clear the region, or let the
  entries expire). Was: foreign JSON deserialized into an empty envelope and counted as a hit with a `null`
  body, `cache.hit = true`, and the inner steps skipped; non-JSON bytes threw a `JsonException` without the key.
- Entries written by earlier versions have no marker and are refused the same way, so a distributed cache
  shared across an upgrade needs its regions cleared once (or a new `InstanceName`).

### Added — MCP `tools/call` span honours `EnableTelemetry` and marks failures

- `mcp.tools/call {server}/{tool}` carries `redb.route.endpoint`, is not opened with `EnableTelemetry = false`, and is
  an error when the call fails (an unknown or dead server, a tool error, the call timeout), unless our own token
  cancelled it. Was: always opened, never marked.

### Breaking — Telegram tracing on the core's transport contract

- **The receive spans are roots.** They were children of whatever activity the polling loop held. They now carry
  `redb.route.endpoint`, honour `EnableTelemetry`, and are errors when the route fails, the failure left on the exchange
  included; our own stop is not. Was: marked only when an exception escaped the route.
- The send span carries `redb.route.endpoint` and honours `EnableTelemetry`; a failed send, the per-request timeout
  included, marks it an error unless our own token cancelled it.

### Breaking — Llm tracing: a root span per scheduled tick; the model span honours `EnableTelemetry` and marks failures

- The scheduled consumer opens a root `Consumer` span per tick, `llm {factory} receive`, with the model call and the
  route under it; a failed tick (the model call or the route) marks it an error, our own stop does not. Was: an
  `llm.consumer.error` tag on whatever activity happened to be current, and the tick's spans under that activity.
- `llm {provider}:{model}` and `llm {provider}:{model} stream` carry `redb.route.endpoint`, are not opened with
  `EnableTelemetry = false`, and are an error when the run fails, unless our own token cancelled it. A `stream=body` run
  cancelled by another token (an HttpClient timeout) now counts as an endpoint error. Was: always opened, never marked.

### Fixed — Cache: `clear` on a distributed cache removes a sliding key that reads kept alive

- `DistributedCacheStore` keeps the keys this process wrote, each with the moment it stops mattering, and sweeps
  the expired ones every 256 writes (`IDistributedCache` cannot enumerate, so region-wide `clear` works from that
  list). A sliding entry lives on with every read, but the list remembered the write plus the window only, so a
  key read all the time was swept and `clear` left it in place. A hit now renews the key's lease, capped by the
  absolute TTL when there is one.

### Fixed — Cache: an out-of-range duration is a `FormatException` like any other typo

- `CacheDuration.Parse("9999999999d")` threw `OverflowException` out of `TimeSpan`, and `NaNs` an
  `ArgumentException`, while every other unusable duration is a `FormatException` naming the text. Now all of
  them are.

### Fixed — Cache: `clear` on the in-process cache removes an overwritten key

- `MemoryCacheStore` keeps the keys it wrote in a list for the region-wide `clear` (`IMemoryCache` cannot
  enumerate) and took a key off it from the entry's eviction callback. `MemoryCache` fires that callback for the
  entry a second `put` of the same key replaces as well, on the thread pool, after the new entry is in, so a key
  written twice vanished from the list while its entry lived on, and `clear` left it in place. The callback now
  ignores a replacement and takes a key off only while the list still holds that entry's own write.

### Breaking — Mail tracing on the core's transport contract

- **The IMAP and POP3 receive spans are roots.** They were children of whatever activity the poll loop had inherited
  from the code that started the routes. They now carry `redb.route.endpoint`, honour `EnableTelemetry`, and are errors
  when the route fails, the failure left on the exchange included; our own stop is not. Was: marked only when an
  exception escaped, a stop included.
- The SMTP send span carries `redb.route.endpoint`, honours `EnableTelemetry` and is an error when the send fails,
  unless our own token cancelled it. Was: never marked.

### Added — MQTT trace context in v5 user properties; consumer spans; the producer span honours `EnableTelemetry` and marks failures

- On a v5 connection the producer writes the W3C context to user properties, replacing a `traceparent` copied from an
  earlier hop into `redbMqtt.userProperties`; the consumer reads it and parents its span on it. Under 3.1.1 nothing is
  written (MQTTnet throws `NotSupportedException` for user properties there) and the receive span is a root. Was: no
  context written.
- The consumer opens a `Consumer` span, `{topic} receive`, per routed message, serial and concurrent dispatch alike;
  without a context it is a root, never a child of the client's receive loop. A failed route marks it an error, our own
  stop does not. A cancellation that is not our own is now logged as a failure instead of being dropped. Was: no inbound
  span.
- `{topic} publish` is an error when the publish fails, unless our own token cancelled it, and `EnableTelemetry = false`
  opens it no more. Was: always opened, never marked.

### Breaking — Exec tracing: the client span named after the resolved executable; a span per scheduled tick

- The producer's client span is named `exec {executable}` and carries `process.executable.name` = the file name of the
  command actually resolved (URI option, header or JSON body), with `process.exit.code`. Was: `exec {command option}`
  (`exec <dynamic>` for a command from a header or body) and `process.executable.name = "exec"`. The arguments never
  reach the span. A refused command, a failed start or a cancellation that is not our own marks it an error; it honours
  `EnableTelemetry`.
- The scheduled consumer opens a root `Consumer` span per tick, `exec {executable} receive`, with the run and the route
  under it; a failed tick marks it an error, our own stop does not. Was: an `exec.consumer.error` tag on whatever
  activity happened to be current, lost when there was none.

### Added — WebSocket consumer spans linked to the handshake; the producer span honours `EnableTelemetry` and marks failures

- The consumer opens a root `Consumer` span, `ws {path} receive`, per routed message, the InOut reply included. A frame
  carries no headers: when the client sent `traceparent` with its upgrade request, every span of the connection links
  that context, and the connection's own activity is never the parent. A failed route marks the span an error; our own
  stop does not. Was: no inbound span.
- `ws send {mode}` is an error when the send fails, unless our own token cancelled it, and `EnableTelemetry = false`
  opens it no more. Was: always opened, never marked.

### Added — SignalR consumer spans linked to the handshake; the producer span honours `EnableTelemetry` and marks failures

- The consumer opens a root `Consumer` span, `signalr {hubPath} receive`, per exchange that reaches the route (each
  invocation and the `Connected` / `Disconnected` events). A hub message carries no headers: when the client sent
  `traceparent` with its connection request, every span of the connection links that context, and the connection's own
  activity is never the parent. A failed route marks the span an error; the connection's own abort does not. Was: no
  inbound span.
- `signalr {mode} {method}` is an error when the send fails, unless our own token cancelled it, and
  `EnableTelemetry = false` opens it no more. Was: always opened, never marked.

### Added — LDAP consumer spans; the producer span is a `Client` span that honours `EnableTelemetry` and marks failures

- The WATCH consumer opens a root `Consumer` span, `ldap {baseDn} receive`, per routed entry: an entry carries no trace
  context, so it is never a child of the activity the poll loop inherited; a poll that finds nothing opens none. A
  failed route marks it an error, our own stop does not. Was: no inbound span.
- `ldap.{operation}` is a `Client` span with `redb.route.endpoint`, an error when the operation fails unless our own
  token cancelled it, and `EnableTelemetry = false` opens it no more. Was: an `Internal` span without the endpoint tag,
  always opened, never marked. A cancellation that is not our own now also counts as an endpoint error.

### Fixed — Elasticsearch consumer: a document whose route failed is no longer deleted with `deleteAfterRead`

- A failure that escaped the route's pipeline — a cancellation inside the route (an HttpClient timeout is a
  `TaskCanceledException`), which the error handlers pass on — was logged and dropped without reaching the exchange, so
  `deleteAfterRead=true` took the route for a success and deleted the document it never processed. The failure now goes
  on the exchange, and the document stays, as for any other failed route.

### Added — TCP consumer spans; the producer span honours `EnableTelemetry` and marks failures

- The consumer opens a root `Consumer` span, `tcp {host}:{port} receive`, per routed message: a framed message carries
  no trace context, so it is never a child of the activity the listener inherited. A failed route marks it an error,
  our own stop does not. Was: no inbound span.
- `tcp {host}:{port}` is an error when the send fails, unless our own token cancelled it, and `EnableTelemetry = false`
  opens it no more. Was: always opened, never marked.

### Added — Elasticsearch consumer spans; the producer span honours `EnableTelemetry` and marks failures

- The consumer opens a root `Consumer` span, `es {index} receive`, per routed hit: a document carries no trace context,
  so it is never a child of the activity the poll loop inherited; an empty poll opens none. A failed route marks it an
  error, our own stop does not. Was: no inbound span.
- `es {operation}` is an error when the call fails, unless our own token cancelled it, and `EnableTelemetry = false`
  opens it no more. Was: always opened, never marked.

### Added — Redis consumer spans; the producer span honours `EnableTelemetry` and marks failures

- The consumer opens a root `Consumer` span, `redis {operation} receive`, per routed pub/sub message, stream entry or
  list item: a Redis value carries no trace context, so it is never a child of the activity the consumer loop
  inherited. A failed route marks it an error, our own stop does not. Was: no inbound span. It is a messaging span:
  `messaging.system = redis` with the channel, stream or list as `messaging.destination.name` (the producer's command
  span keeps `db.system = redis`).
- `redis {operation}` is an error when the command fails, unless our own token cancelled it, and
  `EnableTelemetry = false` opens it no more. Was: always opened, never marked.

### Fixed — file consumers: a cancellation out of the route fails the file, not the poll

- `file`, `ftp`, `sftp`: a cancellation coming out of the route that is not the consumer's own stop — an HttpClient
  timeout is a `TaskCanceledException`, and an error handler passes a cancellation on — escaped the per-file handling
  and failed the whole poll. The failure path of that file did not run (`moveFailed` left it in place), the files after
  it in the listing waited for the next poll, and the poll counted as an error for the backoff. It is now a failure of
  the file like any other exception; only the consumer's own stop still ends the poll.

### Added — File, FTP and SFTP consumer spans; the producer span honours `EnableTelemetry` and marks failures

- The consumers built on `redb.Route.GenericFile` (`file`, `ftp`, `sftp`) open a root `Consumer` span,
  `file read {scheme}`, per routed file. A file carries no trace context, so it is never a child of the activity the
  poll loop inherited; an empty poll opens none. A failed route marks it an error, our own stop does not. Was: no
  inbound span.
- `file write {scheme}` is an error when the write fails, unless our own token cancelled it, and
  `EnableTelemetry = false` opens it no more. Was: always opened, never marked.

### Added — S3 consumer spans; the producer span honours `EnableTelemetry` and marks failures

- The consumer opens a root `Consumer` span, `{bucket} receive`, per routed object. An object carries no trace context,
  so it is never a child of the activity the poll loop inherited; an empty poll opens none. A failed route marks it an
  error, our own stop does not. Was: no inbound span.
- `s3 {operation}` is an error when the call fails, unless our own token cancelled it, and `EnableTelemetry = false`
  opens it no more. Was: always opened, never marked.

### Added — SQL consumer spans; producer spans honour `EnableTelemetry` and mark failures

- The consumer opens a root `Consumer` span, `sql.receive`, per routed exchange: a row, or the list of a batch. A row
  carries no trace context, so it is never a child of the activity the poll runs under. A poll that routes nothing
  opens none; one that routes an empty exchange (`routeEmptyResultSet`, `sendEmptyMessageWhenIdle`) opens one. A
  failed route marks it an error, our own stop does not. Was: no inbound span.
- `sql.execute` and `sql.procedure` are errors when the statement fails, unless our own token cancelled it, and
  `EnableTelemetry = false` opens neither. Was: always opened, never marked.

### Breaking — SQS and SNS tracing on the core's transport contract

- **A message without context opens a root receive span.** It was a child of whatever activity the receive loop had
  inherited from the code that started the routes, so unrelated messages hung under it. With a `traceparent` the span
  is its child, as before; the sender's baggage is now back on it.
- The receive span carries `redb.route.endpoint` and is an error when the route fails, a timeout inside it included;
  the consumer's own stop is not. Was: never marked. The send and publish spans are errors when the call fails, unless
  our own token cancelled it.
- `EnableTelemetry = false` opens none of these spans; the ambient context still goes out in the message attributes.
  Was: the send and publish spans opened anyway.
- **More than 10 message attributes fail the send** with a message naming how many came from the exchange headers and
  how many from the trace context; nothing is dropped. Was: sent as is, for the service to refuse with a bare count
  (LocalStack accepts it).

### Breaking — IBM MQ tracing on the core's transport contract

- **A message without context opens a root receive span**, on the poll and the listener path alike. It was a child of
  whatever activity the receive loop had inherited from the code that started the routes, so unrelated messages hung
  under it. With a `traceparent` the span is its child, as before; the sender's baggage is now back on it.
- The receive span carries `redb.route.endpoint` and is an error when the route or the settlement fails; the send span
  carries `redb.route.endpoint` and is an error when the send fails.
- `EnableTelemetry = false` opens neither span; the ambient context still goes out in the message properties. Was:
  both spans opened, and nothing was written without a send span.
- A missing trace property is told apart by its reason code (MQRC 2471); any other failure to read one is no longer
  swallowed.
- A cancellation is an error on either span unless it is our own: the consumer's stop (a message interrupted by it
  stays unmarked, on both receive paths) or the caller's token cancelling the send. Any other cancellation, a timeout
  inside the route for one, is a failure.

### Breaking — AMQP tracing on the core's transport contract

- **A message without context opens a root receive span.** It was a child of whatever activity the receive loop had
  inherited from the code that started the routes (a host startup span, a request), so unrelated messages hung under
  it. With a `traceparent` the span is its child, as before; the sender's baggage is now back on it.
- The receive span carries `redb.route.endpoint` and is an error when the route or the settlement fails; the send span
  carries `redb.route.endpoint` and is an error when the send fails.
- `EnableTelemetry = false` opens neither span; the ambient context still goes out in the application properties. Was:
  both spans opened, and nothing was written without a send span.
- A cancellation is an error on either span unless it is our own: the consumer's stop (a message interrupted by it
  stays unmarked) or the caller's token cancelling the send. Any other cancellation, a timeout inside the route for
  one, is a failure.

### Breaking — SOAP tracing on the core's transport contract

- **The consumer's server span continues the caller's trace.** `soap receive` takes the host's ASP.NET Core span of the
  request when the application instruments ASP.NET Core, else the caller's `traceparent` header, else it is a root; the
  caller's baggage is back on it. Was: a root unless the receiving thread happened to hold an activity, so the caller's
  trace ended at redb. It honours `EnableTelemetry` and is an error when the route fails.
- **The server span covers the whole request**, the ones refused before the envelope is read (malformed MTOM)
  included. As a 5xx against a 4xx: a Receiver/Server fault, thrown or declared on the reply, and any other failure of
  the route are errors on it, a cancellation included unless the caller went away (a timeout inside the route got a
  bare 500 and an unmarked span; it now gets a fault with the exchange reference); a malformed request or a
  Sender/Client fault is an answer to the caller and is not.
- **The client span is an RPC span:** `rpc.method` = the action; `messaging.operation` (the action) and
  `messaging.destination.name` (the URL) are gone. The request carries the span's context written by the producer, not
  left to HttpClient instrumentation, and the span is an error for a failed call, an HttpClient timeout included (only
  our own token cancelling it is not), or a `soap:Fault` reply.

### Breaking — gRPC tracing on the core's transport contract

- **The consumer's server span continues the caller's trace.** `grpc receive` takes the host's ASP.NET Core span of the
  request when the application instruments ASP.NET Core, else the caller's `traceparent` metadata, else it is a root;
  the caller's baggage is back on it. Was: a root unless the receiving thread happened to hold an activity, so the
  caller's trace ended at redb. It honours `EnableTelemetry` and is an error when the call fails; the caller cancelling
  the call is not a failure, any other cancellation (a timeout inside the route) is.
- **The producer sends the context of its own span.** A proxying route sent the caller's `traceparent` the header
  bridge copied into the metadata, and the HTTP handler under Grpc.Net.Client does not overwrite a trace header already
  on the request, so the next hop hung under the caller instead of under the call. The client span is an error for a
  failed call.

### Added — Azure Service Bus tracing on the core's transport contract

- The consumer, and the session consumer, open a `Consumer` span per message, `{entity} receive`, over the whole unit of
  work, settlement included: a child of the sender's context, or a root when the message carries none — never a child
  of the processor thread's activity. The sender's baggage is back on it, the route's spans are its children, it
  carries `redb.route.endpoint` and `messaging.message.id`, and a failed route marks it an error. Was: no inbound span.
- The context travels in the application properties: the `traceparent` value under Azure's own name, `Diagnostic-Id`,
  `tracestate` and `baggage` under their own names. The producer writes the context of its send span to every message,
  each message of a batch included, replacing a `Diagnostic-Id` the header bridge copied from a received message. Was:
  the copied value went out as is, so the next hop hung under the previous one.
- The send span honours `EnableTelemetry` and is an error when the send fails. With tracing off no span opens, and the
  ambient context still goes out.
- A cancellation is an error on either span unless it is our own: the processor stopping (the receive span of a message
  interrupted by the stop stays unmarked) or the caller's token cancelling the send. Any other cancellation, a timeout
  inside the route for one, is a failure.

### Breaking — Kafka tracing on the core's transport contract

- **A batch is a root linked to every record.** With `maxPollRecords`, the receive span took the first record's context
  as its parent and ignored the others' traces. It is now a root of its own with an `ActivityLink` to the context of
  every record that carries one; `messaging.batch.message_count` stays, and the per-record tags (partition, offset,
  key) are left to the single-record span.
- The sender's baggage is back on the receive span of a record, so the route's own spans and sends carry it on; it was
  dropped. A record without `traceparent` starts a root span, as before.
- Both spans carry `redb.route.endpoint` (sanitized), `EnableTelemetry=false` opens neither, and a failed send (an
  immediate one, or a Kafka transaction of its own) marks its span an error. The send span's context replaces a
  `traceparent` copied from a consumed record's headers, in whatever case it was copied: a route `http` to `kafka`
  bridged the caller's `Traceparent`, which the consumer, reading the name without case, took ahead of the send's.
- A route failing on a record or a batch marks the receive span an error. Only the consumer's own stop does not: any
  other cancellation is a failure, an HttpClient timeout inside the route (a `TaskCanceledException`) for one. The same
  rule holds for the send span: only the caller's own token cancelling it is not a failure.

### Breaking — AS2 tracing on the core's transport contract

- **Span names carry the destination.** The receive span of a message receiver and of an MDN receiver is
  `"{path} receive"` (it was `AS2 receive` / `AS2 receive MDN`), the send span `"{partner AS2 id} send"` (it was
  `AS2 POST`). A dashboard or alert that selects spans by the old names needs the new ones.
- The receive span takes its parent from the host's request span when the application traces ASP.NET Core, else from
  the request's `traceparent`, else it is a root; the sender's baggage is put back on it. It carries
  `redb.route.endpoint`. Every refusal marks it an error: on the message receiver a refused message, a body over
  `maxRequestBodySize` and a failed route; on the MDN receiver an unreadable MDN and a receipt the signature policy
  refuses (both answered 400), a body over `maxRequestBodySize`, and a failed route (answered 200: the partner did
  deliver its receipt).
- The send span writes its own context to the request, replacing a `traceparent` bridged from the exchange: HttpClient
  writes none when the header is already there, so a `From(http) -> To(as2)` route passed the caller's context on
  instead of the send's. A failed send (unreachable partner, no answer within `timeout`, HTTP error, an MDN that does not
  confirm the transfer with `RequireValidMdn`) marks the span an error; a send the caller cancels does not.
- `EnableTelemetry=false` opens neither span; a context the route received still reaches the partner.

### Breaking — AS4 tracing on the core's transport contract

- **Span names carry the destination.** The receive span is `"{path} receive"` (it was `AS4 receive`), the send span
  `"{partner} send"` (it was `AS4 send`). A dashboard or alert that selects spans by the old names needs the new ones.
- The receive span takes its parent from the host's request span when the application traces ASP.NET Core, else from
  the request's `traceparent`, else it is a root; the sender's baggage is put back on it. A refused message (every
  ebMS error and SOAP fault answered) or a failed route marks it an error.
- The send span writes its own context to the request, replacing a `traceparent` already there. An unreachable
  partner, a response without a receipt, an ebMS error or a receipt that does not verify marks it an error.
- `EnableTelemetry=false` opens neither span; the caller's context still reaches the partner.

### Breaking — RabbitMQ tracing on the core's transport contract

- **The receive span takes its parent from the message only.** A delivery without `traceparent` starts a root span;
  it was a child of whatever activity the client's dispatch thread held, usually the one around the consumer's
  `Start`, so unrelated messages landed in one trace. With `traceparent` nothing changes.
- The sender's baggage is back on the receive span, so the route's own spans and sends carry it on; it was dropped.
- Both spans carry `redb.route.endpoint` (sanitized), a failed pipeline, publish, broker confirm, returned message or
  request-reply call marks its span an error, and `EnableTelemetry=false` opens neither span. The send span's context
  replaces a `traceparent` copied from the incoming message.
- The receive span is an error also when the ack or the RPC reply fails after a successful route; a stop that cancels the route is not an error.
- Only a cancellation through the connector's own token (a stop) leaves either span clean; any other, such as an HTTP call inside the route timing out, marks it an error.

### Breaking — tracing: one transport contract in the core; HTTP requests traced; `EnableTelemetry=false` covers transport spans

- `RouteEngineOptions.EnableTelemetry = false` now opens no transport span either, for connectors that open theirs
  through the context (`StartTransportSpan(context, ...)`, `StartConsumerSpan`), so a trace never holds a transport
  span without the route it belongs to. A context that came in is still passed on to the next hop. Was: only the route
  spans went away.
- The HTTP consumer opens a `Server` span per request, `{method} {path}`, over the whole request: a child of the host's
  ASP.NET Core span when the application instruments ASP.NET Core, else of the caller's `traceparent`, else a root. The
  caller's baggage is back on it, the route spans are its children, and it carries `redb.route.endpoint` and
  `http.response.status_code`; a 5xx marks it an error. Was: no inbound span, and the caller's trace ended at redb
  unless the host instrumented ASP.NET Core.
- The HTTP producer sends the context of its own span. A proxying route (`http` in, `http` out) sent the caller's
  `traceparent` copied by the header bridge, and `HttpClient` does not overwrite a trace header already on a request,
  so the next hop hung under the caller instead of under the send. The client span now carries
  `http.response.status_code` and is an error for a 4xx, a 5xx or a failed call.
- A cancellation marks an HTTP span an error unless the caller asked for it: the consumer's request aborted by the
  client, or the producer's own token. An HttpClient timeout (a `TaskCanceledException`) failed the call and left the
  send span unmarked; a route that threw such a cancellation got Kestrel's bare 500 and an unmarked server span, and now
  gets the consumer's 500 with the exchange reference.

### Added — `RouteTelemetryExtensions`: the tracing contract of a transport

- `StartConsumerSpan(context, ..., carrier, readHeader, parent)` opens an inbound span: the parent from the message's
  W3C fields, or a root when there are none — never the receiving thread's ambient activity, which
  `StartActivity(parentContext: default)` otherwise adopts — with the sender's baggage put back; `InboundParent.HostRequest`
  takes the host's span of the same request first. Disposing it gives the thread its ambient activity back.
- `InjectTraceContext(activity, carrier, writeHeader)` writes `traceparent`, `tracestate` and baggage to an outgoing
  message, from the send span or else the ambient activity, replacing a copied value.
- `RecordFailure(activity, exception)` marks a span failed: error status and an OpenTelemetry `exception` event, the
  shape the route spans use. `ExtractTraceContext(...)` reads one message's context, for batch links.
- `IsTracingEnabled(context)` and `StartTransportSpan(context, ...)` honour `EnableTelemetry`.
- `DrainableConsumer.ProcessWithTracking(exchange, span, ct)` runs the route inside a receive span the consumer opened
  and marks it by the same rule: a failed route, whether its failure stayed on the exchange or escaped the pipeline, and
  any cancellation but the consumer's own stop. A polling consumer takes it instead of a copy of its own. Both
  overloads now put a failure that escaped the pipeline (a cancellation an error handler passed on) on the exchange:
  it was logged and dropped, so a consumer deciding on the outcome afterwards saw a clean route.
- A connector supplies the carrier and its names; the W3C and baggage handling is written once. Brokers that carry
  their own copy move onto it with their owners.

### Added — `[ConnectionParameter]`: a connection factory is the whole connection, for every connector

- `[ConnectionParameter]` on an options property says a named connection factory sets it (host, credentials, TLS,
  timeouts); `[ConnectionFactoryReference]` marks the option that names the factory, so the core does not guess it.
  Written beside the factory reference in a URI, a connection parameter is refused by the core when the options are
  bound, for every connector that declares them — as in Camel, where "all connection options set on URI are not
  used", except that here it is an error instead of a silent loss. The message names the factory and the parameters,
  never their values. `EndpointOptions.WrittenBesideConnectionFactory(...)` answers the same question for tooling.
  An options type that declares connection parameters without a factory reference, or with two, is a declaration
  error.
- RabbitMQ declares its 24 connection parameters with it; its own list and check are gone, the behaviour is the same.
  Other connectors with `connectionFactory` are not marked yet: marking one changes what a URI beside a factory does.

### Changed — TLS options carry the names most connectors use

- **Breaking.** The client-certificate password is `sslCertPassword` everywhere: RabbitMQ's `sslCertPassphrase`
  (option, factory property and `SslCertPassphrase`) is renamed. Accepting any server certificate is
  `trustAllCertificates` everywhere: AMQP's `SkipServerCertValidation` (factory) and LDAP's and Mail's
  `skipCertificateValidation` (option, factory, builder method `.SkipCertificateValidation()` →
  `.TrustAllCertificates()`) are renamed. The former names in a URI are refused with the new one named, not bound
  silently.
- `CONCURRENCY.md` says what an asynchronous step (InOnly `.Threads()`, `seda:`) does to a broker consumer's
  `ackMode=manual`: the route returns before the work is done, so the message is settled ahead of it.

### Changed — VS Code extension 0.2.15: `rabbitmq:` options, enum values in any case, flags lists

- The editor catalog gets the new `rabbitmq:` options (`publisherConnection`, `sslCaCertPath`,
  `sslProtocols`, `revocationMode`, `revocationSoftFail`, `authMechanism`), taken from the generator run on the
  connector's build. Its `redb-route-catalog.json` record also had `AutoAck` and no `AckMode` (the ackMode change
  was written into the schema by hand only); it is regenerated.
- Enum options in the catalog schema read the way the engine reads them. The option converter parses member
  names in any case, and the engine's own messages write them lowercase (`ackMode=manual`), but the schema took
  the exact C# names only, so `ackMode="manual"` was an error in the editor. An enum option is now a union of
  the lowercase list (offered as completion) and a case-insensitive pattern.
- A `[Flags]` enum option (`sslProtocols`) takes several members joined by commas (`Tls12,Tls13`), as the
  engine does. The catalog labels it `flags`, the schema accepts the list, and the graph's property panel shows
  a text field instead of a pick-one list.
- The renamed TLS options are in the catalog: `sslCertPassword` on `rabbitmq:` (was
  `sslCertPassphrase`), `trustAllCertificates` on `amqp:`, `ldap:` and the mail schemes.
- `imap:`, `pop3:` and `smtp:` had no options in the catalog at all. The generator looked for the options class by
  the component's name or as its assembly's only one, and the three mail components share `MailEndpointOptions`.
  It now reads it where the code declares it first: the component's endpoint derives from
  `EndpointBase<TOptions>` (`ImapEndpoint : EndpointBase<MailEndpointOptions>`).
- `check --bin` refuses a connection parameter written beside the option that names a connection factory
  (`[ConnectionParameter]`, `[ConnectionFactoryReference]`), URI form and structured form alike, with its
  position and the engine's wording. The catalog carries both declarations (`connectionParameter`,
  `connectionFactoryReference` in `redb-route-catalog.json`); `rabbitmq:` is the connector that declares them so
  far. The schema cannot express it: XSD 1.0 has no rule of one attribute depending on another.
- The other connectors' schema elements get the enum and flags forms with the next regeneration from release
  bins (RELEASE_ARTIFACTS §5a).

### Fixed — Kafka: exactly-once by cluster id; a commit of unknown outcome is not aborted (review 2026-09-28, §6.1, §6.4, §6.5)

- **One cluster however it is listed.** A consumer and a transactional producer were matched by their bootstrap lists
  as written: one cluster reached through other brokers on the two ends counted as two, and the consumed offset stayed
  out of the transaction (at-least-once). They are matched by the cluster id the brokers report now; the producer reads
  it after `InitTransactions`, the consumer on its first offer of offsets.
- **Commit and abort without a timeout of ours**, as librdkafka strongly recommends (its API timeouts do not match the
  protocol requests); they block up to `transaction.timeout.ms`. A commit that keeps timing out has an unknown outcome:
  the producer no longer calls an abort that cannot undo it, it is rebuilt before the next transaction (whose
  initialization settles the previous one) and logs that a redelivery may write the records again.
- A consumer losing its partition mid-transaction (rebalance, `maxPollIntervalMs` exceeded) is now tested on the live
  cluster: the broker refuses its old generation, its output is aborted, and one record comes out.

### Changed — VS Code extension 0.2.14: the editor knows `as4:` and the current `as2:` options

- The editor catalog (`redb-route-1.0.catalog.xsd`, `redb-route-catalog.json`) had no `as4` element, and `as2`
  still offered `certPassword` (now refused) without the six options added since: `asyncMdnAllowedHosts`,
  `allowLegacyAlgorithms`, `maxRequestBodySize`, `maxResponseBodySize`, `idempotentRepository`, `streamBody`.
  Both are taken from the generator run on the connectors' builds at HEAD: `as4` with the sides of its options
  (receive-only `port`, `idempotentRepository`, …; send-only `partner`, `action`, …), `as2` as it is now.
- `check --bin`: the message about an option on the wrong side named the endpoint element in the structured form
  (`<as4> creates the producer`); it names the step now (`<to> creates the producer`).
- Extension version 0.2.14: the schema and the catalog changed since 0.2.13 (bean lists and `ref=`, `whereRedb`,
  `outputType`, strict connectors, option sides, `http`/`log`, `<rest>`, `<constructorArg type>`, `as4`).

### Added — AS4 connector (`redb.Route.As4`): eDelivery AS4 1.16 over ebMS 3.0

- Schemes `as4` and `as4s`. **Send** (`As4.Send(...)`): gzip, encrypt and sign a payload, POST it to the partner's
  access point, verify the receipt it answers with (signature and non-repudiation digests). **Receive**
  (`As4.Receive(...)`): decrypt, verify, decompress, match the message to an agreement, deliver it to the route, and
  answer with a signed receipt or an ebMS error after the route's unit of work.
- One-Way/Push and Two-Way/Push-and-Push (the reply leg with `eb:RefToMessageId`), both required by the profile.
- The profile's cryptography only: RSA-SHA256, SHA-256, AES-128-GCM, RSA-OAEP with MGF1-SHA256, Exclusive C14N; the
  three key references (`BinarySecurityToken`, `IssuerSerial`, `KeyIdentifier`). Anything wider is refused at start,
  or answered with its ebMS code (`EBMS:0101`, `0102`, `0103`, `0010`, ...).
- `As4ConnectionFactory` (our node) and `As4Partner` (one agreement per partner, pinned certificates, P-Mode-style
  matching on agreement, sender, service and action), validated when an endpoint starts.
- Duplicate detection on receive (`idempotentRepository`, required) and redelivery with the same message id and the
  same bytes on send, built from `IIdempotentRepository` and `OnException`.
- Hardening: `wsu:Timestamp` checked by the WSS4J rules, duplicate `wsu:Id` refused, required signature coverage,
  attachment MIME canonicalization (SwA 1.1), certificate validity with optional revocation, no exception text to the
  partner, `maxRequestBodySize` / `maxResponseBodySize`, TLS 1.2/1.3 and mutual TLS with allow-listed thumbprints.
- The request and payloads are spooled through the core stream cache; `streamBody` hands a payload to the route as a
  `Stream`. Shared Kestrel host, admission limits, stop drain, statistics, tracing, `.Transacted()`, Route-XML.
- Interop tested live in both directions against Holodeck B2B 8.1.1 and Domibus 5.1 (Harmony AP 2.6.2). Pull and
  asynchronous receipts are out of scope. See `src/redb.Route.As4/README.md`.

### Fixed — Kafka: `sslEndpointIdentificationAlgorithm=none` turns hostname verification off; TLS proven on a live cluster

- **`none` meant `https`.** Only the empty string turned hostname verification off; any other value, `none`
  included, turned it on, so a route asking to skip the check failed its handshake. The option now takes `https` or
  `none` (empty still means `none`) and refuses anything else by name, on the endpoint and on the connection factory.
  DSL: `.SslVerifyHostname(bool)`.
- **Password in clear.** `saslMechanism=Plain` over `securityProtocol=SaslPlaintext` logs a warning that the password
  goes unencrypted.
- DSL `.SslCert(string, string?, string?)` next to the expression overload.
- TLS, SASL_SSL with SCRAM-SHA-512, mutual TLS and a named factory carrying them are now tested against the 3-node
  cluster, with the refusals: another CA, a wrong password, a hostname the certificate lacks, no client certificate.
  README: a section on encryption and authentication.

### Fixed — RabbitMQ: `ssl=true` is honoured on every connection path (security)

- **A named connection factory never used TLS.** `RabbitMQConnectionFactory.Build()` set the TLS options and then
  assigned `ConnectionFactory.Endpoint`, whose setter replaces them with the endpoint's (TLS off): with
  `connectionFactory=<name>` and `Ssl = true` the client spoke plain AMQP, credentials included. A host list
  (`host=a,b,c` on the URI, or `Host` on a factory) dropped TLS the same way: a connection over an endpoint list takes
  TLS from each endpoint, and the endpoints were built without it. Every endpoint now carries the TLS settings, and
  every connection is opened over the endpoint list.
- **The server name defaults to the host.** Without `sslServerName` the broker certificate was checked against an
  empty name and no SNI was sent, so the handshake could not pass. Each host is now checked against its own name;
  `sslServerName` still overrides it for every host.
- **A named factory's host list is its cluster.** The hosts were taken from the endpoint URI (default `localhost`),
  so a factory with `Host = "r1,r2,r3"` connected to `r1` only and never failed over. The factory's own list is used now.
- **One pooled connection is one TLS identity.** The inline pool key now includes `sslServerName` and `sslCertPath`:
  two endpoints that differ only in the client certificate no longer share the first one's connection.
- An empty host list fails with a message naming the option instead of connecting to `localhost`.

### Fixed — RabbitMQ: a transacted consumer's acks and nacks take effect (review 2026-09-28, R3, R4)

- On a transacted channel an ack or nack is itself transactional and takes effect only at `tx.commit`. The consumer
  committed first and acked after, so each ack waited in the next transaction: the last delivery stayed
  unacknowledged and came back when the channel closed (a duplicate on every stop or restart). A failed delivery was
  rolled back and then nacked, so it was not requeued until the channel closed either. Every ack and nack is now
  followed by `tx.commit`; the nack is committed too, as Spring AMQP commits a transactional reject, since
  `tx.rollback` would discard it. `concurrentConsumers` above 1 needs no lock or restriction: each settle is final,
  and a commit that also carries a neighbour's settle leaves that neighbour's own commit an empty transaction.
- The acknowledgement no longer poses as a deferred action of the route's `.Transacted()` block: it had not been
  registered there since the `ackMode` rework, and the `ITransactedAction` implementation, its `Rollback` and its
  guards were unreachable. The integration test that claimed to cover a double ack did not reproduce one and is
  removed; the new tests check the broker's side (no delivery left behind after stop, a failed delivery redelivered
  at once, twenty concurrent deliveries with failures settled exactly once).

### Changed — RabbitMQ: an RPC reply keeps its bytes and its content type (review 2026-09-28, R1)

- **Behaviour change: `exchange.Out.Body` of a `replyTo=true` call is now `byte[]`**, as the body of a consumed
  message always was; it was a UTF-8 decoded `string`, which turned a binary reply (protobuf, PDF, gzip, a non-UTF-8
  EDI file) into replacement characters. A route that read the reply as a string decodes it, or converts the body
  with `.ConvertBody<string>()`. The reply is mapped by the same code as a consumed message, so `ContentType` and
  the other AMQP basic properties arrive as well; before, only the application headers did.
- The replying consumer sends the reply with the reply's own `ContentType` and headers when the route set
  `exchange.Out`; it used the request's type, so a JSON request answered with bytes went back labelled JSON.

### Fixed — RabbitMQ: a message no queue takes fails the send on every path (review 2026-09-28, R2)

- **The sends of a `.Transacted()` block lost an unroutable message in silence.** The block's batch is published on
  a transacted channel, which has no publisher confirms, so a mandatory message the broker returned (`basic.return`)
  left only a Warning in the log and the route went on as if it was delivered. The batch now collects the returns of
  its own commit and fails with the count: the messages that did route are committed, so a retry of the block sends
  those again.
- **Added `RabbitMQUnroutableException`** (exchange, routing key, reply code and text), thrown by every publish path,
  so one `OnException<RabbitMQUnroutableException>` covers them all. An immediate send and a request-reply call
  already failed, with the client's `PublishReturnException`; that is now the `InnerException`.
- A request-reply call whose request comes back no longer retries on a new reply queue first: no queue takes the
  request, so no reply can come.

### Fixed — RabbitMQ: option values checked at startup, and smaller fixes (review 2026-09-28, R5, R6, R8)

- **`exchangeType`, `queueType` and `overflow` are checked when the endpoint is created.** A value the broker does
  not know failed as a channel error at declare, in the broker's words; it now fails at once, naming the option and
  the allowed values. `exchangeType` takes `direct`, `topic`, `fanout`, `headers` in any case (written in lower case,
  as the broker names them) or an `x-` plugin type such as `x-delayed-message`; `queueType` takes `classic`, `quorum`,
  `stream`; `overflow` takes `drop-head`, `reject-publish`, `reject-publish-dlx`.
- `ProcessedCount` of a consumer counts every message with `concurrentConsumers` above 1; the unsynchronised
  increment could lose some.
- A late reply to a direct reply-to client that has gone (`amq.rabbitmq.reply-to.*`) is logged at Debug, as one to
  an `amq.gen-*` queue already was, instead of as a Warning about an unrouted message.

### Changed — RabbitMQ: a named connection factory is the whole connection (review 2026-09-28, R7)

- **Behaviour change: a URI that names `connectionFactory` and also gives a connection parameter is refused** when the
  endpoint is created (`host`, `port`, `username`, `password`, `virtualHost`, `clientName`, `ssl*`, the timeouts,
  `heartbeat`, the recovery options, `consumerDispatchConcurrency`). The factory already set all of them and the URI
  value was ignored in silence, so `connectionFactory=prod&host=other` went to `prod`. The rule stays Apache Camel's
  ("all connection options set on URI are not used"); only the silence goes.
- Endpoints without a factory share a pooled connection only when every connection setting matches. The first
  endpoint no longer decides the heartbeat, timeouts, recovery policy, client name or password of the others. The
  password enters the pool key as a short hash, since the key is logged.

### Added — RabbitMQ: `publisherConnection` (review 2026-09-28)

- `publisherConnection=true` sends an endpoint's publishing (its producer's sends and its consumer's RPC replies) over
  a second connection with the same settings, named `"<clientName> (publisher)"`. The broker's flow control and
  resource alarms block a publishing connection as a whole, and a consumer sharing it could not ack. Off by default,
  as in Spring AMQP: producers and consumers keep sharing one connection, and two named factories separate them too.

### Added — RabbitMQ: private CA, TLS versions, revocation, EXTERNAL and OAuth 2.0 logins

- **`sslCaCertPath`** (factory: `SslCaCertPath` or `SslCaCertificates`): the broker certificate must chain to these
  roots instead of the system trust store, for a private or corporate CA; the server name is still checked.
- **`ClientCertificate`** on a factory: the `X509Certificate2` itself (Windows store, key vault) for mutual TLS. A client
  certificate, from a file or as an object, must have its private key and be inside its validity period; both are
  checked before the handshake, naming the certificate.
- **`sslProtocols`** (`Tls12`, `Tls13`); SSL 3.0, TLS 1.0 and 1.1 are refused. **`revocationMode`** and
  **`revocationSoftFail`** check the broker certificate against CRL/OCSP; a chain that names no revocation source
  passes, as in the AS4 connector.
- **`authMechanism=External`**: the client certificate is the login (SASL EXTERNAL), no password is sent.
- **OAuth 2.0** on a factory (`OAuth2TokenEndpoint`, `OAuth2ClientId`, `OAuth2ClientSecret`, `OAuth2Scope`): the
  connection logs in with a client-credentials access token and renews it on the open connection before it expires,
  so the broker does not close it. The token endpoint must be https (http only on the loopback address). Any other
  source of credentials plugs in as `CredentialsProvider`. New dependency: `RabbitMQ.Client.OAuth2` 2.0.0.
- **A TLS setting without `ssl=true` is refused** instead of being ignored, and so are contradicting settings (a
  passphrase without a certificate, a certificate given twice, soft-fail without a revocation check, two ways to log
  in, EXTERNAL without a client certificate).
- DSL: `.SslCaCertPath()`, `.SslProtocols()`, `.RevocationMode()`, `.AuthMechanism()`.
- Tested against a broker with a private CA, mutual TLS, EXTERNAL and OAuth 2.0 (60-second tokens, the connection
  outlives the first one only through the renewal).

### Fixed — RabbitMQ: a channel the broker closed (review 2026-09-28, R9, R10)

- **A producer recovers from a channel-level error.** A publish to an exchange that does not exist (404) closes the
  channel but not the connection, and every later send failed with "The producer might need restart". The next send
  now opens a new channel (and a new reply queue for `replyTo=true`); only the send that hit the error fails.
- **A consumer that stops receiving says so.** When the broker closes its channel, or cancels its subscription
  because the queue was deleted, it logs an Error naming the queue; it went quiet before.
- A delivery waiting for a free consumer slot is counted as in flight, so `Stop` drains it too rather than letting it
  start once the drain has ended.

### Fixed — Kafka: a transactional producer stopped during its rebuild stays stopped (review 2026-09-28, R9)

- After a fatal transaction error the producer rebuilds its client before the next transaction. The rebuild went round
  `Start`: a producer stopped in the meantime got a new client that nobody closed, and committed the transaction as if
  it were running. It now closes that client and fails the transaction as a stopped producer does.

### Changed — Kafka: a record passed on keeps its key; guarantee-breaking librdkafka properties are refused (review 2026-09-28)

- **Behaviour change: the consumed key is carried over.** A producer without the `key` option now sends with
  `redbKafka.Key` (the key of the record the route consumed), as camel-kafka and Spring Kafka do. A route passing
  records on (Kafka to Kafka, retry, dead letter) used to send them without a key: they changed partition, lost
  per-key order and compaction. New option `keyFromHeader` (default `true`; DSL `.KeyFromHeader(false)`) restores
  sending without a key; `key` and `partitionNumber` keep precedence.
- **additionalProperties.** `enable.auto.commit=true` is refused (a timer committed records read but not yet processed,
  whatever `ackMode` said), as are `enable.idempotence` or `acks` contradicting `transacted=true` or
  `transactionalIdPrefix`. The rule, and the existing refusal of `transactional.id`, now covers the named connection
  factory's `additionalProperties` too; before, the factory was a way round it. Values that agree with the connector
  (`enable.auto.commit=false`, `acks=all`) are left alone.
- **One `content-type`.** A record passed on is sent with one `content-type` header, `Message.ContentType`'s; the
  consumed header was copied next to it.

### Fixed — Kafka: exactly-once no longer degrades in silence, one message shape in every mode (review 2026-09-28)

- **Offsets left out of a transaction are reported.** When a transactional producer cannot take the consumed offsets,
  because the consumer's brokers are written differently (another cluster by the connector's reckoning) or because the
  route already returned to the consumer (an asynchronous step inside `.Transacted()`), the producer logs a warning with
  the cause, once per producer, then at Debug. Before, only a Debug line told that the route was not exactly-once.
- **Batch mode.** A record read with `maxPollRecords` now carries `redbKafka.Key` and `redbKafka.Timestamp`, as in
  single mode; a route reading the key got `null` in batch mode.
- **Deferred send metadata.** A send deferred to `.Transacted()` with `recordMetadata=true` now sets
  `redbKafka.Sent.Timestamp`, as an immediate send does.
- **Idempotence against the factory's acks.** `enableIdempotence=true` on the endpoint with `acks` other than `all`
  from the connection factory is refused in the connector's words at start, instead of by librdkafka.
- **Docs.** `seekTo` accepts `beginning` or `end` only; the `acks` default is `All`; the README states when the
  consumed offset rides in the transaction and which headers go on the wire.

### Fixed — AS2: the receiver authenticates before it acts, and an MDN confirms only what it proves (review 2026-09-28)

- **Receipt URL (security).** An asynchronous MDN is posted to the sender's `Receipt-Delivery-Option` only when the
  message authenticated, the agreement's `MdnMode` is `Async`, and the host is in the new
  `As2ConnectionFactory.AsyncMdnAllowedHosts` (required for an async receive endpoint). Before, any POST with that
  header, even with a forged signature, made the receiver post a receipt signed by our key to a loopback or private
  address of the caller's choosing. The receipt is posted after the `200`, outside the request, bounded by `timeout`.
- **Identifiers.** `AS2-From` and `AS2-To` must be the agreement's (RFC 4130 §6.2); otherwise the message is refused
  with `authentication-failed` and does not reach the route.
- **MIC.** An MDN without `Received-Content-MIC`, or for a message nobody waits for, is no longer a match. New headers
  `redbAs2.mdnMicStatus` (`matched`, `mismatch`, `absent`, `unknown`) and `redbAs2.mdnConfirmed` (positive, MIC
  matched, signature valid where a signed MDN is agreed); `RequireValidMdn` fails the send on any of them. MIC
  digests are compared as bytes.
- **Asynchronous MDN policy.** An unsigned or foreign-signed receipt where `SignedMdn` is agreed is not a
  confirmation and does not end the wait; with `RequireValidMdn` it is refused with `400`.
- **Agreement checked at start.** `As2ConnectionFactory.Validate(name)` (identifiers, algorithms, certificates and
  private key, URLs) runs when each endpoint starts. With a factory named, agreement options on the URI are refused.
  `sha-1` and `3des` need the new `AllowLegacyAlgorithms`. Pinned certificates must be within their validity period.
- **Protocol.** An MDN only when `Disposition-Notification-To` asks for one, signed only when asked, with the requested
  `signed-receipt-micalg`; the producer asks `required` when `SignedMdn`. A refused message's MDN carries the RFC 4130
  §7.4.3 code and a reference instead of our exception text. Dispositions are read by their fields.
- **Limits and lifecycle.** New `maxRequestBodySize` (receive, 100 MB, 413 above) and `maxResponseBodySize` (send,
  4 MB). The MDN receiver drains on stop, takes the admission limit, opens a `Consumer` span, and labels its exchanges
  with `remoteAddress` and `partner`. The producer disposes its response.
- **Headers.** `Authorization`, `Proxy-Authorization` and `Cookie` of the inbound hop are not put on the exchange;
  `Authorization`, `Cookie` and `Set-Cookie` are not bridged onto an outgoing message.

### Fixed — AS2: interop with OpenAS2 on asynchronous MDN, and the MIC of a received message

- The receiver hashed a received signed part after preparing it for 7bit, so a partner that signs a `binary` part
  (OpenAS2) got a MIC it rejects. The MIC is now over the part as received (RFC 4130 §7.3.1). OpenAS2 checks the MIC
  only on asynchronous MDNs, which is why synchronous interop did not show it.
- The machine-readable MDN part could go out quoted-printable when a field line was long; RFC 3798 requires 7bit, and
  OpenAS2 then cut the Original-Message-ID and could not correlate an asynchronous MDN.
- The producer registered the wait for an asynchronous MDN after the partner's 200; a partner that posts the MDN first
  (OpenAS2 often does) was read as an MDN for an unknown message. The wait is registered before the POST and dropped
  when the send fails.

### Added — AS2: the received message is spooled; `streamBody`

- The request, each decrypted and decompressed stage and the payload go through the core stream cache (a temporary
  file past its threshold), as in the AS4 receiver, instead of three or more copies in memory. Before, parallel
  unauthenticated POSTs up to `maxRequestBodySize` each could exhaust the heap. The MIC is hashed as it is written.
- `streamBody` (receive): the exchange body is the spooled payload as a `Stream`, closed with the exchange.

### Added — AS2: duplicate detection

- `idempotentRepository` on a receive endpoint: a `Message-ID` processed before is answered with
  `processed/warning: duplicate-document` and not delivered again; the claim is taken after authentication and
  released when the route fails.

### Removed — AS2: `certPassword`

- The URI option did nothing (certificates are on the connection factory). A URI that sets it is now refused, saying
  where certificates go.

### Fixed — Route-XML `<bean>`: constructors and factory methods are chosen the way C# chooses them; `type=` names an overload

- A certificate could not be loaded from a PFX in XML on .NET 8. `X509CertificateLoader` exists from .NET 9, and
  `X509Certificate2(string, string)` is one of several two-argument constructors, so the choice failed with
  "Multiple constructors accepting all given argument types". Reported by the AS4 agent; it concerns every
  connection factory holding a certificate (AS2, Soap, AS4).
- The documented `X509CertificateLoader.LoadPkcs12FromFile` example never worked either: the method has two more,
  optional, parameters, and a factory method was looked up by the exact number of arguments.
- Constructors and factory methods are now called the way C# calls them. The parameters after the given
  arguments must be optional and take their defaults, including enum defaults. An overload no markup value can
  reach (a `ReadOnlySpan<char>` or `ref` parameter) is not a candidate.
- `<constructorArg type="System.String">`, like Spring's `constructor-arg type`, names the parameter types; the
  constructor or factory method is then the one with exactly that signature, and each value is converted to its
  type. `type=` goes on every argument or on none.
- Several factory-method overloads of one arity used to be resolved by taking the first. That is now a load error
  listing the candidates. `check --bin` applies the same rule and checks each value against the chosen parameter.
- XSD, the VS Code schemas, the generator (`XmlBeans.Create(…, argumentTypes: [...])`), README and the format
  spec.

### Added — Route-XML: an option on the side of the endpoint that does not read it is caught in the editor and by `check --bin`

- `[EndpointRole]` declares options only one side reads: `http:` producers send `authScheme`,
  `username`, `password`, `authToken`; `http:` consumers require `inboundAuth`, `inboundUsername`,
  `inboundPassword`, `inboundRealm`, `tokenValidator`. The connector refuses such an option on the other side,
  but only when the route starts.
- The catalog carries the side (`CatalogOption.Role`, `"role"` in `redb-route-catalog.json`, read through
  `EndpointOptions.RoleOf`). The catalog schema generates the endpoint elements twice: inside `<from>` the
  consumer's view, inside the other address-carrying steps the producer's (`pollEnrich` included: the engine
  polls through a producer). A strict connector does not declare the other side's options; a lenient one
  (`http:`) declares them with a type no value satisfies, so the editor's message names it
  (`producerOnlyOption`, `consumerOnlyOption`).
- `check --bin` / `pack --bin` report an option on the wrong side, URI form and structured form alike, with its
  position: `'username' is read only by the producer side of the http endpoint; <from> creates the consumer,
  which refuses it.`
- VS Code catalog schema and `redb-route-catalog.json`: `http` and `https` regenerated with the sides.

### Added — `[EndpointRole]`: an option only one side reads says so on its property

- `[EndpointRole(EndpointRole.Consumer)]` / `[EndpointRole(EndpointRole.Producer)]` on an options property, Camel's
  `@UriParam(label = "consumer"/"producer")`. `EndpointOptions.RoleOf(property)` reads it by reflection, without an
  endpoint, for the Route-XML catalog, the editor schema and `check`; `EndpointOptions.WrittenForOtherRole(...)` gives
  the parameters a URI wrote for the other side, as written.
- The http: endpoint declares its nine side-only options with it (`authScheme`, `username`, `password`, `authToken`
  for the producer; `inboundAuth`, `inboundUsername`, `inboundPassword`, `inboundRealm`, `tokenValidator` for the
  consumer) and refuses them on the other side by the declaration instead of two hand-kept lists of names. Behaviour
  and messages are unchanged.

### Added — Route-XML: the package gate knows repository names and types

- `<redb><idempotentRepository name>` now counts as a declaration of that name, like `<bean name>`. Before, the
  gate warned that `repository="#dedup"` named nothing although the repository was declared (the bare name is
  the registry key).
- With `--bin` the gate checks that the object a repository reference names implements what the reference
  needs: `IIdempotentRepository` for `<idempotentConsumer repository>`, `IClaimCheckRepository` for
  `<claimCheck repository>`. A bean of another type is an error with its position, worded like the engine's
  refusal (`'dedup' is registered as …, which is not an IIdempotentRepository`).
- Contributions declare this themselves: `AttributeSpec.References` (the type a reference must name) and
  `AttributeSpec.Registers` (the attribute names a registry entry of that type), init properties next to
  `Resource`. The gate knows no package element by name.

### Fixed — Route-XML tooling: `http:` and `log:` were missing from the component catalog

- `redb-route-xml catalog` created only components with a parameterless constructor. `HttpComponent(string
  scheme = "http")` and the log component have optional parameters only, so both were skipped: the editor
  flagged every structured `<http>` endpoint and offered no `http:` options. Constructors with only optional
  parameters are now used, the way the engine calls them.
- VS Code catalog schema and `redb-route-catalog.json`: `http` and `log` added, `https` refreshed (the inbound
  authentication options), `<rest>` refreshed from the generator (`clientRequestValidation`, `errorHandler`,
  `<param>`, the inbound authentication attributes). The `<rest>` edits had gone into the lean
  `redb-route-1.0.xsd`, which the editor does not read. The hand-made `ackMode` edits were checked against
  the generator output from the connectors' own builds and match byte for byte.

### Changed — idempotent and claim-check repositories are found by their bare name

- **Breaking** for a repository registered by hand under `idempotent:{name}` or `claimcheck:{name}`
  (`AddToRegistry("idempotent:orders", …)`): it is no longer found. Everywhere else `#name` is the registry key as is
  (`dataSource=#main-db`, `bean:#x`, `routePolicy`, `ref=`); these two lookups were the exceptions, so a bean declared
  in markup under `dedup` was not the repository `<idempotentConsumer repository="#dedup">` or an AS4 consumer's
  `idempotentRepository=dedup` looked for, and the context did not start.
- The providers look up the bare name, checked for type, as Camel's `lookupByNameAndType`, and tell the two failures
  apart: nothing registered under the name, or an object of another type there (`'dedup' is registered as …, which is
  not an IIdempotentRepository`). The messages point to `AddIdempotentRepository(name, repo)` /
  `AddClaimCheckRepository(name, repo)` or `<bean name="…" type="…"/>`, not to a prefixed key.
  `IIdempotentRepositoryProvider.TryGet` is false for nothing and throws for another type.
- `AddIdempotentRepository`, `AddClaimCheckRepository` and `AddRedbIdempotentRepository` register under the bare name;
  their signatures are unchanged. `RegistryIdempotentRepositoryProvider.KeyPrefix`,
  `ClaimCheckRepositoryRegistry.KeyPrefix` and `ClaimCheckRepositoryRegistry.DefaultKey` are removed.
- The claim-check repository of steps that name none is the context's `IClaimCheckRepository` service
  (`SetDefaultClaimCheckRepository`, or one the host registered), else one shared in-memory repository kept as that
  service — no `claimcheck:__default` registry key.

### Added — `IRouteContext.GetStreamCacheOptions()` for connectors that spool

- The stream cache options in force for a context, resolved as the `.StreamCaching()` step resolves them: a
  registered `StreamCacheOptions` service, else the engine's `RouteEngineOptions.StreamCaching`, else the defaults. A
  connector spooling through `StreamCache` (AS4) could reach only the registered service, so a threshold and a
  directory set on the engine never reached it. The engine-wide caching switch takes its threshold and directory
  through the same chain.

### Changed — `StreamCache` is public for connectors; stream caching closes what it replaces and obeys the engine switch

- `StreamCache` (memory up to a threshold, then a temporary file with `DeleteOnClose`) is public: a connector spools
  what it must not hand to the route before it is complete and checked — AS4 decrypts an attachment into one and
  passes the stream on only after the tag and the signature verified. It is written through (`Write`, `WriteAsync`,
  `CacheFromSourceAsync`), then `CompleteWriting()` rewinds it and makes it read-only and seekable; reading before or
  writing after that throws `NotSupportedException`. Built from `StreamCacheOptions` or a threshold and a directory.
- The `.StreamCaching()` step closes the stream it replaces. It used to leave it open: the exchange disposes only what
  the body is, so a streamed file (`streamBody=true`) stayed open until the garbage collector ran.
- `RouteEngineOptions.StreamCaching.Enabled` works: every route caches a stream body before its first step (Camel's
  context-wide `streamCaching`). It was never read. `.StreamCaching()` without its own threshold takes the engine's
  options.
- The endpoint byte counter measures a stream body by its length, or counts nothing for a forward-only one, instead
  of serializing the stream object.

### Security — an `http:` consumer drops response-only headers from a request

- `Set-Cookie`, `Location`, `WWW-Authenticate`, `Proxy-Authenticate`, `Authentication-Info`,
  `Proxy-Authentication-Info`, `Retry-After`, `Server`, `Age`, `ETag`, `Accept-Ranges` and `Vary` in an inbound
  request are dropped at the transport, before the exchange is built (`HttpHeaders.ResponseOnlyHeaders`): neither the
  route nor the response ever holds a value a client planted under a response field's name. Stricter than Camel's
  inbound filter, which removes only its own headers. The value comparison of the previous entry stays as the second
  layer, and the connector README states the contract: a processor that builds a response header from a request value
  answers for it.

### Fixed — every credential on a connection factory is marked `[Sensitive]`

- `LdapConnectionFactory.BindPassword` and 34 more secrets on 21 connection factories (passwords, passphrases, API
  keys, access and session tokens, connection strings) lacked the declaration the renderers mask by; SOAP, AS4 and
  Kafka had it. A test over the factories' sources keeps new ones from slipping through.

### Security — a client can no longer suppress a response header the route wrote

- An `http:` consumer does not echo the request's headers back, and it matched them by name: a request carrying
  `Cache-Control` removed the route's `Cache-Control: no-store` from the response, so a token response became
  cacheable at the client's say. Any header the route wrote under a name the client also sent was dropped the same
  way. Only a value that is still the one the client sent is held back now; what the route wrote goes out.

### Added — inbound authentication on `http:` consumers and `Rest(...)`: Basic and Bearer

- `inboundAuth=basic` checks `Authorization` against `inboundUsername` / `inboundPassword` (both halves compared in
  constant time); `inboundAuth=bearer` hands the token to a registered `IHttpTokenValidator` named by
  `tokenValidator=#name`. A refused request gets 401 with `WWW-Authenticate` (`Basic realm="…", charset="UTF-8"`, or
  `Bearer realm="…"` with `error="invalid_token"` when the validator refused it) and never reaches the route; a
  validator's principal whose identity is not authenticated counts as refused. An accepted request reaches the route
  with the principal on the exchange (`ExchangePrincipal`) and without the `Authorization` header.
- The connector does not read tokens: JWT, signing keys, scopes and roles belong to the identity provider's library,
  plugged in through `IHttpTokenValidator`. Written in the connector README as a boundary.
- A half-declared check stops the start: credentials or a validator without `inboundAuth`, `basic` without a password,
  `bearer` without a validator, an unregistered validator name, a realm with quotes. The inbound options on a producer
  are refused, as the producer's `authScheme`/`username`/`password`/`authToken` already are on a consumer.
- `Rest(...)` takes the same options for every route of the declaration, the OpenAPI document included; Route-XML
  `<rest>` takes them as attributes, and the editor schema knows them. The C# generated from `<rest>` now carries the
  `using` directives it needs (`redb.Route.Http`, `redb.Route.Http.Rest`), which it lacked.

### Fixed — a `sql:` step that would write past the route transaction is refused

- On SQLite a `sql:` statement, procedure or batch inside `.Transacted()` ran in autocommit: Microsoft.Data.Sqlite
  does not enlist in System.Transactions, so a block that rolled back kept the step's rows, while the README promised
  enlistment. The step now refuses before it writes when its connection stays outside the ambient transaction:
  Microsoft.Data.Sqlite, Firebird without `Enlist=true` (its client enlists only when asked), and any provider with
  `Enlist=false` or `AutoEnlist=false`. PostgreSQL and SQL Server were checked live: two `sql:` steps in one block
  share the transaction, as before.

### Changed — one `ackMode` on every broker consumer

- **Breaking.** When a consumer settles what it received is one option with one meaning, `ackMode=manual|auto`,
  on RabbitMQ, AMQP 1.0, Kafka, SQS, Azure Service Bus and Redis Streams. `manual` (the default) settles after the
  route by its outcome: acknowledged when the exchange ended well, released for redelivery when it failed
  (at-least-once). `auto` settles on receipt, before the route runs (at-most-once): RabbitMQ auto-ack, Service Bus
  ReceiveAndDelete, Redis `NOACK`, and for AMQP, Kafka and SQS the consumer accepts, commits or deletes right away.
  The default behaviour of every consumer is unchanged.
- It replaces `autoAck`, `autoAccept`, `enableAutoCommit`, `deleteAfterRead` (SQS), `receiveMode` (Service Bus)
  and `streamNoAck`, in URIs, in the fluent builders (`.AckMode(AckMode.Auto)`) and in the editor catalog. The old
  names are refused with their replacement named. `autoAccept=false`, `enableAutoCommit=false` and
  `deleteAfterRead=false` settled nothing at all (no API let the application do it), so every message came back or
  no offset ever moved; they have no successor.
- Contradictions are refused when the endpoint is created: `ackMode=auto` with RabbitMQ `transacted`, with Kafka
  `breakOnFirstError`, with SQS `extendMessageVisibility` or `resetVisibilityOnFailure`, with Redis
  `streamClaimMinIdleMs`.
- `TRANSACTIONS.md` has the table of what each broker does in each mode.

### Added — Route-XML: a strict connector's unknown parameter is caught in the editor and by `check --bin`, not at start

- A connector not marked `[LenientProperties]` refuses a parameter it has no option for when the endpoint is
  created. In XML that surfaced only when the route started: the catalog schema let any attribute through on
  every connector, and the package gate did not look at endpoint parameters.
- The catalog carries the engine's flag (`CatalogComponent.Lenient`, `"lenient"` in `redb-route-catalog.json`,
  read through `EndpointOptions.IsLenient`). In the catalog XSD a strict connector's element takes no unknown
  unqualified attribute; a lenient one (`http:`, `sql:`, `bean:`) takes any, as before.
- `check --bin` / `pack --bin` build the catalog from the same output and hold every endpoint's parameters
  against it, URI form and structured form alike. An unknown name on a strict connector is an error with its
  position, worded by the engine itself (`'perod' is not an option of the Timer endpoint. Did you mean
  'period'?`). Names built at run time (`${…}`, `{{…}}`) and `interceptFrom` patterns are left to the engine.

### Fixed — Route-XML: a foreign-namespace attribute on a structured endpoint is no longer a parameter

- The format tolerates attributes in other namespaces as metadata for other tools, but the structured form
  passed them to the connector by their local name: `<timer ui:xy="10,20"/>` became `xy=10,20`, which a strict
  connector refuses when the route starts. They are now skipped, in the loader and in the C# generator. The
  loader, the generator and the gate read a structured endpoint's options through one function.
- The VS Code catalog schema gets the strict connectors with the next regeneration from release bins
  (RELEASE_ARTIFACTS §5a); until then the editor keeps accepting unknown attributes on them.

### Fixed — `ConsumerTemplate` no longer loses what it does not hand out; `DoneUoW` as in Camel

- **Data loss.** A `Receive` from a polled directory with `delete=true` handed over one file and deleted all of
  them: every `Receive` started a throwaway consumer whose processor returned at once, so the consumer
  committed each exchange of its poll (deleted or moved the file, acknowledged the message) whether or not
  anyone received it, and released the handed-out exchange before the caller read it.
- The template now keeps one consumer per endpoint, started on first use and stopped with the template. Each
  exchange waits in a queue for a `Receive`; the source commits it only when the caller calls the new
  `DoneUoW(exchange)` (Camel's `doneUoW`), and rolls it back when the caller set `exchange.Exception` first.
  Exchanges nobody received stay uncommitted in their source. `ReceiveBody*` completes the unit of work
  itself, reading a streamed body into memory first. A template stopped with units of work still open rolls
  them back: the file stays, the message is redelivered.
- **Breaking** for a caller of `Receive` on a non-SEDA endpoint: it must call `DoneUoW`, otherwise the source
  keeps the exchange until the template stops. SEDA exchanges accept `DoneUoW` too, so one loop serves any
  endpoint.

### Fixed — VS Code editor: the schema it reads knows `<bean>` lists and references and the new `<redbQuery>` attributes

- The extension validates markup against `redb-route-1.0.catalog.xsd` (bound in `media/catalog.xml`). The
  schema edits for `<bean>` lists and `ref=`, and for `whereRedb`, `orderByRedb` and `outputType` of
  `<redbQuery>`, went into the lean `redb-route-1.0.xsd`, which the editor does not read, so the editor
  flagged valid markup and offered no completion for it. Both sections are now taken from the generator
  into the bound schema.
- Route-XML README: `in` / `not in` in conditions and in `<redbQuery>`, with an example covered by a test.
- `RELEASE_ARTIFACTS.md` §5a named the wrong schema as the one the editor reads, and ran `xsd` without bins,
  which drops the package elements from the lean schema. Both corrected.

### Changed — endpoint parameters: a bad value or an unknown name stops the endpoint, for every connector

- **Breaking** for a route whose URI carries a mistake, and every such route ran with an option other than
  the one written. A known option whose value does not convert to its type (`recursive=yes`, `maxDepth=abc`,
  `mode=pul`, a number that names no enum member, `${...}` in an option that takes no expressions) used to
  land among the unmapped parameters and leave the option at its default without a word. It is now refused
  when the endpoint is created, in `EndpointOptions.BindFromUri`, for all connectors at once. The message
  names the value and what the option takes, enum values spelled as the URI takes them (`mode=push, mode=poll`);
  the value of a `[Sensitive]` option is not shown.
- An unknown parameter name is refused the same way unless the options type is marked
  `[LenientProperties]`, Camel's `lenientProperties` (strict unless declared). The message names each
  unknown parameter, by name only, with the nearest option. `http:`, `sql:` and `bean:` are lenient: they
  read the extra parameters themselves (`param.*`, bean properties). The mark is an attribute on the options
  type, so tooling reads it without creating an endpoint (`EndpointOptions.IsLenient`).
- A connector adds its own words through `OptionValueHint` (a refused value) and `UnknownParameterHint`
  (a refused name). The copies of this check in `kafka:` and `redis:` are gone; `redis:` keeps its
  explanation of the removed `streamAutoAck` through the hook. The `sql:` check of unconvertible values is
  gone too, the core one covers it.
- `double`, `float` and `decimal` options no longer accept a thousands separator: `ratio=1,5` was read
  as 15, it is now refused.

### Added — REST request validation: 406, declared parameters, error handler

- `Param(name, type, required, dataType, description)` on a REST verb declares a path, query or header
  parameter; `<param>` does the same under a verb in Route-XML. Declared parameters always go to the
  OpenAPI document with their location, requirement, type and description.
- `RestOptions.ClientRequestValidation` (Camel's `clientRequestValidation`), overridable per verb with
  `ClientRequestValidation(bool)` / `clientRequestValidation=`, enforces them: an `Accept` that excludes
  `Produces` is answered with 406 (RFC 9110 ranges and weights, the most specific range decides, `q=0`
  refuses, no header admits everything); a missing required parameter or a value that does not convert to
  its `dataType` (`integer`, `number`, `boolean`) with 400 naming the parameter. Off by default, and switched
  on only by the option, never by the presence of `Param`: a route that described its parameters for the
  document must not start refusing requests after an upgrade. The 415 check of `Consumes` is unchanged and
  runs either way.
- A path parameter must be a segment of the template and cannot be optional; a parameter declared twice
  is refused at declaration.
- `RestOptions.ErrorHandler` (`errorHandler="#name"`) names a registered `IProcessor` that writes the body
  of a 415, 406 or 400. It finds code, reason and parameter in `RestErrorProperties`. A name that is not
  registered stops the start. Without it the body stays the reason as `text/plain`.
- The editor schema (`redb-route-1.0.xsd`) knows the new attributes and `<param>`.

### Added — `in` and `not in` in the route language and in `redbQuery`

- Membership of a value in a list, as Camel Simple spells it: `header.tier in ('gold','silver')`,
  `header.code not in header.codes`. Works wherever a condition does — `filter`, `choice`/`when`, `onWhen`,
  intercepts. Asked for 2026-09-25 so markup can search by a list of keys in one step.
- The right side is a collection the message carries (`header.codes`, `body.ids`) or a list literal in
  parentheses (`('a','b')`, `(1, 2, 3)`, `()`); parentheses after `in` always mean a literal. Camel's comma
  string (`in 'gold,silver'`) is not adopted: a comma inside a string already means something else in
  `include` and `antInclude`, and a string on the right is refused with both forms named rather than
  iterated character by character. Elements compare with the same equality `==` uses, so `in` cannot
  disagree with `==` about `2` and `2L`. A missing collection is an empty one. `in(` without a space is the
  operator, not a call to a function named `in`; `header.in` is still a header called "in".
- `redbQuery` translates `member in list` into `Enumerable.Contains` over an array typed as the member,
  which the storage turns into an SQL `IN` for props (`where`) and base fields (`whereRedb`) alike. Each
  element converts to the member's type exactly as a single compared value does. An empty list is decided
  in the route — nothing found for `in`, everything for `not in` — and never reaches the storage as an
  empty `IN`, whose meaning is settled in SQL the route cannot see. A member on the right of `in` is
  refused when the route is built. Verified live on PostgreSQL and SQL Server.

### Security — an `http:` consumer refuses credentials it would not check

- `authScheme`, `username`, `password` and `authToken` are what an `http:` producer sends. They live in the options class the
  consumer shares, so `from("http://0.0.0.0:8080/api?authScheme=basic&username=admin&password=…")` bound without
  a word, passed validation — which even demanded a non-empty user and password — and started an endpoint that
  checked nothing. Its author believed it was closed, and the password sat in the route key besides. Found
  while planning inbound authentication, 2026-09-25.
- Creating such a consumer now fails, naming the parameters and never their values. Only what the consumer's
  own URI wrote counts: a named connection factory may carry producer credentials beside the TLS material a
  consumer reuses it for, and that says nothing about inbound access. Producers are unchanged.
- **Breaking** for a route that set these on a consumer — and every such route was unprotected, so the break
  is the point. Put inbound authentication in front of the route (a reverse proxy, or a processor that checks
  `Authorization`) until the connector offers its own.

### Fixed — the SSE summary event carries numbers as numbers, in any locale

- The `event: done` trailer an `http:` consumer writes after a streamed reply turned every value into a string
  through a culture-dependent `ToString()`: a token count went out as `"7"` rather than `7`, and on a
  Russian-locale worker `llm.cost.usd` went out as `"0,0123"` — a number rendered with a comma, inside quotes.
  The Llm documentation shows plain numbers, so the contract and the code had already parted. Reported
  2026-09-25.
- Numbers and booleans are now written as JSON numbers and literals; anything else as invariant text. A client
  that parsed the old strings as strings needs to read numbers instead — the documented shape was always
  numbers.

### Fixed — a cached prompt reaches the usage of an OpenAI-compatible provider

- `OpenAiProvider` read only `prompt_tokens` and `completion_tokens`, so a prefix the provider served from its cache
  never reached `LlmUsage.CacheReadInputTokens`, the `llm.tokens.cache.read` header, or an audit and a cost
  calculator built on them. With DeepSeek, which caches a repeated prefix by itself, every turn looked like a
  full-price prompt (issue #11). The cached part is now read — OpenAI's `prompt_tokens_details.cached_tokens`,
  DeepSeek's `prompt_cache_hit_tokens`, whichever the provider fills — and comes out of `InputTokens`, which is the
  remainder billed at full price, not the whole prompt (DeepSeek names that remainder itself in
  `prompt_cache_miss_tokens`). A token budget and a cost estimate therefore stop paying for a cached prefix twice.
- The streamed path reports the same numbers: it hands the terminal usage frame to the same parser.
- `CacheCreationInputTokens` stays zero on this path: DeepSeek does not bill cache creation separately and OpenAI's
  cache is implicit. Anthropic, which reports both sides, is unchanged.
- The stored conversation keeps the cache counters too, for every provider: `MessageProps` gains
  `CacheCreationInputTokens` and `CacheReadInputTokens`, and a turn read back from `RedbConversationStore` carries its
  whole usage. They used to be dropped on write, Anthropic's included. Turns stored before read them as zero.

### Added — `RedbQuery` / `<redbQuery>` returns one object, a count or a yes/no

- The query always returned a list, so a lookup by key read `body[0]` and an existence check
  loaded rows to test them for emptiness.
- New `outputType`: `List` (default, `ToListAsync`), `First` (`FirstOrDefaultAsync`, the object
  or `null`), `Count` (`CountAsync`), `Any` (`AnyAsync`). It exists in C#
  (`RedbQueryOutput`) and as an attribute of `<redbQuery>`, and the generator prints it.
- `First` takes ordering and `skip`, not `take`. `Count` and `Any` refuse ordering, `take` and
  `skip`. The unbounded-scan guard now applies to `List` only, the one output that loads every
  matching row.

### Added — `RedbQuery` / `<redbQuery>` filter and order on the base fields of the stored object

- A route query could filter only on props. The base fields of the object table (`Id`, `ParentId`,
  `ValueGuid`, `ValueString`, `DateCreate` and the rest) were out of reach, although they are
  indexed columns and the condition that cuts the most rows. Code used redb `WhereRedb` in a bean
  instead, and the markup had nothing to offer.
- New `whereRedb` condition, the same as redb `WhereRedb`, and `orderByRedb`, the same as
  `OrderByRedb`. They exist as `RedbQuery(whereRedb:, orderByRedb:)` in C# and as attributes of
  `<redbQuery>` in XML, and the generator prints them. `whereRedb` combines with `where` by AND,
  because the storage cannot mix base fields and props inside one OR. A query orders by
  `orderBy` or by `orderByRedb`, not both.
- Only the fields the core maps to columns are accepted. The core maps an unknown name to `_id`
  without a word, so a computed member such as `HasParent`, a props name, or a nested path is
  refused when the route is built. `ValueBytes` is left out: it has no comparison to express.
- The condition translator now serves both roots, props and base fields. Text dates convert to
  `DateTimeOffset` members, and the refusal names the attribute (`where` or `whereRedb`) and the
  candidates.

### Added — Route-XML `<bean>` holds lists and references to other beans

- A bean could not list other beans, so a node with its partners (each partner its own registered
  bean, a second certificate slot for rotation) could not be declared in XML. The AS4 agent reported
  it on 2026-09-25 and worked around it with a string of names.
- `<property>` and `<constructorArg>` now take `ref="name"` or a `<list>`. A list holds `<value>`,
  `<ref bean>`, anonymous `<bean>` and nested `<list>` items. It builds into the declared type
  (`T[]`, `List<T>`, `IList<T>`, `ICollection<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`,
  `IEnumerable<T>`), and each item is checked against `T`. A list passed to a constructor names its
  element type with `of=`.
- A reference may only point at a bean declared earlier in load order, or at one registered by
  module code. A forward or unknown reference is a positioned error, never a `null`.
- One grammar (`BeanModel`) and one builder (`BeanFactory`) now serve the loader, the generated C#
  (`XmlBeans.Ref`, `XmlBeans.List`) and the `check` gate. The gate reports forward references across
  files. With `--bin` it also checks list items, nested beans and the type a `factoryMethod` returns.
  Before this, nested beans went unchecked, and a factory bean was checked as its declaring type.
- Generated `XmlBeans.Create` now resolves `{{key}}` in constructor arguments too. Before, it did so
  only in properties.
- XSD and the VS Code extension schema: `ref=` on the slots and the `beanList` type.

### Fixed — stopping an HTTP-hosted route waits for the requests it is already serving

- `http`, `soap`, `as2` and `grpc` unregistered the route and asked the shared listener to stop when
  empty. When another route still holds the port the listener stays up, so nothing waited: a deploy
  that removes one route cut its in-flight requests mid-pipeline while the port kept serving. Reported
  by the AS4 agent 2026-09-25, who hit it while planning the same lifecycle for ebMS.
- All four now count requests through the engine's `InflightDrainGuard` and drain in `Stop()` after
  unregistering — new requests can no longer reach the route, the ones inside it finish. This is the
  guard every broker consumer already uses, and the one SignalR and WebSocket were already using; the
  HTTP family was the gap.
- The drain is bounded (30 seconds by default) and force-cancels what is left, so a hung handler
  cannot hold a deploy forever.
