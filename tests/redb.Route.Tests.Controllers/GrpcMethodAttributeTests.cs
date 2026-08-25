using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Tests.Controllers;

/// <summary>
/// <see cref="GrpcMethodAttribute"/> pins the name gRPC callers dispatch on, so renaming the C# method is
/// not a breaking change for them. Mirrors what <c>SoapOperationAttribute</c> does for SOAP operations.
/// </summary>
public class GrpcMethodAttributeTests
{
    private sealed class PinnedController : RedbController
    {
        [GrpcMethod("ListUsers")]
        public string ListUsersV2() => "listed";

        public string Plain() => "plain";
    }

    private static async Task<string> Dispatch(string method)
    {
        var dispatcher = new GrpcControllerDispatcher(new RouteContext(), typeof(PinnedController));
        var exchange = new Exchange(new Message(Array.Empty<byte>()));
        exchange.In.Headers[GrpcControllerDispatcher.MethodHeader] = method;

        await dispatcher.Process(exchange);

        // The dispatcher passes string results through verbatim and JSON-encodes everything else.
        return exchange.Out!.Body switch
        {
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            var other => other?.ToString() ?? string.Empty,
        };
    }

    [Fact]
    public async Task Attribute_name_dispatches()
    {
        (await Dispatch("ListUsers")).Should().Contain("listed");
        (await Dispatch("Pinned.ListUsers")).Should().Contain("listed");
    }

    [Fact]
    public async Task Csharp_name_is_no_longer_the_wire_name()
    {
        // The point of pinning: the method can be renamed freely because callers never used its C# name.
        var exchange = new Exchange(new Message(Array.Empty<byte>()));
        exchange.In.Headers[GrpcControllerDispatcher.MethodHeader] = "ListUsersV2";

        await new GrpcControllerDispatcher(new RouteContext(), typeof(PinnedController)).Process(exchange);

        exchange.Out!.GetHeader<int>("status.code").Should().Be(404);
    }

    [Fact]
    public async Task Methods_without_the_attribute_keep_their_name()
    {
        (await Dispatch("Plain")).Should().Contain("plain");
    }
}
