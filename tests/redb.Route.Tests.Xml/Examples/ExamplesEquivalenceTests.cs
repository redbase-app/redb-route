using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Aggregation;
using redb.Route.Core;
using redb.Route.Diagnostics;
using redb.Route.Xml;
using static redb.Route.Core.RouteBuilder;

namespace redb.Route.Tests.Xml.Examples;

/// <summary>
/// Route-XML Ф3 §3: for each Ф0.3 example, the definition tree loaded from XML is IDENTICAL to
/// the tree built by the post-V4 fluent C# — proven by comparing <see cref="RouteDescriber"/>
/// output byte-for-byte. This is the Р1 promise made executable: one engine, two spellings.
/// </summary>
public class ExamplesEquivalenceTests
{
    private static string DescribeExample(string fileName)
    {
        using var context = ExampleHarness.NewContext();
        context.AddXmlRoutes(Path.Combine(ExampleHarness.Dir, fileName));
        return RouteDescriber.Describe(ExampleHarness.Definitions(context));
    }

    private static string DescribeTwin(Action<RouteContext> addRoutes)
    {
        using var context = new RouteContext();
        addRoutes(context);
        return RouteDescriber.Describe(ExampleHarness.Definitions(context));
    }

    [Fact]
    public void ScopeDiag_XmlAndCSharp_AreTheSameTree()
    {
        var twin = DescribeTwin(ctx => ctx.AddRoutes(r =>
            r.From("timer://scope-diag?period=60000&delay=10000")
                .RouteId("demo-scope-diag")
                .Description("Диагностика скоупов параллельного сплиттера")
                .AutoStart(false)
                .Log("[SCOPE-DIAG] Starting parallel split: 50 items, maxDop=5")
                .To("bean:#scopeDiag?method=Reset")
                .Split(Expr("body")).ParallelProcessing().MaxParallelism(5)
                    .To("bean:#scopeDiag?method=ProbeItem")
                .EndSplit()
                .To("bean:#scopeDiag?method=Summarize")));

        DescribeExample("scope-diag.route.xml").Should().Be(twin);
    }

    [Fact]
    public void DeepDslShowcase_XmlAndCSharp_AreTheSameTree()
    {
        var twin = DescribeTwin(ctx => ctx.AddRoutes(r =>
        {
            var choice1 = r.From("direct://demo-choice-richlog")
                .RouteId("demo-choice-richlog")
                .Description("Ветвление по типу тела с богатым логом")
                .SetHeader("step", "start")
                .Choice();
            var whenList = choice1.When(new IsStringListDouble()).Description("Тело - коллекция строк")
                .SetHeader("branch", "list");
            whenList.Log(LogLevel.Information).ShowRouteId()
                .Message("opening list branch").Header("branch").Property("trace")
                .EndLog();
            var listSplit = whenList.Split(Expr("body"));
            listSplit.SetBody(Expr("${upper(body)}"))
                .Log("[tmpl]   item=${body} branch=${header.branch} [${routeId}]")
                .Log(LogLevel.Information).ShowRouteId()
                    .Message("[rich-tmpl]   item=${body}").Header("branch").Property("item-index")
                .EndLog();
            listSplit.EndSplit();
            whenList.Log("list branch done [${routeId}]");
            choice1.When(new IsNonEmptyStringDouble()).Description("Тело - непустая строка")
                .SetHeader("branch", "string")
                .SetBody(Expr("STR:${body}"));
            var otherwise1 = choice1.Otherwise()
                .SetHeader("branch", "fallback")
                .SetBody("FALLBACK");
            var route1 = choice1.EndChoice();
            route1.Log(LogLevel.Information).ShowRouteId().Message("route complete").EndLog();

            var choice2 = r.From("direct://demo-trycatch-richlog")
                .RouteId("demo-trycatch-richlog")
                .Description("TryCatch с богатым логом в catch")
                .Choice();
            var when2 = choice2.When(new IsNonEmptyStringDouble());
            var tryCatch = when2.TryCatch();
            tryCatch.ThrowException(typeof(InvalidOperationException), "boom");
            var handler = tryCatch.Catch(typeof(InvalidOperationException));
            handler.Log("[tmpl]   caught: ${exception.type} - ${exception.message} [${routeId}]");
            handler.Log(LogLevel.Warning).ShowRouteId()
                .Message("[rich-tmpl]   ${exception.type}: ${exception.message}")
                .EndLog();
            handler.SetHeader("caught", "true");
            handler.EndCatch();
            tryCatch.EndTryCatch();
            choice2.EndChoice();

            var choice3 = r.From("direct://demo-nested-scopes")
                .RouteId("demo-nested-scopes")
                .Description("Вложенные скоупы: choice - when - split - richLog")
                .Choice();
            var when3 = choice3.When("true");
            var split3 = when3.Split(Expr("body"));
            split3.Log(LogLevel.Information).Message("inside").EndLog();
            split3.Log("item=${body}");
            split3.EndSplit();
            var route3 = choice3.EndChoice();
            route3.SetHeader("after-close", "ok")
                .Log("nested scopes demo done");
        }));

        DescribeExample("deep-dsl-showcase.route.xml").Should().Be(twin);
    }

    [Fact]
    public void Eip_XmlAndCSharp_AreTheSameTree()
    {
        var twin = DescribeTwin(ctx => ctx.AddRoutes(r =>
        {
            r.From("timer://agg-source?period=2000&repeatCount=9")
                .RouteId("demo-aggregator")
                .Description("Агрегатор: 3 события одной партии в одно")
                .To("bean:#demoStamps?method=SetBatchIdAndBody")
                .Log("[AGG] Event: batchId=${header.batchId}, body=${body}")
                .Aggregate("${header.batchId}", AggregationStrategies.ByName("concat: + "), completionSize: 3)
                    .Log("[AGG] Aggregated 3 events: ${body}")
                .EndAggregate();

            r.From("direct://demo-multicast")
                .RouteId("demo-multicast")
                .Description("Широковещание на три endpoint-а")
                .Log("[MCAST] Broadcasting to 3 endpoints...")
                .Multicast().ParallelProcessing()
                    .To("direct://mcast-a")
                    .To("direct://mcast-b")
                    .To("direct://mcast-c")
                .EndMulticast()
                .Log("[MCAST] All endpoints received the message");

            r.From("direct://mcast-a").RouteId("demo-mcast-a")
                .Log("[MCAST-A] Received copy: ${body}")
                .SetHeader("mcast.a", "done");
            r.From("direct://mcast-b").RouteId("demo-mcast-b")
                .Log("[MCAST-B] Received copy: ${body}")
                .Delay(TimeSpan.FromMilliseconds(100))
                .Log("[MCAST-B] Done after 100ms delay");
            r.From("direct://mcast-c").RouteId("demo-mcast-c")
                .Log("[MCAST-C] Received copy: ${body}")
                .SetHeader("mcast.c", "done");

            r.From("direct://demo-recipient-list")
                .RouteId("demo-recipient-list")
                .Description("Динамический список получателей")
                .Log("[RCPT] Routing to dynamic recipients: targets=${header.targets}")
                .RecipientList("${header.targets}", ",", parallelProcessing: true)
                .Log("[RCPT] All recipients processed");

            r.From("direct://rcpt-a").RouteId("demo-rcpt-a").Log("[RCPT-A] Got message: ${body}");
            r.From("direct://rcpt-b").RouteId("demo-rcpt-b").Log("[RCPT-B] Got message: ${body}");

            r.From("direct://demo-dynamic-router")
                .RouteId("demo-dynamic-router")
                .Description("Пошаговая маршрутизация по состоянию")
                .SetProperty("router.step", Expr("0"))
                .Log("[DROUTER] Starting dynamic routing...")
                .DynamicRouter("property.router.step == 0 ? 'direct://drouter-validate' : (property.router.step == 1 ? 'direct://drouter-transform' : (property.router.step == 2 ? 'direct://drouter-store' : null))")
                .Log("[DROUTER] Complete, went through ${property.router.step} hops");

            r.From("direct://drouter-validate").RouteId("demo-drouter-validate")
                .Log("[DROUTER] Step 1: Validate")
                .SetHeader("validated", "true")
                .SetProperty("router.step", Expr("${property.router.step + 1}"));
            r.From("direct://drouter-transform").RouteId("demo-drouter-transform")
                .Log("[DROUTER] Step 2: Transform")
                .SetBody(Expr("${upper(body)}"))
                .SetProperty("router.step", Expr("${property.router.step + 1}"));
            r.From("direct://drouter-store").RouteId("demo-drouter-store")
                .Log("[DROUTER] Step 3: Store - body=${body}")
                .SetProperty("router.step", Expr("${property.router.step + 1}"));

            r.From("direct://demo-loop")
                .RouteId("demo-loop")
                .Description("Цикл: тело прирастает на каждой итерации")
                .Log("[LOOP] Looping 3 times...")
                .Loop(3)
                    .Log("[LOOP]   iteration...")
                    .SetBody(Expr("${body + '-loop'}"))
                .EndLoop()
                .Log("[LOOP] Result: ${body}");

            r.From("direct://demo-resequencer")
                .RouteId("demo-resequencer")
                .Description("Восстановление порядка по seqNum")
                .Log("[RESEQ] Received seq=${header.seqNum}: ${body}")
                .Resequence("${header.seqNum}", 5, TimeSpan.FromSeconds(3))
                    .Log("[RESEQ] Delivered in order: seq=${header.seqNum}, body=${body}")
                .EndResequence();

            r.From("direct://demo-enrich")
                .RouteId("demo-enrich")
                .Description("Обогащение: ответ в заголовок enriched.data")
                .Log("[ENRICH] Original body: ${body}")
                .Enrich("direct://enrichment-source", AggregationStrategies.ByName("intoHeader:enriched.data"))
                .Log("[ENRICH] Enriched: body=${body}, enriched.data=${header.enriched.data}");

            r.From("direct://enrichment-source").RouteId("demo-enrichment-source")
                .Log("[ENRICH-SRC] Providing enrichment data")
                .SetBody(Expr("extra-info-for-${header.traceId ?? 'unknown'}"))
                .Log("[ENRICH-SRC] Returning: ${body}");

            r.From("direct://demo-idempotent")
                .RouteId("demo-idempotent")
                .Description("Дедупликация по messageId")
                .Log("[IDEMP] messageId=${header.messageId}")
                .IdempotentConsumer("${header.messageId}", "idempotentRepo", skipDuplicate: true)
                    .Log("[IDEMP] First time seeing this message, processing...")
                    .SetHeader("idempotent.processed", "true")
                    .Log("[IDEMP] Done")
                .EndIdempotentConsumer();

            var throttle = r.From("direct://demo-throttle")
                .RouteId("demo-throttle")
                .Description("Ограничение частоты: 5/сек")
                .Log("[THROTTLE] Incoming request...")
                .Throttle(5);
            throttle.Period(TimeSpan.FromSeconds(1));
            throttle
                .Log("[THROTTLE] Passed rate limiter (5/sec)")
                .Log("[THROTTLE] Done")
                .EndThrottle();
        }));

        DescribeExample("eip.route.xml").Should().Be(twin);
    }

    [Fact]
    public void MainPipeline_XmlAndCSharp_AreTheSameTree()
    {
        var schemaJson = File.ReadAllText(Path.Combine(ExampleHarness.Dir, "resources", "message.schema.json"));
        var twin = DescribeTwin(ctx => ctx.AddRoutes(r =>
        {
            var onException = r.OnException(typeof(Exception));
            onException.Description("Глобальный обработчик всех демо-маршрутов");
            onException.Handled()
                .MaximumRedeliveries(2)
                .RedeliveryDelay(TimeSpan.FromSeconds(1))
                .UseExponentialBackOff()
                .BackOffMultiplier(2.0)
                .Log("[GLOBAL-ERR] route=${routeId}: ${exception.type} - ${exception.message}, body=${body}", LogLevel.Error)
                .Log("[GLOBAL-ERR] ${exception.message} - handled after retries");

            var entry = r.From("http:{{demo.http.listen:0.0.0.0:5088}}/api/demo?inOut=true")
                .RouteId("demo-http-entry")
                .Description("HTTP-вход демо-конвейера")
                .ConvertBody(typeof(string))
                .Throttle(10)
                    .Log("[1-HTTP] Received: body=${body}, contentType=${contentType}")
                    .To("bean:#stamps?method=NewTraceId")
                    .SetHeader("startedAt", Expr("${dateformat(now(), 'o')}"))
                    .Log("[1-HTTP] traceId=${header.traceId}, mode=${header.mode}, priority=${header.priority}")
                    .ValidateJsonSchema(schemaJson, throwOnFailure: true)
                    .Log("[1-HTTP] JSON schema valid")
                    .IdempotentConsumer("${header.traceId}", "idempotentRepo")
                        .Log("[1-HTTP] Not a duplicate")
                        .To("direct://pipeline")
                    .EndIdempotentConsumer();
            entry.EndThrottle();

            var pipeline = r.From("direct://pipeline")
                .RouteId("demo-pipeline")
                .Description("Основной конвейер обработки")
                .Log("[2-PIPE] Pipeline started, traceId=${header.traceId}");
            var filter = pipeline.Filter("length(body) > 0")
                .Log("[2-PIPE] Body is non-empty");
            var choice = filter.Choice();
            var whenFull = choice.When("header.mode == 'full'")
                .SetHeader("fastTrack", Expr("header.priority == 'high' ? 'true' : 'false'"));
            var normalize = whenFull.TryCatch();
            normalize
                .Filter("!(header.priority == 'high' || header.priority == 'normal' || header.priority == 'low')")
                    .ThrowException(typeof(InvalidOperationException), "unknown priority")
                .EndFilter();
            normalize.SetHeader("stamp.dsl.priority", Expr("${header.priority}"));
            var caught = normalize.Catch(typeof(InvalidOperationException));
            caught.Log(LogLevel.Warning).ShowRouteId()
                .Message("priority normalization failed: ${exception.type}")
                .Header("priority")
                .EndLog();
            caught.SetHeader("stamp.dsl.priority", "unknown")
                .SetHeader("stamp.dsl.caught", "true");
            caught.EndCatch();
            normalize.EndTryCatch();
            whenFull.SetHeader("stamp.dsl", "full-branch");
            choice.When("header.mode == 'short'")
                .SetHeader("fastTrack", "false")
                .SetHeader("stamp.dsl", "short-branch");
            choice.Otherwise()
                .SetHeader("mode", "default")
                .SetHeader("fastTrack", "false")
                .SetHeader("stamp.dsl", "default-branch");
            choice.EndChoice();

            filter.Log(LogLevel.Information).ShowRouteId()
                .Message("pipeline branch decided")
                .Header("mode").Header("priority").Header("fastTrack")
                .Header("stamp.dsl").Header("stamp.dsl.priority")
                .EndLog();
            filter.Log("[2-PIPE] mode=${header.mode}, fastTrack=${header.fastTrack}");

            filter.Traced("broker-roundtrips")
                .Log("[3-RABBIT] Sending to RabbitMQ RPC...")
                .To("{{demo.rabbit.producer}}")
                .Log("[3-RABBIT] stamp.rabbit=${header.stamp.rabbit}")
                .Log("[4-AMQP] Sending to AMQP/Artemis RPC...")
                .To("{{demo.amqp.producer}}")
                .Log("[4-AMQP] stamp.amqp=${header.stamp.amqp}")
                .Log("[5-GRPC] Sending to gRPC server...")
                .To("{{demo.grpc.producer}}")
                .Log("[5-GRPC] stamp.grpc=${header.stamp.grpc}")
                .Log("[6-WMQ] Sending to IBM MQ RPC...")
                .To("{{demo.wmq.rpc.producer}}")
                .Log("[6-WMQ] stamp.wmq=${header.stamp.wmq}")
                .EndTraced();

            filter.ConvertBody(typeof(string))
                .Log("[5-GRPC] Body converted to string: ${body}")
                .Log("[5b-DVM] Calling direct-vm://enricher...")
                .To("direct-vm://enricher")
                .Log("[5b-DVM] stamp.vm=${header.stamp.vm}")
                .SetProperty("originalBody", Expr("${body}"));

            var metered = filter.Metered("sql-operations");
            metered.BeginTransaction();
            metered.Log("[6-TX] Transaction opened")
                .Log("[6-SQL] INSERT demo_log: traceId=${header.traceId}, mode=${header.mode}")
                .To("{{demo.sql.insert}}")
                .Log("[6-SQL] Insert complete")
                .Log("[6b-SQL] SELECT last 5 rows from demo_log")
                .To("{{demo.sql.select}}")
                .Split(Expr("body"))
                    .Log("[6b-SQL]  row: id=${body['id']}, msg=${body['message']}, status=${body['status']}")
                .EndSplit();
            metered.CommitTransaction();
            metered.Log("[6-TX] Transaction committed");
            metered.EndMetered();

            filter.SetBody(Expr("${property.originalBody ?? body}"))
                .Log("[6-RESTORE] Body restored: ${body}")
                .WireTap("{{demo.kafka.wiretap}}")
                .Log("[7-TAP] Kafka: demo-audit topic")
                .WireTap("direct://demo-file-audit")
                .Log("[7-TAP] File: output/traceId.json")
                .WireTap("vm://audit-log")
                .Log("[7-TAP] VM: async audit-log queue")
                .WireTap("direct://demo-redis-pub")
                .WireTap("direct://demo-mqtt-pub")
                .WireTap("direct://demo-wmq-pub")
                .WireTap("direct://demo-seda-send")
                .Log("[8-DONE] Pipeline complete for traceId=${header.traceId}")
                .SetHeader("Content-Type", "application/json")
                .To("bean:#responseBuilder?method=Build");
            filter.EndFilter();

            r.From("direct://demo-file-audit").RouteId("demo-file-audit")
                .To("bean:#responseBuilder?method=Build")
                .To("{{demo.file.wiretap}}");

            r.From("{{demo.rabbit.consumer}}").RouteId("demo-rabbit-worker")
                .Log("[RABBIT-W] Received: ${body}")
                .SetHeader("stamp.rabbit", Expr("ok:${dateformat(now(), 'HH:mm:ss.fff')}"))
                .Log("[RABBIT-W] Stamped, replying");
            r.From("{{demo.amqp.consumer}}").RouteId("demo-amqp-worker")
                .Log("[AMQP-W] Received: ${body}")
                .SetHeader("stamp.amqp", Expr("ok:${dateformat(now(), 'HH:mm:ss.fff')}"))
                .Log("[AMQP-W] Stamped, replying");
            r.From("{{demo.grpc.consumer}}").RouteId("demo-grpc-worker")
                .Log("[GRPC-W] Received: ${body}")
                .SetHeader("stamp.grpc", Expr("ok:${dateformat(now(), 'HH:mm:ss.fff')}"))
                .Log("[GRPC-W] Stamped, replying");
            r.From("{{demo.wmq.rpc.consumer}}").RouteId("demo-wmq-worker")
                .Log("[WMQ-W] Received: ${body}")
                .SetHeader("stamp.wmq", Expr("ok:${dateformat(now(), 'HH:mm:ss.fff')}"))
                .Log("[WMQ-W] Stamped, replying");

            r.From("direct-vm://enricher").RouteId("demo-vm-enricher")
                .Log("[DVM-W] Enriching: traceId=${header.traceId}")
                .SetHeader("stamp.vm", Expr("enriched:${dateformat(now(), 'HH:mm:ss.fff')}"))
                .Log("[DVM-W] Enriched, returning");

            r.From("vm://audit-log?concurrentConsumers=2").RouteId("demo-vm-audit")
                .Log("[VM-AUDIT] Audit event: traceId=${header.traceId}, mode=${header.mode}")
                .SetHeader("audit.processed", Expr("${header.traceId}:${header.mode}:${dateformat(now(), 'HH:mm:ss.fff')}"))
                .Log("[VM-AUDIT] Processed: ${header.audit.processed}");
        }));

        DescribeExample("main-pipeline.route.xml").Should().Be(twin);
    }
}
