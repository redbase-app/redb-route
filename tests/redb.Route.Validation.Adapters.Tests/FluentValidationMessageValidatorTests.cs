using FluentAssertions;
using FluentValidation;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Validation;
using Xunit;

namespace redb.Route.Validation.Adapters.Tests;

public class FluentValidationMessageValidatorTests
{
    public sealed class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    public sealed class PersonValidator : AbstractValidator<Person>
    {
        public PersonValidator()
        {
            RuleFor(p => p.Name).NotEmpty();
            RuleFor(p => p.Age).GreaterThan(0);
        }
    }

    private static IExchange Exchange(object? body) => new Exchange(new Message(body));

    [Fact]
    public void Valid_ReturnsSuccess()
    {
        var v = new FluentValidationMessageValidator<Person>(new PersonValidator());
        var r = v.Validate(Exchange(new Person { Name = "John", Age = 30 }));

        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Invalid_ReturnsFailureWithErrors()
    {
        var v = new FluentValidationMessageValidator<Person>(new PersonValidator());
        var r = v.Validate(Exchange(new Person { Name = "", Age = 0 }));

        r.IsValid.Should().BeFalse();
        r.Errors.Should().HaveCount(2);
        r.Errors.Should().Contain(e => e.Contains("Name"));
        r.Errors.Should().Contain(e => e.Contains("Age"));
    }

    [Fact]
    public void DefaultSeverity_IsError()
    {
        var v = new FluentValidationMessageValidator<Person>(new PersonValidator());
        var r = v.Validate(Exchange(new Person()));
        r.Severity.Should().Be(ValidationSeverity.Error);
    }

    [Fact]
    public void WarningSeverity_IsPropagated()
    {
        var v = new FluentValidationMessageValidator<Person>(new PersonValidator(), ValidationSeverity.Warning);
        var r = v.Validate(Exchange(new Person()));
        r.Severity.Should().Be(ValidationSeverity.Warning);
    }

    [Fact]
    public void NullBody_ReturnsFailure()
    {
        var v = new FluentValidationMessageValidator<Person>(new PersonValidator());
        var r = v.Validate(Exchange(null));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().ContainSingle().Which.Should().Contain("null");
    }

    [Fact]
    public void IncompatibleBody_ReturnsFailure()
    {
        var v = new FluentValidationMessageValidator<Person>(new PersonValidator());
        var r = v.Validate(Exchange("just a string"));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().ContainSingle().Which.Should().Contain("not assignable");
    }

    [Fact]
    public void Constructor_NullValidator_Throws()
    {
        var act = () => new FluentValidationMessageValidator<Person>(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
