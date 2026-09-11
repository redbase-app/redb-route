using System.Diagnostics;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// The transport-span helper is the one funnel every connector's Client/Consumer span goes
/// through. <c>redb.route.endpoint</c> was already sanitized; <c>messaging.destination.name</c>
/// used to carry the raw destination — with the userinfo password when the URL had one.
/// </summary>
[Collection("Telemetry")]
public class TransportSpanSanitizationTests
{
    [Fact]
    public void Destination_IsSanitized()
    {
        using var probe = new RouteTelemetryProbe(a => a.DisplayName == "sanitize-probe");

        using (var activity = RouteTelemetryExtensions.StartTransportSpan(
                   "sanitize-probe", ActivityKind.Client, "http.method", "POST",
                   endpointUri: "http:partner.example/orders",
                   destination: "http://edi:s3cr3t@partner.example/orders?apiKey=k123"))
        {
            activity.Should().NotBeNull();
        }

        var span = probe.Activities.Should().ContainSingle().Subject;
        var destination = RouteTelemetryProbe.Tag(span, "messaging.destination.name");
        destination.Should().NotBeNull();
        destination.Should().NotContain("s3cr3t", "userinfo-пароль не должен уезжать в спаны");
        destination.Should().NotContain("k123", "чувствительный query-параметр тоже маскируется");
        destination.Should().Contain("partner.example/orders", "маскировка сохраняет формат, а не стирает адрес");
    }

    [Fact]
    public void PlainDestination_PassesThroughUnchanged()
    {
        using var probe = new RouteTelemetryProbe(a => a.DisplayName == "sanitize-probe-plain");

        using (RouteTelemetryExtensions.StartTransportSpan(
                   "sanitize-probe-plain", ActivityKind.Producer, "messaging.system", "kafka",
                   endpointUri: "kafka:orders", destination: "orders"))
        {
        }

        var span = probe.Activities.Should().ContainSingle().Subject;
        RouteTelemetryProbe.Tag(span, "messaging.destination.name").Should().Be("orders",
            "не-URI назначения (имена очередей/топиков) должны оставаться байт-в-байт");
    }
}
