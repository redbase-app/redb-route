using AwesomeAssertions;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The words of <see cref="ExpressionSandboxViolationException"/>: a refused method call names the method and its type,
/// a refused read (<c>ref:name</c>, <c>env:NAME</c>) names the spelling and no method, since it calls none.
/// </summary>
public sealed class SandboxViolationTests
{
    [Fact]
    public void AMethodCall_NamesTheMethodAndTheType()
    {
        var exception = new ExpressionSandboxViolationException("Delete", typeof(FileInfo));

        exception.Message.Should().StartWith("Method 'Delete' on FileInfo cannot be called here: the expression runs in a sandbox");
        exception.Written.Should().Be("Delete");
        exception.MethodName.Should().Be("Delete");
        exception.TargetType.Should().Be<FileInfo>();
    }

    [Theory]
    [InlineData("ref:rules")]
    [InlineData("env:HOME")]
    public void ARead_NamesTheSpelling_AndNoMethod(string written)
    {
        var exception = new ExpressionSandboxViolationException(written);

        exception.Message.Should().Be(
            $"'{written}' cannot be read here: the expression runs in a sandbox (a template argument or expr()), " +
            "which reads the message and its values, not the registry or the process environment.");
        exception.Message.Should().NotContain("Method");
        exception.Written.Should().Be(written);
        exception.TargetType.Should().BeNull();
    }

    [Fact]
    public void AReadOfARegistryName_IsRefusedBeforeTheRegistryIsAsked()
    {
        // No context registers 'missing': under the sandbox the refusal comes first, so the registry is never read.
        var exchange = new redb.Route.Core.Exchange(new redb.Route.Core.Message("x"))
        {
            Context = new redb.Route.Core.RouteContext(),
        };
        using var sandbox = ExpressionSandbox.Enter();

        var act = () => new StringExpression("${ref:missing}").Evaluate<object>(exchange);

        act.Should().Throw<ExpressionSandboxViolationException>().Which.Written.Should().Be("ref:missing");
    }
}
