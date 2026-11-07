using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.Http;
using redb.Route.Quartz;
using redb.Route.RedbCore.Extensions;
using redb.Route.Sftp;
using redb.Route.Sql;
using redb.Route.Sql.Connection;
using SerialNumbers.Core.Database;
using SerialNumbers.Core.Infrastructure;
using SerialNumbers.Core.Routes.Catalog;
using SerialNumbers.Core.Routes.Inbound;
using SerialNumbers.Core.Routes.Outbound;
using SerialNumbers.Core.Routes.Processing;
using SerialNumbers.Core.Routes.Reports;
using SerialNumbers.Core.Services;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Core;

/// <summary>
/// Tsak module entry point. The worker finds it by convention (a public static class
/// <c>InitRoute</c> with a <c>main(IRouteContext)</c> method, synchronous or, as here, returning
/// <c>Task&lt;IRouteContext&gt;</c>) and awaits it when the module loads; the debug host
/// <c>SerialNumbers.Worker</c> calls the same method, so the route code exists once.
/// <para>
/// The order matters: components, then connections, then the database, then the routes. Partners
/// are read once here: each gets its own connection factory and its own inbound and delivery routes,
/// so a new partner is new data plus a module reload, not new code.
/// </para>
/// </summary>
public static class InitRoute
{
    public static async Task<IRouteContext> main(IRouteContext context)
    {
        var settings = ModuleSettings.FromContext(context);
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger("SerialNumbers");
        var services = context.GetServiceProvider()
            ?? throw new InvalidOperationException("The route context has no service provider; redb is not available.");

        // One HTTP server per port for the whole process, shared by the AS2 endpoints and the product API.
        // The host registers it: the Tsak worker does, and so does the debug host (AddRedbRouteHttpHosting).
        var httpServers = services.GetRequiredService<SharedHttpServerManager>();

        context.AddComponent(new SftpComponent());
        context.AddComponent(new FileComponent());
        context.AddComponent(new SqlComponent());
        context.AddComponent(new As2Component { ServerManager = httpServers });
        if (!context.HasComponent("http"))
            context.AddComponent(new HttpComponent { ServerManager = httpServers });
        context.AddComponent(new CronComponent());

        // The sql: component reads the outbox from the same database redb lives in.
        DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient", SqlClientFactory.Instance);
        context.AddToRegistry(RegistryNames.SerialsDatabase, (ISqlConnectionFactory)new SqlConnectionFactory(
            new SqlConnectionOptions
            {
                ConnectionString = settings.SqlConnectionString,
                ProviderName = "Microsoft.Data.SqlClient",
            }));

        var partners = await PrepareDatabaseAsync(services);
        RegisterPartnerConnections(context, settings, partners);

        // Route builders run Configure() when the context starts and read the settings and the
        // partners from the context there, so they take nothing through their constructors.
        context.SetProperty(ContextProperties.Partners, partners);

        var routes = (RouteContext)context;
        routes.AddRoutes(new ExceptionRouteBuilder());
        routes.AddRoutes(new SftpInboundRouteBuilder());
        routes.AddRoutes(new As2InboundRouteBuilder());
        routes.AddRoutes(new IntakeRouteBuilder());
        routes.AddRoutes(new SerialNumberRequestRouteBuilder());
        routes.AddRoutes(new ProductStatusRouteBuilder());
        routes.AddRoutes(new ReleaseHeldRequestsRouteBuilder());
        routes.AddRoutes(new OutboxRouteBuilder());
        routes.AddRoutes(new DeliveryRouteBuilder());
        routes.AddRoutes(new QuotaReportRouteBuilder());

        logger?.LogInformation("SerialNumbers module ready: {Partners} partners, archive at {Archive}, product API on port {ApiPort}",
            partners.Count, settings.ArchiveDirectory, settings.ApiPort);

        return context;
    }

    /// <summary>
    /// Schemes, flat tables and the partner list, through a scope of our own: the bootstrap
    /// connection goes back to the pool as soon as this is done instead of staying checked out for
    /// the life of the context.
    /// <para>
    /// The <see cref="IRedbService"/> here, like the one every <c>ProcessWithRedb</c> step gets, is the
    /// host's default redb, registered in its DI container. Under Tsak its provider is
    /// <c>Tsak:Redb:Provider</c>; the schema script below is T-SQL, so the worker runs it on <c>mssql</c>.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<Partner>> PrepareDatabaseAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        await redb.SyncSchemeAsync<Partner>();
        await redb.SyncSchemeAsync<Product>();
        await redb.SyncSchemeAsync<InboundMessage>();
        await redb.SyncSchemeAsync<SerialNumberRequest>();
        await redb.SyncSchemeAsync<SerialNumberResponse>();

        await redb.Context.ExecuteAsync(SqlScripts.Schema);

        var partners = await redb.Query<Partner>().ToListAsync();
        return partners.Select(o => o.Props).ToList();
    }

    /// <summary>One named connection factory per partner. Routes reference it by the partner code.</summary>
    private static void RegisterPartnerConnections(IRouteContext context, ModuleSettings settings, IReadOnlyList<Partner> partners)
    {
        var hubCertificate = partners.Any(p => p.Transport == Transports.As2)
            ? As2Certificates.LoadHub(settings.As2CertificateDirectory, settings.As2CertificatePassword)
            : null;

        foreach (var partner in partners)
        {
            if (partner.Transport == Transports.As2)
            {
                context.AddToRegistry(partner.Code, new As2ConnectionFactory
                {
                    OurCertificate = hubCertificate,
                    PartnerCertificate = As2Certificates.LoadPartner(settings.As2CertificateDirectory, partner.Code),
                    As2From = settings.As2HubId,
                    As2To = partner.As2Id!,
                    PartnerUrl = partner.As2Url!,
                    Sign = true,
                    Encrypt = true,
                    SignedMdn = true,
                    MdnMode = As2MdnMode.Sync,
                    RequireValidMdn = true,
                });
            }
            else
            {
                context.AddToRegistry(partner.Code, new SftpConnectionFactory
                {
                    Host = settings.SftpHost,
                    Port = settings.SftpPort,
                    Username = settings.SftpUsername,
                    Password = settings.SftpPasswordFor(partner.Code),
                });
            }
        }
    }
}
