# SOAP interop: node-soap

An independent SOAP stack (Node.js [`soap`](https://www.npmjs.com/package/soap)) for the `redb.Route.Tests.Soap`
interop tests. It shares no code with `redb.Route.Soap`, so it proves interop that loopback tests cannot.

Profile `soap` of `redb.Route/docker-compose.tests.yml` (see `../README.md`); the image has the `soap` package
baked in.

- `echo.wsdl`: one `Echo` operation (`urn:soaptest`), `msg` in, `result` out.
- `server.js`: SOAP server on 18060 (`/echo`), returns `echo:<msg>`; our producer calls it.
- `client.js`, `mtom-client.js`: clients the tests run through `docker exec soap-echo`, calling our consumer on the
  host (`host.docker.internal`, ports 18160+ and 18180+ offset by the .NET major version).
- `xmlenc-verify.js`: an independent XML-Encryption decryptor (Node `crypto`) for the WSS encryption test, which
  hands over its envelope and key through `SOAP_INTEROP_DIR` (`.generated/soap`, mounted as `/work`).

Readiness: `curl http://localhost:18060/echo?wsdl` answers 200. The tests skip when nothing answers on 18060.
