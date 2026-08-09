# redb.Route.Http.Hosting

Shared Kestrel HTTP hosting infrastructure for the [redb.Route](../redb.Route) ESB framework.

Provides `SharedHttpServerManager` — a multiplexing HTTP server (one Kestrel per `host:port`, many routes)
used by HTTP-based transports (`redb.Route.Http`, `redb.Route.As2`, …). Extracting it here lets those
connectors share one server manager **without depending on each other**: register it once with
`services.AddRedbRouteHttpHosting()` (idempotent), and every connector resolves the same singleton — so
an HTTP route and an AS2 route in the same worker share one Kestrel and never fight over a port.

Standalone hosting only — depends on the ASP.NET runtime, not on redb.Route core or any connector.

Part of the redb.Route family.
