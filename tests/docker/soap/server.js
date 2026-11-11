const soap = require('soap');
const http = require('http');
const fs = require('fs');
const wsdl = fs.readFileSync('/app/echo.wsdl', 'utf8');
const service = { EchoService: { EchoPort: { Echo: (args) => ({ result: 'echo:' + (args && args.msg ? args.msg : '') }) } } };
const server = http.createServer((req, res) => { res.statusCode = 404; res.end(); });
server.listen(18060, () => { soap.listen(server, '/echo', service, wsdl); console.log('SOAP echo (node soap) listening on 18060'); });
