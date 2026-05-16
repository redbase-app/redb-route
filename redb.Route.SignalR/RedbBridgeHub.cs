using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.SignalR;

/// <summary>
/// Internal bridge hub that converts SignalR invocations into redb.Route exchanges.
/// Not subclassed by users — acts as a generic bridge (like gRPC RedbServiceImpl).
/// <para>
/// Clients call <c>Invoke("MethodName", arg1, arg2, ...)</c> — the hub creates an exchange
/// with the method name and arguments, processes it through the pipeline, and returns
/// the Out body as the method result (when InOut is enabled).
/// </para>
/// </summary>
internal sealed class RedbBridgeHub : Hub
{
    /// <summary>
    /// Called by SignalR clients. All client method invocations are routed here.
    /// </summary>
    /// <param name="method">Hub method name the client wants to invoke.</param>
    /// <param name="args">Arguments passed by the client.</param>
    /// <returns>Result from the pipeline (when InOut), otherwise null.</returns>
    public async Task<object?> Invoke(string method, object?[]? args)
    {
        var consumer = Context.GetHttpContext()?.RequestServices.GetService<SignalRConsumer>()
            ?? throw new HubException("SignalRConsumer not available in DI.");

        var options = consumer.EndpointOptions;

        // If a method filter is set, reject non-matching invocations
        if (options.Method is not null
            && !options.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
        {
            throw new HubException($"Method '{method}' is not supported on this hub.");
        }

        // Normalize args: deserialize any JsonElement values to basic .NET types
        var normalizedArgs = NormalizeArgs(args);

        var body = normalizedArgs switch
        {
            null or { Length: 0 } => (object?)null,
            { Length: 1 } => normalizedArgs[0],
            _ => normalizedArgs
        };

        var message = new Message(body);
        message.Headers[SignalRHeaders.Method] = method;
        message.Headers[SignalRHeaders.ConnectionId] = Context.ConnectionId;
        message.Headers[SignalRHeaders.HubPath] = consumer.HubPath;
        message.Headers[SignalRHeaders.Ssl] = options.Ssl.ToString();
        message.Headers[SignalRHeaders.Protocol] = options.MessagePack ? "messagepack" : "json";

        if (Context.UserIdentifier is not null)
            message.Headers[SignalRHeaders.UserId] = Context.UserIdentifier;

        var exchange = Exchange.Create(message, consumer.ScopeFactory);
        exchange.Pattern = options.InOut ? ExchangePattern.InOut : ExchangePattern.InOnly;

        try
        {
            await consumer.ProcessExchange(exchange, Context.ConnectionAborted).ConfigureAwait(false);

            // Post-process: group management commands
            await HandleGroupCommands(exchange).ConfigureAwait(false);

            if (exchange.Exception is not null && !exchange.ExceptionHandled)
                throw new HubException(exchange.Exception.Message);

            // InOut: return Out body
            if (options.InOut && exchange.HasOut && exchange.Out!.Body is not null)
                return exchange.Out.Body;

            return null;
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var consumer = Context.GetHttpContext()?.RequestServices.GetService<SignalRConsumer>();
        if (consumer is null) { await base.OnConnectedAsync(); return; }

        // Auto-add to default group
        if (consumer.EndpointOptions.DefaultGroup is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, consumer.EndpointOptions.DefaultGroup)
                .ConfigureAwait(false);
        }

        // Fire Connected event through pipeline
        var message = new Message(null);
        message.Headers[SignalRHeaders.Event] = "Connected";
        message.Headers[SignalRHeaders.ConnectionId] = Context.ConnectionId;
        message.Headers[SignalRHeaders.HubPath] = consumer.HubPath;

        if (Context.UserIdentifier is not null)
            message.Headers[SignalRHeaders.UserId] = Context.UserIdentifier;

        var exchange = Exchange.Create(message, consumer.ScopeFactory);

        try
        {
            await consumer.ProcessExchange(exchange, Context.ConnectionAborted).ConfigureAwait(false);
            await HandleGroupCommands(exchange).ConfigureAwait(false);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }

        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var consumer = Context.GetHttpContext()?.RequestServices.GetService<SignalRConsumer>();
        if (consumer is null) { await base.OnDisconnectedAsync(exception); return; }

        var message = new Message(null);
        message.Headers[SignalRHeaders.Event] = "Disconnected";
        message.Headers[SignalRHeaders.ConnectionId] = Context.ConnectionId;
        message.Headers[SignalRHeaders.HubPath] = consumer.HubPath;

        if (exception is not null)
            message.Headers[SignalRHeaders.DisconnectError] = exception.Message;

        if (Context.UserIdentifier is not null)
            message.Headers[SignalRHeaders.UserId] = Context.UserIdentifier;

        var exchange = Exchange.Create(message, consumer.ScopeFactory);

        try
        {
            await consumer.ProcessExchange(exchange, default).ConfigureAwait(false);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task HandleGroupCommands(IExchange exchange)
    {
        var outMsg = exchange.HasOut ? exchange.Out! : exchange.In;

        if (outMsg.Headers.TryGetValue(SignalRHeaders.AddToGroup, out var addGroup)
            && addGroup is string groupToAdd && !string.IsNullOrEmpty(groupToAdd))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, groupToAdd).ConfigureAwait(false);
        }

        if (outMsg.Headers.TryGetValue(SignalRHeaders.RemoveFromGroup, out var removeGroup)
            && removeGroup is string groupToRemove && !string.IsNullOrEmpty(groupToRemove))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupToRemove).ConfigureAwait(false);
        }
    }

    private static object?[]? NormalizeArgs(object?[]? args)
    {
        if (args is null) return null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is JsonElement je)
                args[i] = DeserializeJsonElement(je);
        }

        return args;
    }

    private static object? DeserializeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            // For arrays and objects, keep as JsonElement — pipeline can deserialize as needed
            _ => element
        };
    }
}
