using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="TryCatchProcessor"/> and <see cref="CatchClause"/>.</summary>
public class TryCatchProcessorTests
{
    /// <summary>Body executes normally when no exception.</summary>
    [Fact]
    public async Task Process_NoException_BodyExecutes()
    {
        var executed = false;
        var tryCatch = new TryCatchProcessor(new DelegateProcessor(_ => executed = true));

        await tryCatch.Process(new Exchange());

        executed.Should().BeTrue();
    }

    /// <summary>Matching catch clause handles exception.</summary>
    [Fact]
    public async Task Process_MatchingCatch_HandlesException()
    {
        var handled = false;
        var tryCatch = new TryCatchProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException("fail")))
            .Catch<InvalidOperationException>(new DelegateProcessor(ex =>
            {
                handled = true;
                ex.Exception.Should().BeOfType<InvalidOperationException>();
            }));

        var exchange = new Exchange();
        await tryCatch.Process(exchange);

        handled.Should().BeTrue();
        exchange.ExceptionHandled.Should().BeTrue();
        exchange.Exception.Should().BeOfType<InvalidOperationException>();
    }

    /// <summary>Non-matching clause rethrows.</summary>
    [Fact]
    public async Task Process_NoMatchingCatch_Rethrows()
    {
        var tryCatch = new TryCatchProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException("fail")))
            .Catch<ArgumentException>(new DelegateProcessor(_ => { }));

        var act = () => tryCatch.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Finally block always executes — on success.</summary>
    [Fact]
    public async Task Process_Finally_ExecutesOnSuccess()
    {
        var finallyRan = false;
        var tryCatch = new TryCatchProcessor(new DelegateProcessor(_ => { }))
            .SetFinally(new DelegateProcessor(_ => finallyRan = true));

        await tryCatch.Process(new Exchange());

        finallyRan.Should().BeTrue();
    }

    /// <summary>Finally block executes on handled exception.</summary>
    [Fact]
    public async Task Process_Finally_ExecutesOnHandledException()
    {
        var finallyRan = false;
        var tryCatch = new TryCatchProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException()))
            .Catch<InvalidOperationException>(new DelegateProcessor(_ => { }))
            .SetFinally(new DelegateProcessor(_ => finallyRan = true));

        await tryCatch.Process(new Exchange());

        finallyRan.Should().BeTrue();
    }

    /// <summary>Finally block executes on unhandled exception.</summary>
    [Fact]
    public async Task Process_Finally_ExecutesOnUnhandledException()
    {
        var finallyRan = false;
        var tryCatch = new TryCatchProcessor(
                new DelegateProcessor(_ => throw new InvalidOperationException()))
            .SetFinally(new DelegateProcessor(_ => finallyRan = true));

        var act = () => tryCatch.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        finallyRan.Should().BeTrue();
    }

    /// <summary>CatchClause.For creates typed clause.</summary>
    [Fact]
    public void CatchClause_For_CreatesTyped()
    {
        var clause = CatchClause.For<ArgumentException>(new DelegateProcessor(_ => { }));

        clause.ExceptionType.Should().Be(typeof(ArgumentException));
        clause.Matches(new ArgumentException()).Should().BeTrue();
        clause.Matches(new InvalidOperationException()).Should().BeFalse();
    }

    /// <summary>CatchClause with When predicate filters further.</summary>
    [Fact]
    public void CatchClause_WithWhen_Filters()
    {
        var clause = CatchClause.For<ArgumentException>(
            new DelegateProcessor(_ => { }),
            ex => ex.ParamName == "test");

        clause.Matches(new ArgumentException("msg", "test")).Should().BeTrue();
        clause.Matches(new ArgumentException("msg", "other")).Should().BeFalse();
    }

    /// <summary>OperationCanceledException is never caught.</summary>
    [Fact]
    public async Task Process_OperationCanceled_NeverCaught()
    {
        var tryCatch = new TryCatchProcessor(
                new DelegateProcessor(_ => throw new OperationCanceledException()))
            .Catch<Exception>(new DelegateProcessor(_ => { }));

        var act = () => tryCatch.Process(new Exchange());
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Invalid exception type in CatchClause throws.</summary>
    [Fact]
    public void CatchClause_InvalidType_Throws()
    {
        var act = () => new CatchClause(typeof(string), new DelegateProcessor(_ => { }));
        act.Should().Throw<ArgumentException>();
    }
}
