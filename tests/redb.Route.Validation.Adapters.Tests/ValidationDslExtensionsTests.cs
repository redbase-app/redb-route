using FluentAssertions;
using FluentValidation;
using redb.Route.Core;
using redb.Route.Validation;
using Xunit;

namespace redb.Route.Validation.Adapters.Tests;

public class ValidationDslExtensionsTests
{
    public sealed class Item
    {
        public string Name { get; set; } = "";
    }

    private sealed class ItemValidator : AbstractValidator<Item>
    {
        public ItemValidator() => RuleFor(x => x.Name).NotEmpty();
    }

    [Fact]
    public async Task ValidateFluent_Error_ThrowsByDefault()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://fv-error")
                .ValidateFluent(new ItemValidator())
                .Process(_ => { });
        });
        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://fv-error").CreateProducer();
        await producer.Start();

        var act = async () => await producer.Process(new Exchange(new Message(new Item())));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ValidateFluent_Warning_DoesNotThrow_AndSetsHeader()
    {
        await using var ctx = new RouteContext();
        Exchange? captured = null;
        ctx.AddRoutes(r =>
        {
            r.From("direct://fv-warn")
                .ValidateFluent(new ItemValidator(), ValidationSeverity.Warning)
                .Process(e => captured = (Exchange)e);
        });
        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://fv-warn").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message(new Item())));

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey(ValidateProcessor.ValidationWarningsHeader);
    }

    [Fact]
    public async Task ValidateAnnotations_Warning_DoesNotThrow()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://da-warn")
                .ValidateAnnotations(ValidationSeverity.Warning)
                .Process(_ => { });
        });
        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://da-warn").CreateProducer();
        await producer.Start();

        var act = async () => await producer.Process(
            new Exchange(new Message(new FluentValidationMessageValidatorTests.Person())));
        await act.Should().NotThrowAsync();
    }
}
