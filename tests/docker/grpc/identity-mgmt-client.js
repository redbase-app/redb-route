// Client for redb.Identity's management contract (identity.management.v1.proto).
//
// Separate from identity-client.js on purpose: that one is loaded by the interop tests and pinned to the
// protocol contract. This one speaks the admin surface, whose payloads are google.protobuf.Struct.
//
//   node identity-mgmt-client.js <host:port> <Service> <Method> [--token=<jwt>]
//
// Request JSON arrives on stdin (the arguments Struct). Prints one JSON line:
//   {"ok":true,"value":{...}} | {"ok":false,"code":N,"status":"...","details":"...","trailers":{...}}
const grpc = require('@grpc/grpc-js');
const loader = require('@grpc/proto-loader');
const fs = require('fs');

const [, , target, service, method, ...flags] = process.argv;
if (!target || !service || !method) {
  console.error('usage: identity-mgmt-client.js <host:port> <Service> <Method> [--token=<jwt>]');
  process.exit(2);
}

const def = loader.loadSync('/app/identity.management.v1.proto', {
  keepCase: true, longs: String, enums: String, defaults: true, oneofs: true,
});
const pkg = grpc.loadPackageDefinition(def).identity.management.v1;
const Service = pkg[service];
if (!Service) {
  console.error(`unknown service '${service}'. Known: ${Object.keys(pkg).join(', ')}`);
  process.exit(2);
}

const client = new Service(target, grpc.credentials.createInsecure());

const metadata = new grpc.Metadata();
const token = (flags.find(f => f.startsWith('--token=')) || '').slice('--token='.length);
if (token) metadata.add('authorization', 'Bearer ' + token);

const stdin = fs.readFileSync(0, 'utf8').trim();
const args = stdin ? JSON.parse(stdin) : {};

// The wire message is ManagementRequest { google.protobuf.Struct arguments = 1 }. proto-loader accepts a
// plain object for a Struct field, so the arguments go in as-is.
const request = { arguments: args };
const deadline = new Date(Date.now() + 30_000);

client[method](request, metadata, { deadline }, (err, value) => {
  if (err) {
    const trailers = {};
    if (err.metadata) for (const [k, v] of Object.entries(err.metadata.getMap())) trailers[k] = v;
    console.log(JSON.stringify({
      ok: false, code: err.code, status: grpc.status[err.code], details: err.details, trailers,
    }));
    process.exit(0);
  }
  console.log(JSON.stringify({ ok: true, value }));
});
