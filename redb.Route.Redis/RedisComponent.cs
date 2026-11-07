using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Redis;

/// <summary>
/// Redis transport component for redb.Route.
/// Scheme: <c>redis</c>.
/// <para>
/// URI format (colon-path): <c>redis:SET:mykey?ttl=300&amp;connectionString=localhost:6379</c><br/>
/// URI format (standard):   <c>redis://SET/mykey?ttl=300</c>
/// </para>
/// <para>
/// The first path segment is the <see cref="RedisOperationType"/>.
/// The remaining segments form the resource key / channel / stream name.
/// </para>
/// </summary>
public sealed partial class RedisComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "redis";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new RedisEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new RedisEndpoint(uri, this, options);
    }
}
