using System.Net;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace redb.Route.SignalR;

/// <summary>
/// Redis backplane for the hubs of the SignalR transport.
/// </summary>
public static class RedisBackplaneExtensions
{
    /// <summary>
    /// Scales every hub of the SignalR transport out through Redis: a broadcast (<c>Clients.All</c>, <c>Group</c>,
    /// <c>User</c>) sent on one replica reaches the clients of that hub on every replica.
    /// <para>
    /// The Redis channels of a hub are keyed on its path. SignalR names them after the hub class, and every hub of the
    /// transport is the same class, so without that key two hubs on one Redis would see each other's broadcasts. A
    /// <c>ChannelPrefix</c> set in <paramref name="configure"/> (the name of the application or the stand) comes first
    /// and the hub path follows it: <c>myapp</c> and <c>/chat</c> give <c>myapp/chat</c>. The hub path is always part
    /// of the prefix, so replicas of a hub meet on its channels whatever port each of them listens on.
    /// </para>
    /// <para>
    /// A hub refuses to start when another hub of this process already holds its channels on the same Redis: the same
    /// path on two ports with the same prefix. Give the hubs different paths or different prefixes.
    /// </para>
    /// </summary>
    /// <param name="options">The SignalR registration options.</param>
    /// <param name="configuration">Redis connection string (<c>redis:6379,password=...</c>).</param>
    /// <param name="configure">Further Redis options: a <c>ChannelPrefix</c> of the application, a connection factory.</param>
    public static SignalRRegistrationOptions UseRedisBackplane(this SignalRRegistrationOptions options,
        string configuration, Action<RedisOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        return options.ConfigureHubServices(Backplane(configuration, configure));
    }

    /// <summary>
    /// Scales every hub of the SignalR transport out through Redis, with the connection described by
    /// <paramref name="configure"/> alone (<c>Configuration</c> or <c>ConnectionFactory</c>). The channels are keyed on
    /// the hub path as in <see cref="UseRedisBackplane(SignalRRegistrationOptions, string, Action{RedisOptions})"/>.
    /// </summary>
    /// <param name="options">The SignalR registration options.</param>
    /// <param name="configure">The Redis options.</param>
    public static SignalRRegistrationOptions UseRedisBackplane(this SignalRRegistrationOptions options,
        Action<RedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);
        return options.ConfigureHubServices(Backplane(null, configure));
    }

    /// <summary>
    /// The same backplane for a component built by hand (<c>new SignalRComponent { ... }</c>, as a Tsak module does):
    /// it is added to the component's <see cref="SignalRComponent.HubServicesConfigurator"/>, after what is there.
    /// See <see cref="UseRedisBackplane(SignalRRegistrationOptions, string, Action{RedisOptions})"/>.
    /// </summary>
    /// <param name="component">The SignalR component.</param>
    /// <param name="configuration">Redis connection string (<c>redis:6379,password=...</c>).</param>
    /// <param name="configure">Further Redis options: a <c>ChannelPrefix</c> of the application, a connection factory.</param>
    public static SignalRComponent UseRedisBackplane(this SignalRComponent component, string configuration,
        Action<RedisOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        return AddTo(component, Backplane(configuration, configure));
    }

    /// <summary>
    /// The backplane of <see cref="UseRedisBackplane(SignalRRegistrationOptions, Action{RedisOptions})"/> for a component
    /// built by hand.
    /// </summary>
    /// <param name="component">The SignalR component.</param>
    /// <param name="configure">The Redis options.</param>
    public static SignalRComponent UseRedisBackplane(this SignalRComponent component, Action<RedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(configure);
        return AddTo(component, Backplane(null, configure));
    }

    private static SignalRComponent AddTo(SignalRComponent component, Action<IServiceCollection, SignalRHubDescriptor> backplane)
    {
        var previous = component.HubServicesConfigurator;
        component.HubServicesConfigurator = previous is null
            ? backplane
            : (services, hub) => { previous(services, hub); backplane(services, hub); };
        return component;
    }

    private static Action<IServiceCollection, SignalRHubDescriptor> Backplane(string? configuration,
        Action<RedisOptions>? configure) => (services, hub) =>
    {
        var signalR = services.AddSignalR();
        if (configuration is not null)
            signalR.AddStackExchangeRedis(configuration, redis => configure?.Invoke(redis));
        else
            signalR.AddStackExchangeRedis(configure!);

        // After every Configure, so the prefix of the application is known and the hub path is added to it.
        services.PostConfigure<RedisOptions>(redis =>
            redis.Configuration.ChannelPrefix = RedisBackplaneChannels.Prefix(redis.Configuration.ChannelPrefix, hub.Path));
        services.AddSingleton<IHostedService>(sp =>
            new RedisBackplaneClaim(hub, sp.GetRequiredService<IOptions<RedisOptions>>().Value,
                sp.GetService<RedisBackplaneClaims>() ?? RedisBackplaneClaims.Process));
    };
}

/// <summary>The channel prefix of a hub on the Redis backplane.</summary>
internal static class RedisBackplaneChannels
{
    /// <summary>The prefix of the application, if any, followed by the hub path.</summary>
    public static RedisChannel Prefix(RedisChannel application, string hubPath)
        => RedisChannel.Literal(application.IsNullOrEmpty ? hubPath : application.ToString() + hubPath);

    /// <summary>The Redis a backplane publishes to: its endpoints, in a stable order.</summary>
    public static string Server(RedisOptions redis)
        => string.Join(",", redis.Configuration.EndPoints.Select(Format).Order(StringComparer.OrdinalIgnoreCase));

    private static string Format(EndPoint endpoint) => endpoint switch
    {
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        _ => endpoint.ToString() ?? "",
    };
}

/// <summary>
/// The channels the hubs of one process hold on their Redis servers. A process has one; a test that plays several
/// replicas in one process gives each hub container its own.
/// </summary>
internal sealed class RedisBackplaneClaims
{
    /// <summary>The claims of this process.</summary>
    public static RedisBackplaneClaims Process { get; } = new();

    private readonly Dictionary<string, SignalRHubDescriptor> _holders = new(StringComparer.Ordinal);

    /// <summary>Claims <paramref name="key"/> for <paramref name="hub"/>; the hub that already holds it, if any.</summary>
    public SignalRHubDescriptor? TryClaim(string key, SignalRHubDescriptor hub)
    {
        lock (_holders)
        {
            if (_holders.TryGetValue(key, out var holder))
                return holder;
            _holders[key] = hub;
            return null;
        }
    }

    /// <summary>Releases <paramref name="key"/> when <paramref name="hub"/> holds it.</summary>
    public void Release(string key, SignalRHubDescriptor hub)
    {
        lock (_holders)
        {
            if (_holders.TryGetValue(key, out var holder) && ReferenceEquals(holder, hub))
                _holders.Remove(key);
        }
    }
}

/// <summary>
/// Holds the channels of one hub on one Redis for as long as the hub runs, and refuses a second hub of this process
/// that would publish on the same ones: the two would see each other's broadcasts. Started and stopped with the hub
/// as a hosted service of its container.
/// </summary>
internal sealed class RedisBackplaneClaim(SignalRHubDescriptor hub, RedisOptions redis, RedisBackplaneClaims claims)
    : IHostedService
{
    private readonly string _key = RedisBackplaneChannels.Server(redis) + "|" + redis.Configuration.ChannelPrefix;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (claims.TryClaim(_key, hub) is { } holder)
            throw new InvalidOperationException(
                $"SignalR hub {hub.Path} on {hub.Host}:{hub.Port} cannot use the Redis backplane channels " +
                $"'{redis.Configuration.ChannelPrefix}' on {Describe(redis)}: the hub {holder.Path} on " +
                $"{holder.Host}:{holder.Port} of this process already publishes on them, and the two hubs would " +
                "see each other's broadcasts. Give the hubs different paths, or different ChannelPrefix values " +
                "in UseRedisBackplane.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        claims.Release(_key, hub);
        return Task.CompletedTask;
    }

    private static string Describe(RedisOptions redis)
        => RedisBackplaneChannels.Server(redis) is { Length: > 0 } server ? server : "the Redis of the connection factory";
}
