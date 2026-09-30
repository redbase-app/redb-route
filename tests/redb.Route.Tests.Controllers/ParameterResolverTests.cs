using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

public class ParameterResolverTests
{
    [Fact]
    public void ConvertValue_handles_guid()
    {
        var guid = Guid.NewGuid();
        var result = ParameterResolver.ConvertValue(guid.ToString(), typeof(Guid));

        result.Should().Be(guid);
    }

    [Fact]
    public void ConvertValue_handles_int()
    {
        var result = ParameterResolver.ConvertValue("42", typeof(int));

        result.Should().Be(42);
    }

    [Fact]
    public void ConvertValue_handles_bool()
    {
        var result = ParameterResolver.ConvertValue("true", typeof(bool));

        result.Should().Be(true);
    }

    [Fact]
    public void ConvertValue_handles_null_for_reference_type()
    {
        var result = ParameterResolver.ConvertValue(null, typeof(string));

        result.Should().BeNull();
    }

    [Fact]
    public void ConvertValue_handles_null_for_value_type()
    {
        var result = ParameterResolver.ConvertValue(null, typeof(int));

        result.Should().Be(0);
    }
}

public class PositionalParameterResolverTests
{
    private static System.Reflection.MethodInfo GetMethod<T>(string name)
        => typeof(T).GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;

    [Fact]
    public void Resolves_no_params_with_null_body()
    {
        var method = GetMethod<EchoController>("GetAll");
        var result = ParameterResolver.ResolvePositional(method, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolves_single_string_arg()
    {
        var method = GetMethod<EchoController>("Echo");
        var result = ParameterResolver.ResolvePositional(method, "hello");

        result.Should().HaveCount(1);
        result[0].Should().Be("hello");
    }

    [Fact]
    public void Resolves_single_int_arg_with_type_conversion()
    {
        var method = GetMethod<EchoController>("GetById");
        // SignalR may send int as long (JSON protocol)
        var result = ParameterResolver.ResolvePositional(method, 42L);

        result.Should().HaveCount(1);
        result[0].Should().Be(42);
    }

    [Fact]
    public void Resolves_multiple_positional_args()
    {
        var method = GetMethod<EchoController>("Update");
        var body = new object[] { 7, new CreateModuleRequest { Name = "test" } };

        var result = ParameterResolver.ResolvePositional(method, body);

        result.Should().HaveCount(2);
        result[0].Should().Be(7);
        result[1].Should().BeOfType<CreateModuleRequest>();
    }

    [Fact]
    public void Uses_default_value_for_missing_args()
    {
        var method = GetMethod<EchoController>("WithDefault");
        var result = ParameterResolver.ResolvePositional(method, "hello");

        result.Should().HaveCount(2);
        result[0].Should().Be("hello");
        result[1].Should().Be(5); // default value
    }

    [Fact]
    public void Injects_CancellationToken()
    {
        var method = GetMethod<EchoController>("WithCancellation");
        using var cts = new CancellationTokenSource();
        var result = ParameterResolver.ResolvePositional(method, "test", cts.Token);

        result.Should().HaveCount(2);
        result[0].Should().Be("test");
        result[1].Should().Be(cts.Token);
    }

    [Fact]
    public void Handles_all_null_args_for_reference_params()
    {
        var method = GetMethod<EchoController>("Echo");
        var result = ParameterResolver.ResolvePositional(method, null);

        result.Should().HaveCount(1);
        result[0].Should().BeNull(); // string param, no value
    }
}

public class ConvertValueFailureTests
{
    [Theory]
    [InlineData("abc", typeof(int))]
    [InlineData("12,5x", typeof(decimal))]
    [InlineData("yes-please", typeof(bool))]
    [InlineData("not-a-guid", typeof(Guid))]
    [InlineData("medium", typeof(BindingPriority))]
    [InlineData("soon", typeof(DateTimeOffset))]
    public void A_value_that_does_not_convert_is_an_error_naming_it(string value, Type target)
    {
        var act = () => ParameterResolver.ConvertValue(value, target, "p");

        act.Should().Throw<FormatException>()
            .Which.Message.Should().Contain("'p'").And.Contain(value).And.Contain(target.Name);
    }

    [Fact]
    public void A_missing_value_is_still_the_type_default_not_an_error()
    {
        ParameterResolver.ConvertValue(null, typeof(int), "p").Should().Be(0);
    }
}
