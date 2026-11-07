using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Exec;

/// <summary>
/// Process-execution component. Scheme: <c>exec</c>.
/// <para>
/// Producer: synchronous request/response — runs the configured command and writes
/// the result (stdout/stderr/exit code) to <see cref="IExchange.Out"/>.
/// </para>
/// <para>
/// Consumer: scheduled poller driven by <see cref="PeriodicTimer"/> — fires the command on
/// a fixed interval and pushes the result into the route pipeline.
/// </para>
/// <para>
/// URI: <c>exec://run?command=git&amp;args=status&amp;timeoutMs=5000&amp;allowedCommands=git,ls</c>
/// </para>
/// </summary>
public sealed class ExecComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "exec";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new ExecEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new ExecEndpoint(uri, this, options);
    }
}

/// <summary>
/// Exec endpoint. Creates either a request/response producer or a scheduled consumer.
/// The host part of the URI (<c>exec://run</c>) is informational and not interpreted —
/// the actual command target comes from <see cref="ExecEndpointOptions.Command"/> or
/// the inbound exchange.
/// </summary>
public sealed class ExecEndpoint : EndpointBase<ExecEndpointOptions>
{
    /// <summary>Creates an exec endpoint.</summary>
    public ExecEndpoint(EndpointUri uri, ExecComponent component, ExecEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>Endpoint options for external access.</summary>
    internal ExecEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new ExecProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new ExecConsumer(this, Options, processor);
    }
}
