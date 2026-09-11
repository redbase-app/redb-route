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
`.KeyFromBody()` (SHA-256 of the body instead of an explicit key).

## Component

```csharp
.To("cache:customers?action=get&key=${header.customerId}")
.Filter("header.cache.hit == false")
    .Enrich("http://crm/...")
    .To("cache:customers?action=put&key=${header.customerId}&ttl=5m")
.EndFilter()
```

`cache:<region>?action=get|put|remove|clear&key=...&ttl=5m&sliding=1m&provider=memory|distributed&cacheHeaders=true`.
Durations: `500ms`, `30s`, `5m`, `2h`, `1d` or `hh:mm:ss` (a bare number is refused: `5` would be five
days to `TimeSpan`). `get` sets `cache.hit`; `clear` empties the region. Region and key are joined with a
length-prefixed separator, so region `a` / key `b:c` and region `a:b` / key `c` are two entries.

## What a hit hands back

A miss is computed once per key at a time: concurrent exchanges for the same key wait for the first
one's result instead of each running the inner steps. A `Stream` body is buffered on the miss (the live
message continues with the bytes) so a hit can replay it; a `JsonNode` body is copied into and out of the
cache. Text and byte arrays are treated as immutable. **Any other object (a POCO, a list) is shared by
every hit by design**: do not mutate what a cache scope handed you, or clone it first.

## Setup

Nothing for the in-process cache. `context.UseCache(o => o.MaxEntries = 10_000)` / `services.AddRedbRouteCache(o => ...)`
register options (and the `cache:` component in DI). For `Distributed`, register an `IDistributedCache`
(`services.AddStackExchangeRedisCache(...)`, `AddDistributedMemoryCache()`, or
`context.AddService(typeof(IDistributedCache), cache)`). A POCO body goes through the distributed cache
as JSON and comes back as the same CLR type; `clear` on a distributed cache removes the keys this
process has written (the abstraction cannot enumerate).
