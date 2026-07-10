// ============================================================================
//  SqsPubSubDemo — Publish-Subscribe over Amazon SNS → SQS fan-out.
//
//  One publish reaches N independent queues, each with its own consumer — the
//  classic Publish-Subscribe Channel EIP, done the AWS way: an SNS topic with
//  several SQS queues subscribed to it.
//
//    publish "order" ──▶ SNS topic ──┬──▶ SQS queue "orders-billing"  ──▶ billing route
//                                     └──▶ SQS queue "orders-shipping" ──▶ shipping route
//
//  Each order event is delivered to BOTH queues; billing and shipping process
//  it independently, at their own pace, with their own retries.
//
//  RAW MESSAGE DELIVERY
//    By default SNS wraps your payload in a JSON notification envelope
//    ({"Type":"Notification","Message":"<payload>",...}), so the subscriber
//    would have to unwrap it, and SNS message attributes would be buried inside
//    the envelope. This demo subscribes with .RawMessageDelivery() so each queue
//    receives the BARE payload and SNS attributes arrive as SQS attributes —
//    which is what you almost always want for fan-out. We print the raw body of
//    the first message so you can see there is no envelope.
//
//  --- Infrastructure (LocalStack, already in `docker ps`) ---
//    Container: redb-localstack   Endpoint: http://localhost:4566
//    Start it if it is not running:  docker start redb-localstack
//    (or:  docker compose -f C:\Work\yaml\Amazon\docker-compose.yml up -d)
//
//  --- Run ---
//      dotnet run --project redb.Route/demos/SqsPubSubDemo --framework net9.0
//    Expected: 5 order events, each handled once by billing AND once by shipping
//    (10 handled total), every body a bare JSON payload (no SNS envelope).
//
//  In production you would drop .ServiceUrl(...)/.Credentials(...) and let the
//  default AWS credential provider chain + .Region(...) point at real SNS/SQS.
// ============================================================================

using System.Text.Json;

using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;

using Microsoft.Extensions.Logging;

using redb.Route.Abstractions;   // IExchange
using redb.Route.Core;           // RouteContext, Exchange, Message, EndpointUriParser
using redb.Route.Sqs;            // SqsComponent, SnsComponent, SnsEndpoint, SqsHeaders
using SqsDsl = redb.Route.Sqs.Fluent.Sqs;
using SnsDsl = redb.Route.Sqs.Fluent.Sns;
using Message = redb.Route.Core.Message;   // disambiguate from Amazon.SQS.Model.Message

// ─── 0. Settings — LocalStack, matching the integration tests ────────────────
const string ServiceUrl = "http://localhost:4566";
const string Region = "us-east-1";
const string AccessKey = "test";
const string SecretKey = "test";

const string Topic = "orders-events";
const string BillingQueue = "orders-billing";
const string ShippingQueue = "orders-shipping";
const int OrderCount = 5;

// Reusable base builders sharing connection settings.
static redb.Route.Sqs.Fluent.SqsBuilder Q(string name) =>
    SqsDsl.Queue(name).ServiceUrl(ServiceUrl).Region(Region).Credentials(AccessKey, SecretKey).AutoCreateQueue();

// ─── 1. Logging + route context (manual, no Tsak, no database) ───────────────
using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Warning));   // keep the connector quiet — we print our own lines
var log = loggerFactory.CreateLogger("SqsPubSubDemo");

var ctx = new RouteContext(contextId: "sqs-pubsub-demo", loggerFactory: loggerFactory);
ctx.AddComponent(new SqsComponent());
ctx.AddComponent(new SnsComponent());

// ─── 2. Create both queues up front and read their ARNs (needed to subscribe) ─
using var sqs = new AmazonSQSClient(new BasicAWSCredentials(AccessKey, SecretKey),
    new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = Region });

async Task<string> CreateQueueGetArn(string name)
{
    var url = (await sqs.CreateQueueAsync(name)).QueueUrl;
    var attrs = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
    {
        QueueUrl = url,
        AttributeNames = ["QueueArn"],
    });
    return attrs.Attributes["QueueArn"];
}

var billingArn = await CreateQueueGetArn(BillingQueue);
var shippingArn = await CreateQueueGetArn(ShippingQueue);

// ─── 3. Shared state — count what each subscriber handled ────────────────────
var billingCount = 0;
var shippingCount = 0;
var firstBodyLogged = 0;
var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

void OnHandled(string who, IExchange exchange, ref int counter)
{
    // Show the FIRST delivered body verbatim — proof it is the bare payload, not an SNS envelope.
    if (Interlocked.Exchange(ref firstBodyLogged, 1) == 0)
    {
        var body = exchange.In.Body?.ToString() ?? "";
        var envelope = body.TrimStart().StartsWith("{\"Type\"");
        Console.WriteLine($"  [raw check] first delivered body: {body}");
        Console.WriteLine($"  [raw check] looks like an SNS envelope? {(envelope ? "YES (envelope)" : "NO — bare payload ✔")}\n");
    }

    var order = ParseOrderId(exchange.In.Body?.ToString());
    var eventType = Attr(exchange, "eventType") ?? "(none)";
    Console.WriteLine($"  [{who}] order {order}  (eventType attr = {eventType})");

    Interlocked.Increment(ref counter);
    if (Volatile.Read(ref billingCount) >= OrderCount && Volatile.Read(ref shippingCount) >= OrderCount)
        done.TrySetResult();
}

// ─── 4. One consumer route per queue — they run independently ────────────────
ctx.AddRoutes(r =>
{
    r.From(Q(BillingQueue).WaitTimeSeconds(1))
        .RouteId("billing-consumer")
        .Process((exchange, ct) => { OnHandled("billing ", exchange, ref billingCount); return Task.CompletedTask; });

    r.From(Q(ShippingQueue).WaitTimeSeconds(1))
        .RouteId("shipping-consumer")
        .Process((exchange, ct) => { OnHandled("shipping", exchange, ref shippingCount); return Task.CompletedTask; });
});

await ctx.Start();

// ─── 5. Subscribe both queues to the topic WITH raw delivery ─────────────────
// Each sns:// endpoint subscribes one queue (subscribeSnsToSqs takes a single ARN). Both point at the
// same topic, so a single publish fans out to both. .RawMessageDelivery() sets RawMessageDelivery=true
// on each subscription. Starting the producer performs the subscribe; we do it BEFORE publishing.
async Task<IProducer> SubscribeQueue(string queueArn)
{
    var uri = SnsDsl.Topic(Topic)
        .ServiceUrl(ServiceUrl).Region(Region).Credentials(AccessKey, SecretKey)
        .AutoCreateTopic().SubscribeSnsToSqs(queueArn).RawMessageDelivery()
        .Build();
    var endpoint = (SnsEndpoint)new SnsComponent().CreateEndpoint(EndpointUriParser.Parse(uri));
    var producer = endpoint.CreateProducer();
    await producer.Start();   // ← subscribes the queue + enables raw delivery
    return producer;
}

var billingSub = await SubscribeQueue(billingArn);
var shippingSub = await SubscribeQueue(shippingArn);

// A plain publisher endpoint on the same topic (no subscription of its own).
var pubEndpoint = (SnsEndpoint)new SnsComponent().CreateEndpoint(EndpointUriParser.Parse(
    SnsDsl.Topic(Topic).ServiceUrl(ServiceUrl).Region(Region).Credentials(AccessKey, SecretKey)
        .AutoCreateTopic().Build()));
var publisher = pubEndpoint.CreateProducer();
await publisher.Start();

Console.WriteLine($"SqsPubSubDemo — publishing {OrderCount} order events to SNS topic '{Topic}'.");
Console.WriteLine($"Each event fans out to '{BillingQueue}' AND '{ShippingQueue}'.\n");

// ─── 6. Publish the order events (one publish each → reaches both queues) ─────
for (var i = 1; i <= OrderCount; i++)
{
    var payload = JsonSerializer.Serialize(new { orderId = i, amount = i * 100 });
    var exchange = new Exchange(new Message(payload));
    exchange.In.Headers["eventType"] = "OrderPlaced";   // travels as an SNS attribute → SQS attribute (raw)
    await publisher.Process(exchange);
}

// ─── 7. Wait until both subscribers have handled all events ──────────────────
await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30)));

Console.WriteLine();
Console.WriteLine($"billing handled  : {Volatile.Read(ref billingCount)}/{OrderCount}");
Console.WriteLine($"shipping handled : {Volatile.Read(ref shippingCount)}/{OrderCount}");
Console.WriteLine($"One publish → two independent queues. Raw delivery → bare payloads, no SNS envelope.");

// ─── 8. Shutdown ──────────────────────────────────────────────────────────────
await billingSub.Stop();
await shippingSub.Stop();
await publisher.Stop();
await ctx.DisposeAsync();
log.LogInformation("Demo finished, context disposed.");

// ============================================================================
//  Helpers
// ============================================================================

// Read an incoming SQS message attribute exposed as a header (redbSqs.attr.<name>).
static string? Attr(IExchange exchange, string name) =>
    exchange.In.Headers.TryGetValue(SqsHeaders.MessageAttributePrefix + name, out var v) ? v?.ToString() : null;

// Pull orderId out of the bare JSON payload for display.
static string ParseOrderId(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return "?";
    try { return JsonDocument.Parse(json).RootElement.GetProperty("orderId").ToString(); }
    catch { return "?"; }
}
