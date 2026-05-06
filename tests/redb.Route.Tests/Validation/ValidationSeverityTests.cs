using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Validation;
using Xunit;

namespace redb.Route.Tests.Validation;

public class ValidationSeverityTests
{
    private sealed class StubValidator : IMessageValidator
    {
        private readonly ValidationResult _result;
        public StubValidator(ValidationResult result) => _result = result;
        public ValidationResult Validate(IExchange exchange) => _result;
    }

    private static IExchange CreateExchange()
        => new Exchange(new Message("body"));

    [Fact]
    public async Task Warning_AppendsToHeader_AndDoesNotThrow()
    {
        var validator = new StubValidator(
            ValidationResult.Failure(new[] { "field 'x' is suspicious" }, ValidationSeverity.Warning));
        var processor = new ValidateProcessor(validator, throwOnFailure: true);
        var exchange = CreateExchange();

        var act = async () => await processor.Process(exchange);
        await act.Should().NotThrowAsync();

        exchange.In.Headers.Should().ContainKey(ValidateProcessor.ValidationWarningsHeader);
        exchange.In.Headers[ValidateProcessor.ValidationWarningsHeader]
            .Should().Be("field 'x' is suspicious");
    }

    [Fact]
    public async Task Warning_MultipleInvocations_AppendedWithSemicolon()
    {
        var v1 = new StubValidator(ValidationResult.Failure(new[] { "warn-1" }, ValidationSeverity.Warning));
        var v2 = new StubValidator(ValidationResult.Failure(new[] { "warn-2", "warn-3" }, ValidationSeverity.Warning));
        var p1 = new ValidateProcessor(v1);
        var p2 = new ValidateProcessor(v2);
        var exchange = CreateExchange();

        await p1.Process(exchange);
        await p2.Process(exchange);

        exchange.In.Headers[ValidateProcessor.ValidationWarningsHeader]
            .Should().Be("warn-1; warn-2; warn-3");
    }

    [Fact]
    public async Task Error_PreservesThrowBehavior_WhenThrowOnFailureTrue()
    {
        var validator = new StubValidator(
            ValidationResult.Failure(new[] { "boom" }, ValidationSeverity.Error));
        var processor = new ValidateProcessor(validator, throwOnFailure: true);

        await Assert.ThrowsAsync<ValidationException>(() => processor.Process(CreateExchange()));
    }

    [Fact]
    public async Task Error_DoesNotThrow_WhenThrowOnFailureFalse_ButSetsProperties()
    {
        var validator = new StubValidator(
            ValidationResult.Failure(new[] { "boom" }, ValidationSeverity.Error));
        var processor = new ValidateProcessor(validator, throwOnFailure: false);
        var exchange = CreateExchange();

        await processor.Process(exchange);

        exchange.Properties[ValidateProcessor.ValidationResultProperty].Should().Be(false);
        exchange.Properties[ValidateProcessor.ValidationErrorsProperty].Should().Be("boom");
        exchange.In.Headers.Should().NotContainKey(ValidateProcessor.ValidationWarningsHeader);
    }

    [Fact]
    public void DefaultFailureFactory_IsErrorSeverity()
    {
        var result = ValidationResult.Failure(new[] { "x" });

        result.Severity.Should().Be(ValidationSeverity.Error);
    }

    [Fact]
    public void Failure_WithExplicitSeverity_PropagatesSeverity()
    {
        var result = ValidationResult.Failure(new[] { "x" }, ValidationSeverity.Warning);

        result.Severity.Should().Be(ValidationSeverity.Warning);
        result.IsValid.Should().BeFalse();
    }
}
