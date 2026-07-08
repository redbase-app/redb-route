using Microsoft.Extensions.Logging;
using redb.Route.Core;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Echo endpoint — returns the request body straight back to the caller.
/// <para>
/// Ships with <c>.AutoStart(false)</c>: the route stays dormant after the module
/// loads and only spins up when started by hand from the Tsak dashboard / CLI
/// (<c>tsak route start demo-http-echo</c>). That makes it a tidy probe for the
/// start/stop lifecycle API without touching any of the always-on demo routes.
/// </para>
/// <para>
/// Shares the demo's HTTP server on port 5088:
/// <code>curl -X POST http://localhost:5088/api/echo -d '{"hello":"world"}'</code>
/// → the same payload comes straight back.
/// </para>
/// </summary>
internal sealed class EchoRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public EchoRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        From("http:0.0.0.0:5088/api/echo?inOut=true")
            .RouteId("demo-http-echo")
            .AutoStart(false)

            // HTTP body arrives as byte[] — surface it as a string for the reply.
            .ConvertBody<string>()
            .Log("[ECHO] ▶ Received: body=${body}, contentType=${contentType}")

            // Mirror the caller's content type back; default to JSON when absent.
            .SetHeader("Content-Type", e =>
                e.In.Headers.TryGetValue("Content-Type", out var ct) && ct is string s && s.Length > 0
                    ? s
                    : "application/json")

            // inOut=true → whatever the body is at the end becomes the HTTP response,
            // and it is already the (unchanged) request body — a true echo.
            .Log("[ECHO] ◀ Echoing back: ${body}");
    }
}
