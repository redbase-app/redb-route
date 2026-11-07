using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace redb.Route.Core;

/// <summary>
/// The service provider <see cref="ActivatorUtilities"/> gets when redb.Route builds a bean or a component: the host's
/// provider, except for a service type that involves a type of a collectible load context (a module's own type, or a
/// generic closed over one, such as <c>ILogger&lt;TheModulesBean&gt;</c>).
/// <para>
/// The container of Microsoft.Extensions.DependencyInjection keeps every service type it was asked for, found or not,
/// for as long as it lives. The host's container lives as long as the process, so one question about
/// <c>ILogger&lt;T&gt;</c> of a module's type kept the module's load context in memory after its unload. Such a type
/// cannot be registered in the host's container, which was built before the module loaded, so the question never goes
/// there. What the host's open generic registrations would answer is built here instead: <c>ILogger&lt;T&gt;</c> from the
/// host's <see cref="ILoggerFactory"/>; <c>IOptions&lt;T&gt;</c>, <c>IOptionsSnapshot&lt;T&gt;</c>,
/// <c>IOptionsMonitor&lt;T&gt;</c>, <c>IOptionsFactory&lt;T&gt;</c> and <c>IOptionsMonitorCache&lt;T&gt;</c> over the type's
/// default instance, since nothing in the host configures a module's type; an empty <c>IEnumerable&lt;T&gt;</c>.
/// Anything else over a module's type is not a service.
/// </para>
/// </summary>
internal sealed class CollectibleTypeServiceProvider : IServiceProvider, IServiceProviderIsKeyedService, IKeyedServiceProvider
{
    private readonly IServiceProvider _host;

    private CollectibleTypeServiceProvider(IServiceProvider host) => _host = host;

    /// <summary>The provider to hand <see cref="ActivatorUtilities"/> instead of <paramref name="host"/>.</summary>
    internal static IServiceProvider For(IServiceProvider host)
        => host as CollectibleTypeServiceProvider ?? new CollectibleTypeServiceProvider(host);

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProviderIsService) || serviceType == typeof(IServiceProviderIsKeyedService))
            return _host.GetService(serviceType) is null ? null : this;
        if (!InvolvesCollectible(serviceType))
            return _host.GetService(serviceType);

        if (!serviceType.IsGenericType)
            return null;
        var definition = serviceType.GetGenericTypeDefinition();
        var argument = serviceType.GetGenericArguments()[0];
        if (definition == typeof(ILogger<>))
            return _host.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
                ? Activator.CreateInstance(typeof(Logger<>).MakeGenericType(argument), factory)
                : null;
        if (definition == typeof(IEnumerable<>))
            return Array.CreateInstance(argument, 0);
        if (OptionsServices.Contains(definition))
            return DefaultOptions(definition, argument);
        return null;
    }

    /// <summary>The options services the host registers as open generics (<c>AddOptions</c>).</summary>
    private static readonly HashSet<Type> OptionsServices =
    [
        typeof(IOptions<>), typeof(IOptionsSnapshot<>), typeof(IOptionsMonitor<>), typeof(IOptionsFactory<>),
        typeof(IOptionsMonitorCache<>),
    ];

    /// <summary>
    /// What the host's options services would answer for a module's type: nothing in the host configures it (the host
    /// was built before the module loaded), so the value is the type's default instance. Built here, as the host's
    /// open generic registrations build it, over empty configure, post-configure and change-token lists.
    /// </summary>
    private static object? DefaultOptions(Type definition, Type options)
    {
        Type Closed(Type open) => open.MakeGenericType(options);
        object Empty(Type open) => Array.CreateInstance(Closed(open), 0);
        try
        {
            if (definition == typeof(IOptionsMonitorCache<>))
                return Activator.CreateInstance(Closed(typeof(OptionsCache<>)));
            var factory = Activator.CreateInstance(Closed(typeof(OptionsFactory<>)),
                Empty(typeof(IConfigureOptions<>)), Empty(typeof(IPostConfigureOptions<>)))!;
            if (definition == typeof(IOptionsFactory<>))
                return factory;
            if (definition == typeof(IOptionsMonitor<>))
                return Activator.CreateInstance(Closed(typeof(OptionsMonitor<>)), factory,
                    Empty(typeof(IOptionsChangeTokenSource<>)), Activator.CreateInstance(Closed(typeof(OptionsCache<>))));
            return Activator.CreateInstance(Closed(typeof(OptionsManager<>)), factory); // IOptions<T>, IOptionsSnapshot<T>
        }
        catch (ArgumentException)
        {
            return null; // not an options type (a struct): the host could not build it either
        }
    }

    public bool IsService(Type serviceType)
    {
        if (!InvolvesCollectible(serviceType))
            return _host.GetService(typeof(IServiceProviderIsService)) is IServiceProviderIsService host && host.IsService(serviceType);
        if (!serviceType.IsGenericType)
            return false;
        var definition = serviceType.GetGenericTypeDefinition();
        return definition == typeof(IEnumerable<>)
               || OptionsServices.Contains(definition)
               || (definition == typeof(ILogger<>) && _host.GetService(typeof(ILoggerFactory)) is not null);
    }

    public bool IsKeyedService(Type serviceType, object? serviceKey)
        => !InvolvesCollectible(serviceType)
           && _host.GetService(typeof(IServiceProviderIsKeyedService)) is IServiceProviderIsKeyedService host
           && host.IsKeyedService(serviceType, serviceKey);

    public object? GetKeyedService(Type serviceType, object? serviceKey)
        => !InvolvesCollectible(serviceType) && _host is IKeyedServiceProvider keyed
            ? keyed.GetKeyedService(serviceType, serviceKey)
            : null;

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
    {
        if (!InvolvesCollectible(serviceType) && _host is IKeyedServiceProvider keyed)
            return keyed.GetRequiredKeyedService(serviceType, serviceKey);
        throw new InvalidOperationException(
            $"No keyed service for type '{serviceType}' has been registered: the host's container cannot hold a type of a collectible module.");
    }

    /// <summary>Whether <paramref name="type"/> is, or is built over, a type of a collectible load context.</summary>
    internal static bool InvolvesCollectible(Type type)
    {
        if (type.HasElementType)
            return InvolvesCollectible(type.GetElementType()!);
        if (type.Assembly.IsCollectible)
            return true;
        return type.IsGenericType && !type.IsGenericTypeDefinition && type.GetGenericArguments().Any(InvolvesCollectible);
    }
}
