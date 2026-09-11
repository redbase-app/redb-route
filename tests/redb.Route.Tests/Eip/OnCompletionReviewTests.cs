using System.Transactions;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Eip;

/// <summary>
/// Defects from the 2026-09-01 code review (docs/V4/REVIEW-CODE-2026-09-01.md, В1-В3). The copy an
/// OnCompletion block receives shares its body with the live exchange, so releasing the copy must never
/// close that body; the detached block runs with the caller's transaction suppressed, like WireTap; and
/// a failing <c>When</c> can not change the route's own result.
/// </summary>
public class OnCompletionReviewTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    // The stream tests send a caller-owned exchange through a ProducerTemplate: the TestKit helpers
    // create and dispose their own exchange (closing the body themselves), which would hide who did it.

    [Fact]
    public async Task FalseWhen_DoesNotDisposeTheLiveStreamBody()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://stream-when")
                .OnCompletion().When("header.audit").To("mock://never").EndOnCompletion()
                .To("mock://out"));
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var body = new MemoryStream("payload"u8.ToArray());
        var message = new Message(body);
        message.Headers["audit"] = false;
        await template.SendAsync("direct://stream-when", Exchange.Create(message, null));

        body.CanRead.Should().BeTrue("the completion copy shares the body with the live exchange; releasing the copy must not close it");
        await ctx.Mock("mock://out").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task AsyncBlock_DoesNotDisposeTheLiveStreamBody()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://stream-async")
                .OnCompletion().To("mock://done").EndOnCompletion()
                .To("mock://out2"));
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var body = new MemoryStream("payload"u8.ToArray());
        await template.SendAsync("direct://stream-async", Exchange.Create(new Message(body), null));
        await ctx.Mock("mock://done").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        await Task.Delay(100);   // the block's teardown runs after the mock saw the message

        body.CanRead.Should().BeTrue();
    }

    [Fact]
    public async Task ThrowingWhen_DoesNotTurnASuccessIntoAFailure()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://bad-when")
                .OnCompletion().When(_ => throw new InvalidOperationException("predicate broke")).To("mock://never2").EndOnCompletion()
                .SetBody("reply"));
        await ctx.Start();

        var reply = await ctx.RequestBody<string>("direct://bad-when", "in");

        reply.Should().Be("reply", "OnCompletion never changes the route's own result");
    }

    [Fact]
    public async Task ThrowingWhen_OnFailure_KeepsTheRoutesOwnException()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://bad-when-fail")
                .OnCompletion().OnFailureOnly().When(_ => throw new InvalidOperationException("predicate broke")).To("mock://never3").EndOnCompletion()
                .ThrowException<ArgumentException>("the route's own failure"));
        await ctx.Start();

        var act = () => ctx.SendBody("direct://bad-when-fail", "in");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*the route's own failure*");
    }

    [Fact]
    public async Task AsyncBlock_RunsWithTheAmbientTransactionSuppressed()
    {
        Transaction? txInBlock = null;
        var tcs = new TaskCompletionSource();
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://tx")
                .OnCompletion().Process(_ => { txInBlock = Transaction.Current; tcs.TrySetResult(); }).EndOnCompletion()
                .SetBody("ok"));
        await ctx.Start();

        using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await ctx.SendBody("direct://tx", "in");
            // The block runs while the caller's scope is still open — the case where a flowed ambient
            // transaction would be completed underneath it (.NET clears the flowed data on scope disposal,
            // so waiting outside the scope would hide the leak).
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scope.Complete();
        }

        txInBlock.Should().BeNull("the completion block is detached from the originating transaction, like WireTap");
    }

    /// <summary>Second batch: OnCompletion bodies compile inside the route's compile frame, so a checkpoint inside one knows its route.</summary>
    [Fact]
    public async Task Body_MayDeclareAReplayableCheckpoint()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://cp")
                .OnCompletion().Replayable("after", r => r.SetHeader("checkpointed", true)).To("mock://cp").EndOnCompletion()
                .SetBody("ok"));

        var act = () => ctx.Start();

        await act.Should().NotThrowAsync();
        await ctx.SendBody("direct://cp", "x");
        await ctx.Mock("mock://cp").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
    }
}
