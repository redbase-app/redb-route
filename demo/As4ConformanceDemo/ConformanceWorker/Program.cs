// ============================================================================
//  ConformanceWorker — a debug host for the ConformanceModule Tsak module.
//
//    ConformanceWorker             run the module for the eDelivery conformance testing service;
//                                  settings come from the module's config file and the Override layer in the
//                                  environment, as under Tsak (see README.md).
//    ConformanceWorker selftest    run the module against a test driver of its own on loopback and
//                                  play the scenarios of the service's sample messages. Exit code 0
//                                  when every check passed.
// ============================================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using redb.Route.Core;                    // RouteContext

namespace ConformanceWorker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information));
        var sp = services.BuildServiceProvider();

        if (args.Length > 0 && args[0] == "selftest")
            return await SelfTest.RunAsync(sp);

        // lossyproxy <port> <target> <swallow> [host]: stands in front of a real test driver and loses its first
        // responses, to see the retry profile against another MSH. Point the module's TestDriver:Url at it. The host
        // is the address to listen on: loopback by default, "+" inside a container.
        if (args.Length is 4 or 5 && args[0] == "lossyproxy")
        {
            var swallow = args[3] == "all" ? int.MaxValue : int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            await using var proxy = new LossyProxy(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args[2],
                args.Length == 5 ? args[4] : "127.0.0.1");
            proxy.Reset(swallow);
            Console.WriteLine($"Lossy proxy on port {args[1]} -> {args[2]}; responses to lose: {args[3]}. Ctrl+C to exit.");
            var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            while (!done.Wait(TimeSpan.FromSeconds(2)))
                Console.WriteLine($"requests {proxy.Requests}, different bodies {proxy.DistinctBodies}");
            return 0;
        }

        // The context configuration as the Tsak worker builds it: the module's config file, then the Override layer
        // from the environment (Tsak__Contexts__as4-conformance__Override__...).
        var host = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var moduleConfig = Path.Combine(AppContext.BaseDirectory, "ConformanceModule.config.json");
        var contextName = ContextConfiguration.ContextNameOf(moduleConfig);
        var ctx = new RouteContext(sp, contextId: contextName);
        ctx.AddService(typeof(ILoggerFactory), sp.GetRequiredService<ILoggerFactory>());
        ContextConfiguration.ApplyTo(ctx, ContextConfiguration.Build(host, contextName, moduleConfig));
        ConformanceModule.InitRoute.main(ctx);        // <- the exact method the Tsak worker calls
        await ctx.Start();

        Console.WriteLine();
        Console.WriteLine("ConformanceWorker running. Ctrl+C to exit.");
        Console.WriteLine();

        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();

        await ctx.DisposeAsync();
        return 0;
    }
}
