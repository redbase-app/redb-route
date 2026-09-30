# redb.Route.Cache

Cache as an EIP (WSO2 `Cache` mediator / Camel `caffeine-cache` analog) over the .NET caching
abstractions: `IMemoryCache` in-process (default, created on demand) or `IDistributedCache`
(Redis, SQL Server, anything registered in DI).

## Caching scope

```csharp
.Cache("customer-${header.customerId}", TimeSpan.FromMinutes(5))
    .Enrich("http://crm/customers/${header.customerId}")
.EndCache()
```

On a hit the body (and, with `.CacheHeaders()`, the headers) comes from the cache and the inner
steps are skipped; on a miss they run and the result is stored. Header `cache.hit` tells which.
Options: `.Region("customers")`, `.SlidingExpiration(...)`, `.CacheHeaders()`, `.Distributed()`,
`.KeyFromBody()`.

`.KeyFromBody()` takes the SHA-256 of the body instead of an explicit key. Only the body is hashed: the
content type and the headers are not part of the key. A `Stream` body is read to the end for it and the
message continues with its bytes; an object goes through the context's data format for the message's
content type, the way the distributed cache stores it. A body that cannot be serialized fails the
exchange with an error that names `KeyFromBody` and the type.

## Component

```csharp
.To("cache:customers?action=get&key=${header.customerId}")
.Filter("header.cache.hit == false")
    .Enrich("http://crm/...")
    .To("cache:customers?action=put&key=${header.customerId}&ttl=5m")
.EndFilter()
```

`cache:<region>?action=get|put|remove|clear&key=...&ttl=5m&sliding=1m&provider=memory|distributed&headers=X-Rate,Content-Language`.
Durations: `500ms`, `30s`, `5m`, `2h`, `1d` or `hh:mm:ss` (a bare number is refused: `5` would be five
days to `TimeSpan`; a number `TimeSpan` cannot hold is refused the same way). Region and key are joined
with a length-prefixed separator, so region `a` / key `b:c` and region `a:b` / key `c` are two entries.

- `get` sets `cache.hit` and restores the entry where it was cached from: a reply the scope stored out
  of `Out` comes back as `Out`, anything else as `In`.
- `put` stores the message as it reaches the step. The pipeline has folded the previous step's reply
  into `In` by then. Of the headers it stores only the ones named with `headers=` (see Headers).
- `clear` empties the region (see Boundaries for what it can see).
- The options are checked when the endpoint is created, and `To` creates it on the first message.

## What a hit hands back

A miss is computed once per key at a time, across every cache node of the context that shares the
store: concurrent exchanges for the same key wait for the first one's result instead of each running
the inner steps. A failed computation is not cached, so the waiters retry one at a time. A `Stream`
body is buffered on the miss (the live message continues with the bytes) so a hit can replay it; a
`JsonNode` body is copied into and out of the cache. Text and byte arrays are treated as immutable.
**Any other object (a POCO, a list) is shared by every hit by design**: do not mutate what a cache
scope handed you, or clone it first.

## Headers

A hit hands the entry to another exchange, so an entry never carries what an exchange came in with,
and never a credential.

- **Scope, `.CacheHeaders()`.** The entry keeps what the inner steps did to the headers, compared by
  instance with the ones the exchange came in with: the headers added or changed, and the names
  removed. A hit does the same to the current exchange and leaves its other headers alone,
  `Authorization`, `Cookie` and the `redbHttp.*` request metadata included. With the reply in `Out`, the
  reply's own headers and what the steps did to `In` are both kept. A header a step set to the very
  instance it arrived as counts as untouched.
- **Component, `put`.** Only the headers named with `headers=Name1,Name2`; a name the message does not
  carry is skipped. `cacheHeaders=true` is refused: a `put` sees the message as a whole and cannot tell
  the headers of the value from the headers of the request.
- **Never, in either form:** `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`. Naming one
  on a `put` is an error.

## Setup

Nothing for the in-process cache. `context.UseCache(o => o.MaxEntries = 10_000)` / `services.AddRedbRouteCache(o => ...)`
register options (and the `cache:` component in DI). For `Distributed`, register an `IDistributedCache`
(`services.AddStackExchangeRedisCache(...)`, `AddDistributedMemoryCache()`, or
`context.AddService(typeof(IDistributedCache), cache)`). A POCO body goes through the distributed cache
as JSON and comes back as the same CLR type wherever that type's assembly is loaded; where it is not,
the entry counts as a miss.

## Boundaries

- **`clear` works from a list of written keys**, because neither abstraction can enumerate. On a
  distributed cache it removes the keys this process has written; a sliding key stays on the list for
  as long as it is read. On the in-process cache it removes the keys this context has written: two
  contexts sharing one host `IMemoryCache` keep a list each.
- **A value under a cache key that this package did not write is an error** naming the key, not a hit
  and not a miss: no envelope marker, not JSON, or an envelope version this build does not read. Give
  the cache its own key prefix (`RedisCacheOptions.InstanceName`) or another region. Entries written
  before the marker existed are refused the same way, so clear the regions of a distributed cache
  after upgrading.
- **One computation per key holds inside a context.** Other processes, and other contexts in the same
  process, compute on their own: there is no distributed lock.
- **Headers go through a distributed cache as JSON primitives**; other values are dropped.
- **`MaxEntries` bounds the cache the package creates**; a cache registered by the host keeps its own
  settings.
- **No spans or metrics.** A cache node is a call inside the process, not a transport. `cache.hit` is
  the signal, inside the route.
