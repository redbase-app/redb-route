// Independent client for redb.Identity's published gRPC contract (identity.v1.proto).
//
// The point is that it is foreign: @grpc/grpc-js generates its stubs from the .proto file the way any
// consumer would, shares no code with the facade, and runs in another process. What loopback tests cannot
// show is exactly this — that the contract we publish is the contract we serve.
//
//   node identity-client.js <host:port> <Method> [--meta k=v] [--tls] [--gzip]
//
// The request JSON arrives on stdin, not as an argument: `docker exec` passes argv straight through with
// no shell, and Windows argument splitting mangles embedded quotes long before the container sees them.
//
// Prints one JSON line: {"ok":true,"value":{…}} or {"ok":false,"code":N,"status":"…","details":"…",
// "trailers":{…}}. Trailers matter: a non-OK gRPC reply discards its payload, so the OAuth error code
// travels there and nowhere else.
const grpc = require('@grpc/grpc-js');
const loader = require('@grpc/proto-loader');
const fs = require('fs');

const [, , target, method, ...flags] = process.argv;
if (!target || !method) {
  console.error("usage: identity-client.js <host:port> <Method> '<json>' [--meta k=v] [--tls] [--gzip]");
  process.exit(2);
}

const def = loader.loadSync('/app/identity.v1.proto', {
  keepCase: true, longs: String, enums: String, defaults: true, oneofs: true,
});
const Identity = grpc.loadPackageDefinition(def).identity.v1.Identity;

const useTls = flags.includes('--tls');
const credentials = useTls
  ? grpc.credentials.createSsl(fs.readFileSync('/app/certs/ca.crt'))
  : grpc.credentials.createInsecure();

const options = {};
if (flags.includes('--gzip')) options['grpc.default_compression_algorithm'] = 2;

const client = new Identity(target, credentials, options);

const metadata = new grpc.Metadata();
for (const flag of flags) {
  if (!flag.startsWith('--meta=')) continue;
  const pair = flag.slice('--meta='.length);
  const eq = pair.indexOf('=');
  metadata.add(pair.slice(0, eq), pair.slice(eq + 1));
}

const stdin = fs.readFileSync(0, "utf8").trim();
const request = stdin ? JSON.parse(stdin) : {};
const deadline = new Date(Date.now() + 30_000);

client[method](request, metadata, { deadline }, (err, value) => {
  if (err) {
    // Everything a caller can act on: the status, the detail, and the trailers the facade put the
    // machine-readable OAuth code into.
    const trailers = {};
    if (err.metadata) {
      for (const [key, values] of Object.entries(err.metadata.getMap())) trailers[key] = values;
    }
    console.log(JSON.stringify({
      ok: false, code: err.code, status: grpc.status[err.code], details: err.details, trailers,
    }));
    process.exit(0);
  }
  console.log(JSON.stringify({ ok: true, value }));
});
