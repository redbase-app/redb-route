using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Validation;
using Xunit;

namespace redb.Route.Validation.Adapters.Tests;

public class DataAnnotationsValidatorTests
{
    public sealed class Order
    {
        [Required]
        public string CustomerId { get; set; } = "";

        [Range(1, int.MaxValue, ErrorMessage = "Quantity must be positive")]
        public int Quantity { get; set; }
    }

    private static IExchange Exchange(object? body) => new Exchange(new Message(body));

    [Fact]
    public void Valid_ReturnsSuccess()
    {
        var v = new DataAnnotationsValidator();
        var r = v.Validate(Exchange(new Order { CustomerId = "C1", Quantity = 5 }));
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Invalid_ReturnsFailureWithMemberNames()
    {
        var v = new DataAnnotationsValidator();
        var r = v.Validate(Exchange(new Order { CustomerId = "", Quantity = 0 }));

        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("CustomerId"));
        r.Errors.Should().Contain(e => e.Contains("Quantity"));
    }

    [Fact]
    public void DefaultSeverity_IsError()
    {
        var v = new DataAnnotationsValidator();
        var r = v.Validate(Exchange(new Order()));
        r.Severity.Should().Be(ValidationSeverity.Error);
    }

    [Fact]
    public void WarningSeverity_IsPropagated()
    {
        var v = new DataAnnotationsValidator(ValidationSeverity.Warning);
        var r = v.Validate(Exchange(new Order()));
        r.Severity.Should().Be(ValidationSeverity.Warning);
    }

    [Fact]
    public void NullBody_ReturnsFailure()
    {
        var v = new DataAnnotationsValidator();
        var r = v.Validate(Exchange(null));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().ContainSingle().Which.Should().Contain("null");
    }
}
