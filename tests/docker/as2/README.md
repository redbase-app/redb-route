# AS2 interop: OpenAS2

A real OpenAS2 server (greicodex/openas2, BouncyCastle) for `redb.Route.Tests.As2` `As2InteropTests`: signed and
encrypted AS2 messages in both directions, with synchronous and asynchronous signed MDNs. An implementation that
shares no code with `redb.Route.As2` accepting our wire format and MIC is what loopback tests cannot prove.

Profile `as2` of `redb.Route/docker-compose.tests.yml` (see `../README.md`). The image bakes in `config/`;
`gen-certs.sh` writes the keys:

- `.generated/as2/certs/`: `redb.p12` (password `testpass`) and `redb.crt` for our side, `openas2.crt` for the partner;
  `AS2_INTEROP_CERTS` points here.
- `.generated/as2/keystore/as2_certs.p12` (password `testas2`): OpenAS2's key (alias `openas2`) and our certificate
  (alias `redb`, trusted); mounted into the container with `ssl_certs.jks`.
- `.generated/as2/data/`: OpenAS2's message folders, shared with the tests (they drop files into
  `data/outbox/redb-async` and watch `data/pendinginfoMDN3`).

Partnerships (`config/partnerships.xml`): OpenAS2 receives on 10080 inside the container (host 14080) and its async
MDN receiver on 10081 (host 14081); it posts to our receivers at `host.docker.internal:15081` and `:15082`.
The tests skip without the stand; `REDB_AS2_INTEROP=1` makes that a failure.
