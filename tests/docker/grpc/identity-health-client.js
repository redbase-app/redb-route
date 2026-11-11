// Standard gRPC health probe (grpc.health.v1.Health/Check) against a redb.Route gRPC listener.
//
//   node identity-health-client.js <host:port>
//
// No .proto: the request is empty and HealthCheckResponse{ status = 1 } is two bytes, so raw marshallers
// keep this free of generated code. Prints {"ok":true,"serving":true} or the failure status.
const grpc = require('@grpc/grpc-js');

const [, , target] = process.argv;
if (!target) { console.error('usage: identity-health-client.js <host:port>'); process.exit(2); }

const identity = x => x;
const client = new grpc.Client(target, grpc.credentials.createInsecure());

client.makeUnaryRequest(
  '/grpc.health.v1.Health/Check', identity, identity,
  Buffer.alloc(0), new grpc.Metadata(), {},
  (err, value) => {
    if (err) {
      console.log(JSON.stringify({ ok: false, code: err.code, status: grpc.status[err.code], details: err.details }));
      process.exit(0);
    }
    // field 1 (varint) = SERVING(1)  →  0x08 0x01
    const serving = value && value.length >= 2 && value[0] === 0x08 && value[1] === 0x01;
    console.log(JSON.stringify({ ok: true, serving, bytes: [...value] }));
    process.exit(0);
  });
