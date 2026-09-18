using redb.Route.Core;
using SerialNumbers.Core;
using SerialNumbers.Core.Catalog;

namespace SerialNumbers.Worker;

/// <summary>
/// Product status changes typed into the console, for example <c>status 04607005550001 Active</c>.
/// <para>
/// A command goes through a <see cref="ProducerTemplate"/> to <c>direct:set-product-status</c>, the
/// endpoint the REST API calls: code running in the same process as the routes uses them exactly as a
/// web page does over HTTP, and gets the same answer back.
/// </para>
/// </summary>
public static class ProductConsole
{
    public static Task RunAsync(RouteContext hub, CancellationToken stopping) => Task.Run(async () =>
    {
        using var producer = new ProducerTemplate(hub);
        producer.Start();

        while (!stopping.IsCancellationRequested)
        {
            var line = Console.ReadLine();
            if (line is null)
                return;   // no console input, for example when started without a terminal

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0)
                continue;

            if (words is not ["status", var gtin, var status])
            {
                Console.WriteLine("Usage: status <gtin> <Draft|Active|Obsolete>");
                continue;
            }

            var message = new Message(new ProductStatusChange(status, ChangedBy: "console"));
            message.Headers[SerialHeaders.Gtin] = gtin;

            try
            {
                // ProductStatusResult, or ApiError for an unknown product or status.
                Console.WriteLine(await producer.RequestBody(RouteUris.SetProductStatus, message, stopping));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The route logged the failure; the console shows it and keeps reading commands.
                Console.WriteLine($"The status change failed: {ex.Message}");
            }
        }
    }, stopping);
}
