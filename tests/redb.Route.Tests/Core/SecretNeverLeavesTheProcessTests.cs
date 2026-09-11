using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Telemetry;
using redb.Route.TestKit;

namespace redb.Route.Tests.Core;

/// <summary>
/// The end-to-end regression `docs/SECURITY_URI_REDACTION_PLAN.md` asks for, and the evidence behind
/// BR-3 in `redb.Tsak/docs/BOUNDARIES_AND_FOLLOWUPS.md` §1: a route whose consumer URI carries secrets
/// in every shape (query parameter, connector-specific name, userinfo password) is compiled, started
/// and stopped, and **no** secret may appear on any surface that leaves the process — log lines, span
/// tags, metric labels, the route DTO the dashboard reads, or the health-check payload.
/// <para>
/// Each secret is unique to this run, so the assertions are immune to what other tests do in parallel:
/// nobody else can emit this string, and a leak cannot be masked by someone else's silence.
/// </para>
/// </summary>
public class SecretNeverLeavesTheProcessTests
{
    private static readonly string Nonce = Guid.NewGuid().ToString("N")[..8];
    private static readonly string QuerySecret = $"pw-query-{Nonce}";
    private static readonly string LdapSecret = $"pw-ldap-{Nonce}";        // bindPassword: the LDAP leak of the audit
    private static readonly string AwsSecret = $"tok-aws-{Nonce}";         // sessionToken: the S3/SQS leak of the audit
    private static readonly string UserInfoSecret = $"pw-userinfo-{Nonce}";

    private static string[] AllSecrets => [QuerySecret, LdapSecret, AwsSecret, UserInfoSecret];

    private static string RouteUri =>
        $"direct://user:{UserInfoSecret}@orders-{Nonce}" +
        $"?password={QuerySecret}&bindPassword={LdapSecret}&sessionToken={AwsSecret}&acks=all";

    [Fact]
    public async Task NoSurfaceThatLeavesTheProcessCarriesASecret()
    {
        var log = new CapturingLogs();
        using var loggerFactory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(log); });

        var spanTags = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RouteActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spanTags) spanTags.AddRange(a.Tags.Select(t => $"{t.Key}={t.Value}")); },
        };
        ActivitySource.AddActivityListener(listener);

        var metricTags = new List<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == RouteMetrics.MeterName) l.EnableMeasurementEvents(instrument); },
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) => Record(tags));
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) => Record(tags));
        meterListener.Start();

        string dtoFromUri;
        string healthPayload;

        // Telemetry and metrics on: without them a plain route emits no span and no measurement, and the
        // two surfaces would be asserted against an empty collection — a guard that cannot fail.
        var options = new RouteEngineOptions { EnableTelemetry = true, EnableMetrics = true };
        await using (var ctx = new RouteContext(loggerFactory: loggerFactory, options: options))
        {
            // Two routes on purpose: one that succeeds and one that fails. A failing route is the case
            // where the span carries an error status and an exception event, and where the endpoint URI
            // is most likely to be rendered into a diagnostic.
            ctx.AddRoutes(b =>
            {
                b.From(RouteUri).To("mock://out");
                b.From($"direct://failing-{Nonce}?password={QuerySecret}")
                    .Process(_ => throw new InvalidOperationException("route failed on purpose"));
            });
            await ctx.Start();

            await ctx.SendBody(RouteUri, "payload");
            var failing = () => ctx.SendBody($"direct://failing-{Nonce}?password={QuerySecret}", "payload");
            await failing.Should().ThrowAsync<InvalidOperationException>();

            dtoFromUri = ctx.Routes.Should().HaveCount(2).And.Subject
                .Single(r => r.FromUri.Contains($"orders-{Nonce}")).FromUri;

            var health = await new RouteHealthCheck(ctx).CheckHealthAsync(new HealthCheckContext());
            healthPayload = JsonSerializer.Serialize(health.Data);

            await ctx.Stop();
        }

        meterListener.Dispose();

        // Positive controls first. Every surface below is asserted with a negative ("no secret"), and a
        // negative alone is satisfied by a surface that says nothing at all — which would be a useless
        // guard. So each one is first shown to actually carry this route, redacted.
        dtoFromUri.Should().Contain($"orders-{Nonce}", "the route must stay recognisable after redaction");
        dtoFromUri.Should().Contain(EndpointUri.Redacted);
        string.Join("\n", log.Lines).Should().Contain($"orders-{Nonce}", "the log does mention this route");
        string.Join("\n", spanTags).Should().Contain(Nonce, "spans do carry this route's endpoint");
        string.Join("\n", metricTags).Should().Contain(Nonce, "metric labels do carry this route");
        healthPayload.Should().Contain($"orders-{Nonce}", "the health payload does list this route");

        // The failing route ran, so the error path of the span (status + exception event) was exercised.
        string.Join("\n", spanTags).Should().Contain($"failing-{Nonce}");

        foreach (var secret in AllSecrets)
        {
            string.Join("\n", log.Lines).Should().NotContain(secret, "a log line must never carry a secret");
            string.Join("\n", spanTags).Should().NotContain(secret, "a span tag must never carry a secret");
            string.Join("\n", metricTags).Should().NotContain(secret, "a metric label must never carry a secret");
            dtoFromUri.Should().NotContain(secret, "the route DTO is what the dashboard renders");
            healthPayload.Should().NotContain(secret, "the health payload is served over HTTP");
        }

        void Record(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var rendered = new List<string>();
            foreach (var tag in tags) rendered.Add($"{tag.Key}={tag.Value}");
            lock (metricTags) metricTags.AddRange(rendered);
        }
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }

        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            {
                var line = formatter(state, ex) + (ex is null ? "" : " | " + ex);
                lock (owner._lines) owner._lines.Add(line);
            }
        }
    }
}
