using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The knobs the markup used to leave to C# only: an onException that decides whether it takes
/// the failure at all and how loudly it retries, a transaction with its own retry and dead
/// letter channel, a threads scope with a bounded queue, a metered scope split by tags.
/// Behaviour where the engine can show it, generated C# where the setting only travels.
/// </summary>
public class RetryAndScopeKnobsTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IProducer> StartAndProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    private void Load(string xml, string? sourceName = null)
        => _context.AddXmlRoutesFromContent(xml, sourceName ?? "<inline>");

    private static string CodeOf(string xml)
        => XmlCodeGenerator.Generate(System.Xml.Linq.XDocument.Parse(xml), "Knobs", "Tests.Generated",
            sourceName: "<inline>");

    // ── onException: when to take the failure ────────────────────────────────

    [Fact]
    public async Task OnException_When_TakesTheFailureOnlyWhileTheConditionHolds()
    {
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-onwhen">
                <from uri="direct://knob-onwhen-in"/>
                <onException exceptions="System.InvalidOperationException" handled="true">
                  <when expr="header.rescue == 'yes'"/>
                  <setHeader name="rescued" value="yes"/>
                </onException>
                <throwException type="System.InvalidOperationException" message="boom"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://knob-onwhen-in");

        var matching = new Exchange(new Message("x"));
        matching.In.Headers["rescue"] = "yes";
        await producer.Process(matching);
        matching.Exception.Should().BeNull("the condition holds, so the handler takes the failure");
        matching.In.Headers["rescued"].Should().Be("yes");

        var other = new Exchange(new Message("x"));
        other.In.Headers["rescue"] = "no";
        var act = () => producer.Process(other);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "the condition fails, so the handler stands aside and the exception surfaces");
    }

    [Fact]
    public async Task OnException_Continued_ResumesTheRouteAfterTheFailingStep()
    {
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-continued">
                <from uri="direct://knob-continued-in"/>
                <onException exceptions="System.InvalidOperationException" continued="true">
                  <setHeader name="noted" value="yes"/>
                </onException>
                <throwException type="System.InvalidOperationException" message="boom"/>
                <setHeader name="after" value="yes"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://knob-continued-in");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.Exception.Should().BeNull("continued suppresses the exception");
        exchange.ExceptionHandled.Should().BeTrue();
        exchange.In.Headers["noted"].Should().Be("yes");
        // Camel's continued(true): the route picks up at the step after the one that failed (implemented 2026-09-21).
        exchange.In.Headers["after"].Should().Be("yes", "continued resumes the route after the failing step");
    }

    /// <summary>
    /// A condition reads a header whose NAME contains dots (the demos namespace their headers,
    /// <c>serials.decision</c>). Written with ${…} the same line is a template, and a template
    /// in a condition position is truthy whatever it renders — so the spelling matters.
    /// </summary>
    [Fact]
    public async Task ACondition_ReadsADottedHeaderName()
    {
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-dotted">
                <from uri="direct://knob-dotted-in"/>
                <choice>
                  <when expr="header.serials.decision == 'Accepted'">
                    <setHeader name="branch" value="accepted"/>
                  </when>
                  <otherwise>
                    <setHeader name="branch" value="other"/>
                  </otherwise>
                </choice>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://knob-dotted-in");

        var accepted = new Exchange(new Message("x"));
        accepted.In.Headers["serials.decision"] = "Accepted";
        await producer.Process(accepted);
        accepted.In.Headers["branch"].Should().Be("accepted");

        var rejected = new Exchange(new Message("x"));
        rejected.In.Headers["serials.decision"] = "Rejected";
        await producer.Process(rejected);
        rejected.In.Headers["branch"].Should().Be("other", "the branch is chosen by the value, not by truthiness");
    }

    // ── onException: the settings that only travel to the engine ─────────────

    [Fact]
    public void OnException_LoggingAndRetrySettings_ReachTheGeneratedCode()
    {
        var code = CodeOf("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-print">
                <from uri="direct://knob-print-in"/>
                <onException exceptions="System.InvalidOperationException" handled="true"
                             maximumRedeliveries="3" redeliveryDelay="00:00:02"
                             useOriginalBody="true" logStackTrace="false" logExhausted="true"
                             retryAttemptedLogLevel="Debug" retriesExhaustedLogLevel="Critical">
                  <retryWhile expr="header.again == 'yes'"/>
                  <setHeader name="rescued" value="yes"/>
                </onException>
                <setBody value="x"/>
              </route>
            </routes>
            """);

        code.Should().Contain("UseOriginalBody()");
        code.Should().Contain("LogStackTrace(false)");
        code.Should().Contain("LogExhausted(true)");
        code.Should().Contain("RetryAttemptedLogLevel(LogLevel.Debug)");
        code.Should().Contain("RetriesExhaustedLogLevel(LogLevel.Critical)");
        code.Should().Contain("RetryWhile(\"header.again == 'yes'\")");
    }

    [Fact]
    public void OnException_AnUnknownLogLevel_IsAPositionedError()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-badlevel">
                <from uri="direct://knob-badlevel-in"/>
                <onException exceptions="System.InvalidOperationException" retryAttemptedLogLevel="Loud">
                  <setBody value="x"/>
                </onException>
              </route>
            </routes>
            """, "bad-level.xml");

        act.Should().Throw<XmlRouteException>().WithMessage("*bad-level.xml(4,*Loud*log level*");
    }

    // ── onException: the three moments a bean can step into ──────────────────

    [Fact]
    public async Task OnException_CallbackBeans_RunAtTheirMoments()
    {
        var log = new List<string>();
        _context.AddToRegistry("occurred", new MarkProcessor("occurred", log));
        _context.AddToRegistry("retry", new MarkProcessor("retry", log));
        _context.AddToRegistry("prepare", new MarkProcessor("prepare", log));
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-callbacks">
                <from uri="direct://knob-callbacks-in"/>
                <onException exceptions="System.InvalidOperationException" handled="true"
                             maximumRedeliveries="2" redeliveryDelay="00:00:00"
                             onExceptionOccurred="#occurred" onRedelivery="#retry"
                             onPrepareFailure="#prepare">
                  <setHeader name="rescued" value="yes"/>
                </onException>
                <throwException type="System.InvalidOperationException" message="boom"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://knob-callbacks-in");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.In.Headers["rescued"].Should().Be("yes");
        log.Count(m => m == "retry").Should().Be(2, "one call before each of the two redeliveries");
        log.Count(m => m == "occurred").Should().Be(3, "the first failure and the two failed retries");
        log.Count(m => m == "prepare").Should().Be(1, "once, before the handler takes over for good");
        log.Last().Should().Be("prepare", "preparation is the last thing before the handler");
    }

    [Fact]
    public void OnException_ACallbackBeanThatIsNotRegistered_IsAPositionedError()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-missing-bean">
                <from uri="direct://knob-missing-bean-in"/>
                <onException exceptions="System.InvalidOperationException" onRedelivery="#nowhere">
                  <setBody value="x"/>
                </onException>
              </route>
            </routes>
            """, "missing-bean.xml");

        act.Should().Throw<XmlRouteException>().WithMessage("*missing-bean.xml(4,*onRedelivery*nowhere*");
    }

    [Fact]
    public void OnException_CallbackBeans_ReachTheGeneratedCode()
    {
        var code = CodeOf("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-callbacks-print">
                <from uri="direct://knob-callbacks-print-in"/>
                <onException exceptions="System.InvalidOperationException"
                             onExceptionOccurred="#occurred" onRedelivery="#retry"
                             onPrepareFailure="#prepare">
                  <setBody value="x"/>
                </onException>
              </route>
            </routes>
            """);

        code.Should().Contain("OnExceptionOccurred(Context!.GetFromRegistry<IProcessor>(\"occurred\")!)");
        code.Should().Contain("OnRedelivery(Context!.GetFromRegistry<IProcessor>(\"retry\")!)");
        code.Should().Contain("OnPrepareFailure(Context!.GetFromRegistry<IProcessor>(\"prepare\")!)");
    }

    /// <summary>A registry bean that only records that it ran.</summary>
    private sealed class MarkProcessor(string mark, List<string> log) : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            log.Add(mark);
            return Task.CompletedTask;
        }
    }

    // ── transaction: its own retry and dead letter channel ───────────────────

    [Fact]
    public void Transaction_RetryAndDeadLetterChannel_ReachTheGeneratedCode()
    {
        var code = CodeOf("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-tx">
                <from uri="direct://knob-tx-in"/>
                <transaction policy="requiresNew" deadLetterChannel="direct://knob-dlq"
                             retryAttempts="4" retryDelay="00:00:05">
                  <setBody value="x"/>
                </transaction>
              </route>
            </routes>
            """);

        code.Should().Contain("DeadLetterChannel(\"direct://knob-dlq\")");
        code.Should().Contain("Retry(4, TimeSpan.FromSeconds(5))");
    }

    [Fact]
    public void Transaction_RetryAttemptsWithoutADelay_IsRefused()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-tx-half">
                <from uri="direct://knob-tx-half-in"/>
                <transaction retryAttempts="4">
                  <setBody value="x"/>
                </transaction>
              </route>
            </routes>
            """, "half-retry.xml");

        act.Should().Throw<XmlRouteException>().WithMessage("*half-retry.xml(4,*retryDelay*");
    }

    // ── threads: a bounded queue in front of the pool ────────────────────────

    [Fact]
    public void Threads_QueueBounds_ReachTheGeneratedCode()
    {
        var code = CodeOf("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-threads">
                <from uri="direct://knob-threads-in"/>
                <threads poolSize="4" maxQueueSize="100" enqueueTimeout="00:00:03">
                  <setBody value="x"/>
                </threads>
              </route>
            </routes>
            """);

        code.Should().Contain("Threads(4)");
        code.Should().Contain("MaxQueueSize(100)");
        code.Should().Contain("EnqueueTimeout(TimeSpan.FromSeconds(3))");
    }

    // ── metered: the metric split by tags ────────────────────────────────────

    [Fact]
    public void Metered_Tags_ReachTheGeneratedCode()
    {
        var code = CodeOf("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-metered">
                <from uri="direct://knob-metered-in"/>
                <metered name="orders">
                  <tag name="partner" fromHeader="partnerId"/>
                  <tag name="kind" expr="${header.kind}"/>
                  <setBody value="x"/>
                </metered>
              </route>
            </routes>
            """);

        code.Should().Contain("TagFromHeader(\"partner\", \"partnerId\")");
        code.Should().Contain("Tag(\"kind\"");
    }

    [Fact]
    public void Metered_ATagWithNoSource_IsRefused()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="knob-metered-bad">
                <from uri="direct://knob-metered-bad-in"/>
                <metered name="orders">
                  <tag name="partner"/>
                  <setBody value="x"/>
                </metered>
              </route>
            </routes>
            """, "bad-tag.xml");

        act.Should().Throw<XmlRouteException>().WithMessage("*bad-tag.xml(5,*fromHeader*expr*");
    }
}
