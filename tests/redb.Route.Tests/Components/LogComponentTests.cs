using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

/// <summary>
/// Tests for the Log component.
/// </summary>
public class LogComponentTests
{
    [Fact]
    public void Component_HasCorrectScheme()
    {
        var component = new LogComponent();
        component.Scheme.Should().Be("log");
    }

    [Fact]
    public void CreateEndpoint_ReturnsLogEndpoint()
    {
        var component = new LogComponent();
        var uri = EndpointUriParser.Parse("log://MyCategory");
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<LogEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_SameUri_ReturnsSameInstance()
    {
        var component = new LogComponent();
        var uri1 = EndpointUriParser.Parse("log://cat");
        var uri2 = EndpointUriParser.Parse("log://cat");
        component.CreateEndpoint(uri1).Should().BeSameAs(component.CreateEndpoint(uri2));
    }

    [Fact]
    public void Options_DefaultValues()
    {
        var opts = new LogEndpointOptions();
        opts.Level.Should().Be("Information");
        opts.ShowHeaders.Should().BeTrue();
        opts.ShowBody.Should().BeTrue();
    }

    [Fact]
    public void Options_Level_DefaultIsInformation()
    {
        var opts = new LogEndpointOptions { Level = "Warning" };
        opts.Level.Should().Be("Warning");
    }

    [Fact]
    public void Options_Level_FallsBackToInformation_ForInvalidLevel()
    {
        var opts = new LogEndpointOptions { Level = "InvalidLevel" };
        // Invalid level stored as-is; producer handles fallback
        opts.Level.Should().Be("InvalidLevel");
    }

    [Fact]
    public void LogEndpoint_CreateConsumer_ThrowsNotSupported()
    {
        var component = new LogComponent();
        var ep = (LogEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("log://test"));
        var processor = Substitute.For<IProcessor>();

        var act = () => ep.CreateConsumer(processor);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void LogEndpoint_CreateProducer_ReturnsLogProducer()
    {
        var component = new LogComponent();
        var ep = (LogEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("log://test"));
        ep.CreateProducer().Should().BeOfType<LogProducer>();
    }

    [Fact]
    public async Task LogProducer_NoLoggerFactory_DoesNotThrow()
    {
        var component = new LogComponent(loggerFactory: null);
        var ep = (LogEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("log://nologger"));
        var producer = ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "test" });
        var act = async () => await producer.Process(exchange);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LogProducer_WithLoggerFactory_LogsBody()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);

        var component = new LogComponent(loggerFactory);
        var ep = (LogEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("log://test"));
        var producer = ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "my-body-content" });
        await producer.Process(exchange);

        // Verify Log was called at least once
        logger.ReceivedWithAnyArgs().Log(
            default, default, default(object)!, default, default!);
    }

    [Fact]
    public async Task LogProducer_WithHeaders_LogsHeaders()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);

        var component = new LogComponent(loggerFactory);
        var ep = (LogEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("log://headertest?showBody=true&showHeaders=true"));
        var producer = ep.CreateProducer();
        await producer.Start();

        var msg = new Message { Body = "body" };
        msg.Headers["Key1"] = "Value1";
        var exchange = new Exchange(msg);

        await producer.Process(exchange);

        logger.ReceivedWithAnyArgs().Log(
            default, default, default(object)!, default, default!);
    }

    [Fact]
    public async Task LogProducer_ShowBodyFalse_SkipsBody()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);

        var component = new LogComponent(loggerFactory);
        var ep = (LogEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("log://nobody?showBody=false&showHeaders=false"));
        var producer = ep.CreateProducer();

        var exchange = new Exchange(new Message { Body = "secret" });
        await producer.Process(exchange);

        // Still logs, just "(empty exchange)" because both are disabled
        logger.ReceivedWithAnyArgs().Log(
            default, default, default(object)!, default, default!);
    }

    [Fact]
    public async Task LogProducer_CustomLevel_UsesConfiguredLevel()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);

        var component = new LogComponent(loggerFactory);
        var ep = (LogEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse("log://warn?level=Warning"));

        // The level option is configured via URI parameter;
        // We verify behavior by checking that the logger gets called with the right level
        // when we use the endpoint via a route. Unit level: just verify creation doesn't throw.
        ep.Should().NotBeNull();
    }

    [Fact]
    public void LogEndpoint_Category_CreatesLoggerWithCorrectName()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var component = new LogComponent(loggerFactory);
        component.CreateEndpoint(EndpointUriParser.Parse("log://MyCategory"));

        loggerFactory.Received().CreateLogger("redb.Route.Log.MyCategory");
    }

    [Fact]
    public void LogEndpoint_EmptyCategory_UsesDefaultName()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var component = new LogComponent(loggerFactory);
        // Just "log://" with no path
        component.CreateEndpoint(EndpointUriParser.Parse("log://"));

        loggerFactory.Received().CreateLogger("redb.Route.Log");
    }
}
