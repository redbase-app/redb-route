# gRPC interop fixture for `redb.Route.Grpc`

An independent gRPC stack — Node.js [`@grpc/grpc-js`](https://github.com/grpc/grpc-node) — in a container.
It shares no code with our connector, which is the point: `redb.Route.Grpc` implements the gRPC wire
protocol itself (framing, trailers, statuses, deadlines, gzip) instead of taking `Grpc.AspNetCore`, so the
only real proof is a foreign implementation accepting our bytes and us accepting its.

This is also the only **cross-process** coverage: the client runs inside the container and reaches the
host through `host.docker.internal`.

## Run

Profile `grpc` of `redb.Route/docker-compose.tests.yml` (see `../README.md`); the image has the gRPC packages
baked in, and the mTLS certificates come from `gen-certs.sh` (`.generated/grpc`, mounted as `/app/certs`;
`REDB_GRPC_CERTS` points there for the tests).

```bash
dotnet test tests/redb.Route.Tests.Grpc --filter Category=Interop
```

The tests are **gated**: each one no-ops unless the container answers on `127.0.0.1:18100`, so the normal
suite stays green without Docker.

## What is covered

| Direction | Test | Proves |
|---|---|---|
| redb producer → grpc-js server | `Redb_producer_to_grpcjs_server_echoes` | a foreign server parses our request frame and metadata |
| redb producer → grpc-js server | `Redb_producer_reads_a_grpcjs_server_stream` | we read a foreign server-stream as `IAsyncEnumerable` |
| grpc-js client → redb consumer | `Grpcjs_client_to_redb_consumer_echoes` | a foreign client accepts our reply frame and trailers |
| grpc-js client → redb consumer | `Grpcjs_client_gets_a_real_status_from_our_consumer` | `status.code=403` arrives as `PERMISSION_DENIED` with detail |
| grpc-js client → redb consumer | `Grpcjs_client_reads_our_server_stream` | our per-yield frames read as a stream by a foreign client |
| grpc-js client → redb consumer | `Grpcjs_client_over_mtls_is_authenticated_by_thumbprint` | real TLS handshake with a client certificate, pinned by thumbprint |
| grpc-js client → redb consumer | `Grpcjs_client_may_gzip_its_request` | we inflate a foreign client's gzipped request |

The contract is a **typed** `.proto` (`interop.Echo`), not our generic `RedbService` — so the same tests
double as proof that a client generated from a real `.proto` can call a redb route with no server stubs on
our side.

## Files

| File | Role |
|---|---|
| `echo.proto` | the interop contract: `Say` (unary) and `SayMany` (server-streaming) |
| `server.js` | grpc-js server on `18100`, the target for direction A |
| `client.js` | grpc-js client used via `docker exec`, the caller for direction B; prints a JSON verdict |
| `.generated/grpc` | CA, server (`server.pfx`, password `redb`) and client certificates for the mTLS test, from `gen-certs.sh` |
| `Dockerfile` | `node:22-slim` with the gRPC packages baked in |
| `identity.v1.proto` | copy of redb.Identity's published contract - see the section below |
| `identity-client.js` | grpc-js client for that contract; reads its request JSON from stdin |

## Ports

| Port | Who |
|---|---|
| `18100` | grpc-js server (published to the host) |
| `18110+`, `18120+`, `18130+`, `18140+` | our consumers, offset by .NET major so parallel net8/net9/net10 test hosts never collide |

## Second consumer: the redb.Identity gRPC facade

The same container also carries a client for redb.Identity's published contract, so the facade is proven
the way the connector is - by a stack that shares no code with it.

```bash
dotnet test redb.Identity/tests/redb.Identity.Tests --filter Category=Interop
```

| File | Role |
|---|---|
| `identity.v1.proto` | a copy of the published contract from `redb.Identity.Contracts/Protos`, copied into the image |
| `identity-client.js` | `@grpc/grpc-js` client generated from it at load time, exactly as a consumer would |

The test boots Core plus the facade in-process on an ephemeral port, binds `0.0.0.0` so the container can
reach it through `host.docker.internal`, and drives the client through `docker exec -i`. The request JSON
goes in on **stdin**: `docker exec` passes argv straight through with no shell, and Windows argument
splitting eats the quotes out of a JSON literal long before the container sees it.

| Test | Proves |
|---|---|
| `A_foreign_client_gets_a_token_from_the_published_contract` | a client generated from the `.proto` we publish gets a real token from `client_credentials` |
| `A_foreign_client_reads_the_refusal_as_a_status_and_a_trailer` | a wrong secret arrives as `UNAUTHENTICATED` with `invalid_client` in a trailer, the only place a non-OK reply can carry it |
| `A_foreign_client_reads_the_discovery_document` | a `google.protobuf.Struct` - nested maps and lists - decoded by a runtime that never saw our codec |

Same gate as above: no container, no run.

When the contract changes, copy it again and rebuild the image (`docker compose ... build grpc-echo`); the file
here is a copy, not a link:

```bash
cp redb.Identity/src/redb.Identity.Contracts/Protos/identity.v1.proto redb.Route/tests/docker/grpc/identity.v1.proto
```

