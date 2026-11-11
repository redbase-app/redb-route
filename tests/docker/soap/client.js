// Independent SOAP client (Node `soap`) → drives redb.Route.Soap consumer for reverse-direction interop.
// TARGET env = our consumer URL; the WSDL is the shared contract, endpoint overridden to TARGET.
const soap = require('soap');
const target = process.env.TARGET;
const msg = process.argv[2] || 'hi';
soap.createClient('/app/echo.wsdl', { endpoint: target }, (err, client) => {
  if (err) { console.error('CLIENT_ERR ' + err.message); process.exit(2); }
  client.Echo({ msg }, (err, result) => {
    if (err) { console.error('CALL_ERR ' + err.message); process.exit(3); }
    console.log('RESULT:' + (result && result.result));
  });
});
