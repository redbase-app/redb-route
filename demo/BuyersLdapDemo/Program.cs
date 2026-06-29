// ============================================================================
//  BuyersLdapDemo — a simple single-file example for junior developers.
//
//  What this example demonstrates:
//    1) How to set up a redb.Route context MANUALLY (without Tsak packaging).
//    2) How to accept an HTTP request (From "http:...").
//    3) How to query Active Directory via LDAP (.To(ldap)).
//    4) How to persist the result to the database via IRedbService (ProcessWithRedb).
//
//  Business scenario (simplified):
//    - There is one admin. They send a list of buyers with AD logins and roles
//      (Admin / Buyer).
//    - We verify that these users actually exist in AD (LDAP).
//    - Verified users are saved to our database. The "who is a buyer" list lives
//      in the database because that is what we use later to decide who to run.
//
//  Manual testing (after launch). Replace ivanov@ews.ru / petrov@ews.ru
//  with real logins from your AD, otherwise they will land in "missing".
//
//  --- PowerShell (important: use curl.exe, otherwise curl = alias for Invoke-WebRequest) ---
//    POST (upload buyers):
//      curl.exe -X POST http://localhost:5099/api/buyers -H "Content-Type: application/json" -d '[{\"login\":\"ivanov@ews.ru\",\"role\":\"Buyer\"},{\"login\":\"petrov@ews.ru\",\"role\":\"Admin\"}]'
//    GET (list from DB):
//      curl.exe http://localhost:5099/api/buyers
//
//  --- PowerShell without curl (native) ---
//    $body = '[{"login":"ivanov@ews.ru","role":"Buyer"},{"login":"petrov@ews.ru","role":"Admin"}]'
//    Invoke-RestMethod -Method Post -Uri http://localhost:5099/api/buyers -ContentType 'application/json' -Body $body
//    Invoke-RestMethod -Uri http://localhost:5099/api/buyers
//
//  --- cmd.exe ---
//    POST:
//      curl -X POST http://localhost:5099/api/buyers -H "Content-Type: application/json" -d "[{\"login\":\"ivanov@ews.ru\",\"role\":\"Buyer\"},{\"login\":\"petrov@ews.ru\",\"role\":\"Admin\"}]"
//    GET:
//      curl http://localhost:5099/api/buyers
//
//  POST response: {"requested":2,"saved":N,"missing":[...]}  (saved = found in AD and persisted)
//  GET response:  [{"login":"...","role":"Buyer"}, ...]      (current list from DB)
// ============================================================================

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using redb.Core;                          // IRedbService
using redb.Core.Extensions;               // AddRedb(...)
using redb.Core.Models.Configuration;     // PropsSaveStrategy
using redb.Core.Models.Contracts;         // IRedbObject
using redb.Core.Models.Entities;          // RedbObject<T>
using redb.Postgres.Extensions;           // UsePostgres(...)

using redb.Route.Abstractions;            // IRouteContext, IExchange
using redb.Route.Core;                    // RouteContext
using redb.Route.Http;                    // HttpComponent, SharedHttpServerManager
using redb.Route.Ldap;                    // LdapComponent, LdapDsl, LdapEntry, LdapSearchScope
using redb.Route.RedbCore.Extensions;     // ProcessWithRedb(...)

// ─── 0. Settings. Values taken from tsum configs ─────────────────────────────
// LDAP — from tsum.Api/tsum.Api.config.json (section "Ldap").
const string LdapServer = "bravo.ews.ru";
const int LdapPort = 389;
const string LdapBaseDn = "DC=ews,DC=ru";
const string LdapServiceDn = "tsum-ldap@ews.ru"; // service account trusted in AD
const string LdapServicePassword = "****";

// Postgres — from tsum.deploy/appsettings.prod.json (ConnectionStrings.Postgres).
// In a real application, take the connection string from an environment variable.
const string PostgresConnection =
"Host=localhost;Port=5432;Username=<your user>;Password=*****;Database=<your database>;Pooling=true;Maximum Pool Size=100;Minimum Pool Size=1;" +
"Connection Idle Lifetime=60;Connection Pruning Interval=60;Timeout=60;Command Timeout=180;Include Error Detail=true;Keepalive=30;Tcp Keepalive=true;Options=-c jit=off";

//    Host=localhost;Port=5432;Username=lt;Password=kjubrf099;Database=tsum_local;Pooling=true;Maximum Pool Size=100;Minimum Pool Size=1;Connection Idle Lifetime=60;Connection Pruning Interval=60;Timeout=60;Command Timeout=180;Include Error Detail=true;Keepalive=30;Tcp Keepalive=true;Options=-c jit=off",
//   "MSSql": "Server=localhost;Database=redb_tsak;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=true"

const int HttpPort = 5099;

// Key prefix in the database. Makes it easy to find only our "buyer-role" records.
const string KeyPrefix = "buyer-role:";

// ─── 1. DI container: logging + redb (IRedbService) ──────────────────────────
var services = new ServiceCollection();

services.AddLogging(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));

// Register redb with PostgreSQL. After this, IRedbService can be resolved from the container.
// PropsSaveStrategy.DeleteInsert — the simplest save strategy to understand:
// always overwrites all object props in full, without change tracking.
services.AddRedb(options => options
    .UsePostgres(PostgresConnection)
    .Configure(c => c.PropsSaveStrategy = PropsSaveStrategy.DeleteInsert));

var sp = services.BuildServiceProvider();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("BuyersLdapDemo");

// ─── 2. Initialize DB and schema for our class ───────────────────────────────
// Resolve redb directly from the container to prepare the database once.
var redbInit = sp.GetRequiredService<IRedbService>();
await redbInit.InitializeAsync();                       // creates system tables (if they don't exist)
await redbInit.SyncSchemeAsync<BuyerRoleAssignment>();  // creates the redb schema for our class
logger.LogInformation("REDB ready, BuyerRoleAssignment schema synchronized");

// ─── 3. Route context created MANUALLY (without Tsak) ────────────────────────
var ctx = new RouteContext(sp, contextId: "buyers-ldap-demo");

// RouteContext looks up services in its own map; hand it the logger factory
// so that .Log("...") steps inside the route can write to the console.
ctx.AddService(typeof(ILoggerFactory), sp.GetRequiredService<ILoggerFactory>());

// Register transport components: HTTP (incoming requests) and LDAP (AD lookup).
ctx.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });
ctx.AddComponent(new LdapComponent());

// ─── 4. Prepare the LDAP search descriptor (once; reused inside the route) ───
// Searches for users in AD using a filter stored in the "ldapFilter" header.
var userSearch = LdapDsl.Search(LdapBaseDn)
    .Server(LdapServer).Port(LdapPort)
    .BindDn(LdapServiceDn).BindPassword(LdapServicePassword) // service account for reading AD
    .Filter("${header.ldapFilter}")                          // filter is taken from the message header
    .Attributes("sAMAccountName", "userPrincipalName", "displayName", "mail")
    .Referrals(false)
    .Scope(LdapSearchScope.Subtree);

// ─── 5. Define routes ────────────────────────────────────────────────────────
ctx.AddRoutes(r =>
{
    // Single HTTP endpoint /api/buyers. POST — load the list, GET — read from DB.
    r.From($"http:0.0.0.0:{HttpPort}/api/buyers?inOut=true&cors=true&corsOrigins=*")
        .RouteId("buyers-endpoint")
        .ConvertBody<string>() // read request body as string (JSON)
        .Choice()
            // ── POST branch: verify logins in AD and save to DB ───────────────
            .When(e => e.In.Headers.TryGetValue("redbHttp.Method", out var m) && m?.ToString() == "POST")
                // Step A: parse JSON and build the LDAP filter from logins.
                .Process((exchange, ct) =>
                {
                    var json = exchange.In.Body?.ToString();
                    var input = string.IsNullOrWhiteSpace(json)
                        ? []
                        : JsonSerializer.Deserialize<List<BuyerInput>>(json) ?? [];

                    // Normalize: login to lowercase, role must be Admin/Buyer, deduplicate.
                    var normalized = input
                        .Select(x => new BuyerInput(x.Login?.Trim().ToLowerInvariant() ?? "", x.Role?.Trim() ?? ""))
                        .Where(x => x.Login.Length > 0 && x.Role is "Admin" or "Buyer")
                        .DistinctBy(x => x.Login)
                        .ToList();

                    // Store the normalized list in exchange properties — used in step C.
                    exchange.Properties["buyers"] = normalized;

                    // Build a single LDAP filter for all logins: (|(userPrincipalName=a)(userPrincipalName=b)...)
                    var orParts = string.Concat(normalized.Select(x => $"(userPrincipalName={EscapeLdap(x.Login)})"));
                    exchange.In.Headers["ldapFilter"] = $"(&(objectClass=user)(|{orParts}))";

                    return Task.CompletedTask;
                })
                // Step B: the actual Active Directory lookup.
                .To(userSearch)
                // Step C: keep only users actually found and save them to the database in one batch.
                .ProcessWithRedb(async (redb, exchange, ct) =>
                {
                    var requested = exchange.Properties.TryGetValue("buyers", out var b)
                        ? b as List<BuyerInput> ?? []
                        : [];

                    // Logins that AD actually returned.
                    var found = (exchange.In.Body as List<LdapEntry> ?? [])
                        .Select(e => e.GetString("userPrincipalName") ?? e.GetString("sAMAccountName"))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!.ToLowerInvariant())
                        .ToHashSet();

                    // Existing records in the database — to update rather than create duplicates.
                    var existing = await redb.Query<BuyerRoleAssignment>()
                        .WhereRedb(o => o.ValueString!.StartsWith(KeyPrefix))
                        .ToListAsync();

                    var byLogin = existing
                        .Where(o => o.Props != null)
                        .ToDictionary(o => o.Props.Login, StringComparer.OrdinalIgnoreCase);

                    // IMPORTANT: no loop with SaveAsync inside. First collect the list,
                    // then a single SaveAsync(collection) call — that is the batch.
                    var toSave = requested
                        .Where(x => found.Contains(x.Login))
                        .Select(x =>
                        {
                            if (!byLogin.TryGetValue(x.Login, out var obj))
                            {
                                obj = new RedbObject<BuyerRoleAssignment>
                                {
                                    value_string = KeyPrefix + x.Login,
                                    name = $"Buyer role {x.Login}",
                                    Props = new BuyerRoleAssignment()
                                };
                            }

                            obj.Props.Login = x.Login;
                            obj.Props.Role = x.Role;
                            obj.Props.IsActive = true;
                            return obj;
                        })
                        .Cast<IRedbObject>()
                        .ToList();

                    if (toSave.Count > 0)
                        await redb.SaveAsync(toSave); // single batch call for all objects

                    var missing = requested.Where(x => !found.Contains(x.Login)).Select(x => x.Login).ToList();

                    SetJson(exchange, new
                    {
                        requested = requested.Count,
                        saved = toSave.Count,
                        missing
                    });

                    logger.LogInformation("POST /api/buyers: requested={Req} saved={Saved} not found in AD={Missing}",
                        requested.Count, toSave.Count, missing.Count);
                })
            // ── GET branch: return the buyer list from DB ─────────────────────
            .Otherwise()
                .ProcessWithRedb(async (redb, exchange, ct) =>
                {
                    var all = await redb.Query<BuyerRoleAssignment>()
                        .WhereRedb(o => o.ValueString!.StartsWith(KeyPrefix))
                        .ToListAsync();

                    var result = all
                        .Where(o => o.Props is { IsActive: true })
                        .Select(o => new { o.Props.Login, o.Props.Role })
                        .OrderBy(x => x.Login)
                        .ToList();

                    SetJson(exchange, result);
                    logger.LogInformation("GET /api/buyers: returned {Count} records", result.Count);
                })
        .EndChoice();
});

// ─── 6. Start and wait ───────────────────────────────────────────────────────
await ctx.Start();

Console.WriteLine();
Console.WriteLine($"BuyersLdapDemo running: http://localhost:{HttpPort}/api/buyers");
Console.WriteLine("POST — upload buyer list (verify in AD + save to DB)");
Console.WriteLine("GET  — show saved buyers from DB");
Console.WriteLine("Ctrl+C — exit.");
Console.WriteLine();

var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
stop.Wait();

await ctx.DisposeAsync();

// ============================================================================
//  Helper functions and data models
// ============================================================================

// Write an object to the response body as JSON.
static void SetJson(IExchange exchange, object body)
{
    exchange.In.ContentType = "application/json";
    exchange.In.Body = JsonSerializer.Serialize(body);
}

// Escape special characters in an LDAP filter value (RFC 4515), simplified.
static string EscapeLdap(string value) => value
    .Replace("\\", "\\5c")
    .Replace("*", "\\2a")
    .Replace("(", "\\28")
    .Replace(")", "\\29")
    .Replace("\0", "\\00");

// Admin input: one item in the upload list.
public sealed record BuyerInput(string Login, string Role);

// Class persisted to the database (redb schema).
// Schema name = fully qualified class name; [RedbScheme] marks the class as persistable.
[redb.Core.Attributes.RedbScheme]
public sealed class BuyerRoleAssignment
{
    /// <summary>User's AD login (userPrincipalName, lowercased).</summary>
    public string Login { get; set; } = "";

    /// <summary>Assigned role: Admin or Buyer.</summary>
    public string Role { get; set; } = "";

    /// <summary>Whether the record is active (for soft-disable without deletion).</summary>
    public bool IsActive { get; set; } = true;
}
