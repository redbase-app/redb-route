using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// FCM component registered with scheme <c>fcm</c>.
/// Creates producer-only endpoints for sending push notifications.
/// </summary>
internal sealed class FcmComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "fcm";

    /// <summary>Shared credential provider. Set by DI registration or manually.</summary>
    internal IFirebaseCredentialProvider? CredentialProvider { get; set; }

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new FcmEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new FcmEndpoint(uri, this, options);
    }
}

/// <summary>
/// FCM endpoint. Producer-only — consumers are not supported
/// (FCM messages are received on client devices, not server-side).
/// </summary>
internal sealed class FcmEndpoint : EndpointBase<FcmEndpointOptions>
{
    internal FcmEndpoint(EndpointUri uri, FcmComponent component, FcmEndpointOptions options)
        : base(uri, component, options) { }

    /// <summary>The owning FCM component.</summary>
    internal FcmComponent FcmComponent => (FcmComponent)Component;

    /// <summary>Typed options for external access.</summary>
    internal FcmEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer()
        => new FcmProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => throw new NotSupportedException(
            "FCM does not support server-side consumers. Messages are received on client devices.");
}
