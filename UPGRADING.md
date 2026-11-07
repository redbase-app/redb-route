# Upgrading redb.Route

What to check and change when moving to a new version. Each section lists the changes that need action;
[CHANGELOG.md](CHANGELOG.md) has the full list of what changed.

## 4.7.0

### An unclosed `${` is an error

A `${` that no `}` closes used to be read as plain text, or as an empty value when a later `}` closed it by
accident (`${a ${b}` gave nothing). It now fails where the template is compiled:

```
unclosed '${' at position 6 of "text ${body": the placeholder has no closing '}'.
```

A route file fails to load with that message at the element's position, and `check` / `pack` report it as an
error at the attribute. In C#, the `StringExpression`, `SetBody`, `SetHeader` or endpoint option that holds the
template throws when the route is built. To find offenders before upgrading, run `check` over the route files and
search code for `${` without a closing `}`. A literal `${` is written `\${`. `ExpressionResolver.IsTemplate` now returns `true` for a string with an unclosed
`${` (it returned `false`), so a caller that branches on it sends such a string to the compiler and gets the error.

### An unended string literal is an error

A quoted literal that never closes (`header.kind == 'order`) used to be read cut short by one character, silently. It
now fails where the expression is compiled, at load for a route file:

```
unterminated string literal starting at position 13 within the expression "header.kind == 'order".
```

Close the quote. A literal that needs a quote character inside writes it escaped: `'it\'s'`.

### `AND` and `OR` short-circuit

The right side of `AND` / `&&` and `OR` / `||` used to be evaluated even when the left side already decided. It now
runs only when it matters, as in C#. The results are the same; what stops is a side effect on that right side
(`header.n++`, a `bean:` call) and an error it would raise. A route that relied on such a side effect running moves it
out of the condition. `XOR` still evaluates both sides.

`??` follows the same rule: its right side runs only when the left side is `null` (it used to run always in a
compiled expression). The value is unchanged.

### A payload template's `expr(...)` evaluates its text

`expr(x)` in a payload template, and `ExpressionResolver.ResolveExpression`, now read `x` exactly as the inside of a
`${x}` hole. They used to evaluate it only when it held an operator written with spaces, a function call or an index,
and passed any other text through as it was. So `expr('1>0')` gave the text `1>0`, `expr('42')` the text `42`, and a
`bean:` call came back unevaluated.

Now the text is an expression: a quoted literal gives its content (`expr("'yes'")` gives `yes`), a number or a
boolean keeps its type, and a comparison is evaluated with or without spaces. A bare word or a path with `/` or `.` is
a lookup, as in a `${}` hole, and gives empty when nothing answers (`expr('application/json')`); an unquoted
`jpath`/`xpath` path is allowed, as in a hole. A constant belongs in the template text, not in `expr(...)`.

### Incrementing a value that is not a number is an error

An increment or decrement of a header or property that holds something other than a number (`'abc'`) used to treat
it as 0 and overwrite it with 1 or -1, silently. It now fails with
`header.x++: the value 'abc' (String) is not a number.` and leaves the value as it was. A missing value still starts at
0, and a numeric text (`'7'`) still counts.

### Comparing numbers of different types

A comparison of two numbers of different types, or of a number with a numeric text, used to be true whenever the
left value was the smaller one: with `header.qty` = 3 (an `int`), `header.qty == 5.0` and `header.qty == '5'` were
true. Such pairs are now equal only when their values are equal. This applies to `==`, `!=`, `in`, `not in` and
`.contains(...)` on a collection.

What to look for: filters, `when` branches and validations that compare an `int` or `long` header with a decimal
literal (`5.0`), with a quoted number (`'5'`) or with a number read from text (a JSON, XML or form field). Their
outcome may change, and the new one is the correct one. Same-type pairs (`int` with `int`, text with text) are
unchanged.

### A method call on a value fails loudly

Every expression is compiled and run by one engine now, so a method called on a value (`${header.s.toUpper()}`,
`Expr("header.o.Check()")`, `header.o.Check(header.x)` in a condition) reads the same everywhere. These readings change:

- A method that throws used to read as `null`; it is now an error naming the call, its position and the exception:
  `header.o.Boom() at position 1 threw InvalidOperationException: boom`.
- A method the value's type does not have used to read as `null`; it is now an error naming it:
  `'nosuch' is not a method of String at position 1 (in header.s.nosuch())`.
- A method call inside a comparison (`header.s.length() > 10`) used to fail to compile; it now works.
- A bare `exception.Message` used to be the text itself; it now reads the exception's message, as `${exception.Message}`
  always did (`null` when there is no exception).

A value that is `null` still gives `null` (`header.missing.toUpper()`), and text that is not an expression is still the
text itself (`hello`, `plain text`, `key=value`). What to look for: expressions, templates and conditions that call
methods of headers, properties or the body and silently relied on a `null` when the call failed. To keep a text that
looks like a call, quote it (`'header.o.Check()'`) or set it as a constant.

### A format suffix in a placeholder formats

A placeholder that ends in a colon and a standard .NET format, `${header.total.CostUsd:F4}`, used to drop the suffix
silently and write the plain value (`0.125`). It now formats the value as `format(value, 'F4')` does, with the
invariant culture (`0.1250`). The suffix is one letter and up to two digits (`F2`, `N0`, `D8`) after a path or a call.

What to look for: templates with `:F`, `:N` or `:D` before the closing brace, such as log lines and messages that show
amounts. Their text now carries the decimals the author asked for. To keep the old output, drop the suffix.

### `ExpressionSandboxViolationException.TargetType` can be null

A `ref:name` or `env:NAME` refused in a template's sandbox calls no method, so the exception's `TargetType` is `null`
for it (the type is now `Type?`) and `Written` holds the spelling. Code that catches the exception and reads
`TargetType` checks it before use; a build with nullable warnings as errors points at such a read.

### Text after a complete expression is an error

The engine used to stop where an expression was complete and drop the rest: `header.b == 3 garbage` compared
`header.b == 3`, and `${exception.type|message}` rendered empty. Now the rest fails the route when it is built,
naming it: `unexpected '|' at position 15 within the expression: the expression ends before it.` `check` and `pack`
report it as an error at the attribute, where they used to warn.

What moves:

- trailing text after an expression, in `${...}`, in an expression attribute and in a condition: remove it, or quote
  it as text (`'garbage'`);
- a file key in capitals in a template, `${FILE:NAME}`: the message says `file keys are lowercase: ${file:name}`
  (in the file components' own options, such as `moveTo`, capitals still work);
- an Apache Camel spelling the language does not know, such as `${exception.type|message}` or `${a ${b}}`: write it
  in this language (`${exception.Message}`);
- `${header.x y}` with a space in the name part: it is an expression now, and one that does not compile is the error.
  A one-word name (`${header.Content-Type}`, `${header.user.name}`) still reads the header;
- `expr(...)` of a payload template with plain text (`expr('plain text')`): quote the text, `expr("'plain text'")`.

Text with no operator in a value position is unchanged: `plain text` and `header.b 3` still read as the text itself,
and `check` warns about the second, since in a condition it is always true.

### File keys in consumer options resolve or fail

The file consumers' options `moveTo`, `preMove`, `moveFailed`, `doneFileName` and `idempotentKey` (File, FTP, SFTP)
and S3's `doneFileName` used to know only `${file:name}` and `${file:name.noext}`. Any other key stayed as written:
`moveTo=done/${file:ext}` moved files into a directory literally named `${file:ext}`. Now every key of the file
language resolves (`${file:ext}` gives `csv`, `${file:parent}`, `${file:size}`, `${file:modified}` and the rest), and
a key that is not one of them fails when the endpoint is created, listing the keys. Keys still match ignoring case in
these options.

What to look for: a directory under the polled one whose name starts with `${file:`. Rename or move its files where the
option now puts them, or fix the key the error names.

The file producers' `tempFileName` (File, FTP, SFTP) takes the same keys, read from the file being written:
`tempFileName=${file:name}.part` writes `order.xml.part` and renames it to `order.xml`. Before, every key stayed as
written, so all writes shared one temp file literally named `${file:name}.part`. A value without keys is unchanged; an
unknown key fails when the endpoint is created. What to look for: a leftover file whose name starts with `${file:` in
a target directory, left by a write that stopped halfway. Delete it.

Firebase Storage's `moveAfterRead` and `moveFailed` take the same keys, read from the object, and stay prefixes: the
object name follows them. A prefix with a `${file:...}` in it used to be written as is. What to look for: objects whose
name starts with `${file:`; move them where the prefix now puts them.

A subclass of `GenericFileConsumer` that moves files itself: override `PostProcessAsync(GenericFileInfo, string,
CancellationToken)` and `OnProcessingFailedAsync(GenericFileInfo, string, CancellationToken)` to see the size and the
time of the file. The forms with `(filePath, fileName, basePath)` still work: the consumer calls them for a subclass
that overrides them.

### Bare text that breaks off as arithmetic is an error

In an expression (`expr=`, `Expr(...)`, a condition), a text with `+`, `-`, `*` or `/` outside quotes is arithmetic.
It used to be computed from whatever pieces could be read: `1 +` gave `1`, `header.a+b` gave `2b`, and a text such as
`application/json` gave `null`, silently. Now such a text must be whole arithmetic, or it fails where the route is
built, naming the place:

- `1 +`: `the expression ends after '+' at position 3, where a value is expected`
- `header.a+b`: `'b' at position 10 is not a value: write header.b, property.b or the text 'b' in quotes`
- `header.Content-Type`: `'header.Content-Type' at position 1 reads as a subtraction: a name with a dash is written ${header.Content-Type}`

What to look for: an expression attribute or `Expr(...)` holding text that looks like a MIME type
(`application/json`), a path (`some/path/file.txt`) or a parameter list (`text/plain; charset=utf-8`). Write such a
text in quotes (`expr="'application/json'"`), or as a constant (`value="application/json"`, `.SetHeader("k",
"application/json")`). A dashed name goes inside `${...}`. Text without an arithmetic operator is unchanged:
`hello` and `key=value` still read as the text itself.

### `AstNode.Evaluate` runs the compiled expression

Code that parses an expression itself (`new Parser(new Tokenizer(text).GetAllTokens()).Parse()`) and calls
`Evaluate` on the tree now gets what the engine gives for that text: the tree is compiled once and the compiled
delegate runs. It used to be a separate evaluator, in which `++` / `--` did not write back and method calls on values
were not supported; an expression that does not compile now throws `ExpressionCompilationException` on the first
`Evaluate`.

### IBM MQ `connectionFactory` connects through the registered factory

`wmq:` endpoints with `connectionFactory=name` used to ignore the registered `IbmMqConnectionFactory`: the name only
split the connection pool, and the connection was built from the inline options on the URI or their defaults
(`localhost:1414`, `DEV.APP.SVRCONN`, `QM1`, no credentials). Now the registered factory is the connection, and it
takes priority over the inline options; a name nothing is registered under stops the route when it starts.

What to look for: `wmq:` URIs (and `IbmMqDsl` builders) with `connectionFactory=`. What to do: make the registered
factory hold the host, channel, queue manager and credentials the endpoint should use (the inline values on the same
URI no longer count), or remove `connectionFactory` and keep the inline options.

### An XSD file reads its imports

`<validateXsd file="…">` and `validator:schema.xsd` used to read the file as text, so its `xs:import` and `xs:include`
had no location: each one was skipped without a word, and a schema that used an imported type failed with "Type … is
not declared". Now the schema is read from its file and its imports and includes resolve relative to it, from under
`schemaRoot` (the schema's directory unless set). Inline content and `ValidateXsd(string)` read one schema as before.
Three things change for a file schema that loaded before:

- **An import that does not load is an error.** One that is not found, lies outside `schemaRoot` (an absolute
  `file:` path included) or points to the network (`http://www.w3.org/2001/xml.xsd`) used to be skipped when the
  schema did not use it; now the route fails to start, naming the reference. What to look for: `schemaLocation=` in
  the schema and in the files it imports. What to do: copy the referenced files into the package, point
  `schemaLocation` at them, and set `schemaRoot` when they sit above the schema's directory (`..` for UBL's
  `maindoc/`).
- **Lax wildcards are validated.** An `xs:any processContents="lax"` over a namespace whose schema is now imported
  checks the elements there against their declarations; before, no declaration was known and anything passed. What
  to look for: `processContents="lax"` in the schema. What to do: fix the documents the validator now rejects, or
  narrow the wildcard's `namespace=`.
- **Generated code keeps the file.** `redb-route-xml` writes `<validateXsd file="…">` as
  `ValidateXsdFile("…", …)` instead of `ValidateXsd(…)` with the file's text inlined, so the generated project needs
  the schema files at runtime, found through the context's resource resolver as for `xslt`. What to do: ship the
  schema directory with the project, or regenerate after moving it.

### A mail whose exchange fails is left as it is

`imap://` and `pop3://` used to apply `postProcess` to every message whose exchange returned, also when it carried an
unhandled exception or was marked rollback-only with `.RollbackAll()`: under `postProcess=Delete` a failed mail was
deleted, under the default `MarkRead` it was marked read. Now they follow Apache Camel's mail consumer. Only a
successful exchange is committed (`postProcess`, `moveTo`, the headers `redbMail.Delete`, `redbMail.MoveTo` and
`redbMail.CopyTo`); a failed one leaves the message as it is. A handled exception (`OnException(...).Handled(true)`,
a dead letter channel) is a success, as before. `peek` now does what Camel's does, and its default is `true`.

- **IMAP: a failed mail stays unseen and is fetched again.** With `fetchFilter=Unseen` (the default) it is routed
  again on every poll until its exchange succeeds. What to look for: `imap://` routes that leave failures unhandled
  and relied on `postProcess` to mark failed mails read or delete them. What to do: handle the failure; the exchange
  then commits like any success. Under the default `MarkRead` that is all: the mail is marked read and not fetched
  again. Under `postProcess=Delete` it would be deleted, so set `redbMail.Delete=false` in the handler to keep it,
  marked read. Or set `peek=false`, which marks each mail read when it is fetched, so a failed one stays read
  without a handler.
- **POP3: a failed mail stays on the server and is routed again on every poll.** POP3 has no read flag, so `peek`
  does not apply. The mails after it are still routed and deleted. What to look for: `pop3://` routes that leave
  failures unhandled. What to do: handle the failure (under `postProcess=Delete` the mail is then deleted; with
  `redbMail.Delete=false` it is kept and fetched again). To route a kept mail only once, set `idempotentRepository`
  to a persistent repository with `idempotentRepositoryRemoveOnCommit=false`: its key is confirmed when the
  exchange commits and the mail is skipped whenever it is fetched again. A failed exchange releases the key, so the
  failed mail is still routed again.
- **An explicit `peek=false` now marks mails read at fetch.** It used to have no effect. What to look for:
  `peek=false` on `imap://` URIs. What to do: remove it to keep mails unseen until their exchange is committed.

### A step on the side its component lacks fails when the route is built

Some components have one side only (Apache Camel's `producerOnly` / `consumerOnly`): bean, cache, embed, fcm,
knowledge, log, mcp, smtp, sns, stt, validator and xslt only send; imap, pop3, timer, cron and qtimer only receive.
A route that started from a send-only component failed when the context started; a step that sent to a receive-only
one failed only when the first exchange reached it, so a route with such a step in a branch never taken ran without
a word. Now the mistake fails when the route is built or loaded, in one message:
"timer has no producer side: a route cannot send to it (timer is consumer-only). Use it as a From() source."

- **XML:** the loader refuses the route with the position of the step, in both the `uri=` and the structured form,
  and `check` reports it (the core components always, the connectors with `--bin`).
- **C#:** a static target (`To`, `WireTap`, `Enrich`, `PollEnrich`, the endpoints of a load balancer or a
  scatter-gather) fails the route build, so `Start()` throws (`ThrowOnCompilationError`, the default) or the route is
  skipped and logged. A `From` on a send-only component throws at `Start()` as before.
- **Dynamic targets** (`ToD`, `RecipientList`, `RoutingSlip`, `DynamicRouter` with a computed URI) still fail on the
  exchange, with the new message.
- The exception is `NotSupportedException`, as before; its text is the new one.

What to look for: one of the send-only schemes in `<from>` or `From(...)`, or one of the receive-only schemes as a
target, in particular in a branch that is rarely or never taken; and code that matches the old exception texts. What
to do: remove the step, or use the scheme the message's hint names.

### A mail option the other side reads is refused

`imap://`, `pop3://` and `smtp://` share one set of options. An option only the other side reads used to bind and do
nothing: `subject` on an `imap://` URI, `postProcess` on an `smtp://` one. Now the endpoint refuses it when it is
created, naming the parameters and never their values: "smtp producer 'smtp://mail.example.com' sets postProcess:
only an imap or pop3 consumer reads it." A consumer option on `smtp://` is no longer validated either: `delay=-1`,
`maxMessages=-1` or `postProcess=Move` without `moveTo` used to fail an smtp endpoint with a consumer's reason, and
now the refusal names the option.

What to look for:

- `smtp://` URIs (and `Smtp.Send(...)` builders) that carry `folder`, `additionalFolders`, `delay`, `initialDelay`,
  `fetchFilter`, `maxMessages`, `postProcess`, `moveTo`, `sortBy`, `idle`, `idleTimeout`, `fetchBody`,
  `fetchAttachments`, `maxAttachmentSize`, `peek`, `idempotent`, `searchQuery`, `minAge`, `maxAge`, `subjectFilter`,
  `fromFilter`, `mapMimeHeaders` or `keepRawMessage` (and the 4.7 additions `delete`, `copyTo`,
  `idempotentRepository`, `idempotentRepositoryRemoveOnCommit`);
- `imap://` and `pop3://` URIs (and `Imap.Read(...)` / `Pop3.Read(...)` builders) that carry `from`, `to`, `cc`,
  `bcc`, `replyTo`, `subject`, `contentType`, `alternativeBody` or `attachments`.

What to do: remove the option; it had no effect on that side.

## 4.5.0

### A Redis consumer starts only when it receives

A `redis:` consumer (SUBSCRIBE, PSUBSCRIBE, XREAD, XGROUP, BLPOP, BRPOP) now subscribes, creates its consumer group
and pins its XREAD position before `Start()` returns. Before, it did that in the background after the route had
started: a message published or an entry added in between was lost, and a Redis that was down at start left a
consumer that looked started and never received anything, with an error in the log only.

What the operator sees now: when Redis does not answer at start, the route, and with it the context, fails to start
with `InvalidOperationException: Redis consumer redis:<OPERATION>:<resource> cannot start, Redis did not answer: ...`.
In a Tsak worker the context shows as failed instead of running. Check that Redis is reachable from the worker before
the context starts. Once a consumer has started, a later outage is handled as before: StackExchange.Redis reconnects
and subscribes again. Start waits up to the connect and command timeouts of the connection (5 s each by default)
before it gives up.

### An AMQP consumer starts only when the broker accepted its link

An `amqp:` consumer now waits in `Start()` until the broker has answered every receiver link it attaches, as it did
for `dynamic=true` only. Before, a link the broker refused (an address it does not serve, a routing type the address
does not have, no permission to read it) was logged by the receive loop, and the consumer looked started and never
received anything. On a MULTICAST address the subscription is the link itself, so it is now in place when `Start()`
returns.

What the operator sees now: the route, and with it the context, fails to start with
`InvalidOperationException: AMQP consumer amqp:<address> cannot start: the broker refused the receiver link
(<condition>: <description>).`, or `... the broker did not answer the receiver link within <timeout> s.` after the
endpoint's `timeout` (30 s by default). In a Tsak worker the context shows as failed instead of running. Fix the
address, the routing type or the permissions the message names.

### An MQTT publish goes to its endpoint topic

An `mqtt:` publish no longer reads `redbMqtt.topic` as its target. The connector writes that header itself, with the
topic a message was received on and the topic of every publish, so a second publish in a route went to the first
topic, and a received message republished to `mqtt://alerts/...` went back to the topic it came from. A route that set
`redbMqtt.topic` to choose where a publish goes sets `redbMqtt.overrideTopic` (`MqttHeaders.OverrideTopic`) instead;
`redbMqtt.topic` stays readable as the topic of the last receive or publish.

### An MQTT publish takes its endpoint QoS and retain flag

An `mqtt:` publish no longer reads `redbMqtt.qos` and `redbMqtt.retain`. The consumer writes them with the QoS and
retain flag of the received message, so a republished message kept the QoS it arrived with, and a retained message was
retained again on the new topic. A publish now uses the endpoint's `qos`, `qosExpression` and `retain`. A route that
set `redbMqtt.qos` or `redbMqtt.retain` to choose them sets `redbMqtt.overrideQos` (`MqttHeaders.OverrideQos`: 0, 1 or
2) or `redbMqtt.overrideRetain` (`MqttHeaders.OverrideRetain`: true or false) instead, as a number or bool from C# or
as text from markup (`<setHeader name="redbMqtt.overrideQos" value="1"/>`). Another value fails the publish. A route
that wants a republish to keep the incoming flags copies them over:
`exchange.In.Headers[MqttHeaders.OverrideRetain] = exchange.In.Headers[MqttHeaders.Retain];`. The MQTT 5 properties
(`redbMqtt.contentType`, `redbMqtt.responseTopic`, `redbMqtt.correlationData`, `redbMqtt.messageExpiryInterval`,
`redbMqtt.userProperties`) still pass through to a publish.

### XML structure limits

XML that redb.Route reads from outside goes through `SafeXml`, and `SafeXml` now checks the structure of a document
while it reads it. A document over a limit is refused with `XmlLimitException`, an `XmlException`, before it is built.
The limits every caller gets:

| Limit | Default |
|---|---|
| Nesting depth, the root counting as 1 | 256 |
| Attributes on one element, namespace declarations included | 1024 |
| Namespace declarations on one element | 256 |

Size, element count and namespace declarations in the whole document are not bounded by default. These limits apply
to XPath over a message body, the SOAP receiver and sender, Route-XML markup and templates. A document deeper than
256 levels used to be read and is refused now; search for such documents (generated XML, recursive structures)
before upgrading. Code that reads XML through `SafeXml` itself can pass its own `XmlLimits`:
`SafeXml.Parse(xml, XmlLimits.Default with { MaxDepth = 1024 })`.

**SOAP receiver.** A request is checked once against these limits before anything parses it, and again after the
body is decrypted; a request over a limit is a `Client` / `Sender` fault naming the limit. The request body is now
bounded by `maxRequestBodySize`, 100 MB by default; it was not bounded before. `maxRequestBodySize=0` restores no
bound.

**AS4 receiver and sender.** The envelope has tighter limits, the shape of an eDelivery envelope: depth 32,
32 attributes and 16 namespace declarations on an element, 4096 namespace declarations in the envelope, and
`maxEnvelopeCharacters` as before. A received envelope over them is a SOAP `Sender` fault; a partner's answer over
them is an invalid receipt. A message must carry exactly one `xenc:EncryptedKey`, and each payload is decrypted once;
anything else is `EBMS:0102`. Domibus, Holodeck B2B and this connector send messages well within all of this.

### AS4 refusals in their own counter

An AS4 receiver used to count every message it refused (a malformed envelope, a failed signature or decryption, a
message the agreement does not allow) in `Errors`, so a partner with a broken configuration turned the route red. These
are now counted in `IEndpointStatistics.Refused`, with the reason of the last one in `LastRefusalMessage`, and no longer
in `Errors`, the last error or health. `EBMS:0004` (a failure on our side) and route failures stay errors. An alert
that watches `Errors` of an AS4 receive endpoint for refused messages should watch `Refused` instead; in a route,
`stats('current', 'refused')`.

### `attachments` in an expression

A bare `attachments` in an expression (`${attachments}`, `attachments.size`) now reads the attachments of the In
message, as `${attachments}` does in Camel Simple. It used to fall back to an exchange property of that name, for
example the list an `smtp:` route put in `exchange.Properties["Attachments"]`. An expression that meant the property
writes `property.attachments`.

### Mail and SOAP attachments are message attachments

`smtp:`, `imap:`, `pop3:` and `soap:` now work with the message attachments, `exchange.In.Attachments` (see the
README, "Attachments"). The old conventions are removed with no transition period.

**Nothing warns you.** `smtp:` no longer reads the exchange property `Attachments`. That property is an ordinary
property now, so a route still setting it gets no error, and its mail goes out **without the files**. Search the code
for `Properties["Attachments"]`, `MailAttachment` set on an exchange property, `SoapHeaders.Attachments`,
`redbSoap.attachments`, `SoapHeaders.HasAttachments`, `redbSoap.hasAttachments` and `SoapAttachment`, and migrate
each one as shown below.

**Mail: files from code, before `smtp:`.**

```csharp
// before
exchange.Properties["Attachments"] = new List<MailAttachment>
{
    new("orders.zip", zip, "application/zip")
};

// after
exchange.In.Attachments.Add(new Attachment
{
    Name = "orders.zip", FileName = "orders.zip", ContentType = "application/zip",
    Content = AttachmentContent.FromBytes(zip)
});
```

**Mail: file paths, before `smtp:`.**

```csharp
// before
exchange.Properties["Attachments"] = new List<string> { @"C:\out\report.pdf" };

// after: the file is read when the mail is sent
exchange.In.Attachments.Add(new Attachment
{
    Name = "report.pdf", FileName = "report.pdf", ContentType = "application/pdf",
    Content = AttachmentContent.FromFile(@"C:\out\report.pdf")
});
// or a route step: .AddAttachmentFromFile(@"C:\out\report.pdf", contentType: "application/pdf")
```

The `attachments=` endpoint option (comma separated paths) stays as it was.

**Mail: received mail.** `imap:` and `pop3:` also put the attachments of a mail on the message attachments.
`MailMessageBody`, its `Attachments` and the `redbMail.*` attachment headers stay as they were, so routes reading
them need no change. Two behaviors are new: a received mail sent on to `smtp:` carries its attachments (before, they
were dropped), and its text (`MailMessageBody` converts to its text; before, the type name was sent as the text).

**SOAP: sending attachments (producer, or a consumer reply).**

```csharp
// before
msg.Headers[SoapHeaders.Attachments] =
    new List<SoapAttachment> { new("doc-1", "application/pdf", pdfBytes) };

// after
msg.Attachments.Add(new Attachment
{
    Name = "doc-1", ContentId = "doc-1", ContentType = "application/pdf",
    Content = AttachmentContent.FromBytes(pdfBytes)
});
```

A consumer route that replies with attachments replaces the request ones: `e.In.Attachments.Clear()`, then `Add`. As
before, a reply that still holds exactly the request attachments is sent without them.

**SOAP: reading attachments.**

```csharp
// before
var parts = e.In.GetHeader<IReadOnlyList<SoapAttachment>>(SoapHeaders.Attachments);
byte[] bytes = parts![0].Content;
bool any = e.In.GetHeader<bool>(SoapHeaders.HasAttachments);

// after
var part = e.In.Attachments.Find("doc-1");          // named by the Content-ID
byte[] bytes = part!.Content.ReadAllBytes();
bool any = e.In.Attachments.Count > 0;              // in an expression: attachments.size > 0
```

`SoapAttachment.ContentId`, `ContentType` and `Content` map to `Attachment.ContentId` (also its `Name`),
`ContentType` and `Content.ReadAllBytes()`. A producer reply's attachments are on `exchange.Out.Attachments`, and on
`In` after the pipeline moves on.

## 4.4

### Unknown URI options stop the context

`direct-vm:` now reads its URI parameters, and it reads them strictly, like every connector that is not lenient.
Before 4.4 it ignored every parameter, so a URI such as `direct-vm://orders?inOut=true` started without a word. In
4.4 the endpoint refuses such a parameter, and the context fails to start:

```
System.ArgumentException: 'inOut' is not an option of the DirectVm endpoint.
```

`direct-vm:` has one option, `headerFilterStrategy=#name`, and it applies to the caller side only. Drop anything
else from its URIs. A direct-vm call always returns the reply to the caller, so `inOut=true` was never needed.
Options taken from Apache Camel's direct-vm (`block`, `timeout`, `failIfNoConsumers`, `propagateProperties`) are
refused the same way.

This is the rule that every strict connector has followed since 4.2.0. An application that comes from a version
before 4.2.0 meets it on every connector except `http:`, `sql:` and `bean:`, which take extra parameters on purpose:

- **An unknown name** is refused with `'<name>' is not an option of the <Connector> endpoint.`, followed by the
  nearest option (`Did you mean 'period'?`) or by the connector's note on what replaced it.
- **A value that does not convert** is refused with `'<option>=<value>' is not a valid value: <option> takes ...`.
  This covers `recursive=yes`, a misspelt enum value, `${...}` in an option that takes no expressions, and `1,5` in
  place of `1.5`.

Every refused parameter used to be dropped, so the option ran with its default instead of the value written.

To find the offenders before upgrading:

- search route code and markup for each parameter the startup error names, for example `inOut` in `direct-vm:` URIs;
- for Route-XML, run `check --bin` (or `pack --bin`): it reports an unknown name on a strict connector with its
  position, in the engine's own words;
- otherwise start the application once on the new version; the message names every unknown parameter of the
  endpoint at once.

Renamed and removed names that people meet most often. The error text says what replaced each one:

| Connector | Old name | Now |
|---|---|---|
| `direct-vm:` | `inOut` | drop it |
| `rabbitmq:` | `autoAck` | `ackMode=auto` / `ackMode=manual` |
| `rabbitmq:` | `sslCertPassphrase` | `sslCertPassword` |
| `kafka:` | `enableAutoCommit` | `ackMode` |
| `amqp:` | `autoAccept` | `ackMode` |
| `asb:` | `receiveMode` | `ackMode` (`manual` = PeekLock, `auto` = ReceiveAndDelete) |
| `sqs:` | `deleteAfterRead` | `ackMode` |
| `redis:` | `streamNoAck`, `streamAutoAck` | `ackMode` |
| `imap:`, `pop3:`, `smtp:`, `ldap:` | `skipCertificateValidation` | `trustAllCertificates` |
| `cache:` | `cacheHeaders` | `headers=<name>,<name>` |
| `as2:` | `certPassword` | the `As2ConnectionFactory` named by `connectionFactory=` |
| `http:` | `bridgeHeaders`, `copyResponseHeaders` | see [HTTP header options](#http-header-options) |

### HTTP header options

`bridgeHeaders` and `copyResponseHeaders` are Apache Camel's `skipRequestHeaders` and `skipResponseHeaders`, with the
meaning reversed:

| Before | Now |
|---|---|
| `bridgeHeaders=false` | `skipRequestHeaders=true` |
| `copyResponseHeaders=false` | `skipResponseHeaders=true` |
| `NoBridgeHeaders()` | `SkipRequestHeaders()` |
| `NoCopyResponseHeaders()` | `SkipResponseHeaders()` |

The defaults keep today's behaviour and need no option. **An old name fails the endpoint, it is not ignored**: the
HTTP options accept parameters they have no property for (`param.*`), so an unknown name would be dropped without a
word and `bridgeHeaders=false` would quietly send every header. Search URIs and markup for `bridgeHeaders` and
`copyResponseHeaders` before upgrading.

Which headers HTTP carries is now its default header filter strategy, `HttpHeaderFilterStrategy`; an endpoint can
name another with `headerFilterStrategy=#name`. The protocol fields (`HttpHeaders.ProtocolHeaders`: hop-by-hop,
`Host`, `Content-*`) are never carried, whatever the strategy.

### FluentValidation 12

`redb.Route.Validation.Adapters` brings FluentValidation 12 (was 11) into the project that uses it. A validator
written against the API that 12 removed no longer compiles there:

- `Transform` / `TransformForEach`;
- `CascadeMode.StopOnFirstFailure` and the `CascadeMode` properties: use `ClassLevelCascadeMode` /
  `RuleLevelCascadeMode`;
- `InjectValidator`: use `SetValidator` with constructor injection.

FluentValidation 12 needs .NET 8 or later.

### FTPS data channel

FluentFTP 55.1 (was 54.1) checks the certificate of every FTPS data connection. With `validateCertificate=true`, the
default, a server that presents on the data channel a certificate other than the control channel's, and untrusted,
fails the transfer with an `AuthenticationException`. Make the server present a trusted certificate on both
channels, or set `validateCertificate=false` to keep accepting it.

### Elasticsearch delete

`elasticsearch://Delete` of a document id the index does not hold used to fail the exchange; it now completes, and
`redbEs.Result` is `NotFound` (`Deleted` when the document was there). A route that relied on the error to catch a
wrong id checks that header instead.

### FCM topics

FirebaseAdmin 3.7 (was 3.5) manages topics through the FCM v1 API instead of the Instance ID batch API.
`SubscribeToTopic` and `UnsubscribeFromTopic` send one request per registration token, up to 100 at a time, where they
sent one request for up to 1000 tokens. What a route sees stays the same: `redbFcm.SuccessCount` and
`redbFcm.FailureCount` per call, and a token already subscribed counts as a success. Before upgrading:

- the service account needs the FCM v1 permission to manage registrations, not the Instance ID one;
- a large token list costs as many requests as it has tokens, which counts against the project's FCM quota;
- a firewall or proxy that let through `iid.googleapis.com` has to let through `fcm.googleapis.com` for these calls.

### SQL on PostgreSQL: Npgsql 10

- **The failed item of a batch.** Npgsql no longer names the failed command of a batch, so that queries and their
  parameters do not leak into logs. A failed batch write reports in `redbSql.batchFailedIndex` the first item of the
  failed chunk, not the item itself. For the exact item add `Include Failed Batched Command=true` to the connection
  string; the component does not set it on its own.
- **`date` and `time` columns** are read as `DateOnly` and `TimeOnly` where the target type is not given: a row read
  into a dictionary, a scalar read as `object`. Typed targets (`DateTime`, `TimeSpan` properties and parameters) are
  converted as before.
- **TLS with a root certificate.** With `Root Certificate` in the connection string only that certificate validates
  the server's chain; the system certificate store is no longer consulted as well.

### Quartz 3.22

Quartz moves from 3.16 to 3.22. Corrections that change when or whether a job fires:

- `[DisallowConcurrentExecution]` (stateful cron routes) holds a job to one run at a time across the whole cluster,
  where it used to be held per node only;
- day-of-month `L`, `LW` and `W` expressions that used to misfire now fire as written;
- fire times in the repeated hour of a DST fall-back, day-based calendars near DST changes and
  `DailyTimeIntervalTrigger` past `EndTimeUtc` are corrected;
- pausing or resuming groups by prefix covers every matching group; `ResumeAll` resumes empty paused groups.

A clustered job store (AdoJobStore) warns at start while three sets of columns are missing: `MISFIRE_ORIG_FIRE_TIME`
(3.17), `EXECUTION_GROUP` (3.18), `PREFERRED_NODE` and `PREFERRED_NODE_AUTO` (3.19). Without them Quartz runs as 3.16
did. An application that owns its Quartz tables adds them with the Quartz.NET 3.x migrations
(`database/migrations/3.17` to `3.19`); a redb.Tsak worker adds them itself at start.

### RabbitMQ empty queue name

A consumer with an empty queue name and `declare=false` now fails at start: only the topology declaration creates and
binds a temporary queue, so such a consumer read an unbound queue and got nothing. Name the queue to read, or set
`declare=true`. In the fluent API this endpoint is `Rabbit.Exchange(name, type).Declare()`: `Rabbit.Queue` takes no
empty name.

### SignalR on a shared listener

A hub is served from a service container of its own and joins or leaves a running listener, so the listener API
changes for a component that brings its own endpoints, and the hub configuration runs once per hub.

`RegisterServerConfigurator` and `UnregisterServerConfigurator` are removed. `RegisterEndpoints` maps the endpoints
with the component's own container, before or after the listener started, and returns a registration to dispose:

```csharp
// was: services went into the listener's container, endpoints were mapped when it was built, not after
var handle = manager.RegisterServerConfigurator(host, port,
    services: s => s.AddSingleton<StatusSource>(),
    endpoints: app => app.MapGet("/status", (StatusSource s) => s.Current()));
manager.UnregisterServerConfigurator(handle);

// now: the endpoints come with their own container and may join a running listener
var services = new ServiceCollection().AddRouting().AddSingleton<StatusSource>().BuildServiceProvider();
var registration = manager.RegisterEndpoints(host, port, services,
    endpoints => endpoints.MapGet("/status", (StatusSource s) => s.Current()));
registration.Dispose();
await services.DisposeAsync();
```

A middleware the configurator added with `app.Use(...)` has no place in a registration: put the check on the endpoints
as a convention or an endpoint filter.

Every listener accepts WebSocket upgrades now. `EnableWebSockets` is no longer needed to get them; it sets the
keep-alive of a listener that has not started, and on a running listener it changes nothing instead of throwing.

`ConfigureHubServices` and `SignalRComponent.HubServicesConfigurator` take the hub they configure:

```csharp
// was
o.ConfigureHubServices(s => s.AddSignalR().AddStackExchangeRedis("redis:6379"));

// now: the backplane package keys the channels on the hub path
o.UseRedisBackplane("redis:6379");                 // redb.Route.SignalR.StackExchangeRedis

// or, by hand, with the prefix the host is now responsible for
o.ConfigureHubServices((s, hub) => s.AddSignalR().AddStackExchangeRedis("redis:6379",
    redis => redis.Configuration.ChannelPrefix = RedisChannel.Literal(hub.Path)));
```

A backplane added without a prefix that includes the hub path lets the hubs of one listener see each other's
broadcasts through Redis, as every hub of the connector is the same hub class. Hosts with more than one hub path on
one Redis move to `UseRedisBackplane` or add the prefix.

The hub's container is its own: `Context.GetHttpContext().RequestServices` of a hub request resolves from it, and
services of the listener are not there. `messagePack=true` and `transport=...` apply to their own hub; a hub that got
MessagePack because another hub of the same port asked for it now needs `messagePack=true` itself.
