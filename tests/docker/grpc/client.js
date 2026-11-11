// Independent gRPC client (@grpc/grpc-js). Direction B: calls a redb consumer through host-gateway,
// proving a foreign client accepts our replies, trailers and statuses.
//
//   node client.js <host:port> <method> [--tls] [--gzip] [--msg=text]
// method: say | saymany | fail
const grpc = require('@grpc/grpc-js');
const loader = require('@grpc/proto-loader');
const fs = require('fs');

const [target, method] = process.argv.slice(2);
const flags = process.argv.slice(4);
const useTls = flags.includes('--tls');
const useGzip = flags.includes('--gzip');
const msgArg = (flags.find(f => f.startsWith('--msg=')) || '--msg=hello-redb').slice(6);

const pkg = grpc.loadPackageDefinition(loader.loadSync('/app/echo.proto', { keepCase: true }));

const creds = useTls
  ? grpc.credentials.createSsl(
      fs.readFileSync('/app/certs/ca.crt'),
      fs.readFileSync('/app/certs/client.key'),
      fs.readFileSync('/app/certs/client.crt'))
  : grpc.credentials.createInsecure();

const options = { 'grpc.ssl_target_name_override': 'localhost' };
if (useGzip) options['grpc.default_compression_algorithm'] = 2; // 2 = gzip

const client = new pkg.interop.Echo(target, creds, options);

function done(err, value) {
  if (err) {
    // Print the gRPC status so the test can assert on it.
    console.log(JSON.stringify({ ok: false, code: err.code, details: err.details }));
    process.exit(0);
  }
  console.log(JSON.stringify({ ok: true, value }));
  process.exit(0);
}

if (method === 'saymany') {
  const items = [];
  const call = client.SayMany({ msg: msgArg });
  call.on('data', r => items.push(r.result));
  call.on('end', () => done(null, items));
  call.on('error', e => done(e));
} else {
  client.Say({ msg: msgArg }, (err, reply) => done(err, err ? null : reply.result));
}
