// FCM HTTP v1 API mock for redb.Route.Firebase e2e tests (no npm deps).
// The FirebaseAdmin SDK is pointed here via AppOptions.HttpClientFactory in the tests.
//
//   POST /v1/projects/{project}/messages:send  -> {"name":"projects/{project}/messages/{id}"}
//     ?validate_only=true (dryRun) is accepted and recorded.
//   GET    /messages  -> JSON array of every recorded send (envelope + received dryRun flag)
//   DELETE /messages  -> clear the store (test isolation)
//   GET    /healthz   -> ok
const http = require('http');

const PORT = 18200;
let messages = [];
let counter = 0;

const server = http.createServer((req, res) => {
  const sendMatch = req.url.match(/^\/v1\/projects\/([^/]+)\/messages:send/);

  if (req.method === 'POST' && sendMatch) {
    let body = '';
    req.on('data', (chunk) => { body += chunk; });
    req.on('end', () => {
      let parsed;
      try {
        parsed = JSON.parse(body);
      } catch (e) {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: { code: 400, message: 'invalid JSON: ' + e.message } }));
        return;
      }
      const project = sendMatch[1];
      const id = 'msg-' + (++counter);
      messages.push({ project, envelope: parsed, receivedAt: new Date().toISOString() });
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ name: `projects/${project}/messages/${id}` }));
    });
    return;
  }

  // Topic management (FirebaseAdmin SubscribeToTopic/UnsubscribeFromTopic → IID batch API):
  // POST /iid/v1:batchAdd | /iid/v1:batchRemove  body {to:"/topics/x", registration_tokens:[...]}
  // Response: {"results":[{}...]} — one empty object per token = success.
  if (req.method === 'POST' && (req.url === '/iid/v1:batchAdd' || req.url === '/iid/v1:batchRemove')) {
    let body = '';
    req.on('data', (chunk) => { body += chunk; });
    req.on('end', () => {
      let parsed;
      try {
        parsed = JSON.parse(body);
      } catch (e) {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'invalid JSON: ' + e.message }));
        return;
      }
      messages.push({
        project: (parsed.to || '').replace('/topics/', 'topic:'),
        envelope: { topicOp: req.url.endsWith('batchAdd') ? 'add' : 'remove', request: parsed },
        receivedAt: new Date().toISOString(),
      });
      const results = (parsed.registration_tokens || []).map(() => ({}));
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ results }));
    });
    return;
  }

  if (req.method === 'GET' && req.url === '/messages') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(messages));
    return;
  }

  if (req.method === 'DELETE' && req.url === '/messages') {
    messages = [];
    res.writeHead(204);
    res.end();
    return;
  }

  if (req.method === 'GET' && req.url === '/healthz') {
    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end('ok');
    return;
  }

  res.writeHead(404, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ error: { code: 404, message: 'not found: ' + req.method + ' ' + req.url } }));
});

server.listen(PORT, '0.0.0.0', () => console.log(`fcm-echo listening on :${PORT}`));
