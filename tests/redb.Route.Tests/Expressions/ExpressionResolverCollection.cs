namespace redb.Route.Tests.Expressions;

/// <summary>
/// The classes that clear and count the expression caches, which are process state. The collection disables
/// parallelization, so no test of another collection compiles or clears between a clear and the count read after it.
/// </summary>
[CollectionDefinition("ExpressionResolver", DisableParallelization = true)]
public class ExpressionResolverCollection;
