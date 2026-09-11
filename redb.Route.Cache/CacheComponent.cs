using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Cache;

/// <summary>The <c>cache:</c> component: explicit get / put / remove / clear as endpoints (Camel cache-component analog).</summary>
public sealed class CacheComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "cache";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new CacheEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        return new CacheEndpoint(uri, this, options);
    }
}

/// <summary>A <c>cache:&lt;region&gt;</c> endpoint; producer only.</summary>
public sealed class CacheEndpoint : EndpointBase<CacheEndpointOptions>
{
    internal CacheEndpoint(EndpointUri uri, CacheComponent component, CacheEndpointOptions options)
        : base(uri, component, options)
    {
        Region = string.IsNullOrWhiteSpace(uri.Path) ? "default" : uri.Path.Trim('/');
    }

    /// <summary>Region taken from the URI path (<c>cache:customers</c> → <c>customers</c>).</summary>
    public string Region { get; }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new CacheProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => throw new NotSupportedException("cache: endpoints are producers only (To(\"cache:...\")).");
}
