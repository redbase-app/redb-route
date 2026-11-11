// Independent gRPC server (@grpc/grpc-js — no shared code with redb.Route.Grpc).
// Direction A target: a redb producer calls this, proving a foreign stack accepts our wire framing.
const grpc = require('@grpc/grpc-js');
const loader = require('@grpc/proto-loader');

const pkg = grpc.loadPackageDefinition(loader.loadSync('/app/echo.proto', { keepCase: true }));

const server = new grpc.Server();
server.addService(pkg.interop.Echo.service, {
  Say: (call, cb) => cb(null, { result: 'echo:' + (call.request.msg || '') }),
  SayMany: (call) => {
    for (let i = 1; i <= 3; i++) call.write({ result: `page-${i}` });
    call.end();
  },
});

server.bindAsync('0.0.0.0:18100', grpc.ServerCredentials.createInsecure(), (err, port) => {
  if (err) { console.error(err); process.exit(1); }
  console.log('gRPC echo (@grpc/grpc-js) listening on ' + port);
});
