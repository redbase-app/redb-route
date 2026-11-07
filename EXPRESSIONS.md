# The redb.Route expression language

What you can write in a condition, a value or a `${...}` template, and what you get. Every table marked for running
is evaluated by `ExpressionsGuideExamplesTests` on the exchange of section 0: the results below are what the engine
gives, not what it is meant to give.

---

## 0. The exchange behind the examples

```
header.a = 42          header.b = 3            header.neg = -7        header.zero = 0      header.price = 2.5
header.s = "Hello World"                        header.flag = true     header.off = false
header.Content-Type = "text/xml"                header.empty = ""      header.O'Brien = "obrien"
header.user = { Name = "Ann", Age = 30, Active = true, Manager = { Name = "Bob", Age = 51 } }
header.list = [5, 1, 9]                         header.when = 2026-08-28 10:30 UTC
header.redbFile.Name = "orders/a.xml"          header.redbFile.NameOnly = "a"     (as a file consumer sets them)
property.count = 7     property.items = ["p", "q", "r"]     property.nums = [1, 2, 3, 4]
property.cfg = { enabled = true, limit = 5 }    property.counter = 10
body = "payload"       exception.Message = "boom"
registry: "pricing" and "pricing-rules", both a bean with Gross(int qty, double price), IsGold(string name),
          Badge(string name, int points), Touch(string), a Rate property, and CheckAsync-style methods where noted;
          "limits.max", the number 100
```

---

## 1. Three positions

The same text means different things depending on where it is written. This is the language's main rule.

| Position | In the DSL | What happens to the text |
|---|---|---|
| **String** | `.SetHeader("k", "a>b")`, `Rabbit.Queue("q").Password("p@ss(1)")` | nothing: a literal, whatever it contains |
| **Template** | `.SetHeader("k", Expr("Hi, ${header.s}!"))`, `.ToD("kafka://${header.region}")`, `.Log("id=${header.a}")` | text as written; each `${...}` is evaluated |
| **Expression** | `.SetBody(Expr("header.a + 1"))`, `.Transform(Expr("upper(body)"))` | the whole text is an expression |
| **Condition** | `.Filter("header.a > 10")`, `.When("header.flag")`, `.LoopWhile("property.i < 3")`, `.Validate("header.a > 0")` | an expression whose result is read as yes or no |

Inside `${...}` and in an expression the language is the same: `${...}` only marks an expression inside text.
Whitespace around operators carries no meaning: `header.a>10` and `header.a > 10` are the same.

In route markup an attribute typed as an expression (`expr=`, `correlation=`, `while=`, ...) is an expression, and a
`${...}` in it makes it a template; `value=` is a literal.

**Bare text in an expression.** A text without an arithmetic operator (`+ - * /`) outside quotes is a lookup when it
names one (`header.a`, `body`) and otherwise the text itself, as in Apache Camel: `hello`, `plain text`, `key=value`.
A text with one is arithmetic and must be whole: it parses to its end, and each bare word in it is a value (a root
such as `header.a`, `property.x`, `body`, a number, or quoted text). Otherwise it is an error naming the place:
`1 +` ends where a value is expected, `header.a+b` has `'b' at position 10 is not a value`, and `application/json`
has `'application' at position 1 is not a value`. Write text in quotes: `'application/json'`.

---

## 2. Reading the exchange

<!-- run: value -->
| Expression | Result |
|---|---|
| `header.a` | `42` |
| `header.s` | `Hello World` |
| `header.missing` | `null` |
| `property.count` | `7` |
| `body` | `payload` |
| `header.user.Name` | `Ann` |
| `header.user.Manager.Name` | `Bob` |
| `header.user.Age` | `30` |
| `header.list.Count` | `3` |
| `header.s.Length` | `11` |
| `property.cfg.enabled` | `True` |
| `property.cfg.limit` | `5` |
| `property.items.Count` | `3` |
| `header.list[1]` | `1` |
| `property.items[0]` | `p` |

`attachments.size` is the number of attachments of the In message and `attachments[0].fileName` the file name of the
first (`name`, `contentType`, `contentId`, `length` likewise).

An object reads the same wherever it lies: in a header, in a property or as the body. Members are found by name,
case-insensitive, public fields included; a dictionary answers `.key` and `['key']`. Member access is null-safe by
default: a member of a missing value is `null` (there is no separate `?.` operator to write).

**Names with a dot or a dash.** The literal name comes first: `header.dotted.key` is the header named `dotted.key` if
there is one, else the member `key` of the header `dotted`. In a template the same rule keeps a dashed name readable:
`${header.Content-Type}` gives `text/xml`. In a bare expression `header.Content-Type` would be a subtraction, so it is
an error that says so: `'header.Content-Type' at position 1 reads as a subtraction: a name with a dash is written
${header.Content-Type}`.

---

## 3. Arithmetic

<!-- run: value -->
| Expression | Result |
|---|---|
| `header.a + header.b` | `45` |
| `header.a - header.b` | `39` |
| `header.a * 2` | `84` |
| `header.a / 4` | `10.5` |
| `(header.a + header.b) * 2` | `90` |
| `header.a + 1.5` | `43.5` |
| `-header.a` | `-42` |
| `header.price * 2` | `5` |
| `header.a % 5` | `2` |
| `header.a % header.zero` | `null` |

Precedence is the usual one and parentheses work. Increments **change the exchange**, for headers and properties,
prefix and postfix, in an expression and inside `${...}`. A missing value starts at 0; a value that is there but
not a number is an error naming it (`header.x++: the value 'abc' (String) is not a number.`):

<!-- run: value -->
| Expression | Returns | Afterwards |
|---|---|---|
| `header.a++` | `42` | `header.a = 43` |
| `++header.a` | `43` | `header.a = 43` |
| `header.a--` | `42` | `header.a = 41` |
| `--header.a` | `41` | `header.a = 41` |
| `property.counter++` | `10` | `property.counter = 11` |
| `++property.counter` | `11` | `property.counter = 11` |

---

## 4. Comparison and logic

<!-- run: condition -->
| Condition | Result |
|---|---|
| `header.a > 10` | `True` |
| `header.a>10` | `True` |
| `header.a == 42` | `True` |
| `header.s == 'Hello World'` | `True` |
| `header.s != 'x'` | `True` |
| `header.missing == null` | `True` |
| `header.a >= 42 AND header.b < 5` | `True` |
| `header.a > 100 OR header.b < 5` | `True` |
| `header.a > 10 XOR header.b > 10` | `True` |
| `NOT header.flag` | `False` |
| `!header.off` | `True` |
| `header.a > 10 && header.b < 5` | `True` |
| `header.s == 'a > b'` | `False` |
| `header.flag OR bean:nobody.Check()` | `True` |
| `header.off AND bean:nobody.Check()` | `False` |
| `header.s =~ 'hello world'` | `True` |
| `header.s == 'hello world'` | `False` |
| `header.a =~ '42'` | `True` |
| `header.a range '1..42'` | `True` |
| `header.price range '2.5..3'` | `True` |
| `header.a not range '1..10'` | `True` |
| `header.missing range '1..10'` | `False` |

Operators: `== != =~ > < >= <=`, `range` and `not range`, `in` and `not in` (section 7), `AND OR XOR NOT` (any case),
`&& || !`. String literals take single or double quotes; an operator inside quotes is text. Equality is not
truthiness: `42 == 'x'` is false.

- **`==` on two strings is exact**, case included. **`=~`** is Apache Camel's equals-ignoring-case: two strings compare
  ignoring case (ordinal, no culture); any other pair compares as `==`. There is no negated form: write
  `!(header.s =~ 'x')`.
- **Numbers of different types** are equal when their values are: `3 == 3.0` and `3 == '3'` are true, `3 == 5.0` is
  false.
- **`range 'from..to'`** is true for a number within both ends, inclusive, as in Apache Camel; `not range` is its
  opposite. The right side is a quoted literal of two numbers, decimals allowed, read when the expression is compiled:
  a malformed one (`'1-10'`) or one whose start is above its end (`'10..1'`) is an error at load and in `check` /
  `pack`. A missing value is in no range. A text that is not a number is an error naming it:
  `header.s range '1..10': the value 'Hello World' (String) is not a number.` A numeric text (`'12'`) counts.
- **`range` is an operator only after an operand.** Elsewhere it is a name: `header.range` reads the header called
  `range`.

`AND` and `OR` short-circuit: the right side runs only when the left one does not decide, so in the last two rows
the call to a bean that does not exist never happens. `XOR` needs both sides and evaluates both.

---

## 5. Ternary and `??`

<!-- run: value -->
| Expression | Result |
|---|---|
| `header.a > 10 ? 'big' : 'small'` | `big` |
| `header.flag ? header.a : header.b` | `42` |
| `header.missing ?? 'default'` | `default` |
| `header.a ?? 0` | `42` |
| `header.a ?? bean:nobody.Check()` | `42` |

`??` evaluates its right side only when the left one is `null`: the last row never calls the bean that does not exist.

---

## 6. Strings

A function `f(x, ...)` and a method `x.f(...)` both work.

<!-- run: value -->
| Expression | Result |
|---|---|
| `upper(header.s)` | `HELLO WORLD` |
| `lower(header.s)` | `hello world` |
| `trim('  x  ')` | `x` |
| `length(header.s)` | `11` |
| `concat(header.s, '!')` | `Hello World!` |
| `substring(header.s, 0, 5)` | `Hello` |
| `substring(header.s, 6)` | `World` |
| `replace(header.s, 'World', 'Route')` | `Hello Route` |
| `contains(header.s, 'World')` | `True` |
| `startswith(header.s, 'Hello')` | `True` |
| `endswith(header.s, 'World')` | `True` |
| `header.s.toUpper()` | `HELLO WORLD` |
| `header.s.substring(0, 5)` | `Hello` |
| `header.s.replace('o', '0')` | `Hell0 W0rld` |
| `header.s.indexOf('W')` | `6` |

A method not in the language's own list is looked up on the CLR object, so `header.list.Count` and
`header.user.Manager.Name` need no special support.

---

## 7. Numbers, collections, dates

<!-- run: value -->
| Expression | Result |
|---|---|
| `count(property.items)` | `3` |
| `sum(property.nums)` | `10` |
| `avg(property.nums)` | `2.5` |
| `abs(header.neg)` | `7` |
| `round(header.price)` | `2` |
| `round(2.567, 2)` | `2.57` |
| `min(header.a, header.b)` | `3` |
| `min(property.nums)` | `1` |
| `max(header.list)` | `9` |
| `dateformat(header.when, 'yyyy-MM-dd')` | `2026-08-28` |
| `dateformat(header.when, 'HH:mm')` | `10:30` |
| `dateformat(dateadd(header.when, 1, 'day'), 'yyyy-MM-dd')` | `2026-08-29` |
| `datediff('2026-01-03', '2026-01-01', 'days')` | `2` |

A date written bare is arithmetic (`2026-08-28` is `1990`); quote it (`'2026-08-28'`) or read it from a header.
`round` rounds half to even, as .NET does (`2.5` gives `2`). `now()` is the current time and `uuid()` a new GUID
(`uuid('N')` compact); both change on every evaluation. `stats(target, metric)` reads an endpoint's measurements
(see [METRICS.md](METRICS.md)) and `messageHistory('compact')` the exchange's trail. `attachmentContent('name')`,
`attachmentContentAsText`, `attachmentContentType` and `attachmentHeader` read an attachment of the In message.
Expressions are synchronous, so these functions read a whole attachment; they are meant for small parts.

---

## 8. JSON and XML in the body

<!-- run: template body=json -->
| Template | Result |
|---|---|
| `${jpath($.order.id)}` | `7` |
| `id=${jpath($.order.id)} total=${jpath($.order.total)}` | `id=7 total=150.5` |
| `${jsonpath:$.order.id}` | `7` |

<!-- run: template body=xml -->
| Template | Result |
|---|---|
| `${xpath(/order/id)}` | `7` |
| `${xpath:/order/id}` | `7` |

Apache Camel's spellings `${jsonpath:path}` and `${xpath:path}` mean `jpath('path')` and `xpath('path')`. The spelling
takes the whole inside of its `${...}`: the path runs to the closing brace, and text may stand around the placeholder
(`id=${jsonpath:$.order.id}`).

Inside `${...}` a path may be written bare: `${jpath($.order.id)}`. In a bare expression and in a condition the path
is **quoted**: `jpath('$.order.id')`, `xpath('/order/id')`; unquoted, `$` and `/` read as operators and the expression
does not compile (it fails when the route is built, never silently). `jpath` follows RFC 9535 (JsonPath.Net):
`.name`, `['name']`, `[0]`, slices, `[*]`, `..`, filters such as `[?(@.price < 10)]` and the standard functions.

---

## 9. Templates `${...}`

<!-- run: template -->
| Template | Result |
|---|---|
| `Hello, ${header.s}!` | `Hello, Hello World!` |
| `${header.user.Name} is ${header.user.Age}` | `Ann is 30` |
| `a=${header.a}, b=${header.b}, sum=${header.a + header.b}` | `a=42, b=3, sum=45` |
| `${header.a + 1}` | `43` |
| `${header.a > 10}` | `True` |
| `${property.cfg.limit > 3 ? 'over' : 'under'}` | `over` |
| `${count(property.items)} items` | `3 items` |
| `${header.Content-Type}` | `text/xml` |
| `${header.price}` | `2.5` |
| `${exception.Message}` | `boom` |
| `[${header.missing}]` | `[]` |

**A whole placeholder keeps its type.** `SetHeader("n", Expr("${header.a}"))` stores the `int` 42, not the text "42";
`${header.a > 10}` stores a `bool`. A template with text around its placeholders is always a string. Numbers and
dates render the same on every machine: `2.5`, and dates in ISO 8601.

A placeholder that does not parse (`${<xml>}`) fails when the route is built, not on every message.

### Braces and quoting

A placeholder starts at `${` and ends at the `}` that balances it. Everything else in a template is text.

<!-- run: template -->
| Template | Result |
|---|---|
| `${concat('a', '}', 'b')}` | `a}b` |
| `{${header.a}}` | `{42}` |
| `${}` | `${}` |
| `${header.O'Brien}` | `obrien` |
| `echo \${HOME} for ${header.s}` | `echo ${HOME} for Hello World` |
| `text ${body` | `error: unclosed '${' at position 6 of "text ${body": the placeholder has no closing '}'.` |

- A `}` inside a quoted literal belongs to the literal.
- A plain `{` does not open anything; the outer braces of `{${header.a}}` stay text.
- `${}` is not a placeholder; it stays as written.
- A placeholder may hold another one only as an argument of a `bean:` call (section 11); elsewhere a `${` inside an
  expression is an error naming its position. The general form `${header.a + ${header.b}}` is not supported.
- A quote that never ends is an ordinary character: `${header.O'Brien}` reads the header `O'Brien`.
- A `${` that nothing closes is an error where the template is compiled, at load for a route file, and `check` /
  `pack` report it at the attribute.
- A literal `${` is written `\${`: it opens nothing and renders as `${`. A backslash meant as text right before a
  placeholder (`\\${header.x}`) has no spelling.

### A format suffix `${value:F2}`

<!-- run: template -->
| Template | Result |
|---|---|
| `${header.price:F2}` | `2.50` |
| `${header.a:D5}` | `00042` |
| `${header.price * 4:F1}` | `10.0` |

A placeholder that ends in a colon and a standard .NET format (one letter and up to two digits: `F2`, `N0`, `D8`) is
`format(value, 'F2')` with the invariant culture. The value before the colon is a path or a call (`header.price`,
`round(x)`), so a namespace written against its name (`env:X`) is never a format, and a ternary's colon is never one.
A custom format (`yyyy-MM-dd`) is written with `format(...)` or `dateformat(...)`.

### File names `${file:name}`

<!-- run: template -->
| Template | Result |
|---|---|
| `${file:name}` | `orders/a.xml` |
| `archive/${file:name.noext}.done` | `archive/a.done` |

Apache Camel's file language reads the headers a file consumer sets on the message. A `${file:key}` takes the whole
inside of its `${...}`, the key running to the closing brace, and text may stand around it, as in
`archive/${file:name.noext}.done`:

| Key | Header |
|---|---|
| `name`, `onlyname` | `redbFile.Name` |
| `name.noext`, `onlyname.noext` | `redbFile.NameOnly` |
| `ext`, `name.ext` | `redbFile.Extension`, without the dot (`xml`) |
| `parent` | `redbFile.Parent` |
| `path`, `absolute.path` | `redbFile.AbsolutePath` |
| `relative.path` | `redbFile.RelativePath` |
| `length`, `size` | `redbFile.Length` |
| `modified` | `redbFile.LastModified` |

A message that did not come from a file has no such headers, and the key is empty. A key not in the table is an error
when the route is built, listing the keys.

The file consumers' own options (`moveTo`, `preMove`, `moveFailed`, `doneFileName`, `idempotentKey`, and S3's
`doneFileName`) take the same keys and give the same values, read from the file being handled; the file producers'
`tempFileName` reads the file being written (`${file:name}.part`). There a key matches
ignoring case (`${FILE:NAME}`), as these options always did; in a template it matches exactly, as in Camel. An unknown
key in an option fails when the endpoint is created.

### Configuration values `{{key}}`

`{{key}}` and `{{key:default}}` are not part of the expression language: they are configuration placeholders, resolved
before the route runs in endpoint URIs, in `enabled=` of a route, in bean values and in `value=` of `<setHeader>`,
`<setProperty>` and `<setBody>`. A key is looked up in this order:

1. `IConfiguration` of the host, with its own sources in the host's order (environment, `appsettings.json`, secrets);
2. the context properties (in Tsak, filled from the context's configuration section);
3. the OS environment, only when the context has no `IConfiguration`.

With an `IConfiguration` the host decides whether the environment takes part. A key with no value and no default is an
error naming the key. Inside an expression, read a configured value through a header set from `{{key}}`.

---

## 10. Conditions and truthiness

A condition is any expression; its result is read by **one rule**:

| Value | Reads as |
|---|---|
| `bool` | itself |
| number | not zero |
| string | the words `true/false`, `1/0`, `yes/no`, `y/n`, `on/off`; any other text: true when not empty |
| `null` | false |
| any other object | true |

<!-- run: condition -->
| Condition | Result |
|---|---|
| `header.flag` | `True` |
| `header.off` | `False` |
| `header.missing` | `False` |
| `header.empty` | `False` |
| `property.count` | `True` |
| `'no'` | `False` |
| `'anything'` | `True` |
| `header.user.Age > 18 AND header.user.Active` | `True` |
| `count(property.items) >= 3` | `True` |
| `${header.flag}` | `True` |
| `logical(header.a)` | `True` |

The boolean words are English only, so the author's locale never changes what a route means. A condition that does
not compile is a build error: `.Filter("header.a >")` throws `ExpressionCompilationException` at `Start()`.

**Write a comparison bare.** `${header.kind} == 'order'` renders to text such as `refund == 'order'`, which is
non-empty and therefore true. Write `header.kind == 'order'`, or the whole comparison inside the braces. The package
gate warns about a condition that compares outside its placeholders.

---

## 11. Registry objects: `bean:` and `ref:`

`bean:name.Method(args)` calls a public method of an object in the route context's registry (`AddToRegistry`, or a
`<bean name type>` declaration), in any position of the language: a condition, a value, a template.

<!-- run: value -->
| Expression | Result |
|---|---|
| `bean:pricing.Gross(header.b, header.price)` | `7.5` |
| `${bean:pricing.Gross(${header.b}, ${header.price})}` | `7.5` |
| `bean:pricing-rules.Badge(header.user.Name, 7)` | `Ann:7` |
| `bean:pricing.Gross(header.b, 2) - 1` | `5` |
| `bean:pricing.Touch('x')` | `null` |

<!-- run: condition -->
| Condition | Result |
|---|---|
| `bean:pricing.IsGold(header.user.Name)` | `True` |
| `header.flag && bean:pricing.IsGold('Bob')` | `False` |

<!-- run: template -->
| Template | Result |
|---|---|
| `total ${bean:pricing.Gross(header.b, 2)} EUR` | `total 6 EUR` |

- **The name** is the registry name without `#`. It may contain dashes (`pricing-rules`).
- **The method** is required: `bean:pricing` alone is an error. `bean:pricing.Method` without parentheses calls it with
  no arguments. A dash after a method without parentheses is a minus: `bean:svc.Count-1` calls `Count`, then
  subtracts one.
- **Arguments** are expressions of the language. Inside a `${bean:...}` placeholder, `${...}` may wrap an argument and
  means the same as writing it bare. In a bare expression write the arguments bare: a `${` there makes the whole text
  a template (section 1), so `bean:pricing.Gross(${header.b}, 2)` renders as text instead of calling.
- **Binding** follows the `bean:` URI rules: the method by name (any case) and number of arguments; a parameter of type
  `IExchange`, `IMessage` or `CancellationToken` is filled in and does not count; each value converts to its
  parameter's type.
- **The result** keeps its type; a `void` method gives `null`, which reads as false or empty, as in Apache Camel.
- **Synchronous only**, as in Apache Camel's Simple language: a method returning `Task`, `Task<T>`, `ValueTask` or
  `ValueTask<T>` is refused, with the advice to call it as a step (`<bean ref="#name" method="..."/>`).
- **When it is resolved.** The bean is looked up on the call's first evaluation in a route context, not when the route
  is loaded, and the bound method is kept for that context. A missing bean or method is an error naming the call.
- **Templates are data.** A payload template (`SetBodyTemplate` and the like) renders under a sandbox in which an
  expression reads members but never runs code: a method call on an object is refused with
  `ExpressionSandboxViolationException`. A `bean:` call in a template argument (`args.Set("gold", "bean:...")`) or in
  the template's own `expr(...)` is refused the same way, so the bean never runs. A `ref:name` or `env:NAME` there is
  refused too, before the registry or the environment is read; its message names the spelling (`'ref:rules' cannot be
  read here`), and `Written` carries it.
- **The colon touches both sides.** `bean:` and `ref:` are namespaces only when the colon touches the word and the
  name. In a ternary whose branch is a variable named `bean` or `ref`, keep the spaces: `flag ? bean : other`;
  `flag?bean:other` is a call and `flag?ref:other` a registry name.

### `env:NAME`, an environment variable

`env:NAME` is the OS environment variable `NAME`, as Apache Camel's `${env:NAME}`: `${env:BILLING_TOKEN}` in a
template, `env:REGION == 'eu'` in a condition. A variable that is not set is `null` and renders empty. It is read on
every evaluation. A template's sandbox refuses it, so a payload template cannot read the process environment. For a
value that may come from configuration or the environment, `{{BILLING_TOKEN}}` reads `IConfiguration` first (see
Configuration values above).

### `ref:name`, the object itself

`ref:name` is the object registered under `name`, as Apache Camel's `${ref:name}`. Nothing is called on it.

<!-- run: value -->
| Expression | Result |
|---|---|
| `ref:limits.max` | `100` |
| `ref:limits.max - header.b` | `97` |
| `${ref:limits.max}` | `100` |

<!-- run: condition -->
| Condition | Result |
|---|---|
| `header.a < ref:limits.max` | `True` |

<!-- run: template -->
| Template | Result |
|---|---|
| `max ${ref:limits.max}` | `max 100` |

- **The name** is the whole run of letters, digits, `_`, `.` and `-` after the colon: `ref:chat.settings` is the
  object registered as `chat.settings`. Put a space before a minus: `ref:limit-1` is the name `limit-1`,
  `ref:limit - 1` subtracts.
- **Nothing is read from it** by `ref:` itself. A whole placeholder `${ref:name}` keeps the object (a header set from it
  holds the instance); with text around it, the template writes its `ToString()`. To read a property, call a method
  with `bean:name.Method(...)`, or pass the object to one: `bean:fmt.Describe(ref:chat.settings)`. `ref:name(...)` is
  an error pointing to `bean:`.
- **Missing, sandbox, context, colon:** as for `bean:`. A name not in the registry is an error naming it; the object is
  looked up once per route context; a template's sandbox refuses it; `flag ? ref : other` needs its spaces.

### `Method()` in C#

`Method(reference, spec)` is the typed form of the same call, Apache Camel's `method()`:

```csharp
From("direct://orders")
    .Filter(Method("#loyalty", "IsGold(${header.customer})"))      // a condition
        .SetBody(Method("#pricing", "Gross(${header.qty}, 2.5)"))  // a value, its type kept
    .EndFilter()
    .Validate(Method("#rules", "IsAllowed(${header.customer})"), "not allowed");
```

The spec is written as in the `bean:` URI (`Name` or `Name(arg, ...)`, each argument a template or an expression). The
bean is a registry name; a type goes through `<bean name type>` or `AddToRegistry` first. In a condition (`Filter`,
`When`, `Validate`) a method returning `Task<bool>` is awaited; in a value position the call is synchronous, as the
function is. `Validate(IExpression)` reads any expression as a condition, as `Filter` does: `Validate(Expr("header.a > 0"))`.

### What `check` and `pack` verify

Every expression of a route file compiles as the engine compiles it, a bare one and each `${...}`: one the engine
refuses is an error at its attribute, in the engine's words. Text after a complete expression is such an error, in a
placeholder, a value and a condition alike: `${exception.type|message}` gives
`unexpected '|' at position 15 within the expression: the expression ends before it.` A bare expression that starts
with a root of the exchange and then becomes text by the rule for bare text (`header.b 3`) is a warning: its value is
the text itself, and in a condition it is always true. In an endpoint's own options, `${file:...}` is the file
component's and is checked when the endpoint is created.

For every `bean:` call and `ref:` name in a route file: that it parses (an error at the attribute otherwise); that the
name is declared by a `<bean>` of the package (a warning otherwise, since module code may register it); and, with
`--bin`, that a declared bean's type has the called method with that many arguments and that it is synchronous. A
bare `bean:` call or `ref:` name in a text that also holds a `${...}` is warned: the text renders as a template.

---

## 12. Compared with Apache Camel's Simple

| Camel Simple | redb.Route |
|---|---|
| `${header.x}`, `${exchangeProperty.x}`, `${body}` | `${header.x}`, `${property.x}`, `${body}` |
| `${header.x} == 'a'` as a predicate | `header.x == 'a'`; `${...}` around an operand renders text first |
| `${bean:name.method(${body})}`, `${bean:name?method=m}` | `${bean:name.method(${body})}` or `bean:name.method(body)`; the `?method=` form belongs to the `bean:` URI |
| `method(ref, "name")` | `Method("#ref", "name(args)")` |
| `${ref:x}` | `${ref:x}` or `ref:x`, the whole run is the name |
| `${header.x} =~ 'abc'` | `header.x =~ 'abc'`, the same rule: two strings ignore case, any other pair is `==` |
| `${header.n} range '100..199'`, `not range` | `header.n range '100..199'`, `not range`; the literal is checked at load |
| `${env:X}` | `${env:X}` or `env:X`; `{{X}}` reads configuration first |
| `${jsonpath:$.x}`, `${xpath:/x}` | the same, or `jpath('$.x')` / `xpath('/x')` |
| `${file:name}`, `${file:name.noext}`, `${file:size}` | the same, over the `redbFile.*` headers |
| `${date:now:yyyyMMdd}` | `dateformat(now(), 'yyyyMMdd')` |
| escaping `\${` and `$${` | `\${`; `$${` is a dollar sign followed by a placeholder here |

---

## 13. Known limits

| What | Today |
|---|---|
| `jpath($.x)` / `xpath(/x)` bare and unquoted | does not compile; quote the path or write it inside `${...}` |
| `header.Content-Type` bare | an error pointing to `${header.Content-Type}` (a subtraction otherwise) |
| `round(2.5)` | `2`, banker's rounding |
