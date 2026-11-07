using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// The one resolver behind every consumer-concurrency option that accepts <c>auto</c>
/// (план HTTP_CONCURRENCY_LIMITS_PLAN, решение В-7). Red-before context: these options used to
/// be int-typed, so <c>concurrentConsumers=auto</c> — and any typo — silently bound to the
/// default of 1; the garbage cases below THREW nowhere before the string conversion.
/// </summary>
public sealed class ConcurrencyOptionTests
{
    [Fact]
    public void Auto_IsAtLeastTwo_AndCpuBound()
    {
        ConcurrencyOption.Auto.Should().Be(Math.Max(Environment.ProcessorCount, 2),
            "формула NServiceBus: max(CPU, 2)");
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("  ", 1)]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData(" 3 ", 3)]
    public void Resolve_NumbersAndDefault(string? raw, int expected)
        => ConcurrencyOption.Resolve(raw, "concurrentConsumers").Should().Be(expected);

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    [InlineData(" auto ")]
    public void Resolve_Auto_IsCaseInsensitive(string raw)
        => ConcurrencyOption.Resolve(raw, "concurrentConsumers").Should().Be(ConcurrencyOption.Auto);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("trash")]
    [InlineData("2x")]
    [InlineData("1.5")]
    public void Resolve_Garbage_FailsLoud_NamingTheOption(string raw)
    {
        var act = () => ConcurrencyOption.Resolve(raw, "concurrentConsumers");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*concurrentConsumers*", "ошибка обязана называть опцию")
            .WithMessage("*auto*", "и подсказывать валидные значения");
    }

    [Fact]
    public void Resolve_CustomDefault_IsHonored()
        => ConcurrencyOption.Resolve(null, "x", defaultValue: 5).Should().Be(5);

    [Fact]
    public void Seda_AutoBinds_AndGarbageFailsLoud()
    {
        // The end-to-end proof through a real component: "auto" resolves, garbage throws at
        // endpoint creation. On the old int options both silently became 1.
        var context = new RouteContext();
        var auto = context.GetEndpoint("seda:v5-auto?concurrentConsumers=auto");
        var opts = (SedaEndpointOptions)typeof(EndpointBase<SedaEndpointOptions>)
            .GetProperty("Options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(auto)!;
        opts.ResolvedConcurrentConsumers.Should().Be(ConcurrencyOption.Auto);

        var act = () => context.GetEndpoint("seda:v5-bad?concurrentConsumers=trash");
        act.Should().Throw<ArgumentException>().WithMessage("*concurrentConsumers*");
    }
}
