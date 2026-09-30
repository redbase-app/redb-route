using FluentAssertions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// A known option whose value does not convert to the option's type is an error at binding, for
/// every connector: the name is right, the value is wrong — the most common typo — and the option
/// must not quietly keep its default. Unknown names are a different question (the connector's
/// strictness) and stay among the unmapped parameters.
/// </summary>
public class EndpointOptionsInvalidValueTests
{
    public enum Delivery { Push, Poll }

    [Flags]
    public enum Rights { None = 0, Read = 1, Write = 2 }

    public sealed class Probe : EndpointOptions
    {
        public int Port { get; set; } = 5672;
        public bool Durable { get; set; } = true;
        public long? MaxBytes { get; set; }
        public double Ratio { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public Delivery Mode { get; set; }
        public Rights Access { get; set; }
        public DynamicValue<int>? Ttl { get; set; }

        [Sensitive]
        public int? Pin { get; set; }

        public override void Validate() { }
    }

    /// <summary>A connector that adds its own hint to the core refusal.</summary>
    public sealed class HintedProbe : EndpointOptions
    {
        public Delivery Mode { get; set; }

        protected override string? OptionValueHint(string optionName)
            => optionName == nameof(Mode) ? "Push delivers as it arrives, Poll on a schedule." : null;

        public override void Validate() { }
    }

    private static Action Bind(EndpointOptions options, string key, string value)
        => () => options.BindFromUri(new Dictionary<string, string> { [key] = value });

    [Theory]
    [InlineData("port", "abc", "*'port=abc'*port*whole number*")]
    [InlineData("port", "99999999999", "*'port=99999999999'*")]
    [InlineData("durable", "yes", "*'durable=yes'*true or false*")]
    [InlineData("maxBytes", "", "*'maxBytes='*whole number*")]
    [InlineData("ratio", "1,5", "*'ratio=1,5'*number*")]
    [InlineData("timeout", "30s", "*'timeout=30s'*time span*")]
    [InlineData("mode", "pul", "*'mode=pul'*mode=push*mode=poll*")]
    [InlineData("ttl", "soon", "*'ttl=soon'*whole number*")]
    public void An_unconvertible_value_of_a_known_option_is_refused_with_what_it_takes(string key, string value, string message)
    {
        var act = Bind(new Probe(), key, value);

        act.Should().Throw<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public void A_number_that_names_no_enum_member_is_refused()
    {
        var act = Bind(new Probe(), "mode", "7");

        act.Should().Throw<ArgumentException>().WithMessage("*'mode=7'*mode=push*");
    }

    [Fact]
    public void Flags_combinations_still_bind()
    {
        var probe = new Probe();

        Bind(probe, "access", "Read, Write")();

        probe.Access.Should().Be(Rights.Read | Rights.Write);
    }

    [Fact]
    public void An_expression_in_a_plain_option_is_refused_and_told_where_expressions_go()
    {
        var act = Bind(new Probe(), "port", "${header.port}");

        act.Should().Throw<ArgumentException>().WithMessage("*does not accept ${...} expressions*");
    }

    [Fact]
    public void An_expression_in_a_dynamic_option_still_binds()
    {
        var probe = new Probe();

        Bind(probe, "ttl", "${header.ttl}")();

        probe.Ttl!.Value.IsDynamic.Should().BeTrue();
    }

    [Fact]
    public void The_value_of_a_sensitive_option_never_reaches_the_message()
    {
        var act = Bind(new Probe(), "pin", "12x4");

        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("12x4").And.Contain("pin");
    }

    [Fact]
    public void A_connector_adds_its_own_hint()
    {
        var act = Bind(new HintedProbe(), "mode", "pul");

        act.Should().Throw<ArgumentException>().WithMessage("*mode=push*Poll on a schedule*");
    }

    // ── Unknown names: strict unless the options type declares itself lenient ──

    [LenientProperties]
    public sealed class LenientProbe : EndpointOptions
    {
        public int Port { get; set; }

        public override void Validate() { }
    }

    public sealed class RenamedProbe : EndpointOptions
    {
        public bool Durable { get; set; }

        protected override string? UnknownParameterHint(string name)
            => name == "streamAutoAck" ? "'streamAutoAck' is gone, use 'durable'." : null;

        public override void Validate() { }
    }

    [Fact]
    public void An_unknown_name_is_refused_with_the_nearest_option()
    {
        var act = Bind(new Probe(), "prot", "abc");

        act.Should().Throw<ArgumentException>().WithMessage("'prot' is not an option of the Probe endpoint. Did you mean 'port'?*");
    }

    [Fact]
    public void Every_unknown_name_is_named_by_name_only()
    {
        var act = () => new Probe().BindFromUri(new Dictionary<string, string> { ["prot"] = "s3cret", ["zzz"] = "s3cret" });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*'prot'*'zzz' is not an option of the Probe endpoint.*")
            .Which.Message.Should().NotContain("s3cret", "the value may be a secret");
    }

    [Fact]
    public void A_connector_explains_a_name_it_used_to_take()
    {
        var act = Bind(new RenamedProbe(), "streamAutoAck", "false");

        act.Should().Throw<ArgumentException>().WithMessage("*'streamAutoAck' is gone, use 'durable'.*");
    }

    [Fact]
    public void A_lenient_options_type_keeps_unknown_names_for_the_connector()
    {
        var probe = new LenientProbe();

        Bind(probe, "param.id", "7")();

        probe.UnmappedParameters.Should().ContainKey("param.id");
        EndpointOptions.IsLenient(typeof(LenientProbe)).Should().BeTrue();
        EndpointOptions.IsLenient(typeof(Probe)).Should().BeFalse();
    }

    [Fact]
    public void A_lenient_options_type_still_refuses_a_bad_value_of_a_known_option()
    {
        var act = Bind(new LenientProbe(), "port", "abc");

        act.Should().Throw<ArgumentException>().WithMessage("*'port=abc'*");
    }
}
