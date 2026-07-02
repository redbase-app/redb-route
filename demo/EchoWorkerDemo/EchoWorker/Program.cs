// ============================================================================
//  EchoWorker — a debug host for the EchoModule Tsak module.
//
//  It reproduces, in the smallest possible way, what the Tsak worker does:
//    1) stand redb up on SQLite (Free tier — the worker's default),
//    2) create the redb system tables once,
//    3) hand a RouteContext to EchoModule.InitRoute.main — the SAME entry point
//       the worker calls, so no route code is duplicated here.
//
//  Run it, then (PowerShell — JSON in single quotes; cmd.exe needs \" escaping instead):
//    POST: curl.exe -X POST http://localhost:5099/api/notes -H "Content-Type: application/json" -d '{"tag":"work","text":"hello"}'
//    GET:  curl.exe "http://localhost:5099/api/notes?tag=work"
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using redb.Core;                          // IRedbService
using redb.Core.Extensions;               // AddRedb
using redb.Core.Models.Configuration;     // PropsSaveStrategy
using redb.SQLite.Pro.Extensions;         // UseSqlite (tier-agnostic: AddRedb → Free)
using redb.SQLite.Data;                   // SqliteDataSource.NativeExtensionPath

using redb.Route.Core;                    // RouteContext

namespace EchoWorker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // ── Free SQLite needs the native redb extension. The packaged Tsak worker ships
        //    it; running from source we point at the one built under redb.SQLite/native/build.
        SqliteDataSource.NativeExtensionPath ??= ResolveNativeExtension();

        // ── DI: console logging + redb on SQLite (single-file DB next to the exe) ──
        var services = new ServiceCollection();

        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information));

        services.AddRedb(o => o
            .UseSqlite("Data Source=echo_demo.db")
            .Configure(c => c.PropsSaveStrategy = PropsSaveStrategy.DeleteInsert));

        var sp = services.BuildServiceProvider();

        // ── Create the redb system tables once (the worker does this on boot) ──
        //    ensureCreated: true builds the 13 base tables on a fresh SQLite file.
        await sp.GetRequiredService<IRedbService>().InitializeAsync(ensureCreated: true);

        // ── Build a route context over that provider and call the module entry point ──
        var ctx = new RouteContext(sp, contextId: "echo-worker");
        ctx.AddService(typeof(ILoggerFactory), sp.GetRequiredService<ILoggerFactory>());
        EchoModule.InitRoute.main(ctx);       // <- the exact method the Tsak worker calls

        await ctx.Start();

        Console.WriteLine();
        Console.WriteLine("EchoWorker running: http://localhost:5099/api/notes");
        Console.WriteLine("  POST  {\"tag\":\"work\",\"text\":\"hello\"}   → save");
        Console.WriteLine("  GET   ?tag=work                          → list by tag");
        Console.WriteLine("Ctrl+C to exit.");
        Console.WriteLine();

        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();

        await ctx.DisposeAsync();
    }

    // Walk up from the app dir to the repo's built Free SQLite native extension.
    // Returns null when running from a packaged worker (it resolves the extension itself).
    private static string? ResolveNativeExtension()
    {
        var suffix = OperatingSystem.IsWindows() ? ".dll"
                   : OperatingSystem.IsMacOS()   ? ".dylib"
                   : ".so";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "redb.SQLite", "native", "build", "redb" + suffix);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
