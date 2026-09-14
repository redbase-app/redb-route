// ============================================================================
//  SerialNumbers.Worker: the debug host of the SerialNumbers module.
//
//  1) reads its own configuration: appsettings.json, then environment variables,
//  2) creates the demo database and brings redb up on SQL Server with two partners and two products,
//  3) builds the context configuration the way the Tsak worker does (module config file, then the
//     Override section of appsettings.json) and sets it on the route context,
//  4) calls SerialNumbers.Core.InitRoute.main(ctx), the method the Tsak worker calls,
//  5) starts two simulated partners next to the hub: ACME on SFTP, GLOBEX on AS2.
//
//  Then drop a file from samples/ into runtime/partners/<partner>/outbox and watch it travel.
// ============================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Extensions;
using redb.MSSql.Extensions;
using redb.Route.Core;
using SerialNumbers.Core;

namespace SerialNumbers.Worker;

public static class Program
{
    public static async Task Main()
    {
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

        SerialNumbers.Core.InitRoute.main(hub);   // <- the exact method the Tsak worker calls

        var partnersDirectory = Path.GetFullPath(configuration["Demo:PartnersDirectory"] ?? "runtime/partners");
        var partners = PartnerSimulator.Create(provider, settings, partnersDirectory);

        await hub.Start();
        await partners.Start();

        Console.WriteLine();
        Console.WriteLine("SerialNumbers hub is running.");
        Console.WriteLine($"  ACME   (SFTP): drop a file into {Path.Combine(partnersDirectory, "acme", "outbox")}");
        Console.WriteLine($"  GLOBEX (AS2):  drop a file into {Path.Combine(partnersDirectory, "globex", "outbox")}");
        Console.WriteLine($"  Responses arrive in {Path.Combine(partnersDirectory, "<partner>", "inbox")}");
        Console.WriteLine("Ctrl+C to exit.");
        Console.WriteLine();

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        await stop.Task;

        await partners.DisposeAsync();
        await hub.DisposeAsync();
    }
}
