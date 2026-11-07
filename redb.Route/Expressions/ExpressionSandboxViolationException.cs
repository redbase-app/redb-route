namespace redb.Route.Expressions;

/// <summary>
/// Thrown when an expression tries to run code or read outside the message while an <see cref="ExpressionSandbox"/>
/// scope is open: a method call, a registry object (<c>ref:name</c>) or an environment variable (<c>env:NAME</c>).
/// </summary>
public sealed class ExpressionSandboxViolationException : InvalidOperationException
{
    private const string Where = "the expression runs in a sandbox (a template argument or expr())";

    /// <summary>Creates the exception for <paramref name="methodName"/> on <paramref name="type"/>.</summary>
    public ExpressionSandboxViolationException(string methodName, Type type)
        : base($"Method '{methodName}' on {type.Name} cannot be called here: {Where}, where objects expose their members but never code.")
    {
        MethodName = methodName;
        Written = methodName;
        TargetType = type;
    }

    /// <summary>
    /// Creates the exception for a read the sandbox refuses, named as written: <c>ref:rules</c> (an object of the
    /// registry) or <c>env:HOME</c> (the process environment). No method is involved, so the message names the spelling.
    /// </summary>
    /// <param name="written">The spelling as the expression writes it.</param>
    public ExpressionSandboxViolationException(string written)
        : base($"'{written}' cannot be read here: {Where}, which reads the message and its values, not the registry or the process environment.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(written);
        MethodName = written;
        Written = written;
    }

    /// <summary>The method the expression tried to call; for a refused read, the spelling (<see cref="Written"/>).</summary>
    public string MethodName { get; }

    /// <summary>What the expression wrote: the method name, or the spelling of a read (<c>ref:rules</c>, <c>env:HOME</c>).</summary>
    public string Written { get; }

    /// <summary>The type the method was looked up on; <c>null</c> for a refused read, which calls no method.</summary>
    public Type? TargetType { get; }
}
