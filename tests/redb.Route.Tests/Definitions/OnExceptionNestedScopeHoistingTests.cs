using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Transactions;
using Xunit;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Regression tests for route-scoped OnException hoisting (Camel parity):
/// an OnException declared inside a nested scope (Transacted, Traced, ...) must be
/// hoisted to wrap the whole route — and must NOT compile into an inline pipeline
/// step that runs its handler on every healthy exchange.
/// </summary>
public class OnExceptionNestedScopeHoistingTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OnException_InsideTransactedScope_DoesNotRunHandlerOnHealthyExchange()
    {
        var handlerRan = false;
        var endReached = false;

        _context.AddRoutes(r =>
        {
            var scope = r.From("direct://oe-tx-healthy")
                .Transaction(TransactionPolicy.Suppress);
            scope
                .OnException<InvalidOperationException>()
                    .Handled()
                    .Process(_ => handlerRan = true)
                .EndOnException()
                .Process(_ => endReached = true);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://oe-tx-healthy").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("ok")));

        handlerRan.Should().BeFalse("the OnException handler must only run when an exception was thrown");
        endReached.Should().BeTrue();
    }

    [Fact]
    public async Task OnException_InsideTransactedScope_IsHoistedAndHandlesException()
    {
        var handlerRan = false;
        Exception? seenAtHandlerTime = null;

        _context.AddRoutes(r =>
        {
            var scope = r.From("direct://oe-tx-throw")
                .Transaction(TransactionPolicy.Suppress);
            scope
                .OnException<InvalidOperationException>()
                    .Handled()
                    .Process(ex => { handlerRan = true; seenAtHandlerTime = ex.Exception; })
                .EndOnException()
                .Process(_ => throw new InvalidOperationException("boom"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://oe-tx-throw").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("go"));
        await producer.Process(exchange);

        handlerRan.Should().BeTrue("the OnException declared inside the Transacted scope must be hoisted to route level");
        seenAtHandlerTime.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task OnException_InsideTransactedScope_OnWhenPredicateIsRespected()
    {
        var handlerRan = false;

        _context.AddRoutes(r =>
        {
            var scope = r.From("direct://oe-tx-onwhen")
                .Transaction(TransactionPolicy.Suppress);
            scope
                .OnException<InvalidOperationException>()
                    .OnWhen(e => e.Exception?.Message == "match")
                    .Handled()
                    .Process(_ => handlerRan = true)
                .EndOnException()
                .Process(_ => throw new InvalidOperationException("no-match"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://oe-tx-onwhen").CreateProducer();
        await producer.Start();
        var act = () => producer.Process(new Exchange(new Message("go")));

        await act.Should().ThrowAsync<InvalidOperationException>("OnWhen returned false, so the exception must propagate");
        handlerRan.Should().BeFalse();
    }

    [Fact]
    public void OnException_InsideCatchBlock_FailsFastAtCompilation()
    {
        var def = new RouteDefinition();
        var tryScope = ((IRouteDefinition)def.From("direct://oe-ff")).TryCatch();
        tryScope.Catch<Exception>()
            .OnException<InvalidOperationException>()
                .Handled();

        var act = () => def.CreateProcessor(_context);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*route-scoped*");
    }
}
