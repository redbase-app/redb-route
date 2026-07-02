using System.Text.Json;

using redb.Core;                          // IRedbService, Query, SaveAsync, SyncSchemeAsync
using redb.Core.Attributes;               // RedbScheme
using redb.Core.Models.Entities;          // RedbObject<T>

using redb.Route.Abstractions;            // IRouteContext, IExchange
using redb.Route.Core;                    // RouteContext
using redb.Route.Http;                    // HttpComponent, SharedHttpServerManager
using redb.Route.RedbCore.Extensions;     // ProcessWithRedb, GetRedbService

namespace EchoModule;

/// <summary>
/// Tsak module entry point.
/// <para>
/// The worker discovers it by convention — a public static class named
/// <c>InitRoute</c> with a public static <c>main(IRouteContext)</c> — and calls it
/// once when the module loads. The debug host (EchoWorker/Program.cs) calls the very
/// same method, so the route code below lives in exactly one place.
/// </para>
/// <para>
/// Two minimal endpoints on the shared HTTP server (port 5099), backed by redb/SQLite:
/// <code>
///   POST /api/notes   body {"tag":"work","text":"hello"}   → save one note
///   GET  /api/notes?tag=work                               → list notes with that tag
/// </code>
/// </para>
/// </summary>
public static class InitRoute
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IRouteContext main(IRouteContext context)
    {
        // redb schema for Note. Idempotent — safe to call every load. The worker
        // (or the debug host) has already brought redb + SQLite up by now.
        context.GetRedbService().SyncSchemeAsync<Note>().GetAwaiter().GetResult();

        // One shared HTTP server; both routes below bind to it.
        context.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });

        ((RouteContext)context).AddRoutes(r =>
        {
            // ── POST /api/notes — save one note ────────────────────────────────
            r.From("http:0.0.0.0:5099/api/notes?inOut=true&methods=POST")
                .RouteId("notes-post")
                .ConvertBody<string>()                       // HTTP body → string (JSON)
                .ProcessWithRedb(async (db, ex, ct) =>
                {
                    var note = JsonSerializer.Deserialize<Note>(ex.In.Body?.ToString() ?? "{}", Json) ?? new Note();
                    var obj = new RedbObject<Note> { name = $"note:{note.Tag}", Props = note };
                    await db.SaveAsync(obj);                 // one insert into redb (SQLite)
                    Reply(ex, new { saved = true, id = obj.id });
                }).Log("Save ${body}");

            // ── GET /api/notes?tag=work — list by tag ──────────────────────────
            r.From("http:0.0.0.0:5099/api/notes?inOut=true&methods=GET")
                .RouteId("notes-get")
                .ProcessWithRedb(async (db, ex, ct) =>
                {
                    // ?tag=... arrives as the header redbHttp.QueryParam.tag
                    var tag = ex.In.Headers.TryGetValue("redbHttp.QueryParam.tag", out var t)
                        ? t?.ToString() ?? ""
                        : "";

                    // Server-side filter: the GET parameter goes straight into Where(...).
                    var found = await db.Query<Note>()
                        .Where(n => n.Tag == tag)
                        .ToListAsync();

                    Reply(ex, found.Select(o => new { o.Props.Tag, o.Props.Text }));
                }).Log("Load ${header.redbHttp.QueryParam.tag}");
        });

        return context;
    }

    // inOut=true → whatever the body is at the end becomes the HTTP response.
    private static void Reply(IExchange ex, object body)
    {
        ex.In.ContentType = "application/json";
        ex.In.Body = JsonSerializer.Serialize(body);
    }
}

/// <summary>Persisted note. <c>[RedbScheme]</c> marks the class as a redb schema.</summary>
[RedbScheme]
public sealed class Note
{
    public string Tag { get; set; } = "";
    public string Text { get; set; } = "";
}
