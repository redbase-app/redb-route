using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Expressions;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Tools;

namespace redb.Route.Llm.Extensions;

/// <summary>
/// Extension methods for registering the LLM transport, governance defaults
/// and redb-backed storage in a DI container.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LLM component, agent engine, tool registry and no-op
    /// defaults for every governance dependency. Call <see cref="AddRedbLlmStorage"/>
    /// afterwards to swap the in-memory defaults for redb-backed stores.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteLlm();
    ///     route.Services.AddRedbLlmStorage();
    ///     route.Services.AddLlmConnectionFactory("claude", f =&gt;
    ///     {
    ///         f.Provider = "anthropic";
    ///         f.ModelId = "claude-haiku-4.5";
    ///         f.ApiKeySecretRef = "anthropic.api-key";
    ///     });
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteLlm(this IServiceCollection services)
    {
        // Register ${conversation.*} / ${tool.*} expression prefix resolvers exactly once.
        LlmExpressionResolverSetup.EnsureRegistered();

        services.AddSingleton<LlmComponent>();
        services.AddSingleton<IAgentEngine, AgentEngine>();

        // Producer template — used by the engine to dispatch tool-bridge endpoints.
        // Started lazily on first resolution so it is ready before any tool call.
        services.TryAddSingleton<IProducerTemplate>(sp =>
        {
            var template = new ProducerTemplate(sp.GetRequiredService<IRouteContext>());
            template.Start();
            return template;
        });

        // Tool descriptor registry — auto-collects every ILlmToolDescriptor registered
        // in DI (via AddLlmTool<T>() / services.AddSingleton<ILlmToolDescriptor, X>() /
        // the .AsLlmTool(...) DSL after route bootstrap).
        services.AddSingleton<IToolDescriptorRegistry>(sp =>
        {
            var registry = new ToolDescriptorRegistry();
            foreach (var descriptor in sp.GetServices<ILlmToolDescriptor>())
                registry.Register(descriptor);
            return registry;
        });

        // Governance Noop defaults — TryAdd so consumers can swap in real impls.
        services.TryAddSingleton<IAgentObserver, NoopAgentObserver>();
        services.TryAddSingleton<IBudgetEnforcer, NoopBudgetEnforcer>();
        services.TryAddSingleton<IApprovalGate, AutoApproveGate>();
        services.TryAddSingleton<IRedactionFilter, NoopRedactionFilter>();
        services.TryAddSingleton<IShadowRunner, NoopShadowRunner>();

        // Storage InMemory fallbacks — TryAdd so AddRedbLlmStorage() (or any
        // custom registration) overrides cleanly. IToolIdempotencyStore is
        // deliberately omitted: its InMemory variant requires IIdempotentRepository
        // which the host must supply explicitly.
        services.TryAddSingleton<IConversationStore, InMemoryConversationStore>();
        services.TryAddSingleton<IApprovalStore, InMemoryApprovalStore>();
        services.TryAddSingleton<ICostBudgetStore, InMemoryCostBudgetStore>();
        services.TryAddSingleton<IPromptTemplateRegistry, InMemoryPromptTemplateRegistry>();
        services.TryAddSingleton<IToolCacheStore, InMemoryToolCacheStore>();
        services.TryAddSingleton<IEvalRunStore, InMemoryEvalRunStore>();
        services.TryAddSingleton<IKnowledgeStore, InMemoryKnowledgeStore>();
        services.TryAddSingleton<IBatchStore, InMemoryBatchStore>();

        services.AddSingleton<ILlmComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<LlmComponent>();
            context.AddComponent(component);
            return new LlmComponentRegistrar();
        });

        return services;
    }

    /// <summary>
    /// Registers an <see cref="ILlmToolDescriptor"/> implementation as a DI singleton.
    /// The descriptor is automatically picked up by the <see cref="IToolDescriptorRegistry"/>
    /// factory registered in <see cref="AddRedbRouteLlm"/>.
    /// </summary>
    public static IServiceCollection AddLlmTool<TTool>(this IServiceCollection services)
        where TTool : class, ILlmToolDescriptor
    {
        services.AddSingleton<TTool>();
        services.AddSingleton<ILlmToolDescriptor>(sp => sp.GetRequiredService<TTool>());
        return services;
    }

    /// <summary>
    /// Registers an already-constructed <see cref="ILlmToolDescriptor"/> instance.
    /// Useful for descriptors built via <c>RouteToolBridge</c> / <c>LlmTool.Define(...)</c>.
    /// </summary>
    public static IServiceCollection AddLlmTool(this IServiceCollection services, ILlmToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        services.AddSingleton<ILlmToolDescriptor>(descriptor);
        return services;
    }

    /// <summary>
    /// Scans <paramref name="assemblies"/> for non-abstract <see cref="ILlmToolDescriptor"/>
    /// implementations and registers each as a singleton via <see cref="AddLlmTool{TTool}"/>.
    /// </summary>
    public static IServiceCollection AddLlmToolsFromAssemblies(
        this IServiceCollection services,
        params System.Reflection.Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(ILlmToolDescriptor).IsAssignableFrom(type)) continue;
                services.AddSingleton(type);
                services.AddSingleton(typeof(ILlmToolDescriptor), sp => sp.GetRequiredService(type));
            }
        }
        return services;
    }

    /// <summary>
    /// Scans <typeparamref name="T"/> for class-level and method-level
    /// <see cref="ExposeAsLlmToolAttribute"/> declarations and registers a
    /// <see cref="RouteToolBridge"/> descriptor for each one. The caller is
    /// responsible for mounting the actual route on the produced endpoint URI
    /// (e.g. <c>From("direct:llm.tools.weather").Process(...)</c>) — this
    /// helper only exposes descriptors to the model.
    /// <para>
    /// The endpoint URI for each tool is built via <paramref name="endpointUriFactory"/>;
    /// when omitted the default is <c>direct:llm.tools.{toolName}</c>.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type to scan. Class-level attribute exposes the type itself
    /// as one tool; method-level attributes expose each annotated method as a separate tool.</typeparam>
    /// <param name="services">DI services.</param>
    /// <param name="endpointUriFactory">Optional override. Receives the tool name from the attribute.</param>
    public static IServiceCollection AddLlmToolsFromType<T>(
        this IServiceCollection services,
        Func<string, string>? endpointUriFactory = null)
        => AddLlmToolsFromType(services, typeof(T), endpointUriFactory);

    /// <summary>Non-generic overload of <see cref="AddLlmToolsFromType{T}"/>.</summary>
    public static IServiceCollection AddLlmToolsFromType(
        this IServiceCollection services,
        Type type,
        Func<string, string>? endpointUriFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(type);

        endpointUriFactory ??= static name => $"direct:llm.tools.{name}";

        var classAttr = (ExposeAsLlmToolAttribute?)Attribute.GetCustomAttribute(
            type, typeof(ExposeAsLlmToolAttribute), inherit: false);
        if (classAttr is not null)
        {
            services.AddLlmTool(new RouteToolBridge(
                classAttr.ToCapability(RouteToolBridge.DefaultInputSchema),
                endpointUriFactory(classAttr.Name)));
        }

        const System.Reflection.BindingFlags methodFlags =
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(methodFlags))
        {
            var attr = (ExposeAsLlmToolAttribute?)Attribute.GetCustomAttribute(
                method, typeof(ExposeAsLlmToolAttribute), inherit: false);
            if (attr is null) continue;

            services.AddLlmTool(new RouteToolBridge(
                attr.ToCapability(RouteToolBridge.DefaultInputSchema),
                endpointUriFactory(attr.Name)));
        }

        return services;
    }

    /// <summary>
    /// Replaces the in-memory storage fallbacks with redb-backed implementations of
    /// <see cref="IConversationStore"/>, <see cref="IToolIdempotencyStore"/>,
    /// <see cref="IApprovalStore"/>, <see cref="ICostBudgetStore"/> and the
    /// <see cref="RedbAuditObserver"/>. Replaces the default
    /// <see cref="IAgentObserver"/> with the audit observer.
    /// <para>
    /// The redb-backed idempotency store wraps the
    /// <see cref="IIdempotentRepository"/> already registered by
    /// <c>AddRedbIdempotentRepository(...)</c>; if no repository is registered
    /// the call will throw at first use.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRedbLlmStorage(this IServiceCollection services)
    {
        // Schemes for every props row used below are auto-synced by the host's
        // own `redb.InitializeAsync()` (it scans loaded assemblies for
        // [RedbScheme] — see RedbServiceBase.AutoSyncSchemesAsync). The host
        // owns startup; stores stay free of any "is the scheme there yet?"
        // gates.

        services.AddSingleton<IConversationStore>(sp =>
            new RedbConversationStore(sp.GetRequiredService<IRouteContext>()));

        services.AddSingleton<IToolIdempotencyStore>(sp =>
            new RedbToolIdempotencyStore(
                sp.GetRequiredService<IIdempotentRepository>(),
                sp.GetRequiredService<IRouteContext>()));

        services.AddSingleton<IApprovalStore>(sp =>
            new RedbApprovalStore(sp.GetRequiredService<IRouteContext>()));

        services.AddSingleton<ICostBudgetStore>(sp =>
            new RedbCostBudgetStore(sp.GetRequiredService<IRouteContext>()));

        services.AddSingleton<IPromptTemplateRegistry>(sp =>
            new RedbPromptTemplateRegistry(sp.GetRequiredService<IRouteContext>()));

        services.AddSingleton<IToolCacheStore>(sp =>
            new RedbToolResultCache(sp.GetRequiredService<IRouteContext>()));

        services.AddSingleton<IEvalRunStore>(sp =>
            new RedbEvalRunStore(sp.GetRequiredService<IRouteContext>()));

        services.AddSingleton<IKnowledgeStore>(sp =>
            new RedbKnowledgeStore(sp.GetRequiredService<IRouteContext>()));

        services.AddSingleton<IBatchStore>(sp =>
            new RedbBatchStore(sp.GetRequiredService<IRouteContext>()));

        services.Replace(ServiceDescriptor.Singleton<IAgentObserver>(sp =>
            new RedbAuditObserver(sp.GetRequiredService<IRouteContext>())));

        return services;
    }

    /// <summary>
    /// Registers a named <see cref="LlmConnectionFactory"/> in the route registry.
    /// The supplied <paramref name="name"/> is set on <see cref="LlmConnectionFactory.Name"/>
    /// automatically and used as the registry key (matches <c>llm://&lt;name&gt;</c>).
    /// </summary>
    public static IServiceCollection AddLlmConnectionFactory(
        this IServiceCollection services,
        string name,
        Action<LlmConnectionFactory> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<ILlmFactoryRegistrar>(sp =>
        {
            var factory = new LlmConnectionFactory { Name = name };
            configure(factory);
            factory.Name = name; // re-assert in case configure overwrote it
            sp.GetRequiredService<IRouteContext>().AddToRegistry(name, factory);
            return new LlmFactoryRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for component DI registration.</summary>
internal interface ILlmComponentRegistrar;

/// <summary>Marker registration for component DI.</summary>
internal sealed class LlmComponentRegistrar : ILlmComponentRegistrar;

/// <summary>Marker interface for factory DI registration.</summary>
internal interface ILlmFactoryRegistrar;

/// <summary>Marker registration for factory DI.</summary>
internal sealed class LlmFactoryRegistrar : ILlmFactoryRegistrar;
