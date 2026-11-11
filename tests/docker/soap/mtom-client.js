// Independent MTOM SOAP client (Node `soap`, forceMTOM) → drives redb.Route.Soap consumer.
// Sends a binary attachment; prints the result and any response attachments the connector returned.
// mtomResponseAttachments is an object { parts: [{ body, headers }] } (soap's own shape), not an array.
const soap = require('soap');
const target = process.env.TARGET;
const msg = process.argv[2] || 'hi';
const attBody = process.argv[3] || 'node-mtom-bytes';
soap.createClient('/app/echo.wsdl', { endpoint: target, parseReponseAttachments: true }, (err, client) => {
  if (err) { console.error('CLIENT_ERR ' + err.message); process.exit(2); }
  const options = {
    forceMTOM: true,
    attachments: [{ mimetype: 'application/octet-stream', contentId: 'up-1', name: 'up-1', body: Buffer.from(attBody) }],
  };
  client.Echo({ msg }, (err, result, rawResponse, soapHeader, rawRequest, mtomAtts) => {
    if (err) { console.error('CALL_ERR ' + err.message); process.exit(3); }
    console.log('RESULT:' + (result && result.result));
    const parts = (mtomAtts && mtomAtts.parts) || [];
    const atts = parts.length ? parts.map(p => (p.body ? p.body.toString() : '')).join(',') : 'none';
    console.log('RESPATT:' + atts);
  }, options);
});
