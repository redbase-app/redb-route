using System.Reflection;
using System.Runtime.ExceptionServices;

namespace redb.Route.Controllers;

/// <summary>
/// Invokes a controller action and turns what it returned into the value the reply carries. Every dispatcher and
/// the direct-invoke DSL overloads go through here, so an action behaves the same behind every transport.
/// </summary>
internal static class ActionInvoker
{
    /// <summary>
    /// Calls <paramref name="method"/> on <paramref name="controller"/>, awaits it when it is asynchronous, and
    /// returns its result: <c>null</c> for <c>void</c>, <see cref="Task"/> and <see cref="ValueTask"/>.
    /// <para>
    /// The shape is read from the method's DECLARED return type, as ASP.NET Core's <c>ObjectMethodExecutor</c>
    /// does. The run-time type cannot tell: an <c>async Task</c> method returns a <c>Task&lt;VoidTaskResult&gt;</c>,
    /// whose runtime-internal result then reached the reply as a body; and a <see cref="ValueTask{TResult}"/> is a
    /// struct that is not a <see cref="Task"/> at all, so it reached the reply unawaited.
    /// </para>
    /// <para>
    /// An exception the action throws synchronously arrives wrapped in <see cref="TargetInvocationException"/> by
    /// <see cref="MethodBase.Invoke(object, object[])"/>; exactly that one wrapper is removed and the action's own
    /// exception is rethrown with its stack trace. An asynchronous action's exception comes out of the awaited
    /// task as it is. Nothing below that level is unwrapped.
    /// </para>
    /// </summary>
    internal static async Task<object?> InvokeAsync(MethodInfo method, object controller, object?[] arguments)
    {
        object? returned;
        try
        {
            returned = method.Invoke(controller, arguments);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable: Throw() never returns
        }

        var declared = method.ReturnType;

        if (declared == typeof(Task))
        {
            await ((Task)returned!).ConfigureAwait(false);
            return null;
        }

        if (declared == typeof(ValueTask))
        {
            await ((ValueTask)returned!).ConfigureAwait(false);
            return null;
        }

        if (declared.IsGenericType)
        {
            var definition = declared.GetGenericTypeDefinition();

            if (definition == typeof(Task<>))
            {
                var task = (Task)returned!;
                await task.ConfigureAwait(false);
                return declared.GetProperty(nameof(Task<object>.Result))!.GetValue(task);
            }

            if (definition == typeof(ValueTask<>))
            {
                var task = (Task)declared.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(returned, null)!;
                await task.ConfigureAwait(false);
                return typeof(Task<>).MakeGenericType(declared.GetGenericArguments())
                    .GetProperty(nameof(Task<object>.Result))!.GetValue(task);
            }
        }

        return returned;
    }
}
