using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Reference;

/// <summary>
/// Reference / canonical DSL examples for the <c>DoTry</c> / <c>DoCatch</c> /
/// <c>Finally</c> Enterprise Integration Pattern (Camel <c>doTry/doCatch/doFinally</c>).
///
/// Every try body, catch handler and finally block is a real multi-step pipeline.
/// <c>TryCatchProcessor</c> sets <c>exchange.Exception</c> before invoking the matched
/// handler and flips <c>ExceptionHandled = true</c> after the handler returns, so the
/// tail after <c>End()</c> can observe both. Unmatched exceptions propagate out of the
/// scope (after running <c>Finally</c>, if present).
///
/// Forms covered:
///   1. Leaf DoTry/DoCatch — single typed catch, simple handler.
///   2. Multi-step try body — pipeline runs in order until something throws.
///   3. Multiple typed DoCatch branches — first-match-wins.
///   4. DoCatch with <c>.When(predicate)</c> guard — clause skipped when predicate fails.
///   5. DoTry/DoCatch/Finally — finally runs on success AND on failure.
///   6. Catch handler reads <c>e.Exception</c> and can transform/repair the body.
///   7. Nested DoTry inside an outer DoCatch handler — error escalation pattern.
///   8. DoTry nested inside a Choice When branch — per-message try/catch in a branch.
///   9. Enterprise scenario: payment pipeline with validation, processing, audit
///      catch and finally book-keeping.
/// </summary>
public class DslDoTryReferenceTests
{
    // ── 1. Leaf DoTry/DoCatch ────────────────────────────────────────────────

    /// <summary>
    /// Simplest possible form. A throwing step inside the try body is caught by the
    /// matching typed DoCatch. After <c>End()</c> the route scope is restored and the
    /// tail runs for every exchange — both the ones that threw and the ones that did not.
    /// </summary>
    [Fact]
    public async Task Leaf_DoTry_DoCatch_HandlesAndContinues()
    {
        await using var context = new RouteContext();
        var caught = new List<int>();
        var tail = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://doTry-leaf")
                .DoTry()
                    .Process(e =>
                    {
                        if ((int)e.In.Body! % 2 == 0)
                            throw new InvalidOperationException("even-boom");
                    })
                .DoCatch<InvalidOperationException>()
                    .Process(e => caught.Add((int)e.In.Body!))
                .End()
                .Process(e => tail.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://doTry-leaf").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        caught.Should().Equal(2, 4);
        tail.Should().Equal(1, 2, 3, 4);
    }

    // ── 2. Multi-step try body pipeline ──────────────────────────────────────

    /// <summary>
    /// Try body is a real pipeline of multiple steps. When step #2 throws, step #3 is
    /// skipped, the handler runs, and <c>exchange.ExceptionHandled</c> becomes true.
    /// </summary>
    [Fact]
    public async Task MultiStep_TryBody_StopsOnFirstException()
    {
        await using var context = new RouteContext();
        var beforeThrow = new List<int>();
        var afterThrow = new List<int>();
        var caught = new List<(string msg, bool handled)>();

        context.AddRoutes(r =>
        {
            r.From("direct://doTry-multistep")
                .DoTry()
                    .Process(e => beforeThrow.Add((int)e.In.Body!))   // always runs
                    .Process(e =>
                    {
                        if ((int)e.In.Body! < 0) throw new InvalidOperationException("neg");
                    })
                    .Process(e => afterThrow.Add((int)e.In.Body!))    // skipped on throw
                .DoCatch<InvalidOperationException>()
                    .Process(e => caught.Add((e.Exception!.Message, e.ExceptionHandled)))
                .End();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://doTry-multistep").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, -1, 2, -3 })
            await producer.Process(new Exchange(new Message(i)));

        beforeThrow.Should().Equal(1, -1, 2, -3);
        afterThrow.Should().Equal(1, 2);
        // Handler sees the exception; ExceptionHandled flips AFTER the handler returns,
        // so inside the handler it is still false.
        caught.Should().Equal(("neg", false), ("neg", false));
    }

    // ── 3. Multiple typed DoCatch — first match wins ─────────────────────────

    /// <summary>
    /// Catch clauses are evaluated in declaration order. The first one whose
    /// <c>ExceptionType.IsAssignableFrom(actual)</c> wins; later clauses (including
    /// broader <c>Exception</c> base) are skipped.
    /// </summary>
    [Fact]
    public async Task MultipleCatch_FirstMatchWins_ByDeclarationOrder()
    {
        await using var context = new RouteContext();
        var caughtInvalid = new List<string>();
        var caughtArg = new List<string>();
        var caughtAny = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://doTry-multicatch")
                .DoTry()
                    .Process(e =>
                    {
                        switch ((string)e.In.Body!)
                        {
                            case "invalid": throw new InvalidOperationException("inv");
                            case "arg":     throw new ArgumentException("arg");
                            case "other":   throw new ApplicationException("other");
                        }
                    })
                .DoCatch<InvalidOperationException>()
                    .Process(e => caughtInvalid.Add(e.Exception!.Message))
                .Catch<ArgumentException>()
                    .Process(e => caughtArg.Add(e.Exception!.Message))
                .Catch<Exception>()
                    .Process(e => caughtAny.Add(e.Exception!.Message))
                .End();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://doTry-multicatch").CreateProducer();
        await producer.Start();
        foreach (var s in new[] { "invalid", "arg", "other", "ok" })
            await producer.Process(new Exchange(new Message(s)));

        caughtInvalid.Should().Equal("inv");
        caughtArg.Should().Equal("arg");
        caughtAny.Should().Equal("other");
    }

    // ── 4. DoCatch with When predicate ───────────────────────────────────────

    /// <summary>
    /// A <c>When</c> predicate on a catch clause acts as a secondary guard:
    /// the type must match AND the predicate must return true. When the predicate
    /// fails the clause is skipped and the next clause is tried.
    /// </summary>
    [Fact]
    public async Task DoCatch_With_When_Predicate_GuardsTheClause()
    {
        await using var context = new RouteContext();
        var transient = new List<string>();
        var fatal = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://doTry-catchwhen")
                .DoTry()
                    .Process(e => throw new InvalidOperationException((string)e.In.Body!))
                .DoCatch<InvalidOperationException>()
                    .When(ex => ex.Message.StartsWith("transient:"))
                    .Process(e => transient.Add(e.Exception!.Message))
                .Catch<InvalidOperationException>()
                    .Process(e => fatal.Add(e.Exception!.Message))
                .End();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://doTry-catchwhen").CreateProducer();
        await producer.Start();
        foreach (var s in new[] { "transient:timeout", "boom", "transient:retry", "kapow" })
            await producer.Process(new Exchange(new Message(s)));

        transient.Should().Equal("transient:timeout", "transient:retry");
        fatal.Should().Equal("boom", "kapow");
    }

    // ── 5. Finally — always runs ─────────────────────────────────────────────

    /// <summary>
    /// The <c>Finally</c> block runs after the try body on success, and after the
    /// matched catch handler on failure. It always runs exactly once per exchange.
    /// </summary>
    [Fact]
    public async Task DoTry_DoCatch_Finally_AlwaysRuns()
    {
        await using var context = new RouteContext();
        var done = new List<int>();
        var caught = new List<int>();
        var finallyRan = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://doTry-finally")
                .DoTry()
                    .Process(e =>
                    {
                        if ((int)e.In.Body! < 0) throw new InvalidOperationException("neg");
                        done.Add((int)e.In.Body!);
                    })
                .DoCatch<InvalidOperationException>()
                    .Process(e => caught.Add((int)e.In.Body!))
                .Finally()
                    .Process(e => finallyRan.Add((int)e.In.Body!))
                .EndTryCatch();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://doTry-finally").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, -2, 3, -4, 5 })
            await producer.Process(new Exchange(new Message(i)));

        done.Should().Equal(1, 3, 5);
        caught.Should().Equal(-2, -4);
        finallyRan.Should().Equal(1, -2, 3, -4, 5);
    }

    // ── 6. Catch handler repairs the body ────────────────────────────────────

    /// <summary>
    /// A catch handler reads <c>e.Exception</c> and writes a replacement body, then
    /// the tail after <c>End()</c> observes the repaired body. Successful exchanges
    /// flow through with their original body.
    /// </summary>
    [Fact]
    public async Task CatchHandler_RepairsBody_TailSeesRepair()
    {
        await using var context = new RouteContext();
        var output = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://doTry-repair")
                .DoTry()
                    .Transform(e =>
                    {
                        var s = (string)e.In.Body!;
                        if (!int.TryParse(s, out var n))
                            throw new FormatException($"bad-int:{s}");
                        return n * 10;
                    })
                .DoCatch<FormatException>()
                    .Transform(e => $"<repaired:{e.Exception!.Message}>")
                .End()
                .Process(e => output.Add(e.In.Body!.ToString()!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://doTry-repair").CreateProducer();
        await producer.Start();
        foreach (var s in new[] { "1", "xx", "2", "yy" })
            await producer.Process(new Exchange(new Message(s)));

        output.Should().Equal("10", "<repaired:bad-int:xx>", "20", "<repaired:bad-int:yy>");
    }

    // ── 7. Nested DoTry inside outer catch handler ───────────────────────────

    /// <summary>
    /// Error-escalation pattern: outer catch tries a recovery action that may itself
    /// fail. The inner DoTry catches the recovery failure and routes the exchange to
    /// a dead-letter sink. Demonstrates that DoTry composes recursively.
    /// </summary>
    [Fact]
    public async Task NestedDoTry_InsideCatch_EscalatesToDeadLetter()
    {
        await using var context = new RouteContext();
        var primary = new List<int>();
        var recovered = new List<int>();
        var deadLetter = new List<(int body, string err)>();

        context.AddRoutes(r =>
        {
            r.From("direct://doTry-nested")
                .DoTry()
                    .Process(e =>
                    {
                        // Primary path throws for everything > 0; lets non-positive pass.
                        if ((int)e.In.Body! > 0) throw new InvalidOperationException("primary-down");
                        primary.Add((int)e.In.Body!);
                    })
                .DoCatch<InvalidOperationException>()
                    .DoTry()
                        .Process(e =>
                        {
                            // Recovery succeeds for even values, fails for odd.
                            if ((int)e.In.Body! % 2 != 0)
                                throw new ApplicationException("recovery-failed");
                            recovered.Add((int)e.In.Body!);
                        })
                    .DoCatch<ApplicationException>()
                        .Process(e => deadLetter.Add(((int)e.In.Body!, e.Exception!.Message)))
                    .End()
                .End();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://doTry-nested").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 0, 1, 2, 3, 4, -1 })
            await producer.Process(new Exchange(new Message(i)));

        primary.Should().Equal(0, -1);
        recovered.Should().Equal(2, 4);
        deadLetter.Should().Equal((1, "recovery-failed"), (3, "recovery-failed"));
    }

    // ── 8. DoTry inside a Choice When branch ─────────────────────────────────

    /// <summary>
    /// DoTry composes inside any scope opener — here it is the entire body of a
    /// Choice When branch. The catch handler only runs for exchanges that fail
    /// inside this specific branch.
    /// </summary>
    [Fact]
    public async Task DoTry_InsideChoiceWhen_BranchLocalErrorHandling()
    {
        await using var context = new RouteContext();
        var ok = new List<(string kind, int body)>();
        var caught = new List<(string kind, string err)>();

        context.AddRoutes(r =>
        {
            r.From("direct://doTry-in-when")
                .Choice(c => c
                    .When(e => (string)e.In.Headers["kind"]! == "risky", b => b
                        .DoTry()
                            .Process(e =>
                            {
                                if ((int)e.In.Body! < 0) throw new InvalidOperationException("risky-neg");
                                ok.Add(((string)e.In.Headers["kind"]!, (int)e.In.Body!));
                            })
                        .DoCatch<InvalidOperationException>()
                            .Process(e => caught.Add(((string)e.In.Headers["kind"]!, e.Exception!.Message)))
                        .End())
                    .Otherwise(b => b
                        .Process(e => ok.Add(((string)e.In.Headers["kind"]!, (int)e.In.Body!)))));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://doTry-in-when").CreateProducer();
        await producer.Start();

        async Task Send(int v, string kind) =>
            await producer.Process(new Exchange(new Message
            {
                Body = v,
                Headers = { ["kind"] = kind }
            }));

        await Send(1, "risky");
        await Send(-2, "risky");
        await Send(3, "safe");
        await Send(-4, "risky");
        await Send(5, "safe");

        ok.Should().Equal(("risky", 1), ("safe", 3), ("safe", 5));
        caught.Should().Equal(("risky", "risky-neg"), ("risky", "risky-neg"));
    }

    // ── 9. Enterprise scenario: payment pipeline ─────────────────────────────

    /// <summary>
    /// Realistic payment pipeline:
    ///   • Try body validates input, then "settles" the payment (may throw).
    ///   • A typed catch handles validation errors and pushes to a reject queue.
    ///   • A broad catch handles every other exception as a transient failure.
    ///   • Finally always book-keeps the attempt count for audit.
    /// </summary>
    [Fact]
    public async Task EnterpriseScenario_PaymentPipeline_AuditedTryCatchFinally()
    {
        await using var context = new RouteContext();
        var settled = new List<(string id, decimal amt)>();
        var rejected = new List<(string id, string reason)>();
        var transient = new List<(string id, string reason)>();
        var attempts = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://payments")
                .DoTry()
                    .Process(e =>
                    {
                        var amount = (decimal)e.In.Body!;
                        if (amount <= 0m) throw new ArgumentException("non-positive");
                        if (amount > 10_000m) throw new InvalidOperationException("settlement-down");
                    })
                    .Process(e => settled.Add(((string)e.In.Headers["id"]!, (decimal)e.In.Body!)))
                .DoCatch<ArgumentException>()
                    .Process(e => rejected.Add(((string)e.In.Headers["id"]!, e.Exception!.Message)))
                .Catch<Exception>()
                    .Process(e => transient.Add(((string)e.In.Headers["id"]!, e.Exception!.Message)))
                .Finally()
                    .Process(e => attempts.Add((string)e.In.Headers["id"]!))
                .EndTryCatch();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://payments").CreateProducer();
        await producer.Start();

        async Task Send(string id, decimal amt) =>
            await producer.Process(new Exchange(new Message
            {
                Body = amt,
                Headers = { ["id"] = id }
            }));

        await Send("p1", 100m);     // settled
        await Send("p2", 0m);       // rejected (non-positive)
        await Send("p3", 50_000m);  // transient (settlement-down)
        await Send("p4", -1m);      // rejected
        await Send("p5", 250m);     // settled

        settled.Should().Equal(("p1", 100m), ("p5", 250m));
        rejected.Should().Equal(("p2", "non-positive"), ("p4", "non-positive"));
        transient.Should().Equal(("p3", "settlement-down"));
        attempts.Should().Equal("p1", "p2", "p3", "p4", "p5");
    }
}
