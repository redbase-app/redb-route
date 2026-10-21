# redb.Route changelog: 2.x

Releases of the 2.x line, moved here unchanged from CHANGELOG.md when the main file started to
follow the current major only. Newer releases: [CHANGELOG.md](../CHANGELOG.md).

## [2.0.2] — 2026-05-16

### Changed
- **`redb.Route.Core`** — bumped `redb.Core` dependency to `2.0.2`.
  `redb.Core 2.0.2` renames `EavSaveStrategy` → `PropsSaveStrategy`; no API
  changes in `redb.Route.Core` itself.

## [2.0.1] — 2026-05-12

### Fixed
- **`redb.Route.Http`** — `HttpConsumer.WriteResponse` no longer echoes
  request headers back into the response. The original request header names
  are remembered on the exchange (`redbHttp.RequestHeaderNames` property)
  and skipped when copying headers from `exchange.In` (which acts as the
  fallback response message when `Out` is not set).
- **`redb.Route.Http`** — invalid header values (control characters and
  non-ASCII bytes that Kestrel would reject) are now filtered out instead
  of crashing the response pipeline.
- **`redb.Route.Http`** — internal framework headers (`redb*`, `Camel*`)
  are stripped from outgoing responses.
- **`redb.Route.Http`** — body-less InOut responses (HTTP 302 redirects,
  204 No Content, Set-Cookie-only replies) continue to propagate
  `Location` / `Set-Cookie` / etc. correctly; header copying remains
  unconditional and only the body write is gated on `Body is not null`.
- **`redb.Route.Ldap`** — service-account authenticated endpoints
  (`bindDn` is set) no longer reuse pooled connections. Active Directory
  could report *"successful bind must be completed"* on a pooled socket
  that was TCP-connected but no longer bound server-side. Such connections
  are now created per-operation and disposed on release.
- **`redb.Route.Ldap`** — `PageSize=0` is now a valid value that disables
  the RFC 2696 paged-results control entirely, for LDAP servers that do
  not support it. Validation accepts `PageSize >= 0`.
- **`redb.Route.Ldap`** — `LdapReferralException` raised during a search
  with `followReferrals=false` is now logged at Debug and the result
  iteration breaks cleanly instead of bubbling up.

### Changed
- **`redb.Route.Http`** — `HttpConsumer.HandleRequest` wraps `WriteResponse`
  in a try/catch that logs the failing method, path, route id and whether
  the response had already started, to aid diagnosing
  *"response already started"* errors.
- **`redb.Route.Ldap`** — service-account `Bind` switched from the
  4-argument overload (with explicit protocol version) to the 2-argument
  `BindAsync(dn, password, ct)`. The `protocolVersion` option is no longer
  forwarded to the bind call (LDAPv3 default of the underlying client
  applies).

## [2.0.0] — 2026-05-07

### Changed
- **License re-stated as Apache-2.0** as part of the RedBase 2.0 release
  alignment. Previous public release (`1.0.4`) carried the same license text
  in `LICENSE` but was tagged as MIT in some README badges; all metadata is
  now consistent (`Apache-2.0` in csproj, README badges, and CONTRIBUTING).
- Every nupkg now ships `LICENSE` + `NOTICE` files (Apache 2.0 § 4).
- Contributions are accepted under Apache-2.0; see `CONTRIBUTING.md`.
- Version bumped to `2.0.0` to align with the RedBase 2.0 release train
  (root packages also moved 1.3.0 → 2.0.0). No source-level API changes vs 1.0.4.
