// ============================================================================
//  SqsRpcDemo — RPC over Amazon SQS with concurrent (multi-threaded) workers.
//
//  A single-file example that shows, end to end, the three things people ask
//  about the SQS connector:
//
//    1) PRODUCERS   — a client sends request messages to a queue, and a worker
//                     sends reply messages back.
//    2) CONSUMERS   — a worker route consumes the request queue; the client
//                     consumes the reply queue.
//    3) MULTITHREADING (concurrency) — the worker route runs N *competing
//                     consumers* (.ConcurrentConsumers(N)), the SQS-native
//                     concurrency model, so up to N requests are processed in
//                     parallel. We measure and print the peak concurrency.
//
//  RPC pattern (request/reply over a queue):
//    SQS has no built-in request/reply, so we use the classic correlation
//    pattern — the same one Apache Camel uses for JMS/SQS RPC:
//      • the client tags each request with a unique  correlationId  and a
//        replyTo  queue name (both travel as SQS message attributes);
//      • the worker computes a result and sends it to the  replyTo  queue,
//        echoing the correlationId back;
//      • the client's reply consumer matches the correlationId to the pending
//        request and completes it.
//    Message attributes round-trip as exchange headers: a header you set on the
//    request surfaces on the consumer as "redbSqs.attr.<name>", and the SQS
//    producer forwards it back out as an attribute — so correlationId returns
//    on the reply automatically.
//
//  See ../../CONCURRENCY.md for how .ConcurrentConsumers(N) (broker/queue
//  source) compares to the .Threads(N) processing EIP (polling sources).
//
//  --- Infrastructure (LocalStack, already in `docker ps`) ---
//    Container: redb-localstack   Endpoint: http://localhost:4566
//    Anonymous test/test credentials, region us-east-1 — exactly what the
//    redb.Route.Tests.Sqs integration tests use. Start it if it is not running:
//      docker start redb-localstack
//    (or:  docker compose -f C:\Work\yaml\Amazon\docker-compose.yml up -d)
//
//  --- Run ---
//      dotnet run --project redb.Route/demos/SqsRpcDemo
//    Expected: 12 numbers squared via RPC, each with a round-trip time, and a
//    line reporting the peak worker concurrency (up to WorkerPool = 4).
//
//  In production you would drop .ServiceUrl(...)/.Credentials(...) and let the
//  default AWS credential provider chain + .Region(...) point at real SQS.
// ============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

using redb.Route.Abstractions;   // IExchange, IProducerTemplate
using redb.Route.Core;           // RouteContext, ProducerTemplate, Message
using redb.Route.Sqs;            // SqsComponent, SnsComponent, SqsHeaders
using SqsDsl = redb.Route.Sqs.Fluent.Sqs;

// ─── 0. Settings — LocalStack, matching the integration tests ────────────────
const string ServiceUrl = "http://localhost:4566";
const string Region = "us-east-1";
const string AccessKey = "test";
const string SecretKey = "test";

const string RequestQueue = "rpc-requests";  // client → worker
const string ReplyQueue = "rpc-replies";     // worker → client
const int WorkerPool = 4;                     // competing consumers = max parallelism
const int RequestCount = 12;                  // how many RPC calls the client makes

// One reusable base builder so every endpoint shares connection settings.
// AutoCreateQueue() creates the queue on first use (no manual topology needed).
static redb.Route.Sqs.Fluent.SqsBuilder Q(string name) =>
    SqsDsl.Queue(name)
        .ServiceUrl(ServiceUrl).Region(Region).Credentials(AccessKey, SecretKey)
        .AutoCreateQueue();

// ─── 1. Logging + route context (created MANUALLY, no Tsak, no database) ──────
using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Warning));   // Warning: keep the connector quiet, we print our own lines
var log = loggerFactory.CreateLogger("SqsRpcDemo");

var ctx = new RouteContext(contextId: "sqs-rpc-demo", loggerFactory: loggerFactory);
ctx.AddComponent(new SqsComponent());   // registers the sqs:// scheme
ctx.AddComponent(new SnsComponent());   // registers sns:// too (not used here, but the pair belong together)

// ─── 2. Shared state ─────────────────────────────────────────────────────────
// Peak concurrency observed inside the worker — proof the pool really parallelises.
var inFlight = 0;
var peakConcurrency = 0;
var peakLock = new object();

// Pending RPC calls: correlationId → the awaiting client call. When the reply
// consumer sees a matching correlationId it completes the TaskCompletionSource.
var pending = new ConcurrentDictionary<string, TaskCompletionSource<string>>();

// ─── 3. Routes ───────────────────────────────────────────────────────────────
ctx.AddRoutes(r =>
{
    // ── WORKER: consume requests with N competing consumers, reply to replyTo ──
    // .ConcurrentConsumers(WorkerPool) = up to WorkerPool messages processed at once.
    // .MaxNumberOfMessages(1) so each of the N loops holds exactly one message —
    // the pool truly saturates instead of one loop grabbing a batch of 10.
    r.From(Q(RequestQueue)
            .ConcurrentConsumers(WorkerPool)
            .MaxNumberOfMessages(1)
            .WaitTimeSeconds(1))          // short long-poll so the demo shuts down promptly
        .RouteId("rpc-worker")
        .Process(async (exchange, ct) =>
        {
            // Track how many requests are being processed simultaneously.
            var now = Interlocked.Increment(ref inFlight);
            lock (peakLock) { if (now > peakConcurrency) peakConcurrency = now; }

            var n = int.Parse(exchange.In.Body!.ToString()!);

            // Simulate real work (I/O or CPU). With a serial consumer this would
            // add up; with WorkerPool competing consumers it overlaps.
            await Task.Delay(300, ct);

            // The result becomes the reply body. correlationId travels back
            // automatically (incoming attribute → forwarded attribute).
            exchange.In.Body = (n * (long)n).ToString();

            Interlocked.Decrement(ref inFlight);
        })
        // Reply to the queue named in the request's replyTo attribute. .ToD takes
        // a factory so the destination is chosen per-message at runtime.
        .ToD(exchange =>
        {
            var replyTo = Attr(exchange, "replyTo") ?? ReplyQueue;
            return Q(replyTo).Build();
        });

    // ── CLIENT REPLY CONSUMER: correlate replies back to pending calls ──────────
    // A single consumer is enough for the client side; batch up to 10 per receive.
    r.From(Q(ReplyQueue).WaitTimeSeconds(1).MaxNumberOfMessages(10))
        .RouteId("rpc-reply-collector")
        .Process((exchange, ct) =>
        {
            var correlationId = Attr(exchange, "correlationId");
            var result = exchange.In.Body?.ToString();
            if (correlationId is not null && pending.TryRemove(correlationId, out var tcs))
                tcs.TrySetResult(result ?? "");
            return Task.CompletedTask;
        });
});

// ─── 4. Start the context (both consumers begin polling) ─────────────────────
await ctx.Start();
Console.WriteLine($"SqsRpcDemo started — worker pool = {WorkerPool} competing consumers on '{RequestQueue}'.");
Console.WriteLine($"Sending {RequestCount} RPC requests (square the number)...\n");

// ─── 5. CLIENT: fire all requests, then await their correlated replies ────────
var template = new ProducerTemplate(ctx);
template.Start();

var overall = Stopwatch.StartNew();
var calls = new List<Task>();

for (var i = 1; i <= RequestCount; i++)
{
    var value = i;
    calls.Add(CallAsync(value));
}

async Task CallAsync(int value)
{
    var correlationId = Guid.NewGuid().ToString("N");
    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    pending[correlationId] = tcs;

    // The request: body = the number; attributes = correlationId + replyTo.
    var request = new Message(value.ToString());
    request.Headers["correlationId"] = correlationId;
    request.Headers["replyTo"] = ReplyQueue;

    var sw = Stopwatch.StartNew();
    await template.SendAsync(Q(RequestQueue).Build(), request);

    // Wait for the correlated reply (with a safety timeout).
    var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    sw.Stop();

    if (completed == tcs.Task)
        Console.WriteLine($"  {value,2}² = {tcs.Task.Result,-4}  (round-trip {sw.ElapsedMilliseconds,4} ms)");
    else
    {
        pending.TryRemove(correlationId, out _);
        Console.WriteLine($"  {value,2}²  — TIMED OUT waiting for reply");
    }
}

await Task.WhenAll(calls);
overall.Stop();

// ─── 6. Report ────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"All {RequestCount} RPC calls completed in {overall.ElapsedMilliseconds} ms.");
Console.WriteLine($"Peak worker concurrency observed: {peakConcurrency} (pool size = {WorkerPool}).");
Console.WriteLine(
    $"Serial would take ~{RequestCount * 300} ms; the pool overlaps the work, so it finishes much sooner.");

// ─── 7. Shutdown (graceful drain of in-flight messages) ───────────────────────
template.Stop();
await ctx.DisposeAsync();
log.LogInformation("Demo finished, context disposed.");

// ============================================================================
//  Helper — read an incoming SQS message attribute exposed as a header.
//  The consumer surfaces every message attribute under "redbSqs.attr.<name>".
// ============================================================================
static string? Attr(IExchange exchange, string name) =>
    exchange.In.Headers.TryGetValue(SqsHeaders.MessageAttributePrefix + name, out var v)
        ? v?.ToString()
        : null;
