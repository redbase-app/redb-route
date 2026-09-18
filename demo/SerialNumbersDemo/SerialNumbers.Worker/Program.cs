// ============================================================================
//  SerialNumbers.Worker: the debug host of the SerialNumbers module.
//
//  1) reads its own configuration: appsettings.json, then environment variables,
//  2) creates the demo database and brings redb up on SQL Server with two partners and three products,
//  3) builds the context configuration the way the Tsak worker does (module config file, then the
//     Override section of appsettings.json) and sets it on the route context,
//  4) awaits SerialNumbers.Core.InitRoute.main(ctx), the method the Tsak worker calls,
//  5) starts two simulated partners next to the hub: ACME on SFTP, GLOBEX on AS2,
//  6) reads product status commands from the console and sends them through a ProducerTemplate.
//
//  Then drop a file from samples/ into runtime/partners/<partner>/outbox and watch it travel.
//
//  "dotnet run --project SerialNumbers.Worker -- seed" stops after the database, the demo data, the AS2
//  certificates and the SFTP folders: what the module needs before it runs under Tsak.
// ============================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Extensions;
using redb.MSSql.Extensions;
using redb.Route.Core;
using redb.Route.Http;
using SerialNumbers.Core;

namespace SerialNumbers.Worker;

public static class Program
{
    /// <summary>
    /// No arguments: run the hub and the simulated partners. <c>seed</c>: prepare what the module needs
    /// before it runs under Tsak (the database, the demo partners and products, the AS2 certificates and
    /// the ACME SFTP folders), then exit.
    /// </summary>
    public static async Task Main(string[] args)
    {
        var seedOnly = args is ["seed"];
        if (args.Length > 0 && !seedOnly)
            throw new ArgumentException($"Unknown arguments '{string.Join(' ', args)}'. Run without arguments, or with 'seed'.");

        // Environment variables win over the file, as on the Tsak worker:
        // ConnectionStrings__MSSql, Tsak__Contexts__serial-numbers__Override__Report__Cron, ...
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var sqlConnection = configuration.GetConnectionString(ModuleSettings.ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ModuleSettings.ConnectionStringName} is not set.");
        await DemoDatabase.EnsureCreatedAsync(sqlConnection);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information));
        services.AddRedb(o => o.UseMsSql(sqlConnection));
        // The HTTP servers of the process, one per port, as the Tsak worker registers them.
        services.AddRedbRouteHttpHosting();
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.InitializeAsync(ensureCreated: true);   // the Tsak worker does this on boot
            await DemoData.SeedAsync(redb);
        }

        // The context, its configuration and its logger factory, as the Tsak worker hands them over.
        var moduleConfig = Path.Combine(AppContext.BaseDirectory, "SerialNumbers.Core.config.json");
        var contextName = ContextConfiguration.ContextNameOf(moduleConfig);
        var hub = new RouteContext(provider, contextName, provider.GetRequiredService<ILoggerFactory>());
        ContextConfiguration.ApplyTo(hub, ContextConfiguration.Build(configuration, contextName, moduleConfig));

        var settings = ModuleSettings.FromContext(hub);
        DemoCertificates.EnsureCreated(settings.As2CertificateDirectory, settings.As2CertificatePassword);

        if (seedOnly)
        {
            PartnerSimulator.EnsureAcmeSftpFolders(settings);
            await hub.DisposeAsync();
            Console.WriteLine($"Seeded: database '{new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sqlConnection).InitialCatalog}', " +
                              $"demo partners and products, AS2 certificates in {settings.As2CertificateDirectory}, ACME SFTP folders.");
            return;
        }

        await SerialNumbers.Core.InitRoute.main(hub);   // <- the exact method the Tsak worker calls

        var partnersDirectory = Path.GetFullPath(configuration["Demo:PartnersDirectory"] ?? "runtime/partners");
        var partners = PartnerSimulator.Create(provider, settings, partnersDirectory);

        await hub.Start();
        await partners.Start();

        Console.WriteLine();
        Console.WriteLine("SerialNumbers hub is running.");
        Console.WriteLine($"  ACME   (SFTP): drop a file into {Path.Combine(partnersDirectory, "acme", "outbox")}");
        Console.WriteLine($"  GLOBEX (AS2):  drop a file into {Path.Combine(partnersDirectory, "globex", "outbox")}");
        Console.WriteLine($"  Responses arrive in {Path.Combine(partnersDirectory, "<partner>", "inbox")}");
        Console.WriteLine($"  Product API:   PUT http://localhost:{settings.ApiPort}/api/products/<gtin>/status  {{\"status\":\"Active\",\"changedBy\":\"you\"}}");
        Console.WriteLine($"  Console:       status <gtin> <{ProductStatusNames}>");
        Console.WriteLine("Ctrl+C to exit.");
        Console.WriteLine();

        using var stopping = new CancellationTokenSource();
        _ = ProductConsole.RunAsync(hub, stopping.Token);

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        await stop.Task;

        await stopping.CancelAsync();
        await partners.DisposeAsync();
        await hub.DisposeAsync();
    }

    private const string ProductStatusNames = "Draft|Active|Obsolete";
}
