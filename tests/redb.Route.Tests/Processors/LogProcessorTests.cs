using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="LogProcessor"/>.</summary>
public class LogProcessorTests
{
    /// <summary>Static message is logged.</summary>
    [Fact]
    public async Task Process_StaticMessage_Logged()
    {
        var logger = Substitute.For<ILogger>();
        var processor = new LogProcessor(logger, "hello route");

        await processor.Process(new Exchange());

        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>Dynamic message receives the exchange.</summary>
    [Fact]
    public async Task Process_DynamicMessage_UsesExchange()
    {
        var logger = Substitute.For<ILogger>();
        var processor = new LogProcessor(logger, ex => $"body={ex.In.Body}");

        await processor.Process(new Exchange(new Message("test")));

        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>Custom log level is used.</summary>
    [Fact]
    public async Task Process_CustomLevel_Used()
    {
        var logger = Substitute.For<ILogger>();
        var processor = new LogProcessor(logger, "warn!", LogLevel.Warning);

        await processor.Process(new Exchange());

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>Null logger throws.</summary>
    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new LogProcessor(null!, "msg");
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Null message func throws.</summary>
    [Fact]
    public void Constructor_NullMessageFunc_Throws()
    {
        var logger = Substitute.For<ILogger>();
        var act = () => new LogProcessor(logger, (Func<IExchange, string>)null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
