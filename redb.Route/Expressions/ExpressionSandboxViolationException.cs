namespace redb.Route.Expressions;

/// <summary>Thrown when an expression tries to invoke a method while an <see cref="ExpressionSandbox"/> scope is open.</summary>
public sealed class ExpressionSandboxViolationException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="methodName"/> on <paramref name="type"/>.</summary>
    public ExpressionSandboxViolationException(string methodName, Type type)
        : base($"Method '{methodName}' on {type.Name} cannot be called here: the expression runs in a sandbox (a template argument or expr()), where objects expose their members but never code.")
    {
        MethodName = methodName;
        TargetType = type;
    }

    /// <summary>The method the expression tried to call.</summary>
    public string MethodName { get; }

    /// <summary>The type the method was looked up on.</summary>
    public Type TargetType { get; }
}
